using System;
using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee;

static class WaveeEqualizerCurve
{
    public static readonly string[] FrequencyLabels = ["31", "62", "125", "250", "500", "1k", "2k", "4k", "8k", "16k"];

    internal sealed record Props(float[] Gains, Action<int, float> OnBandChanged, bool IsEnabled, float? Height = null);

    /// <param name="height">Explicit surface height (null = the original width-derived heuristic,
    /// <c>Math.Clamp(width * 0.38f, 252f, 360f)</c>). The setup wizard's Sound stage card fixes this to fit its own
    /// budget rather than letting the curve claim whatever the card's width would otherwise imply.</param>
    public static Element Create(float[] gains, Action<int, float> onBandChanged, bool isEnabled = true, float? height = null)
        // Re-pushed live props (the G4 channel) — replaces the deleted Ctx.Provide(Props.Channel, …) pattern; the
        // record-equality gate re-renders the core only when Gains/OnBandChanged/IsEnabled/Height actually change.
        => Embed.Comp(new Props(gains, onBandChanged, isEnabled, height), () => new WaveeEqualizerCurveCore());
}

sealed class WaveeEqualizerCurveCore : Component
{
    const float MinGain = -12f;
    const float MaxGain = 12f;
    const float PadLeft = 40f;
    const float PadRight = 16f;
    const float PadTop = 18f;
    const float PadBottom = 34f;
    const float NodeRest = 14f;
    const float NodeHot = 18f;
    const float FallbackWidth = 720f;
    const int Samples = 96;

    readonly Signal<int> _active = new(5);
    readonly Signal<int> _hover = new(-1);
    int _dragBand = -1;

    public override Element Render()
    {
        var p = UsePropsOrDefault<WaveeEqualizerCurve.Props>();
        var measuredW = UseMeasuredWidth(1f);   // self-measured root width (replaces the hand OnBoundsChanged→signal mirror)
        if (p is null) return new BoxEl { MinHeight = 250f };

        float measured = measuredW.Value;
        float width = measured > 0.5f ? measured : FallbackWidth;
        int active = Math.Clamp(_active.Value, 0, 9);
        int hover = _hover.Value;
        return new BoxEl
        {
            Direction = 1,
            Children = [BuildSurface(p, MathF.Max(width, 260f), active, hover)],
        };
    }

