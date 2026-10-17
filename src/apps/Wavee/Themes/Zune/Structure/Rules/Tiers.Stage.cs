// ── Themes/Zune/Structure/Rules/Tiers.Stage.cs — the fullscreen stage's tier records (P5 ZT3) ──────────────────────────────────────
//
// Read off the stage boards at every window tier (Z-NowPlaying-*, Z-Lyrics-*, Z-Queue-Page-*; Standard is Z4b's frame, unchanged):
//
//   Compact   (Z-NowPlaying-Compact, Z-Lyrics-Compact, Z-Queue-Page-Compact, 400 x 860) the 48 bar (exit chevron, "now playing", options)
//             over the pivots' OWN 48 row (28/36, rotated so the lit word leads, scrolling under an edge fade: they never clip); the 152
//             plate is a STACK of three rows (the 36 seek row, the 68 row shuffle · previous · the 56 disc · next · repeat spread across,
//             the 44 row heart · device · "…"); Visualizer mode stacks the cover, the titles and the controls down the column (the
//             Stack arrangement: the cover at 0.76 of the width, the 40/48 title, the 64 disc). Every target is at least 40.
//   Small     (Z-NowPlaying-Small, Z-Lyrics-Small, Z-Queue-Page-Small, 800 x 600) the pivots LEAD the 48 bar (20/28, 20 apart), the look's
//             words and the options / exit glyphs at its right; the 96 plate (the 30 seek row, the 66 row: heart · the centred cluster
//             with the 48 disc · device · "…"); Large art is the 360 cover beside a 360 column (the Column arrangement, one rung down).
//   Standard  Z4b's frame: the 48 bar, the 112 plate, the 600 card, the 532 column.
//   Huge      (Z-NowPlaying-Huge, Z-Lyrics-Huge, Z-Queue-Page-Huge, 2560 x 1440) the 56 bar (pivots 22/28 28 apart, words 14), the 64 gutter,
//             the 120 plate (40 seek row, 80 row, 44 targets, the 64 disc, the 160 volume line); the Card arrangement is the 860 card
//             (padding 32, the 360 cover, the 64/72 title, the 72 disc) under the face's 150/160 display title; Lyrics mode docks the 440
//             Up next column at the right.
//   Huge 4K   (Z-NowPlaying-Huge4K, 3840 x 2160) the 72 bar (pivots 28/36 36 apart, words 17), the 96 gutter, the 280 plate (the 72
//             seek row, the 160 row: 56 targets 40 apart, the 96 disc, the 220 volume line), larger type everywhere. The allocator reads
//             the 280 plate from the stage's own Huge 4K rung (`StagePolicy.HugeWide`, `stage.huge4k` at 3200, held 24).
//
// The arrangements' records (`ZuneStageCard`, `ZuneStageColumn`, `ZuneStageCentered`, `ZuneStageStack`) carry the voice they draw in
// (`ZuneStageVoice`); an arrangement that does not fit at its tier's size tries Standard's before it gives way to the plate.
// Public, so `Wavee.Tests` pins them. Literal numbers are allowed here (WVTH003 / WVTH013 exempt `Rules\**`).

using System.Collections.Immutable;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

public static partial class ZuneTiers
{
    /// <summary>The stage's Huge 4K rung (the 280 plate, the larger type): the Huge 4K edge on the window axis, held 24.</summary>
    public const string StageHuge4KRung = "stage.huge4k";

    static partial void RungsStage(ImmutableArray<NamedRung>.Builder rungs)
        => rungs.Add(new NamedRung(StageHuge4KRung, new TierEdge(Huge4KFrom, NarrowHold: Hold, WidenHold: 0f), RungAxis.Window));
}

/// <summary>Zune's stage policy (the metrics the core allocator budgets from) and the stage views' per-window-tier record.</summary>
public static class ZuneStageTiers
{
    // ── the metrics the core allocator budgets from ──────────────────────────────────────────────────────────────────────

    /// <summary>The Standard plate's height (Z-NowPlaying: 112).</summary>
    public const float PlateH = 112f;

