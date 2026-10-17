// ── Themes/Zune/Structure/Views/Lyrics/ZuneStageLyrics.cs — Zune's `Lyrics.View` (P5 Z4c) ────────────────────────────────────────────────
//
// The lyrics reading surface (Z-Lyrics), one tree for BOTH mounts: the 340 panel and the immersive stage's reading column. The core host
// (`Lyrics.ViewCore`) keeps what decides and drives: the document slot, the media clock, the follow and resync machine, the cascade, the
// depth-of-field ramp and every node handle; it hands this template the layers it owns (the document boundary, the frame ticker, the
// developer layer, the AI disclosure) and the `ILyricsHost` the overlays read. The LINES are Zune's list style (`ZuneLyricsLines`, Z3: one
// line of type per line, the sung line in the accent, the ramp as opacity); this view lays out the surface around them:
//
//   column[ layers: ZStack[ body, (ticker), dots, resync, video note, developer layer ], AI disclosure ]
//
//   · the AI disclosure sits BELOW the reading layers in a column, never a virtual-list item (the follow geometry assumes ItemCount == Lines.Count);
//   · the "resync" pill: a flat square plate with the 4 s countdown ring and the word in the accent (lowercase through the command case), up
//     while the user owns the scroll; a press re-attaches the follow (`ILyricsHost.Resync`). Its target is the window tier's (40 at Compact);
//   · the three breathing dots of an instrumental break are the accent's, round (the one round part of the surface);
//   · the video note ("no sync during video") is a flat plate in the label case;
//   · the stage's reading column is capped (1000 times the type scale), left-anchored, its gutter spelled as TRAILING padding.
//   · SHRINK (G-254): the rail places this view in a ROW slot and flex items do not shrink by default, so every layer shrinks.
//
// Not on this view's host: the board's "translate" word (the secondary-line choice has no verb on `ILyricsHost`; the lines already draw the
// secondary line when the user's choice says so), reported as a gap.

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

using LR = Wavee.Themes.Zune.ZuneStageLyricsRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's lyrics reading surface.</summary>
internal static class ZuneStageLyrics
{
    /// <summary>The registered template of <see cref="ViewKeys.LyricsView"/>.</summary>
    public static Element Template(LyricsViewProps props, in ViewContext ctx)
    {
        var tiers = ctx.Tier();   // hooks first, unconditionally
        var host = props.Host;
        Element content = props.Message is { } text
            ? ctx.StyleOf(StyleKeys.LyricsLines).Message(host, text)
            : Layers(props, host, ZuneStageTiers.StageOf(in tiers).TargetH);
        return host.OnStage ? StageColumn(host, content) : content;
    }

    /// <summary>The immersive stage's reading column. MEASURED, never predicted: the column grows into whatever the pane gives it, capped by
    /// MaxWidth (the type grows with a big stage, and the cap with it). LEFT-ANCHORED, never centred: centring converted an over-wide pane
    /// into text pushed off-screen.</summary>
    static Element StageColumn(ILyricsHost host, Element content) => new BoxEl
    {
        Direction = 0, Grow = 1f, Shrink = 1f, MinHeight = 0f, MinWidth = 0f,
        Justify = FlexJustify.Start, AlignItems = FlexAlign.Stretch,
        Padding = new Edges4(0f, 0f, LR.StageColumnGutter, 0f),
        Children =
        [
            new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, MinHeight = 0f, MinWidth = 0f,
                MaxWidth = LR.StageColumnMaxW * host.TypeScale,
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

    /// <summary>The note a VIDEO raises while it suppresses timed sync, so the missing highlight reads as a deliberate state. The full-bleed
    /// pass-through positioner stays OUTSIDE <c>Flow.Show</c>.</summary>
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
                        Corners = R.Shape.All(ShapeRole.Control),
                        Fill = ink.Plate,
                        HitTestVisible = false,
                        Children =
                        [
                            new TextEl(Loc.Get(Strings.Player.LyricsSyncUnavailableDuringVideo))
                            {
                                Size = LR.VideoNoteSize, Weight = LR.NoteWeight, MinWidth = 0f, Color = ink.Tertiary, Case = R.Case.For(LabelKind.Label),
                                Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                            },
                        ],
                    }),
            ],
        };
    }

    /// <summary>The "resync" pill with its 4 s countdown ring, up while the user owns the scroll: a flat square plate, the word in the accent.</summary>
    static Element ResyncOverlay(ILyricsHost host, float targetMinH)
    {
        var ink = host.Ink;
        ColorF word = host.Accent is { } accent ? accent.Value : R.Ink.AccentAction;
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
                        Direction = 0, AlignItems = FlexAlign.Center, Gap = LR.ResyncGap, MinHeight = targetMinH,
                        Padding = new Edges4(LR.ResyncPadX, LR.ResyncPadY, LR.ResyncPadX, LR.ResyncPadY),
                        Corners = R.Shape.All(ShapeRole.Control),
                        Fill = ink.Plate, HoverFill = ink.PlateHover, PressedFill = ink.PlatePressed,
                        // PRESS only: the pill never grows on hover.
                        PressScale = R.Motion.ScaleSubtle.Press, Cursor = CursorId.Hand,
                        OnClick = host.Resync,
                        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
                        Enter = new EnterExit(Dy: LR.ResyncRiseDy, Opacity: 0f, Active: true),
                        Exit = new EnterExit(Dy: LR.ResyncRiseDy, Opacity: 0f, Active: true),
                        Layout = LayoutTransition.Fade,
                        Children =
                        [
                            ProgressRing.Create(host.ResyncProgress, size: LR.ResyncRing, foreground: ink.RingFill, track: ink.RingTrack),
                            new TextEl(Loc.Get(Strings.Player.ResyncLyrics))
                            {
                                Size = LR.ResyncLabelSize, LineHeight = LR.ResyncLabelLine, Weight = LR.ResyncWeight, Color = word,
                                Case = R.Case.For(LabelKind.Command), MaxLines = 1,
                            },
                        ],
                    }),
            ],
        };
    }

    /// <summary>The instrumental break's three breathing dots: an overlay, not a row (a pseudo-row would put a conditional off-by-one into every
    /// index mapping). Two empty Grow spacers split the panel at the band and the row's bottom margin buys the rest, exact at every viewport size.
    /// Decorative: <c>HitTestVisible = false</c> on the whole subtree, so the lyrics stay scrollable and clickable straight through it.</summary>
    static Element InterludeDots(ILyricsHost host)
    {
        bool large = host.Large;
        float band = host.Band;
        float d = Lyrics.Interlude.DotSize(large);
        ColorF fill = host.Accent is { } accent ? accent.Value : host.Ink.Primary;
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
                    Enter = new EnterExit(Sx: LR.DotsEnterScale, Sy: LR.DotsEnterScale, Opacity: 0f, Active: true),
                    Exit = new EnterExit(Sx: LR.DotsEnterScale, Sy: LR.DotsEnterScale, Opacity: 0f, Active: true),
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
            Corners = CornerRadius4.All(size * LR.DotRadiusFraction),
            Fill = fill,
            Opacity = (Prop<float>)host.DotAlpha[k],
        };
    }
}
