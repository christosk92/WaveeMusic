// ── Wavee.Tests/ProfileAskTests.cs — what a profile surface asks for, and how its regions read ─────────────────────────
//
// `ProfileAsk.Plan` and `ProfileLoadRule` are pure over values (profile pages plan, Appendix D §3.6): every decision is
// pinned here without a scope. The two impure doors (`Plan(User, …)`, `Header(User)`) read the live tables, so the last
// facts boot a fresh scope — and the class joins `EntitiesCollection` for it.

using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class ProfileAskTests
{
    static ProfileAskPlan Page(bool knowsSocial = false, bool unavailable = false, bool showFollows = false, bool current = false,
                               EdgeState followers = EdgeState.Unknown, EdgeState following = EdgeState.Unknown)
        => ProfileAsk.Plan(ProfileSurface.Page, knowsSocial, unavailable, showFollows, current, followers, following);

    [Fact]
    public void A_first_visit_ensures_the_row_and_both_lists_in_parallel()
    {
        // Nothing is known yet — not even ShowFollows — and the lists do not wait for it: the official client fires all three.
        var plan = Page();
        Assert.Equal(ProfileRowAsk.Ensure, plan.Row);
        Assert.Equal(ProfileEdgeAsk.Ensure, plan.Followers);
        Assert.Equal(ProfileEdgeAsk.Ensure, plan.Following);
        Assert.False(plan.TopArtists);
    }

    [Fact]
    public void A_revisit_invalidates_the_known_row_and_lists()
    {
        var plan = Page(knowsSocial: true, showFollows: true, followers: EdgeState.Complete, following: EdgeState.Partial);
        Assert.Equal(ProfileRowAsk.Invalidate, plan.Row);
        Assert.Equal(ProfileEdgeAsk.Invalidate, plan.Followers);
        Assert.Equal(ProfileEdgeAsk.Invalidate, plan.Following);

        // A list nobody answered yet (or one a failure un-asked) is Ensured, not Invalidated: there is nothing to keep rendering.
        var half = Page(knowsSocial: true, showFollows: true, followers: EdgeState.Complete, following: EdgeState.Unknown);
        Assert.Equal(ProfileEdgeAsk.Invalidate, half.Followers);
        Assert.Equal(ProfileEdgeAsk.Ensure, half.Following);
    }

    [Fact]
    public void Hidden_follows_skip_the_lists_on_a_revisit_but_never_on_your_own_profile()
    {
        var hidden = Page(knowsSocial: true, showFollows: false, followers: EdgeState.Complete, following: EdgeState.Complete);
        Assert.Equal(ProfileRowAsk.Invalidate, hidden.Row);
        Assert.Equal(ProfileEdgeAsk.None, hidden.Followers);
        Assert.Equal(ProfileEdgeAsk.None, hidden.Following);

        var own = Page(knowsSocial: true, showFollows: false, current: true, followers: EdgeState.Complete, following: EdgeState.Complete);
        Assert.Equal(ProfileEdgeAsk.Invalidate, own.Followers);
        Assert.Equal(ProfileEdgeAsk.Invalidate, own.Following);

        Assert.False(ProfileAsk.ListsWanted(knowsSocial: true, unavailable: false, showFollows: false, isCurrentUser: false));
        Assert.True(ProfileAsk.ListsWanted(knowsSocial: true, unavailable: false, showFollows: false, isCurrentUser: true));
        Assert.True(ProfileAsk.ListsWanted(knowsSocial: false, unavailable: false, showFollows: false, isCurrentUser: false));
    }

    [Fact]
    public void An_unavailable_profile_asks_no_lists()
    {
        // Even with the flags that would normally ask them: the 404 is a known negative, and its lists would 404 too.
        var plan = Page(knowsSocial: true, unavailable: true, showFollows: true, current: true,
                        followers: EdgeState.Complete, following: EdgeState.Complete);
        Assert.Equal(ProfileRowAsk.Invalidate, plan.Row);
        Assert.Equal(ProfileEdgeAsk.None, plan.Followers);
        Assert.Equal(ProfileEdgeAsk.None, plan.Following);
    }

    [Fact]
    public void Only_your_own_profile_asks_top_artists()
    {
        Assert.True(Page(knowsSocial: true, current: true).TopArtists);
        Assert.False(Page(knowsSocial: true, current: false).TopArtists);
        // The list pages never ask them, whoever the profile is.
        Assert.False(ProfileAsk.Plan(ProfileSurface.Followers, true, false, true, true, EdgeState.Unknown, EdgeState.Unknown).TopArtists);
        Assert.False(ProfileAsk.Plan(ProfileSurface.Following, true, false, true, true, EdgeState.Unknown, EdgeState.Unknown).TopArtists);
    }

    [Fact]
    public void A_list_page_asks_only_its_list_and_only_while_unknown()
    {
        // The page that opened it already refreshed this visit: a landed list is left alone.
        var landed = ProfileAsk.Plan(ProfileSurface.Followers, true, false, true, false, EdgeState.Complete, EdgeState.Unknown);
        Assert.Equal(new ProfileAskPlan(ProfileRowAsk.None, ProfileEdgeAsk.None, ProfileEdgeAsk.None, false), landed);

        // Deep-linked straight to a list: the row is Ensured too, and only the list the page shows is asked.
        var cold = ProfileAsk.Plan(ProfileSurface.Followers, false, false, false, false, EdgeState.Unknown, EdgeState.Unknown);
        Assert.Equal(new ProfileAskPlan(ProfileRowAsk.Ensure, ProfileEdgeAsk.Ensure, ProfileEdgeAsk.None, false), cold);

        var following = ProfileAsk.Plan(ProfileSurface.Following, true, false, true, false, EdgeState.Complete, EdgeState.Unknown);
        Assert.Equal(new ProfileAskPlan(ProfileRowAsk.None, ProfileEdgeAsk.None, ProfileEdgeAsk.Ensure, false), following);

        var partial = ProfileAsk.Plan(ProfileSurface.Following, true, false, true, false, EdgeState.Unknown, EdgeState.Partial);
        Assert.Equal(new ProfileAskPlan(ProfileRowAsk.None, ProfileEdgeAsk.None, ProfileEdgeAsk.None, false), partial);
    }

    [Theory]
    //   knows  unavail failed asked  inflight → verdict
    [InlineData(false, true, true, true, true, ProfileLoad.Unavailable)]      // the 404 flag wins over every mark
    [InlineData(true, true, false, true, false, ProfileLoad.Unavailable)]
    [InlineData(true, false, false, true, true, ProfileLoad.Ready)]           // a known row renders while a refresh is out
    [InlineData(true, false, true, true, false, ProfileLoad.Ready)]           // … and after a failed refresh
    [InlineData(false, false, false, false, false, ProfileLoad.Loading)]      // not asked yet: shimmer
    [InlineData(false, false, false, true, true, ProfileLoad.Loading)]        // asked and on the wire
    [InlineData(false, false, true, true, true, ProfileLoad.Loading)]         // a retry in flight outranks the old failure
    [InlineData(false, false, true, true, false, ProfileLoad.Failed)]         // terminal failure: the Retry vacancy
    [InlineData(false, false, false, true, false, ProfileLoad.Unavailable)]   // asked, nothing coming, nothing known: sealed
    public void The_header_reads_unavailable_first_ready_while_known_failed_then_sealed(
        bool knows, bool unavailable, bool failed, bool asked, bool inflight, ProfileLoad expected)
        => Assert.Equal(expected, ProfileLoadRule.Header(knows, unavailable, failed, asked, inflight));

    [Fact]
    public void Riding_shelves_follow_the_header_until_they_land()
    {
        // A landed shelf is Ready whatever the header says (it keeps rendering through a refresh).
        Assert.Equal(ProfileLoad.Ready, ProfileLoadRule.Riding(ProfileLoad.Loading, EdgeState.Complete));
        Assert.Equal(ProfileLoad.Ready, ProfileLoadRule.Riding(ProfileLoad.Failed, EdgeState.Partial));
        // The header landed but this shelf has not: still loading, never "empty".
        Assert.Equal(ProfileLoad.Loading, ProfileLoadRule.Riding(ProfileLoad.Ready, EdgeState.Unknown));
        // Before the header lands, or when it cannot: the header's own verdict.
        Assert.Equal(ProfileLoad.Loading, ProfileLoadRule.Riding(ProfileLoad.Loading, EdgeState.Unknown));
        Assert.Equal(ProfileLoad.Failed, ProfileLoadRule.Riding(ProfileLoad.Failed, EdgeState.Unknown));
        Assert.Equal(ProfileLoad.Unavailable, ProfileLoadRule.Riding(ProfileLoad.Unavailable, EdgeState.Failed));
    }

    [Fact]
    public void A_list_noroute_failure_reads_unavailable()
    {
        Assert.Equal(ProfileLoad.Unavailable, ProfileLoadRule.List(EdgeState.Failed, EdgeTableBase.NoRoute));
        Assert.Equal(ProfileLoad.Failed, ProfileLoadRule.List(EdgeState.Failed, 500));
        Assert.Equal(ProfileLoad.Failed, ProfileLoadRule.List(EdgeState.Failed, EdgeTableBase.Transport));
        Assert.Equal(ProfileLoad.Loading, ProfileLoadRule.List(EdgeState.Unknown, 0));
        Assert.Equal(ProfileLoad.Ready, ProfileLoadRule.List(EdgeState.Complete, 0));
        Assert.Equal(ProfileLoad.Ready, ProfileLoadRule.List(EdgeState.Partial, 0));
    }

    [Fact]
    public void Top_artists_rule()
    {
        Assert.Equal(ProfileLoad.Ready, ProfileLoadRule.TopArtists(HomeLoad.Pending, 3));        // rows beat every state
        Assert.Equal(ProfileLoad.Ready, ProfileLoadRule.TopArtists(HomeLoad.Failed, 1));
        Assert.Equal(ProfileLoad.Loading, ProfileLoadRule.TopArtists(HomeLoad.Pending, 0));
        Assert.Equal(ProfileLoad.Failed, ProfileLoadRule.TopArtists(HomeLoad.Failed, 0));
        Assert.Equal(ProfileLoad.Ready, ProfileLoadRule.TopArtists(HomeLoad.Ready, 0));          // answered, and there are none
        Assert.Equal(ProfileLoad.Unavailable, ProfileLoadRule.TopArtists(HomeLoad.Idle, 0));     // offline / --fake: nothing coming
    }

    // ── the two doors that read the live tables ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_never_asked_profile_row_plans_a_first_visit_and_reads_loading()
    {
        TestScope.Fresh();
        var user = Entities.User(EntityUri.Parse("spotify:user:ask-tester".AsSpan()));

        Assert.Equal(new ProfileAskPlan(ProfileRowAsk.Ensure, ProfileEdgeAsk.Ensure, ProfileEdgeAsk.Ensure, false),
                     ProfileAsk.Plan(user, ProfileSurface.Page));
        Assert.Equal(ProfileLoad.Loading, ProfileLoadRule.Header(user));
    }

    [Fact]
    public void A_known_hidden_profile_row_plans_a_revisit_without_lists()
    {
        TestScope.Fresh();
        var user = Entities.User(EntityUri.Parse("spotify:user:ask-hidden".AsSpan()));
        var t = Entities.Current.Users;
        t.Known[user.Slot] |= (uint)UserFields.Social;
        t.Flags[user.Slot] = 0;                                    // ShowFollows off, not the current user

        Assert.Equal(new ProfileAskPlan(ProfileRowAsk.Invalidate, ProfileEdgeAsk.None, ProfileEdgeAsk.None, false),
                     ProfileAsk.Plan(user, ProfileSurface.Page));
        Assert.Equal(ProfileLoad.Ready, ProfileLoadRule.Header(user));

        t.Flags[user.Slot] = (uint)(UserFlags.Unavailable);       // the 404 negative
        Assert.Equal(ProfileLoad.Unavailable, ProfileLoadRule.Header(user));
    }
}