    /// <summary>Compact: the 152 plate stack, full-bleed, the lyrics at 26, the identity card in Lyrics / Up next, the gallery on request.</summary>
    public static StageTierMetrics CompactMetrics { get; } = new(TransportH: 152f, TransportInset: 0f, LyricsTypeMin: 26f, LyricsTypeMax: 26f)
    {
        IdentityCard = true, GalleryOnDemand = true,
    };

    /// <summary>Small: the 96 plate (Z-Lyrics-Small), the lyrics 30 up to 36.</summary>
    public static StageTierMetrics SmallMetrics { get; } = new(TransportH: 96f, TransportInset: 0f, LyricsTypeMin: 30f, LyricsTypeMax: 36f);

    /// <summary>Standard: the full-bleed 112 plate, the lyrics 36 grown up to 64.</summary>
    public static StageTierMetrics StandardMetrics { get; } = new(TransportH: PlateH, TransportInset: 0f, LyricsTypeMin: 36f, LyricsTypeMax: 64f);

    /// <summary>Huge: the 120 plate (Z-Lyrics-Huge), the lyrics capped at 48, the 440 Up next column beside them.</summary>
    public static StageTierMetrics HugeMetrics { get; } = new(TransportH: 120f, TransportInset: 0f, LyricsTypeMin: 36f, LyricsTypeMax: 48f) { UpNextW = 440f };

    /// <summary>Huge 4K: the 280 plate (Z-NowPlaying-Huge4K), the lyrics 40 up to 64, the 560 Up next column.</summary>
    public static StageTierMetrics Huge4KMetrics { get; } = new(TransportH: 280f, TransportInset: 0f, LyricsTypeMin: 40f, LyricsTypeMax: 64f) { UpNextW = 560f };

    /// <summary>Zune's stage policy: the core's class edges, the metrics above (Huge 4K through the stage's own rung). One instance, so a
    /// republish is not a change. Declared after the metrics it names: initializers run in textual order.</summary>
    public static StagePolicy Policy { get; } = new(AspectThresholds.Default)
    {
        Metrics = StandardMetrics,
        Compact = CompactMetrics,
        Small = SmallMetrics,
        Huge = HugeMetrics,
        HugeWide = Huge4KMetrics,
        HugeWideRung = ZuneTiers.StageHuge4KRung,
    };

    // ── the arrangements' voices and records ─────────────────────────────────────────────────────────────────────────────

    static readonly ZuneStageVoice s_cardStd = new(TitleSize: 40f, TitleLine: 48f, TitleWeight: 300, TitleTracking: -12f, MetaSize: 20f, MetaLine: 28f,
        MetaWeight: 400, EyebrowSize: 13f, EyebrowLine: 18f, SourceSize: 13f, SourceLine: 18f, SeekH: 36f, RowH: 56f, RowGap: 20f, Disc: 56f, Box: 36f,
        Glyph: 18f, PlayGlyph: 22f, VolumeW: 128f, AsideH: 36f);

    static readonly ZuneStageVoice s_cardHuge = new(64f, 72f, 300, -16f, 26f, 32f, 400, 13f, 18f, 14f, 20f, 44f, 72f, 28f, 72f, 44f, 20f, 26f, 160f, 44f);

    static readonly ZuneStageVoice s_card4K = new(80f, 88f, 300, -20f, 32f, 40f, 400, 17f, 22f, 17f, 22f, 56f, 96f, 40f, 96f, 56f, 26f, 36f, 220f, 56f);

    static readonly ZuneStageCard s_cardStandard = new(MaxW: 600f, MinW: 520f, Pad: 24f, Art: 220f, ArtGap: 24f, SeekTop: 16f, AsideW: 320f, FaceRoomMin: 120f,
        DisplaySize: 0f, DisplayLine: 0f, DisplayMetaSize: 0f, DisplayMetaLine: 0f, DisplayInset: 0f, s_cardStd);

