// ── Themes/WinUi/Structure/Rules/Tiers.cs — WinUI's window tiers and the frame's tier records (themes plan 3.2, 6.2; P6 W0) ──────────
//
// THE LADDERS (board `W-Tiers`, the tier source of truth: "When the window gets narrower, each threshold waits 24 px longer before switching"):
//
//   window   Compact < 641 · Small 641-1007 · Standard 1008-1919 · Huge >= 1920: NavigationView's own thresholds
//            (CompactModeThresholdWidth 641, ExpandedModeThresholdWidth 1008) plus 1920. Every edge commits at the line on widening and
//            holds 24 on narrowing, so a narrowing window changes tier only below 617 / 984 / 1896. A fresh resolve (a launch, a navigation,
//            a theme switch) uses the line itself. NO Huge 4K edge (P6 decision D3, the board's "3840: Huge rules, more columns"): the
//            Huge records cover every width from 1920 up and the window tier never reads Huge4K.
//   content  the same three edges over the content width (the board: "in-page thresholds go by content width, not window width"). A
//            family with its own in-page breakpoints (560 / 860 / 900 / 1100 ...) adds a named ladder in its `Tiers.<Family>.cs`.
//   height   Short below 720 tall, Regular from 720, held 24 on shrinking (the board: "Below 720 window height the short hero is used").
//
// THE RECORD. A view reads `ctx.Tier()` and one of the records below; it never compares a width (WVTH013).
//   · `WinUiFrameLayout` per WINDOW tier: the content inset (16 / 24 / 36 / 48 left and right, 16 / 20 / 24 / 32 top), the title bar's
//     two rows (the 40 tab strip over the 48 toolbar; one 48 bar with tabs off), the play bar (Mini 64 at Compact, 72 elsewhere) and the
//     regions a resized frame may clip.
//
// FAMILY HOOKS. A later lane adds its finer ladders and rungs in its own `Rules\Tiers.<Family>.cs`, implementing the partial methods
// below (`LaddersPlayer`, `RungsDetail`, ...). They run inside `Policy`'s initializer, so an implementation must not read a static of
// this class declared in another file (the textual order across partial files is undefined): keep its numbers in its own class.
//
// Literal numbers are allowed in this file (WVTH003 exempts `Rules\**`). Public, so `Wavee.Tests` pins the ladders and the records.

using System.Collections.Immutable;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's tier policy and the frame's tier records.</summary>
public static partial class WinUiTiers
{
    // ── the window ladder (W-Tiers) ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Small begins here (NavigationView's CompactModeThresholdWidth: the 48 rail).</summary>
    public const float SmallFrom = 641f;

    /// <summary>Standard begins here (NavigationView's ExpandedModeThresholdWidth: the docked 280 pane).</summary>
    public const float StandardFrom = 1008f;

    /// <summary>Huge begins here. There is no Huge 4K edge: 3840 uses the Huge rules with more columns.</summary>
    public const float HugeFrom = 1920f;

    /// <summary>Every edge's hold: none on widening, 24 on narrowing.</summary>
    public const float Hold = 24f;

    /// <summary>The window ladder: 641, 1008 and 1920, each committing at the line on widening and held 24 on narrowing.</summary>
    public static TierLadder Window { get; } = WidthLadder();

    /// <summary>The content ladder: the same edges and holds over the content width.</summary>
    public static TierLadder Content { get; } = WidthLadder();

    private static TierLadder WidthLadder() => TierLadder.Of(
        new TierEdge(SmallFrom, NarrowHold: Hold, WidenHold: 0f),
        new TierEdge(StandardFrom, NarrowHold: Hold, WidenHold: 0f),
        new TierEdge(HugeFrom, NarrowHold: Hold, WidenHold: 0f));

    // ── the height ladder ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The window is Short below this height (W-Tiers: the short hero and the 48 sticky bar below 720).</summary>
    public const float ShortBelow = 720f;

    /// <summary>The height ladder: Short below 720, Regular from 720, held 24 on shrinking.</summary>
    public static TierLadder Height { get; } = TierLadder.Of(new TierEdge(ShortBelow, NarrowHold: Hold, WidenHold: 0f));

    /// <summary>The page height WinUI guarantees at the 330-DIP window minimum: 330 less the two title rows (40 + 48) and the 72 play bar.</summary>
    public const float ShortFloorH = 170f;

