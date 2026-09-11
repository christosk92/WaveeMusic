using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Wavee.Backend;
using Wavee.Backend.Lyrics;
using Wavee.Backend.Lyrics.Sources;
using Wavee.Backend.Metadata;
using Wavee.Backend.Persistence;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Wavee.SpotifyLive;
using Xunit;
using Xm = Wavee.Protocol.ExtendedMetadata;
using Pb = Wavee.Protocol.Metadata;

namespace Wavee.Tests.ApiWaste;

public class PathfinderResourceTests
{
    static SessionContext Ctx => new("me", "US", "premium", "en", Tier.Premium, false);

    [Fact]
    public async Task SameOperationAndVariables_CoalescesParallelCalls()
    {
        var http = new FakeExchange((_, _) =>
            new HttpResp(200, new Dictionary<string, string>(), Encoding.UTF8.GetBytes("""{"data":{"ok":true}}""")));
        var resource = new PathfinderResource(new PathfinderClient(http), () => Ctx);

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => resource.QueryAsync("home", "hash",
                w => w.WriteString("uri", "spotify:album:A"),
                PathfinderClient.Platform.WebPlayer,
                TestContext.Current.CancellationToken))
            .ToArray();

        var docs = await Task.WhenAll(tasks);

        Assert.All(docs, d =>
        {
            Assert.NotNull(d);
            Assert.True(d!.RootElement.GetProperty("data").GetProperty("ok").GetBoolean());
            d.Dispose();
        });
        Assert.Equal(1, http.Calls);
    }

    [Fact]
    public async Task ExactInvalidation_RefetchesOnce_AndMakesTheFreshBodyResident()
    {
        var http = new FakeExchange((_, call) =>
            new HttpResp(200, new Dictionary<string, string>(),
                Encoding.UTF8.GetBytes("{\"data\":{\"version\":" + call + "}}")));
        var resource = new PathfinderResource(new PathfinderClient(http), () => Ctx);
        static void Variables(Utf8JsonWriter w) => w.WriteString("facet", "music-chip");

        using (var first = await resource.UseQueryAsync("home", "hash", Variables,
                   PathfinderClient.Platform.Desktop, TestContext.Current.CancellationToken))
            Assert.Equal(1, first!.RootElement.GetProperty("data").GetProperty("version").GetInt32());
        using (var hit = await resource.UseQueryAsync("home", "hash", Variables,
                   PathfinderClient.Platform.Desktop, TestContext.Current.CancellationToken))
            Assert.Equal(1, hit!.RootElement.GetProperty("data").GetProperty("version").GetInt32());
        Assert.Equal(1, http.Calls);

        resource.Invalidate("home", "hash", Variables, PathfinderClient.Platform.Desktop);

        using (var refreshed = await resource.UseQueryAsync("home", "hash", Variables,
                   PathfinderClient.Platform.Desktop, TestContext.Current.CancellationToken))
            Assert.Equal(2, refreshed!.RootElement.GetProperty("data").GetProperty("version").GetInt32());
        using (var resident = await resource.UseQueryAsync("home", "hash", Variables,
                   PathfinderClient.Platform.Desktop, TestContext.Current.CancellationToken))
            Assert.Equal(2, resident!.RootElement.GetProperty("data").GetProperty("version").GetInt32());

        Assert.Equal(2, http.Calls);
        Assert.Equal(0, resource.PendingBodyCount);
    }
}

public class CatalogTransportRetentionTests
{
    static SessionContext Ctx => new("me", "US", "premium", "en", Tier.Premium, false);

