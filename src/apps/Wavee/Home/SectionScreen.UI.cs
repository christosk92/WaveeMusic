// ── Home/SectionScreen.UI.cs — the "See all" / charts drill page ──────────────────────────────────────────────────
//
// Owner rule: NO reference to the old `HomeSectionPageView` (Entities/Home.Page.cs §6) — it was read only to learn
// the data contracts this page still rides: `SectionPaging`'s RAW-vs-deduped cursor arithmetic, the `Section` handle
// (`Entities.Section`/`Entities.BrowseSection`, `Home.EnsureSection`, `Home.RequestNextPage`), and the eager Charts
// walk's shape (ask the next offset, fold it in, stop on the cursor's own terminator — never on a fraction reaching
// 1). Every TYPE here is new; the walk-fraction/near-tail math is `Home/SectionScreen.Rules.cs`, fresh and tested.
//
// Wave 2E (home-redesign-remediation.md F9/§3.6): the grid is `ItemsView.CreateBound` over `RepeatLayout.GridFit` —
// no measured width, no frozen template, no hand-rolled tier table (`GridFit.Columns6`/`CoverWidth` are gone from
// this file). The card is a width-free bound row (`AlignSelf = Stretch`, `AspectRatio` cover) built locally as
// `SectionCard`: the plan's target (§3.3) points this at a bound `Items.GridItem` twin, but `Home/Items.UI.cs`'s
// `GridItem` still takes an explicit pixel `width` (a shelf-fed value, `PagedShelf`'s own fit) as of this wave and
// this file's remit does not extend to `Items.UI.cs` — see this file's own remarks on `SectionCard` for the
// deviation and the follow-up this leaves.
//
// Role: UI
// Owner: B5
// Wave: 3 (remediated Wave 2E)
// Spec: docs/plans/wavee/home-redesign-remediation.md §3.6 (`SectionScreen.UI.cs`), F9, Appendix L (loc keys).

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
/// section (<c>browse-section:</c>, <paramref name="Browse"/> = true). Charts (<see cref="Section.IsChart"/>, Browse
/// only) walk every page eagerly behind a determinate bar; every other section pages silently as the grid nears its
/// tail.</summary>
public sealed class SectionScreen : Component
{
    public sealed record Props(string Uri, string? Title, bool Browse);

    /// <summary>Route-constructible factory (plan: "expose a static <c>Element For(...)</c>-style factory"). C1
    /// (Wave 4) wires this from <c>Shell.SetPage(RouteKind.HomeSection / .BrowseSection, …)</c> — this file does not
    /// touch <c>Shell.cs</c>. <paramref name="browse"/> selects the family: <c>false</c> pages through
    /// <c>Entities.Section</c>/<see cref="Home.EnsureSection"/>(browse: false) (a <c>home-section:</c> route),
    /// <c>true</c> through <c>Entities.BrowseSection</c> (a <c>browse-section:</c> route, where Charts also lives).
    /// <paramref name="title"/> is the frame-one masthead text (the route's <c>Arg</c>) until the row's own title
    /// lands.</summary>
    public static Element For(string sectionUri, string? title, bool browse = false)
        => Embed.Comp(new Props(sectionUri ?? "", title, browse), () => new SectionScreen());

    // ── the eager Charts walk's tuning (ported verbatim as CONSTANTS from the old page's own numbers — not its code:
    //    the walk's SHAPE is a data-contract fact, these two knobs are just how patient it is) ──────────────────────
    const int WalkPageAssumed = 20;
    const int WalkMaxRequests = 40;
    const float WalkBarWidth = 160f;

    // The grid's fit knobs: a MIN cell width (RepeatLayout.GridFit derives the column count from it and the engine's
    // own measured cross size — no app-side width/tier math) and a row-height ESTIMATE (the seed before the first
    // row measures; GridFit corrects to the real measured height itself, this never pins it).
    const float SectionCardMinWidth = 176f;
    const float SectionCardRowEstimate = SectionCardMinWidth + 8f + 20f + 16f;   // + title line (14/20) + caption line (12/16)
    // A fixed decode bucket for the cover image: `HomeTok.CoverDecodePx` (the plan's §3.3 shared constant) isn't
    // landed yet (owned by the Items.UI.cs/Metrics.cs wave) — this literal is a decode-quality target, not a
    // layout width, so it is not the "no width math" rule's target; it stays local until that constant exists.
    const int SectionCardDecodePx = 320;

