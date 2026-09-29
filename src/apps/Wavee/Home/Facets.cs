// ── Home/Facets.cs ─────────────────────────────────────────────────────────────────────────────────────────────────
// The content facet pivot (All / Music / Podcasts / Audiobooks + Following), the facet-switch state machine and the
// per-facet cache freshness/prefetch rules, plus the deep-link/history route mapping for the facet arg. Every type
// here is engine-free and pure: no signals, no tables, no I/O. `Home/Screen.UI.cs` is the only caller that touches
// the engine — the zone-enter stagger is the engine's own `Stagger`/`Enter` on the facet content root and the
// compact-band hysteresis is `UseScrollThreshold(64, 56)` at the call site (remediation F13, F35): neither is
// duplicated here any more.
//
// Role: CORE
// Owner: A3
// Wave: 1 (cut in Wave 2G per docs/plans/wavee/home-redesign-remediation.md §3.9, §4)
// Spec: docs/plans/wavee/home-redesign-implementation.md (Workstream H) + docs/plans/wavee/home-redesign/06-facet-design.md §2

using System;
using System.Collections.Generic;

namespace Wavee.HomeUi;

/// <summary>One word in the facet pivot row (rest state) / segmented control (compact band). "All" is the app's own
/// synthetic entry (<see cref="Id"/> == "") — it is not a server chip and never carries a sub-chip.</summary>
public readonly record struct FacetWord(string Id, string Label, bool HasSub, string? SubId, string? SubLabel);

/// <summary>Builds the pivot's word list from the server's <c>homeChips</c> and resolves selection/target ids
/// against it. Pure projection of <c>ChipInput</c> (owned by <c>Home/Model.cs</c>) — see 06 §2.1, §2.3.</summary>
public static class FacetPivot
{
    /// <summary>"All" first (synthetic, <see cref="FacetWord.Id"/> == ""), then the server chips in server order.
    /// A null/empty chip list still yields the single "All" word (offline/never-fetched launch).</summary>
    public static FacetWord[] Words(IReadOnlyList<ChipInput>? chips, string allLabel)
    {
        int n = chips?.Count ?? 0;
        var words = new FacetWord[n + 1];
        words[0] = new FacetWord("", allLabel, HasSub: false, SubId: null, SubLabel: null);
        for (int i = 0; i < n; i++)
        {
            ChipInput c = chips![i];
            words[i + 1] = new FacetWord(c.Id, c.Label, HasSub: c.SubId is not null, c.SubId, c.SubLabel);
        }
        return words;
    }

    /// <summary>Resolves the currently-selected facet id (which may be a sub-chip id, e.g. the Following facet)
    /// back to its parent word's index and whether the Following toggle is on. Falls back to word 0 (All) when
    /// <paramref name="selected"/> matches nothing — the same "step back out" guarantee `06` §4.1 asks the row to
    /// keep even when the chip list changed under it.</summary>
    public static (int Word, bool FollowingOn) Resolve(FacetWord[] words, string selected)
    {
        for (int i = 0; i < words.Length; i++)
        {
            if (words[i].Id == selected)
                return (i, false);
            if (words[i].HasSub && words[i].SubId == selected)
                return (i, true);
        }
        return (0, false);
    }

    /// <summary>The facet id to request/publish for a word given the Following toggle's state: the sub-chip id
    /// when on (and the word has one), else the word's own id. Turning Following on for a word with no sub-chip
    /// is a caller bug (§2.3: the toggle only exists when <c>HasSub</c>) — this stays defined and returns the
    /// word's plain id rather than throwing, since it is pure projection, not validation.</summary>
    public static string Target(FacetWord w, bool followingOn) =>
        followingOn && w.HasSub && w.SubId is not null ? w.SubId : w.Id;
}

/// <summary>Lifecycle of the facet content region during a switch. See <see cref="FacetSwitch"/> for the
/// transition table.</summary>
public enum FacetPhase : byte
{
    /// <summary>Settled: <see cref="FacetSwitchState.Published"/> is showing at full opacity, no bar.</summary>
    Idle,

    /// <summary>Target has no cached document (Missing). Old content dimmed .6, bar showing, not interactive.</summary>
    Loading,

    /// <summary>Target's cached document is stale (&gt; TTL). Cached content is already showing at full opacity;
    /// a background refetch is in flight with the bar showing and no dim.</summary>
    Refreshing,

