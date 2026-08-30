using System;
using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Features.Concerts;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The when-area drill-down flyout content (concerts v2 §3.5): a presentational panel with a <c>view</c> signal
/// that swaps between the ROOT (Anytime / Today / This weekend / Next weekend + a month list drilling in) and a MONTH
/// LEAF (All of {month}, its Fri–Sun weekends, and a Su–Sa day-grid calendar where the first tap is the start and the
/// second the end). Months are derived from <see cref="Now"/> — current month through +3, no hardcoded tables. Selecting
/// anything builds a <see cref="ConcertWhen"/> and hands it to <see cref="OnPick"/> (the bar closes the flyout on pick).
/// Phase 1: the apply button is plain localized text, NO per-row counts. Navigation only — no playback.</summary>
sealed class ConcertDateFlyout : Component
{
    public required Action<ConcertWhen> OnPick;
    public DateTimeOffset Now = DateTimeOffset.Now;
    public CultureInfo Culture = CultureInfo.CurrentCulture;

    readonly Signal<int> _view = new(-1);            // -1 = root; 0..3 = month-leaf index (offset from this month)
    readonly Signal<DateOnly?> _start = new(null);   // calendar range: first tap
    readonly Signal<DateOnly?> _end = new(null);     // calendar range: second tap
    bool _forward = true;                            // last drill direction → picks the page-slide recipe below

    public override Element Render()
    {
        int view = _view.Value;
        var today = DateOnly.FromDateTime(Now.LocalDateTime.Date);
        var firstThisMonth = new DateOnly(today.Year, today.Month, 1);
        // The root ⇄ month swap rides the app's standing page transition: keyed per view, drill-in slides forward
        // (enter +X / exit −X), back mirrors — the same PageSlide recipe ContentHost uses for page navigation.
        Element body = new BoxEl
        {
            Key = view < 0 ? "when-view:root" : "when-view:month:" + view,
            Animate = _forward ? MotionRecipes.PageSlideForward : MotionRecipes.PageSlideBack,
            Direction = 1, MinWidth = 0f,
            Children = [ view < 0 ? BuildRoot(firstThisMonth, today) : BuildMonth(firstThisMonth.AddMonths(view), today) ],
        };
        return new BoxEl
        {
            Direction = 1, Width = 320f, ClipToBounds = true,
            Padding = new Edges4(Spacing.S, Spacing.S, Spacing.S, Spacing.S),
            Children = [ body ],
        };
    }

    // ── root ─────────────────────────────────────────────────────────────────────────────────────────────────────────
    Element BuildRoot(DateOnly firstThisMonth, DateOnly today)
    {
        var rows = new List<Element>(9)
        {
            RowButton(Loc.Get(Strings.Concerts.Filter.Anytime), null, () => OnPick(ConcertWhen.Any)),
            RowButton(Loc.Get(Strings.Concerts.Filter.Today), null,
                () => PickPreset(ConcertWhenKind.Today, Loc.Get(Strings.Concerts.Filter.Today))),
            RowButton(Loc.Get(Strings.Concerts.Filter.ThisWeekend), null,
                () => PickPreset(ConcertWhenKind.ThisWeekend, Loc.Get(Strings.Concerts.Filter.ThisWeekend))),
            RowButton(Loc.Get(Strings.Concerts.Filter.NextWeekend), null,
                () => PickPreset(ConcertWhenKind.NextWeekend, Loc.Get(Strings.Concerts.Filter.NextWeekend))),
            Divider(),
        };
        for (int i = 0; i < 4; i++)
        {
            int idx = i;
            var month = firstThisMonth.AddMonths(i);
            rows.Add(RowButton(month.ToString("MMMM yyyy", Culture), Icons.ChevronRight,
                () => { _forward = true; _view.Value = idx; }));
        }
        return new BoxEl { Direction = 1, Gap = 2f, Children = rows.ToArray() };
    }

