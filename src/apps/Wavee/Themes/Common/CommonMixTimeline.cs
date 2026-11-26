// ── Themes/Common/CommonMixTimeline.cs — the Mix editor's timeline, shared by every theme (#48 M6, full-support plan §2.5) ──────────────
//
// Both songs of a pair on one time axis around the join, stacked as Spotify's editor stacks them (the outgoing song above, the incoming
// below), drawn in the calling theme's look (`MixTimelineLook`):
//
//   ┌ A ─────────────────────────╥▒▒▒▒▒▒▒▒▒▒▒▒▒▒║░░░░░░░░░░░┐   waveform, beat ticks (downbeats taller), the transition band, A's
//   │  ▁▃▅▇▅▃▁▃▅▇▅▃▁▃▅▇▅▃▁▃▅▇▅▃▁ ║ ‾‾‾‾‾‾‾╲___  ║ (A is over)│   volume curve over it (and the filter's cutoff, dashed), the
//   └─────────────────────────────╨──────[▶]─────║───────────┘   bend handle at its middle, A dimmed after the band; the theme's
//   ┌ B ░░░░░(before its in-point)╥ ___╱‾‾‾‾‾‾‾  ║ ▁▃▅▇▅▃▁▃▅▇┐   Hear it button between the lanes; B from its in-point at the band's
//   └─────────────────────────────╨──────────────║───────────┘   start (at its beatmatch speed), dimmed before it
//                                                ↑ the band's end grip (the overlap)
//
// Full-page, an overview strip heads it: both songs whole on one axis (A, then B from the join), the transition marked.
//
// Drag the A lane: the out-point moves (A slides under the band). Drag the B lane: the in-point moves. Drag the grip: the overlap. Drag a
// handle up or down: that side's volume curve bends (a hand-drawn curve, recipe 2.6 / 2.7). Every drag snaps through the model's snap verbs
// (beats, bar phase, bars) and the PICTURE FOLLOWS LIVE: on each pointer move whose snapped value (or quantized bend) changed, the model's
// `Preview` hands back the picture with that value in place (the same math the commit runs) and this component redraws from it, band,
// waveforms, curves and the editor's numbers; the release commits it once. The band starts at a fixed share of the width, so an overlap
// that changes moves only the band's end; while the grip moves the view holds its scale too (an overlap past the 12 s view re-fits on the
// release).

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Render;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using TL = Wavee.Themes.Common.CommonMixTimelineRules;

namespace Wavee.Themes.Common;

/// <summary>The timeline's props: the model, the theme's look, the theme's Hear it button (between the lanes; null: none) and whether the
/// overview strip heads it (the full-page editor).</summary>
public sealed record MixTimelineProps(MixTimelineModel Model, MixTimelineLook Look, Element? Centre = null, bool Overview = false);

public static class MixTimelineView
{
    /// <summary>The timeline of <paramref name="model"/> in <paramref name="look"/>, with <paramref name="centre"/> between its lanes and, when
    /// <paramref name="overview"/>, the whole of both songs above it.</summary>
    public static Element Create(MixTimelineModel model, MixTimelineLook look, Element? centre = null, bool overview = false)
        => Embed.Comp(new MixTimelineProps(model, look, centre, overview), static () => new MixTimeline()) with { Key = "mix:tl" };
}

internal sealed class MixTimeline : Component
{
    enum Gesture : byte { None, Out, In, Length, BendOut, BendIn }

    Gesture _gesture;
    float _downX, _downY;
    long _snapped;
    float _bend0, _bendQ;
    /// <summary>The model the gesture started on (its commit compares with it; the bend handle's hit box stays where it was pressed).</summary>
    MixTimelineModel? _m, _start;
    float _laneH, _pxPerMs = 1f;
    /// <summary>The view the grip's drag holds (the span's start in A's time and the scale), so the band's end stays under the pointer.</summary>
    double _heldV0;
    float _heldPx;
    /// <summary>The grip's box x now and at the press (its local pointer coordinates move with it).</summary>
    float _gripX, _gripX0;
    /// <summary>The bend handle's hit box at the press.</summary>
    float _bendX0, _bendTop0;