    /// <summary>WinUI's policy. One instance, so the facts a structure republishes compare equal. Declared after the ladders it names:
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
        RungsFrame(rungs);
        RungsNav(rungs);
        RungsLists(rungs);
        RungsPlayer(rungs);
        RungsStage(rungs);
        RungsDiscovery(rungs);
        RungsDetail(rungs);
        RungsEntities(rungs);
        RungsCollection(rungs);
        RungsOverlays(rungs);
        RungsScreens(rungs);
        return rungs.ToImmutable();
    }

    private static ImmutableArray<NamedLadder> BuildLadders()
    {
        var ladders = ImmutableArray.CreateBuilder<NamedLadder>();
        LaddersFrame(ladders);
        LaddersNav(ladders);
        LaddersLists(ladders);
        LaddersPlayer(ladders);
        LaddersStage(ladders);
        LaddersDiscovery(ladders);
        LaddersDetail(ladders);
        LaddersEntities(ladders);
        LaddersCollection(ladders);
        LaddersOverlays(ladders);
        LaddersScreens(ladders);
        return ladders.ToImmutable();
    }

    // The family hooks: each later lane implements its own in `Rules\Tiers.<Family>.cs` (an unimplemented one compiles away).
    static partial void RungsFrame(ImmutableArray<NamedRung>.Builder rungs);
    static partial void RungsNav(ImmutableArray<NamedRung>.Builder rungs);
    static partial void RungsLists(ImmutableArray<NamedRung>.Builder rungs);
    static partial void RungsPlayer(ImmutableArray<NamedRung>.Builder rungs);
    static partial void RungsStage(ImmutableArray<NamedRung>.Builder rungs);
    static partial void RungsDiscovery(ImmutableArray<NamedRung>.Builder rungs);
    static partial void RungsDetail(ImmutableArray<NamedRung>.Builder rungs);
    static partial void RungsEntities(ImmutableArray<NamedRung>.Builder rungs);
    static partial void RungsCollection(ImmutableArray<NamedRung>.Builder rungs);
    static partial void RungsOverlays(ImmutableArray<NamedRung>.Builder rungs);
    static partial void RungsScreens(ImmutableArray<NamedRung>.Builder rungs);

    static partial void LaddersFrame(ImmutableArray<NamedLadder>.Builder ladders);
    static partial void LaddersNav(ImmutableArray<NamedLadder>.Builder ladders);
    static partial void LaddersLists(ImmutableArray<NamedLadder>.Builder ladders);
    static partial void LaddersPlayer(ImmutableArray<NamedLadder>.Builder ladders);
    static partial void LaddersStage(ImmutableArray<NamedLadder>.Builder ladders);
    static partial void LaddersDiscovery(ImmutableArray<NamedLadder>.Builder ladders);
    static partial void LaddersDetail(ImmutableArray<NamedLadder>.Builder ladders);
    static partial void LaddersEntities(ImmutableArray<NamedLadder>.Builder ladders);
    static partial void LaddersCollection(ImmutableArray<NamedLadder>.Builder ladders);
    static partial void LaddersOverlays(ImmutableArray<NamedLadder>.Builder ladders);
    static partial void LaddersScreens(ImmutableArray<NamedLadder>.Builder ladders);

    // ── the frame's records (per window tier) ────────────────────────────────────────────────────────────────────────────

    /// <summary>The tab strip's row (TabView on Mica Alt, the caption buttons at its end).</summary>
    public const float TabStripH = 40f;

    /// <summary>The toolbar row under the strip (hamburger, Back, Forward, search, bell, avatar); the whole bar with tabs off.</summary>
    public const float ToolbarH = 48f;

    /// <summary>The play bar's height at every tier but Compact (reserved: the bar's tiers never resize it).</summary>
    public const float BarH = 72f;

    /// <summary>The Mini play bar's height at Compact (with its 2-px progress edge).</summary>
    public const float MiniBarH = 64f;

    /// <summary>The nav pane's docked width and its rail (NavigationView: open 280, compact 48).</summary>
    public const float PaneW = 280f, RailW = 48f;

    /// <summary>The content floor a docked side pane leaves (the board's one rule: nav + 340 + 480 must fit to dock).</summary>
    public const float ContentFloorW = 480f;

    /// <summary>The region ids of the frame's clip contract.</summary>
    public const string NavRegionId = "nav.pane", ContentRegionId = "content.card";

    private static readonly WinUiFrameLayout[] s_frames =
    [
        // Compact (W-Shell-Compact): LeftMinimal, no rail; the content card fills the window; 16 inset; the Mini bar 64.
        new(WidthTier.Compact, InsetX: 16f, InsetTop: 16f, BarH: MiniBarH,
            ImmutableArray.Create(new TierRegion(ContentRegionId, TierPolicy.WindowFloor))),
        // Small (W-Shell-Small): LeftCompact, the 48 rail beside the card; 24 inset, 20 top; the bar 72.
        new(WidthTier.Small, InsetX: 24f, InsetTop: 20f, BarH: BarH,
            ImmutableArray.Create(new TierRegion(NavRegionId, RailW), new TierRegion(ContentRegionId, SmallFrom - RailW))),
        // Standard (the Std boards): Left, the 280 pane docked (the user may collapse it to the rail); 36 inset, 24 top.
        new(WidthTier.Standard, InsetX: 36f, InsetTop: 24f, BarH: BarH,
            ImmutableArray.Create(new TierRegion(NavRegionId, RailW), new TierRegion(ContentRegionId, ContentFloorW))),
        // Huge (W-Shell-Huge) and every width from 1920 up, 3840 included: 48 inset, 32 top.
        new(WidthTier.Huge, InsetX: 48f, InsetTop: 32f, BarH: BarH,
            ImmutableArray.Create(new TierRegion(NavRegionId, RailW), new TierRegion(ContentRegionId, ContentFloorW))),
    ];

    /// <summary>The frame's record for window tier <paramref name="window"/> (Huge's from 1920 up: WinUI has no Huge 4K record).</summary>
    public static WinUiFrameLayout Frame(WidthTier window) => s_frames[Math.Clamp((int)window, 0, s_frames.Length - 1)];

    /// <summary>The frame's record for <paramref name="facts"/>; Standard's where no frame resolved the tiers (a view mounted alone).</summary>
    public static WinUiFrameLayout FrameOf(in TierFacts facts) => Frame(facts.Policy is null ? WidthTier.Standard : facts.Window);

    /// <summary>The content inset, left and right, for <paramref name="facts"/>: 16 / 24 / 36 / 48 by window tier.</summary>
    public static float InsetOf(in TierFacts facts) => FrameOf(in facts).InsetX;
}

