// ── Wavee.Tests/PlaylistItemKindTests.cs — a playlist member's Kind, and where it lands ────────────────────────────
//
// Plan §3.1, ledger row 12: `Relation.PlaylistTracks` used to resolve EVERY member into `Current.Tracks`, unlike
// `AlbumTracks`, which guards `Target.Kind != Track`. A `spotify:episode:` playlist item became a Track slot keyed by
// an episode gid and never hydrated. `Entities/Edges.Staging.cs`'s commit now dispatches per row, mirroring
// `AlbumTracks`'s kind filter: an episode member resolves into `Current.Episodes`, everything else keeps landing in
// `Current.Tracks` exactly as before the mix existed. `Relation.ShowEpisodes` stays episodes-only.
//
// Nothing here decodes bytes (`EdgesStagingTests.cs`'s own rule): a `StagedId` parent, an ordered run of `StagedId`
// children by `Relation`, then the live edge table read back — the shape a decoder actually hands the drain.

using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class PlaylistItemKindTests
{
    // Deterministic catalog identities — the packed form a protobuf decoder actually holds (no uri text anywhere),
    // the same helper EdgesStagingTests uses.
    static EntityId Gid(EntityKind kind, byte seed)
    {
        Span<byte> gid = stackalloc byte[16];
        for (int i = 0; i < 16; i++) gid[i] = (byte)(seed + i);
        return EntityId.ForGid(kind, gid);
    }

    static Track TrackOf(byte seed) => Entities.Track(Gid(EntityKind.Track, seed));
    static Episode EpisodeOf(byte seed) => Entities.Episode(Gid(EntityKind.Episode, seed));
    static Playlist PlaylistOf(byte seed) => Entities.Playlist(Gid(EntityKind.Playlist, seed));
    static Show ShowOf(byte seed) => Entities.Show(Gid(EntityKind.Show, seed));

    [Fact]
    public void A_mixed_page_resolves_each_row_into_the_table_its_own_kind_names()
    {
        TestScope.Fresh();
        var s = Staging.Rent();

        var run = s.Run(Relation.PlaylistTracks);
        run.Add(Gid(EntityKind.Track, 10));                     // 0: a track
        run.Add(Gid(EntityKind.Episode, 20));                   // 1: an episode
        run.Add(Gid(EntityKind.Track, 11));                     // 2: another track
        StagedId playlist = Gid(EntityKind.Playlist, 90);
        run.End(in playlist, EdgeState.Complete, 3);

        TestScope.CommitAndPublish(s);

        var edges = Entities.Current.Edges.PlaylistTracks;
        int slot = PlaylistOf(90).Slot;
        Assert.Equal(EdgeState.Complete, edges.State(slot));
        Assert.Equal(3, edges.Count(slot));

        var targets = edges.Targets(slot);
        var payload = edges.Payload(slot);
        Assert.Equal(PlaylistItemKind.Track, payload[0].Kind);
        Assert.Equal(PlaylistItemKind.Episode, payload[1].Kind);
        Assert.Equal(PlaylistItemKind.Track, payload[2].Kind);

        // The target slot indexes the table its own Kind names — a track's slot means nothing in Episodes and vice
        // versa, so the row is only real once both agree.
        Assert.Equal(TrackOf(10).Slot, targets[0]);
        Assert.Equal(EpisodeOf(20).Slot, targets[1]);
        Assert.Equal(TrackOf(11).Slot, targets[2]);

        // The fold that runs at commit (Playlist.Refold) already saw the mix.
        Assert.True(new Playlist(slot).IsMixed);
    }

    [Fact]
    public void ShowEpisodes_never_needs_a_per_row_check_but_still_carries_episode_kind()
    {
        TestScope.Fresh();
        var s = Staging.Rent();

        var run = s.Run(Relation.ShowEpisodes);
        run.Add(Gid(EntityKind.Episode, 30));
        run.Add(Gid(EntityKind.Episode, 31));
        StagedId show = Gid(EntityKind.Show, 95);
        run.End(in show, EdgeState.Complete, 2);

        TestScope.CommitAndPublish(s);

        var edges = Entities.Current.Edges.ShowEpisodes;
        int slot = ShowOf(95).Slot;
        var targets = edges.Targets(slot);
        var payload = edges.Payload(slot);
        Assert.Equal(2, targets.Length);
        Assert.Equal(EpisodeOf(30).Slot, targets[0]);
        Assert.Equal(EpisodeOf(31).Slot, targets[1]);
        Assert.Equal(PlaylistItemKind.Episode, payload[0].Kind);
        Assert.Equal(PlaylistItemKind.Episode, payload[1].Kind);
    }

    [Fact]
    public void A_member_the_wire_names_no_special_kind_for_still_lands_in_tracks()
    {
        // A local file, or any uri EntityUri.KindOf does not read as Episode, keeps the pre-mix behaviour: it is
        // still a real row (unlike AlbumTracks's Table.None skip for a mismatched kind) and it lands in Tracks.
        TestScope.Fresh();
        var s = Staging.Rent();

        var run = s.Run(Relation.PlaylistTracks);
        ref var local = ref run.Add();
        local.Target = s.AddText(System.Text.Encoding.UTF8.GetBytes(Playlist.LocalFileUri(@"C:\music\solo.mp3")));
        StagedId playlist = Gid(EntityKind.Playlist, 91);
        run.End(in playlist, EdgeState.Complete, 1);

        TestScope.CommitAndPublish(s);

        var edges = Entities.Current.Edges.PlaylistTracks;
        int slot = PlaylistOf(91).Slot;
        Assert.Equal(1, edges.Count(slot));
        Assert.Equal(PlaylistItemKind.Track, edges.Payload(slot)[0].Kind);
        Assert.False(new Playlist(slot).IsMixed);
    }

    [Fact]
    public void Refold_derives_the_episode_count_and_duration_from_each_rows_own_table()
    {
        TestScope.Fresh();
        var seed = Staging.Rent();
        ref var track = ref seed.Tracks.RowFor(new StagedId(Gid(EntityKind.Track, 40)), Authority.Full, (uint)TrackFields.Duration);
        track.DurationMs = 180_000;
        ref var episode = ref seed.Episodes.RowFor(new StagedId(Gid(EntityKind.Episode, 41)), Authority.Full, (uint)EpisodeFields.Duration);
        episode.DurationMs = 2_400_000;
        TestScope.CommitAndPublish(seed);

        var p = PlaylistOf(96);
        int trackSlot = TrackOf(40).Slot, episodeSlot = EpisodeOf(41).Slot;
        p.ApplyMembership([trackSlot, episodeSlot],
            [new PlaylistTrackEdge(default, 0, 0, 0, 0, 0, 0, PlaylistItemKind.Track),
             new PlaylistTrackEdge(default, 0, 0, 0, 0, 0, 0, PlaylistItemKind.Episode)]);

        Assert.True(p.Refold());
        Assert.Equal(1, p.EpisodeCount);
        Assert.Equal(180_000 + 2_400_000, p.DurationMs);
        Assert.True(p.IsMixed);
    }
}
