// ── Themes/WinUi/Structure/Views/Stage/WinUiStageCaption.cs — WinUI's (from Classic's) `Stage.Caption` (themes plan 6.1, T7a1) ───────────
//
// From `Stage.Caption.UI.cs`: the element tree `CaptionHost` built inline. The core host keeps the playhead clock, the view edge
// wake, the render-thread wipe and the dots' driver; it hands this template what one render decided (`StageCaptionProps`) and the
// template lays it out. Elements, keys, motion and bound channels are exactly what the stage built before the move.
//
// The lyric over the visualizer, presented as three lines: the PREVIOUS line above (small), the ACTIVE line at the hero-title
// scale and the NEXT line below (small), centred in the face's region at the allocator's caption geometry (the lower third, above
// the transport and the hairline) over a soft elliptical veil, never a plate.
//   - The active line carries the lyrics view's karaoke WIPE and a blurred accent BLOOM layer that follows the same split; a
//     line-synced line has no wipe and fades in whole. No other line is blurred.
//   - A real instrumental break (and a long intro) shows the lyrics view's three breathing dots in the active slot.
//   - Every slot is KEYED by its role + line index (+ the document's generation), so a hand-off plays the Exit (up and out) on the
//     outgoing line and the Enter (up from below) on the incoming one; reduced motion keeps only the fade. The hand-off is SEQUENCED,
//     never a cross-fade: the outgoing slot is painted at its old rect (it leaves the flow) and the incoming one takes the same slot, so
//     overlapping the two drew one line through the other (the ghost behind the active line). The exit runs StandardExit and the
//     enter waits it out before its StandardEnter: the core's `Stage.CaptionSlot`, every theme's recipe.
//   - Nothing is ellipsised: every line wraps (the block is bottom-anchored, so a longer line grows the block upward).
// The root layer is the stage's own `Stage.Layer`; the root places it under a `Flow.Show(CaptionOn)` with a RepaintBoundary.

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's lyric caption over the stage.</summary>
internal static class WinUiStageCaption
{
    // The block glides to its new bottom margin when a tier edge moves the transport (196 sheet / 104 / 112 / 128 card): no jump on a crossing
    static readonly LayoutTransition s_fit = new(TransitionChannels.Bounds,
        TransitionDynamics.Tween(WinUiStageCaptionGeometry.FitMs, Easing.FluentDecelerate), SizeMode.Relayout);

    /// <summary>The registered template of <see cref="ViewKeys.StageCaption"/>.</summary>
    public static Element Template(StageCaptionProps props, in ViewContext ctx)
    {
        string gen = props.Generation;
        var kids = new List<Element>(3);
        if (props.PreviousText is { } previous)
            kids.Add(ContextLine(previous, "cap:p:" + gen + ":" + props.PreviousIndex, props));
        if (props.Dots) kids.Add(DotsSlot("cap:d:" + gen + ":" + props.Anchor, props));
        else if (props.ActiveText is { } active)
            kids.Add(ActiveSlot(active, "cap:a:" + gen + ":" + props.ActiveIndex, props));
        if (props.NextText is { } next)
            kids.Add(ContextLine(next, "cap:n:" + gen + ":" + props.NextIndex, props));

        return Stage.Layer with
        {
            HitTestVisible = false, HitTestPassThrough = false,
            Children =
            [
                // the veil: a wide ELLIPSE behind the block at its tallest, feathered to nothing (radial) — legibility over a
                // bright face without a plate; BOTTOM-anchored around the block (vertical = AlignSelf, horizontal = JustifySelf)
                new BoxEl
                {
                    Width = props.Width + 2f * Stage.Layout.CaptionVeilPadX, Height = props.BlockMaxHeight + 2f * Stage.Layout.CaptionVeilPadY,
                    AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Start, HitTestVisible = false,
                    Margin = new Edges4(props.X - Stage.Layout.CaptionVeilPadX, 0f, 0f, MathF.Max(0f, props.Bottom - Stage.Layout.CaptionVeilPadY)),
                    Gradient = RadialGradient(
                        new GradientStop(0f, Shade(Stage.Tone.CaptionVeilA)),
                        new GradientStop(WinUiStageCaptionGeometry.VeilMidStop, Shade(Stage.Tone.CaptionVeilA * WinUiStageCaptionGeometry.VeilMidAlpha)),
                        new GradientStop(1f, Shade(0f))),
                },
                // the block: previous · active (or the dots) · next, centred in its region, bottom-anchored above the transport
                new BoxEl
                {
                    Width = props.Width, AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Start, Margin = new Edges4(props.X, 0f, 0f, props.Bottom),
                    Direction = 1, AlignItems = FlexAlign.Center, Gap = Stage.Layout.CaptionLineGap, HitTestVisible = false, Layout = s_fit,
                    Children = kids.ToArray(),
                },
                .. props.Ticker,
            ],
        };
    }

    static ColorF Shade(float a) => Ink.Veil with { A = a };

