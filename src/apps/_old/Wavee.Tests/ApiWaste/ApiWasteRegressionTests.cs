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
using Wavee.Backend.Hydration;
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

public class ExtensionEtagCacheTests
{
    static SessionContext Ctx => new("me", "US", "premium", "en", Tier.Premium, false);
    static SessionContext DutchCtx => Ctx with { Locale = "nl" };

    [Fact]
    public async Task SecondStaleFetch_SendsEtag_AndKeepsPayloadOn304()
    {
        const string uri = "spotify:album:A";
        string? secondEtag = null;
        var http = new FakeExchange((req, call) =>
        {
            var body = Xm.BatchedEntityRequest.Parser.ParseFrom(HttpCompression.Gunzip(req.Body!));
            if (call == 2) secondEtag = Assert.Single(Assert.Single(body.EntityRequest).Query).Etag;
            return new HttpResp(200, new Dictionary<string, string>(),
                call == 1
                    ? ExtensionResponse(uri, Xm.ExtensionKind.RecommendedPlaylists, 200, "v1", ByteString.CopyFromUtf8("payload"))
                    : ExtensionResponse(uri, Xm.ExtensionKind.RecommendedPlaylists, 304, "v1", null));
        });
        var source = new ExtendedMetadataSource(http, () => "https://spclient.test", () => Ctx);
        var cache = new ExtensionEtagCache(source, () => Ctx);

        var first = await cache.GetPayloadAsync(uri, Xm.ExtensionKind.RecommendedPlaylists, TestContext.Current.CancellationToken);
        cache.MarkStale(uri, Xm.ExtensionKind.RecommendedPlaylists);
        var second = await cache.GetPayloadAsync(uri, Xm.ExtensionKind.RecommendedPlaylists, TestContext.Current.CancellationToken);

        Assert.Equal("payload", first!.ToStringUtf8());
        Assert.Equal("payload", second!.ToStringUtf8());
        Assert.Equal("v1", secondEtag);
        Assert.Equal(2, http.Calls);
    }

