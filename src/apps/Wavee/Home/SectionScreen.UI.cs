// ── Home/SectionScreen.UI.cs — the "See all" / charts drill page ──────────────────────────────────────────────────
//
// A PAGE DEMANDS ITS WHOLE MODEL; THE QUERY LAYER PAGES IT (the "no page-side fetch windows" rule). The page asks the
// section row's `SectionFields.Identity | Whole` once (`Home.EnsureSection`); the section's own route walks
// `sectionItems.pagingInfo.nextOffset` to the server's end and lands the whole card list in one run
// (Spotify/Spotify.Api.Browse.cs — `homeSection` and `browseSection` alike, Charts included). The grid renders what the
// row holds; while the walk is in flight the region shows its skeleton (`SectionScreenLoadRule`, Home/SectionScreen.Rules.cs).
// No near-tail hook, no append, no walk bar — nothing here ever asks for a page.
//
// Wave 2E (home-redesign-remediation.md F9/§3.6): the grid is `ItemsView.CreateBound` over `RepeatLayout.GridFit` —
// no measured width, no frozen template, no hand-rolled tier table (`GridFit.Columns6`/`CoverWidth` are gone from
// this file). Each cell is the app's ONE media surface (#157): `Controls.BoundSurface(item, Shape.Grid, …)` over
// `HomeCards.GridCardData` — the plate, hover, play FAB, "…", menu, drag, circle and trim tooltip every other card has,
// with the slot root owning invoke and the roving tab stop. A blank seed is the surface's own seed face.
//
// Role: UI
// Owner: B5
// Wave: 3 (remediated Wave 2E; shared media surface wave 1a)
// Spec: docs/plans/wavee/home-redesign-remediation.md §3.6 (`SectionScreen.UI.cs`), F9, Appendix L (loc keys);
//       docs/plans/wavee/shared-media-surface-implementation.md Appendix A.4.

using System;
using System.Collections.Generic;
using System.Linq;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using Wavee;

namespace Wavee.HomeUi;

/// <summary>The "See all" drill page for one section — a Home source section (<c>home-section:</c>) or a Browse
/// section (<c>browse-section:</c>, <paramref name="Browse"/> = true). It demands the section WHOLE and renders what the
/// row holds; the query layer walks every page (file header).</summary>
public sealed class SectionScreen : Component
{
    public sealed record Props(string Uri, string? Title, bool Browse, Shell.Route Route);

    /// <summary>Route-constructible factory (plan: "expose a static <c>Element For(...)</c>-style factory"). C1
    /// (Wave 4) wires this from <c>Shell.SetPage(RouteKind.HomeSection / .BrowseSection, …)</c> — this file does not
    /// touch <c>Shell.cs</c>. <paramref name="browse"/> selects the family: <c>false</c> pages through
    /// <c>Entities.Section</c>/<see cref="Home.EnsureSection"/>(browse: false) (a <c>home-section:</c> route),
    /// <c>true</c> through <c>Entities.BrowseSection</c> (a <c>browse-section:</c> route, where Charts also lives).
    /// <paramref name="title"/> is the hero text (the route's <c>Arg</c>, <see cref="SectionScreenTitle"/>); without
    /// one the row's own title shows once it lands. <paramref name="route"/> is the page's own route — the masthead
    /// publication's key.</summary>
    public static Element For(string sectionUri, string? title, Shell.Route route, bool browse = false)
        => Embed.Comp(new Props(sectionUri ?? "", title, browse, route), () => new SectionScreen());

    // The grid's fit knobs: a MIN cell width (RepeatLayout.GridFit derives the column count from it and the engine's
    // own measured cross size — no app-side width/tier math) and a row-height ESTIMATE (the seed before the first
    // row measures; GridFit corrects to the real measured height itself, this never pins it). The estimate is the
    // surface's own geometry, never a local sum: an estimate that drifts from the rendered card re-pins the scroll.
    const float SectionCardMinWidth = 176f;
    static readonly float SectionCardRowEstimate = SurfaceGeometry.GridRowEstimate(SectionCardMinWidth, Shape.Grid, hasSubtitle: true);

