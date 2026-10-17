// ── Themes/Zune/Structure/Rules/Tiers.cs — Zune's window tiers and the frame's tier records (themes plan 3.2, 6.3; P5 Z0) ─────────
//
// THE LADDERS (board `Z-Sizes-Breakpoints`, the tier source of truth: "widening commits at the line, narrowing holds 24 dip below it"):
//
//   window   Compact 300-639 · Small 640-1007 · Standard 1008-1919 · Huge 1920-3199 · Huge 4K from 3200. Every edge commits at the
//            line on widening and holds 24 on narrowing, so a narrowing window changes tier only below 616 / 984 / 1896 / 3176. A fresh
//            resolve (a launch, a navigation, a theme switch) uses the line itself. Zune DRAWS the Huge 4K band (gutter 64, title bar 56,
//            play bar 88), so its fifth edge is present, unlike Classic's.
//   content  the same edges over the content width (the window less the docked panels: Zune has no nav pane).
//   height   Short below 700 tall, Regular from 700, held 24 on shrinking (the board's "short height < 700"; the hold of 24 is the
//            lane's assumption, plan "Zune specifics").
//   rungs    `pane.second` at 1920 and `pane.third` at 3200, on the window axis: the docked panel's second and third pane
//            (queue | lyrics · friends at Huge, queue | lyrics | friends at Huge 4K). `ZunePolicies` caps them by `maxDockedPanes`.
//
// THE RECORDS. A view reads `ctx.Tier()` and one of the records below; it never compares a width (WVTH013).
//   · `ZuneFrameLayout` per WINDOW tier: the gutter every band inset and page title share (16 / 24 / 48 / 48 / 64), the title bar
//     (48, 56 at Huge 4K), the band (row 1 + row 2: 92 at Compact where row 2 is the 40-tall touch row, 84 elsewhere; 52 collapsed)
//     and the play bar (mini 64 at Compact, 72 on the desktop tiers, 88 at Huge 4K).
//
// FAMILY HOOKS. A later lane adds its finer ladders and rungs in its own `Rules\Tiers.<Family>.cs`, implementing the partial methods
// below (`LaddersPlayer`, `RungsDetail`, ...). They run inside `Policy`'s initializer, so an implementation must not read a static of
// this class declared in another file (the textual order across partial files is undefined): keep its numbers in its own class.
//
// Public, so `Wavee.Tests` pins the ladders and the records (the assembly has no InternalsVisibleTo).

using System.Collections.Immutable;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

/// <summary>Zune's tier policy and the frame's tier records.</summary>
public static partial class ZuneTiers
{
    // ── the window ladder (Z-Sizes-Breakpoints) ──────────────────────────────────────────────────────────────────────────

    /// <summary>Small begins here.</summary>
    public const float SmallFrom = 640f;

    /// <summary>Standard begins here (the panel docks inline from this width).</summary>
    public const float StandardFrom = 1008f;

    /// <summary>Huge begins here (the second docked pane, <see cref="PaneSecondRung"/>).</summary>
    public const float HugeFrom = 1920f;

    /// <summary>Huge 4K begins here (the third docked pane, <see cref="PaneThirdRung"/>).</summary>
    public const float Huge4KFrom = 3200f;

    /// <summary>Every edge's hold: none on widening, 24 on narrowing (the board's player-bar hysteresis, used for every tier).</summary>
    public const float Hold = 24f;

    /// <summary>The window ladder: 640, 1008, 1920 and 3200, each committing at the line on widening and held 24 on narrowing.</summary>
    public static TierLadder Window { get; } = WidthLadder();

    /// <summary>The content ladder: the same edges and holds over the content width.</summary>
    public static TierLadder Content { get; } = WidthLadder();

    private static TierLadder WidthLadder() => TierLadder.Of(
        new TierEdge(SmallFrom, NarrowHold: Hold, WidenHold: 0f),
        new TierEdge(StandardFrom, NarrowHold: Hold, WidenHold: 0f),
        new TierEdge(HugeFrom, NarrowHold: Hold, WidenHold: 0f),
        new TierEdge(Huge4KFrom, NarrowHold: Hold, WidenHold: 0f));

    // ── the height ladder ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The window is Short below this height (Z-Sizes-Breakpoints: "short height &lt; 700").</summary>
    public const float ShortBelow = 700f;

    /// <summary>The height ladder: Short below 700, Regular from 700, held 24 on shrinking.</summary>
    public static TierLadder Height { get; } = TierLadder.Of(new TierEdge(ShortBelow, NarrowHold: Hold, WidenHold: 0f));

