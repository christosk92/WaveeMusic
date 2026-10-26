// ── Themes/Zune/Structure/Views/Concert/ZuneConcertSchedule.cs — Zune's `Pages.ArtistConcerts` (themes plan 6.3; P5 Z6b2) ───────────────────
//
// An artist's tour dashboard. No board: built from Zune's parts (owner Q18) — flat on the ground, the page's own 26/32 light headings, the date as a
// light numeral, rows divided by hairlines, the accent for what is next. `Concert.ScheduleHost` (core) keeps the demand, the readiness verdicts, the
// selected month and the shaping (`ConcertScheduleModel`: spotlight, month groups with their runs, the near-you guard, stats); this file draws it.
//
//   · the HERO: "on tour" (the accent word), the artist's name in the hero voice (a name, as written), the stats line, and — with a spotlight — a
//     hairline, the next-show row (the numeral over its month, "next show · in 3 days", the title and the venue) and the "view event" button beside
//     the round location control; beside the copy the spotlight's photo (a quiet plate while it has none), over the copy on a narrow page;
//   · TOUR DATES: the month tabs (the stock selector bar, the style's pivot voice) over ONE month board at a time, remembered by its `yyyy-MM` key;
//   · the MONTH BOARD: its heading and "N shows", then one column — or two BALANCED chronological columns split by a hairline — and, over six tiles,
//     "show all {shows}". A multi-night run is ONE tile that expands in place;
//   · the SHY MONTH PILL: a round raised word floating at the top while the page scrolls past the board's header.
//
// THE WIDTH IS THE TIER'S: the wide arm (two columns, the photo beside the copy) is a field of the content tier's layout record, never a measured
// number. VIEW STATE (the page-owned state the boards and the pill share): the expansion sets survive a board's keyed remount; the tracker is read
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
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using static FluentGpu.Dsl.Ui;
using Cells = Wavee.Themes.Zune.ZuneConcertCells;
using K = Wavee.Themes.Zune.ZuneDetailKit;
using Rules = Wavee.Themes.Zune.ZuneConcertRules;

namespace Wavee.Themes.Zune;

/// <summary>Page-owned state the boards and the shy pill share: the expansion sets survive a board's keyed remount; the tracker is read
/// imperatively through <c>SceneStore.AbsoluteRect</c>, never through an ancestor.</summary>
internal sealed class ZuneConcertBoardState
{
    public readonly HashSet<string> Months = new(StringComparer.Ordinal), Runs = new(StringComparer.Ordinal);
    public readonly Signal<int> Revision = new(0);
    public NodeHandle Viewport, PanelHeader;
    public string Label = "";
    public void Bump() => Revision.Value = Revision.Peek() + 1;
}

/// <summary>Zune's artist tour dashboard.</summary>
internal static class ZuneConcertSchedule
{
    /// <summary>The view's one hook cell: the shared board state, the viewport's scroll handle and the stable factories and callbacks (a fresh delegate
    /// would re-arm what holds it).</summary>
    sealed class Cell
    {
        public readonly ZuneConcertBoardState Board = new();
        public readonly ScrollHandle Handle = new();
        public readonly Func<ZuneConcertMonthBoard> MonthFactory;
        public readonly Func<ZuneConcertShyMonthPill> PillFactory;
        public readonly Action<NodeHandle> CaptureViewport;

        public Cell()
        {
            MonthFactory = () => new ZuneConcertMonthBoard(Board);
            PillFactory = () => new ZuneConcertShyMonthPill(Board, Handle);
            CaptureViewport = h => Board.Viewport = h;
        }
    }

