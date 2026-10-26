// ── Themes/Zune/Structure/Views/Detail/ZuneFacts.cs — Zune's facts bento, the insights panel's body (themes line p6) ───────────────────
//
// `StyleKeys.DetailFacts`: the pictures the core facts components (`User.FactsPanel`) compose, drawn in Zune's Metro voice. No board draws the
// insights (Q18); the look is the side panel's (Z-Frame-Panel, Queue) and the track drawer's figures:
//
//   · NO CHROME. A section is its small lowercase label (13/18, tertiary) over its content on the panel's own wash: no plate, no border, no
//     corner, no shadow. Sections stand apart by the theme's section gap.
//   · TYPE CARRIES IT. A hero figure ("2024", "101") is the theme's StatHero (40/48·200, the light display face, as the drawer states plays and
//     bpm) with its lowercase meta caption under it; the chart stands under the figure at the panel's full width (Zune is large type: the
//     figure never shares a row it would have to shrink in).
//   · FLAT, QUIET CHARTS. Square bars, the accent on the bar that matters (the peak, the newest week, the lit lens) and a neutral tint on the
//     rest; the tempo a thin accent line with a whisper of fill over a hairline baseline, the median a hairline, the axis in the micro type.
//   · ONE SCRUB TARGET. A bar strip (the weeks, the years) is one target the full strip wide and tall: hover, a press or a drag picks the bar
//     under the pointer and its value reads over the strip at once; Left/Right move the pick; a tap runs the bar's lens (`SparkBars` Scrub).
//   · EVERYTHING FITS. A head's trailing fact wraps under its label when the two do not fit one line; names, legend entries and pills trim.
//
// The core keeps the hooks, the settle, the lens verbs, the tooltips, the face pile and the all-artists flyout. Numbers: `ZuneFactsRules`.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Render;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using F = Wavee.Themes.Zune.ZuneFactsRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's facts bento (<see cref="StyleKeys.DetailFacts"/>).</summary>
internal static class ZuneFacts
{
    public static readonly DetailFactsStyle Style = new()
    {
        SectionGap = static () => R.Space.SectionGap,
        StackPadding = static outer => outer ? new Edges4(Spacing.L, Spacing.S, Spacing.L, Spacing.L) : Edges4.All(0f),
        Section = Section,
        SectionInset = static () => 0f,
        Head = Head,
        Figure = Figure,
        FigureRow = static (figure, chart) => new BoxEl
        {
            Direction = 1, Gap = F.FigureChartGap, MinWidth = 0f, Children = [figure, chart],
        },
        Spark = static () => SparkBars.DefaultStyle with
        {
            Height = F.SparkHeight, Gap = F.SparkGap, MinBar = F.SparkMinBar,
            RestInk = Tok.TextPrimary with { A = F.SparkRestAlpha },
            BarCorners = CornerRadius4.All(0f), ColumnCorners = CornerRadius4.All(0f),
            HoverWash = R.Layer.ChromeHover, PressedWash = R.Layer.ChromePressed,
            // One scrub target (the owner's call, V23): twelve weeks in a Compact sheet are 27-DIP columns, under the 40 a touch needs. The whole
            // 56-DIP strip takes the finger, the bar under it is picked and its week reads over the strip, as Apple's Health charts do.
            Scrub = true,
        },
        Density = static () => DensityPlot.DefaultStyle with
        {
            Height = F.DensityHeight, AreaAlpha = F.DensityAreaAlpha, LineWidth = F.DensityLine, LineFloor = F.DensityLineFloor,
            BaselineInk = Tok.StrokeDividerDefault, BaselineWidth = F.Hairline,
            Marker = Tok.TextTertiary, MarkerWidth = F.Hairline,
            AxisFontSize = F.AxisSize, AxisHeight = F.AxisHeight, AxisGap = F.AxisGap, AxisInk = Tok.TextTertiary,
            BandCorners = CornerRadius4.All(0f), HoverWash = R.Layer.ChromeHover, PressedWash = R.Layer.ChromePressed,
        },
        ArtistRow = ArtistRow,
        ArtistGap = static () => F.ArtistGap,
        ArtistRowGap = static () => 0f,
        Disclosure = Disclosure,
        LegendEntry = LegendEntry,
        LegendGap = static () => 0f,
        BlendBar = static () => new DetailFactsBar(F.BlendBarH, CornerRadius4.All(0f), F.BlendSegGap),
        Rule = static () => Tok.StrokeDividerDefault,
        Pill = Pill,
        PillGap = static () => F.PillGap,
        Prose = static (text, secondary, lines) => Meta(text, secondary ? Tok.TextSecondary : Tok.TextTertiary) with
        {
            Wrap = lines > 1 ? TextWrap.Wrap : TextWrap.NoWrap, MaxLines = lines,
        },
        FootnotePadding = static () => Edges4.All(0f),
        Command = Command,
    };

    static BoxEl Section(string key, Element head, Element body) => new()
    {
        Direction = 1, Gap = F.SectionInnerGap, MinWidth = 0f, Children = [head, body],
    };

    /// <summary>The label grows; the trailing fact sits at the end of its line, or wraps onto the next one when the two do not fit.</summary>
    static Element Head(string title, string? trailing)
    {
        var label = Meta(title, Tok.TextTertiary) with { Case = R.Case.For(LabelKind.Label), Grow = 1f };
        Element[] kids = trailing is { Length: > 0 } fact ? [label, Meta(fact, Tok.TextTertiary)] : [label];
        return new BoxEl { Direction = 0, Wrap = true, AlignItems = FlexAlign.Center, Gap = F.HeadGap, MinWidth = 0f, Children = kids };
    }