    Element BuildSurface(WaveeEqualizerCurve.Props p, float width, int active, int hover)
    {
        float height = p.Height ?? Math.Clamp(width * 0.38f, 252f, 360f);
        float plotW = MathF.Max(120f, width - PadLeft - PadRight);
        float plotH = MathF.Max(120f, height - PadTop - PadBottom);
        float zeroY = PadTop + GainToY(0f, plotH);
        bool enabled = p.IsEnabled;

        void Commit(int band, float y)
        {
            if (!enabled) return;
            band = Math.Clamp(band, 0, 9);
            _dragBand = band;
            _active.Value = band;
            float gain = MathF.Round(YToGain(y - PadTop, plotH) * 2f) * 0.5f;
            p.OnBandChanged(band, gain);
        }

        void Down(Point2 pt) => Commit(NearestBand(pt.X, plotW), pt.Y);
        void Drag(Point2 pt)
        {
            int band = _dragBand >= 0 ? _dragBand : NearestBand(pt.X, plotW);
            Commit(band, pt.Y);
        }

        void Hover(Point2 pt)
        {
            int band = NearestBand(pt.X, plotW);
            _hover.Value = band;
        }

        void Exit()
        {
            _dragBand = -1;
            _hover.Value = -1;
        }

        void Key(KeyEventArgs e)
        {
            if (!enabled) return;
            int band = Math.Clamp(_active.Peek(), 0, 9);
            float current = GainAtBand(p.Gains, band);
            float next = current;
            switch (e.KeyCode)
            {
                case Keys.Left:  band = Math.Max(0, band - 1); _active.Value = band; e.Handled = true; return;
                case Keys.Right: band = Math.Min(9, band + 1); _active.Value = band; e.Handled = true; return;
                case Keys.Up: next = current + 0.5f; break;
                case Keys.Down: next = current - 0.5f; break;
                case Keys.PageUp: next = current + 3f; break;
                case Keys.PageDown: next = current - 3f; break;
                case Keys.Home: next = 0f; break;
                case Keys.End: next = current >= 0f ? MinGain : MaxGain; break;
                default: return;
            }
            e.Handled = true;
            _active.Value = band;
            p.OnBandChanged(band, Math.Clamp(next, MinGain, MaxGain));
        }

        var kids = new List<Element>(240);
        AddGrid(kids, plotW, plotH);
        AddFill(kids, p.Gains, plotW, plotH, zeroY, enabled);
        AddCurve(kids, p.Gains, plotW, plotH, enabled);
        AddNodes(kids, p.Gains, plotW, plotH, active, hover, enabled);
        AddFrequencyLabels(kids, plotW, height);
        AddValuePill(kids, p.Gains, plotW, plotH, active, width, height);

        return new BoxEl
        {
            Width = width,
            Height = height,
            ZStack = true,
            ClipToBounds = true,
            Corners = CornerRadius4.All(8f),
            Fill = Tok.FillCardDefault,
            BorderWidth = 1f,
            BorderColor = Tok.StrokeCardDefault,
            Opacity = enabled ? 1f : 0.58f,
            Role = AutomationRole.Slider,
            Focusable = enabled,
            IsEnabled = enabled,
            Cursor = enabled ? CursorId.Hand : null,
            FocusVisualMargin = Edges4.All(-3f),
            OnPointerDown = enabled ? Down : null,
            OnDrag = enabled ? Drag : null,
            OnHoverMove = enabled ? Hover : null,
            OnPointerExit = enabled ? Exit : null,
            OnKeyDown = enabled ? Key : null,
            Children = kids.ToArray(),
        };
    }

    static void AddGrid(List<Element> kids, float plotW, float plotH)
    {
        float[] gains = [12f, 6f, 0f, -6f, -12f];
        for (int i = 0; i < gains.Length; i++)
        {
            float g = gains[i];
            bool zero = MathF.Abs(g) < 0.01f;
            float y = PadTop + GainToY(g, plotH);
            kids.Add(new BoxEl
            {
                Width = 34f,
                Height = 14f,
                OffsetX = 2f,
                OffsetY = y - 7f,
                Children =
                [
                    new TextEl(g == 0f ? "0 dB" : g.ToString("+0;-0", CultureInfo.InvariantCulture))
                    {
                        Size = 10f,
                        Weight = zero ? (ushort)650 : (ushort)400,
                        FontFamily = "Cascadia Code",
                        Color = zero ? Tok.TextSecondary : Tok.TextTertiary,
                    },
                ],
            });
            AddDashedH(kids, PadLeft, y, plotW, zero ? Tok.TextSecondary with { A = 0.34f } : Tok.StrokeDividerDefault, zero ? 1.2f : 1f, zero ? 5f : 2.5f, zero ? 5f : 5f);
        }

        for (int i = 0; i < 10; i++)
        {
            float x = PadLeft + (i / 9f) * plotW;
            AddDashedV(kids, x, PadTop, plotH, Tok.StrokeDividerDefault with { A = 0.58f }, 1f, 2f, 5f);
        }
    }

    static void AddFill(List<Element> kids, float[] gains, float plotW, float plotH, float zeroY, bool enabled)
    {
        ColorF fill = (enabled ? Tok.AccentDefault : Tok.TextDisabled) with { A = enabled ? 0.15f : 0.10f };
        float lastX = PadLeft;
        float lastY = PadTop + GainToY(SampleGain(gains, 0f), plotH);
        for (int s = 1; s < Samples; s++)
        {
            float u = (s / (float)(Samples - 1)) * 9f;
            float x = PadLeft + (u / 9f) * plotW;
            float y = PadTop + GainToY(SampleGain(gains, u), plotH);
            float midX = (lastX + x) * 0.5f;
            float midY = (lastY + y) * 0.5f;
            float stripW = MathF.Max(1.5f, x - lastX + 0.75f);
            float top = MathF.Min(midY, zeroY);
            float h = MathF.Abs(midY - zeroY);
            if (h > 0.5f)
                kids.Add(new BoxEl
                {
                    Width = stripW,
                    Height = h,
                    OffsetX = midX - stripW * 0.5f,
                    OffsetY = top,
                    Fill = fill,
                });
            lastX = x;
            lastY = y;
        }
    }