    readonly Signal<bool> _loading = new(false);
    readonly Signal<bool> _walking = new(false);
    readonly Signal<bool> _exhausted = new(false);
    readonly Signal<IReadOnlyList<HomeCard>> _cardsSig = new(SeedCards);
    readonly FloatSignal _walkFrac = new(0f);
    readonly BoundItemsSource<HomeCard> _cardsSource;
    readonly Action _demand, _sync;
    readonly Action<int, int> _onVisibleRange;
    readonly Func<BoundItemScope<HomeCard>, Element> _cardTemplate;
    readonly Action<int, HomeCard> _onInvoked;
    readonly Func<int, HomeCard, bool> _isEnabled;
    readonly Func<int, int> _contentTypeOf;

    Props? _p;
    HomeCard[] _cards = [];
    IReadOnlyList<HomeCard>? _cardsSrc;
    string? _title;
    string _scrollScope = "";
    int _total;
    bool _hasIdentity;
    bool _failed;

    // The one paging request in flight (the append preloader or a walk step): the count before it landed and the
    // row's versions at ask time — a version change is the landing, an edge failure is the miss.
    int _beforeCount = -1;
    int _requestOffset;
    uint _reqVersion, _reqCardVersion;
    bool _walkStarted, _walkDone;
    int _walkRequests;

    public SectionScreen()
    {
        _demand = Demand;
        _sync = Sync;
        _onVisibleRange = OnVisibleRange;
        _cardsSource = BoundItems.From(_cardsSig, HomeCard.Blank());
        _cardTemplate = SectionCard;
        _onInvoked = static (_, card) => OpenCard(card);
        _isEnabled = static (_, card) => !card.IsBlank;
        _contentTypeOf = ContentTypeOf;
    }

    /// <summary><c>Corners</c> (<c>BoxEl</c>/<c>ImageEl</c>) is a plain value, not a bindable <c>Prop</c> — a round
    /// Artist cover and a square Playlist/Album cover cannot both be expressed by one persistent bound slot's
    /// template. <c>ListOptions&lt;HomeCard&gt;.ContentType</c> keeps Artist cards in their own recycle pool (a
    /// cross-type reuse REBUILDS the slot instead of rebinding it — the same shape the Recents flat list uses for
    /// its own heterogeneous rows), so <see cref="SectionCard"/> can bake the corner radius once, at build time,
    /// from whichever item the fresh slot was built for.</summary>
    int ContentTypeOf(int index) => _cardsSource.TryPeek(index, out var c) && c.Kind == HomeCardKind.Artist ? 1 : 0;

