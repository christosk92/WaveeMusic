// ── Themes/Zune/Structure/Views/Stage/ZuneStageTransport.cs — Zune's `Stage.Transport` (P5 Z4b) ────────────────────────────────────
//
// ONE view on a full-bleed chrome layer that places its own parts for the arrangement the stage is in (`ZuneStageGeometry.Arrange`):
//
//   PLATE    (Z-NowPlaying, Z-Lyrics, Z-Queue-Page) the flat plate across the foot: the stage veil (0.85 over a face, opaque under a pane),
//            the window's gutter each side; the 36 seek row (times · the 2-px seek line · times), then the row: "playing from …" left,
//            shuffle · previous · the accent DISC · next · repeat centred, heart · mute · the volume line · the device word · "…" right.
//            A plate too narrow for the three clusters stacks to heart · previous · disc · next · "…".
//   CARD     (Z-NowPlaying-Card) inside the card: the seek row and the 56 row (shuffle · previous · disc · next · repeat, then heart · "…" at
//            the right), "playing from …" at the foot of the title column; mute · volume · device at the bottom-right gutter.
//   COLUMN   (Z-NowPlaying-LargeArt) in the column: "playing from …" at its top; at its foot 40 down the seek row, the row (… heart), 16 down
//            the volume row (mute · volume · device · "…").
//
// Each arrangement is a keyed child of the view's layer, so a layout switch cross-fades the plate into the card's controls while the cover
// springs between them: the transport's slot (and the stage) never remount. What is playback state arrives as `StageTransportFacts`, read
// in this render; the mute glyph, the volume line and the device word keep their own components (a mute, a drag or a device change
// re-renders that part only). The "…" menu is `Stage.NowPlayingMenu` and holds the chrome while it is open.
//
// The seek line itself is the player family's `Player.SeekRail` (the bar's own seek, mounted by `props.SeekBar`): this view gives it its
// 36 row. Casing: "playing from …" is the label case; the source's and the device's names are content and keep their own.

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

using G = Wavee.Themes.Zune.ZuneStageGeometry;
using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee.Themes.Zune;

/// <summary>Zune's stage transport.</summary>
internal static class ZuneStageTransport
{
    /// <summary>The plate yields its width to a gallery that runs to the foot on the face's re-centre tween.</summary>
    static readonly LayoutTransition s_yield = new(TransitionChannels.Bounds, TransitionDynamics.Tween(G.GalleryYieldMs, Easing.FluentDecelerate), SizeMode.Relayout);

    /// <summary>The registered template of <see cref="ViewKeys.StageTransport"/>.</summary>
    public static Element Template(StageTransportProps props, in ViewContext ctx)
    {
        var st = props.Stage;
        var L = st.Layout.Value;
        var k = st.Look.Value;
        var t = ZuneStageTiers.StageOf(ctx.Tier());
        bool gallery = k.Mode == Stage.Mode.Visualizer && st.GalleryShown.Value;
        var a = G.Arrange(in L, in k, t, gallery);
        var kit = new Kit(props, props.Facts(), st.Accent.Value, t, ctx.Hooks.UseContext(Overlay.Service));
        var fade = ZuneMotion.SwapFade;
        Element body = a.Form switch
        {
            ZuneStageForm.Card => Card(kit, in a) with { Key = "stage:transport:card", Enter = fade, Exit = fade, Transition = MotionTok.ControlNormal },
            ZuneStageForm.Column => Column(kit, in a) with { Key = "stage:transport:column", Enter = fade, Exit = fade, Transition = MotionTok.ControlNormal },
            _ => Plate(kit, in L, k.Mode, gallery) with { Key = "stage:transport:plate", Enter = fade, Exit = fade, Transition = MotionTok.ControlNormal },
        };
        return Stage.Layer with { Children = [body] };
    }

    // ── the arrangements ─────────────────────────────────────────────────────────────────────────────────────────────────