    readonly Signal<IReadOnlyList<HomeCard>> _cardsSig = new(SeedCards);
    // The page's state, written ONLY by Sync (a signal effect) and read reactively: the region's Pending/Failed/Content
    // read _load (the walk landing, or a seal that lands while the page is open, swaps the skeleton for the grid / the
    // error card; a revisit reads it on Sync's eager first run), the header reads the live title and total.
    readonly Signal<SectionScreenLoad> _load = new(SectionScreenLoad.Pending);
    readonly Signal<string?> _liveTitle = new(null);
    readonly Signal<int> _total = new(0);
    readonly BoundItemsSource<HomeCard> _cardsSource;
    readonly Func<bool> _pendingFn, _failedFn;
    readonly Func<Element> _contentFn, _failedPanelFn;
    readonly Action _demand, _sync, _retry, _publish;
    readonly Func<BoundItemScope<HomeCard>, Element> _cardTemplate;
    readonly Func<HomeCard, Controls.CardData?> _adapt;
    readonly Action<int, HomeCard> _onInvoked;
    readonly Func<int, HomeCard, bool> _isEnabled;

    Props? _p;
    HomeCard[] _cards = [];
    IReadOnlyList<HomeCard>? _cardsSrc;
    string _scrollScope = "";
    string _title = "";
    bool _demanded;
    // Read by the adapter at SLOT render time (after this page's render): the overlay that opens a card's menu (Render
    // reads it from context) and whether the section is a chart (Sync, beside the title and total — a chart's cards
    // carry no subtitle).
    IOverlayService? _overlay;
    bool _charts;

    /// <summary>What the page demands of its section row: the band's identity and its WHOLE card list — the query layer's
    /// walk (Spotify.Api.Browse.cs). Also what the error card's Retry re-asks.</summary>
    const SectionFields Demanded = SectionFields.Identity | SectionFields.Whole;

    public SectionScreen()
    {
        _demand = Demand;
        _sync = Sync;
        _publish = Publish;
        _pendingFn = () => _load.Value == SectionScreenLoad.Pending;
        _failedFn = () => _load.Value == SectionScreenLoad.Failed;
        _contentFn = BodyContent;
        _retry = Retry;
        _failedPanelFn = () => Controls.Vacancy(Controls.VacancyVoice.Error, onAction: _retry);
        _cardsSource = BoundItems.From(_cardsSig, HomeCard.Blank());
        _cardTemplate = CardSlot;
        _adapt = c => HomeCards.GridCardData(in c, _overlay, _charts);
        _onInvoked = static (_, card) => OpenCard(card);
        _isEnabled = static (_, card) => !card.IsBlank;
    }

    public override Element Render()
    {
        var p = UseProps<Props>();
        _p = p;
        _scrollScope = UseContext(Shell.PageScrollScope);
        _overlay = UseContext(Overlay.Service);

        UseEffect(_demand, DepKey.From(StringComparer.Ordinal.GetHashCode(p.Uri) ^ (p.Browse ? 1 : 0)));
        UseSignalEffect(_sync);

        // The title is the masthead's (the overlay band paints the route's arg, else what Publish says): this page owns no
        // header of its own, only the reserve under the band and a thin "N items" meta row.
        _title = SectionScreenTitle.Of(p.Title, _liveTitle.Value);
        UseEffect(_publish, DepKey.From(StringComparer.Ordinal.GetHashCode(_title)));
        int total = _total.Value;

        return new BoxEl
        {
            Key = "home-section-body", Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Gap = 4f,
            Padding = BrowseMastheadMetrics.FamilyBodyPad(Spacing.XXL),
            Children =
            [
                total > 0
                    ? Design.Type.TrackMeta(Strings.Home.SectionItems(total))
                    : new BoxEl(),
                new BoxEl { Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1, Children = [Body()] },
            ],
        };
    }

