// ── Themes/Classic/Structure/Views/Pages/ClassicConcertSchedule.cs — Classic's `Pages.ArtistConcerts` (themes plan 6.1, T10b3) ────────────────────
//
// From `Entities\Concert.Page.cs` §7 (`SchedulePage.Dashboard`, `Hero`, `TourDates`, `BoardState`, `MonthBoard`, `ShyMonthPill`, `LingerClock`; 0.2.9
// `ArtistSchedulePage`): the artist's tour dashboard. The split hero owns the next show; ONE month at a time renders in the tour-dates card, remembered by its
// `yyyy-MM` key; the shy month pill floats over the viewport. `Concert.ScheduleHost` (core) keeps the demand, the readiness verdicts, the selected month and the
// shaping (`ConcertScheduleModel`: spotlight, month groups with their runs, the near-you guard, stats); this file draws it at the measured width.
//
// THE WIDTH IS THE PAGE'S OWN: the dashboard is built inside `Responsive.Of`, and the 760 / 720 hysteresis (`ClassicConcertTiers.ScheduleWide`) is answered
// from that width with the latch kept in one hook cell of the view (the skeleton's seed build shares it, as the old page's fields did).
//
// VIEW STATE (the page-owned state the boards and the pill share, §1.4 traps): the expansion sets survive a board's keyed remount; the tracker is read
// imperatively through `SceneStore.AbsoluteRect`, never through an ancestor. Nothing ticks at rest: the linger clock exists only while the pill shows.

using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using Cells = Wavee.Themes.Classic.ClassicConcertCells;
using Rules = Wavee.Themes.Classic.ClassicConcertRules;

namespace Wavee.Themes.Classic;

/// <summary>Page-owned state the boards and the shy pill share: the expansion sets survive a board's keyed remount; the tracker is read imperatively through
/// <c>SceneStore.AbsoluteRect</c>, never through an ancestor.</summary>
internal sealed class ClassicConcertBoardState
{
    public readonly HashSet<string> Months = new(StringComparer.Ordinal), Runs = new(StringComparer.Ordinal);
    public readonly Signal<int> Revision = new(0);
    public NodeHandle Viewport, PanelHeader;
    public string Label = "";
    public void Bump() => Revision.Value = Revision.Peek() + 1;
}

/// <summary>Wavee Classic's artist tour dashboard.</summary>
internal static class ClassicConcertSchedule
{
    /// <summary>The view's one hook cell: the shared board state, the viewport's scroll handle, the width latch and the stable factories and callbacks (a fresh
    /// delegate would re-arm what holds it).</summary>
    sealed class Cell
    {
        public readonly ClassicConcertBoardState Board = new();
        public readonly ScrollHandle Handle = new();
        public readonly Func<ClassicConcertMonthBoard> MonthFactory;
        public readonly Func<ClassicConcertShyMonthPill> PillFactory;
        public readonly Action<NodeHandle> CaptureViewport;
        public bool WasWide, WideInit;

        public Cell()
        {
            MonthFactory = () => new ClassicConcertMonthBoard(Board);
            PillFactory = () => new ClassicConcertShyMonthPill(Board, Handle);
            CaptureViewport = h => Board.Viewport = h;
        }
    }

    /// <summary>The registered template of <see cref="ViewKeys.ArtistConcerts"/>.</summary>
    public static Element Template(ConcertScheduleProps props, in ViewContext ctx)
    {
        var cell = ctx.Hooks.UseRef(new Cell()).Value;
        Func<float, Element> build = w => Dashboard(props, cell, props.Model(), Wide(cell, w));
        Func<float, Element> buildSeed = w => Dashboard(props, cell, props.Seed(), Wide(cell, w));
        Func<Element> content = () => props.Empty
            ? Controls.Vacancy(Controls.VacancyVoice.Empty, title: Loc.Get(Strings.Concerts.Schedule.NoUpcoming),
                subtitle: Loc.Get(Strings.Concerts.Schedule.NoUpcomingSubtitle))
            : Responsive.Of(build, fallback: Rules.ScheduleFallbackWidth);
        Element region = Cells.Region(props.Pending, props.Failed, content, () => Responsive.Of(buildSeed, fallback: Rules.ScheduleFallbackWidth),
            () => Controls.Vacancy(Controls.VacancyVoice.Error, onAction: props.Retry), "artist-schedule:" + props.RouteKey);
        Element scroll = Cells.SubPageScroll(props.ScrollScope + "artist-schedule:" + props.RouteKey, region) with
        {
            Handle = cell.Handle, OnRealized = cell.CaptureViewport,
        };
        return new BoxEl
        {
            Grow = 1f, MinHeight = 0f, ZStack = true,
            Children = [scroll, Embed.Comp(cell.PillFactory) with { Key = "shy-month:" + props.RouteKey }],
        };
    }

