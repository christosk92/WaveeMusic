// ── Themes/Classic/Structure/Views/Stage/ClassicStageTransport.cs — Classic's `Stage.Transport` (themes plan 6.1, T7a1) ──────
//
// From `Shell\Stage.UI.cs`'s `TransportCard` and its parts: the acrylic card with the seek row (the bar's own seek and times) and the three
// clusters (the "Playing from" column; shuffle · previous · the play disc · next · repeat; heart · mute · volume · device · "…"). It fills its
// wrapper (Grow); the root places it under the chrome's Show. Where the card cannot seat the three clusters (`Layout.TransportStacked`: a
// narrow window, C-Stage-Compact) the row stacks to heart · previous · play · next · "…" spread across the card at its 12 padding; the disc,
// the volume slider, the card's padding and its cluster gaps are the window tier's (`ClassicStageTiers.StageOf`: Small's 48 disc and 88
// slider, C-Stage-Small; Huge's 24 / 14 card with 12 between the centred buttons, C-Stage-Huge).
//
// What the card draws that is playback state arrives as `StageTransportFacts`, read INSIDE this render (`props.Facts()`): a signal read there
// subscribes the card, as the component read them. The three small parts that kept their own components keep them (the mute button, the volume
// slider with its mirror cell, the device button with its flyout), each reading its own signal through a `Func` on the props, so a mute, a volume
// drag or a device change re-renders that part and not the card (V-U50). The "…" menu is `Stage.NowPlayingMenu` (asked at OPEN time) and holds the
// chrome while it is open (V-U22).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;

using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's stage transport card.</summary>
internal static class ClassicStageTransport
{
    static readonly FormatCache<int> s_percent = FormatCache.Create<int>();