    /// <summary>One publication per title change (as <c>CategoryPage</c>): a route with no arg — a deep link, a history
    /// entry — still gets its title from the row once it lands, so the masthead never falls back to "Home".</summary>
    void Publish()
    {
        if (_p is not { } p) return;
        Shell.Mastheads.Publish(p.Route, new Shell.MastheadPublication(_title.Length > 0 ? _title : null));
    }

    // ── the shimmer/content/failed boundary (Skel — Pending until the whole section has landed) ────────────────────

    Element Body() => new SkelRegionEl(
        Pending: _pendingFn,
        Failed: _failedFn,
        Content: _contentFn,
        ShimmerSource: null,   // Content(seed) IS the shimmer: while Pending, _cardsSig still holds SeedCards ⇒ blank cards
        OnFailed: _failedPanelFn,
        Reveal: SkelReveal.StaggerRows,
        Style: SkeletonStyle.Default,
        Group: this,
        SmoothResize: false);

    Element BodyContent()
    {
        if (_load.Value == SectionScreenLoad.Empty) return Controls.Vacancy(Controls.VacancyVoice.Empty);
        return Grid();
    }

    static readonly HomeCard[] SeedCards =
    [
        HomeCard.Blank(HomeCardKind.Playlist, 0), HomeCard.Blank(HomeCardKind.Album, 1),
        HomeCard.Blank(HomeCardKind.Playlist, 2), HomeCard.Blank(HomeCardKind.Artist, 3),
        HomeCard.Blank(HomeCardKind.Playlist, 4), HomeCard.Blank(HomeCardKind.Album, 5),
    ];

    /// <summary>The grid: a BOUND <c>ItemsView</c> over <c>RepeatLayout.GridFit</c> (F9) — the column count and cell
    /// width come from the engine's own measured cross size and never from an app-side width read, so there is no
    /// tier flip and no frozen-at-first-measure column count. <see cref="_cardsSource"/> is the reactive item source
    /// (<see cref="_cardsSig"/> starts at <see cref="SeedCards"/> and Sync swaps it to the live row's cards once the
    /// section's WHOLE list has landed); the row template (<see cref="CardSlot"/>) runs ONCE per recycled slot and
    /// reads the bound item reactively, so a later answer (a feed refresh re-landing the band) just re-skins the slots
    /// the source already realized — no remount, no re-render of this component. The grid never asks for anything: the
    /// row holds the whole section (file header). No content-type pools: a circle is <c>CardData.Circular</c>, which
    /// the one surface re-renders for, so an artist and a playlist share every slot.</summary>
    Element Grid() => ItemsView.CreateBound(_cardsSource, _cardTemplate,
        RepeatLayout.GridFit(SectionCardMinWidth, Spacing.Card, SectionCardRowEstimate),
        new ListOptions<HomeCard>
        {
            Grow = 1f, SelectionMode = ItemsSelectionMode.None, Selector = SelectorVisual.None,
            IsItemInvokedEnabled = true, OnInvokedTyped = _onInvoked, IsItemEnabledTyped = _isEnabled,
            Scroll = new ScrollOptions { ScrollKey = _scrollScope + "home:section:" + (_p?.Uri ?? "") },
        });

    /// <summary>The bound cell: the app's ONE surface inside the slot root (<see cref="Controls.BoundSurface{T}"/> — the
    /// root owns press/Enter/Space through the row's <see cref="RowScope.OnInteraction"/> and the roving tab stop, F21's
    /// contract). The adapter (<see cref="HomeCards.GridCardData"/>) runs in the slot's render, so a hydrating title or
    /// cover re-describes the card. A blank seed renders the surface's seed face and is disabled (not invocable, dimmed)
    /// through <c>ListOptions&lt;HomeCard&gt;.IsItemEnabledTyped</c>.</summary>
    Element CardSlot(BoundItemScope<HomeCard> item) => Controls.BoundSurface(in item, Shape.Grid, _adapt);

    // ── the demand + the table sync ────────────────────────────────────────────────────────────────────────────────

    Section RowOf(Props p)
    {
        if (string.IsNullOrEmpty(p.Uri)) return default;
        return p.Browse ? Entities.BrowseSection(p.Uri) : Entities.Section(p.Uri);
    }

