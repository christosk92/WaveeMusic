using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Wavee.Backend;
using Wavee.Backend.Hydration;
using Wavee.Backend.Library;
using Wavee.Backend.Metadata;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Xunit;
using Xm = Wavee.Protocol.ExtendedMetadata;
using Pb = Wavee.Protocol.Metadata;

namespace Wavee.Tests.ApiWaste;

// ── What the façade is FOR, measured in requests (plan §3, design §4) ────────────────────────────────────────────────
// The other hydration suites pin behaviour with doubles at the port boundary; this one runs the whole stack a page open
// actually traverses — StoreLibrarySource → SpotifyProviderHydrator → the ladders → XmCatalogFetch → ExtensionEtagCache
// → ExtendedMetadataSource → a FakeExchange — and counts what reaches the wire. These numbers ARE the design: an album
// open was up to six POSTs across four services with four freshness rules; it is one catalogue POST plus one trait pass
// here, and a re-open is zero.
//
// Every assertion below decodes the real gzipped BatchedEntityRequest, so a regression that "only" changes batching
// (a second pass for the same kind, a uri asked twice, a repair that fans out per row) fails here and nowhere else.
public class HydrationWasteTests
{
    static SessionContext Ctx => new("me", "US", "premium", "en", Tier.Premium, false);

    static ByteString Gid(byte fill) { var a = new byte[16]; Array.Fill(a, fill); return ByteString.CopyFrom(a); }
    static string Id(byte fill) => Base62.Encode(Gid(fill).Span);
    static string AlbumUri(byte fill) => "spotify:album:" + Id(fill);
    static string TrackUri(byte fill) => "spotify:track:" + Id(fill);

    /// <summary>An AlbumV4 payload with <paramref name="rows"/> disc tracks. <paramref name="named"/> false makes them
    /// gid-only — how AlbumV4 genuinely lands rows the album entity carried no names for, and the case the ladder's
    /// TrackV4 repair exists for.</summary>
    static byte[] AlbumResponse(byte gid, int rows, bool named)
    {
        var album = new Pb.Album { Gid = Gid(gid), Name = "Album " + Id(gid), Date = new Pb.Date { Year = 2024 } };
        album.Artist.Add(new Pb.Artist { Gid = Gid(0xAA), Name = "Artist One" });
        var disc = new Pb.Disc { Number = 1 };
        for (int i = 0; i < rows; i++)
        {
            var t = new Pb.Track { Gid = Gid((byte)(0x10 + i)), Duration = 210_000 };
            if (named) t.Name = "Song " + i;
            disc.Track.Add(t);
        }
        album.Disc.Add(disc);
        return Wrap(Xm.ExtensionKind.AlbumV4, "spotify:album:" + Id(gid), album);
    }

    static byte[] TrackResponse(byte gid, string name, bool full)
    {
        var track = new Pb.Track { Gid = Gid(gid), Name = name, Duration = 210_000 };
        if (full)
        {
            track.Artist.Add(new Pb.Artist { Gid = Gid(0xAA), Name = "Artist One" });
            track.Album = new Pb.Album { Gid = Gid(0xBB), Name = "Album One" };
        }
        return Wrap(Xm.ExtensionKind.TrackV4, TrackUri(gid), track);
    }

    static byte[] Wrap(Xm.ExtensionKind kind, string uri, IMessage payload)
    {
        var array = new Xm.EntityExtensionDataArray { ExtensionKind = kind };
        array.ExtensionData.Add(new Xm.EntityExtensionData
        {
            EntityUri = uri,
            Header = new Xm.EntityExtensionDataHeader { StatusCode = 200, OfflineTtlInSeconds = 3600 },
            ExtensionData = Any.Pack(payload),
        });
        var resp = new Xm.BatchedExtensionResponse();
        resp.ExtendedMetadata.Add(array);
        return resp.ToByteArray();
    }

    /// <summary>Merge several per-kind responses into ONE body — a mixed batch answers in a single response.</summary>
    static byte[] Merge(params byte[][] parts)
    {
        var resp = new Xm.BatchedExtensionResponse();
        foreach (var p in parts)
            resp.ExtendedMetadata.AddRange(Xm.BatchedExtensionResponse.Parser.ParseFrom(p).ExtendedMetadata);
        return resp.ToByteArray();
    }

