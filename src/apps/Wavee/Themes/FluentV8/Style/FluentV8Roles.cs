// ── Themes/FluentV8/Style/FluentV8Roles.cs — the FluentV8 roles: the inner style's table with the v8 deltas (SPIKE) ──────────────────────
//
// The role table is the inner (WinUI) style's, rewritten where Fluent UI React v8 differs:
//   · TYPE: every face is Segoe UI (v8 has no Display optical size); the page-title pivot row is the v8 Pivot (14/20 links in 44-tall items,
//     8 apart, the 2-px underline under the selected word).
//   · SHAPE: roundedCorner2 for cards, covers, controls, overlays, tiles and pins; a square content seam; the selection mark is the v8 Nav's
//     straight 2-px bar (no rounded pill ends).
//   · LAYER: v8 surfaces are opaque and flat: the page is bodyBackground (white / #1b1a19) on a neutralLighterAlt frame; rows hover
//     neutralLighter, press and select neutralLight, a selected row under the pointer neutralQuaternaryAlt (DetailsRow.styles.ts); no zebra.
//   · INK: one accent voice, themePrimary.

using FluentGpu.Foundation;
using Wavee.Theming;
using ThemeKind = FluentGpu.Dsl.ThemeKind;

namespace Wavee.Themes.FluentV8;

/// <summary>The roles of the FluentV8 style.</summary>
public static class FluentV8Roles
{
    /// <summary>The inner table with the v8 deltas.</summary>
    public static WaveeStyleRoles Derive(WaveeStyleRoles r) => r with
    {
        Type = Type(r.Type),
        Shape = r.Shape with
        {
            Card = 2f, Cover = 2f, Control = 2f, Overlay = 2f, Tile = 2f, PinArt = 2f,
            CurrentMark = 0f, ContentSeam = 0f, OmniRow = 2f, SeekTrack = 2f,
        },
        Layer = Layer(r.Layer),
    };

    private static TextRole Face(TextRole t) => t.Face is null ? t : t with { Face = FluentV8Style.FaceChain };

    private static TypeRoles Type(TypeRoles t) => t with
    {
        DisplayFace = FluentV8Style.FaceChain,
        TrackTitle = Face(t.TrackTitle), CardTitle = Face(t.CardTitle), TrackMeta = Face(t.TrackMeta), Eyebrow = Face(t.Eyebrow),
        MicroMeta = Face(t.MicroMeta), DenseMeta = Face(t.DenseMeta), DenseTitle = Face(t.DenseTitle), SheetTitle = Face(t.SheetTitle),
        RailHeader = Face(t.RailHeader), ModuleHeader = Face(t.ModuleHeader), PageHero = Face(t.PageHero), DetailHero = Face(t.DetailHero),
        SurfaceDisplay = Face(t.SurfaceDisplay), HeroTitle = Face(t.HeroTitle), ArtistDisplay = Face(t.ArtistDisplay),
        ArtistTitle = Face(t.ArtistTitle), ArtistCompactTitle = Face(t.ArtistCompactTitle), NowPlayingTitle = Face(t.NowPlayingTitle),
        PickQuote = Face(t.PickQuote), FoldTitle = Face(t.FoldTitle), PivotLabel = Face(t.PivotLabel), NpvLyric = Face(t.NpvLyric),
        StatHero = Face(t.StatHero),
        // The v8 Pivot: 14/20 (fonts.medium), links 44 tall (the Pivot's link height), 12 padding (v8 pads 8; 12 keeps the 40-DIP Compact hit floor), the rest weight kept at the selection
        // (v8 reserves the semibold width with a hidden ::before; one weight here so the row never reflows).
        FacetTitle = new FacetRole(14f, 20f, FluentV8Style.FaceChain, 0f, 400, 400, new Edges4(12f, 12f, 12f, 12f), 44f, 0f, 0f),
        FacetCompact = new FacetRole(14f, 20f, FluentV8Style.FaceChain, 0f, 400, 400, new Edges4(12f, 12f, 12f, 12f), 44f, 0f, 0f),
        Panorama = Face(t.Panorama), PanoramaCompact = Face(t.PanoramaCompact), Hero = Face(t.Hero), Pivot = Face(t.Pivot),
        SubPivot = Face(t.SubPivot), Numeral = Face(t.Numeral), NumeralLarge = Face(t.NumeralLarge),
        NowPlayingDisplay = Face(t.NowPlayingDisplay), Wordmark = Face(t.Wordmark), LyricsLine = Face(t.LyricsLine),
        Heading = Face(t.Heading), QuickplayHead = Face(t.QuickplayHead),
    };

    private static ColorRole By(Func<ThemeKind, ColorF> f) => ColorRole.By(f);

    private static LayerRoles Layer(LayerRoles l)
    {
        var body = By(FluentV8Palette.Body);
        var frame = By(FluentV8Palette.Frame);
        var hover = By(FluentV8Palette.Hovered);
        var check = By(FluentV8Palette.Checked);
        var unpainted = ColorRole.Fixed(ColorF.Transparent);
        return l with
        {
            Toolbar = unpainted, Sidebar = unpainted, PlayerBar = unpainted,
            FileArea = body, Content = body, ContentAlt = frame,
            ShellGround = frame, ContentSurface = body, ContentLayer = body,
            FloatingChrome = frame, FloatingPane = body,
            RowZebra = unpainted,
            RowHover = hover, RowPressed = check, RowHoverZebra = hover, RowPressedZebra = check,
            SelectedRest = check, SelectedHover = By(FluentV8Palette.CheckedHovered), SelectedPressed = By(FluentV8Palette.CheckedHovered),
            ChromeHover = hover, ChromePressed = check,
            EventPlate = frame, EventHeroPlate = hover,
            ArtPlaceholder = hover,
        };
    }
}
