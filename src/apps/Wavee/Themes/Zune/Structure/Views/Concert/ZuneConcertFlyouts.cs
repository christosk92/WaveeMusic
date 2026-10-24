// ── Themes/Zune/Structure/Views/Concert/ZuneConcertFlyouts.cs — Zune's concert filter flyouts (themes plan 6.3; P5 Z6b2) ──────────────────────
//
// The flyout row and divider, the WHEN flyout (the date drill-down) and the WHERE flyout (the radius). Both mount fresh per open inside the overlay the
// filter bar opens, and hand every choice to a callback; the date range rules are core (`ConcertHub`, `ConcertWhen`). The overlay options are
// `Concert.FlyoutOptions` (focus-trapped, light-dismiss). No board: built from Zune's parts (Q18) — square rows on the wash, the accent for the choice
// that is on, the words lowered by the label case; a month and a weekend are culture text lowered with them.

using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming;
using Wavee.Theming.Roles;
using static FluentGpu.Dsl.Ui;
using Rules = Wavee.Themes.Zune.ZuneConcertRules;

namespace Wavee.Themes.Zune;

/// <summary>The flyout pieces the when and where flyouts share.</summary>
internal static class ZuneConcertFlyoutCells
{
    /// <summary>A flyout row: MinH 40, pad (12,4,12,4), the control shape, the label voice, an optional lead 16 / trailing 14 glyph, Subtle.
    /// A radio row (<paramref name="toggle"/>) reserves its 14×14 check box, so the labels never shift.</summary>
    public static Element FlyoutRow(string label, Action onClick, string? lead = null, string? trailing = null,
                                    bool toggle = false, bool active = false)
    {
        var kids = new List<Element>(3);
        if (lead is not null) kids.Add(Icon(lead, Rules.FlyoutLead, Tok.TextSecondary) with { Shrink = 0f });
        kids.Add(Body(label) with
        {
            Color = active ? R.Ink.AccentDecor : Tok.TextPrimary, Weight = (ushort)(active ? 600 : 400), Case = R.Case.For(LabelKind.Label),
            Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        });
        if (trailing is not null) kids.Add(Icon(trailing, Rules.FlyoutTrailing, Tok.TextSecondary) with { Shrink = 0f });
        if (toggle) kids.Add(active ? Icon(Icons.Check, Rules.FlyoutTrailing, R.Ink.AccentDecor) with { Shrink = 0f } : new BoxEl { Width = Rules.FlyoutCheck, Height = Rules.FlyoutCheck, Shrink = 0f });
        return new BoxEl
        {
            Direction = 0, MinHeight = Rules.FlyoutRowMinH, AlignItems = FlexAlign.Center, Gap = Spacing.S,
            Padding = new Edges4(Spacing.M, Spacing.XS, Spacing.M, Spacing.XS), Corners = R.Shape.All(ShapeRole.Control),
            Role = toggle ? AutomationRole.ToggleButton : AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            OnClick = onClick, Children = kids.ToArray(),
        }.Interactive(Interaction.Subtle);
    }

    public static Element FlyoutDivider() => new BoxEl
    {
        AlignSelf = FlexAlign.Stretch, Padding = new Edges4(0f, Spacing.XS, 0f, Spacing.XS),
        Children = [new BoxEl { Height = 1f, AlignSelf = FlexAlign.Stretch, Fill = Tok.StrokeSurfaceDefault }],
    };
}