    static BoxEl Plate(Kit kit, in Stage.Layout L, Stage.Mode mode, bool gallery)
    {
        var t = kit.Tier;
        float rowH = MathF.Max(0f, L.TransportH - G.SeekRowH);
        float disc = G.DiscFor(t, rowH);
        var (stacked, volume) = G.PlateFit(in L, t, disc);
        Element row;
        if (stacked)
        {
            row = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween, Height = rowH,
                Children = [kit.Heart(), kit.Previous(), kit.Play(disc), kit.Next(), kit.More()],
            };
        }
        else
        {
            var right = new List<Element>(5) { kit.Heart(), kit.Mute() };
            if (volume) right.Add(kit.Volume());
            right.Add(kit.Device());
            right.Add(kit.More());
            row = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Height = rowH, Gap = t.ClusterGap,
                Children =
                [
                    new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, Direction = 0, AlignItems = FlexAlign.Center, ClipToBounds = true, Children = [kit.Source()] },
                    kit.Centre(disc),
                    new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, Gap = G.RightGap, Children = right.ToArray() },
                ],
            };
        }
        return new BoxEl
        {
            AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Stretch, Height = L.TransportH, Direction = 1,
            Margin = new Edges4(L.TransportLeft, 0f, L.TransportRight(gallery), L.TransportInset), Layout = s_yield,
            Padding = new Edges4(t.Gutter, 0f, t.Gutter, 0f), Fill = Ink.Veil with { A = G.PlateAlpha(mode) },
            OnHoverMove = _ => kit.Over(true), OnPointerExit = () => kit.Over(false),
            Children = [kit.SeekRow(), row],
        };
    }

    static BoxEl Card(Kit kit, in ZuneStageArrangement a)
    {
        float disc = G.DiscFor(kit.Tier, G.CardRowH);
        var controls = new BoxEl
        {
            Direction = 1,
            Children =
            [
                kit.SeekRow(),
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Height = G.CardRowH, Gap = G.CardRowGap,
                    Children = [kit.Shuffle(), kit.Previous(), kit.Play(disc), kit.Next(), kit.Repeat(), new BoxEl { Grow = 1f, MinWidth = 0f }, kit.Heart(), kit.More()],
                },
            ],
        };
        var aside = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, Height = G.AsideH, Gap = G.RightGap,
            Children = [kit.Mute(), kit.Volume(), kit.Device()],
        };
        return Stage.Layer with
        {
            Children =
            [
                Placed(kit, a.Source, kit.Source(), "stage:transport:source"),
                Placed(kit, a.Controls, controls, "stage:transport:controls"),
                Placed(kit, a.Aside, aside, "stage:transport:aside"),
            ],
        };
    }

    static BoxEl Column(Kit kit, in ZuneStageArrangement a)
    {
        float disc = G.DiscFor(kit.Tier, G.CardRowH);
        var controls = new BoxEl
        {
            Direction = 1, Padding = new Edges4(0f, G.ColumnSeekTop, 0f, 0f),
            Children =
            [
                kit.SeekRow(),
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Height = G.CardRowH, Gap = kit.Tier.ClusterGap,
                    Children = [kit.Shuffle(), kit.Previous(), kit.Play(disc), kit.Next(), kit.Repeat(), new BoxEl { Grow = 1f, MinWidth = 0f }, kit.Heart()],
                },
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Height = G.AsideH, Gap = G.RightGap, Margin = new Edges4(0f, G.ColumnVolumeTop, 0f, 0f),
                    Children = [kit.Mute(), kit.Volume(), new BoxEl { Grow = 1f, MinWidth = 0f }, kit.Device(), kit.More()],
                },
            ],
        };
        return Stage.Layer with
        {
            Children =
            [
                Placed(kit, a.Source, kit.Source(), "stage:transport:source"),
                Placed(kit, a.Controls, controls, "stage:transport:controls"),
            ],
        };
    }

    /// <summary>A part placed at an arrangement's rect (top-left in the layer, its rect's size); it holds the chrome while the pointer is on it.</summary>
    static BoxEl Placed(Kit kit, in ZuneRect r, Element child, string key) => new()
    {
        Key = key, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Width = r.W, Height = r.H, Margin = new Edges4(r.X, r.Y, 0f, 0f),
        Direction = 1, MinWidth = 0f, ClipToBounds = true,
        OnHoverMove = _ => kit.Over(true), OnPointerExit = () => kit.Over(false),
        Children = [child],
    };

    // ── the parts ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The pieces every arrangement draws, over one render's facts.</summary>
    sealed class Kit(StageTransportProps props, StageTransportFacts facts, AccentSet set, ZuneStageLayout tier, IOverlayService overlay)
    {
        public ZuneStageLayout Tier => tier;

        public void Over(bool over) => props.Stage.OverControls(over);

        /// <summary>The 36 seek row: elapsed · the bar's own seek (the 2-px line) · remaining. Its scrub edge holds the chrome.</summary>
        public Element SeekRow() => new BoxEl
        {
            Key = "stage:transport:seek", Height = G.SeekRowH, Direction = 0, AlignItems = FlexAlign.Center, Gap = G.SeekGap,
            Children =
            [
                props.TimeText(false, Ink.InkSecondary),
                new BoxEl { Grow = 1f, Shrink = 1f, MinWidth = 0f, Children = [props.SeekBar(props.Stage.Scrubbing)] },
                props.TimeText(true, Ink.InkSecondary),
            ],
        };

        /// <summary>"playing from playlist · late night drive": the label in the tertiary ink (label case), the source's name in the ink.
        /// Nothing without a source.</summary>
        public Element Source()
        {
            if (facts.PlayingFrom.Length == 0) return new BoxEl { HitTestVisible = false };
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, Shrink = 1f, HitTestVisible = false,
                Children =
                [
                    new TextEl(facts.PlayingFrom) { Size = G.SourceSize, LineHeight = G.SourceLine, Color = Ink.InkTertiary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Shrink = 0f },
                    new TextEl(facts.Source.Length > 0 ? " · " : "") { Size = G.SourceSize, LineHeight = G.SourceLine, Color = Ink.InkTertiary, Shrink = 0f },
                    new TextEl(facts.Source) { Size = G.SourceSize, LineHeight = G.SourceLine, Color = Ink.Ink, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
                ],
            };
        }

        /// <summary>shuffle · previous · the disc · next · repeat, the tier's gap apart.</summary>
        public Element Centre(float disc) => new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = tier.ClusterGap, Shrink = 0f,
            Children = [Shuffle(), Previous(), Play(disc), Next(), Repeat()],
        };

        public Element Shuffle() => ToolTip.Wrap(Latch(Icons.Shuffle, props.Verbs.ToggleShuffle, facts.CanTransport, facts.Shuffle), Loc.Get(Strings.Player.Shuffle));

        public Element Repeat() => ToolTip.Wrap(Latch(facts.RepeatOne ? Icons.RepeatOne : Icons.RepeatAll, props.Verbs.CycleRepeat, facts.CanTransport, facts.RepeatOn), Loc.Get(Strings.Player.Repeat));

        public Element Previous() => ToolTip.Wrap(Glyph(Icons.Previous, props.Verbs.Previous, facts.PrevEnabled, Ink.Ink, tier.GlyphBox), Loc.Get(Strings.Player.Previous));

        public Element Next() => ToolTip.Wrap(Glyph(Icons.Next, props.Verbs.Next, facts.NextEnabled, Ink.Ink, tier.GlyphBox), Loc.Get(Strings.Player.Next));

        /// <summary>The accent disc (Z-NowPlaying: 56, the glyph in the accent's contrast ink), round: the one round control of the plate.</summary>
        public Element Play(float box)
        {
            bool enabled = facts.PrimaryEnabled;
            var verbs = props.Verbs;
            return new BoxEl
            {
                Key = "stage:transport:disc", Width = box, Height = box, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.Circle(box),
                Fill = enabled ? set.Fill : Ink.ScrimRest, HoverFill = enabled ? set.FillSecondary : Ink.ScrimRest, PressedFill = enabled ? set.FillTertiary : Ink.ScrimRest,
                BrushTransitionMs = Design.Motion.Faster,
                HoverScale = R.Motion.ScaleEmphatic.HoverIf(enabled), PressScale = R.Motion.ScaleEmphatic.PressIf(enabled),
                Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, IsEnabled = enabled,
                OnClick = () => verbs.TogglePlay("stage.button"), Cursor = enabled ? CursorId.Hand : (CursorId?)null,
                Children = [new TextEl(facts.Playing ? Icons.Pause : Icons.Play) { Size = G.PlayGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = enabled ? set.Ink : Ink.InkTertiary }],
            };
        }

        public Element Heart()
        {
            var like = facts.Like;
            bool saved = facts.Saved;
            return new BoxEl
            {
                Width = tier.GlyphBox, Height = tier.GlyphBox, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Role = AutomationRole.Button, Focusable = like is not null, AllowFocusOnInteraction = false, OnClick = like, Cursor = like is null ? (CursorId?)null : CursorId.Hand, BlocksDragArm = true,
                Children = [new BoxEl { Key = saved ? "sh:on" : "sh:off", Children = [new TextEl(saved ? Icons.HeartFill : Icons.Heart) { Size = G.TransportGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = saved ? set.Text : Ink.Ink, HoverColor = saved ? set.Text : Ink.Ink }] }],
            };
        }

        public Element More()
        {
            var menuOpen = props.Stage.MenuOpen;
            return new BoxEl
            {
                Width = tier.GlyphBox, Height = tier.GlyphBox, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, AllowFocusOnInteraction = false, ClickRequestsContext = true,
                Children = [new TextEl(Icons.More) { Size = G.TransportGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = Ink.InkSecondary, HoverColor = Ink.Ink }],
            }.WithContextMenu(overlay,
                () => { var m = Stage.NowPlayingMenu?.Invoke(); if (m is { } model && ContextMenu.HasAnyEnabled(model)) menuOpen(true); return m; },   // the "…" menu holds the chrome; an empty one never opens
                new ContextMenuOptions { OnClosed = _ => menuOpen(false) });
        }

        public Element Mute() => Embed.Comp(new ZuneStageMute.Props(props, tier.GlyphBox), static () => new ZuneStageMute());

        public Element Volume() => Embed.Comp(new ZuneStageVolume.Props(tier.VolumeW, props), static () => new ZuneStageVolume());

        public Element Device() => Embed.Comp(new ZuneStageDevice.Props(props), static () => new ZuneStageDevice());

        /// <summary>A plateless glyph: the ink lifts on hover, the tertiary ink when disabled.</summary>
        public static BoxEl Glyph(string glyph, Action? onClick, bool enabled, ColorF ink, float box) => new()
        {
            Width = box, Height = box, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            PressScale = R.Motion.ScaleSubtle.PressIf(enabled),
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, IsEnabled = enabled, OnClick = enabled ? onClick : null,
            Cursor = enabled ? CursorId.Hand : (CursorId?)null,
            Children = [new TextEl(glyph) { Size = G.TransportGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = enabled ? ink : Ink.InkTertiary, HoverColor = enabled ? Ink.Ink : Ink.InkTertiary }],
        };

        /// <summary>Shuffle / repeat: the accent INK while latched (Z-NowPlaying draws the latched shuffle in the accent), never a plate.</summary>
        BoxEl Latch(string glyph, Action onClick, bool enabled, bool latched)
            => Glyph(glyph, onClick, enabled, latched ? set.Text : Ink.InkSecondary, tier.GlyphBox);
    }
}

