// ── Themes/Zune/Structure/Views/Player/ZuneTransport.cs — Zune's seek rail, chapter ticks, readout and time labels (P5 Z4a) ─────────
//
// The pieces the play bar, the fullscreen stage's transport plate (Z4b) and the strip over a video share (boards Z-PlayBar,
// Z-Part-PlayBar). The core hosts (`Shell.SeekBar` / `Shell.TimeText`) keep the scrub controller, every hook and overlay, the chapter
// lease and the time formatting; the props say what to draw and carry the binds (`ISeekRailHost`).
//
//   Player.SeekRail      THE 2-PX LINE: the ink at 0.12 with the accent value over it, square, in a 12 hit box with the line on its centre
//                        (a row seats it with `ZuneTransportLayout.RailMargin`: on the bar's 2-px row the box reaches 5 above and below the
//                        line). The 12 round accent thumb shows on hover and while scrubbing. A DVR window marks its live edge; nothing
//                        to rewind is the breathing accent line; the Compact mini bar's hairline is the line alone, inert.
//   Player.SeekChapters  an episode's chapter ticks (1 x 8, the ink at 0.6) hanging from the line, and the tip's invisible anchor
//   Player.ChapterTip    the tip: a square raised plate, the time, the chapter's title and its range
//   Player.SeekReadout   the scrub readout: a square raised plate over the thumb
//   Player.TimeLabel     the digits (12/16, tabular through the slot reservation), or the live slot (LIVE · GO LIVE, lowercase words)
//
// Everything hot is a compositor bind (the fill's scale, the thumb's and the ticks' translates) or a bound text channel.

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

using T = Wavee.Themes.Zune.ZuneTransportLayout;

namespace Wavee.Themes.Zune;

/// <summary>Zune's transport pieces.</summary>
internal static class ZuneTransport
{
    // ══ THE SEEK RAIL ═══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.SeekRail"/>.</summary>
    public static Element SeekRail(SeekRailProps props, in ViewContext ctx)
    {
        if (props.Line)
        {
            var playing = props.Host.Playing;
            return Embed.Comp(() => new ZuneLiveLine(playing));
        }

        var host = props.Host;
        bool enabled = props.Enabled;
        ColorF rail = Tok.TextPrimary with { A = T.RailA };
        ColorF value = enabled ? Tok.AccentDefault : Tok.TextTertiary;

        if (props.Hairline)
            return new BoxEl
            {
                Height = T.LineH, Grow = 1f, ZStack = true, ClipToBounds = true, HitTestVisible = false,
                Fill = rail, OnBoundsChanged = host.BoundsChanged,
                Children = [new BoxEl { Grow = 1f, Fill = value, HitTestVisible = false, TransformOriginX = 0f, Transform = host.FillTransform }],
            };

        var lineKids = new List<Element>(2)
        {
            new BoxEl { Grow = 1f, Height = T.LineH, Fill = value, HitTestVisible = false, TransformOriginX = 0f, Transform = host.FillTransform },
        };
        if (props.Dvr)
            lineKids.Add(new BoxEl
            {
                Grow = 1f, Height = T.LineH, Direction = 0, Justify = FlexJustify.End, HitTestVisible = false,
                Children = [new BoxEl { Width = T.LiveEdgeW, Height = T.LineH, Shrink = 0f, Fill = Tok.AccentDefault, HitTestVisible = false }],
            });

        Element chapters = props.Chapters ?? new BoxEl { HitTestVisible = false };
        var stack = new BoxEl
        {
            ZStack = true, Grow = 1f, Shrink = 1f, MinWidth = 0f, Height = T.HitH, AlignItems = FlexAlign.Center, HitTestVisible = false,
            Children =
            [
                new BoxEl
                {
                    Height = T.LineH, Grow = 1f, Shrink = 1f, MinWidth = 0f, AlignSelf = FlexAlign.Center, Fill = rail,
                    ClipToBounds = true, ZStack = true, HitTestVisible = false, Children = lineKids.ToArray(),
                },
                chapters,
                new BoxEl
                {
                    Width = T.Thumb, Height = T.Thumb, Corners = Radii.Circle(T.Thumb), Fill = Tok.AccentDefault,
                    Opacity = 0f, HoverOpacity = enabled ? 1f : 0f, PressedOpacity = enabled ? 1f : 0f,
                    HitTestVisible = false, Transform = host.ThumbTransform(T.Thumb), OnRealized = host.ThumbRealized,
                },
                host.KeyLadderTick(),
            ],
        };

        // Click anywhere + drag scrub; OnClick is the drag-END commit edge. The ticks never own input.
        return new BoxEl
        {
            Grow = 1f, Shrink = 1f, MinWidth = 0f, Height = T.HitH, Direction = 0, AlignItems = FlexAlign.Center,
            Role = AutomationRole.Slider, IsEnabled = enabled, Cursor = enabled ? CursorId.Hand : null,
            OnBoundsChanged = host.BoundsChanged,
            OnPointerDown = enabled ? host.PointerDown : null,
            OnDrag = enabled ? host.DragMove : null,
            OnClick = enabled ? host.Commit : null,
            OnDragCanceled = enabled ? host.Cancel : null,
            OnHoverMove = props.ChapterRail && enabled ? host.HoverMove : null,
            OnPointerExit = props.ChapterRail ? host.PointerExit : null,
            Children = [stack],
        };
    }

