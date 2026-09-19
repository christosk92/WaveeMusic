// ── Wavee.Tests/PlaylistPlaybackKindTests.cs — A3's follow-ups over the kind-aware playlist member ─────────────────
//
// `PlaylistItemKindTests.cs` already pins the commit-time dispatch (`Edges.Staging.cs`'s per-row `Relation.
// PlaylistTracks` arm) and `Playlist.Refold`'s own per-Kind fold. What this file pins is the NEXT layer up — every
// place that turns a landed `PlaylistTrackEdge` row into a playable `EntityRef`/handle must read the row's own
// `PlaylistItemKind`, never assume every member is a Track:
//
//   · a mixed page's row -> EntityRef mapping (the shape `Playback.Host.Context.cs`'s `AddRow`/`FillQueueIds` and
//     `Spotify.Library.cs`'s deposit resolver both rely on) — a Track-kind row's target slot indexes `Current.
//     Tracks`, an Episode-kind row's indexes `Current.Episodes`, and reading the wrong one is silent data
//     corruption, not a crash (D13: a Track/Episode handle is just an int over its own table's columns);
//   · the `--fake` seed (`Entities.Fake.Library.cs`'s X5), which used to fake an episode-in-playlist member by
//     flagging a TRACK row `TrackFlags.Podcast` — the exact "episode rendered as a Track row" shape plan §3.1
//     (ledger row 12) superseded. It now lands a real `Current.Episodes` row and a `Kind = Episode` membership edge,
//     so the offline demo exercises the same mixed-page path a live decode does.
//
// No source-text tests: every fact below drives the real `Entities.SeedFake` / `Staging` + `Commit` + `Playlist.
// ApplyMembership`/`Refold` seam, never greps production source.

using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class PlaylistPlaybackKindTests
{
    const long Now0 = 1_788_000_000;   // Platform.Clock.FixedSeedEpoch, restated (EntitiesFakeTests.cs's own convention)

    static EntityId Gid(EntityKind kind, byte seed)
    {
        Span<byte> gid = stackalloc byte[16];
        for (int i = 0; i < 16; i++) gid[i] = (byte)(seed + i);
        return EntityId.ForGid(kind, gid);
    }

    // ══ 1. the row → EntityRef mapping a mixed page hands to playback/deposit ═══════════════════════════════════════

    /// <summary>The shape every walker of a landed `PlaylistTracks` page must follow: the row's OWN
    /// <see cref="PlaylistItemKind"/> says which table its target slot indexes, so a blind `new Track(slot)` over an
    /// Episode-kind row would silently wrap whatever unrelated row happens to sit at that index in <c>Tracks</c>.</summary>
    [Fact]
    public void A_mixed_pages_rows_resolve_into_the_right_handle_kind_in_order()
    {
        TestScope.Fresh();
        var s = Staging.Rent();

        ref var t0 = ref s.Tracks.RowFor(Gid(EntityKind.Track, 10), Authority.Seed, (uint)TrackFields.Duration);
        t0.DurationMs = 200_000;
        ref var e0 = ref s.Episodes.RowFor(Gid(EntityKind.Episode, 20), Authority.Seed, (uint)EpisodeFields.Duration);
        e0.DurationMs = 1_800_000;
        ref var t1 = ref s.Tracks.RowFor(Gid(EntityKind.Track, 11), Authority.Seed, (uint)TrackFields.Duration);
        t1.DurationMs = 210_000;

        var run = s.Run(Relation.PlaylistTracks);
        run.Add(Gid(EntityKind.Track, 10));
        run.Add(Gid(EntityKind.Episode, 20));
        run.Add(Gid(EntityKind.Track, 11));
        StagedId playlist = Gid(EntityKind.Playlist, 90);
        run.End(in playlist, EdgeState.Complete, 3);

        TestScope.CommitAndPublish(s);

        int slot = Entities.Playlist(Gid(EntityKind.Playlist, 90)).Slot;
        var edges = Entities.Current.Edges.PlaylistTracks;
        var targets = edges.Targets(slot);
        var payload = edges.Payload(slot);
        Assert.Equal(3, targets.Length);

        // The mapping a caller (AddRow/FillQueueIds, the deposit resolver) walks: dispatch on the row's own Kind,
        // never on what happens to be resident at the target slot in either table.
        long durationSum = 0;
        int trackCount = 0, episodeCount = 0;
        for (int i = 0; i < targets.Length; i++)
        {
            if (payload[i].Kind == PlaylistItemKind.Episode)
            {
                var ep = new Episode(targets[i]);
                Assert.True(ep.IsValid);
                durationSum += ep.DurationMs;
                episodeCount++;
            }
            else
            {
                var tr = new Track(targets[i]);
                Assert.True(tr.IsValid);
                durationSum += tr.DurationMs;
                trackCount++;
            }
        }
        Assert.Equal(2, trackCount);
        Assert.Equal(1, episodeCount);
        Assert.Equal(200_000 + 1_800_000 + 210_000, durationSum);

        // Row 1 is the episode: its target slot is an Episodes-table index and means nothing in Tracks (it is either
        // out of range or names an unrelated row) — the exact corruption a Track-only reader would hit.
        Assert.Equal(PlaylistItemKind.Episode, payload[1].Kind);
        Assert.Equal(Entities.Episode(Gid(EntityKind.Episode, 20)).Slot, targets[1]);
    }

    // ══ 2. the --fake seed's X5: real Episode rows, not a Track row flagged Podcast ═════════════════════════════════

    /// <summary>Plan §3.1 (ledger row 12): X5 used to stage its three "episodes" as TRACK rows carrying
    /// <c>TrackFlags.Podcast</c> — the superseded design. It now lands real <c>Current.Episodes</c> rows and marks
    /// their membership <c>Kind = Episode</c>, so the offline demo's mixed playlist exercises the same per-row
    /// dispatch a live decode does.</summary>
    [Fact]
    public void The_fake_seed_lands_X5s_three_episode_members_as_real_episode_rows()
    {
        Entities.Boot(CatalogScope.Fake());
        Entities.SeedFake(Now0);

        var playlist = Entities.Playlist(EntityUri.Parse("spotify:playlist:plx5"));
        Assert.True(playlist.IsValid);
        Assert.True(playlist.IsMixed);
        Assert.Equal(3, playlist.EpisodeCount);

        var edges = Entities.Current.Edges.PlaylistTracks;
        var targets = edges.Targets(playlist.Slot);
        var payload = edges.Payload(playlist.Slot);
        Assert.Equal(20, targets.Length);

        Span<int> episodeRows = [4, 11, 17];
        long expectedDuration = 0;
        for (int k = 0; k < targets.Length; k++)
        {
            bool shouldBeEpisode = episodeRows.Contains(k);
            Assert.Equal(shouldBeEpisode ? PlaylistItemKind.Episode : PlaylistItemKind.Track, payload[k].Kind);
            if (shouldBeEpisode)
            {
                var ep = new Episode(targets[k]);
                Assert.True(ep.IsValid);
                Assert.True(ep.Title.Length > 0);
                Assert.True(ep.DurationMs > 0);
                expectedDuration += ep.DurationMs;
            }
            else
            {
                var tr = new Track(targets[k]);
                Assert.True(tr.IsValid);
                expectedDuration += tr.DurationMs;
            }
        }

        // The playlist's own duration sum agrees with a per-row, kind-correct fold — the seed's `Land`/`Refold` path
        // (not a duplicate, Track-only fold that would misread the episode rows' slots).
        Assert.Equal(expectedDuration, playlist.DurationMs);
    }
}