    void PickPreset(ConcertWhenKind kind, string name) =>
        OnPick(new ConcertWhen(kind, name, ConcertHub.PresetRange(kind, Now)));

    // ── month leaf ───────────────────────────────────────────────────────────────────────────────────────────────────
    Element BuildMonth(DateOnly first, DateOnly today)
    {
        var start = _start.Value;   // subscribe → calendar highlight + apply-button enablement
        var end = _end.Value;
        string monthName = first.ToString("MMMM", Culture);
        int daysInMonth = DateTime.DaysInMonth(first.Year, first.Month);
        var last = new DateOnly(first.Year, first.Month, daysInMonth);

        var rows = new List<Element>(12)
        {
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinHeight = 36f,
                Children =
                [
                    BackButton(),
                    BodyStrong(first.ToString("MMMM yyyy", Culture)) with { Color = Tok.TextPrimary, MaxLines = 1 },
                ],
            },
            RowButton(Strings.Concerts.Filter.AllOf(monthName), null,
                () => PickCustom(new ConcertDateRange(first, last), monthName)),
        };
        foreach (var (fri, sun) in Weekends(first))
        {
            var range = new ConcertDateRange(fri, sun);
            rows.Add(RowButton(Loc.Get(Strings.Concerts.Filter.Weekend) + " · " + ConcertHub.WhenLabel(range, Culture),
                null, () => PickCustom(range, Loc.Get(Strings.Concerts.Filter.Weekend))));
        }
        rows.Add(BuildCalendar(first, today, start, end));
        rows.Add(Footer(start));

