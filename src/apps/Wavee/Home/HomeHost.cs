// ── Home/HomeHost.cs — HomeHost: the Home page's facet state and the effects that drive it (C16, X43) ──────────────────
//
// Role: CORE (engine-touching: signals, the entity tables, the shell route)
// Spec: docs/themes/plan/technical-plan.md §5.1 C16 (X43), §5.7 (props stability)
//
// WHAT MOVED. `HomeScreen` used to own, in its Render, the facet machine's state (the switch signal, the pivot words, the
// selector index, the "shown this session" set, the Following memory, the per-facet shimmer shape) AND the effects that
// run it: route arg -> facet, demand, the shown/words/shape record, the selector-index sync, the landing edge, the
// Following-chip prefetch, plus the select path (verdict + state machine + fetch + history push) and the retry. All of it
// is here now, as plain methods over those fields; `HomeScreen` keeps the hooks (a `UseEffect` is a component's) and
// calls one host method per effect, so the order and the dependency keys are exactly what they were.
//
// ONE HOST PER MOUNTED TAB. Every field is a stable object held for the host's whole life (the props-freeze rule: nothing
// is rebuilt per render); `Tab` is re-pushed each render, so the select delegates read the CURRENT tab instead of a
// render-time capture. The host never draws: the zone planner, the model memo, the verdict log and the shell wash stay in
// the screen (they are the view's read of the tables), and a theme's Home view receives the host's signals and row props.
//
// ONE DOOR FOR THE DAYLIST CLOCK. The clock MODE (Settings > Appearance > Daylist clock) is a typed appearance pref;
// `DaylistClock()` is the single read the daylist card's clock makes, so the card never reaches for the store.

using System.Diagnostics.CodeAnalysis;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;

namespace Wavee.HomeUi;

public sealed class HomeHost
{
    // ── the facet machine's state (per tab) ──
    /// <summary>The facet-switch machine; <c>Target</c> is the selected facet, <c>Published</c> the one on screen.</summary>
    public readonly Signal<FacetSwitchState> Switch = new(FacetSwitchState.Initial(""));
    /// <summary>The pivot's words, "All" first (<see cref="FacetPivot.Words"/>); empty until the first model lands.</summary>
    public readonly Signal<FacetWord[]> Words = new(Array.Empty<FacetWord>());
    /// <summary>The SelectorBar's own index (the bar writes it on click/roving; <see cref="SyncFacetIndex"/> re-syncs it).</summary>
    public readonly Signal<int> FacetIndex = new(0);

    readonly HashSet<string> _shown = new(StringComparer.Ordinal);                       // "revealed this session"
    readonly Dictionary<string, bool> _followingMemory = new(StringComparer.Ordinal);
    readonly Dictionary<string, ZoneKind[]> _lastShape = new(StringComparer.Ordinal);     // per-facet shimmer shape
    readonly ScrollHandle _scroll;
    FacetRowProps? _rowProps;                             // built once: its delegates read Tab, never a render-time capture

    /// <param name="scroll">The page's scroll handle: the landing edge reads its offset (a Refreshing swap only lands at the top).</param>
    public HomeHost(ScrollHandle scroll) => _scroll = scroll;

    /// <summary>This tab's route tab, re-pushed by the screen each render.</summary>
    public int Tab { get; set; }

    /// <summary>The facet row's props: the signals are this tab's own stable fields and the delegates read <see cref="Tab"/>,
    /// so ONE record serves the host's whole life (a fresh record per render would re-push on every parent render for nothing).</summary>
    public FacetRowProps RowProps => _rowProps ??= new FacetRowProps(Words, Switch, Index: () => FacetIndex,
        Select: target => Select(target, Tab),
        Dismiss: () => Switch.Value = FacetSwitch.Dismiss(Switch.Peek()),
        FollowingMemory: id => _followingMemory.TryGetValue(id, out bool on) && on)
    {
        // The facet row's Friend activity button (Classic Compact, contract 1.22): the shell's friends panel, as the title bar's button toggles it.
        ToggleFriends = static () => Shell.Ui.Toggle(Shell.RailMode.Friends),
    };

    /// <summary>The facet's last-known zone shape on this tab (F30), for the shimmer; false before it was first shown.</summary>
    public bool TryLastShape(string facet, [MaybeNullWhen(false)] out ZoneKind[] kinds) => _lastShape.TryGetValue(facet, out kinds);

