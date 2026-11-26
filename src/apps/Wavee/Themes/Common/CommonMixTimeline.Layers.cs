// ── Themes/Common/CommonMixTimeline.Layers.cs — the Mix timeline's layer view: what you hear and one lane per layer (#48 Mix lesson) ────
//
// The timeline's second picture (MixTimelineModel.Heard / .Layers; the lesson's teaching mode, Theming/Contracts/MixLessonProps.cs), in the
// calling theme's look:
//
//   What you hear
//   Night Drive   ┌ ▍▌▍▌▍▌▍▌▍▌▍▌▍▌▍▌▍▌▍▌▍▌▍··̣·̣·̣·̣·̣·̣·̣·̣ ┃ ▌|||||  ┐   each song after every layer (the whole sound, its lows as the core), the echo's
//   124 BPM · 8A  └───────────────────╫───────────────┃───────┘   tail in its hue, the beat ticks, the overlap washed, a hard cut dashed
//   Sunrise       ┌ ··········▍▌▍▌▍▌▍▌▍▌▍▌▍▌▍▌▍▌▍▌▍▌▍▌▍▌▍▌▍▌ ┐
//   Layers
//   ① Volume      ┌ ‾‾‾‾‾‾‾‾‾‾╲___  ___╱‾‾‾‾  ●  ◌ Drag here  ┐   one lane per layer, drawn from its marks; the focused one tinted in its hue,
//   Fade, 4 bars  └─────────────────────────────────────────┘   its handle (a slider thumb in its hue) and its target (the hint colour's
//   ② Bass        ┌ ════════════│═══════════════════════════ ┐   beacon, a dashed guide from the handle, a tip); a dimmed lane fades back
//   bar 4   5     6     7     8     9     10    11                the bar ruler
//
// A lane that turns on draws in (its lines trim in from the left as it fades up) and one that turns off fades out under the quiet picture
// of what it would leave alone; the waveform crossfades into a new picture. The handle drags along time or value (the model snaps it);
// the lane under it is a drag surface too, the handle's keys step it. The playhead is bound: it moves without a re-render.
// At a narrow width (no label column) each lane's name sits on a plate inside it.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Render;
using FluentGpu.Signals;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using TL = Wavee.Themes.Common.CommonMixTimelineRules;

namespace Wavee.Themes.Common;

internal sealed partial class MixTimeline
{
    // The layer view's geometry this render (node-local DIP): the plot's left edge and width, the window.
    float _plotX0, _plotW;
    double _from, _to;
    /// <summary>The lane being dragged (−1: none), the handle box's place at the press (it holds still under the pointer), that lane's top.</summary>
    int _dragLane = -1;
    float _heldLeft, _heldTop, _dragTop, _dragH;
    MixLayerHandle? _dragHandle;
    (int Version, float W, float H) _heardKey;
    PathData? _outerA, _coreA, _outerB, _coreB, _echoA, _lTicksA, _lDownA, _lTicksB, _lDownB;

    float X(double ms) => TL.PlotPad + (float)((ms - _from) / Math.Max(1, _to - _from)) * Math.Max(1f, _plotW - 2f * TL.PlotPad);
    float MsAt(float x) => (float)(_from + (x - TL.PlotPad) / Math.Max(1f, _plotW - 2f * TL.PlotPad) * (_to - _from));

    static ColorF Hue(uint argb) => R.Ink.DataDot(argb);
    static ColorF Mul(ColorF c, float a) => c with { A = c.A * a };