        return new ScrollEl
        {
            ContentSized = true, MaxHeight = 400f,
            Content = new BoxEl { Direction = 1, Gap = Spacing.XS, Children = rows.ToArray() },
        };
    }

    void PickCustom(ConcertDateRange range, string name) =>
        OnPick(new ConcertWhen(ConcertWhenKind.Custom, name, range));

    Element Footer(DateOnly? start) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinHeight = 44f,
        Padding = new Edges4(Spacing.XS, Spacing.XS, Spacing.XS, 0f),
        Children =
        [
            Button.Standard(Loc.Get(Strings.Concerts.Filter.Clear), ClearRange),
            new BoxEl { Grow = 1f },
            Button.Accent(Loc.Get(Strings.Concerts.Filter.ShowEvents), ApplyCalendar, isEnabled: start is not null),
        ],
    };

    void ClearRange()
    {
        _start.Value = null;
        _end.Value = null;
    }

    void ApplyCalendar()
    {
        if (_start.Peek() is not { } s) return;
        var e = _end.Peek() ?? s;
        OnPick(new ConcertWhen(ConcertWhenKind.Custom, Loc.Get(Strings.Concerts.Filter.Custom),
            new ConcertDateRange(s, e)));
    }

    // ── calendar ─────────────────────────────────────────────────────────────────────────────────────────────────────
    Element BuildCalendar(DateOnly first, DateOnly today, DateOnly? start, DateOnly? end)
    {
        int days = DateTime.DaysInMonth(first.Year, first.Month);
        int lead = (int)first.DayOfWeek;   // Sunday = 0 … Saturday = 6 (the Su-start grid offset)
        var abbr = Culture.DateTimeFormat.AbbreviatedDayNames;

        var grid = new List<Element>(7);
        var header = new Element[7];
        for (int i = 0; i < 7; i++) header[i] = HeaderCell(Shorten(abbr[i]));
        grid.Add(new BoxEl { Direction = 0, Gap = 4f, Children = header });

        var week = new List<Element>(7);
        for (int i = 0; i < lead; i++) week.Add(EmptyCell());
        for (int d = 1; d <= days; d++)
        {
            var day = new DateOnly(first.Year, first.Month, d);
            bool past = day < today;
            bool selected = (start is { } s0 && day == s0) || (end is { } e0 && day == e0);
            bool inRange = start is { } s1 && end is { } e1 && day > s1 && day < e1;
            week.Add(DayCell(d, past, selected, inRange, () => TapDay(day)));
            if (week.Count == 7)
            {
                grid.Add(new BoxEl { Direction = 0, Gap = 4f, Children = week.ToArray() });
                week = new List<Element>(7);
            }
        }
        if (week.Count > 0)
        {
            while (week.Count < 7) week.Add(EmptyCell());
            grid.Add(new BoxEl { Direction = 0, Gap = 4f, Children = week.ToArray() });
        }
        return new BoxEl { Direction = 1, Gap = 4f, Children = grid.ToArray() };
    }

    void TapDay(DateOnly day)
    {
        var s = _start.Peek();
        var e = _end.Peek();
        if (s is null || e is not null) { _start.Value = day; _end.Value = null; }   // fresh selection
        else if (day < s) { _start.Value = day; }                                    // restart before the anchor
        else { _end.Value = day; }                                                   // close the range
    }

    static Element HeaderCell(string text) => new BoxEl
    {
        Width = 38f, Height = 20f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children = [ Caption(text) with { Color = Tok.TextSecondary, MaxLines = 1 } ],
    };

    static Element EmptyCell() => new BoxEl { Width = 38f, Height = 32f, Shrink = 0f };

    Element DayCell(int day, bool past, bool selected, bool inRange, Action onTap)
    {
        ColorF fill = selected ? Tok.AccentDefault : inRange ? Tok.AccentSubtle : ColorF.Transparent;
        ColorF fg = selected ? Tok.TextOnAccentPrimary : past ? Tok.TextDisabled : Tok.TextPrimary;
        var cell = new BoxEl
        {
            Width = 38f, Height = 32f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(Radii.Control), Fill = fill,
            Children = [ Body(day.ToString(Culture)) with { Color = fg, MaxLines = 1 } ],
        };
        if (past) return cell with { HitTestVisible = false };
        return cell with
        {
            HoverFill = selected ? Tok.AccentSecondary : Tok.FillSubtleSecondary,
            PressedFill = selected ? Tok.AccentTertiary : Tok.FillSubtleTertiary,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onTap,
        };
    }

    // ── shared row chrome ────────────────────────────────────────────────────────────────────────────────────────────
    Element RowButton(string label, string? trailingGlyph, Action onClick)
    {
        var children = new List<Element>(2)
        {
            Body(label) with
            {
                Color = Tok.TextPrimary, Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
        };
        if (trailingGlyph is not null) children.Add(Icon(trailingGlyph, 14f, Tok.TextSecondary) with { Shrink = 0f });
        return new BoxEl
        {
            Direction = 0, MinHeight = 40f, AlignItems = FlexAlign.Center, Gap = Spacing.S,
            Padding = new Edges4(Spacing.M, Spacing.XS, Spacing.M, Spacing.XS),
            Corners = CornerRadius4.All(Radii.Control),
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
            Children = children.ToArray(),
        }.Interactive(Interaction.Subtle);
    }

    Element BackButton() => new BoxEl
    {
        Width = 28f, Height = 28f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(Radii.Control),
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
        OnClick = () => { _forward = false; _view.Value = -1; },
        Children = [ Icon(Icons.ChevronLeft, 14f, Tok.TextSecondary) ],
    }.Interactive(Interaction.Subtle);

    static Element Divider() => new BoxEl
    {
        AlignSelf = FlexAlign.Stretch, Padding = new Edges4(0f, Spacing.XS, 0f, Spacing.XS),
        Children = [ new BoxEl { Height = 1f, AlignSelf = FlexAlign.Stretch, Fill = Tok.StrokeSurfaceDefault } ],
    };

    // The month's Fri–Sun weekends, computed from DateOnly math (a weekend spilling past month-end keeps its Sun).
    static IEnumerable<(DateOnly Fri, DateOnly Sun)> Weekends(DateOnly first)
    {
        var d = first;
        while (d.DayOfWeek != DayOfWeek.Friday) d = d.AddDays(1);
        for (; d.Month == first.Month; d = d.AddDays(7))
            yield return (d, d.AddDays(2));
    }

    static string Shorten(string s) => s.Length <= 2 ? s : s.Substring(0, 2);
}

