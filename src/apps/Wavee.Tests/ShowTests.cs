// ── Wavee.Tests/ShowTests.cs — the show columns and THE PAGING CURSOR ─────────────────────────────────────────────
//
// Wave 1's gate for `Entities/Show.cs`. The cursor is the whole point of this file: 0.2.9 gated "Load more" on the
// RESIDENT count, so a withdrawn or region-locked member left the count permanently short of the total, the pill never
// disappeared, and every tap re-asked the same unanswerable page (ch 09 DATA GAPS; `ShowEpisodePagingTests`
// `LoadMoreEpisodes_AdvancesPastMembersThatCannotHydrate` is the 0.2.9 test that proves it).

using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class ShowTests
{
    [Fact]
    public void Identity_carries_the_publisher_line_that_0_2_9_built_and_never_rendered()
    {
        // ch 09 §1.1 / §9: the meta line is a DEFECT to fix, not a behaviour to port — so the column is in Identity.
        TestScope.Fresh();
        var s = Staging.Rent();
        ref var row = ref s.Shows.Add();
        row.Id = s.Text("spotify:show:s1");
        row.Title = s.Text("Slab Talk");
        row.Image = s.Text("spotify:image:ab6765630000ba8a2222222222222222222222aa");
        row.Publisher = s.Text("Wavee Studios");
        row.Known = (uint)ShowFields.Identity;
        row.Authority = Authority.Full;
        TestScope.CommitAndPublish(s);

        var show = Entities.Show(EntityUri.Parse("spotify:show:s1"));
        Assert.True(show.Knows(ShowFields.Identity));
        Assert.Equal("Slab Talk", show.Title);
        Assert.Equal("Wavee Studios", Entities.Strings.Resolve(show.PublisherId));
        Assert.False(show.Knows(ShowFields.About));
    }

    [Fact]
    public void The_episode_total_comes_from_the_edge_not_from_a_column()
    {
        TestScope.Fresh();
        var shows = Entities.Current.Shows;
        var episodes = Entities.Current.Episodes;
        int show = shows.Slot("spotify:show:paged".AsSpan());
        int e1 = episodes.Slot("spotify:episode:p1".AsSpan());
        int e2 = episodes.Slot("spotify:episode:p2".AsSpan());

        Entities.Current.Edges.ShowEpisodes.ReplacePage(show, 0, [e1, e2], default, total: 700);

        var handle = new Show(show);
        Assert.Equal(700, handle.TotalEpisodes);
        Assert.Equal(2, handle.EpisodeSlots.Length);
        Assert.Equal(EdgeState.Partial, Entities.Current.Edges.ShowEpisodes.State(show));
    }

    [Fact]
    public void The_cursor_only_moves_forward_so_an_out_of_order_page_cannot_rewind_it()
    {
        TestScope.Fresh();

        var first = Staging.Rent();
        ref var page2 = ref first.Shows.Add();
        page2.Id = first.Text("spotify:show:cursor");
        page2.EpisodesAsked = 100;
        page2.Known = (uint)ShowFields.None;                        // paging is not a field group: it is bookkeeping
        page2.Authority = Authority.Full;
        TestScope.CommitAndPublish(first);

        var second = Staging.Rent();
        ref var page1 = ref second.Shows.Add();
        page1.Id = second.Text("spotify:show:cursor");
        page1.EpisodesAsked = 50;                                   // the earlier page landed late
        page1.Authority = Authority.Full;
        TestScope.CommitAndPublish(second);

        var show = Entities.Show(EntityUri.Parse("spotify:show:cursor"));
        Assert.Equal(100, show.EpisodesAsked);
    }

    [Fact]
    public void The_cursor_advances_past_members_that_cannot_hydrate()
    {
        // The 0.2.9 bug, reproduced as a fact: a page of 50 comes back with 2 usable rows. Gating on the resident count
        // pins the pill forever; gating on the cursor retires it at the right moment.
        TestScope.Fresh();
        var shows = Entities.Current.Shows;
        var episodes = Entities.Current.Episodes;
        int show = shows.Slot("spotify:show:withdrawn".AsSpan());
        int e1 = episodes.Slot("spotify:episode:w1".AsSpan());
        int e2 = episodes.Slot("spotify:episode:w2".AsSpan());
        Entities.Current.Edges.ShowEpisodes.ReplacePage(show, 0, [e1, e2], default, total: 50);
        shows.EpisodesAsked[show] = 50;

        var handle = new Show(show);
        Assert.Equal(2, handle.EpisodeSlots.Length);                 // resident count says "keep asking" …
        Assert.Equal(50, handle.TotalEpisodes);
        Assert.False(handle.EpisodesAsked < handle.TotalEpisodes);   // … and the cursor says, correctly, "nothing left"
    }

    // ── the packed identity, and the text the row owns (defect 1, doc §4.4) ──────────────────────────

    /// <summary>A show's uri is a view over the packed id — a field load, not the 45-100 ns re-parse every <c>.Uri</c>
    /// read used to pay (defect 3) — and a catalog show interns nothing for its identity.</summary>
    [Fact]
    public void The_uri_is_a_view_over_the_id_and_a_catalog_show_interns_nothing_for_it()
    {
        TestScope.Fresh();
        var t = Entities.Current.Shows;
        int before = Entities.Strings.MapCount;

        int slot = t.Slot("spotify:show:4uLU6hMCjMI75M1A2tKUQC".AsSpan());
        Assert.Equal(before, Entities.Strings.MapCount);

        var show = new Show(slot);
        Assert.Equal(EntityKind.Show, show.Id.Kind);
        Assert.True(show.Id.IsContainer);
        Assert.Equal(EntityProvider.Spotify, show.Uri.Provider);
        Assert.Equal("spotify:show:4uLU6hMCjMI75M1A2tKUQC", show.Uri.Text);
    }

    /// <summary>DEFECT 1 for this kind: four text columns. The count is the check — the rail description is the
    /// longest single string this table holds, and before the override a trim reclaimed the row and none of it.</summary>
    [Fact]
    public void A_freed_show_row_hands_back_all_four_of_its_strings_and_its_uri()
    {
        TestScope.Fresh();
        var t = Entities.Current.Shows;
        int before = Entities.Strings.MapCount;

        int slot = t.Slot("spotify:show:teardown-20260912".AsSpan());             // a TEXT-form row: the uri is owned too
        t.SetText(ref t.Title, slot, Entities.Strings.Intern("show teardown title 20260912"));
        t.SetText(ref t.Image, slot, Entities.Strings.Intern("show teardown cover 20260912"));
        t.SetText(ref t.Publisher, slot, Entities.Strings.Intern("show teardown publisher 20260912"));
        t.SetText(ref t.Description, slot, Entities.Strings.Intern("show teardown blurb 20260912"));
        Assert.Equal(before + 5, Entities.Strings.MapCount);

        t.FreeSlot(slot);
        Assert.Equal(before, Entities.Strings.MapCount);
        Assert.True(t.Publisher[slot].IsEmpty);
    }

    /// <summary>Wave 5 (owner M): the page's gate reads THIS column through `Episode.Rules.CanLoadMore` — the cursor
    /// and the edge's total, never the resident count (ch 09 §9.1).</summary>
    [Fact]
    public void The_load_more_gate_is_the_cursor_through_the_episode_rules()
    {
        TestScope.Fresh();
        var shows = Entities.Current.Shows;
        var episodes = Entities.Current.Episodes;
        int show = shows.Slot("spotify:show:gate".AsSpan());
        int e1 = episodes.Slot("spotify:episode:g1".AsSpan());
        Entities.Current.Edges.ShowEpisodes.ReplacePage(show, 0, [e1], default, total: 30);

        var handle = new Show(show);
        Assert.True(Episode.Rules.CanLoadMore(handle.EpisodesAsked, 0, handle.TotalEpisodes));
        shows.EpisodesAsked[show] = 30;
        Assert.False(Episode.Rules.CanLoadMore(handle.EpisodesAsked, 0, handle.TotalEpisodes));
    }
}
