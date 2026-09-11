using System;
using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>
/// Winamp: two 275:116 windows — the player and the equalizer — stacked in the square.
///
/// <para><b>The analyser is the deck's only fast channel.</b> <c>LevelModel</c> fills 19 bands; every bar binds ONE
/// of them as a <c>ScaleY</c> about its bottom edge and its 1 px cap as a translation, so a 30 Hz spectrum costs 38
/// compositor writes and not a single re-render. The equalizer window's bars bind the SAME signals as the player
/// window's — that is why they move together, exactly as a real Winamp's do.</para>
///
/// <para><b>Everything else is slow.</b> The time read-out is a 1 Hz bound <c>Text</c> at a fixed width, the title
/// is a <see cref="Marquee"/> over a bound string (the control re-measures itself when the track changes; no
/// component re-render), the bitrate/format cells read the bridge's stream facts, and the position slider is one
/// bound translation.</para>
///
/// <para>Geometry and colours come from <c>npv-player-styles-mica.html</c> (<c>.wa*</c>); the title bar's stripe
/// pattern is the bundled <c>winamp-tbar.png</c> because the renderer has no repeating gradient.</para>
/// </summary>
static class WinampDeck
{
    const int SpectrumBands = 19;
    const int ScopeDots = 24;

    static readonly FormatCache<int> TimeCache = FormatCache.Create<int>();
    static readonly FormatCache<int> BitrateCache = FormatCache.Create<int>();

    public static Element Build(in NpvPlayerCatalog.Preset preset, IAppSettings? settings, float side,
                                DeckSignals sig, PlaybackBridge bridge, DeckHost host)
    {
        string skin = NpvPlayerPrefs.ChoiceSlug(settings, preset, "skin");
        bool scope = NpvPlayerPrefs.ChoiceSlug(settings, preset, "vis") == "scope";

        ColorF wa1 = skin switch { "modern" => Hex(0x4A4F57), "dark" => Hex(0x1A1C22), _ => Hex(0x3B4459) };
        ColorF wa2 = skin switch { "modern" => Hex(0x2B2E34), "dark" => Hex(0x0E0F13), _ => Hex(0x222A3A) };
        ColorF wa3 = skin switch { "modern" => Hex(0x8C9199), "dark" => Hex(0x5B5F6B), _ => Hex(0x6E7A97) };
        ColorF lcd = skin switch { "modern" => Hex(0xE5F3FF), "dark" => Hex(0xFF8A00), _ => Hex(0x00FF00) };

        float w = 0.92f * side;
        float h = w * 116f / 275f;
        float mainY = 0.10f * side;
        float eqY = mainY + h + 0.02f * side;

        return Canvas.Create(side, side,
        [
            new CanvasChild(0.04f * side, mainY, MainWindow(side, w, h, wa1, wa2, wa3, lcd, scope, sig, bridge)),
            new CanvasChild(0.04f * side, eqY, EqWindow(side, w, h, wa1, wa2, wa3, lcd, scope, sig)),
        ]) with { Fill = Hex(0x0F1218) };
    }

    // ── the player window ───────────────────────────────────────────────────────────────────────────────────────

