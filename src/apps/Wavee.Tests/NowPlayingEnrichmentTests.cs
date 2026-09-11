using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// The now-playing row's own hydration (design §1.5). A cluster player_state is routinely thin, so the projection raises
// the current playable to Open through THE façade and folds the store row back in. The properties that matter are all
// "how often": it must resolve a uri ONCE and then stop — because MaybeEnrichCurrent runs on every cluster push, every
// local snapshot and every playback event, so anything that stays "thin" after a resolve becomes a per-heartbeat loop
// (a façade call, a store read, and a Changes broadcast that wakes the player bar and the queue panel).
public class NowPlayingEnrichmentTests
{
    sealed class CountingHydrator : IEntityHydrator
    {
        public int Ensures;
        public HydrationLevel LevelOf(string uri) => HydrationLevel.None;

        public Task<HydrationOutcome> EnsureAsync(string uri, HydrationLevel level, HydrationOptions opts = default,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref Ensures);
            return Task.FromResult(new HydrationOutcome(HydrationLevel.Open, HydrationStatus.Reached));
        }

        public Task<HydrationBatchOutcome> EnsureManyAsync(IReadOnlyList<string> uris, HydrationLevel level,
            HydrationOptions opts = default, CancellationToken ct = default)
            => Task.FromResult(new HydrationBatchOutcome(uris, Array.Empty<string>(), HydrationStatus.Reached));

        public Task EnsureTraitsAsync(IReadOnlyList<string> uris, TraitSurface surface, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task EnsureTraitsAsync(IReadOnlyList<string> uris, TraitSet traits, TraitSurface surface, CancellationToken ct = default)
            => Task.CompletedTask;
        public void Invalidate(string uri) { }
    }

    const string EpisodeUri = "spotify:episode:e1";
    const string ShowUri = "spotify:show:s1";

    /// <summary>A cluster row for an episode as the wire actually gives it: a title, no artist, the show in the album
    /// slot, and no artwork — i.e. thin enough that the bar cannot paint it.</summary>
    static RemoteTrack ThinEpisode() =>
        new(EpisodeUri, "Episode One", "", "", "", ShowUri, null, 1_800_000);

    // PAUSED on purpose: a playing cluster starts the position ticker, whose play-state watchdog can publish a
    // structural change of its own — noise in a test whose whole subject is "how many Changes did the ENRICHMENT fire".
    static ClusterDelta Cluster(RemoteTrack track) =>
        new("other-device", true, track, "spotify:show:s1", false, true, false, 0, 0, 0, track.DurationMs,
            false, RepeatMode.Off, Array.Empty<ConnectDeviceRow>(), Array.Empty<RemoteTrack>());

    static Episode OpenEpisode() =>
        new("e1", EpisodeUri, "Episode One", "The Show", new Image("https://i.scdn.co/image/e1"),
            1_800_000, DateTimeOffset.UnixEpoch);

    /// <summary>The resolve is fire-and-forget off the fold, so a test has to wait for it to settle.</summary>
    static async Task SettleAsync(Func<bool> done)
    {
        for (int i = 0; i < 200 && !done(); i++) await Task.Delay(5);
    }

    [Fact]
    public async Task Episode_ResolvesOnce_ThenStopsAskingAndStopsFiringChanges()
    {
        var store = new InMemoryStore();
        store.UpsertEpisode(OpenEpisode());
        var hydrator = new CountingHydrator();
        var p = new NowPlayingProjection("us", hydrator, store);

        p.OnCluster(Cluster(ThinEpisode()));
        await SettleAsync(() => p.CurrentTrack?.Image is not null);
        Assert.Equal(1, Volatile.Read(ref hydrator.Ensures));

        int changes = 0;
        using var sub = p.Changes.Subscribe(ConnectHarness.Obs<IPlaybackState>(_ => changes++));
        changes = 0;   // SimpleSubject replays its last value to a new subscriber; count only what comes AFTER.

        // The heartbeat: the same cluster, over and over. Nothing about the row can improve, so nothing may be asked
        // for and nothing may be published.
        for (int i = 0; i < 5; i++) p.OnCluster(Cluster(ThinEpisode()));
        await Task.Delay(60);

        Assert.Equal(1, Volatile.Read(ref hydrator.Ensures));   // resolved once, never re-fired
        Assert.Equal(5, changes);                               // the five folds themselves — and NOT one enrich each
    }

