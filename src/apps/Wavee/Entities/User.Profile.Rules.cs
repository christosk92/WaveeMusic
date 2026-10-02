// ── Entities/User.Profile.Rules.cs — what a profile surface asks for, and how its regions read ────────────────────────
//
// Role: CORE (pure rules over values) + two impure doors (ProfileAsk.Plan(User, …) reads the tables, ProfileAsk.Apply writes
//       the demand)
// Owner: D3 (profile pages)
// Spec: docs/plans/wavee/profile-pages-implementation.md Appendix D §3.6 and "Reconciliation, round 2": ONE load vocabulary
//       (ProfileLoad + ProfileLoadRule), ONE demand (ProfileAsk) for the page and for both list pages.

namespace Wavee;

/// <summary>Which profile surface is mounting: the page, or one of its two "Show all" list pages.</summary>
public enum ProfileSurface : byte { Page, Followers, Following }
public enum ProfileRowAsk : byte { None, Ensure, Invalidate }
public enum ProfileEdgeAsk : byte { None, Ensure, Invalidate }
public readonly record struct ProfileAskPlan(ProfileRowAsk Row, ProfileEdgeAsk Followers, ProfileEdgeAsk Following, bool TopArtists);
/// <summary>A profile region's verdict: shimmer / content / "isn't available" vacancy / error vacancy with Retry.</summary>
public enum ProfileLoad : byte { Loading, Ready, Unavailable, Failed }

/// <summary>THE PROFILE DEMAND, once per (user slot, scope epoch) — the PageHost's DepKey. The official client re-reads the
/// profile and both lists on EVERY visit (research §1), so a known row is INVALIDATED (stale keeps rendering), never
/// left; a first visit fires the three reads in PARALLEL (the lists don't wait for ShowFollows); a known profile with
/// hidden follows asks no lists unless it is your own. The riding shelves have no ask — Social brings them.</summary>
public static class ProfileAsk
{
    public const UserFields PageFields = UserFields.Identity | UserFields.Social | UserFields.Follow;

    public static ProfileAskPlan Plan(ProfileSurface surface, bool knowsSocial, bool unavailable, bool showFollows,
                                      bool isCurrentUser, EdgeState followers, EdgeState following)
    {
        if (surface == ProfileSurface.Page)
        {
            bool lists = ListsWanted(knowsSocial, unavailable, showFollows, isCurrentUser);
            return new(knowsSocial ? ProfileRowAsk.Invalidate : ProfileRowAsk.Ensure,
                       lists ? Revisit(followers) : ProfileEdgeAsk.None,
                       lists ? Revisit(following) : ProfileEdgeAsk.None,
                       isCurrentUser);
        }
        // A list page: the page just refreshed the list on this visit — ask only while it is still Unknown (or un-asked by a failure).
        var row = knowsSocial ? ProfileRowAsk.None : ProfileRowAsk.Ensure;
        return surface == ProfileSurface.Followers
            ? new(row, FirstOnly(followers), ProfileEdgeAsk.None, false)
            : new(row, ProfileEdgeAsk.None, FirstOnly(following), false);
    }

    public static bool ListsWanted(bool knowsSocial, bool unavailable, bool showFollows, bool isCurrentUser)
        => !knowsSocial || (!unavailable && (showFollows || isCurrentUser));

    static ProfileEdgeAsk Revisit(EdgeState s) => s == EdgeState.Unknown ? ProfileEdgeAsk.Ensure : ProfileEdgeAsk.Invalidate;
    static ProfileEdgeAsk FirstOnly(EdgeState s) => s == EdgeState.Unknown ? ProfileEdgeAsk.Ensure : ProfileEdgeAsk.None;

    /// <summary>The plan for a live handle (reads the table).</summary>
    public static ProfileAskPlan Plan(User user, ProfileSurface surface)
        => Plan(surface, user.Knows(UserFields.Social), user.IsUnavailable, user.ShowFollows, user.IsCurrentUser,
                Entities.Current.Edges.ProfileFollowers.State(user.Slot), Entities.Current.Edges.ProfileFollowing.State(user.Slot));

