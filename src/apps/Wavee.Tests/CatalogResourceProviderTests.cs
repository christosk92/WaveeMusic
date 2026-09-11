using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Wavee.Backend;
using Wavee.Backend.Catalog;
using Wavee.Backend.Metadata;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Wavee.Core.Catalog;
using Wavee.SpotifyLive;
using Wavee.SpotifyLive.Catalog;
using Xunit;
using Xm = Wavee.Protocol.ExtendedMetadata;
using Lean = Wavee.Protocol.Lean;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Tests;

public sealed class CatalogResourceProviderTests
{
    [Theory]
    [InlineData("account")]
    [InlineData("locale")]
    [InlineData("market")]
    [InlineData("catalogue")]
    [InlineData("tier")]
    [InlineData("explicit")]
    [InlineData("unknown-context")]
    public async Task InstalledTransport_RejectsAnotherAccountOrContextBeforeAnyHttp(string difference)
    {
        var http = new FakeExchange((_, _) => throw new InvalidOperationException("Mismatched scope reached HTTP."));
        var scope = difference switch
        {
            "account" => Scope with { ProviderAccount = "other" },
            "locale" => Scope with { Locale = "nl" },
            "market" => Scope with { Market = "US" },
            "catalogue" => Scope with { Catalogue = "free" },
            "tier" => Scope with { Tier = 0 },
            "explicit" => Scope with { ExplicitFilter = true },
            _ => Scope with { ContextKnown = false },
        };
        var request = Request(FacetKind.Descriptors) with
        { Key = new(scope, "spotify:track:t", FacetKind.Descriptors), Stamp = new(scope, scope.ProviderAccount, 1, 0, 1) };
        var result = Assert.Single(await Provider(http).FetchAsync([request], TestContext.Current.CancellationToken));
        Assert.Equal(ResourceFetchStatus.Failed, result.Result.Status);
        Assert.Equal(ResourceErrorKind.Forbidden, result.Result.Error?.Kind);
        Assert.Equal(0, http.Calls);
        Assert.Null(result.Seeds);
        Assert.Null(result.Transports);
    }