    Element Layered(MixTimelineModel m, MixTimelineLook look, float w)
    {
        float labelCol = look.LabelColumn;
        bool wide = labelCol > 0f;
        _plotX0 = labelCol;
        _plotW = MathF.Max(1f, w - labelCol);
        var window = m.Window ?? new MixTimelineWindow(m.OutStartMs - 4000, m.OutStartMs + m.OverlapMs + 4000);
        _from = window.FromMs;
        _to = window.ToMs;
        ColorF muted = look.Muted ?? Tok.TextSecondary with { A = 0.3f };
        float songH = look.SongHeight;
        float captionH = wide && m.HeardCaption.Length > 0 ? Caption("").LineHeight + 4f : 0f;
        if (w > 1f && m.Heard is { } heard) HeardGeometry(m, heard, songH);

        var rows = new List<Element>(12);
        var tops = new float[m.Layers.Length];
        float y = 0f;
        if (captionH > 0f) { rows.Add(CaptionRow(m.HeardCaption, captionH, labelCol, look)); y += captionH; }
        float songTop = y;
        rows.Add(SongRow(m, look, false, songH, labelCol, muted));
        y += songH + look.SongGap;
        rows.Add(new BoxEl { Height = look.SongGap, Shrink = 0f });
        rows.Add(SongRow(m, look, true, songH, labelCol, muted));
        y += songH;
        // The section gap holds the "Layers" caption at its foot (wide only).
        rows.Add(new BoxEl
        {
            Height = look.SectionGap, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.End, MinWidth = 0f,
            Children = wide && m.LayersCaption.Length > 0 ? [Text(look.CaptionText, m.LayersCaption, Tok.TextTertiary) with { Margin = new Edges4(0f, 0f, 0f, 2f) }] : [],
        });
        y += look.SectionGap;
        float layersTop = y;
        for (int i = 0; i < m.Layers.Length; i++)
        {
            var lane = m.Layers[i];
            float h = MathF.Round(look.LayerHeight * lane.HeightShare);
            tops[i] = y;
            rows.Add(LayerRow(lane, i, h, labelCol, look, muted));
            y += h;
            if (i + 1 < m.Layers.Length) { rows.Add(new BoxEl { Height = look.LayerGap, Shrink = 0f }); y += look.LayerGap; }
        }
        float lanesBottom = y;
        if (!m.Ruler.IsDefaultOrEmpty) { rows.Add(RulerRow(m, look, labelCol)); y += look.RulerHeight; }

        var layers = new List<Element>(8)
        {
            new BoxEl { Key = "mix:tl:lanes", Direction = 1, MinWidth = 0f, Children = rows.ToArray() },
        };
        // The bar lines and the overlap's wash under everything (they take no input).
        layers.Insert(0, Grid(m, look, songTop, lanesBottom, songH));
        // The playhead over the songs and the layers.
        if (m.PlayheadMs is { } ph)
        {
            float x0 = _plotX0;
            var self = this;
            layers.Add(new BoxEl
            {
                Key = "mix:tl:playhead", JustifySelf = FlexAlign.Start, AlignSelf = FlexAlign.Start, Width = TL.PlayheadWidth, Height = MathF.Max(1f, lanesBottom - songTop),
                Fill = look.Playhead ?? Tok.AccentDefault, Corners = CornerRadius4.All(1f), HitTestVisible = false,
                Transform = Prop.Of(() => Affine2D.Translation(float.IsNaN(ph.Value) ? -10f : x0 + self.X(ph.Value) - 1f, songTop)),
                Opacity = Prop.Of(() => float.IsNaN(ph.Value) ? 0f : 1f),
            });
        }
        // Each active lane's guide, target and handle, over the lanes (a tip rises past its lane's edge).
        for (int i = 0; i < m.Layers.Length; i++)
        {
            var lane = m.Layers[i];
            float h = MathF.Round(look.LayerHeight * lane.HeightShare);
            if (lane.Target is { } t) Target(layers, lane, t, tops[i], h, look);
            if (lane.Handle is { } hd) Handle(layers, lane, hd, i, tops[i], h, look);
        }
        return new BoxEl { Key = "mix:tl:root", ZStack = true, MinWidth = 0f, Height = y, Shrink = 0f, Children = layers.ToArray() };
    }

    static TextEl Caption(string s) => Ui.Caption(s);

    static TextEl Text(Func<string, TextEl>? role, string s, ColorF ink)
        => (role?.Invoke(s) ?? Ui.Caption(s)) with { Color = ink, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f };

    Element CaptionRow(string text, float h, float labelCol, MixTimelineLook look) => new BoxEl
    {
        Height = h, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Start, MinWidth = 0f,
        Children = [new BoxEl { Width = labelCol, Shrink = 0f, Children = [Text(look.CaptionText, text, Tok.TextTertiary)] }],
    };

    // ── the songs ────────────────────────────────────────────────────────────────────────────────────────────────────

