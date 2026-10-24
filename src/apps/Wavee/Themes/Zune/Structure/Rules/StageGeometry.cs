// ── Themes/Zune/Structure/Rules/StageGeometry.cs — the numbers of Zune's stage frame (P5 Z4b, tiers ZT3) ──────────────────────────
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
// The per-window-tier numbers (the bar, the plate, the arrangements' records) are `Rules\Tiers.Stage.cs` (ZT3); the constants below are
// the Standard board's and the tier-free parts. Literal numbers are allowed here (WVTH003 / WVTH013 exempt `Rules\**`).

using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

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

    /// <summary>The Centered arrangement's foot (Z-NowPlaying-Centered): clear at its top, the stage veil at 0.7 at the stage's foot.</summary>
    public const float FootVeilA = 0.7f;

    // ── the top bar ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The gap between the top bar's parts at Standard (the tier's <c>BarGap</c> elsewhere).</summary>
    public const float BarGap = 24f;

    /// <summary>The top bar's veil over a face (Z-NowPlaying-Card: 0.7 fading to nothing).</summary>
    public const float TopBarVeilA = 0.7f;

    /// <summary>The bar's label and its word actions at Standard: 13/18.</summary>
    public const float WordSize = 13f, WordLine = 18f;

    /// <summary>The Standard pivots' estimated row and what each side column must hold for the look's words to show beside it.</summary>
    public const float PivotRowW = 360f, LookWordsMinSideW = 440f;

    /// <summary>Where the pivots LEAD the bar (Small): what the look's two words take beside the glyphs.</summary>
    public const float LookWordsMinW = 240f;

    /// <summary>A glyph of the icon-only bar and the gap between two.</summary>
    public const float BarGlyphSize = 16f, BarGlyphGap = 4f;

    /// <summary>The bar's glyph button's width (a 40 target).</summary>
    public const float BarGlyphW = 40f;

    // ── the transport plate ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The Standard seek row: 36 tall, the times 16 from the line.</summary>
    public const float SeekRowH = 36f, SeekGap = 16f;

    /// <summary>Compact's stacked seek block: the row of the two times under the line.</summary>
    public const float StackTimesH = 18f;

    /// <summary>The plate's veil over a face (Z-NowPlaying: 0.85) and under a pane (Z-Lyrics, Z-Queue-Page: opaque).</summary>
    public const float PlateFaceA = 0.85f, PlatePaneA = 1f;

    /// <summary>The right cluster's gap at Standard.</summary>
    public const float RightGap = 8f;

    /// <summary>The Standard transport glyphs (18) and the disc's (22).</summary>
    public const float TransportGlyph = 18f, PlayGlyph = 22f;

    /// <summary>"Playing from …" at Standard: 13/18.</summary>
    public const float SourceSize = 13f, SourceLine = 18f;

    /// <summary>The "next" card (Z-NowPlaying-Card: the 48 thumb flush in a flat plate, "next" over "Get Lucky · Daft Punk", 12 from the thumb
    /// and 16 from the plate's right edge, 12 over the aside) at the Standard source type; it scales with the voice's source type.</summary>
    public const float NextThumb = 48f, NextTextGap = 12f, NextPadR = 16f, NextAboveAside = 12f, NextEyebrowSize = 11f, NextEyebrowLine = 14f;

    /// <summary>The "next" card's scale in a voice: its source type over the Standard one.</summary>
    public static float NextScale(float sourceSize) => sourceSize / SourceSize;

    /// <summary>The device word: 36 tall (the tier's <c>DeviceH</c>), 8 each side, its chevron 10, and the floor it shrinks to.</summary>
    public const float DeviceH = 36f, DevicePadX = 8f, DeviceNameSize = 13f, DeviceNameLine = 18f, ChevronGlyph = 10f, DeviceMinW = 48f, DeviceMaxW = 220f;

    /// <summary>The volume line: a 2-px track inside a 32 hit box, its square 10 thumb, and the ink alphas of its rail.</summary>
    public const float VolumeTrack = 2f, VolumeThickness = 32f, VolumeThumb = 10f, VolumeThumbInner = 6f, RailA = 0.18f, RailDisabledA = 0.08f, ThumbDisabledA = 0.3f;

    /// <summary>The volume thumb's tooltip reads 0..1 as a percent.</summary>
    public const float PercentScale = 100f;

    public const int PercentScaleMax = 100;

    /// <summary>The estimate of the Standard right cluster at its floor (heart, mute, the device word's floor, "...", and their gaps).</summary>
    public const float RightClusterMinW = 4f * 36f + DeviceMinW + 4f * RightGap + 60f;

    // ── the pure decisions ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The disc in a row <paramref name="rowH"/> tall: the tier's, never taller than the row.</summary>
    public static float DiscFor(ZuneStageLayout tier, float rowH) => MathF.Max(0f, MathF.Min(tier.Disc, rowH));

    /// <summary>The plate's control row: what the plate leaves under its seek row (and Compact's third row), capped by the tier's row.</summary>
    public static float PlateRowFor(in Stage.Layout layout, ZuneStageLayout tier, ZunePlateForm form)
    {
        float left = layout.TransportH - tier.SeekRowH - (form == ZunePlateForm.Stack ? tier.PlateFootH : 0f);
        return form == ZunePlateForm.Stacked ? MathF.Max(0f, layout.TransportH - tier.SeekRowH) : MathF.Max(0f, MathF.Min(left, tier.PlateRowH));
    }

    /// <summary>The right cluster's floor in <paramref name="tier"/>: heart, mute, "…" and a glyph's room for the volume's ends, the device's floor.</summary>
    public static float RightMinW(ZuneStageLayout tier) => 4f * tier.GlyphBox + DeviceMinW + 4f * tier.RightGap + 60f;

    /// <summary>How the plate seats its controls at this stage: the tier's kind where it fits, else one stacked row (heart · previous · disc ·
    /// next · "…" spread across); the volume line only where it still fits.</summary>
    public static (ZunePlateForm Form, bool Volume) PlateFit(in Stage.Layout layout, ZuneStageLayout tier)
    {
        if (tier.Plate == ZunePlateKind.Stack)
            return (layout.TransportH >= tier.SeekRowH + tier.PlateRowH + tier.PlateFootH ? ZunePlateForm.Stack : ZunePlateForm.Stacked, false);
        float disc = DiscFor(tier, MathF.Min(tier.PlateRowH, layout.TransportH - tier.SeekRowH));
        float centre = 4f * tier.GlyphBox + disc + 4f * tier.ClusterGap;
        float side = MathF.Max(0f, (layout.TransportW - 2f * tier.Gutter - centre - 2f * tier.ClusterGap) * 0.5f);
        if (tier.Plate == ZunePlateKind.HeartLead)
            return (side >= DeviceMinW + tier.GlyphBox + tier.RightGap ? ZunePlateForm.HeartLead : ZunePlateForm.Stacked, false);
        float floor = RightMinW(tier);
        bool stacked = side < floor;
        return (stacked ? ZunePlateForm.Stacked : ZunePlateForm.Full, !stacked && tier.VolumeW > 0f && layout.ShowVolume && side >= floor + tier.RightGap + tier.VolumeW);
    }

    /// <summary>The top bar has room for the look's words: beside the centred pivots, each side column holds them; where the pivots lead
    /// (Small), what the pivots and the two glyphs leave holds them.</summary>
    public static bool LookWordsFit(in Stage.Layout layout, ZuneStageLayout tier)
    {
        if (layout.IconOnlySelector && !tier.PivotsLead) return false;
        if (tier.PivotsLead)
            return layout.W - tier.BarPadL - tier.BarPadR - tier.PivotRowW - 2f * BarGlyphW - 3f * tier.BarGap >= LookWordsMinW;
        return (layout.W - tier.BarPadL - tier.BarPadR - tier.PivotRowW - 2f * tier.BarGap) * 0.5f >= LookWordsMinSideW;
    }

    /// <summary>A rect the allocator hangs right under its 48 top bar (the identity card, a Portrait or Compact identity, the hero's lyrics
    /// cover) moves down by the tier's drop, so the taller head never covers it.</summary>
    public const float TopHungBand = 48f;

    /// <summary>The drop for a rect at <paramref name="y"/>: the tier's where the allocator hung it under the bar, else none.</summary>
    public static float DropFor(float y, ZuneStageLayout tier) => y < Stage.Layout.TopBarH + TopHungBand ? tier.Drop : 0f;

    /// <summary>The plate's veil: 0.85 over a face (Visualizer mode), opaque under the panes.</summary>
    public static float PlateAlpha(Stage.Mode mode) => mode == Stage.Mode.Visualizer ? PlateFaceA : PlatePaneA;
}