    /// <summary>The registered template of <see cref="ViewKeys.StageTransport"/>.</summary>
    public static Element Template(StageTransportProps props, in ViewContext ctx)
    {
        var st = props.Stage;
        var L = st.Layout.Value;
        var set = st.Accent.Value;
        var overlay = ctx.Hooks.UseContext(Overlay.Service);
        var f = props.Facts();
        var verbs = props.Verbs;
        var tier = ClassicStageTiers.StageOf(ctx.Tier());
        bool compact = L.Aspect == Stage.Aspect.Compact;
        bool stacked = L.TransportStacked;
        float disc = MathF.Min(compact ? ClassicStageGeometry.PlayDiscCompact : ClassicStageGeometry.PlayDisc, tier.PlayDiscMax);
        var menuOpen = st.MenuOpen;
        var overControls = st.OverControls;

        var more = new BoxEl
        {
            Width = ClassicStageGeometry.GlyphBox, Height = ClassicStageGeometry.GlyphBox, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
            Fill = Ink.GlassRest, HoverFill = Ink.GlassHover, PressedFill = Ink.GlassPressed, BrushTransitionMs = Design.Motion.Faster,
            Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, AllowFocusOnInteraction = false, ClickRequestsContext = true,
            Children = [new TextEl(Icons.More) { Size = ClassicStageGeometry.SatelliteGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = Ink.Ink }],
        }.WithContextMenu(overlay,
            () => { var m = Stage.NowPlayingMenu?.Invoke(); if (m is { } model && ContextMenu.HasAnyEnabled(model)) menuOpen(true); return m; },  // the "…" menu holds the chrome (V-U22); an empty menu never opens, so never counts
            new ContextMenuOptions { OnClosed = _ => menuOpen(false) });                                        // ContextMenu.cs:43

        float rowH = MathF.Min(ClassicStageGeometry.ClusterRowMax, L.TransportH - ClassicStageGeometry.ClusterRowReserve);   // 10 pad + 32 seek hit-box + this ≤ the card (Compact is 96)
        Element row;
        if (stacked)
        {
            // C-Stage-Compact: heart · previous · play · next · "…", spread across the card (the rest is the "…" menu's and the bar's).
            row = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween, Height = rowH,
                Children =
                [
                    ClassicStageTopBar.Held(Heart(f.Saved, f.Like, set.Fill), overControls),
                    ClassicStageTopBar.Held(ToolTip.Wrap(Glyph(Icons.Previous, verbs.Previous, ClassicStageGeometry.GlyphBox, ClassicStageGeometry.TransportGlyph, f.PrevEnabled), Loc.Get(Strings.Player.Previous)), overControls),
                    ClassicStageTopBar.Held(Play(f.Playing, f.PrimaryEnabled, disc, set, verbs), overControls),
                    ClassicStageTopBar.Held(ToolTip.Wrap(Glyph(Icons.Next, verbs.Next, ClassicStageGeometry.GlyphBox, ClassicStageGeometry.TransportGlyph, f.NextEnabled), Loc.Get(Strings.Player.Next)), overControls),
                    ClassicStageTopBar.Held(more, overControls),
                ],
            };
        }
        else
        {
            // A TIER CROSSING (Huge's wider padding and gaps) moves every control of the row: each glides to its new place on the tier's shove
            // (a position FLIP; padding and gaps cannot ease themselves), so the saved heart and its neighbours never step
            var right = new List<Element>(5) { Glide(Heart(f.Saved, f.Like, set.Fill)), Glide(Embed.Comp(new MuteGlyph.Props(props), static () => new MuteGlyph())) };
            if (L.VolumeFits) right.Add(Glide(Embed.Comp(new VolumeRow.Props(set, tier.VolumeW, props), static () => new VolumeRow())));   // only where the cluster still fits the card
            right.Add(Glide(Embed.Comp(new DeviceButton.Props(props), static () => new DeviceButton())));
            right.Add(Glide(more));
            row = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Height = rowH, Layout = ClassicTierMotion.Shove,
                Children =
                [
                    new BoxEl
                    {
                        Grow = 1f, Basis = 0f, MinWidth = 0f, Direction = 1,
                        Children =
                        [
                            // a formatted key: the generated method formats; Loc.Get would render "[…]" (V-D15). No source ⇒ no label (never "Playing from search" for nothing).
                            new TextEl(f.PlayingFrom) { Size = ClassicStageGeometry.SourceLabelSize, LineHeight = ClassicStageGeometry.SourceLabelLine, Color = Ink.InkTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                            new TextEl(f.Source) { Size = ClassicStageGeometry.SourceNameSize, LineHeight = ClassicStageGeometry.SourceNameLine, Weight = ClassicStageGeometry.SourceNameWeight, Color = Ink.Ink, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                        ],
                    },
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center, Gap = tier.ClusterGap ?? Spacing.S, Shrink = 0f, Layout = ClassicTierMotion.Shove,
                        OnHoverMove = _ => overControls(true), OnPointerExit = () => overControls(false),
                        Children =
                        [
                            Glide(ToolTip.Wrap(Satellite(Icons.Shuffle, verbs.ToggleShuffle, f.CanTransport, f.Shuffle, set.Fill), Loc.Get(Strings.Player.Shuffle))),
                            Glide(ToolTip.Wrap(Glyph(Icons.Previous, verbs.Previous, ClassicStageGeometry.GlyphBox, ClassicStageGeometry.TransportGlyph, f.PrevEnabled), Loc.Get(Strings.Player.Previous))),
                            Glide(Play(f.Playing, f.PrimaryEnabled, disc, set, verbs)),
                            Glide(ToolTip.Wrap(Glyph(Icons.Next, verbs.Next, ClassicStageGeometry.GlyphBox, ClassicStageGeometry.TransportGlyph, f.NextEnabled), Loc.Get(Strings.Player.Next))),
                            Glide(ToolTip.Wrap(Satellite(f.RepeatOne ? Icons.RepeatOne : Icons.RepeatAll, verbs.CycleRepeat, f.CanTransport, f.RepeatOn, set.Fill), Loc.Get(Strings.Player.Repeat))),
                        ],
                    },
                    // the right cluster's buttons hug their own row: the column's empty lead holds nothing
                    new BoxEl
                    {
                        Grow = 1f, Basis = 0f, MinWidth = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End,
                        Children = [new BoxEl { Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Gap = tier.RightGap, Layout = ClassicTierMotion.Shove, OnHoverMove = _ => overControls(true), OnPointerExit = () => overControls(false), Children = right.ToArray() }],
                    },
                ],
            };
        }

