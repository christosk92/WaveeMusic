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
// THE MEDIA SCOPE (the owner, 2026-11-08: "in light mode the canvas looks a bit ugly"). Over the canvas the panel is a dark media surface in
// BOTH kinds, as over a photograph (`MediaScrim`'s rule: light on-media ink over a darkening veil). The scrims over the canvas darken toward
// `BleedShade` (the panel's ground in the dark kind; the media stage in the light kind, so a light ground never fades over a dark video into
// a grey haze), what is drawn over the canvas paints with `CanvasInk` (the on-media ladder in the light kind; the theme's own ink in the dark
// kind, which is already light), and the light ground returns only below the canvas's foot, through a short ramp (each theme's rule). A view
// the core mounts over the canvas (the lyrics peek) reads `OverCanvas`.

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>The ink of what is drawn over the edge-to-edge canvas (<paramref name="Over"/>) or on the panel's ground: a MODE, like
/// <see cref="Lyrics.Ink"/>, resolved where the element is built, so a theme switch re-reads it. Over the canvas in the light kind it is the
/// on-media ladder (light ink; the accent solved as light text on the media stage); everywhere else, and in the dark kind, the theme's own.</summary>
public readonly record struct CanvasInk(bool Over)
{
    bool Media => Over && R.IsLight;

    /// <summary>Titles, the selected tab, glyphs.</summary>
    public ColorF Primary => Media ? R.OnMedia.Ink : Tok.TextPrimary;
    /// <summary>Subtitles, the other tabs, a chevron.</summary>
    public ColorF Secondary => Media ? R.OnMedia.InkSecondary : Tok.TextSecondary;
    /// <summary>Captions.</summary>
    public ColorF Tertiary => Media ? R.OnMedia.InkTertiary : Tok.TextTertiary;
    /// <summary>Accent TEXT (a link, the album): over the media stage, the first light rung of the accent's own ramp that reads on it
    /// (<see cref="ColorScience.AccentFloor"/>, as the dark kind's accent text is a light rung).</summary>
    public ColorF Accent => Media ? ColorScience.AccentFloor(Tok.AccentTextPrimary, ThemeKind.Dark, Tok.MediaStage, R.OnMedia.Ink) : Tok.AccentTextPrimary;
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

    /// <summary>The colour the scrims over the canvas darken toward: the panel's <paramref name="ground"/> in the dark kind, the media stage in
    /// the light kind (a light ground fading over a dark video reads as a grey haze).</summary>
    public static ColorF BleedShade(ColorF ground) => R.IsLight ? Tok.MediaStage : ground;

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

    /// <summary>A slow horizontal drift for the ambient layers over the edge-to-edge canvas (Zune's giant artist name, Classic's waves): the
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
