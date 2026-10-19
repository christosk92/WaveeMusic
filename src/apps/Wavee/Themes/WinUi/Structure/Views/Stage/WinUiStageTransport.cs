// ── Themes/WinUi/Structure/Views/Stage/WinUiStageTransport.cs — WinUI's `Stage.Transport` (P6 W4b) ─────────────────────────────────────
//
// W-Stage / W-Lyrics: the acrylic r8 card at the stage's foot (112 at 24 on Standard; 104 at 16 Small; 128 at 32 Huge), three columns: the
// LEAD column left, the centre column (shuffle · previous · the accent disc · next · repeat over the seek with its two times), the right cluster
// (heart · mute · the volume line · the labelled device button · "..."; Small: the volume, device and "..." glyphs). The card never hides: the
// boards keep it at rest. The lead column (the cover, the title, the artists, "Playing from", the chips; Small's heart and one word; Huge's
// heart, "Playing from" and the chips) needs the identity's facts as well as these, so the root's placement draws it over this card's left
// column (`WinUiStageTransportPlace`); this template leaves the column empty at the shared width (`Columns`).
//
// The Compact SHEET (W-Stage-Compact) stacks instead, flush in the sheet with no card: the seek with its times under it, the 96 row (the 48
// shuffle and repeat at the ends, previous and next, the 64 disc), the 40 foot (the device button, "from ...", "...").
//
// The small parts that read their own signal keep their own components (the mute glyph, the volume line with its mirror cell, the device button
// with its flyout), so a mute, a drag or a device change re-renders that part only (V-U50). The "..." menu is `Stage.NowPlayingMenu` and holds
// the chrome while open (V-U22).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;

using G = Wavee.Themes.WinUi.WinUiStageGeometry;
using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's stage transport card.</summary>
internal static class WinUiStageTransport
{
    static readonly FormatCache<int> s_percent = FormatCache.Create<int>();

    /// <summary>The card's three columns at stage <paramref name="L"/>: the centre column's width and each side's, and whether the volume line fits
    /// (the right cluster drops it to the glyph before the centre narrows below <see cref="WinUiStageGeometry.CenterMinW"/>).</summary>
    public static (float Center, float Side, bool Volume) Columns(in Stage.Layout L, WinUiStageLayout t, bool gallery)
    {
        float inner = MathF.Max(0f, L.W - L.TransportLeft - L.TransportRight(gallery) - t.TransportPadding.Left - t.TransportPadding.Right);
        bool volume = t.VolumeW > 0f;
        float center = CenterFor(inner, RightW(t, volume), t);
        if (volume && center < MathF.Min(t.CenterW, G.CenterMinW)) { volume = false; center = CenterFor(inner, RightW(t, false), t); }
        float side = MathF.Max(0f, (inner - center - 2f * t.ColumnGap) * 0.5f);
        return (center, side, volume);
    }

    static float CenterFor(float inner, float right, WinUiStageLayout t) => MathF.Max(0f, MathF.Min(t.CenterW, inner - 2f * (right + t.ColumnGap)));

    /// <summary>The right cluster's natural width: the heart (Standard: it sits here), mute, the volume line, the device button and "...".</summary>
    static float RightW(WinUiStageLayout t, bool volume)
        => t.Form switch
        {
            WinUiTransportForm.HeartLead => 3f * t.ButtonBox + 2f * G.BarButtonGap,
            _ => (t.Form == WinUiTransportForm.Identity ? t.ButtonBox + G.BarButtonGap : 0f) + 2f * t.ButtonBox
                 + (volume ? t.VolumeW + G.BarButtonGap : 0f) + G.DeviceMaxW * 0.5f + 3f * G.BarButtonGap,
        };

