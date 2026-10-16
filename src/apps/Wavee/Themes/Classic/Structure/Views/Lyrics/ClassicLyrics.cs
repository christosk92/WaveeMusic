// ── Themes/Classic/Structure/Views/Lyrics/ClassicLyrics.cs — Classic's `Lyrics.View` (themes plan 6.1, T7b) ────────────────────
//
// From `Shell/Lyrics.UI.cs`: the element tree `Lyrics.ViewCore` built around the document. The core host keeps the document slot, the
// media clock, the follow and resync machine, the cascade, the depth-of-field ramp, the word wipe and every node handle they write; it
// hands this template the layers it owns (the document boundary, the frame ticker, the developer layer, the AI disclosure) and the
// `ILyricsHost` the overlays read, and the template lays them out. Elements, keys, motion and bound channels are exactly what the
// surface built before the move; the lines themselves are `ClassicLyricsLines` (the `StyleKeys.LyricsLines` style).
//
// THE READING SURFACE (ch 22 §9), one tree for BOTH mounts - the 340-DIP rail panel and the immersive stage's reading column; the
// only differences are the host's `Large` (type, gutter, band, σ ladder), its ink, and the stage's column:
//
//   column[ layers: ZStack[ body, (ticker), dots, resync pill, video note, developer layer ], AI disclosure ]
//
//   - The AI disclosure sits BELOW the reading layers in a column, never a virtual-list item (the follow geometry assumes
//     ItemCount == Lines.Count). It is zero-extent until the AI times the track; its clip box then grows through real layout, so the
//     lyrics viewport shrinks smoothly, and the resync pill (bottom-anchored inside the layers) rides above it. The podcast
//     reader carries no disclosure (episodes are never timed) and keeps the bare layer stack.
//   - SHRINK (G-254): the rail places this view in a ROW slot, a ZStack measures its widest layer (the loading shimmer's bars are
//     ~263 DIP; the rail can be 200), and flex items do not shrink by default - a narrow rail, or a drag back to narrow, would keep
//     the wider width and clip the reading column.
//   - The stage's reading column is capped (1000 times the type scale), left-anchored, its gutter spelled as TRAILING padding.
//   - TIERS (TC3): the lines are fluid at every panel width (200-500); on the stage their type is the window tier's (`Stage.Layout.LyricsTypeSize`:
//     26 Compact, 30 Small, 36 Standard, up to 48 Huge). On a Compact window the Resync pill is a 40-tall target (`ClassicStageTiers.PanelOf`).

using FluentGpu.Animation;
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

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's lyrics reading surface.</summary>
internal static class ClassicLyrics
{
    /// <summary>The registered template of <see cref="ViewKeys.LyricsView"/>.</summary>
    public static Element Template(LyricsViewProps props, in ViewContext ctx)
    {
        var host = props.Host;
        Element content = props.Message is { } text
            ? ctx.StyleOf(StyleKeys.LyricsLines).Message(host, text)
            : Layers(props, host, ClassicStageTiers.PanelOf(ctx.Tier()).MinTarget);
        return host.OnStage ? StageColumn(host, content) : content;
    }

