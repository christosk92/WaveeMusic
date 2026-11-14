// ── Shell/Deck.Bleed.cs ────────────────────────────────────────────────────────────────────────────────────────────
// the EDGE-TO-EDGE canvas: the Canvas player style at rest fills the side panel from its very top, behind the head and the style chip (the
// owner's approved concept, 2026-11-08: `canvas-design/project/Bleed*.dc.html`)
//
// Role: UI (core: the panel frames place it and draw their own scrims and endings over it; the hero holds the room under it through the
// stage's `bleed` height)
//
// THE SIZE is the panel's width × 16/9, never capped: a wider panel shows more of the canvas, taller. A width change eases the height (the
// stage's Reflow motion), so dragging the panel's edge never jumps the canvas.
//
// WHO PRESENTS. It is the canvas surface `Bleed`, the lowest priority: the picker's Canvas cell (`Deck`) and the full-screen stage take the
// one player from it and hand it back when they let go (Canvas.cs's owner hand-off), so opening the picker never mounts two presenters.
//
// THE MEDIA SCOPE (the owner, 2026-11-08: "in light mode the canvas looks a bit ugly"). Over the canvas the panel is a media surface in
// BOTH kinds, as over a photograph (`MediaScrim`'s rule: light on-media ink over a darkening veil). The head's scrim darkens toward
// `BleedShade` (the panel's ground in the dark kind; the media stage in the light kind, so a light ground never fades over a dark video into
// a grey haze), what is drawn over the canvas paints with `CanvasInk` (the on-media ladder in the light kind; the theme's own ink in the dark
// kind, which is already light). A view the core mounts over the canvas (the lyrics peek) reads `OverCanvas`.
//
// THE LIGHT KIND'S FOOT (the owner, 2026-11-08, second pass: "in zune the fade is too dark for light mode", "same for winui"). The dark kind
// fades the canvas's lower half into its dark ground and sets the copy on it. The light kind does not fade into black: the copy over the
// canvas ends `LightFoot` above the canvas's foot (`CopyFoot`), a veil only as dark as the copy needs sits behind the copy (`LightScope`: the
// house scrim, a static floor plus the boost `MediaScrim` solves on the canvas's poster, capped well below black), and the canvas's last
// `LightFoot` dissolves, eased, into the panel's light ground, which is where "Next up" starts: video, then the light panel.

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;

namespace Wavee;

/// <summary>The ink of what is drawn over the edge-to-edge canvas (<paramref name="Over"/>) or on the panel's ground: a MODE, like
/// <see cref="Lyrics.Ink"/>, resolved where the element is built, so a theme switch re-reads it. Over the canvas in the light kind it is the
/// on-media ladder (light ink; the accent solved as light text on the ground the copy sits on, <see cref="Deck.LightCopyGround"/>); everywhere
/// else, and in the dark kind, the theme's own.</summary>
public readonly record struct CanvasInk(bool Over)
{
    bool Media => Over && R.IsLight;

    /// <summary>Titles, the selected tab, glyphs.</summary>
    public ColorF Primary => Media ? R.OnMedia.Ink : Tok.TextPrimary;
    /// <summary>Subtitles, the other tabs, a chevron.</summary>
    public ColorF Secondary => Media ? R.OnMedia.InkSecondary : Tok.TextSecondary;
    /// <summary>Captions.</summary>
    public ColorF Tertiary => Media ? R.OnMedia.InkTertiary : Tok.TextTertiary;
    /// <summary>Accent TEXT (a link, the album): over the canvas, the first light rung of the accent's own ramp that reads on the ground the copy
    /// sits on (<see cref="Deck.LightCopyGround"/>; <see cref="ColorScience.AccentFloor"/>, as the dark kind's accent text is a light rung), else
    /// the on-media ink.</summary>
    public ColorF Accent => Media ? ColorScience.AccentFloor(Tok.AccentTextPrimary, ThemeKind.Dark, Deck.LightCopyGround, R.OnMedia.Ink) : Tok.AccentTextPrimary;
    /// <summary>An accent FILL that marks (a selection pill): over the media stage the accent text's colour, where the light kind's deep fill
    /// would not show.</summary>
    public ColorF AccentFill => Media ? Accent : Tok.AccentDefault;
    /// <summary>The hairline that rings a plate or an image.</summary>
    public ColorF Stroke => Media ? R.OnMedia.Stroke : Tok.StrokeCardDefault;
    /// <summary>A card's rest fill (over the canvas a breath of the on-media ink, as the dark kind's card is).</summary>
    public ColorF Plate => Media ? R.OnMedia.GlassPlate : Tok.FillCardDefault;
    /// <summary>A glyph button's subtle hover fill.</summary>
    public ColorF Hover => Media ? R.OnMedia.GlassHover : Tok.FillSubtleSecondary;
    /// <summary>A glyph button's subtle press fill.</summary>
    public ColorF Pressed => Media ? R.OnMedia.GlassPressed : Tok.FillSubtleTertiary;
}