    static readonly ZuneStageCard s_cardHugeRec = new(860f, 720f, 32f, 360f, 32f, 20f, 440f, 160f, 150f, 160f, 28f, 36f, 12f, s_cardHuge);

    static readonly ZuneStageCard s_card4KRec = new(1120f, 960f, 40f, 480f, 40f, 24f, 600f, 200f, 200f, 212f, 36f, 44f, 16f, s_card4K);

    static readonly ZuneStageVoice s_colStd = new(120f, 120f, 200, -17f, 28f, 36f, 600, 13f, 18f, 13f, 18f, 36f, 56f, 24f, 56f, 36f, 18f, 22f, 128f, 36f);

    static readonly ZuneStageVoice s_colSmall = new(64f, 72f, 200, -10f, 22f, 28f, 600, 13f, 18f, 13f, 18f, 32f, 56f, 18f, 52f, 40f, 17f, 20f, 96f, 40f);

    static readonly ZuneStageVoice s_colHuge = new(150f, 150f, 200, -20f, 32f, 40f, 600, 13f, 18f, 14f, 20f, 44f, 72f, 28f, 72f, 44f, 20f, 26f, 160f, 44f);

    static readonly ZuneStageVoice s_col4K = new(200f, 200f, 200, -24f, 40f, 48f, 600, 17f, 22f, 17f, 22f, 56f, 96f, 40f, 96f, 56f, 26f, 36f, 220f, 56f);

    static readonly ZuneStageColumn s_columnStandard = new(Top: 96f, Gap: 56f, MinW: 420f, CoverMin: 240f, ControlsMaxW: 532f, TitlesH: 184f, TitleMax: 120f,
        TitleMin: 64f, SeekTop: 40f, VolumeTop: 16f, VolumeRow: true, AsideW: 320f, PaneTop: 16f, PaneGap: 24f, PaneMinH: 120f, s_colStd);

    static readonly ZuneStageColumn s_columnSmall = new(64f, 32f, 360f, 200f, 360f, 108f, 64f, 40f, 16f, 16f, false, 240f, 16f, 16f, 96f, s_colSmall);

    static readonly ZuneStageColumn s_columnHuge = new(112f, 64f, 560f, 320f, 640f, 224f, 150f, 80f, 40f, 16f, true, 440f, 16f, 32f, 160f, s_colHuge);

    static readonly ZuneStageColumn s_column4K = new(136f, 80f, 720f, 400f, 860f, 288f, 200f, 96f, 48f, 24f, true, 600f, 24f, 40f, 200f, s_col4K);

    static readonly ZuneStageVoice s_cenSmall = new(32f, 40f, 300, -8f, 18f, 24f, 400, 13f, 18f, 13f, 18f, 32f, 56f, 20f, 48f, 36f, 17f, 20f, 96f, 36f);

    static readonly ZuneStageVoice s_cenStd = new(40f, 48f, 300, -12f, 20f, 28f, 400, 13f, 18f, 13f, 18f, 36f, 64f, 24f, 56f, 36f, 18f, 22f, 128f, 36f);

    static readonly ZuneStageVoice s_cenHuge = new(56f, 64f, 300, -14f, 26f, 32f, 400, 13f, 18f, 14f, 20f, 44f, 80f, 28f, 72f, 44f, 20f, 26f, 160f, 44f);

    static readonly ZuneStageVoice s_cen4K = new(72f, 80f, 300, -18f, 32f, 40f, 400, 17f, 22f, 17f, 22f, 56f, 112f, 40f, 96f, 56f, 26f, 36f, 220f, 56f);

    static readonly ZuneStageCentered s_centeredSmall = new(CoverMax: 240f, CoverMin: 120f, CoverFrac: 0.4f, CoverWFrac: 0.25f, TitleTop: 20f, MetaGap: 2f,
        SeekTop: 16f, SeekMaxW: 480f, FootH: 64f, FootSideW: 240f, CaptionGap: 12f, s_cenSmall);

    static readonly ZuneStageCentered s_centeredStandard = new(360f, 160f, 0.4f, 0.25f, 28f, 2f, 24f, 640f, 76f, 360f, 16f, s_cenStd);

