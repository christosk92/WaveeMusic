// ── Entities/User.Facts.Look.cs ────────────────────────────────────────────────────────────────────────────────────
// The facts bento's FLUENT pictures (`StyleKeys.DetailFacts`): the cards it always drew (ch 07 §3), moved out of the components so a
// theme can draw its own (Zune: `ZuneFacts.Style`). Classic registers this style; WinUI takes it through the stand-in.
//
// Role: UI
// Owner: O
// Spec: ch 07 §3 (fact card, card head, numeral, name row, legend, pill), §0 items 7-10

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Render;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public readonly partial struct User
{
    /// <summary>The Fluent facts bento: white (layer) cards with a hairline and the card elevation, Title numerals, rounded bars. Every
    /// member reads its tokens where the picture is built.</summary>
    public static readonly DetailFactsStyle FactsFluentStyle = new()
    {
        SectionGap = static () => Spacing.S,
        StackPadding = static outer => outer ? new Edges4(Spacing.L, Spacing.S, Spacing.L, Spacing.L) : Edges4.All(0f),
        Section = FluentCard,
        SectionInset = static () => Spacing.M,
        Head = FluentCardHead,
        Figure = FluentNumeral,
        FigureRow = static (figure, chart) => new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.End, MinWidth = 0f, Children = [figure, chart],
        },
        Spark = static () => SparkBars.DefaultStyle,
        Density = static () => DensityPlot.DefaultStyle with
        {
            LineWidth = 1.25f, AxisFontSize = 10f, Marker = Tok.AccentTextPrimary with { A = 0.45f },
        },
        ArtistRow = FluentNameRow,
        ArtistGap = static () => Spacing.S,
        ArtistRowGap = static () => Spacing.XXS,
        Disclosure = FluentDisclosure,
        LegendEntry = FluentLegendEntry,
        LegendGap = static () => Spacing.XS,
        BlendBar = static () => new DetailFactsBar(8f, CornerRadius4.All(4f), 2f),
        Rule = static () => Tok.StrokeCardDefault,
        Pill = FluentPill,
        PillGap = static () => 6f,
        Prose = static (text, secondary, lines) => Caption(text) with
        {
            Color = secondary ? Tok.TextSecondary : Tok.TextTertiary, Wrap = lines > 1 ? TextWrap.Wrap : TextWrap.NoWrap, MaxLines = lines,
            Trim = TextTrim.CharacterEllipsis,
        },
        FootnotePadding = static () => new Edges4(Spacing.XXS, Spacing.XXS, Spacing.XXS, 0f),
        Command = static (label, click) => Button.Create(label, click, ButtonAppearance.Standard, ControlSize.Small) with { Shrink = 0f },
    };

    /// <summary>ch 07 §3 "fact card": pad 12/10/12/11, gap 6, radius 8, card fill + stroke + elevation.</summary>
    static BoxEl FluentCard(string key, Element head, Element body) => new()
    {
        Direction = 1, Gap = 6f, MinWidth = 0f,
        Padding = new Edges4(Spacing.M, 10f, Spacing.M, 11f),
        Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardDefault,
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Shadow = Elevation.Card,
        Children = [head, body],
    };

    /// <summary>The card header: the eyebrow GROWS so the fact is pushed to the trailing edge without a spacer node.</summary>
    static Element FluentCardHead(string title, string? trailing)
    {
        var label = R.Type.Eyebrow(title) with
        {
            Color = Tok.TextTertiary, Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        };
        Element[] kids = trailing is { Length: > 0 } fact
            ? [label, Caption(fact) with { Color = Tok.TextTertiary, Shrink = 0f, MaxLines = 1 }]
            : [label];
        return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f, Children = kids };
    }

    /// <summary>The big numeral over its caption: Title 28/36/600 in a ZStack keyed on the value (M-9 TextSwap).</summary>
    static Element FluentNumeral(string value, string caption, float width)
    {
        var box = new BoxEl
        {
            Direction = 1, Gap = Spacing.XS, Shrink = 0f,
            Children =
            [
                ZStack(new BoxEl { Key = "v:" + value, Animate = MotionRecipes.TextSwap, Children = [Title(value) with { MaxLines = 1 }] }),
                Caption(caption) with { Color = Tok.TextTertiary, MaxLines = 1 },
            ],
        };
        return float.IsNaN(width) ? box : box with { Width = width };
    }

    static Element FluentNameRow(string name, string count, bool lit, Action? onClick)
    {
        bool live = onClick is not null;
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, MinWidth = 0f,
            Padding = new Edges4(2f, 1f, 2f, 1f), Corners = CornerRadius4.All(4f),
            Role = live ? AutomationRole.Button : AutomationRole.None,
            Focusable = live, Cursor = live ? CursorId.Hand : CursorId.Arrow,
            FocusVisualMargin = live ? Design.FocusInsetRow : null,
            Fill = lit ? Tok.AccentSubtle : ColorF.Transparent,
            HoverFill = !live ? ColorF.Transparent : lit ? Tok.AccentSecondary : Tok.FillSubtleSecondary,
            PressedFill = live ? Tok.FillSubtleTertiary : ColorF.Transparent,
            HoverScale = live ? R.Motion.ScaleSubtle.Hover : 1f,
            PressScale = live ? R.Motion.ScaleSubtle.Press : 1f,
            HoverDurationMs = MotionTok.ControlFaster.DurationMs, HoverEasing = MotionTok.ControlFaster.Easing,
            OnClick = onClick,
            Children =
            [
                Caption(name) with
                {
                    Weight = 600, Color = lit ? Tok.AccentTextPrimary : Tok.TextPrimary,
                    Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
                Caption("·") with { Color = Tok.TextTertiary, Shrink = 0f },
                Caption(count) with { Color = Tok.TextTertiary, Shrink = 0f, MaxLines = 1 },
            ],
        };
    }

    /// <summary>The artists' "N more" row and the blend legend's closing "N more  14 %" ⇄ "Show less" entry.</summary>
    static Element FluentDisclosure(DetailFactsDisclosure d)
    {
        var kids = new List<Element>(3)
        {
            Sidebar.Chevron.Disclosure(d.Open),
            Caption(d.Label) with { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
        };
        if (d.Trailing is { } trailing) kids.Add(Caption(trailing) with { Color = Tok.TextTertiary, MaxLines = 1 });
        return new BoxEl
        {
            Key = d.Key, Direction = 0, AlignItems = FlexAlign.Center, Gap = 6f, Shrink = 0f,
            Padding = d.InLegend ? new Edges4(4f, 2f, 4f, 2f) : new Edges4(2f, 1f, 2f, 1f), Corners = CornerRadius4.All(4f),
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            FocusVisualMargin = Design.FocusInsetRow,
            HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            HoverScale = R.Motion.ScaleSubtle.Hover, PressScale = R.Motion.ScaleSubtle.Press,
            HoverDurationMs = MotionTok.ControlFaster.DurationMs, HoverEasing = MotionTok.ControlFaster.Easing,
            OnClick = d.Toggle, OnKeyDown = d.KeyDown,
            Children = kids.ToArray(),
        };
    }

    static Element FluentLegendEntry(ColorF ink, string label, string share, bool lit, Action onClick) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = 6f, Shrink = 0f,
        Padding = new Edges4(4f, 2f, 4f, 2f), Corners = CornerRadius4.All(4f),
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
        FocusVisualMargin = Design.FocusInsetRow,
        Fill = lit ? Tok.AccentSubtle : ColorF.Transparent,
        HoverFill = lit ? Tok.AccentSecondary : Tok.FillSubtleSecondary,
        PressedFill = Tok.FillSubtleTertiary,
        HoverScale = R.Motion.ScaleSubtle.Hover, PressScale = R.Motion.ScaleSubtle.Press,
        HoverDurationMs = MotionTok.ControlFaster.DurationMs, HoverEasing = MotionTok.ControlFaster.Easing,
        OnClick = onClick,
        Children =
        [
            new BoxEl { Width = 8f, Height = 8f, Shrink = 0f, Corners = CornerRadius4.All(2f), Fill = ink },
            Caption(label) with { Color = lit ? Tok.AccentTextPrimary : Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            Caption(share) with { Color = Tok.TextTertiary, MaxLines = 1 },
        ],
    };

    static Element FluentPill(DetailFactsPill p)
    {
        bool live = p.Toggle is not null;
        bool lit = p.Lit;
        var ink = lit ? Tok.AccentTextPrimary : Tok.TextTertiary;
        var kids = new List<Element>(4)
        {
            new PathEl
            {
                Geometry = p.Glyph, Width = 13f, Height = 13f, ViewBoxW = 24f, ViewBoxH = 24f, Shrink = 0f,
                StrokeColor = ink, Stroke = new StrokeStyle(2.4f, LineCap.Round, LineJoin.Round),
            },
        };
        if (p.Lead.Length > 0) kids.Add(Caption(p.Lead) with { Color = lit ? Tok.AccentTextPrimary : Tok.TextSecondary, MaxLines = 1 });
        kids.Add(Caption(p.Strong) with
        {
            Weight = 600, Color = lit ? Tok.AccentTextPrimary : Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            Shrink = 1f, MinWidth = 0f,
        });
        if (p.Trailing is { Length: > 0 } trailing) kids.Add(Caption(trailing) with { Color = Tok.TextTertiary, MaxLines = 1 });

        var rest = lit ? Tok.AccentSubtle : Tok.FillCardDefault;
        return new BoxEl
        {
            Key = p.Key, Direction = 0, AlignItems = FlexAlign.Center, Gap = 6f, Height = 26f, MinWidth = 0f,
            Padding = new Edges4(8f, 0f, 10f, 0f), Corners = CornerRadius4.All(13f),
            Fill = rest, BorderWidth = 1f, BorderColor = lit ? Tok.AccentSecondary : Tok.StrokeCardDefault, Shadow = Elevation.Card,
            Role = live ? AutomationRole.Button : AutomationRole.None, Focusable = live,
            Cursor = live ? CursorId.Hand : CursorId.Arrow, FocusVisualMargin = live ? Design.FocusInsetRow : null,
            // A non-live pill pins hover AND pressed to its own idle fill: a plain pill must not animate (W23 h).
            HoverFill = live ? (lit ? Tok.AccentSecondary : Tok.FillSubtleSecondary) : rest,
            PressedFill = live ? Tok.FillSubtleTertiary : rest,
            HoverDurationMs = MotionTok.ControlFaster.DurationMs, HoverEasing = MotionTok.ControlFaster.Easing,
            OnClick = p.Toggle,
            Children = kids.ToArray(),
        };
    }
}