/// <summary>The where-pill drill-down (concerts v2 §3.5, Phase 1): a compact anchored panel with the city-search and
/// use-my-location entries (both funnel into the page's existing location picker / OS-consent flow) plus three radius
/// radio rows (25 / 50 / 100 km) with a check on the active value. Radius selection keeps the panel open and re-reads
/// <see cref="Radius"/> (the check moves); the two location rows close it and hand off. Navigation only.</summary>
sealed class ConcertWhereFlyout : Component
{
    public required IReadSignal<int> Radius;
    public required Action<int> OnRadius;
    public required Action OnSearchCities;
    public required Action OnUseMyLocation;

    static readonly int[] Options = { 25, 50, 100 };

    public override Element Render()
    {
        int radius = Radius.Value;   // subscribe → the active-radius check
        var kids = new List<Element>(7)
        {
            ActionRow(Icons.MapPin, Loc.Get(Strings.Concerts.Location.SearchCities), Icons.ChevronRight, OnSearchCities),
            ActionRow(Icons.MapPin, Loc.Get(Strings.Concerts.Location.UseMine), null, OnUseMyLocation),
            Divider(),
        };
        foreach (int km in Options)
        {
            int value = km;
            kids.Add(RadioRow(Strings.Concerts.Filter.WithinKm(km), km == radius, () => OnRadius(value)));
        }
        return new BoxEl
        {
            Direction = 1, Width = 264f, Gap = 2f,
            Padding = new Edges4(Spacing.S, Spacing.S, Spacing.S, Spacing.S),
            Children = kids.ToArray(),
        };
    }

    static Element ActionRow(string glyph, string label, string? trailingGlyph, Action onClick)
    {
        var children = new List<Element>(3)
        {
            Icon(glyph, 16f, Tok.TextSecondary) with { Shrink = 0f },
            Body(label) with
            {
                Color = Tok.TextPrimary, Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
        };
        if (trailingGlyph is not null) children.Add(Icon(trailingGlyph, 14f, Tok.TextSecondary) with { Shrink = 0f });
        return new BoxEl
        {
            Direction = 0, MinHeight = 40f, AlignItems = FlexAlign.Center, Gap = Spacing.S,
            Padding = new Edges4(Spacing.M, Spacing.XS, Spacing.M, Spacing.XS),
            Corners = CornerRadius4.All(Radii.Control),
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
            Children = children.ToArray(),
        }.Interactive(Interaction.Subtle);
    }

    static Element RadioRow(string label, bool active, Action onClick)
    {
        Element check = active
            ? Icon(Icons.Check, 14f, Tok.AccentTextPrimary) with { Shrink = 0f }
            : new BoxEl { Width = 14f, Height = 14f, Shrink = 0f };
        return new BoxEl
        {
            Direction = 0, MinHeight = 40f, AlignItems = FlexAlign.Center, Gap = Spacing.S,
            Padding = new Edges4(Spacing.M, Spacing.XS, Spacing.M, Spacing.XS),
            Corners = CornerRadius4.All(Radii.Control),
            Role = AutomationRole.ToggleButton, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
            Children =
            [
                Body(label) with
                {
                    Color = active ? Tok.AccentTextPrimary : Tok.TextPrimary, Weight = (ushort)(active ? 600 : 400),
                    Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
                check,
            ],
        }.Interactive(Interaction.Subtle);
    }

    static Element Divider() => new BoxEl
    {
        AlignSelf = FlexAlign.Stretch, Padding = new Edges4(0f, Spacing.XS, 0f, Spacing.XS),
        Children = [ new BoxEl { Height = 1f, AlignSelf = FlexAlign.Stretch, Fill = Tok.StrokeSurfaceDefault } ],
    };
}
