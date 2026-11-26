// ── Themes/Common/CommonMixTimeline.cs — the Mix editor's timeline, shared by every theme (#48 M6, full-support plan §2.5) ──────────────
//
// Both songs of a pair on one time axis around the join, each in its own lane (the outgoing song above, the incoming below), drawn in the
// calling theme's look (`MixTimelineLook`):
//
//   ┌ A ─────────────────────────╥▒▒▒▒▒▒▒▒▒▒▒▒▒▒║░░░░░░░░░░░┐   waveform, beat ticks (downbeats taller), the transition band, A's
//   │  ▁▃▅▇▅▃▁▃▅▇▅▃▁▃▅▇▅▃▁▃▅▇▅▃▁ ║ ‾‾‾‾‾‾‾╲___  ║ (A is over)│   volume curve over it (and the filter's cutoff, dashed), the
//   └─────────────────────────────╨──────────────║───────────┘   bend handle at its middle, A dimmed after the band
//   ┌ B ░░░░░(before its in-point)╥ ___╱‾‾‾‾‾‾‾  ║ ▁▃▅▇▅▃▁▃▅▇┐   B from its in-point at the band's start (at its beatmatch speed),
//   └─────────────────────────────╨──────────────║───────────┘   dimmed before it, its volume curve over its own window
//                                                ↑ the band's end grip (the overlap)
//
// Drag the A lane: the out-point moves (A slides under the band). Drag the B lane: the in-point moves. Drag the grip: the overlap. Drag a
// handle up or down: that side's volume curve bends (a hand-drawn curve, recipe 2.6 / 2.7). Every drag snaps live through the model's snap
// verbs (beats, bar phase, bars) and commits once, on release. While a drag runs only bound transforms and one readout move (FloatSignals
// and a string set when the snapped value changes): nothing re-renders until the commit, and the geometry is rebuilt only when the width or
// the model changes.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Render;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using TL = Wavee.Themes.Common.CommonMixTimelineRules;

namespace Wavee.Themes.Common;

public sealed record MixTimelineProps(MixTimelineModel Model, MixTimelineLook Look);

public static class MixTimelineView
{
    /// <summary>The timeline of <paramref name="model"/> in <paramref name="look"/>.</summary>
    public static Element Create(MixTimelineModel model, MixTimelineLook look)
        => Embed.Comp(new MixTimelineProps(model, look), static () => new MixTimeline()) with { Key = "mix:tl" };
}

internal sealed class MixTimeline : Component
{
    enum Gesture : byte { None, Out, In, Length, BendOut, BendIn }

    Gesture _gesture;
    float _downX, _downY;
    long _snapped;
    MixTimelineModel? _m;
    float _laneH, _pxPerMs = 1f;

    FloatSignal? _shiftA, _shiftB, _grow, _bendDy;
    Signal<string>? _tip;
    Signal<int>? _tipLane;   // 0 none, 1 lane A, 2 lane B

    Action<Point2>? _downA, _downB, _downGrip, _downBendOut, _downBendIn, _move;
    Action<PointerEventArgs>? _up;
    Action? _cancel;

    (float W, MixTimelineModel? M, float LaneH) _geomKey;
    PathData? _waveA, _waveB, _beatsA, _beatsB, _downbeatsA, _downbeatsB, _curveOut, _curveIn, _cutOut, _cutIn;