    /// <summary>The registered template of <see cref="ViewKeys.ArtistConcerts"/>.</summary>
    public static Element Template(ConcertScheduleProps props, in ViewContext ctx)
    {
        var cell = ctx.Hooks.UseRef(new Cell()).Value;
        var facts = ctx.Tier();
        var lay = ZuneConcertTiers.Of(in facts);
        float gutter = ZuneTiers.FrameOf(in facts).Gutter;
        Func<Element> content = () => props.Empty
            ? Controls.Vacancy(Controls.VacancyVoice.Empty, title: Loc.Get(Strings.Concerts.Schedule.NoUpcoming),
                subtitle: Loc.Get(Strings.Concerts.Schedule.NoUpcomingSubtitle))
            : Dashboard(props, cell, props.Model(), lay);
        Element region = Cells.Region(props.Pending, props.Failed, content, () => Dashboard(props, cell, props.Seed(), lay),
            () => Controls.Vacancy(Controls.VacancyVoice.Error, onAction: props.Retry), "artist-schedule:" + props.RouteKey);
        Element scroll = Cells.SubPageScroll(props.ScrollScope + "artist-schedule:" + props.RouteKey, region, gutter) with
        {
            Handle = cell.Handle, OnRealized = cell.CaptureViewport,
        };
        return new BoxEl
        {
            Grow = 1f, MinHeight = 0f, ZStack = true,
            Children = [scroll, Embed.Comp(cell.PillFactory) with { Key = "shy-month:" + props.RouteKey }],
        };
    }

    /// <summary>The loaded (or seeded) dashboard in the tier's record.</summary>
    static Element Dashboard(ConcertScheduleProps props, Cell cell, ConcertScheduleModel m, ZuneConcertLayout lay)
    {
        var sections = new List<Element>(2) { Hero(props, m, lay) };
        if (m.Selected >= 0)
        {
            var group = m.Groups[m.Selected];
            if (!m.Seed)
                cell.Board.Label = new DateTime(group.Year, group.Month, 1).ToString("MMMM yyyy", CultureInfo.CurrentCulture)
                                 + " · " + Strings.Concerts.Schedule.ShowCount(group.ShowCount);
            sections.Add(TourDates(props, cell, m, lay));
        }
        else
        {
            if (!m.Seed) { cell.Board.Label = ""; cell.Board.PanelHeader = NodeHandle.Null; }
            // The hero rendered but no month remains (the one show on sale IS the spotlight).
            sections.Add(new BoxEl
            {
                Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Center, Padding = new Edges4(0f, Rules.EmptyPadY, 0f, Rules.EmptyPadY),
                Children =
                [
                    Controls.Vacancy(Controls.VacancyVoice.Empty, title: Loc.Get(Strings.Concerts.Schedule.NoMoreDates),
                        subtitle: Loc.Get(Strings.Concerts.Schedule.NoMoreDatesSubtitle),
                        actionLabel: Loc.Get(Strings.Concerts.Location.Set), onAction: props.OpenPicker),
                ],
            });
        }
        return new BoxEl { Direction = 1, MinWidth = 0f, Gap = lay.SectionGap, Children = sections.ToArray() };
    }

