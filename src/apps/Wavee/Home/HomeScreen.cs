// ── Home/HomeScreen.cs — HomeScreen: the Home page's integration point (home rebuild, fifth pass — stock controls) ──
//
// Role: UI
// Spec: docs/plans/wavee/home-rebuild-implementation.md "Fifth pass — stock controls" (Page, Sticky headers),
//       "Seventh pass" (the facet words are the page title; no greeting headline; no pointer handler on the dim host).
//
// The DATA half is carried over from the previous passes unchanged (Props, PageFor, the per-tab fields, the route-arg
// effect, Demand, Compute, Verdict, LocalizedTitles/Subtitles, SelectFacet*, RevertRoute, RememberFollowing, Prefetch,
// Seed, BuildWash, the facet/verdict/content logs) — see git history for why each is shaped the way it is. The RENDER
// half is rebuilt from the app's own page idiom (Artist.Page.cs ~389, Browse.Page.cs ~150): ONE plain `ScrollView`
// column, no `ItemsView` of zones, no lanes/extents/RowAt/Tail, no width query, no parallax, no overlay InfoBar.
//
//   HomeScreen
//   └─ ZStack [ Palette.ShellTint,
//      ScrollView (Key/ScrollKey per PUBLISHED facet, Handle = _scroll)
//        └─ centred column  Gap=XL(20)  MaxWidth=PageMaxW  Padding=(PageWide, XXL(24), PageWide, Dock.Reserve+32)
//           │                ScrollScope=Facet.PageScope
//           ├─ FacetRow (Facet.UI.cs: SelectorBar in Design.FacetTitleStyle — the facet words ARE the page title —
//           │     · Following ToggleButton · busy ProgressBar · failure InfoBar)
//           │     its band is `.Sticky(Facet.StuckInset, scope: Facet.PageScope)` — transparent, no acrylic
//           └─ content  `.StickyClip(Facet.StuckBottom)` + EdgeFade(Top, 24) { WhileStuck }   (the Browse masthead idiom)
//      (the ScrollView has EdgeCues None: its viewport top feather would fade the pinned rows)
//                └─ SkelRegionEl(Content: keyed facet column → dim host (Opacity/HitTest binds only) → Zones.Column(model),
//                                ShimmerSource: Zones.Column(Seed), OnFailed: Controls.Vacancy(Error, retry), Reveal None) ]
//
// NO GREETING HEADLINE (owner, seventh pass): the greeting lives only in the daylist card's eyebrow (Daylist.UI.cs).
// The page column's own gap is the 20-px facet-row → content step; the 40-px rhythm between zones is Zones.Column's.
//
// STICKY WITHOUT A BACKGROUND: the facet band paints nothing (live Mica); the content column under it cuts ITSELF at
// the band's lower edge (`StickyClip(Facet.StuckBottom)`) with a top feather that arms only while the clip is engaged
// (`EdgeFadeSpec.WhileStuck`), so content dissolves into the band, is never guillotined, and never softens at rest.
// Unlike Browse the band is IN the scroll flow (an in-flow `.Sticky(Facet.StuckInset)` row, 12 below the viewport top),
// so no spacer is needed above the clipped node: the clip engages exactly when the content's top reaches the pinned
// band's lower edge. Chapter headers inside `Zones.Column` pin at `Facet.StuckBottom` under this band (Zones.UI.cs).
//
// ── FACET HISTORY + DEEP LINK (owner decision, v1) ──
//   A facet switch calls `Shell.GoTo(new Shell.Route(Shell.RouteKind.Home, arg: FacetRoute.ArgOf(facet), Tab: tab))`.
//   Home's route row is `KeyedByArg = false` (a facet switch reuses the SAME mounted `HomeScreen`) but `PlaceByArg =
//   true` (`Shell.cs` §3.10) — History/Back/Forward and the tab's stored route all see a facet switch as a new PLACE.
//   `PageFor` re-pushes fresh `Props(route.Tab, route.Arg)` on every commit; an effect keyed on `Props.Arg` resolves it.
//
// ── THE SELECTOR INDEX (no render-time writes) ──
//   The stock `SelectorBar` OWNS `_facetIndex` (writes it on click/roving, then fires OnChange → SelectFacet). An
//   effect re-syncs it from `FacetPivot.Resolve(words, switch.Target)` — so a Missing→Failed revert, an Unavailable
//   no-op, a deep link or a Following toggle all move the selection without a write during Render.