    /// <summary>THE page's demand: the section row's identity and its WHOLE card list (<see cref="Demanded"/>) — the
    /// query layer walks the section to its end and lands it in one run. The page never asks for a page.</summary>
    void Demand()
    {
        if (_p is not { } p) return;
        var s = RowOf(p);
        if (!s.IsValid) return;
        _demanded = true;
        Home.EnsureSection(s, p.Browse, Demanded);
    }

    /// <summary>The error card's Retry: ask the row's identity and whole list AGAIN whatever the scope sealed —
    /// <see cref="Demand"/>'s plain ensure no-ops on a row already Asked-and-unanswered, which is exactly the Failed
    /// shape; only <see cref="Entities.Refresh"/> un-asks it (Browse's Charts band / category page rule). The re-ask is in
    /// flight, so the region reads Pending again and follows the answer.</summary>
    void Retry()
    {
        if (_p is not { } p) return;
        var s = RowOf(p);
        if (!s.IsValid) return;
        int slot = s.Slot;
        Entities.Refresh(Entities.Current.Sections, new ReadOnlySpan<int>(in slot), (uint)Demanded);
    }

    /// <summary>The table sync: the header follows the row's title and total as soon as it has an identity (a feed band's,
    /// before the walk lands); the GRID follows only the WHOLE list — a feed's or a page's first page is never shown as
    /// the drill; and the page's load state (<see cref="SectionScreenLoadRule"/>) is decided on EVERY run, so the region
    /// follows the walk landing — or a seal landing while the page is open — and a revisit reads the settled state on
    /// this effect's eager first run (inside the first Render).</summary>
    void Sync()
    {
        if (_p is not { } p) return;
        var scope = Entities.Current;
        _ = scope.Sections.Changed.Value;
        _ = scope.Edges.SectionCards.Changed.Value;
        // Tracked: an unanswered ask only reads as Failed while Online, so a session reaching Online re-decides.
        bool online = Spotify.Status.Value == Spotify.SessionPhase.Online;
        var s = RowOf(p);
        if (!s.IsValid)
        {
            _load.Value = SectionScreenLoadRule.Of(rowValid: false, whole: false, cardCount: 0, failed: false,
                inflight: false, asked: false, online: online);
            return;
        }

        var t = scope.Sections;
        bool whole = s.Knows(Demanded);
        // A first-page answer for the same band (a feed refresh) took the whole list back — and its ask with it
        // (Home.Commit) — after this page had demanded it: nothing is on the wire and nothing failed, so ask again, or the
        // region would wait for an answer nobody asked for.
        if (_demanded && !whole && t.Inflight[s.Slot] == 0 && (t.Asked[s.Slot] & (uint)SectionFields.Whole) == 0
            && !t.IsFailed(s.Slot, (uint)Demanded))
            Home.EnsureSection(s, p.Browse, Demanded);

        if (s.Knows(SectionFields.Identity))
        {
            var input = SectionReader.Of(s);
            _total.Value = input.TotalCount;
            _liveTitle.Value = input.Title;
            _charts = s.IsChart;    // BEFORE the cards land: the slots their swap re-renders read it
            var live = input.Cards;
            if (whole && !ReferenceEquals(_cardsSrc, live))
            {
                _cardsSrc = live;
                _cards = live as HomeCard[] ?? live.ToArray();
                _cardsSig.Value = _cards;
            }
        }

        _load.Value = SectionScreenLoadRule.Of(rowValid: true, whole: whole, cardCount: _cards.Length,
            failed: t.IsFailed(s.Slot, (uint)Demanded),
            inflight: t.Inflight[s.Slot] != 0,
            asked: (t.Asked[s.Slot] & (uint)Demanded) != 0,
            online: online);
    }

    // ── card open/play routing (the app-wide HomeCardNav, Entities/Home.Rules.cs + Entities/Browse.Cards.cs) ─────────

    static void OpenCard(HomeCard card) => HomeCardNav.Open(in card, HomeCardNav.HomeOrigin);
}
