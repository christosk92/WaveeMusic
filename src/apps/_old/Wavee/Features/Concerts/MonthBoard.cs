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

/// <summary>The selected month inside the Tour Dashboard. It is one grouped Fluent surface rather than a card per
/// month: a full month heading, one/two chronological columns of quiet event rows, and a bounded Show-all footer.</summary>
sealed class MonthBoard : Component
{
    readonly ConcertMonthGroup _group;
    readonly HashSet<string> _nearby;
    readonly string? _artistName;
    readonly Action<string, string?> _go;
    readonly bool _wide;
    readonly HashSet<string> _expandedMonths;
    readonly HashSet<string> _expandedRuns;
    readonly Signal<int> _revision;
    readonly Action<NodeHandle> _onHeaderRealized;

    public MonthBoard(ConcertMonthGroup group, HashSet<string> nearby, string? artistName,
        Action<string, string?> go, bool wide, HashSet<string> expandedMonths, HashSet<string> expandedRuns,
        Signal<int> revision, Action<NodeHandle> onHeaderRealized)
    {
        _group = group;
        _nearby = nearby;
        _artistName = artistName;
        _go = go;
        _wide = wide;
        _expandedMonths = expandedMonths;
        _expandedRuns = expandedRuns;
        _revision = revision;
        _onHeaderRealized = onHeaderRealized;
    }

    public override Element Render()
    {
        _ = _revision.Value;
        var runs = _group.Runs;
        bool capped = !_expandedMonths.Contains(_group.Key) && runs.Count > ConcertScheduleShaping.TileCap;
        int visibleCount = capped ? ConcertScheduleShaping.TileCap : runs.Count;

        Element body;
        if (_wide && visibleCount > 1)
        {
            var split = ConcertScheduleShaping.BalancedColumns(runs, visibleCount);
            body = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Start, MinWidth = 0f,
                Children =
                [
                    Column(split.Left) with { Grow = 1f, Basis = 0f },
                    new BoxEl { Width = 1f, AlignSelf = FlexAlign.Stretch, Fill = Tok.StrokeDividerDefault },
                    Column(split.Right) with { Grow = 1f, Basis = 0f },
                ],
            };
        }
        else
        {
            var visible = new ConcertRun[visibleCount];
            for (int i = 0; i < visibleCount; i++) visible[i] = runs[i];
            body = Column(visible);
        }

        var children = new List<Element>(5) { Header(), Divider(), body };
        if (capped)
        {
            children.Add(Divider());
            children.Add(ShowAllFooter());
        }