    /// <summary>The flight concluded with no sections while online. <see cref="FacetSwitchState.FailedTarget"/>
    /// names what failed; <see cref="FacetSwitchState.Published"/> has already been reverted to.</summary>
    Failed,
}

/// <summary>Snapshot of the facet switch machine. One instance lives on the Home tab (not process-global — two
/// Home tabs can be mid-switch independently, 06 §2.7).</summary>
public readonly record struct FacetSwitchState(
    FacetPhase Phase,
    string Published,
    string Target,
    long StartedAtMs,
    string? FailedTarget)
{
    /// <summary>True while the old content is fully dimmed (Missing → Loading only; Stale/Fresh switches never dim,
    /// 06 §2.6).</summary>
    public bool Dim => Phase == FacetPhase.Loading;

    /// <summary>True whenever the indeterminate ProgressBar should show under the pivot/band.</summary>
    public bool Bar => Phase is FacetPhase.Loading or FacetPhase.Refreshing;

    /// <summary>The rest state for a freshly-mounted Home tab, published facet = <paramref name="facet"/> (its
    /// launch facet, "" for All).</summary>
    public static FacetSwitchState Initial(string facet) =>
        new(FacetPhase.Idle, facet, facet, StartedAtMs: 0, FailedTarget: null);
}

/// <summary>Per-facet document freshness verdict, decided against the Homes table clock (`06` §2.8, §4.1).</summary>
public enum FacetCacheVerdict : byte
{
    /// <summary>Shown this session and &lt; TTL old: instant swap, no bar, no dim.</summary>
    Fresh,

    /// <summary>Shown this session but &gt;= TTL old: instant swap of the stale copy + a silent background refresh
    /// (bar, no dim).</summary>
    Stale,

    /// <summary>Never fetched this session (or never fetched at all): Loading (dim + bar) until it lands.</summary>
    Missing,

    /// <summary>Offline and not cached: the word is disabled and selecting it is a no-op.</summary>
    Unavailable,
}

/// <summary>The facet-switch state machine. Every entry point is a pure `(state, ...) -> state` transform; the
/// caller (`Home/Screen.UI.cs`) is the only place that turns a resulting <see cref="FacetSwitchState"/> into
/// fetch calls and rendered opacity/bar/InfoBar — the old-content-fades/new-content-mounts swap itself is the
/// engine's keyed <c>Exit</c>/<c>Enter</c> reconcile on the facet content root (remediation F13), not a phase or a
/// timer here. Transition table (06 §2.6, §2.8, Appendix A, as revised by the remediation):
///
///   Idle            --Select(Fresh)-->      Idle, Published = Target (instant: the caller keys the content root
///                                            on the target facet, so the engine's own Exit/Enter fades the swap)
///   Idle            --Select(Stale)-->      Refreshing, Published = Target (the stale copy is shown at once, same
///                                            as Fresh; a background refetch is kicked with the bar, no dim)
///   Idle            --Select(Missing)-->    Loading, Published unchanged (dim .6 + bar)
///   Idle            --Select(Unavailable)-->Idle (no-op: word is disabled, caller shouldn't even call this, but
///                                            the transform is defensive)
///   *               --Select(current Target)--> unchanged (no-op: re-selecting what's already the switch target,
///                                            including the settled Published facet, does nothing)
///   Loading         --Landed-->             Idle, Published = Target, swap=true
///   Refreshing      --Landed-->             swap=true -> Idle (Published already = Target), when scrollOffset
///                                            &lt;= 0.5 AND (nowMs - StartedAtMs) &lt; BackgroundSwapWindowMs;
///                                            otherwise swap=false -> Idle, Published unchanged (the fresh copy is
///                                            kept for the next visit, not shown now). Scrolled-away is the only
///                                            "the user moved on" signal: the page has no interactive ancestor to
///                                            observe a click on, and adding one made the whole column a hover scope
///                                            (seventh pass — F35's Interacted mark is deleted)
///   Loading         --Fail-->               Failed, revertTo = Published (the previously-published facet); the
///                                            caller reverts pivot selection + history arg to revertTo
///   Refreshing      --Fail-->               Failed is not reached: a background refresh failing silently keeps
///                                            the stale copy shown (Refreshing -> Idle via Landed's non-swap path,
///                                            modelled by the caller simply not calling Fail for a background
///                                            flight; Fail is defined for every phase for symmetry and safety)
///   Failed          --Dismiss-->            Idle (Published unchanged; the InfoBar closes/expires)
/// </summary>
public static class FacetSwitch
{
    /// <summary>User (or history/deep-link) selects <paramref name="target"/>. Re-selecting the facet that is
    /// already the active target (mid-switch or settled) is a no-op — including re-clicking the currently
    /// published, idle facet. <paramref name="v"/> is the cache verdict for <paramref name="target"/> at the
    /// moment of selection.</summary>
    public static FacetSwitchState Select(in FacetSwitchState s, string target, FacetCacheVerdict v, long nowMs)
    {
        if (target == s.Target && s.Phase != FacetPhase.Failed)
            return s;

        switch (v)
        {
            case FacetCacheVerdict.Unavailable:
                return s;

            case FacetCacheVerdict.Fresh:
                // Instant: the target is already published; the caller keys the facet content root on it, and the
                // engine's own Exit/Enter reconcile plays the swap. No fetch in flight, no timer here.
                return new FacetSwitchState(FacetPhase.Idle, target, target, nowMs, FailedTarget: null);

            case FacetCacheVerdict.Stale:
                // Same instant publish as Fresh, but the phase is Refreshing: the caller also kicks a background
                // refetch, with the bar showing and no dim, while the stale copy is already on screen.
                return new FacetSwitchState(FacetPhase.Refreshing, target, target, nowMs, FailedTarget: null);

            case FacetCacheVerdict.Missing:
                return new FacetSwitchState(FacetPhase.Loading, s.Published, target, nowMs, FailedTarget: null);

            default:
                return s;
        }
    }