    // ══ THE CHAPTER TICKS AND TIP ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.SeekChapters"/>: the ticks hang from the line; nothing here takes input.</summary>
    public static Element SeekChapters(SeekChaptersProps props, in ViewContext ctx)
    {
        var width = props.Width;
        var fractions = props.Fractions;
        var marks = new Element[fractions.Length + 1];
        ColorF ink = Tok.TextPrimary with { A = T.TickA };
        for (int i = 0; i < fractions.Length; i++)
        {
            float fraction = fractions[i];
            marks[i] = new BoxEl
            {
                Key = "chapter-tick:" + i, Width = T.TickW, Height = T.TickH, Shrink = 0f, AlignSelf = FlexAlign.Start,
                Margin = new Edges4(0f, T.LineTop, 0f, 0f), Fill = ink, HitTestVisible = false,
                Transform = Prop.Of(() => Affine2D.Translation(fraction * width.Value, 0)),
            };
        }
        var tipFraction = props.TipFraction;
        marks[^1] = new BoxEl
        {
            Key = "chapter-tip-anchor", Width = T.TickW, Height = T.TickH, Shrink = 0f, AlignSelf = FlexAlign.Start,
            Margin = new Edges4(0f, T.LineTop, 0f, 0f), HitTestVisible = false, OnRealized = props.TipAnchorRealized,
            Transform = Prop.Of(() => Affine2D.Translation(Math.Clamp(tipFraction(), 0, 1) * width.Value, 0)),
        };
        return new BoxEl
        {
            ZStack = true, Grow = 1f, Shrink = 1f, MinWidth = 0f, Height = T.HitH, HitTestVisible = false, Children = marks,
        };
    }

    /// <summary>The registered template of <see cref="ViewKeys.ChapterTip"/>.</summary>
    public static Element ChapterTip(ChapterTipProps props, in ViewContext ctx)
    {
        var has = props.HasChapter;
        return new BoxEl
        {
            Direction = 1, Gap = T.TipGap, Width = T.TipW, MaxWidth = T.TipMaxW, Padding = T.TipPadding,
            Fill = R.Layer.FloatingChrome, Corners = R.Shape.All(ShapeRole.Overlay), Shadow = Elevation.Tooltip, HitTestVisible = false,
            Children =
            [
                new TextEl(Prop.Of(props.Time)) { Size = T.TipTimeSize, LineHeight = T.TipTimeLine, Color = Tok.TextSecondary },
                new TextEl(Prop.Of(props.Title))
                {
                    Size = T.TipTitleSize, LineHeight = T.TipTitleLine, MaxLines = T.TipTitleLines, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis,
                    Color = Tok.TextPrimary, MinWidth = 0f, Visible = Prop.Of(has),
                },
                new TextEl(Prop.Of(props.Range)) { Size = T.TipTimeSize, LineHeight = T.TipTimeLine, Color = Tok.TextTertiary, Visible = Prop.Of(has) },
            ],
        };
    }

    // ══ THE SCRUB READOUT ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.SeekReadout"/>: the time under the pointer on a square raised plate.</summary>
    public static Element SeekReadout(SeekReadoutProps props, in ViewContext ctx) => new BoxEl
    {
        Fill = R.Layer.FloatingChrome, Corners = R.Shape.All(ShapeRole.Overlay), Shadow = Elevation.Flyout, Padding = T.ReadoutPadding,
        Children = [new TextEl(Prop.Of(props.Text)) { Size = T.ReadoutSize, LineHeight = T.ReadoutLine, Color = Tok.TextPrimary }],
    };

