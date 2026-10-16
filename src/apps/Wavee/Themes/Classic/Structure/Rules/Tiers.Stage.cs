// ── Themes/Classic/Structure/Rules/Tiers.Stage.cs — the stage family's tiers: the stage, lyrics, video and queue (themes plan 3.2, 3.6; TC3) ──
//
// THE STAGE'S CLASS LADDERS (plan 3.1: `Stage.cs` `Layout.ClassOf`, moved out of the allocator as engine `TierLadder`s). The fullscreen
// stage classifies its own (W, H) into Desktop · Ultrawide · Portrait · Compact; the core allocator composes the three ladders (in this
// order: a tall window is Portrait at every width, a narrow one Portrait until it is landscape and Compact from there, a short landscape
// one Compact, the ratio splits the rest):
//
//   width    narrow under 600 wide (Compact when landscape, Portrait when not). No hold.
//   height   a landscape stage is Compact at or below 460 tall and leaves at 484 (the previous stage's 24 fold hold).
//   ratio    Portrait at or below 0.80 (leaves at 0.85), Ultrawide from 2.00 (leaves at 1.95 or below).
//
//   The lower bands are INCLUSIVE (at or below 460, at or below 0.80) and Ultrawide's hold is exclusive (it holds while the ratio stays
//   above 1.95), while a ladder edge reads "at or above At". `AspectThresholds.AtOrBelow` / `.From` state those edges one float above
//   the board value, so every edge is the primitive's and the classes are exactly the ones the stage always had, but for one change the
//   boards make: a narrow window that is not landscape (400 x 860, C-Stage-Compact / C-Lyrics-Compact) is Portrait, a column with its
//   pane, where it was the landscape-short Compact class.
//
// THE STAGE'S TIER METRICS (the stage fills the window, so its tier is the window's; `StagePolicy.MetricsFor`):
//   Compact   the 104 card at a 16 gutter (C-Stage-Compact), lyrics 26/33 (C-Lyrics-Compact)
//   Small     the 96 card at a 16 gutter (C-Stage-Small, C-Lyrics-Small), lyrics 30-36
//   Standard  the tuned stage: the 112 card at 24, lyrics 36 grown up to 64
//   Huge      the 112 card at 24, lyrics capped at 48 (C-Lyrics-Huge, C-Stage-Huge4K: 48/60)
// and the view's record (`ClassicStageLayout`): the play disc, the volume slider, the Compact 40 targets of the top bar.
//
// THE PANEL VIEWS' CONTENTS (`ClassicPanelContentLayout`): the queue, lyrics and video views lay out at whatever width the panel frame
// gives them (200-500, TC2's) because every row is fluid; what changes per WINDOW tier is the hit target: a Compact window shows the
// panel as a sheet or an overlay, so its targets are at least 40 x 40 (the brief's Compact rule).
//
// Public, so `Wavee.Tests` pins the ladders and the records. Literal numbers are allowed here (WVTH003 / WVTH013 exempt `Rules\**`).

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Classic's stage family tiers: the stage's class ladders and metrics, and the records the stage and panel views read.</summary>
public static class ClassicStageTiers
{
    // ── the class ladders ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A stage under this width is Compact (unless it is tall enough to be Portrait).</summary>
    public const float WideFrom = 600f;

    /// <summary>A landscape stage at or below this height is Compact, and leaves Compact at <see cref="CompactLeaveH"/>.</summary>
    public const float CompactAtOrBelowH = 460f, CompactLeaveH = 484f;

    /// <summary>Portrait at or below this width / height ratio, left at <see cref="PortraitLeave"/>.</summary>
    public const float PortraitAtOrBelow = 0.80f, PortraitLeave = 0.85f;

    /// <summary>Ultrawide from this ratio, left at <see cref="UltrawideLeaveAtOrBelow"/> or below.</summary>
    public const float UltrawideFrom = 2.00f, UltrawideLeaveAtOrBelow = 1.95f;

    /// <summary>The width ladder: one edge, no hold.</summary>
    public static TierLadder Width { get; } = TierLadder.Of(new TierEdge(WideFrom));

