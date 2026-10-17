// ── Themes/Zune/Structure/Rules/StageGeometry.cs — the numbers of Zune's stage frame and its tier records (P5 Z4b) ─────────────────
//
// Read off the boards Z-NowPlaying (the frame: the 48 top bar, the flat 112 transport plate), Z-NowPlaying-Card, Z-NowPlaying-LargeArt,
// Z-Lyrics and Z-Queue-Page (all 1440 x 900, the Standard window tier):
//
//   TOP BAR    48 tall, the window's gutter on the left (48) and 24 on the right, 24 between its parts: "now playing · full screen" (13/18,
//              secondary ink), the four lowercase pivots centred (20/28 display, 350 at rest, 600 selected, 24 apart), then the WORD
//              ACTIONS ("layout · …", "face · …" in Visualizer mode, "options", "exit full screen"; 13/18). Over a face it carries a 0.7
//              veil fading to nothing.
//   PLATE      the transport is a FLAT plate across the stage's foot, 112 tall, the stage veil at 0.85 over a face and opaque under the
//              panes (Z-Lyrics, Z-Queue-Page draw #0a0a0a): the 36 seek row (12/16 times, the 2-px seek line between them, 16 apart), then
//              the 76 row: "playing from …" left, shuffle · previous · the 56 accent DISC · next · repeat centred 24 apart, heart · mute
//              · the 128 volume line · the device word · "…" right, 8 apart. The plate's sides are the window's gutter.
//
// THE STAGE POLICY. The core allocator (`Stage.Layout`) budgets the face-safe rect, the caption and the pane from the transport's height
// and gutter; Zune's plate is full-bleed, so its gutter is 0 on the desktop tiers (the allocator then clears the plate exactly). The
// Compact window keeps the identity card and the on-demand gallery of the core boards (ZT3 designs Zune's Compact, Small, Huge and
// Huge 4K stages; the values below keep each of them whole until then).
//
// Public, so `Wavee.Tests` pins them (the assembly has no InternalsVisibleTo). Literal numbers are allowed here (WVTH003 / WVTH013
// exempt `Rules\**`).

using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

/// <summary>Zune's stage policy (the metrics the core allocator budgets from) and the stage views' per-window-tier record.</summary>
public static class ZuneStageTiers
{
    // ── the metrics the core allocator budgets from ──────────────────────────────────────────────────────────────────────

    /// <summary>The flat plate's height on the wide classes (Z-NowPlaying: 112).</summary>
    public const float PlateH = 112f;

    /// <summary>Compact: the core's 104 stacked card at a 16 gutter, the lyrics at 26, the identity card and the gallery only on request.</summary>
    public static StageTierMetrics CompactMetrics { get; } = new(TransportH: 104f, TransportInset: 16f, LyricsTypeMin: 26f, LyricsTypeMax: 26f)
    {
        IdentityCard = true, GalleryOnDemand = true,
    };

    /// <summary>Small: the full-bleed 112 plate (Z-NowPlaying-Small), the lyrics 30 up to 36.</summary>
    public static StageTierMetrics SmallMetrics { get; } = new(TransportH: PlateH, TransportInset: 0f, LyricsTypeMin: 30f, LyricsTypeMax: 36f);

    /// <summary>Standard: the full-bleed 112 plate, the lyrics 36 grown up to 64.</summary>
    public static StageTierMetrics StandardMetrics { get; } = new(TransportH: PlateH, TransportInset: 0f, LyricsTypeMin: 36f, LyricsTypeMax: 64f);

    /// <summary>Huge and Huge 4K: Standard's plate, the lyrics capped at 48.</summary>
    public static StageTierMetrics HugeMetrics { get; } = StandardMetrics with { LyricsTypeMax = 48f };

    /// <summary>Zune's stage policy: the core's class edges, the metrics above. One instance, so a republish is not a change. Declared
    /// after the metrics it names: initializers run in textual order.</summary>
    public static StagePolicy Policy { get; } = new(AspectThresholds.Default)
    {
        Metrics = StandardMetrics,
        Compact = CompactMetrics,
        Small = SmallMetrics,
        Huge = HugeMetrics,
    };