    // ══ THE TIME LABELS ═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.TimeLabel"/>: the digits' slot (the elapsed one hugs what follows it, the
    /// right-hand one what precedes it), or the live slot.</summary>
    public static Element TimeLabel(TimeLabelProps props, in ViewContext ctx)
    {
        if (props.LiveSlot) return Embed.Comp(() => new ZuneLiveSlot(props));

        bool right = props.Remaining;
        bool onMedia = props.Ink is not null;
        ColorF ink = props.Ink ?? Tok.TextSecondary;
        return new BoxEl
        {
            Width = props.IsLive || onMedia ? float.NaN : T.TimeSlotW, MinWidth = onMedia ? T.TimeSlotMediaMinW : T.TimeSlotW, Shrink = 0f,
            // the stage's labels (an ink is handed) sit on the stage's gutter edges (Z-Lyrics: "1:14" on the 48, "-3:04" ending on the 1392); the bar's hug the line
            Direction = 0, AlignItems = FlexAlign.Center, Justify = onMedia ? (right ? FlexJustify.End : FlexJustify.Start) : right ? FlexJustify.Start : FlexJustify.End,
            OnClick = right ? props.ToggleRemaining : null, Cursor = right ? CursorId.Hand : null,
            Role = right ? AutomationRole.Button : AutomationRole.Text,
            // the stage's labels reserve the widest this track can print (an unseen copy of it sizes the slot), so the seek line never moves mid-track
            ZStack = onMedia && props.Widest is not null,
            Children = onMedia && props.Widest is { } widest
                ?
                [
                    new TextEl(Prop.Of(widest)) { Size = T.TimeSize, LineHeight = T.TimeLine, Color = ColorF.Transparent, Wrap = TextWrap.NoWrap },
                    new BoxEl
                    {
                        JustifySelf = right ? FlexAlign.End : FlexAlign.Start, AlignSelf = FlexAlign.Center, HitTestVisible = false,
                        Children = [new TextEl(Prop.Of(props.Label)) { Size = T.TimeSize, LineHeight = T.TimeLine, Color = ink, Wrap = TextWrap.NoWrap }],
                    },
                ]
                :
                [
                    new TextEl(Prop.Of(props.Label))
                    {
                        Size = T.TimeSize, LineHeight = T.TimeLine, Color = ink, HoverColor = right && props.Ink is null ? Tok.TextPrimary : ink, Wrap = TextWrap.NoWrap,
                    },
                ],
        };
    }
}

/// <summary>Nothing to rewind: the 2-px accent line breathes 0.55 ↔ 1 over 3 s while playing in an active window with motion on; otherwise
/// a finite flat track replaces the loop in place, so the frame loop quiesces. Not a slider: no hit test, no role.</summary>
internal sealed class ZuneLiveLine(IReadSignal<bool> playing) : Component
{
    readonly IReadSignal<bool> _playing = playing;

    public override Element Render()
    {
        bool isPlaying = _playing.Value;
        bool windowActive = UseIsActive().Value;
        bool breathe = isPlaying && windowActive && !Design.Reduced;
        UseKeyframes(AnimChannel.Opacity, breathe ? T.LiveLineBreathe : T.LiveLineFlat,
            breathe ? T.LiveLineBreatheMs : T.LiveLineFlatMs, breathe, DepKey.From(breathe));
        return new BoxEl
        {
            Grow = 1f, Shrink = 1f, MinWidth = 0f, Height = T.HitH, Direction = 0, AlignItems = FlexAlign.Center, HitTestVisible = false,
            Children = [new BoxEl { Grow = 1f, Shrink = 1f, MinWidth = 0f, Height = T.LineH, Fill = Tok.AccentDefault, HitTestVisible = false }],
        };
    }
}

/// <summary>The right slot while live: one reservation holding the "live" mark (the static red dot and the outlined word) or, behind a
/// rewindable window, the "go live" word; the swap moves nothing. Which of the two is the model's decided edge.</summary>
internal sealed class ZuneLiveSlot(TimeLabelProps props) : Component
{
    readonly TimeLabelProps _props = props;
    readonly Prop<string> _goLive = Prop.Of(props.GoLiveText);

    public override Element Render()
    {
        var props = _props;
        bool offer = UseComputed(props.OffersGoLive).Value;
        ColorF ink = props.Ink ?? Tok.AccentDefault;
        var @case = R.Case.For(LabelKind.Command);
        Element face = offer
            ? new BoxEl
            {
                Height = T.LiveMarkH, Direction = 0, AlignItems = FlexAlign.Center, Padding = new Edges4(T.LiveMarkPadX, 0f, T.LiveMarkPadX, 0f),
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = props.GoLive,
                Children = [new TextEl(_goLive) { Size = T.LiveSize, LineHeight = T.LiveLine, Weight = T.LiveWeight, Color = ink, Case = @case, Wrap = TextWrap.NoWrap }],
            }
            : new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = T.LiveGap,
                Children =
                [
                    new BoxEl { Width = T.LiveDot, Height = T.LiveDot, Shrink = 0f, Corners = Radii.Circle(T.LiveDot), Fill = Tok.SystemFillCritical },
                    new BoxEl
                    {
                        Height = T.LiveMarkH, Padding = new Edges4(T.LiveMarkPadX, 0f, T.LiveMarkPadX, 0f), BorderWidth = 1f, BorderColor = ink,
                        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Children = [new TextEl(Loc.Get(Strings.Play.Live)) { Size = T.LiveSize, LineHeight = T.LiveLine, Weight = T.LiveWeight, Color = ink, Case = @case, Wrap = TextWrap.NoWrap }],
                    },
                ],
            };
        return new BoxEl { Width = T.LiveSlotW, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, Children = [face] };
    }
}