/// <summary>The when-area drill-down (0.2.9 <c>ConcertDateFlyout</c>): ROOT (Anytime / Today / This weekend / Next
/// weekend, a divider, this month + 3 drilling in) ⇄ MONTH LEAF (back, All of {month}, each Fri–Sun weekend, the Su–Sa
/// calendar, Clear / Show events). The swap is keyed per view and slides forward on a drill, back on a return (250 ms,
/// folded to instant under reduced motion). Mounts fresh per open; hands every choice to <c>onPick</c>.</summary>
internal sealed class ZuneConcertWhenFlyout(Action<ConcertWhen> onPick) : Component
{
    readonly Signal<int> _view = new(-1);            // -1 root; 0..3 = the month leaf's offset from this month
    readonly Signal<DateOnly?> _start = new(null), _end = new(null);
    bool _forward = true;
    ZuneConcertLayout _lay = ZuneConcertTiers.Standard;
    float _width = Rules.WhenFlyoutW, _maxHeight = Rules.MonthMaxH;

    public override Element Render()
    {
        int view = _view.Value;
        var culture = CultureInfo.CurrentCulture;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var first = new DateOnly(today.Year, today.Month, 1);
        // The flyout never outgrows the window it opens in (Compact windows are 300 wide), and its calendar cells share what is left; the days are
        // the tier's hit edge tall (40 at Compact).
        var facts = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers;
        _lay = ZuneConcertTiers.Of(in facts);
        var viewport = UseContext(Viewport.Size);
        _width = Rules.FlyoutWidthIn(Rules.WhenFlyoutW, viewport.Width);
        _maxHeight = Rules.FlyoutHeightIn(Rules.MonthMaxH, viewport.Height);
        return new BoxEl
        {
            Direction = 1, Width = _width, ClipToBounds = true, Padding = Edges4.All(Spacing.S),
            Children =
            [
                new BoxEl
                {
                    Key = view < 0 ? "when-view:root" : "when-view:month:" + view.ToString(CultureInfo.InvariantCulture),
                    Animate = _forward ? MotionRecipes.PageSlideForward : MotionRecipes.PageSlideBack,
                    Direction = 1, MinWidth = 0f,
                    Children = [view < 0 ? Root(first, culture) : Month(first.AddMonths(view), today, culture)],
                },
            ],
        };
    }

    Element Root(DateOnly first, CultureInfo culture)
    {
        var rows = new Element[9];
        rows[0] = ZuneConcertFlyoutCells.FlyoutRow(Loc.Get(Strings.Concerts.Filter.Anytime), () => onPick(ConcertWhen.Any));
        rows[1] = ZuneConcertFlyoutCells.FlyoutRow(Loc.Get(Strings.Concerts.Filter.Today), () => Preset(ConcertWhenKind.Today, Strings.Concerts.Filter.Today));
        rows[2] = ZuneConcertFlyoutCells.FlyoutRow(Loc.Get(Strings.Concerts.Filter.ThisWeekend), () => Preset(ConcertWhenKind.ThisWeekend, Strings.Concerts.Filter.ThisWeekend));
        rows[3] = ZuneConcertFlyoutCells.FlyoutRow(Loc.Get(Strings.Concerts.Filter.NextWeekend), () => Preset(ConcertWhenKind.NextWeekend, Strings.Concerts.Filter.NextWeekend));
        rows[4] = ZuneConcertFlyoutCells.FlyoutDivider();
        for (int i = 0; i < 4; i++)
        {
            int index = i;
            rows[5 + i] = ZuneConcertFlyoutCells.FlyoutRow(first.AddMonths(i).ToString("MMMM yyyy", culture),
                () => { _forward = true; _view.Value = index; }, trailing: Icons.ChevronRight);
        }
        return new BoxEl { Direction = 1, Gap = Rules.FlyoutGap, Children = rows };
    }

    void Preset(ConcertWhenKind kind, string key)
        => onPick(new ConcertWhen(kind, Loc.Get(key), ConcertHub.PresetRange(kind, DateTimeOffset.Now)));