/// <summary>What WinUI's frame lays out in one window tier.</summary>
/// <param name="Tier">The window tier.</param>
/// <param name="InsetX">The content inset left and right: 16 / 24 / 36 / 48.</param>
/// <param name="InsetTop">The content inset at the top: 16 / 20 / 24 / 32.</param>
/// <param name="BarH">The play bar's height: Mini 64 at Compact, 72 elsewhere.</param>
/// <param name="Regions">The regions a resized frame may clip, each with its floor: the content card, and from Small the nav pane's rail.</param>
public sealed record WinUiFrameLayout(WidthTier Tier, float InsetX, float InsetTop, float BarH, ImmutableArray<TierRegion> Regions) : ITierLayoutRecord
{
    /// <summary>The node keys of the frame's three top-level regions (the tier harness finds them): the title bar, the content column and the
    /// play bar. The same keys as Classic's and Zune's frames, so the harness reads the stand-in frame and WinUI's.</summary>
    public const string TitleBarKey = "shell.titlebar", ContentKey = "shell.content.card", PlayerBarKey = "shell.playerbar";

    /// <summary>The node key of a page's title (W2a's page head, W5's greeting): the lint's inset edge resolves it.</summary>
    public const string PageTitleKey = "winui.page.title";

    /// <summary>The tab strip's row: 40 (W-Tiers: "tabs stay on in every tier").</summary>
    public float TabStripH => WinUiTiers.TabStripH;

    /// <summary>The toolbar row: 48.</summary>
    public float ToolbarH => WinUiTiers.ToolbarH;

    /// <summary>The title bar's height: the strip over the toolbar (88) with tabs on, the one 48 bar with tabs off.</summary>
    public float TitleBarH(bool tabs) => tabs ? TabStripH + ToolbarH : ToolbarH;

    /// <summary>The content layer is square and flush (W-Shell-Compact, W-Narrow: LeftMinimal, no rail beside it); from Small up it is the r8
    /// top-left card beside the pane (W2a).</summary>
    public bool SquareCard => Tier == WidthTier.Compact;
}
