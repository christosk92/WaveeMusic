// ── Themes/Classic/Structure/Views/Player/ClassicTransport.cs — Classic's seek rail, chapter ticks, readout and time labels (leftovers P3) ──
//
// From `Shell.PlayerBar.UI.cs` (`BarSeekRail`'s tree, `BarLiveLine`, the readout bubble, `BarTimeText`'s tree, `BarLiveSlot`) and
// `Shell.PlayerBar.Chapters.UI.cs` (the chapter ticks, the tip's anchor and its bubble): the element trees the core built before, with
// the same keys, roles, binds and handlers, so the bar, the stage's transport card and the on-media strip draw and behave as they did.
// The core hosts (`Shell.SeekBar` / `Shell.TimeText`, `[CoreHost]`) keep the scrub controller, every hook and overlay, the chapter lease
// and the time formatting; the props say what to draw and carry the binds and handlers (`ISeekRailHost`).
//
//   THE COST MODEL (ch 20 §9). Everything hot is a compositor bind (the fill's scale, the thumb's translate, the ticks' and the anchor's
//   translates) or a bound text channel (the digits, the readout, the tip's lines): a template runs only when its host re-renders on an
//   enablement / mode / advancing / live edge.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's transport pieces: the seek rail, the chapter ticks and tip, the scrub readout, the time labels.</summary>
internal static class ClassicTransport
{
    // ══ THE SEEK RAIL ═══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.SeekRail"/>: the bespoke media rail (not a Slider) over the host's binds,
    /// or the live line when there is nothing to rewind.</summary>
    public static Element SeekRail(SeekRailProps props, in ViewContext ctx)
    {
        // NOTHING TO REWIND: the rail stops pretending to be one.
        if (props.Line)
        {
            var playing = props.Host.Playing;
            return Embed.Comp(() => new LiveLine(playing));
        }

        var host = props.Host;
        bool enabled = props.Enabled;
        // The mini bar's hairline (TC2, board C-PlayerBar mini): the played fraction as a 2-DIP line, the strong rail behind it, nothing to grab.
        if (props.Hairline)
            return new BoxEl
            {
                Height = ClassicPlayerTiers.MiniProgressH, Grow = 1f, ZStack = true, ClipToBounds = true, HitTestVisible = false,
                Fill = Tok.FillControlStrong, OnBoundsChanged = host.BoundsChanged,
                Children =
                [
                    new BoxEl
                    {
                        Grow = 1f, Fill = enabled ? Tok.AccentDefault : Tok.FillControlStrong, HitTestVisible = false,
                        TransformOriginX = 0f, Transform = host.FillTransform,
                    },
                ],
            };
        Element chapters = props.Chapters ?? new BoxEl { HitTestVisible = false };
        var s = Slider.DefaultStyle;
        var fill = new BoxEl
        {
            Grow = 1f, Height = s.TrackHeight, AlignSelf = FlexAlign.Center,
            Fill = enabled ? s.ValueFill : s.ValueFillDisabled,
            HoverFill = enabled ? s.ValueFillPointerOver : s.ValueFillDisabled,
            PressedFill = enabled ? s.ValueFillPressed : s.ValueFillDisabled,
            // Square: a scaled rounded rect shows cap slivers at the value edge.
            Corners = CornerRadius4.All(0f), HitTestVisible = false,
            TransformOriginX = 0f, Transform = host.FillTransform,
        };
        // The DVR rail's right end IS the live edge, so it is marked.
        Element[] railKids = props.Dvr
            ? new Element[]
            {
                fill,
                new BoxEl
                {
                    Grow = 1f, Height = s.TrackHeight, Direction = 0, Justify = FlexJustify.End, HitTestVisible = false,
                    Children = [new BoxEl { Width = ClassicTransportLayout.LiveEdgeWidth, Height = s.TrackHeight, Shrink = 0f, Fill = Tok.AccentDefault, HitTestVisible = false }],
                },
            }
            : new Element[] { fill };

        float rest = enabled ? s.InnerRestScale : s.InnerDisabledScale;
        const float ring = ClassicTransportLayout.SeekRingDiameter;
        var stack = new BoxEl
        {
            // Shrink + MinWidth 0 on every layer: the engine's flex items do not shrink by default, and a ZStack can
            // measure its available width as its own size, so without them the rail keeps that width and paints under the
            // right cluster to the window edge.
            ZStack = true, Grow = 1f, Shrink = 1f, MinWidth = 0f, Height = ClassicTransportLayout.SeekHitHeight, AlignItems = FlexAlign.Center, HitTestVisible = false,
            Children =
            [
                new BoxEl
                {
                    Height = s.TrackHeight, Grow = 1f, Shrink = 1f, MinWidth = 0f, AlignSelf = FlexAlign.Center,
                    Fill = enabled ? s.RailFill : s.RailFillDisabled,
                    Corners = CornerRadius4.All(s.TrackCornerRadius), ClipToBounds = true, ZStack = true,
                    HitTestVisible = false, Children = railKids,
                },
                chapters,
                new BoxEl
                {
                    Width = ring, Height = ring,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Corners = CornerRadius4.All(s.ThumbCornerRadius),
                    Fill = s.ThumbRing, HoverFill = s.ThumbRing, PressedFill = s.ThumbRing,
                    BorderBrush = s.ThumbBorder, BorderWidth = s.ThumbBorderWidth,
                    Opacity = 0f, HoverOpacity = enabled ? 1f : 0f, PressedOpacity = enabled ? 1f : 0f,
                    HitTestVisible = false, Transform = host.ThumbTransform(ring), OnRealized = host.ThumbRealized,
                    Children =
                    [
                        new BoxEl
                        {
                            Width = s.InnerThumbDiameter, Height = s.InnerThumbDiameter,
                            Corners = CornerRadius4.All(s.InnerThumbDiameter * 0.5f),
                            Fill = enabled ? s.ThumbFill : s.ThumbFillDisabled,
                            HoverFill = enabled ? s.ThumbFillPointerOver : s.ThumbFillDisabled,
                            PressedFill = enabled ? s.ThumbFillPressed : s.ThumbFillDisabled,
                            ScaleX = rest, ScaleY = rest,
                            HoverScale = enabled ? s.InnerHoverScale / s.InnerRestScale : 1f,
                            PressScale = enabled ? s.InnerPressScale / s.InnerRestScale : 1f,
                            HoverDurationMs = Design.Motion.Standard, PressDurationMs = Design.Motion.Standard,
                            HitTestVisible = false,
                        },
                    ],
                },
                // The keyboard ladder's commit clock (zero-size; the rail never re-renders for it): placed wherever a rail is, so the bar,
                // the fullscreen stage and the on-media transport all commit their Left/Right steps.
                host.KeyLadderTick(),
            ],
        };

        // Click-anywhere + drag scrub; OnClick is the drag-END commit edge. Chapter decoration never owns input.
        return new BoxEl
        {
            Grow = 1f, Shrink = 1f, MinWidth = 0f, Height = ClassicTransportLayout.SeekHitHeight, Direction = 0, AlignItems = FlexAlign.Center,
            Role = AutomationRole.Slider, IsEnabled = enabled,
            Cursor = enabled ? CursorId.Hand : null,
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

    /// <summary>The rail for a broadcast with NOTHING TO REWIND: a 2-DIP accent line that breathes 0.55 ↔ 1 over 3 s —
    /// only while playing, the window is active and motion is not reduced; otherwise the loop is REPLACED IN PLACE by a
    /// finite flat track so the frame loop quiesces. Not a slider: no hit test, no role.</summary>
    private sealed class LiveLine(IReadSignal<bool> playing) : Component
    {
        readonly IReadSignal<bool> _playing = playing;

        public override Element Render()
        {
            bool isPlaying = _playing.Value;
            bool windowActive = UseIsActive().Value;
            bool breathe = isPlaying && windowActive && !Design.Reduced;   // reduced motion is a VALUE, never a hook branch
            UseKeyframes(AnimChannel.Opacity, breathe ? ClassicTransportLayout.LiveLineBreathe : ClassicTransportLayout.LiveLineFlat,
                breathe ? ClassicTransportLayout.LiveLineBreatheMs : ClassicTransportLayout.LiveLineFlatMs, breathe, DepKey.From(breathe));

            // Shrink on both, like the rail (G-254): a grown line keeps its last arranged width across a narrowing
            // resize unless it may shrink, and pushes the right cluster off the bar.
            return new BoxEl
            {
                Grow = 1f, Shrink = 1f, Height = ClassicTransportLayout.SeekHitHeight, Direction = 0, AlignItems = FlexAlign.Center,
                HitTestVisible = false, Role = AutomationRole.None,
                Children =
                [
                    new BoxEl
                    {
                        Grow = 1f, Shrink = 1f, MinWidth = 0f, Height = ClassicTransportLayout.LiveLineThickness,
                        Corners = CornerRadius4.All(ClassicTransportLayout.LiveLineCorner),
                        Fill = Tok.AccentDefault, HitTestVisible = false,
                    },
                ],
            };
        }
    }

    // ══ THE CHAPTER TICKS AND TIP ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.SeekChapters"/>: pure decoration over the scrub surface. Neither the ticks
    /// nor the moving anchor receive input.</summary>
    public static Element SeekChapters(SeekChaptersProps props, in ViewContext ctx)
    {
        var width = props.Width;
        var fractions = props.Fractions;
        var marks = new Element[fractions.Length + 1];
        for (int i = 0; i < fractions.Length; i++)
        {
            float fraction = fractions[i];
            marks[i] = new BoxEl
            {
                Key = "chapter-tick:" + i, Width = 1f, Height = ClassicTransportLayout.ChapterTickHeight, Shrink = 0, AlignSelf = FlexAlign.Center,
                Fill = Tok.TextSecondary, HitTestVisible = false,
                Transform = Prop.Of(() => Affine2D.Translation(fraction * width.Value, 0)),
            };
        }
        // A live node anchor lets the stock overlay positioner follow the composited pointer position and
        // clamp the bubble to the window. It is invisible and cannot steal the rail's capture/keyboard focus.
        var tipFraction = props.TipFraction;
        marks[^1] = new BoxEl
        {
            Key = "chapter-tip-anchor", Width = 1, Height = ClassicTransportLayout.ChapterTickHeight, Shrink = 0, AlignSelf = FlexAlign.Center,
            HitTestVisible = false, OnRealized = props.TipAnchorRealized,
            Transform = Prop.Of(() => Affine2D.Translation(Math.Clamp(tipFraction(), 0, 1) * width.Value, 0)),
        };
        return new BoxEl
        {
            ZStack = true, Grow = 1, Shrink = 1, MinWidth = 0, Height = ClassicTransportLayout.SeekHitHeight,
            AlignItems = FlexAlign.Center, HitTestVisible = false, Children = marks,
        };
    }

    /// <summary>The registered template of <see cref="ViewKeys.ChapterTip"/>: the time, the chapter's title and its range.</summary>
    public static Element ChapterTip(ChapterTipProps props, in ViewContext ctx)
    {
        var has = props.HasChapter;
        return new BoxEl
        {
            Direction = 1, Gap = ClassicTransportLayout.ChapterTipGap, Width = ClassicTransportLayout.ChapterTipWidth, MaxWidth = ClassicTransportLayout.ChapterTipMaxWidth,
            Acrylic = Tok.AcrylicFlyout, BorderColor = Tok.StrokeFlyoutDefault, BorderWidth = 1,
            Corners = Radii.ControlAll, Shadow = Elevation.Tooltip, Padding = ClassicTransportLayout.ChapterTipPadding,
            HitTestVisible = false,
            Children =
            [
                new TextEl(Prop.Of(props.Time))
                { Size = ClassicTransportLayout.ChapterTipTimeSize, Weight = ClassicTransportLayout.ChapterTipWeight, Color = Tok.TextPrimary },
                new TextEl(Prop.Of(props.Title))
                {
                    Size = ClassicTransportLayout.ChapterTipTitleSize, Weight = ClassicTransportLayout.ChapterTipWeight,
                    MaxLines = ClassicTransportLayout.ChapterTipTitleLines, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis,
                    Color = Tok.TextPrimary, MinWidth = 0,
                    Visible = Prop.Of(has),
                },
                new TextEl(Prop.Of(props.Range))
                {
                    Size = ClassicTransportLayout.ChapterTipTimeSize, Color = Tok.TextSecondary,
                    Visible = Prop.Of(has),
                },
            ],
        };
    }

    // ══ THE SCRUB READOUT ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.SeekReadout"/>: the time under the pointer, in the Slider's own kind of
    /// bubble over the thumb (signal-bound, so the bubble never re-renders per move).</summary>
    public static Element SeekReadout(SeekReadoutProps props, in ViewContext ctx) => new BoxEl
    {
        Fill = ColorF.Transparent, Acrylic = Tok.AcrylicFlyout,
        BorderColor = Tok.StrokeFlyoutDefault, BorderWidth = 1f,
        Corners = Radii.ControlAll, Shadow = Elevation.Flyout,
        Padding = ClassicTransportLayout.ReadoutPadding,
        Children = [new TextEl(Prop.Of(props.Text)) { Size = ClassicTransportLayout.ReadoutSize, Color = Tok.TextPrimary }],
    };

    // ══ THE TIME LABELS ═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.TimeLabel"/>: the digits' slot, or the live slot.</summary>
    public static Element TimeLabel(TimeLabelProps props, in ViewContext ctx)
    {
        if (props.LiveSlot) return Embed.Comp(() => new LiveSlot(props));

        bool right = props.Remaining;
        var ink = props.Ink;
        var caption = FluentGpu.Dsl.Ui.Caption("");
        Prop<string> label = Prop.Of(props.Label);
        return new BoxEl
        {
            // The slot is a reservation at rest (0:09 → 0:10 moves nothing) and a FLOOR while live (h:mm:ss).
            Width = props.IsLive ? float.NaN : ClassicTransportLayout.TimeSlotWidth, MinWidth = ClassicTransportLayout.TimeSlotWidth, Shrink = 0f,
            Direction = 0, AlignItems = FlexAlign.Center,
            Justify = right ? FlexJustify.Start : FlexJustify.End,   // the digits hug the rail
            HoverFill = right ? (ink is null ? Tok.FillSubtleSecondary : R.OnMedia.GlassHover) : ColorF.Transparent,
            PressedFill = right ? (ink is null ? Tok.FillSubtleTertiary : R.OnMedia.GlassPressed) : ColorF.Transparent,
            Corners = Radii.ControlAll,
            OnClick = right ? props.ToggleRemaining : null,
            Cursor = right ? CursorId.Hand : null,
            Children =
            [
                ink is { } inkColor
                    ? caption with { Text = label, Color = inkColor, Wrap = TextWrap.NoWrap }
                    : caption with { Text = label, Wrap = TextWrap.NoWrap },
            ],
        };
    }

    /// <summary>The right slot while live: ONE 104-DIP reservation, right-aligned, holding the LIVE word-mark or —
    /// behind a rewindable window — the GO LIVE action. The swap moves nothing (parity 65); which of the two is the
    /// model's DECIDED edge state, never a threshold here.</summary>
    private sealed class LiveSlot(TimeLabelProps props) : Component
    {
        readonly TimeLabelProps _props = props;
        readonly Prop<string> _goLive = Prop.Of(props.GoLiveText);

        public override Element Render()
        {
            var props = _props;
            bool offer = UseComputed(props.OffersGoLive).Value;
            ColorF fg = props.Ink ?? R.Ink.AccentDecor;
            bool media = props.Ink is not null;
            Element face = offer
                ? new BoxEl
                {
                    // A TEXT button, not a plate — a filled accent here would outrank the transport itself.
                    MinWidth = ClassicTransportLayout.GoLiveMinWidth, Shrink = 0f, Height = ClassicTransportLayout.GoLiveHeight,
                    Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Padding = new Edges4(Spacing.XS, 0f, Spacing.XS, 0f), Corners = Radii.ControlAll,
                    HoverFill = media ? R.OnMedia.GlassHover : Tok.FillSubtleSecondary,
                    PressedFill = media ? R.OnMedia.GlassPressed : Tok.FillSubtleTertiary,
                    HoverScale = R.Motion.ScaleSubtle.Hover, PressScale = R.Motion.ScaleSubtle.Press,
                    Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                    OnClick = props.GoLive,
                    Children =
                    [
                        new TextEl(_goLive)
                        {
                            Size = ClassicTransportLayout.LiveTextSize, LineHeight = ClassicTransportLayout.LiveTextLineHeight,
                            Weight = ClassicTransportLayout.LiveTextWeight, Color = fg, Wrap = TextWrap.NoWrap,
                        },
                    ],
                }
                : new BoxEl
                {
                    Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, Gap = Spacing.XXS,
                    Children =
                    [
                        // STATIC red: a blinking dot would keep the frame loop awake for its whole life.
                        new BoxEl
                        {
                            Width = ClassicTransportLayout.LiveDotSize, Height = ClassicTransportLayout.LiveDotSize, Shrink = 0f,
                            Corners = CornerRadius4.All(ClassicTransportLayout.LiveDotCorner), Fill = Tok.SystemFillCritical,
                        },
                        new BoxEl
                        {
                            Height = ClassicTransportLayout.LiveBadgeHeight, Padding = new Edges4(Spacing.XXS, 0f, Spacing.XXS, 0f), Shrink = 0f,
                            Corners = CornerRadius4.All(ClassicTransportLayout.LiveBadgeCorner), BorderWidth = 1f, BorderColor = fg,
                            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                            Children =
                            [
                                new TextEl(Loc.Get(Strings.Play.Live))
                                {
                                    Size = ClassicTransportLayout.LiveTextSize, LineHeight = ClassicTransportLayout.LiveTextLineHeight,
                                    Weight = ClassicTransportLayout.LiveTextWeight, Color = fg, Wrap = TextWrap.NoWrap,
                                },
                            ],
                        },
                    ],
                };

            return new BoxEl
            {
                Width = ClassicTransportLayout.LiveSlotWidth, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End,
                Children = [face],
            };
        }
    }
}
