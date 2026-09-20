// ── Wavee.Tests/EdgesStagingTests.cs — the staged-edge buffer and Entities.CommitEdges ───────────────────────────
//
// The gate for `Entities/Edges.Staging.cs`: the half of the relationship model that crosses the thread boundary.
// `EdgesTests.cs` pins the live CSR table against direct calls; this pins what a DECODER hands the drain — a
// `StagedId` parent, an ordered run of `StagedId` children, a payload family chosen by the run's `Relation` — and the
// commit that turns them into that table's `Replace` / `ReplacePage`.
//
// Nothing here decodes bytes. `EdgeRun` is the whole staging API a decoder uses, so the facts are written the way a
// decoder writes them, and every assertion reads the LIVE edge table afterwards — never the staged buffer.
//
// The recents facts sit here too: `Edges.Recents` / `Edges.RecentsMembers` are relations with their own payload and
// their own commit arm (ch 16 §7.2), and the thing worth pinning is the same thing — a staged page becomes a table.

using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class EdgesStagingTests
{
    // Deterministic catalog identities, the packed form a protobuf decoder actually holds (no uri text anywhere).
    static EntityId Gid(EntityKind kind, byte seed)
    {
        Span<byte> gid = stackalloc byte[16];
        for (int i = 0; i < 16; i++) gid[i] = (byte)(seed + i);
        return EntityId.ForGid(kind, gid);
    }

    static Track TrackOf(byte seed) => Entities.Track(Gid(EntityKind.Track, seed));
    static Album AlbumOf(byte seed) => Entities.Album(Gid(EntityKind.Album, seed));
    static Artist ArtistOf(byte seed) => Entities.Artist(Gid(EntityKind.Artist, seed));
    /// <summary>The signed-in account's row, AND the scope pointed at it. `CatalogScope.Fake()` carries no account,
    /// so `Entities.Boot` leaves `MeSlot` at `Table.None` — and `Recents.Me`, which is what a page binds, would read
    /// parent 0 while the answer staged against the account's own row. A real boot resolves the two together
    /// (`Entities.ResolveMe`); a test that stages a library or a recents snapshot has to do the same.</summary>
    static User Me()
    {
        var me = Entities.User(EntityUri.Parse("spotify:user:tester".AsSpan()));
        Entities.Current.MeSlot = me.Slot;
        return me;
    }

    // ── two relations in one batch ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Two_relations_stage_side_by_side_and_land_in_their_own_tables()
    {
        TestScope.Fresh();
        var s = Staging.Rent();

        // An album's tracklist, with the disc/number pair that belongs to the PAIR and not to the track (D10) …
        var tracks = s.Run(Relation.AlbumTracks);
        for (byte i = 0; i < 3; i++)
        {
            ref var edge = ref tracks.Add();
            edge.Target = Gid(EntityKind.Track, (byte)(10 + i));
            edge.B0 = 1;
            edge.U0 = (ushort)(i + 1);
        }
        StagedId album = Gid(EntityKind.Album, 90);
        tracks.End(in album, EdgeState.Complete, 3);

        // … and, interleaved in the same buffer, one of those tracks' artists.
        var artists = s.Run(Relation.TrackArtists);
        artists.Add(Gid(EntityKind.Artist, 50));
        artists.Add(Gid(EntityKind.Artist, 60));
        StagedId track = Gid(EntityKind.Track, 10);
        artists.End(in track);

        TestScope.CommitAndPublish(s);

        var edges = Entities.Current.Edges;
        int albumSlot = AlbumOf(90).Slot;
        Assert.Equal(EdgeState.Complete, edges.AlbumTracks.State(albumSlot));
        Assert.Equal(3, edges.AlbumTracks.Count(albumSlot));
        Assert.True(edges.AlbumTracks.Targets(albumSlot)
            .SequenceEqual([TrackOf(10).Slot, TrackOf(11).Slot, TrackOf(12).Slot]));
        Assert.Equal(1, edges.AlbumTracks.Payload(albumSlot)[2].Disc);
        Assert.Equal(3, edges.AlbumTracks.Payload(albumSlot)[2].Number);

        int trackSlot = TrackOf(10).Slot;
        Assert.Equal(EdgeState.Complete, edges.TrackArtists.State(trackSlot));
        Assert.True(edges.TrackArtists.Targets(trackSlot).SequenceEqual([ArtistOf(50).Slot, ArtistOf(60).Slot]));
    }

    [Fact]
    public void A_child_the_wire_never_identified_is_compacted_out_of_its_run()
    {
        TestScope.Fresh();
        var s = Staging.Rent();

        var artists = s.Run(Relation.TrackArtists);
        artists.Add(Gid(EntityKind.Artist, 50));
        artists.Add().B0 = 7;                                  // named, never identified: the wire gave no gid
        artists.Add(Gid(EntityKind.Artist, 60));
        StagedId track = Gid(EntityKind.Track, 10);
        artists.End(in track);

        TestScope.CommitAndPublish(s);

        var list = Entities.Current.Edges.TrackArtists;
        int slot = TrackOf(10).Slot;
        Assert.Equal(2, list.Count(slot));                     // and NOT three with a slot-0 hole in the middle
        Assert.True(list.Targets(slot).SequenceEqual([ArtistOf(50).Slot, ArtistOf(60).Slot]));
    }

    // ── pages (D7) ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_staged_page_stays_partial_until_its_own_length_reaches_the_total()
    {
        TestScope.Fresh();
        StagedId album = Gid(EntityKind.Album, 90);

        var first = Staging.Rent();
        var page1 = first.Run(Relation.AlbumTracks);
        page1.Add(Gid(EntityKind.Track, 10));
        page1.Add(Gid(EntityKind.Track, 11));
        page1.Page(in album, offset: 0, total: 4);
        TestScope.CommitAndPublish(first);

        var edges = Entities.Current.Edges.AlbumTracks;
        int slot = AlbumOf(90).Slot;
        Assert.Equal(EdgeState.Partial, edges.State(slot));
        Assert.Equal(4, edges.Total(slot));

        var second = Staging.Rent();
        var page2 = second.Run(Relation.AlbumTracks);
        page2.Add(Gid(EntityKind.Track, 12));
        page2.Add(Gid(EntityKind.Track, 13));
        page2.Page(in album, offset: 2, total: 4);
        TestScope.CommitAndPublish(second);

        Assert.Equal(EdgeState.Complete, edges.State(slot));
        Assert.Equal(4, edges.Count(slot));
        Assert.Equal(TrackOf(13).Slot, edges.Targets(slot)[3]);
    }

    [Fact]
    public void A_run_whose_parent_turned_out_to_have_no_identity_lands_nothing_and_leaves_no_orphans()
    {
        TestScope.Fresh();
        var s = Staging.Rent();

        var orphan = s.Run(Relation.AlbumTracks);
        orphan.Add(Gid(EntityKind.Track, 70));
        orphan.Add(Gid(EntityKind.Track, 71));
        orphan.Discard();                                      // the album's own gid never arrived

        // The NEXT run must not be able to slice the discarded children into itself.
        var artists = s.Run(Relation.TrackArtists);
        artists.Add(Gid(EntityKind.Artist, 50));
        StagedId track = Gid(EntityKind.Track, 10);
        artists.End(in track);

        TestScope.CommitAndPublish(s);

        Assert.Equal(EdgeState.Unknown, Entities.Current.Edges.AlbumTracks.State(AlbumOf(90).Slot));
        Assert.Equal(1, Entities.Current.Edges.TrackArtists.Count(TrackOf(10).Slot));
    }

    [Fact]
    public void An_empty_complete_run_is_a_real_answer_and_not_an_unasked_question()
    {
        // "This track has no descriptors" is an ANSWER (finding 27): the relation settles Complete with nothing in it,
        // and the planner stops asking. An absent run leaves it Unknown, which renders as a skeleton.
        TestScope.Fresh();
        var s = Staging.Rent();
        var tags = s.Run(Relation.TrackTags);
        StagedId track = Gid(EntityKind.Track, 10);
        tags.EndEvenIfEmpty(in track);
        TestScope.CommitAndPublish(s);

        var edges = Entities.Current.Edges.TrackTags;
        Assert.Equal(EdgeState.Complete, edges.State(TrackOf(10).Slot));
        Assert.Equal(0, edges.Count(TrackOf(10).Slot));
        Assert.Equal(EdgeState.Unknown, edges.State(TrackOf(11).Slot));
    }

    [Fact]
    public void The_payload_can_be_the_row_itself_so_a_tag_run_needs_no_targets()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        var tags = s.Run(Relation.TrackTags);
        tags.Add().Text = s.Text("chill");
        tags.Add().Text = s.Text("late night");
        StagedId track = Gid(EntityKind.Track, 10);
        tags.EndEvenIfEmpty(in track, 2);
        TestScope.CommitAndPublish(s);

        var payload = Entities.Current.Edges.TrackTags.Payload(TrackOf(10).Slot);
        Assert.Equal(2, payload.Length);
        Assert.Equal("chill", Entities.Strings.Resolve(payload[0]));
        Assert.Equal("late night", Entities.Strings.Resolve(payload[1]));
    }

    // ── the library: a text-form parent and a payload with a fact on it ─────────────────────────────────────────────

    [Fact]
    public void A_library_run_hangs_off_the_account_row_and_keeps_its_added_at()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        StagedId parent = s.Text("spotify:user:tester");        // a user is the TEXT form: its id is not a gid

        var liked = s.Run(Relation.Liked);
        liked.Add(Gid(EntityKind.Track, 10)).At = 1_700_000_000;
        liked.Add(Gid(EntityKind.Track, 11)).At = 1_600_000_000;
        liked.End(in parent);

        TestScope.CommitAndPublish(s);

        int me = Me().Slot;
        var edges = Entities.Current.Edges.Liked;
        Assert.Equal(EdgeState.Complete, edges.State(me));
        Assert.True(edges.Targets(me).SequenceEqual([TrackOf(10).Slot, TrackOf(11).Slot]));
        Assert.Equal(1_700_000_000, edges.Payload(me)[0].AddedAt);
        Assert.True(edges.Contains(me, TrackOf(11).Slot));     // membership IS the edge (G6, P3)
    }

    /// <summary>ONE RUN AT A TIME, and this is why the shared <c>collection</c> walk collects instead of opening two
    /// (<c>Spotify.Api.Library.CollectionFullWalk</c>). A run is a CONTIGUOUS slice of the staging's one edge list
    /// (<c>StagedEdgeList.Append</c>: start + length), so a second run opened while the first is still taking children
    /// puts its own children INSIDE the first's slice. `collection` is one wire set feeding two relations and its
    /// answer is PAGED, so from page two on the Liked run's slice swallowed the saved ALBUMS staged from page one —
    /// 18 of them on a real account, which is where `entity.miskind table=Track id=Album` and the repeating
    /// `store.edge.dropped relation=Liked dropped=18` came from. This pins the shape that replaced it: the paged
    /// relation takes every page, closes, and only then does the second relation open its own run.</summary>
    [Fact]
    public void A_second_relation_staged_after_the_paged_one_closes_keeps_both_slices_intact()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        StagedId parent = s.Text("spotify:user:tester");

        // "Page one" names two liked tracks and one saved album; "page two" names two more tracks and one more album.
        // The albums are COLLECTED — never staged into an open run — while the paged relation is still taking children.
        var collected = new List<(StagedId Target, int At)>();
        var liked = s.Run(Relation.Liked);
        liked.Add(Gid(EntityKind.Track, 10)).At = 11;
        liked.Add(Gid(EntityKind.Track, 11)).At = 12;
        collected.Add((Gid(EntityKind.Album, 90), 13));
        liked.Add(Gid(EntityKind.Track, 12)).At = 14;
        liked.Add(Gid(EntityKind.Track, 13)).At = 15;
        collected.Add((Gid(EntityKind.Album, 91), 16));
        liked.EndEvenIfEmpty(in parent);

        var albums = s.Run(Relation.SavedAlbums);
        for (int i = 0; i < collected.Count; i++)
        {
            var target = collected[i].Target;
            albums.Add(in target).At = collected[i].At;
        }
        albums.EndEvenIfEmpty(in parent);

        TestScope.CommitAndPublish(s);

        int me = Me().Slot;
        var likedEdges = Entities.Current.Edges.Liked;
        var albumEdges = Entities.Current.Edges.SavedAlbums;

        // Four tracks and nothing else: no album ever entered the Liked slice, so nothing mints an Album id in Tracks.
        Assert.True(likedEdges.Targets(me).SequenceEqual(
            [TrackOf(10).Slot, TrackOf(11).Slot, TrackOf(12).Slot, TrackOf(13).Slot]));
        Assert.True(albumEdges.Targets(me).SequenceEqual([AlbumOf(90).Slot, AlbumOf(91).Slot]));

        // The payloads travelled with their own run, so edge i still describes target i on BOTH sides.
        Assert.Equal(11, likedEdges.Payload(me)[0].AddedAt);
        Assert.Equal(15, likedEdges.Payload(me)[3].AddedAt);
        Assert.Equal(13, albumEdges.Payload(me)[0].AddedAt);
        Assert.Equal(16, albumEdges.Payload(me)[1].AddedAt);
    }

    // ── the cross-kind relations carry the table each target belongs to ────────────────────────────────────────────

    [Fact]
    public void A_section_card_run_records_which_table_each_card_indexes()
    {
        // A band mixes playlists, albums and artists in one ranked order; a bare slot cannot say which is which
        // (ch 10 §7's `Edges.SectionCards`, defect 5).
        TestScope.Fresh();
        var s = Staging.Rent();
        StagedId section = s.Text("spotify:section:0JQ5DAIiKWzVFULQfUm85Y");

        var cards = s.Run(Relation.SectionCards);
        cards.Add(Gid(EntityKind.Album, 90));
        cards.Add(Gid(EntityKind.Artist, 50));
        cards.End(in section);

        TestScope.CommitAndPublish(s);

        int slot = Entities.Section("spotify:section:0JQ5DAIiKWzVFULQfUm85Y".AsSpan()).Slot;
        var edges = Entities.Current.Edges.SectionCards;
        Assert.Equal(2, edges.Count(slot));
        Assert.Equal(EntityKind.Album, edges.Payload(slot)[0].Kind);
        Assert.Equal(EntityKind.Artist, edges.Payload(slot)[1].Kind);
        Assert.Equal(AlbumOf(90).Slot, edges.Targets(slot)[0]);
        Assert.Equal(ArtistOf(50).Slot, edges.Targets(slot)[1]);
    }

    // ── recents (ch 16 §7.2) ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_recents_snapshot_lands_its_rows_its_members_and_its_revision()
    {
        TestScope.Fresh();
        Me();                                                   // the account row, and the scope pointed at it
        var s = Staging.Rent();

        ref var page = ref s.RecentsPages.Add();
        page.Parent = s.Text("spotify:user:tester");
        page.Revision = s.Text("00ff10");
        page.RowStart = 0;
        page.MemberStart = 0;

        // A collapsed GROUP header — its declared child count is the wire's, never the length of the member run,
        // which the server truncates.
        ref var group = ref s.RecentsRows.Add();
        group.Id = Gid(EntityKind.Album, 90);
        group.ItemId = s.Text("a1b2");
        group.PlayedAtMs = 1_700_000_000_000;
        group.ChildCount = 9;
        group.Kind = (byte)RecentsRowKind.Group;
        group.Reason = (byte)RecentsReason.Played;
        group.ContentType = (byte)RecentsContentType.Music;
        group.MembersStart = 0;
        group.MembersLen = 2;

        ref var single = ref s.RecentsRows.Add();
        single.Id = Gid(EntityKind.Track, 11);
        single.ItemId = s.Text("c3d4");
        single.PlayedAtMs = 1_690_000_000_000;
        single.Kind = (byte)RecentsRowKind.Single;
        single.Reason = (byte)RecentsReason.Saved;

        for (byte i = 0; i < 2; i++)
        {
            ref var member = ref s.RecentsMembers.Add();
            member.Id = Gid(EntityKind.Track, (byte)(10 + i));
            member.ItemId = s.Text("m" + i);
            member.PlayedAtMs = 1_700_000_000_000 - i;
        }

        page.RowCount = 2;
        page.MemberCount = 2;
        TestScope.CommitAndPublish(s);

        var recents = Recents.Me;
        Assert.Equal(EdgeState.Complete, recents.State);
        Assert.Equal(2, recents.Count);
        Assert.Equal(AlbumOf(90).Slot, recents.Slots[0]);

        ref readonly var row = ref recents.Rows[0];
        Assert.Equal(RecentsRowKind.Group, row.Shape);
        Assert.Equal(RecentsReason.Played, row.Why);
        Assert.Equal(RecentsContentType.Music, row.Axis);
        Assert.Equal(9, row.ChildCount);                        // the DECLARED count, not the two members present
        Assert.True(row.CanExpand);
        Assert.Equal("a1b2", Entities.Strings.Resolve(row.ItemId));

        Assert.True(recents.MemberSlots(in row).SequenceEqual([TrackOf(10).Slot, TrackOf(11).Slot]));
        Assert.Equal("m0", Entities.Strings.Resolve(recents.Members(in row)[0].ItemId));

        Assert.False(recents.Rows[1].CanExpand);                // a single play has no drawer
        Assert.Equal("00ff10", Entities.Strings.Resolve(recents.Revision));
    }

    [Fact]
    public void A_recents_snapshot_with_nothing_in_it_is_a_real_empty()
    {
        TestScope.Fresh();
        Me();                                                   // the account row, and the scope pointed at it
        var s = Staging.Rent();
        ref var page = ref s.RecentsPages.Add();
        page.Parent = s.Text("spotify:user:tester");
        TestScope.CommitAndPublish(s);

        var recents = Recents.Me;
        Assert.Equal(EdgeState.Complete, recents.State);         // "no recent plays", and the skeleton stops
        Assert.Equal(0, recents.Count);
    }

    // ── the allocation gate (P1, P8) ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_warm_re_stage_of_the_same_runs_allocates_nothing()
    {
        // The staged-edge buffer, the run list and the commit's payload scratch all grow to a high-water mark and
        // never again — which is the whole reason the buffer is pooled with its `Staging` (P8).
        TestScope.Fresh();
        var s = Staging.Rent();
        for (int i = 0; i < 3; i++) { StageOne(s); Entities.Commit(s); Entities.Publish(); s.Reset(); }

        long before = GC.GetAllocatedBytesForCurrentThread();
        StageOne(s);
        Entities.Commit(s);
        long after = GC.GetAllocatedBytesForCurrentThread();
        Entities.Publish();
        Staging.Return(s);

        Assert.Equal(0L, after - before);

        static void StageOne(Staging s)
        {
            var tracks = s.Run(Relation.AlbumTracks);
            for (byte i = 0; i < 8; i++)
            {
                ref var edge = ref tracks.Add();
                edge.Target = Gid(EntityKind.Track, (byte)(10 + i));
                edge.B0 = 1;
                edge.U0 = (ushort)(i + 1);
            }
            StagedId album = Gid(EntityKind.Album, 90);
            tracks.End(in album, EdgeState.Complete, 8);
        }
    }
}
