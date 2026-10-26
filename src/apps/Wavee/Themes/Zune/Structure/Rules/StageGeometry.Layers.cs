// ── Themes/Zune/Structure/Rules/StageGeometry.Layers.cs — the numbers of Zune's stage layers, caption, gallery and options (P5 Z4b) ──
//
// The identity (the morphing cover and its titles), the now-playing card, the chips, the face chip, the pane region, the lyric caption,
// and the two option surfaces no Zune board draws (the gallery and the options popover: built from Zune's parts, owner Q18: flat raised
// plates, square corners, lowercase headings and words, the accent as the one selection voice). The motion values are the stage's
// production recipes, kept as they are (owner rule): the cover's spring, the card's drop, the pane's rise.
//
// Literal numbers are allowed here (WVTH003 exempts `Rules\**`).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Themes.Zune;

public static partial class ZuneStageGeometry
{
    // ── the identity ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The cover's and the titles' spring (the one cover node morphs between the arrangements): response, damping.</summary>
    public const float CoverResponse = 0.45f, CoverDamping = 0.90f;

    /// <summary>A cover larger than this decodes at 1024 px, else 512.</summary>
    public const float CoverLargeDecodeFrom = 400f;

    public const int CoverDecodeLarge = 1024, CoverDecodeSmall = 512;

    /// <summary>The allocator's titles in Zune's voice: the display face at 300 from this size up, the text face at 400 under it.</summary>
    public const float DisplayFrom = 20f;

    public const ushort TitleLight = 300, TitleRegular = 400, ColumnTitleWeight = 200, ColumnMetaWeight = 600, SelectedWeight = 600;

    /// <summary>The title's weight at <paramref name="size"/>.</summary>
    public static ushort TitleWeight(float size) => size >= DisplayFrom ? TitleLight : TitleRegular;

    /// <summary>The chips under the allocator's titles: 24 tall, 6 inside, 12/16, 8 each side.</summary>
    public const float ChipHeight = 24f, ChipGap = 6f, ChipTextSize = 12f, ChipTextLine = 16f, ChipPadX = 8f;

    // ── the now-playing card ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The allocator's 460 card (and the Compact identity card) on the plate arrangement: the stage veil at 0.6 (Z-NowPlaying).</summary>
    public const float PlateCardA = 0.6f;

    /// <summary>The card drops in from this far above after this delay, and follows a crossing on the cover's spring.</summary>
    public const float CardEnterDy = -8f, CardEnterDelayMs = 150f;

    // ── the face chip (a Compact window's identity card, Visualizer mode) ───────────────────────────────────────────────

    /// <summary>The chip's sides, gap, glyph, name (12/16) and chevron; its height and place are the allocator's (a 40 target).</summary>
    public const float FaceChipPadX = 12f, FaceChipGap = 6f, FaceChipGlyph = 14f, FaceChipText = 12f, FaceChipLine = 16f, FaceChipChevron = 10f;

    // ── the pane region ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The region slides to its new rect on this spring; a pane's body rises 40 and fades in over 500 ms, and fades out in place.</summary>
    public const float PaneMoveResponse = 0.45f, PaneMoveDamping = 0.90f, PaneEnterDy = 40f, PaneFadeMs = 500f;

    // ── the lyric caption ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The caption's weight (Zune's lyric voice: semilight 350), the context lines' and the secondary line's line budget, the
    /// secondary line's gap, the veil's middle stop and its share of the centre alpha, a breathing dot's corner fraction.</summary>
    public const int CaptionWeight = 350, ContextMaxLines = 1, SecondaryMaxLines = 2;

    public const float SecondaryGap = 4f, VeilMidStop = 0.55f, VeilMidAlpha = 0.55f, DotRadiusFraction = 0.5f;

    // ── the gallery (no board: Zune's parts) ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The pane: padding 20 / 16 / 20 / 20, 12 under the title, a tile's floor width, 8 between tiles, the preview strip.</summary>
    public const float GalleryPadX = 20f, GalleryGap = 12f, GalleryTileGap = 8f, GalleryTileMinW = 180f, GalleryPreviewH = 78f, GalleryContentGap = 12f;

