// ── Theming/Contracts/BrowseProps.cs — the props of the Browse family's views (themes plan 6.0, T10a2) ─────────────────────
//
// The core hosts keep the data and the decisions: the directory's load verdict and the memoized band list (`Browse.DirectoryPage`), the Charts
// band's demand and deck (`BrowseChartsBand`), a category page's readiness, its section layout and the card rows each band reads
// (`Browse.CategoryPage`). A theme's view draws what these records carry. Every member is read-only (WVTH006). The pure rules a view needs stay
// where they are and are called by the view (`BrowseTaxonomy`, `BrowseDirectorySeeds`, `BrowsePageLayout.TitleIsRedundant`, `BrowseTiles.ToModel`).

using FluentGpu.Controls;

namespace Wavee.Theming.Contracts;

/// <summary>The props of <see cref="ViewKeys.BrowseDirectory"/>. The host rebuilds it on every render it makes; the page re-runs exactly when
/// the tile list, a tile row or the load verdict changed (<see cref="Groups"/> is the host's memo and keeps its reference while nothing did).</summary>
public sealed record BrowseDirectoryProps
{
    /// <summary>The scroll key of the directory's offset (the page's scroll scope plus "browse").</summary>
    public required string ScrollKey { get; init; }

    /// <summary>Pending (the cold load: the skeleton shows), Ready (the bands show) or Failed (the error vacancy with Retry).</summary>
    public required BrowseLoad Load { get; init; }

    /// <summary>The bands in taxonomy order with their categories (<see cref="BrowseTaxonomy.Grouped"/>); null until the directory is Ready; EMPTY when it
    /// answered with no categories (the "unavailable" vacancy).</summary>
    public IReadOnlyList<(BrowseGroup Group, IReadOnlyList<BrowseCategory> Items)>? Groups { get; init; }

    /// <summary>The failure vacancy's Retry (un-asks the directory first, then asks again).</summary>
    public required Action Retry { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.BrowseCharts"/>: the Charts band's deck and the verbs of its tiles.</summary>
public sealed record BrowseChartsProps
{
    /// <summary>Pending (the three blank folds shimmer), Ready or Failed.</summary>
    public required HomeLoad State { get; init; }

    /// <summary>The chart deck's section views (empty while not Ready, and for a Ready-and-empty answer).</summary>
    public required IReadOnlyList<HomeSectionView> Deck { get; init; }

    /// <summary>A tile was invoked: drill into that chart section with the directory as its origin.</summary>
    public required Action<HomeSectionView> OpenTile { get; init; }

    /// <summary>The deck header's chevron: the Charts category page.</summary>
    public required Action OpenHeader { get; init; }

    /// <summary>The compact failure vacancy's Retry (refetches only the deck).</summary>
    public required Action Retry { get; init; }
}

/// <summary>One band of a category page: its layout facts, and either a shelf's cards or a tile block's categories.</summary>
public sealed record BrowseBand
{
    /// <summary>The section's facts (uri, title, kind, counts, row slot) as the layout rule saw it.</summary>
    public required BrowseSectionFacts Facts { get; init; }

    /// <summary>A shelf band's card view (the section's cards, de-duplicated); null for a tile block.</summary>
    public HomeSectionView? Shelf { get; init; }

    /// <summary>A grid / related band's categories, in wire order; empty for a shelf.</summary>
    public IReadOnlyList<BrowseCategory> Tiles { get; init; } = Array.Empty<BrowseCategory>();
}

/// <summary>The props of <see cref="ViewKeys.BrowseCategory"/>. Rebuilt by the host on every render it makes (a hydrating card row re-describes
/// the shelves), so the page view re-runs exactly when the page tree used to be rebuilt.</summary>
public sealed record BrowseCategoryProps
{
    /// <summary>Pending (the page's own row has not answered), Ready, or Failed (the error arm: Retry and the Explore link).</summary>
    public required BrowseLoad Load { get; init; }

    /// <summary>The page's own title (the masthead's; a shelf drill and a flattened heading compare against it).</summary>
    public required string Title { get; init; }

    /// <summary>The category's own colour (opaque ARGB: the page header's accent, else its directory tile's), contract 1.62; null when it has none (a
    /// theme that paints a banner falls back to its own colour).</summary>
    public uint? Color { get; init; }

    /// <summary>The page the category was opened from, in its own words ("Music" over "Pop"), contract 1.62; null when it was opened from the directory
    /// or from nowhere.</summary>
    public string? Parent { get; init; }

    /// <summary>The page uri (a flattened grid with no lone-shelf key is keyed by it).</summary>
    public required string PageUri { get; init; }

    /// <summary>The scroll key of the page's offset (the tab's scope, "browse:", the page uri).</summary>
    public required string ScrollKey { get; init; }

    /// <summary>The body's SHAPE (<see cref="BrowsePageLayout.Of"/>): shelves, one flattened grid, two merged shelves.</summary>
    public required BrowsePageLayout.Mode Mode { get; init; }

    /// <summary>Every answered band, in wire order (empty when the page has nothing: the "unavailable" arm).</summary>
    public required IReadOnlyList<BrowseBand> Bands { get; init; }

    /// <summary>FlattenTwoConcat's merged card list, memoized on the two shelf views by the host so the grid's gate compares by reference; empty otherwise.</summary>
    public required IReadOnlyList<HomeCard> Merged { get; init; }

    /// <summary>The overlay service the cards' menus open on.</summary>
    public IOverlayService? Overlay { get; init; }

    /// <summary>The error arm's Retry (un-asks the page first, then asks again).</summary>
    public required Action Retry { get; init; }

    /// <summary>A tile of a band was invoked: open that category with THIS page as the origin (read at click time).</summary>
    public required Action<string, string> OpenCategory { get; init; }

    /// <summary>A titled shelf's header: drill into that section with THIS page as the origin.</summary>
    public required Action<HomeSectionView> OpenSection { get; init; }

    /// <summary>"Explore all categories": the directory, no origin.</summary>
    public required Action ExploreAll { get; init; }
}
