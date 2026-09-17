// ── Shell/Stage.UI.cs ──────────────────────────────────────────────────────────────────────────────────────────────
// StageChrome, StageIdentity, all of StagePanes — the pane switcher, the pane box, the stage queue pane's skin and
// the lyrics pane's FRAME. Owns the ~500 lines chapters 21 and 22 both counted (their bodies stay in Lyrics.UI.cs)
//
// Role: UI
// Owner: K
// Wave: 4
// Budget: 1150 lines
// Spec: ch 21 §9.5
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// THE IMMERSIVE STAGE (ch 21 W11-W16, §4.4-§4.6, §6.6). `Stage.View()` is the full-bleed surface the shell mounts while
// `Shell.Ui.ImmersiveLyrics` is up: a live caption strip, the body band, a live player-bar strip. The body band is
// floor → σ80 baked-blur cover (1.30× paint overscale, drifting) → Scrim → ColumnShade → a childless hit SHIELD →
// content [ TopBar, StageBody [ identity column | panes ] ].
//
//   • ONE tree, ONE reflow flag: `Stage.Layout.Wide`, resolved in an effect off the viewport; the identity and the panes
//     keep their keys across the flip so neither the lyrics document nor the queue lane is ever remounted by a resize.
//   • Every colour comes from `Design.StageInk` at the point of consumption; nothing here names a theme.
//   • No stage container with several interactive descendants owns a pointer handler (the context SHIELD rule).
//   • Both panes stay mounted; the switch is a 250 ms opacity cross-fade with HitTestVisible following the active one.
//
// Mounted from other files: `Lyrics.StagePane()` (K3) is the reading column's BODY; the queue pane's rows are owner Q's
// (Wave 5) through `Stage.QueuePaneBody`; the seek and the two times are the player bar's own (`Shell.SeekBar()`,
// `Shell.TimeText`, owner I), never a fork. The now-playing context menu is installed through `Stage.NowPlayingMenu`.

using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

using Ink = Wavee.Design.StageInk;
using RepeatMode = Wavee.Spotify.Decode.RepeatMode;

namespace Wavee;

public static partial class Stage
{
    // MOUNT POINT (stage B contract)
    /// <summary>The immersive stage surface. Mount it full-bleed while <c>Shell.Ui.ImmersiveLyrics</c> is true, with
    /// <see cref="EnterTerminal"/> / <see cref="ExitTerminal"/> on the mounting branch.</summary>
    public static Element View() => Embed.Comp(static () => new SurfaceCore());

    /// <summary>The mount/unmount terminals: a fade always; the slight scale only when reduced motion is off (a VALUE read
    /// at the point of consumption, never a hook branch).</summary>
    public static EnterExit EnterTerminal => Design.Reduced
        ? new EnterExit(Opacity: 0f, Active: true) : new EnterExit(Sx: 1.03f, Sy: 1.03f, Opacity: 0f, Active: true);

    /// <inheritdoc cref="EnterTerminal"/>
    public static EnterExit ExitTerminal => Design.Reduced
        ? new EnterExit(Opacity: 0f, Active: true) : new EnterExit(Sx: 1.02f, Sy: 1.02f, Opacity: 0f, Active: true);

    /// <summary>The queue pane's rows (owner Q's stage queue pane, Wave 5). Null renders the empty skin.</summary>
    public static Func<Element>? QueuePaneBody { get; set; }

    /// <summary>The now-playing context menu (the action platform's). Right-click / Menu key anywhere on the identity
    /// region and the wide "…" raise it; the factory is asked at OPEN time.</summary>
    public static Func<ContextMenuModel?>? NowPlayingMenu { get; set; }

    // ══ 1. THE CHROME (StageChrome) ═══════════════════════════════════════════════════════════════════════════════════

    static ColorF Shade(float a) => Ink.Veil with { A = a };

    /// <summary>THE scrim: one full-bleed continuous vertical gradient — deep top, flat plateau, deep bottom.</summary>
    static GradientSpec Scrim() => GradientDown(
        new GradientStop(0f, Shade(Layout.ScrimTopA)),
        new GradientStop(Layout.ScrimTopStop, Shade(Layout.ScrimBaseA)),
        new GradientStop(Layout.ScrimBottomStop, Shade(Layout.ScrimBaseA)),
        new GradientStop(1f, Shade(Layout.ScrimBottomA)));

    /// <summary>The identity column's deepening: held across the designed column, curved to EXACTLY zero over 260 DIP.</summary>
    static GradientSpec ColumnShade() => GradientRight(
        new GradientStop(0f, Shade(Layout.ColumnShadeA)),
        new GradientStop(MathF.Max(0.01f, Layout.ColumnShadeHoldStop), Shade(Layout.ColumnShadeA)),
        new GradientStop(Layout.ColumnShadeMidStop, Shade(Layout.ColumnShadeA * Layout.ColumnShadeMidFrac)),
        new GradientStop(1f, Shade(0f)));

    /// <summary>The QUEUE pane's floor: up out of zero on its left, deepest at the window edge.</summary>
    static GradientSpec PaneShade() => GradientRight(
        new GradientStop(0f, Shade(0f)),
        new GradientStop(Layout.PaneShadeFeatherStop, Shade(Layout.PaneShadeA * 0.4f)),
        new GradientStop(1f, Shade(Layout.PaneShadeA)));

    /// <summary>A PLATELESS glyph button — the stage's default control (glass on hover, emphatic scale, no cursor when dead).</summary>
    static BoxEl Glyph(string glyph, Action? onClick, float box, float glyphSize, bool enabled = true, Action<NodeHandle>? onRealized = null) => new()
    {
        Width = box, Height = box, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = Radii.ControlAll, Fill = Ink.GlassRest,
        HoverFill = enabled ? Ink.GlassHover : Ink.GlassRest, PressedFill = enabled ? Ink.GlassPressed : Ink.GlassRest,
        BrushTransitionMs = Design.Motion.Faster,
        HoverScale = Design.Motion.ScaleEmphatic.HoverIf(enabled), PressScale = Design.Motion.ScaleEmphatic.PressIf(enabled),
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
        IsEnabled = enabled, OnClick = enabled ? onClick : null, Cursor = enabled ? CursorId.Hand : (CursorId?)null,
        OnRealized = onRealized,
        Children =
        [
            new TextEl(glyph)
            {
                Size = glyphSize, FontFamily = Theme.IconFont,
                Color = enabled ? Ink.InkSecondary : Ink.InkTertiary, HoverColor = enabled ? Ink.Ink : Ink.InkTertiary,
            },
        ],
    };