    /// <summary>The height ladder: short at or below 460, held to 484 on the way up.</summary>
    public static TierLadder Height { get; } = TierLadder.Of(AspectThresholds.AtOrBelow(CompactAtOrBelowH, leaveAt: CompactLeaveH));

    /// <summary>The ratio ladder: Portrait | Desktop | Ultrawide, each edge with its hold.</summary>
    public static TierLadder Ratio { get; } = TierLadder.Of(
        AspectThresholds.AtOrBelow(PortraitAtOrBelow, leaveAt: PortraitLeave),
        AspectThresholds.From(UltrawideFrom, leaveAtOrBelow: UltrawideLeaveAtOrBelow));

    /// <summary>The three ladders as the stage policy's class edges. Declared after them: initializers run in textual order.</summary>
    public static AspectThresholds Aspect { get; } = new(Width, Height, Ratio);

    // ── the tier metrics the core allocator budgets from ─────────────────────────────────────────────────────────────────

    /// <summary>Compact (C-Stage-Compact, C-Lyrics-Compact): the 104 stacked card at a 16 gutter, the lyrics at 26.</summary>
    public static StageTierMetrics CompactMetrics { get; } = new(TransportH: 104f, TransportInset: 16f, LyricsTypeMin: 26f, LyricsTypeMax: 26f);

    /// <summary>Small (C-Stage-Small, C-Lyrics-Small): the 96 card at a 16 gutter, the lyrics at 30, up to the authored 36.</summary>
    public static StageTierMetrics SmallMetrics { get; } = new(TransportH: 96f, TransportInset: 16f, LyricsTypeMin: 30f, LyricsTypeMax: 36f);

    /// <summary>Standard: the tuned stage (the 112 card at 24, the authored 36 grown up to 64).</summary>
    public static StageTierMetrics StandardMetrics { get; } = new(TransportH: 112f, TransportInset: 24f, LyricsTypeMin: 36f, LyricsTypeMax: 64f);

    /// <summary>Huge and Huge 4K (C-Lyrics-Huge, C-Stage-Huge4K): Standard's card, the lyrics capped at the boards' 48.</summary>
    public static StageTierMetrics HugeMetrics { get; } = StandardMetrics with { LyricsTypeMax = 48f };

    /// <summary>Classic's stage policy. One instance, so a republish is not a change.</summary>
    public static StagePolicy Policy { get; } = new(Aspect)
    {
        Metrics = StandardMetrics,
        Compact = CompactMetrics,
        Small = SmallMetrics,
        Huge = HugeMetrics,
    };

    // ── the stage views' record (per window tier) ────────────────────────────────────────────────────────────────────────

    private static readonly ClassicStageLayout[] s_stage =
    [
        // Compact (C-Stage-Compact): 40 x 40 targets in the top bar (the board draws 40 x 36 mode tabs; the Compact rule wins), the 56 disc.
        new(WidthTier.Compact, BarTargetH: 40f, PlayDiscMax: 56f, VolumeW: 128f),
        // Small (C-Stage-Small, C-Lyrics-Small): the 48 disc and the 88 volume slider of the 96 card.
        new(WidthTier.Small, BarTargetH: 36f, PlayDiscMax: 48f, VolumeW: 88f),
        // Standard (the tuned stage) and Huge (C-Stage-Huge, C-Stage-Huge4K: the same 56 disc and 128 slider).
        new(WidthTier.Standard, BarTargetH: 36f, PlayDiscMax: 56f, VolumeW: 128f),
        new(WidthTier.Huge, BarTargetH: 36f, PlayDiscMax: 56f, VolumeW: 128f),
    ];

    /// <summary>The stage views' record for window tier <paramref name="window"/> (Huge 4K reads Huge's).</summary>
    public static ClassicStageLayout Stage(WidthTier window) => s_stage[Math.Min((int)window, (int)WidthTier.Huge)];

