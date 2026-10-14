// ── Themes/Classic/Structure/Rules/StageGeometry.cs — the numbers of Classic's stage frame (themes plan 6.1, T7a1) ─────────
//
// What `Shell\Stage.UI.cs` spelled inline in the root, the chrome host, the top bar and the transport card, moved verbatim with
// the pictures: the chrome's Enter/Exit offsets, the top bar's selector and brand metrics, the transport card's paddings, glyph and
// disc sizes and the volume slider's ink alphas. They are data, not decisions: `Views\Stage\ClassicStageRoot.cs`, `ClassicStageTopBar.cs`
// and `ClassicStageTransport.cs` read them (WVTH003 allows literals only here, in `Rules\**`). The options popover, the gallery and the
// caption keep theirs in `StageGeometry.Options.cs`, `StageGeometry.Gallery.cs` and `StageGeometry.Caption.cs`.
//
// WHERE THE ALLOCATOR'S NUMBERS ARE. The chrome's authored extents (the top bar's 48, the transport's 112 / 96, the gutter, the gallery's
// column, the hairline) and the aspect classes with their hysteresis (`StagePolicy.Aspect` -> `Stage.Layout.ClassOf/Resolve`) are NOT
// here: the core allocator budgets the face-safe rect, the caption and the pane from them (`Stage.Layout`), so the view reads the
// constants by name. What this file adds is the look of the parts inside those extents.

using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Themes.Classic;

/// <summary>The constants of Classic's stage root, top bar and transport card.</summary>
internal static class ClassicStageGeometry
{
    // ── the root ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The face fades in from this scale.</summary>
    public const float FaceEnterScale = 0.97f;

    /// <summary>The lyric caption's layer rises in from this far below.</summary>
    public const float CaptionEnterDy = 12f;

    /// <summary>The hairline progress grows from its left edge, centred vertically.</summary>
    public const float HairlineOriginY = 0.5f;

    // ── the chrome's placement ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The top bar slides in from above (and out upward), the transport card from below, by this much.</summary>
    public const float BarEnterDy = 8f;

    /// <summary>The gallery slides in from the right (or up from the foot on Portrait's sheet) by this much.</summary>
    public const float GallerySlide = 32f;

    // ── the pane region (Lyrics, Up next, Artist: `Stage.Pane`) ──

    /// <summary>The region slides to its new rect with the cover (Lyrics ⇄ Large art) on this position spring: response, damping.</summary>
    public const float PaneMoveResponse = 0.45f, PaneMoveDamping = 0.90f;

    /// <summary>A pane's body enters rising from this far below, fading in over <see cref="PaneFadeMs"/> (FluentDecelerate); it exits
    /// fading out in place.</summary>
    public const float PaneEnterDy = 40f;

    public const float PaneFadeMs = 500f;

    /// <summary>Portrait's gallery sheet parks above the transport card and its two gutters.</summary>
    public const float SheetGutters = 2f;

    // ── the top bar ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The bar's padding: Spacing.L left, Spacing.S right.</summary>
    public static Edges4 TopBarPadding => new(Spacing.L, 0f, Spacing.S, 0f);

    /// <summary>The bar's gradient veil starts at this alpha and fades to nothing.</summary>
    public const float TopBarVeilA = 0.42f;

    /// <summary>The SelectorBar's selected weight, item height and the gap between items.</summary>
    public const ushort SelectorWeight = 600;

    public const float SelectorItemHeight = 36f, SelectorItemGap = 4f;

    /// <summary>The brand mark: an accent tile with a 4.5 corner.</summary>
    public const float BrandMark = 18f;

    public static readonly CornerRadius4 BrandMarkCorners = CornerRadius4.All(4.5f);

    /// <summary>The brand text: "Wavee" and the subtitle.</summary>
    public const float BrandSize = 12f, BrandLine = 16f;

    public const ushort BrandWeight = 600;

    /// <summary>A glass glyph button of the bar (the options toggle, the icon-only exit): 40 wide (the allocator's) by this tall, with this glyph.</summary>
    public const float BarButtonHeight = 36f, BarGlyphSize = 16f;

    // ── the transport card ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The card's padding: 20 · 10 · 20 · 0 (the seek row sits 10 below the top).</summary>
    public static readonly Edges4 TransportPadding = new(20f, 10f, 20f, 0f);

    /// <summary>The cluster row is at most this tall, and leaves this much of the card to the padding and the seek hit-box
    /// (10 pad + 32 seek hit-box + the row ≤ the card; Compact is 96).</summary>
    public const float ClusterRowMax = 58f, ClusterRowReserve = 46f;

    /// <summary>The "Playing from" label and the source's name.</summary>
    public const float SourceLabelSize = 12f, SourceLabelLine = 16f, SourceNameSize = 14f, SourceNameLine = 20f;

    public const ushort SourceNameWeight = 600;

    /// <summary>A plateless glyph button's box, the transport glyphs (previous, next, mute), the satellites' glyph and the heart's.</summary>
    public const float GlyphBox = 40f, TransportGlyph = 17f, SatelliteGlyph = 16f, HeartGlyph = 18f;

    /// <summary>A latched satellite's hover fill alpha.</summary>
    public const float LatchedHoverAlpha = 0.9f;

    /// <summary>The play disc, and its Compact size, and its glyph.</summary>
    public const float PlayDisc = 56f, PlayDiscCompact = 52f, PlayGlyph = 22f;

    /// <summary>The right cluster's gap.</summary>
    public const float RightClusterGap = 6f;

    // ── the volume slider ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The slider's thickness.</summary>
    public const float VolumeThickness = 32f;

    /// <summary>The rail's ink alpha at rest and disabled, and the value / thumb fills' disabled alpha.</summary>
    public const float RailAlpha = 0.545f, RailDisabledAlpha = 0.235f, ThumbDisabledAlpha = 0.3f;

    /// <summary>The thumb tooltip reads 0..1 as a percent: the scale and its ceiling.</summary>
    public const float PercentScale = 100f;

    public const int PercentScaleMax = 100;

    // ── the device button ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The button's height, padding (11 left, 10 right) and widest extent; the floor is the allocator's (<c>Stage.Layout.DeviceMinW</c>).</summary>
    public const float DeviceHeight = 32f, DeviceMaxWidth = 220f;

    public static readonly Edges4 DevicePadding = new(11f, 0f, 10f, 0f);

    /// <summary>The glyph, the name and the chevron.</summary>
    public const float DeviceGlyph = 14f, DeviceNameSize = 14f, DeviceNameLine = 20f, ChevronGlyph = 12f;
}