    /// <summary>The 40-DIP on-media SCRIM FAB (the secondary-line toggle): the scrim plate at rest + the hairline ring.</summary>
    static BoxEl ScrimFab(string glyph, Action onClick, float glyphSize, ColorF accent, bool latched) => new()
    {
        Width = 40f, Height = 40f, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = Radii.Circle(40f), Fill = Ink.ScrimRest, HoverFill = Ink.ScrimHover, PressedFill = Ink.ScrimPressed,
        BorderWidth = 1f, BorderColor = Ink.Stroke, BrushTransitionMs = Design.Motion.Faster,
        HoverScale = Design.Motion.ScaleEmphatic.Hover, PressScale = Design.Motion.ScaleEmphatic.Press,
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, OnClick = onClick, Cursor = CursorId.Hand,
        Children = [new TextEl(glyph) { Size = glyphSize, FontFamily = Theme.IconFont, Color = latched ? accent : Ink.InkSecondary, HoverColor = latched ? accent : Ink.Ink }],
    };

    /// <summary>THE WAY OUT: a 44-DIP disc made of INK with a card shadow — deliberately one rung above the 40-DIP FAB
    /// beside it. The shadow is load-bearing: the one separation channel that works in both polarities.</summary>
    internal static BoxEl ExitFab(string glyph, Action onClick) => new()
    {
        Width = 44f, Height = 44f, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = Radii.Circle(44f), Fill = Ink.GlassPlate, HoverFill = Ink.GlassPlateHover, PressedFill = Ink.GlassPlatePressed,
        BorderWidth = 1f, BorderColor = Ink.Stroke, Shadow = Elevation.Card, BrushTransitionMs = Design.Motion.Faster,
        HoverScale = Design.Motion.ScaleEmphatic.Hover, PressScale = Design.Motion.ScaleEmphatic.Press,
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, OnClick = onClick, Cursor = CursorId.Hand,
        Children = [new TextEl(glyph) { Size = 18f, FontFamily = Theme.IconFont, Color = Ink.Ink, HoverColor = Ink.Ink }],
    };

    /// <summary>A transport SATELLITE (shuffle / repeat): a subtle plate + accent glyph while latched, unpainted otherwise.</summary>
    static BoxEl Satellite(string glyph, Action onClick, bool enabled, bool latched, ColorF accent, float box) => new()
    {
        Width = box, Height = box, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = Radii.ControlAll,
        Fill = latched && enabled ? Ink.GlassHover : Ink.GlassRest,
        HoverFill = latched && enabled ? Ink.GlassPressed : enabled ? Ink.GlassHover : Ink.GlassRest,
        PressedFill = enabled ? Ink.GlassPressed : Ink.GlassRest, BrushTransitionMs = Design.Motion.Faster,
        HoverScale = Design.Motion.ScaleEmphatic.HoverIf(enabled), PressScale = Design.Motion.ScaleEmphatic.PressIf(enabled),
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
        IsEnabled = enabled, OnClick = onClick, Cursor = enabled ? CursorId.Hand : (CursorId?)null,
        Children =
        [
            new TextEl(glyph)
            {
                Size = 14f, FontFamily = Theme.IconFont,
                Color = !enabled ? Ink.InkTertiary : latched ? accent : Ink.InkSecondary,
                HoverColor = !enabled ? Ink.InkTertiary : latched ? accent : Ink.Ink,
            },
        ],
    };

    /// <summary>THE filled control (play/pause). Disabled, the plate falls back to the SCRIM rest and the glyph to tertiary
    /// ink — it does not simply grey.</summary>
    static BoxEl Play(bool playing, bool enabled, float box, float glyphSize) => new()
    {
        Width = box, Height = box, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = Radii.Circle(box),
        Fill = enabled ? Ink.ButtonFill : Ink.ScrimRest,
        HoverFill = enabled ? Ink.ButtonFillHover : Ink.ScrimRest, PressedFill = enabled ? Ink.ButtonFillPressed : Ink.ScrimRest,
        BrushTransitionMs = Design.Motion.Faster,
        HoverScale = Design.Motion.ScaleEmphatic.HoverIf(enabled), PressScale = Design.Motion.ScaleEmphatic.PressIf(enabled),
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
        IsEnabled = enabled, OnClick = Shell.TogglePlayPause, Cursor = enabled ? CursorId.Hand : (CursorId?)null,
        Children = [new TextEl(playing ? Icons.Pause : Icons.Play) { Size = glyphSize, FontFamily = Theme.IconFont, Color = enabled ? Ink.ButtonInk : Ink.InkTertiary }],
    };

    /// <summary>The saved HEART: accent when saved, secondary ink otherwise; keyed "sh:on"/"sh:off" so the glyph remounts.</summary>
    static BoxEl Heart(bool saved, Action? onLike, ColorF accent) => new()
    {
        Width = Controls.IconButtonSize, Height = Controls.IconButtonSize, Shrink = 0f,
        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
        Fill = Ink.GlassRest, HoverFill = onLike is null ? Ink.GlassRest : Ink.GlassHover, PressedFill = onLike is null ? Ink.GlassRest : Ink.GlassPressed,
        BrushTransitionMs = Design.Motion.Faster,
        HoverScale = Design.Motion.ScaleEmphatic.HoverIf(onLike is not null), PressScale = Design.Motion.ScaleEmphatic.PressIf(onLike is not null),
        Role = AutomationRole.Button, Focusable = onLike is not null, AllowFocusOnInteraction = false,
        OnClick = onLike, Cursor = onLike is null ? (CursorId?)null : CursorId.Hand, BlocksDragArm = true,
        Children =
        [
            new BoxEl
            {
                Key = saved ? "sh:on" : "sh:off",
                Children = [new TextEl(saved ? Icons.HeartFill : Icons.Heart) { Size = 16f, FontFamily = Theme.IconFont, Color = saved ? accent : Ink.InkSecondary, HoverColor = saved ? accent : Ink.Ink }],
            },
        ],
    };

    // The pivot rung reads the context band's OWN constants (`Detail.BandLayout`, G-214) — never a restated copy.

    /// <summary>One pivot link: 14/20/600, a tab role, and an ALWAYS-mounted underline that switches COLOUR (167 ms).</summary>
    static Element PivotLink(string label, bool active, ColorF accent, Action go) => new BoxEl
    {
        Direction = 1, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(Detail.BandLayout.PivotPadX, Spacing.XS, Detail.BandLayout.PivotPadX, Spacing.XXS), Corners = Radii.ControlAll,
        Role = AutomationRole.Tab, Focusable = true, Cursor = CursorId.Hand, OnClick = go,
        Children =
        [
            new TextEl(label)
            {
                Size = 14f, LineHeight = 20f, Weight = 600, Color = active ? Ink.Ink : Ink.InkTertiary, HoverColor = Ink.Ink,
                MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
            },
            new BoxEl
            {
                Height = Detail.BandLayout.UnderlineHeight, AlignSelf = FlexAlign.Stretch, Margin = new Edges4(0f, Detail.BandLayout.UnderlineGap, 0f, 0f),
                Fill = active ? accent : ColorF.Transparent, BrushTransitionMs = Design.Motion.Fast, HitTestVisible = false,
            },
        ],
    };

