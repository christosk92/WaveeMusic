// ── Themes/WinUi/Structure/Views/Player/WinUiMiniPlayer.cs — the mini player's look in WinUI (#197) ─────────────────────
//
// The layout is the Common template's (Themes/Common/CommonMiniPlayer.cs); this is WinUI's MiniPlayerStyle: an acrylic card in the system
// accent (or the album's, with "Colour from album art"), Segoe UI Variable type, the 4-DIP seek track with WinUI's ringed thumb, rounded
// accent bars for the visualizer and the active lyric line marked by the accent bar and set semibold.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Pal;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Common;
using H = Wavee.Themes.Common.CommonMiniPlayerRules;
using M = Wavee.Themes.WinUi.WinUiMiniRules;

namespace Wavee.Themes.WinUi;

internal static class WinUiMiniPlayer
{
    public static void Register(ViewRegistryBuilder views) => CommonMiniPlayer.Register(views, "winui.player.mini", Style);

    static TextEl Line(TextEl e) => e with { MaxLines = 1, Shrink = 1f, MinWidth = 0f, Trim = TextTrim.CharacterEllipsis };

    static readonly MiniRailLook s_rail = new()
    {
        Track = M.Track, Fill = M.Track, Corner = M.TrackCorner, TrackAlpha = M.TrackAlpha, Thumb = M.Thumb, ThumbCorner = M.Thumb * 0.5f,
        ThumbDot = M.ThumbDot, ThumbRing = H.RuleW, ThumbFill = static (_, onDark) => onDark ? Tok.FillSolidBase : Tok.FillControlSolid,
    };

    public static readonly MiniPlayerStyle Style = new()
    {
        MaterialLight = WindowMaterial.Acrylic,
        MaterialDark = WindowMaterial.Acrylic,
        ArtColourSlot = M.ArtColourSlot,
        ArtColourDefault = false,
        Ground = static _ => Tok.FillSolidBase with { A = M.GroundAlpha },
        Solid = static _ => Tok.FillSolidBase,
        Glow = static _ => ColorF.Transparent,
        Tint = static a => a with { A = M.TintAlpha },
        Tint2 = static a => a with { A = M.Tint2Alpha },
        Radius = M.Radius, ArtCorner = M.ArtCorner, ThumbCorner = M.ThumbCorner,
        ButtonBox = M.ButtonBox, ButtonGlyph = M.ButtonGlyph, PlayBox = M.PlayBox, PlayBigBox = M.PlayBigBox, PlayGlyph = M.PlayGlyph,
        ChromeBox = M.ChromeBox, ChromeGlyph = M.ChromeGlyph, ButtonCorner = M.ButtonCorner, PlayCorner = M.PlayCorner, ChromeCorner = M.ChromeCorner,
        BadgeCorner = M.BadgeCorner, Heart = MiniHeart.Plain,
        Pad = M.Pad, Gap = M.Gap, StripPadStart = M.StripPadStart, StripThumb = M.StripThumb, StripGap = M.StripGap, Badge = M.Badge,
        Title = static (text, shape) => Line(R.Type.TrackTitle(text)) with
        {
            Size = M.TitleSize(shape), LineHeight = M.TitleSize(shape) * M.TitleLeading, Weight = M.TitleWeight, FontFamily = R.Type.DisplayFace,
        },
        TitleBleeds = false,
        TitleFade = 0f,
        Artist = static (text, _, onDark) => Line(R.Type.TrackMeta(text)) with
        {
            Size = M.MetaSize, LineHeight = M.MetaLeading, Color = onDark ? H.OnMediaInk with { A = H.OnMediaSecondaryAlpha } : Tok.TextSecondary,
        },
        Detail = static text => Line(R.Type.TrackMeta(text)) with { Size = M.MetaSize, LineHeight = M.MetaLeading, Color = Tok.TextTertiary },
        CoverBleed = null,
        GroundBand = false,
        Label = static text => Line(R.Type.TrackMeta(text)) with { Size = M.LabelSize, Color = Tok.TextPrimary },
        Small = static text => Line(R.Type.TrackMeta(text)) with { Size = M.SmallSize, LineHeight = M.SmallLeading, Color = Tok.TextSecondary },
        Heading = static text => Line(R.Type.TrackTitle(text)) with { Size = M.HeadingSize, Color = Tok.TextPrimary },
        SeekRail = static r => CommonMiniRail.Draw(r, s_rail),
        Visualizer = Bars,
        TickerBar = M.TickerBar,
        LyricScale = M.LyricScale,
        LyricLine = LyricLine,
        Shade = static () => Tok.FillSmoke with { A = M.ShadeAlpha },
        HoverInMs = M.HoverInMs,
        HoverOutDelayMs = M.HoverOutDelayMs,
        WideDetailFrom = M.WideDetailFrom,
    };

    /// <summary>Rounded accent bars rising from the box's foot, lit toward white at their tops, one per band (a bound scale: no re-render, no frame
    /// at rest). A silent band rests on the resting swell (<see cref="M.VizRest"/>), so a pause or a quiet passage is a low, smooth shape, never a
    /// row of stubs; a band's level lifts its bar from there to the full height.</summary>
    static Element Bars(MiniVisualizerFrame f, ColorF accent)
    {
        int n = f.Bands.Length;
        float innerH = f.Height * (1f - M.VizInsetTop - M.VizInsetFoot);
        var bars = new Element[n];
        var grad = new GradientSpec(GradientShape.Linear, H.ShadeAngle, [new GradientStop(0f, H.OnMediaInk with { A = M.VizTopAlpha }), new GradientStop(1f, accent)]);
        for (int i = 0; i < n; i++)
        {
            var band = f.Bands[i];
            float rest = M.VizRest(i, n);
            bars[i] = new BoxEl
            {
                Grow = 1f, Basis = 0f, MinWidth = 0f, Height = innerH, Opacity = M.VizAlpha, Gradient = grad, HitTestVisible = false,
                Corners = new CornerRadius4(M.VizTopCorner, M.VizTopCorner, M.VizFootCorner, M.VizFootCorner), TransformOriginY = 1f,
                Transform = Prop.Of(() => Affine2D.Scale(1f, rest + (1f - rest) * Math.Clamp(band.Value, 0f, 1f))),
            };
        }
        return new BoxEl
        {
            Grow = 1f, AlignSelf = FlexAlign.Stretch, Direction = 0, AlignItems = FlexAlign.End, Gap = f.Width * M.VizGap, HitTestVisible = false,
            Margin = new Edges4(f.Width * M.VizInsetX, f.Height * M.VizInsetTop, f.Width * M.VizInsetX, f.Height * M.VizInsetFoot),
            Children = bars,
        };
    }

    /// <summary>The strip ticker's lines: the current line semibold, its next line in the tertiary ink. (The lyric windows are the app's own
    /// lyrics view.)</summary>
    static Element LyricLine(MiniLyricLine l)
    {
        if (l.Use == MiniLyricUse.TickerNow)
            return Line(R.Type.TrackTitle(l.Text)) with { Size = M.TickerNow, LineHeight = M.TickerNowLeading, Color = Tok.TextPrimary };
        return Line(R.Type.TrackMeta(l.Text)) with { Size = M.TickerNext, LineHeight = M.TickerNextLeading, Color = Tok.TextTertiary };
    }
}