        // the audio modes' card (Video mode's transport is the control bar over the picture, `ClassicStageVideoBar`)
        return new BoxEl
        {
            Grow = 1f, Height = L.TransportH, Direction = 1, Padding = stacked ? ClassicStageTiers.StackedTransportPadding : tier.TransportPadding, Corners = Radii.CardAll,
            Acrylic = Ink.Card, Shadow = Elevation.Card, ClipToBounds = true,
            // the chrome is held by the controls (the seek bar, the buttons), not by the card: a pointer resting on "Playing from" or the
            // card's padding idles out like one resting on the picture
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Layout = ClassicTierMotion.Shove,
                    // the bar's own seek; its scrub edge holds the chrome through the new onScrubbing seam (§4.12 — a PlayerChromeFeed is inert without a MediaPlayerElement owner)
                    // the card idles out with the chrome: its seek and times hold while it is hidden (the props' WithChrome hosts)
                    Children = [(props.TimeTextWithChrome ?? props.TimeText)(false, Ink.InkSecondary), new BoxEl { Grow = 1f, Shrink = 1f, MinWidth = 0f, OnHoverMove = _ => overControls(true), OnPointerExit = () => overControls(false), Children = [(props.SeekBarWithChrome ?? props.SeekBar)(st.Scrubbing)] }, (props.TimeTextWithChrome ?? props.TimeText)(true, Ink.InkSecondary)],
                },
                row,
            ],
        };
    }

    /// <summary><paramref name="control"/> gliding to its new place when a tier crossing moves it (<see cref="ClassicTierMotion.Shove"/>).</summary>
    static Element Glide(Element control) => control with { Layout = ClassicTierMotion.Shove };

    /// <summary>The 56-DIP filled play disc: a hand-built circular BoxEl on the surface's AccentSet (Button.Create has no circular
    /// variant, so <c>ButtonPalette.ForAccent</c> is not used — V-U46).</summary>
    static Element Play(bool playing, bool enabled, float box, AccentSet set, IPlayerVerbs verbs) => new BoxEl
    {
        Width = box, Height = box, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.Circle(box),
        Fill = enabled ? set.Fill : Ink.ScrimRest, HoverFill = enabled ? set.FillSecondary : Ink.ScrimRest, PressedFill = enabled ? set.FillTertiary : Ink.ScrimRest,
        BrushTransitionMs = Design.Motion.Faster, Shadow = enabled ? Elevation.Card : (ShadowSpec?)null,
        HoverScale = R.Motion.ScaleEmphatic.HoverIf(enabled), PressScale = R.Motion.ScaleEmphatic.PressIf(enabled),
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, IsEnabled = enabled,
        OnClick = () => verbs.TogglePlay("stage.button"), Cursor = enabled ? CursorId.Hand : (CursorId?)null,
        Children = [new TextEl(playing ? Icons.Pause : Icons.Play) { Size = ClassicStageGeometry.PlayGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = enabled ? set.Ink : Ink.InkTertiary }],
    };

    /// <summary>A plateless 40-DIP glyph button (glass on hover).</summary>
    static BoxEl Glyph(string glyph, Action? onClick, float box, float glyphSize, bool enabled = true, Action<NodeHandle>? onRealized = null) => new()
    {
        Width = box, Height = box, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
        Fill = Ink.GlassRest, HoverFill = enabled ? Ink.GlassHover : Ink.GlassRest, PressedFill = enabled ? Ink.GlassPressed : Ink.GlassRest, BrushTransitionMs = Design.Motion.Faster,
        HoverScale = R.Motion.ScaleEmphatic.HoverIf(enabled), PressScale = R.Motion.ScaleEmphatic.PressIf(enabled),
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, IsEnabled = enabled, OnClick = enabled ? onClick : null,
        Cursor = enabled ? CursorId.Hand : (CursorId?)null, OnRealized = onRealized,
        Children = [new TextEl(glyph) { Size = glyphSize, FontFamily = Tok.Definition.Fonts.Icon, Color = enabled ? Ink.InkSecondary : Ink.InkTertiary, HoverColor = enabled ? Ink.Ink : Ink.InkTertiary }],
    };

    /// <summary>Shuffle / repeat: accent-filled while latched (the prototype's <c>.ab.chk</c>).</summary>
    static BoxEl Satellite(string glyph, Action onClick, bool enabled, bool latched, ColorF accent) => new()
    {
        Width = ClassicStageGeometry.GlyphBox, Height = ClassicStageGeometry.GlyphBox, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
        Fill = latched && enabled ? accent : Ink.GlassRest, HoverFill = latched && enabled ? accent with { A = ClassicStageGeometry.LatchedHoverAlpha } : enabled ? Ink.GlassHover : Ink.GlassRest,
        PressedFill = enabled ? Ink.GlassPressed : Ink.GlassRest, BrushTransitionMs = Design.Motion.Faster,
        HoverScale = R.Motion.ScaleEmphatic.HoverIf(enabled), PressScale = R.Motion.ScaleEmphatic.PressIf(enabled),
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, IsEnabled = enabled, OnClick = onClick, Cursor = enabled ? CursorId.Hand : (CursorId?)null,
        Children = [new TextEl(glyph) { Size = ClassicStageGeometry.SatelliteGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = !enabled ? Ink.InkTertiary : latched ? ColorContrast.PickContrast(accent) : Ink.InkSecondary, HoverColor = !enabled ? Ink.InkTertiary : latched ? ColorContrast.PickContrast(accent) : Ink.Ink }],
    };

    static BoxEl Heart(bool saved, Action? onLike, ColorF accent) => new()
    {
        Width = ClassicStageGeometry.GlyphBox, Height = ClassicStageGeometry.GlyphBox, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
        Fill = Ink.GlassRest, HoverFill = onLike is null ? Ink.GlassRest : Ink.GlassHover, PressedFill = onLike is null ? Ink.GlassRest : Ink.GlassPressed, BrushTransitionMs = Design.Motion.Faster,
        Role = AutomationRole.Button, Focusable = onLike is not null, AllowFocusOnInteraction = false, OnClick = onLike, Cursor = onLike is null ? (CursorId?)null : CursorId.Hand, BlocksDragArm = true,
        Children = [new BoxEl { Key = saved ? "sh:on" : "sh:off", Children = [new TextEl(saved ? Icons.HeartFill : Icons.Heart) { Size = ClassicStageGeometry.HeartGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = saved ? accent : Ink.InkSecondary, HoverColor = saved ? accent : Ink.Ink }] }],
    };

    sealed class MuteGlyph : Component
    {
        public sealed record Props(StageTransportProps T);

        public override Element Render()
        {
            var t = UseProps<Props>().T;
            bool muted = t.Muted();
            return ToolTip.Wrap(Glyph(muted ? Icons.Mute : Icons.Volume, t.ToggleMute, ClassicStageGeometry.GlyphBox, ClassicStageGeometry.TransportGlyph), Loc.Get(muted ? Strings.Player.Unmute : Strings.Player.Mute));
        }
    }

    /// <summary>The stock Slider in the surface accent at a fixed length (re-pushed props: a new accent re-skins in place).</summary>
    sealed class VolumeRow : Component
    {
        public sealed record Props(AccentSet Set, float Length, StageTransportProps T);
        static readonly Slider.SliderOptions Options = new()
        {
            ThumbToolTipValueConverter = static v => s_percent.Get(Math.Clamp((int)MathF.Round(v * ClassicStageGeometry.PercentScale), 0, ClassicStageGeometry.PercentScaleMax), static p => p + "%"),   // cached: the tooltip re-reads per drag frame
        };
        readonly FloatSignal _level = new(0f);

        public override Element Render()
        {
            var p = UseProps<Props>();
            var t = p.T;
            UseSignalEffect(() => _level.SetIfChanged(t.Volume()));
            var style = Slider.DefaultStyle with
            {
                RailFill = Ink.Ink with { A = ClassicStageGeometry.RailAlpha }, RailFillDisabled = Ink.Ink with { A = ClassicStageGeometry.RailDisabledAlpha },
                ValueFill = p.Set.Fill, ValueFillPointerOver = p.Set.FillSecondary, ValueFillPressed = p.Set.FillTertiary, ValueFillDisabled = p.Set.Fill with { A = ClassicStageGeometry.ThumbDisabledAlpha },
                ThumbRing = Tok.FillControlSolid, ThumbFill = p.Set.Fill, ThumbFillPointerOver = p.Set.FillSecondary, ThumbFillPressed = p.Set.FillTertiary, ThumbFillDisabled = p.Set.Fill with { A = ClassicStageGeometry.ThumbDisabledAlpha },
                ThumbBorder = GradientSpec.Solid(Ink.Stroke),
            };
            return VolumeSlider.Create(_level, t.SetVolume, Options, length: p.Length, thickness: ClassicStageGeometry.VolumeThickness, style: style);
        }
    }

    /// <summary>"This PC ▾" — the two-section device picker over the SAME items the player bar builds. Over the picture (<see cref="Props.OnPicture"/>,
    /// the control bar `ClassicStageVideoBar`) it is the device glyph alone on the on-media ink, the same flyout.</summary>
    internal sealed class DeviceButton : Component
    {
        public sealed record Props(StageTransportProps T, bool OnPicture = false);

        public override Element Render()
        {
            var p = UseProps<Props>();
            var t = p.T;
            var st = t.Stage;
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var overlay = UseContext(Overlay.Service);
            var device = t.Device();
            void Toggle()
            {
                if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                t.OpenDevicePicker();
                var opened = overlay.Open(() => anchor.Value, () => MenuFlyout.Create(t.DeviceItems(), () => handle.Value?.Close(), size: OverlayFit.MenuSize),
                    FlyoutPlacement.TopEdgeAlignedRight, new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = true });
                st.MenuOpen(true);                                                          // the flyout holds the chrome (V-U22)
                opened.ClosedAction = () => { handle.Value = null; st.MenuOpen(false); };
                handle.Value = opened;
            }
            if (p.OnPicture)
                return ToolTip.Wrap(ClassicStageVideoBar.Glyph(device.Glyph, Toggle, true, onRealized: h => anchor.Value = h) with { Key = "picture:bar:device" }, device.Name);
            return new BoxEl
            {
                Height = ClassicStageGeometry.DeviceHeight, Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Padding = ClassicStageGeometry.DevicePadding, Corners = Radii.ControlAll,
                Fill = Ink.GlassRest, HoverFill = Ink.GlassHover, PressedFill = Ink.GlassPressed, BrushTransitionMs = Design.Motion.Faster, BorderWidth = 1f, BorderColor = Ink.Stroke,
                Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = Toggle, OnRealized = h => anchor.Value = h, MaxWidth = ClassicStageGeometry.DeviceMaxWidth,
                Shrink = 1f, MinWidth = Stage.Layout.DeviceMinW,   // the name gives way before the cluster leaves the card (Layout.TransportRightMinW)
                Children =
                [
                    new TextEl(device.Glyph) { Size = ClassicStageGeometry.DeviceGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = Ink.Ink },
                    new TextEl(device.Name) { Size = ClassicStageGeometry.DeviceNameSize, LineHeight = ClassicStageGeometry.DeviceNameLine, Color = Ink.Ink, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
                    new TextEl(Icons.ChevronDown) { Size = ClassicStageGeometry.ChevronGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = Ink.InkSecondary },
                ],
            };
        }
    }
}