using System;
using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll.Effects;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using Wavee;
using static FluentGpu.Dsl.Ui;

namespace Wavee.HomeUi;

public sealed class HomeScreen : Component
{
    /// <summary>F23/W0: the facet arg is a RE-PUSHED PROP, not a read off a Home-only Shell static — the Wave-0
    /// `gate.keepalive.same-key-view` spike found `Flow.KeepAlive` re-runs `view` (fresh props, same instance) on a
    /// same-key token change, so `PageFor` below simply hands the route's own `Arg` through every time.</summary>
    public sealed record Props(int Tab, StringId Arg);

    public static Element PageFor(in Shell.Route route)
        => Embed.Comp(new Props(route.Tab, route.Arg), static () => new HomeScreen())
            with { Key = "home:" + route.Tab.ToString(CultureInfo.InvariantCulture) };

    // ── per-tab state (props-freeze rule: every field below is a stable object held for the component's whole
    //    lifetime, never rebuilt per render) ──
    readonly Signal<FacetSwitchState> _switch = new(FacetSwitchState.Initial(""));
    readonly Signal<FacetWord[]> _words = new(Array.Empty<FacetWord>());
    readonly Signal<int> _facetIndex = new(0);                                            // the SelectorBar's own index
    readonly HashSet<string> _shown = new(StringComparer.Ordinal);                       // "revealed this session"
    readonly Dictionary<string, bool> _followingMemory = new(StringComparer.Ordinal);
    readonly Dictionary<string, ZoneKind[]> _lastShape = new(StringComparer.Ordinal);     // per-facet shimmer shape
    readonly ScrollHandle _scroll = new();
    readonly object _tintOwner = new();
    FacetRowProps? _rowProps;                             // built once: its delegates read _tab, never a render-time capture
    int _tab;
    LoadState? _lastVerdict;                              // edge-gated facet.verdict log
    string? _contentFacet;                                // the facet whose content root is mounted (facet.content log)

    public override Element Render()
    {
        var p = UseProps<Props>();
        int tab = p.Tab;
        _tab = tab;
        var layout = UseRequiredContext(HomeLayout.Slot);
        string scope = UseContext(Shell.PageScrollScope);
        var overlay = UseContext(Overlay.Service);

        // ── route arg → facet (deep link, Back/Forward, a palette "Home: …" command, a tab restore). Re-runs
        // whenever the route's own Arg changes (a DepKey, not auto-tracking — Props are not a signal) OR the chip
        // strip's word count changes (a cold-start deep link named a facet before its words landed).
        UseEffect(() =>
        {
            string resolved = FacetRoute.FacetOf(Entities.Strings.Resolve(p.Arg), _words.Value);
            if (resolved != _switch.Peek().Target) SelectFacetCore(resolved, pushHistory: false, tab);
        }, DepKey.From(p.Arg.Value, _words.Peek().Length));

        // ── demand (§3.12: the Artist/Album/Show idiom — Demand() in an effect, never in the model memo) ──
        UseEffect(() => { _ = Entities.ScopeEpoch.Value; Demand(_switch.Value.Target); });   // re-ask in a new scope

        // ── the screen model: DERIVED over the table signals. No publish, no stamps, no writes. ──
        var facetSig = UseComputed(() => _switch.Value.Published);
        var model = UseComputed(() => Compute(facetSig.Value, layout.Doc.Value));
        var verdict = UseComputed(() => Verdict(facetSig.Value, model.Value));

        // ── "shown this session" + words + the shimmer shape memo: written by an effect on the Ready edge, never
        // in the model memo itself. ──
        UseEffect(() =>
        {
            var m = model.Value;
            if (m is null) return;
            _shown.Add(m.Facet);
            _words.Value = FacetPivot.Words(m.Chips, Loc.Get(Strings.Home.FacetAll));
            var kinds = new ZoneKind[m.Zones.Count];
            for (int i = 0; i < kinds.Length; i++) kinds[i] = m.Zones[i].Kind;
            _lastShape[m.Facet] = kinds;
        });

        // ── the SelectorBar index follows the switch machine (see the header): reads its own signal too, so a click
        // the machine refused (Unavailable / no-op) snaps the selection back on the next turn. ──
        UseEffect(() =>
        {
            var (idx, _) = FacetPivot.Resolve(_words.Value, _switch.Value.Target);
            if (_facetIndex.Value != idx) _facetIndex.Value = idx;
        });

        // ── the landing edge: Loading/Refreshing → Idle once the target's document is known (F13: no timer). ──
        UseEffect(() =>
        {
            var s = _switch.Value;
            if (s.Phase is not (FacetPhase.Loading or FacetPhase.Refreshing)) return;
            // The fetch concludes by clearing the row's in-flight stamp and bumping the Home table — neither is a
            // signal `LiveAttemptConcluded` reads, so subscribe to the table's change edge or the switch never lands.
            _ = Entities.ScopeEpoch.Value;
            _ = Home.Changed.Value;
            var h = Entities.HomeFeed(s.Target);
            if (!h.LiveAttemptConcluded) return;
            if (h.SectionCount == 0 && !h.Knows(HomeFields.Sections) && Spotify.Status.Value == Spotify.SessionPhase.Online)
            {
                _switch.Value = FacetSwitch.Fail(s, s.Target, out string revertTo);
                RevertRoute(revertTo, tab);
                return;
            }
            _switch.Value = FacetSwitch.Landed(s, s.Target, (float)_scroll.Offset.Value, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), out bool swapped);
            Log.Event(WaveeLogLevel.Info, "home", "facet.landed", "facet landed target=" + s.Target + " swap=" + swapped);
        });

