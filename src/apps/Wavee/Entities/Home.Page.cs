// ── Entities/Home.Page.cs ──────────────────────────────────────────────────────────────────────────────────────────
// the landing page, the extent table, and Home.SectionPage — one class serving home-section: and browse-section:,
// switching on the PREFIX and never on the section's own uri
//
// Role: UI
// Owner: P (stream P2)
// Wave: 5
// Budget: 2230 lines
// Spec: ch 10 §9's settled seven-file Home table (ch 10 1,750 + ch 12 480); ch 10 §1-§8 (the landing: reveal gate,
//       rows, estimator, wash, facets), ch 12 §0-§7a (the section page)
//
// ── THE LANDING READS TABLES, NEVER A LOOP ──────────────────────────────────────────────────────────────────────────
//
// 0.2.9's page ran a 60-s refresh loop and a facet read of its own. In 0.3 the page DEMANDS its whole model on mount
// (`Home.EnsureFeed`, `HomeBrowseCards.EnsureChartDeck`, `Home.Feeds.EnsureTopContent` — contract §8) and composes what
// the tables hold (`HomeComposer.For`). A signal effect over the home / section tables and the session phase is the
// one place a composed document is OFFERED to the reveal gate; the gate (P1's `HomeRevealGate`, verbatim) decides
// Withheld / Held / Reveal / Swap exactly as it did. The 1,500-ms chrome hold and the 8-s force release are one-shot
// host timers; the gate's clock is the host timer clock (`TimerHandle.NowMs`), never the wall.
//
// The gate's EPOCH is page-local: every distinct composed document (the composer memoizes, so a new instance IS a new
// read) takes the next number. A facet and the landing are different rows with different versions, so a row version
// could not order them; arrival order on the one UI thread can.
//
// ── TWO VIEWPORTS, TWO EXTENT TABLES ─────────────────────────────────────────────────────────────────────────────────
//
// The landing (`HomeLandingLayout`) and a facet document (`HomeFacetLayout`) are different row tables and keep their own
// measured corrections and their own scroll keys ("home", "home:<facet>"). The chip row alone keeps ONE key across both
// ("home:row:Chips"), so switching facet re-describes the strip in place and never replays the fused pill's morph.
// Every estimate is the renderer's own arithmetic: the gap from the CAPPED module width (ch 10 §9 defect #1), the artist
// row's OWN gap and its ramp through `RampScaleFor` (#2, #3), the facet row 0's greeting only when that document has no
// hero (#4), and the chip strip only when there are chips (#5).
//
// ── EVERY ROW READS THE LIVE DOCUMENT ────────────────────────────────────────────────────────────────────────────────
//
// Each row is an ungated `Responsive.Of` whose closure reads the loadable's value (so a Ready→Ready swap re-describes
// the realized rows) and the card entity tables' publish signals (so a hydrating playlist repaints its cell), scoped to
// the REALIZED rows only — never a page-wide subscription (ch 10 trap 4). Module rows are keyed by the group's content
// fingerprint (`HomeModuleLayout.SourceGroupKey` — it moves with the section Versions), so a module whose structure
// changed remounts and one that did not rebinds (ReuseGuard stays silent on a table publish).