    // ── the stage views' record (per window tier) ────────────────────────────────────────────────────────────────────────

    private static readonly ZuneStageLayout[] s_stage =
    [
        // Compact (no stage board yet; ZT3): 40 targets everywhere, the pivots one rung down, no bar label and no look words.
        new(WidthTier.Compact, Gutter: ZuneTiers.Frame(WidthTier.Compact).Gutter, TargetH: 40f, BarPadR: 8f, PivotSize: 14f, PivotLine: 20f,
            PivotGap: 16f, BarLabel: false, LookWords: false, GlyphBox: 40f, Disc: 56f, VolumeW: 88f, ClusterGap: 16f),
        // Small (Z-NowPlaying-Small): the 24 gutter, 8 on the right, the pivots 20 apart, the 52 disc.
        new(WidthTier.Small, Gutter: ZuneTiers.Frame(WidthTier.Small).Gutter, TargetH: 36f, BarPadR: 8f, PivotSize: 20f, PivotLine: 28f,
            PivotGap: 20f, BarLabel: true, LookWords: false, GlyphBox: 36f, Disc: 52f, VolumeW: 88f, ClusterGap: 20f),
        // Standard (Z-NowPlaying, -Card, -LargeArt, Z-Lyrics, Z-Queue-Page): the boards' 1440 x 900 frame.
        new(WidthTier.Standard, Gutter: ZuneTiers.Frame(WidthTier.Standard).Gutter, TargetH: 36f, BarPadR: 24f, PivotSize: 20f, PivotLine: 28f,
            PivotGap: 24f, BarLabel: true, LookWords: true, GlyphBox: 36f, Disc: 56f, VolumeW: 128f, ClusterGap: 24f),
        // Huge (Z-NowPlaying-Huge): the pivots 22/28, 28 apart, the 72 disc.
        new(WidthTier.Huge, Gutter: ZuneTiers.Frame(WidthTier.Huge).Gutter, TargetH: 36f, BarPadR: 24f, PivotSize: 22f, PivotLine: 28f,
            PivotGap: 28f, BarLabel: true, LookWords: true, GlyphBox: 36f, Disc: 72f, VolumeW: 128f, ClusterGap: 24f),
        // Huge 4K (Z-NowPlaying-Huge4K): the 64 gutter, the pivots 28/36, 36 apart. The board's 280 plate and 96 disc are ZT3's; on the
        // 112 plate the disc stays 72.
        new(WidthTier.Huge4K, Gutter: ZuneTiers.Frame(WidthTier.Huge4K).Gutter, TargetH: 36f, BarPadR: 24f, PivotSize: 28f, PivotLine: 36f,
            PivotGap: 36f, BarLabel: true, LookWords: true, GlyphBox: 36f, Disc: 72f, VolumeW: 128f, ClusterGap: 24f),
    ];

    /// <summary>The stage views' record for window tier <paramref name="window"/>.</summary>
    public static ZuneStageLayout Stage(WidthTier window) => s_stage[Math.Clamp((int)window, 0, s_stage.Length - 1)];

    /// <summary>The stage views' record for <paramref name="facts"/>; Standard's where no frame resolved the tiers (a view mounted alone).</summary>
    public static ZuneStageLayout StageOf(in TierFacts facts) => Stage(facts.Policy is null ? WidthTier.Standard : facts.Window);
}