    public static readonly Edges4 GalleryPadding = new(GalleryPadX, 16f, GalleryPadX, 20f);

    /// <summary>The title (the group voice, 22/28 at 300) and the group headers (13/18).</summary>
    public const float GalleryTitleSize = 22f, GalleryTitleLine = 28f, GroupHeaderSize = 13f, GroupHeaderLine = 18f;

    /// <summary>A group's header margin: 6 above every header but the first.</summary>
    public static Edges4 GroupHeaderMargin(bool first) => new(0f, first ? 0f : 6f, 0f, 0f);

    /// <summary>A tile: its inner gap, its padding, its border (2: the accent on the picked face, the ink on the cursor), its preview's inset.</summary>
    public const float TileInnerGap = 6f, TileBorderWidth = 2f, TilePreviewInset = 8f;

    public static readonly Edges4 TilePadding = new(4f, 4f, 4f, 8f);

    /// <summary>The check square on the picked tile: its size, glyph and offset from the preview's top-right corner.</summary>
    public const float CheckBox = 20f, CheckGlyph = 12f;

    public static readonly Edges4 CheckMargin = new(0f, 6f, 6f, 0f);

    /// <summary>The label block: 14/20 name, 12/16 pace and blurb.</summary>
    public const float TileNameSize = 14f, TileNameLine = 20f, TileMetaSize = 12f, TileMetaLine = 16f, TileLabelGap = 6f;

    public static readonly Edges4 TileLabelPadding = new(2f, 0f, 2f, 0f);

    /// <summary>How long the pointer rests on a tile before its preview goes live, the cursor's gutter in the scroller, the scroller's key.</summary>
    public const float HoverPromoteMs = 120f;

    public const double CursorGutter = 10.0;

    public const string GalleryScrollKey = "stagegallery";

    /// <summary>The settings: the header (13/18), a 48 row (12 / 16 inside, a 16 glyph, 14/20 label), the toggles' floor, the sliders.</summary>
    public const float SettingsHeaderSize = 13f, SettingsHeaderLine = 18f, RowMinHeight = 48f, RowGlyphSize = 16f, RowLabelSize = 14f, RowLabelLine = 20f;

    public static readonly Edges4 SettingsHeaderMargin = new(0f, 8f, 0f, 0f);

    public static readonly Edges4 RowPadding = new(16f, 0f, 12f, 0f);

    public const float ToggleMinWidth = 40f, SliderLength = 150f, SliderThickness = 24f;

    /// <summary>The sync offset snaps to 10 ms.</summary>
    public static int SnapOffset(float value) => (int)MathF.Round(value / 10f) * 10;

