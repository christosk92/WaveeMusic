// ── Themes/Classic/Structure/Rules/Tiers.cs — Classic's window tiers and the frame's tier records (themes plan 3.2, 3.6; TC1) ────────
//
// THE LADDERS (board `C-Breakpoints`, plan 3.1 / A.6, owner NQ12):
//
//   window   Compact < 528 · Small 528-1007 · Standard 1008-1919 · Huge >= 1920. The holds are 40 DIP and directional: a window
//            WIDENING past 528 or 1008 changes tier only at 568 / 1048, one NARROWING past 1920 only below 1880. The Huge edge has
//            no widening hold, so a launch at 1920 and a window maximised to 1920 both land in Huge. A fresh resolve (a launch, a
//            navigation, a theme switch) uses the edge itself, so 528-567 and 1008-1047 are direction-dependent (NQ12). There is no
//            Huge 4K band: "Huge 4K uses the same rules at 3840", so the Huge records cover every width from 1920 up.
//   content  the page gutter's ladder (tuned Classic F3): the content region (the window less the nav pane and an inline panel) is
//            Compact below 600 (gutter 16), Small from 600 (32), Standard from 880 (36), each edge with 24 DIP of hysteresis either
//            way (the artist hero's `TierHysteresis`), and Huge from 1600 (36; the width where Standard's 1600 page cap would leave
//            dead margins), held 40 on narrowing like the window's Huge edge.
//   height   Short below 680 tall, Regular from 720 (the board's "height < 680, leave >= 720").
//
// THE RECORDS. A view reads `ctx.Tier()` and one of the records below; it never compares a width (WVTH013).
//   · `ClassicFrameLayout` per WINDOW tier: the content card's state (square and flush at Compact, the r8 card with its left and top
//     hairline from Small up) and the regions a resized frame may clip. The nav pane's mode per tier is `ClassicPolicies.NavPane`'s
//     (Compact the drawer, Small the forced 48 rail over the whole band, Standard and Huge the resizable pane); the title bar's
//     allocation per tier is `ClassicPolicies.Chrome`'s (`ChromePolicy.Compact` / `.Huge`).
//   · `ClassicPageLayout` per CONTENT tier: the one page gutter every Classic page insets its content by.
//   · The tier-invariant frame rhythm (F3): the pane inset 16, the head rhythm (title → meta 4, 24 of air over a page head) and the
//     detail rail's padding (24 / 8 / 24 / 16, top right bottom left).
//
// Public, so `Wavee.Tests` pins the ladders and the records (the assembly has no InternalsVisibleTo).

using System.Collections.Immutable;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Classic's tier policy and the frame's tier records.</summary>
public static class ClassicTiers
{
    // ── the window ladder (C-Breakpoints) ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Small begins here: production's drawer edge (48 + the 480 content floor), so the tier and the nav change together.</summary>
    public const float SmallFrom = 528f;

    /// <summary>Standard begins here: below it a 320 pane plus a 340 panel would leave the page less than 340.</summary>
    public const float StandardFrom = 1008f;

    /// <summary>Huge begins here: above it Standard's 1600 page cap would leave dead margins.</summary>
    public const float HugeFrom = 1920f;

    /// <summary>The window ladder's hold: on widening at 528 and 1008, on narrowing at 1920.</summary>
    public const float WindowHold = 40f;

    /// <summary>The window ladder: Compact | Small at 528 and Small | Standard at 1008 held 40 on widening, Standard | Huge at 1920 held
    /// 40 on narrowing, no Huge 4K edge.</summary>
    public static TierLadder Window { get; } = TierLadder.Of(
        new TierEdge(SmallFrom, NarrowHold: 0f, WidenHold: WindowHold),
        new TierEdge(StandardFrom, NarrowHold: 0f, WidenHold: WindowHold),
        new TierEdge(HugeFrom, NarrowHold: WindowHold, WidenHold: 0f));

    // ── the content ladder: the page gutter (F3) ─────────────────────────────────────────────────────────────────────────