/// <summary>What Zune's stage views draw in one window tier (the allocator's geometry is <c>Stage.Layout</c>'s, the arrangement of the
/// Card and Large art layouts <see cref="ZuneStageGeometry.Arrange"/>'s).</summary>
/// <param name="Tier">The window tier.</param>
/// <param name="Gutter">The stage's side gutter: the frame's (16 / 24 / 48 / 48 / 64). The top bar's left padding, the plate's sides, the
/// Card and Large art arrangements' margins.</param>
/// <param name="TargetH">The least height of every word, pivot and glyph target of the top bar (40 at Compact: the Compact target rule).</param>
/// <param name="BarPadR">The top bar's right padding.</param>
/// <param name="PivotSize">The pivots' type size.</param>
/// <param name="PivotLine">The pivots' line height.</param>
/// <param name="PivotGap">The gap between two pivots.</param>
/// <param name="BarLabel">The top bar shows "now playing · full screen" on its left.</param>
/// <param name="LookWords">The top bar may show the look's words ("layout · …", "face · …") in Visualizer mode, room permitting.</param>
/// <param name="GlyphBox">A plateless glyph button of the transport.</param>
/// <param name="Disc">The play disc's largest size (the row it sits in caps it).</param>
/// <param name="VolumeW">The volume line's length.</param>
/// <param name="ClusterGap">The gap between the centred buttons (shuffle, previous, the disc, next, repeat).</param>
public sealed record ZuneStageLayout(WidthTier Tier, float Gutter, float TargetH, float BarPadR, float PivotSize, float PivotLine, float PivotGap,
    bool BarLabel, bool LookWords, float GlyphBox, float Disc, float VolumeW, float ClusterGap);

/// <summary>The constants of Zune's stage frame: the root's motion, the top bar, the transport plate and its parts.</summary>
public static partial class ZuneStageGeometry
{
    // ── the root's motion (the production recipes of the stage, kept as they are: owner rule) ──────────────────────────────

    /// <summary>The face fades in from this scale.</summary>
    public const float FaceEnterScale = 0.97f;

    /// <summary>The lyric caption's layer rises in from this far below.</summary>
    public const float CaptionEnterDy = 12f;

    /// <summary>The top bar slides in from above (and out upward), the transport from below, by this much.</summary>
    public const float BarEnterDy = 8f;

    /// <summary>The gallery slides in from the right (or up from the foot on Portrait's sheet) by this much.</summary>
    public const float GallerySlide = 32f;

    /// <summary>The plate yields its width to a gallery that runs to the foot, and the gallery's foot follows a crossing, on this tween.</summary>
    public const float GalleryYieldMs = 550f;

    /// <summary>The Up next column's padding and the distance it slides in from the right.</summary>
    public const float UpNextPad = 20f, UpNextSlide = 32f;

    /// <summary>Portrait's gallery sheet parks above the transport and its two gutters.</summary>
    public const float SheetGutters = 2f;

    /// <summary>The progress line shown while the chrome rests: Zune's 2-px line, growing from its left edge, centred vertically.</summary>
    public const float HairlineH = 2f, HairlineOriginY = 0.5f;

    /// <summary>The smoke under a Card face on the plate arrangement: the core's 440 strip to the stage veil at 0.6 (the plate reads over any face).</summary>
    public const float SmokeH = 440f, SmokeA = 0.6f;

    /// <summary>The Card arrangement's veil over the face (Z-NowPlaying-Card: transparent to 30 %, 0.55 at 70 %, 0.8 at the foot).</summary>
    public const float CardVeilClearStop = 0.3f, CardVeilMidStop = 0.7f, CardVeilMidA = 0.55f, CardVeilFootA = 0.8f;

    // ── the top bar ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The gap between the top bar's parts (the label, the pivots, each word action).</summary>
    public const float BarGap = 24f;

    /// <summary>The top bar's veil over a face (Z-NowPlaying-Card: 0.7 fading to nothing).</summary>
    public const float TopBarVeilA = 0.7f;

    /// <summary>The bar's label and its word actions: 13/18.</summary>
    public const float WordSize = 13f, WordLine = 18f;

    /// <summary>The pivots' estimated row on the boards (four words and three gaps at 20 DIP) and what the side columns must hold for
    /// the look's words to show beside it: below that the bar keeps "options" and "exit full screen" only.</summary>
    public const float PivotRowW = 360f, LookWordsMinSideW = 440f;