    [Fact]
    public async Task Episode_Enrichment_KeepsTheShowLinkTheClusterCarried()
    {
        var store = new InMemoryStore();
        store.UpsertEpisode(OpenEpisode());
        var p = new NowPlayingProjection("us", new CountingHydrator(), store);

        p.OnCluster(Cluster(ThinEpisode()));
        await SettleAsync(() => p.CurrentTrack?.Image is not null);

        // EpisodeAsTrack has no show URI to give (Episode carries none), so folding its ref in wholesale used to erase
        // the one the cluster DID carry — and the player-bar subtitle stopped being a link to the podcast.
        Assert.Equal("The Show", p.CurrentTrack!.Album.Name);
        Assert.Equal(ShowUri, p.CurrentTrack.Album.Uri);
        Assert.Equal(1_800_000, p.CurrentTrack.DurationMs);
    }

    [Fact]
    public async Task UnresolvableTrack_IsAskedOnce_AndNeverRepublishesAnIdenticalRow()
    {
        // A row the ladder can only get to Identity: it IS resident, so the fold below has something to apply — it
        // just never becomes any better than what is already on the slab.
        var store = new InMemoryStore();
        store.UpsertTrack(new Track("t1", "spotify:track:t1", "Song", Array.Empty<ArtistRef>(),
            new AlbumRef("", "", ""), 0, false, null));
        var hydrator = new CountingHydrator();
        var p = new NowPlayingProjection("us", hydrator, store);
        var thin = new RemoteTrack("spotify:track:t1", "Song", "", "", "", "", null, 210_000);

        p.OnCluster(Cluster(thin));
        await SettleAsync(() => Volatile.Read(ref hydrator.Ensures) > 0);

        int changes = 0;
        using var sub = p.Changes.Subscribe(ConnectHarness.Obs<IPlaybackState>(_ => changes++));
        changes = 0;   // SimpleSubject replays its last value to a new subscriber; count only what comes AFTER.
        for (int i = 0; i < 4; i++) p.OnCluster(Cluster(thin));
        await Task.Delay(60);

        // The façade is allowed to be asked again (its ledger answers from the Exhausted seal for free), but a row that
        // did not actually move must never be republished.
        Assert.Equal(4, changes);
    }

    // ── identity: the wire title is the title; the catalogue fills what the wire left out (#139) ───────────────────────

    // The phone's frames for 2t4RCW carried title='The First Time' and an artist_uri but NO artist_name. Enrichment fills
    // the artists from the catalogue and keeps the wire title — it never swaps a present title for the catalogue's.
    [Fact]
    public async Task WireTitleKept_ArtistsFromCatalog()
    {
        const string uri = "spotify:track:2t4RCW";
        var store = new InMemoryStore();
        store.UpsertTrack(new Track("2t4RCW", uri, "The First Time (Catalogue Edit)",
            new[] { new ArtistRef("7AaGb", "spotify:artist:7AaGb", "Damiano David") },
            new AlbumRef("fte", "spotify:album:fte", "FUNNY little FEARS"), 217_000, false,
            new Image("https://i.scdn.co/image/fte")));
        using var p = new NowPlayingProjection("us", new CountingHydrator(), store);

        p.OnCluster(Cluster(new RemoteTrack(uri, "The First Time", "", "spotify:artist:7AaGb", "", "", null, 217_000)));
        await SettleAsync(() => p.CurrentTrack is { Artists.Count: > 0 } t && t.Artists[0].Name.Length > 0);

        var now = p.CurrentTrack!;
        Assert.Equal("The First Time", now.Title);                 // the wire's, verbatim
        Assert.Equal("Damiano David", now.Artists[0].Name);        // the catalogue's
        Assert.Equal("spotify:artist:7AaGb", now.Artists[0].Uri);
        Assert.Equal(IdentitySuspicion.None, NowPlayingIdentity.Suspicion(now));   // not "Damiano David / Damiano David"
    }