/// <summary>The mute glyph: its own component, so a mute re-renders it alone.</summary>
internal sealed class ZuneStageMute : Component
{
    public sealed record Props(StageTransportProps T, float Box);

    public override Element Render()
    {
        var p = UseProps<Props>();
        bool muted = p.T.Muted();
        var glyph = new BoxEl
        {
            Width = p.Box, Height = p.Box, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, OnClick = p.T.ToggleMute, Cursor = CursorId.Hand,
            Children = [new TextEl(muted ? Icons.Mute : Icons.Volume) { Size = ZuneStageGeometry.TransportGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = Ink.Ink, HoverColor = Ink.Ink }],
        };
        return ToolTip.Wrap(glyph, Loc.Get(muted ? Strings.Player.Unmute : Strings.Player.Mute));
    }
}

/// <summary>The volume LINE (Z-NowPlaying: a 2-px track, the ink as its value, a small square thumb) at the tier's length. Its own
/// component, with a mirror cell, so a drag re-renders it alone.</summary>
internal sealed class ZuneStageVolume : Component
{
    public sealed record Props(float Length, StageTransportProps T);

    static readonly FormatCache<int> s_percent = FormatCache.Create<int>();

    static readonly Slider.SliderOptions s_options = new()
    {
        ThumbToolTipValueConverter = static v => s_percent.Get(Math.Clamp((int)MathF.Round(v * ZuneStageGeometry.PercentScale), 0, ZuneStageGeometry.PercentScaleMax), static p => p + "%"),
    };

