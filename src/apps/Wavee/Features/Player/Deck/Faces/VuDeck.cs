using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Render;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>
/// The hi-fi VU deck: two lit meter faces over an amber LCD strip.
///
/// <para><b>Two writes a tick.</b> <c>MeterModel</c> puts the left and right needle angles in
/// <c>DeckSignals.Angle0</c>/<c>Angle1</c>; each needle is one <c>BoxEl</c> whose pivot is its own bottom edge and
/// whose only bound channel is a rotation. Nothing else on this face moves at tick rate — the LCD's two clocks are
/// 1 Hz bound text at a fixed width, and the scale is static geometry.</para>
///
/// <para><b>The scale is three interned paths, not thirty boxes.</b> The dial arc, the red overload arc and the ten
/// tick marks are authored once in the mockup's 100x82 view box and registered with <c>PathGeometryTable</c>, so
/// they are parsed and tessellated exactly ONCE for the process however many times a deck mounts. The ten numbers
/// are <c>TextEl</c>s placed on the same arc (there is no text-on-path).</para>
///
/// <para>The <c>ballistics</c> option (VU vs PPM) is a MODEL choice — the needle time constant — so this face draws
/// the same pixels either way; <c>DeckModels</c> reads it.</para>
/// </summary>
static class VuDeck
{
    const float ViewW = 100f, ViewH = 82f;

    /// <summary>The dial's labelled points, in order along the arc from -45 deg to +45 deg in 10 deg steps (the
    /// mockup's table). Anything above 0 is in the red.</summary>
    static readonly int[] TickValues = [-20, -10, -7, -5, -3, -1, 0, 1, 2, 3];

    static readonly PathData InkScale = Intern(BuildScale(red: false));
    static readonly PathData RedArc = Intern("M68 32 A40 40 0 0 1 86 62");
    static readonly PathData RedTicks = Intern(BuildScale(red: true));

    static readonly FormatCache<int> ElapsedCache = FormatCache.Create<int>();
    static readonly FormatCache<int> RemainingCache = FormatCache.Create<int>();

    public static Element Build(in NpvPlayerCatalog.Preset preset, IAppSettings? settings, float side,
                                DeckSignals sig, PlaybackBridge bridge, DeckHost host)
    {
        string face = NpvPlayerPrefs.ChoiceSlug(settings, preset, "face");
        ColorF plate = face switch { "blue" => Hex(0x0A2A5A), "black" => Hex(0x141519), _ => Hex(0xEFE6CF) };
        ColorF ink = face switch { "blue" => Hex(0x9FD6FF), "black" => Hex(0xF0F0F0), _ => Hex(0x222222) };
        ColorF red = face switch { "blue" => Hex(0xFF6A4A), "black" => Hex(0xFF4A3A), _ => Hex(0xC8321E) };
        ColorF glow = face switch
        {
            "blue" => new ColorF(120f / 255f, 200f / 255f, 1f, 0.25f),
            "black" => new ColorF(1f, 1f, 1f, 0.12f),
            _ => new ColorF(1f, 200f / 255f, 120f / 255f, 0.25f),
        };

        float mw = 0.43f * side, mh = mw * 0.82f;
        float top = 0.12f * side;
        float lcdW = 0.90f * side, lcdH = 0.20f * side;
        float knob = 0.09f * side;

        return Canvas.Create(side, side,
        [
            new CanvasChild(0.05f * side, top, Meter(side, mw, mh, plate, ink, red, glow, sig.Angle0, "LEFT")),
            new CanvasChild(side - 0.05f * side - mw, top, Meter(side, mw, mh, plate, ink, red, glow, sig.Angle1, "RIGHT")),
            new CanvasChild((side - knob) * 0.5f, 0.60f * side, Knob(knob)),
            new CanvasChild(0.05f * side, side * 0.91f - lcdH, Lcd(side, lcdW, lcdH, bridge)),
        ]) with
        {
            Gradient = GradientSpec.Vertical(Hex(0x1A1B20), Hex(0x0F1013)),
        };
    }

