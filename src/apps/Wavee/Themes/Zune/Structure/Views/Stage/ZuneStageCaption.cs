// ── Themes/Zune/Structure/Views/Stage/ZuneStageCaption.cs — Zune's `Stage.Caption` (P5 Z4b) ────────────────────────────────────────
//
// The lyric over the face (no board draws it on the stage; built from Zune's parts, owner Q18): the previous line, the ACTIVE line and the
// next line, centred in the face's region, in Zune's lyric voice (the display face at semilight 350, Z-Lyrics) over a soft elliptical veil,
// never a plate. The core host keeps the playhead clock, the wipe and the dots; this template lays out what one render decided:
//   · the active line carries the karaoke WIPE and the accent BLOOM following the same split; a line-synced line fades in whole;
//   · a break shows the three breathing dots in the active slot;
//   · every slot is keyed by role + line index + the document's generation, so a hand-off plays the exit (up and out) and the enter (up
//     from below) - the stage's production recipe, SEQUENCED (`Stage.CaptionSlot`: the enter waits the exit out, so two lines never
//     share a slot).
// In the Card arrangement (Z-NowPlaying-Card) the block rises above the card instead of sitting above a plate.

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using static FluentGpu.Dsl.Ui;

using G = Wavee.Themes.Zune.ZuneStageGeometry;
using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee.Themes.Zune;

/// <summary>Zune's lyric caption over the stage.</summary>
internal static class ZuneStageCaption
{
    /// <summary>The block's move when its arrangement re-places it (a tier crossing): the cover's spring, so the lines travel with the
    /// cover and the card instead of snapping 16-200 DIP (audit B3bc, the no-jump rule). The veil rides the same spring.</summary>
    static readonly LayoutTransition s_place = new(TransitionChannels.Bounds, TransitionDynamics.Spring(G.CoverResponse, G.CoverDamping), SizeMode.Relayout);

    /// <summary>The registered template of <see cref="ViewKeys.StageCaption"/>.</summary>
    public static Element Template(StageCaptionProps props, in ViewContext ctx)
    {
        float bottom = props.Bottom;
        var tiers = ctx.Tier();   // hooks first, unconditionally
        if (ctx.Hooks.UseContext(Stage.StageContext) is { } sc)
        {
            var a = ZuneStageKit.Arrangement(sc, tiers);
            var L = sc.Layout.Value;
            bottom = G.CaptionBottom(in L, in a, props.Bottom);
            // the Compact stack fills the column with the cover, the titles and the controls: no line over them (the clock stays)
            if (!G.CaptionAllowed(in a)) return Stage.Layer with { HitTestVisible = false, HitTestPassThrough = false, Children = [.. props.Ticker] };
        }
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
                new BoxEl
                {
                    Key = "cap:veil", Layout = s_place,
                    Width = props.Width + 2f * Stage.Layout.CaptionVeilPadX, Height = props.BlockMaxHeight + 2f * Stage.Layout.CaptionVeilPadY,
                    AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Start, HitTestVisible = false,
                    Margin = new Edges4(props.X - Stage.Layout.CaptionVeilPadX, 0f, 0f, MathF.Max(0f, bottom - Stage.Layout.CaptionVeilPadY)),
                    Gradient = RadialGradient(
                        new GradientStop(0f, Shade(Stage.Tone.CaptionVeilA)),
                        new GradientStop(G.VeilMidStop, Shade(Stage.Tone.CaptionVeilA * G.VeilMidAlpha)),
                        new GradientStop(1f, Shade(0f))),
                },
                new BoxEl
                {
                    Key = "cap:block", Layout = s_place,
                    Width = props.Width, AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Start, Margin = new Edges4(props.X, 0f, 0f, bottom),
                    Direction = 1, AlignItems = FlexAlign.Center, Gap = Stage.Layout.CaptionLineGap, HitTestVisible = false,
                    Children = kids.ToArray(),
                },
                .. props.Ticker,
            ],
        };
    }

    static ColorF Shade(float a) => Ink.Veil with { A = a };

    static Element ContextLine(string text, string key, StageCaptionProps p) => new BoxEl
    {
        Key = key, Direction = 1, AlignItems = FlexAlign.Center, MaxWidth = p.Width, HitTestVisible = false,
        Enter = Stage.CaptionSlot.Enter, Exit = Stage.CaptionSlot.Exit, Layout = Stage.CaptionSlot.Motion,
        Children =
        [
            new TextEl(text)
            {
                Size = p.ContextSize, LineHeight = p.ContextLineHeight, Weight = G.CaptionWeight, FontFamily = R.Type.DisplayFace,
                Color = Ink.InkSecondary, Wrap = TextWrap.Wrap, TextAlign = TextAlign.Center, MaxLines = G.ContextMaxLines, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            },
        ],
    };

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
            Key = key, Direction = 1, AlignItems = FlexAlign.Center, Gap = G.SecondaryGap, MaxWidth = p.Width, HitTestVisible = false,
            Enter = Stage.CaptionSlot.Enter, Exit = Stage.CaptionSlot.Exit, Layout = Stage.CaptionSlot.Motion,
            Children = p.Secondary is { Length: > 0 } secondary
                ? [stack, new TextEl(secondary) { Size = p.ContextSize, LineHeight = p.ContextLineHeight, Color = Ink.InkSecondary, Wrap = TextWrap.Wrap, TextAlign = TextAlign.Center, MaxLines = G.SecondaryMaxLines, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }]
                : [stack],
        };
    }

    static TextEl LineText(string text, float size, float line, ColorF color) => new(text)
    {
        Size = size, LineHeight = line, Weight = G.CaptionWeight, FontFamily = R.Type.DisplayFace, Color = color,
        Wrap = TextWrap.Wrap, TextAlign = TextAlign.Center, MaxLines = Stage.Layout.CaptionActiveMaxLines, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
    };

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
            Width = size, Height = size, Shrink = 0f, Corners = CornerRadius4.All(size * G.DotRadiusFraction), Fill = Ink.Ink, Opacity = (Prop<float>)level,
        };
    }
}
