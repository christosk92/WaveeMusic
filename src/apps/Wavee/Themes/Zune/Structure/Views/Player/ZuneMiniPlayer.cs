// ── Themes/Zune/Structure/Views/Player/ZuneMiniPlayer.cs — the mini player's look in Zune (#197) ─────────────────────────
//
// The layout is the Common template's (Themes/Common/CommonMiniPlayer.cs); this is Zune's MiniPlayerStyle: a flat square card on the
// theme's ground, the pink accent, thin lowercase display type that runs off the edge under a fade instead of shrinking or cutting (owner
// rule), the artist in pink, the round play disc and the outlined round heart, a 2-DIP seek line, the artist written big across the square's
// cover, a thin centred waveform in pink and white, and the active lyric line big and pink.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Pal;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Common;
using H = Wavee.Themes.Common.CommonMiniPlayerRules;
using M = Wavee.Themes.Zune.ZuneMiniRules;

namespace Wavee.Themes.Zune;

internal static class ZuneMiniPlayer
{
    public static void Register(ViewRegistryBuilder views) => CommonMiniPlayer.Register(views, "zune.player.mini", Style);

    static TextEl Line(TextEl e) => e with { MaxLines = 1, Shrink = 1f, MinWidth = 0f, Trim = TextTrim.CharacterEllipsis };

    /// <summary>The light display face, lowercase: the title, the cover bleed, the big lyric lines, the flyout heading.</summary>
    static TextEl Display(string text, float size, float leading) => R.Type.SurfaceDisplay(text) with
    {
        Size = size, LineHeight = leading, Case = TextCase.Lower, MaxLines = 1, Trim = TextTrim.None,
    };

    /// <summary>Zune's pink lifted a little on dark imagery (the prototype's brightness 1.3).</summary>
    static ColorF Lift(ColorF a) => ColorF.Lerp(a, H.OnMediaInk, M.OnDarkLift);

    static readonly MiniRailLook s_rail = new()
    {
        Track = M.Track, Fill = M.Track, Corner = 0f, TrackAlpha = M.TrackAlphaDark, Thumb = M.Thumb, ThumbCorner = M.Thumb * 0.5f, ThumbDot = 0f,
        ThumbFill = static (_, onDark) => onDark ? H.OnMediaInk : Tok.TextPrimary,
    };

    static readonly MiniRailLook s_railLight = s_rail with { TrackAlpha = M.TrackAlphaLight };