    /// <summary>The flight for <paramref name="facet"/> has landed (sections known). <paramref name="swap"/>
    /// reports whether the caller should actually replace the shown content:
    /// <list type="bullet">
    /// <item>Loading -&gt; always swap (there was nothing else to show).</item>
    /// <item>Refreshing -&gt; swap only if the user is still at the top of the target's scroll region and within the
    /// 2 s background window.</item>
    /// <item>Idle/Failed, or a facet other than the current <see cref="FacetSwitchState.Target"/> -&gt; stale
    /// notification, ignored (swap=false, state unchanged): a slow response for a facet the user has since
    /// navigated away from.</item>
    /// </list></summary>
    public static FacetSwitchState Landed(in FacetSwitchState s, string facet, float scrollOffset, long nowMs, out bool swap)
    {
        if (facet != s.Target || s.Phase is FacetPhase.Idle or FacetPhase.Failed)
        {
            swap = false;
            return s;
        }

        if (s.Phase == FacetPhase.Loading)
        {
            swap = true;
            return new FacetSwitchState(FacetPhase.Idle, s.Target, s.Target, s.StartedAtMs, FailedTarget: null);
        }

        // Refreshing: Published already equals Target (Select(Stale) published it up front); the only question is
        // whether the caller keeps showing it (swap=true, just settle to Idle) or leaves it for the next visit.
        bool atTop = scrollOffset <= 0.5f;
        bool withinWindow = nowMs - s.StartedAtMs < FacetCache.BackgroundSwapWindowMs;
        if (atTop && withinWindow)
        {
            swap = true;
            return new FacetSwitchState(FacetPhase.Idle, s.Published, s.Target, s.StartedAtMs, FailedTarget: null);
        }

        // Fresh copy is kept in the table for the next visit; what's on screen (the stale copy already published
        // by Select(Stale)) is left alone.
        swap = false;
        return new FacetSwitchState(FacetPhase.Idle, s.Published, s.Published, s.StartedAtMs, FailedTarget: null);
    }

    /// <summary>The flight for <paramref name="facet"/> concluded with no sections while online (`06` §2.6, §4.2).
    /// Only meaningful from <see cref="FacetPhase.Loading"/> (a Missing switch failing outright); a background
    /// Refreshing failure is handled by the caller simply not calling this and leaving the stale copy shown, so a
    /// call for a facet that isn't the current Loading target is ignored. <paramref name="revertTo"/> is the facet
    /// the caller should re-select in the pivot and in history.</summary>
    public static FacetSwitchState Fail(in FacetSwitchState s, string facet, out string revertTo)
    {
        if (facet != s.Target || s.Phase != FacetPhase.Loading)
        {
            revertTo = s.Published;
            return s;
        }

        revertTo = s.Published;
        return new FacetSwitchState(FacetPhase.Failed, s.Published, s.Published, s.StartedAtMs, FailedTarget: facet);
    }