        // ── D-plan §6.3: the "From artists you follow" block on All needs the `music-following-chip` document.
        // Prefetched once per All-entry edge; `Home.EnsureFeed` dedupes on Known/Asked, so re-entering All while it's
        // still in flight (or already known) costs nothing extra. ──
        UseEffect(() =>
        {
            if (facetSig.Value.Length == 0) Home.EnsureFeed(Entities.HomeFeed("music-following-chip"));
        }, DepKey.From(facetSig.Value.Length == 0));

        // The row's props: the signals are this tab's own stable fields and the delegates read `_tab`, so ONE record
        // serves the component's whole life (a fresh record per render would re-push on every parent render for nothing).
        _rowProps ??= new FacetRowProps(_words, _switch, _facetIndex,
            Select: target => SelectFacet(target, _tab),
            Dismiss: () => _switch.Value = FacetSwitch.Dismiss(_switch.Peek()),
            FollowingMemory: id => _followingMemory.TryGetValue(id, out bool on) && on);

        // smoothResize false: a page-level region whose branches differ by hundreds of DIP lands its height at once.
        // Reveal None: the zones own their entrance (Design.Entrance rows in Zones.Column); the shimmer is the SAME tree
        // rendered against the seed, so the skeleton is derived from the real page, never authored by hand.
        Element region = new SkelRegionEl(
            Pending: () => verdict.Value == LoadState.Pending,
            Failed: () => verdict.Value == LoadState.Failed,
            Content: () => FacetContent(model.Value ?? Seed(facetSig.Value, layout.Doc.Value), overlay),
            ShimmerSource: () => Zones.Column(Seed(facetSig.Value, layout.Doc.Value), facetSig.Value, overlay),
            OnFailed: () => Controls.Vacancy(Controls.VacancyVoice.Error, onAction: RetryFirstLoad),
            Reveal: SkelReveal.None, Style: SkeletonStyle.Default, Group: this, SmoothResize: false);

        // THE CLIP IS THE CONTRACT: the band paints nothing, so the content cuts itself at the band's lower edge and
        // feathers that cut exactly while the clip is engaged (Browse.Page.cs's directory / Artist.Page.cs's magazine).
        Element content = new BoxEl
        {
            Key = "home:content", Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
            EdgeFade = new EdgeFadeSpec(EdgeMask.Top, Detail.VerticalLayout.StickyFadeBand) { WhileStuck = true },
            Children = [region],
        }.StickyClip(Facet.StuckBottom);

