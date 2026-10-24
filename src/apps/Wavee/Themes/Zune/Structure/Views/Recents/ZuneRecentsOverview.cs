// ── Themes/Zune/Structure/Views/Recents/ZuneRecentsOverview.cs — Zune's Recents overview and calendar (themes plan 6.3; P5 Z7) ─────────────────
//
// Board Z-Recents (the 300 column), -Small (220), -Huge (420), -Compact (the month on its own). The model (`RecentsCalendar`: the months, the density
// levels, the top item of a day), the readout and tooltip phrases and the jump verbs are the host's.
//
//   THE OVERVIEW beside the list (Small and up): the heading, then the newest months as grids of seven equal columns (the cell height is the tier's),
//   each under its "october · 212 plays so far" line, the heat key, "jump to today" and the busiest day. A day jumps the list beside it to that day
//   (`IRecentsHost.JumpIn`: nothing is zoomed out).
//   THE CALENDAR is the zoomed-out view of the semantic zoom, reached by the band's "overview" word or a day heading at every tier: the readout of the
//   day under the pointer, the heat key, and every month as a card (numerals in the cells; 40 tall targets at Compact).
//
// The heat is the style's accent over the surface wash (the board's accent at 35 / 65 / 100 %), bound, so a theme change repaints it without a render.
// Today is ONE cue: a 2-DIP ring. A lead or trail day is a blank spacer; only a day the list has rows for is a button.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using Z = Wavee.Themes.Zune.ZuneRecentsRules;

namespace Wavee.Themes.Zune;

/// <summary>The overview column beside the list.</summary>
internal sealed class ZuneRecentsOverview(IRecentsHost host) : Component
{
    public override Element Render()
    {
        int epoch = host.ShapeEpoch.Value;
        _ = host.Epoch.Value;
        var shape = host.ShapeNow;
        var calendar = shape.Calendar;
        var facts = UseContext(ThemeFactsContext.Signal);
        var layout = ZuneRecentsTiers.Of(facts is null ? TierFacts.Unknown : facts.Value.Tiers);

        TextEl head = layout.AsideHead switch
        {
            ZuneRecentsHead.Large => R.Type.PageHero(Loc.Get(Strings.Recents.Overview)),
            ZuneRecentsHead.Standard => R.Type.Heading(Loc.Get(Strings.Recents.Overview)),
            _ => R.Type.QuickplayHead(Loc.Get(Strings.Recents.Overview)),
        };
        var kids = new List<Element>(8)
        {
            head with { Case = R.Case.For(LabelKind.Heading), MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
        };
        int shown = Math.Min(layout.AsideMonths, calendar.Months.Length);
        for (int i = 0; i < shown; i++)
        {
            // A month with nothing played collapses to its line ("october · nothing played"): no blank grid reserves the room.
            bool played = calendar.Months[i].TotalPlays > 0;
            kids.Add(new BoxEl
            {
                Key = Z.MonthPrefix + FormatCache.Int(calendar.Months[i].Year) + ":" + FormatCache.Int(calendar.Months[i].Month),
                Direction = 1, Gap = Spacing.S, MinWidth = 0f,
                Children = played
                    ? [
                        ZuneRecentsCalendarParts.MonthLine(calendar.Months[i], host.Culture),
                        ZuneRecentsCalendarParts.MonthGrid(host, shape, i, layout.CellH, layout.GridGap, weekdays: false, numerals: false),
                    ]
                    : [ZuneRecentsCalendarParts.MonthLine(calendar.Months[i], host.Culture)],
            });
        }
        if (calendar.Months.Length > 0)
        {
            kids.Add(new BoxEl
            {
                // Wraps where the key and the word do not share the aside's width (Small's 220): "jump to today" drops under the key whole.
                Direction = 0, Wrap = true, AlignItems = FlexAlign.Center, Gap = Spacing.M, MinWidth = 0f,
                Children = [ZuneRecentsCalendarParts.Legend(), new BoxEl { Grow = 1f, MinWidth = 0f }, ZuneRecentsCalendarParts.JumpToday(host)],
            });
            if (ZuneRecentsCalendarParts.Busiest(calendar.Months[0], host.Culture) is { } busiest)
                kids.Add(busiest);
        }
        return ScrollViewOf(host.ScrollScope + "recents-overview:" + FormatCache.Int(epoch),
            new BoxEl
            {
                Direction = 1, Gap = Z.AsideGap, MinWidth = 0f, OnPointerExit = host.ResetCalendarDayAction,
                Padding = new Edges4(0f, Z.AsideTop, 0f, Z.PageBottom),
                Children = kids.ToArray(),
            });
    }

    static Element ScrollViewOf(string scrollKey, Element content)
        => Ui.ScrollView(content) with { Grow = 1f, Basis = 0f, MinHeight = 0f, MinWidth = 0f, ScrollKey = scrollKey, AutoEdgeFade = true };
}

/// <summary>The zoomed-out view of the semantic zoom: the readout and the heat key over a grid of month cards. Its scroll key carries the shape epoch:
/// a re-cut forgets.</summary>
internal sealed class ZuneRecentsCalendar : Component
{
    readonly IRecentsHost _host;
    readonly int _shapeEpoch;
    readonly Func<int, Element> _monthCard;
    readonly Func<int, string> _monthKey;

