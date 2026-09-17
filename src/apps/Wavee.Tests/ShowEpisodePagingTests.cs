// ── Wavee.Tests/ShowEpisodePagingTests.cs — show paging end to end, over the edge and the cursor (ch 09 §7-§8) ────────
//
// Ported from 0.2.9 `Wavee.Tests/ShowEpisodePagingTests.cs` (246 lines, 5 facts), which drove the hydration ladder,
// the pump and `StoreLibrarySource`. Those have no 0.3 counterpart: the membership is `Edges.ShowEpisodes` (paged by
// `ReplacePage`, Partial until its length reaches the stated total), the POST arithmetic is `Spotify.Api.Batches`, the
// cursor is `ShowTable.EpisodesAsked` (advanced by the answer whether or not rows came back, forward only) and the gate
// is `Episode.Rules.CanLoadMore`. The assertions keep 0.2.9's numbers — 700 members in 300/300/100, a cursor that
// comes back unmoved past the end, a 12-episode show already done, and the regression fact
// `LoadMoreEpisodes_AdvancesPastMembersThatCannotHydrate` (8 members, 5 that land). NOT ported, with reason:
// `Open_AwaitsTheHeadPage_AndAsksForFullInTheBackground` and `Open_PagesTheWholeTailOnThePump` (the Open/Full ladder
// and the pump are 0.2.9's hydrator — 0.3's page demands its model through `Entities.EnsureEdge`, owned by Fetch) and
// `Open_RecordsTheShowAsARecentSurface_OnThePersistedStore` (the store's recent-surface pin, owned by Store.cs).