    static void AddCurve(List<Element> kids, float[] gains, float plotW, float plotH, bool enabled)
    {
        ColorF under = (enabled ? Tok.AccentDefault : Tok.TextDisabled) with { A = enabled ? 0.28f : 0.22f };
        ColorF main = enabled ? Tok.AccentDefault : Tok.TextDisabled;
        Point2 last = CurvePoint(gains, 0f, plotW, plotH);
        for (int s = 1; s < Samples; s++)
        {
            float u = (s / (float)(Samples - 1)) * 9f;
            Point2 pt = CurvePoint(gains, u, plotW, plotH);
            kids.Add(Line(last, pt, under, 5.5f));
            kids.Add(Line(last, pt, main, 2.5f));
            last = pt;
        }
    }

    static void AddNodes(List<Element> kids, float[] gains, float plotW, float plotH, int active, int hover, bool enabled)
    {
        for (int i = 0; i < 10; i++)
        {
            float gain = GainAtBand(gains, i);
            float x = PadLeft + (i / 9f) * plotW;
            float y = PadTop + GainToY(gain, plotH);
            bool hot = enabled && (i == active || i == hover);
            float d = hot ? NodeHot : NodeRest;
            kids.Add(new BoxEl
            {
                Width = d,
                Height = d,
                OffsetX = x - d * 0.5f,
                OffsetY = y - d * 0.5f,
                Corners = Radii.Circle(d),
                Fill = enabled ? Tok.FillControlSolid : Tok.FillControlDisabled,
                BorderWidth = hot ? 3f : 2.5f,
                BorderBrush = enabled ? Tok.ControlElevationBorder : GradientSpec.Solid(Tok.StrokeControlDefault),
                BorderColor = enabled ? Tok.AccentDefault : Tok.TextDisabled,
                Shadow = hot ? Elevation.Flyout : null,
                BrushTransitionMs = WaveeMotion.Faster,
            });
        }
    }

    static void AddFrequencyLabels(List<Element> kids, float plotW, float height)
    {
        bool dense = plotW < 420f;
        for (int i = 0; i < 10; i++)
        {
            if (dense && (i % 2) != 0 && i != 9) continue;
            float x = PadLeft + (i / 9f) * plotW;
            kids.Add(new BoxEl
            {
                Width = 42f,
                Height = 14f,
                OffsetX = x - 21f,
                OffsetY = height - 24f,
                Children =
                [
                    new TextEl(WaveeEqualizerCurve.FrequencyLabels[i])
                    {
                        Size = 10f,
                        FontFamily = "Cascadia Code",
                        Color = Tok.TextTertiary,
                    },
                ],
            });
        }
    }