    /// <summary>The 20 × 2 accent RULE — a section ornament, never a selection bar.</summary>
    static Element SectionRule(ColorF accent) => new BoxEl { Width = 20f, Height = Detail.BandLayout.UnderlineHeight, Shrink = 0f, Fill = accent, HitTestVisible = false };

    // ══ 2. SHARED READS ═══════════════════════════════════════════════════════════════════════════════════════════════

    static Track CurrentTrack()
    {
        var r = Playback.Current.Value;
        _ = Entities.Current.Tracks.Changed.Value;
        return r.Kind == EntityKind.Track ? new Track(r.Slot) : default;
    }

    /// <summary>The stage's art-derived accent for the playing cover, subscribing the late grading.</summary>
    static ColorF CurrentAccent(Track track)
    {
        string url = track.IsValid ? Controls.ArtUrl(track.ImageId) ?? "" : "";
        if (url.Length > 0) _ = Palette.Watch(url).Value;
        return Ink.Accent(url);
    }

    static bool RemoteActive() => Playback.OwnerSignal.Value == Playback.Owner.Foreign;

    // ══ 3. THE SURFACE HOST ═══════════════════════════════════════════════════════════════════════════════════════════

    sealed class SurfaceCore : Component
    {
        NodeHandle _driftNode, _root;
        readonly DriftClock _drift = new();
        Action? _tick;
        IReadSignal<Size2>? _viewport;

        public override Element Render()
        {
            var hooks = UseContext(InputHooks.Current);
            var vp = UseContextSignal(Viewport.Size);
            _viewport = vp;

            // The ONE reflow flag: a coarse band signal written only on a real change, so a resize pixel re-renders nothing.
            var stage = UseSignal(Layout.Seed(vp.Peek().Width, Band.ColumnAvailH(vp.Peek().Height)));
            var seeded = UseRef(vp.Peek().Width > 0f);
            UseSignalEffect(() =>
            {
                var prev = stage.Peek();
                var size = vp.Value;
                var next = Layout.Resolve(size.Width, Band.ColumnAvailH(size.Height), seeded.Value ? prev : (Layout?)null);
                if (size.Width > 0f) seeded.Value = true;
                if (!next.Equals(prev)) stage.Value = next;
            });

            bool drift = Prefs.Lyrics.AnimatedBackdrop() && !Design.Reduced;
            var track = CurrentTrack();
            string art = track.IsValid ? Controls.ArtUrl(track.ImageId) ?? "" : "";
            bool runDrift = Drift.Runs(drift, false, Playback.IsPlaying.Value, art.Length > 0);
            // NOT read here: the secondary-line capability, the line mode and the pane (`SecondaryLineFab` owns them).
            var L = stage.Value;

            // Escape routes to the FOCUSED node, so the surface takes focus once at mount (no ring: visual false).
            UseLayoutEffect(() => { if (!Context.HostNode.IsNull) hooks.FocusNode?.Invoke(Context.HostNode, false); }, DepKey.Empty);
            UseEffect(() => { if (!drift) ResetDrift(); }, drift);             // a drift turned off must not strand the carrier
            UseEffect(() => { _drift.SetRunning(runDrift, NowSeconds()); }, runDrift);
            UseInterval(_tick ??= new Action(DriftTick), Drift.IntervalMs, enabled: runDrift);

            return new BoxEl
            {
                // Shrink 1 + MinWidth 0: the backdrop's paint overscale must never climb into the root's arranged size.
                Grow = 1f, Direction = 1, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
                HitTestPassThrough = true, Focusable = true,
                OnRealized = h => _root = h,
                OnKeyDown = static e =>
                {
                    if (e.Handled || e.KeyCode != Keys.Escape) return;
                    e.Handled = true;
                    Shell.Ui.ImmersiveLyrics.Value = false;
                },
                // Never leave focus NULL while up: an unhandled Escape clears it and disarms the keyboard exit.
                OnFocusChanged = got =>
                {
                    if (got || _root.IsNull) return;
                    if ((hooks.GetFocus?.Invoke() ?? default).IsNull) hooks.FocusNode?.Invoke(_root, false);
                },
                Children =
                [
                    new BoxEl { Height = Band.CaptionH, Shrink = 0f, HitTestPassThrough = true },
                    new BoxEl
                    {
                        Grow = 1f, Shrink = 1f, MinHeight = 0f, ZStack = true, ClipToBounds = true,
                        Fill = Prop.Of(static () => Ink.Floor),
                        Children =
                        [
                            Backdrop(vp, stage, art),
                            // The SHIELD: childless, full-bleed, no fill — it takes every hit the content does not.
                            new BoxEl
                            {
                                Key = "stage:shield", AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                                OnClick = () => { if (!_root.IsNull) hooks.FocusNode?.Invoke(_root, false); },
                            },
                            new BoxEl
                            {
                                Grow = 1f, Direction = 1, MinHeight = 0f, MinWidth = 0f,
                                Children = [TopBar(L.Wide), StageBody(stage, L)],
                            },
                        ],
                    },
                    new BoxEl { Height = Band.PlayerBarH, Shrink = 0f, HitTestPassThrough = true },
                ],
            };
        }

        Element Backdrop(IReadSignal<Size2> vp, IReadSignal<Layout> stage, string art)
        {
            Element cover = art.Length > 0
                ? Ui.Image(art, ImageFit.Cover, aspect: float.NaN, decodePx: 512f, corners: 0f, placeholder: Ink.ArtStandIn(art),
                           transition: ImageTransition.Fade(220f)) with
                {
                    AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                    BakedBlur = new BakedBlurSpec(Drift.SigmaDip, Drift.ResolutionScale),   // baked once per art change
                }
                : new BoxEl { AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, Fill = Ink.ArtStandIn(default) };

            return new BoxEl
            {
                Grow = 1f, ZStack = true, ClipToBounds = true, HitTestVisible = false,
                Children =
                [
                    new BoxEl
                    {
                        // The overscale is a PAINT scale about the slot's centre, never a layout size (a declared 1.30×
                        // Width climbed the ZStacks and arranged the root wider than the window).
                        ZStack = true, HitTestVisible = false, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                        Transform = Prop.Of(() =>
                        {
                            var size = vp.Value;
                            float o = Drift.Overscale, cx = size.Width * 0.5f, cy = Band.BodyH(size.Height) * 0.5f;
                            return new Affine2D(o, 0f, 0f, o, cx * (1f - o), cy * (1f - o));
                        }),
                        // The drift carrier declares NO transform, so nothing else ever stomps the one this writes.
                        Children = [new BoxEl { ZStack = true, HitTestVisible = false, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, OnRealized = h => _driftNode = h, Children = [cover] }],
                    },
                    new BoxEl { Grow = 1f, HitTestVisible = false, Gradient = Scrim() },
                    new BoxEl
                    {
                        // Bound: the wide⇄compact flip re-solves the shade without re-rendering the surface.
                        Width = Prop.Of(() => stage.Value.Wide ? Layout.ColumnShadeW : 0f),
                        AlignSelf = FlexAlign.Stretch, Shrink = 0f, HitTestVisible = false, Gradient = ColumnShade(),
                    },
                ],
            };
        }