    [Fact]
    public async Task OlderTransportTeardown_CannotClearItsSuccessor()
    {
        var old = new OfflineCatalogResourceProvider("spotify");
        var next = new OfflineCatalogResourceProvider("spotify");
        var source = new SwitchableCatalogResourceProvider("spotify", old);
        source.SetTarget(next);
        Assert.False(source.TrySetTarget(old, new OfflineCatalogResourceProvider("spotify")));
        Assert.True(source.TrySetTarget(next, old));
        Assert.Single(await source.FetchAsync([Request(FacetKind.Descriptors)], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AlbumDetailAndVersions_ShareOneFiniteBody_AndForceReadsANewBody()
    {
        var http = new FakeExchange((_, call) => Ok(System.Text.Encoding.UTF8.GetBytes(
            "{\"data\":{\"albumUnion\":{\"uri\":\"spotify:album:a\",\"name\":\"Album\",\"label\":\"Label " + call +
            "\",\"tracksV2\":{\"items\":[]},\"releases\":{\"items\":[]}}}}")));
        await using var fixture = new CatalogResourceWireFixture(http, Session);
        var detail = new ResourceKey(fixture.Scope, "spotify:album:a", FacetKind.AlbumDetail);
        var versions = new ResourceKey(fixture.Scope, "spotify:album:a", FacetKind.AlbumVersions, new(0, 50));
        await fixture.Data.Resources.EnsureAsync([detail, versions], ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, http.Calls);
        Assert.Equal("Label 1", Assert.IsType<AlbumDetailValue>(fixture.Data.Catalog.Peek(detail).Value).Label);
        Assert.Empty(Assert.IsType<RelationPageValue>(fixture.Data.Catalog.Peek(versions).Value).Items);
        await fixture.Data.Resources.EnsureAsync([detail, versions], force: true, ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, http.Calls);
        Assert.Equal("Label 2", Assert.IsType<AlbumDetailValue>(fixture.Data.Catalog.Peek(detail).Value).Label);
    }

    [Theory]
    [InlineData(200, ResourceFetchStatus.Present)]
    [InlineData(503, ResourceFetchStatus.Failed)]
    public async Task VideoAliasRecovery_IsFiniteAndNeverCachesAContradictionAsAbsent(int canonicalStatus, ResourceFetchStatus expected)
    {
        const string alias = "spotify:track:alias", canonical = "spotify:track:canonical", counterpart = "spotify:track:video";
        int posts = 0;
        var http = new FakeExchange((request, _) =>
        {
            posts++;
            var parsed = Xm.BatchedEntityRequest.Parser.ParseFrom(HttpCompression.Gunzip(request.Body!));
            var response = new Xm.BatchedExtensionResponse();
            var arrays = new Dictionary<Xm.ExtensionKind, Xm.EntityExtensionDataArray>();
            foreach (var entity in parsed.EntityRequest)
                foreach (var query in entity.Query)
                {
                    if (!arrays.TryGetValue(query.ExtensionKind, out var array))
                    { array = new() { ExtensionKind = query.ExtensionKind }; arrays.Add(query.ExtensionKind, array); response.ExtendedMetadata.Add(array); }
                    ByteString? body = (entity.EntityUri, query.ExtensionKind) switch
                    {
                        (alias, Xm.ExtensionKind.ConsumptionExperienceTrait) => ByteString.CopyFrom(new byte[] { 34, 1, 2 }),
                        (alias, Xm.ExtensionKind.TrackV4) => new Lean.LeanTrack { CanonicalUri = canonical }.ToByteString(),
                        (alias, Xm.ExtensionKind.PlaybackTrait) => ByteString.CopyFrom(new byte[] { 18, 16 }.Concat(Enumerable.Repeat((byte)7, 16)).ToArray()),
                        (canonical, Xm.ExtensionKind.VideoAssociations) when canonicalStatus == 200 => new Xm.VideoAssociations { Association = new Xm.Association { AssociatedUri = counterpart } }.ToByteString(),
                        _ => null,
                    };
                    int status = entity.EntityUri == canonical ? canonicalStatus : body is null ? 404 : 200;
                    var row = new Xm.EntityExtensionData { EntityUri = entity.EntityUri,
                        Header = new Xm.EntityExtensionDataHeader { StatusCode = status, Etag = entity.EntityUri == canonical ? "canonical-etag" : "alias-etag" } };
                    if (body is not null) row.ExtensionData = new Any { Value = body };
                    array.ExtensionData.Add(row);
                }
            return Ok(response.ToByteArray());
        });
        var result = Assert.Single(await Provider(http).FetchAsync([Request(FacetKind.VideoAssociation, alias)], TestContext.Current.CancellationToken));
        Assert.Equal(3, posts);
        Assert.Equal(expected, result.Result.Status);
        if (expected == ResourceFetchStatus.Present)
        {
            var value = Assert.IsType<VideoAssociationValue>(result.Result.Patch!.Apply(null)).Association;
            Assert.True(value.HasVideo); Assert.Equal(counterpart, value.CounterpartUri);
            Assert.Equal(string.Concat(Enumerable.Repeat("07", 16)), value.VideoGidHex);
            Assert.Null(value.Etag);
            Assert.Contains(result.Transports!, record => record.Subject == canonical && record.Etag == "canonical-etag");
            Assert.Contains(result.Seeds!, seed => seed.Key.Subject == alias && seed.Patch.Apply(null) is TrackIdentityValue { CanonicalUri: canonical });
        }
    }

    static readonly CatalogScope Scope = new("spotify", "account-a", "en", "NL", "premium", 1, false);
    static readonly SessionContext Session = new("account-a", "NL", "premium", "en", Tier.Premium, false);
    static ResourceRequest Request(FacetKind facet, string uri = "spotify:track:t", int id = 1, ResourceArguments args = default)
        => new(new(Scope, uri, facet, args), new(Scope, "account-a", 1, 0, id), ResourcePriority.Visible);
    static SpotifyCatalogResourceProvider Provider(FakeExchange http, CatalogTransportRecord? cached = null)
        => new(new ExtendedMetadataSource(http, () => "https://spclient.test", () => Session), new Bytes(cached),
            new PathfinderClient(http), http, () => "https://spclient.test", () => HomeModuleTitles.Default, TimeProvider.System);
    static HttpResp Ok(byte[] bytes) => new(200, new Dictionary<string, string>(), bytes);
    static byte[] Wire(string uri, Xm.ExtensionKind kind, int status, ByteString? payload = null, string? etag = null)
    {
        var row = new Xm.EntityExtensionData { EntityUri = uri, Header = new Xm.EntityExtensionDataHeader { StatusCode = status } };
        if (etag is not null) row.Header.Etag = etag;
        if (payload is not null) row.ExtensionData = new Any { Value = payload };
        var array = new Xm.EntityExtensionDataArray { ExtensionKind = kind };
        array.ExtensionData.Add(row);
        var response = new Xm.BatchedExtensionResponse(); response.ExtendedMetadata.Add(array);
        return response.ToByteArray();
    }

    [Fact]
    public async Task ZeroPlayCount_IsPresentAndEmptyDescriptorsAreSuccessfulBytes()
    {
        foreach (var (facet, kind, bytes) in new[]
        {
            (FacetKind.PlayCount, Xm.ExtensionKind.OnPlatformReputationTrait, ByteString.CopyFrom(new byte[] { 24, 0 })),
            (FacetKind.Descriptors, Xm.ExtensionKind.TrackDescriptor, ByteString.Empty),
        })
        {
            var http = new FakeExchange((_, _) => Ok(Wire("spotify:track:t", kind, 200, bytes)));
            var response = Assert.Single(await Provider(http).FetchAsync([Request(facet)], TestContext.Current.CancellationToken));
            Assert.Equal(ResourceFetchStatus.Present, response.Result.Status);
            var value = response.Result.Patch!.Apply(null);
            if (value is PlayCountValue count) Assert.Equal(0, count.Count);
            else Assert.Empty(Assert.IsType<DescriptorsValue>(value).Tags);
            Assert.NotNull(Assert.Single(response.Transports!).Payload);
        }
    }

    [Theory]
    [InlineData(404, ResourceFetchStatus.Absent, null)]
    [InlineData(403, ResourceFetchStatus.Failed, ResourceErrorKind.Forbidden)]
    [InlineData(429, ResourceFetchStatus.Failed, ResourceErrorKind.RateLimited)]
    [InlineData(503, ResourceFetchStatus.Failed, ResourceErrorKind.Transport)]
    public async Task ExtensionStatus_DoesNotTurnFailureIntoEmptySuccess(int status, ResourceFetchStatus expected, ResourceErrorKind? error)
    {
        var http = new FakeExchange((_, _) => Ok(Wire("spotify:track:t", Xm.ExtensionKind.TrackDescriptor, status)));
        var response = Assert.Single(await Provider(http).FetchAsync([Request(FacetKind.Descriptors)], TestContext.Current.CancellationToken));
        Assert.Equal(expected, response.Result.Status); Assert.Equal(error, response.Result.Error?.Kind);
        Assert.Equal(1, http.Calls); Assert.Null(response.Transports);
    }

    [Fact]
    public async Task NotModifiedWithoutMatchingBytes_MakesExactlyOneUnconditionalRequest()
    {
        var asks = new List<Xm.BatchedEntityRequest>();
        var http = new FakeExchange((request, call) =>
        {
            asks.Add(Xm.BatchedEntityRequest.Parser.ParseFrom(HttpCompression.Gunzip(request.Body!)));
            return Ok(Wire("spotify:track:t", Xm.ExtensionKind.TrackDescriptor, call == 1 ? 304 : 200,
                call == 1 ? null : ByteString.Empty, "new"));
        });
        var response = Assert.Single(await Provider(http).FetchAsync([Request(FacetKind.Descriptors)], TestContext.Current.CancellationToken));
        Assert.Equal(ResourceFetchStatus.Present, response.Result.Status);
        Assert.Equal(2, http.Calls);
        Assert.All(asks, ask => Assert.False(Assert.Single(Assert.Single(ask.EntityRequest).Query).HasEtag));
    }

    [Fact]
    public async Task MatchingNotModified_ReusesBytesButStillPerformsConditionalHttp()
    {
        var cached = new CatalogTransportRecord(Scope, "spotify:track:t", (int)Xm.ExtensionKind.TrackDescriptor, "etag", [], DateTimeOffset.UtcNow);
        var http = new FakeExchange((request, _) =>
        {
            var ask = Xm.BatchedEntityRequest.Parser.ParseFrom(HttpCompression.Gunzip(request.Body!));
            Assert.Equal("etag", Assert.Single(Assert.Single(ask.EntityRequest).Query).Etag);
            return Ok(Wire("spotify:track:t", Xm.ExtensionKind.TrackDescriptor, 304, etag: "etag"));
        });
        var provider = Provider(http, cached);
        var first = Assert.Single(await provider.FetchAsync([Request(FacetKind.Descriptors)], TestContext.Current.CancellationToken));
        var forced = Assert.Single(await provider.FetchAsync([Request(FacetKind.Descriptors, id: 2)], TestContext.Current.CancellationToken));
        Assert.Equal(ResourceFetchStatus.Present, first.Result.Status); Assert.Equal(ResourceFetchStatus.Present, forced.Result.Status);
        Assert.Equal(2, http.Calls);
    }

    [Fact]
    public void AlbumRelations_PreserveDuplicateOccurrencesAndSeedOnlyTheRequestedPage()
    {
        static ByteString Gid(byte value) => ByteString.CopyFrom(Enumerable.Repeat(value, 16).ToArray());
        var album = new Lean.LeanAlbum { Gid = Gid(1), Name = "Album" };
        var disc1 = new Lean.LeanDisc { Number = 1 }; disc1.Track.Add(new Lean.LeanTrack { Gid = Gid(2), Name = "First", Number = 1 });
        var disc2 = new Lean.LeanDisc { Number = 2 }; disc2.Track.Add(new Lean.LeanTrack { Gid = Gid(2), Name = "Again", Number = 1 });
        album.Disc.Add(disc1); album.Disc.Add(disc2);
        var key = Request(FacetKind.AlbumTracks, "spotify:album:a", args: new(Offset: 1, Limit: 1)).Key;
        var result = SpotifyCatalogDecoder.Decode(key, album.ToByteString());
        var page = Assert.IsType<RelationPageValue>(result.Patch.Apply(null));
        Assert.Equal(2, page.Total); Assert.Null(page.NextCursor);
        Assert.Equal(2, Assert.Single(page.Items).Context!.DiscNumber);
        Assert.Single(result.Seeds.Where(seed => seed.Key.Facet == FacetKind.TrackIdentity));
        var all = Assert.IsType<RelationPageValue>(SpotifyCatalogDecoder.Decode(key with { Arguments = new(Limit: 50) }, album.ToByteString()).Patch.Apply(null));
        Assert.Equal(all.Items[0].EntityUri, all.Items[1].EntityUri);
        Assert.NotEqual(all.Items[0].OccurrenceKey, all.Items[1].OccurrenceKey);
    }

    [Fact]
    public async Task PlaylistHeader_UsesOneDecoratedBodyForCapabilitiesRolloverAndEdition()
    {
        var revision = new byte[24]; revision[23] = 7;
        var body = new Pl.SelectedListContent { Revision = ByteString.CopyFrom(revision), Length = 0, OwnerUsername = "account-a",
            Attributes = new Pl.ListAttributes { Name = "Fresh daylist", Format = "daylist", Description = "" },
            Capabilities = new Pl.Capabilities { CanView = true, CanEditItems = false, CanEditMetadata = false } };
        var http = new FakeExchange((request, _) => { Assert.Contains("?decorate=revision,attributes,length,owner,capabilities,picture", request.Url); return Ok(body.ToByteArray()); });
        var response = Assert.Single(await Provider(http).FetchAsync([Request(FacetKind.PlaylistHeader, "spotify:playlist:p")], TestContext.Current.CancellationToken));
        var header = Assert.IsType<PlaylistHeaderValue>(response.Result.Patch!.Apply(null));
        Assert.Equal("Fresh daylist", header.Name); Assert.Equal(0, header.TrackCount); Assert.Equal("", header.Description);
        Assert.Equal(Convert.ToHexString(revision), header.Edition); Assert.True(header.Capabilities!.Value.Known);
        Assert.Equal("daylist", header.Format); Assert.Equal(1, http.Calls);
    }

    sealed class Bytes(CatalogTransportRecord? record) : ICatalogTransportReader
    {
        public ValueTask<CatalogTransportRecord?> ReadTransportAsync(CatalogScope scope, string subject, int extensionKind, CancellationToken ct = default)
            => ValueTask.FromResult(record is not null && record.Scope == scope && record.Subject == subject && record.ExtensionKind == extensionKind ? record : null);
    }
}