    // ── one meter ───────────────────────────────────────────────────────────────────────────────────────────────

    static Element Meter(float side, float w, float h, ColorF plate, ColorF ink, ColorF red, ColorF glow,
                         FluentGpu.Signals.FloatSignal angle, string channel)
    {
        float k = w / ViewW;                       // the view box's uniform fit — both axes agree (100:82 == 1:.82)
        float label = MathF.Max(5f, 5f * k);
        float needleH = 0.78f * h;
        float pivot = 0.08f * w;
        float vu = MathF.Max(8f, side * 0.040f);
        float chan = MathF.Max(6f, side * 0.026f);

        var kids = new List<CanvasChild>(18)
        {
            // The lamp behind the dial — a radial wash rising from the bottom edge, the way a lit meter glows.
            new(0f, 0f, new BoxEl
            {
                Width = w, Height = h, Shrink = 0f, HitTestVisible = false,
                Gradient = new GradientSpec(GradientShape.Radial, 0f,
                    [new GradientStop(0f, glow), new GradientStop(1f, glow with { A = 0f })])
                {
                    RadialCenter = new Point2(0.5f, 1f), RadialRadius = new Point2(0.6f, 0.4f),
                },
            }),
            new(0f, 0f, new PathEl
            {
                Width = w, Height = h, ViewBoxW = ViewW, ViewBoxH = ViewH, Geometry = InkScale,
                StrokeColor = ink, Stroke = new StrokeStyle(1.2f, LineCap.Round, LineJoin.Round),
            }),
            new(0f, 0f, new PathEl
            {
                Width = w, Height = h, ViewBoxW = ViewW, ViewBoxH = ViewH, Geometry = RedArc,
                StrokeColor = red, Stroke = new StrokeStyle(3f, LineCap.Butt, LineJoin.Round),
            }),
            new(0f, 0f, new PathEl
            {
                Width = w, Height = h, ViewBoxW = ViewW, ViewBoxH = ViewH, Geometry = RedTicks,
                StrokeColor = red, Stroke = new StrokeStyle(1f, LineCap.Round, LineJoin.Round),
            }),
        };

        for (int i = 0; i < TickValues.Length; i++)
        {
            int v = TickValues[i];
            string text = v > 0 ? "+" + v.ToString(CultureInfo.InvariantCulture) : v.ToString(CultureInfo.InvariantCulture);
            float a = (-45f + i * 10f) * DeckArt.Deg2Rad;
            float tx = (50f + 30f * MathF.Sin(a)) * k;
            float ty = (62f - 30f * MathF.Cos(a)) * k;
            // No text-anchor in the layout engine: centre the run by its own estimated advance.
            float halfW = text.Length * label * 0.30f;
            kids.Add(new CanvasChild(tx - halfW, ty - label * 0.62f, new TextEl(text)
            {
                // Start, not the container's default Stretch: a Canvas child with no explicit height would
                // otherwise be stretched to the whole meter face.
                Size = label, Color = v > 0 ? red : ink, Wrap = TextWrap.NoWrap, AlignSelf = FlexAlign.Start,
            }));
        }

        kids.Add(new CanvasChild(0.06f * w, 0.06f * h, new TextEl(channel)
        {
            Size = chan, Weight = 700, Color = ink with { A = ink.A * 0.7f }, Wrap = TextWrap.NoWrap,
            AlignSelf = FlexAlign.Start,
        }));
        kids.Add(new CanvasChild((w - vu * 1.9f) * 0.5f, 0.52f * h, new BoxEl
        {
            Width = vu * 1.9f, Height = MathF.Ceiling(vu * 1.4f), Shrink = 0f,
            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Children = [new TextEl("VU") { Size = vu, Weight = 700, Color = ink, CharSpacing = 100f, Wrap = TextWrap.NoWrap }],
        }));
        // The needle: pivoted on its own bottom-centre, one bound rotation, nothing else.
        kids.Add(new CanvasChild(w * 0.5f - 0.75f, h * 0.90f - needleH, new BoxEl
        {
            Width = 1.5f, Height = needleH, Shrink = 0f, Fill = ink, HitTestVisible = false,
            TransformOriginX = 0.5f, TransformOriginY = 1f,
            Transform = Prop.Of(() => Affine2D.Rotation(angle.Value * DeckArt.Deg2Rad)),
        }));
        kids.Add(new CanvasChild((w - pivot) * 0.5f, h * 0.94f - pivot, new BoxEl
        {
            Width = pivot, Height = pivot, Shrink = 0f, Corners = Radii.Circle(pivot),
            Gradient = new GradientSpec(GradientShape.Radial, 0f,
                [new GradientStop(0f, Hex(0x555555)), new GradientStop(1f, Hex(0x111111))]),
        }));

        return Canvas.Create(w, h, kids) with
        {
            Fill = plate,
            Corners = new CornerRadius4(6f, 6f, 14f, 14f),
            BorderWidth = 1f, BorderColor = Hex(0x000000, 0.5f),
            Shadow = new ShadowSpec(20f, 8f, 0f, Hex(0x000000, 0.5f)),
        };
    }