    // ══ THE HERO ══════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>"on tour" · the name (≤ 2 lines) · the stats line; with a spotlight a hairline, the next-show row and "view event" beside the round
    /// location control (stacked when narrow). Without one the location control stands alone at full width and the photo's plate is untinted.</summary>
    static Element Hero(ConcertScheduleProps props, ConcertScheduleModel m, ZuneConcertLayout lay)
    {
        var blocks = new List<Element>(6)
        {
            R.Type.DenseMeta(Loc.Get(Strings.Concerts.Schedule.OnTour)) with
            {
                Color = Prop.Of(static () => R.Ink.AccentDecor), Case = R.Case.For(LabelKind.Label), MaxLines = 1, Wrap = TextWrap.NoWrap,
            },
            R.Type.Hero(m.Name) with
            {
                Size = lay.ScheduleTitleSize, LineHeight = lay.ScheduleTitleLine, Color = Tok.TextPrimary, MinWidth = 0f,
                Wrap = TextWrap.Wrap, MaxLines = Rules.TitleLines, Trim = TextTrim.CharacterEllipsis,
            },
            R.Type.TrackMeta(ConcertScheduleShaping.StatsLine(m.Stats)) with
            {
                Case = R.Case.For(LabelKind.Label), MinWidth = 0f, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
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
                    Cells.DateColumn(next.Date, lay.ScheduleDateW, lay.RelatedDaySize, lay.RelatedDayLine),
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Rules.TileTextGap,
                        Children =
                        [
                            R.Type.MicroMeta(Strings.Concerts.Schedule.NextShow(ConcertScheduleShaping.RelativeTime(next.Date, m.Now))) with
                            {
                                Color = Prop.Of(static () => R.Ink.AccentDecor), Case = R.Case.For(LabelKind.Label), MinWidth = 0f, MaxLines = 1,
                                Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
                            },
                            R.Type.TrackTitle(text.Primary) with { MinWidth = 0f, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis },
                            R.Type.TrackMeta(text.Secondary) with { MinWidth = 0f, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis },
                        ],
                    },
                ],
            });
        }
        BoxEl location = Cells.LocationButton(m.LocationLabel, props.OpenPicker, lay.HubPill) with { OnRealized = props.CaptureAnchor };
        blocks.Add(m.Spotlight is null
            ? location
            : lay.ScheduleWide
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
        Element photo = Photo(m.HeroImageUrl, m.SpotlightAccent, lay);
        return lay.ScheduleWide
            ? new BoxEl
            {
                Key = "zune-schedule:hero", Direction = 0, MinWidth = 0f, Gap = lay.HeroGap, AlignItems = FlexAlign.Start,
                Children = [new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Children = [copy] }, photo],
            }
            : new BoxEl { Key = "zune-schedule:hero", Direction = 1, MinWidth = 0f, Gap = Spacing.L, Children = [photo, copy] };
    }

    /// <summary>The spotlight's photo at the Cover shape: the image where the artist has one, else the quiet plate (tinted toward the show's accent, or
    /// flat without one) with a centred calendar — the pane never collapses, so the hero never jumps when the photo lands.</summary>
    static Element Photo(string? imageUrl, uint accent, ZuneConcertLayout lay)
    {
        ColorF baseFill = R.Layer.EventHeroPlate;
        ColorF mediaFill = accent != 0 ? ColorF.Lerp(baseFill, ColorScience.ToColor(accent), R.Wash.ConcertMediaMix) : baseFill;
        Element picture = imageUrl is { Length: > 0 }
            ? new ImageEl
            {
                Source = imageUrl, Fit = ImageFit.Cover, FocusY = Rules.HeroFocusY, DecodePx = Rules.HeroDecode, Placeholder = mediaFill,
                RevealTransition = ImageTransition.Fade(Rules.HeroRevealMs),
            }
            : new BoxEl
            {
                Fill = mediaFill, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [Icon(Icons.Calendar, Rules.HeroGlyph, Tok.TextSecondary with { A = Rules.GlyphAlpha })],
            };
        return new BoxEl
        {
            Key = "zune-schedule:photo", Width = lay.ScheduleWide ? lay.SchedulePhotoW : float.NaN, Height = lay.SchedulePhotoH, Shrink = 0f,
            AlignSelf = lay.ScheduleWide ? FlexAlign.Start : FlexAlign.Stretch, ClipToBounds = true, Corners = R.Shape.All(ShapeRole.Cover),
            Children = [picture],
        };
    }

    // ══ TOUR DATES ════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>"tour dates" over the month tabs (a stock selector bar in an edge-faded scroller), then the selected month's board — keyed on its
    /// composed signature (props freeze at mount).</summary>
    static Element TourDates(ConcertScheduleProps props, Cell cell, ConcertScheduleModel m, ZuneConcertLayout lay)
    {
        var groups = m.Groups;
        var labels = ConcertScheduleShaping.MonthTabLabels(groups, CultureInfo.CurrentCulture);
        Element tabs = ScrollView(new BoxEl
        {
            Direction = 0,
            Children = [SelectorBar.Create(labels, new Signal<int>(m.Selected), onChange: i => props.SelectMonth(groups[i].Key))],
        }, horizontal: true) with
        {
            Height = MathF.Max(Rules.MonthTabsH, lay.Target), Grow = 0f, AutoEdgeFade = true, AutoEdgeFadeBand = Rules.BarEdgeFadeBand, SuppressScrollBar = true,
            ScrollKey = props.ScrollScope + "artist-schedule-months:" + props.RouteKey,
        };
        var group = groups[m.Selected];
        string signature = group.Key + ":" + group.Runs.Count.ToString(CultureInfo.InvariantCulture) + ":"
            + group.ShowCount.ToString(CultureInfo.InvariantCulture) + ":" + (group.Runs.Count > 0 ? group.Runs[0].Uri : "") + (lay.ScheduleWide ? ":w" : ":n");
        return new BoxEl
        {
            Key = "zune-schedule:tour", Direction = 1, MinWidth = 0f, Gap = Rules.SectionHeadGap,
            Children =
            [
                K.Heading(Loc.Get(Strings.Concerts.Schedule.TourDates)),
                tabs,
                Embed.Comp(new ZuneConcertMonthBoard.BoardProps(group, m.Nearby, m.Name, lay.ScheduleWide, m.Seed, lay.Target), cell.MonthFactory)
                    with { Key = "dashboard-month:" + signature },
            ],
        };
    }
}

