// ── Wavee.Tests/EdgePageTests.cs — ReplacePage's terminal shrink, and AlbumTracks' non-shifting kind filter ───────
//
// Two edge-list fixes pinned together because they share one failure mode: a page landing on top of a longer
// PREVIOUS answer must never leave a stale duplicate row behind, whether the staleness comes from `ReplacePage`
// never shrinking (Fix 1) or from a defensive kind filter compacting a run and shifting every ordinal after the
// drop (Fix 2). Pure CSR mechanics — tests 1-4 hold no `Entities.Current` at all; 5-6 go through `Entities.Commit`
// exactly as a decoder would, because the fixes' sharp edge (the rootlist's owned strings, the `tracksV2` ordinal)
// only shows up at that seam.

using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class EdgePageTests
{
    const int Parent = 5;

    // ── Fix 1: ReplacePage's guarded, terminal shrink ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_terminal_page_shorter_than_the_stored_list_shrinks_it()
    {
        var edges = new EdgeTable<AlbumTrackEdge>();

        var first = new int[12];
        var firstPayload = new AlbumTrackEdge[12];
        for (int i = 0; i < 12; i++)
        {
            first[i] = 100 + i;
            firstPayload[i] = new AlbumTrackEdge(1, (ushort)(i + 1));
        }
        edges.ReplacePage(Parent, 0, first, firstPayload, total: 12);
        Assert.Equal(12, edges.Count(Parent));
        Assert.Equal(EdgeState.Complete, edges.State(Parent));

        // The real answer is shorter, and it SAYS so (total: 10, reached by this very page) — a terminal page.
        var second = new int[10];
        var secondPayload = new AlbumTrackEdge[10];
        for (int i = 0; i < 10; i++)
        {
            second[i] = 200 + i;
            secondPayload[i] = new AlbumTrackEdge(2, (ushort)(i + 1));
        }
        edges.ReplacePage(Parent, 0, second, secondPayload, total: 10);

        Assert.Equal(10, edges.Count(Parent));                              // Fix 1: the list actually shrinks
        Assert.Equal(10, edges.Total(Parent));
        Assert.Equal(EdgeState.Complete, edges.State(Parent));
        Assert.True(edges.Targets(Parent).SequenceEqual(second));
        Assert.Equal(2, edges.Payload(Parent)[9].Disc);

        // The old rows 10 and 11 (targets 110, 111) are not just uncounted — they are gone: no consumer can find a
        // duplicate of the previous, longer answer surviving past the new, terminal extent ("reads as none", P3).
        Assert.False(edges.Contains(Parent, 110));
        Assert.False(edges.Contains(Parent, 111));
        Assert.Equal(-1, edges.IndexOf(Parent, 110));
        Assert.Equal(-1, edges.IndexOf(Parent, 111));
    }

    [Fact]
    public void A_non_terminal_shorter_page_never_shrinks_a_longer_list()
    {
        var edges = new EdgeTable<NoEdge>();
        int[] twelve = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
        edges.ReplacePage(Parent, 0, twelve, [], total: 12);
        Assert.Equal(12, edges.Count(Parent));

        // `total: 0` is D7's "nobody said how many there are" — a shorter re-ask here must NOT delete what would be
        // a real later page on a multi-page list.
        int[] ten = [101, 102, 103, 104, 105, 106, 107, 108, 109, 110];
        edges.ReplacePage(Parent, 0, ten, [], total: 0);

        Assert.Equal(12, edges.Count(Parent));                              // unchanged: the guard held
        Assert.Equal(EdgeState.Complete, edges.State(Parent));               // a stated total survives an unstated one
        Assert.Equal(12, edges.Total(Parent));

        int[] expected = [101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 11, 12];
        Assert.True(edges.Targets(Parent).SequenceEqual(expected));         // the untouched tail (11, 12) still stands
    }

    [Fact]
    public void Re_landing_page_zero_shorter_does_not_disturb_a_later_pages_rows()
    {
        var edges = new EdgeTable<NoEdge>();
        edges.ReplacePage(Parent, 0, [1, 2, 3, 4, 5], [], total: 10);
        edges.ReplacePage(Parent, 5, [6, 7, 8, 9, 10], [], total: 10);
        Assert.Equal(EdgeState.Complete, edges.State(Parent));
        Assert.Equal(10, edges.Count(Parent));

        // Page 0 re-answers shorter (3 rows instead of 5), and total still says 10 — but THIS page's own extent (3)
        // comes nowhere near it, so it is not terminal and must not touch page 1's rows at all.
        edges.ReplacePage(Parent, 0, [101, 102, 103], [], total: 10);

        Assert.Equal(10, edges.Count(Parent));                              // page 1 survives entirely
        Assert.Equal(EdgeState.Complete, edges.State(Parent));
        int[] expected = [101, 102, 103, 4, 5, 6, 7, 8, 9, 10];
        Assert.True(edges.Targets(Parent).SequenceEqual(expected));
    }

    [Fact]
    public void A_terminal_shrink_leaves_the_relation_complete_not_stuck_partial()
    {
        var edges = new EdgeTable<NoEdge>();
        int[] twelve = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
        // An earlier (as it turns out, wrong) answer said the total was 12, and it was believed.
        edges.ReplacePage(Parent, 0, twelve, [], total: 12);
        Assert.Equal(EdgeState.Complete, edges.State(Parent));

        // The real, corrected answer: only 10 rows, and it says so too.
        int[] ten = [201, 202, 203, 204, 205, 206, 207, 208, 209, 210];
        edges.ReplacePage(Parent, 0, ten, [], total: 10);

        // Without breaking `_total`'s own `Math.Max`, `_total` would stay stuck at 12 and `_length (10) >= 12` would
        // never be true again — Partial forever, and the planner would re-ask this page in a loop that never settles.
        Assert.Equal(EdgeState.Complete, edges.State(Parent));
        Assert.Equal(10, edges.Total(Parent));

        // A page asked again for the same parent must keep reading Complete, not flip back to Partial.
        edges.MarkAsked(Parent, 0);
        Assert.Equal(EdgeState.Complete, edges.State(Parent));
    }

    // ── Fix 1's sharp coupling: the rootlist's owned FolderName/FolderId must not leak past a terminal shrink ───────

    [Fact]
    public void A_terminal_shrink_releases_the_truncated_rootlist_rows_owned_strings()
    {
        TestScope.Fresh();

        var s1 = Staging.Rent();
        var run1 = s1.Run(Relation.Rootlist);
        for (int i = 0; i < 12; i++)
        {
            ref var edge = ref run1.Add();
            edge.Text = s1.Text($"rootleak-name-{i}");
            edge.Aux = s1.Text($"rootleak-id-{i}");
            edge.U0 = (ushort)i;
            edge.B1 = (byte)RootlistKind.FolderStart;
        }
        StagedId account1 = s1.Text("spotify:user:rootleak-tester");
        run1.End(in account1, EdgeState.Complete, 12);
        TestScope.CommitAndPublish(s1);

        int userSlot = Entities.User(EntityUri.Parse("spotify:user:rootleak-tester".AsSpan())).Slot;
        var rootlist = Entities.Current.Edges.Rootlist;
        Assert.Equal(12, rootlist.Count(userSlot));

        // Rows 10 and 11 are the ones a shorter, terminal page below will truncate but never overwrite. Capture the
        // string ids they own now, before the shrink, so a later re-intern of the SAME content can tell "reclaimed"
        // from "leaked" — ids are never reused (the engine's StringTable), so getting the identical id back means the
        // map entry survived, i.e. the row's reference was never released.
        StringId staleName = rootlist.Payload(userSlot)[11].FolderName;
        StringId staleId = rootlist.Payload(userSlot)[11].FolderId;
        Assert.Equal("rootleak-name-11", Entities.Strings.Resolve(staleName));

        var s2 = Staging.Rent();
        var run2 = s2.Run(Relation.Rootlist);
        for (int i = 0; i < 10; i++)
        {
            ref var edge = ref run2.Add();
            edge.Text = s2.Text($"rootleak-name2-{i}");
            edge.Aux = s2.Text($"rootleak-id2-{i}");
            edge.U0 = (ushort)i;
            edge.B1 = (byte)RootlistKind.FolderStart;
        }
        StagedId account2 = s2.Text("spotify:user:rootleak-tester");        // same content: the same account row
        run2.Page(in account2, offset: 0, total: 10);                       // terminal: 0 + 10 >= 10
        TestScope.CommitAndPublish(s2);

        Assert.Equal(10, rootlist.Count(userSlot));                         // Fix 1: the tail actually shrank
        Assert.Equal(10, rootlist.Total(userSlot));
        Assert.Equal(EdgeState.Complete, rootlist.State(userSlot));

        // The leak test: re-intern the exact content the truncated row owned. A fresh id here proves the map entry
        // was removed (the row's ref was released, as `ReleaseRootlistRows` now does for the whole truncated tail);
        // the SAME id back would prove it never was.
        StringId reinternedName = Entities.Strings.Intern("rootleak-name-11");
        StringId reinternedId = Entities.Strings.Intern("rootleak-id-11");
        Assert.NotEqual(staleName, reinternedName);
        Assert.NotEqual(staleId, reinternedId);
    }

    // ── Fix 2: a non-Track entry in an AlbumTracks run is skipped in place, not compacted ──────────────────────────

    static EntityId Gid(EntityKind kind, byte seed)
    {
        Span<byte> gid = stackalloc byte[16];
        for (int i = 0; i < 16; i++) gid[i] = (byte)(seed + i);
        return EntityId.ForGid(kind, gid);
    }

    static Track TrackOf(byte seed) => Entities.Track(Gid(EntityKind.Track, seed));
    static Album AlbumOf(byte seed) => Entities.Album(Gid(EntityKind.Album, seed));

    [Fact]
    public void A_non_track_entry_in_an_album_tracks_run_is_skipped_in_place_not_compacted()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var tracks = s.Run(Relation.AlbumTracks);

        // Five entries; entry 2 is a mis-ordered ARTIST identity (G-231's defensive filter) — the rest are real
        // tracks, each with its own disc/number pair, the ordinal a `tracksV2` row reads as its track number.
        for (byte i = 0; i < 5; i++)
        {
            ref var edge = ref tracks.Add();
            if (i == 2)
            {
                edge.Target = Gid(EntityKind.Artist, 77);
            }
            else
            {
                edge.Target = Gid(EntityKind.Track, (byte)(10 + i));
                edge.B0 = 1;
                edge.U0 = (ushort)(i + 1);
            }
        }
        StagedId album = Gid(EntityKind.Album, 95);
        tracks.End(in album, EdgeState.Complete, 5);
        TestScope.CommitAndPublish(s);

        var edges = Entities.Current.Edges.AlbumTracks;
        int albumSlot = AlbumOf(95).Slot;

        Assert.Equal(5, edges.Count(albumSlot));                            // no shift: the run stays five wide

        var targets = edges.Targets(albumSlot);
        var payload = edges.Payload(albumSlot);

        Assert.Equal(TrackOf(10).Slot, targets[0]);
        Assert.Equal(TrackOf(11).Slot, targets[1]);
        Assert.Equal(Table.None, targets[2]);                               // the skipped entry, in place …
        Assert.Equal(default(AlbumTrackEdge), payload[2]);
        Assert.Equal(TrackOf(13).Slot, targets[3]);                         // … not shifted onto index 2 …
        Assert.Equal(TrackOf(14).Slot, targets[4]);                         // … and nothing after it moved either

        Assert.Equal(1, payload[0].Disc);
        Assert.Equal(1, payload[0].Number);
        Assert.Equal(2, payload[1].Number);
        Assert.Equal(4, payload[3].Number);                                 // the ordinal survives — never renumbered
        Assert.Equal(5, payload[4].Number);
    }
}