    static readonly ZuneStageCentered s_centeredHuge = new(520f, 200f, 0.4f, 0.25f, 36f, 4f, 32f, 880f, 96f, 480f, 20f, s_cenHuge);

    static readonly ZuneStageCentered s_centered4K = new(720f, 280f, 0.4f, 0.25f, 48f, 6f, 40f, 1200f, 120f, 900f, 24f, s_cen4K);

    static readonly ZuneStageVoice s_stackVoice = new(40f, 48f, 300, -12f, 20f, 28f, 400, 13f, 18f, 13f, 18f, 40f, 72f, 0f, 64f, 48f, 20f, 24f, 0f, 48f);

    static readonly ZuneStageStack s_stack = new(Top: 12f, CoverFrac: 0.76f, CoverMin: 160f, TitleTop: 20f, SeekTop: 18f, BottomPad: 16f, s_stackVoice);

    // ── the stage views' record (per window tier) ────────────────────────────────────────────────────────────────────────

    private static readonly ZuneStageLayout[] s_stage =
    [
        // Compact (Z-NowPlaying-Compact, Z-Lyrics-Compact, Z-Queue-Page-Compact)
        new()
        {
            Tier = WidthTier.Compact, Gutter = 16f, TargetH = 40f, BarH = 48f, PivotRowH = 48f, BarPadL = 4f, BarPadR = 4f, BarGap = 4f,
            PivotSize = 28f, PivotLine = 36f, PivotGap = 24f, PivotRowW = 0f, PivotsLead = false, BarLabel = true, LookWords = false, GlyphActions = true,
            WordSize = 13f, WordLine = 18f, Plate = ZunePlateKind.Stack, SeekRowH = 36f, PlateRowH = 68f, PlateFootH = 44f,
            GlyphBox = 44f, Glyph = 18f, PlayGlyph = 22f, Disc = 56f, VolumeW = 0f, ClusterGap = 16f, RightGap = 4f, SourceSize = 13f, SourceLine = 18f,
            DeviceH = 40f, Card = s_cardStandard, Column = s_columnStandard, Centered = null, Stack = s_stack,
        },
        // Small (Z-NowPlaying-Small, Z-Lyrics-Small, Z-Queue-Page-Small)
        new()
        {
            Tier = WidthTier.Small, Gutter = 24f, TargetH = 36f, BarH = 48f, PivotRowH = 0f, BarPadL = 24f, BarPadR = 8f, BarGap = 20f,
            PivotSize = 20f, PivotLine = 28f, PivotGap = 20f, PivotRowW = 340f, PivotsLead = true, BarLabel = false, LookWords = true, GlyphActions = true,
            WordSize = 13f, WordLine = 18f, Plate = ZunePlateKind.HeartLead, SeekRowH = 30f, PlateRowH = 66f, PlateFootH = 0f,
            GlyphBox = 36f, Glyph = 17f, PlayGlyph = 20f, Disc = 48f, VolumeW = 96f, ClusterGap = 20f, RightGap = 8f, SourceSize = 13f, SourceLine = 18f,
            DeviceH = 36f, Card = s_cardStandard, Column = s_columnSmall, Centered = s_centeredSmall, Stack = null,
        },
        // Standard (Z-NowPlaying, -Card, -LargeArt, -Centered, Z-Lyrics, Z-Queue-Page): Z4b's frame.
        new()
        {
            Tier = WidthTier.Standard, Gutter = 48f, TargetH = 36f, BarH = 48f, PivotRowH = 0f, BarPadL = 48f, BarPadR = 24f, BarGap = 24f,
            PivotSize = 20f, PivotLine = 28f, PivotGap = 24f, PivotRowW = 360f, PivotsLead = false, BarLabel = true, LookWords = true, GlyphActions = false,
            WordSize = 13f, WordLine = 18f, Plate = ZunePlateKind.Full, SeekRowH = 36f, PlateRowH = 76f, PlateFootH = 0f,
            GlyphBox = 36f, Glyph = 18f, PlayGlyph = 22f, Disc = 56f, VolumeW = 128f, ClusterGap = 24f, RightGap = 8f, SourceSize = 13f, SourceLine = 18f,
            DeviceH = 36f, Card = s_cardStandard, Column = s_columnStandard, Centered = s_centeredStandard, Stack = null,
        },
        // Huge (Z-NowPlaying-Huge, Z-Lyrics-Huge, Z-Queue-Page-Huge)
        new()
        {
            Tier = WidthTier.Huge, Gutter = 64f, TargetH = 44f, BarH = 56f, PivotRowH = 0f, BarPadL = 64f, BarPadR = 24f, BarGap = 28f,
            PivotSize = 22f, PivotLine = 28f, PivotGap = 28f, PivotRowW = 400f, PivotsLead = false, BarLabel = true, LookWords = true, GlyphActions = false,
            WordSize = 14f, WordLine = 20f, Plate = ZunePlateKind.Full, SeekRowH = 40f, PlateRowH = 80f, PlateFootH = 0f,
            GlyphBox = 44f, Glyph = 20f, PlayGlyph = 24f, Disc = 64f, VolumeW = 160f, ClusterGap = 28f, RightGap = 8f, SourceSize = 14f, SourceLine = 20f,
            DeviceH = 44f, Card = s_cardHugeRec, Column = s_columnHuge, Centered = s_centeredHuge, Stack = null,
            UpNextGap = 64f, LyricsPaneMaxW = 900f,
        },
        // Huge 4K (Z-NowPlaying-Huge4K)
        new()
        {
            Tier = WidthTier.Huge4K, Gutter = 96f, TargetH = 56f, BarH = 72f, PivotRowH = 0f, BarPadL = 96f, BarPadR = 32f, BarGap = 36f,
            PivotSize = 28f, PivotLine = 36f, PivotGap = 36f, PivotRowW = 520f, PivotsLead = false, BarLabel = true, LookWords = true, GlyphActions = false,
            WordSize = 17f, WordLine = 22f, Plate = ZunePlateKind.Full, SeekRowH = 72f, PlateRowH = 160f, PlateFootH = 0f,
            GlyphBox = 56f, Glyph = 26f, PlayGlyph = 36f, Disc = 96f, VolumeW = 220f, ClusterGap = 40f, RightGap = 14f, SourceSize = 17f, SourceLine = 22f,
            DeviceH = 56f, Card = s_card4KRec, Column = s_column4K, Centered = s_centered4K, Stack = null,
            UpNextGap = 336f,
        },
    ];

