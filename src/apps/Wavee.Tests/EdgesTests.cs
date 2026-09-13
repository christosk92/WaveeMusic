// ── Wavee.Tests/EdgesTests.cs — CSR replace / page / contains / insert / settle / compact ────────────────────────
//
// Wave 1's gate for Entities/Edges.cs (plan §5, §4.15's named case: "ReplacePage_marks_partial_until_total_reached").
// Pure CSR mechanics over plain int parents, so nothing here needs a Scope, a kind table or a handle type.

using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class EdgesTests
{
    const int Parent = 5;
    static readonly int[] Three = [11, 22, 33];

    // ── whole-list writes ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Replace_writes_targets_payload_state_and_total()
    {
        var edges = new EdgeTable<AlbumTrackEdge>();
        AlbumTrackEdge[] payload = [new(1, 1), new(1, 2), new(2, 1)];
        edges.Replace(Parent, Three, payload, EdgeState.Complete, 3);

        Assert.True(edges.Targets(Parent).SequenceEqual(Three));
        Assert.Equal(3, edges.Count(Parent));
        Assert.Equal(EdgeState.Complete, edges.State(Parent));
        Assert.Equal(3, edges.Total(Parent));
        Assert.Equal(2, edges.Payload(Parent)[2].Disc);
    }

    [Fact]
    public void An_unknown_parent_reads_as_empty_and_unknown_never_as_an_empty_list()
    {
        var edges = new EdgeTable<NoEdge>();
        Assert.Equal(EdgeState.Unknown, edges.State(999));                  // a skeleton, not "this album has no tracks"
        Assert.Equal(0, edges.Count(999));
        Assert.True(edges.Targets(999).IsEmpty);
        Assert.False(edges.Contains(999, 1));

        edges.Replace(999, [], [], EdgeState.Complete, 0);
        Assert.Equal(EdgeState.Complete, edges.State(999));                 // NOW it is a real, renderable empty
    }

    [Fact]
    public void ReplaceRun_lands_a_complete_list_in_one_call()
    {
        var edges = new EdgeTable<NoEdge>();
        edges.ReplaceRun(Parent, Three, []);                                // ch 31's seed shape: no per-row Add
        Assert.Equal(EdgeState.Complete, edges.State(Parent));
        Assert.Equal(3, edges.Total(Parent));
        Assert.True(edges.Targets(Parent).SequenceEqual(Three));
    }

    /// <summary>Plan §4.15's named edge case.</summary>
    [Fact]
    public void ReplacePage_marks_partial_until_total_reached()
    {
        var edges = new EdgeTable<PlaylistTrackEdge>();
        edges.ReplacePage(Parent, 0, [1, 2, 3, 4], [], total: 10);
        Assert.Equal(EdgeState.Partial, edges.State(Parent));
        Assert.Equal(4, edges.Count(Parent));
        Assert.Equal(10, edges.Total(Parent));                              // the scrollbar's real extent

        edges.ReplacePage(Parent, 4, [5, 6, 7, 8], [], total: 10);
        Assert.Equal(EdgeState.Partial, edges.State(Parent));
        Assert.Equal(8, edges.Count(Parent));

        edges.ReplacePage(Parent, 8, [9, 10], [], total: 10);
        Assert.Equal(EdgeState.Complete, edges.State(Parent));              // the last page flips it; nobody says so
        Assert.Equal(10, edges.Count(Parent));
        Assert.True(edges.Targets(Parent).SequenceEqual([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]));
    }

    [Fact]
    public void A_page_that_arrives_out_of_order_leaves_none_holes_the_next_page_fills()
    {
        var edges = new EdgeTable<NoEdge>();
        edges.ReplacePage(Parent, 4, [5, 6], [], total: 6);
        Assert.Equal(6, edges.Count(Parent));
        Assert.Equal(0, edges.Targets(Parent)[0]);                          // slot 0 = "none" — an un-arrived row (P3)
        Assert.Equal(5, edges.Targets(Parent)[4]);

        edges.ReplacePage(Parent, 0, [1, 2, 3, 4], [], total: 6);
        Assert.True(edges.Targets(Parent).SequenceEqual([1, 2, 3, 4, 5, 6]));
        Assert.Equal(EdgeState.Complete, edges.State(Parent));
    }

    [Fact]
    public void Replace_bumps_the_parents_version_and_marks_the_table_dirty()
    {
        Entities.Publish();
        var edges = new EdgeTable<NoEdge>();
        uint before = edges.Version(Parent);

        edges.Replace(Parent, Three, [], EdgeState.Complete, 3);
        Assert.Equal(before + 1, edges.Version(Parent));
        Assert.Equal(1, Entities.PendingPublications);                      // one signal for the whole rewrite (D8)

        uint publication = Entities.Publish();
        Assert.Equal(publication, edges.Changed.Peek());
    }

    [Fact]
    public void Clear_forgets_the_children_and_goes_back_to_unknown()
    {
        var edges = new EdgeTable<NoEdge>();
        edges.ReplaceRun(Parent, Three, []);
        edges.Clear(Parent);
        Assert.Equal(0, edges.Count(Parent));
        Assert.Equal(EdgeState.Unknown, edges.State(Parent));
        Assert.False(edges.Contains(Parent, 11));
    }

    // ── membership ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_short_list_answers_membership_by_scanning_and_builds_no_index()
    {
        var edges = new EdgeTable<LibraryEdge>();
        edges.ReplaceRun(Parent, Three, []);
        Assert.True(edges.Contains(Parent, 22));
        Assert.False(edges.Contains(Parent, 44));
        Assert.False(edges.Indexed);                                        // a hash set for three ints is pure overhead
        Assert.Equal(1, edges.IndexOf(Parent, 22));
        Assert.Equal(-1, edges.IndexOf(Parent, 44));
    }

    [Fact]
    public void A_long_list_builds_an_index_that_stays_coherent_through_every_mutation()
    {
        var edges = new EdgeTable<LibraryEdge>();
        var liked = new int[500];
        for (int i = 0; i < liked.Length; i++) liked[i] = 1_000 + i;
        edges.ReplaceRun(Parent, liked, []);

        Assert.True(edges.Contains(Parent, 1_250));
        Assert.True(edges.Indexed);                                         // 500 liked songs: worth an index
        Assert.False(edges.Contains(Parent, 9_999));

        edges.Insert(Parent, 9_999, new LibraryEdge(7, 0), at: 0);
        Assert.True(edges.Contains(Parent, 9_999));                         // the insert updated the index in place

        Assert.True(edges.Remove(Parent, 1_250));
        Assert.False(edges.Contains(Parent, 1_250));                        // and so did the remove

        edges.ReplaceRun(Parent, [1, 2], []);
        Assert.False(edges.Contains(Parent, 9_999));                        // and the whole-list rewrite
        Assert.True(edges.Contains(Parent, 2));

        edges.Clear(Parent);
        Assert.False(edges.Contains(Parent, 2));
    }

    [Fact]
    public void Membership_is_per_parent_never_per_table()
    {
        var edges = new EdgeTable<LibraryEdge>();
        var many = new int[100];
        for (int i = 0; i < many.Length; i++) many[i] = i + 1;
        edges.ReplaceRun(7, many, []);
        edges.ReplaceRun(8, [500], []);

        Assert.True(edges.Contains(7, 50));
        Assert.False(edges.Contains(8, 50));
        Assert.True(edges.Contains(8, 500));
        Assert.False(edges.Contains(7, 500));
    }

    // ── single-edge writes (C6) ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Insert_at_zero_prepends_which_is_what_liking_a_track_does()
    {
        var edges = new EdgeTable<LibraryEdge>();
        edges.ReplaceRun(Parent, Three, [new(1, 0), new(2, 0), new(3, 0)]);

        edges.Insert(Parent, 44, new LibraryEdge(99, 0), at: 0, EdgePending.Add);
        Assert.True(edges.Targets(Parent).SequenceEqual([44, 11, 22, 33]));  // newest first, as the Liked cover needs
        Assert.Equal(99, edges.Payload(Parent)[0].AddedAt);
        Assert.Equal(1, edges.Payload(Parent)[1].AddedAt);                   // the old rows shifted, not scrambled
        Assert.Equal(EdgePending.Add, edges.PendingOf(Parent, 44));
        Assert.Equal(EdgePending.None, edges.PendingOf(Parent, 11));
        Assert.Equal(4, edges.Total(Parent));
    }

    [Fact]
    public void Insert_with_no_index_appends()
    {
        var edges = new EdgeTable<NoEdge>();
        edges.ReplaceRun(Parent, Three, []);
        edges.Insert(Parent, 44, default);
        Assert.True(edges.Targets(Parent).SequenceEqual([11, 22, 33, 44]));
    }

    [Fact]
    public void Inserting_a_target_that_is_already_there_updates_it_in_place()
    {
        var edges = new EdgeTable<LibraryEdge>();
        edges.ReplaceRun(Parent, Three, [new(1, 0), new(2, 0), new(3, 0)]);
        edges.Insert(Parent, 22, new LibraryEdge(500, 0), at: 0, EdgePending.Add);

        Assert.Equal(3, edges.Count(Parent));                                // a double-click adds no second edge
        Assert.True(edges.Targets(Parent).SequenceEqual(Three));             // …and does not move it
        Assert.Equal(500, edges.Payload(Parent)[1].AddedAt);
        Assert.Equal(EdgePending.Add, edges.PendingOf(Parent, 22));
    }

    [Fact]
    public void Insert_onto_a_parent_nobody_has_answered_for_makes_a_complete_local_list()
    {
        var edges = new EdgeTable<QueueEdge>();
        edges.Insert(1, 42, new QueueEdge(7, 0, 1));                         // parent 1 = the playback session subject
        Assert.Equal(1, edges.Count(1));
        Assert.Equal(EdgeState.Complete, edges.State(1));
        Assert.Equal(7ul, edges.Payload(1)[0].ItemId);
    }

    [Fact]
    public void Remove_takes_the_edge_out_and_closes_the_gap()
    {
        var edges = new EdgeTable<LibraryEdge>();
        edges.ReplaceRun(Parent, Three, [new(1, 0), new(2, 0), new(3, 0)]);
        Assert.True(edges.Remove(Parent, 22));
        Assert.True(edges.Targets(Parent).SequenceEqual([11, 33]));
        Assert.Equal(3, edges.Payload(Parent)[1].AddedAt);                   // the payload moved with its target
        Assert.Equal(2, edges.Total(Parent));
        Assert.False(edges.Remove(Parent, 22));
    }

    [Theory]
    // a pending ADD: confirmed keeps it, rejected takes it back out
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    // a pending REMOVE: confirmed takes it out, rejected brings it back
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void Settle_resolves_an_optimistic_edge_four_ways(bool add, bool ok, bool present)
    {
        var edges = new EdgeTable<LibraryEdge>();
        edges.ReplaceRun(Parent, Three, []);

        if (add) edges.Insert(Parent, 44, new LibraryEdge(1, 0), at: 0, EdgePending.Add);
        else Assert.True(edges.MarkRemove(Parent, 22));

        int target = add ? 44 : 22;
        Assert.Equal(add ? EdgePending.Add : EdgePending.Remove, edges.PendingOf(Parent, target));

        Assert.True(edges.Settle(Parent, target, ok));
        Assert.Equal(present, edges.Contains(Parent, target));
        Assert.Equal(EdgePending.None, edges.PendingOf(Parent, target));     // …and nothing is left in flight
    }

    [Fact]
    public void Settling_an_edge_that_was_never_pending_changes_nothing()
    {
        var edges = new EdgeTable<LibraryEdge>();
        edges.ReplaceRun(Parent, Three, []);
        Assert.False(edges.Settle(Parent, 22, ok: true));                    // a late duplicate confirmation
        Assert.False(edges.Settle(Parent, 44, ok: false));                   // …or one for an edge we never had
        Assert.Equal(3, edges.Count(Parent));
    }

    // ── the arena ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The span rule, demonstrated: a rewrite that outgrows the parent's range MOVES it, so a span taken
    /// before the write points at the old bytes. This is why no caller may hold one across a drain.</summary>
    [Fact]
    public void A_rewrite_that_outgrows_the_range_moves_it()
    {
        var edges = new EdgeTable<NoEdge>();
        edges.ReplaceRun(Parent, [1, 2], []);
        int firstRange = edges.ArenaUsed;                                    // two edges plus the minimum slack
        Assert.Equal(0, edges.ArenaDead);

        edges.ReplaceRun(Parent, [1, 2, 3, 4, 5, 6], []);
        Assert.True(edges.ArenaUsed > firstRange);                           // the range moved to the tail…
        Assert.Equal(firstRange, edges.ArenaDead);                           // …and abandoned its old home
        Assert.True(edges.Targets(Parent).SequenceEqual([1, 2, 3, 4, 5, 6])); // reading through the table is always right
    }

    [Fact]
    public void Repeated_inserts_reuse_the_slack_instead_of_copying_every_time()
    {
        var edges = new EdgeTable<NoEdge>();
        edges.Insert(Parent, 1, default);
        int afterFirst = edges.ArenaUsed;
        for (int i = 2; i <= 4; i++) edges.Insert(Parent, i, default);
        Assert.Equal(afterFirst, edges.ArenaUsed);                           // ×2 slack absorbed the next three
        Assert.Equal(4, edges.Count(Parent));
    }

    [Fact]
    public void Compact_reclaims_the_arena_the_regrows_abandoned()
    {
        var edges = new EdgeTable<NoEdge>();
        for (int n = 1; n <= 40; n++)
        {
            var targets = new int[n];
            for (int i = 0; i < n; i++) targets[i] = i + 1;
            edges.ReplaceRun(7, targets, []);
        }
        edges.ReplaceRun(9, [1, 2, 3], []);

        Assert.True(edges.ArenaDead > 0);
        int reclaimed = edges.Compact();
        Assert.True(reclaimed > 0);
        Assert.Equal(0, edges.ArenaDead);
        Assert.Equal(43, edges.ArenaUsed);                                   // 40 + 3, exactly the live edges
        Assert.Equal(40, edges.Count(7));
        Assert.True(edges.Targets(9).SequenceEqual([1, 2, 3]));
        Assert.Equal(0, edges.Compact());                                    // nothing left to do
    }

    // ── the payloads and the merch side table ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_edge_payload_can_be_the_row_itself()
    {
        var tags = new EdgeTable<StringId>();
        StringId[] payload = [Entities.Strings.Intern("chill"), Entities.Strings.Intern("2010s")];
        tags.ReplaceRun(Parent, [0, 0], payload);                            // targets unused: the payload IS the row
        Assert.Equal("chill", Entities.Strings.Resolve(tags.Payload(Parent)[0]));
        Assert.Equal(2, tags.Count(Parent));
    }

    [Fact]
    public void Merch_rows_are_a_side_table_an_ordinary_edge_points_into()
    {
        var edges = new Edges();
        int first = edges.Merch.AllocRun(2);
        Assert.NotEqual(0, first);                                           // slot 0 is "none" here too
        edges.Merch.Row[first].Name = Entities.Strings.Intern("Tour Tee");
        edges.Merch.Row[first].Price = Entities.Strings.Intern("$25");       // the wire's own formatted string
        edges.Merch.Row[first + 1].Name = Entities.Strings.Intern("Poster");

        edges.AlbumMerch.ReplaceRun(Parent, [first, first + 1], []);
        Assert.Equal(2, edges.AlbumMerch.Count(Parent));
        int row = edges.AlbumMerch.Targets(Parent)[0];
        Assert.Equal("Tour Tee", Entities.Strings.Resolve(edges.Merch.Row[row].Name));
        Assert.Equal("$25", Entities.Strings.Resolve(edges.Merch.Row[row].Price));
    }

    [Fact]
    public void The_friends_feed_hangs_off_one_synthetic_subject()
    {
        var edges = new Edges();
        const int feed = 1;                                                  // ch 21 G6: a synthetic parent, not an entity
        edges.Friends.ReplaceRun(feed, [0, 0], [new FriendEdge(4, 1_700_000_000_000, 9, 3, 2, 7),
                                               new FriendEdge(5, 1_700_000_001_000, 8, 3, 2, 7)]);
        Assert.Equal(2, edges.Friends.Count(feed));
        Assert.Equal(9, edges.Friends.Payload(feed)[0].TrackSlot);           // five real handles, not five uri strings
        Assert.Equal(EdgeState.Complete, edges.Friends.State(feed));
    }

    // ── cross-kind relations (defect 5) ───────────────────────────────────────────────────────────────────────────

    /// <summary>A search "All" facet is six kinds in one ranked order, and a CSR target is a bare <c>int</c>: without
    /// the payload nothing records which table each hit belongs to (<c>Search.cs:7</c>, doc §3.1 requirement 4). The
    /// kind comes off the row's <see cref="EntityId"/>, so the writer copies one byte and the reader pairs it back.</summary>
    [Fact]
    public void A_cross_kind_result_list_records_which_table_each_slot_indexes()
    {
        var edges = new Edges();
        const int allFacet = 3;                                              // one synthetic subject per (query, facet)

        // What a commit does: it holds the identity it just resolved, so the payload is one field off it.
        var hits = new (EntityId Id, int Slot)[]
        {
            (EntityId.Parse("spotify:track:4uLU6hMCjMI75M1A2tKUQC".AsSpan()), 11),
            (EntityId.Parse("spotify:album:2noRn2Aes5aoNVsU6iWThc".AsSpan()), 4),
            (EntityId.Parse("spotify:artist:0OdUWJ0sBjDrqHygGUXeCF".AsSpan()), 4),   // same SLOT as the album above
            (EntityId.Parse("wavee:playlist:session-1".AsSpan()), 2),
        };
        var targets = new int[hits.Length];
        var payload = new KindEdge[hits.Length];
        for (int i = 0; i < hits.Length; i++) { targets[i] = hits[i].Slot; payload[i] = KindEdge.Of(hits[i].Id); }

        edges.SearchResult.Replace(allFacet, targets, payload, EdgeState.Complete, hits.Length);

        var slots = edges.SearchResult.Targets(allFacet);
        var kinds = edges.SearchResult.Payload(allFacet);
        Assert.Equal(EntityKind.Track, kinds[0].Kind);
        Assert.Equal(EntityKind.Album, kinds[1].Kind);
        Assert.Equal(EntityKind.Artist, kinds[2].Kind);
        Assert.Equal(EntityKind.Playlist, kinds[3].Kind);

        // Slot 4 appears twice and means two different rows in two different tables — which is exactly what a bare
        // slot could not say, and what an EntityRef does.
        Assert.Equal(new EntityRef(EntityKind.Album, 4), kinds[1].Ref(slots[1]));
        Assert.Equal(new EntityRef(EntityKind.Artist, 4), kinds[2].Ref(slots[2]));
        Assert.NotEqual(kinds[1].Ref(slots[1]), kinds[2].Ref(slots[2]));
        Assert.False(kinds[0].Ref(slots[0]).IsNone);
        Assert.True(new EntityRef(EntityKind.Track, 0).IsNone);
        Assert.True(new EntityRef(EntityKind.Unknown, 7).IsNone);
    }

    /// <summary>A single-kind facet writes the same payload in every entry — one byte a hit, and no reader has to know
    /// which facet it is reading. An empty payload is still legal (the relation is the fact), and then the kind is
    /// whatever the relation says it is.</summary>
    [Fact]
    public void A_single_kind_facet_pays_one_byte_a_hit_and_may_still_write_no_payload()
    {
        var edges = new Edges();
        edges.SearchResult.Replace(5, [7, 8, 9], [new KindEdge(EntityKind.Track), new KindEdge(EntityKind.Track),
                                                  new KindEdge(EntityKind.Track)], EdgeState.Complete, 3);
        Assert.Equal(EntityKind.Track, edges.SearchResult.Payload(5)[2].Kind);

        edges.SearchResult.Replace(6, [1, 2], default, EdgeState.Partial, 40);
        Assert.Equal(2, edges.SearchResult.Count(6));
        Assert.Equal(EntityKind.Unknown, edges.SearchResult.Payload(6)[0].Kind);
        Assert.Equal(EdgeState.Partial, edges.SearchResult.State(6));
    }
}