    // ══ THE EFFECTS (each is one `UseEffect` body in HomeScreen.Render) ══════════════════════════════════════════════

    /// <summary>Route arg -> facet (deep link, Back/Forward, a palette "Home: ..." command, a tab restore). The screen keys it
    /// on the arg and the word count; it also reads <see cref="Words"/> here, so a cold-start deep link that named a facet
    /// before its words landed resolves once they do.</summary>
    public void ResolveRouteArg(StringId arg, int tab)
    {
        string resolved = FacetRoute.FacetOf(Entities.Strings.Resolve(arg), Words.Value);
        if (resolved != Switch.Peek().Target) SelectCore(resolved, pushHistory: false, tab);
    }

    /// <summary>§3.12's demand for the switch target (the Artist/Album/Show idiom: in an effect, never in the model memo);
    /// re-asks in a new scope.</summary>
    public void DemandTarget()
    {
        _ = Entities.ScopeEpoch.Value;
        Demand(Switch.Value.Target);
    }

    /// <summary>Record that <paramref name="m"/>'s facet was shown this session, publish its pivot words and remember its zone
    /// shape. Written by an effect on the Ready edge, never in the model memo itself.</summary>
    public void RecordShown(ScreenModel? m)
    {
        if (m is null) return;
        _shown.Add(m.Facet);
        Words.Value = FacetPivot.Words(m.Chips, Loc.Get(Strings.Home.FacetAll));
        var kinds = new ZoneKind[m.Zones.Count];
        for (int i = 0; i < kinds.Length; i++) kinds[i] = m.Zones[i].Kind;
        _lastShape[m.Facet] = kinds;
    }

    /// <summary>The SelectorBar index follows the switch machine; it reads its own signal too, so a click the machine refused
    /// (Unavailable / no-op) snaps the selection back on the next turn.</summary>
    public void SyncFacetIndex()
    {
        var (idx, _) = FacetPivot.Resolve(Words.Value, Switch.Value.Target);
        if (FacetIndex.Value != idx) FacetIndex.Value = idx;
    }

