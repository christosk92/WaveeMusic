// ── Themes/Classic/Structure/Views/Pages/ClassicRecentsCalendar.cs — Classic's Recents calendar overview (themes plan 6.1, T10c2) ─────────────────
//
// From `Entities\Recents.Page.cs` (`CalendarSurface`, `CalendarBand`) and `Entities\Recents.UI.cs` (`MonthCard`, `CalendarCell`, `Legend`): the zoomed-out view of the
// semantic zoom (W11-W14, §9.1 #8-#9). The calendar's model (`RecentsCalendar`: the months, the density levels, the top item of a day), the readout and tooltip
// phrases and the jump verbs are the host's.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>The overview: the readout band + legend over a GridFit of month cards (290 min, 36 gutters, the TALLEST
/// month's height as the row estimate). Its scroll key carries the shape epoch: a re-cut forgets (§9.1 #16).</summary>
internal sealed class ClassicRecentsCalendar : Component
{
    readonly IRecentsHost _host;
    readonly int _shapeEpoch;
    readonly Func<int, Element> _monthCard;
    readonly Func<int, string> _monthKey;

    public ClassicRecentsCalendar(IRecentsHost host, int shapeEpoch)
    {
        _host = host;
        _shapeEpoch = shapeEpoch;
        _monthKey = MonthKey;
        _monthCard = i => Embed.Comp(() => new ClassicRecentsMonthCard(host, i)) with { Key = MonthKey(i) };
    }

    string MonthKey(int i)
    {
        var months = _host.ShapeNow.Calendar.Months;
        return (uint)i < (uint)months.Length
            ? "recents-month:" + FormatCache.Int(months[i].Year) + ":" + FormatCache.Int(months[i].Month)
            : "recents-month:none";
    }

    public override Element Render()
    {
        var host = _host;
        var calendar = host.ShapeNow.Calendar;
        var facts = UseContext(ThemeFactsContext.Signal);   // the tiers this page is drawn in, as its template reads them
        Element grid = ItemsView.Create(calendar.Months.Length, _monthCard,
            RepeatLayout.GridFit(RecentsLayout.CalGridW, ClassicRecentsTiers.Of(facts is null ? TierFacts.Unknown : facts.Value.Tiers).CalendarGap, RecentsLayout.MonthCardHeight(RecentsView.MaxWeeks(calendar))),
            new ListOptions
            {
                SelectionMode = ItemsSelectionMode.None, Selector = SelectorVisual.None,
                Controller = host.CalendarController, Grow = 1f, KeyOf = _monthKey,
                Scroll = new ScrollOptions { ScrollKey = host.ScrollScope + "recents-calendar:" + FormatCache.Int(_shapeEpoch), AutoEdgeFade = true },
            });
        return new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Gap = Spacing.L,
            OnPointerExit = host.ResetCalendarDayAction,
            Children = [Embed.Comp(() => new ClassicRecentsCalendarBand(host)), grid],
        };
    }
}

/// <summary>ONE band: the hovered day over its readout, the legend beside them; the readout gives width back.</summary>
internal sealed class ClassicRecentsCalendarBand(IRecentsHost host) : Component
{
    public override Element Render()
    {
        DateOnly selected = host.CalendarDay.Value;
        _ = host.Epoch.Value;
        var shape = host.ShapeNow;
        return new BoxEl
        {
            Direction = 0, Shrink = 0f, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.M,
            Padding = new Edges4(Spacing.S, Spacing.S, Spacing.S, 0f),
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XXS,
                    Children =
                    [
                        Caption(selected.ToString("dddd d MMMM", host.Culture)) with
                        { Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        Caption(host.CalendarReadout(shape, selected)) with
                        { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    ],
                },
                ClassicRecentsCalendarParts.Legend(host),
            ],
        };
    }
}