    /// <summary>The 760 / 720 hysteresis held in the view's cell.</summary>
    static bool Wide(Cell cell, float width)
    {
        bool wide = ClassicConcertTiers.ScheduleWide(width, cell.WasWide, cell.WideInit);
        cell.WasWide = wide;
        cell.WideInit = true;
        return wide;
    }

    /// <summary>The loaded (or seeded) dashboard at a measured width.</summary>
    static Element Dashboard(ConcertScheduleProps props, Cell cell, ConcertScheduleModel m, bool wide)
    {
        var sections = new List<Element>(2) { Hero(props, m, wide) };
        if (m.Selected >= 0)
        {
            var group = m.Groups[m.Selected];
            if (!m.Seed)
                cell.Board.Label = new DateTime(group.Year, group.Month, 1).ToString("MMMM yyyy", CultureInfo.CurrentCulture)
                                 + " · " + Strings.Concerts.Schedule.ShowCount(group.ShowCount);
            sections.Add(TourDates(props, cell, m, wide));
        }
        else
        {
            if (!m.Seed) { cell.Board.Label = ""; cell.Board.PanelHeader = NodeHandle.Null; }
            // W18: the hero rendered but no month remains (the one show on sale IS the spotlight).
            sections.Add(new BoxEl
            {
                Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Center,
                Padding = new Edges4(Spacing.XL, Spacing.XXL, Spacing.XL, Spacing.XXL),
                Corners = Radii.CardAll, Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                Children =
                [
                    Controls.Vacancy(Controls.VacancyVoice.Empty, title: Loc.Get(Strings.Concerts.Schedule.NoMoreDates),
                        subtitle: Loc.Get(Strings.Concerts.Schedule.NoMoreDatesSubtitle),
                        actionLabel: Loc.Get(Strings.Concerts.Location.Set), onAction: props.OpenPicker),
                ],
            });
        }
        return new BoxEl { Direction = 1, MinWidth = 0f, Gap = Spacing.XL, Children = sections.ToArray() };
    }