    readonly FloatSignal _level = new(0f);

    public override Element Render()
    {
        var p = UseProps<Props>();
        var t = p.T;
        UseSignalEffect(() => _level.SetIfChanged(t.Volume()));
        var style = Slider.DefaultStyle with
        {
            TrackHeight = ZuneStageGeometry.VolumeTrack, TrackCornerRadius = 0f,
            RailFill = Ink.Ink with { A = ZuneStageGeometry.RailA }, RailFillDisabled = Ink.Ink with { A = ZuneStageGeometry.RailDisabledA },
            ValueFill = Ink.Ink, ValueFillPointerOver = Ink.Ink, ValueFillPressed = Ink.InkSecondary, ValueFillDisabled = Ink.Ink with { A = ZuneStageGeometry.ThumbDisabledA },
            ThumbRingDiameter = ZuneStageGeometry.VolumeThumb, InnerThumbDiameter = ZuneStageGeometry.VolumeThumbInner, ThumbCornerRadius = 0f,
            ThumbRing = Ink.Ink, ThumbFill = Ink.Ink, ThumbFillPointerOver = Ink.Ink, ThumbFillPressed = Ink.InkSecondary, ThumbFillDisabled = Ink.Ink with { A = ZuneStageGeometry.ThumbDisabledA },
            ThumbBorder = null, ThumbBorderWidth = 0f,
        };
        return Slider.Create(_level, t.SetVolume, s_options, length: p.Length, thickness: ZuneStageGeometry.VolumeThickness, style: style);
    }
}