    /// <summary>The registered template of <see cref="ViewKeys.StageTransport"/>.</summary>
    public static Element Template(StageTransportProps props, in ViewContext ctx)
    {
        var st = props.Stage;
        var L = st.Layout.Value;
        var set = st.Accent.Value;
        var overlay = ctx.Hooks.UseContext(Overlay.Service);
        var f = props.Facts();
        var verbs = props.Verbs;
        var t = WinUiStageTiers.StageOf(ctx.Tier());
        var overControls = st.OverControls;
        var more = More(st, overlay, t.ButtonBox);

        if (t.Form == WinUiTransportForm.Stack) return Stack(props, f, set, more, t, L);

        bool gallery = st.Mode.Value == Stage.Mode.Visualizer && st.GalleryShown.Value;
        var (center, side, volume) = Columns(in L, t, gallery);
        var right = new List<Element>(6);
        if (t.Form == WinUiTransportForm.HeartLead)
        {
            right.Add(Embed.Comp(new MuteGlyph.Props(props, t), static () => new MuteGlyph()));
            right.Add(Embed.Comp(new DeviceButton.Props(props, t, Labelled: false), static () => new DeviceButton()));
        }
        else
        {
            if (t.Form == WinUiTransportForm.Identity) right.Add(Heart(f.Saved, f.Like, set.Fill, t));
            right.Add(Embed.Comp(new MuteGlyph.Props(props, t), static () => new MuteGlyph()));
            if (volume) right.Add(Embed.Comp(new VolumeRow.Props(set, t.VolumeW, props), static () => new VolumeRow()));
            right.Add(Embed.Comp(new DeviceButton.Props(props, t, Labelled: true), static () => new DeviceButton()));
        }
        right.Add(more);

        return new BoxEl
        {
            Key = "winui-stage-transport", Grow = 1f, Height = L.TransportH, Direction = 0, AlignItems = FlexAlign.Center, Gap = t.ColumnGap,
            Padding = t.TransportPadding, Corners = Radii.CardAll, Acrylic = Ink.Card, BorderWidth = 1f, BorderColor = Ink.Stroke, Shadow = Elevation.Card, ClipToBounds = true,
            OnHoverMove = _ => overControls(true), OnPointerExit = () => overControls(false),
            Children =
            [
                // the lead column: drawn by the placement over this slot (it needs the identity's facts)
                new BoxEl { Key = "transport:lead", Width = side, Shrink = 0f, HitTestVisible = false },
                new BoxEl
                {
                    Key = "transport:center", Width = center, Shrink = 0f, Direction = 1, AlignItems = FlexAlign.Center, Gap = t.SeekGap,
                    Children =
                    [
                        Cluster(f, set, verbs, t, t.ButtonBox),
                        new BoxEl
                        {
                            AlignSelf = FlexAlign.Stretch, Direction = 0, AlignItems = FlexAlign.Center, Gap = t.TimeGap,
                            Children =
                            [
                                new BoxEl { Width = t.TimeW, Shrink = 0f, Direction = 0, Justify = FlexJustify.End, Children = [props.TimeText(false, Ink.InkSecondary)] },
                                new BoxEl { Grow = 1f, Shrink = 1f, MinWidth = 0f, Children = [props.SeekBar(st.Scrubbing)] },
                                new BoxEl { Width = t.TimeW, Shrink = 0f, Direction = 0, Children = [props.TimeText(true, Ink.InkSecondary)] },
                            ],
                        },
                    ],
                },
                new BoxEl { Key = "transport:right", Width = side, Shrink = 0f, MinWidth = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, Gap = G.BarButtonGap, ClipToBounds = true, Children = right.ToArray() },
            ],
        };
    }