    /// <summary>The page height Zune's Short layout guarantees at the 330-DIP window minimum: 330 less the 48 title bar, the 84 band
    /// and the 72 play bar.</summary>
    public const float ShortFloorH = 126f;

    // ── the rungs ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The docked panel's second pane (queue | lyrics · friends, 720 in all) from the Huge edge.</summary>
    public const string PaneSecondRung = "pane.second";

    /// <summary>The docked panel's third pane (queue | lyrics | friends, 1080 in all) from the Huge 4K edge.</summary>
    public const string PaneThirdRung = "pane.third";

    /// <summary>Zune's policy. One instance, so the facts a structure republishes compare equal. Declared after the ladders it names:
    /// property initializers run in textual order.</summary>
    public static TierPolicy Policy { get; } = new TierPolicy(
        Window, Content, Height,
        CompactFloor: TierPolicy.WindowFloor,
        ShortFloor: ShortFloorH,
        Rungs: BuildRungs(),
        Ladders: BuildLadders());

    private static ImmutableArray<NamedRung> BuildRungs()
    {
        var rungs = ImmutableArray.CreateBuilder<NamedRung>();
        rungs.Add(new NamedRung(PaneSecondRung, new TierEdge(HugeFrom, NarrowHold: Hold, WidenHold: 0f), RungAxis.Window));
        rungs.Add(new NamedRung(PaneThirdRung, new TierEdge(Huge4KFrom, NarrowHold: Hold, WidenHold: 0f), RungAxis.Window));
        RungsFrame(rungs);
        RungsBand(rungs);
        RungsLists(rungs);
        RungsPlayer(rungs);
        RungsStage(rungs);
        RungsDiscovery(rungs);
        RungsDetail(rungs);
        RungsArtist(rungs);
        RungsCollection(rungs);
        RungsOverlays(rungs);
        RungsScreens(rungs);
        return rungs.ToImmutable();
    }

    private static ImmutableArray<NamedLadder> BuildLadders()
    {
        var ladders = ImmutableArray.CreateBuilder<NamedLadder>();
        LaddersFrame(ladders);
        LaddersBand(ladders);
        LaddersLists(ladders);
        LaddersPlayer(ladders);
        LaddersStage(ladders);
        LaddersDiscovery(ladders);
        LaddersDetail(ladders);
        LaddersArtist(ladders);
        LaddersCollection(ladders);
        LaddersOverlays(ladders);
        LaddersScreens(ladders);
        return ladders.ToImmutable();
    }

    // The family hooks: each later lane implements its own in `Rules\Tiers.<Family>.cs` (an unimplemented one compiles away).
    static partial void RungsFrame(ImmutableArray<NamedRung>.Builder rungs);
    static partial void RungsBand(ImmutableArray<NamedRung>.Builder rungs);
    static partial void RungsLists(ImmutableArray<NamedRung>.Builder rungs);
    static partial void RungsPlayer(ImmutableArray<NamedRung>.Builder rungs);
    static partial void RungsStage(ImmutableArray<NamedRung>.Builder rungs);
    static partial void RungsDiscovery(ImmutableArray<NamedRung>.Builder rungs);
    static partial void RungsDetail(ImmutableArray<NamedRung>.Builder rungs);
    static partial void RungsArtist(ImmutableArray<NamedRung>.Builder rungs);
    static partial void RungsCollection(ImmutableArray<NamedRung>.Builder rungs);
    static partial void RungsOverlays(ImmutableArray<NamedRung>.Builder rungs);
    static partial void RungsScreens(ImmutableArray<NamedRung>.Builder rungs);

    static partial void LaddersFrame(ImmutableArray<NamedLadder>.Builder ladders);
    static partial void LaddersBand(ImmutableArray<NamedLadder>.Builder ladders);
    static partial void LaddersLists(ImmutableArray<NamedLadder>.Builder ladders);
    static partial void LaddersPlayer(ImmutableArray<NamedLadder>.Builder ladders);
    static partial void LaddersStage(ImmutableArray<NamedLadder>.Builder ladders);
    static partial void LaddersDiscovery(ImmutableArray<NamedLadder>.Builder ladders);
    static partial void LaddersDetail(ImmutableArray<NamedLadder>.Builder ladders);
    static partial void LaddersArtist(ImmutableArray<NamedLadder>.Builder ladders);
    static partial void LaddersCollection(ImmutableArray<NamedLadder>.Builder ladders);
    static partial void LaddersOverlays(ImmutableArray<NamedLadder>.Builder ladders);
    static partial void LaddersScreens(ImmutableArray<NamedLadder>.Builder ladders);