/// <summary>One month card: its title as its OWN node (the <c>ModuleHeader(title, meta)</c> span cannot carry the
/// accent bind), the culture-rotated weekday band, and <c>WeekCount</c> week rows — never a fixed 6. Keyed by
/// year:month under a shape-keyed surface, so the two constructor values are identity, not data.</summary>
internal sealed class ClassicRecentsMonthCard(IRecentsHost host, int monthIndex) : Component
{
    public override Element Render()
    {
        var shape = host.ShapeNow;
        var months = shape.Calendar.Months;
        if ((uint)monthIndex >= (uint)months.Length) return new BoxEl { Width = RecentsLayout.CalGridW };
        var month = months[monthIndex];
        var culture = host.Culture;
        DateOnly today = host.Today;

        var weekNames = new Element[7];
        var firstDay = culture.DateTimeFormat.FirstDayOfWeek;
        for (int i = 0; i < 7; i++)
            weekNames[i] = new BoxEl
            {
                Width = RecentsLayout.CalCellW, Height = RecentsLayout.CalHeaderH, Shrink = 0f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [Caption(culture.DateTimeFormat.AbbreviatedDayNames[((int)firstDay + i) % 7]) with { Color = Tok.TextSecondary, MaxLines = 1 }],
            };

        var weeks = new Element[1 + month.WeekCount];
        weeks[0] = new BoxEl { Direction = 0, Gap = RecentsLayout.CalGap, Children = weekNames };
        DateOnly first = new(month.Year, month.Month, 1);
        DateOnly gridFirst = first.AddDays(-month.FirstDayOffset);
        for (int week = 0; week < month.WeekCount; week++)
        {
            var cells = new Element[7];
            for (int column = 0; column < 7; column++)
                cells[column] = ClassicRecentsCalendarParts.Cell(host, shape, gridFirst.AddDays(week * 7 + column), month, today, monthIndex);
            weeks[1 + week] = new BoxEl { Direction = 0, Gap = RecentsLayout.CalGap, Children = cells };
        }

        // Three meta states and none is "0 plays" (W13).
        string meta = month.TotalPlays > 0
            ? Strings.Recents.PlayCount(month.TotalPlays) + (month.IsCurrentMonth ? " · " + Loc.Get(Strings.Recents.SoFar) : "")
            : Loc.Get(Strings.Recents.NothingPlayed);
        return new BoxEl
        {
            Direction = 1, Width = RecentsLayout.CalGridW, Gap = Spacing.S, OnPointerExit = host.ResetCalendarDayAction,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
                    Children =
                    [
                        R.Type.ModuleHeader(first.ToString("MMMM", culture)) with
                        {
                            Color = month.IsCurrentMonth ? host.AccentInkProp : (Prop<ColorF>)Tok.TextPrimary,
                            BrushTransitionMs = ClassicRecents.AccentTransitionMs, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 0f,
                        },
                        Caption(meta) with
                        {
                            Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                            Grow = 1f, Basis = 0f, MinWidth = 0f,
                        },
                    ],
                },
                new BoxEl { Direction = 1, Gap = RecentsLayout.CalGap, Children = weeks },
            ],
        };
    }
}

/// <summary>The calendar's cell and key.</summary>
internal static class ClassicRecentsCalendarParts
{
    /// <summary>One 38 × 32 cell, FIXED (a stretchy cell turns the heatmap into ragged bars). A lead/trail day is a BLANK
    /// spacer; today is ONE cue — the numeral at 600 in the accent ink. Only a day the list has rows for is a button.</summary>
    internal static Element Cell(IRecentsHost host, RecentsShape shape, DateOnly date, RecentsCalendarMonth month, DateOnly today, int monthIndex)
    {
        if (date.Year != month.Year || date.Month != month.Month)
            return new BoxEl { Width = RecentsLayout.CalCellW, Height = RecentsLayout.CalCellH, Shrink = 0f };

        var day = RecentsLayout.CalendarDay(shape.Calendar, date);
        bool isToday = date == today;
        bool hasRows = RecentsLayout.HeaderFlatFor(shape.Sections, date) >= 0;
        var numeral = Body(FormatCache.Int(date.Day)) with
        {
            Weight = (ushort)(isToday ? 600 : 400), Wrap = TextWrap.NoWrap, MaxLines = 1,
            Color = isToday ? host.AccentInkProp : (Prop<ColorF>)Tok.TextPrimary,
            BrushTransitionMs = ClassicRecents.AccentTransitionMs,
        };
        var cell = new BoxEl
        {
            Width = RecentsLayout.CalCellW, Height = RecentsLayout.CalCellH, Shrink = 0f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
            Fill = host.DensityFillProp(day?.DensityLevel ?? 0), BrushTransitionMs = ClassicRecents.AccentTransitionMs,
            Role = hasRows ? AutomationRole.Button : AutomationRole.None,
            Focusable = hasRows, TabStop = hasRows, Cursor = hasRows ? CursorId.Hand : CursorId.Arrow,
            OnHoverMove = _ => host.HoverDay(date),
            OnClick = hasRows ? () => host.JumpIn(date, monthIndex) : null,
            OnFocusChanged = hasRows ? focused => host.FocusDay(focused, date) : null,
            Children = [numeral],
        };
        return ToolTip.Wrap(cell, host.CalendarTooltip(shape, date));
    }

    /// <summary>The "Quieter → Busier" key: five 12 × 12 swatches painted by the SAME fill function the cells use.</summary>
    internal static Element Legend(IRecentsHost host)
    {
        var kids = new Element[7];
        kids[0] = Caption(Loc.Get(Strings.Recents.Legend.Quieter)) with { Color = Tok.TextTertiary, MaxLines = 1, Shrink = 0f };
        for (int level = 1; level <= 5; level++)
            kids[level] = new BoxEl
            {
                Width = ClassicRecentsRules.SwatchEdge, Height = ClassicRecentsRules.SwatchEdge, Shrink = 0f, Corners = Radii.ControlAll,
                Fill = host.DensityFillProp(level), BrushTransitionMs = ClassicRecents.AccentTransitionMs,
            };
        kids[6] = Caption(Loc.Get(Strings.Recents.Legend.Busier)) with { Color = Tok.TextTertiary, MaxLines = 1, Shrink = 0f };
        return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, Shrink = 0f, Children = kids };
    }
}