using System.Globalization;
using Wavee;
using Xunit;
using Rules = Wavee.Episode.Rules;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class ShowEpisodePagingTests
{
    const string ShowUri = "spotify:show:s700";
    const int Members = 700;

    static int Ep(int i) => Entities.Current.Episodes.Slot(("spotify:episode:e" + i.ToString(CultureInfo.InvariantCulture)).AsSpan());

    static int[] Episodes(int from, int count)
    {
        var slots = new int[count];
        for (int i = 0; i < count; i++) slots[i] = Ep(from + i);
        return slots;
    }

    /// <summary>The answer's cursor, the way a decoded show row carries it: forward only, bookkeeping not a field group.</summary>
    static void Asked(string showUri, int through)
    {
        var s = Staging.Rent();
        ref var row = ref s.Shows.RowFor(s.Text(showUri), Authority.Full, (uint)ShowFields.None);
        row.EpisodesAsked = through;
        TestScope.CommitAndPublish(s);
    }

    [Fact]
    public void Full_Pages700MembersInThreePages()
    {
        // THE regression: a 700-member show used to end at 300. Three requests of 300/300/100 — sizes, and the last is
        // the remainder rather than a padded page, and every member is asked exactly once.
        Span<Range> pages = stackalloc Range[8];
        int n = Spotify.Api.Batches(Members, pages);
        Assert.Equal(3, n);
        Span<int> sizes = stackalloc int[3];
        for (int i = 0; i < n; i++) sizes[i] = pages[i].End.Value - pages[i].Start.Value;
        sizes.Sort();
        Assert.Equal(new[] { 100, 300, 300 }, sizes.ToArray());
        Assert.Equal(Members, sizes[0] + sizes[1] + sizes[2]);
        Assert.Equal(Members, pages[n - 1].End.Value);
    }

    [Fact]
    public void LoadMoreEpisodes_PagesTheTailOnDemand_OnePagePerAsk()
    {
        TestScope.Fresh();
        var edges = Entities.Current.Edges.ShowEpisodes;
        var show = Entities.Show(EntityUri.Parse(ShowUri));

        edges.ReplacePage(show.Slot, 0, Episodes(0, 300), default, total: Members);
        Asked(ShowUri, 300);
        Assert.Equal(EdgeState.Partial, edges.State(show.Slot));
        Assert.Equal(300, show.EpisodeSlots.Length);
        Assert.True(Rules.CanLoadMore(show.EpisodesAsked, 0, show.TotalEpisodes));

        edges.ReplacePage(show.Slot, 300, Episodes(300, 300), default, total: Members);
        Asked(ShowUri, 600);
        Assert.Equal(600, show.EpisodeSlots.Length);
        Assert.Equal(600, show.EpisodesAsked);

        edges.ReplacePage(show.Slot, 600, Episodes(600, 100), default, total: Members);
        Asked(ShowUri, Members);
        Assert.Equal(Members, show.EpisodeSlots.Length);                // the LAST page is the remainder
        Assert.Equal(EdgeState.Complete, edges.State(show.Slot));       // its length reached the stated total

        // Past the end there is nothing to ask for — the cursor stays where it is, and the affordance is retired.
        Asked(ShowUri, Members);
        Assert.Equal(Members, show.EpisodesAsked);
        Assert.False(Rules.CanLoadMore(show.EpisodesAsked, 0, show.TotalEpisodes));
    }

    [Fact]
    public void LoadMoreEpisodes_OnAShorterThanOnePageShow_IsAlreadyDone()
    {
        TestScope.Fresh();
        var show = Entities.Show(EntityUri.Parse(ShowUri));
        Entities.Current.Edges.ShowEpisodes.ReplaceRun(show.Slot, Episodes(0, 12), default);
        Asked(ShowUri, 12);

        Assert.Equal(12, show.EpisodeSlots.Length);
        Assert.Equal(12, show.TotalEpisodes);                          // resident == total ⇒ no affordance
        Assert.Equal(12, show.EpisodesAsked);
        Assert.False(Rules.CanLoadMore(show.EpisodesAsked, 12, show.TotalEpisodes));
    }

    [Fact]
    public void LoadMoreEpisodes_AdvancesPastMembersThatCannotHydrate()
    {
        // THE load-more regression (finding 4): members that never hydrate — withdrawn, region-locked — keep the RESIDENT
        // count below the membership. The cursor advances on the ASK, so one page walks past them and retires the pill.
        const int members = 8;
        TestScope.Fresh();
        var show = Entities.Show(EntityUri.Parse(ShowUri));
        Entities.Current.Edges.ShowEpisodes.ReplacePage(show.Slot, 0, Episodes(0, 5), default, total: members);
        Asked(ShowUri, 5);                                              // one past the last member that HAS a row

        Assert.Equal(5, show.EpisodeSlots.Length);
        Assert.Equal(members, show.TotalEpisodes);
        Assert.Equal(5, show.EpisodesAsked);
        Assert.True(Rules.CanLoadMore(show.EpisodesAsked, 0, show.TotalEpisodes));

        Asked(ShowUri, members);                                        // the ask walked to the end; nothing landed
        Assert.Equal(5, show.EpisodeSlots.Length);                      // still five resident — the three are gone
        Assert.Equal(members, show.EpisodesAsked);
        Assert.False(Rules.CanLoadMore(show.EpisodesAsked, 0, show.TotalEpisodes));   // retired, not looping forever
    }

    [Fact]
    public void The_pages_own_cursor_covers_an_answer_that_wrote_nothing()
    {
        // 0.2.9's `_pagedTo` half (EpisodeList.cs:31-35, 70-71): a page whose members all failed writes nothing to the
        // model, so the model's cursor never moves — the page's own cursor is what stops the pill re-asking.
        TestScope.Fresh();
        var show = Entities.Show(EntityUri.Parse(ShowUri));
        Entities.Current.Edges.ShowEpisodes.ReplacePage(show.Slot, 0, Episodes(0, 300), default, total: 400);
        Asked(ShowUri, 300);

        Assert.True(Rules.CanLoadMore(show.EpisodesAsked, localAsked: 0, total: show.TotalEpisodes));
        Assert.False(Rules.CanLoadMore(show.EpisodesAsked, localAsked: 400, total: show.TotalEpisodes));
    }
}