    // ── the frame's records (per window tier) ────────────────────────────────────────────────────────────────────────────

    /// <summary>The band's row 1 (the pivots and pins): what a collapsed band keeps.</summary>
    public const float PivotRowH = 52f;

    /// <summary>The page reserves the desktop play bar's height at every tier, so the mini 64 ↔ 72 cross-fade moves nothing below it.</summary>
    public const float BarReserveH = 72f;

    private static readonly ZuneFrameLayout[] s_frames =
    [
        // Compact (Z-Frame-Compact): gutter 16, the 48 title bar with 40 icons, the band's row 2 is the 40-tall touch row, the mini bar 64.
        new(WidthTier.Compact, Gutter: 16f, TitleBarH: 48f, BandH: PivotRowH + 40f, BandCollapsedH: PivotRowH, BarH: 64f,
            ImmutableArray<TierRegion>.Empty),
        // Small (Z-Frame-Small): gutter 24, row 2 is 32 under a fade, the bar 72.
        new(WidthTier.Small, Gutter: 24f, TitleBarH: 48f, BandH: PivotRowH + 32f, BandCollapsedH: PivotRowH, BarH: 72f,
            ImmutableArray<TierRegion>.Empty),
        // Standard (Z-Frame-Home): gutter 48, band 84, bar 72.
        new(WidthTier.Standard, Gutter: 48f, TitleBarH: 48f, BandH: PivotRowH + 32f, BandCollapsedH: PivotRowH, BarH: 72f,
            ImmutableArray<TierRegion>.Empty),
        // Huge (Z-Frame-Huge): gutter 48, band 84 (never collapses: there is room), bar 72.
        new(WidthTier.Huge, Gutter: 48f, TitleBarH: 48f, BandH: PivotRowH + 32f, BandCollapsedH: PivotRowH, BarH: 72f,
            ImmutableArray<TierRegion>.Empty),
        // Huge 4K (Z-Frame-Huge4K): gutter 64, the 56 title bar, band 84, bar 88.
        new(WidthTier.Huge4K, Gutter: 64f, TitleBarH: 56f, BandH: PivotRowH + 32f, BandCollapsedH: PivotRowH, BarH: 88f,
            ImmutableArray<TierRegion>.Empty),
    ];

    /// <summary>The frame's record for window tier <paramref name="window"/>.</summary>
    public static ZuneFrameLayout Frame(WidthTier window) => s_frames[Math.Clamp((int)window, 0, s_frames.Length - 1)];

    /// <summary>The frame's record for <paramref name="facts"/>; Standard's where no frame resolved the tiers (a view mounted alone).</summary>
    public static ZuneFrameLayout FrameOf(in TierFacts facts) => Frame(facts.Policy is null ? WidthTier.Standard : facts.Window);
}

/// <summary>What Zune's frame lays out in one window tier.</summary>
/// <param name="Tier">The window tier.</param>
/// <param name="Gutter">The gutter the band's inset and every page title share: 16 / 24 / 48 / 48 / 64.</param>
/// <param name="TitleBarH">The title bar's height: 48, 56 at Huge 4K.</param>
/// <param name="BandH">The band's height with both rows: 92 at Compact (row 2 is the touch row), 84 elsewhere.</param>
/// <param name="BandCollapsedH">The band's height collapsed to row 1 on scroll: 52.</param>
/// <param name="BarH">The play bar's height: mini 64 at Compact, 72 on the desktop tiers, 88 at Huge 4K.</param>
/// <param name="Regions">The regions a resized frame may clip, each with its floor (the frame lane, Z2a, declares them).</param>
public sealed record ZuneFrameLayout(WidthTier Tier, float Gutter, float TitleBarH, float BandH, float BandCollapsedH, float BarH,
    ImmutableArray<TierRegion> Regions) : ITierLayoutRecord
{
    /// <summary>The node keys of the frame's three top-level regions (the tier harness finds them): the title bar, the content column
    /// (the band and the page) and the play bar. The same keys as Classic's frame, so the harness reads the stand-in frame and Zune's.</summary>
    public const string TitleBarKey = "shell.titlebar", ContentKey = "shell.content.card", PlayerBarKey = "shell.playerbar";
}