    Element SongRow(MixTimelineModel m, MixTimelineLook look, bool incoming, float h, float labelCol, ColorF muted)
    {
        var heard = m.Heard;
        uint hue = heard is null ? 0 : incoming ? heard.HueB : heard.HueA;
        string name = incoming ? m.LabelB : m.LabelA, facts = incoming ? m.FactsB : m.FactsA;
        var kids = new List<Element>(8);
        // The waveform, keyed by its picture: a new picture crossfades in over the old.
        if (heard is not null)
        {
            var outer = incoming ? _outerB : _outerA;
            var core = incoming ? _coreB : _coreA;
            var waves = new List<Element>(3);
            ColorF ink = Hue(hue);
            if (outer is not null) waves.Add(new PathEl { Geometry = outer, Width = _plotW, Height = h, StrokeColor = Mul(ink, TL.OuterAlpha), Stroke = new StrokeStyle(2f, LineCap.Butt) });
            if (core is not null) waves.Add(new PathEl { Geometry = core, Width = _plotW, Height = h, StrokeColor = ink, Stroke = new StrokeStyle(2f, LineCap.Butt) });
            if (!incoming && _echoA is not null)
                waves.Add(new PathEl { Geometry = _echoA, Width = _plotW, Height = h, StrokeColor = Mul(Hue(heard.HueEcho), TL.EchoAlpha), Stroke = new StrokeStyle(2f, LineCap.Butt) });
            var fade = new EnterExit(Opacity: 0f, Active: true);
            kids.Add(new BoxEl
            {
                Key = "mix:tl:heard:" + (incoming ? "b:" : "a:") + heard.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ZStack = true, HitTestVisible = false, Enter = fade, Exit = fade,
                Transition = MotionTokenDef.Eased(TL.HeardFadeMs, Easing.FluentDecelerate, ReducedMotionPolicy.SnapEnd), Children = waves.ToArray(),
            });
            // The beat ticks along the lane's foot (downbeats taller).
            var ticks = incoming ? _lTicksB : _lTicksA;
            var downs = incoming ? _lDownB : _lDownA;
            ColorF tick = Mul(ink, 0.55f);
            if (ticks is not null) kids.Add(new PathEl { Geometry = ticks, Width = _plotW, Height = h, StrokeColor = tick, Stroke = new StrokeStyle(2f, LineCap.Butt) });
            if (downs is not null) kids.Add(new PathEl { Geometry = downs, Width = _plotW, Height = h, StrokeColor = tick, Stroke = new StrokeStyle(2f, LineCap.Butt) });
            // What a solo leaves out ("Only the lows play" on Night Drive, "Sunrise is muted for now" on Sunrise).
            string note = incoming ? heard.NoteB : heard.NoteA;
            if (note.Length > 0)
                kids.Add(new BoxEl
                {
                    Key = incoming ? "mix:tl:note:b" : "mix:tl:note:a", Direction = 0, MinWidth = 0f, HitTestVisible = false,
                    Justify = incoming ? FlexJustify.Center : FlexJustify.End, AlignItems = incoming ? FlexAlign.Center : FlexAlign.Start,
                    Padding = new Edges4(look.LabelInset, look.LabelInset * 0.5f, look.LabelInset, 0f),
                    Enter = new EnterExit(Opacity: 0f, Active: true), Exit = new EnterExit(Opacity: 0f, Active: true),
                    Children = [Text(look.ReadoutText, note, Tok.TextSecondary) with { Shrink = 0f }],
                });
        }
        // The hard cut, dashed through the song, its word on A.
        if (m.CutAtMs >= 0)
        {
            float cx = X(m.CutAtMs);
            var cut = new List<Element>(2)
            {
                new BoxEl { Width = 0f, Height = h, OffsetX = cx, JustifySelf = FlexAlign.Start, BorderColor = Tok.TextPrimary, BorderWidth = 1f,
                    BorderDashOn = 4f, BorderDashOff = 4f, HitTestVisible = false },
            };
            if (!incoming)
                cut.Add(new BoxEl { JustifySelf = FlexAlign.Start, AlignSelf = FlexAlign.Start, OffsetX = cx + 6f, OffsetY = 4f, HitTestVisible = false,
                    Children = [Text(look.NameText, m.CutLabel, Tok.TextPrimary) with { Shrink = 0f }] });
            kids.Add(new BoxEl
            {
                Key = incoming ? "mix:tl:cut:b" : "mix:tl:cut:a", ZStack = true, HitTestVisible = false,
                Enter = new EnterExit(Opacity: 0f, Active: true), Exit = new EnterExit(Opacity: 0f, Active: true), Children = cut.ToArray(),
            });
        }
        if (labelCol <= 0f) kids.Add(Plate(name, look, null));
        var plot = new BoxEl
        {
            Key = incoming ? "mix:tl:b" : "mix:tl:a", Width = _plotW, Height = h, Shrink = 0f, ZStack = true, ClipToBounds = true,
            Fill = look.LaneFill, Corners = CornerRadius4.All(look.Corner), HitTestVisible = false, Children = kids.ToArray(),
        };
        if (labelCol <= 0f) return plot;
        return new BoxEl
        {
            Direction = 0, MinWidth = 0f, Height = h, Shrink = 0f,
            Children =
            [
                new BoxEl
                {
                    Width = labelCol, Shrink = 0f, Direction = 1, Justify = FlexJustify.Center, MinWidth = 0f, Padding = new Edges4(0f, 0f, look.LabelInset, 0f),
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 0, AlignItems = FlexAlign.Center, Gap = TL.LabelGap, MinWidth = 0f,
                            Children = [Swatch(Hue(hue), look), Text(look.NameText, name, Tok.TextPrimary)],
                        },
                        Text(look.ReadoutText, facts, Tok.TextTertiary) with { Margin = new Edges4(18f, 0f, 0f, 0f) },
                    ],
                },
                plot,
            ],
        };
    }

    static Element Swatch(ColorF c, MixTimelineLook look) => new BoxEl
    {
        Width = TL.Swatch, Height = TL.Swatch, Shrink = 0f, Fill = c, Corners = CornerRadius4.All(look.SquareCorner ?? TL.SwatchCorner),
    };

    /// <summary>A name on a plate inside its lane (the narrow layout).</summary>
    static Element Plate(string name, MixTimelineLook look, Element? lead) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Start, HitTestVisible = false, MinWidth = 0f,
        Padding = new Edges4(look.LabelInset * 0.5f, look.LabelInset * 0.5f, look.LabelInset, 0f),
        Children =
        [
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = TL.PlateGap, Shrink = 1f, MinWidth = 0f, Padding = new Edges4(look.LabelPadX, 1f, look.LabelPadX, 1f),
                Fill = look.LabelPlate, Corners = CornerRadius4.All(look.Corner),
                Children = lead is null ? [Text(look.NameText, name, look.Label)] : [lead, Text(look.NameText, name, look.Label)],
            },
        ],
    };

    /// <summary>The waveforms, the echo and the ticks of what you hear, rebuilt only when the picture or the size changes.</summary>
    void HeardGeometry(MixTimelineModel m, MixHeardLevels heard, float h)
    {
        if (_heardKey == (heard.Version, _plotW, h) && _outerA is not null) return;
        _heardKey = (heard.Version, _plotW, h);
        float amp = h * 0.5f - 5f;
        _outerA = Wave(heard.OuterA, heard.StepMs, h, amp, null);
        _coreA = Wave(heard.CoreA, heard.StepMs, h, amp * 0.92f, heard.OuterA);
        _outerB = Wave(heard.OuterB, heard.StepMs, h, amp, null);
        _coreB = Wave(heard.CoreB, heard.StepMs, h, amp * 0.92f, heard.OuterB);
        _echoA = Wave(heard.Echo, heard.StepMs, h, amp, null, floor: 0.01f);
        (_lTicksA, _lDownA) = Beats(m.BeatsA, m.DownA, h, m.OutStartMs + m.OverlapMs);
        (_lTicksB, _lDownB) = Beats(m.BeatsB, m.DownB, h, long.MaxValue);
    }

    const float Col = 3f;

    /// <summary>A mirrored envelope: one 2-DIP stroke every <see cref="Col"/> DIP, its half height the loudness of the loudest level under it.</summary>
    PathData? Wave(float[] levels, int stepMs, float h, float amp, float[]? cap, float floor = 0f)
    {
        if (levels.Length == 0 || stepMs <= 0) return null;
        float mid = h * 0.5f;
        int cols = (int)(_plotW / Col);
        var b = new PathBuilder();
        bool any = false;
        for (int c = 0; c < cols; c++)
        {
            float x = c * Col + 1f;
            int i0 = (int)(MsAt(x - 1f) / stepMs), i1 = (int)(MsAt(x + Col - 1f) / stepMs);
            if (i1 < 0 || i0 >= levels.Length) continue;
            float v = 0f, top = 0f;
            for (int i = Math.Max(0, i0); i <= Math.Min(levels.Length - 1, i1); i++)
            {
                v = MathF.Max(v, levels[i]);
                if (cap is not null) top = MathF.Max(top, cap[i]);
            }
            if (v <= floor) continue;
            float half = MathF.Pow(v, TL.Loudness) * amp;
            if (cap is not null) half = MathF.Min(half, MathF.Pow(top, TL.Loudness) * amp / 0.92f * 0.999f);
            half = MathF.Max(0.5f, half);
            b.MoveTo(x, mid - half);
            b.LineTo(x, mid + half);
            any = true;
        }
        return any ? b.Finish(PathContentEpoch.Mint(), FillRule.NonZero) : null;
    }

    (PathData?, PathData?) Beats(uint[] beats, bool[] down, float h, long until)
    {
        if (beats.Length == 0) return (null, null);
        var b = new PathBuilder();
        var d = new PathBuilder();
        bool anyB = false, anyD = false;
        for (int i = 0; i < beats.Length; i++)
        {
            if (beats[i] < _from - 1 || beats[i] > until) continue;
            float x = MathF.Round(X(beats[i]));
            if (x < 0f || x > _plotW) continue;
            bool isDown = i < down.Length && down[i];
            var p = isDown ? d : b;
            p.MoveTo(x, h - 3f - (isDown ? 6f : 3f));
            p.LineTo(x, h - 3f);
            if (isDown) anyD = true; else anyB = true;
        }
        return (anyB ? b.Finish(PathContentEpoch.Mint(), FillRule.NonZero) : null, anyD ? d.Finish(PathContentEpoch.Mint(), FillRule.NonZero) : null);
    }

    /// <summary>The bar lines through every lane and the overlap's wash over the songs.</summary>
    Element Grid(MixTimelineModel m, MixTimelineLook look, float top, float bottom, float songH)
    {
        var kids = new List<Element>(12);
        ColorF line = look.Grid ?? Tok.TextPrimary with { A = 0.06f };
        if (m.OverlapMs > 0)
        {
            float a = X(m.OutStartMs), z = X(m.OutStartMs + m.OverlapMs);
            kids.Add(new BoxEl
            {
                JustifySelf = FlexAlign.Start, AlignSelf = FlexAlign.Start, Width = MathF.Max(0f, z - a), Height = 2f * songH + look.SongGap,
                OffsetX = _plotX0 + a, OffsetY = top, Fill = look.Overlap ?? Tok.TextPrimary with { A = 0.03f }, HitTestVisible = false,
            });
        }
        foreach (var mark in m.Ruler)
            kids.Add(new BoxEl
            {
                JustifySelf = FlexAlign.Start, AlignSelf = FlexAlign.Start, Width = 1f, Height = MathF.Max(0f, bottom - top),
                OffsetX = MathF.Round(_plotX0 + X(mark.Ms)), OffsetY = top, Fill = line, HitTestVisible = false,
            });
        return new BoxEl { Key = "mix:tl:grid", ZStack = true, HitTestVisible = false, MinWidth = 0f, Children = kids.ToArray() };
    }

    Element RulerRow(MixTimelineModel m, MixTimelineLook look, float labelCol)
    {
        var kids = new List<Element>(m.Ruler.Length);
        float last = _plotW - TL.PlotPad;
        foreach (var mark in m.Ruler)
        {
            float x = X(mark.Ms);
            if (x > last) break;
            // A label too close to the end sits left of its bar line instead of past it.
            bool end = x > _plotW - TL.MarkLabel;
            kids.Add(new BoxEl
            {
                JustifySelf = FlexAlign.Start, AlignSelf = FlexAlign.Center, Width = TL.MarkLabel, Direction = 0, Justify = end ? FlexJustify.End : FlexJustify.Start,
                OffsetX = end ? x - TL.MarkLabel - TL.PlateGap : x + TL.PlateGap, HitTestVisible = false,
                Children = [Text(look.ReadoutText, mark.Label, look.Ruler ?? Tok.TextTertiary) with { Shrink = 0f }],
            });
        }
        return new BoxEl
        {
            Key = "mix:tl:ruler", Direction = 0, Height = look.RulerHeight, Shrink = 0f, MinWidth = 0f,
            Children = [new BoxEl { Width = labelCol, Shrink = 0f }, new BoxEl { Width = _plotW, Height = look.RulerHeight, ZStack = true, Children = kids.ToArray() }],
        };
    }

    // ── the layers ───────────────────────────────────────────────────────────────────────────────────────────────────

    Element LayerRow(MixLayerLane lane, int index, float h, float labelCol, MixTimelineLook look, ColorF muted)
    {
        ColorF ink = Hue(lane.Hue);
        var kids = new List<Element>(4);
        // The layer's picture: on, in its hue (drawn in); off, the quiet picture of what it leaves alone. One of the two at a time.
        var marks = new List<Element>(lane.Marks.Length);
        foreach (var mk in lane.Marks) marks.Add(Mark(mk, h, muted, look, lane.On));
        string state = lane.On ? ":on" : ":off";
        kids.Add(new BoxEl
        {
            Key = "mix:tl:layer:" + lane.Key + state, ZStack = true, HitTestVisible = false, Children = marks.ToArray(),
            Enter = new EnterExit(Opacity: 0f, Active: true), Exit = new EnterExit(Opacity: 0f, Active: true),
            Transition = MotionTokenDef.Eased(lane.On ? TL.DrawInMs : TL.FadeOutMs, Easing.FluentDecelerate, ReducedMotionPolicy.SnapEnd),
        });
        if (labelCol <= 0f) kids.Add(Plate(lane.Name, look, Badge(lane, look, muted, small: true)));
        var handle = lane.Handle;
        bool drags = handle is not null;
        var plot = new BoxEl
        {
            Key = "mix:tl:layer:" + lane.Key, Width = _plotW, Height = h, Shrink = 0f, ZStack = true, ClipToBounds = true, Corners = CornerRadius4.All(look.Corner),
            Fill = lane.Focus ? Mul(ink, TL.FocusTint) : look.LaneFill, HoverFill = lane.Focus ? Mul(ink, TL.FocusTint) : look.LaneFill,
            PressedFill = lane.Focus ? Mul(ink, TL.FocusTint) : look.LaneFill,
            BorderColor = lane.Focus ? Mul(ink, TL.FocusEdge) : ColorF.Transparent, BorderWidth = 1f, BrushTransitionMs = 250f,
            Cursor = drags ? handle!.Axis == MixLayerAxis.Time ? CursorId.SizeWE : CursorId.SizeNS : null,
            OnPointerDown = drags ? p => BeginLane(index, handle!, p, h) : null,
            OnDrag = drags ? p => DragLane(index, p) : null,
            OnPointerReleased = drags ? _ => EndLane() : null,
            OnDragCanceled = drags ? EndLane : null,
            Children = kids.ToArray(),
        };
        var row = new List<Element>(2);
        if (labelCol > 0f)
            row.Add(new BoxEl
            {
                Width = labelCol, Shrink = 0f, Direction = 1, Justify = FlexJustify.Center, MinWidth = 0f, Padding = new Edges4(0f, 0f, look.LabelInset, 0f),
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center, Gap = TL.LabelGap, MinWidth = 0f,
                        Children = [Badge(lane, look, muted, small: false), Text(look.NameText, lane.Name, lane.On ? Tok.TextPrimary : Tok.TextSecondary)],
                    },
                    Text(look.ReadoutText, lane.Readout, lane.On ? Tok.TextSecondary : Tok.TextTertiary) with { Key = "mix:tl:layer:" + lane.Key + ":readout", Margin = new Edges4(26f, 0f, 0f, 0f) },
                ],
            });
        row.Add(plot);
        // A lane that dims (a solo of another layer, a comparison's original) or comes back fades over the house's short ease, never at once.
        var box = new BoxEl { Direction = 0, Height = h, Shrink = 0f, MinWidth = 0f, Children = row.ToArray() };
        return Embed.Comp(new DimProps(box, lane.Dim), static () => new LaneDim()) with { Key = "mix:tl:layer:" + lane.Key + ":row" };
    }

    /// <summary>The lane's number on a disc in its hue (on) or a quiet ring (off).</summary>
    static Element Badge(MixLayerLane lane, MixTimelineLook look, ColorF muted, bool small)
    {
        float d = small ? TL.BadgeSmall : TL.Badge;
        ColorF ink = Hue(lane.Hue);
        return new BoxEl
        {
            Width = d, Height = d, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(look.SquareCorner ?? d * 0.5f), Fill = lane.On ? ink : ColorF.Transparent,
            BorderColor = lane.On ? ink : Tok.TextTertiary, BorderWidth = 1f, BrushTransitionMs = 250f,
            Children = [new TextEl(lane.Number.ToString(System.Globalization.CultureInfo.InvariantCulture))
                { Size = TL.BadgeText, Weight = 600, Color = lane.On ? Tok.TextOnAccentPrimary : Tok.TextTertiary, MaxLines = 1 }],
        };
    }

    /// <summary>One mark of a lane (see MixMarkKind), in lane coordinates.</summary>
    Element Mark(MixLayerMark mk, float h, ColorF muted, MixTimelineLook look, bool on)
    {
        ColorF ink = mk.Hue == 0 ? muted : Hue(mk.Hue);
        ink = Mul(ink, mk.Alpha);
        float x0 = X(mk.FromMs), x1 = X(mk.ToMs);
        float stroke = mk.Hue == 0 ? 1.5f : TL.LayerStroke;
        switch (mk.Kind)
        {
            case MixMarkKind.Line or MixMarkKind.Dash or MixMarkKind.Area:
            {
                var line = Poly(mk, x0, x1, h, close: false);
                var style = mk.Kind == MixMarkKind.Dash ? new StrokeStyle(1.5f, LineCap.Butt, LineJoin.Round, 4f, 4f, 4f) : new StrokeStyle(stroke, LineCap.Round, LineJoin.Round);
                if (mk.Kind != MixMarkKind.Area)
                    return new PathEl { Geometry = line, Width = _plotW, Height = h, StrokeColor = ink, Stroke = style };
                return new PathEl { Geometry = Poly(mk, x0, x1, h, close: true), Width = _plotW, Height = h, Fill = ink };
            }
            case MixMarkKind.Bar:
                return new BoxEl
                {
                    JustifySelf = FlexAlign.Start, AlignSelf = FlexAlign.Start, Width = MathF.Max(0f, x1 - x0), Height = mk.Size, OffsetX = x0,
                    OffsetY = mk.Y0 * h - mk.Size * 0.5f, Fill = ink, Corners = CornerRadius4.All(look.SquareCorner ?? mk.Size * 0.5f),
                };
            case MixMarkKind.Rule:
                return new BoxEl
                {
                    JustifySelf = FlexAlign.Start, AlignSelf = FlexAlign.Start, Width = mk.Size, Height = MathF.Max(1f, (mk.Y1 - mk.Y0) * h), OffsetX = x0 - mk.Size * 0.5f,
                    OffsetY = mk.Y0 * h, Fill = ink,
                };
            case MixMarkKind.Ring:
            {
                float d = mk.Size * h;
                return new BoxEl
                {
                    JustifySelf = FlexAlign.Start, AlignSelf = FlexAlign.Start, Width = d, Height = d, OffsetX = x0 - d * 0.5f, OffsetY = mk.Y0 * h - d * 0.5f,
                    BorderColor = ink, BorderWidth = TL.RingStroke, Corners = CornerRadius4.All(look.SquareCorner ?? d * 0.5f),
                };
            }
            case MixMarkKind.Block:
                return new BoxEl
                {
                    JustifySelf = FlexAlign.Start, AlignSelf = FlexAlign.Start, Width = MathF.Max(0f, x1 - x0), Height = MathF.Max(1f, (mk.Y1 - mk.Y0) * h),
                    OffsetX = x0, OffsetY = mk.Y0 * h, Fill = ink, Corners = CornerRadius4.All(look.SquareCorner ?? 3f),
                };
            default:
                return new BoxEl
                {
                    JustifySelf = FlexAlign.Start, AlignSelf = FlexAlign.Start, Width = TL.MarkLabel, OffsetX = x0 - TL.MarkLabel - TL.PlateGap, OffsetY = mk.Y0 * h - TL.MarkLabel * 0.18f,
                    Direction = 0, Justify = FlexJustify.End,
                    Children = [Text(look.ReadoutText, mk.Text, Tok.TextTertiary) with { Shrink = 0f }],
                };
        }
    }

    PathData Poly(MixLayerMark mk, float x0, float x1, float h, bool close)
    {
        var b = new PathBuilder();
        if (mk.Ys is { Length: >= 2 } ys)
        {
            for (int i = 0; i < ys.Length; i++)
            {
                float x = x0 + (x1 - x0) * i / (ys.Length - 1), y = ys[i] * h;
                if (i == 0) b.MoveTo(x, y); else b.LineTo(x, y);
            }
        }
        else
        {
            b.MoveTo(x0, mk.Y0 * h);
            b.LineTo(x1, mk.Y1 * h);
        }
        if (close)
        {
            b.LineTo(x1, h - 3f);
            b.LineTo(x0, h - 3f);
            b.Close();
        }
        return b.Finish(PathContentEpoch.Mint(), FillRule.NonZero);
    }

    // ── a lane's target and handle ───────────────────────────────────────────────────────────────────────────────────

    void Target(List<Element> layers, MixLayerLane lane, MixLayerTarget t, float top, float h, MixTimelineLook look)
    {
        float tx = _plotX0 + X(t.Ms), ty = top + t.Y * h;
        ColorF hint = CommonMixLessonRules.Hint(t.Hue);
        // The dashed guide from the handle to the target (until it is reached).
        if (lane.Handle is { } hd && !t.Reached)
        {
            float hx = _plotX0 + X(hd.Ms), hy = top + hd.Y * h;
            if (MathF.Abs(hx - tx) + MathF.Abs(hy - ty) > 20f)
            {
                float left = MathF.Min(hx, tx), up = MathF.Min(hy, ty);
                var b = new PathBuilder();
                b.MoveTo(hx - left + 1f, hy - up + 1f);
                b.LineTo(tx - left + 1f, ty - up + 1f);
                layers.Add(new PathEl
                {
                    Key = "mix:tl:layer:" + lane.Key + ":guide", Geometry = b.Finish(PathContentEpoch.Mint(), FillRule.NonZero),
                    Width = MathF.Abs(hx - tx) + 2f, Height = MathF.Abs(hy - ty) + 2f, JustifySelf = FlexAlign.Start, AlignSelf = FlexAlign.Start,
                    OffsetX = left - 1f, OffsetY = up - 1f, StrokeColor = Mul(hint, 0.8f), Stroke = new StrokeStyle(1.6f, LineCap.Butt, LineJoin.Round, 4f, TL.GuideDash, TL.GuideGap),
                });
            }
        }
        float d = look.BeaconSize;
        var style = new Beacon.Style
        {
            Color = hint, Done = Tok.SystemFillSuccess, OnDone = Tok.TextOnAccentPrimary, Size = d, Corner = look.SquareCorner,
        };
        layers.Add(new BoxEl
        {
            Key = "mix:tl:layer:" + lane.Key + ":target", JustifySelf = FlexAlign.Start, AlignSelf = FlexAlign.Start, Width = d, Height = d,
            Transform = Affine2D.Translation(tx - d * 0.5f, ty - d * 0.5f), HitTestVisible = false,
            Enter = new EnterExit(Opacity: 0f, Sx: 0.6f, Sy: 0.6f, Active: true), Exit = new EnterExit(Opacity: 0f, Active: true),
            Children = [Beacon.Create(style, t.Reached)],
        });
        layers.Add(Tip(lane, t, tx, ty, d, hint, look));
    }

    /// <summary>The tip ("Drag here" / "Done"): above a time target, beside a value target (on the side with room), its tail on the target.</summary>
    Element Tip(MixLayerLane lane, MixLayerTarget t, float tx, float ty, float d, ColorF hint, MixTimelineLook look)
    {
        bool side = lane.Handle?.Axis == MixLayerAxis.Value;
        ColorF plate = look.TipFill ?? Tok.FillSolidTertiary;
        ColorF edge = look.TipStroke ?? Tok.StrokeCardDefault;
        float corner = look.SquareCorner ?? 8f;
        var dot = new BoxEl
        {
            Width = TL.Swatch, Height = TL.Swatch, Shrink = 0f, Corners = CornerRadius4.All(look.SquareCorner ?? TL.Swatch * 0.5f),
            Fill = t.Reached ? Tok.SystemFillSuccess : ColorF.Transparent, BorderColor = t.Reached ? Tok.SystemFillSuccess : hint, BorderWidth = TL.TipRing,
        };
        var bubble = new BoxEl
        {
            Key = "mix:tl:layer:" + lane.Key + ":tip", Direction = 0, AlignItems = FlexAlign.Center, Gap = TL.TipGap, Height = TL.TipHeight, Shrink = 0f,
            Padding = new Edges4(TL.TipPadX, 0f, TL.TipPadX, 0f), Corners = CornerRadius4.All(corner), Fill = plate, BorderColor = edge, BorderWidth = 1f,
            Shadow = Elevation.Flyout, HitTestVisible = false,
            Children = [dot, Text(look.NameText, t.Tip, Tok.TextPrimary) with { Shrink = 0f }],
        };
        const float wrap = TL.TipWrap;
        float left, top;
        FlexJustify justify;
        if (side)
        {
            bool flip = tx < _plotX0 + _plotW * 0.5f;
            left = flip ? tx + d * 0.5f + 8f : tx - d * 0.5f - 8f - wrap;
            justify = flip ? FlexJustify.Start : FlexJustify.End;
            top = ty - 14f;
        }
        else
        {
            left = Math.Clamp(tx - wrap * 0.5f, _plotX0, MathF.Max(_plotX0, _plotX0 + _plotW - wrap));
            justify = tx - wrap * 0.5f < _plotX0 ? FlexJustify.Start : tx + wrap * 0.5f > _plotX0 + _plotW ? FlexJustify.End : FlexJustify.Center;
            top = ty - d * 0.5f - 8f - 28f;
        }
        return new BoxEl
        {
            Key = "mix:tl:layer:" + lane.Key + ":tipbox", JustifySelf = FlexAlign.Start, AlignSelf = FlexAlign.Start, Width = wrap, Height = TL.TipHeight,
            Transform = Affine2D.Translation(left, top), Direction = 0, Justify = justify, AlignItems = FlexAlign.Center, HitTestVisible = false,
            Enter = new EnterExit(Opacity: 0f, Dy: side ? 0f : 4f, Active: true), Exit = new EnterExit(Opacity: 0f, Active: true),
            Children = [bubble],
        };
    }

    void Handle(List<Element> layers, MixLayerLane lane, MixLayerHandle hd, int index, float top, float h, MixTimelineLook look)
    {
        float hx = _plotX0 + X(hd.Ms), hy = top + hd.Y * h;
        float size = look.LayerHandle, hit = MathF.Max(look.HandleHit, 32f);
        bool held = _dragLane == index;
        float left = held ? _heldLeft : hx - hit * 0.5f, boxTop = held ? _heldTop : hy - hit * 0.5f;
        ColorF ink = Hue(lane.Hue);
        // The visible thumb (WinUI's slider thumb: a solid plate, its value colour inside) rides the value; the hit box holds still while held.
        layers.Add(new BoxEl
        {
            Key = "mix:tl:layer:" + lane.Key + ":dot", JustifySelf = FlexAlign.Start, AlignSelf = FlexAlign.Start, Width = size, Height = size,
            Transform = Affine2D.Translation(hx - size * 0.5f, hy - size * 0.5f), HitTestVisible = false,
            Children = [Embed.Comp(new ThumbProps(ink, look.HandlePlate ?? Tok.FillControlSolid, size, look.SquareCorner, hd.Nudge, hd.Axis), static () => new LayerThumb())],
        });
        layers.Add(ToolTip.Wrap(new BoxEl
        {
            Key = "mix:tl:layer:" + lane.Key + ":handle", JustifySelf = FlexAlign.Start, AlignSelf = FlexAlign.Start, Width = hit, Height = hit,
            Transform = Affine2D.Translation(left, boxTop), Fill = ColorF.Transparent, HoverFill = ColorF.Transparent, PressedFill = ColorF.Transparent,
            Corners = CornerRadius4.All(hit * 0.5f), Role = AutomationRole.Slider, Focusable = true,
            Cursor = hd.Axis == MixLayerAxis.Time ? CursorId.SizeWE : CursorId.SizeNS,
            OnPointerDown = p => BeginHandle(index, hd, p, left, boxTop, top, h),
            OnDrag = p => DragHandle(p),
            OnPointerReleased = _ => EndLane(),
            OnDragCanceled = EndLane,
            OnKeyDown = e => Key(hd, e),
        }, hd.Name));
    }

    static void Key(MixLayerHandle hd, KeyEventArgs e)
    {
        int dir = e.KeyCode switch { 0x27 or 0x26 => 1, 0x25 or 0x28 => -1, 0x21 => 2, 0x22 => -2, _ => 0 };
        if (dir == 0) return;
        e.Handled = true;
        hd.Step(Math.Sign(dir), Math.Abs(dir) > 1);
    }

    void BeginLane(int index, MixLayerHandle hd, Point2 p, float h)
    {
        _dragLane = index;
        _dragHandle = hd;
        _dragTop = 0f;
        _dragH = h;
        _heldLeft = _heldTop = float.NaN;
        hd.Drag(MsAt(p.X), Math.Clamp(p.Y / h, 0f, 1f));
    }

    void DragLane(int index, Point2 p)
    {
        if (_dragLane != index || _dragHandle is not { } hd || !float.IsNaN(_heldLeft)) return;
        hd.Drag(MsAt(p.X), Math.Clamp(p.Y / _dragH, 0f, 1f));
    }

    void BeginHandle(int index, MixLayerHandle hd, Point2 p, float left, float top, float laneTop, float h)
    {
        _dragLane = index;
        _dragHandle = hd;
        _heldLeft = left;
        _heldTop = top;
        _dragTop = laneTop;
        _dragH = h;
        hd.Drag(MsAt(left + p.X - _plotX0), Math.Clamp((top + p.Y - laneTop) / h, 0f, 1f));
    }

    void DragHandle(Point2 p)
    {
        if (_dragLane < 0 || _dragHandle is not { } hd || float.IsNaN(_heldLeft)) return;
        hd.Drag(MsAt(_heldLeft + p.X - _plotX0), Math.Clamp((_heldTop + p.Y - _dragTop) / _dragH, 0f, 1f));
    }

    void EndLane()
    {
        _dragLane = -1;
        _dragHandle = null;
        _heldLeft = _heldTop = float.NaN;
    }
}