    static void AddValuePill(List<Element> kids, float[] gains, float plotW, float plotH, int active, float width, float height)
    {
        if ((uint)active >= 10u) return;
        float gain = GainAtBand(gains, active);
        float x = PadLeft + (active / 9f) * plotW;
        float y = PadTop + GainToY(gain, plotH);
        float pillW = 104f;
        float px = Math.Clamp(x - pillW * 0.5f, 6f, MathF.Max(6f, width - pillW - 6f));
        float py = Math.Clamp(y - 42f, 6f, MathF.Max(6f, height - 58f));
        kids.Add(new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Gap = 5f,
            Width = pillW,
            Height = 28f,
            OffsetX = px,
            OffsetY = py,
            Padding = new Edges4(8f, 0f, 8f, 0f),
            Corners = CornerRadius4.All(14f),
            Fill = Tok.FillControlDefault,
            BorderWidth = 1f,
            BorderBrush = Tok.ControlElevationBorder,
            Shadow = Elevation.Tooltip,
            Children =
            [
                new TextEl(WaveeEqualizerCurve.FrequencyLabels[active])
                {
                    Size = 11f,
                    FontFamily = "Cascadia Code",
                    Color = Tok.TextSecondary,
                    Shrink = 0f,
                },
                new TextEl(FormatDb(gain))
                {
                    Size = 12f,
                    Weight = 700,
                    Color = Tok.TextPrimary,
                    Grow = 1f,
                    MaxLines = 1,
                    Trim = TextTrim.CharacterEllipsis,
                },
            ],
        });
    }

    static Element Line(Point2 a, Point2 b, ColorF color, float thickness)
        => new PolylineStrokeEl
        {
            Width = MathF.Abs(b.X - a.X) + thickness,
            Height = MathF.Abs(b.Y - a.Y) + thickness,
            OffsetX = MathF.Min(a.X, b.X) - thickness * 0.5f,
            OffsetY = MathF.Min(a.Y, b.Y) - thickness * 0.5f,
            P0 = new Point2(a.X - MathF.Min(a.X, b.X) + thickness * 0.5f, a.Y - MathF.Min(a.Y, b.Y) + thickness * 0.5f),
            P1 = new Point2(b.X - MathF.Min(a.X, b.X) + thickness * 0.5f, b.Y - MathF.Min(a.Y, b.Y) + thickness * 0.5f),
            PointCount = 2,
            Color = color,
            Thickness = thickness,
            RoundCaps = true,
        };

    static void AddDashedH(List<Element> kids, float x, float y, float w, ColorF color, float h, float dash, float gap)
    {
        for (float dx = 0f; dx < w; dx += dash + gap)
            kids.Add(new BoxEl
            {
                Width = MathF.Min(dash, w - dx),
                Height = h,
                OffsetX = x + dx,
                OffsetY = y,
                Fill = color,
            });
    }

    static void AddDashedV(List<Element> kids, float x, float y, float h, ColorF color, float w, float dash, float gap)
    {
        for (float dy = 0f; dy < h; dy += dash + gap)
            kids.Add(new BoxEl
            {
                Width = w,
                Height = MathF.Min(dash, h - dy),
                OffsetX = x,
                OffsetY = y + dy,
                Fill = color,
            });
    }

    static Point2 CurvePoint(float[] gains, float u, float plotW, float plotH)
        => new(PadLeft + (u / 9f) * plotW, PadTop + GainToY(SampleGain(gains, u), plotH));

    static float SampleGain(float[] gains, float u)
    {
        if (u <= 0f) return GainAtBand(gains, 0);
        if (u >= 9f) return GainAtBand(gains, 9);
        int i = Math.Clamp((int)MathF.Floor(u), 0, 8);
        float t = u - i;
        float p0 = GainAtBand(gains, Math.Max(0, i - 1));
        float p1 = GainAtBand(gains, i);
        float p2 = GainAtBand(gains, i + 1);
        float p3 = GainAtBand(gains, Math.Min(9, i + 2));
        float t2 = t * t;
        float t3 = t2 * t;
        return Math.Clamp(0.5f * ((2f * p1) + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3), MinGain, MaxGain);
    }

    static int NearestBand(float x, float plotW)
    {
        float n = Math.Clamp((x - PadLeft) / MathF.Max(plotW, 1f), 0f, 1f);
        return Math.Clamp((int)MathF.Round(n * 9f), 0, 9);
    }

    static float GainAtBand(float[] gains, int band)
        => (uint)band < 10u && band < gains.Length ? Math.Clamp(gains[band], MinGain, MaxGain) : 0f;

    static float GainToY(float gain, float plotH)
        => (MaxGain - Math.Clamp(gain, MinGain, MaxGain)) / (MaxGain - MinGain) * plotH;

    static float YToGain(float y, float plotH)
        => Math.Clamp(MaxGain - (Math.Clamp(y, 0f, plotH) / MathF.Max(plotH, 1f)) * (MaxGain - MinGain), MinGain, MaxGain);

    static string FormatDb(float gain)
        => gain.ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture) + " dB";
}