    public ZuneRecentsCalendar(IRecentsHost host, int shapeEpoch)
    {
        _host = host;
        _shapeEpoch = shapeEpoch;
        _monthKey = MonthKey;
        _monthCard = i => Embed.Comp(() => new ZuneRecentsMonthCard(host, i)) with { Key = MonthKey(i) };
    }

    string MonthKey(int i)
    {
        var months = _host.ShapeNow.Calendar.Months;
        return (uint)i < (uint)months.Length
            ? Z.MonthPrefix + FormatCache.Int(months[i].Year) + ":" + FormatCache.Int(months[i].Month)
            : Z.MonthPrefix + "none";
    }

    public override Element Render()
    {
        var host = _host;
        var calendar = host.ShapeNow.Calendar;
        var facts = UseContext(ThemeFactsContext.Signal);
        var layout = ZuneRecentsTiers.Of(facts is null ? TierFacts.Unknown : facts.Value.Tiers);
        float cardH = ZuneRecentsCalendarParts.CardHeight(RecentsView.MaxWeeks(calendar), layout.CalendarCellH);
        Element grid = ItemsView.Create(calendar.Months.Length, _monthCard,
            RepeatLayout.GridFit(Z.MonthMinW, layout.CalendarGap, cardH),
            new ListOptions
            {
                SelectionMode = ItemsSelectionMode.None, Selector = SelectorVisual.None,
                Controller = host.CalendarController, Grow = 1f, KeyOf = _monthKey,
                Scroll = new ScrollOptions { ScrollKey = host.ScrollScope + "recents-calendar:" + FormatCache.Int(_shapeEpoch), AutoEdgeFade = true },
            });
        return new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Gap = Z.AsideGap,
            OnPointerExit = host.ResetCalendarDayAction,
            Children = [Embed.Comp(() => new ZuneRecentsCalendarBand(host)), grid],
        };
    }
}

/// <summary>The hovered day over its readout, the heat key and "jump to today" beside them.</summary>
internal sealed class ZuneRecentsCalendarBand(IRecentsHost host) : Component
{
    public override Element Render()
    {
        DateOnly selected = host.CalendarDay.Value;
        _ = host.Epoch.Value;
        var shape = host.ShapeNow;
        return new BoxEl
        {
            Direction = 0, Shrink = 0f, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.L,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XXS,
                    Children =
                    [
                        R.Type.TrackTitle(selected.ToString("dddd d MMMM", host.Culture)) with
                        { Color = Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                        R.Type.DenseMeta(host.CalendarReadout(shape, selected)) with
                        { Color = Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                    ],
                },
                ZuneRecentsCalendarParts.Legend(),
                ZuneRecentsCalendarParts.JumpToday(host),
            ],
        };
    }
}

/// <summary>One month card of the zoomed-out calendar: its line and a grid with the weekday band and the numerals.</summary>
internal sealed class ZuneRecentsMonthCard(IRecentsHost host, int monthIndex) : Component
{
    public override Element Render()
    {
        var shape = host.ShapeNow;
        var months = shape.Calendar.Months;
        if ((uint)monthIndex >= (uint)months.Length) return new BoxEl { Width = Z.MonthMinW };
        var facts = UseContext(ThemeFactsContext.Signal);
        var layout = ZuneRecentsTiers.Of(facts is null ? TierFacts.Unknown : facts.Value.Tiers);
        return new BoxEl
        {
            Direction = 1, Gap = Z.MonthTitleGap, MinWidth = 0f, OnPointerExit = host.ResetCalendarDayAction,
            Children =
            [
                new BoxEl { Height = RecentsLayout.CalTitleH, Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = [ZuneRecentsCalendarParts.MonthLine(months[monthIndex], host.Culture)] },
                ZuneRecentsCalendarParts.MonthGrid(host, shape, monthIndex, layout.CalendarCellH, RecentsLayout.CalGap, weekdays: true, numerals: true),
            ],
        };
    }
}

