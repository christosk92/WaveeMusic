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

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace Wavee;

public static partial class Deck
{
    /// <summary>The edge-to-edge canvas's node key.</summary>
    public const string BleedKey = "npv-bleed";

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