    /// <summary>Closes the failure InfoBar. Only meaningful from Failed; otherwise a no-op.</summary>
    public static FacetSwitchState Dismiss(in FacetSwitchState s) =>
        s.Phase == FacetPhase.Failed
            ? new FacetSwitchState(FacetPhase.Idle, s.Published, s.Published, s.StartedAtMs, FailedTarget: null)
            : s;
}

/// <summary>Per-facet document freshness and prefetch-on-intent rules (`06` §2.8, §4.1). Stateless: every method
/// takes the caller's read of the Homes table (`Home.FetchedAt`, `Home.Knows(Sections)`) plus the clock.</summary>
public static class FacetCache
{
    /// <summary>A shown-this-session document is Fresh under this age; past it, Stale (`06` §2.8 "freshness 10 min").</summary>
    public const long TtlMs = 600_000;

    /// <summary>A Refreshing background swap only lands if it completes within this long of the switch starting
    /// (`06` §2.6 "&lt; 2 s since the switch").</summary>
    public const long BackgroundSwapWindowMs = 2_000;

    /// <summary>Hover/focus dwell before a facet is prefetched (`06` §2.8).</summary>
    public const long PrefetchHoverMs = 150;

    /// <summary>Decides the verdict for a facet given what the caller knows about its document.
    /// <paramref name="knowsSections"/>: the Homes row has a decoded section list (any version, even a stale one).
    /// <paramref name="shownThisSession"/>: the document has been revealed at least once this session (a cold
    /// snapshot warmed from disk but never shown does not count as "shown", `06` §2.8 "Cached = revealed this
    /// session"). <paramref name="fetchedAtUnixS"/>: the row's <c>FetchedAt</c> (unix seconds; 0/unset = never).
    /// <paramref name="nowUnixMs"/>: the caller's clock in unix milliseconds. <paramref name="online"/>: network
    /// reachability.</summary>
    public static FacetCacheVerdict Decide(bool knowsSections, bool shownThisSession, int fetchedAtUnixS, long nowUnixMs, bool online)
    {
        if (!knowsSections || !shownThisSession)
            return online ? FacetCacheVerdict.Missing : FacetCacheVerdict.Unavailable;

        long ageMs = nowUnixMs - fetchedAtUnixS * 1000L;
        if (ageMs < TtlMs)
            return FacetCacheVerdict.Fresh;

        return online ? FacetCacheVerdict.Stale : FacetCacheVerdict.Fresh; // offline: an old cached copy still swaps instantly, no refresh attempted
    }

    /// <summary>Whether a 150 ms hover/focus dwell on <paramref name="isSubChip"/>'s word should trigger a
    /// prefetch fetch. Never for a sub-chip (Following is never prefetched, `06` §2.8), never offline, and never
    /// for a facet already fetched within the TTL window (at most once per facet per 10 min).</summary>
    public static bool ShouldPrefetch(bool everFetched, int fetchedAtUnixS, long nowUnixMs, bool online, bool isSubChip)
    {
        if (isSubChip || !online)
            return false;
        if (!everFetched)
            return true;
        long ageMs = nowUnixMs - fetchedAtUnixS * 1000L;
        return ageMs >= TtlMs;
    }
}

/// <summary>Maps the facet id to/from the Home route's deep-link/history <c>arg</c> (`06` §2.7,
/// `.claude/skills/wavee/deep-linking.md`: <c>wavee://open?route=home&amp;arg=&lt;chip-id&gt;</c>).</summary>
public static class FacetRoute
{
    /// <summary>The history/deep-link arg for <paramref name="facet"/>. All ("") has no arg — an empty string, so
    /// the route reads as plain `home` with nothing appended, matching the existing convention that `arg` is
    /// unused for pages.</summary>
    public static string ArgOf(string facet) => facet;

    /// <summary>Resolves a route arg back to a known facet id. A missing/empty/unknown arg — including one that
    /// doesn't match any current word (a stale deep link, a chip the server stopped sending) — falls back to All
    /// (""), per `06` §2.7 "Missing/unknown arg → All".</summary>
    public static string FacetOf(string? arg, FacetWord[] words)
    {
        if (string.IsNullOrEmpty(arg))
            return "";

        foreach (FacetWord w in words)
        {
            if (w.Id == arg)
                return w.Id;
            if (w.HasSub && w.SubId == arg)
                return w.SubId!;
        }
        return "";
    }
}
