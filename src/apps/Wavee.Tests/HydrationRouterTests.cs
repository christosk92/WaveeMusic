using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// ── P4-A: the multi-source hydration router (docs/plans/wavee/hydration-facade-design.md §2.1, architecture.md §4.3) ──
// `Services.Hydrator` is a HydrationRouter over the SourceRegistry: ownership for hydration is the SAME question
// single-item reads ask (`OwnerOf` = the first Catalog-capable source whose `Owns` claims the uri), so there is no
// second notion of "who owns this uri". These tests pin the three properties that matters:
//   • a mixed batch is SPLIT — the Spotify ladder never sees a `local:` uri, and a `local:` uri never costs a POST;
//   • an unowned uri is NotOwned/Unsupported rather than an exception, so the rest of the batch still completes;
//   • ownership is decided in REGISTRY order, and the fallback (FakeSource) is a READ capability, not an owner.

/// <summary>A hydrator that records everything it was asked and answers a scripted status. One per fake source, so a
/// test can assert exactly which uris rode which source's ladder.</summary>
public sealed class RoutingRecorder : IEntityHydrator
{
    readonly List<string>? _log;
    readonly string _id;
    readonly HydrationStatus _status;

    public RoutingRecorder(string id, HydrationStatus status = HydrationStatus.Reached, List<string>? log = null)
        => (_id, _status, _log) = (id, status, log);

    public int Calls { get; private set; }
    public List<string> Asked { get; } = new();
    public List<string> Invalidated { get; } = new();
    public List<TraitSurface> Surfaces { get; } = new();
    public List<TraitSet> TraitSets { get; } = new();
    /// <summary>The exact list instance the router handed over (the single-owner fast path forwards the caller's own).</summary>
    public IReadOnlyList<string>? LastList { get; private set; }

    public HydrationLevel LevelOf(string uri) => HydrationLevel.Full;

    public Task<HydrationOutcome> EnsureAsync(string uri, HydrationLevel level,
        HydrationOptions opts = default, CancellationToken ct = default)
    {
        Calls++; Asked.Add(uri); _log?.Add(_id);
        return Task.FromResult(new HydrationOutcome(
            _status == HydrationStatus.Reached ? level : HydrationLevel.None, _status));
    }

    public Task<HydrationBatchOutcome> EnsureManyAsync(IReadOnlyList<string> uris, HydrationLevel level,
        HydrationOptions opts = default, CancellationToken ct = default)
    {
        Calls++; Asked.AddRange(uris); LastList = uris; _log?.Add(_id);
        return _status == HydrationStatus.Reached
            ? Task.FromResult(new HydrationBatchOutcome(uris.ToArray(), Array.Empty<string>(), _status))
            : Task.FromResult(new HydrationBatchOutcome(Array.Empty<string>(), uris.ToArray(), _status));
    }

    public Task EnsureTraitsAsync(IReadOnlyList<string> uris, TraitSurface surface, CancellationToken ct = default)
    {
        Calls++; Asked.AddRange(uris); LastList = uris; Surfaces.Add(surface); _log?.Add(_id);
        return Task.CompletedTask;
    }

    public Task EnsureTraitsAsync(IReadOnlyList<string> uris, TraitSet traits, TraitSurface surface, CancellationToken ct = default)
    {
        Calls++; Asked.AddRange(uris); LastList = uris; Surfaces.Add(surface); TraitSets.Add(traits); _log?.Add(_id);
        return Task.CompletedTask;
    }

    public void Invalidate(string uri) { Invalidated.Add(uri); _log?.Add(_id); }
}

/// <summary>A catalog source that owns one provider namespace and routes hydration to its own recorder. Everything it
/// is not asked in these tests is deliberately unsupported so a stray call is loud.</summary>
public sealed class RoutingSource : ICatalogSource
{
    readonly string _provider;
    public RoutingSource(string id, string provider, IEntityHydrator hydrator,
        SourceCapabilities caps = SourceCapabilities.Catalog)
        => (Id, _provider, Hydrator, Capabilities) = (id, provider, hydrator, caps);

    public string Id { get; }
    public bool Owns(string uri) => EntityUri.Parse(uri).Provider == _provider;
    public SourceCapabilities Capabilities { get; }
    public IEntityHydrator Hydrator { get; }