    static Element MainWindow(float side, float w, float h, ColorF wa1, ColorF wa2, ColorF wa3, ColorF lcd,
                              bool scope, DeckSignals sig, PlaybackBridge bridge)
    {
        float tbH = 0.12f * h;
        float small = MathF.Max(6f, side * 0.024f);
        float lcdSize = MathF.Max(9f, side * 0.064f);
        float stSize = MathF.Max(6f, side * 0.026f);

        float timeX = 0.14f * w, timeY = 0.20f * h, timeW = 0.32f * w, timeH = 0.30f * h;
        float visX = 0.50f * w, visW = 0.34f * w;
        float stX = 0.14f * w, stY = 0.54f * h, stW = 0.80f * w, stH = 0.12f * h;
        float posX = 0.06f * w, posY = 0.82f * h, posW = 0.88f * w, posH = 0.06f * h;
        const float ThumbW = 12f;
        float btnW = MathF.Max(6f, side * 0.044f), btnH = MathF.Max(5f, side * 0.030f);

        var frac = sig.Frac;

        var kids = new List<CanvasChild>(12)
        {
            new(0f, 0f, TitleBar(w, tbH, wa1, wa3, small, "WINAMP")),
            // Time read-out: a fixed-width slot, so a 9:59 -> 10:00 roll cannot nudge the analyser beside it.
            new(timeX, timeY, new BoxEl
            {
                Width = timeW, Height = timeH, Shrink = 0f, Fill = Hex(0x000000),
                BorderWidth = 1f, BorderColor = Hex(0x000000),
                Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children =
                [
                    new TextEl(Prop.Of(() => ClockText(bridge)))
                    {
                        Size = lcdSize, Weight = 700, Color = lcd, FontFamily = "Consolas",
                        CharSpacing = 60f, Wrap = TextWrap.NoWrap, MaxLines = 1,
                    },
                ],
            }),
            new(visX, timeY, new BoxEl
            {
                Width = visW, Height = timeH, Shrink = 0f, Fill = Hex(0x000000),
                BorderWidth = 1f, BorderColor = Hex(0x000000), ClipToBounds = true,
                Children = [scope ? Scope(visW - 2f, timeH - 2f, sig, lcd) : Bars(visW - 2f, timeH - 2f, sig, lcd, 1f, 2f)],
            }),
            new(stX, stY, new BoxEl
            {
                Width = stW, Height = stH, Shrink = 0f, Fill = Hex(0x000000),
                BorderWidth = 1f, BorderColor = Hex(0x000000), ClipToBounds = true,
                Direction = 0, AlignItems = FlexAlign.Center, Padding = new Edges4(3f, 0f, 3f, 0f),
                Children =
                [
                    Marquee.Of(Prop.Of(() => ScrollTitle(bridge)), new Marquee.Style
                    {
                        FontSize = stSize, Weight = 400, Foreground = lcd, FontFamily = "Arial",
                        Speed = 22f, Gap = 24f, FadeBand = 0f, StartDelayMs = 0f,
                        Mode = Marquee.ScrollMode.Loop, Trigger = Marquee.TriggerMode.Always,
                    }),
                ],
            }),
            new(stX, 0.69f * h, Cell(Prop.Of(() => KbpsText(bridge)), small, lcd)),
            new(0.34f * w, 0.69f * h, Cell(Prop.Of(() => KhzText(bridge)), small, lcd)),
            new(w - 0.08f * w - small * 4.2f, 0.69f * h, new BoxEl
            {
                Width = small * 4.2f, Height = MathF.Ceiling(small * 1.5f), Shrink = 0f,
                Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End,
                Children = [new TextEl("STEREO") { Size = small, Color = lcd, CharSpacing = 100f, Wrap = TextWrap.NoWrap }],
            }),
            new(posX, posY, new BoxEl { Width = posW, Height = posH, Shrink = 0f, Fill = Hex(0x000000) }),
            new(posX, posY - posH * 0.4f, new BoxEl
            {
                Width = ThumbW, Height = posH * 1.8f, Shrink = 0f,
                Gradient = new GradientSpec(GradientShape.Linear, 0f,
                    [new GradientStop(0f, Hex(0x8A92A6)), new GradientStop(1f, Hex(0x4E5568))]),
                BorderWidth = 1f, BorderColor = Hex(0x000000),
                Transform = Prop.Of(() => Affine2D.Translation(Clamp01(frac.Value) * MathF.Max(0f, posW - ThumbW), 0f)),
            }),
            new(posX, 0.90f * h, Transport(btnW, btnH, bridge)),
        };

        return Frame(w, h, wa2, wa3, kids);
    }

    // ── the equalizer window ────────────────────────────────────────────────────────────────────────────────────

    static Element EqWindow(float side, float w, float h, ColorF wa1, ColorF wa2, ColorF wa3, ColorF lcd,
                            bool scope, DeckSignals sig)
    {
        float tbH = 0.12f * h;
        float small = MathF.Max(6f, side * 0.024f);
        float gx = 0.06f * w, gy = 0.18f * h, gw = 0.88f * w, gh = 0.68f * h;

        var kids = new List<CanvasChild>(4)
        {
            new(0f, 0f, TitleBar(w, tbH, wa1, wa3, small, scope ? "WINAMP OSCILLOSCOPE" : "WINAMP SPECTRUM ANALYZER")),
            new(gx, gy, new BoxEl
            {
                Width = gw, Height = gh, Shrink = 0f, Fill = Hex(0x000000),
                BorderWidth = 1f, BorderColor = Hex(0x000000), ClipToBounds = true,
                Children = [scope ? Scope(gw - 2f, gh - 2f, sig, lcd) : Bars(gw - 2f, gh - 2f, sig, lcd, 2f, 3f)],
            }),
            // The 10 hairlines are the panel's own grid, drawn OVER the bars (a repeating gradient does not exist).
            new(gx, gy, Grid(gw, gh)),
        };

        return Frame(w, h, wa2, wa3, kids);
    }