    /// <summary>The immersive stage's reading column. MEASURED, never predicted: the column grows into whatever the pane gives it,
    /// capped by MaxWidth (the type grows with a big stage, and the cap with it). LEFT-ANCHORED, never centred: centring converted
    /// an over-wide pane into text pushed off-screen.</summary>
    static Element StageColumn(ILyricsHost host, Element content) => new BoxEl
    {
        Direction = 0, Grow = 1f, Shrink = 1f, MinHeight = 0f, MinWidth = 0f,
        Justify = FlexJustify.Start, AlignItems = FlexAlign.Stretch,
        Padding = new Edges4(0f, 0f, ClassicLyricsRules.StageColumnGutter, 0f),
        Children =
        [
            new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, MinHeight = 0f, MinWidth = 0f,
                MaxWidth = ClassicLyricsRules.StageColumnMaxW * host.TypeScale,
                Children = [content],
            },
        ],
    };

    static Element Layers(LyricsViewProps props, ILyricsHost host, float targetMinH)
    {
        // The frame stepper stays UNMOUNTED while the host's gate is up, so a surface that cannot step costs no frames.
        var kids = new List<Element>(6);
        if (props.Body is { } body) kids.Add(body);
        if (props.Ticker is { } ticker) kids.Add(ticker);
        kids.Add(InterludeDots(host));
        kids.Add(ResyncOverlay(host, targetMinH));
        kids.Add(VideoNote(host));
        if (props.Debug is { } debug) kids.Add(debug);

        var layers = new BoxEl
        {
            Grow = 1f, Shrink = 1f, MinHeight = 0f, ClipToBounds = true, ZStack = true,
            Children = kids.ToArray(),
        };
        // The podcast reader never carries the on-device AI disclosure: it keeps the bare layer stack (mount config, so the
        // root's shape never flips for a mounted instance).
        if (props.Footer is not { } footer) return layers;
        return new BoxEl
        {
            Grow = 1f, Shrink = 1f, MinHeight = 0f, Direction = 1,
            Children = [layers, footer],
        };
    }

    // ── the overlays ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The note a VIDEO raises while it suppresses timed sync, so the missing highlight reads as a deliberate state. The
    /// full-bleed pass-through positioner stays OUTSIDE <c>Flow.Show</c>.</summary>
    static Element VideoNote(ILyricsHost host)
    {
        var ink = host.Ink;
        return new BoxEl
        {
            Grow = 1f, MinHeight = 0f, HitTestPassThrough = true,
            Direction = 1, Justify = FlexJustify.Start, AlignItems = FlexAlign.Center,
            Padding = new Edges4(Spacing.M, Lyrics.Surface.VideoNoteTopPad(host.Large), Spacing.M, 0f),
            Children =
            [
                Flow.Show(
                    host.VideoNoteShown,
                    new BoxEl
                    {
                        Shrink = 0f, MinWidth = 0f,
                        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS,
                        Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS),
                        Corners = CornerRadius4.All(Radii.Control),
                        Fill = ink.Plate,
                        HitTestVisible = false,
                        Children =
                        [
                            new TextEl(Loc.Get(Strings.Player.LyricsSyncUnavailableDuringVideo))
                            {
                                Size = ClassicLyricsRules.VideoNoteSize, Weight = ClassicLyricsRules.NoteWeight, MinWidth = 0f, Color = ink.Tertiary,
                                Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                            },
                        ],
                    }),
            ],
        };
    }

    /// <summary>The "Resync" pill with its 4 s countdown ring, up while the user owns the scroll.</summary>
    static Element ResyncOverlay(ILyricsHost host, float targetMinH)
    {
        var ink = host.Ink;
        return new BoxEl
        {
            Grow = 1f, MinHeight = 0f, HitTestPassThrough = true,
            Direction = 1, Justify = FlexJustify.End, AlignItems = FlexAlign.Center,
            Padding = new Edges4(0f, 0f, 0f, Lyrics.Surface.ResyncInset(host.Large)),
            Children =
            [
                Flow.Show(
                    host.ResyncShown,
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center, Gap = ClassicLyricsRules.ResyncGap, MinHeight = targetMinH,
                        Padding = new Edges4(ClassicLyricsRules.ResyncPadLeft, ClassicLyricsRules.ResyncPadTop, ClassicLyricsRules.ResyncPadRight, ClassicLyricsRules.ResyncPadBottom),
                        Corners = CornerRadius4.All(ClassicLyricsRules.ResyncRadius),
                        Fill = ink.Plate, BorderWidth = 1f, BorderColor = ink.PlateStroke,
                        HoverFill = ink.PlateHover, PressedFill = ink.PlatePressed,
                        // PRESS only - the pill never grows on hover (ch 22 audit 1, parity 75).
                        PressScale = R.Motion.ScaleSubtle.Press, Cursor = CursorId.Hand,
                        OnClick = host.Resync,
                        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
                        Enter = new EnterExit(Dy: ClassicLyricsRules.ResyncRiseDy, Opacity: 0f, Active: true),
                        Exit = new EnterExit(Dy: ClassicLyricsRules.ResyncRiseDy, Opacity: 0f, Active: true),
                        Layout = LayoutTransition.Fade,
                        Children =
                        [
                            ProgressRing.Create(host.ResyncProgress, size: ClassicLyricsRules.ResyncRingSize, foreground: ink.RingFill, track: ink.RingTrack),
                            new TextEl(Loc.Get(Strings.Player.ResyncLyrics))
                            {
                                Size = ClassicLyricsRules.ResyncLabelSize, LineHeight = ClassicLyricsRules.ResyncLabelLineHeight,
                                Weight = ClassicLyricsRules.NoteWeight, Color = ink.Primary,
                            },
                        ],
                    }),
            ],
        };
    }

    /// <summary>The instrumental break's three breathing dots: an overlay, not a row (a pseudo-row would put a conditional
    /// off-by-one into every index mapping). Two empty Grow spacers split the panel at the band and the row's bottom margin buys the
    /// rest - exact at every viewport size. Decorative: <c>HitTestVisible = false</c> on the whole subtree, so the lyrics stay
    /// scrollable and clickable straight through it.</summary>
    static Element InterludeDots(ILyricsHost host)
    {
        bool large = host.Large;
        float band = host.Band;
        float d = Lyrics.Interlude.DotSize(large);
        var ink = host.Ink;
        return new BoxEl
        {
            Grow = 1f, MinHeight = 0f, HitTestVisible = false,
            Direction = 1, AlignItems = FlexAlign.Start,
            Children =
            [
                new BoxEl { Grow = band, MinHeight = 0f },
                Flow.Show(host.DotsShown, new BoxEl
                {
                    // Enter/exit owner. The breath lives on the CHILD (one transform owner per node).
                    Direction = 0, Shrink = 0f,
                    Margin = new Edges4(host.Metrics.SidePad, 0f, 0f, Lyrics.Interlude.AnchorMargin(large, band)),
                    Enter = new EnterExit(Sx: ClassicLyricsRules.DotsEnterScale, Sy: ClassicLyricsRules.DotsEnterScale, Opacity: 0f, Active: true),
                    Exit = new EnterExit(Sx: ClassicLyricsRules.DotsEnterScale, Sy: ClassicLyricsRules.DotsEnterScale, Opacity: 0f, Active: true),
                    Layout = LayoutTransition.Fade,
                    Children =
                    [
                        new BoxEl
                        {
                            // Breath owner: declares no transform and owns no animation channel, so the frame lane's
                            // direct LocalTransform write is the only thing that touches it.
                            Direction = 0, Shrink = 0f, Gap = Lyrics.Interlude.DotGap(large), AlignItems = FlexAlign.Center,
                            OnRealized = host.OnDotsRealized,
                            Children = [Dot(0, d), Dot(1, d), Dot(2, d)],
                        },
                    ],
                }),
                new BoxEl { Grow = 1f - band, MinHeight = 0f },
            ],
        };

        Element Dot(int k, float size) => new BoxEl
        {
            Width = size, Height = size, Shrink = 0f,
            Corners = CornerRadius4.All(size * ClassicLyricsRules.DotRadiusFraction),
            Fill = ink.Primary,
            Opacity = (Prop<float>)host.DotAlpha[k],
        };
    }
}