    public override Element Render()
    {
        var p = UseProps<MixTimelineProps>();
        var width = UseMeasuredWidth(TL.WidthKey, 1f);
        _shiftA = UseFloatSignal(0f);
        _shiftB = UseFloatSignal(0f);
        _grow = UseFloatSignal(0f);
        _bendDy = UseFloatSignal(0f);
        _tip = UseSignal("");
        _tipLane = UseSignal(0);
        Wire();

        var m = p.Model;
        var look = p.Look;
        _m = m;
        _laneH = look.LaneHeight;
        float w = width.Value;
        float span = m.None ? TL.MinSpanMs : Math.Clamp(m.OverlapMs * TL.SpanPerOverlap, TL.MinSpanMs, TL.MaxSpanMs);
        float pxPerMs = w > 0f ? w / span : 0f;
        _pxPerMs = pxPerMs;
        double v0 = m.OutStartMs + m.OverlapMs / 2.0 - span / 2.0;
        float x0 = (float)((m.OutStartMs - v0) * pxPerMs);
        float bandW = m.OverlapMs * pxPerMs;
        float x1 = x0 + bandW;
        float inW = m.InWindowMs * pxPerMs;
        if (w > 0f) Geometry(m, w, look.LaneHeight, v0, pxPerMs, x0, bandW, inW);

        bool live = m.Editable && w > 0f;
        var corners = CornerRadius4.All(look.Corner);
        float h = look.LaneHeight;
        // The band and A's curve stretch from the band's start while the grip moves; B's curve too when its window is the overlap (beatmatched).
        var shiftA = _shiftA;
        var shiftB = _shiftB;
        var grow = _grow;
        var bendDy = _bendDy;
        bool bScales = m.SpeedB != 1f;
        Prop<Affine2D> bandScale = Prop.Of(() => Stretch(x0, bandW, grow.Value));
        Prop<Affine2D> inScale = bScales ? bandScale : (Prop<Affine2D>)Affine2D.Identity;
        int lane = 0;

        Element Lane(bool incoming)
        {
            lane++;
            int me = lane;
            var kids = new List<Element>(10);
            // The transition band (both lanes), behind the waveform.
            if (!m.None)
                kids.Add(new BoxEl
                {
                    JustifySelf = FlexAlign.Start, Width = MathF.Max(0f, bandW), Fill = look.Band, HitTestVisible = false,
                    Transform = Prop.Of(() => Placed(x0, bandW, grow.Value)),
                });
            var wave = incoming ? _waveB : _waveA;
            var beats = incoming ? _beatsB : _beatsA;
            var downs = incoming ? _downbeatsB : _downbeatsA;
            var content = new List<Element>(3);
            if (wave is not null) content.Add(new PathEl { Geometry = wave, Width = w, Height = h, StrokeColor = incoming ? look.WaveB : look.WaveA, Stroke = new StrokeStyle(TL.WaveStep * TL.WaveBar, LineCap.Butt) });
            if (beats is not null) content.Add(new PathEl { Geometry = beats, Width = w, Height = h, StrokeColor = look.Tick, Stroke = new StrokeStyle(TL.TickWidth, LineCap.Butt) });
            if (downs is not null) content.Add(new PathEl { Geometry = downs, Width = w, Height = h, StrokeColor = look.Downbeat, Stroke = new StrokeStyle(TL.TickWidth, LineCap.Butt) });
            var shift = incoming ? shiftB : shiftA;
            kids.Add(new BoxEl
            {
                ZStack = true, HitTestVisible = false, Transform = Prop.Of(() => Affine2D.Translation(shift.Value, 0f)), Children = content.ToArray(),
            });
            // A is over after the band; B is not heard before its in-point.
            if (incoming) kids.Add(new BoxEl { JustifySelf = FlexAlign.Start, Width = MathF.Max(0f, x0), Fill = look.Dim, HitTestVisible = false });
            else kids.Add(new BoxEl { JustifySelf = FlexAlign.Start, Width = MathF.Max(0f, w - x1), OffsetX = x1, Fill = look.Dim, HitTestVisible = false });
            // The curves over the band (B's over its own window).
            var curve = incoming ? _curveIn : _curveOut;
            var cut = incoming ? _cutIn : _cutOut;
            var curves = new List<Element>(2);
            if (cut is not null)
                curves.Add(new PathEl { Geometry = cut, Width = w, Height = h, StrokeColor = look.Cutoff, Stroke = new StrokeStyle(look.Stroke, LineCap.Round, LineJoin.Round, 4f, look.Stroke * 3f, look.Stroke * 2f) });
            if (curve is not null)
                curves.Add(new PathEl { Geometry = curve, Width = w, Height = h, StrokeColor = incoming ? look.CurveIn : look.CurveOut, Stroke = new StrokeStyle(look.Stroke) });
            if (curves.Count > 0)
                kids.Add(new BoxEl { ZStack = true, HitTestVisible = false, Transform = incoming ? inScale : bandScale, Children = curves.ToArray() });
            // The song's title, or the readout while this lane's point is dragged.
            string label = incoming ? m.LabelB : m.LabelA;
            var tip = _tip!;
            var tipLane = _tipLane!;
            // A stack layer at Start fills the lane, so the plate hugs its words inside a row that does.
            kids.Add(new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Start, HitTestVisible = false, MinWidth = 0f,
                Padding = new Edges4(look.LabelInset, look.LabelInset * 0.5f, look.LabelInset, 0f),
                Children =
                [
                    new BoxEl
                    {
                        Shrink = 1f, MinWidth = 0f, Padding = new Edges4(look.LabelPadX, 0f, look.LabelPadX, 0f), Fill = look.LabelPlate,
                        Corners = CornerRadius4.All(look.Corner),
                        Children =
                        [
                            Ui.Caption("") with
                            {
                                Text = Prop.Of(() => tipLane.Value == me ? tip.Value : label), Color = look.Label, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                                Shrink = 1f, MinWidth = 0f,
                            },
                        ],
                    },
                ],
            });
            // The bend handle at the curve's middle.
            if (live && curve is not null)
            {
                float[] values = incoming ? m.VolumeIn : m.VolumeOut;
                float mid = values.Length > 0 ? values[values.Length / 2] : 0.5f;
                float cx = x0 + (incoming ? inW : bandW) * 0.5f;
                float cy = TL.CurveInset + (1f - mid) * (h - 2f * TL.CurveInset);
                float hs = look.HandleSize, hit = MathF.Max(look.HandleHit, hs);
                bool bendOut = !incoming;
                // The hit box stays inside the lane (the lane clips): the dot moves within it when the curve's middle is near an edge.
                float top = Math.Clamp(cy - hit * 0.5f, 0f, MathF.Max(0f, h - hit));
                kids.Add(new BoxEl
                {
                    Key = incoming ? "mix:tl:bend:in" : "mix:tl:bend:out",
                    JustifySelf = FlexAlign.Start, AlignSelf = FlexAlign.Start, Width = hit, Height = hit,
                    Fill = ColorF.Transparent, HoverFill = ColorF.Transparent, PressedFill = ColorF.Transparent, Cursor = CursorId.SizeNS, Role = AutomationRole.Slider,
                    Focusable = true,
                    // The whole-matrix channel carries the placement too (it replaces OffsetX / OffsetY).
                    Transform = Prop.Of(() => Affine2D.Translation(cx - hit * 0.5f, top + (_gesture == (bendOut ? Gesture.BendOut : Gesture.BendIn) ? bendDy.Value : 0f))),
                    OnPointerDown = incoming ? _downBendIn : _downBendOut, OnDrag = _move, OnPointerReleased = _up, OnDragCanceled = _cancel,
                    Children =
                    [
                        new BoxEl
                        {
                            Width = hs, Height = hs, Shrink = 0f, Margin = new Edges4((hit - hs) * 0.5f, cy - top - hs * 0.5f, 0f, 0f),
                            Corners = CornerRadius4.All(hs * 0.5f), Fill = look.Handle, BorderWidth = look.Stroke, BorderColor = look.HandleRing, HitTestVisible = false,
                        },
                    ],
                });
            }
            return new BoxEl
            {
                Key = incoming ? "mix:tl:b" : "mix:tl:a", Height = h, Shrink = 0f, MinWidth = 0f, ZStack = true, ClipToBounds = true,
                // A pressable box auto-tints its fill on hover and press; a lane is a drag surface, so it keeps its own.
                Fill = look.LaneFill, HoverFill = look.LaneFill, PressedFill = look.LaneFill, Corners = corners, Role = AutomationRole.Slider,
                Cursor = live ? CursorId.SizeWE : null,
                OnPointerDown = live ? incoming ? _downB : _downA : null,
                OnDrag = live ? _move : null,
                OnPointerReleased = live ? _up : null,
                OnDragCanceled = live ? _cancel : null,
                Children = kids.ToArray(),
            };
        }