    // ── shared parts ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The window plate: the skin fill, a hard black outline and the 1 px light inner bevel that gives a
    /// Winamp window its "cut out of metal" edge.</summary>
    static Element Frame(float w, float h, ColorF wa2, ColorF wa3, List<CanvasChild> kids)
    {
        // LAST, so the bevel reads as the window's edge rather than being overpainted by the title bar.
        kids.Add(new CanvasChild(1f, 1f, new BoxEl
        {
            Width = MathF.Max(0f, w - 2f), Height = MathF.Max(0f, h - 2f), Shrink = 0f,
            BorderWidth = 1f, BorderColor = wa3 with { A = 0.45f }, HitTestVisible = false,
        }));
        return Canvas.Create(w, h, kids) with
        {
            Fill = wa2, BorderWidth = 1f, BorderColor = Hex(0x000000),
        };
    }

    /// <summary>The striped title bar. The stripes are a bundled 256x16 alpha PNG tinted with the skin's highlight —
    /// <c>repeating-linear-gradient</c> has no renderer equivalent, and a per-stripe BoxEl would be 100 nodes.</summary>
    static Element TitleBar(float w, float h, ColorF wa1, ColorF wa3, float size, string caption) => new BoxEl
    {
        Width = w, Height = h, Shrink = 0f, ZStack = true, Fill = wa1, ClipToBounds = true,
        Children =
        [
            DeckArt.Texture("winamp-tbar.png", w, h, default, wa3),
            new BoxEl
            {
                Width = w, Height = h, Shrink = 0f, Direction = 0,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children =
                [
                    new BoxEl
                    {
                        Height = h, Shrink = 0f, Fill = wa1, Padding = new Edges4(8f, 0f, 8f, 0f),
                        Direction = 0, AlignItems = FlexAlign.Center,
                        Children =
                        [
                            new TextEl(caption)
                            {
                                Size = size, Color = Hex(0xFFFFFF), CharSpacing = 240f,
                                Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.Clip,
                            },
                        ],
                    },
                ],
            },
        ],
    };

    /// <summary>19 spectrum bars + their 1 px peak caps. Each bar captures its OWN band signal before the thunk is
    /// built, so the 30 Hz path allocates nothing and every bar is an independent compositor write.</summary>
    static Element Bars(float w, float h, DeckSignals sig, ColorF lcd, float gap, float pad)
    {
        float inner = MathF.Max(1f, h - pad * 2f);
        float barW = MathF.Max(1f, (w - pad * 2f - gap * (SpectrumBands - 1)) / SpectrumBands);
        var grad = new GradientSpec(GradientShape.Linear, 90f,
        [
            new GradientStop(0f, Hex(0xFF4A4A)),
            new GradientStop(0.45f, Hex(0xFFD24A)),
            new GradientStop(1f, lcd),
        ]);

        var cols = new Element[SpectrumBands];
        for (int i = 0; i < SpectrumBands; i++)
        {
            var band = sig.Bands[i];
            var peak = sig.Peaks[i];
            cols[i] = new BoxEl
            {
                Width = barW, Height = inner, Shrink = 0f, ZStack = true,
                Children =
                [
                    new BoxEl
                    {
                        Width = barW, Height = inner, Shrink = 0f, Gradient = grad,
                        TransformOriginX = 0.5f, TransformOriginY = 1f,
                        Transform = Prop.Of(() => Affine2D.Scale(1f, MathF.Max(band.Value, 0.02f))),
                    },
                    new BoxEl
                    {
                        Width = barW, Height = 1f, Shrink = 0f, Fill = Hex(0xFFFFFF, 0.9f),
                        Transform = Prop.Of(() => Affine2D.Translation(0f, inner * (1f - Clamp01(peak.Value)))),
                    },
                ],
            };
        }

        return new BoxEl
        {
            Width = w, Height = h, Shrink = 0f, Direction = 0, Gap = gap, Padding = Edges4.All(pad),
            AlignItems = FlexAlign.End, Children = cols,
        };
    }

