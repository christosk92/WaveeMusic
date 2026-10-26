// ── Theming/Contracts/ViewKeys.Browse.cs — the Browse and Search families of published view keys (themes plan 6.0, T10a2) ─────
//
// T10a2 publishes the Browse directory (`Pages.Browse`), its Charts band, a Browse category page (`Pages.BrowseCategory`), the Browse
// section drill page (`Pages.BrowseSection`, the Home drill page's template under its own key so a theme may dress it apart) and the
// Search results page (`Pages.Search`).
//
// What stays core: the load verdicts (`BrowseLoadGate`, the page's readiness marks), the demands and retries, the masthead publication, the
// section layout rule (`BrowsePageLayout`), the band taxonomy (`BrowseTaxonomy`), the search query model, the facet strip rule
// (`Search.FacetsFrom`), the readiness of each facet, the fallback interleave, the rail's items, the hit adapters (`Search.HitData`,
// `OpenHit`, `MenuOf`, `DragOf`, `TrailingOf`), the column hysteresis and the omnibar.

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>The Browse directory: the masthead reserve, the clipped scroll column and the bands (Top, Charts, For you, Genres, Mood, More).
    /// Mounted by `Browse.DirectoryPage`. Props: <see cref="BrowseDirectoryProps"/>.</summary>
    public static readonly ViewKey<BrowseDirectoryProps> BrowseDirectory = new("Pages.Browse");

    /// <summary>The Browse directory's Charts band (the Fold deck over the five chart sections, its shimmer, its empty and its Retry).
    /// Mounted by `BrowseChartsBand`. Props: <see cref="BrowseChartsProps"/>.</summary>
    public static readonly ViewKey<BrowseChartsProps> BrowseCharts = new("Browse.Charts");

    /// <summary>A Browse category page: the shelves, the tile blocks, the flattened grid, the empty and error arms and the shimmer.
    /// Mounted by `Browse.CategoryPage`. Props: <see cref="BrowseCategoryProps"/>.</summary>
    public static readonly ViewKey<BrowseCategoryProps> BrowseCategory = new("Pages.BrowseCategory");

    /// <summary>The Browse section drill page (`browse-section:`): the "N items" line over the bound card grid in a skeleton region. Mounted by
    /// `SectionScreen` (which mounts <see cref="HomeSection"/> for a Home section). Props: <see cref="HomeSectionProps"/>.</summary>
    public static readonly ViewKey<HomeSectionProps> BrowseSection = new("Pages.BrowseSection");

    /// <summary>The Search results page: the query echo, the facet row, the keyed sliding facet body (Top result, Best matches, the playlist rail,
    /// the grids and lists, the genre links, the related queries) and its empty, offline and error states. Mounted by `Search.PageHost`.
    /// Props: <see cref="SearchPageProps"/>.</summary>
    public static readonly ViewKey<SearchPageProps> SearchPage = new("Pages.Search");
}
