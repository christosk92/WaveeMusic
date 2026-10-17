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
//
// ROW 2 (P5 Z5, contract 1.26). A band-style structure (Zune) draws the facet words as its band's row 2 instead of the page's facet row,
// so the host publishes them through the page-chrome channel: one `PageView` per word ("all" first), the target's word lit, Go running
// the same select path a facet-row click does (the word's Following memory included); an action that toggles the lit word's Following
// sub-chip when it has one; and the covers the shown facet leads with as the page's mix backdrop. It publishes from the selector-sync
// effect (`SyncFacetIndex`, which already re-runs on the words and the switch), under the route on screen when that is this tab's Home,
// else under the target's own route: Home's facet changes the route's ARGUMENT inside one mounted page, and the channel keys a
// publication by the argument, so the sink the content host built at mount names the first facet's place, not the current one. The
// row is rebuilt only when what it shows changed (an equal row is the same instance: the channel ignores it). Before the first model
// lands the row is "all" alone, so the band holds its two rows from the first frame Home publishes. Classic draws no row 2 and reads none of it.

using System.Diagnostics.CodeAnalysis;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;

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

    // ── row 2 (P5 Z5): the last row published and what it was built from ──
    readonly Dictionary<string, Action> _go = new(StringComparer.Ordinal);                 // one stable Go per word id
    readonly Dictionary<string, IReadOnlyList<string>> _mix = new(StringComparer.Ordinal);  // per facet: the covers it leads with
    PageChromeRow? _chromeRow;
    FacetWord[] _chromeWords = [];
    int _chromeLit = -1;
    bool _chromeFollowing;
    IReadOnlyList<string>? _chromeMix;
    Action? _toggleFollowing;

    /// <summary>The row-2 views' id of the synthetic "All" word (a node key, never the label).</summary>
    public const string AllViewId = "all";

    /// <summary>The id of the row-2 action that toggles the lit word's Following sub-chip.</summary>
    public const string FollowingActionId = "following";

    /// <summary>How many covers the shown facet hands the page's mix backdrop.</summary>
    public const int MixCovers = 6;

    /// <summary>The row 2 this host last published (null before the selector-sync effect first ran).</summary>
    public PageChromeRow? ChromeRow => _chromeRow;

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
        _mix[m.Facet] = MixOf(m, _mix.TryGetValue(m.Facet, out var before) ? before : null);
    }

    /// <summary>The SelectorBar index follows the switch machine; it reads its own signal too, so a click the machine refused
    /// (Unavailable / no-op) snaps the selection back on the next turn. The same run publishes row 2 (P5 Z5, see the header).</summary>
    public void SyncFacetIndex()
    {
        var words = Words.Value;
        var state = Switch.Value;
        var (idx, _) = FacetPivot.Resolve(words, state.Target);
        if (FacetIndex.Value != idx) FacetIndex.Value = idx;
        PublishChrome(words, state);
    }

    // ══ ROW 2 (P5 Z5) ════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Publish the facet words as row 2 (see the header): rebuilt only when the words, the lit word, its Following state or the
    /// shown facet's covers changed.</summary>
    void PublishChrome(FacetWord[] words, FacetSwitchState state)
    {
        if (words.Length == 0) words = FacetPivot.Words(null, Loc.Get(Strings.Home.FacetAll));
        var (lit, followingOn) = FacetPivot.Resolve(words, state.Target);
        var mix = _mix.TryGetValue(state.Published, out var covers) ? covers : null;
        if (_chromeRow is null || lit != _chromeLit || followingOn != _chromeFollowing || !ReferenceEquals(mix, _chromeMix)
            || !SameWords(words, _chromeWords))
        {
            _chromeRow = ChromeRowOf(words, lit, followingOn, mix);
            _chromeWords = words;
            _chromeLit = lit;
            _chromeFollowing = followingOn;
            _chromeMix = mix;
        }
        var route = Shell.Current.Peek();
        if (route.Kind != Shell.RouteKind.Home || route.Tab != Tab)
            route = new Shell.Route(Shell.RouteKind.Home, Arg: Entities.Strings.Intern(FacetRoute.ArgOf(state.Target)), Tab: Tab);
        Shell.PageChromeChannel.SinkFor(route).Publish(_chromeRow);
    }

    /// <summary>The row: a view per word (the lit one current), the Following toggle when the lit word has a sub-chip, the mix backdrop.</summary>
    PageChromeRow ChromeRowOf(FacetWord[] words, int lit, bool followingOn, IReadOnlyList<string>? mix)
    {
        var views = new PageView[words.Length];
        for (int i = 0; i < views.Length; i++)
            views[i] = new PageView(words[i].Id.Length == 0 ? AllViewId : words[i].Id, words[i].Label, i == lit, GoOf(words[i].Id));
        IReadOnlyList<PageAction> actions = (uint)lit < (uint)words.Length && words[lit] is { HasSub: true, SubId: not null }
            // Off: the word for the sub-chip ("Following"); on: "All", the way back to the whole word.
            ? [new PageAction(FollowingActionId, Loc.Get(followingOn ? Strings.Home.FacetAll : Strings.Home.Following),
                              _toggleFollowing ??= ToggleFollowing)]
            : [];
        return new PageChromeRow(views, actions, mix is { Count: > 0 } ? new PageBackdrop(Bleed: null, mix) : null);
    }

    /// <summary>The word's one Go (cached per id, so an equal row holds equal verbs): the select path a facet-row click takes, carrying the
    /// sub-chip when Following was last on for that word.</summary>
    Action GoOf(string id)
    {
        if (!_go.TryGetValue(id, out var go))
            _go[id] = go = () =>
            {
                foreach (var w in Words.Peek())
                    if (w.Id == id) { Select(FacetPivot.Target(w, _followingMemory.TryGetValue(id, out bool on) && on), Tab); return; }
                Select(id, Tab);   // "All" before the words landed, or a word the chips dropped: its own id
            };
        return go;
    }

    /// <summary>The Following action: the lit word's sub-chip on or off (the facet row's toggle).</summary>
    void ToggleFollowing()
    {
        var words = Words.Peek();
        var (lit, on) = FacetPivot.Resolve(words, Switch.Peek().Target);
        if ((uint)lit < (uint)words.Length && words[lit].HasSub) Select(FacetPivot.Target(words[lit], !on), Tab);
    }

    static bool SameWords(FacetWord[] a, FacetWord[] b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (!a[i].Equals(b[i])) return false;
        return true;
    }

    /// <summary>The first <see cref="MixCovers"/> covers of <paramref name="m"/>, zone by zone; <paramref name="before"/> again when they did not
    /// change (so an equal row stays the same instance).</summary>
    static IReadOnlyList<string> MixOf(ScreenModel m, IReadOnlyList<string>? before)
    {
        var covers = new List<string>(MixCovers);
        foreach (var zone in m.Zones)
        {
            foreach (var card in zone.Items)
            {
                if (card.ImageUrl is { Length: > 0 } url && !covers.Contains(url)) covers.Add(url);
                if (covers.Count == MixCovers) break;
            }
            if (covers.Count == MixCovers) break;
        }
        if (before is not null && before.Count == covers.Count)
        {
            bool same = true;
            for (int i = 0; i < covers.Count && same; i++) same = string.Equals(before[i], covers[i], StringComparison.Ordinal);
            if (same) return before;
        }
        return covers;
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