    Element Month(DateOnly first, DateOnly today, CultureInfo culture)
    {
        var start = _start.Value;
        var end = _end.Value;
        string monthName = first.ToString("MMMM", culture);
        var last = new DateOnly(first.Year, first.Month, DateTime.DaysInMonth(first.Year, first.Month));
        var rows = new List<Element>(12)
        {
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinHeight = Rules.MonthHeadMinH,
                Children =
                [
                    new BoxEl
                    {
                        Width = Rules.BackEdge(_lay.Target), Height = Rules.BackEdge(_lay.Target), Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Corners = R.Shape.All(ShapeRole.Control), Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                        OnClick = () => { _forward = false; _view.Value = -1; },
                        Children = [Icon(Icons.ChevronLeft, Rules.FlyoutTrailing, Tok.TextSecondary)],
                    }.Interactive(Interaction.Subtle),
                    BodyStrong(first.ToString("MMMM yyyy", culture)) with { Color = Tok.TextPrimary, MaxLines = 1 },
                ],
            },
            ZuneConcertFlyoutCells.FlyoutRow(Strings.Concerts.Filter.AllOf(monthName),
                () => onPick(new ConcertWhen(ConcertWhenKind.Custom, monthName, new ConcertDateRange(first, last)))),
        };
        // Every Fri–Sun of the month (a weekend spilling past month-end keeps its Sunday).
        var friday = first;
        while (friday.DayOfWeek != DayOfWeek.Friday) friday = friday.AddDays(1);
        for (; friday.Month == first.Month; friday = friday.AddDays(7))
        {
            var range = new ConcertDateRange(friday, friday.AddDays(2));
            string weekend = Loc.Get(Strings.Concerts.Filter.Weekend);
            rows.Add(ZuneConcertFlyoutCells.FlyoutRow(weekend + " · " + ConcertHub.WhenLabel(range, culture),
                () => onPick(new ConcertWhen(ConcertWhenKind.Custom, weekend, range))));
        }
        rows.Add(Calendar(first, today, start, end, culture));
        rows.Add(new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinHeight = Rules.MonthFootMinH,
            Padding = new Edges4(Spacing.XS, Spacing.XS, Spacing.XS, 0f),
            Children =
            [
                Button.Standard(Loc.Get(Strings.Concerts.Filter.Clear), () => { _start.Value = null; _end.Value = null; }),
                new BoxEl { Grow = 1f },
                Button.Accent(Loc.Get(Strings.Concerts.Filter.ShowEvents), Apply, isEnabled: start is not null),
            ],
        });
        return new ScrollEl
        {
            ContentSized = true, MaxHeight = _maxHeight,
            Content = new BoxEl { Direction = 1, Gap = Spacing.XS, Children = rows.ToArray() },
        };
    }

    void Apply()
    {
        if (_start.Peek() is not { } s) return;
        onPick(new ConcertWhen(ConcertWhenKind.Custom, Loc.Get(Strings.Concerts.Filter.Custom),
            new ConcertDateRange(s, _end.Peek() ?? s)));
    }

    /// <summary>The Su–Sa grid: 38×20 two-letter headers, 38×32 days with 4-DIP gaps; past days inert and disabled
    /// ink; the chosen ends on the accent plate, the days between on <c>AccentSubtle</c> (W8).</summary>
    Element Calendar(DateOnly first, DateOnly today, DateOnly? start, DateOnly? end, CultureInfo culture)
    {
        int days = DateTime.DaysInMonth(first.Year, first.Month), lead = (int)first.DayOfWeek;
        float cellW = Rules.CalendarCellWIn(_width), cellH = Rules.CalendarCellHIn(_lay.Target);
        var names = culture.DateTimeFormat.AbbreviatedDayNames;
        var grid = new List<Element>(7);
        var header = new Element[7];
        for (int i = 0; i < 7; i++)
            header[i] = new BoxEl
            {
                Width = cellW, Height = Rules.CalendarHeadH, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [Caption(names[i].Length <= 2 ? names[i] : names[i][..2]) with { Color = Tok.TextSecondary, MaxLines = 1 }],
            };
        grid.Add(new BoxEl { Direction = 0, Gap = Rules.CalendarGap, Children = header });
        var week = new List<Element>(7);
        for (int i = 0; i < lead; i++) week.Add(new BoxEl { Width = cellW, Height = cellH, Shrink = 0f });
        for (int d = 1; d <= days; d++)
        {
            var day = new DateOnly(first.Year, first.Month, d);
            bool past = day < today;
            bool selected = day == start || day == end;
            bool inRange = start is { } s && end is { } e && day > s && day < e;
            var cell = new BoxEl
            {
                Width = cellW, Height = cellH, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = R.Shape.All(ShapeRole.Control), Fill = selected ? Tok.AccentDefault : inRange ? Tok.AccentSubtle : ColorF.Transparent,
                Children = [Body(d.ToString(culture)) with { Color = selected ? Tok.TextOnAccentPrimary : past ? Tok.TextDisabled : Tok.TextPrimary, MaxLines = 1 }],
            };
            week.Add(past
                ? cell with { HitTestVisible = false }
                : cell with
                {
                    HoverFill = selected ? Tok.AccentSecondary : Tok.FillSubtleSecondary,
                    PressedFill = selected ? Tok.AccentTertiary : Tok.FillSubtleTertiary,
                    Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = () => TapDay(day),
                });
            if (week.Count == 7) { grid.Add(new BoxEl { Direction = 0, Gap = Rules.CalendarGap, Children = week.ToArray() }); week.Clear(); }
        }
        if (week.Count > 0)
        {
            while (week.Count < 7) week.Add(new BoxEl { Width = cellW, Height = cellH, Shrink = 0f });
            grid.Add(new BoxEl { Direction = 0, Gap = Rules.CalendarGap, Children = week.ToArray() });
        }
        return new BoxEl { Direction = 1, Gap = Rules.CalendarGap, Children = grid.ToArray() };
    }

    /// <summary>First tap = start; a second tap ≥ start closes the range; a tap before the start restarts it; a third
    /// tap starts fresh (ch 17 §6).</summary>
    void TapDay(DateOnly day)
    {
        var s = _start.Peek();
        if (s is null || _end.Peek() is not null) { _start.Value = day; _end.Value = null; }
        else if (day < s) _start.Value = day;
        else _end.Value = day;
    }
}