    /// <summary>A context line (previous / next): small, one line, the stage arm's secondary ink (at the old 0.35 / 0.45 ink alphas it read 2 : 1 over a bright visualizer), never blurred.</summary>
    static Element ContextLine(string text, string key, StageCaptionProps p) => new BoxEl
    {
        Key = key, Direction = 1, AlignItems = FlexAlign.Center, MaxWidth = p.Width, HitTestVisible = false,
        Enter = Stage.CaptionSlot.Enter, Exit = Stage.CaptionSlot.Exit, Layout = Stage.CaptionSlot.Motion,
        Children =
        [
            new TextEl(text)
            {
                Size = p.ContextSize, LineHeight = p.ContextLineHeight, Weight = WinUiStageCaptionGeometry.LineWeight, FontFamily = R.Type.DisplayFace,
                Color = Ink.InkSecondary, Wrap = TextWrap.Wrap, MaxLines = 0, Trim = TextTrim.None, MinWidth = 0f,
            },
        ],
    };

    /// <summary>The active line: a ZStack of [the accent bloom (blurred, low alpha), the sung/unsung text], both carrying the
    /// SAME wipe for a word-synced line (the halo follows the voice, LineRow's recipe); a line-synced line is plain ink and
    /// fades in whole with its slot. The translation / romanization, when chosen, sits under it at the context size.</summary>
    static Element ActiveSlot(string text, string key, StageCaptionProps p)
    {
        ColorF sung = Ink.Ink, unsung = Ink.Ink with { A = Stage.Tone.CaptionUnsungA };
        ColorF bloom = p.Bloom;
        TextEl main = LineText(text, p.ActiveSize, p.ActiveLineHeight, sung), glow = LineText(text, p.ActiveSize, p.ActiveLineHeight, bloom);
        if (p.Wipe is { } w)
        {
            main = main with { Wipe = new GlyphWipe(sung, unsung, w.Split, w.Softness, w.Lift) { Run = w.Run }, OnRealized = p.OnMain };
            glow = glow with { Wipe = new GlyphWipe(bloom, bloom with { A = 0f }, w.Split, w.Softness, w.Lift) { Run = w.Run }, OnRealized = p.OnGlow };
        }
        var stack = new BoxEl
        {
            ZStack = true, HitTestVisible = false,
            Children =
            [
                new BoxEl { Direction = 1, Blur = Stage.Tone.CaptionBloomSigma, Opacity = Stage.Tone.CaptionBloomA, HitTestVisible = false, Children = [glow] },
                main,
            ],
        };
        return new BoxEl
        {
            Key = key, Direction = 1, AlignItems = FlexAlign.Center, Gap = WinUiStageCaptionGeometry.SecondaryGap, MaxWidth = p.Width, HitTestVisible = false,
            Enter = Stage.CaptionSlot.Enter, Exit = Stage.CaptionSlot.Exit, Layout = Stage.CaptionSlot.Motion,
            Children = p.Secondary is { Length: > 0 } secondary
                ? [stack, new TextEl(secondary) { Size = p.ContextSize, LineHeight = p.ContextLineHeight, Color = Ink.InkSecondary, Wrap = TextWrap.Wrap, MaxLines = 0, Trim = TextTrim.None, MinWidth = 0f }]
                : [stack],
        };
    }

    static TextEl LineText(string text, float size, float line, ColorF color) => new(text)
    {
        Size = size, LineHeight = line, Weight = WinUiStageCaptionGeometry.LineWeight, FontFamily = R.Type.DisplayFace, Color = color,
        Wrap = TextWrap.Wrap, MaxLines = 0, Trim = TextTrim.None, MinWidth = 0f,
    };

    /// <summary>The break's three breathing dots in the active slot (the lyrics view's dots: size, gap, ink); one active
    /// line tall so the block keeps its rhythm. The inner row is the breath owner (no transform of its own).</summary>
    static Element DotsSlot(string key, StageCaptionProps p)
    {
        float d = Lyrics.Interlude.DotSize(large: true);
        var alpha = p.DotAlpha;
        return new BoxEl
        {
            Key = key, Height = p.ActiveLineHeight, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
            Enter = Stage.CaptionSlot.Enter, Exit = Stage.CaptionSlot.Exit, Layout = Stage.CaptionSlot.Motion,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Shrink = 0f, Gap = Lyrics.Interlude.DotGap(large: true), AlignItems = FlexAlign.Center, OnRealized = p.OnDots,
                    Children = [Dot(alpha[0], d), Dot(alpha[1], d), Dot(alpha[2], d)],
                },
            ],
        };

        static Element Dot(FloatSignal level, float size) => new BoxEl
        {
            Width = size, Height = size, Shrink = 0f, Corners = CornerRadius4.All(size * WinUiStageCaptionGeometry.DotRadiusFraction), Fill = Ink.Ink, Opacity = (Prop<float>)level,
        };
    }
}