    public override Element Render()
    {
        var p = UseProps<Props>();
        _p = p;
        _scrollScope = UseContext(Shell.PageScrollScope);

        UseEffect(_demand, DepKey.From(StringComparer.Ordinal.GetHashCode(p.Uri) ^ (p.Browse ? 1 : 0)));
        UseSignalEffect(_sync);

        var section = RowOf(p);
        bool chart = p.Browse && section.IsValid && section.IsChart;
        bool walking = _walking.Value;
        string title = _title is { Length: > 0 } live ? live : (p.Title ?? "");

        return new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
            Children =
            [
                Header(title, chart, walking),
                new BoxEl
                {
                    Key = "home-section-body", Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                    Padding = new Edges4(Left: Spacing.XXXL, Top: 0f, Right: Spacing.XXXL, Bottom: Spacing.XXL),
                    Children = [Body()],
                },
            ],
        };
    }

    // ── header: title 28/36, subtitle, the charts walk bar's PERMANENT slot ────────────────────────────────────────

    Element Header(string title, bool chart, bool walking) => new BoxEl
    {
        Key = "home-section-header", Direction = 1, Gap = 4f, Shrink = 0f,
        Padding = new Edges4(Left: Spacing.XXXL, Top: Spacing.XXL, Right: Spacing.XXXL, Bottom: 0f),
        Children =
        [
            Design.Type.PageHero(title) with { Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1 },
            _total > 0
                ? Design.Type.TrackMeta(Strings.Home.SectionItems(_total))
                : new BoxEl(),
            chart
                ? new BoxEl
                {
                    Key = "home-section-walk", Height = 3f, Width = WalkBarWidth,
                    Shrink = 0f, AlignSelf = FlexAlign.Start,
                    Children = walking ? [ProgressBar.Create(_walkFrac, width: WalkBarWidth)] : Array.Empty<Element>(),
                }
                : new BoxEl(),
        ],
    };

    // ── the shimmer/content/failed boundary (Skel — first load only; a landed append never re-shimmers) ────────────

    Element Body() => new SkelRegionEl(
        Pending: () => !_hasIdentity && !_failed,
        Failed: () => _failed,
        Content: BodyContent,
        ShimmerSource: null,   // Content(seed) IS the shimmer: while Pending, _hasIdentity is false ⇒ blank cards
        OnFailed: static () => Controls.Vacancy(Controls.VacancyVoice.Error),
        Reveal: SkelReveal.StaggerRows,
        Style: SkeletonStyle.Default,
        Group: this,
        SmoothResize: false);

    Element BodyContent()
    {
        if (_hasIdentity && _cards.Length == 0) return Controls.Vacancy(Controls.VacancyVoice.Empty);
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
    /// section has identity); the row template (<see cref="SectionCard"/>) runs ONCE per recycled slot and reads the
    /// bound item reactively, so a page landing after mount just re-skins the slots the source already realized —
    /// no remount, no re-render of this component. <see cref="_onVisibleRange"/>/<c>Scroll.ScrollKey</c> are wired
    /// unconditionally (the props-freeze contract: <c>ItemsView.CreateBound</c> is itself a single-arg
    /// <c>Embed.Comp</c> factory, so anything decided from a mount-time flag here would freeze forever); the guard
    /// against paging over the not-yet-identified seed lives inside <see cref="OnVisibleRange"/> itself.</summary>
    Element Grid() => ItemsView.CreateBound(_cardsSource, _cardTemplate,
        RepeatLayout.GridFit(SectionCardMinWidth, Spacing.Card, SectionCardRowEstimate),
        new ListOptions<HomeCard>
        {
            Grow = 1f, SelectionMode = ItemsSelectionMode.None, Selector = SelectorVisual.None,
            IsItemInvokedEnabled = true, OnInvokedTyped = _onInvoked, IsItemEnabledTyped = _isEnabled,
            ContentType = _contentTypeOf,
            Scroll = new ScrollOptions { ScrollKey = _scrollScope + "home:section:" + (_p?.Uri ?? "") },
            OnVisibleRange = _onVisibleRange,
        });

    /// <summary>The bound cell: a square <c>AspectRatio</c> cover (no pixel width — it fills whatever cell
    /// <c>GridFit</c> arranges it into) + a one-line title + an optional one-line caption, keyed by the card's uri.
    /// This is the plan's §3.3 <c>Items.SectionCard</c> shape (bound twin of <c>GridItem</c>) built LOCALLY: this
    /// wave's remit is this file only, and <c>Home/Items.UI.cs</c>'s current <c>GridItem</c> takes an explicit pixel
    /// <c>width</c> fed by <c>PagedShelf</c>'s own fit (a shelf card, not an <c>ItemsView</c> bound row) — reusing it
    /// here would mean computing a width from the grid's arranged cell, which no <c>BoundItemScope</c> template
    /// receives and which the "no width math" rule forbids reintroducing. <see cref="SelectorVisualsBound.None"/>
    /// wires press/Enter/Space/focus through the row's own <see cref="RowScope.OnInteraction"/> (F21's contract —
    /// the slot root owns invoke, not a hand-rolled <c>OnClick</c>/<c>Focusable</c>/<c>Cursor</c>); a blank seed card
    /// is disabled (not invocable) through <c>ListOptions&lt;HomeCard&gt;.IsItemEnabledTyped</c>, which also dims it,
    /// so it needs no separate "ready" branch here.</summary>
    Element SectionCard(BoundItemScope<HomeCard> item)
    {
        // Corners is a plain (non-bindable) value — safe to bake once here because ContentTypeOf keeps Artist cards
        // in their own recycle pool, so this slot only ever rebinds within one shape (see ContentTypeOf's remarks).
        var corners = item.Item.Peek().Kind == HomeCardKind.Artist ? Radii.FullAll : CornerRadius4.All(Radii.Control);
        var cover = new BoxEl
        {
            AlignSelf = FlexAlign.Stretch, AspectRatio = 1f, ClipToBounds = true, Corners = corners,
            Children =
            [
                new ImageEl
                {
                    Source = item.Image(static c => c.ImageUrl), Fit = ImageFit.Cover, AspectRatio = 1f,
                    DecodePx = SectionCardDecodePx, Corners = corners,
                    Placeholder = item.Value(static c => Design.PlaceholderFor((c.ImageUrl ?? "").AsSpan())),
                },
            ],
        };
        var title = Design.Type.CardTitle("") with
        {
            Text = item.Text(static c => c.IsBlank ? "" : c.Title),
            MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
        };
        var caption = Design.Type.TrackMeta("") with
        {
            Text = item.Text(static c => c.Subtitle ?? ""),
            MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
            Visible = item.Show(static c => c.Subtitle is { Length: > 0 }),
        };
        var content = new BoxEl
        {
            Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Gap = 4f,
            Corners = CornerRadius4.All(Radii.Card),
            Children = [cover, title, caption],
        };
        return SelectorVisualsBound.None(item.Row, content);
    }

    // ── demand + the table sync (the paging/walk state machine) ────────────────────────────────────────────────────

    Section RowOf(Props p)
    {
        if (string.IsNullOrEmpty(p.Uri)) return default;
        return p.Browse ? Entities.BrowseSection(p.Uri) : Entities.Section(p.Uri);
    }

    void Demand()
    {
        if (_p is not { } p) return;
        var s = RowOf(p);
        if (s.IsValid) Home.EnsureSection(s, p.Browse);
    }

    void Sync()
    {
        if (_p is not { } p) return;
        _ = Entities.Current.Sections.Changed.Value;
        _ = Entities.Current.Edges.SectionCards.Changed.Value;
        var s = RowOf(p);
        if (!s.IsValid) return;

        bool hasIdentity = s.Knows(SectionFields.Identity) || s.Cards > 0;
        if (hasIdentity)
        {
            _hasIdentity = true;
            _failed = false;
            var input = SectionReader.Of(s);
            var live = input.Cards;
            if (!ReferenceEquals(_cardsSrc, live))
            {
                _cardsSrc = live;
                _cards = live as HomeCard[] ?? live.ToArray();
                _cardsSig.Value = _cards;
            }
            _total = input.TotalCount;
            _title = input.Title;

            if (p.Browse && s.IsChart) Walk(s);
            else LandIfPending(s);
            return;
        }

        var t = Entities.Current.Sections;
        bool askFailed = Entities.Current.Edges.SectionCards.Readiness(s.Slot) == EdgeState.Failed
            || (t.Inflight[s.Slot] == 0 && (t.Asked[s.Slot] & (uint)SectionFields.Identity) != 0
                && Spotify.Status.Value == Spotify.SessionPhase.Online);
        if (askFailed) _failed = true;
    }

    (bool Landed, bool Failed) Outcome(Section s)
        => (s.Version != _reqVersion || s.CardVersion != _reqCardVersion, Entities.Current.Edges.SectionCards.IsFailed(s.Slot));

    /// <summary>An append landed or missed. Exhausted is latched off the cursor's own arithmetic (never the total).</summary>
    void LandIfPending(Section s)
    {
        if (!_loading.Peek()) return;
        var (landed, failed) = Outcome(s);
        if (!landed && !failed) return;
        _loading.Value = false;
        if (!landed)
        {
            Log.Warn("home", "home.section.page.fail uri=" + s.Id.Text);
            return;
        }
        if (!SectionPaging.CanAdvance(_requestOffset, s.NextOffset)) _exhausted.Value = true;
    }

    /// <summary>The grid's near-tail silent auto-page (non-chart sections). Charts never calls this — its own eager
    /// walk (<see cref="Walk"/>) owns every request. Guards on <see cref="_hasIdentity"/> itself (rather than being
    /// wired conditionally at <see cref="Grid"/>'s mount) because <c>ItemsView.CreateBound</c>'s options freeze at
    /// mount — a delegate wired only once identity has landed would never be wired at all for a page whose row was
    /// already known when this component first mounted, and would stay wired forever once landed either way.</summary>
    void OnVisibleRange(int first, int lastExclusive)
    {
        if (_p is not { } p || !_hasIdentity) return;
        var s = RowOf(p);
        bool chart = p.Browse && s.IsValid && s.IsChart;
        if (!SectionScreenRules.CanAutoPage(chart, s.IsValid && s.HasMore, _loading.Peek(), _exhausted.Peek())) return;
        if (!SectionScreenRules.NearTail(lastExclusive, _cards.Length)) return;
        LoadMore(s, p.Browse);
    }

    void LoadMore(Section s, bool browse)
    {
        if (!s.IsValid || _loading.Peek() || _exhausted.Peek() || !s.HasMore) return;
        int offset = s.NextRequest;
        uint v = s.Version, cv = s.CardVersion;
        if (!Home.RequestNextPage(s, browse)) { _exhausted.Value = true; return; }
        _requestOffset = offset;
        _reqVersion = v;
        _reqCardVersion = cv;
        _loading.Value = true;
    }

    /// <summary>The eager Charts walk: publish what the row holds, ask for the next offset, fold each landed page
    /// (the table already folds it — this just watches the version move), stop on the section's own cursor
    /// terminator, a miss, or the request backstop.</summary>
    void Walk(Section s)
    {
        if (_walkDone) return;
        if (!_walkStarted)
        {
            _walkStarted = true;
            if (!s.HasMore) { FinishWalk(); return; }
            _walking.Value = true;
            _walkFrac.Value = SectionWalk.Fraction(_cards.Length, _total, WalkPageAssumed);
        }
        if (_loading.Peek())
        {
            var (landed, failed) = Outcome(s);
            if (!landed && !failed) return;
            _loading.Value = false;
            int before = _beforeCount;
            _beforeCount = -1;
            if (!landed)
            {
                Log.Warn("home", "home.section.walk.fail uri=" + s.Id.Text + " requests="
                    + _walkRequests.ToString(System.Globalization.CultureInfo.InvariantCulture));
                FinishWalk();
                return;
            }
            bool progressed = before < 0 || _cards.Length > before;
            bool canAdvance = SectionPaging.CanAdvance(_requestOffset, s.NextOffset);
            if (!canAdvance || !progressed) { FinishWalk(); return; }
            _walkFrac.Value = SectionWalk.Fraction(_cards.Length, _total, WalkPageAssumed);
        }
        if (++_walkRequests > WalkMaxRequests)
        {
            Log.Warn("home", "home.section.walk.capped uri=" + s.Id.Text + "; the section is shown truncated");
            FinishWalk();
            return;
        }
        int offset = s.NextRequest;
        uint v = s.Version, cv = s.CardVersion;
        if (!Home.RequestNextPage(s, browse: true)) { FinishWalk(); return; }
        _beforeCount = _cards.Length;
        _requestOffset = offset;
        _reqVersion = v;
        _reqCardVersion = cv;
        _loading.Value = true;
    }

    void FinishWalk()
    {
        _walkDone = true;
        _walking.Value = false;
        _walkFrac.Value = 1f;
        _exhausted.Value = true;
    }

    // ── card open/play routing (the app-wide HomeCardNav, Entities/Home.Rules.cs + Entities/Browse.Cards.cs) ─────────

    static void OpenCard(HomeCard card) => HomeCardNav.Open(in card, HomeCardNav.HomeOrigin);
}