/// <summary>The calendar's cell, grid, lines and key.</summary>
internal static class ZuneRecentsCalendarParts
{
    static Prop<ColorF>[]? s_heat;

    /// <summary>The heat's paint for a level 0..5, bound: the quiet wash at 0, the style's accent at the level's alpha above it.</summary>
    static Prop<ColorF> Heat(int level)
    {
        var heat = s_heat;
        if (heat is null)
        {
            heat = new Prop<ColorF>[Z.HeatLevels];
            for (int l = 0; l < heat.Length; l++)
            {
                int level0 = l;
                heat[l] = level0 == 0
                    ? Prop.Of(static () => (ColorF)Tok.FillSubtleSecondary)
                    : Prop.Of(() => RecentsLayout.DensityFill(level0, R.Ink.AccentAction, Z.HeatFloor));
            }
            s_heat = heat;
        }
        return heat[Math.Clamp(level, 0, heat.Length - 1)];
    }

    /// <summary>A month card's height: its title, the gap, the weekday band and <paramref name="weeks"/> rows of <paramref name="cellH"/> cells.</summary>
    internal static float CardHeight(int weeks, float cellH)
        => RecentsLayout.CalTitleH + Z.MonthTitleGap + RecentsLayout.CalHeaderH + RecentsLayout.CalGap + weeks * (cellH + RecentsLayout.CalGap) - RecentsLayout.CalGap;