        var lanes = new BoxEl
        {
            Key = "mix:tl:lanes", Direction = 1, Gap = look.LaneGap, MinWidth = 0f,
            Children = [Lane(false), Lane(true)],
        };
        var layers = new List<Element>(2) { lanes };
        if (live)
        {
            // The band's end grip across both lanes (its visible edge centred in a wider hit box).
            float gw = look.GripWidth;
            layers.Add(new BoxEl
            {
                Key = "mix:tl:len", JustifySelf = FlexAlign.Start, Width = gw, Cursor = CursorId.SizeWE, Role = AutomationRole.Slider,
                AlignItems = FlexAlign.Center, Fill = ColorF.Transparent, HoverFill = ColorF.Transparent, PressedFill = ColorF.Transparent,
                Transform = Prop.Of(() => Affine2D.Translation(x1 - gw * 0.5f + grow.Value, 0f)),
                OnPointerDown = _downGrip, OnDrag = _move, OnPointerReleased = _up, OnDragCanceled = _cancel,
                Children = [new BoxEl { Width = look.Stroke, Grow = 1f, Fill = look.BandEdge, HitTestVisible = false }],
            });
        }
        return new BoxEl { Key = "mix:tl:root", ZStack = true, MinWidth = 0f, Children = layers.ToArray() };
    }

    /// <summary>The band's box (laid out at the lane's start, <paramref name="bandW"/> wide) placed at <paramref name="x0"/> and stretched by the grip's
    /// <paramref name="grow"/> DIP.</summary>
    static Affine2D Placed(float x0, float bandW, float grow)
        => new(bandW > 0f && grow != 0f ? MathF.Max(0.05f, (bandW + grow) / bandW) : 1f, 0f, 0f, 1f, x0, 0f);

    /// <summary>The band's stretch while the grip moves: x0 stays, the band grows by <paramref name="grow"/> DIP.</summary>
    static Affine2D Stretch(float x0, float bandW, float grow)
    {
        if (grow == 0f || bandW <= 0f) return Affine2D.Identity;
        float k = MathF.Max(0.05f, (bandW + grow) / bandW);
        return new Affine2D(k, 0f, 0f, 1f, x0 - k * x0, 0f);
    }

    // ── the gestures ───────────────────────────────────────────────────────────────────────────────────────────────────

    void Wire()
    {
        if (_move is not null) return;
        _downA = p => Begin(Gesture.Out, p);
        _downB = p => Begin(Gesture.In, p);
        _downGrip = p => Begin(Gesture.Length, p);
        _downBendOut = p => Begin(Gesture.BendOut, p);
        _downBendIn = p => Begin(Gesture.BendIn, p);
        _move = Move;
        _up = _ => Commit();
        _cancel = Reset;
    }

    void Begin(Gesture g, Point2 p)
    {
        if (_gesture != Gesture.None) Commit();   // a release the engine never reported: the last drag lands first
        if (_m is not { Editable: true } m || _pxPerMs <= 0f) return;
        _gesture = g;
        _downX = p.X;
        _downY = p.Y;
        _snapped = g switch { Gesture.Out => m.OutStartMs, Gesture.In => m.InStartMs, Gesture.Length => m.OverlapMs, _ => 0 };
    }

    /// <summary>A pointer move: the dragged value snapped, shown by a bound transform and the lane's readout (set only when it changes).</summary>
    void Move(Point2 p)
    {
        if (_m is not { } m || _gesture == Gesture.None) return;
        // The drag's local position: the lanes and the grip report in their own box, and the grip's box moves with the drag itself.
        float dx = p.X - _downX + (_gesture == Gesture.Length ? _grow!.Peek() : 0f);
        float dy = p.Y - _downY + (_gesture is Gesture.BendOut or Gesture.BendIn ? _bendDy!.Peek() : 0f);
        long snapped;
        switch (_gesture)
        {
            case Gesture.Out:
                snapped = m.SnapOut(m.OutStartMs - (long)Math.Round(dx / _pxPerMs));
                _shiftA!.Value = -(snapped - m.OutStartMs) * _pxPerMs;
                Show(1, snapped, m.FormatOut);
                break;
            case Gesture.In:
                snapped = m.SnapIn(m.InStartMs - (long)Math.Round(dx / _pxPerMs * m.SpeedB));
                _shiftB!.Value = -(snapped - m.InStartMs) / m.SpeedB * _pxPerMs;
                Show(2, snapped, m.FormatIn);
                break;
            case Gesture.Length:
                snapped = m.SnapOverlap(m.OverlapMs + (long)Math.Round(dx / _pxPerMs));
                _grow!.Value = (snapped - m.OverlapMs) * _pxPerMs;
                Show(1, snapped, m.FormatOverlap);
                break;
            default:
                // A handle stays inside its lane.
                float half = _laneH * 0.5f;
                _bendDy!.Value = Math.Clamp(dy, -half, half);
                break;
        }
    }

    void Show(int lane, long snapped, Func<long, string> format)
    {
        if (snapped == _snapped && _tipLane!.Peek() == lane) return;
        _snapped = snapped;
        _tip!.Value = format(snapped);
        _tipLane!.Value = lane;
    }

    /// <summary>The release: the snapped value goes to the editor (one draft change), then the drag's transforms go back to rest; the editor's
    /// new model draws the same picture at rest in the same frame.</summary>
    void Commit()
    {
        var g = _gesture;
        var m = _m;
        if (g == Gesture.None || m is null) { Reset(); return; }
        _gesture = Gesture.None;
        switch (g)
        {
            case Gesture.Out: if (_snapped != m.OutStartMs) m.CommitOut(_snapped); break;
            case Gesture.In: if (_snapped != m.InStartMs) m.CommitIn(_snapped); break;
            case Gesture.Length: if (_snapped != m.OverlapMs) m.CommitOverlap(_snapped); break;
            default:
                bool incoming = g == Gesture.BendIn;
                float dy = _bendDy!.Peek();
                if (dy != 0f)
                {
                    float bend = Math.Clamp((incoming ? m.BendIn : m.BendOut) - dy / _laneH * TL.BendPerLane, -1f, 1f);
                    m.CommitBend(incoming, bend);
                }
                break;
        }
        Reset();
    }

    void Reset()
    {
        _gesture = Gesture.None;
        _shiftA!.Value = 0f;
        _shiftB!.Value = 0f;
        _grow!.Value = 0f;
        _bendDy!.Value = 0f;
        _tipLane!.Value = 0;
    }

    // ── the geometry (node-local DIP; rebuilt only when the width or the model changes) ──────────────────────────────────────

    void Geometry(MixTimelineModel m, float w, float h, double v0, float pxPerMs, float x0, float bandW, float inW)
    {
        if (_geomKey.W == w && ReferenceEquals(_geomKey.M, m) && _geomKey.LaneH == h) return;
        _geomKey = (w, m, h);
        // A's media time at x, and B's (from its in-point at the band's start, at its speed); drawn a width either side for the drags.
        double AtA(float x) => v0 + x / pxPerMs;
        double AtB(float x) => m.InStartMs + (x - x0) / pxPerMs * m.SpeedB;
        _waveA = Wave(m.LevelsA, m.StepMs, m.DurationA, AtA, w, h);
        _waveB = Wave(m.LevelsB, m.StepMs, m.DurationB, AtB, w, h);
        (_beatsA, _downbeatsA) = Ticks(m.BeatsA, m.DownA, ms => (float)((ms - v0) * pxPerMs), w, h);
        (_beatsB, _downbeatsB) = Ticks(m.BeatsB, m.DownB, ms => (float)(x0 + (ms - m.InStartMs) / m.SpeedB * pxPerMs), w, h);
        _curveOut = m.None ? null : Curve(m.VolumeOut, x0, bandW, h);
        _curveIn = m.None ? null : Curve(m.VolumeIn, x0, inW, h);
        _cutOut = m.None || m.CutoffOut is null ? null : Curve(m.CutoffOut, x0, bandW, h);
        _cutIn = m.None || m.CutoffIn is null ? null : Curve(m.CutoffIn, x0, inW, h);
    }

    /// <summary>A mirrored envelope around the lane's middle: a vertical stroke every <see cref="TL.WaveStep"/> DIP (a silent column draws nothing), from
    /// half a width before the lane to half a width after it, so a drag slides drawn waveform into view. Strokes, not a filled outline: the path
    /// realizer gave up on long filled outlines whose columns repeat a level.</summary>
    static PathData? Wave(float[] levels, int stepMs, long duration, Func<float, double> msAt, float w, float h)
    {
        if (levels.Length == 0 || stepMs <= 0) return null;
        float mid = h * 0.5f, depth = mid * TL.WaveDepth;
        float from = -w * TL.WaveReach;
        int cols = (int)(w * (1f + 2f * TL.WaveReach) / TL.WaveStep) + 1;
        var b = new PathBuilder();
        bool any = false;
        for (int i = 0; i < cols; i++)
        {
            float x = from + i * TL.WaveStep;
            double ms = msAt(x);
            if (ms < 0 || ms >= duration) continue;
            int at = (int)(ms / stepMs);
            float half = at < levels.Length ? levels[at] * depth : 0f;
            if (half < TL.WaveFloor) continue;
            b.MoveTo(x, mid - half);
            b.LineTo(x, mid + half);
            any = true;
        }
        return any ? b.Finish(PathContentEpoch.Mint(), FillRule.NonZero) : null;
    }

    /// <summary>The beat ticks along the lane's foot (a downbeat the lane's full height), as two stroked paths.</summary>
    static (PathData? Beats, PathData? Downbeats) Ticks(uint[] beats, bool[] down, Func<long, float> xAt, float w, float h)
    {
        if (beats.Length == 0) return (null, null);
        var b = new PathBuilder();
        var d = new PathBuilder();
        bool anyB = false, anyD = false;
        float tick = h * TL.TickShare;
        for (int i = 0; i < beats.Length; i++)
        {
            float x = xAt(beats[i]);
            if (x < -w || x > 2f * w) continue;
            if (i < down.Length && down[i]) { d.MoveTo(x, 0f); d.LineTo(x, h); anyD = true; }
            else { b.MoveTo(x, h - tick); b.LineTo(x, h); anyB = true; }
        }
        return (anyB ? b.Finish(PathContentEpoch.Mint(), FillRule.NonZero) : null, anyD ? d.Finish(PathContentEpoch.Mint(), FillRule.NonZero) : null);
    }

    /// <summary>A curve's samples (0..1, evenly over its window) as a polyline from <paramref name="x0"/> over <paramref name="width"/> DIP.</summary>
    static PathData? Curve(float[] values, float x0, float width, float h)
    {
        if (values.Length < 2 || width <= 0f) return null;
        float top = TL.CurveInset, range = h - 2f * TL.CurveInset;
        var b = new PathBuilder();
        for (int i = 0; i < values.Length; i++)
        {
            float x = x0 + width * i / (values.Length - 1);
            float y = top + (1f - Math.Clamp(values[i], 0f, 1f)) * range;
            if (i == 0) b.MoveTo(x, y); else b.LineTo(x, y);
        }
        return b.Finish(PathContentEpoch.Mint(), FillRule.NonZero);
    }
}