        return new BoxEl { Direction = 1, MinWidth = 0f, Children = children.ToArray() };
    }

    Element Header()
    {
        var first = new DateTime(_group.Year, _group.Month, 1);
        string month = first.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
        int shows = _group.ShowCount;
        return new BoxEl
        {
            Direction = 0, MinWidth = 0f, MinHeight = 58f, AlignItems = FlexAlign.Center, Gap = Spacing.M,
            Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.M),
            OnRealized = _onHeaderRealized,
            Children =
            [
                new TextEl(month)
                {
                    // Subtitle (20/28/600) - a section header, one rung under the page heading. Was 22/650.
                    Size = 20f, LineHeight = 28f, Weight = 600, Color = Tok.TextPrimary, Grow = 1f, Basis = 0f, MinWidth = 0f,
                    MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
                Body(Strings.Concerts.Schedule.ShowCount(shows)) with
                { Color = Tok.TextSecondary, MaxLines = 1, Shrink = 0f },
            ],
        };
    }

    BoxEl Column(IReadOnlyList<ConcertRun> runs)
    {
        var children = new List<Element>(Math.Max(0, runs.Count * 2 - 1));
        for (int i = 0; i < runs.Count; i++)
        {
            if (i > 0) children.Add(Divider());
            children.Add(RunOrTile(runs[i]));
        }
        return new BoxEl { Direction = 1, MinWidth = 0f, Children = children.ToArray() };
    }

    Element RunOrTile(ConcertRun run)
    {
        bool near = IsNear(run);
        if (!run.IsMultiNight) return NightTile(run.First, near);

        if (_expandedRuns.Contains(run.Uri))
        {
            var nights = new List<Element>(run.NightCount * 2 - 1);
            for (int i = 0; i < run.NightCount; i++)
            {
                if (i > 0) nights.Add(Divider());
                nights.Add(NightTile(run.Nights[i], near));
            }
            return new BoxEl { Key = "run:" + run.Uri, Direction = 1, MinWidth = 0f, Children = nights.ToArray() };
        }

        return RunTile(run, near);
    }

    Element NightTile(Concert c, bool near)
    {
        var txt = ConcertScheduleShaping.TileText(c, _artistName);
        var secondary = new List<Element>(2);
        if (near) secondary.Add(Icon(Icons.MapPin, 12f, Tok.AccentTextPrimary) with { Shrink = 0f });
        if (txt.Secondary.Length > 0)
            secondary.Add(Body(txt.Secondary) with
            {
                Color = Tok.TextSecondary, Grow = 1f, Shrink = 1f, MinWidth = 0f,
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            });

        return new BoxEl
        {
            Key = c.Uri,
            Direction = 0, MinHeight = 68f, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.M,
            Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.S),
            Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = new Edges4(2f, 2f, 2f, 2f),
            Cursor = CursorId.Hand, OnClick = () => _go(ConcertRoutes.Detail(c.Uri), c.Title ?? c.Venue),
            Children =
            [
                NearRail(near),
                DayColumn(c.Date),
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = 2f, Justify = FlexJustify.Center,
                    Children =
                    [
                        BodyStrong(txt.Primary) with
                        { Color = Tok.TextPrimary, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        new BoxEl
                        { Direction = 0, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.XS, Children = secondary.ToArray() },
                    ],
                },
                Icon(Icons.ChevronRight, 16f, Tok.TextSecondary) with { Shrink = 0f },
            ],
        }.Interactive(Interaction.Subtle);
    }

    Element RunTile(ConcertRun run, bool near)
    {
        var c = run.First;
        var txt = ConcertScheduleShaping.TileText(c, _artistName);
        var meta = new List<Element>(4);
        if (near) meta.Add(Icon(Icons.MapPin, 12f, Tok.AccentTextPrimary) with { Shrink = 0f });
        meta.Add(Chip(run.NightCount + " nights"));
        if (txt.CityIsPrimary)
        {
            if (ConcertScheduleShaping.SupportActs(c, _artistName) is { Length: > 0 } support)
                meta.Add(Body(support) with
                { Color = Tok.TextSecondary, Grow = 1f, Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
        }
        else if (c.City.Length > 0)
        {
            meta.Insert(near ? 1 : 0, Body(c.City) with
            { Color = Tok.TextSecondary, Grow = 1f, Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
        }

        return new BoxEl
        {
            Key = "run:" + run.Uri,
            Direction = 0, MinHeight = 68f, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.M,
            Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.S),
            Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = new Edges4(2f, 2f, 2f, 2f),
            Cursor = CursorId.Hand,
            OnClick = () => { if (_expandedRuns.Add(run.Uri)) _revision.Value++; },
            Children =
            [
                NearRail(near),
                RunDayColumn(run),
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = 2f, Justify = FlexJustify.Center,
                    Children =
                    [
                        BodyStrong(txt.Primary) with
                        { Color = Tok.TextPrimary, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        new BoxEl
                        { Direction = 0, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.XS, Children = meta.ToArray() },
                    ],
                },
                Icon(Icons.ChevronDown, 16f, Tok.TextSecondary) with { Shrink = 0f },
            ],
        }.Interactive(Interaction.Subtle);
    }

    Element ShowAllFooter() => new BoxEl
    {
        Direction = 0, MinWidth = 0f, MinHeight = 44f, AlignItems = FlexAlign.Center,
        Justify = FlexJustify.Center, Gap = Spacing.XS,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
        OnClick = () => { if (_expandedMonths.Add(_group.Key)) _revision.Value++; },
        Children =
        [
            BodyStrong(Strings.Concerts.Schedule.ShowAll(_group.ShowCount)) with { Color = Tok.AccentTextPrimary, MaxLines = 1 },
            Icon(Icons.ChevronDown, 14f, Tok.AccentTextPrimary) with { Shrink = 0f },
        ],
    }.Interactive(Interaction.Subtle);

    static Element NearRail(bool near) => new BoxEl
    {
        Width = 3f, Height = 32f, Shrink = 0f,
        Corners = CornerRadius4.All(2f), Fill = near ? Tok.AccentDefault : ColorF.Transparent,
    };

    static Element DayColumn(DateTimeOffset d)
    {
        var c = CultureInfo.CurrentCulture;
        return new BoxEl
        {
            Width = 44f, Shrink = 0f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = 1f,
            Children =
            [
                BodyStrong(d.Day.ToString(c)) with { Color = Tok.TextPrimary, MaxLines = 1 },
                WaveeType.Eyebrow(d.ToString("ddd", c)) with { Color = Tok.TextSecondary, MaxLines = 1 },
            ],
        };
    }

    static Element RunDayColumn(ConcertRun run)
    {
        var c = CultureInfo.CurrentCulture;
        return new BoxEl
        {
            Width = 60f, Shrink = 0f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = 1f,
            Children =
            [
                BodyStrong(run.First.Date.Day.ToString(c) + "–" + run.Last.Date.Day.ToString(c)) with
                { Color = Tok.TextPrimary, MaxLines = 1 },
                WaveeType.Eyebrow(run.First.Date.ToString("ddd", c) + "–" + run.Last.Date.ToString("ddd", c)) with
                { Color = Tok.TextSecondary, MaxLines = 1 },
            ],
        };
    }

    static Element Chip(string text) => new BoxEl
    {
        AlignSelf = FlexAlign.Center, Shrink = 0f, Padding = new Edges4(Spacing.S, 1f, Spacing.S, 1f),
        Corners = CornerRadius4.All(Radii.Full), Fill = Tok.AccentSubtle,
        Children = [ Caption(text) with { Color = Tok.AccentTextPrimary, MaxLines = 1 } ],
    };

    static Element Divider() => new BoxEl { Height = 1f, MinWidth = 0f, Fill = Tok.StrokeDividerDefault };

    bool IsNear(ConcertRun run)
    {
        for (int i = 0; i < run.Nights.Count; i++)
            if (_nearby.Contains(run.Nights[i].Uri)) return true;
        return false;
    }
}