    /// <summary>"october · 212 plays so far": the month, its plays (or "Nothing played") and, for the current month, "So far". The line is words and a
    /// month name, so the label case role lowers it (the board writes it lowercase).</summary>
    internal static Element MonthLine(RecentsCalendarMonth month, CultureInfo culture)
    {
        DateOnly first = new(month.Year, month.Month, 1);
        string meta = month.TotalPlays > 0
            ? Strings.Recents.PlayCount(month.TotalPlays) + (month.IsCurrentMonth ? " · " + Loc.Get(Strings.Recents.SoFar) : "")
            : Loc.Get(Strings.Recents.NothingPlayed);
        return R.Type.DenseMeta(first.ToString("MMMM", culture) + " · " + meta) with
        {
            Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
    }

    /// <summary>The busiest day of a month ("busiest day · oct 6 · 31 plays"), or null for a month with none.</summary>
    internal static Element? Busiest(RecentsCalendarMonth month, CultureInfo culture)
    {
        if (month.BusiestDay is not { } day || month.BusiestDayPlays <= 0) return null;
        return R.Type.MicroMeta(Loc.Get(Strings.Theme.Zune.Collection.Recents.BusiestDay) + " · " + day.ToString("MMM d", culture) + " · "
                                + Strings.Recents.PlayCount(month.BusiestDayPlays)) with
        {
            Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
    }

    /// <summary>"jump to today": a link word that brings the list (or the zoomed-out calendar) to today.</summary>
    internal static Element JumpToday(IRecentsHost host) => new BoxEl
    {
        Key = "zune-recents-today", Direction = 0, Shrink = 1f, MinWidth = 0f, AlignItems = FlexAlign.Center, MinHeight = Controls.IconButtonSize,
        Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true, AllowFocusOnInteraction = false,
        OnClick = () => host.JumpIn(host.Today, 0),
        Children =
        [
            R.Type.DenseMeta(Loc.Get(Strings.Theme.Zune.Collection.Recents.JumpToToday)) with
            {
                Color = Tok.TextSecondary, HoverColor = Tok.TextPrimary, Case = R.Case.For(LabelKind.Command), MaxLines = 1, Wrap = TextWrap.NoWrap,
                Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            },
        ],
    };

    /// <summary>"quieter ▢▢▢▢ busier": the key's swatches are painted by the SAME fill function the cells use.</summary>
    internal static Element Legend()
    {
        var swatches = new Element[Z.KeyLevels.Length];
        for (int i = 0; i < swatches.Length; i++)
            swatches[i] = new BoxEl { Width = Z.SwatchEdge, Height = Z.SwatchEdge, Shrink = 0f, Fill = Heat(Z.KeyLevels[i]) };
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Shrink = 0f,
            Children =
            [
                R.Type.MicroMeta(Loc.Get(Strings.Recents.Legend.Quieter)) with { Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Shrink = 0f },
                new BoxEl { Direction = 0, Gap = Z.SwatchGap, Shrink = 0f, Children = swatches },
                R.Type.MicroMeta(Loc.Get(Strings.Recents.Legend.Busier)) with { Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Shrink = 0f },
            ],
        };
    }

    /// <summary>One month as seven equal columns of <paramref name="cellH"/> cells <paramref name="gap"/> apart (a stretchy width, a FIXED height), with
    /// the culture-rotated weekday band over it when <paramref name="weekdays"/>.</summary>
    internal static Element MonthGrid(IRecentsHost host, RecentsShape shape, int monthIndex, float cellH, float gap, bool weekdays, bool numerals)
    {
        var month = shape.Calendar.Months[monthIndex];
        var culture = host.Culture;
        DateOnly today = host.Today;
        var rows = new List<Element>(8);
        if (weekdays)
        {
            var names = new Element[Z.Week];
            var firstDay = culture.DateTimeFormat.FirstDayOfWeek;
            for (int i = 0; i < Z.Week; i++)
                names[i] = new BoxEl
                {
                    Grow = 1f, Basis = 0f, MinWidth = 0f, Height = RecentsLayout.CalHeaderH, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children =
                    [
                        R.Type.MicroMeta(culture.DateTimeFormat.AbbreviatedDayNames[((int)firstDay + i) % Z.Week]) with
                        { Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Wrap = TextWrap.NoWrap, MinWidth = 0f },
                    ],
                };
            rows.Add(new BoxEl { Direction = 0, Gap = gap, MinWidth = 0f, Children = names });
        }
        DateOnly first = new(month.Year, month.Month, 1);
        DateOnly gridFirst = first.AddDays(-month.FirstDayOffset);
        for (int week = 0; week < month.WeekCount; week++)
        {
            var cells = new Element[Z.Week];
            for (int column = 0; column < Z.Week; column++)
                cells[column] = Cell(host, shape, gridFirst.AddDays(week * Z.Week + column), month, today, monthIndex, cellH, numerals);
            rows.Add(new BoxEl { Direction = 0, Gap = gap, MinWidth = 0f, Children = cells });
        }
        return new BoxEl { Direction = 1, Gap = gap, MinWidth = 0f, Children = rows.ToArray() };
    }

    /// <summary>One cell: a flat plate whose fill is the day's heat. A lead or trail day is a BLANK spacer; today wears a ring; only a day the list has
    /// rows for is a button (it jumps the list, or zooms in).</summary>
    static Element Cell(IRecentsHost host, RecentsShape shape, DateOnly date, RecentsCalendarMonth month, DateOnly today, int monthIndex, float cellH, bool numerals)
    {
        if (date.Year != month.Year || date.Month != month.Month)
            return new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, Height = cellH };

        var day = RecentsLayout.CalendarDay(shape.Calendar, date);
        bool isToday = date == today;
        bool hasRows = RecentsLayout.HeaderFlatFor(shape.Sections, date) >= 0;
        int level = day?.DensityLevel ?? 0;
        var cell = new BoxEl
        {
            Grow = 1f, Basis = 0f, MinWidth = 0f, Height = cellH,
            Corners = R.Shape.All(ShapeRole.Tile), Fill = Heat(level),
            BorderWidth = isToday ? Z.TodayRing : 0f, BorderColor = Tok.TextPrimary,
            Direction = 1, Justify = FlexJustify.End, AlignItems = FlexAlign.Start,
            Padding = numerals ? new Edges4(Spacing.XS, 0f, 0f, Spacing.XXS) : default,
            Role = hasRows ? AutomationRole.Button : AutomationRole.None,
            Focusable = hasRows, TabStop = hasRows, Cursor = hasRows ? CursorId.Hand : CursorId.Arrow,
            OnHoverMove = _ => host.HoverDay(date),
            OnClick = hasRows ? () => host.JumpIn(date, monthIndex) : null,
            OnFocusChanged = hasRows ? focused => host.FocusDay(focused, date) : null,
            Children = numerals
                ? [R.Type.MicroMeta(FormatCache.Int(date.Day)) with
                    { Color = level >= Z.NumeralInkFrom ? Prop.Of(static () => ColorContrast.PickContrast(R.Ink.AccentAction)) : (Prop<ColorF>)Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap }]
                : [],
        };
        return ToolTip.Wrap(cell, host.CalendarTooltip(shape, date));
    }
}