/// <summary>Where the transport card stands: the bottom edge (AlignSelf End), full width at the tier's gutter, right of the art on the Compact
/// class (<c>Layout.TransportLeft</c>), and a gutter short of a docked gallery that runs to the foot (<c>Layout.TransportRight</c>, C-Stage-Huge).
/// Its own component so a gallery toggle re-renders this placement and not the stage's root; the card yields its width on the face's re-centre
/// tween (re-laid out as it moves, so the clusters re-centre with it). The root is a full-bleed layer: this anchor is a flex column (V-U9).</summary>
internal sealed class ClassicStageTransportPlace : Component
{
    public sealed record Props(Stage.StageCtx Stage, StageTransportProps Transport);

    static readonly LayoutTransition s_yield = new(TransitionChannels.Bounds,
        TransitionDynamics.Tween(ClassicStageGeometry.GalleryYieldMs, Easing.FluentDecelerate), SizeMode.Relayout);

    public override Element Render()
    {
        var p = UseProps<Props>();
        var sc = p.Stage;
        var L = sc.Layout.Value;
        bool gallery = sc.Mode.Value == Stage.Mode.Visualizer && sc.GalleryShown.Value;   // the root's own gate on the docked gallery
        // "chrome:transport" sits on THIS layer, whose first child is the card (the root's Show layer above the component's anchor is
        // "chrome:transport:motion"): FirstChild of the chrome key is the placed card, as for the top bar and the gallery
        return Stage.Layer with
        {
            Key = "chrome:transport",
            Children =
            [
                new BoxEl
                {
                    Key = "stage:transport:card", AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Stretch, Direction = 0,
                    Margin = new Edges4(L.TransportLeft, 0f, L.TransportRight(gallery), L.TransportInset),
                    Layout = s_yield,
                    Children = [Slots.View(ViewKeys.StageTransport, p.Transport)],
                },
            ],
        };
    }
}