        // The centred page column (Artist.Page.cs's Magazine): Grow toward the free width, capped at PageMaxW, the wide
        // gutter, the dock's reserve under the last section. It NAMES the sticky scope the facet band pins against. The
        // facet row is the page title, so the column opens 24 below the top and steps 20 to the content (the prototype's
        // facet-row → first zone step); the zones space themselves at SectionGapWide inside Zones.Column.
        string facet = facetSig.Value;
        Element column = new BoxEl
        {
            Key = "home:column", Direction = 1, Gap = Spacing.XL,
            Grow = 1f, Shrink = 1f, MinWidth = 0f, Basis = 0f, MaxWidth = Design.Size.PageMaxW,
            AlignItems = FlexAlign.Stretch, ScrollScope = Facet.PageScope,
            Padding = new Edges4(Spacing.PageWide, Spacing.XXL, Spacing.PageWide, Design.Dock.Reserve + Spacing.XXXL),
            Children =
            [
                Embed.Comp(_rowProps, static () => new FacetRow()) with { Key = "home:facet-row" },
                content,
            ],
        };

        // Keyed per PUBLISHED facet (Artist.Page.cs:389 idiom): each facet keeps its own scroll offset under its own
        // ScrollKey, and the handle feeds the Refreshing swap's at-top test (FacetSwitch.Landed).
        Element scroll = ScrollView(new BoxEl { Direction = 0, Justify = FlexJustify.Center, MinWidth = 0f, Children = [column] })
            with
            {
                Key = "home:scroll:" + facet, Grow = 1f, MinWidth = 0f,
                ScrollKey = scope + "home:" + facet,
                Handle = _scroll,
                // No viewport edge cue: its 40-DIP top feather would fade the pinned facet band and chapter headers.
                // The page dissolves its own content under them (StickyClip + WhileStuck), as Artist.Page does.
                EdgeCues = ScrollEdgeCues.None,
            };