    /// <summary>Execute a plan. UI thread; call from the demand effect only.</summary>
    public static void Apply(User user, in ProfileAskPlan plan)
    {
        if (!user.IsValid) return;
        if (plan.Row == ProfileRowAsk.Ensure) Entities.Ensure(user, PageFields);
        else if (plan.Row == ProfileRowAsk.Invalidate) Entities.Invalidate(user, PageFields);
        ApplyEdge(FetchEdge.ProfileFollowers, user.Slot, plan.Followers);
        ApplyEdge(FetchEdge.ProfileFollowing, user.Slot, plan.Following);
        if (plan.TopArtists) Home.Feeds.EnsureTopContent();
    }

    static void ApplyEdge(FetchEdge edge, int parent, ProfileEdgeAsk ask)
    {
        if (ask == ProfileEdgeAsk.Ensure) Entities.EnsureEdge(edge, parent);
        else if (ask == ProfileEdgeAsk.Invalidate) Entities.InvalidateEdge(edge, parent, FetchPriority.Visible);
    }

    /// <summary>The header's Retry: Refresh, never Ensure (a sealed Asked row is a no-op for Ensure).</summary>
    public static void RetryHeader(User user)
    {
        int slot = user.Slot;
        Entities.Refresh(Entities.Current.Users, new ReadOnlySpan<int>(in slot), (uint)PageFields);
    }

    /// <summary>A list's Retry (the two lists only; a riding shelf retries with the header).</summary>
    public static void RetryList(User user, ProfileShelf shelf)
    {
        var edge = User.FetchEdgeOf(shelf);
        if (edge == FetchEdge.None) RetryHeader(user);
        else Entities.RefreshEdge(edge, user.Slot);
    }
}

/// <summary>The profile's load verdicts, PURE over the marks (the SectionScreenLoadRule pattern).</summary>
public static class ProfileLoadRule
{
    /// <summary>The header, off the row's Social group. Unavailable (the 404 flag) wins; a KNOWN row is Ready even while a
    /// refresh is out or after it failed (it keeps rendering); a terminal failure of an unknown row is Failed; asked with
    /// nothing in flight and nothing known (a seal no answer will lift) is Unavailable; otherwise Loading.</summary>
    public static ProfileLoad Header(bool knowsSocial, bool unavailable, bool failed, bool asked, bool inflight)
    {
        if (unavailable) return ProfileLoad.Unavailable;
        if (knowsSocial) return ProfileLoad.Ready;
        if (inflight) return ProfileLoad.Loading;
        if (failed) return ProfileLoad.Failed;
        return asked ? ProfileLoad.Unavailable : ProfileLoad.Loading;
    }

    public static ProfileLoad Header(User u)
        => Header(u.Knows(UserFields.Social), u.IsUnavailable, u.IsFailed(UserFields.Social), u.IsAsked(UserFields.Social), u.IsInflight);

    /// <summary>A shelf that rides the header (Playlists, Artists): its own state once landed, else the header's.</summary>
    public static ProfileLoad Riding(ProfileLoad header, EdgeState state)
        => state is EdgeState.Complete or EdgeState.Partial ? ProfileLoad.Ready
         : header == ProfileLoad.Ready ? ProfileLoad.Loading : header;

    /// <summary>A door-asked list (Followers, Following): <see cref="EdgeTableBase.Readiness"/> + its failure.</summary>
    public static ProfileLoad List(EdgeState readiness, int failure) => readiness switch
    {
        EdgeState.Complete or EdgeState.Partial => ProfileLoad.Ready,
        EdgeState.Failed => failure == EdgeTableBase.NoRoute ? ProfileLoad.Unavailable : ProfileLoad.Failed,
        _ => ProfileLoad.Loading,
    };

    /// <summary>Your own "Top artists this month" (Home.Feeds.TopContentState + the relation's count).</summary>
    public static ProfileLoad TopArtists(HomeLoad state, int count)
        => count > 0 ? ProfileLoad.Ready
         : state switch { HomeLoad.Pending => ProfileLoad.Loading, HomeLoad.Failed => ProfileLoad.Failed,
                          HomeLoad.Ready => ProfileLoad.Ready, _ => ProfileLoad.Unavailable };
}
