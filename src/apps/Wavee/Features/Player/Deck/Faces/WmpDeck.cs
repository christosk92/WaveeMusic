using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>
/// The Windows Media Player visualizer: a black plate, three presets over the same 24-band synth, a small cover in
/// the corner and a hairline progress line.
///
/// <para><b>One model, three geometries.</b> <c>LevelModel</c> fills 24 bands and 24 peak caps; each preset binds
/// them differently — bars mirror them about a centre line, alchemy modulates three concentric rings, battery
/// scales a 6x6 grid of squares. Nothing re-renders: every animated node owns exactly one bound
/// <c>Transform</c>.</para>
///
/// <para><b>Colour is a bound channel.</b> The <c>colour</c> option is either the theme accent or the cover's own
/// accent, and both are read INSIDE the bind thunk — so a theme switch, a late colour grading and a track change
/// all repaint the visualizer without rebuilding it. (That is also why the alchemy rings are hollow-SDF ring
/// <c>BoxEl</c>s rather than <c>ArcSpec</c> decorations: an <c>ArcSpec</c> carries a frozen <c>ColorF</c>, a
/// <c>BorderColor</c> is a <c>Prop</c>.)</para>
/// </summary>
static class WmpDeck
{
    const int Bands = 24;
    const int WaveDots = 32;