        static double NowSeconds() => Design.FrameTime.NowQpc / (double)System.Diagnostics.Stopwatch.Frequency;

        /// <summary>ZERO managed allocation per tick: Peek only, scalar maths, one paint write behind the write gate.</summary>
        void DriftTick()
        {
            if (!_drift.Running || _viewport is null) return;
            var scene = Context.Scene;
            var h = _driftNode;
            if (scene is null || h.IsNull || !scene.IsLive(h)) return;
            var size = _viewport.Peek();
            float w = MathF.Max(1f, size.Width), bh = Band.BodyH(size.Height);
            var pose = Drift.At(_drift.Sample(NowSeconds()), w, bh);
            float s = pose.Scale, cx = w * 0.5f, cy = bh * 0.5f;
            var next = new Affine2D(s, 0f, 0f, s, cx * (1f - s) + pose.Dx, cy * (1f - s) + pose.Dy);
            ref NodePaint p = ref scene.Paint(h);
            var cur = p.LocalTransform;
            if (!Drift.Worth(new Drift.Pose(cur.Dx, cur.Dy, cur.M11), new Drift.Pose(next.Dx, next.Dy, next.M11))) return;
            p.LocalTransform = next;
            scene.Mark(h, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
        }

        void ResetDrift()
        {
            _drift.Reset();
            var scene = Context.Scene;
            var h = _driftNode;
            if (scene is null || h.IsNull || !scene.IsLive(h)) return;
            ref NodePaint p = ref scene.Paint(h);
            if (p.LocalTransform.IsIdentity) return;
            p.LocalTransform = Affine2D.Identity;
            scene.Mark(h, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
        }
    }

    /// <summary>The surface's own controls, pushed right under the scrim's top deepening (no veil of its own). The 🌐 is
    /// composed only when the document on screen has a second layer AND the lyrics pane is up; the ⌄ is unconditional
    /// and its tooltip teaches the keyboard half.</summary>
    static Element TopBar(bool wide) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Start, Gap = Spacing.S, Shrink = 0f, Height = Band.TopBandFor(wide),
        Padding = new Edges4(Spacing.L, Spacing.L, Spacing.L, Spacing.M),
        Children =
        [
            // Shrink (G-256): a grown spacer that keeps its last arranged width across maximize → restore would push the ⌄.
            new BoxEl { Grow = 1f, Shrink = 1f, MinWidth = 0f, HitTestVisible = false },
            Embed.Comp(static () => new SecondaryLineFab()),
            ToolTip.Wrap(ExitFab(Icons.ChevronDown, static () => Shell.Ui.ImmersiveLyrics.Value = false), Loc.Get(Strings.Player.CloseLyricsHint)),
        ],
    };

    /// <summary>The 🌐 secondary-line FAB as its OWN subscriber (capability, line mode, pane, accent), so a flip re-renders
    /// this FAB alone. It must never be read by <see cref="SurfaceCore"/>: the lyrics pane mounts INSIDE the surface's first
    /// render, and its document host publishes the capability from an eager effect in that same run — the engine drops a
    /// write into a computation that is still running (the lost wakeup, D42), so the surface kept the stale capability.
    /// Absent, it is a zero box (the spacer absorbs its gap, so the ⌄ never moves).</summary>
    sealed class SecondaryLineFab : Component
    {
        public override Element Render()
        {
            int available = Prefs.Lyrics.Available.Value;
            int secondary = Prefs.Lyrics.SecondaryLine();
            bool lyricsPane = Pane.Current.Value == Pane.Lyrics;
            if (available == 0 || !lyricsPane) return new BoxEl { Width = 0f, Height = 0f, Shrink = 0f, HitTestVisible = false };
            var accent = CurrentAccent(CurrentTrack());
            return ToolTip.Wrap(ScrimFab(Icons.Globe, () => Prefs.Lyrics.SetSecondaryLine(Prefs.Lyrics.Next(secondary, available)),
                Rail.HeaderGlyph, accent, latched: (available & Prefs.Lyrics.BitFor(secondary)) != 0), Prefs.Lyrics.Tooltip(secondary));
        }
    }

    /// <summary>The two regions. The identity's horizontal participation lives on THIS wrapper (Grow 0, the authored
    /// width, a column), so the component anchor's mirrored Grow can only ever be vertical — the grow-leak fix.</summary>
    static Element StageBody(IReadSignal<Layout> stage, in Layout L) => new BoxEl
    {
        Direction = (byte)(L.Wide ? 0 : 1), Grow = 1f, Shrink = 1f, MinHeight = 0f, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
        Gap = L.Wide ? Layout.RegionGapW : 0f,
        Children =
        [
            new BoxEl
            {
                Key = "stage:identity", Direction = 1, MinHeight = 0f, MinWidth = 0f, Grow = 0f, Shrink = 0f,
                Width = L.Wide ? L.LayoutWidth : float.NaN,
                Children = [Embed.Comp(() => new IdentityCore(stage)) with { Key = "stage:identity-comp" }],
            },
            new BoxEl
            {
                Key = "stage:panes", Grow = 1f, Shrink = 1f, MinHeight = 0f, MinWidth = 0f,
                Children = [Embed.Comp(static () => new PanesCore())],
            },
        ],
    };

    // ══ 4. THE IDENTITY COLUMN (StageIdentity) ════════════════════════════════════════════════════════════════════════

    /// <summary>The LEFT region: identity + transport, optically centred in a 352 column (wide) or folded into a header
    /// row (compact). The layout arrives as a SIGNAL, never a frozen value.</summary>
    sealed class IdentityCore(IReadSignal<Layout> layout) : Component
    {
        public override Element Render()
        {
            var L = layout.Value;
            var overlay = UseContext(Overlay.Service);
            var track = CurrentTrack();
            bool playing = Playback.IsPlaying.Value;
            bool loading = Playback.PhaseSignal.Value == Playback.Phase.Loading;
            bool error = Playback.Error.Value != Playback.Fault.None;
            var lib = Controls.Library;
            string uri = track.IsValid ? track.Uri.Text : "";
            string title = track.IsValid ? track.Title : "";
            bool saved = lib is not null && uri.Length > 0 && lib.IsSaved(uri);
            bool can = Transport.CanTransport(track.IsValid, error);
            bool primary = Transport.PrimaryEnabled(track.IsValid, loading);
            var accent = CurrentAccent(track);
            Action? like = lib is { } seam && uri.Length > 0 ? () => seam.ToggleSaved(uri, title) : null;
            string shown = Transport.UsesTitle(title, uri) ? title : Loc.Get(Strings.Player.NothingPlaying);

            BoxEl body = L.Wide
                ? WideColumn(L, track, shown, playing, can, primary, saved, like, accent)
                : CompactHeader(L, track, shown, playing, can, primary, saved, like, accent);

            // The menu goes on a SHELL plus a childless full-bleed SHIELD beneath the content: the shield always wins the
            // hit over the shell and its hover/press cascade reaches nothing; the shell keeps the context bit as an
            // ANCESTOR, which carries a right-click on the art, the title or a button — and the "…" — up to the menu.
            return new BoxEl
            {
                ZStack = true, MinHeight = 0f, Width = body.Width, Grow = body.Grow, Shrink = body.Shrink,
                Children =
                [
                    new BoxEl { Key = "stage:context-shield" }.WithContextMenu(overlay, static () => NowPlayingMenu?.Invoke()),
                    body with { Width = float.NaN, Grow = 0f, Shrink = 0f },
                ],
            }.WithContextMenu(overlay, static () => NowPlayingMenu?.Invoke());
        }
    }