    /// <summary>The content region is Small (gutter 32) from this width.</summary>
    public const float ContentSmallFrom = 600f;

    /// <summary>The content region is Standard (gutter 36) from this width.</summary>
    public const float ContentStandardFrom = 880f;

    /// <summary>The content region is Huge from this width.</summary>
    public const float ContentHugeFrom = 1600f;

    /// <summary>The gutter ladder's hysteresis, either way (the artist hero's 24).</summary>
    public const float GutterHold = 24f;

    /// <summary>The content ladder: 600 and 880 held 24 either way, 1600 held 40 on narrowing.</summary>
    public static TierLadder Content { get; } = TierLadder.Of(
        new TierEdge(ContentSmallFrom, NarrowHold: GutterHold, WidenHold: GutterHold),
        new TierEdge(ContentStandardFrom, NarrowHold: GutterHold, WidenHold: GutterHold),
        new TierEdge(ContentHugeFrom, NarrowHold: WindowHold, WidenHold: 0f));

    // ── the height ladder ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The window is Short below this height (C-Breakpoints, C-Shell-Short).</summary>
    public const float ShortBelow = 680f;

    /// <summary>The Short → Regular hold (Regular from 720).</summary>
    public const float ShortHold = 40f;

    /// <summary>The height ladder: Short below 680, Regular from 720.</summary>
    public static TierLadder Height { get; } = TierLadder.Of(new TierEdge(ShortBelow, NarrowHold: 0f, WidenHold: ShortHold));

    /// <summary>The page height Classic's Short layout guarantees at the 330-DIP window minimum: 330 less the 48-DIP title row and the
    /// 72-DIP player bar (plan 3.2; C-Shell-Short draws exactly this 210).</summary>
    public const float ShortFloorH = 210f;

    /// <summary>Classic's policy. One instance, so the facts a structure republishes compare equal. Declared after the ladders it names:
    /// property initializers run in textual order.</summary>
    public static TierPolicy Policy { get; } = new TierPolicy(
        Window, Content, Height,
        CompactFloor: TierPolicy.WindowFloor,
        ShortFloor: ShortFloorH,
        Rungs: ImmutableArray<NamedRung>.Empty,
        Ladders: ImmutableArray<NamedLadder>.Empty);

    // ── the frame's records (per window tier) ────────────────────────────────────────────────────────────────────────────

    /// <summary>The nav pane's region floor where the pane is docked: the 48 rail (a user may collapse the Standard pane to it).</summary>
    private const float RailRegionW = 48f;

    /// <summary>The content card's floor beside a docked pane (the nav policy's content floor).</summary>
    private const float ContentRegionW = 480f;

    private static readonly ClassicFrameLayout[] s_frames =
    [
        // Compact (C-Shell-Compact, C-Frame-Narrow 420): no rail, the drawer over the page; the card is square and flush to the window
        // edge, its only hairline the top one.
        new(WidthTier.Compact, SquareCard: true, ImmutableArray.Create(new TierRegion("content.card", TierPolicy.WindowFloor))),
        // Small (C-Shell-Small, C-Frame-Narrow 640): the forced 48 rail over the whole band, the r8 card beside it.
        new(WidthTier.Small, SquareCard: false,
            ImmutableArray.Create(new TierRegion("nav.pane", RailRegionW), new TierRegion("content.card", SmallFrom - RailRegionW))),
        // Standard (the tuned boards) and Huge (C-Shell-Huge): the resizable pane, the r8 card.
        new(WidthTier.Standard, SquareCard: false,
            ImmutableArray.Create(new TierRegion("nav.pane", RailRegionW), new TierRegion("content.card", ContentRegionW))),
        new(WidthTier.Huge, SquareCard: false,
            ImmutableArray.Create(new TierRegion("nav.pane", RailRegionW), new TierRegion("content.card", ContentRegionW))),
    ];