    public static Element Build(in NpvPlayerCatalog.Preset preset, IAppSettings? settings, float side,
                                DeckSignals sig, PlaybackBridge bridge, DeckHost host)
    {
        var option = NpvPlayerCatalog.Option(preset, "preset");
        int index = NpvPlayerPrefs.Choice(settings, preset, option);
        string style = option.Choices[index].Slug;
        string caption = option.Choices[index].LabelKey;
        bool fromCover = NpvPlayerPrefs.ChoiceSlug(settings, preset, "colour") == "cover";

        // Read live inside the thunk: the accent must follow the theme AND the album, and neither may rebuild a face.
        Func<ColorF> colour;
        if (fromCover) colour = () => CoverAccent(bridge);
        else colour = static () => Tok.AccentDefault;

        float cover = 0.22f * side;
        float capSize = MathF.Max(8f, side * 0.034f);
        var frac = sig.Frac;

        var kids = new List<CanvasChild>(6);
        switch (style)
        {
            case "alchemy": kids.Add(new CanvasChild(0f, 0f, Alchemy(side, sig, colour))); break;
            case "battery": kids.Add(new CanvasChild(0f, 0f, Battery(side, sig, colour))); break;
            default: kids.Add(new CanvasChild(0f, 0f, BarsAndWaves(side, sig, colour))); break;
        }

        kids.Add(new CanvasChild(0.05f * side, side * 0.92f - cover, new BoxEl
        {
            Width = cover, Height = cover, Shrink = 0f, Corners = CornerRadius4.All(3f),
            Shadow = new ShadowSpec(20f, 8f, 0f, Hex(0x000000, 0.6f)),
            Children =
            [
                new ImageEl
                {
                    // A bound Source: the corner cover follows the track without this face ever being rebuilt.
                    Source = Prop.Of(() => CoverUrl(bridge)),
                    Width = cover, Height = cover, Fit = ImageFit.Cover, DecodePx = 128f,
                    Corners = CornerRadius4.All(3f), Placeholder = Hex(0x14171F),
                },
            ],
        }));
        kids.Add(new CanvasChild(side - 0.05f * side - side * 0.42f, side * 0.92f - capSize * 1.4f, new BoxEl
        {
            Width = side * 0.42f, Height = MathF.Ceiling(capSize * 1.4f), Shrink = 0f,
            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End,
            Children =
            [
                new TextEl(Loc.Bind(caption))
                {
                    Size = capSize * 0.72f, Color = Hex(0xFFFFFF, 0.45f), CharSpacing = 140f,
                    Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
            ],
        }));
        kids.Add(new CanvasChild(0f, side - 2f, new BoxEl
        {
            Width = side, Height = 2f, Shrink = 0f, Fill = Hex(0xFFFFFF, 0.12f), ClipToBounds = true,
            Children =
            [
                new BoxEl
                {
                    Width = side, Height = 2f, Shrink = 0f, Fill = Prop.Of(colour),
                    TransformOriginX = 0f, TransformOriginY = 0.5f,
                    Transform = Prop.Of(() => Affine2D.Scale(Clamp01(frac.Value), 1f)),
                },
            ],
        }));

        return Canvas.Create(side, side, kids) with { Fill = Hex(0x04060C) };
    }

    // ── Bars and Waves ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>24 bars standing on the centre line with a dimmer reflection under it, plus a 32-dot wave sampled
    /// off the peak caps. 80 bound transforms — the deck's heaviest preset, and still zero re-renders.</summary>
    static Element BarsAndWaves(float side, DeckSignals sig, Func<ColorF> colour)
    {
        float mid = side * 0.5f;
        float bw = side / Bands;
        float body = MathF.Max(1f, bw - 4f);
        float maxH = side * 0.55f;
        float reflect = maxH * 0.6f;
        var fill = Prop.Of(colour);

        var kids = new List<CanvasChild>(Bands * 2 + WaveDots);
        for (int i = 0; i < Bands; i++)
        {
            var band = sig.Bands[i];
            kids.Add(new CanvasChild(i * bw + 2f, mid - maxH, new BoxEl
            {
                Width = body, Height = maxH, Shrink = 0f, Fill = fill, Opacity = 0.85f,
                TransformOriginX = 0.5f, TransformOriginY = 1f,
                Transform = Prop.Of(() => Affine2D.Scale(1f, Clamp01(band.Value))),
            }));
            kids.Add(new CanvasChild(i * bw + 2f, mid, new BoxEl
            {
                Width = body, Height = reflect, Shrink = 0f, Fill = fill, Opacity = 0.35f,
                TransformOriginX = 0.5f, TransformOriginY = 0f,
                Transform = Prop.Of(() => Affine2D.Scale(1f, Clamp01(band.Value))),
            }));
        }

        const float Dot = 2f;
        float span = MathF.Max(1f, side - Dot);
        for (int i = 0; i < WaveDots; i++)
        {
            // The wave is sampled off the PEAK caps (they hold and fall, which is what gives the trace its shape);
            // 0.5 is its rest, so the deflection is measured from the centre line in both directions.
            var peak = sig.Peaks[i % Bands];
            kids.Add(new CanvasChild(i * span / (WaveDots - 1), mid - Dot * 0.5f, new BoxEl
            {
                Width = Dot, Height = Dot, Shrink = 0f, Fill = Hex(0xFFFFFF, 0.85f),
                Corners = Radii.Circle(Dot),
                Transform = Prop.Of(() => Affine2D.Translation(0f, (Clamp01(peak.Value) - 0.5f) * 0.36f * side)),
            }));
        }

        return Canvas.Create(side, side, kids) with { HitTestVisible = false };
    }

    // ── Alchemy ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Three concentric rings, each with two dimmer trailing copies. The rotation is driven by PROGRESS
    /// (the only slow signal on this face), the ellipticity by a band — so the ribbons turn with the track and
    /// breathe with the music.</summary>
    static Element Alchemy(float side, DeckSignals sig, Func<ColorF> colour)
    {
        var stroke = Prop.Of(colour);
        var frac = sig.Frac;
        var kids = new List<CanvasChild>(9);

        for (int r = 0; r < 3; r++)
        {
            float d = 2f * side * (0.18f + 0.10f * r);
            float turns = 6f * (r + 1);
            for (int trail = 0; trail < 3; trail++)
            {
                var band = sig.Bands[(r * 7 + trail) % DeckSignals.BandCount];
                float lag = trail * 0.15f;
                float opacity = trail == 0 ? 0.9f : trail == 1 ? 0.35f : 0.15f;
                kids.Add(new CanvasChild((side - d) * 0.5f, (side - d) * 0.5f, new BoxEl
                {
                    Width = d, Height = d, Shrink = 0f, Corners = Radii.Circle(d),
                    BorderWidth = 2f, BorderColor = stroke, Opacity = opacity,
                    TransformOriginX = 0.5f, TransformOriginY = 0.5f,
                    Transform = Prop.Of(() =>
                    {
                        float s = 1f + 0.25f * Clamp01(band.Value);
                        return Affine2D.Rotation(Clamp01(frac.Value) * MathF.Tau * turns - lag)
                                       .Multiply(Affine2D.Scale(s, 0.8f * s));
                    }),
                }));
            }
        }

        return Canvas.Create(side, side, kids) with { HitTestVisible = false };
    }

    // ── Battery ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A 6x6 grid of squares, each pulsing on one band. Static opacity, one bound scale per cell.</summary>
    static Element Battery(float side, DeckSignals sig, Func<ColorF> colour)
    {
        const int Cols = 6, Rows = 6;
        var fill = Prop.Of(colour);
        float cw = side / Cols, ch = side / Rows;
        float cell = MathF.Min(cw, ch);

        var kids = new List<CanvasChild>(Cols * Rows);
        for (int i = 0; i < Cols; i++)
        {
            for (int j = 0; j < Rows; j++)
            {
                var band = sig.Bands[(i * 3 + j) % Bands];
                kids.Add(new CanvasChild(i * cw + (cw - cell) * 0.5f, j * ch + (ch - cell) * 0.5f, new BoxEl
                {
                    Width = cell, Height = cell, Shrink = 0f, Fill = fill, Opacity = 0.7f,
                    TransformOriginX = 0.5f, TransformOriginY = 0.5f,
                    Transform = Prop.Of(() =>
                    {
                        float s = 0.15f + 0.7f * Clamp01(band.Value);
                        return Affine2D.Scale(s, s);
                    }),
                }));
            }
        }

        return Canvas.Create(side, side, kids) with { HitTestVisible = false };
    }

    // ── cover-derived colour ────────────────────────────────────────────────────────────────────────────────────

    static string CoverUrl(PlaybackBridge b)
    {
        var track = b.CurrentTrack.Value;
        return track is null ? "" : DeckArt.CoverUrl(track) ?? "";
    }

    /// <summary>The LIVE cover accent. <c>DeckArt.CoverAccent</c> binds one fixed url; this deck's cover changes
    /// under a face that is built once, so the url is resolved inside the thunk too.</summary>
    static ColorF CoverAccent(PlaybackBridge b)
    {
        string url = CoverUrl(b);
        if (url.Length > 0) _ = SpotifyLive.CoverColorPlane.Current.Watch(url).Value;
        var scheme = Surfaces.SchemeFor(url.Length > 0 ? url : null);
        return scheme is { } s ? WaveePalette.Accent(s) : Tok.AccentDefault;
    }

    static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

    static ColorF Hex(uint rgb, float a = 1f)
        => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, a);
}