    /// <summary>A glyph of the icon-only bar (a narrow stage: options and exit as glyphs) and the gap between the two.</summary>
    public const float BarGlyphSize = 16f, BarGlyphGap = 4f;

    /// <summary>The bar's glyph button's width (the allocator's, a 40 target).</summary>
    public const float BarGlyphW = 40f;

    // ── the transport plate ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The seek row: 36 tall, the times 16 from the line.</summary>
    public const float SeekRowH = 36f, SeekGap = 16f;

    /// <summary>The plate's veil over a face (Z-NowPlaying: 0.85) and under a pane (Z-Lyrics, Z-Queue-Page: opaque).</summary>
    public const float PlateFaceA = 0.85f, PlatePaneA = 1f;

    /// <summary>The right cluster's gap (heart, mute, volume, device, "...").</summary>
    public const float RightGap = 8f;

    /// <summary>The transport glyphs: previous / next / mute 18, shuffle / repeat / heart / "..." 18, the disc's 22.</summary>
    public const float TransportGlyph = 18f, PlayGlyph = 22f;

    /// <summary>"Playing from …": 13/18.</summary>
    public const float SourceSize = 13f, SourceLine = 18f;

    /// <summary>The device word: 36 tall, 8 each side, 13/18, its chevron 10, and the floor it shrinks to (the chevron and a few letters).</summary>
    public const float DeviceH = 36f, DevicePadX = 8f, DeviceNameSize = 13f, DeviceNameLine = 18f, ChevronGlyph = 10f, DeviceMinW = 48f, DeviceMaxW = 220f;

    /// <summary>The volume line: a 2-px track inside a 32 hit box, its square 10 thumb, and the ink alphas of its rail.</summary>
    public const float VolumeTrack = 2f, VolumeThickness = 32f, VolumeThumb = 10f, VolumeThumbInner = 6f, RailA = 0.18f, RailDisabledA = 0.08f, ThumbDisabledA = 0.3f;

    /// <summary>The volume thumb's tooltip reads 0..1 as a percent.</summary>
    public const float PercentScale = 100f;

    public const int PercentScaleMax = 100;

    /// <summary>The estimates the plate seats its clusters by: the right cluster at its floor (heart, mute, the device word's floor, "...",
    /// and their gaps) and the room the volume line takes beside it.</summary>
    public const float RightClusterMinW = 4f * 36f + DeviceMinW + 4f * RightGap + 60f;

    // ── the pure decisions ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The disc in a row <paramref name="rowH"/> tall: the tier's, never taller than the row.</summary>
    public static float DiscFor(ZuneStageLayout tier, float rowH) => MathF.Max(0f, MathF.Min(tier.Disc, rowH));

    /// <summary>The plate's veil: 0.85 over a face (Visualizer mode), opaque under the panes.</summary>
    public static float PlateAlpha(Stage.Mode mode) => mode == Stage.Mode.Visualizer ? PlateFaceA : PlatePaneA;

    /// <summary>How the plate seats its clusters at this stage: stacked (heart · previous · disc · next · "..." spread across) when the
    /// right cluster does not fit beside the centred one, and the volume line only where it still fits.</summary>
    public static (bool Stacked, bool Volume) PlateFit(in Stage.Layout layout, ZuneStageLayout tier, float disc)
    {
        float centre = 4f * tier.GlyphBox + disc + 4f * tier.ClusterGap;
        float side = MathF.Max(0f, (layout.TransportW - 2f * tier.Gutter - centre - 2f * tier.ClusterGap) * 0.5f);
        bool stacked = side < RightClusterMinW;
        return (stacked, !stacked && layout.ShowVolume && side >= RightClusterMinW + RightGap + tier.VolumeW);
    }

    /// <summary>The top bar's side columns (each half of what the pivots leave) hold the look's words.</summary>
    public static bool LookWordsFit(in Stage.Layout layout, ZuneStageLayout tier)
        => !layout.IconOnlySelector && (layout.W - tier.Gutter - tier.BarPadR - PivotRowW - 2f * BarGap) * 0.5f >= LookWordsMinSideW;
}
