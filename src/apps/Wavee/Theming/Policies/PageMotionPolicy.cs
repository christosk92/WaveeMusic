// ── Theming/Policies/PageMotionPolicy.cs — how a structure moves one page to the next (winui page motion) ─────────────────
//
//   · `PageSwap`          the KIND of a page swap, which the shell decides from the two routes and the verb that moved
//                         (`Shell.SwapOf`): a top-level destination, a drill into a detail page, Back out of either, a sibling
//                         beside it in a pivot's order, a tab switch.
//   · `PageMotionPolicy`  what a structure answers per kind: the page root's recipe (both legs) and the staggered entrance of the
//                         incoming page's units (`EntranceCascade` over its `Element.EntranceGroup` containers). A structure that
//                         declares none keeps the core's one fade-through for every swap (Classic, Zune).

using FluentGpu.Foundation;

namespace Wavee.Theming;

/// <summary>The kind of a page swap: WinUI's navigation transitions by name (Microsoft Learn, "Page transitions").</summary>
public enum PageSwap : byte
{
    /// <summary>A tab switch, a new tab's first page, a page that turned out to be another: no direction.</summary>
    Neutral,
    /// <summary>A top-level destination (a nav pane item, a top-level route): WinUI's page refresh.</summary>
    Refresh,
    /// <summary>Back out of a top-level destination.</summary>
    RefreshBack,
    /// <summary>Deeper: a card or row opened its detail page (album, artist, playlist, show, ...). WinUI's drill in.</summary>
    DrillIn,
    /// <summary>Back out of a detail page.</summary>
    DrillBack,
    /// <summary>A sibling AFTER the current one in its pivot's order (the new page comes from the right).</summary>
    SlideFromRight,
    /// <summary>A sibling BEFORE the current one (the new page comes from the left).</summary>
    SlideFromLeft,
}

/// <summary>What a structure answers for a page swap. Equality is by value (the delegates by reference: a structure hands out
/// cached statics), so an unchanged bundle republished is not a change.</summary>
/// <param name="Recipe">The page root's recipe for a kind, both legs (<c>Exit.Active</c> keeps the outgoing page drawing for its exit);
/// null = the core's shared fade-through for that swap.</param>
/// <param name="Cascade">The incoming page's staggered entrance for a kind (its on-screen <c>EntranceGroup</c> children enter one after
/// another); null = the page enters as the one block the recipe moves.</param>
public sealed record PageMotionPolicy(Func<PageSwap, LayoutTransition?>? Recipe = null, Func<PageSwap, EntranceCascade?>? Cascade = null)
{
    /// <summary>The shell as it shipped: the core's fade-through for every swap, no cascade, and a cut under reduced motion.</summary>
    public static PageMotionPolicy Default { get; } = new();

    /// <summary>The swap under reduced motion (Windows "Animation effects" off), for every kind: a quick fade with no slide, no scale
    /// and no stagger. Null = a cut, as the shell shipped.</summary>
    public LayoutTransition? ReducedFade { get; init; }
}