    const float TitleFontSize = 22f, TitleLine = 28f, CompactTitleSize = 16f, CompactTitleLine = 22f, StackGap = 18f, TransportGap = 6f;
    const ushort TitleWeight = 650;
    const string DisplayFace = "Segoe UI Variable Display";

    static BoxEl WideColumn(in Layout L, Track track, string title, bool playing, bool can, bool primary, bool saved, Action? like, ColorF accent)
    {
        int rung = 0;   // the entrance cascade; the COVER is deliberately not on it
        var kids = new List<Element>(6)
        {
            new BoxEl
            {
                Key = "stage:art", Width = L.ArtSize, Height = L.ArtSize, Shrink = 0f, Corners = Radii.CardAll,
                Shadow = Elevation.Dialog, Margin = new Edges4(0f, 0f, 0f, StackGap),
                Children = [Controls.Artwork(track.IsValid ? Controls.ArtUrl(track.ImageId) : null, L.ArtSize, L.ArtSize, Radii.Card, decodePx: 512)],
            },
            // Every wrapper is a COLUMN on purpose: a row's single child takes its intrinsic width (the stub-seek report).
            new BoxEl { Key = "stage:identity-row", Direction = 1, Animate = Design.Entrance.Row(rung++), Children = [IdentityRow(title, saved, like, accent, wide: true)] },
            new BoxEl { Key = "stage:seek", Direction = 1, Animate = Design.Entrance.Row(rung++), Margin = new Edges4(0f, StackGap, 0f, 0f), Children = [SeekBlock()] },
            new BoxEl { Key = "stage:transport", Direction = 1, Animate = Design.Entrance.Row(rung++), Margin = new Edges4(0f, Spacing.S, 0f, 0f), Children = [TransportRow(L, playing, can, primary, accent)] },
        };
        if (L.ShowVolume)
            kids.Add(new BoxEl { Key = "stage:volume", Direction = 1, Animate = Design.Entrance.Row(rung++), Margin = new Edges4(0f, StackGap, 0f, 0f), Children = [Embed.Comp(static () => new VolumeRow())] });
        if (L.ShowDeviceLine)
            kids.Add(new BoxEl { Key = "stage:device", Direction = 1, Animate = Design.Entrance.Row(rung), Margin = new Edges4(0f, Spacing.S, 0f, 0f), Children = [Embed.Comp(static () => new DeviceLine())] });

        return new BoxEl
        {
            // Grow is VERTICAL only (the wrapper owns the horizontal claim) and it is what gives Justify.Center space.
            Width = L.LayoutWidth, Shrink = 0f, Grow = 1f, MinHeight = 0f, Direction = 1, Justify = FlexJustify.Center,
            Padding = new Edges4(Layout.ColumnPadX, Layout.ColumnPadY, Layout.ColumnPadX, Layout.ColumnPadY),
            Children = kids.ToArray(),
        };
    }

    /// <summary>Compact: art 64 · identity · prev · play · next · "…", with the seek block beneath — it NEVER folds.</summary>
    static BoxEl CompactHeader(in Layout L, Track track, string title, bool playing, bool can, bool primary, bool saved, Action? like, ColorF accent)
    {
        var row = new List<Element>(6)
        {
            new BoxEl
            {
                Key = "stage:art", Width = L.ArtSize, Height = L.ArtSize, Shrink = 0f, Corners = Radii.ControlAll, Shadow = Elevation.Card,
                Children = [Controls.Artwork(track.IsValid ? Controls.ArtUrl(track.ImageId) : null, L.ArtSize, L.ArtSize, Radii.Control, decodePx: 192)],
            },
            IdentityRow(title, saved, like, accent, wide: false) with { Key = "stage:identity-row" },
            Glyph(Icons.Previous, static () => Playback.Previous(), L.StepBox, 15f, can) with { Key = "stage:prev" },
            Play(playing, primary, L.PlayBox, 17f) with { Key = "stage:play" },
            Glyph(Icons.Next, static () => Playback.Next(), L.StepBox, 15f, can) with { Key = "stage:next" },
        };
        var folded = L;                                                    // the compact fold set is constant for the shape
        if (L.ShowOverflow) row.Add(Embed.Comp(() => new OverflowButton(folded)) with { Key = "stage:overflow" });
        return new BoxEl
        {
            Direction = 1, Shrink = 0f, Padding = new Edges4(Spacing.L, Spacing.S, Spacing.L, Spacing.S),
            Children =
            [
                new BoxEl { Key = "stage:header-row", Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Animate = Design.Entrance.Row(0), Children = row.ToArray() },
                new BoxEl { Key = "stage:seek", Direction = 1, Animate = Design.Entrance.Row(1), Margin = new Edges4(0f, Spacing.XS, 0f, 0f), Children = [SeekBlock()] },
            ],
        };
    }