    /// <summary>The landing edge: Loading/Refreshing -> Idle once the target's document is known (F13: no timer). A flight that
    /// concluded with nothing while online fails the switch and reverts the route.</summary>
    public void Land(int tab)
    {
        var s = Switch.Value;
        if (s.Phase is not (FacetPhase.Loading or FacetPhase.Refreshing)) return;
        // The fetch concludes by clearing the row's in-flight stamp and bumping the Home table: neither is a
        // signal `LiveAttemptConcluded` reads, so subscribe to the table's change edge or the switch never lands.
        _ = Entities.ScopeEpoch.Value;
        _ = Home.Changed.Value;
        var h = Entities.HomeFeed(s.Target);
        if (!h.LiveAttemptConcluded) return;
        if (h.SectionCount == 0 && !h.Knows(HomeFields.Sections) && Spotify.Status.Value == Spotify.SessionPhase.Online)
        {
            Switch.Value = FacetSwitch.Fail(s, s.Target, out string revertTo);
            RevertRoute(revertTo, tab);
            return;
        }
        Switch.Value = FacetSwitch.Landed(s, s.Target, (float)_scroll.Offset.Value, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), out bool swapped);
        Log.Event(WaveeLogLevel.Info, "home", "facet.landed", "facet landed target=" + s.Target + " swap=" + swapped);
    }

    /// <summary>D-plan §6.3: the "From artists you follow" block on All needs the <c>music-following-chip</c> document.
    /// Prefetched once per All-entry edge; <c>Home.EnsureFeed</c> dedupes on Known/Asked, so re-entering All while it's still
    /// in flight (or already known) costs nothing extra.</summary>
    public static void EnsureFollowingChipFeed() => Home.EnsureFeed(Entities.HomeFeed("music-following-chip"));

    // ══ DEMAND AND RETRY ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>§3.12's D2 demand: the document first, then, once it's known, the baseline preview lookup.
    /// `Home.EnsureFeed`/`Home.EnsurePreviews` are both idempotent (the fetch planner dedupes on Known/Asked); in a
    /// `--fake` build, `Home.EnsureFeed` itself routes through `Entities.SimulateFacetFetch` (F15): this file never
    /// asks which build it is.</summary>
    public static void Demand(string facet)
    {
        var h = Entities.HomeFeed(facet);
        if (!h.Knows(HomeFields.Sections)) Home.EnsureFeed(h);
        else Home.EnsurePreviews(h);
    }

    /// <summary>The first load concluded with no document while online (the Vacancy's Retry): ask for the launch
    /// facet's document AGAIN whatever the planner sealed. <see cref="Entities.Refresh"/> un-asks first, where a plain
    /// <c>Home.EnsureFeed</c> would no-op on a row already marked Asked-and-unanswered (Browse.Page.cs's Retry rule).</summary>
    public void RetryFirstLoad()
    {
        var h = Entities.HomeFeed(Switch.Peek().Target);
        if (!h.IsValid) return;
        int slot = h.Slot;
        Entities.Refresh(Entities.Current.Homes, new ReadOnlySpan<int>(in slot), (uint)HomeFields.All, FetchPriority.Visible);
    }

    // ══ FACET SELECTION ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A click (or roving) on a facet word or its Following sub-chip: the one select path, with a history push.</summary>
    public void Select(string target, int tab) => SelectCore(target, pushHistory: true, tab);

    void SelectCore(string target, bool pushHistory, int tab)
    {
        var s = Switch.Peek();
        if (target == s.Target && s.Phase != FacetPhase.Failed) return;

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var h = Entities.HomeFeed(target);
        bool knowsSections = h.Knows(HomeFields.Sections);
        bool shownThisSession = _shown.Contains(target);
        bool online = Spotify.Status.Value == Spotify.SessionPhase.Online;
        var verdict = FacetCache.Decide(knowsSections, shownThisSession, h.FetchedAt, nowMs, online);

        var next = FacetSwitch.Select(s, target, verdict, nowMs);
        Log.Event(WaveeLogLevel.Info, "home", "facet.select",
            "facet select target=" + target + " from=" + s.Published + " verdict=" + verdict + " phase=" + next.Phase
            + " published=" + next.Published + " knows=" + knowsSections + " shown=" + shownThisSession);
        if (next.Equals(s)) return;   // Unavailable / no-op

        Switch.Value = next;
        RememberFollowing(target);

        if (pushHistory)
            Shell.GoTo(new Shell.Route(Shell.RouteKind.Home, Arg: Entities.Strings.Intern(FacetRoute.ArgOf(target)), Tab: tab));

        switch (verdict)
        {
            case FacetCacheVerdict.Missing:
                Home.EnsureFeed(h);
                break;
            case FacetCacheVerdict.Stale:
                int slot = h.Slot;
                Entities.Refresh(Entities.Current.Homes, new ReadOnlySpan<int>(in slot), (uint)HomeFields.All, FetchPriority.Visible);
                break;
        }
    }

    static void RevertRoute(string revertTo, int tab)
        => Shell.GoTo(new Shell.Route(Shell.RouteKind.Home, Arg: Entities.Strings.Intern(FacetRoute.ArgOf(revertTo)), Tab: tab));

    void RememberFollowing(string target)
    {
        var words = Words.Peek();
        foreach (var w in words)
        {
            if (!w.HasSub) continue;
            if (w.SubId == target) { _followingMemory[w.Id] = true; return; }
            if (w.Id == target) { _followingMemory[w.Id] = false; return; }
        }
    }

    /// <summary>Prefetch a facet's document on intent when its freshness rules say so.</summary>
    public static void Prefetch(string target)
    {
        var h = Entities.HomeFeed(target);
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool everFetched = h.FetchedAt != 0;
        bool online = Spotify.Status.Value == Spotify.SessionPhase.Online;
        if (FacetCache.ShouldPrefetch(everFetched, h.FetchedAt, nowMs, online, isSubChip: false))
            Home.EnsureFeed(h);
    }

    // ══ THE DAYLIST CLOCK PREF ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The daylist clock's mode (Countdown and update time, Update time only, Countdown only): a SUBSCRIBING read of
    /// the appearance pref (it joins <c>Prefs.Appearance.Epoch</c>, so a Settings pick swaps the Counting face on the same
    /// frame). Clamped by the pref: a hand-edited or downgraded value reads as <see cref="DaylistClockMode.Both"/>.</summary>
    public static DaylistClockMode DaylistClock() => Prefs.Appearance.DaylistClock();
}