/// <summary>264 wide: Search cities ›, Use my location, a divider, the 25 / 50 / 100 km radios. A radius pick KEEPS the
/// flyout open (the check moves, the pill re-labels); the two location rows close it and hand off.</summary>
internal sealed class ZuneConcertWhereFlyout(IReadSignal<int> radius, Action<int> onRadius, Action onSearch, Action onUseMine) : Component
{
    public override Element Render()
    {
        int current = radius.Value;
        float width = Rules.FlyoutWidthIn(Rules.WhereFlyoutW, UseContext(Viewport.Size).Width);
        var options = ConcertHub.RadiusOptionsKm;
        var kids = new Element[3 + options.Length];
        kids[0] = ZuneConcertFlyoutCells.FlyoutRow(Loc.Get(Strings.Concerts.Location.SearchCities), onSearch, lead: Icons.MapPin, trailing: Icons.ChevronRight);
        kids[1] = ZuneConcertFlyoutCells.FlyoutRow(Loc.Get(Strings.Concerts.Location.UseMine), onUseMine, lead: Icons.MapPin);
        kids[2] = ZuneConcertFlyoutCells.FlyoutDivider();
        for (int i = 0; i < options.Length; i++)
        {
            int km = options[i];
            kids[3 + i] = ZuneConcertFlyoutCells.FlyoutRow(Strings.Concerts.Filter.WithinKm(km), () => onRadius(km), toggle: true, active: km == current);
        }
        return new BoxEl { Direction = 1, Width = width, Gap = Rules.FlyoutGap, Padding = Edges4.All(Spacing.S), Children = kids };
    }
}
