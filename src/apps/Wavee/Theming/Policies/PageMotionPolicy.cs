// ── Theming/Policies/PageMotionPolicy.cs — how a structure moves one page to the next (winui page motion) ─────────────────
//
//   · `PageSwap`          the KIND of a page swap, which the shell decides from the two routes and the verb that moved
//                         (`Shell.SwapOf`): a top-level destination, a drill into a detail page, Back out of either, a sibling
//                         beside it in a pivot's order, a tab switch.
//   · `PageMotionPolicy`  what a structure answers per kind: the page root's recipe (both legs) and the staggered entrance of the
//                         incoming page's units (`EntranceCascade` over its `Element.EntranceGroup` containers). A structure that
//                         declares none keeps the core's one fade-through for every swap (Classic, Zune).
//   · `SectionJump`       how a tab INSIDE a page moves to its section (the artist page's pivot): a glide when the section is near, an
//                         arrival (a cut to a lead short of it, then the glide's tail) when it is far. None keeps the full glide.

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

    /// <summary>How a tab INSIDE a page moves to its section (the artist page's pivot). Null = the page's own scroll glide the whole way.</summary>
    public SectionJump? SectionJump { get; init; }

    /// <summary>The library family's pivot (<see cref="ShellCapabilities.LibraryFamily"/>): the recipe of the body under the fixed head when the kind
    /// changes, both legs, given whether the new kind comes after the old one in the pivot's order (it comes in from the right) and the facts the host is
    /// drawn in. Asked under reduced motion too (the host lets the recipe own it): answer the quick fade there. Null = the core's
    /// <c>Design.Nav.SameKindSwap</c> fade.</summary>
    public Func<bool, TierFacts, LayoutTransition?>? LibraryPivot { get; init; }

    /// <summary>The connected navigation flight (<c>SharedTransition.BeginNav</c>): an album card's cover flies into the album page's hero,
    /// and back into the card on Back. Null = this structure flies nothing (the swap plays as authored).</summary>
    public FluentGpu.Animation.ConnectedNavSpec? Connected { get; init; }

    /// <summary>Under a flight, the page swap keeps its recipe's opacity legs and timings and loses its slide, zoom and stagger
    /// (<c>Design.Nav.Quiet</c>): the cover is the only thing that travels (Microsoft: a connected animation "does not work well" over a
    /// translating page transition). False keeps the authored recipe under the flight.</summary>
    public bool ConnectedQuietPage { get; init; } = true;
}

/// <summary>How a tab INSIDE a page (the artist page's pivot) moves the page to its section, by the distance <c>d</c> to it and the page viewport's
/// height <c>vh</c>. Near (<c>d</c> &lt;= <see cref="NearViewports"/> x <c>vh</c>) the page glides there. Far, it cuts to a lead short of the section
/// on the side the user comes from, already moving, and decelerates in (<c>ScrollHandle.ScrollTo</c>'s <c>maxTravel</c>), while what the page shows
/// fades in over the same-kind swap's 100 ms from 40 % (<c>Design.Nav.SameKindEnterMs</c>, <c>SameKindEnterFrom</c>): a glide over many screens
/// would cross virtualized grids at a viewport per frame. Under reduced motion every tab jumps and keeps the fade.</summary>
/// <param name="NearViewports">The farthest a section glides from, in viewport heights.</param>
/// <param name="LeadViewports">The lead a far section arrives from, in viewport heights, clamped to [<paramref name="LeadMin"/>, <paramref name="LeadMax"/>].</param>
/// <param name="LeadMin">The shortest lead, DIP.</param>
/// <param name="LeadMax">The longest lead, DIP.</param>
public sealed record SectionJump(float NearViewports, float LeadViewports, float LeadMin, float LeadMax)
{
    /// <summary>Whether a section <paramref name="distance"/> away (DIP, either sign) in a <paramref name="viewport"/>-high page is far.</summary>
    public bool IsFar(double distance, double viewport) => Math.Abs(distance) > NearViewports * Math.Max(1.0, viewport);

    /// <summary>The lead a far section arrives from in a <paramref name="viewport"/>-high page, DIP.</summary>
    public double LeadFor(double viewport) => Math.Clamp(LeadViewports * viewport, LeadMin, LeadMax);
}