    sealed class Rig : IDisposable
    {
        public required InMemoryStore Store { get; init; }
        public required FakeExchange Http { get; init; }
        public required List<HttpReq> Sent { get; init; }
        public required RecordingTraitPipeline Traits { get; init; }
        public required FakeEnvelopeFetch Envelopes { get; init; }
        public required SpotifyProviderHydrator Hydrator { get; init; }
        public required StoreLibrarySource Library { get; init; }
        public required HydrationPump Pump { get; init; }

        /// <summary>Every (uri, kind) query that reached the wire, in order — the unit of waste.</summary>
        public List<(string Uri, Xm.ExtensionKind Kind)> Queries =>
            Sent.SelectMany(r => Xm.BatchedEntityRequest.Parser.ParseFrom(HttpCompression.Gunzip(r.Body!)).EntityRequest)
                .SelectMany(e => e.Query.Select(q => (e.EntityUri, q.ExtensionKind)))
                .ToList();

        public void Dispose() { Library.Dispose(); Pump.Dispose(); }
    }

    static Rig Build(Func<HttpReq, int, byte[]> respond)
    {
        var sent = new List<HttpReq>();
        var http = new FakeExchange((req, call) =>
        {
            sent.Add(req);
            return new HttpResp(200, new Dictionary<string, string>(), respond(req, call));
        });
        var store = new InMemoryStore();
        var source = new ExtendedMetadataSource(http, () => "https://spclient.test", () => Ctx);
        var cache = new ExtensionEtagCache(source, () => Ctx);
        var traits = new RecordingTraitPipeline();
        var envelopes = new FakeEnvelopeFetch();
        var pump = new HydrationPump(CancellationToken.None);
        var policy = new TraitPolicy();
        var hydrator = new SpotifyProviderHydrator(store, () => Ctx, new XmCatalogFetch(cache, store), traits, policy,
            HydrationPolicy.Default,
            [
                new AlbumHydration(store, envelopes),
                new PlayableHydration(Wavee.Core.EntityKind.Track, store, envelopes),
            ], pump);
        return new Rig
        {
            Store = store, Http = http, Sent = sent, Traits = traits, Envelopes = envelopes,
            Hydrator = hydrator, Pump = pump,
            Library = new StoreLibrarySource(store, new SwitchableEntityHydrator(hydrator), OfflineOnlineCatalog.Instance),
        };
    }

    // ── album open ───────────────────────────────────────────────────────────────────────────────────────────────────

    // The headline number. A cold Rich album open (what DetailPage asks for) costs ONE catalogue POST — the AlbumV4 —
    // plus ONE trait pass carrying the whole bundle, and NO getAlbum: the Full envelope is a below-the-fold rung now.
    [Fact]
    public async Task AlbumOpenCold_IsOneCataloguePost_OneTraitPass_AndNoEnvelope()
    {
        using var rig = Build((_, _) => AlbumResponse(0x01, rows: 3, named: true));
        // The trait pass is what lands the 183 facets, so it is also what carries the album from Open to Rich.
        rig.Traits.OnEnsure = _ =>
        {
            if (rig.Store.GetAlbum(AlbumUri(0x01)) is { } a) rig.Store.UpsertAlbum(a with { Copyright = "© 2024 Label" });
        };

        var album = await rig.Library.GetAlbumAsync(AlbumUri(0x01), HydrationLevel.Rich);

        Assert.Equal(3, album!.Tracks!.Count);
        Assert.Equal(1, rig.Http.Calls);
        Assert.Empty(rig.Envelopes.AlbumCalls);
        var (uris, set, surface) = Assert.Single(rig.Traits.Calls);
        Assert.Equal(TraitSurface.AlbumOpen, surface);
        Assert.Equal(TraitSet.RowBundle | TraitSet.PlayCount | TraitSet.Publishing, set);
        Assert.Equal(4, uris.Count);   // the album itself (for 183) + its three rows
        Assert.Contains(AlbumUri(0x01), uris);
    }

    // …and the second open sends NOTHING. Presence (HydrationLevels.Of) says Rich is resident and the ledger says the
    // answer is still fresh, so the ladder is not entered at all — no POST, and not a second trait pass either.
    [Fact]
    public async Task AlbumSecondOpen_SendsNothing()
    {
        using var rig = Build((_, _) => AlbumResponse(0x01, rows: 3, named: true));
        rig.Traits.OnEnsure = _ =>
        {
            if (rig.Store.GetAlbum(AlbumUri(0x01)) is { } a) rig.Store.UpsertAlbum(a with { Copyright = "© 2024 Label" });
        };

        _ = await rig.Library.GetAlbumAsync(AlbumUri(0x01), HydrationLevel.Rich);
        int posts = rig.Http.Calls, traitPasses = rig.Traits.Calls.Count;
        _ = await rig.Library.GetAlbumAsync(AlbumUri(0x01), HydrationLevel.Rich);

        Assert.Equal(posts, rig.Http.Calls);
        Assert.Equal(traitPasses, rig.Traits.Calls.Count);
    }