    // A title that equals its artist's name is a legitimate shape (a self-titled song). The tripwire FLAGS it — once, in the
    // log — and nothing renames it: the heuristic that blanked such titles (and swapped in the catalogue's) is gone.
    [Fact]
    public async Task SelfTitledTrack_Unchanged()
    {
        const string uri = "spotify:track:self";
        var store = new InMemoryStore();
        store.UpsertTrack(new Track("self", uri, "Weezer (Catalogue Edit)",
            new[] { new ArtistRef("wz", "spotify:artist:wz", "Weezer") },
            new AlbumRef("blue", "spotify:album:blue", "Weezer"), 200_000, false,
            new Image("https://i.scdn.co/image/blue")));
        using var p = new NowPlayingProjection("us", new CountingHydrator(), store);

        p.OnCluster(Cluster(new RemoteTrack(uri, "Weezer", "Weezer", "spotify:artist:wz", "", "", null, 200_000)));
        await SettleAsync(() => p.CurrentTrack?.Image is not null);

        var now = p.CurrentTrack!;
        Assert.NotNull(now.Image);                                 // enrichment DID land…
        Assert.Equal("Weezer", now.Title);                         // …and the title is exactly the wire's
        Assert.Equal("Weezer", now.Artists[0].Name);
        Assert.Equal(IdentitySuspicion.TitleEqualsArtist, NowPlayingIdentity.Suspicion(now));
    }

    [Fact]
    public void Suspicion_Table()
    {
        static Track Row(string uri, string title, params string[] artists)
        {
            var refs = new ArtistRef[artists.Length];
            for (int i = 0; i < artists.Length; i++)
                refs[i] = new ArtistRef("a" + i, "spotify:artist:a" + i, artists[i]);
            return new Track(uri[(uri.LastIndexOf(':') + 1)..], uri, title, refs, new AlbumRef("", "", ""), 1000, false, null);
        }

        var cases = new (Track?, IdentitySuspicion)[]
        {
            (null, IdentitySuspicion.None),
            (Row("spotify:track:ok", "Lost on You", "LP"), IdentitySuspicion.None),
            (Row("spotify:episode:e1", "Episode One"), IdentitySuspicion.None),             // no artists: a podcast's shape
            (Row("spotify:track:e", "", "A"), IdentitySuspicion.TitleEmpty),
            (Row("spotify:track:w", "   ", "A"), IdentitySuspicion.TitleEmpty),
            (Row("spotify:track:u", "spotify:track:u", "A"), IdentitySuspicion.TitleIsUri),
            (Row("spotify:track:lp", "LP", "LP"), IdentitySuspicion.TitleEqualsArtist),
            (Row("spotify:track:lp2", " lp ", "LP"), IdentitySuspicion.TitleEqualsArtist),   // case / edge whitespace
            (Row("spotify:track:ft", "Collab", "Main", "Collab"), IdentitySuspicion.TitleEqualsArtist),   // any artist
            (Row("spotify:track:2t4RCW", "The First Time", ""), IdentitySuspicion.ArtistsUnnamed),   // artist_uri, no name
            (Row("spotify:track:p", "", ""), IdentitySuspicion.TitleEmpty),                  // most severe wins
            (Row("spotify:track:q", "LP", "", "LP"), IdentitySuspicion.TitleEqualsArtist),   // …over an unnamed artist
        };

        foreach (var (row, expected) in cases)
            Assert.Equal(expected, NowPlayingIdentity.Suspicion(row));
    }
}
