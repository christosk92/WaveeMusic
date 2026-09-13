// ── Wavee.Tests/UserTests.cs — the account row, the chip slabs, and the C6 library dance ─────────────────────────
//
// Wave 1's gate for Entities/User.cs (plan §5: "CSR replace/page/contains/insert/settle"). The library relations are
// ordinary `EdgeTable<LibraryEdge>`s, so they are exercised here directly rather than through `User.Me` — a handle
// needs `Entities.Current`, which needs `Boot` (harmless for a fake scope: `Store.Boot` is a no-op until a path is
// set, D17). What is pinned is the SEMANTICS User.cs depends on: newest-first insertion, a double-click that does not
// add a second edge, and the four settle cases.
//
// Added 2026-09-12 with the packed identity (docs/plans/wavee/wavee-0.3-entity-identity-memory.md, option 2): the
// write seam now carries an `EntityId` instead of a `StringId` uri, and every string a user row owns is REF-COUNTED
// (defect 1). The lifetime facts observe the engine's own contract rather than a counter — the LAST release removes
// the map entry and ids are never reused, so re-interning the same content afterwards mints a DIFFERENT id. Each of
// them uses strings unique to the fact, because the interner is process-wide and this collection is what serialises
// the tests.

using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class UserTests
{
    const int Me = 3;
    static StringId Uri(string s) => Entities.Strings.Intern(s);

    /// <summary>A real-shaped gid: 22 base62 characters (a fixture id would take the text form).</summary>
    static string Gid(int seed)
    {
        Span<char> buf = stackalloc char[Base62.GidChars];
        Base62.Encode(new UInt128((ulong)seed * 0x9E37_79B9_7F4A_7C15UL, (ulong)seed * 0xC2B2_AE3D_27D4_EB4FUL + 7), buf);
        return new string(buf);
    }

    // ── the row ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_user_row_is_two_columns_and_grows_with_the_table()
    {
        var t = new UserTable();
        int slot = 0;
        for (int i = 0; i < 40; i++) slot = t.Alloc(Uri($"spotify:user:u{i}"));
        t.SetText(ref t.Name, slot, Uri("Christos"));  // the only sanctioned write to a text column (defect 1)
        t.Followers[slot] = 12;
        Assert.Equal(12, t.Followers[slot]);
        Assert.Equal(EntityKind.User, t.Kind);
    }

    [Fact]
    public void Content_filter_chips_are_two_parallel_slabs_addressed_by_a_range()
    {
        // ch 07 §7 G3/G4: the chip joins on the TOKEN and renders the LABEL. One string for both is why a chip could
        // match nothing, so the row carries a range into two slabs, not one list of one string.
        var t = new UserTable();
        int a = t.Alloc(Uri("spotify:user:a"));
        int b = t.Alloc(Uri("spotify:user:b"));

        t.SetContentFilters(a, [Uri("K-Pop"), Uri("Chill")], [Uri("k-pop"), Uri("chill")]);
        t.SetContentFilters(b, [Uri("Rock")], [Uri("rock")]);

        Assert.Equal(2, t.FilterCount[a]);
        Assert.Equal(1, t.FilterCount[b]);
        Assert.Equal(Uri("k-pop"), t.FilterTokens[t.FilterStart[a]]);
        Assert.Equal(Uri("Rock"), t.FilterTitles[t.FilterStart[b]]);
        Assert.NotEqual(t.FilterStart[a], t.FilterStart[b]);          // the ranges do not overlap
    }

    [Fact]
    public void An_empty_chip_answer_is_a_real_answer_and_not_a_missing_one()
    {
        // ch 07 §7 G10: an EMPTY curated set is PUBLISHED — it is what hands the bar to the descriptor fallback. A
        // rewrite to zero chips must therefore stick, not leave the previous set standing.
        var t = new UserTable();
        int slot = t.Alloc(Uri("spotify:user:a"));
        t.SetContentFilters(slot, [Uri("K-Pop")], [Uri("k-pop")]);
        t.SetContentFilters(slot, [], []);
        Assert.Equal(0, t.FilterCount[slot]);
    }

    // ── the library, as edges (G6) ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Liking_prepends_so_the_liked_cover_stays_newest_first()
    {
        var liked = new EdgeTable<LibraryEdge>();
        liked.Replace(Me, [10, 11, 12], [], EdgeState.Complete, 3);

        liked.Insert(Me, 20, new LibraryEdge(1_700_000, 0), at: 0, EdgePending.Add);

        Assert.True(liked.Targets(Me).SequenceEqual([20, 10, 11, 12]));
        Assert.Equal(EdgePending.Add, liked.PendingOf(Me, 20));
        Assert.True(liked.Contains(Me, 20));                          // the heart fills in the same frame (C6)
    }

    [Fact]
    public void A_double_click_on_the_heart_does_not_add_a_second_edge()
    {
        var liked = new EdgeTable<LibraryEdge>();
        liked.Insert(Me, 20, new LibraryEdge(1, 0), at: 0, EdgePending.Add);
        liked.Insert(Me, 20, new LibraryEdge(2, 0), at: 0, EdgePending.Add);

        Assert.Equal(1, liked.Count(Me));
        Assert.Equal(2, liked.Payload(Me)[0].AddedAt);                // updated in place
    }

    [Fact]
    public void An_unlike_keeps_the_row_until_the_server_agrees()
    {
        var liked = new EdgeTable<LibraryEdge>();
        liked.Replace(Me, [10, 11], [], EdgeState.Complete, 2);

        Assert.True(liked.MarkRemove(Me, 10));
        Assert.Equal(2, liked.Count(Me));                             // still there, greyed — the list must not jump
        Assert.Equal(EdgePending.Remove, liked.PendingOf(Me, 10));

        Assert.True(liked.Settle(Me, 10, ok: true));
        Assert.Equal(1, liked.Count(Me));
        Assert.False(liked.Contains(Me, 10));
    }

    [Fact]
    public void A_rejected_write_reverts_in_both_directions()
    {
        var liked = new EdgeTable<LibraryEdge>();
        liked.Replace(Me, [10], [], EdgeState.Complete, 1);

        liked.Insert(Me, 20, new LibraryEdge(1, 0), at: 0, EdgePending.Add);
        liked.Settle(Me, 20, ok: false);
        Assert.False(liked.Contains(Me, 20));                         // the like never happened

        liked.MarkRemove(Me, 10);
        liked.Settle(Me, 10, ok: false);
        Assert.True(liked.Contains(Me, 10));                          // the row comes back
        Assert.Equal(EdgePending.None, liked.PendingOf(Me, 10));
    }

    [Fact]
    public void An_unanswered_library_is_unknown_and_not_empty()
    {
        // ch 15 §7: `State == 0` is the "…" caption, never an empty state. Getting this wrong is how a library page
        // tells a user with 400 saved albums that they have none.
        var saved = new EdgeTable<LibraryEdge>();
        Assert.Equal(EdgeState.Unknown, saved.State(Me));
        Assert.Equal(0, saved.Count(Me));

        saved.Replace(Me, [], [], EdgeState.Complete, 0);
        Assert.Equal(EdgeState.Complete, saved.State(Me));            // NOW it is a real, renderable empty
    }

    [Fact]
    public void The_added_at_lives_on_the_edge_and_not_on_the_row()
    {
        // ch 07 §7 G6: the week spark, the "since" line and the rediscover pick all read this, and it is an edge
        // payload — two accounts saving the same album disagree about the date, and only the edge can hold that.
        var saved = new EdgeTable<LibraryEdge>();
        saved.Replace(Me, [10, 11], [new LibraryEdge(100, 0), new LibraryEdge(200, 0)], EdgeState.Complete, 2);
        Assert.Equal(200, saved.Payload(Me)[1].AddedAt);
    }

    // ── the rootlist ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_rootlist_is_a_flat_stream_with_folder_markers()
    {
        // Not a tree: a reorder has to be a single index move, and the sidebar's tree is built from this stream.
        var rootlist = new EdgeTable<RootlistEdge>();
        rootlist.ReplaceRun(Me,
            [0, 41, 42, 0],
            [
                new RootlistEdge(0, 0, (byte)RootlistKind.FolderStart, Uri("Mixes"), 0),
                new RootlistEdge(1, 1, (byte)RootlistKind.Item, StringId.Empty, 10),
                new RootlistEdge(2, 1, (byte)RootlistKind.Item, StringId.Empty, 20),
                new RootlistEdge(3, 0, (byte)RootlistKind.FolderEnd, StringId.Empty, 0),
            ]);

        var rows = rootlist.Payload(Me);
        Assert.Equal(4, rows.Length);
        Assert.Equal((byte)RootlistKind.FolderStart, rows[0].Kind);
        Assert.Equal(1, rows[1].Depth);
        Assert.Equal((byte)RootlistKind.FolderEnd, rows[3].Kind);
        Assert.Equal(Table.None, rootlist.Targets(Me)[0]);            // a marker has no playlist behind it (P3)
    }

    [Theory]
    [InlineData(RootlistKind.Item, 0)]
    [InlineData(RootlistKind.FolderStart, 1)]
    [InlineData(RootlistKind.FolderEnd, 2)]
    public void The_rootlist_marker_codes_are_the_wires(RootlistKind kind, int wire) => Assert.Equal(wire, (byte)kind);

    // ── identity: a user is always the text form ───────────────────────────────────────────────────

    [Fact]
    public void A_user_row_keeps_its_uri_text_because_a_username_is_not_a_gid()
    {
        // `spotify:user:<name>` is never 22 base62 characters, so a user row takes the TEXT form and is one of the
        // populations that gets no memory win from the packed id — only the correct lifetime (doc §6 "not solved").
        var t = new UserTable();
        int slot = t.Slot("spotify:user:christos".AsSpan());
        Assert.Equal(EntityForm.Text, t.Id[slot].Form);
        Assert.Equal(EntityKind.User, t.Id[slot].Kind);
        Assert.Equal(EntityProvider.Spotify, t.Id[slot].Provider);
        Assert.Equal("spotify:user:christos", t.Id[slot].Text);
        Assert.Equal(1, t.TextRows);
        Assert.Equal(0, t.IndexedRows);                               // nothing in the gid index
    }

    // ── the write seam carries the packed identity (C6) ──────────────────────────────────────────

    [Fact]
    public void Liking_lands_the_edge_optimistically_and_dispatches_the_packed_id()
    {
        // `Add` used to take a `StringId targetUri`, which meant the model resolved an interned string per click for a
        // shell that only ever formats it into a request body (doc §3.1 item 2). It carries the `EntityId` now — the
        // kind rides along, so the dispatcher does not infer one from the relation. `Dispatch` is an unimplemented
        // partial method here (D17), so what this pins is the MODEL half: the heart fills in the same frame.
        TestScope.Fresh();
        var me = Entities.User(EntityUri.Parse("spotify:user:christos".AsSpan()));
        var target = EntityId.Parse($"spotify:track:{Gid(301)}".AsSpan());
        int trackSlot = Entities.Current.Tracks.Slot(target);

        me.Add(LibraryEdgeKind.Liked, trackSlot, target);

        Assert.True(me.Has(LibraryEdgeKind.Liked, trackSlot));
        Assert.Equal(EdgePending.Add, me.PendingOf(LibraryEdgeKind.Liked, trackSlot));
        Assert.Equal(EntityKind.User, me.Id.Kind);
        Assert.Equal("spotify:user:christos", me.Uri.Text);

        me.Remove(LibraryEdgeKind.Liked, trackSlot, target);
        Assert.Equal(EdgePending.Remove, me.PendingOf(LibraryEdgeKind.Liked, trackSlot));
        Assert.True(me.Has(LibraryEdgeKind.Liked, trackSlot));        // still there, greyed, until the server agrees
    }

    // ── defect 1: the row owns its text, and gives it back ─────────────────────────────────────────────

    [Fact]
    public void A_freed_user_row_hands_back_its_name_its_avatar_and_its_uri()
    {
        var t = new UserTable();
        StringId uri = Uri("spotify:user:UserTests-freed");
        int slot = t.Alloc(uri);
        StringId name = Uri("UserTests/freed/name");
        StringId image = Uri("UserTests/freed/image");
        t.SetText(ref t.Name, slot, name);
        t.SetText(ref t.Image, slot, image);

        t.FreeSlot(slot);

        Assert.NotEqual(uri, Uri("spotify:user:UserTests-freed"));
        Assert.NotEqual(name, Uri("UserTests/freed/name"));
        Assert.NotEqual(image, Uri("UserTests/freed/image"));
        Assert.Equal(0, t.TextRows);
    }

    [Fact]
    public void Rewriting_the_chip_set_hands_the_previous_set_back()
    {
        // The chip slabs are a bump allocator: a rewrite ABANDONS the previous range (P5). Abandoning the cells is
        // fine; abandoning the strings is the leak — an account whose curated set is re-answered once a session would
        // pin every set it ever had. `SetContentFilters` releases the old range before it appends the new one.
        var t = new UserTable();
        int slot = t.Alloc(Uri("spotify:user:UserTests-chips"));
        StringId title = Uri("UserTests/chips/K-Pop");
        StringId token = Uri("UserTests/chips/k-pop");
        t.SetContentFilters(slot, [title], [token]);

        t.SetContentFilters(slot, [Uri("UserTests/chips/Rock")], [Uri("UserTests/chips/rock")]);

        Assert.NotEqual(title, Uri("UserTests/chips/K-Pop"));
        Assert.NotEqual(token, Uri("UserTests/chips/k-pop"));
        Assert.Equal(1, t.FilterCount[slot]);
        Assert.Equal("UserTests/chips/Rock", Entities.Strings.Resolve(t.FilterTitles[t.FilterStart[slot]]));
    }

    [Fact]
    public void An_empty_chip_answer_hands_the_whole_previous_set_back()
    {
        // ch 07 §7 G10 already says an empty answer is a real publish; with ref counting it also has to be a real
        // release, or "the user turned their curated set off" is the most expensive thing they can do.
        var t = new UserTable();
        int slot = t.Alloc(Uri("spotify:user:UserTests-chips-empty"));
        StringId a = Uri("UserTests/chipsEmpty/one");
        StringId b = Uri("UserTests/chipsEmpty/two");
        t.SetContentFilters(slot, [a, b], [a, b]);

        t.SetContentFilters(slot, [], []);

        Assert.Equal(0, t.FilterCount[slot]);
        Assert.NotEqual(a, Uri("UserTests/chipsEmpty/one"));
        Assert.NotEqual(b, Uri("UserTests/chipsEmpty/two"));
    }

    [Fact]
    public void Freeing_the_row_hands_its_chip_set_back_too()
    {
        var t = new UserTable();
        int slot = t.Alloc(Uri("spotify:user:UserTests-chips-freed"));
        StringId title = Uri("UserTests/chipsFreed/label");
        t.SetContentFilters(slot, [title], [Uri("UserTests/chipsFreed/token")]);

        t.FreeSlot(slot);

        Assert.Equal(0, t.FilterCount[slot]);
        Assert.NotEqual(title, Uri("UserTests/chipsFreed/label"));
    }
}