        return new BoxEl
        {
            Grow = 1f, Direction = 1, ZStack = true,
            Children = [BuildWash(() => model.Value), scroll],
        };
    }

    // ══ THE SCREEN MODEL (derived, never published by hand) ═══════════════════════════════════════════════════════

    ScreenModel? Compute(string facet, LayoutDoc layoutDoc)
    {
        // A sign-in re-boots the entity scope: every table (and its Changed signal) is replaced, so subscribe to the
        // scope epoch first or the model stays bound to the dead scope's signals and never sees the feed land.
        _ = Entities.ScopeEpoch.Value;
        var h = Entities.HomeFeed(facet);
        _ = Home.Changed.Value;
        _ = Entities.Current.Edges.SectionCards.Changed.Value;
        _ = Entities.Current.Edges.SectionPreviewTracks.Changed.Value;
        _ = h.Version; _ = h.SectionVersion;
        if (!h.Knows(HomeFields.Sections)) return null;

        var inputs = SectionReader.Read(h);
        var titles = LocalizedTitles();
        // §6.3: All folds the music-following-chip document's releases into "From artists you follow" once known.
        IReadOnlyList<SectionInput>? following = null;
        if (facet.Length == 0)
        {
            var fh = Entities.HomeFeed("music-following-chip");
            _ = fh.Version; _ = fh.SectionVersion;
            if (fh.Knows(HomeFields.Sections)) following = SectionReader.Read(fh);
        }
        IReadOnlyList<Zone> zones = IsPodcastsFacet(facet)
            ? PodcastPlanner.Plan(inputs, titles, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            : ZonePlanner.Plan(inputs, facet, titles, facet.Length == 0 ? kind => LayoutZoneOf(kind) is { } lz && layoutDoc.IsHidden(lz) : null,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), following, LocalizedSubtitles());
        if (zones.Count == 0) zones = [new Zone(ZoneKind.EmptyFacet, "home:empty:" + facet, null, null, Array.Empty<HomeCard>())];
        return new ScreenModel(facet, zones, SectionReader.Greeting(h), SectionReader.Chips(h));
    }

    LoadState Verdict(string facet, ScreenModel? m)
    {
        _ = Entities.ScopeEpoch.Value;   // see Compute: the tables are per scope
        _ = Home.Changed.Value;
        var h = Entities.HomeFeed(facet);
        var phase = Spotify.Status.Value;
        int zones = m?.Zones.Count ?? 0;
        bool knows = h.Knows(HomeFields.Sections), concluded = h.LiveAttemptConcluded;
        var v = ScreenLoad.Of(zones, knows, concluded,
            phase == Spotify.SessionPhase.Online, phase is >= Spotify.SessionPhase.Offline and <= Spotify.SessionPhase.Minting);
        if (v != _lastVerdict)
        {
            _lastVerdict = v;
            Log.Event(WaveeLogLevel.Info, "home", "facet.verdict", "facet verdict facet=" + facet + " verdict=" + v + " zones=" + zones
                + " knows=" + knows + " concluded=" + concluded + " phase=" + phase + " sections=" + h.SectionCount);
        }
        return v;
    }

    /// <summary>§3.12's D2 demand: the document first, then — once it's known — the baseline preview lookup.
    /// `Home.EnsureFeed`/`Home.EnsurePreviews` are both idempotent (the fetch planner dedupes on Known/Asked); in a
    /// `--fake` build, `Home.EnsureFeed` itself routes through `Entities.SimulateFacetFetch` (F15) — this file never
    /// asks which build it is.</summary>
    static void Demand(string facet)
    {
        var h = Entities.HomeFeed(facet);
        if (!h.Knows(HomeFields.Sections)) Home.EnsureFeed(h);
        else Home.EnsurePreviews(h);
    }

    /// <summary>The first load concluded with no document while online (the Vacancy's Retry): ask for the launch
    /// facet's document AGAIN whatever the planner sealed — <see cref="Entities.Refresh"/> un-asks first, where a plain
    /// <c>Home.EnsureFeed</c> would no-op on a row already marked Asked-and-unanswered (Browse.Page.cs's Retry rule).</summary>
    void RetryFirstLoad()
    {
        var h = Entities.HomeFeed(_switch.Peek().Target);
        if (!h.IsValid) return;
        int slot = h.Slot;
        Entities.Refresh(Entities.Current.Homes, new ReadOnlySpan<int>(in slot), (uint)HomeFields.All, FetchPriority.Visible);
    }

    static ZoneTitles LocalizedTitles() => new(
        MadeForYou: Loc.Get(Strings.Home.MadeForYou),
        BecauseYouLike: Loc.Get(Strings.Home.Zone.BecauseYouLike),
        MoreForYou: Loc.Get(Strings.Home.Zone.MoreForYou),
        RadioAndMixes: Loc.Get(Strings.Home.Zone.RadioAndMixes),
        Browse: Loc.Get(Strings.Home.Zone.Browse),
        JumpBackIn: Loc.Get(Strings.Home.JumpBackIn),
        RecentlyPlayed: Loc.Get(Strings.Sidebar.Section.RecentlyPlayed),
        NewEpisodes: Loc.Get(Strings.Home.Zone.NewEpisodes),
        ContinueListening: Loc.Get(Strings.Home.Zone.ContinueListening),
        VideosYouMightLike: Loc.Get(Strings.Home.Zone.VideosYouMightLike),
        EpisodesYouMightLike: Loc.Get(Strings.Home.Zone.EpisodesYouMightLike),
        BecauseYouListenTo: Loc.Get(Strings.Home.Zone.BecauseYouListenTo),
        FromArtistsYouFollow: Loc.Get(Strings.Home.Zone.FromArtistsYouFollow),
        YourShows: Loc.Get(Strings.Home.Zone.YourShows),
        ShowsYouMightLike: Loc.Get(Strings.Home.Zone.ShowsYouMightLike));

    static ZoneSubtitles LocalizedSubtitles() => new(
        MadeForYou: Loc.Get(Strings.Home.ZoneSub.MadeForYou),
        NewMusic: Loc.Get(Strings.Home.ZoneSub.NewMusic),
        BecauseYouLike: Loc.Get(Strings.Home.ZoneSub.BecauseYouLike),
        JumpBackIn: Loc.Get(Strings.Home.ZoneSub.JumpBackIn),
        Radio: Loc.Get(Strings.Home.ZoneSub.Radio),
        Browse: Loc.Get(Strings.Home.ZoneSub.Browse),
        NewEpisodes: Loc.Get(Strings.Home.ZoneSub.NewEpisodes));

    static bool IsPodcastsFacet(string facet) => facet is "podcasts-chip" or "podcasts-following-chip";

    static LayoutZone? LayoutZoneOf(ZoneKind k) => k switch
    {
        ZoneKind.Daylist => LayoutZone.Daylist,
        ZoneKind.RecentGrid => LayoutZone.Recents,
        ZoneKind.CoverShelf => LayoutZone.MadeForYou,
        ZoneKind.WideTiles => LayoutZone.NewMusic,
        ZoneKind.ReleaseList => LayoutZone.Releases,
        ZoneKind.ClusterCards => LayoutZone.BecauseYouLike,
        ZoneKind.MixedCovers => LayoutZone.JumpBackIn,
        ZoneKind.RadioShelf => LayoutZone.Radio,
        ZoneKind.BrowseTiles => LayoutZone.Browse,
        _ => null,
    };

    // ══ FACET SELECTION ═════════════════════════════════════════════════════════════════════════════════════════════

    void SelectFacet(string target, int tab) => SelectFacetCore(target, pushHistory: true, tab);

    void SelectFacetCore(string target, bool pushHistory, int tab)
    {
        var s = _switch.Peek();
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

        _switch.Value = next;
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

    void RevertRoute(string revertTo, int tab)
        => Shell.GoTo(new Shell.Route(Shell.RouteKind.Home, Arg: Entities.Strings.Intern(FacetRoute.ArgOf(revertTo)), Tab: tab));

    void RememberFollowing(string target)
    {
        var words = _words.Peek();
        foreach (var w in words)
        {
            if (!w.HasSub) continue;
            if (w.SubId == target) { _followingMemory[w.Id] = true; return; }
            if (w.Id == target) { _followingMemory[w.Id] = false; return; }
        }
    }

    void Prefetch(string target)
    {
        var h = Entities.HomeFeed(target);
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool everFetched = h.FetchedAt != 0;
        bool online = Spotify.Status.Value == Spotify.SessionPhase.Online;
        if (FacetCache.ShouldPrefetch(everFetched, h.FetchedAt, nowMs, online, isSubChip: false))
            Home.EnsureFeed(h);
    }

    // ══ RENDER ═══════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The facet content: one KEYED subtree per published facet — the swap IS the reconciler's keyed
    /// Exit/Enter reconcile (F13), not a timer — over ONE dim host for the whole zone column (F17/F18): opacity and
    /// hit-testing are compositor binds on the switch phase, and NOTHING ELSE. The host carries no pointer handler:
    /// any OnPointerDown/OnHoverMove/… sets the node's PointerBit (Reconciler.cs "hit-testable so it receives
    /// press/drag AND bare-hover/exit"), which made the whole zone column an interactive hover scope — every
    /// card → gap → card move flipped it and re-walked hover over the column (seventh pass: the late hover). F35's
    /// "interacted" mark went with it; a Refreshing landing swaps on at-top + within-window alone (FacetSwitch.Landed).</summary>
    Element FacetContent(ScreenModel m, IOverlayService overlay)
    {
        if (m.Facet != _contentFacet)
        {
            _contentFacet = m.Facet;
            Log.Event(WaveeLogLevel.Info, "home", "facet.content", "facet content facet=" + m.Facet + " zones=" + m.Zones.Count
                + " first=" + (m.Zones.Count > 0 ? m.Zones[0].Kind.ToString() : "-"));
        }
        return new BoxEl
        {
            Key = "facet:" + m.Facet, Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
            Enter = new EnterExit(Opacity: 0f, Active: true), Exit = new EnterExit(Opacity: 0f, Active: true),
            Transition = MotionTokenDef.Eased(Design.Motion.Faster, Easing.FluentStandard, ReducedMotionPolicy.KeepFade),
            Children =
            [
                new BoxEl   // the dim host
                {
                    Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
                    Opacity = Prop.Of(() => FacetDimPlan.Of(_switch.Value.Phase).Opacity),      // compositor-only (F18)
                    HitTestVisible = Prop.Of(() => !_switch.Value.Dim),                          // E15
                    Children = [Zones.Column(m, m.Facet, overlay)],
                },
            ],
        };
    }

    /// <summary>The Pending/shimmer document: the facet's OWN last-known zone shape when this tab has shown it
    /// before this session (F30), else a generic first-paint shape per facet family. All opens on the daylist card
    /// (ZonePlanner.PlanAll is the only planner that emits a Daylist zone) unless the Home layout hides it; the other
    /// music facets open on the Recents grid; Podcasts on its own lead. Rendered through the SAME `Zones.Column` as the
    /// live page, so the skeleton is derived from the real tree (the daylist card's SkeletonProxy draws its silhouette
    /// for the blank seed card).</summary>
    ScreenModel Seed(string facet, LayoutDoc layoutDoc)
    {
        ZoneKind[] kinds = _lastShape.TryGetValue(facet, out var known) ? known
            : IsPodcastsFacet(facet)
                ? [ZoneKind.EpisodeLead, ZoneKind.ContinueEpisodes, ZoneKind.ShowGrid]
                : facet.Length == 0 && !layoutDoc.IsHidden(LayoutZone.Daylist)
                    ? [ZoneKind.Daylist, ZoneKind.RecentGrid, ZoneKind.CoverShelf, ZoneKind.CoverShelf]
                    : [ZoneKind.RecentGrid, ZoneKind.CoverShelf, ZoneKind.CoverShelf];

        var zones = new Zone[kinds.Length];
        for (int i = 0; i < kinds.Length; i++)
        {
            var kind = kinds[i];
            int count = kind switch { ZoneKind.RecentGrid => 8, ZoneKind.ShowGrid => 8, ZoneKind.EpisodeLead or ZoneKind.Daylist => 1, _ => 6 };
            var cardKind = kind switch
            {
                ZoneKind.EpisodeLead or ZoneKind.ContinueEpisodes or ZoneKind.EpisodeRows => HomeCardKind.Episode,
                ZoneKind.ShowGrid => HomeCardKind.Podcast,
                ZoneKind.ReleaseList or ZoneKind.WideTiles => HomeCardKind.Album,
                _ => HomeCardKind.Playlist,
            };
            var items = new HomeCard[count];
            string? format = kind == ZoneKind.Daylist ? "daylist" : null;   // a blank of the daylist format (Home.cs s_blankFormats)
            for (int j = 0; j < count; j++) items[j] = HomeCard.Blank(cardKind, j, format);
            zones[i] = new Zone(kind, "seed:" + i, kind == ZoneKind.Daylist ? null : " ", null, items);
        }
        return new ScreenModel(facet, zones, "", Array.Empty<ChipInput>());
    }

    // ── the shell wash (WashPick.Pick + Palette.ShellTint) ──────────────────────────────────────────────────────────

    Element BuildWash(Func<ScreenModel?> model)
    {
        var m = model();
        string? url = null;
        uint accent = 0;
        if (m is { Zones.Count: > 0 })
        {
            var first = m.Zones[0];
            if (first.Items.Count > 0)
            {
                url = first.Items[0].ImageUrl;
                if (first.Kind == ZoneKind.Daylist) accent = first.Items[0].Accent;
            }
        }
        var shellSlot = UseContext(ShellMaterial.Slot);
        uint payload = WashPick.Pick(accent, 0, 0);
        // Every detail-shaped page must mount ONE of these or the window chrome stays neutral while the page is
        // coloured (Palette.Host.cs's own doc comment) — the 0-size leaf lives in the tree, never discarded.
        return Palette.ShellTint(url, ready: url is { Length: > 0 }, disabled: !Prefs.Appearance.ColorWashes(), apply: true,
            owner: _tintOwner, slot: shellSlot, key: "home-tint", payloadAccent: payload);
    }
}