internal sealed record DimProps(BoxEl Row, bool Dim);

/// <summary>A lane row whose opacity eases between full and dimmed when its state flips (the first render lands at rest).</summary>
internal sealed class LaneDim : Component
{
    public override Element Render()
    {
        var p = UseProps<DimProps>();
        var was = UseRef(p.Dim);
        float to = p.Dim ? CommonMixTimelineRules.DimOpacity : 1f;
        float from = was.Value ? CommonMixTimelineRules.DimOpacity : 1f;
        UseTransition(AnimChannel.Opacity, from, to, CommonMixTimelineRules.DimMs, Easing.FluentDecelerate, DepKey.From(p.Dim ? 1 : 0));
        was.Value = p.Dim;
        return p.Row with { Opacity = to };
    }
}

internal sealed record ThumbProps(ColorF Ink, ColorF Plate, float Size, float? Square, bool Nudge, MixLayerAxis Axis);

/// <summary>A layer's thumb: a solid plate with its value colour inside; while untouched it nudges along its axis every few seconds ("drag me").</summary>
internal sealed class LayerThumb : Component
{
    public override Element Render()
    {
        var p = UseProps<ThumbProps>();
        bool nudge = p.Nudge && !Motion.ReducedMotion;
        var channel = p.Axis == MixLayerAxis.Time ? AnimChannel.TranslateX : AnimChannel.TranslateY;
        float sign = p.Axis == MixLayerAxis.Time ? -1f : 1f;
        UseLayoutEffect(() =>
        {
            var anim = Context.Anim;
            var scene = Context.Scene;
            var host = Context.HostNode;
            if (anim is null || scene is null || host.IsNull || !scene.IsLive(host)) return;
            if (!nudge) { anim.CancelToRest(host, AnimChannel.TranslateX); anim.CancelToRest(host, AnimChannel.TranslateY); return; }
            anim.Keyframes(host, channel,
            [
                new(0f, 0f, Easing.Linear), new(0.7f, 0f, Easing.Linear), new(0.78f, 4f * sign, Easing.FluentDecelerate),
                new(0.86f, -3f * sign, Easing.FluentDecelerate), new(1f, 0f, Easing.FluentDecelerate),
            ], CommonMixTimelineRules.NudgeMs, loop: true);
        }, DepKey.From(HashCode.Combine(nudge, (int)p.Axis)));
        float inner = MathF.Round(p.Size * 0.55f);
        return new BoxEl
        {
            Width = p.Size, Height = p.Size, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
            Corners = CornerRadius4.All(p.Square ?? p.Size * 0.5f), Fill = p.Plate, BorderColor = Tok.StrokeControlDefault, BorderWidth = 1f,
            Shadow = Elevation.Flyout,
            Children = [new BoxEl { Width = inner, Height = inner, Corners = CornerRadius4.All(p.Square ?? inner * 0.5f), Fill = p.Ink }],
        };
    }
}