    /// <summary>W13: "On tour" · the name (≤ 2 lines) · the stats line; with a spotlight a hairline, the next-show row
    /// and "View event" beside a 220-wide location button (stacked when narrow). Without one (W13b) the location button
    /// stands alone at full width and the media pane is untinted.</summary>
    static Element Hero(ConcertScheduleProps props, ConcertScheduleModel m, bool wide)
    {
        var blocks = new List<Element>(6)
        {
            Cells.SectionCaption(Loc.Get(Strings.Concerts.Schedule.OnTour)),
            R.Type.PageHero(m.Name) with { Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis },
            Body(ConcertScheduleShaping.StatsLine(m.Stats)) with { Color = Tok.TextSecondary, MinWidth = 0f, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
        };
        if (m.Spotlight is { } next)
        {
            var text = ConcertScheduleShaping.TileText(next, m.Name);
            blocks.Add(new BoxEl { Height = 1f, MinWidth = 0f, Fill = Tok.StrokeDividerDefault });
            blocks.Add(new BoxEl
            {
                Direction = 0, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                Children =
                [
                    Cells.DateBlock(next.Date),
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XS,
                        Children =
                        [
                            Cells.SectionCaption(Strings.Concerts.Schedule.NextShow(ConcertScheduleShaping.RelativeTime(next.Date, m.Now))),
                            R.Type.TrackTitle(text.Primary) with { MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                            R.Type.TrackMeta(text.Secondary) with { MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        ],
                    },
                ],
            });
        }
        BoxEl location = Cells.LocationButton(m.LocationLabel, props.OpenPicker) with { OnRealized = props.CaptureAnchor };
        blocks.Add(m.Spotlight is null
            ? location
            : wide
                ? new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                    Children =
                    [
                        Button.Accent(Loc.Get(Strings.Concerts.Schedule.ViewEvent), props.OpenSpotlight) with { Shrink = 0f },
                        new BoxEl { Width = Rules.ScheduleLocationW, MinWidth = 0f, Children = [location] },
                    ],
                }
                : new BoxEl { Direction = 1, Gap = Spacing.S, Children = [Button.Accent(Loc.Get(Strings.Concerts.Schedule.ViewEvent), props.OpenSpotlight), location] });
        var copy = new BoxEl { Direction = 1, MinWidth = 0f, Gap = Spacing.M, Children = blocks.ToArray() };
        return Cells.SplitHero(m.HeroImageUrl, m.SpotlightAccent, copy, wide);
    }

    /// <summary>The tour-dates card: "Tour dates" 28/36/600, the month tabs in a 48-high edge-faded scroller, then the
    /// selected month's board — keyed on its composed signature (props freeze at mount, §9 #9).</summary>
    static Element TourDates(ConcertScheduleProps props, Cell cell, ConcertScheduleModel m, bool wide)
    {
        var groups = m.Groups;
        var labels = ConcertScheduleShaping.MonthTabLabels(groups, CultureInfo.CurrentCulture);
        Element tabs = ScrollView(new BoxEl
        {
            Direction = 0,
            Children = [SelectorBar.Create(labels, new Signal<int>(m.Selected), onChange: i => props.SelectMonth(groups[i].Key))],
        }, horizontal: true) with
        {
            Height = Rules.MonthTabsH, Grow = 0f, AutoEdgeFade = true, AutoEdgeFadeBand = Rules.BarEdgeFadeBand, SuppressScrollBar = true,
            ScrollKey = props.ScrollScope + "artist-schedule-months:" + props.RouteKey,
        };
        var group = groups[m.Selected];
        string signature = group.Key + ":" + group.Runs.Count.ToString(CultureInfo.InvariantCulture) + ":"
            + group.ShowCount.ToString(CultureInfo.InvariantCulture) + ":" + (group.Runs.Count > 0 ? group.Runs[0].Uri : "") + (wide ? ":w" : ":n");
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f, ClipToBounds = true, Corners = Radii.CardAll,
            Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, MinWidth = 0f, Gap = Spacing.S, Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.S),
                    Children =
                    [
                        new TextEl(Loc.Get(Strings.Concerts.Schedule.TourDates)) { Size = Rules.TourDatesSize, LineHeight = Rules.TourDatesLine, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1 },
                        tabs,
                    ],
                },
                Embed.Comp(new ClassicConcertMonthBoard.BoardProps(group, m.Nearby, m.Name, wide, m.Seed), cell.MonthFactory) with { Key = "dashboard-month:" + signature },
            ],
        };
    }
}

/// <summary>The selected month (0.2.9 <c>MonthBoard</c>): header (MinH 58, the month 20/28/600 + "N shows"), a hairline,
/// one column — or at wide two BALANCED chronological columns split by a 1-DIP divider — and, over six tiles, a
/// divider + "Show all {shows}" (§0 #10). A multi-night run is ONE tile that expands in place.</summary>
internal sealed class ClassicConcertMonthBoard(ClassicConcertBoardState state) : Component
{
    internal sealed record BoardProps(ConcertMonthGroup Group, HashSet<string> Near, string ArtistName, bool Wide, bool Seed);

    static readonly Edges4 s_focusMargin = Edges4.All(Rules.FocusMargin);

    BoardProps _p = null!;
    readonly Action<NodeHandle> _header = h => state.PanelHeader = h;

    public override Element Render()
    {
        _p = UseProps<BoardProps>();
        _ = state.Revision.Value;
        var runs = _p.Group.Runs;
        bool capped = !state.Months.Contains(_p.Group.Key) && _p.Group.OverflowsCap;
        int visible = capped ? ConcertScheduleShaping.TileCap : runs.Count;
        Element body;
        if (_p.Wide && visible > 1)
        {
            var split = ConcertScheduleShaping.BalancedColumns(runs, visible);
            body = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Start, MinWidth = 0f,
                Children =
                [
                    Column(split.Left, split.Left.Count) with { Grow = 1f, Basis = 0f },
                    new BoxEl { Width = 1f, AlignSelf = FlexAlign.Stretch, Fill = Tok.StrokeDividerDefault },
                    Column(split.Right, split.Right.Count) with { Grow = 1f, Basis = 0f },
                ],
            };
        }
        else body = Column(runs, visible);