    /// <summary>The frame's record for window tier <paramref name="window"/> (Huge 4K reads Huge's: Classic draws no 4K band).</summary>
    public static ClassicFrameLayout Frame(WidthTier window) => s_frames[Math.Min((int)window, (int)WidthTier.Huge)];

    /// <summary>The frame's record for <paramref name="facts"/>; Standard's where no frame resolved the tiers (a view mounted alone).</summary>
    public static ClassicFrameLayout FrameOf(in TierFacts facts) => Frame(facts.Policy is null ? WidthTier.Standard : facts.Window);

    // ── the page's records (per content tier): the one page gutter (F3) ──────────────────────────────────────────────────

    private static readonly ClassicPageLayout[] s_pages =
    [
        new(WidthTier.Compact, Gutter: 16f),     // C-Shell-Compact 16, C-Frame-Narrow (640 and 420) 16
        new(WidthTier.Small, Gutter: 32f),       // C-Shell-Small, C-Shell-Short 32
        new(WidthTier.Standard, Gutter: 36f),    // the tuned Standard boards' 36
        new(WidthTier.Huge, Gutter: 36f),        // C-Shell-Huge 36: Huge widens the grids, not the margins
    ];

    /// <summary>The page's record for content tier <paramref name="content"/>.</summary>
    public static ClassicPageLayout Page(WidthTier content) => s_pages[Math.Min((int)content, (int)WidthTier.Huge)];

    /// <summary>The page's record for <paramref name="facts"/>; Standard's where no frame resolved the tiers (a view mounted alone).</summary>
    public static ClassicPageLayout PageOf(in TierFacts facts) => Page(facts.Policy is null ? WidthTier.Standard : facts.Content);

    // ── the tier-invariant frame rhythm (F3) ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The one inset of a pane's content (the insights sheet): 16 at every tier.</summary>
    public const float PaneInset = 16f;

    /// <summary>The head rhythm: a page title's meta line sits this far under it.</summary>
    public const float HeadMetaGap = 4f;

    /// <summary>The head rhythm: the air over a page head that has no masthead (the Liked head's 24).</summary>
    public const float HeadTop = 24f;

    /// <summary>The detail rail's padding: 24 / 8 / 24 / 16 (top, right, bottom, left).</summary>
    public static readonly Edges4 RailPadding = new(16f, 24f, 8f, 24f);
}

/// <summary>What Classic's frame lays out in one window tier.</summary>
/// <param name="Tier">The window tier.</param>
/// <param name="SquareCard">The content card is square and flush to the window's left edge, with only its top hairline (Compact: no
/// rail beside it); otherwise it carries the r8 top-left corner and the left and top hairline.</param>
/// <param name="Regions">The regions a resized frame may clip, in the row's order, each with its floor.</param>
public sealed record ClassicFrameLayout(WidthTier Tier, bool SquareCard, ImmutableArray<TierRegion> Regions) : ITierLayoutRecord
{
    /// <summary>The node keys of the frame's four top-level regions (the tier tests find them): the title bar, the nav pane's column,
    /// the content card and the player bar.</summary>
    public const string TitleBarKey = "shell.titlebar", NavColumnKey = "shell.nav.column", ContentCardKey = "shell.content.card",
        PlayerBarKey = "shell.playerbar";

    /// <summary>The content card's corners: square at Compact, the card radius on the top left otherwise.</summary>
    public CornerRadius4 CardCorners => SquareCard ? default : new CornerRadius4(Radii.Card, 0f, 0f, 0f);
}

/// <summary>What every Classic page reads in one content tier.</summary>
/// <param name="Tier">The content tier.</param>
/// <param name="Gutter">The page gutter: 16 / 32 / 36 / 36.</param>
public sealed record ClassicPageLayout(WidthTier Tier, float Gutter)
{
    /// <summary>A body inset by the gutter either side, <paramref name="top"/> over it and <paramref name="bottom"/> under it.</summary>
    public Edges4 Pad(float top, float bottom) => new(Gutter, top, Gutter, bottom);
}