    /// <summary>The centred buttons: shuffle · previous · the accent disc · next · repeat.</summary>
    static Element Cluster(StageTransportFacts f, AccentSet set, IPlayerVerbs verbs, WinUiStageLayout t, float box) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = t.ClusterGap, Shrink = 0f,
        Children =
        [
            ToolTip.Wrap(Satellite(Icons.Shuffle, verbs.ToggleShuffle, f.CanTransport, f.Shuffle, set.Fill, box, t.Glyph), Loc.Get(Strings.Player.Shuffle)),
            ToolTip.Wrap(Glyph(Icons.Previous, verbs.Previous, box, t.Glyph, f.PrevEnabled), Loc.Get(Strings.Player.Previous)),
            Play(f.Playing, f.PrimaryEnabled, t.Disc, t.PlayGlyph, set, verbs),
            ToolTip.Wrap(Glyph(Icons.Next, verbs.Next, box, t.Glyph, f.NextEnabled), Loc.Get(Strings.Player.Next)),
            ToolTip.Wrap(Satellite(f.RepeatOne ? Icons.RepeatOne : Icons.RepeatAll, verbs.CycleRepeat, f.CanTransport, f.RepeatOn, set.Fill, box, t.Glyph), Loc.Get(Strings.Player.Repeat)),
        ],
    };

    /// <summary>The Compact sheet's stacked transport (W-Stage-Compact): no card, the seek and its times, the 96 row, the 40 foot.</summary>
    static Element Stack(StageTransportProps props, StageTransportFacts f, AccentSet set, Element more, WinUiStageLayout t, in Stage.Layout L)
    {
        var st = props.Stage;
        var verbs = props.Verbs;
        var overControls = st.OverControls;
        return new BoxEl
        {
            Key = "winui-stage-transport", Grow = 1f, Height = L.TransportH, Direction = 1, Justify = FlexJustify.End, Padding = t.TransportPadding,
            OnHoverMove = _ => overControls(true), OnPointerExit = () => overControls(false),
            Children =
            [
                new BoxEl { Direction = 1, Children = [props.SeekBar(st.Scrubbing)] },
                new BoxEl
                {
                    Direction = 0, Justify = FlexJustify.SpaceBetween, Height = G.StackTimeLine,
                    Children = [props.TimeText(false, Ink.InkSecondary), props.TimeText(true, Ink.InkSecondary)],
                },
                new BoxEl
                {
                    Height = t.StackRowH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween, Padding = new Edges4(G.StackGap, 0f, G.StackGap, 0f),
                    Children =
                    [
                        ToolTip.Wrap(Satellite(Icons.Shuffle, verbs.ToggleShuffle, f.CanTransport, f.Shuffle, set.Fill, t.ButtonBox, t.Glyph), Loc.Get(Strings.Player.Shuffle)),
                        ToolTip.Wrap(Glyph(Icons.Previous, verbs.Previous, t.ButtonBox, t.Glyph, f.PrevEnabled), Loc.Get(Strings.Player.Previous)),
                        Play(f.Playing, f.PrimaryEnabled, t.Disc, t.PlayGlyph, set, verbs),
                        ToolTip.Wrap(Glyph(Icons.Next, verbs.Next, t.ButtonBox, t.Glyph, f.NextEnabled), Loc.Get(Strings.Player.Next)),
                        ToolTip.Wrap(Satellite(f.RepeatOne ? Icons.RepeatOne : Icons.RepeatAll, verbs.CycleRepeat, f.CanTransport, f.RepeatOn, set.Fill, t.ButtonBox, t.Glyph), Loc.Get(Strings.Player.Repeat)),
                    ],
                },
                new BoxEl
                {
                    Height = t.StackFootH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Gap = G.StackGap,
                    Children =
                    [
                        Embed.Comp(new DeviceButton.Props(props, t, Labelled: true), static () => new DeviceButton()),
                        new BoxEl
                        {
                            Grow = 1f, MinWidth = 0f, Direction = 0, Justify = FlexJustify.End,
                            Children = [new TextEl(f.PlayingFrom) { Size = G.LeadMeta, LineHeight = G.LeadMetaLine, Color = Ink.InkSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f }],
                        },
                        more,
                    ],
                },
            ],
        };
    }

    /// <summary>"...": the now-playing menu (asked at open time); it holds the chrome while open (V-U22).</summary>
    static Element More(Stage.StageCtx st, IOverlayService overlay, float box)
    {
        var menuOpen = st.MenuOpen;
        return Glyph(Icons.More, null, box, G.BarGlyphSize, enabled: true, clickRequestsContext: true).WithContextMenu(overlay,
            () => { var m = Stage.NowPlayingMenu?.Invoke(); if (m is { } model && ContextMenu.HasAnyEnabled(model)) menuOpen(true); return m; },
            new ContextMenuOptions { OnClosed = _ => menuOpen(false) });
    }

    /// <summary>The accent disc (48 Standard and Small, 56 Huge, 64 in the sheet): a hand-built circle on the surface's AccentSet.</summary>
    internal static Element Play(bool playing, bool enabled, float box, float glyph, AccentSet set, IPlayerVerbs verbs) => new BoxEl
    {
        Key = "transport:play", Width = box, Height = box, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.Circle(box),
        Fill = enabled ? set.Fill : Ink.ScrimRest, HoverFill = enabled ? set.FillSecondary : Ink.ScrimRest, PressedFill = enabled ? set.FillTertiary : Ink.ScrimRest,
        BrushTransitionMs = Design.Motion.Faster,
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, IsEnabled = enabled,
        OnClick = () => verbs.TogglePlay("stage.button"), Cursor = enabled ? CursorId.Hand : (CursorId?)null,
        Children = [new TextEl(playing ? WinUiStageGeometry.PauseSolid : WinUiStageGeometry.PlaySolid) { Size = glyph, FontFamily = Tok.Definition.Fonts.Icon, Color = enabled ? set.Ink : Ink.InkTertiary }],
    };

    /// <summary>A plateless glyph button (the subtle plate on hover).</summary>
    internal static BoxEl Glyph(string glyph, Action? onClick, float box, float glyphSize, bool enabled = true, bool clickRequestsContext = false) => new()
    {
        Width = box, Height = box, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
        Fill = ColorF.Transparent, HoverFill = enabled ? Ink.GlassHover : ColorF.Transparent, PressedFill = enabled ? Ink.GlassPressed : ColorF.Transparent, BrushTransitionMs = Design.Motion.Faster,
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, IsEnabled = enabled, OnClick = enabled ? onClick : null, ClickRequestsContext = clickRequestsContext,
        Cursor = enabled ? CursorId.Hand : (CursorId?)null,
        Children = [new TextEl(glyph) { Size = glyphSize, FontFamily = Tok.Definition.Fonts.Icon, Color = enabled ? Ink.Ink : Ink.InkTertiary }],
    };

    /// <summary>Shuffle / repeat: the accent glyph while latched (WinUI's toggled AppBarButton keeps no plate).</summary>
    static BoxEl Satellite(string glyph, Action onClick, bool enabled, bool latched, ColorF accent, float box, float glyphSize) => new()
    {
        Width = box, Height = box, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
        Fill = ColorF.Transparent, HoverFill = enabled ? Ink.GlassHover : ColorF.Transparent, PressedFill = enabled ? Ink.GlassPressed : ColorF.Transparent, BrushTransitionMs = Design.Motion.Faster,
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, IsEnabled = enabled, OnClick = onClick, Cursor = enabled ? CursorId.Hand : (CursorId?)null,
        Children = [new TextEl(glyph) { Size = glyphSize, FontFamily = Tok.Definition.Fonts.Icon, Color = !enabled ? Ink.InkTertiary : latched ? accent : Ink.Ink }],
    };

    /// <summary>The heart: the accent glyph when saved.</summary>
    internal static BoxEl Heart(bool saved, Action? onLike, ColorF accent, WinUiStageLayout t) => new()
    {
        Key = "transport:heart", Width = t.ButtonBox, Height = t.ButtonBox, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
        Fill = ColorF.Transparent, HoverFill = onLike is null ? ColorF.Transparent : Ink.GlassHover, PressedFill = onLike is null ? ColorF.Transparent : Ink.GlassPressed, BrushTransitionMs = Design.Motion.Faster,
        Role = AutomationRole.Button, Focusable = onLike is not null, AllowFocusOnInteraction = false, OnClick = onLike, Cursor = onLike is null ? (CursorId?)null : CursorId.Hand, BlocksDragArm = true,
        Children = [new BoxEl { Key = saved ? "sh:on" : "sh:off", Children = [new TextEl(saved ? Icons.HeartFill : Icons.Heart) { Size = t.Glyph, FontFamily = Tok.Definition.Fonts.Icon, Color = saved ? accent : Ink.Ink }] }],
    };

    sealed class MuteGlyph : Component
    {
        public sealed record Props(StageTransportProps T, WinUiStageLayout Tier);

        public override Element Render()
        {
            var p = UseProps<Props>();
            bool muted = p.T.Muted();
            return ToolTip.Wrap(Glyph(muted ? Icons.Mute : Icons.Volume, p.T.ToggleMute, p.Tier.ButtonBox, p.Tier.Glyph), Loc.Get(muted ? Strings.Player.Unmute : Strings.Player.Mute));
        }
    }

    /// <summary>The stock Slider in the surface accent at the tier's length (re-pushed props: a new accent re-skins in place).</summary>
    sealed class VolumeRow : Component
    {
        public sealed record Props(AccentSet Set, float Length, StageTransportProps T);
        static readonly Slider.SliderOptions Options = new()
        {
            ThumbToolTipValueConverter = static v => s_percent.Get(Math.Clamp((int)MathF.Round(v * G.PercentScale), 0, G.PercentScaleMax), static p => p + "%"),
        };
        readonly FloatSignal _level = new(0f);

        public override Element Render()
        {
            var p = UseProps<Props>();
            var t = p.T;
            UseSignalEffect(() => _level.SetIfChanged(t.Volume()));
            var style = Slider.DefaultStyle with
            {
                RailFill = Ink.Ink with { A = G.RailAlpha }, RailFillDisabled = Ink.Ink with { A = G.RailDisabledAlpha },
                ValueFill = p.Set.Fill, ValueFillPointerOver = p.Set.FillSecondary, ValueFillPressed = p.Set.FillTertiary, ValueFillDisabled = p.Set.Fill with { A = G.ThumbDisabledAlpha },
                ThumbRing = Tok.FillControlSolid, ThumbFill = p.Set.Fill, ThumbFillPointerOver = p.Set.FillSecondary, ThumbFillPressed = p.Set.FillTertiary, ThumbFillDisabled = p.Set.Fill with { A = G.ThumbDisabledAlpha },
                ThumbBorder = GradientSpec.Solid(Ink.Stroke),
            };
            return Slider.Create(_level, t.SetVolume, Options, length: p.Length, thickness: G.VolumeThickness, style: style);
        }
    }

    /// <summary>"This PC ▾" (labelled) or the device glyph (Small): the two-section device picker over the same items the player bar builds.</summary>
    sealed class DeviceButton : Component
    {
        public sealed record Props(StageTransportProps T, WinUiStageLayout Tier, bool Labelled);

        public override Element Render()
        {
            var p = UseProps<Props>();
            var t = p.T;
            var tier = p.Tier;
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
                st.MenuOpen(true);
                opened.ClosedAction = () => { handle.Value = null; st.MenuOpen(false); };
                handle.Value = opened;
            }
            if (!p.Labelled)
                return ToolTip.Wrap(new BoxEl
                {
                    Width = tier.ButtonBox, Height = tier.ButtonBox, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
                    Fill = ColorF.Transparent, HoverFill = Ink.GlassHover, PressedFill = Ink.GlassPressed, BrushTransitionMs = Design.Motion.Faster,
                    Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = Toggle, OnRealized = h => anchor.Value = h,
                    Children = [new TextEl(device.Glyph) { Size = tier.Glyph, FontFamily = Tok.Definition.Fonts.Icon, Color = Ink.Ink }],
                }, device.Name);
            return new BoxEl
            {
                Height = tier.DeviceH, Direction = 0, AlignItems = FlexAlign.Center, Gap = G.DeviceGap, Padding = new Edges4(tier.DevicePadX, 0f, tier.DevicePadX, 0f), Corners = Radii.ControlAll,
                Fill = tier.Sheet ? Ink.GlassRest : ColorF.Transparent, HoverFill = Ink.GlassHover, PressedFill = Ink.GlassPressed, BrushTransitionMs = Design.Motion.Faster,
                Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = Toggle, OnRealized = h => anchor.Value = h,
                MaxWidth = G.DeviceMaxW, Shrink = 1f, MinWidth = Stage.Layout.DeviceMinW,
                Children =
                [
                    new TextEl(device.Glyph) { Size = G.DeviceGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = Ink.Ink },
                    new TextEl(device.Name) { Size = G.DeviceName, LineHeight = G.DeviceNameLine, Color = Ink.Ink, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
                    new TextEl(Icons.ChevronDown) { Size = G.DeviceChevron, FontFamily = Tok.Definition.Fonts.Icon, Color = Ink.InkSecondary },
                ],
            };
        }
    }
}