    // Fix 3: an album with NO ©/℗ facet at all (the trait pass answers, but never sets Copyright/ReleaseDate) can
    // never reach Rich, so a real ledger seals it Exhausted for a day and every later open used to short-circuit
    // AlbumHydration.ContinueAsync wholesale — its own PlayCount/RowBundle trait step included. StoreLibrarySource's
    // companion trait ask keeps that bundle alive on a re-open regardless of the seal, WITHOUT doubling the very
    // first (cold) open's own trait pass.
    [Fact]
    public async Task AlbumWithNoPublishingFacet_ReOpenStillAsksForPlayCountAndRowBundle_ButNotTwiceOnTheFirstOpen()
    {
        using var rig = Build((_, _) => AlbumResponse(0x01, rows: 1, named: true));
        // Deliberately does NOT set Copyright/ReleaseDate — the genuine "this release carries no publishing facet" album.

        _ = await rig.Library.GetAlbumAsync(AlbumUri(0x01), HydrationLevel.Rich);

        Assert.Single(rig.Traits.Calls);   // the ladder's own Rich-branch ask — exactly one, on the first open
        Assert.Equal(HydrationLevel.Open, HydrationLevels.Of(rig.Store.GetAlbum(AlbumUri(0x01))));   // never reaches Rich

        _ = await rig.Library.GetAlbumAsync(AlbumUri(0x01), HydrationLevel.Rich);

        Assert.Equal(1, rig.Http.Calls);            // the catalogue seal still holds — no second AlbumV4 POST
        Assert.Equal(2, rig.Traits.Calls.Count);     // …but the companion ask fired on the RE-open, not starved by it
        var (uris, set, surface) = rig.Traits.Calls[1];
        Assert.Equal(TraitSurface.AlbumOpen, surface);
        Assert.Equal(TraitSet.RowBundle | TraitSet.PlayCount | TraitSet.Publishing, set);
        Assert.Contains(AlbumUri(0x01), uris);
    }

    // Gid-only disc rows (how AlbumV4 lands a tracklist the album entity carried no names for) cost exactly ONE extra
    // POST: the repair is a single TrackV4 batch for every unnamed row in the wave, never one request per row.
    [Fact]
    public async Task AlbumWithUnnamedDiscRows_RepairsInOneBatchedPost_NotOnePerRow()
    {
        using var rig = Build((req, call) => call == 1
            ? AlbumResponse(0x01, rows: 3, named: false)
            : Merge(TrackResponse(0x10, "Song 0", full: true),
                    TrackResponse(0x11, "Song 1", full: true),
                    TrackResponse(0x12, "Song 2", full: true)));

        var album = await rig.Library.GetAlbumAsync(AlbumUri(0x01), HydrationLevel.Open);

        Assert.Equal(2, rig.Http.Calls);                       // AlbumV4, then ONE TrackV4 repair batch
        Assert.Empty(rig.Envelopes.AlbumCalls);                // V4 got there → no getAlbum fallback
        Assert.All(album!.Tracks!, t => Assert.NotEqual("", t.Title));
        var repair = Xm.BatchedEntityRequest.Parser.ParseFrom(HttpCompression.Gunzip(rig.Sent[1].Body!));
        Assert.Equal(3, repair.EntityRequest.Count);           // three uris in ONE request
        Assert.All(repair.EntityRequest, e => Assert.Equal(Xm.ExtensionKind.TrackV4, e.Query.Single().ExtensionKind));
    }

