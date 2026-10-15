// ── Theming/Contracts/DiscographyPageProps.cs — the props of the standalone discography page (themes plan 6.0, T10b2) ──────────────
//
// `disco:<kind>:<artist>`: a breadcrumb ("Artist › Albums"), the page title, the facet selector and the SAME grid + drawer the artist page's facets
// use. `Artist.DiscoPageHost` (`[CoreHost]`, Entities\Artist.Discography.cs) keeps the route parse, the artist handle and its demand, the scroll handle
// and the 24-DIP scroll floor it provides to the grid (`LazyScroll.Slot`), and the navigation the crumbs and the selector cause. Deliberately NO cover
// palette (the system accent) and NO shell tint (ch 08 W22).

using FluentGpu.Dsl;
using FluentGpu.Scroll.Runtime;

namespace Wavee.Theming.Contracts;

/// <summary>The props of <see cref="ViewKeys.Discography"/>. The host rebuilds it on every render (the artist table's publication re-renders it to
/// pick up a landed name), so it never compares equal to another instance.</summary>
public sealed record DiscographyPageProps
{
    /// <summary>The route parsed as a discography route; false draws the error vacancy and nothing else.</summary>
    public required bool Parsed { get; init; }

    /// <summary>The artist's name (the fallback name until it lands): the page's one ancestor crumb.</summary>
    public required string Name { get; init; }

    /// <summary>The facet's title: the page title.</summary>
    public required string Title { get; init; }

    /// <summary>The route's facet: the selector's selection (a FRESH signal per render: the route, not the control, is the truth).</summary>
    public required DiscoFacet Facet { get; init; }

    /// <summary>A crumb click (item 0 opens the artist).</summary>
    public required Action<int> OnCrumb { get; init; }

    /// <summary>The selector's change: navigates to the other facet's route (the next route re-seeds the selector).</summary>
    public required Action<int> OnFacet { get; init; }

    /// <summary>The facet's grid at an expanded top inset (null while the route holds no valid artist).</summary>
    public Func<float, Element>? Grid { get; init; }

    /// <summary>The scroll key of the page's offset (the tab's scope plus the route key).</summary>
    public required string ScrollKey { get; init; }

    /// <summary>The page viewport's scroll handle (the host watches its offset).</summary>
    public required ScrollHandle Scroll { get; init; }

    /// <summary>Equal only to itself: the host rebuilds the record on every render.</summary>
    public bool Equals(DiscographyPageProps? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}