    /// <summary>Title (display face 650) over the hover-underlined "Artist — Album" link · heart · (wide) "…".</summary>
    static BoxEl IdentityRow(string title, bool saved, Action? like, ColorF accent, bool wide)
    {
        var kids = new List<Element>(3)
        {
            new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center, Gap = Spacing.XXS, ClipToBounds = true,
                Children =
                [
                    new TextEl(title)
                    {
                        Size = wide ? TitleFontSize : CompactTitleSize, LineHeight = wide ? TitleLine : CompactTitleLine,
                        Weight = TitleWeight, FontFamily = DisplayFace, Color = Ink.Ink,
                        Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    },
                    Embed.Comp(static () => new MetaLink()),
                ],
            },
            Heart(saved, like, accent),
        };
        if (wide)
            kids.Add(new BoxEl
            {
                Width = Controls.IconButtonSize, Height = Controls.IconButtonSize, Shrink = 0f,
                Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
                Fill = Ink.GlassRest, HoverFill = Ink.GlassHover, PressedFill = Ink.GlassPressed, BrushTransitionMs = Design.Motion.Faster,
                Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, AllowFocusOnInteraction = false,
                ClickRequestsContext = true,                                       // the same menu, byte-identical to a right-click
                Children = [new TextEl(Icons.More) { Size = 16f, FontFamily = Theme.IconFont, Color = Ink.InkSecondary, HoverColor = Ink.Ink }],
            });
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
            Grow = wide ? 0f : 1f, Basis = wide ? float.NaN : 0f, Shrink = 1f, Children = kids.ToArray(),
        };
    }

    /// <summary>The bar's own SeekBar + elapsed · quality · remaining, the times in the stage's tertiary ink.</summary>
    static Element SeekBlock() => new BoxEl
    {
        Direction = 1, Gap = Spacing.XXS, MinWidth = 0f,
        Children =
        [
            Shell.SeekBar(),
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Children =
                [
                    Shell.TimeText(remaining: false, ink: Ink.InkTertiary),
                    // Shrink on both spacers (G-256): a grown spacer keeps its last arranged width across maximize →
                    // restore unless it may shrink, and pushes the remaining-time label off the column.
                    new BoxEl { Grow = 1f, Shrink = 1f, MinWidth = 0f, HitTestVisible = false },
                    Embed.Comp(static () => new QualityBadge()),
                    new BoxEl { Grow = 1f, Shrink = 1f, MinWidth = 0f, HitTestVisible = false },
                    Shell.TimeText(remaining: true, ink: Ink.InkTertiary),
                ],
            },
        ],
    };

    /// <summary>The playing stream's format ("FLAC", "Vorbis 320 kbps") — or NOTHING (no format, or a remote device).</summary>
    sealed class QualityBadge : Component
    {
        public override Element Render()
        {
            var fmt = Playback.StreamFormat.Value;
            if (!Transport.ShowsQualityBadge(!fmt.IsEmpty, RemoteActive())) return new BoxEl { HitTestVisible = false };
            return new TextEl(Entities.Strings.Resolve(fmt))
            {
                Size = 11.5f, LineHeight = 16f, Weight = 600, Color = Ink.InkTertiary, Shrink = 0f,
                Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Margin = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
            };
        }
    }

    /// <summary>"Artist — Album": the hover scopes to THIS word (never the row), and the route resolves at INVOKE time.</summary>
    sealed class MetaLink : Component
    {
        public override Element Render()
        {
            var hover = UseSignal(false);
            var track = CurrentTrack();
            _ = Entities.Current.Albums.Changed.Value;
            string artists = track.IsValid ? Entities.Strings.Resolve(track.ArtistLineId) : "";
            string album = track.IsValid && track.Album.IsValid ? track.Album.Title : "";
            string line = artists.Length > 0 && album.Length > 0 ? artists + " — " + album : artists.Length > 0 ? artists : album;
            if (line.Length == 0) return new BoxEl { Height = 0f, HitTestVisible = false };
            bool enabled = Shell.LinkFor(track, Shell.LinkSlot.Artist).Kind != Shell.RouteKind.NotFound;
            bool lit = enabled && hover.Value;
            return new BoxEl
            {
                MinWidth = 0f, Shrink = 1f, ClipToBounds = true, Cursor = enabled ? CursorId.Hand : (CursorId?)null,
                OnClick = enabled ? s_go : null,
                OnHoverMove = enabled ? _ => { if (!hover.Peek()) hover.Value = true; } : null,
                OnPointerExit = enabled ? () => { if (hover.Peek()) hover.Value = false; } : null,
                Role = enabled ? AutomationRole.Hyperlink : AutomationRole.Text, Focusable = enabled, AllowFocusOnInteraction = false,
                Children =
                [
                    new TextEl(line)
                    {
                        Size = 14f, LineHeight = 20f, Weight = 400, Color = lit ? Ink.Ink : Ink.InkSecondary, Underline = lit,
                        Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    },
                ],
            };
        }

        static readonly Action s_go = Go;

        static void Go()
        {
            var r = Playback.Current.Peek();
            if (r.Kind != EntityKind.Track) return;
            var route = Shell.LinkFor(new Track(r.Slot), Shell.LinkSlot.Artist);
            if (route.Kind != Shell.RouteKind.NotFound) Shell.GoTo(route);
        }
    }

    /// <summary>32 · 40 · 56 · 40 · 32, centred; the satellites leave with the compact fold.</summary>
    static Element TransportRow(in Layout L, bool playing, bool can, bool primary, ColorF accent)
    {
        var kids = new List<Element>(5);
        if (L.ShowSatellites)
            kids.Add(ToolTip.Wrap(Satellite(Icons.Shuffle, Shell.ToggleShuffle, can, Playback.Shuffle.Value, accent, L.SatelliteBox),
                Loc.Get(Strings.Player.Shuffle)) with { Key = "tp:shuffle" });
        kids.Add(ToolTip.Wrap(Glyph(Icons.Previous, static () => Playback.Previous(), L.StepBox, 17f, can), Loc.Get(Strings.Player.Previous)) with { Key = "tp:prev" });
        kids.Add(Play(playing, primary, L.PlayBox, 22f) with { Key = "tp:play" });
        kids.Add(ToolTip.Wrap(Glyph(Icons.Next, static () => Playback.Next(), L.StepBox, 17f, can), Loc.Get(Strings.Player.Next)) with { Key = "tp:next" });
        if (L.ShowSatellites)
        {
            var repeat = Playback.Repeat.Value;
            kids.Add(ToolTip.Wrap(Satellite(repeat == RepeatMode.Track ? Icons.RepeatOne : Icons.RepeatAll, Shell.CycleRepeat, can, repeat != RepeatMode.Off, accent, L.SatelliteBox),
                Loc.Get(Strings.Player.Repeat)) with { Key = "tp:repeat" });
        }
        return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = TransportGap, Children = kids.ToArray() };
    }

    static bool Muted() => Playback.Audio.Muted.Value || Playback.Volume.Value <= 0.001f;

    static readonly FormatCache<int> s_percent = FormatCache.Create<int>();

    /// <summary>The mute glyph (its own component, so the zero-crossing swap re-renders only itself) + the STOCK slider
    /// recoloured onto the stage ink, with a DERIVED track length (264).</summary>
    sealed class VolumeRow : Component
    {
        static readonly Slider.SliderOptions Options = new()
        {
            ThumbToolTipValueConverter = static v => s_percent.Get(Math.Clamp((int)MathF.Round(v * 100f), 0, 100), static p => p + "%"),
        };

        public override Element Render()
        {
            var level = UseFloatSignal(Playback.Volume.Peek());
            UseSignalEffect(() => level.Value = Playback.Volume.Value);
            ColorF ink = Ink.Ink;
            var style = Slider.DefaultStyle with
            {
                RailFill = ink with { A = 0.26f }, RailFillDisabled = ink with { A = 0.14f },
                ValueFill = ink, ValueFillPointerOver = ink with { A = 0.90f }, ValueFillPressed = ink with { A = 0.80f }, ValueFillDisabled = ink with { A = 0.32f },
                ThumbRing = ink, ThumbFill = ink, ThumbFillPointerOver = ink with { A = 0.90f }, ThumbFillPressed = ink with { A = 0.80f },
                ThumbFillDisabled = ink with { A = 0.32f }, ThumbBorder = GradientSpec.Solid(Ink.Stroke),
            };
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
                Children =
                [
                    Embed.Comp(static () => new MuteGlyph()),
                    new BoxEl
                    {
                        Grow = 1f, Shrink = 1f, MinWidth = 0f,
                        Children = [Slider.Create(level, static v => Playback.SetVolume(v), options: Options, length: Band.VolumeTrackW, thickness: 20f, style: style)],
                    },
                ],
            };
        }
    }

    sealed class MuteGlyph : Component
    {
        public override Element Render()
        {
            bool muted = Muted();
            return ToolTip.Wrap(Glyph(muted ? Icons.Mute : Icons.Volume, Shell.ToggleMute, Controls.IconButtonSize, 15f),
                Loc.Get(muted ? Strings.Player.Unmute : Strings.Player.Mute));
        }
    }

    /// <summary>What is actually playing this: a Connect device ("Playing on {device}") or the selected local endpoint /
    /// the system default. A click opens the two-section device picker, anchored above the line.</summary>
    sealed class DeviceLine : Component
    {
        public override Element Render()
        {
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var overlay = UseContext(Overlay.Service);
            _ = Playback.Devices.Changed.Value;
            int slot = Playback.ActiveDeviceSlot.Value;
            var rows = Playback.Devices.Rows;
            bool remote = RemoteActive() && (uint)slot < (uint)rows.Length;
            string name;
            string glyph;
            if (remote)
            {
                name = Strings.Player.PlayingOn(rows[slot].Name ?? "");
                glyph = Icons.Devices;
            }
            else
            {
                string? selected = Playback.Audio.SelectedOutputId.Value;
                name = Loc.Get(Strings.Player.SystemDefault);
                glyph = Icons.Speakers;
                foreach (var d in Playback.Audio.Devices.Value)
                {
                    if (selected is not { Length: > 0 } || !string.Equals(d.Id, selected, StringComparison.OrdinalIgnoreCase)) continue;
                    name = d.Name;
                    glyph = LocalGlyph(d.Kind);
                    break;
                }
            }

            void Toggle()
            {
                if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                var opened = overlay.Open(() => anchor.Value, () => MenuFlyout.Create(DeviceItems(), () => handle.Value?.Close()),
                    FlyoutPlacement.TopEdgeAlignedLeft,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
                opened.ClosedAction = () => handle.Value = null;
                handle.Value = opened;
            }

            ColorF rest = remote ? Ink.InkSecondary : Ink.InkTertiary;
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinHeight = 24f, MinWidth = 0f,
                Padding = new Edges4(Spacing.XS, 0f, Spacing.S, 0f), Corners = Radii.ControlAll,
                Fill = Ink.GlassRest, HoverFill = Ink.GlassHover, PressedFill = Ink.GlassPressed, BrushTransitionMs = Design.Motion.Faster,
                ClipToBounds = true, Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
                Cursor = CursorId.Hand, OnClick = Toggle, OnRealized = h => anchor.Value = h,
                Children =
                [
                    new TextEl(glyph) { Size = 12f, FontFamily = Theme.IconFont, Color = rest, HoverColor = Ink.Ink },
                    new TextEl(name) { Size = 12f, LineHeight = 16f, Weight = 400, Color = rest, HoverColor = Ink.Ink, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
                ],
            };
        }
    }

    /// <summary>The local endpoint's form factor (the endpoint roster's kind byte): headphones/headset, HDMI, else speakers.</summary>
    static string LocalGlyph(byte kind) => kind switch
    {
        1 or 2 => Icons.Headphones,
        3 => Icons.TvMonitor,
        _ => Icons.Speakers,
    };

    /// <summary>The two-section picker's rows (`Shell.DevicePickerRows`) as menu items, built at OPEN time.</summary>
    static List<MenuFlyoutItem> DeviceItems()
    {
        var connect = Playback.Devices.Rows;
        int active = Playback.ActiveDeviceSlot.Peek();
        bool remote = Playback.OwnerSignal.Peek() == Playback.Owner.Foreign;
        string? activeId = (uint)active < (uint)connect.Length ? connect[active].Id : null;
        var rows = Shell.DevicePickerRows(Playback.Audio.Devices.Peek(), Playback.Audio.SelectedOutputId.Peek(),
            Playback.Audio.Supported.Peek(), !remote, connect, activeId);
        var items = new List<MenuFlyoutItem>(rows.Count);
        foreach (var r in rows)
        {
            string id = r.DeviceId;
            items.Add(r.Kind switch
            {
                Shell.DevicePickerRowKind.Separator => MenuFlyoutItem.Separator,
                Shell.DevicePickerRowKind.LocalDefault => MenuFlyoutItem.RadioItem(r.Label, r.IsChecked,
                    r.Enabled ? static () => Playback.Audio.Select(null) : null, Icons.Speakers, r.Enabled) with { AcceleratorText = r.Accelerator },
                Shell.DevicePickerRowKind.LocalDevice => MenuFlyoutItem.RadioItem(r.Label, r.IsChecked,
                    r.Enabled ? () => Playback.Audio.Select(id) : null, LocalGlyph(r.LocalKind), r.Enabled) with { AcceleratorText = r.Accelerator },
                Shell.DevicePickerRowKind.ConnectDevice => MenuFlyoutItem.RadioItem(r.Label, r.IsChecked, () =>
                {
                    var roster = Playback.Devices.Rows;
                    for (int i = 0; i < roster.Length; i++)
                        if (string.Equals(roster[i].Id, id, StringComparison.OrdinalIgnoreCase)) { Playback.TransferTo(i); return; }
                }, Icons.Devices),
                _ => new MenuFlyoutItem(r.Label, default, false, null),     // section headers and empty hints
            });
        }
        return items;
    }

    /// <summary>The compact header's "…": the FOLDED controls, live, built at open time. Nothing folded ⇒ it opens nothing.</summary>
    sealed class OverflowButton(Layout layout) : Component
    {
        public override Element Render()
        {
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var overlay = UseContext(Overlay.Service);

            void Toggle()
            {
                if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                var items = new List<MenuFlyoutItem>(8);
                if (!layout.Shows(Control.Shuffle))
                    items.Add(MenuFlyoutItem.Toggle(Loc.Get(Strings.Player.Shuffle), Playback.Shuffle.Peek(),
                        Shell.ToggleShuffle, Icons.Shuffle));
                if (!layout.Shows(Control.Repeat))
                {
                    var r = Playback.Repeat.Peek();
                    items.Add(MenuFlyoutItem.Toggle(Loc.Get(Strings.Player.Repeat), r != RepeatMode.Off, Shell.CycleRepeat,
                        r == RepeatMode.Track ? Icons.RepeatOne : Icons.RepeatAll));
                }
                if (!layout.Shows(Control.Volume))
                {
                    bool muted = Playback.Audio.Muted.Peek() || Playback.Volume.Peek() <= 0.001f;
                    items.Add(new MenuFlyoutItem(Loc.Get(muted ? Strings.Player.Unmute : Strings.Player.Mute), muted ? Icons.Volume : Icons.Mute, true, Shell.ToggleMute));
                }
                if (!layout.Shows(Control.OutputDevice))
                {
                    if (items.Count > 0) items.Add(MenuFlyoutItem.Separator);
                    items.AddRange(DeviceItems());
                }
                if (items.Count == 0) return;                                  // never an empty menu frame
                var opened = overlay.Open(() => anchor.Value, () => MenuFlyout.Create(items, () => handle.Value?.Close()),
                    FlyoutPlacement.BottomEdgeAlignedRight,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
                opened.ClosedAction = () => handle.Value = null;
                handle.Value = opened;
            }

            return ToolTip.Wrap(Glyph(Icons.More, Toggle, Controls.IconButtonSize, 16f, onRealized: h => anchor.Value = h), Loc.Get(Strings.Player.NowPlaying));
        }
    }

    // ══ 5. THE PANES (StagePanes) ═════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Both panes MOUNTED; a 250 ms opacity cross-fade (the token's own reduced-motion policy) with HitTestVisible
    /// following the active one, and the "Lyrics · Queue" pivot along the bottom edge.</summary>
    sealed class PanesCore : Component
    {
        public override Element Render()
        {
            bool lyrics = Pane.Current.Value == Pane.Lyrics;
            var accent = CurrentAccent(CurrentTrack());
            return new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
                Children =
                [
                    new BoxEl
                    {
                        Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, ZStack = true, ClipToBounds = true,
                        Children =
                        [
                            new BoxEl
                            {
                                Key = "pane:lyrics", AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                                Opacity = lyrics ? 1f : 0f, Transition = MotionTok.ControlNormal, HitTestVisible = lyrics,
                                Children = [Lyrics.StagePane()],             // K3's column: capped 700, left-anchored, gutter + pivot band reserved
                            },
                            new BoxEl
                            {
                                Key = "pane:queue", AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                                Opacity = lyrics ? 0f : 1f, Transition = MotionTok.ControlNormal, HitTestVisible = !lyrics,
                                Gradient = PaneShade(),
                                Children = [Embed.Comp(static () => new QueueSkin())],
                            },
                        ],
                    },
                    new BoxEl
                    {
                        Direction = 0, Height = Band.PivotBandH, Shrink = 0f, AlignItems = FlexAlign.End, Justify = FlexJustify.End, Gap = Detail.BandLayout.PivotGap,
                        Padding = new Edges4(Spacing.XXL, 0f, Spacing.XXL, Spacing.L),
                        Children =
                        [
                            PivotLink(Loc.Get(Strings.Player.Lyrics), lyrics, accent, static () => Pane.Current.Value = Pane.Lyrics),
                            PivotLink(Loc.Get(Strings.Player.Queue), !lyrics, accent, static () => Pane.Current.Value = Pane.Queue),
                        ],
                    },
                ],
            };
        }
    }

    /// <summary>The stage queue pane's SKIN: "Playing next · from {context}" over the 20 × 2 rule, the ∞ Autoplay row, and
    /// the rows (owner Q's) — or, until they are installed / when nothing is upcoming, the plain tertiary sentence.</summary>
    sealed class QueueSkin : Component
    {
        public override Element Render()
        {
            var track = CurrentTrack();
            var accent = CurrentAccent(track);
            _ = Platform.SettingsChanged.Value;
            bool autoplay = Platform.Settings.Get(Platform.Keys.AutoplayEnabled);
            EntityId contextId = Playback.ContextUri.Value;
            UseEffect(static () => Queue.EnsureContext(Playback.ContextUri.Peek()), DepKey.From(contextId.GetHashCode()));
            string? source = Queue.ContextName(contextId);
            Element? rows = QueuePaneBody?.Invoke();
            var content = new List<Element>(2) { AutoplayRow(autoplay, accent) };
            content.Add(rows ?? new BoxEl
            {
                Padding = new Edges4(0f, Spacing.XXL, 0f, 0f),
                Children = [new TextEl(Loc.Get(Strings.Player.QueueEmpty)) { Size = 14f, LineHeight = 20f, Color = Ink.InkTertiary }],
            });

            var title = new TextEl(Loc.Get(Strings.Player.PlayingNext))
            {
                Size = 20f, LineHeight = 28f, Weight = 600, Color = Ink.Ink, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 0f,
            };
            Element[] headerRow = [title];
            if (source is { Length: > 0 })
                headerRow = [title, new TextEl(Strings.Player.FromContext(source)) { Size = 12f, LineHeight = 16f, Color = Ink.InkTertiary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f }];

            return new BoxEl
            {
                Direction = 1, Grow = 1f, MinHeight = 0f, MinWidth = 0f, ClipToBounds = true,
                Padding = new Edges4(Spacing.XXL, Spacing.XXL, Spacing.XXL, 0f),
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, Shrink = 0f, Gap = Spacing.S, Padding = new Edges4(0f, 0f, 0f, Spacing.M),
                        Children = [new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f, Children = headerRow }, SectionRule(accent)],
                    },
                    new ScrollEl
                    {
                        Grow = 1f, MinHeight = 0f, AutoEdgeFade = true, ScrollKey = "stagequeue",
                        Content = new BoxEl { Direction = 1, MinHeight = 0f, Padding = new Edges4(0f, 0f, 0f, Band.PivotBandH), Children = content.ToArray() },
                    },
                ],
            };
        }
    }

    /// <summary>The ∞ Autoplay row — a CHECKBOX role over the same setting the Settings toggle and the rail's pill write.</summary>
    static Element AutoplayRow(bool on, ColorF accent) => new BoxEl
    {
        Key = "stage:autoplay", Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, MinHeight = 48f,
        Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f), Corners = Radii.ControlAll,
        Fill = Ink.GlassRest, HoverFill = Ink.GlassHover, PressedFill = Ink.GlassPressed, BrushTransitionMs = Design.Motion.Faster,
        Role = AutomationRole.CheckBox, Focusable = true, Cursor = CursorId.Hand,
        OnClick = static () => Platform.Settings.Set(Platform.Keys.AutoplayEnabled, !Platform.Settings.Get(Platform.Keys.AutoplayEnabled)),
        Children =
        [
            new TextEl("∞") { Size = 17f, LineHeight = 22f, Weight = 600, Color = on ? accent : Ink.InkTertiary, Width = 22f },
            new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XXS,
                Children =
                [
                    new TextEl(Loc.Get(Strings.Player.Autoplay)) { Size = 13f, LineHeight = 18f, Weight = 600, Color = on ? Ink.Ink : Ink.InkSecondary },
                    new TextEl(Loc.Get(Strings.Player.AutoplayHint)) { Size = 12f, LineHeight = 16f, Color = Ink.InkTertiary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                ],
            },
        ],
    };
}