using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public readonly partial struct Home
{
    // ══ 1. THE PAGE FACTORIES (contract §3; P1's InstallPages registers them) ════════════════════════════════════════

    /// <summary><see cref="Shell.RouteKind.Home"/>: the landing page. The route carries nothing the page reads.</summary>
    public static Element LandingPageFor(in Shell.Route route)
        => Embed.Comp(static () => new HomeLandingView()) with { Key = "home-landing" };

    /// <summary><see cref="Shell.RouteKind.HomeSection"/> AND <see cref="Shell.RouteKind.BrowseSection"/>: ONE page class.
    /// The route KIND (the prefix the caller built) selects the endpoint family — never the section's uri, which a chart
    /// section shares the shape of with a Home section (ch 12 §0.1). Any other kind is a routing bug: it is logged and
    /// painted as the error vacancy, never sent to either endpoint.</summary>
    public static Element SectionPageFor(in Shell.Route route)
    {
        bool browse = route.Kind == Shell.RouteKind.BrowseSection;
        if (!browse && route.Kind != Shell.RouteKind.HomeSection)
        {
            Log.Warn("home", "home.section.route kind=" + route.Kind + " is not a section route");
            return Controls.Vacancy(Controls.VacancyVoice.Error);
        }
        string name = Shell.NameOf(route);
        return Embed.Comp(new HomeSectionPageView.Props(route, browse), static () => new HomeSectionPageView())
            with { Key = "home-section-page:" + name };
    }

    // ══ 2. THE LANDING ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>One reveal group for the page and the Charts row nested inside it, so the deriver reveals them as ONE
    /// transition instead of staggering the inner region a second time.</summary>
    static readonly object s_skeletonGroup = new();

    /// <summary>Row keys by <see cref="HomeRow"/> ordinal — the chrome rows' whole key, the module rows' prefix. A table,
    /// not <c>row.ToString()</c>, which allocates on the realize path.</summary>
    static readonly string[] s_rowKeys =
    [
        "home:row:Chips", "home:row:Hero", "home:row:Weekly", "home:row:Quick", "home:row:Recents", "home:row:MixBand",
        "home:row:Artists", "home:row:ChipCards", "home:row:Radio", "home:row:EpisodesAndBooks", "home:row:Queue",
        "home:row:Books", "home:row:Podcasts", "home:row:Timeline", "home:row:Sections", "home:row:Editorial",
        "home:row:Feed", "home:row:Tail", "home:row:Charts",
    ];

    static string RowKeyOf(HomeRow row) => (int)row < s_rowKeys.Length ? s_rowKeys[(int)row] : "home:row:?";

    /// <summary>The module head the estimators charge: the 20/28 header line plus the 12-DIP head gap.</summary>
    const float ModuleHead = 28f + HomeModuleLayout.HeadGap;

    /// <summary>The tail row: both editorial destinations, then the dock clearance.</summary>
    static float TailRowExtent(float available) => HomeLandingRules.TailExtent(available) + Design.Dock.Reserve + Spacing.XXL;

    /// <summary>The landing page component. Instance state only — two mounted Home tabs each track what THEY consumed.</summary>
    sealed class HomeLandingView : Component
    {
        readonly HomeRevealGate<HomeFeedView> _gate = new();
        readonly HomeLandingLayout _landingLayout = new();
        readonly HomeFacetLayout _facetLayout = new();
        readonly object _washOwner = new();

        readonly Action _feedTick, _chartsTick, _chromeTick, _holdCap, _forceRelease, _mountDemand, _reactivate;
        readonly Action _openRecents, _openCharts, _retryFeed, _retryCharts;
        readonly Action<HomeSectionView> _openSection, _openBrowseSection;
        readonly Func<HomeFeedView, Element> _content;
        readonly Func<Element> _empty, _failed, _chartsEmpty, _chartsFailed;
        readonly Func<IReadOnlyList<HomeSectionView>, Element> _chartsContent;

        Loadable<HomeFeedView> _home = null!;
        Loadable<IReadOnlyList<HomeSectionView>> _charts = null!;
        Loadable<HomeFeedView>? _homeInit;
        Loadable<IReadOnlyList<HomeSectionView>>? _chartsInit;
        TimerHandle _holdTimer, _forceTimer;
        IOverlayService? _overlay;

        int _epoch;
        HomeFeedView? _lastOffered;
        int _demandKey = int.MinValue;
        bool _wasOnline;
        string _publishedFacet = "";
        int _facetFlightSlot = Table.None;

        public HomeLandingView()
        {
            _feedTick = FeedTick;
            _chartsTick = ChartsTick;
            _chromeTick = ChromeTick;
            _holdCap = HoldCap;
            _forceRelease = ForceRelease;
            _mountDemand = MountDemand;
            _reactivate = Reactivate;
            _openRecents = static () => Shell.GoTo(new Shell.Route(Shell.RouteKind.Recents), HomeCardNav.HomeOrigin);
            _openCharts = static () => Shell.GoTo(Shell.Parse("browse:" + ChartPages.Charts, Loc.Get(Strings.Home.Charts)),
                                                  HomeCardNav.HomeOrigin);
            _openSection = static s => HomeCardNav.OpenSection(s, HomeCardNav.HomeOrigin);
            _openBrowseSection = static s => HomeCardNav.OpenBrowseSection(s, HomeCardNav.HomeOrigin);
            _retryFeed = RetryFeed;
            _retryCharts = RetryCharts;
            _content = Content;
            _empty = () => StateHome(Controls.Vacancy(Controls.VacancyVoice.Empty));
            _failed = () => StateHome(Controls.Vacancy(Controls.VacancyVoice.Error, onAction: _retryFeed));
            _chartsContent = list => HomeModules.FoldDeck(list, Loc.Get(Strings.Home.Charts), _openBrowseSection, _openCharts);
            _chartsEmpty = () => ChartsState(Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact,
                                                              title: Loc.Get(Strings.Home.ChartsEmpty), subtitle: ""));
            // Two DIFFERENT blocks at one estimate (ch 10 parity 74): the empty arm is a lone compact line, the failed arm
            // the page-scale sentence + caption + [Retry].
            _chartsFailed = () => ChartsState(Controls.Vacancy(Controls.VacancyVoice.Error, onAction: _retryCharts));
        }

        // ── the warm start (a returning page must paint on frame 1) ───────────────────────────────────────────────

        /// <summary>The <c>_home</c> Loadable's SEED — computed once, at this instance's first construction of it,
        /// straight off the entity graph rather than always defaulting to <see cref="HomeFeedView.Seed"/>'s
        /// skeleton shape. A remount (keep-alive eviction, a second Home tab, a nav back) lands on a Home row this
        /// facet already painted for real THIS SESSION (<see cref="Feeds.HasRevealed"/>) — <see
        /// cref="HomeFeedReadiness.ShouldPaintOnMount(int, bool)"/> says so precisely when the graph still holds
        /// sections for it, so the page's <c>Skel.Region</c> never enters its shimmer branch for data that was never
        /// actually lost. A facet never revealed this session (a genuine cold start, or the very first visit to this
        /// facet) falls through to the ordinary Pending seed and the existing reveal gate, unchanged.</summary>
        static Loadable<HomeFeedView> InitialHome()
        {
            var h = Entities.HomeFeed(SelectedFacet.Peek());
            if (h.IsValid)
            {
                var feed = HomeComposer.For(h, HomeModuleCopy.Titles);
                if (HomeFeedReadiness.ShouldPaintOnMount(feed.Groups.Count, Feeds.HasRevealed(feed.Facet)))
                    return Loadable<HomeFeedView>.Ready(feed);
            }
            return Loadable<HomeFeedView>.Pending(HomeFeedView.Seed);
        }

        /// <summary>The <c>_charts</c> Loadable's SEED — the same warm-start idea for the Charts row: its deck is
        /// already a live read off the section table (<see cref="HomeBrowseCards.ChartDeck"/>, itself memoized), so
        /// a remount that lands after Charts already revealed this session must not re-shimmer the row while it
        /// waits for an effect to notice. A deck that has never revealed, or currently reads empty/failed, falls
        /// through to the ordinary Pending seed — <see cref="ChartsTick"/> resolves it on the same mount pass.</summary>
        static Loadable<IReadOnlyList<HomeSectionView>> InitialCharts()
        {
            var deck = HomeBrowseCards.ChartDeck(Shell.Auth.Value == Shell.AuthState.Live, out _);
            return HomeFeedReadiness.ShouldPaintOnMount(deck.Count, Feeds.HasChartsRevealed())
                ? Loadable<IReadOnlyList<HomeSectionView>>.Ready(deck)
                : Loadable<IReadOnlyList<HomeSectionView>>.Pending(HomeBrowseCards.ChartDeckSeed);
        }

        public override Element Render()
        {
            _home = UseLoadable(_homeInit ??= InitialHome());
            _charts = UseLoadable(_chartsInit ??= InitialCharts());
            var overlay = UseContext(Overlay.Service);
            _overlay = Controls.IsNullOverlay(overlay) ? null : overlay;
            var shellSlot = UseContext(ShellMaterial.Slot);

            UseEffect(_mountDemand, DepKey.Empty);
            UseSignalEffect(_feedTick);
            UseSignalEffect(_chartsTick);
            UseSignalEffect(_chromeTick);
            // Both one-shot: the hold cap is re-armed by a HOLD (Restart), the force release fires once, 8 s after mount.
            _holdTimer = UseTimeout(_holdCap, (float)HomeFeedReadiness.ChromeSettleMs);
            _forceTimer = UseTimeout(_forceRelease, (float)HomeFeedReadiness.ForceReleaseMs);
            UseActivation(onActivated: _reactivate);

            return new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
                Children =
                [
                    Embed.Comp(new HomeWashBinder.Props(_home, null, _washOwner, shellSlot), static () => new HomeWashBinder()),
                    Skel.Region(_home, _content, reveal: SkelReveal.StaggerRows, onFailed: _failed,
                        // A facet is the server's own ordered document (rendered whatever it says); the empty guard is
                        // the unfiltered landing's only, and the gate never publishes a provisional empty (ch 10 §8).
                        isEmpty: static feed => feed.Facet.Length == 0 && feed.Groups.Count == 0, onEmpty: _empty,
                        group: s_skeletonGroup, smoothResize: false),
                ],
            };
        }

        // ── the demand ─────────────────────────────────────────────────────────────────────────────────────────────

        void MountDemand()
        {
            HomeBrowseCards.EnsureChartDeck();
            Feeds.EnsureTopContent();
        }

        /// <summary>A KeepAlive reactivation re-states the demand (idempotent — the planner's freshness decides) instead
        /// of 0.2.9's epoch compare + refetch.</summary>
        void Reactivate()
        {
            EnsureFeed(Entities.HomeFeed(SelectedFacet.Peek()));
            HomeBrowseCards.EnsureChartDeck();
            Feeds.RearmDaylist();
        }

        // ── the feed effect: compose, then offer to the gate ───────────────────────────────────────────────────────

        void FeedTick()
        {
            uint scopeEpoch = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Homes.Changed.Value;
            _ = scope.Sections.Changed.Value;
            _ = scope.Edges.HomeSection.Changed.Value;
            _ = scope.Edges.SectionCards.Changed.Value;
            string selected = SelectedFacet.Value;
            var h = Entities.HomeFeed(selected);
            if (!h.IsValid) return;
            bool concluded = h.LiveAttemptConcluded;                 // reads Spotify.Status: the phase flip re-offers
            bool online = Spotify.Status.Peek() == Spotify.SessionPhase.Online;

            // The demand, once per (scope, row, version, section list, coming online) — EnsureFeed is idempotent, the key
            // only spares the planner a walk.
            int demand = HashCode.Combine(scopeEpoch, h.Slot, h.Version, h.SectionVersion, online);
            if (demand != _demandKey) { _demandKey = demand; EnsureFeed(h); }
            if (online && !_wasOnline) { HomeBrowseCards.EnsureChartDeck(); Feeds.EnsureTopContent(); }
            _wasOnline = online;

            var feed = HomeComposer.For(h, HomeModuleCopy.Titles);
            if (selected.Length > 0)
            {
                var homes = scope.Homes;
                if (homes.Inflight[h.Slot] != 0) _facetFlightSlot = h.Slot;
                if (!h.Knows(HomeFields.Sections))
                {
                    // An unanswered facet keeps the previous document on screen. One that WAS in flight and concluded
                    // with nothing while online failed: the strip goes back where it was and the user is told — a tab
                    // underlined over the previous facet's feed is a page lying about what it shows (0.2.9 RefreshForFacet).
                    if (_facetFlightSlot == h.Slot && homes.Inflight[h.Slot] == 0 && online)
                    {
                        _facetFlightSlot = Table.None;
                        Log.Warn("home", "home.facet.failed facet=" + selected + "; keeping the previous feed");
                        SelectedFacet.Value = _publishedFacet;
                        Notify.Say(Loc.Get(Strings.Home.FacetFailed), InfoBarSeverity.Error);
                    }
                    return;
                }
            }
            // FacetMatches: a document read for another selection never lands on this one.
            if (!string.Equals(feed.Facet, selected, StringComparison.Ordinal)) return;
            if (!ReferenceEquals(feed, _lastOffered)) { _lastOffered = feed; _epoch++; }
            Offer(_epoch, feed, concluded, force: false);
        }

        void Offer(int epoch, HomeFeedView feed, bool concluded, bool force)
        {
            var verdict = _gate.Offer(epoch, feed, feed.Groups.Count, faceted: feed.Facet.Length > 0, concluded, force,
                alreadyResolved: _home.State.Peek() != (byte)LoadState.Pending, ChromeConcluded(), _forceTimer.NowMs);
            switch (verdict)
            {
                case HomeRevealVerdict.Reveal:
                case HomeRevealVerdict.Swap:
                    Publish(feed);
                    break;
                case HomeRevealVerdict.Held:
                    _holdTimer.Restart();   // the cap runs from THIS settle, not from mount
                    break;
            }
        }

        void Publish(HomeFeedView feed)
        {
            _publishedFacet = feed.Facet;
            LogModules(feed);
            _home.SetReady(feed);
            // Session-scoped, survives a keep-alive eviction of THIS component — see InitialHome / Home.Host.cs.
            Feeds.MarkRevealed(feed.Facet);
        }

        static string s_lastShape = "";

        /// <summary>One <c>home.feed.modules</c> line per DISTINCT composed shape (0.2.9 <c>HomeFeedDiagnostics</c>, ch 10
        /// parity 55): which modules the composer produced with how many cards, and which card won the hero — the two
        /// questions a screenshot cannot answer. The facet is part of the dedupe key (two facets can compose one shape).</summary>
        static void LogModules(HomeFeedView feed)
        {
            var sb = new System.Text.StringBuilder(160);
            HomeCard? hero = null;
            for (int i = 0; i < feed.Groups.Count; i++)
            {
                var g = feed.Groups[i];
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(g.Kind).Append('=').Append(g.Cards.Count);
                if (hero is null && g.Kind == HomeGroupKind.Hero && g.Cards.Count > 0) hero = g.Cards[0];
            }
            string shape = sb.ToString();
            string heroLine = hero is { } h
                ? h.Uri + " | " + h.Title + " | fmt=" + (h.Format ?? "-") + " seeds=" + (h.Seeds?.Count ?? 0).ToString(CultureInfo.InvariantCulture)
                : "none";
            string line = "facet=" + feed.Facet + " || " + shape + " || hero: " + heroLine;
            if (string.Equals(line, s_lastShape, StringComparison.Ordinal)) return;
            s_lastShape = line;
            Log.Info("ui", "home.feed.modules facet=" + feed.Facet + " modules=" + shape
                + " groups=" + feed.Groups.Count.ToString(CultureInfo.InvariantCulture) + " hero=" + heroLine
                + " chips=" + (feed.Chips?.Count ?? 0).ToString(CultureInfo.InvariantCulture) + " greeting=" + feed.Greeting);
        }

        // ── the chrome: Charts + the notification feeds (the first reveal waits ≤ 1,500 ms for them) ────────────────

        bool ChromeConcluded() => Home.ChromeConcluded(ChartsLoad(), hasNotifications: true,
            LoadOf(Notify.ReleasesState.Peek()), LoadOf(Notify.SocialState.Peek()));

        HomeLoad ChartsLoad() => _charts.State.Peek() switch
        {
            (byte)LoadState.Pending => HomeLoad.Pending,
            (byte)LoadState.Failed => HomeLoad.Failed,
            _ => HomeLoad.Ready,
        };

        /// <summary>Idle (never fetched: offline, <c>--fake</c>) is CONCLUDED; Loading is the one state worth waiting on.</summary>
        static HomeLoad LoadOf(Notify.FeedState state) => state switch
        {
            Notify.FeedState.Idle => HomeLoad.Idle,
            Notify.FeedState.Loading => HomeLoad.Pending,
            Notify.FeedState.Offline or Notify.FeedState.Error => HomeLoad.Failed,
            _ => HomeLoad.Ready,
        };

        void ChromeTick()
        {
            _ = _charts.State.Value;
            _ = Notify.ReleasesState.Value;
            _ = Notify.SocialState.Value;
            if (_gate.Tick(ChromeConcluded(), _forceTimer.NowMs) is { } feed) Publish(feed);
        }

        void HoldCap()
        {
            if (_gate.Tick(ChromeConcluded(), _forceTimer.NowMs) is { } feed) { Publish(feed); return; }
            // A cap that fired a hair early on the host clock re-arms briefly rather than stranding the held feed.
            if (_gate.IsHolding && !_gate.Revealed) _holdTimer.RestartIn(50f);
        }

        /// <summary>The hard fallback (8 s after mount): if the region is still Pending, force the best document this page
        /// has seen — a held one, else the last read even if withheld — or the empty feed. Nothing ever landed while the
        /// session is online reads as a FAILURE (Retry), not as an empty account.</summary>
        void ForceRelease()
        {
            if (_home.State.Peek() != (byte)LoadState.Pending) return;
            var (epoch, feed) = _gate.ForceRelease();
            if (feed is null)
            {
                var h = Entities.HomeFeed(SelectedFacet.Peek());
                if (h.IsValid && !h.Knows(HomeFields.Sections) && Spotify.Status.Peek() == Spotify.SessionPhase.Online)
                {
                    Log.Warn("home", "home.feed.force-release nothing landed in 8 s while online");
                    _home.SetFailed(s_feedUnavailable);
                    return;
                }
            }
            Offer(Math.Max(epoch, _epoch), feed ?? HomeFeedView.Empty, concluded: true, force: true);
        }

        static readonly Exception s_feedUnavailable = new InvalidOperationException("The home feed did not answer.");
        static readonly Exception s_chartsUnavailable = new InvalidOperationException("browseSection answered no Featured chart section.");

        void RetryFeed()
        {
            EnsureFeed(Entities.HomeFeed(SelectedFacet.Peek()));
            _home.SetPending(HomeFeedView.Seed);
            _forceTimer.Restart();
        }

        // ── the Charts deck: a loadable the row's own region binds, synced from the section rows ──────────────────────

        void ChartsTick()
        {
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Sections.Changed.Value;
            _ = scope.Edges.SectionCards.Changed.Value;
            bool live = Shell.Auth.Value == Shell.AuthState.Live;
            var deck = HomeBrowseCards.ChartDeck(live, out var state);
            // Session-scoped mark for InitialCharts' warm-start seed — see Home.Host.cs. Ready with cards is the
            // only state InitialCharts fast-paths, but the mark is harmless to set whenever the row is no longer
            // genuinely unresolved.
            if (state != HomeLoad.Pending) Feeds.MarkChartsRevealed();
            byte now = _charts.State.Peek();
            switch (state)
            {
                case HomeLoad.Failed:
                    if (now != (byte)LoadState.Ready) _charts.SetFailed(s_chartsUnavailable);
                    break;
                case HomeLoad.Pending:
                    // Monotonic: a revealed deck never re-skeletonizes one row inside a revealed page (0.2.9 "armed once").
                    if (now == (byte)LoadState.Ready && deck.Count > 0 && !ReferenceEquals(_charts.Value.Peek(), deck))
                        _charts.SetReady(deck);
                    break;
                default:
                    if (now != (byte)LoadState.Ready || !ReferenceEquals(_charts.Value.Peek(), deck)) _charts.SetReady(deck);
                    break;
            }
        }

        void RetryCharts()
        {
            HomeBrowseCards.EnsureChartDeck();
            _charts.SetPending(HomeBrowseCards.ChartDeckSeed);
        }

        Element ChartsRow() => Skel.Region(_charts, _chartsContent, reveal: SkelReveal.None,
            onFailed: _chartsFailed, isEmpty: static list => list.Count == 0, onEmpty: _chartsEmpty,
            group: s_skeletonGroup, smoothResize: false);

        /// <summary>Empty / failed Charts keep the ROW (header + the compact grammar): the estimator's
        /// <c>FoldStateExtent</c> is this shape, so the row never reports 0 and flaps the anchor.</summary>
        Element ChartsState(Element state) => new BoxEl
        {
            Direction = 1, Gap = HomeModuleLayout.HeadGap, MinWidth = 0f,
            Children = [HomeModules.DrillHeader(Loc.Get(Strings.Home.Charts), _openCharts), state],
        };

        // ── the content: the landing or a facet document ───────────────────────────────────────────────────────────

        Element Content(HomeFeedView feed) => feed.Facet.Length == 0 ? VirtualHome(feed) : VirtualFacet(feed);

        /// <summary>The empty / failed viewport: the SAME gutter and first-row inset as the live feed, the greeting above
        /// the state, the editorial tail below it.</summary>
        Element StateHome(Element state) => ScrollView(new BoxEl
        {
            Direction = 1, Gap = Spacing.XL, MinWidth = 0f,
            Padding = new Edges4(Spacing.PageWide, Spacing.XXL, Spacing.PageWide, Design.Dock.Reserve + Spacing.XXL),
            Children = [HomeModules.GreetingBlock(null, hasHero: false, chips: null), state, HomeModules.Tail()],
        }) with { Grow = 1f, ScrollKey = "home" };

        Element VirtualHome(HomeFeedView feed)
        {
            var landing = Landing(feed);
            var chips = ChipsOf(feed);
            byte chartsState = _charts.State.Value;                       // subscribe: the Charts row re-estimates
            bool chartsEmpty = _charts.Value.Value.Count == 0;
            _landingLayout.Configure(landing, chips?.Count ?? 0, TopArtistCount(), TimelineRows(), chartsState, chartsEmpty);
            var rows = _landingLayout.Rows;

            string KeyAt(int index)
            {
                var row = (uint)index < (uint)rows.Length ? rows[index] : HomeRow.Tail;
                return FeedGroup(landing, row) is { } g
                    ? RowKeyOf(row) + ":" + HomeModuleLayout.SourceGroupKey(g)
                    : RowKeyOf(row);
            }

            Element RowAt(int index)
            {
                var row = (uint)index < (uint)rows.Length ? rows[index] : HomeRow.Tail;
                string key = KeyAt(index);
                return Responsive.Of(width =>
                {
                    var liveFeed = _home.Value.Value;                      // subscribe: a swap re-describes this row
                    var liveLanding = Landing(liveFeed);
                    SubscribeCards(FeedGroup(liveLanding, row));
                    float available = HomeLandingRules.Available(width);
                    float gap = HomeModuleLayout.Gap(available);
                    float top = row == HomeRow.Chips ? Spacing.XXL : 0f;
                    float bottom = row == HomeRow.Tail ? Design.Dock.Reserve + Spacing.XXL
                                 : RowHasContent(liveLanding, row) ? gap : 0f;
                    return HomeModules.RowShell(RenderRow(liveFeed, liveLanding, row), key, top, bottom);
                }, fallback: HomeModuleLayout.FallbackWidth) with { Key = key };
            }

            return Virtual.Measured(rows.Length, _landingLayout, RowAt, KeyAt, overscan: 1) with
            {
                Grow = 1f, Shrink = 1f, MinHeight = 0f, ScrollKey = "home",
            };
        }

        /// <summary>A facet does not filter the landing — it is a different DOCUMENT, rendered as the server's own ordered
        /// sections (one module per section, server titles, drilling into their own section pages).</summary>
        Element VirtualFacet(HomeFeedView feed)
        {
            var facetRows = FacetRows(feed);
            var chips = ChipsOf(feed);
            _facetLayout.Configure(facetRows, chips?.Count ?? 0);
            int count = facetRows.Count + 2;                              // chips + one row per section + tail
            string facetTag = "home:" + feed.Facet;

            string KeyAt(int index)
            {
                if (index == 0) return RowKeyOf(HomeRow.Chips);           // byte-identical to the landing's
                if (index > facetRows.Count) return RowKeyOf(HomeRow.Tail);
                var row = facetRows[index - 1];
                return facetTag + ":sec:" + (index - 1).ToString(CultureInfo.InvariantCulture) + ":"
                       + HomeModuleLayout.SourceGroupKey(row.Group);
            }

            Element RowAt(int index)
            {
                string key = KeyAt(index);
                bool chipsRow = index == 0;
                bool tail = index > facetRows.Count;
                var mounted = chipsRow || tail ? null : facetRows[index - 1];
                return Responsive.Of(width =>
                {
                    var liveFeed = _home.Value.Value;
                    float available = HomeLandingRules.Available(width);
                    float gap = HomeModuleLayout.Gap(available);
                    if (chipsRow)
                    {
                        SubscribeCards(null);
                        return HomeModules.RowShell(HomeModules.GreetingBlock(liveFeed.Greeting, LiveHasHero(liveFeed), ChipsOf(liveFeed)),
                            key, Spacing.XXL, gap);
                    }
                    if (tail) return HomeModules.RowShell(HomeModules.Tail(), key, 0f, Design.Dock.Reserve + Spacing.XXL);
                    // The row's ROLE stays as mounted; a document whose shape moved produced a different key and remounts.
                    var liveRows = liveFeed.Facet.Length > 0 ? FacetRows(liveFeed) : facetRows;
                    var row = index - 1 < liveRows.Count ? liveRows[index - 1] : mounted!;
                    SubscribeCards(row.Group);
                    if (row.Group.Cards.Count == 0) return HomeModules.RowShell(new BoxEl(), key, 0f, 0f);
                    return HomeModules.RowShell(RenderFacetRow(liveFeed, row), key, 0f, gap);
                }, fallback: HomeModuleLayout.FallbackWidth) with { Key = key };
            }

            return Virtual.Measured(count, _facetLayout, RowAt, KeyAt, overscan: 1) with
            {
                // Per facet: the previous facet's offset belongs to a document that no longer exists.
                Grow = 1f, Shrink = 1f, MinHeight = 0f, ScrollKey = facetTag,
            };
        }

        // ── rows ───────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The realized row's subscriptions: the container tables every card handle reads, plus the item tables
        /// only when the group holds such a card (a track table publish is hot and most rows hold none).</summary>
        static void SubscribeCards(HomeGroup? group)
        {
            var scope = Entities.Current;
            _ = scope.Playlists.Changed.Value;
            _ = scope.Albums.Changed.Value;
            _ = scope.Artists.Changed.Value;
            _ = scope.Shows.Changed.Value;
            if (group is null) return;
            bool episodes = false, tracks = false;
            var cards = group.Cards;
            for (int i = 0; i < cards.Count; i++)
            {
                var k = cards[i].Kind;
                episodes |= k == HomeCardKind.Episode;
                tracks |= k == HomeCardKind.Track;
            }
            if (episodes) _ = scope.Episodes.Changed.Value;
            if (tracks) _ = scope.Tracks.Changed.Value;
        }

        static HomeGroup? FeedGroup(HomeLanding landing, HomeRow row) => row switch
        {
            HomeRow.Hero => landing.Get(HomeGroupKind.Hero)?.Group,
            HomeRow.Weekly => landing.Get(HomeGroupKind.WeeklyPair)?.Group,
            HomeRow.Quick => landing.Get(HomeGroupKind.QuickGrid)?.Group,
            HomeRow.Recents => landing.Get(HomeGroupKind.Recents)?.Group,
            HomeRow.MixBand => landing.Get(HomeGroupKind.MixBand)?.Group,
            HomeRow.ChipCards => landing.Get(HomeGroupKind.ChipCards)?.Group,
            HomeRow.Radio => landing.Get(HomeGroupKind.RadioDial)?.Group,
            HomeRow.Podcasts => landing.Get(HomeGroupKind.PodcastShelf)?.Group,
            HomeRow.Editorial => landing.Get(HomeGroupKind.Featured)?.Group,
            HomeRow.Feed => landing.Get(HomeGroupKind.DiscoverFeed)?.Group,
            HomeRow.EpisodesAndBooks => landing.Get(HomeGroupKind.QueueList)?.Group ?? landing.Get(HomeGroupKind.RatedShelf)?.Group,
            HomeRow.Queue => landing.Get(HomeGroupKind.QueueList)?.Group,
            HomeRow.Books => landing.Get(HomeGroupKind.RatedShelf)?.Group,
            _ => null,
        };

        /// <summary>Who pays the module gap (ch 10 §3): the Artists and Timeline rows pay their OWN (and nothing when
        /// empty); Chips, Charts (present in every state) and Tail always; Sections when the deck has tiles; a module row
        /// when it has a group.</summary>
        static bool RowHasContent(HomeLanding landing, HomeRow row) => row switch
        {
            HomeRow.Artists or HomeRow.Timeline => false,
            HomeRow.Chips or HomeRow.Tail or HomeRow.Charts => true,
            HomeRow.Sections => landing.Sections.Count > 0,
            _ => FeedGroup(landing, row) is not null,
        };

        Action<HomeGroup>? Drill(HomeLanding landing, HomeGroupKind kind)
            => landing.Get(kind) is { PrimarySection: { } section } ? _ => _openSection(section) : null;

        Element RenderRow(HomeFeedView feed, HomeLanding landing, HomeRow row)
        {
            var host = _overlay;
            switch (row)
            {
                case HomeRow.Chips:
                    return HomeModules.GreetingBlock(feed.Greeting, LiveHasHero(feed), ChipsOf(feed));
                case HomeRow.Hero:
                    return landing.Get(HomeGroupKind.Hero) is { Group: { Cards.Count: > 0 } hero }
                        ? HeroModule(feed, hero, null) : new BoxEl();
                case HomeRow.Weekly:
                    return FeedGroup(landing, row) is { } weekly
                        ? HomeModules.WeeklyPair(weekly, host, Drill(landing, HomeGroupKind.WeeklyPair)) : new BoxEl();
                case HomeRow.Quick:
                    return FeedGroup(landing, row) is { } quick
                        ? HomeModules.Quick(quick, host, Drill(landing, HomeGroupKind.QuickGrid)) : new BoxEl();
                case HomeRow.Recents:
                    // The app's OWN Recents page, armed unconditionally: the projection's Recents group carries no uri,
                    // and the destination's availability has nothing to do with this shelf's payload.
                    return FeedGroup(landing, row) is { } recents ? HomeModules.Recents(recents, host, _openRecents) : new BoxEl();
                case HomeRow.MixBand:
                    return FeedGroup(landing, row) is { } mixes
                        ? HomeModules.MixBand(mixes, host, Drill(landing, HomeGroupKind.MixBand)) : new BoxEl();
                case HomeRow.Artists:
                    return ArtistsRow();
                case HomeRow.ChipCards:
                    return FeedGroup(landing, row) is { } chipCards
                        ? HomeModules.ChipCards(chipCards, host, Drill(landing, HomeGroupKind.ChipCards)) : new BoxEl();
                case HomeRow.Radio:
                    return FeedGroup(landing, row) is { } radio
                        ? HomeModules.Radio(radio, host, Drill(landing, HomeGroupKind.RadioDial)) : new BoxEl();
                case HomeRow.EpisodesAndBooks:
                {
                    var episodes = landing.Get(HomeGroupKind.QueueList)?.Group;
                    var books = landing.Get(HomeGroupKind.RatedShelf)?.Group;
                    if (episodes is null && books is null) return new BoxEl();
                    if (episodes is null) return HomeModules.SplitSingle(HomeModules.Audiobooks(books!, host, Drill(landing, HomeGroupKind.RatedShelf)));
                    if (books is null) return HomeModules.SplitSingle(HomeModules.UpNext(episodes, host, Drill(landing, HomeGroupKind.QueueList)));
                    return HomeModules.SplitEven(HomeModules.UpNext(episodes, host, Drill(landing, HomeGroupKind.QueueList)),
                                                 HomeModules.Audiobooks(books, host, Drill(landing, HomeGroupKind.RatedShelf)));
                }
                case HomeRow.Queue:
                    return landing.Get(HomeGroupKind.QueueList)?.Group is { } queueOnly
                        ? HomeModules.SplitSingle(HomeModules.UpNext(queueOnly, host, Drill(landing, HomeGroupKind.QueueList)))
                        : new BoxEl();
                case HomeRow.Books:
                    return landing.Get(HomeGroupKind.RatedShelf)?.Group is { } booksOnly
                        ? HomeModules.SplitSingle(HomeModules.Audiobooks(booksOnly, host, Drill(landing, HomeGroupKind.RatedShelf)))
                        : new BoxEl();
                case HomeRow.Timeline:
                    return HomeModules.Timeline();
                case HomeRow.Podcasts:
                    return FeedGroup(landing, row) is { } podcasts
                        ? HomeModules.Podcasts(podcasts, host, Drill(landing, HomeGroupKind.PodcastShelf)) : new BoxEl();
                case HomeRow.Charts:
                    return ChartsRow();
                case HomeRow.Sections:
                    return landing.Sections.Count == 0
                        ? new BoxEl()
                        : HomeModules.FoldDeck(landing.Sections, Loc.Get(Strings.Home.Sections), _openSection);
                case HomeRow.Editorial:
                    return FeedGroup(landing, row) is { } editorial
                        ? HomeModules.Editorial(editorial, host, Drill(landing, HomeGroupKind.Featured)) : new BoxEl();
                case HomeRow.Feed:
                    return FeedGroup(landing, row) is { } discover
                        ? HomeModules.Feed(discover, host, Drill(landing, HomeGroupKind.DiscoverFeed)) : new BoxEl();
                default:
                    return HomeModules.Tail();
            }
        }

        /// <summary>One server section in the shape the facet projection decided its cards name. <c>open</c> drills into
        /// the section the row came FROM; the coalesced discover feed is not one section, so it has none.</summary>
        Element RenderFacetRow(HomeFeedView feed, HomeFacetRow row)
        {
            var g = row.Group;
            var host = _overlay;
            Action<HomeGroup>? open = row.Section is { } section ? _ => _openSection(section) : null;
            return row.Kind switch
            {
                HomeFacetRowKind.Hero => HeroModule(feed, g, open),
                HomeFacetRowKind.Recents => HomeModules.Recents(g, host, _openRecents),
                HomeFacetRowKind.Podcasts => HomeModules.Podcasts(g, host, open),
                HomeFacetRowKind.Audiobooks => HomeModules.SplitSingle(HomeModules.Audiobooks(g, host, open)),
                HomeFacetRowKind.Episodes => HomeModules.SplitSingle(HomeModules.UpNext(g, host, open)),
                HomeFacetRowKind.Feed => HomeModules.Feed(g, host, null),
                _ => HomeModules.Shelf(g, host, open),
            };
        }

        /// <summary>The hero band inside its server-titled module. The eyebrow is the greeting (server first, local clock
        /// second, the name from <c>User.Me</c>); "· your daylist" only for a real daylist card.</summary>
        Element HeroModule(HomeFeedView feed, HomeGroup g, Action<HomeGroup>? open)
        {
            if (g.Cards.Count == 0) return new BoxEl();
            var card = g.Cards[0];
            string eyebrow = HomeModules.HeroEyebrow(in card, feed.Greeting);
            string meta = HomeModules.CardMeta(in card);
            var host = _overlay;
            var menu = HomeCardNav.MenuOf(in card);
            return HomeModules.SourceModule(g,
                Responsive.Of(w => HomeCards.HeroBand(in card, eyebrow, meta,
                    () => HomeCardNav.Play(in card), () => HomeCardNav.Shuffle(in card),
                    () => HomeCardNav.Open(in card, HomeCardNav.HomeOrigin), () => HomeCardNav.Like(in card),
                    host, menu, w), fallback: 900f),
                open);
        }

        /// <summary>The strip's chips: the document's own, else — for a facet document that carries none — the unfiltered
        /// feed's, so a selected facet can always be stepped back out of.</summary>
        static IReadOnlyList<HomeChip>? ChipsOf(HomeFeedView feed)
            => feed.Chips is { Count: > 0 } chips ? chips
             : feed.Facet.Length > 0 ? HomeComposer.For(Entities.HomeFeed(), HomeModuleCopy.Titles).Chips
             : null;

        /// <summary>Does the page composed for <paramref name="feed"/> open with a hero band? ONE answer for both lists:
        /// the landing asks its projection (a hidden hero is no hero); a facet asks its own rows (ch 10 §9 defect #4).</summary>
        bool LiveHasHero(HomeFeedView feed)
        {
            if (feed.Facet.Length == 0) return Landing(feed).Get(HomeGroupKind.Hero) is not null;
            var rows = FacetRows(feed);
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].Kind == HomeFacetRowKind.Hero) return true;
            return false;
        }

        static int TopArtistCount()
        {
            var scope = Entities.Current;
            int n = scope.Edges.UserTopArtists.Count(scope.MeSlot);
            // While the top content is still coming, estimate the podium's loaded shape rather than nothing.
            return n > 0 ? Math.Min(n, 10) : Feeds.TopContentState.Peek() == HomeLoad.Pending ? 10 : 0;
        }

        static HomeTimelineFeed TimelineRows() => HomeTimelineMerge.Build(Notify.Items.Peek().Items);

        // ── the projections, memoized on the document (a reference hit is a content hit; titles by value) ──────────────

        HomeFeedView? _landingFeed;
        HomeModuleTitles? _landingTitles;
        HomeLanding? _landing;
        HomeLayoutDoc? _landingDoc;
        int _landingDocVersion = -1;

        HomeLanding Landing(HomeFeedView feed)
        {
            var prefs = HomePreferences.Current;
            int version = prefs.LayoutVersion.Value;                      // subscribe: a customize change re-projects
            var doc = prefs.Layout;
            var titles = HomeModuleCopy.Titles;
            if (_landing is { } cached && ReferenceEquals(_landingFeed, feed) && titles.Equals(_landingTitles)
                && version == _landingDocVersion && ReferenceEquals(_landingDoc, doc))
                return cached;
            var landing = HomeLandingProjection.Project(feed, titles, doc);
            _landingFeed = feed;
            _landingTitles = titles;
            _landing = landing;
            _landingDoc = doc;
            _landingDocVersion = version;
            return landing;
        }

        HomeFeedView? _facetFeed;
        HomeModuleTitles? _facetTitles;
        IReadOnlyList<HomeFacetRow>? _facetRows;

        IReadOnlyList<HomeFacetRow> FacetRows(HomeFeedView feed)
        {
            var titles = HomeModuleCopy.Titles;
            if (_facetRows is { } cached && ReferenceEquals(_facetFeed, feed) && titles.Equals(_facetTitles)) return cached;
            var rows = HomeFacetProjection.Rows(feed, titles);
            _facetFeed = feed;
            _facetTitles = titles;
            _facetRows = rows;
            return rows;
        }
    }

    // ══ 3. THE LANDING EXTENT TABLE ═════════════════════════════════════════════════════════════════════════════════

    /// <summary>The landing's variable-height stack with row-aware first estimates. The engine measures every realized row
    /// and feeds the exact extent back through <see cref="SetMeasured"/>; the estimates only make the cold window and the
    /// content extent credible. Retained across swaps, so steady scrolling stays on the Fenwick path. The row table is the
    /// projection's order (hide + reorder applied); <see cref="Estimate"/> switches on <see cref="HomeRow"/> alone.</summary>
    sealed class HomeLandingLayout : IMeasuredVirtualLayout
    {
        HomeRow[] _rows = CopyRows(HomeLandingProjection.DefaultRows);
        public HomeRow[] Rows => _rows;

        readonly ExtentTable _extents = new(0, 1f);
        readonly int[] _counts = new int[(int)HomeGroupKind.PodcastShelf + 1];
        readonly bool[] _titled = new bool[(int)HomeGroupKind.PodcastShelf + 1];
        int _sectionDeck, _chips, _topArtists, _timelineRows;
        byte _chartsState;
        bool _chartsEmpty;
        int _shapeVersion, _seededVersion = -1;
        float _seededCross = float.NaN;

        /// <summary>Mirror the shape in: which kinds are present and how many cards each shows, whether each is titled, the
        /// section deck, the chip count, the podium's artist count, the timeline's rows and the Charts state. Bumps the
        /// shape version only on an ACTUAL change, so a hover or a grading costs nothing here.</summary>
        public void Configure(HomeLanding landing, int chips, int topArtists, in HomeTimelineFeed timeline, byte chartsState,
                              bool chartsEmpty)
        {
            bool changed = false;
            for (int k = 0; k < _counts.Length; k++)
            {
                var g = landing.Get((HomeGroupKind)k)?.Group;
                int n = g?.Cards.Count ?? 0;
                bool titled = g?.Title is { Length: > 0 };
                if (n != _counts[k] || titled != _titled[k]) { _counts[k] = n; _titled[k] = titled; changed = true; }
            }
            changed |= Set(ref _sectionDeck, landing.Sections.Count);
            changed |= Set(ref _chips, chips);
            changed |= Set(ref _topArtists, topArtists);
            changed |= Set(ref _timelineRows, timeline.Shown);
            if (chartsState != _chartsState || chartsEmpty != _chartsEmpty)
            {
                _chartsState = chartsState;
                _chartsEmpty = chartsEmpty;
                changed = true;
            }
            if (!SameRows(_rows, landing.Rows)) { _rows = CopyRows(landing.Rows); changed = true; }
            if (changed) _shapeVersion++;
        }

        static bool Set(ref int field, int value)
        {
            if (field == value) return false;
            field = value;
            return true;
        }

        static bool SameRows(HomeRow[] a, IReadOnlyList<HomeRow> b)
        {
            if (a.Length != b.Count) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        static HomeRow[] CopyRows(IReadOnlyList<HomeRow> src)
        {
            var copy = new HomeRow[src.Count];
            for (int i = 0; i < copy.Length; i++) copy[i] = src[i];
            return copy;
        }

        void Ensure(int itemCount, float crossSize)
        {
            // Measure can ask before arrange publishes a finite cross size: reuse the last real width so a 0-width
            // prepass cannot reset a corrected table every frame.
            float cross = crossSize > 1f ? crossSize : !float.IsNaN(_seededCross) ? _seededCross : HomeModuleLayout.FallbackWidth;
            if (_extents.Count == itemCount && _seededVersion == _shapeVersion
                && !float.IsNaN(_seededCross) && MathF.Abs(_seededCross - cross) <= 0.5f)
                return;
            _extents.Reset(itemCount, 240f);
            for (int i = 0; i < itemCount; i++) _extents.SetExtent(i, Estimate(i, cross));
            _seededCross = cross;
            _seededVersion = _shapeVersion;
        }

        float Estimate(int index, float cross)
        {
            // The SAME arithmetic RowShell performs (cap, then the gutters) — and the gap from THAT width (defect #1).
            float available = HomeLandingRules.Available(cross);
            float gap = HomeModuleLayout.Gap(available);
            var row = (uint)index < (uint)_rows.Length ? _rows[index] : HomeRow.Tail;
            return row switch
            {
                HomeRow.Chips => HomeLandingRules.ChipsContent(_counts[(int)HomeGroupKind.Hero] > 0, _chips) + Spacing.XXL + gap,
                HomeRow.Hero => RowStack(HomeGroupKind.Hero, available, gap),
                HomeRow.Weekly => RowStack(HomeGroupKind.WeeklyPair, available, gap),
                HomeRow.Quick => RowStack(HomeGroupKind.QuickGrid, available, gap),
                HomeRow.Recents => RowStack(HomeGroupKind.Recents, available, gap, shelfOwnsHeader: true),
                HomeRow.MixBand => RowStack(HomeGroupKind.MixBand, available, gap),
                HomeRow.Artists => ArtistsExtent(available),
                HomeRow.ChipCards => RowStack(HomeGroupKind.ChipCards, available, gap),
                HomeRow.Radio => RowStack(HomeGroupKind.RadioDial, available, gap),
                HomeRow.EpisodesAndBooks => SplitExtent(Stack(HomeGroupKind.QueueList, available), Stack(HomeGroupKind.RatedShelf, available),
                                                        available, gap),
                HomeRow.Queue => RowStack(HomeGroupKind.QueueList, available, gap),
                HomeRow.Books => RowStack(HomeGroupKind.RatedShelf, available, gap),
                HomeRow.Podcasts => RowStack(HomeGroupKind.PodcastShelf, available, gap, shelfOwnsHeader: true),
                // The timeline pays its own gap and hides when empty: head + its shown rows (a 40 cover, 8 a side).
                HomeRow.Timeline => _timelineRows == 0 ? 0f
                    : ModuleHead + _timelineRows * (Design.Size.Thumb40 + 2f * Spacing.S) + gap,
                // Pending estimates the LOADED shape (the seed is Fold-shaped); only a settled Failed or a settled empty
                // deck switches to the compact state grammar.
                HomeRow.Charts => (_chartsState == (byte)LoadState.Failed || (_chartsState == (byte)LoadState.Ready && _chartsEmpty)
                    ? HomeModuleLayout.FoldStateExtent : HomeModuleLayout.FoldExtent) + gap,
                HomeRow.Sections => _sectionDeck == 0 ? 0f : HomeModuleLayout.FoldExtent + gap,
                HomeRow.Editorial => RowStack(HomeGroupKind.Featured, available, gap),
                HomeRow.Feed => _counts[(int)HomeGroupKind.DiscoverFeed] == 0 ? 0f
                    : HomeModuleLayout.ContentExtent(HomeGroupKind.DiscoverFeed, available, _counts[(int)HomeGroupKind.DiscoverFeed]) + gap,
                _ => TailRowExtent(available),
            };
        }

        float Stack(HomeGroupKind kind, float available, bool shelfOwnsHeader = false)
        {
            int n = _counts[(int)kind];
            if (n == 0) return 0f;
            return (!shelfOwnsHeader && _titled[(int)kind] ? ModuleHead : 0f) + HomeModuleLayout.ContentExtent(kind, available, n);
        }

        float RowStack(HomeGroupKind kind, float available, float gap, bool shelfOwnsHeader = false)
        {
            float extent = Stack(kind, available, shelfOwnsHeader);
            return extent > 0f ? extent + gap : 0f;
        }

        /// <summary>Side by side above the split threshold (the taller), stacked below (the sum + the stack gap).</summary>
        static float SplitExtent(float left, float right, float width, float outerGap)
        {
            if (left <= 0f && right <= 0f) return 0f;
            if (width >= HomeModuleLayout.SplitEvenMin || left <= 0f || right <= 0f) return MathF.Max(left, right) + outerGap;
            return left + HomeModuleLayout.Gap(width) + right + outerGap;
        }

        /// <summary>The podium sizes itself: head + its padding + the TALLEST avatar at the fill-the-width ramp
        /// (<see cref="HomeArtistRowLayout.RampScaleFor"/>, defect #3) + the pod gap + a two-line 12/16 label, then the
        /// row's OWN module gap (<see cref="HomeArtistRowLayout.ModuleGap"/>, defect #2). Zero artists render nothing.</summary>
        float ArtistsExtent(float available)
        {
            int count = _topArtists;
            if (count <= 0) return 0f;
            const float PodChrome = Spacing.S;
            float podiumContentW = MathF.Max(0f, available - 2f * Spacing.M);
            var (_, fittedPodW) = FillRowVirtualLayout.Fit(podiumContentW, HomeArtistRowLayout.BaseArtSize(0) + PodChrome, 9999f,
                                                           Spacing.S, perPageOverride: count);
            float scale = HomeArtistRowLayout.RampScaleFor(fittedPodW, count, PodChrome);
            return ModuleHead + 2f * Spacing.M + HomeArtistRowLayout.ArtSize(0, scale) + Spacing.S + 2f * 16f
                   + HomeArtistRowLayout.ModuleGap(available);
        }

        public float ContentExtent(int itemCount, float crossSize)
        {
            Ensure(itemCount, crossSize);
            return (float)_extents.Total;
        }

        public void Window(int itemCount, float crossSize, float viewportExtent, float scrollOffset, int overscan,
                           out int first, out int last)
        {
            Ensure(itemCount, crossSize);
            first = Math.Max(0, _extents.IndexAt(scrollOffset) - overscan);
            last = Math.Min(itemCount, _extents.IndexAt(scrollOffset + viewportExtent) + 1 + overscan);
            if (last < first) last = first;
        }

        public RectF ItemRect(int index, float crossSize)
        {
            Ensure(_rows.Length, crossSize);
            return new RectF(0f, _extents.OffsetOf(index), crossSize, _extents.ExtentAt(index));
        }

        public void SetMeasured(int index, float mainExtent, float crossSize)
        {
            Ensure(_rows.Length, crossSize);
            _extents.SetExtent(index, mainExtent);
        }

        public float OffsetOf(int index, float crossSize)
        {
            Ensure(_rows.Length, crossSize);
            return _extents.OffsetOf(index);
        }

        public int IndexAt(float offset, float crossSize)
        {
            Ensure(_rows.Length, crossSize);
            return _extents.IndexAt(offset);
        }
    }

    // ══ 4. THE FACET EXTENT TABLE ═══════════════════════════════════════════════════════════════════════════════════

    /// <summary>The facet document's stack: the chip / greeting block at 0, one row per server section, the tail last.
    /// Its own table (a facet and the landing are different documents: one table describing both would reseed on every
    /// swap and discard both sets of corrections). The fingerprint per row is (kind, card count, titled).</summary>
    sealed class HomeFacetLayout : IMeasuredVirtualLayout
    {
        readonly record struct RowMetric(HomeFacetRowKind Kind, int Count, bool Titled);

        RowMetric[] _rows = [];
        readonly ExtentTable _extents = new(0, 1f);
        int _chips;
        bool _hasHero;
        int _shapeVersion, _seededVersion = -1;
        float _seededCross = float.NaN;

        public int ItemCount => _rows.Length + 2;

        public void Configure(IReadOnlyList<HomeFacetRow> rows, int chips)
        {
            bool hasHero = false;
            for (int i = 0; i < rows.Count; i++) hasHero |= rows[i].Kind == HomeFacetRowKind.Hero;
            bool changed = chips != _chips || hasHero != _hasHero;
            _chips = chips;
            _hasHero = hasHero;
            if (!SameShape(_rows, rows))
            {
                var next = new RowMetric[rows.Count];
                for (int i = 0; i < next.Length; i++) next[i] = Metric(rows[i]);
                _rows = next;
                changed = true;
            }
            if (changed) _shapeVersion++;
        }

        static RowMetric Metric(HomeFacetRow row) => new(row.Kind, row.Group.Cards.Count, row.Group.Title is { Length: > 0 });

        static bool SameShape(RowMetric[] a, IReadOnlyList<HomeFacetRow> b)
        {
            if (a.Length != b.Count) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != Metric(b[i])) return false;
            return true;
        }

        void Ensure(int itemCount, float crossSize)
        {
            float cross = crossSize > 1f ? crossSize : !float.IsNaN(_seededCross) ? _seededCross : HomeModuleLayout.FallbackWidth;
            if (_extents.Count == itemCount && _seededVersion == _shapeVersion
                && !float.IsNaN(_seededCross) && MathF.Abs(_seededCross - cross) <= 0.5f)
                return;
            _extents.Reset(itemCount, 240f);
            for (int i = 0; i < itemCount; i++) _extents.SetExtent(i, Estimate(i, cross));
            _seededCross = cross;
            _seededVersion = _shapeVersion;
        }

        float Estimate(int index, float cross)
        {
            float available = HomeLandingRules.Available(cross);
            float gap = HomeModuleLayout.Gap(available);
            // Row 0: the greeting shows only when THIS document opens with no hero (defect #4); the strip only with chips.
            if (index == 0) return HomeLandingRules.ChipsContent(_hasHero, _chips) + Spacing.XXL + gap;
            int row = index - 1;
            if ((uint)row >= (uint)_rows.Length) return TailRowExtent(available);
            var metric = _rows[row];
            if (metric.Count == 0) return 0f;
            return metric.Kind switch
            {
                HomeFacetRowKind.Hero => (metric.Titled ? ModuleHead : 0f)
                    + HomeModuleLayout.ContentExtent(HomeGroupKind.Hero, available, 1) + gap,
                // Every paged shelf owns its header, chevrons and lift clearance: ShelfExtent IS the whole row.
                HomeFacetRowKind.Audiobooks => (metric.Titled ? ModuleHead : 0f)
                    + HomeModuleLayout.ContentExtent(HomeGroupKind.RatedShelf, available, metric.Count) + gap,
                HomeFacetRowKind.Episodes => (metric.Titled ? ModuleHead : 0f)
                    + HomeModuleLayout.ContentExtent(HomeGroupKind.QueueList, available, metric.Count) + gap,
                _ => HomeModuleLayout.ShelfExtent(available) + gap,
            };
        }

        public float ContentExtent(int itemCount, float crossSize)
        {
            Ensure(itemCount, crossSize);
            return (float)_extents.Total;
        }

        public void Window(int itemCount, float crossSize, float viewportExtent, float scrollOffset, int overscan,
                           out int first, out int last)
        {
            Ensure(itemCount, crossSize);
            first = Math.Max(0, _extents.IndexAt(scrollOffset) - overscan);
            last = Math.Min(itemCount, _extents.IndexAt(scrollOffset + viewportExtent) + 1 + overscan);
            if (last < first) last = first;
        }

        public RectF ItemRect(int index, float crossSize)
        {
            Ensure(ItemCount, crossSize);
            return new RectF(0f, _extents.OffsetOf(index), crossSize, _extents.ExtentAt(index));
        }

        public void SetMeasured(int index, float mainExtent, float crossSize)
        {
            Ensure(ItemCount, crossSize);
            _extents.SetExtent(index, mainExtent);
        }

        public float OffsetOf(int index, float crossSize)
        {
            Ensure(ItemCount, crossSize);
            return _extents.OffsetOf(index);
        }

        public int IndexAt(float offset, float crossSize)
        {
            Ensure(ItemCount, crossSize);
            return _extents.IndexAt(offset);
        }
    }

    // ══ 5. THE WASH LEAF (the landing's three legs; a section page's one) ═══════════════════════════════════════════

    /// <summary>The shell material publisher at LEAF scope: it watches at most three artworks (never a plane epoch) and
    /// the card tables, so a landed grading or a hydrating cover re-publishes the wash without re-rendering the page.
    /// Claims on its first publish and on every KeepAlive reactivation; refreshes while the owner; never clears
    /// (<see cref="ShellMaterial.Publish"/>). Washes off ⇒ a DEFINITE neutral claim. Renders a 0 × 0 box.</summary>
    sealed class HomeWashBinder : Component
    {
        /// <summary>Exactly one source: the landing's feed loadable, or a section page's section loadable. Loadables are
        /// compared by reference, so the parent never re-renders this leaf; its own reads do.</summary>
        public sealed record Props(Loadable<HomeFeedView>? Feed, Loadable<HomeSectionView>? Section, object Owner,
                                   Signal<ShellMaterialState>? Slot);

        /// <summary>How far down a section the wash may look for a card that can vouch for a colour — the TOP of the page,
        /// not a search over a fully paged section.</summary>
        const int WashScan = 32;

        Props? _p;
        HomeWash? _wash;
        bool _definite, _claimed;
        readonly Action _publish, _claim;

        public HomeWashBinder()
        {
            _publish = () => Publish(isClaim: !_claimed);
            _claim = () => Publish(isClaim: true);
        }

        public override Element Render()
        {
            var p = UseProps<Props>();
            _p = p;
            bool washes = Prefs.Appearance.ColorWashes();                 // the Settings toggle applies LIVE
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Playlists.Changed.Value;
            _ = scope.Albums.Changed.Value;
            _ = scope.Artists.Changed.Value;
            _ = scope.Shows.Changed.Value;

            HomeWashPicks picks = default;
            if (p.Feed is { } feedLoadable)
            {
                // The seed and the loaded feed go through this ONE path: blank cards carry no accent and no artwork, so the
                // seed resolves to no layer and the shell keeps its bare ground while Home loads — never a placeholder hue.
                var cards = HomeWashSource.Sources(feedLoadable.Value.Value);
                Watch(HomeWashSource.PlaneUrl(cards.Hero));
                Watch(HomeWashSource.PlaneUrl(cards.Weekly));
                Watch(HomeWashSource.PlaneUrl(cards.Mix));
                if (washes) picks = HomeWashSource.Select(in cards, HomeCards.ChromeScheme);
                _wash = washes ? new HomeWash(Layer(picks.Hero), Layer(picks.Weekly), Layer(picks.Mix)) : null;
            }
            else if (p.Section is { } sectionLoadable)
            {
                var card = WashCard(sectionLoadable.Value.Value);
                Watch(HomeWashSource.PlaneUrl(card));
                var pick = washes ? HomeWashSource.Pick(card, HomeCards.ChromeScheme) : null;
                picks = new HomeWashPicks(pick, null, null);
                _wash = pick is { } k ? new HomeWash(new WashLayer(k.Color, k.Key), null, null) : null;
            }
            _definite = !washes;

            UseEffect(_publish, DepKey.From(HashCode.Combine(washes, HomeWashSource.Fingerprint(in picks), (int)Tok.Theme)));
            UseActivation(onActivated: _claim);
            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        }

        void Publish(bool isClaim)
        {
            if (_p is not { } p) return;
            ShellMaterial.Publish(p.Slot, p.Owner, isClaim, _definite, tint: null, wash: _wash);
            _claimed = true;
        }

        /// <summary>Subscribe to ONE artwork's grading. A null/empty url is never watched.</summary>
        static void Watch(string? url)
        {
            if (url is { Length: > 0 }) _ = Palette.Watch(url).Value;
        }

        static WashLayer? Layer(HomeWashPick? pick) => pick is { } p ? new WashLayer(p.Color, p.Key) : null;

        /// <summary>The section's first card that can vouch for a colour (a payload accent or a gradeable cover), or null
        /// — a loading section's blank cards carry neither, which is why it shows the bare ground.</summary>
        static HomeCard? WashCard(HomeSectionView section)
        {
            var cards = section.Cards;
            int scan = Math.Min(cards.Count, WashScan);
            for (int i = 0; i < scan; i++)
            {
                var c = cards[i];
                if (c.Accent != 0u || c.ImageUrl is { Length: > 0 }) return c;
            }
            return null;
        }
    }

    // ══ 6. THE SECTION PAGE (ch 12) ═════════════════════════════════════════════════════════════════════════════════

    /// <summary>One page class for two drill-in kinds — a Home source section and a Browse section — because both render
    /// the identical grid of cards. The route KIND selects the family: a <c>home-section:</c> route pages through
    /// <c>homeSection</c> only, a <c>browse-section:</c> one through <c>browseSection</c> only, with no fallback either way
    /// (a stale hash surfaces as a visible failure, never a silent read of the wrong endpoint).
    /// <para>The masthead is a PUBLICATION (<c>Shell.Mastheads</c>): the title is the route's arg until the row lands, so
    /// the band paints real content from frame one; only the grid shimmers. Charts (the DECODED
    /// <c>SectionFlags.Chart</c>) adds the filter toolbar and walks every page eagerly; every other section pages silently
    /// on scroll through the append preloader and offers "Show all" on the band.</para></summary>
    sealed class HomeSectionPageView : Component
    {
        public sealed record Props(Shell.Route Route, bool Browse);

        /// <summary>WinUI's compact search width, and the shared 32-DIP toolbar rung.</summary>
        const float FilterBoxWidth = 300f, FilterBoxHeight = 32f;
        /// <summary>The walk indicator's PERMANENT slot (the row never moves when the bar appears); 3 = ProgressBarMinHeight.</summary>
        const float WalkBarWidth = 160f, WalkBarTrackH = 3f;
        /// <summary>What an unknown remaining count is worth to the walk fraction: one more page. Charts under-reports.</summary>
        const int WalkPageAssumed = 20;
        /// <summary>The backstop on the eager walk: the endpoint's terminators are the real end.</summary>
        const int WalkMaxRequests = 40;
        /// <summary>Charts titles wrap to two lines, paid for by the metadata rung chart cards do not render.</summary>
        const int ChartsTitleLines = 2;
        static readonly string[] NoSuggest = [];
        static readonly Exception s_sectionFailed = new InvalidOperationException("The section did not answer.");

        readonly Signal<bool> _loading = new(false), _nearTail = new(true), _walking = new(false), _exhausted = new(false);
        /// <summary>False while KeepAlive has parked the page: the eager walk asks for nothing in the background (ch 12
        /// parity 36) and resumes from where it stood on reactivation.</summary>
        readonly Signal<bool> _active = new(true);
        readonly Action _reactivated, _deactivated;
        readonly FloatSignal _walkFrac = new(0f);
        readonly Signal<string> _filter = new("");
        readonly object _washOwner = new();
        readonly (Func<ScrollGeometry, long> Project, Action<ScrollGeometry> Action) _scrollWatch;
        readonly Action _sync, _demand, _loadMore, _publishMasthead;
        readonly Action<HomeCard> _open;
        readonly Func<HomeSectionView, Element> _gridBody;
        readonly Func<Element> _empty, _failed;
        readonly Func<HomeSectionView, bool> _isEmpty;

        Props? _p;
        Loadable<HomeSectionView> _section = null!;
        Loadable<HomeSectionView>? _sectionInit;

        HomeSectionView? _lastView;
        Spotify.SessionPhase _lastPhase;
        // The one request in flight (append or walk step): the view before it, the offset it asked, and the row's
        // versions at ask time — a change in either is the landing, a recorded edge failure is the miss.
        HomeSectionView? _before;
        int _requestOffset;
        uint _reqVersion, _reqCardVersion;
        bool _walkStarted, _walkDone;
        int _walkRequests;

        // the masthead publication's latest values (the effect reads them; its dep key is their hash)
        string _pubTitle = "";
        bool _pubCan, _pubLoading;

        public HomeSectionPageView()
        {
            _scrollWatch = HomeSectionAppendPreloader.NearTailWatch(_nearTail);
            _sync = Sync;
            _demand = Demand;
            _loadMore = LoadMore;
            _publishMasthead = PublishMasthead;
            _open = static card => HomeCardNav.Open(in card);
            _gridBody = GridBody;
            _isEmpty = static s => s.Cards.Count == 0 && s.UnsupportedCount == 0;
            _empty = static () => new BoxEl { Grow = 1f, MinHeight = 0f, Children = [Controls.Vacancy(Controls.VacancyVoice.Empty)] };
            // No Retry on the section page's failure (ch 12 parity 21): the drill is re-entered, not retried in place.
            _failed = static () => new BoxEl { Grow = 1f, MinHeight = 0f, Children = [Controls.Vacancy(Controls.VacancyVoice.Error)] };
            _reactivated = () => { _active.Value = true; PublishMasthead(); };
            _deactivated = () => _active.Value = false;
        }

        public override Element Render()
        {
            var p = UseProps<Props>();
            _p = p;
            string uri = p.Route.Subject.Text;
            // A client-minted `wavee:local:` identity addresses nothing on any server (a persisted route from 0.2.9's
            // preview store): the page's ordinary empty state with the route's title, and no request ever issued.
            bool expired = HomeSectionRoutes.IsLocal(uri);
            _section = UseLoadable(_sectionInit ??= InitialState(p, uri, expired));
            var shellSlot = UseContext(ShellMaterial.Slot);

            UseEffect(_demand, DepKey.From(StringComparer.Ordinal.GetHashCode(uri)));
            UseSignalEffect(_sync);

            var current = _section.Value.Value;                           // subscribe: title, toolbar, Show all
            _ = _section.State.Value;
            bool charts = p.Browse && current.IsChart;
            bool loading = _loading.Value;
            bool exhausted = _exhausted.Value;
            bool walking = _walking.Value;
            bool canLoadMore = !charts && !expired && CanPage(current) && !exhausted && HomeSectionPaging.HasMore(current, current.NextOffset);

            _pubTitle = SectionTitle(current, p);
            _pubCan = canLoadMore;
            _pubLoading = loading;
            UseEffect(_publishMasthead, DepKey.From(HashCode.Combine(_pubTitle, canLoadMore, loading)));
            // The registry is a 16-entry LRU: a reactivated page re-publishes rather than trusting it survived.
            UseActivation(onActivated: _reactivated, onDeactivated: _deactivated);

            Element body = new BoxEl
            {
                Key = "home-section-body",
                Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children =
                [
                    Skel.Region(_section, _gridBody, reveal: SkelReveal.StaggerRows, onFailed: _failed,
                        isEmpty: _isEmpty, onEmpty: _empty, smoothResize: false),
                ],
            };
            Element[] kids = charts ? [ChartsToolbar(walking), body] : [body];

            return new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
                // The masthead is the shell's overlay band; the family body pad clears it, and the gutters match the
                // directory and a category page so a drill never jumps the content column.
                Padding = BrowseMastheadMetrics.FamilyBodyPad(Spacing.L),
                Children =
                [
                    Embed.Comp(new HomeWashBinder.Props(null, _section, _washOwner, shellSlot), static () => new HomeWashBinder()),
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Gap = Spacing.M,
                        Children = kids,
                    },
                ],
            };
        }

        /// <summary>ONE toolbar row whose child COUNT never changes: the filter box, a spacer, and the walk bar's permanent
        /// slot (only the bar inside it comes and goes). Height reserved on the row: the box is a component, and a row of
        /// one unmirrored ComponentEl measures 0.</summary>
        Element ChartsToolbar(bool walking) => new BoxEl
        {
            Key = "home-section-charts-toolbar",
            Direction = 0, Grow = 0f, Shrink = 0f, MinHeight = FilterBoxHeight, MinWidth = 0f,
            AlignSelf = FlexAlign.Stretch, AlignItems = FlexAlign.Center, Gap = Spacing.M,
            Children =
            [
                AutoSuggestBox.Create(NoSuggest, Loc.Get(Strings.Library.Filter), width: FilterBoxWidth, text: _filter,
                    queryIcon: Icons.Search, minHeight: FilterBoxHeight, cornerRadius: Radii.Control),
                new BoxEl { Grow = 1f, Shrink = 1f, MinWidth = 0f },
                new BoxEl
                {
                    Direction = 0, Grow = 0f, Shrink = 0f, Width = WalkBarWidth, AlignSelf = FlexAlign.Center,
                    MinHeight = WalkBarTrackH,
                    Children = walking ? [ProgressBar.Create(_walkFrac, width: WalkBarWidth)] : Array.Empty<Element>(),
                },
            ],
        };

        /// <summary>The grid body: the card list is derived INSIDE the responsive closure, off signals, so a landed page
        /// and a filter keystroke re-describe the grid; the silent append preloader trails it, keyed on the cursor (its
        /// Key is its prop channel — a new cursor is a new preloader with a fresh attempt budget).</summary>
        Element GridBody(HomeSectionView current)
        {
            var p = _p!;
            bool canAutoPage = !(p.Browse && current.IsChart) && CanPage(current) && !_exhausted.Value
                               && HomeSectionPaging.HasMore(current, current.NextOffset);
            return new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
                Children =
                [
                    Responsive.Of(width =>
                    {
                        var live = _section.Value.Value;
                        bool isChart = _p!.Browse && live.IsChart;
                        string q = isChart ? _filter.Value.Trim() : "";
                        var shown = isChart ? ChartTitleMatch.Filter(live.Cards, q) : live.Cards;
                        if (q.Length > 0 && shown.Count == 0)
                            return new BoxEl
                            {
                                Grow = 1f, MinHeight = 0f,
                                // The Subtitle rung (20/28), the one sentence (ch 12 parity 33).
                                Children = [Controls.Vacancy(Controls.VacancyVoice.NoMatch, Controls.VacancyScale.Compact,
                                                             title: Loc.Get(Strings.Library.NoMatch), subtitle: "")],
                            };
                        return HomeModules.SectionGrid(shown, live.Uri, width, _open,
                            onScrollGeometryChanged: isChart ? null : _scrollWatch,
                            highlightQuery: q.Length > 0 ? q : null,
                            titleLines: isChart ? ChartsTitleLines : 1,
                            charts: isChart);
                    }, fallback: HomeModuleLayout.FallbackWidth, grow: 1f),
                    canAutoPage
                        ? (Element)(Embed.Comp(() => new HomeSectionAppendPreloader { Loading = _loading, NearTail = _nearTail, Start = _loadMore })
                            with { Key = "home-section-append:" + (current.Uri ?? "") + ":"
                                         + HomeSectionPaging.NextOffset(current).ToString(CultureInfo.InvariantCulture) })
                        : new BoxEl(),
                ],
            };
        }

        // ── the demand and the table sync ──────────────────────────────────────────────────────────────────────────

        Section RowOf(Props p)
        {
            string uri = p.Route.Subject.Text;
            if (uri.Length == 0 || HomeSectionRoutes.IsLocal(uri)) return default;
            return p.Browse ? Entities.BrowseSection(uri) : Entities.Section(uri);
        }

        void Demand()
        {
            if (_p is not { } p) return;
            var s = RowOf(p);
            if (s.IsValid) EnsureSection(s, p.Browse);
        }

        /// <summary>The row → the loadable: Ready once the band has an identity or cards; Failed when the ask concluded
        /// with nothing while online (or its card edge recorded a failure); Pending otherwise. Then the in-flight page's
        /// bookkeeping, and — for Charts — the next step of the eager walk.</summary>
        void Sync()
        {
            if (_p is not { } p) return;
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Sections.Changed.Value;
            _ = scope.Edges.SectionCards.Changed.Value;
            var phase = Spotify.Status.Value;
            bool active = _active.Value;
            var s = RowOf(p);
            if (!s.IsValid) return;
            if (phase == Spotify.SessionPhase.Online && _lastPhase != Spotify.SessionPhase.Online) EnsureSection(s, p.Browse);
            _lastPhase = phase;

            var view = HomeSectionView.Of(s);
            if (s.Knows(SectionFields.Identity) || view.Cards.Count > 0)
            {
                if (!ReferenceEquals(view, _lastView) || _section.State.Peek() != (byte)LoadState.Ready)
                {
                    _lastView = view;
                    _section.SetReady(view);
                }
                if (p.Browse && view.IsChart) Walk(s, view, active);
                else Land(s, view, p.Browse);
                return;
            }
            var t = scope.Sections;
            bool failed = scope.Edges.SectionCards.Readiness(s.Slot) == EdgeState.Failed
                          || (t.Inflight[s.Slot] == 0 && (t.Asked[s.Slot] & (uint)SectionFields.Identity) != 0
                              && phase == Spotify.SessionPhase.Online);
            if (failed && _section.State.Peek() == (byte)LoadState.Pending) _section.SetFailed(s_sectionFailed);
        }

        /// <summary>Did the request in flight land (the row moved) or miss (the edge recorded a failure)?</summary>
        (bool Landed, bool Failed) Outcome(Section s)
            => (s.Version != _reqVersion || s.CardVersion != _reqCardVersion, Entities.Current.Edges.SectionCards.IsFailed(s.Slot));

        /// <summary>An append landed: disarm the tail until a FRESH scroll re-confirms (so a still page never chain-loads
        /// the whole section), then latch exhausted when the cursor cannot advance or the page put nothing new on screen
        /// — never by the total, which the server over- and under-reports.</summary>
        void Land(Section s, HomeSectionView view, bool browse)
        {
            if (!_loading.Peek() || _before is null) return;
            var (landed, failed) = Outcome(s);
            if (!landed && !failed) return;
            _loading.Value = false;
            _nearTail.Value = false;
            var before = _before;
            _before = null;
            if (!landed)
            {
                // Transient: what we have stays visible and "Show all" stays armed, so the user can retry.
                Log.Warn("home", "home.section.page.fail uri=" + (view.Uri ?? "") + " offset=" + _requestOffset.ToString(CultureInfo.InvariantCulture));
                return;
            }
            int next = browse
                ? HomeSectionPaging.BrowseSectionNextOffset(_requestOffset, view.NextOffset,
                    Math.Max(0, view.RawItemCount - _requestOffset), view.TotalCount)
                : view.NextOffset;
            bool advances = HomeSectionPaging.CanAdvance(_requestOffset, next) && HomeSectionPaging.Progressed(before, view);
            if (!advances) _exhausted.Value = true;
        }

        /// <summary>"Show all" (the band's tool) and the preloader's verb: ask for the next page at the RAW cursor.</summary>
        void LoadMore()
        {
            if (_p is not { } p || _loading.Peek() || _exhausted.Peek()) return;
            var s = RowOf(p);
            if (!s.IsValid) return;
            var view = HomeSectionView.Of(s);
            if (!CanPage(view)) return;
            int offset = HomeSectionPaging.NextOffset(view);
            uint version = s.Version, cardVersion = s.CardVersion;       // BEFORE the ask: a cache answer may land inside it
            if (!RequestNextPage(s, p.Browse)) { _exhausted.Value = true; return; }
            _before = view;
            _requestOffset = offset;
            _reqVersion = version;
            _reqCardVersion = cardVersion;
            _loading.Value = true;
        }

        /// <summary>The eager Charts walk (ch 12 W9): publish what the row holds, ask for the next offset, fold each landed
        /// page through <see cref="BrowseSectionWalk.Fold"/>, stop on the endpoint's terminators, a miss, or the 40-request
        /// backstop — logged, because a truncated walk leaves no other affordance behind.</summary>
        void Walk(Section s, HomeSectionView view, bool active)
        {
            if (_walkDone) return;
            if (!_walkStarted)
            {
                _walkStarted = true;
                if (BrowseSectionWalk.Begin(view).Exhausted) { FinishWalk(); return; }
                _walking.Value = true;
                _walkFrac.Value = HomeSectionPaging.WalkFraction(view, WalkPageAssumed);
            }
            if (_loading.Peek())
            {
                var (landed, failed) = Outcome(s);
                if (!landed && !failed) return;
                _loading.Value = false;
                var before = _before ?? view;
                _before = null;
                if (!landed)
                {
                    Log.Warn("home", "home.section.walk.fail uri=" + (view.Uri ?? "") + " requests="
                        + _walkRequests.ToString(CultureInfo.InvariantCulture) + "; the pages already folded remain visible");
                    FinishWalk();
                    return;
                }
                var step = BrowseSectionWalk.Fold(before, view, _requestOffset);
                if (step.Exhausted) { FinishWalk(); return; }
                _walkFrac.Value = HomeSectionPaging.WalkFraction(view, WalkPageAssumed);
            }
            if (!active) return;                                           // parked: the next ask waits for reactivation
            if (++_walkRequests > WalkMaxRequests)
            {
                Log.Warn("home", "home.section.walk.capped uri=" + (view.Uri ?? "") + " offset="
                    + HomeSectionPaging.NextOffset(view).ToString(CultureInfo.InvariantCulture) + "; the section is shown truncated");
                FinishWalk();
                return;
            }
            int offset = HomeSectionPaging.NextOffset(view);
            uint version = s.Version, cardVersion = s.CardVersion;
            if (!RequestNextPage(s, browse: true)) { FinishWalk(); return; }
            _before = view;
            _requestOffset = offset;
            _reqVersion = version;
            _reqCardVersion = cardVersion;
            _loading.Value = true;
        }

        void FinishWalk()
        {
            _walkDone = true;
            _walking.Value = false;
            _walkFrac.Value = 1f;
            _exhausted.Value = true;
        }

        void PublishMasthead()
        {
            if (_p is not { } p) return;
            var route = p.Route;
            Shell.Mastheads.Publish(in route, new Shell.MastheadPublication(_pubTitle, ToolsVisible: _pubCan,
                ToolsLoading: _pubLoading, ToolsAction: _pubCan ? _loadMore : null));
        }

        // ── pure helpers ───────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Pageable only when the view names a real server row: a seed or a client-minted identity has no endpoint.</summary>
        static bool CanPage(HomeSectionView section)
            => section.Slot > Table.None && section.Uri is { Length: > 0 } uri && !HomeSectionRoutes.IsLocal(uri);

        static string RouteTitle(Props p) => p.Route.Arg.IsEmpty ? "" : Entities.Strings.Resolve(p.Route.Arg);

        /// <summary>The masthead title (0.2.9 <c>SectionTitle</c>): the section's own — the seed's is the route's arg, or a
        /// single space when the route carries none, which the band paints as the bare family crumb until the row lands
        /// (ch 12 parity 72) — else the route's arg, else its first card's, else the family word (parity 73).</summary>
        static string SectionTitle(HomeSectionView section, Props p)
        {
            if (section.Title is { Length: > 0 } title) return title;
            if (RouteTitle(p) is { Length: > 0 } arg) return arg;
            if (section.Cards.Count > 0 && section.Cards[0].Title is { Length: > 0 } first) return first;
            return Loc.Get(Strings.Browse.Title);
        }

        /// <summary>The first frame's state. A band the tables already hold (a Fold tile's section, a chart deck row) starts
        /// READY with its cards painted — never an empty-grid flash before the sync effect runs (ch 10 parity 42); an
        /// unknown one starts on the seed; an expired local identity starts on its empty state.</summary>
        Loadable<HomeSectionView> InitialState(Props p, string uri, bool expired)
        {
            if (expired)
                return Loadable<HomeSectionView>.Ready(
                    new HomeSectionView(Table.None, null, RouteTitle(p), null, Array.Empty<HomeCard>(), 0, 0));
            var s = RowOf(p);
            if (s.IsValid && HomeSectionView.Of(s) is var view && (s.Knows(SectionFields.Identity) || view.Cards.Count > 0))
            {
                _lastView = view;
                return Loadable<HomeSectionView>.Ready(view);
            }
            return Loadable<HomeSectionView>.Pending(SeedView(uri, p));
        }

        /// <summary>The loading shape: the route's title and eight blank cards at the grid's own geometry.</summary>
        static HomeSectionView SeedView(string uri, Props p)
        {
            var cards = new HomeCard[8];
            for (int i = 0; i < cards.Length; i++) cards[i] = HomeCard.Blank(HomeCardKind.Playlist, i);
            string title = RouteTitle(p);
            return new HomeSectionView(Table.None, uri, title.Length > 0 ? title : " ", null, cards, 8, 8);
        }
    }
}