    public Task<Playlist?> GetPlaylistAsync(string uri, HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<Album?> GetAlbumAsync(string uri, HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<Artist?> GetArtistAsync(string uri, HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default) => throw new NotSupportedException();
    public IAsyncEnumerable<TrackPage> StreamTracksAsync(string contextUri, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<LibraryItem>> GetLibraryAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<PlaylistSummary>> GetPlaylistsAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<Album>> GetAlbumsAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<Artist>> GetArtistsAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<Track>> GetLikedSongsAsync(HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<SearchResults> SearchAsync(string query, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<HomeContribution> GetHomeAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<LibraryStats> GetStatsAsync(CancellationToken ct = default) => throw new NotSupportedException();
}

public class HydrationRouterTests
{
    const string Unowned = "wavee:mystery:9";   // a wavee: tail no provider claims → EntityProviders.None

    static (HydrationRouter Router, RoutingRecorder Spotify, RoutingRecorder Local, List<string> Log) Rig(
        HydrationStatus spotify = HydrationStatus.Reached, HydrationStatus local = HydrationStatus.Reached)
    {
        var log = new List<string>();
        var sp = new RoutingRecorder("spotify", spotify, log);
        var lo = new RoutingRecorder("local", local, log);
        var reg = new SourceRegistry(new ISource[]
        {
            new RoutingSource("spotify", EntityProviders.Spotify, sp),
            new RoutingSource("local", EntityProviders.Local, lo),
        });
        return (new HydrationRouter(reg), sp, lo, log);
    }

    [Fact]
    public async Task MixedBatch_IsSplitByOwner_AndForwardedInRegistryOrder()
    {
        var (router, sp, lo, log) = Rig();
        // Deliberately interleaved, and deliberately starting with the SECOND-registered source's uri.
        var uris = new[] { "local:track:1", "spotify:track:a", Unowned, "spotify:track:b", "local:track:2" };

        var outcome = await router.EnsureManyAsync(uris, HydrationLevel.Open);

        Assert.Equal(new[] { "spotify:track:a", "spotify:track:b" }, sp.Asked);   // first-seen order INSIDE the group
        Assert.Equal(new[] { "local:track:1", "local:track:2" }, lo.Asked);
        Assert.Equal(new[] { "spotify", "local" }, log);                          // …groups forwarded in REGISTRY order
        Assert.Equal(4, outcome.Reached.Count);
        Assert.Equal(new[] { Unowned }, outcome.Missing);                         // the unowned group, and only it
        Assert.Equal(HydrationStatus.Unsupported, outcome.Status);                // = the worst of (Reached, Unsupported)
    }

    [Fact]
    public async Task UnownedUri_AnswersNotOwned_Unsupported()
    {
        var (router, sp, lo, _) = Rig();

        Assert.Same(NotOwnedEntityHydrator.Instance, router.HydratorFor(Unowned));
        Assert.Equal(HydrationLevel.None, router.LevelOf(Unowned));
        var one = await router.EnsureAsync(Unowned, HydrationLevel.Open);
        Assert.Equal(HydrationStatus.Unsupported, one.Status);
        Assert.Equal(HydrationLevel.None, one.Reached);
        Assert.False(one.Ok);
        Assert.Equal(0, sp.Calls);
        Assert.Equal(0, lo.Calls);
    }

    [Fact]
    public async Task SpotifyLadder_OnlyEverSeesSpotifyUris()
    {
        var (router, sp, lo, _) = Rig();

        await router.EnsureAsync("local:track:1", HydrationLevel.Open);
        await router.EnsureManyAsync(new[] { "local:track:2", Unowned }, HydrationLevel.Open);
        await router.EnsureTraitsAsync(new[] { "local:track:3" }, TraitSurface.Queue);
        router.Invalidate("local:track:4");

        Assert.Equal(0, sp.Calls);
        Assert.Empty(sp.Asked);
        Assert.Empty(sp.Invalidated);
        Assert.Equal(new[] { "local:track:4" }, lo.Invalidated);
    }

    [Fact]
    public async Task LocalUri_ReachesTheCompleteHydrator_WithZeroSpotifyCalls()
    {
        // The REAL peer source (its Hydrator is the ICatalogSource default: complete-at-construction ⇒ every rung is
        // already there), beside a recording Spotify source — the shape Services.CreateReal now wires.
        var sp = new RoutingRecorder("spotify");
        var reg = new SourceRegistry(new ISource[]
        {
            new RoutingSource("spotify", EntityProviders.Spotify, sp),
            new LocalSource(),
        });
        var router = new HydrationRouter(reg);

        var one = await router.EnsureAsync("local:track:1", HydrationLevel.Full);
        Assert.True(one.Ok);
        Assert.Equal(HydrationStatus.Reached, one.Status);
        Assert.Equal(HydrationLevel.Full, router.LevelOf("wavee:local:file:abc"));

        var many = await router.EnsureManyAsync(new[] { "local:track:1", "wavee:local:file:abc" }, HydrationLevel.Open);
        Assert.Equal(HydrationStatus.Reached, many.Status);
        Assert.Equal(2, many.Reached.Count);
        Assert.Empty(many.Missing);
        Assert.Equal(0, sp.Calls);   // a local file never costs a spclient round trip
    }

    [Fact]
    public async Task EnsureTraits_RoutesPerOwner_BothOverloads()
    {
        var (router, sp, lo, log) = Rig();
        var uris = new[] { "local:track:1", "spotify:track:a", Unowned };

        await router.EnsureTraitsAsync(uris, TraitSurface.PlaylistOpen);
        Assert.Equal(new[] { "spotify:track:a" }, sp.Asked);
        Assert.Equal(new[] { "local:track:1" }, lo.Asked);
        Assert.Equal(new[] { TraitSurface.PlaylistOpen }, sp.Surfaces);
        Assert.Equal(new[] { "spotify", "local" }, log);

        await router.EnsureTraitsAsync(uris, TraitSet.Video, TraitSurface.NowPlaying);
        Assert.Equal(new[] { TraitSet.Video }, sp.TraitSets);
        Assert.Equal(new[] { TraitSet.Video }, lo.TraitSets);
        Assert.Equal(new[] { "spotify:track:a", "spotify:track:a" }, sp.Asked);
    }

    [Fact]
    public async Task SingleOwner_ForwardsTheCallersOwnList_Unsplit()
    {
        var (router, sp, _, _) = Rig();
        var uris = new[] { "spotify:track:a", "spotify:track:b" };

        await router.EnsureManyAsync(uris, HydrationLevel.Open);
        Assert.Same(uris, sp.LastList);      // no copy, no regrouping — one owner is the hot path

        await router.EnsureTraitsAsync(uris, TraitSurface.Queue);
        Assert.Same(uris, sp.LastList);
    }

    [Fact]
    public async Task EmptyBatch_IsReached_AndAsksNobody()
    {
        var (router, sp, lo, _) = Rig();
        var outcome = await router.EnsureManyAsync(Array.Empty<string>(), HydrationLevel.Open);

        Assert.Equal(HydrationStatus.Reached, outcome.Status);
        Assert.Empty(outcome.Reached);
        Assert.Empty(outcome.Missing);
        await router.EnsureTraitsAsync(Array.Empty<string>(), TraitSurface.Queue);
        Assert.Equal(0, sp.Calls + lo.Calls);
    }

    [Fact]
    public async Task MergedStatus_IsTheWorstOfTheGroups()
    {
        var (router, _, _, _) = Rig(spotify: HydrationStatus.Partial, local: HydrationStatus.Failed);
        var mixed = await router.EnsureManyAsync(new[] { "spotify:track:a", "local:track:1" }, HydrationLevel.Open);
        Assert.Equal(HydrationStatus.Failed, mixed.Status);      // Failed is the only "ask again" — it wins
        Assert.Empty(mixed.Reached);
        Assert.Equal(2, mixed.Missing.Count);

        var (r2, _, _, _) = Rig(spotify: HydrationStatus.Reached, local: HydrationStatus.Partial);
        var p = await r2.EnsureManyAsync(new[] { "spotify:track:a", "local:track:1" }, HydrationLevel.Open);
        Assert.Equal(HydrationStatus.Partial, p.Status);
        Assert.Equal(new[] { "spotify:track:a" }, p.Reached);
        Assert.Equal(new[] { "local:track:1" }, p.Missing);
    }

    [Fact]
    public void Invalidate_RoutesToTheOwner_AndIsANoOpForAnUnownedUri()
    {
        var (router, sp, lo, _) = Rig();
        router.Invalidate("spotify:album:a");
        router.Invalidate(Unowned);              // must not throw — NotOwnedEntityHydrator swallows it

        Assert.Equal(new[] { "spotify:album:a" }, sp.Invalidated);
        Assert.Empty(lo.Invalidated);
    }

    [Fact]
    public void Router_RefusesANullRegistry()
        => Assert.Throws<ArgumentNullException>(() => new HydrationRouter(null!));
}

// ── FakeSource ownership: `fake:` only, plus the Fallback CAPABILITY ─────────────────────────────────────────────────
public class FakeSourceOwnsTests
{
    static readonly FakeSource Fake = new();

    [Theory]
    [InlineData("fake:album:1")]
    [InlineData("fake:track:9")]
    [InlineData("al7")]     // the bare legacy ids FakeData mints
    [InlineData("tr3")]
    [InlineData("pl2")]
    [InlineData("ar11")]
    public void OwnsItsOwnNamespace(string uri) => Assert.True(Fake.Owns(uri));

    [Theory]
    [InlineData("spotify:track:1")]
    [InlineData("local:file:x")]
    [InlineData("wavee:local:file:abc")]
    [InlineData("wavee:playlist:1")]
    [InlineData("wavee:show:1")]
    [InlineData("wavee:episode:1")]
    [InlineData("wavee:mystery:9")]
    public void DoesNotOwnAPeersNamespace_NorTheUnclaimed(string uri) => Assert.False(Fake.Owns(uri));

    [Fact]
    public void DeclaresCatalogAndFallback()
    {
        Assert.True(Fake.Capabilities.HasFlag(SourceCapabilities.Catalog));
        Assert.True(Fake.Capabilities.HasFlag(SourceCapabilities.Fallback));
    }

    [Fact]
    public void ThePeerSourcesKeepTheirOwnUris_InTheDemoRegistry()
    {
        var reg = DemoRegistry();
        Assert.Equal("local", reg.OwnerOf("local:track:1")!.Id);
        Assert.Equal("local", reg.OwnerOf("wavee:local:file:abc")!.Id);
        Assert.Equal("user-playlists", reg.OwnerOf("wavee:playlist:1")!.Id);
        Assert.Equal("fake", reg.OwnerOf("fake:album:1")!.Id);
        Assert.Null(reg.OwnerOf("wavee:mystery:9"));   // unowned — a READ falls back, routing does not
    }

    [Fact]
    public async Task AnUnownedUriStillOpens_ThroughTheFallbackCapability()
    {
        var cat = new AggregateCatalog(DemoRegistry());

        var album = await cat.GetAlbumAsync("wavee:mystery:9");
        Assert.False(string.IsNullOrEmpty(album.Name));       // NOT the minimal (id, id, "") empty shape
        Assert.NotEmpty(album.Tracks ?? Array.Empty<Track>());

        // …and an owned uri whose owner has NO data (a wavee:playlist: the session never created) falls through too.
        var playlist = await cat.GetPlaylistAsync("wavee:playlist:999");
        Assert.False(string.IsNullOrEmpty(playlist.Name));
    }

    /// <summary>The demo backend's catalog sources in Services.CreateFake order, minus the export (which needs a
    /// loaded fixture) — the fallback is last, exactly as registered.</summary>
    static SourceRegistry DemoRegistry() => new(new ISource[]
    {
        new LocalSource(),
        new UserPlaylistSource(),
        new FakeSource(),
        new FakePodcastSource(),
    });
}

// ── AggregateCatalog: the explicit last-resort step ──────────────────────────────────────────────────────────────────
public class AggregateFallbackTests
{
    [Fact]
    public async Task NoFallbackSource_KeepsTheEmptyShape()
    {
        // The REAL backend's shape: no source declares Fallback, so an unowned uri must NOT invent an entity.
        var cat = new AggregateCatalog(new SourceRegistry(new ISource[] { new LocalSource() }));

        var album = await cat.GetAlbumAsync("wavee:mystery:9");
        Assert.Equal("wavee:mystery:9", album.Id);            // the minimal (id, id, "") shape, unchanged
        Assert.Equal("", album.Name);
        var artist = await cat.GetArtistAsync("wavee:mystery:9");
        Assert.Equal("wavee:mystery:9", artist.Id);
        Assert.Equal("", artist.Name);
        var disc = await cat.GetDiscographyAsync("wavee:mystery:9", DiscographyKind.Albums, 0, 10);
        Assert.Empty(disc.Items);
        Assert.Equal(0, disc.Total);
    }

    [Fact]
    public async Task TheFallbackIsNeverAskedTwice_ForAUriItOwns()
    {
        var counting = new CountingFallback();
        var cat = new AggregateCatalog(new SourceRegistry(new ISource[] { counting }));

        await cat.GetAlbumAsync("fake:album:1");    // it OWNS this → answered in the loop, not re-asked as the fallback
        Assert.Equal(1, counting.AlbumCalls);

        await cat.GetAlbumAsync("wavee:mystery:9"); // unowned → exactly one fallback call
        Assert.Equal(2, counting.AlbumCalls);
    }

    [Fact]
    public async Task TheFallbackStreamsAContextNoCatalogSourceOwns()
    {
        // A synthetic show: FakePodcastSource declares Podcasts only, so it is not a CATALOG owner and OwnerOf answers
        // null — before P4-A the FakeSource catch-all silently covered this. The Fallback capability is what keeps it
        // playable now, without FakeSource claiming a namespace it does not hold.
        var cat = new AggregateCatalog(new SourceRegistry(new ISource[]
            { new LocalSource(), new FakeSource(), new FakePodcastSource() }));

        int tracks = 0;
        await foreach (var page in cat.StreamTracksAsync("wavee:show:1")) tracks += page.Tracks.Count;
        Assert.True(tracks > 0);

        // …and with no fallback registered it is cleanly empty (the real backend must not synthesize a context).
        int none = 0;
        await foreach (var page in new AggregateCatalog(new SourceRegistry(new ISource[] { new LocalSource() }))
                           .StreamTracksAsync("wavee:show:1")) none += page.Tracks.Count;
        Assert.Equal(0, none);
    }

    [Fact]
    public async Task TheFallbackAnswersDiscographyForAnUnownedArtist()
    {
        var reg = new SourceRegistry(new ISource[] { new LocalSource(), new FakeSource() });
        var page = await new AggregateCatalog(reg).GetDiscographyAsync("wavee:mystery:9", DiscographyKind.Albums, 0, 10);
        Assert.NotEmpty(page.Items);
        Assert.True(page.Total > 0);
    }

    sealed class CountingFallback : ICatalogSource
    {
        readonly FakeSource _inner = new();
        public int AlbumCalls { get; private set; }

        public string Id => "counting-fallback";
        public bool Owns(string uri) => _inner.Owns(uri);
        public SourceCapabilities Capabilities => SourceCapabilities.Catalog | SourceCapabilities.Fallback;

        public Task<Album?> GetAlbumAsync(string uri, HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default)
        {
            AlbumCalls++;
            return _inner.GetAlbumAsync(uri, level, ct);
        }

        public Task<Playlist?> GetPlaylistAsync(string uri, HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default) => _inner.GetPlaylistAsync(uri, level, ct);
        public Task<Artist?> GetArtistAsync(string uri, HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default) => _inner.GetArtistAsync(uri, level, ct);
        public IAsyncEnumerable<TrackPage> StreamTracksAsync(string contextUri, CancellationToken ct = default) => _inner.StreamTracksAsync(contextUri, ct);
        public Task<IReadOnlyList<LibraryItem>> GetLibraryAsync(CancellationToken ct = default) => _inner.GetLibraryAsync(ct);
        public Task<IReadOnlyList<PlaylistSummary>> GetPlaylistsAsync(CancellationToken ct = default) => _inner.GetPlaylistsAsync(ct);
        public Task<IReadOnlyList<Album>> GetAlbumsAsync(CancellationToken ct = default) => _inner.GetAlbumsAsync(ct);
        public Task<IReadOnlyList<Artist>> GetArtistsAsync(CancellationToken ct = default) => _inner.GetArtistsAsync(ct);
        public Task<IReadOnlyList<Track>> GetLikedSongsAsync(HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default) => _inner.GetLikedSongsAsync(level, ct);
        public Task<SearchResults> SearchAsync(string query, CancellationToken ct = default) => _inner.SearchAsync(query, ct);
        public Task<HomeContribution> GetHomeAsync(CancellationToken ct = default) => _inner.GetHomeAsync(ct);
        public Task<LibraryStats> GetStatsAsync(CancellationToken ct = default) => _inner.GetStatsAsync(ct);
    }
}
