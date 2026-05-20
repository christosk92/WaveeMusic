using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Wavee.UI.WinUI.Models.PodcastBrowse;

/// <summary>
/// Visual layout kind for one section on the podcast-browse Zune surface.
/// The wire DTO's <c>TypeName</c> maps onto one of these in
/// <c>PodcastBrowseViewModel.PickLayoutKind</c>; the
/// <see cref="Controls.PodcastBrowse.PodcastBrowseSectionTemplateSelector"/>
/// then routes each section to the right DataTemplate.
/// </summary>
public enum PodcastBrowseSectionLayoutKind
{
    /// <summary>Horizontal grid of colored category tiles
    /// (<c>BrowseGridSectionData</c>).</summary>
    ArtworkRail,
    /// <summary>Small CTA pill bar (<c>BrowseRelatedSectionData</c>).</summary>
    Cta,
    /// <summary>Numbered ranked list (e.g. "Most subscribed"). Reserved for
    /// future Spotify section types that return ranked data; falls back to
    /// <see cref="ArtworkRail"/> until we encounter them.</summary>
    RankedList,
    /// <summary>Plain title-only list (e.g. "New additions"). Reserved; falls
    /// back to <see cref="ArtworkRail"/> until used.</summary>
    PlainList,
    /// <summary>Multi-row UniformGridLayout — used when a drilled category
    /// page has a single show-bearing section with paginated content (e.g.
    /// "Books → Popular books podcasts" with 1000 items behind the wire).
    /// Renders the items as a wall plus a "Show more" affordance for the
    /// next page.</summary>
    Grid,
}

/// <summary>
/// One shelf on the podcast-browse page. The owning
/// <c>PodcastBrowseViewModel.ContentShelves</c> collection is bound to an
/// <c>ItemsControl</c> whose template selector keys off
/// <see cref="LayoutKind"/>.
/// </summary>
public sealed partial class PodcastBrowseSection : ObservableObject
{
    public string Title { get; init; } = string.Empty;
    public string? Subtitle { get; init; }
    public string SectionUri { get; init; } = string.Empty;
    public PodcastBrowseSectionLayoutKind LayoutKind { get; init; }
    public ObservableCollection<PodcastBrowseTile> Items { get; init; } = [];

    /// <summary>Server-reported total item count for the section. Used by
    /// the grid layout to decide when <see cref="HasMore"/> is still true.
    /// Source-gen pushes a HasMore PropertyChanged when this changes so
    /// the "Show more" button's Visibility refreshes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMore))]
    private int _totalCount;

    /// <summary>How many items we've actually pulled from the server for this
    /// section (includes any items currently displayed in the hero filmstrip,
    /// since the server-side offset is independent of where items render).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMore))]
    private int _loadedFromServer;

    /// <summary>True while a pagination request is in flight for this section
    /// — the "Show more" button binds against this to swap to a loading
    /// state.</summary>
    [ObservableProperty]
    private bool _isLoadingMore;

    /// <summary>True while there are more items to fetch from the server.
    /// Used by the page's scroll-near-end auto-pagination trigger.</summary>
    public bool HasMore => LoadedFromServer < TotalCount;

    /// <summary>
    /// Fixed-size collection of 5 sentinel objects backing the grid's shimmer
    /// footer row. The shimmer ItemsRepeater binds to this — the items
    /// themselves are unused; the template renders a static shimmer card per
    /// slot. One row × 5 slots roughly matches the tile width at the page's
    /// default content width, so the loading state reserves a single new row
    /// of space without jumping the layout. Instance property (not static)
    /// so x:Bind picks it up without static-binding gymnastics.
    /// </summary>
    public IReadOnlyList<object> PaginationShimmerSlots { get; } =
        new object[] { new(), new(), new(), new(), new() };
}

/// <summary>
/// One card inside an <see cref="PodcastBrowseSection"/>. Carries everything
/// the colored-tile renderer needs (artwork + bg color hex) plus the URI to
/// drill into when tapped.
/// </summary>
public sealed class PodcastBrowseTile
{
    public string Title { get; init; } = string.Empty;
    public string? Subtitle { get; init; }
    public string? ImageUrl { get; init; }
    /// <summary>Spotify <c>cardRepresentation.backgroundColor.hex</c> (e.g.
    /// "#0d73ec"). Drives the tile's background when <c>IsCategoryTile</c>
    /// mode is on.</summary>
    public string? ColorHex { get; init; }
    public string NavigationUri { get; init; } = string.Empty;
}

/// <summary>
/// One chip in the horizontal chip rail beneath the hero. <see cref="IsSelected"/>
/// is flipped imperatively from <c>PodcastBrowseViewModel.MarkCategorySelected</c>
/// when the user taps a chip; the bind drives the accent-fill visual.
/// </summary>
public sealed partial class PodcastBrowseCategoryItem : ObservableObject
{
    public string Title { get; init; } = string.Empty;
    public string Uri { get; init; } = string.Empty;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// A header-and-chips group inside the chip rail. Each group corresponds to
/// one <c>BrowseGridSectionData</c> section in Spotify's all-categories
/// response (Arts &amp; Entertainment, Business &amp; Technology, etc.).
/// </summary>
public sealed class PodcastBrowseCategoryGroup
{
    public string Title { get; init; } = string.Empty;
    public ObservableCollection<PodcastBrowseCategoryItem> Chips { get; } = [];
}

/// <summary>
/// One rung in the breadcrumb trail above the title. The VM owns a stack of
/// these so drilling Podcasts → Comedy → "All charts" produces a click-able
/// trail back to any rung.
/// </summary>
public sealed class BreadcrumbItem
{
    public string Title { get; init; } = string.Empty;
    public string Uri { get; init; } = string.Empty;
}