    /// <summary>The stage views' record for window tier <paramref name="window"/>.</summary>
    public static ZuneStageLayout Stage(WidthTier window) => s_stage[Math.Clamp((int)window, 0, s_stage.Length - 1)];

    /// <summary>The stage views' record for <paramref name="facts"/>; Standard's where no frame resolved the tiers (a view mounted alone).</summary>
    public static ZuneStageLayout StageOf(in TierFacts facts) => Stage(facts.Policy is null ? WidthTier.Standard : facts.Window);
}

/// <summary>How the plate seats its controls in a window tier.</summary>
public enum ZunePlateKind : byte
{
    /// <summary>Three clusters on one row: "playing from …" left, the centred cluster, heart · mute · volume · device · "…" right.</summary>
    Full = 0,

    /// <summary>Small: heart left, the centred cluster, device · "…" right.</summary>
    HeartLead = 1,

    /// <summary>Compact: three rows (the seek row; shuffle · previous · disc · next · repeat spread across; heart · device · "…").</summary>
    Stack = 2,
}

/// <summary>How the plate is drawn at one stage: the tier's kind, or one stacked row where the kind does not fit.</summary>
public enum ZunePlateForm : byte { Full = 0, HeartLead = 1, Stack = 2, Stacked = 3 }

/// <summary>What Zune's stage views draw in one window tier (the allocator's geometry is <c>Stage.Layout</c>'s, the arrangements
/// <see cref="ZuneStageGeometry.Arrange"/>'s).</summary>
public sealed record ZuneStageLayout
{
    /// <summary>The window tier.</summary>
    public required WidthTier Tier { get; init; }