    // The invariant behind every count above: within a session, no (uri, kind) pair is ever asked for twice. This is
    // what the ledger + the etag cache buy, and it is the thing six per-service negative memos used to approximate.
    [Fact]
    public async Task NoUriKindPairIsRequestedTwice_AcrossOverlappingSurfaces()
    {
        using var rig = Build((req, call) => call == 1
            ? AlbumResponse(0x01, rows: 3, named: false)
            : Merge(TrackResponse(0x10, "Song 0", full: true),
                    TrackResponse(0x11, "Song 1", full: true),
                    TrackResponse(0x12, "Song 2", full: true)));

        // Three surfaces over the same entities: the page open, the play path, and a re-open.
        _ = await rig.Library.GetAlbumAsync(AlbumUri(0x01), HydrationLevel.Open);
        await foreach (var _ in rig.Library.StreamTracksAsync(AlbumUri(0x01))) { }
        _ = await rig.Library.GetAlbumAsync(AlbumUri(0x01), HydrationLevel.Open);
        await HydrationTestSupport.DrainAsync(rig.Pump);

        var queries = rig.Queries;
        Assert.Equal(queries.Count, queries.Distinct().Count());
    }

    // ── now playing ──────────────────────────────────────────────────────────────────────────────────────────────────

    // The thin now-playing row: TrackV4 answers with a title and nothing else, so the ladder pays getTrack ONCE. When
    // that still cannot reach Open, the level is sealed EXHAUSTED — which is what replaces the old heartbeat gate, and
    // is why a cluster that re-pushes the same thin row every second does not re-fetch it every second.
    [Fact]
    public async Task NowPlayingThinRow_ResolvesOnce_ThenNeverRefiresWithinTheTtl()
    {
        using var rig = Build((_, _) => TrackResponse(0x30, "Broken Angel", full: false));
        var opts = new HydrationOptions(Surface: TraitSurface.NowPlaying, Priority: 1);

        var first = await rig.Hydrator.EnsureAsync(TrackUri(0x30), HydrationLevel.Open, opts);

        Assert.Equal(HydrationLevel.Identity, first.Reached);   // title only — the row is still thin
        Assert.Equal(1, rig.Http.Calls);
        Assert.Equal(TrackUri(0x30), Assert.Single(rig.Envelopes.TrackCalls));

        // Five more heartbeats carrying the same thin row.
        for (int i = 0; i < 5; i++) await rig.Hydrator.EnsureAsync(TrackUri(0x30), HydrationLevel.Open, opts);

        Assert.Equal(1, rig.Http.Calls);
        Assert.Single(rig.Envelopes.TrackCalls);
    }

    // …and when getTrack CAN repair it, the row reaches Open and the seal is a plain "reached": still one of each.
    [Fact]
    public async Task NowPlayingThinRow_RepairedByGetTrack_ReachesOpenWithOneOfEach()
    {
        using var rig = Build((_, _) => TrackResponse(0x30, "Broken Angel", full: false));
        rig.Envelopes.OnTrack = uri => new Track("t", uri, "Broken Angel",
            [new ArtistRef("a1", "spotify:artist:a1", "Arash")],
            new AlbumRef("al1", "spotify:album:al1", "SUPERMAN"), 180_000, false, new Image("https://i.scdn.co/image/c"));

        var outcome = await rig.Hydrator.EnsureAsync(TrackUri(0x30), HydrationLevel.Open,
            new HydrationOptions(Surface: TraitSurface.NowPlaying, Priority: 1));

        Assert.True(outcome.Ok);
        Assert.Equal(1, rig.Http.Calls);
        Assert.Single(rig.Envelopes.TrackCalls);

        await rig.Hydrator.EnsureAsync(TrackUri(0x30), HydrationLevel.Open, default);
        Assert.Equal(1, rig.Http.Calls);
        Assert.Single(rig.Envelopes.TrackCalls);
    }

    // ── the REAL trait pipeline (P2) ─────────────────────────────────────────────────────────────────────────────────
    // Everything above runs the trait door as a recording double, because those tests are about the LADDER's request
    // count. These run the real TraitPipeline over the real projector registry, so the assertion is the one the design
    // is actually sold on: the six per-row facets and the album's ©/℗ ride ONE POST, and a second surface asking for
    // uris the session already resolved sends nothing at all.

    sealed class RealRig : IDisposable
    {
        public required InMemoryStore Store { get; init; }
        public required FakeExchange Http { get; init; }
        public required List<HttpReq> Sent { get; init; }
        public required FakeEnvelopeFetch Envelopes { get; init; }
        public required SpotifyProviderHydrator Hydrator { get; init; }
        public required StoreLibrarySource Library { get; init; }
        public required HydrationPump Pump { get; init; }