    public static readonly MiniPlayerStyle Style = new()
    {
        MaterialLight = WindowMaterial.None,
        MaterialDark = WindowMaterial.None,
        ArtColourSlot = M.ArtColourSlot,
        ArtColourDefault = false,
        Ground = static _ => Tok.WindowBackground,
        Solid = static _ => Tok.WindowBackground,
        Glow = static a => R.IsLight ? ColorF.Transparent : a with { A = M.TintAlpha },
        Tint = static a => a with { A = M.TintAlpha },
        Tint2 = static a => a with { A = M.Tint2Alpha },
        Radius = M.Radius, ArtCorner = M.ArtCorner, ThumbCorner = M.ThumbCorner,
        ButtonBox = M.ButtonBox, ButtonGlyph = M.ButtonGlyph, PlayBox = M.PlayBox, PlayBigBox = M.PlayBigBox, PlayGlyph = M.PlayGlyph,
        ChromeBox = M.ChromeBox, ChromeGlyph = M.ChromeGlyph, ButtonCorner = M.ButtonCorner, PlayCorner = M.PlayCorner, ChromeCorner = M.ChromeCorner,
        BadgeCorner = M.BadgeCorner, Heart = MiniHeart.Ring,
        Pad = M.Pad, Gap = M.Gap, StripPadStart = M.StripPadStart, StripThumb = M.StripThumb, StripGap = M.StripGap, Badge = M.Badge,
        Title = static (text, shape) => Display(text, M.TitleSize(shape), M.TitleSize(shape) * M.TitleLeading),
        TitleBleeds = true,
        TitleFade = M.TitleFade,
        Artist = static (text, accent, onDark) => Line(R.Type.TrackMeta(text)) with
        {
            Size = M.MetaSize, LineHeight = M.MetaLeading, Weight = M.ArtistWeight, Color = onDark ? Lift(accent) : accent,
        },
        Detail = static text => Line(R.Type.TrackMeta(text)) with { Size = M.MetaSize, LineHeight = M.MetaLeading, Color = Tok.TextTertiary },
        CoverBleed = static (artist, width) => Display(artist, M.BleedSize(width), M.BleedSize(width)) with
        {
            Color = H.OnMediaInk, CharSpacing = M.BleedTracking, Shrink = 0f,
        },
        GroundBand = false,
        Label = static text => Line(R.Type.TrackMeta(text)) with { Size = M.LabelSize, Color = Tok.TextPrimary, Case = TextCase.Lower },
        Small = static text => Line(R.Type.TrackMeta(text)) with { Size = M.SmallSize, LineHeight = M.SmallLeading, Color = Tok.TextSecondary, Case = TextCase.Lower },
        Heading = static text => Display(text, M.HeadingSize, M.HeadingLeading) with { Color = Tok.TextPrimary },
        SeekRail = static r => CommonMiniRail.Draw(r, R.IsLight && !r.OnDark ? s_railLight : s_rail),
        Visualizer = Waveform,
        TickerBar = 0f,
        LyricScale = M.LyricScale,
        LyricLine = LyricLine,
        Shade = static () => Tok.FillSmoke with { A = M.ShadeAlpha },
        HoverInMs = M.HoverInMs,
        HoverOutDelayMs = M.HoverOutDelayMs,
        WideDetailFrom = M.WideDetailFrom,
    };

    /// <summary>A thin centred waveform: 2-DIP bars spread edge to edge, growing both ways from the middle line, pink with every third white.</summary>
    static Element Waveform(MiniVisualizerFrame f, ColorF accent)
    {
        int n = f.Bands.Length;
        float innerH = f.Height * (1f - 2f * M.VizInsetY);
        var bars = new Element[n];
        for (int i = 0; i < n; i++)
        {
            var band = f.Bands[i];
            bars[i] = new BoxEl
            {
                Width = M.VizBar, Height = innerH, Shrink = 0f, Fill = (i + 1) % M.VizWhiteEvery == 0 ? H.OnMediaInk : accent, HitTestVisible = false,
                TransformOriginY = 0.5f, Transform = Prop.Of(() => Affine2D.Scale(1f, M.VizMin + (1f - M.VizMin) * Math.Clamp(band.Value, 0f, 1f))),
            };
        }
        return new BoxEl
        {
            Grow = 1f, AlignSelf = FlexAlign.Stretch, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween, HitTestVisible = false,
            Margin = new Edges4(f.Width * M.VizInsetX, f.Height * M.VizInsetY, f.Width * M.VizInsetX, f.Height * M.VizInsetY),
            Children = bars,
        };
    }

    /// <summary>The strip ticker's lines in the light display face: the sung line big and pink, running off the edge under a fade (never shrunk,
    /// never cut), the next line under it. (The lyric windows are the app's own lyrics view.)</summary>
    static Element LyricLine(MiniLyricLine l)
    {
        if (l.Use == MiniLyricUse.TickerNow)
            return new BoxEl
            {
                Direction = 0, MinWidth = 0f, ClipToBounds = true, Margin = new Edges4(0f, 0f, -M.TickerBleed, 0f), HitTestVisible = false,
                EdgeFade = new EdgeFadeSpec(EdgeMask.Right, M.TitleFade),
                Children = [Display(l.Text, M.TickerNow, M.TickerNowLeading) with { Color = l.Accent, Case = TextCase.AsWritten, Shrink = 0f }],
            };
        return Line(Display(l.Text, M.TickerNext, M.TickerNextLeading)) with { Color = Tok.TextTertiary, Case = TextCase.AsWritten };
    }
}