    /// <summary>The stage's side gutter (the boards': 16 / 24 / 48 / 64 / 96): the plate's sides, the arrangements' margins.</summary>
    public required float Gutter { get; init; }

    /// <summary>The least height of every word, pivot and glyph target of the top bar (40 at Compact).</summary>
    public required float TargetH { get; init; }

    /// <summary>The top bar's height (48, 56 at Huge, 72 at Huge 4K).</summary>
    public required float BarH { get; init; }

    /// <summary>The pivots' own row under the bar (Compact: 48); 0: the pivots sit in the bar.</summary>
    public required float PivotRowH { get; init; }

    /// <summary>The bar's left and right padding and the gap between its parts.</summary>
    public required float BarPadL { get; init; }

    public required float BarPadR { get; init; }

    public required float BarGap { get; init; }

    /// <summary>The pivots' type size, line height and gap.</summary>
    public required float PivotSize { get; init; }

    public required float PivotLine { get; init; }

    public required float PivotGap { get; init; }

    /// <summary>The pivots' estimated row in the bar (four words, three gaps): what the look's words must clear.</summary>
    public required float PivotRowW { get; init; }

    /// <summary>Small: the pivots lead the bar (no label), the actions at its right.</summary>
    public required bool PivotsLead { get; init; }

    /// <summary>The bar shows its label ("now playing · full screen"; "now playing" at Compact).</summary>
    public required bool BarLabel { get; init; }

    /// <summary>The bar may show the look's words ("layout · …", "face · …") in Visualizer mode, room permitting.</summary>
    public required bool LookWords { get; init; }

    /// <summary>Options and exit are 40-wide glyphs ("…", exit), not words.</summary>
    public required bool GlyphActions { get; init; }

    /// <summary>The bar's words (label, actions): type size and line height.</summary>
    public required float WordSize { get; init; }

    public required float WordLine { get; init; }

    /// <summary>How the plate seats its controls.</summary>
    public required ZunePlateKind Plate { get; init; }

    /// <summary>The plate's seek row, its control row (capped: the 280 plate keeps a 160 row) and Compact's third row.</summary>
    public required float SeekRowH { get; init; }

    public required float PlateRowH { get; init; }

    public required float PlateFootH { get; init; }

    /// <summary>A plateless glyph button of the plate, its glyph, the disc's glyph.</summary>
    public required float GlyphBox { get; init; }

    public required float Glyph { get; init; }

    public required float PlayGlyph { get; init; }

    /// <summary>The plate's disc at its largest (the row caps it).</summary>
    public required float Disc { get; init; }

    /// <summary>The volume line's length (0: none on the plate).</summary>
    public required float VolumeW { get; init; }

    /// <summary>The gap between the centred buttons, and between the right cluster's parts.</summary>
    public required float ClusterGap { get; init; }

    public required float RightGap { get; init; }

    /// <summary>"Playing from …": type size and line height.</summary>
    public required float SourceSize { get; init; }

    public required float SourceLine { get; init; }

    /// <summary>The device word's height (a target: 40 at Compact).</summary>
    public required float DeviceH { get; init; }

    /// <summary>The Card arrangement (Z-NowPlaying-Card, -Huge).</summary>
    public required ZuneStageCard Card { get; init; }

    /// <summary>The Large art column (Z-NowPlaying-LargeArt, -Small).</summary>
    public required ZuneStageColumn Column { get; init; }

    /// <summary>The Centered arrangement (Z-NowPlaying-Centered); null: the allocator's.</summary>
    public required ZuneStageCentered? Centered { get; init; }

    /// <summary>The Compact Visualizer stack (Z-NowPlaying-Compact); null: none.</summary>
    public required ZuneStageStack? Stack { get; init; }

    /// <summary>Lyrics mode docks the Up next column at the right (Huge, Huge 4K): the least gap between the lyrics pane's right edge and the
    /// column's left edge (Huge: the gutter; Huge 4K: the board's 336). Unused where the tier docks none.</summary>
    public float UpNextGap { get; init; }