    Signal<MixTimelineModel?>? _live;
    FloatSignal? _bendDy;

    Action<Point2>? _downA, _downB, _downGrip, _downBendOut, _downBendIn, _move;
    Action<PointerEventArgs>? _up;
    Action? _cancel;

    (float W, MixTimelineModel? M, float LaneH, double V0, float Px) _geomKey;
    PathData? _waveA, _waveB, _beatsA, _beatsB, _downbeatsA, _downbeatsB, _curveOut, _curveIn, _cutOut, _cutIn, _overA, _overB;

    public override Element Render()
    {
        var p = UseProps<MixTimelineProps>();
        var width = UseMeasuredWidth(TL.WidthKey, 1f);
        _live = UseSignal<MixTimelineModel?>(null);
        _bendDy = UseFloatSignal(0f);
        Wire();

        // A drag shows its preview; a new model from the editor (the commit, an undo, another preset) ends any stale one.
        var live = _live.Value;
        if (live is not null && _gesture == Gesture.None) live = null;
        var m = live ?? p.Model;
        if (_gesture == Gesture.None) _start = null;
        var look = p.Look;
        _m = m;
        _laneH = look.LaneHeight;
        float w = width.Value;
        float span = m.None ? TL.MinSpanMs : Math.Clamp(m.OverlapMs * TL.SpanPerOverlap, TL.MinSpanMs, TL.MaxSpanMs);
        float pxPerMs = w > 0f ? w / span : 0f;
        // The band starts at a fixed share of the width: a longer or shorter overlap grows its end and never slides the join.
        double v0 = m.OutStartMs - span * TL.BandLead;
        if (_gesture == Gesture.Length && _heldPx > 0f) { pxPerMs = _heldPx; v0 = _heldV0; }
        _pxPerMs = pxPerMs;
        float x0 = (float)((m.OutStartMs - v0) * pxPerMs);
        float bandW = m.OverlapMs * pxPerMs;
        float x1 = x0 + bandW;
        float inW = m.InWindowMs * pxPerMs;
        if (w > 0f) Geometry(m, w, look.LaneHeight, v0, pxPerMs, x0, bandW, inW);
        if (p.Overview && w > 0f) OverviewGeometry(m, w, look.LaneHeight * TL.OverviewShare);

        bool editable = m.Editable && w > 0f;
        var corners = CornerRadius4.All(look.Corner);
        float h = look.LaneHeight;
        var bendDy = _bendDy;

        Element Lane(bool incoming)
        {
            var kids = new List<Element>(9);
            // The transition band (both lanes), behind the waveform.
            if (!m.None)
                kids.Add(new BoxEl { JustifySelf = FlexAlign.Start, Width = MathF.Max(0f, bandW), OffsetX = x0, Fill = look.Band, HitTestVisible = false });
            var wave = incoming ? _waveB : _waveA;
            var beats = incoming ? _beatsB : _beatsA;
            var downs = incoming ? _downbeatsB : _downbeatsA;
            var content = new List<Element>(3);
            if (wave is not null) content.Add(new PathEl { Geometry = wave, Width = w, Height = h, StrokeColor = incoming ? look.WaveB : look.WaveA, Stroke = new StrokeStyle(TL.WaveStep * TL.WaveBar, LineCap.Butt) });
            if (beats is not null) content.Add(new PathEl { Geometry = beats, Width = w, Height = h, StrokeColor = look.Tick, Stroke = new StrokeStyle(TL.TickWidth, LineCap.Butt) });
            if (downs is not null) content.Add(new PathEl { Geometry = downs, Width = w, Height = h, StrokeColor = look.Downbeat, Stroke = new StrokeStyle(TL.TickWidth, LineCap.Butt) });
            if (content.Count > 0) kids.Add(new BoxEl { ZStack = true, HitTestVisible = false, Children = content.ToArray() });
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
            if (curves.Count > 0) kids.Add(new BoxEl { ZStack = true, HitTestVisible = false, Children = curves.ToArray() });
            // The song's title on a plate (a stack layer at Start fills the lane, so the plate hugs its words inside a row that does).
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
                        Children = [Ui.Caption(incoming ? m.LabelB : m.LabelA) with { Color = look.Label, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f }],
                    },
                ],
            });
            // The bend handle at the curve's middle: the dot rides the (live) curve, its hit box stays under the pointer while it is dragged.
            if (editable && curve is not null)
            {
                float[] values = incoming ? m.VolumeIn : m.VolumeOut;
                float mid = values.Length > 0 ? values[values.Length / 2] : 0.5f;
                float cx = x0 + (incoming ? inW : bandW) * 0.5f;
                float cy = TL.CurveInset + (1f - mid) * (h - 2f * TL.CurveInset);
                float hs = look.HandleSize, hit = MathF.Max(look.HandleHit, hs);
                var mine = incoming ? Gesture.BendIn : Gesture.BendOut;
                bool held = _gesture == mine;
                // The hit box stays inside the lane (the lane clips).
                float top = held ? _bendTop0 : Math.Clamp(cy - hit * 0.5f, 0f, MathF.Max(0f, h - hit));
                float left = held ? _bendX0 : cx - hit * 0.5f;
                kids.Add(new BoxEl
                {
                    Key = incoming ? "mix:tl:dot:in" : "mix:tl:dot:out", JustifySelf = FlexAlign.Start, AlignSelf = FlexAlign.Start, Width = hs, Height = hs,
                    OffsetX = cx - hs * 0.5f, OffsetY = cy - hs * 0.5f, Corners = CornerRadius4.All(hs * 0.5f), Fill = look.Handle,
                    BorderWidth = look.Stroke, BorderColor = look.HandleRing, HitTestVisible = false,
                });
                kids.Add(new BoxEl
                {
                    Key = incoming ? "mix:tl:bend:in" : "mix:tl:bend:out",
                    JustifySelf = FlexAlign.Start, AlignSelf = FlexAlign.Start, Width = hit, Height = hit,
                    Fill = ColorF.Transparent, HoverFill = ColorF.Transparent, PressedFill = ColorF.Transparent, Cursor = CursorId.SizeNS, Role = AutomationRole.Slider,
                    Focusable = true,
                    // The whole-matrix channel carries the placement too (it replaces OffsetX / OffsetY); the drag moves it with the pointer.
                    Transform = Prop.Of(() => Affine2D.Translation(left, top + (held ? bendDy.Value : 0f))),
                    OnPointerDown = incoming ? _downBendIn : _downBendOut, OnDrag = _move, OnPointerReleased = _up, OnDragCanceled = _cancel,
                });
                if (!held) { if (incoming) { _bendInX = left; _bendInTop = top; } else { _bendOutX = left; _bendOutTop = top; } }
            }
            return new BoxEl
            {
                Key = incoming ? "mix:tl:b" : "mix:tl:a", Height = h, Shrink = 0f, MinWidth = 0f, ZStack = true, ClipToBounds = true,
                // A pressable box auto-tints its fill on hover and press; a lane is a drag surface, so it keeps its own.
                Fill = look.LaneFill, HoverFill = look.LaneFill, PressedFill = look.LaneFill, Corners = corners, Role = AutomationRole.Slider,
                Cursor = editable ? CursorId.SizeWE : null,
                OnPointerDown = editable ? incoming ? _downB : _downA : null,
                OnDrag = editable ? _move : null,
                OnPointerReleased = editable ? _up : null,
                OnDragCanceled = editable ? _cancel : null,
                Children = kids.ToArray(),
            };
        }

        // Between the lanes: the band carried across (one window through both songs) and the theme's Hear it button.
        var gapKids = new List<Element>(2);
        if (!m.None) gapKids.Add(new BoxEl { JustifySelf = FlexAlign.Start, Width = MathF.Max(0f, bandW), OffsetX = x0, Fill = look.Band, HitTestVisible = false });
        var column = new List<Element>(4);
        if (p.Overview) column.Add(Overview(m, look, w));
        column.Add(Lane(false));
        column.Add(new BoxEl { Key = "mix:tl:gap", Height = look.CentreGap, Shrink = 0f, ZStack = true, MinWidth = 0f, Children = gapKids.ToArray() });
        column.Add(Lane(true));
        var lanes = new BoxEl { Key = "mix:tl:lanes", Direction = 1, Gap = look.LaneGap, MinWidth = 0f, Children = column.ToArray() };
        var layers = new List<Element>(2) { lanes };
        if (editable)
        {
            // The band's end grip across both lanes (its visible edge centred in a wider hit box).
            float gw = look.GripWidth;
            _gripX = x1 - gw * 0.5f;
            float overviewH = p.Overview ? look.LaneHeight * TL.OverviewShare + look.LaneGap : 0f;
            layers.Add(new BoxEl
            {
                Key = "mix:tl:len", JustifySelf = FlexAlign.Start, Width = gw, Cursor = CursorId.SizeWE, Role = AutomationRole.Slider,
                Margin = new Edges4(0f, overviewH, 0f, 0f),
                AlignItems = FlexAlign.Center, Fill = ColorF.Transparent, HoverFill = ColorF.Transparent, PressedFill = ColorF.Transparent,
                Transform = Affine2D.Translation(_gripX, 0f),
                OnPointerDown = _downGrip, OnDrag = _move, OnPointerReleased = _up, OnDragCanceled = _cancel,
                Children = [new BoxEl { Width = look.Stroke, Grow = 1f, Fill = look.BandEdge, HitTestVisible = false }],
            });
        }
        // The theme's Hear it button on top, in the room between the lanes: over the grip where they cross (the grip stays in reach on the lanes).
        if (p.Centre is { } centre)
        {
            float above = (p.Overview ? look.LaneHeight * TL.OverviewShare + look.LaneGap : 0f) + look.LaneHeight + look.LaneGap;
            layers.Add(new BoxEl
            {
                Key = "mix:tl:centre", AlignSelf = FlexAlign.Start, Height = look.CentreGap, Margin = new Edges4(0f, above, 0f, 0f),
                Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, MinWidth = 0f, Children = [centre],
            });
        }
        return new BoxEl { Key = "mix:tl:root", ZStack = true, MinWidth = 0f, Children = layers.ToArray() };
    }

    float _bendOutX, _bendOutTop, _bendInX, _bendInTop;

    /// <summary>The overview: both songs whole on one axis (A from its start, B from the join), A in the strip's upper half and B in its lower,
    /// the transition window marked.</summary>
    Element Overview(MixTimelineModel m, MixTimelineLook look, float w)
    {
        float h = look.LaneHeight * TL.OverviewShare;
        double total = OverviewTotal(m);
        float px = total > 0 && w > 0f ? (float)(w / total) : 0f;
        var kids = new List<Element>(4);
        if (!m.None)
            kids.Add(new BoxEl { JustifySelf = FlexAlign.Start, Width = MathF.Max(1f, m.OverlapMs * px), OffsetX = m.OutStartMs * px, Fill = look.Band, HitTestVisible = false });
        if (_overA is not null) kids.Add(new PathEl { Geometry = _overA, Width = w, Height = h, StrokeColor = look.WaveA, Stroke = new StrokeStyle(TL.OverviewStep * TL.WaveBar, LineCap.Butt) });
        if (_overB is not null) kids.Add(new PathEl { Geometry = _overB, Width = w, Height = h, StrokeColor = look.WaveB, Stroke = new StrokeStyle(TL.OverviewStep * TL.WaveBar, LineCap.Butt) });
        return new BoxEl
        {
            Key = "mix:tl:overview", Height = h, Shrink = 0f, MinWidth = 0f, ZStack = true, ClipToBounds = true, HitTestVisible = false,
            Fill = look.LaneFill, Corners = CornerRadius4.All(look.Corner), Children = kids.ToArray(),
        };
    }

    /// <summary>The overview's axis: from A's start to B's end, B entering at the out-point.</summary>
    static double OverviewTotal(MixTimelineModel m)
        => Math.Max(m.DurationA, m.OutStartMs + Math.Max(0, m.DurationB - m.InStartMs) / (double)Math.Max(0.01f, m.SpeedB));

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
        _cancel = Cancel;
    }

    void Begin(Gesture g, Point2 p)
    {
        if (_gesture != Gesture.None) Commit();   // a release the engine never reported: the last drag lands first
        if (_m is not { Editable: true } m || _pxPerMs <= 0f) return;
        _gesture = g;
        _start = m;
        _downX = p.X;
        _downY = p.Y;
        _snapped = g switch { Gesture.Out => m.OutStartMs, Gesture.In => m.InStartMs, Gesture.Length => m.OverlapMs, _ => 0 };
        _bend0 = g == Gesture.BendIn ? m.BendIn : m.BendOut;
        _bendQ = _bend0;
        _gripX0 = _gripX;
        if (g == Gesture.BendOut) { _bendX0 = _bendOutX; _bendTop0 = _bendOutTop; }
        if (g == Gesture.BendIn) { _bendX0 = _bendInX; _bendTop0 = _bendInTop; }
        if (g == Gesture.Length)
        {
            // Hold the view the grip was pressed in.
            float span = Math.Clamp(m.OverlapMs * TL.SpanPerOverlap, TL.MinSpanMs, TL.MaxSpanMs);
            _heldPx = _pxPerMs;
            _heldV0 = m.OutStartMs - span * TL.BandLead;
        }
    }

    /// <summary>A pointer move: the dragged value snapped; when it changed, the picture with it in place (the model's preview) is drawn.</summary>
    void Move(Point2 p)
    {
        if (_start is not { } m || _gesture == Gesture.None) return;
        // The drag's local position: the lanes report in their own (still) box; the grip's and the handle's boxes move with the drag itself.
        float dx = p.X - _downX + (_gesture == Gesture.Length ? _gripX - _gripX0 : 0f);
        float dy = p.Y - _downY + (_gesture is Gesture.BendOut or Gesture.BendIn ? _bendDy!.Peek() : 0f);
        switch (_gesture)
        {
            case Gesture.Out:
                Show(MixTimelineEditKind.Out, m.SnapOut(m.OutStartMs - (long)Math.Round(dx / _pxPerMs)));
                break;
            case Gesture.In:
                Show(MixTimelineEditKind.In, m.SnapIn(m.InStartMs - (long)Math.Round(dx / _pxPerMs * m.SpeedB)));
                break;
            case Gesture.Length:
                Show(MixTimelineEditKind.Overlap, m.SnapOverlap(m.OverlapMs + (long)Math.Round(dx / _pxPerMs)));
                break;
            default:
            {
                // A handle stays inside its lane; the curve bends with it.
                float half = _laneH * 0.5f;
                dy = Math.Clamp(dy, -half, half);
                _bendDy!.Value = dy;
                float bend = Math.Clamp(_bend0 - dy / _laneH * TL.BendPerLane, -1f, 1f);
                float q = MathF.Round(bend / TL.BendStep) * TL.BendStep;
                if (q == _bendQ) break;
                _bendQ = q;
                var kind = _gesture == Gesture.BendIn ? MixTimelineEditKind.BendIn : MixTimelineEditKind.BendOut;
                _live!.Value = m.Preview(new MixTimelineEdit(kind, 0, q));
                break;
            }
        }
    }

    void Show(MixTimelineEditKind kind, long snapped)
    {
        if (snapped == _snapped) return;
        _snapped = snapped;
        _live!.Value = _start!.Preview(new MixTimelineEdit(kind, snapped, 0f));
    }

    /// <summary>The release: the dragged value goes to the editor (one draft change); its new model draws the same picture the preview did.</summary>
    void Commit()
    {
        var g = _gesture;
        var m = _start;
        if (g == Gesture.None || m is null) { Cancel(); return; }
        bool changed = g switch
        {
            Gesture.Out => _snapped != m.OutStartMs,
            Gesture.In => _snapped != m.InStartMs,
            Gesture.Length => _snapped != m.OverlapMs,
            _ => _bendQ != _bend0,
        };
        Rest();
        if (!changed) { m.EndPreview(); return; }
        switch (g)
        {
            case Gesture.Out: m.CommitOut(_snapped); break;
            case Gesture.In: m.CommitIn(_snapped); break;
            case Gesture.Length: m.CommitOverlap(_snapped); break;
            default: m.CommitBend(g == Gesture.BendIn, _bendQ); break;
        }
    }

    void Cancel()
    {
        var m = _start;
        Rest();
        m?.EndPreview();
    }

    void Rest()
    {
        _gesture = Gesture.None;
        _heldPx = 0f;
        _bendDy!.Value = 0f;
        _live!.Value = null;
    }

    // ── the geometry (node-local DIP; rebuilt only when the width, the model or the view changes) ─────────────────────────────

    void Geometry(MixTimelineModel m, float w, float h, double v0, float pxPerMs, float x0, float bandW, float inW)
    {
        if (_geomKey.W == w && ReferenceEquals(_geomKey.M, m) && _geomKey.LaneH == h && _geomKey.V0 == v0 && _geomKey.Px == pxPerMs) return;
        _geomKey = (w, m, h, v0, pxPerMs);
        // A's media time at x, and B's (from its in-point at the band's start, at its speed); drawn a width either side for the drags.
        double AtA(float x) => v0 + x / pxPerMs;
        double AtB(float x) => m.InStartMs + (x - x0) / pxPerMs * m.SpeedB;
        _waveA = Wave(m.LevelsA, m.StepMs, m.DurationA, AtA, w, h, TL.WaveStep, 0f);
        _waveB = Wave(m.LevelsB, m.StepMs, m.DurationB, AtB, w, h, TL.WaveStep, 0f);
        (_beatsA, _downbeatsA) = Ticks(m.BeatsA, m.DownA, ms => (float)((ms - v0) * pxPerMs), w, h);
        (_beatsB, _downbeatsB) = Ticks(m.BeatsB, m.DownB, ms => (float)(x0 + (ms - m.InStartMs) / m.SpeedB * pxPerMs), w, h);
        _curveOut = m.None ? null : Curve(m.VolumeOut, x0, bandW, h);
        _curveIn = m.None ? null : Curve(m.VolumeIn, x0, inW, h);
        _cutOut = m.None || m.CutoffOut is null ? null : Curve(m.CutoffOut, x0, bandW, h);
        _cutIn = m.None || m.CutoffIn is null ? null : Curve(m.CutoffIn, x0, inW, h);
    }

    (float W, MixTimelineModel? M, float H) _overKey;

    void OverviewGeometry(MixTimelineModel m, float w, float h)
    {
        if (_overKey.W == w && ReferenceEquals(_overKey.M, m) && _overKey.H == h) return;
        _overKey = (w, m, h);
        double total = OverviewTotal(m);
        if (total <= 0) { _overA = _overB = null; return; }
        double msPerPx = total / w;
        double speed = Math.Max(0.01f, m.SpeedB);
        // A in the upper half, B in the lower (Spotify's overview): each a mirrored envelope around its half's middle.
        _overA = Wave(m.LevelsA, m.StepMs, m.DurationA, x => x * msPerPx, w, h * 0.5f, TL.OverviewStep, 0f, reach: false);
        _overB = Wave(m.LevelsB, m.StepMs, m.DurationB, x => m.InStartMs + (x * msPerPx - m.OutStartMs) * speed, w, h * 0.5f, TL.OverviewStep, h * 0.5f, reach: false);
    }

    /// <summary>A mirrored envelope around the middle of a band <paramref name="h"/> tall from <paramref name="top"/>: a vertical stroke every
    /// <paramref name="step"/> DIP (a silent column draws nothing), from half a width before the lane to half a width after it unless
    /// <paramref name="reach"/> is off. Strokes, not a filled outline: the path realizer gave up on long filled outlines whose columns repeat a level.</summary>
    static PathData? Wave(float[] levels, int stepMs, long duration, Func<float, double> msAt, float w, float h, float step, float top, bool reach = true)
    {
        if (levels.Length == 0 || stepMs <= 0) return null;
        float mid = top + h * 0.5f, depth = h * 0.5f * TL.WaveDepth;
        float from = reach ? -w * TL.WaveReach : 0f;
        int cols = (int)(w * (reach ? 1f + 2f * TL.WaveReach : 1f) / step) + 1;
        var b = new PathBuilder();
        bool any = false;
        for (int i = 0; i < cols; i++)
        {
            float x = from + i * step;
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