    // ── the amber strip ─────────────────────────────────────────────────────────────────────────────────────────

    static Element Lcd(float side, float w, float h, PlaybackBridge b)
    {
        ColorF amber = Hex(0xFFB000);
        float big = MathF.Max(10f, side * 0.055f);
        float smallSize = MathF.Max(6f, side * 0.028f);
        float padX = 0.04f * w, padY = 0.03f * h;
        float clockW = w * 0.34f;

        var content = new BoxEl
        {
            Width = w, Height = h, Shrink = 0f, Direction = 1, Justify = FlexJustify.SpaceBetween,
            Padding = new Edges4(padX, padY, padX, padY),
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Justify = FlexJustify.SpaceBetween, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        Clock(clockW, big, amber, b, false),
                        Clock(clockW, big, amber, b, true),
                    ],
                },
                new BoxEl
                {
                    Direction = 0, Justify = FlexJustify.SpaceBetween, AlignItems = FlexAlign.Center, Gap = 8f,
                    Children =
                    [
                        new TextEl(Prop.Of(() => NowLine(b)))
                        {
                            Size = smallSize, Color = amber with { A = 0.85f }, CharSpacing = 140f,
                            Grow = 1f, Shrink = 1f, MinWidth = 0f,
                            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        },
                        new TextEl(Prop.Of(() => FormatLine(b)))
                        {
                            Size = smallSize, Color = amber with { A = 0.85f }, CharSpacing = 140f,
                            Shrink = 0f, Wrap = TextWrap.NoWrap, MaxLines = 1,
                        },
                    ],
                },
            ],
        };

        return new BoxEl
        {
            Width = w, Height = h, Shrink = 0f, ZStack = true, ClipToBounds = true,
            Fill = Hex(0x1A0D00), Corners = CornerRadius4.All(4f),
            BorderWidth = 1f, BorderColor = Hex(0x3A2000),
            Children =
            [
                // Text carries no shadow channel, so the LCD's amber bloom is a wash BEHIND the glyphs rather than a
                // glow around them — the same read at a fraction of the cost.
                new BoxEl
                {
                    Width = w, Height = h, Shrink = 0f, HitTestVisible = false,
                    Gradient = new GradientSpec(GradientShape.Radial, 0f,
                        [new GradientStop(0f, amber with { A = 0.12f }), new GradientStop(1f, amber with { A = 0f })])
                    {
                        RadialCenter = new Point2(0.5f, 0.35f), RadialRadius = new Point2(0.7f, 0.8f),
                    },
                },
                content,
            ],
        };
    }

    static Element Clock(float w, float size, ColorF amber, PlaybackBridge b, bool remaining) => new BoxEl
    {
        Width = w, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center,
        Justify = remaining ? FlexJustify.End : FlexJustify.Start,
        Children =
        [
            new TextEl(Prop.Of(() => remaining ? RemainingText(b) : ElapsedText(b)))
            {
                Size = size, Color = amber, FontFamily = "Consolas", CharSpacing = 60f,
                Wrap = TextWrap.NoWrap, MaxLines = 1,
            },
        ],
    };

    static Element Knob(float d) => Canvas.Create(d, d,
    [
        new CanvasChild(d * 0.5f - 1f, d * 0.08f, new BoxEl
        {
            Width = 2f, Height = d * 0.30f, Shrink = 0f, Fill = Hex(0xFFFFFF),
        }),
    ]) with
    {
        Corners = Radii.Circle(d),
        Gradient = new GradientSpec(GradientShape.Radial, 0f,
            [new GradientStop(0f, Hex(0x8A8F99)), new GradientStop(1f, Hex(0x2B2E35))])
        {
            RadialCenter = new Point2(0.4f, 0.35f), RadialRadius = new Point2(0.7f, 0.7f),
        },
        Shadow = new ShadowSpec(8f, 3f, 0f, Hex(0x000000, 0.6f)),
    };

    // ── bound text ──────────────────────────────────────────────────────────────────────────────────────────────

    static long PlayheadMs(PlaybackBridge b) => b.ScrubTargetMs.Value ?? b.SeekTargetMs.Value ?? b.PositionMs.Value;

    static string ElapsedText(PlaybackBridge b)
        => ElapsedCache.Get((int)Math.Max(0L, PlayheadMs(b) / 1000L), static s => Mmss(s));

    static string RemainingText(PlaybackBridge b)
        => RemainingCache.Get((int)Math.Max(0L, (b.DurationMs.Value - PlayheadMs(b)) / 1000L), static s => "-" + Mmss(s));

    static string Mmss(int s)
        => (s / 60).ToString("D2", CultureInfo.InvariantCulture) + ":" + (s % 60).ToString("D2", CultureInfo.InvariantCulture);

    static string NowLine(PlaybackBridge b)
    {
        var t = b.CurrentTrack.Value;
        if (t is null) return "";
        string artist = t.Artists.Count > 0 ? t.Artists[0].Name : "";
        return artist.Length == 0 ? t.Title.ToUpperInvariant() : (t.Title + " · " + artist).ToUpperInvariant();
    }

    /// <summary>The right-hand technical cell. The bridge publishes a codec name and a bitrate, not a sample rate,
    /// so the strip states those rather than inventing "44.1 kHz".</summary>
    static string FormatLine(PlaybackBridge b)
    {
        string? format = b.StreamFormat.Value;
        int kbps = b.StreamBitrateKbps.Value;
        if (string.IsNullOrEmpty(format)) return kbps > 0 ? kbps.ToString(CultureInfo.InvariantCulture) + " KBPS" : "";
        return kbps > 0
            ? format.ToUpperInvariant() + " · " + kbps.ToString(CultureInfo.InvariantCulture) + " KBPS"
            : format.ToUpperInvariant();
    }

    // ── static geometry ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The dial arc (ink only) plus the tick marks on one side of 0 dB, as SVG path data in the mockup's
    /// 100x82 view box. Built once at type init and interned, so the tessellation cache never misses.</summary>
    static string BuildScale(bool red)
    {
        var sb = new StringBuilder(red ? 96 : 256);
        if (!red) sb.Append("M14 62 A40 40 0 0 1 86 62");
        for (int i = 0; i < TickValues.Length; i++)
        {
            if (TickValues[i] > 0 != red) continue;
            float a = (-45f + i * 10f) * MathF.PI / 180f;
            float sin = MathF.Sin(a), cos = MathF.Cos(a);
            sb.Append(" M").Append(N(50f + 40f * sin)).Append(' ').Append(N(62f - 40f * cos))
              .Append(" L").Append(N(50f + 35f * sin)).Append(' ').Append(N(62f - 35f * cos));
        }
        return sb.ToString();
    }

    static string N(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    static PathData Intern(string d)
    {
        int id = PathGeometryTable.Shared.Register(d, ViewW, ViewH, FillRule.NonZero);
        PathGeometryTable.Shared.TryGet(id, out var data);
        return data;
    }

    static ColorF Hex(uint rgb, float a = 1f)
        => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, a);
}