    /// <summary>The widest the lyrics pane runs on a plate stage (Z-Lyrics-Huge: 900, so the 48 type keeps a reading measure and its lines
    /// do not strand one word); 0: none (the column's own cap of 1000 times the type scale stands).</summary>
    public float LyricsPaneMaxW { get; init; }

    /// <summary>The chrome at the stage's head: the bar and the pivots' row.</summary>
    public float HeadH => BarH + PivotRowH;

    /// <summary>How far the head runs below the allocator's 48 top bar: what every rect hung under the bar moves down by.</summary>
    public float Drop => MathF.Max(0f, HeadH - Stage.Layout.TopBarH);
}

/// <summary>The voice an arrangement draws its titles and controls in.</summary>
public sealed record ZuneStageVoice(float TitleSize, float TitleLine, ushort TitleWeight, float TitleTracking, float MetaSize, float MetaLine, ushort MetaWeight,
    float EyebrowSize, float EyebrowLine, float SourceSize, float SourceLine, float SeekH, float RowH, float RowGap, float Disc, float Box, float Glyph,
    float PlayGlyph, float VolumeW, float AsideH);

/// <summary>The Card arrangement in one tier: the card, its cover, the aside at the bottom-right gutter, and (Huge, Huge 4K) the display title
/// over the face.</summary>
public sealed record ZuneStageCard(float MaxW, float MinW, float Pad, float Art, float ArtGap, float SeekTop, float AsideW, float FaceRoomMin,
    float DisplaySize, float DisplayLine, float DisplayMetaSize, float DisplayMetaLine, float DisplayInset, ZuneStageVoice Voice)
{
    /// <summary>The card's height: its padding, the cover, the seek row and the transport row.</summary>
    public float H => Pad + Art + SeekTop + Voice.SeekH + Voice.RowH + Pad;

    /// <summary>The display block's height (the title and the artist line); 0: none.</summary>
    public float DisplayH => DisplaySize > 0f ? DisplayLine + DisplayMetaLine : 0f;
}

/// <summary>The Large art column in one tier.</summary>
public sealed record ZuneStageColumn(float Top, float Gap, float MinW, float CoverMin, float ControlsMaxW, float TitlesH, float TitleMax, float TitleMin,
    float SeekTop, float VolumeTop, bool VolumeRow, float AsideW, float PaneTop, float PaneGap, float PaneMinH, ZuneStageVoice Voice)
{
    /// <summary>The controls: the seek row, the transport row and (where the column carries it) the volume row.</summary>
    public float ControlsH => SeekTop + Voice.SeekH + Voice.RowH + (VolumeRow ? VolumeTop + Voice.AsideH : 0f);

    /// <summary>The foot block (the titles and the controls).</summary>
    public float BlockH => TitlesH + ControlsH;

    /// <summary>Under the block: the aside (mute · volume · device) where the column has no volume row.</summary>
    public float FootH => VolumeRow ? 0f : VolumeTop + Voice.AsideH;
}

/// <summary>The Centered arrangement in one tier (Z-NowPlaying-Centered): the cover, the titles and the controls centred, the gradient foot.</summary>
public sealed record ZuneStageCentered(float CoverMax, float CoverMin, float CoverFrac, float CoverWFrac, float TitleTop, float MetaGap, float SeekTop,
    float SeekMaxW, float FootH, float FootSideW, float CaptionGap, ZuneStageVoice Voice);

/// <summary>The Compact Visualizer stack (Z-NowPlaying-Compact): the cover, the titles and "playing from …" from the head down, the controls
/// (the seek row, the transport row, the heart · device · face · layout row) at the foot.</summary>
public sealed record ZuneStageStack(float Top, float CoverFrac, float CoverMin, float TitleTop, float SeekTop, float BottomPad, ZuneStageVoice Voice)
{
    public float ControlsH => Voice.SeekH + Voice.RowH + Voice.AsideH;
}
