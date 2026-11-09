// ── Themes/Classic/Structure/Views/Player/ClassicMiniPlayer.cs — the mini player's look in Classic (#197) ───────────────────
//
// The layout is the Common template's (Themes/Common/CommonMiniPlayer.cs); this is Classic's MiniPlayerStyle: a warm wash tinted from the
// accent (the album's with "Colour from album art", on by default as in the app), soft 14-DIP corners, the artist in the accent, the heart
// on an album-tinted plate, the wave edge on the square's cover and behind the tall column (the Common template draws it from WaveEdge),
// and Classic's own motion: the seek fill ripples like water while the music moves and lies flat while it rests; the visualizer is three
// slow sine waves; the active lyric line rests on a tinted plate.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Pal;
using FluentGpu.Render;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Common;
using H = Wavee.Themes.Common.CommonMiniPlayerRules;
using M = Wavee.Themes.Classic.ClassicMiniRules;

namespace Wavee.Themes.Classic;

internal static class ClassicMiniPlayer
{
    public static void Register(ViewRegistryBuilder views) => CommonMiniPlayer.Register(views, "classic.player.mini", Style);

    static TextEl Line(TextEl e) => e with { MaxLines = 1, Shrink = 1f, MinWidth = 0f, Trim = TextTrim.CharacterEllipsis };

    /// <summary>The wash: the window's ground drawn toward the accent.</summary>
    static ColorF Wash(ColorF accent) => ColorF.Lerp(Tok.WindowBackground, accent with { A = 1f }, R.IsLight ? M.WashLight : M.WashDark);

    static readonly MiniRailLook s_rail = new()
    {
        Track = M.Track, Fill = M.Track, Corner = M.TrackCorner, TrackAlpha = M.TrackAlpha, Thumb = M.Thumb, ThumbCorner = M.Thumb * 0.5f, ThumbDot = 0f,
        ThumbFill = static (accent, _) => accent, ThumbOnHot = true, Wave = Ripple, WaveH = M.WaveH,
    };

    public static readonly MiniPlayerStyle Style = new()
    {
        MaterialLight = WindowMaterial.None,
        MaterialDark = WindowMaterial.None,
        ArtColourSlot = M.ArtColourSlot,
        ArtColourDefault = true,
        Ground = Wash,
        Solid = Wash,
        Tint = static a => a with { A = M.TintAlpha },
        Tint2 = static a => a with { A = M.Tint2Alpha },
        Radius = M.Radius, ArtCorner = M.ArtCorner, ThumbCorner = M.ThumbCorner,
        ButtonBox = M.ButtonBox, ButtonGlyph = M.ButtonGlyph, PlayBox = M.PlayBox, PlayBigBox = M.PlayBigBox, PlayGlyph = M.PlayGlyph,
        ChromeBox = M.ChromeBox, ChromeGlyph = M.ChromeGlyph, ButtonCorner = M.ButtonCorner, PlayCorner = M.PlayCorner, ChromeCorner = M.ChromeCorner,
        BadgeCorner = M.BadgeCorner, Heart = MiniHeart.Plate,
        Pad = M.Pad, Gap = M.Gap, StripPadStart = M.StripPadStart, StripThumb = M.StripThumb, StripGap = M.StripGap, Badge = M.Badge,
        Title = static (text, shape) => Line(R.Type.TrackTitle(text)) with { Size = M.TitleSize(shape), LineHeight = M.TitleSize(shape) * M.TitleLeading },
        TitleBleeds = false,
        TitleFade = 0f,
        Artist = static (text, accent, onDark) => Line(R.Type.TrackMeta(text)) with
        {
            Size = M.MetaSize, LineHeight = M.MetaLeading, Color = onDark ? H.OnMediaInk with { A = M.OnDarkArtistAlpha } : accent,
        },
        Detail = static text => Line(R.Type.TrackMeta(text)) with { Size = M.MetaSize, LineHeight = M.MetaLeading, Color = Tok.TextTertiary },
        CoverBleed = null,
        WaveEdge = true,
        Label = static text => Line(R.Type.TrackMeta(text)) with { Size = M.LabelSize, Color = Tok.TextPrimary },
        Heading = static text => Line(R.Type.TrackTitle(text)) with { Size = M.HeadingSize, Color = Tok.TextPrimary, FontFamily = R.Type.DisplayFace },
        SeekRail = static r => CommonMiniRail.Draw(r, s_rail),
        Visualizer = static (f, accent) => Embed.Comp(new SinesProps(f, accent), static () => new Sines()),
        TickerBar = M.TickerBar,
        LyricPad = M.LyricPad,
        LyricLine = LyricLine,
        Veil = static () => Tok.FillSmoke with { A = M.VeilAlpha },
        Shade = static () => Tok.FillSmoke with { A = M.ShadeAlpha },
        HoverInMs = M.HoverInMs,
        HoverOutDelayMs = M.HoverOutDelayMs,
    };

    // ══ THE RIPPLING SEEK FILL ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The played fill while the music moves: a line of small waves drifting one period per WaveMs (a seamless render-thread loop).</summary>
    static Element Ripple(float width, ColorF accent)
    {
        float w = width + M.WavePeriod;
        return Deck.Drift(new PathEl
        {
            Geometry = Sine(w, M.WaveH, M.WavePeriod, M.WaveAmp), Width = w, Height = M.WaveH, StrokeColor = accent,
            Stroke = new StrokeStyle(M.WaveStroke, LineCap.Round, LineJoin.Round),
        }, M.WavePeriod, M.WaveMs, pingPong: false);
    }