        /// <summary>The (uri → kinds) map of ONE decoded POST body.</summary>
        public Dictionary<string, HashSet<Xm.ExtensionKind>> Post(int index)
        {
            var map = new Dictionary<string, HashSet<Xm.ExtensionKind>>(StringComparer.Ordinal);
            var body = Xm.BatchedEntityRequest.Parser.ParseFrom(HttpCompression.Gunzip(Sent[index].Body!));
            foreach (var entity in body.EntityRequest)
            {
                if (!map.TryGetValue(entity.EntityUri, out var kinds))
                    map[entity.EntityUri] = kinds = new HashSet<Xm.ExtensionKind>();
                foreach (var query in entity.Query) kinds.Add(query.ExtensionKind);
            }
            return map;
        }

        public void Dispose() { Library.Dispose(); Pump.Dispose(); }
    }

    /// <summary>Answers every (uri, kind) in the request with a 404 — the shape a trait ask takes for entities the wire
    /// has no facet for, and the one that exercises the negative memo the re-ask assertions depend on.</summary>
    static byte[] TraitMisses(HttpReq req)
    {
        var body = Xm.BatchedEntityRequest.Parser.ParseFrom(HttpCompression.Gunzip(req.Body!));
        var resp = new Xm.BatchedExtensionResponse();
        var byKind = new Dictionary<Xm.ExtensionKind, Xm.EntityExtensionDataArray>();
        foreach (var entity in body.EntityRequest)
            foreach (var query in entity.Query)
            {
                if (!byKind.TryGetValue(query.ExtensionKind, out var array))
                {
                    array = new Xm.EntityExtensionDataArray { ExtensionKind = query.ExtensionKind };
                    byKind[query.ExtensionKind] = array;
                    resp.ExtendedMetadata.Add(array);
                }
                array.ExtensionData.Add(new Xm.EntityExtensionData
                {
                    EntityUri = entity.EntityUri,
                    Header = new Xm.EntityExtensionDataHeader { StatusCode = 404, OfflineTtlInSeconds = 60 },
                });
            }
        return resp.ToByteArray();
    }

    static RealRig BuildReal(Func<HttpReq, int, byte[]>? respond = null)
    {
        var sent = new List<HttpReq>();
        var http = new FakeExchange((req, call) =>
        {
            sent.Add(req);
            return new HttpResp(200, new Dictionary<string, string>(), respond?.Invoke(req, call) ?? TraitMisses(req));
        });
        var store = new InMemoryStore();
        var source = new ExtendedMetadataSource(http, () => "https://spclient.test", () => Ctx);
        var cache = new ExtensionEtagCache(source, () => Ctx);
        var negatives = new NegativeMemo();
        var reader = new ExtensionReader(cache, negatives);
        // No CoverColorPlane in a headless test: kind 179 is still ASKED (that is the request-count claim), it simply
        // projects nothing — which is exactly the offline/no-plane arm the projector documents.
        var traits = new TraitPipeline(store, cache, negatives, TraitProjectors.Default(reader, () => null));
        var envelopes = new FakeEnvelopeFetch();
        var pump = new HydrationPump(CancellationToken.None);
        var hydrator = new SpotifyProviderHydrator(store, () => Ctx, new XmCatalogFetch(cache, store), traits,
            new TraitPolicy(), HydrationPolicy.Default,
            [
                new AlbumHydration(store, envelopes),
                new PlayableHydration(Wavee.Core.EntityKind.Track, store, envelopes),
                new PlayableHydration(Wavee.Core.EntityKind.Episode, store, envelopes),
            ], pump);
        return new RealRig
        {
            Store = store, Http = http, Sent = sent, Envelopes = envelopes, Hydrator = hydrator, Pump = pump,
            Library = new StoreLibrarySource(store, new SwitchableEntityHydrator(hydrator), OfflineOnlineCatalog.Instance),
        };
    }