    [Fact]
    public async Task Restart_RestoresExactLocalePayloadAndEtag_ForConditionalRevalidation()
    {
        const string uri = "spotify:album:persistent";
        string path = Path.Combine(Path.GetTempPath(), "wavee-extension-test-" + Guid.NewGuid().ToString("N") + ".db");
        static void DeleteDb(string p)
        {
            foreach (string suffix in new[] { "", "-wal", "-shm" })
                try { File.Delete(p + suffix); } catch { }
        }

        try
        {
            using (var cold = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, "nl-NL"))
            {
                var firstHttp = new FakeExchange((req, _) =>
                {
                    Assert.Equal("nl", req.Headers["Accept-Language"]);
                    return new HttpResp(200, new Dictionary<string, string>(),
                        ExtensionResponse(uri, Xm.ExtensionKind.RecommendedPlaylists, 200, "persistent-v1", ByteString.CopyFromUtf8("disk payload")));
                });
                var source = new ExtendedMetadataSource(firstHttp, () => "https://spclient.test", () => DutchCtx);
                var cache = new ExtensionEtagCache(source, () => DutchCtx, persistent: cold);
                Assert.Equal("disk payload", (await cache.GetPayloadAsync(uri, Xm.ExtensionKind.RecommendedPlaylists,
                    TestContext.Current.CancellationToken))!.ToStringUtf8());
                cold.Flush();
            }

            string? sentEtag = null;
            using (var reopened = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, "nl"))
            {
                var secondHttp = new FakeExchange((req, _) =>
                {
                    var body = Xm.BatchedEntityRequest.Parser.ParseFrom(HttpCompression.Gunzip(req.Body!));
                    sentEtag = Assert.Single(Assert.Single(body.EntityRequest).Query).Etag;
                    return new HttpResp(200, new Dictionary<string, string>(),
                        ExtensionResponse(uri, Xm.ExtensionKind.RecommendedPlaylists, 304, "persistent-v1", null));
                });
                var source = new ExtendedMetadataSource(secondHttp, () => "https://spclient.test", () => DutchCtx);
                var cache = new ExtensionEtagCache(source, () => DutchCtx, persistent: reopened);
                cache.MarkStale(uri, Xm.ExtensionKind.RecommendedPlaylists);

                var restored = await cache.GetPayloadAsync(uri, Xm.ExtensionKind.RecommendedPlaylists,
                    TestContext.Current.CancellationToken);

                Assert.Equal("disk payload", restored!.ToStringUtf8());
                Assert.Equal("persistent-v1", sentEtag);
                Assert.Equal(1, secondHttp.Calls);
            }
        }
        finally { DeleteDb(path); }
    }

    internal static byte[] ExtensionResponse(string uri, Xm.ExtensionKind kind, int status, string? etag, ByteString? payload)
    {
        var hdr = new Xm.EntityExtensionDataHeader { StatusCode = status, OfflineTtlInSeconds = 60 };
        if (etag is not null) hdr.Etag = etag;
        var data = new Xm.EntityExtensionData { EntityUri = uri, Header = hdr };
        if (payload is not null) data.ExtensionData = new Any { Value = payload };
        var array = new Xm.EntityExtensionDataArray { ExtensionKind = kind };
        array.ExtensionData.Add(data);
        var response = new Xm.BatchedExtensionResponse();
        response.ExtendedMetadata.Add(array);
        return response.ToByteArray();
    }

    [Fact]
    public async Task Missing404_PersistsWithoutEtag_AndRefetchSendsNoEtag()
    {
        const string uri = "spotify:track:missing";
        string path = Path.Combine(Path.GetTempPath(), "wavee-ext-miss-" + Guid.NewGuid().ToString("N") + ".db");
        static void DeleteDb(string p)
        {
            foreach (string suffix in new[] { "", "-wal", "-shm" })
                try { File.Delete(p + suffix); } catch { }
        }

        try
        {
            string? secondEtag = "sentinel";
            using (var cold = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, "en"))
            {
                var http = new FakeExchange((req, call) =>
                {
                    var body = Xm.BatchedEntityRequest.Parser.ParseFrom(HttpCompression.Gunzip(req.Body!));
                    var query = Assert.Single(Assert.Single(body.EntityRequest).Query);
                    if (call == 2) secondEtag = query.Etag;
                    // Wire offers an ETag on 404 — Fold must refuse to adopt it onto Missing.
                    return new HttpResp(200, new Dictionary<string, string>(),
                        ExtensionResponse(uri, Xm.ExtensionKind.TrackV4, 404, "should-not-stick", null));
                });
                var source = new ExtendedMetadataSource(http, () => "https://spclient.test", () => Ctx);
                var cache = new ExtensionEtagCache(source, () => Ctx, persistent: cold);

                var first = await cache.GetAsync([(uri, Xm.ExtensionKind.TrackV4)], TestContext.Current.CancellationToken);
                Assert.True(first[(uri, Xm.ExtensionKind.TrackV4)].Missing);
                Assert.Null(first[(uri, Xm.ExtensionKind.TrackV4)].Etag);
                cold.Flush();

                var rows = cold.LoadExtensions([uri], (int)Xm.ExtensionKind.TrackV4);
                Assert.True(Assert.Single(rows).Missing);
                Assert.Null(Assert.Single(rows).Etag);

                cache.MarkStale(uri, Xm.ExtensionKind.TrackV4);
                await cache.GetAsync([(uri, Xm.ExtensionKind.TrackV4)], TestContext.Current.CancellationToken);
                Assert.True(string.IsNullOrEmpty(secondEtag));
                Assert.Equal(2, http.Calls);
            }
        }
        finally { DeleteDb(path); }
    }

    [Fact]
    public async Task AbsentFromResponse_DoesNotSeed_AndDoesNotInventMissing()
    {
        const string present = "spotify:track:present";
        const string absent = "spotify:track:absent";
        var http = new FakeExchange((_, _) =>
            new HttpResp(200, new Dictionary<string, string>(),
                ExtensionResponse(present, Xm.ExtensionKind.TrackV4, 200, "v1", ByteString.CopyFromUtf8("payload"))));
        // Response deliberately omits `absent` — GetAsync must not Seed a synthetic Missing for it.
        var source = new ExtendedMetadataSource(http, () => "https://spclient.test", () => Ctx);
        var cache = new ExtensionEtagCache(source, () => Ctx);

        var values = await cache.GetAsync(
            [(present, Xm.ExtensionKind.TrackV4), (absent, Xm.ExtensionKind.TrackV4)],
            TestContext.Current.CancellationToken);

        Assert.False(values[(present, Xm.ExtensionKind.TrackV4)].Missing);
        Assert.False(values.ContainsKey((absent, Xm.ExtensionKind.TrackV4)));

        // Second call: present is fresh (no network); absent still misses → network again for the omitted key.
        await cache.GetAsync(
            [(present, Xm.ExtensionKind.TrackV4), (absent, Xm.ExtensionKind.TrackV4)],
            TestContext.Current.CancellationToken);
        Assert.Equal(2, http.Calls);
    }

    [Fact]
    public async Task MissingCannot304_PastTtl_RequiresFullBody()
    {
        const string uri = "spotify:track:ghost";
        // Pre-seed a Missing row WITH an etag via cold tier (legacy wedge shape), then MarkStale + 304 from wire —
        // FetchBatch must refuse the 304 and leave the key unsealed for a full-body retry.
        string path = Path.Combine(Path.GetTempPath(), "wavee-ext-304miss-" + Guid.NewGuid().ToString("N") + ".db");
        static void DeleteDb(string p)
        {
            foreach (string suffix in new[] { "", "-wal", "-shm" })
                try { File.Delete(p + suffix); } catch { }
        }

        try
        {
            using var cold = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, "en");
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            cold.UpsertExtension(new ColdExtension(uri, (int)Xm.ExtensionKind.TrackV4, null, "legacy-etag", 0,
                Missing: true, now + 3600, now));
            cold.Flush();

            int calls = 0;
            int conditionalCalls = 0;
            var http = new FakeExchange((req, _) =>
            {
                calls++;
                var body = Xm.BatchedEntityRequest.Parser.ParseFrom(HttpCompression.Gunzip(req.Body!));
                foreach (var entity in body.EntityRequest)
                    foreach (var query in entity.Query)
                        if (query.HasEtag && query.Etag.Length > 0) conditionalCalls++;
                // HydrateFromCold strips ETag on Missing → no conditional; if a 304 somehow arrives with Missing
                // prior, Fold/FetchBatch must not adopt it. Force a 304 body to exercise the guard.
                return new HttpResp(200, new Dictionary<string, string>(),
                    ExtensionResponse(uri, Xm.ExtensionKind.TrackV4, 304, "legacy-etag", null));
            });
            var source = new ExtendedMetadataSource(http, () => "https://spclient.test", () => Ctx);
            var cache = new ExtensionEtagCache(source, () => Ctx, persistent: cold);
            cache.MarkStale(uri, Xm.ExtensionKind.TrackV4);

            var values = await cache.GetAsync([(uri, Xm.ExtensionKind.TrackV4)], TestContext.Current.CancellationToken);
            Assert.Equal(1, calls);
            // The legacy ETag never leaves the process: HydrateFromCold strips it on a Missing row, so the server is
            // given no way to answer 304 at all. This is the FIRST of the two guards and the cheaper one.
            Assert.Equal(0, conditionalCalls);

            // The second guard: a 304 that arrives anyway is dropped from FetchBatch's result, so nothing is Seeded and
            // nothing is re-Persisted. The key is still reported to the caller — the offline/SWR arm hands back the
            // already-known stale row rather than a hole — but it is the PRE-EXISTING Missing, carrying no payload…
            Assert.True(values.TryGetValue((uri, Xm.ExtensionKind.TrackV4), out var value));
            Assert.True(value.Missing);
            Assert.Null(value.Payload);

            // …and, crucially, still UNSEALED: the refused 304 bought no fresh TTL, so the very next read goes back to
            // the wire for a full body instead of being served a resealed negative. That is what "RequiresFullBody"
            // means, and asserting it here is what a `values` key-absence check only ever implied.
            await cache.GetAsync([(uri, Xm.ExtensionKind.TrackV4)], TestContext.Current.CancellationToken);
            Assert.Equal(2, calls);
        }
        finally { DeleteDb(path); }
    }
}