/// <summary>The device word ("this computer ▾"): the device's name (content: its own casing) and a chevron, no plate; it opens the
/// two-section device picker over the same items the player bar builds, and holds the chrome while it is open.</summary>
internal sealed class ZuneStageDevice : Component
{
    public sealed record Props(StageTransportProps T);

    public override Element Render()
    {
        var t = UseProps<Props>().T;
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
        return new BoxEl
        {
            Height = ZuneStageGeometry.DeviceH, Direction = 0, AlignItems = FlexAlign.Center, Gap = ZuneStageGeometry.RightGap,
            Padding = new Edges4(ZuneStageGeometry.DevicePadX, 0f, ZuneStageGeometry.DevicePadX, 0f),
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = Toggle, OnRealized = h => anchor.Value = h,
            MaxWidth = ZuneStageGeometry.DeviceMaxW, Shrink = 1f, MinWidth = ZuneStageGeometry.DeviceMinW,
            Children =
            [
                new TextEl(device.Name) { Size = ZuneStageGeometry.DeviceNameSize, LineHeight = ZuneStageGeometry.DeviceNameLine, Color = Ink.InkSecondary, HoverColor = Ink.Ink, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
                new TextEl(Icons.ChevronDown) { Size = ZuneStageGeometry.ChevronGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = Ink.InkSecondary },
            ],
        };
    }
}
