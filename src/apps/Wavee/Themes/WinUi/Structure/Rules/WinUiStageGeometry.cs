// ── Themes/WinUi/Structure/Rules/WinUiStageGeometry.cs — the numbers of WinUI's stage frame (P6 W4b) ─────────────────────────────────
//
// The look of the parts inside the extents the allocator and the tier record budget (`Stage.Layout`, `WinUiStageTiers`): the chrome's motion, the
// top bar's wordmark, pill and word buttons (W-Stage: the 16 tile, the 40 acrylic pill with 4 inside, 40 x 36 glyph buttons 4 apart, 36 word
// buttons), the transport card's parts (W-Stage: 12 / 14 / 20 type, the 4 rail, the 18 slider thumb box), the now-playing card's type and the
// sheet's grabber (W-Stage-Compact: 32 x 4 at 8). Per-tier numbers are the record's (`Rules\Tiers.Stage.cs`). Literal numbers are allowed here
// (WVTH003 exempts `Rules\**`); the views read them by name.

using FluentGpu.Foundation;

namespace Wavee.Themes.WinUi;

/// <summary>The constants of WinUI's stage root, top bar and transport card.</summary>
internal static class WinUiStageGeometry
{
    // ── the root ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The face fades in from this scale.</summary>
    public const float FaceEnterScale = 0.97f;

    /// <summary>The lyric caption's layer rises in from this far below.</summary>
    public const float CaptionEnterDy = 12f;

    /// <summary>The hairline progress grows from its left edge, centred vertically (unused: WinUI's chrome never rests).</summary>
    public const float HairlineOriginY = 0.5f;

    /// <summary>The bar and the card slide in by this much with the stage (a remount of the chrome's layer).</summary>
    public const float BarEnterDy = 8f;

    /// <summary>The gallery slides in from the right (or up from the foot on Portrait's sheet) by this much.</summary>
    public const float GallerySlide = 32f;

    /// <summary>The card yields its width to a gallery that runs to the foot over the face's re-centre tween.</summary>
    public const float GalleryYieldMs = 550f;

    /// <summary>The bar's height gliding to the next tier's (52 / 48 / 56) on a crossing, ms: the tier edge never snaps it.</summary>
    public const float BarFitMs = 220f;

    /// <summary>The Up next column's padding (W-Lyrics-Huge: 20) and the distance it slides in from the right.</summary>
    public const float UpNextPad = 20f, UpNextSlide = 32f;

    /// <summary>The pane region slides to its new rect with the cover on this position spring: response, damping.</summary>
    public const float PaneMoveResponse = 0.45f, PaneMoveDamping = 0.90f;

    /// <summary>A pane's body enters rising from this far below, fading in over <see cref="PaneFadeMs"/>.</summary>
    public const float PaneEnterDy = 40f;

    public const float PaneFadeMs = 500f;

    /// <summary>Portrait's gallery sheet parks above the transport and its two gutters.</summary>
    public const float SheetGutters = 2f;

    /// <summary>The Compact sheet's grabber (W-Stage-Compact: 32 x 4, r2, white at 0.3, 8 from the top) and the sheet's fill (#2c2c2c).</summary>
    public const float GrabberW = 32f, GrabberH = 4f, GrabberTop = 3f, GrabberAlpha = 0.3f;

    public static readonly ColorF SheetFill = ColorF.FromRgba(0x2c, 0x2c, 0x2c);

    // ── the top bar ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The pill's node key (the tests find the centred selector by it).</summary>
    public const string PillKey = "winui-stage-pill";

    /// <summary>The options glyph's node key (the popover's anchor).</summary>
    public const string OptionsKey = "winui-stage-options-button";

    /// <summary>The mode pill: 40 tall, 4 inside at the sides (W-Stage: `height: 40px; padding: 0 4px; border-radius: 8px`).</summary>
    public const float PillH = 40f, PillPadX = 4f;

    /// <summary>The gap between the right cluster's buttons.</summary>
    public const float BarButtonGap = 4f;

    /// <summary>The wordmark: the 16 accent tile (r4), the 12 / 16 words, 12 apart.</summary>
    public const float BrandMark = 16f, BrandSize = 12f, BrandLine = 16f, BrandGap = 12f;

    public static readonly CornerRadius4 BrandMarkCorners = CornerRadius4.All(4f);

    public const ushort BrandWeight = 600;

    /// <summary>A glyph button's glyph.</summary>
    public const float BarGlyphSize = 16f;

    /// <summary>A word button (Romanization, the face, the layout): its glyph, its 12 / 16 word, the chevron, the gap and the side padding.</summary>
    public const float WordGlyph = 14f, WordSize = 12f, WordLine = 16f, WordChevron = 10f, WordGap = 6f, WordPadX = 10f;

    // ── the transport card ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The identity column: the cover's corner (r4), the gap to the titles, the titles' rhythm (Visualizer mode: 20 / 28 over 14 / 20; otherwise
    /// 14 / 20 over 12 / 16), "Playing from" (12 / 16), the chips' gap and type.</summary>
    public const float LeadArtCorners = 4f, LeadGap = 12f, LeadLineGap = 2f;

    public const float LeadTitleBig = 20f, LeadTitleBigLine = 28f, LeadTitle = 14f, LeadTitleLine = 20f, LeadMetaBig = 14f, LeadMetaBigLine = 20f;

    public const float LeadMeta = 12f, LeadMetaLine = 16f, LeadChipsTop = 2f, LeadChipGap = 6f, ChipText = 12f, ChipLine = 16f;

    public const ushort LeadTitleWeight = 600;

    /// <summary>Huge's source column: "Playing from" at 14 / 20 and its gap to the chips.</summary>
    public const float SourceBig = 14f, SourceBigLine = 20f, SourceGap = 6f;

    /// <summary>The seek row's times: 12 / 16.</summary>
    public const float TimeSize = 12f;

    /// <summary>The centre column never narrows below this (the five buttons and a short seek), and the right cluster's device button never widens past this.</summary>
    public const float CenterMinW = 320f, DeviceMaxW = 180f;

    /// <summary>The device button's glyph, name and chevron.</summary>
    public const float DeviceGlyph = 14f, DeviceName = 14f, DeviceNameLine = 20f, DeviceChevron = 10f, DeviceGap = 8f;

    /// <summary>The volume slider's thickness (the 18 thumb box of the board's 4 rail) and the rail's ink.</summary>
    public const float VolumeThickness = 32f, RailAlpha = 0.5442f, RailDisabledAlpha = 0.235f, ThumbDisabledAlpha = 0.3f;

    /// <summary>The thumb tooltip reads 0..1 as a percent.</summary>
    public const float PercentScale = 100f;

    public const int PercentScaleMax = 100;

    /// <summary>The Compact stack: the times' row under the seek (12 / 16), the foot's "from ..." (12 / 16).</summary>
    public const float StackTimeLine = 16f, StackGap = 4f;

    // ── the now-playing card ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The card's type (W-Stage-Small, W-Stage-Huge): the eyebrow 12 / 16, the title 20 / 28 semibold, the artists 14 / 20, the gap 2.</summary>
    public const float CardEyebrow = 12f, CardEyebrowLine = 16f, CardTitle = 20f, CardTitleLine = 28f, CardMeta = 14f, CardMetaLine = 20f, CardLineGap = 2f;
}
