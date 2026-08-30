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
}