    /// <summary>The stage views' record for <paramref name="facts"/>; Standard's where no frame resolved the tiers.</summary>
    public static ClassicStageLayout StageOf(in TierFacts facts) => Stage(facts.Policy is null ? WidthTier.Standard : facts.Window);

    /// <summary>The stacked transport card's padding (C-Stage-Compact: 12 · 12 · 12 · 8).</summary>
    public static readonly Edges4 StackedTransportPadding = new(12f, 12f, 12f, 8f);

    // ── the panel views' contents (per window tier) ──────────────────────────────────────────────────────────────────────

    private static readonly ClassicPanelContentLayout[] s_panel =
    [
        // Compact: the panel is a sheet or an overlay; every target of its content is at least 40 x 40.
        new(WidthTier.Compact, Target: 40f, HeartBox: 40f, CloseBox: 40f, MediaGlyphBox: 40f, MediaStripH: 40f, MediaChromeAtRest: true),
        // Small up: the boards' desktop targets (the 26 heart lane, the 32 close, the hover-revealed 24 media glyphs on their 30 strip).
        new(WidthTier.Small, Target: 0f, HeartBox: 26f, CloseBox: 32f, MediaGlyphBox: 24f, MediaStripH: 30f, MediaChromeAtRest: false),
        new(WidthTier.Standard, Target: 0f, HeartBox: 26f, CloseBox: 32f, MediaGlyphBox: 24f, MediaStripH: 30f, MediaChromeAtRest: false),
        new(WidthTier.Huge, Target: 0f, HeartBox: 26f, CloseBox: 32f, MediaGlyphBox: 24f, MediaStripH: 30f, MediaChromeAtRest: false),
    ];

    /// <summary>The panel views' content record for window tier <paramref name="window"/> (Huge 4K reads Huge's).</summary>
    public static ClassicPanelContentLayout Panel(WidthTier window) => s_panel[Math.Min((int)window, (int)WidthTier.Huge)];

    /// <summary>The panel views' content record for <paramref name="facts"/>; Standard's where no frame resolved the tiers.</summary>
    public static ClassicPanelContentLayout PanelOf(in TierFacts facts) => Panel(facts.Policy is null ? WidthTier.Standard : facts.Window);
}

/// <summary>What Classic's stage views draw in one window tier (the allocator's geometry is <c>Stage.Layout</c>'s).</summary>
/// <param name="Tier">The window tier.</param>
/// <param name="BarTargetH">The top bar's glyph buttons' and mode tabs' height (40 at Compact: the Compact target rule).</param>
/// <param name="PlayDiscMax">The play disc's largest size (the Compact class's 96 card caps it at 52 below this).</param>
/// <param name="VolumeW">The transport's volume slider.</param>
public sealed record ClassicStageLayout(WidthTier Tier, float BarTargetH, float PlayDiscMax, float VolumeW);

/// <summary>What Classic's queue, lyrics and video views lay out inside the panel in one window tier.</summary>
/// <param name="Tier">The window tier.</param>
/// <param name="Target">The least hit target of the content (0: the desktop sizes stand).</param>
/// <param name="HeartBox">The queue rows' heart button.</param>
/// <param name="CloseBox">The queue rows' remove button.</param>
/// <param name="MediaGlyphBox">The video surfaces' chrome glyph buttons (the docked strip, the mini player).</param>
/// <param name="MediaStripH">The video surfaces' chrome strip.</param>
/// <param name="MediaChromeAtRest">The video chrome shows at rest (no hover on a narrow, touch-sized window: C-Video-Compact).</param>
public sealed record ClassicPanelContentLayout(WidthTier Tier, float Target, float HeartBox, float CloseBox, float MediaGlyphBox, float MediaStripH, bool MediaChromeAtRest)
{
    /// <summary><paramref name="desktop"/> raised to the tier's least target.</summary>
    public float AtLeast(float desktop) => MathF.Max(desktop, Target);

    /// <summary>The least size a target box takes in this tier: the tier's target, or none (auto) where the desktop sizes stand.</summary>
    public float MinTarget => Target > 0f ? Target : float.NaN;
}