/// <summary>The selected month: header (the month in the group voice + "N shows"), a hairline, one column — or at wide two BALANCED chronological
/// columns split by a 1-dip hairline — and, over six tiles, a hairline + "show all {shows}". A multi-night run is ONE tile that expands in place.</summary>
internal sealed class ZuneConcertMonthBoard(ZuneConcertBoardState state) : Component
{
    internal sealed record BoardProps(ConcertMonthGroup Group, HashSet<string> Near, string ArtistName, bool Wide, bool Seed, float Target);

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
                Direction = 0, AlignItems = FlexAlign.Start, MinWidth = 0f, Gap = Rules.BoardColumnGap,
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
                OnRealized = _p.Seed ? null : _header,
                Children =
                [
                    R.Type.SheetTitle(new DateTime(_p.Group.Year, _p.Group.Month, 1).ToString("MMMM yyyy", culture)) with
                    {
                        Case = R.Case.For(LabelKind.Heading), Color = Tok.TextPrimary, Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1,
                        Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
                    },
                    R.Type.DenseMeta(Strings.Concerts.Schedule.ShowCount(_p.Group.ShowCount)) with
                    {
                        Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label), Wrap = TextWrap.NoWrap, MaxLines = 1, Shrink = 0f,
                    },
                ],
            },
            Divider(), body,
        };
        if (capped)
        {
            kids.Add(Divider());
            kids.Add(new BoxEl
            {
                Direction = 0, MinWidth = 0f, MinHeight = _p.Target, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = Spacing.XS,
                HoverFill = Prop.Of(static () => R.Layer.RowHover), PressedFill = Prop.Of(static () => R.Layer.RowPressed),
                Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
                OnClick = () => { if (state.Months.Add(_p.Group.Key)) state.Bump(); },
                Children =
                [
                    // "show all {SHOWS}" while the cap counts TILES.
                    R.Type.DenseTitle(Strings.Concerts.Schedule.ShowAll(_p.Group.ShowCount)) with
                    {
                        Color = Prop.Of(static () => R.Ink.AccentDecor), Case = R.Case.For(LabelKind.Command), MaxLines = 1,
                    },
                    Icon(Icons.ChevronDown, Rules.BoardChevron, R.Ink.AccentDecor) with { Shrink = 0f },
                ],
            });
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
        if (near) secondary.Add(Icon(Icons.MapPin, Rules.BoardPin, R.Ink.AccentDecor) with { Shrink = 0f });
        if (text.Secondary.Length > 0) secondary.Add(Meta(text.Secondary));
        string uri = c.Uri;
        bool seed = _p.Seed;
        return Tile(uri, near, DayColumn(c.Date.Day.ToString(CultureInfo.CurrentCulture), c.Date.ToString("ddd", CultureInfo.CurrentCulture), Rules.BoardDayW),
            text.Primary, secondary, Icons.ChevronRight,
            () => { if (!seed) ConcertVerbs.OpenConcertByUri(uri); });
    }

    /// <summary>The run tile's secondary row, in BUILT order: a venue-primary run reads [pin] city [N nights]; a city-primary run reads [pin] [N nights]
    /// with …; the pin only inside the guarded near set.</summary>
    Element RunTile(ConcertBoardRun run, bool near)
    {
        var c = run.First;
        var text = ConcertScheduleShaping.TileText(c, _p.ArtistName);
        var meta = new List<Element>(4);
        if (near) meta.Add(Icon(Icons.MapPin, Rules.BoardPin, R.Ink.AccentDecor) with { Shrink = 0f });
        meta.Add(new BoxEl
        {
            AlignSelf = FlexAlign.Center, Shrink = 0f, Padding = new Edges4(Spacing.S, 1f, Spacing.S, 1f),
            Corners = R.Shape.All(ShapeRole.Avatar), Fill = Tok.AccentSubtle,
            Children =
            [
                R.Type.MicroMeta(ConcertCopy.Current.Nights(run.NightCount)) with
                {
                    Color = Prop.Of(static () => R.Ink.AccentDecor), Case = R.Case.For(LabelKind.Label), MaxLines = 1,
                },
            ],
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

    /// <summary>A board tile: the near rail (always laid out, transparent when not near, so widths never differ), the day column, the primary line over
    /// the secondary row, the trailing glyph; the tier's target tall at least.</summary>
    Element Tile(string key, bool near, Element day, string primary, List<Element> secondary, string glyph, Action onClick) => new BoxEl
    {
        Key = key, Direction = 0, MinHeight = MathF.Max(Rules.BoardTileMinH, _p.Target), MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.M,
        Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.S),
        HoverFill = Prop.Of(static () => R.Layer.RowHover), PressedFill = Prop.Of(static () => R.Layer.RowPressed),
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, FocusVisualMargin = s_focusMargin, Cursor = CursorId.Hand, OnClick = onClick,
        Children =
        [
            new BoxEl { Width = Rules.BoardRailW, Height = Rules.BoardRailH, Shrink = 0f, Fill = near ? Prop.Of(static () => R.Ink.AccentAction) : ColorF.Transparent },
            day,
            new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Rules.BoardTextGap, Justify = FlexJustify.Center,
                Children =
                [
                    R.Type.TrackTitle(primary) with { Color = Tok.TextPrimary, MinWidth = 0f, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis },
                    new BoxEl { Direction = 0, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.XS, Children = secondary.ToArray() },
                ],
            },
            Icon(glyph, Rules.BoardTrailing, Tok.TextSecondary) with { Shrink = 0f },
        ],
    };

    /// <summary>The growing text run: Grow 1 + Shrink 1 + MinWidth 0 over its MEASURED basis (never Basis 0).</summary>
    static TextEl Meta(string text)
        => R.Type.TrackMeta(text) with { Grow = 1f, Shrink = 1f, MinWidth = 0f, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis };

    /// <summary>The day column: the light numeral (a run reads "12–14") over the lowercase weekday.</summary>
    static Element DayColumn(string day, string weekday, float width) => new BoxEl
    {
        Width = width, Shrink = 0f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = 1f,
        Children =
        [
            R.Type.Numeral(day) with { Color = Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap },
            R.Type.MicroMeta(weekday) with { Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Wrap = TextWrap.NoWrap },
        ],
    };

    static Element Divider() => new BoxEl { Height = 1f, MinWidth = 0f, Fill = Tok.StrokeDividerDefault };
}