public static partial class Deck
{
    /// <summary>The edge-to-edge canvas's node key.</summary>
    public const string BleedKey = "npv-bleed";

    /// <summary>Under this, a view is drawn over the edge-to-edge canvas (the hero's lyrics peek while the canvas fills the panel): it paints
    /// with <c>new CanvasInk(true)</c>.</summary>
    public static readonly Context<bool> OverCanvas = new(false);

    /// <summary>The colour the head's scrim over the canvas darkens toward (and the frosted chip's plate): the panel's <paramref name="ground"/>
    /// in the dark kind, the media stage in the light kind (a light ground fading over a dark video reads as a grey haze).</summary>
    public static ColorF BleedShade(ColorF ground) => R.IsLight ? Tok.MediaStage : ground;

    // ── the light kind's foot (the file header) ──

    /// <summary>In the light kind, how far the canvas's foot dissolves into the panel's light ground (DIP): the copy over the canvas ends this
    /// far above the foot.</summary>
    public const float LightFoot = 128f;

    /// <summary>The light kind's veil behind the copy: black, ramping in over <see cref="LightVeilLead"/> DIP above the copy's top to
    /// <see cref="LightVeilA"/> (the floor: an average frame reads under it), and the boost <see cref="MediaScrim"/> solves on the canvas's poster
    /// over that floor, capped at <see cref="LightBoostCeiling"/> (the two together stay well below black: at most 1 - 0.58 x 0.55 = 68 %).</summary>
    public const float LightVeilLead = 96f, LightVeilA = 0.42f, LightBoostCeiling = 0.45f;

    /// <summary>The ground the copy over the canvas sits on in the light kind, for a colour solved against it (<see cref="CanvasInk.Accent"/>): the
    /// lightest grey the house scrim lets under the copy, i.e. the grey on which the on-media ink just meets body text's ratio with the solve's
    /// headroom (<see cref="MediaScrim.NeedBody"/> x <see cref="MediaScrim.Headroom"/>), so an accent that reads there reads wherever the white
    /// copy does.</summary>
    public static ColorF LightCopyGround
    {
        get
        {
            ColorF ink = R.OnMedia.Ink;
            float need = MediaScrim.NeedBody * MediaScrim.Headroom, lo = 0f, hi = 1f;
            for (int i = 0; i < 20; i++)
            {
                float mid = (lo + hi) / 2f;
                if (MediaScrim.Contrast(ink, mid) >= need) lo = mid; else hi = mid;
            }
            return new ColorF(lo, lo, lo, 1f);
        }
    }

    /// <summary>The foot's dissolve into the ground, eased in and out (smoothstep traced at the thirds: the engine's gradients take four stops),
    /// so the canvas never ends on a line.</summary>
    static readonly (float At, float A)[] FootEase = [(0f, 0f), (1f / 3f, 0.26f), (2f / 3f, 0.74f), (1f, 1f)];