    // THE headline: a cold album open is 1 catalogue POST + exactly ONE trait POST, and that trait POST carries the six
    // per-row kinds under each track and 183 under the ALBUM. Six services' worth of requests, in two.
    [Fact]
    public async Task AlbumOpenCold_IsOneCataloguePost_PlusExactlyOneTraitPost_CarryingEveryKind()
    {
        using var rig = BuildReal((req, call) => call == 1 ? AlbumResponse(0x01, rows: 3, named: true) : TraitMisses(req));

        _ = await rig.Library.GetAlbumAsync(AlbumUri(0x01), HydrationLevel.Rich);

        Assert.Equal(2, rig.Http.Calls);            // AlbumV4, then the ONE trait POST
        Assert.Empty(rig.Envelopes.AlbumCalls);     // …and no getAlbum

        var post = rig.Post(1);
        Assert.Equal(4, post.Count);                // the album + its three rows, in ONE body
        foreach (var track in rig.Store.GetAlbum(AlbumUri(0x01))!.Tracks!)
        {
            var kinds = post[track.Uri];
            Assert.Equal(
                [Xm.ExtensionKind.VideoAssociations, Xm.ExtensionKind.ConsumptionExperienceTrait,
                 Xm.ExtensionKind.AudioAttributesV2, Xm.ExtensionKind.TrackDescriptor,
                 Xm.ExtensionKind.VisualIdentityTrait, Xm.ExtensionKind.OnPlatformReputationTrait],
                kinds.OrderBy(k => (int)k).Distinct().OrderBy(k => (int)k).ToHashSet());
            Assert.DoesNotContain(Xm.ExtensionKind.PublishingMetadataTrait, kinds);   // 183 is an ALBUM fact
        }
        // …and 183 rides the same body under the album uri — never a second POST, and never fused into step 0 as well.
        Assert.Contains(Xm.ExtensionKind.PublishingMetadataTrait, post[AlbumUri(0x01)]);
        Assert.DoesNotContain(Xm.ExtensionKind.PublishingMetadataTrait, rig.Post(0)[AlbumUri(0x01)]);
    }

    // A Liked/playlist wave is ONE trait POST per MaxEntitiesPerRequest rows — the cap that used to be copied into
    // seven services (and missing from two of them, which is how a 10k list went out as one request body).
    [Fact]
    public async Task LikedSongsWave_IsOneTraitPostPerThreeHundredRows()
    {
        using var rig = BuildReal();
        var uris = new List<string>(700);
        for (int i = 0; i < 700; i++) uris.Add("spotify:track:" + i.ToString("D22"));

        await rig.Hydrator.EnsureTraitsAsync(uris, TraitSurface.LikedSongs);

        Assert.Equal(300, MetadataChunking.MaxEntitiesPerRequest);
        Assert.Equal(3, rig.Http.Calls);                       // 300 + 300 + 100
        Assert.Equal(300, rig.Post(0).Count);
        Assert.Equal(100, rig.Post(2).Count);
    }

    // A queue bump re-asks for the DELTA only: the memo (shared with the display-only reader) answers for everything
    // the session already resolved, so re-sending the whole queue costs one small POST instead of a full one.
    [Fact]
    public async Task QueueBump_AsksOnlyForTheUrisItDidNotAlreadyResolve()
    {
        using var rig = BuildReal();
        string a = TrackUri(0x41), b = TrackUri(0x42), c = TrackUri(0x43);

        await rig.Hydrator.EnsureTraitsAsync([a, b], TraitSurface.Queue);
        Assert.Equal(1, rig.Http.Calls);

        await rig.Hydrator.EnsureTraitsAsync([a, b, c], TraitSurface.Queue);

        Assert.Equal(2, rig.Http.Calls);
        Assert.Equal([c], rig.Post(1).Keys.ToArray());   // a and b are memoized negatives — nothing re-asked
    }

    // An episode in a playlist is ASKED ONCE for the per-playable kinds (the probe never covered episodes, and guessing
    // "never" is what left every podcast row in the app without a single trait) and the 404 is then honoured forever.
    [Fact]
    public async Task Episode_InAPlaylist_GetsTheAskOnceTraits_ThenNeverAgain()
    {
        using var rig = BuildReal();
        const string episode = "spotify:episode:0000000000000000000001";

        await rig.Hydrator.EnsureTraitsAsync([episode], TraitSurface.PlaylistOpen);

        Assert.Equal(1, rig.Http.Calls);
        var kinds = rig.Post(0)[episode];
        Assert.Contains(Xm.ExtensionKind.VideoAssociations, kinds);
        Assert.Contains(Xm.ExtensionKind.AudioAttributesV2, kinds);
        Assert.Contains(Xm.ExtensionKind.TrackDescriptor, kinds);
        Assert.Contains(Xm.ExtensionKind.VisualIdentityTrait, kinds);
        Assert.Contains(Xm.ExtensionKind.OnPlatformReputationTrait, kinds);
        Assert.DoesNotContain(Xm.ExtensionKind.PublishingMetadataTrait, kinds);

        await rig.Hydrator.EnsureTraitsAsync([episode], TraitSurface.PlaylistOpen);
        Assert.Equal(1, rig.Http.Calls);   // the 404s are memoized — one request per session, as designed
    }
}