public class BulkMetadataEtagTests
{
    static SessionContext Ctx => new("me", "US", "premium", "en", Tier.Premium, false);

    // The guardrail is unchanged; only the caller moved. MetadataService is gone (hydration-facade-plan.md 1.6) and
    // XmCatalogFetch is THE catalogue arm, so this pins the same intent on it: a catalogue hydrate goes through the
    // ETag cache, a re-hydrate of a stale row sends the stored etag, and a 304 keeps the resident payload. Request
    // count stays 2 - one full body, one conditional revalidation.
    [Fact]
    public async Task CatalogFetch_UsesConditionalExtensionCache_ForCatalogHydration()
    {
        const string uri = "spotify:track:x";
        string? secondEtag = null;
        var http = new FakeExchange((req, call) =>
        {
            var body = Xm.BatchedEntityRequest.Parser.ParseFrom(HttpCompression.Gunzip(req.Body!));
            if (call == 2) secondEtag = Assert.Single(Assert.Single(body.EntityRequest).Query).Etag;
            return new HttpResp(200, new Dictionary<string, string>(),
                call == 1
                    ? ExtensionEtagCacheTests.ExtensionResponse(uri, Xm.ExtensionKind.TrackV4, 200, "track-etag", TrackPayload())
                    : ExtensionEtagCacheTests.ExtensionResponse(uri, Xm.ExtensionKind.TrackV4, 304, "track-etag", null));
        });
        var store = new InMemoryStore();
        var source = new ExtendedMetadataSource(http, () => "https://spclient.test", () => Ctx);
        var cache = new ExtensionEtagCache(source, () => Ctx);
        var fetch = new XmCatalogFetch(cache, store);

        await fetch.FetchAsync([EntityUri.Parse(uri)], null, TraitSurface.None, TestContext.Current.CancellationToken);
        cache.MarkStale(uri, Xm.ExtensionKind.TrackV4);
        await fetch.FetchAsync([EntityUri.Parse(uri)], null, TraitSurface.None, TestContext.Current.CancellationToken);

        Assert.Equal("track-etag", secondEtag);
        Assert.Equal(2, http.Calls);
        Assert.Equal("Waste Track", Assert.Single(store.QueryTracks()).Title);
    }

    static ByteString TrackPayload()
    {
        var gid = ByteString.CopyFrom(Enumerable.Repeat((byte)0x11, 16).ToArray());
        var track = new Pb.Track { Gid = gid, Name = "Waste Track", Duration = 123000 };
        track.Artist.Add(new Pb.Artist { Gid = ByteString.CopyFrom(Enumerable.Repeat((byte)0x22, 16).ToArray()), Name = "Artist" });
        track.Album = new Pb.Album { Gid = ByteString.CopyFrom(Enumerable.Repeat((byte)0x33, 16).ToArray()), Name = "Album" };
        return track.ToByteString();
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