    public static readonly Slider.SliderOptions SensitivityOptions = new()
    {
        Min = Visualizer.Bands.MinSensitivity, Max = Visualizer.Bands.MaxSensitivity, Step = 0.05f, IsThumbToolTipEnabled = true,
        ThumbToolTipValueConverter = static v => ((int)MathF.Round(v * 100f)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%",
    };

    public static readonly Slider.SliderOptions OffsetOptions = new()
    {
        Min = -500f, Max = 500f, Step = 10f, IsThumbToolTipEnabled = true,
        ThumbToolTipValueConverter = static v => ((int)MathF.Round(v)).ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms",
    };

    // ── the options popover (no board: Zune's parts) ─────────────────────────────────────────────────────────────────────

    /// <summary>The panel: at most 440 x 720, 24 from each edge of the stage.</summary>
    public const float OptionsMaxW = 440f, OptionsMaxH = 720f, OptionsEdgeInset = 48f;

    /// <summary>The panel's title (the group voice, 22/28 at 300), its header's padding and gap, the two tab words (the pivot voice one
    /// rung down: 20/28, 350 / 600, 20 apart).</summary>
    public const float OptionsTitleSize = 22f, OptionsTitleLine = 28f, OptionsHeaderGap = 12f, TabSize = 20f, TabLine = 28f, TabGap = 20f, TabH = 36f;

    public static readonly Edges4 OptionsHeaderPadding = new(20f, 18f, 20f, 0f);

    public const int TabCount = 2, OptionsGridCols = 3;

    /// <summary>Body (14/20), caption (12/16), the help line (13/19).</summary>
    public const float BodySize = 14f, BodyLine = 20f, CaptionSize = 12f, CaptionLine = 16f, HelpSize = 13f, HelpLine = 19f;

    /// <summary>The Visualizer tab: its rhythm, the "now showing" poster and row, the group chips (square, 32 tall), the poster grid.</summary>
    public const float VizBodyGap = 14f, NowPosterW = 136f, NowPosterH = 76f, NowGap = 14f, NowTextGap = 2f;

    public static readonly Edges4 VizBodyPadding = new(0f, 16f, 0f, 0f);

    public static readonly Edges4 OptionsSidePadding = new(20f, 0f, 20f, 0f);

    public const float ChipGridGap = 6f, ChipMinHeight = 32f, ChipSize = 13f, ChipLine = 18f;

    public static readonly Edges4 ChipPadding = new(12f, 0f, 12f, 0f);

    public const float GridPadX = 40f, GridGap = 8f, PosterAspectW = 16f, PosterAspectH = 9f, GridTileGap = 4f, GridTileBorder = 2f, TilePosterInsetW = 8f, TilePosterInsetH = 4f;

    public static readonly Edges4 GridPadding = new(20f, 2f, 20f, 8f);

    public static readonly Edges4 GridTilePadding = Edges4.All(2f);

    public static readonly Edges4 GridTileNameMargin = new(2f, 0f, 2f, 2f);

    /// <summary>The footer: the promise and the "open gallery" word (36 tall) with its key cap.</summary>
    public const float FooterGap = 12f, FooterButtonGap = 8f, FooterButtonMinH = 36f, KeyCapSize = 11f;

    public static readonly Edges4 FooterPadding = new(20f, 12f, 20f, 16f);

    public static readonly Edges4 KeyCapPadding = new(6f, 1f, 6f, 1f);

    /// <summary>The Layout tab: its rhythm, a layout row (square, 10 inside, 2 accent border when picked), the artist-photo group, the dimming.</summary>
    public const float LayoutGap = 12f, LayoutRowGap = 12f, LayoutRowTextGap = 2f, LayoutRowBorderOn = 2f, LayoutRowBorderOff = 1f, LayoutRowSelectedA = 0.12f, CheckSize = 12f;

    public static readonly Edges4 LayoutPadding = new(20f, 16f, 20f, 16f);

    public static readonly Edges4 LayoutRowPadding = Edges4.All(10f);

    public const float SectionGap = 8f, RowGap = 2f, OptionRowMinH = 32f, HeroGap = 12f, HeroDimGap = 6f, HeroSliderInset = 40f, HeroSliderThickness = 24f, NoteLine = 17f;

    public static readonly Edges4 SectionPadding = new(0f, 10f, 0f, 0f);

    public static readonly Edges4 HeroPadding = new(0f, 10f, 0f, 0f);

    public static readonly Edges4 RadioFocusMargin = Edges4.All(-2f);

    public const float DimMin = 0f, DimMax = 100f, DimStep = 5f;

    /// <summary>The layout row's 84 x 48 schematic (the face's first colour at 0.55 under the stage veil) and its marks.</summary>
    public const float ThumbW = 84f, ThumbH = 48f, ThumbPlateMix = 0.55f, ThumbArtistPlateA = 0.45f, ThumbArtistDotA = 0.85f;

    public static readonly ZuneRect CardMark = new(6f, 30f, 30f, 12f);

    public static readonly ZuneRect LargeMark = new(6f, 6f, 36f, 36f);

    public static readonly ZuneRect CenteredMark = new(31f, 9f, 22f, 22f);

    public static readonly ZuneRect ArtistMark = new(6f, 37f, 30f, 5f);
}