    /// <summary>A sine-like line (a cubic rise and fall per period) across <paramref name="w"/>, centred in <paramref name="h"/>.</summary>
    static PathData Sine(float w, float h, float period, float amp)
    {
        float mid = h * 0.5f;
        var b = new PathBuilder();
        b.MoveTo(0f, mid);
        for (float x = 0f; x < w - 0.5f; x += period)
        {
            b.CubicTo(x + 0.25f * period, mid - amp, x + 0.25f * period, mid - amp, x + 0.5f * period, mid);
            b.CubicTo(x + 0.75f * period, mid + amp, x + 0.75f * period, mid + amp, x + period, mid);
        }
        return b.Finish(PathContentEpoch.Mint(), FillRule.NonZero);
    }

    // ══ THE VISUALIZER: THREE SLOW SINES ═════════════════════════════════════════════════════════════════════════════════

    sealed record SinesProps(MiniVisualizerFrame Frame, ColorF Accent);

    /// <summary>Classic's analyser: three waves drifting at their own pace (they move only while the music does), each swelling with its third of
    /// the bands (a bound scale, no re-render).</summary>
    sealed class Sines : Component
    {
        public override Element Render()
        {
            var p = UseProps<SinesProps>();
            var f = p.Frame;
            bool run = f.Running.Value;
            return new BoxEl
            {
                Width = f.Width, Height = f.Height, ZStack = true, ClipToBounds = true, HitTestVisible = false,
                Children =
                [
                    Wave(f, run, 2, M.Viz2Top, M.Viz2H, M.Viz2Ms, p.Accent with { A = M.Viz2Alpha }),
                    Wave(f, run, 1, M.Viz1Top, M.Viz1H, M.Viz1Ms, H.OnMediaInk with { A = M.Viz1Alpha }),
                    Wave(f, run, 0, M.Viz0Top, M.Viz0H, M.Viz0Ms, p.Accent),
                ],
            };
        }

        static Element Wave(MiniVisualizerFrame f, bool run, int third, float top, float height, float ms, ColorF ink)
        {
            float h = f.Height * height, w = f.Width + M.VizPeriod;
            var bands = f.Bands;
            int from = third * bands.Length / 3, to = (third + 1) * bands.Length / 3;
            Element line = new PathEl
            {
                Geometry = Sine(w, h, M.VizPeriod, h * M.VizAmp), Width = w, Height = h, StrokeColor = ink,
                Stroke = new StrokeStyle(M.VizStroke, LineCap.Round, LineJoin.Round),
            };
            return new BoxEl
            {
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Width = f.Width, Height = h, Margin = new Edges4(0f, f.Height * top, 0f, 0f),
                ClipToBounds = true, HitTestVisible = false, TransformOriginY = 0.5f,
                Transform = Prop.Of(() =>
                {
                    float sum = 0f;
                    for (int i = from; i < to; i++) sum += bands[i].Value;
                    float level = to > from ? sum / (to - from) : 0f;
                    return Affine2D.Scale(1f, M.VizMin + (1f - M.VizMin) * Math.Clamp(level, 0f, 1f));
                }),
                Children = [run ? Deck.Drift(line, M.VizPeriod, ms, pingPong: false) : line],
            };
        }
    }

    // ══ LYRICS ═══════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A lyric line in the display face: the ticker's sung line over its next; a column line on a soft plate, the active one tinted
    /// with the album's colour (on imagery, a white veil).</summary>
    static Element LyricLine(MiniLyricLine l)
    {
        if (l.Use == MiniLyricUse.TickerNow)
            return Line(R.Type.TrackMeta(l.Text)) with { Size = M.TickerNow, LineHeight = M.TickerNowLeading, Color = Tok.TextPrimary, FontFamily = R.Type.DisplayFace };
        if (l.Use == MiniLyricUse.TickerNext)
            return Line(R.Type.TrackMeta(l.Text)) with { Size = M.TickerNext, LineHeight = M.TickerNextLeading, Color = Tok.TextTertiary };
        float size = M.LyricSize(l.Shape);
        ColorF ink = l.Active ? (l.OnDark ? H.OnMediaInk : Tok.TextPrimary) : (l.OnDark ? H.OnMediaInk with { A = M.InactiveOnDarkAlpha } : Tok.TextTertiary);
        ColorF plate = !l.Active ? ColorF.Transparent : l.OnDark ? H.OnMediaInk with { A = M.ActiveOnDarkPlate } : l.Accent with { A = M.Tint2Alpha };
        float space = size * M.LyricSpace;
        return new BoxEl
        {
            Direction = 1, Padding = new Edges4(M.LyricPad, space, M.LyricPad, space), Margin = new Edges4(0f, 0f, 0f, M.LyricGap),
            Corners = CornerRadius4.All(M.LyricCorner), Fill = plate, BrushTransitionMs = M.HoverInMs, HitTestVisible = false,
            Children =
            [
                R.Type.TrackMeta(l.Text) with
                {
                    Size = size, LineHeight = size * M.LyricLeading, Color = ink, FontFamily = R.Type.DisplayFace, Wrap = TextWrap.Wrap, MaxLines = 0,
                    Trim = TextTrim.None, Shrink = 1f, MinWidth = 0f,
                },
            ],
        };
    }
}