    /// <summary>Where the copy over a canvas <paramref name="vh"/> tall ends in a panel <paramref name="panelHeight"/> tall (0: not measured yet):
    /// the canvas's foot in the dark kind, <see cref="LightFoot"/> above it in the light kind, never below the panel's foot. A theme puts its
    /// meta's top its own lift above this.</summary>
    public static float CopyFoot(float vh, float panelHeight)
        => MathF.Min(vh - (R.IsLight ? LightFoot : 0f), panelHeight > 0f ? panelHeight : vh);

    /// <summary>The light kind's layers over the canvas (the file header): the veil behind the copy (whose top sits <paramref name="copyLift"/>
    /// above <see cref="CopyFoot"/>) and the foot's dissolve into the panel's <paramref name="ground"/>. A ZStack layer at the panel's top, after
    /// the canvas and before the head's scrim; a component, so the panel's height and the poster's measurement re-render only it.</summary>
    public static Element LightScope(NowPlayingHeroProps np, float panelWidth, float copyLift, ColorF ground)
        => Embed.Comp(new LightScopeProps(np, panelWidth, copyLift, ground), static () => new LightScopeView());

    sealed record LightScopeProps(NowPlayingHeroProps NowPlaying, float PanelWidth, float CopyLift, ColorF Ground);

    sealed class LightScopeView : Component
    {
        public override Element Render()
        {
            var p = UseProps<LightScopeProps>();
            float w = p.PanelWidth, vh = BleedHeight(w);
            float copyFoot = CopyFoot(vh, p.NowPlaying.RailHeight.Value), copyTop = MathF.Max(0f, copyFoot - p.CopyLift);
            float lead = MathF.Min(LightVeilLead, copyTop), veilTop = copyTop - lead, veilH = MathF.Max(1f, vh - veilTop);
            var floor = new MediaScrim.Floor(veilTop, lead, LightVeilA);
            ColorF black = ColorF.FromRgba(0, 0, 0);   // the veil MediaScrim solves (its blend is black over the frame)
            var fade = new GradientStop[FootEase.Length];
            for (int i = 0; i < fade.Length; i++) fade[i] = new GradientStop(FootEase[i].At, p.Ground with { A = FootEase[i].A });
            float foot = MathF.Min(LightFoot, vh);
            return new BoxEl
            {
                AlignSelf = FlexAlign.Start, Height = vh, Shrink = 0f, ZStack = true, HitTestVisible = false,
                Children =
                [
                    new BoxEl
                    {
                        AlignSelf = FlexAlign.Start, Margin = new Edges4(0f, veilTop, 0f, 0f), Height = veilH,
                        Gradient = Ui.GradientDown(new GradientStop(0f, black with { A = 0f }), new GradientStop(lead / veilH, black with { A = LightVeilA }),
                            new GradientStop(1f, black with { A = LightVeilA })),
                    },
                    new BoxEl
                    {
                        AlignSelf = FlexAlign.Start, HitTestVisible = false,
                        Children =
                        [
                            Controls.ScrimBoost(new Controls.ScrimBoostProps(CanvasVideo.Current.Value.Poster, w, vh,
                                [new MediaScrim.Band(0f, copyTop, w, copyFoot, MediaScrim.NeedBody)], floor, LightBoostCeiling, copyTop)),
                        ],
                    },
                    new BoxEl { AlignSelf = FlexAlign.Start, Margin = new Edges4(0f, vh - foot, 0f, 0f), Height = foot, Gradient = Ui.GradientDown(fade) },
                ],
            };
        }
    }

    /// <summary>Does the canvas fill the panel now: the Canvas style stored, the picker shut, a canvas to play and the setting on? Reactive.</summary>
    public static bool Bleeds()
        => Rail.PlayerPrefs.Style() == Rail.PlayerCatalog.Canvas && !Shell.StylePicker.Open.Value
           && CanvasVideo.Current.Value.Shows && CanvasVideo.Enabled();

    /// <summary>The edge-to-edge canvas's height in a panel <paramref name="panelWidth"/> wide.</summary>
    public static float BleedHeight(float panelWidth) => MathF.Round(MathF.Max(0f, panelWidth) * CanvasAspect);