    [Fact]
    public async Task SharedDocumentReads_CoalesceAndRevalidateWithTheDurableEtag()
    {
        const string uri = "spotify:album:A";
        string? secondEtag = null;
        var http = new FakeExchange((request, call) =>
        {
            var body = Xm.BatchedEntityRequest.Parser.ParseFrom(HttpCompression.Gunzip(request.Body!));
            if (call == 2) secondEtag = Assert.Single(Assert.Single(body.EntityRequest).Query).Etag;
            return new HttpResp(200, new Dictionary<string, string>(),
                ExtensionResponse(uri, Xm.ExtensionKind.RecommendedPlaylists, call == 1 ? 200 : 304,
                    "v1", call == 1 ? ByteString.CopyFromUtf8("payload") : null));
        });
        await using var fixture = new CatalogResourceWireFixture(http, Ctx);
        var reads = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => fixture.Reader.ReadDocumentsAsync(
            [(uri, Xm.ExtensionKind.RecommendedPlaylists)], TestContext.Current.CancellationToken)));
        Assert.All(reads, result => Assert.Equal("payload", result[(uri, Xm.ExtensionKind.RecommendedPlaylists)].ToStringUtf8()));
        Assert.Equal(1, http.Calls);
        var key = new Wavee.Core.Catalog.ResourceKey(fixture.Scope, uri, Wavee.Core.Catalog.FacetKind.ExtensionDocument,
            new Wavee.Core.Catalog.ResourceArguments(Filter: ((int)Xm.ExtensionKind.RecommendedPlaylists).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        await fixture.Data.Catalog.InvalidateAsync([key], TestContext.Current.CancellationToken);
        var refreshed = await fixture.Reader.ReadDocumentsAsync([(uri, Xm.ExtensionKind.RecommendedPlaylists)], TestContext.Current.CancellationToken);
        Assert.Equal("payload", refreshed[(uri, Xm.ExtensionKind.RecommendedPlaylists)].ToStringUtf8());
        Assert.Equal("v1", secondEtag);
        Assert.Equal(2, http.Calls);
    }

    [Fact]
    public async Task MissingDocument_DoesNotRetainAnEtagOrPermitConditionalAbsence()
    {
        const string uri = "spotify:album:missing";
        var http = new FakeExchange((request, _) =>
        {
            var query = Assert.Single(Assert.Single(Xm.BatchedEntityRequest.Parser.ParseFrom(HttpCompression.Gunzip(request.Body!)).EntityRequest).Query);
            Assert.False(query.HasEtag);
            return new HttpResp(200, new Dictionary<string, string>(), ExtensionResponse(uri, Xm.ExtensionKind.RecommendedPlaylists, 404, "bad-etag", null));
        });
        await using var fixture = new CatalogResourceWireFixture(http, Ctx);
        Assert.Empty(await fixture.Reader.ReadDocumentsAsync([(uri, Xm.ExtensionKind.RecommendedPlaylists)], TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Reader.ReadDocumentsAsync([(uri, Xm.ExtensionKind.RecommendedPlaylists)], TestContext.Current.CancellationToken));
        Assert.Equal(1, http.Calls);
    }

    static byte[] ExtensionResponse(string uri, Xm.ExtensionKind kind, int status, string? etag, ByteString? payload)
    {
        var header = new Xm.EntityExtensionDataHeader { StatusCode = status, OfflineTtlInSeconds = 60 };
        if (etag is not null) header.Etag = etag;
        var row = new Xm.EntityExtensionData { EntityUri = uri, Header = header };
        if (payload is not null) row.ExtensionData = new Any { Value = payload };
        var array = new Xm.EntityExtensionDataArray { ExtensionKind = kind };
        array.ExtensionData.Add(row);
        var response = new Xm.BatchedExtensionResponse(); response.ExtendedMetadata.Add(array);
        return response.ToByteArray();
    }
}

public class LyricsNegativeCacheTests
{
    [Fact]
    public async Task AmllSource_SkipsHttp_WhenSpotifyLyricsKnown()
    {
        var http = new CountingLyricHttp();
        var source = new AmllTtmlDbSource(http);
        var req = new LyricsRequest("t1", "spotify:track:t1", "Song", ["Artist"], "Album", 1000, HasSpotifyLyrics: true);

        var result = await source.FetchAsync(req, TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Equal(0, http.Calls);
    }

    sealed class CountingLyricHttp : ILyricHttpWithStatus
    {
        public int Calls;

        public Task<string?> GetStringAsync(string url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<string?>(null);
        }

        public Task<LyricHttpResult> GetAsync(string url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new LyricHttpResult(404, null));
        }
    }
}