        var culture = CultureInfo.CurrentCulture;
        var kids = new List<Element>(5)
        {
            new BoxEl
            {
                Direction = 0, MinWidth = 0f, MinHeight = Rules.BoardHeadMinH, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.M), OnRealized = _p.Seed ? null : _header,
                Children =
                [
                    new TextEl(new DateTime(_p.Group.Year, _p.Group.Month, 1).ToString("MMMM yyyy", culture))
                    { Size = Rules.BoardMonthSize, LineHeight = Rules.BoardMonthLine, Weight = 600, Color = Tok.TextPrimary, Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    Body(Strings.Concerts.Schedule.ShowCount(_p.Group.ShowCount)) with { Color = Tok.TextSecondary, Wrap = TextWrap.NoWrap, MaxLines = 1, Shrink = 0f },
                ],
            },
            Divider(), body,
        };
        if (capped)
        {
            kids.Add(Divider());
            kids.Add(new BoxEl
            {
                Direction = 0, MinWidth = 0f, MinHeight = Rules.BoardShowAllMinH, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = Spacing.XS,
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                OnClick = () => { if (state.Months.Add(_p.Group.Key)) state.Bump(); },
                Children =
                [
                    // "Show all {SHOWS}" while the cap counts TILES (parity 63).
                    BodyStrong(Strings.Concerts.Schedule.ShowAll(_p.Group.ShowCount)) with { Color = Tok.AccentTextPrimary, MaxLines = 1 },
                    Icon(Icons.ChevronDown, Rules.BoardChevron, Tok.AccentTextPrimary) with { Shrink = 0f },
                ],
            }.Interactive(Interaction.Subtle));
        }
        return new BoxEl { Direction = 1, MinWidth = 0f, Children = kids.ToArray() };
    }

    BoxEl Column(IReadOnlyList<ConcertBoardRun> runs, int count)
    {
        var kids = new List<Element>(Math.Max(0, count * 2 - 1));
        for (int i = 0; i < count; i++)
        {
            if (i > 0) kids.Add(Divider());
            var run = runs[i];
            bool near = false;
            for (int k = 0; k < run.NightCount && !near; k++) near = _p.Near.Contains(run.Nights[k].Uri);
            if (!run.IsMultiNight) kids.Add(NightTile(run.First, near));
            else if (state.Runs.Contains(run.Uri))
            {
                var nights = new List<Element>(run.NightCount * 2 - 1);
                for (int k = 0; k < run.NightCount; k++)
                {
                    if (k > 0) nights.Add(Divider());
                    nights.Add(NightTile(run.Nights[k], near));
                }
                kids.Add(new BoxEl { Key = "run:" + run.Uri, Direction = 1, MinWidth = 0f, Children = nights.ToArray() });
            }
            else kids.Add(RunTile(run, near));
        }
        return new BoxEl { Direction = 1, MinWidth = 0f, Children = kids.ToArray() };
    }

    Element NightTile(ConcertShow c, bool near)
    {
        var text = ConcertScheduleShaping.TileText(c, _p.ArtistName);
        var secondary = new List<Element>(2);
        if (near) secondary.Add(Icon(Icons.MapPin, Rules.BoardPin, Tok.AccentTextPrimary) with { Shrink = 0f });
        if (text.Secondary.Length > 0) secondary.Add(Meta(text.Secondary));
        string uri = c.Uri;
        bool seed = _p.Seed;
        return Tile(uri, near, DayColumn(c.Date.Day.ToString(CultureInfo.CurrentCulture), c.Date.ToString("ddd", CultureInfo.CurrentCulture), Rules.BoardDayW),
            text.Primary, secondary, Icons.ChevronRight,
            () => { if (!seed) ConcertVerbs.OpenConcertByUri(uri); });
    }

    /// <summary>The run tile's secondary row, in BUILT order (W15): a venue-primary run reads [pin] City [N nights]; a
    /// city-primary run reads [pin] [N nights] with …; the pin only inside the guarded near set.</summary>
    Element RunTile(ConcertBoardRun run, bool near)
    {
        var c = run.First;
        var text = ConcertScheduleShaping.TileText(c, _p.ArtistName);
        var meta = new List<Element>(4);
        if (near) meta.Add(Icon(Icons.MapPin, Rules.BoardPin, Tok.AccentTextPrimary) with { Shrink = 0f });
        meta.Add(new BoxEl
        {
            AlignSelf = FlexAlign.Center, Shrink = 0f, Padding = new Edges4(Spacing.S, 1f, Spacing.S, 1f),
            Corners = Radii.FullAll, Fill = Tok.AccentSubtle,
            Children = [Caption(ConcertCopy.Current.Nights(run.NightCount)) with { Color = Tok.AccentTextPrimary, MaxLines = 1 }],
        });
        if (text.CityIsPrimary)
        {
            if (ConcertScheduleShaping.SupportActs(c, _p.ArtistName) is { Length: > 0 } support) meta.Add(Meta(support));
        }
        else if (c.City.Length > 0) meta.Insert(near ? 1 : 0, Meta(c.City));
        var culture = CultureInfo.CurrentCulture;
        string uri = run.Uri;
        return Tile("run:" + uri, near,
            DayColumn(c.Date.Day.ToString(culture) + "–" + run.Last.Date.Day.ToString(culture),
                      c.Date.ToString("ddd", culture) + "–" + run.Last.Date.ToString("ddd", culture), Rules.BoardRunDayW),
            text.Primary, meta, Icons.ChevronDown,
            () => { if (state.Runs.Add(uri)) state.Bump(); });
    }

    /// <summary>A board tile: MinH 68, pad (12,8,12,8), gap 12 — the near rail (always laid out, transparent when not
    /// near, so widths never differ), the day column, primary/secondary lines, the trailing 16-DIP glyph.</summary>
    static Element Tile(string key, bool near, Element day, string primary, List<Element> secondary, string glyph, Action onClick) => new BoxEl
    {
        Key = key, Direction = 0, MinHeight = Rules.BoardTileMinH, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.M,
        Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.S),
        Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = s_focusMargin, Cursor = CursorId.Hand, OnClick = onClick,
        Children =
        [
            new BoxEl { Width = Rules.BoardRailW, Height = Rules.BoardRailH, Shrink = 0f, Corners = CornerRadius4.All(Rules.BoardRailRadius), Fill = near ? Tok.AccentDefault : ColorF.Transparent },
            day,
            new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Rules.BoardTextGap, Justify = FlexJustify.Center,
                Children =
                [
                    BodyStrong(primary) with { Color = Tok.TextPrimary, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    new BoxEl { Direction = 0, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.XS, Children = secondary.ToArray() },
                ],
            },
            Icon(glyph, Rules.BoardTrailing, Tok.TextSecondary) with { Shrink = 0f },
        ],
    }.Interactive(Interaction.Subtle);

    /// <summary>The growing text run: Grow 1 + Shrink 1 + MinWidth 0 over its MEASURED basis (never Basis 0, §9 #7).</summary>
    static TextEl Meta(string text)
        => Body(text) with { Color = Tok.TextSecondary, Grow = 1f, Shrink = 1f, MinWidth = 0f, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis };

    static Element DayColumn(string day, string weekday, float width) => new BoxEl
    {
        Width = width, Shrink = 0f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = 1f,
        Children =
        [
            BodyStrong(day) with { Color = Tok.TextPrimary, MaxLines = 1 },
            R.Type.Eyebrow(weekday) with { Color = Tok.TextSecondary, MaxLines = 1 },
        ],
    };

    static Element Divider() => new BoxEl { Height = 1f, MinWidth = 0f, Fill = Tok.StrokeDividerDefault };
}