    /// <summary>The edge-to-edge canvas: a ZStack layer the panel's full width and <see cref="BleedHeight"/> tall at the panel's top, square,
    /// the canvas cropped to fill it (its poster first, then the video). <paramref name="panelWidth"/> answers until the layer has measured
    /// its own width; from then on a width change re-renders it and its height eases (Reflow), like the stage's.</summary>
    public static Element BleedVideo(float panelWidth) => Embed.Comp(new BleedProps(panelWidth), static () => new BleedView());

    sealed record BleedProps(float PanelWidth);

    sealed class BleedView : Component
    {
        readonly FloatSignal _width = new(float.NaN);
        Action<RectF>? _onBounds;

        public override Element Render()
        {
            var p = UseProps<BleedProps>();
            _onBounds ??= r => { if (!(MathF.Abs(_width.Peek() - r.W) <= 0.25f)) _width.Value = r.W; };
            float w = _width.Value;
            float width = float.IsFinite(w) && w > 0f ? w : p.PanelWidth;
            // The root fills the panel's stack (it measures the width); a column at its top carries the canvas, whose height eases as a flex child.
            return new BoxEl
            {
                AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f,
                ZStack = true, HitTestVisible = false, OnBoundsChanged = _onBounds,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                        Children =
                        [
                            new BoxEl
                            {
                                Key = BleedKey, Height = BleedHeight(width), Shrink = 0f, ZStack = true, ClipToBounds = true, HitTestVisible = false,
                                Animate = Design.Reduced ? null : StageHost.HeightMotion,
                                Children = [CanvasVideo.Layer(CanvasVideo.Surface.Bleed)],
                            },
                        ],
                    },
                ],
            };
        }
    }

    /// <summary>A slow horizontal drift for the ambient layers over the edge-to-edge canvas (Zune's giant artist name, the Classic mini player's visualizer lines): the
    /// child moves <paramref name="travel"/> DIP left over <paramref name="periodMs"/>, then back (<paramref name="pingPong"/>) or on round a
    /// seamless loop (a child that repeats every <paramref name="travel"/> DIP). The motion is one engine track per leg (render-thread motion: no
    /// UI frame and no allocation while it runs), re-armed each period while the layer is active; reduced motion holds it still.</summary>
    public static Element Drift(Element child, float travel, float periodMs, bool pingPong)
        => Embed.Comp(new DriftProps(child, travel, periodMs, pingPong), static () => new DriftView());

    sealed record DriftProps(Element Child, float Travel, float PeriodMs, bool PingPong);

    sealed class DriftView : Component
    {
        DriftProps _p = null!;
        NodeHandle _node;
        bool _back;
        Action<NodeHandle>? _realized;
        Action? _leg;

        public override Element Render()
        {
            _p = UseProps<DriftProps>();
            var active = UseIsActive();
            bool moves = active.Value && !Design.Reduced && _p.Travel > 0f && _p.PeriodMs > 0f;
            _leg ??= Leg;
            _realized ??= h => { _node = h; _leg(); };
            UseInterval(_leg, _p.PeriodMs, enabled: moves);
            return new BoxEl { Direction = 1, AlignSelf = FlexAlign.Start, OnRealized = _realized, HitTestVisible = false, Children = [_p.Child] };
        }

        /// <summary>One leg from where the child is drawn: the loop restarts a whole travel back (the same picture), the ping-pong turns.</summary>
        void Leg()
        {
            if (Context.Anim is not { } anim || Context.Scene is not { } scene || _node.IsNull || !scene.IsLive(_node)) return;
            if (Design.Reduced) return;
            float t = _p.Travel, cur = scene.Paint(_node).LocalTransform.Dx;
            if (_p.PingPong)
            {
                float to = _back ? 0f : -t;
                _back = !_back;
                float ms = _p.PeriodMs * MathF.Max(0.05f, MathF.Abs(to - cur) / t);
                anim.Animate(_node, AnimChannel.TranslateX, cur, to, ms, Easing.Sine);
                return;
            }
            float from = cur <= -0.5f * t ? cur + t : cur;
            anim.Animate(_node, AnimChannel.TranslateX, from, from - t, _p.PeriodMs, Easing.Linear);
        }
    }
}