    /// <summary>The oscilloscope option: 24 one-pixel dots, each translated off the centre line by its band. A
    /// per-frame <c>PathEl</c> would re-tessellate 30 times a second; 24 bound translations do not.</summary>
    static Element Scope(float w, float h, DeckSignals sig, ColorF lcd)
    {
        const float Dot = 1.5f;
        float span = MathF.Max(1f, w - Dot);
        var kids = new List<CanvasChild>(ScopeDots);
        for (int i = 0; i < ScopeDots; i++)
        {
            var band = sig.Bands[i];
            kids.Add(new CanvasChild(i * span / (ScopeDots - 1), (h - Dot) * 0.5f, new BoxEl
            {
                Width = Dot, Height = Dot, Shrink = 0f, Fill = lcd,
                // The synth centres a scope trace on 0.5, so the deflection is measured FROM the centre line.
                Transform = Prop.Of(() => Affine2D.Translation(0f, (Clamp01(band.Value) - 0.5f) * h)),
            }));
        }
        return Canvas.Create(w, h, kids);
    }

    static Element Grid(float w, float h)
    {
        var lines = new List<CanvasChild>(10);
        for (int i = 0; i < 10; i++)
            lines.Add(new CanvasChild(0f, MathF.Round(h * (i + 1f) / 10f) - 1f, new BoxEl
            {
                Width = w, Height = 1f, Shrink = 0f, Fill = Hex(0xFFFFFF, 0.06f),
            }));
        return Canvas.Create(w, h, lines) with { HitTestVisible = false };
    }

    static Element Cell(Prop<string> text, float size, ColorF lcd) => new BoxEl
    {
        Height = MathF.Ceiling(size * 1.5f), Shrink = 0f, Fill = Hex(0x000000),
        Padding = new Edges4(4f, 1f, 4f, 1f), Direction = 0, AlignItems = FlexAlign.Center,
        Children = [new TextEl(text) { Size = size, Color = lcd, Wrap = TextWrap.NoWrap, MaxLines = 1 }],
    };

    /// <summary>The five transport keys. Winamp's STOP has no equivalent in a streaming transport, so it pauses —
    /// the honest mapping, rather than a dead key.</summary>
    static Element Transport(float bw, float bh, PlaybackBridge b) => new BoxEl
    {
        Direction = 0, Gap = 2f, Shrink = 0f, Height = bh,
        Children =
        [
            TransportKey(bw, bh, () => { _ = b.Player.PreviousAsync(); }),
            TransportKey(bw, bh, () => { _ = b.Player.ResumeAsync(); }),
            TransportKey(bw, bh, () => { _ = b.Player.PauseAsync(); }),
            TransportKey(bw, bh, () => { _ = b.Player.PauseAsync(); }),
            TransportKey(bw, bh, () => { _ = b.Player.NextAsync(); }),
        ],
    };

    static Element TransportKey(float w, float h, Action onClick) => new BoxEl
    {
        Width = w, Height = h, Shrink = 0f,
        Gradient = GradientSpec.Vertical(Hex(0x5A6480), Hex(0x38405A)),
        BorderWidth = 1f, BorderColor = Hex(0x000000),
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
    };

    // ── bound text ──────────────────────────────────────────────────────────────────────────────────────────────

    static string ClockText(PlaybackBridge b)
    {
        long ms = b.ScrubTargetMs.Value ?? b.SeekTargetMs.Value ?? b.PositionMs.Value;
        return TimeCache.Get((int)Math.Max(0L, ms / 1000L), static s =>
            (s / 60).ToString("D2", CultureInfo.InvariantCulture) + ":" + (s % 60).ToString("D2", CultureInfo.InvariantCulture));
    }

    static string ScrollTitle(PlaybackBridge b)
    {
        var t = b.CurrentTrack.Value;
        if (t is null) return "1. Wavee *** ";
        string artist = t.Artists.Count > 0 ? t.Artists[0].Name : "";
        return "1. " + artist + " - " + t.Title + " (" + FormatCache.DurationMmSs(t.DurationMs) + ") *** ";
    }

    static string KbpsText(PlaybackBridge b)
    {
        int kbps = b.StreamBitrateKbps.Value;
        return kbps > 0 ? BitrateCache.Get(kbps, static k => k.ToString(CultureInfo.InvariantCulture) + " kbps") : "---";
    }

    /// <summary>Winamp's "kHz" cell. The bridge publishes a stream FORMAT, not a sample rate, so the cell shows the
    /// fact we actually have — a codec name — rather than inventing 44.</summary>
    static string KhzText(PlaybackBridge b)
    {
        string? format = b.StreamFormat.Value;
        return string.IsNullOrEmpty(format) ? "---" : format.ToUpperInvariant();
    }

    static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

    static ColorF Hex(uint rgb, float a = 1f)
        => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, a);
}