/// <summary>The shy month pill (0.2.9 <c>ShyMonthPill</c>, §0 #9): a 32-DIP acrylic capsule, top-centre 12 down, shown only
/// while scrolling AND once the board header has crossed the viewport top (≤ +4 DIP), gone at once above it, lingering
/// 1 s past the last step on the ANIM clock. Never hit-testable: the scroller beneath always wins the wheel. It reads the viewport's scroll handle itself: the
/// offset on an 8-DIP write floor drives the step (the old page published that floor in a signal only this pill read).</summary>
internal sealed class ClassicConcertShyMonthPill(ClassicConcertBoardState state, ScrollHandle handle) : Component
{
    static readonly LayoutTransition s_presence = new(
        TransitionChannels.Opacity, TransitionDynamics.Tween(Rules.PillPresenceMs, Easing.SmoothOut),
        Enter: new EnterExit(Dy: -Rules.PillEnterDy, Opacity: 0f, Active: true), Exit: new EnterExit(Dy: -Rules.PillExitDy, Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(Rules.PillExitMs, Easing.SmoothOut));

    readonly Signal<bool> _active = new(false);
    readonly Signal<string> _label = new("");
    readonly Signal<int> _arm = new(0);
    float _last = float.NaN;
    int _scrollStep = int.MinValue;
    Func<ClassicConcertLingerClock>? _clock;

    public override Element Render()
    {
        var clock = _clock ??= () => new ClassicConcertLingerClock(() => _active.SetIfChanged(false));
        UseSignalEffect(() =>
        {
            float offset = (float)handle.Offset.Value;
            int step = (int)(offset / Rules.ScrollStepDip);
            if (step == _scrollStep) return;
            _scrollStep = step;
            Reactive.Untrack(() => Step(offset));
        });
        int arm = _arm.Value;
        bool show = _active.Value;
        string text = _label.Value;
        Element pill = Flow.Show(() => _active.Value, new BoxEl
        {
            Animate = s_presence, TransformOriginX = 0.5f, TransformOriginY = 0f, HitTestVisible = false,
            AlignSelf = FlexAlign.Center, Height = Rules.PillPresenceH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Padding = new Edges4(Spacing.M, 0f, Spacing.M, 0f), Corners = Radii.FullAll,
            Acrylic = Tok.AcrylicFlyout, Fill = Tok.FillLayerDefault, Shadow = Elevation.Card,
            BorderWidth = 1f, BorderColor = Tok.StrokeSurfaceDefault,
            Children = [ClassicConcertCells.SectionCaption(text)],
        });
        return new BoxEl
        {
            HitTestVisible = false, HitTestPassThrough = true, Grow = 1f, Direction = 1,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Start, Padding = new Edges4(0f, Spacing.M, 0f, 0f),
            Children = show ? [pill, Embed.Comp(clock) with { Key = "shy-linger:" + arm.ToString(CultureInfo.InvariantCulture) }] : [pill],
        };
    }

    void Step(float offset)
    {
        float previous = _last;
        _last = offset;
        if (float.IsNaN(previous) || MathF.Abs(offset - previous) < Rules.PillStepNoise) return;   // the baseline, and sub-half-DIP noise
        var scene = Context.Scene;
        var viewport = state.Viewport;
        var header = state.PanelHeader;
        bool crossed = scene is not null && !viewport.IsNull && scene.IsLive(viewport) && !header.IsNull && scene.IsLive(header)
            && state.Label.Length > 0 && scene.AbsoluteRect(header).Y <= scene.AbsoluteRect(viewport).Y + Rules.PillCrossSlack;
        if (!crossed) { _active.SetIfChanged(false); return; }             // above the boards: gone at once (parity 70)
        _label.SetIfChanged(state.Label);
        _active.SetIfChanged(true);
        _arm.Value = _arm.Peek() + 1;                                       // re-arm the 1 s linger
    }
}

/// <summary>An invisible, re-armable 1 s countdown on the engine's ANIM clock: it seeds a duration track on its own node
/// and fires once when the track settles. A new key re-arms it; it costs a frame wake only while mounted.</summary>
internal sealed class ClassicConcertLingerClock(Action elapsed) : Component
{
    NodeHandle _self;
    bool _seeded, _fired;

    public override Element Render()
    {
        var tick = UseContextSignal(FrameClock.Tick);
        UseSignalEffect(() =>
        {
            _ = tick.Value;
            var anim = Context.Anim;
            var scene = Context.Scene;
            if (_fired || anim is null || scene is null || _self.IsNull || !scene.IsLive(_self)) return;
            if (!_seeded) { _seeded = true; anim.Animate(_self, AnimChannel.Opacity, 1f, 1f, Rules.LingerMs, Easing.Linear); return; }
            if (!anim.HasTracks(_self)) { _fired = true; elapsed(); }
        });
        return new BoxEl { HitTestVisible = false, Width = 0f, Height = 0f, OnRealized = h => _self = h };
    }
}