    /// <summary>The figure in the StatHero voice over its lowercase caption; the value swaps with the core's text-swap motion.</summary>
    static Element Figure(string value, string caption, float width) => new BoxEl
    {
        Direction = 1, Gap = F.FigureCaptionGap, MinWidth = 0f,
        Children =
        [
            Ui.ZStack(new BoxEl
            {
                Key = "v:" + value, Animate = MotionRecipes.TextSwap,
                Children = [R.Type.StatHero(value, null) with { Color = Tok.TextPrimary }],
            }),
            Meta(caption, Tok.TextTertiary) with { Case = R.Case.For(LabelKind.Label) },
        ],
    };

    static Element ArtistRow(string name, string count, bool lit, Action? onClick)
    {
        bool live = onClick is not null;
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = F.RowInnerGap, MinWidth = 0f, Height = F.RowH,
            Role = live ? AutomationRole.Button : AutomationRole.None, Focusable = live, Cursor = live ? CursorId.Hand : CursorId.Arrow,
            HoverFill = live ? R.Layer.ChromeHover : ColorF.Transparent, PressedFill = live ? R.Layer.ChromePressed : ColorF.Transparent,
            OnClick = onClick,
            Children =
            [
                R.Type.TrackTitle(name) with
                {
                    Color = lit ? Tok.AccentTextPrimary : Tok.TextPrimary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                    Shrink = 1f, MinWidth = 0f,
                },
                Meta(count, Tok.TextTertiary) with { Shrink = 0f },
            ],
        };
    }

    static Element Disclosure(DetailFactsDisclosure d)
    {
        var kids = new List<Element>(3)
        {
            Sidebar.Chevron.Disclosure(d.Open),
            Meta(d.Label, Tok.TextSecondary) with { Case = R.Case.For(LabelKind.Command), HoverColor = Tok.TextPrimary },
        };
        if (d.Trailing is { } trailing) kids.Add(Meta(trailing, Tok.TextTertiary) with { Shrink = 0f });
        return new BoxEl
        {
            Key = d.Key, Direction = 0, AlignItems = FlexAlign.Center, Gap = F.LegendInnerGap, MinWidth = 0f,
            Height = F.RowH, Padding = new Edges4(0f, 0f, d.InLegend ? F.LegendGap : 0f, 0f),
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            HoverFill = R.Layer.ChromeHover, PressedFill = R.Layer.ChromePressed,
            OnClick = d.Toggle, OnKeyDown = d.KeyDown,
            Children = kids.ToArray(),
        };
    }

    /// <summary>A legend entry; its trailing air is inside it (the legend wraps with no gap, so its lines sit one entry height apart).</summary>
    static Element LegendEntry(ColorF ink, string label, string share, bool lit, Action onClick) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = F.LegendInnerGap, MinWidth = 0f, Height = F.RowH,
        Padding = new Edges4(0f, 0f, F.LegendGap, 0f),
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
        HoverFill = R.Layer.ChromeHover, PressedFill = R.Layer.ChromePressed,
        OnClick = onClick,
        Children =
        [
            new BoxEl { Width = F.Swatch, Height = F.Swatch, Shrink = 0f, Fill = ink },
            Meta(label, lit ? Tok.AccentTextPrimary : Tok.TextSecondary),
            Meta(share, Tok.TextTertiary) with { Shrink = 0f },
        ],
    };

    /// <summary>A section's one command: the lowercase word (the queue's "clear"), secondary at rest, primary under the pointer.</summary>
    static Element Command(string label, Action onClick) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Height = F.RowH, Shrink = 0f,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
        Children = [Meta(label, Tok.TextSecondary) with { Case = R.Case.For(LabelKind.Command), HoverColor = Tok.TextPrimary, Shrink = 0f }],
    };

    /// <summary>A pill without the pill: the glyph and the words, the accent ink while it lenses the list.</summary>
    static Element Pill(DetailFactsPill p)
    {
        bool live = p.Toggle is not null;
        var ink = p.Lit ? Tok.AccentTextPrimary : Tok.TextTertiary;
        var kids = new List<Element>(4)
        {
            new PathEl
            {
                Geometry = p.Glyph, Width = F.PillGlyph, Height = F.PillGlyph, ViewBoxW = F.GlyphViewBox, ViewBoxH = F.GlyphViewBox, Shrink = 0f,
                StrokeColor = ink, Stroke = new StrokeStyle(F.PillGlyphStroke, LineCap.Square, LineJoin.Miter),
            },
        };
        if (p.Lead.Length > 0) kids.Add(Meta(p.Lead, p.Lit ? Tok.AccentTextPrimary : Tok.TextSecondary) with { Case = R.Case.For(LabelKind.Label), Shrink = 0f });
        kids.Add(Meta(p.Strong, p.Lit ? Tok.AccentTextPrimary : Tok.TextPrimary));
        if (p.Trailing is { Length: > 0 } trailing) kids.Add(Meta(trailing, Tok.TextTertiary) with { Shrink = 0f });
        return new BoxEl
        {
            Key = p.Key, Direction = 0, AlignItems = FlexAlign.Center, Gap = F.LegendInnerGap, Height = F.RowH, MinWidth = 0f,
            Role = live ? AutomationRole.Button : AutomationRole.None, Focusable = live, Cursor = live ? CursorId.Hand : CursorId.Arrow,
            HoverFill = live ? R.Layer.ChromeHover : ColorF.Transparent, PressedFill = live ? R.Layer.ChromePressed : ColorF.Transparent,
            OnClick = p.Toggle,
            Children = kids.ToArray(),
        };
    }

    /// <summary>The panel's meta voice (13/18): one line, trimmed, yielding its width.</summary>
    static TextEl Meta(string text, ColorF ink) => R.Type.TrackMeta(text) with
    {
        Color = ink, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
    };
}