/// <summary>The shy month pill: a round raised plate, top-centre, shown only while scrolling AND once the board header has crossed the viewport top
/// (≤ +4 DIP), gone at once above it, lingering 1 s past the last step on the ANIM clock. Never hit-testable: the scroller beneath always wins the
/// wheel. It reads the viewport's scroll handle itself: the offset on an 8-DIP write floor drives the step.</summary>
internal sealed class ZuneConcertShyMonthPill(ZuneConcertBoardState state, ScrollHandle handle) : Component
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
    Func<ZuneConcertLingerClock>? _clock;

    public override Element Render()
    {
        var clock = _clock ??= () => new ZuneConcertLingerClock(() => _active.SetIfChanged(false));
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
            Padding = new Edges4(Spacing.M, 0f, Spacing.M, 0f), Corners = CornerRadius4.All(Rules.PillPresenceH / 2f),
            Fill = Prop.Of(static () => R.Layer.FloatingChrome), Shadow = Elevation.Card,
            Children =
            [
                R.Type.DenseTitle(text) with { Color = Prop.Of(static () => R.Ink.AccentDecor), Case = R.Case.For(LabelKind.Label), MaxLines = 1, Wrap = TextWrap.NoWrap },
            ],
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
        if (!crossed) { _active.SetIfChanged(false); return; }             // above the boards: gone at once
        _label.SetIfChanged(state.Label);
        _active.SetIfChanged(true);
        _arm.Value = _arm.Peek() + 1;                                       // re-arm the 1 s linger
    }
}

/// <summary>An invisible, re-armable 1 s countdown on the engine's ANIM clock: it seeds a duration track on its own node and fires once when the track
/// settles. A new key re-arms it; it costs a frame wake only while mounted.</summary>
internal sealed class ZuneConcertLingerClock(Action elapsed) : Component
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
