// ── Themes/Classic/Structure/Rules/Tiers.cs — Classic's window tiers (themes plan 3.2, 6.1; T3b) ───────────────────────
//
// THE LADDERS CLASSIC SHIPS TODAY, as data. T3b moves, verbatim and out of core, the ladders core models consume:
//
//   window ladder   the sidebar's window band (was `SidebarPaneModeRules`, `Shell\Sidebar.Resize.cs`): Tiny below 528 (leave
//                   at 568), Narrow below 660 (leave at 700), Wide above. Maps to Compact / Small / Standard. A fresh resolve
//                   (a launch, a navigation) uses the entering edges 528 and 660, exactly as `InitialBand` did.
//   player bar      the six pressure tiers over the bar's own width, 440 / 760 / 900 / 1100 / 1240 with a 24-DIP hold on
//                   narrowing (was `Shell.PlayerBar`, `Shell\Shell.cs`): `Tiers.PlayerBar.cs`.
//
// Classic's window has no Huge band today (Auto zoom folds 1920+ back into Standard), so its Huge and Huge 4K edges are
// absent. TC1 redesigns the window ladder from the Classic boards (`C-Breakpoints`: 528 / 1008 / 1920 with 40-DIP widening
// holds); until then "Classic's window-size behaviour is unchanged" and the numbers below are the production numbers.
//
// NOT MOVED, because none of them is a ladder this primitive can state without a behaviour change (plan 3.6: "what cannot
// move without a behaviour change stays"): the chrome-row allocator (`Shell.Chrome.cs`: a space-accounting allocator with
// promotion reserves), the stage aspect classes (`Stage.cs` `Layout.ClassOf`: width, height and ratio edges with different
// holds), the right-rail fit (`Shell.CanFitRail`: arithmetic over the presented pane), and the detail page-mode ladder
// (`Detail.Breakpoints.ModeFor`: a vertical exit that jumps to the nominal mode). They stay where they are, for T3 (policy
// bundles) and the page PRs.
//
// NEW DATA, consumed by nothing yet: the content ladder (Classic's content region tiers on the window's edges until TC1
// draws its own) and the height ladder (the board's Short below 680 tall, left at 720). They exist so `TierFacts` is
// complete from the first frame; no Classic view reads them in T3b.

using System.Collections.Immutable;
using FluentGpu.Foundation;
using Wavee.Theming;

namespace Wavee.Themes.Classic;

/// <summary>Classic's tier policy.</summary>
internal static partial class ClassicTiers
{
    /// <summary>The window is Tiny below this width (no rail, the drawer): 48 + the 480 content floor.</summary>
    public const float TinyEnterW = 528f;

    /// <summary>The Tiny → Narrow edge: a Tiny window leaves Tiny once it reaches this width (hysteresis 40).</summary>
    public const float TinyLeaveW = 568f;

    /// <summary>The window is Narrow below this width (a forced rail whose toggle opens the overlay pane).</summary>
    public const float NarrowEnterW = 660f;

    /// <summary>The Narrow → Wide edge: a Narrow window leaves Narrow once it reaches this width (hysteresis 40).</summary>
    public const float WideEnterW = 700f;

    /// <summary>The window is Short below this height (the Classic short board).</summary>
    public const float ShortEnterH = 680f;

    /// <summary>The Short → Regular edge (hysteresis 40).</summary>
    public const float ShortLeaveH = 720f;

    /// <summary>The page height Classic's Short layout guarantees at the 330-DIP minimum window height: 330 less the 48-DIP
    /// title row and the 72-DIP player bar (plan 3.2).</summary>
    public const float ShortFloorH = 210f;

    /// <summary>The window ladder: Compact | Small at 528, Small | Standard at 660, widening holds 40 (the 568 and 700 leave
    /// edges), no hold on narrowing, no Huge edge.</summary>
    public static TierLadder Window { get; } = TierLadder.Of(
        new TierEdge(TinyEnterW, NarrowHold: 0f, WidenHold: TinyLeaveW - TinyEnterW),
        new TierEdge(NarrowEnterW, NarrowHold: 0f, WidenHold: WideEnterW - NarrowEnterW));

    /// <summary>The content ladder (new, unconsumed): the window's edges applied to the content region's width.</summary>
    public static TierLadder Content { get; } = TierLadder.Of(
        new TierEdge(TinyEnterW, NarrowHold: 0f, WidenHold: TinyLeaveW - TinyEnterW),
        new TierEdge(NarrowEnterW, NarrowHold: 0f, WidenHold: WideEnterW - NarrowEnterW));

    /// <summary>The height ladder (new, unconsumed): Short below 680, Regular from 720.</summary>
    public static TierLadder Height { get; } = TierLadder.Of(
        new TierEdge(ShortEnterH, NarrowHold: 0f, WidenHold: ShortLeaveH - ShortEnterH));

    /// <summary>Classic's policy. One instance, so the facts a structure republishes compare equal.</summary>
    public static TierPolicy Policy { get; }

    // A static constructor, not a property initializer: the ladders live in two partial files, and a field initializer of one
    // file may run before another file's. The constructor body runs after every initializer.
    static ClassicTiers()
    {
        Policy = new TierPolicy(
            Window, Content, Height,
            CompactFloor: TierPolicy.WindowFloor,
            ShortFloor: ShortFloorH,
            Rungs: ImmutableArray<NamedRung>.Empty,
            Ladders: ImmutableArray.Create(new NamedLadder(TierLadderIds.PlayerBar, PlayerBar)));
    }
}
