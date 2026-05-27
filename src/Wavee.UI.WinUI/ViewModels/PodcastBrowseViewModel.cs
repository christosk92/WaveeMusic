using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Models.PodcastBrowse;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// Microsoft Store-style podcast browse VM. Owns four independently-scoped
/// surfaces: <see cref="CategoryGroups"/> (grouped chip rail beneath the hero,
/// loaded once on first root nav and frozen), <see cref="HeroSlides"/> (top
/// filmstrip, repopulated on every drill), <see cref="ContentShelves"/>
/// (typed shelves below the chips), and <see cref="Breadcrumbs"/> (nav trail
/// above the title). The trail truncates to a clicked rung and reloads only
/// the hero + shelves; the chip rail stays static across drills.
/// </summary>
[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial class PodcastBrowseViewModel : SectionFeedViewModelBase
{
    /// <summary>
    /// Canonical root URI for the podcasts browse page. Public so navigation
    /// helpers / the sidebar entry can pass it in <c>ContentNavigationParameter.Uri</c>
    /// without re-typing the magic string. Sourced from Spotify's web payload
    /// (<c>spotify:page:0JQ5DArNBzkmxXHCqFLx2J</c>).
    /// </summary>
    public static string RootPodcastsUri { get; set; } = "spotify:page:0JQ5DArNBzkmxXHCqFLx2J";

    /// <summary>The "All podcast categories" drill target. Sourced from
    /// Spotify's web payload — the URI behind the root's "See all categories"
    /// CTA. The full ~60-entry categorical catalogue lives here; the rail
    /// loads from it on first root visit.</summary>
    public const string AllCategoriesUri = "spotify:page:0JQ5DArNBzkmxXHCqFLx2U";

    /// <summary>The "Podcast Charts" page — Spotify's editorial chart of
    /// top-ranked shows. Fetched alongside the root visit so the hero
    /// filmstrip carries actual show artwork instead of staying empty
    /// (the root response itself has no show-bearing section).</summary>
    public const string PodcastChartsUri = "spotify:page:0JQ5DAB3zgCauRwnvdEQjJ";

    private readonly IPodcastService _podcastService;
    private readonly ILogger? _logger;

    public PodcastBrowseViewModel(
        IPodcastService podcastService,
        ILogger<PodcastBrowseViewModel>? logger = null)
    {
        _podcastService = podcastService;
        _logger = logger;
    }

    [ObservableProperty]
    public partial string CurrentUri { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? SelectedHeroImageUrl { get; set; }

    [ObservableProperty]
    public partial string? BackgroundImageUrl { get; set; }

    /// <summary>Pinned synthetic chip at the head of the chip rail. Always
    /// points at the root podcasts page so "All" deselects the active
    /// category and reloads the editorial root content.</summary>
    public PodcastBrowseCategoryItem AllCategoriesChip { get; } = new()
    {
        Title = "All",
        Uri = RootPodcastsUri,
        IsSelected = true,
    };

    /// <summary>Chip rail beneath the hero, grouped by Spotify's natural
    /// "all-categories" section breakdown (Arts &amp; Entertainment,
    /// Business &amp; Technology, etc.). Populated once on first root nav
    /// and frozen — drill-downs do not touch it.</summary>
    public ObservableCollection<PodcastBrowseCategoryGroup> CategoryGroups { get; } = [];

    /// <summary>Typed shelves below the hero. Each entry's
    /// <see cref="PodcastBrowseSection.LayoutKind"/> selects its render template.</summary>
    public ObservableCollection<PodcastBrowseSection> ContentShelves { get; } = [];

    /// <summary>Crumb trail above the title. The VM owns the stack so drilling
    /// inside the same page instance doesn't require Frame history.</summary>
    public ObservableCollection<BreadcrumbItem> Breadcrumbs { get; } = [];

    /// <summary>
    /// Entry point called from <c>PodcastBrowsePage.OnEntered</c>. Branches on
    /// URI shape to either drill into a single section, drill into a sub-page,
    /// or render the root (which is what populates the left rail).
    /// </summary>
    public async Task LoadAsync(ContentNavigationParameter? parameter)
    {
        var uri = string.IsNullOrEmpty(parameter?.Uri) ? RootPodcastsUri : parameter!.Uri!;
        var pushBreadcrumb = !string.Equals(uri, CurrentUri, StringComparison.Ordinal);

        CurrentUri = uri;
        Title = parameter?.Title ?? "Podcasts";
        Subtitle = parameter?.Subtitle;
        SelectedHeroImageUrl = parameter?.ImageUrl;
        IsLoading = true;
        HasError = false;
        ErrorMessage = null;

        try
        {
            if (IsSectionUri(uri))
            {
                var section = await _podcastService.GetPodcastBrowseSectionAsync(uri).ConfigureAwait(true);
                if (section is null)
                {
                    ContentShelves.Clear();
                    HeroSlides = [];
                    HasError = true;
                    ErrorMessage = "This topic isn't available right now.";
                    return;
                }

                if (!string.IsNullOrEmpty(section.Title))
                    Title = section.Title;

                if (pushBreadcrumb)
                    PushBreadcrumb(Title, uri);

                HeroSlides = [];
                ContentShelves.Clear();
                ContentShelves.Add(MapSection(section));
            }
            else
            {
                var page = await _podcastService.GetPodcastBrowsePageAsync(uri).ConfigureAwait(true);
                if (page is null)
                {
                    ContentShelves.Clear();
                    HeroSlides = [];
                    HasError = true;
                    ErrorMessage = "Couldn't load podcast browse.";
                    return;
                }

                if (!string.IsNullOrEmpty(page.Title))
                    Title = page.Title;
                Subtitle = page.Subtitle ?? Subtitle;
                BackgroundImageUrl = page.BackgroundImageUrl;

                if (pushBreadcrumb)
                    PushBreadcrumb(Title, uri);

                if (IsRootUri(uri))
                {
                    // Root visit: chip rail comes from the richer
                    // all-categories endpoint (60+ entries grouped under 8
                    // headers); hero + shelves come from the Charts page.
                    // Both re-fire on every root visit so the surface stays
                    // current.
                    var auxTasks = new List<Task>(2);
                    auxTasks.Add(LoadCategoriesRailAsync());
                    auxTasks.Add(LoadChartsRootAsync());
                    await Task.WhenAll(auxTasks).ConfigureAwait(true);
                    AllCategoriesChip.IsSelected = true;
                }
                else
                {
                    // Drilled page: chip groups, hero, and shelves ALL come
                    // from THIS page's own response. The drilled page may
                    // carry its own sub-category sections (rendered as chips
                    // for further sideways drilling) and its own show-bearing
                    // sections (hero + shelves).
                    RebuildChipGroupsFromPage(page);
                    BuildHeroAndShelves(page);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "PodcastBrowse load failed for {Uri}", uri);
            ContentShelves.Clear();
            HeroSlides = [];
            HasError = true;
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Drill into a child page/section URI, pushing onto the breadcrumb
    /// stack. Called by the page code-behind when the user taps a tile or a
    /// category-rail entry that points at <c>spotify:page:*</c> or
    /// <c>spotify:section:*</c>.
    /// </summary>
    public Task DrillToAsync(string uri, string title, string? subtitle = null, string? imageUrl = null)
    {
        return LoadAsync(new ContentNavigationParameter
        {
            Uri = uri,
            Title = title,
            Subtitle = subtitle,
            ImageUrl = imageUrl,
        });
    }

    /// <summary>
    /// Truncate the breadcrumb stack to the clicked rung and reload the right
    /// pane. The left rail stays untouched per the Zune-stable-anchor design.
    /// </summary>
    public Task NavigateToBreadcrumbAsync(BreadcrumbItem rung)
    {
        if (rung is null) return Task.CompletedTask;

        // Drop everything after this rung — the new pane is its content.
        var index = Breadcrumbs.IndexOf(rung);
        if (index < 0) return Task.CompletedTask;

        for (int i = Breadcrumbs.Count - 1; i > index; i--)
            Breadcrumbs.RemoveAt(i);

        // Force a re-fetch even though CurrentUri may already match (the user's
        // intent is "reload from this rung").
        CurrentUri = string.Empty;
        ClearCategorySelection();
        return LoadAsync(new ContentNavigationParameter
        {
            Uri = rung.Uri,
            Title = rung.Title,
        });
    }

    /// <summary>
    /// Required by <see cref="SectionFeedViewModelBase"/>. Re-fetch the current
    /// URI without touching the rail or breadcrumb stack.
    /// </summary>
    public override Task ReloadAsync()
    {
        var saved = CurrentUri;
        CurrentUri = string.Empty;
        return LoadAsync(new ContentNavigationParameter { Uri = saved, Title = Title, Subtitle = Subtitle });
    }

    /// <summary>
    /// Fetch the all-categories page and populate <see cref="CategoryGroups"/>
    /// preserving Spotify's section breakdown (Arts &amp; Entertainment, etc).
    /// Failure is swallowed (warning log only) — the main view must paint
    /// regardless of this auxiliary fetch's outcome.
    /// </summary>
    private async Task LoadCategoriesRailAsync()
    {
        try
        {
            var page = await _podcastService.GetPodcastBrowsePageAsync(AllCategoriesUri).ConfigureAwait(true);
            if (page is null) return;

            CategoryGroups.Clear();
            foreach (var section in page.Sections)
            {
                if (section.Items.Count == 0) continue;

                var group = new PodcastBrowseCategoryGroup
                {
                    Title = section.Title,
                };
                foreach (var item in section.Items)
                {
                    if (string.IsNullOrEmpty(item.Uri) || string.IsNullOrEmpty(item.Title))
                        continue;
                    group.Chips.Add(new PodcastBrowseCategoryItem
                    {
                        Title = item.Title,
                        Uri = item.Uri,
                    });
                }
                if (group.Chips.Count > 0)
                    CategoryGroups.Add(group);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load podcast categories rail from {Uri}", AllCategoriesUri);
        }
    }

    /// <summary>
    /// Rebuild <see cref="CategoryGroups"/> from a drilled page's own
    /// response. Categorical sections (no show items) become chip groups;
    /// show-bearing sections are left for <see cref="BuildHeroAndShelves"/>.
    /// If the page has no categorical sections the chip rail goes empty
    /// (just the pinned "All" chip remains) — the breadcrumb still works
    /// for back navigation.
    /// </summary>
    private void RebuildChipGroupsFromPage(PodcastBrowsePageDto page)
    {
        CategoryGroups.Clear();
        foreach (var section in page.Sections)
        {
            if (section.Items.Count == 0) continue;
            if (section.HasShows) continue; // belongs in shelves
            // Section has only Category/Section items — emit as a chip group.
            var group = new PodcastBrowseCategoryGroup { Title = section.Title };
            foreach (var item in section.Items)
            {
                if (string.IsNullOrEmpty(item.Uri) || string.IsNullOrEmpty(item.Title))
                    continue;
                group.Chips.Add(new PodcastBrowseCategoryItem
                {
                    Title = item.Title,
                    Uri = item.Uri,
                });
            }
            if (group.Chips.Count > 0)
                CategoryGroups.Add(group);
        }
    }

    /// <summary>
    /// Fetch the Podcast Charts page once and use its response for BOTH the
    /// hero filmstrip (first show-bearing section's top 5) AND the root
    /// content shelves (every show-bearing section). Single network call
    /// drives the entire root visual. Re-fires on every root visit so chart
    /// rankings stay fresh. Failure leaves both surfaces empty — the main
    /// view is unaffected.
    /// </summary>
    private async Task LoadChartsRootAsync()
    {
        try
        {
            var charts = await _podcastService.GetPodcastBrowsePageAsync(PodcastChartsUri).ConfigureAwait(true);
            if (charts is null)
            {
                HeroSlides = [];
                ContentShelves.Clear();
                return;
            }

            var showSections = charts.Sections
                .Where(static s => s.HasShows && s.Items.Count > 0)
                .ToList();

            // Hero: first show-bearing section with ≥3 items.
            var topShows = showSections.FirstOrDefault(static s => s.Items.Count >= 3);
            if (topShows != null)
            {
                HeroSlides = topShows.Items.Take(5).Select(MapToHeroSlide).ToList();
                SelectedHeroImageUrl = topShows.Items
                    .FirstOrDefault(static i => !string.IsNullOrEmpty(i.ImageUrl))?.ImageUrl;
            }
            else
            {
                HeroSlides = [];
            }

            // Shelves: every show-bearing section EXCEPT the one that fed the
            // hero (avoid rendering the same shows twice).
            ContentShelves.Clear();
            foreach (var section in showSections)
            {
                if (topShows != null && ReferenceEquals(section, topShows)) continue;
                ContentShelves.Add(MapSection(section));
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load podcast charts root from {Uri}", PodcastChartsUri);
        }
    }

    /// <summary>
    /// Drilled (non-root) page path. Splits by the number of show-bearing
    /// sections on the page:
    /// <list type="bullet">
    /// <item><b>Exactly one</b> — the page IS a single category-detail surface
    /// (e.g. "Books" → "Popular books podcasts"). The first 5 items feed the
    /// hero filmstrip; everything else lands in a vertical paged grid via
    /// <see cref="PodcastBrowseSectionLayoutKind.Grid"/>. The grid carries
    /// pagination state so the "Show more" button can pull
    /// <c>GetPodcastBrowseSectionAsync</c> for the next chunk on demand.</item>
    /// <item><b>Two or more</b> — first show-bearing section feeds the hero,
    /// remaining ones render as horizontal shelves (existing carousel
    /// behaviour for multi-shelf pages).</item>
    /// </list>
    /// Categorical sections are skipped — they've been promoted into the chip
    /// rail by <see cref="RebuildChipGroupsFromPage"/>.
    /// </summary>
    private void BuildHeroAndShelves(PodcastBrowsePageDto page)
    {
        ContentShelves.Clear();

        var showSections = page.Sections.Where(static s => s.HasShows && s.Items.Count > 0).ToList();

        if (showSections.Count == 0)
        {
            HeroSlides = [];
            return;
        }

        if (showSections.Count == 1)
        {
            // Single-section detail page — hero + paged grid.
            var section = showSections[0];
            HeroSlides = section.Items.Take(5).Select(MapToHeroSlide).ToList();
            SelectedHeroImageUrl = section.Items.FirstOrDefault(static i => !string.IsNullOrEmpty(i.ImageUrl))?.ImageUrl;

            var grid = new PodcastBrowseSection
            {
                Title = section.Title,
                Subtitle = section.Subtitle,
                SectionUri = section.Uri,
                LayoutKind = PodcastBrowseSectionLayoutKind.Grid,
                // Items 5..N go to the grid; items 0..5 are already in the hero.
                Items = new ObservableCollection<PodcastBrowseTile>(
                    section.Items.Skip(5).Select(MapTile)),
                // Server-side offset MUST include the hero items — the next
                // GetPodcastBrowseSectionAsync request starts there.
                LoadedFromServer = section.Items.Count,
                TotalCount = section.TotalCount > 0 ? section.TotalCount : section.Items.Count,
            };
            ContentShelves.Add(grid);
            return;
        }

        // Multi-section page: hero from first show-bearing, rest as shelves.
        var hero = showSections[0];
        HeroSlides = hero.Items.Take(5).Select(MapToHeroSlide).ToList();
        SelectedHeroImageUrl = hero.Items.FirstOrDefault(static i => !string.IsNullOrEmpty(i.ImageUrl))?.ImageUrl;

        for (var i = 1; i < showSections.Count; i++)
            ContentShelves.Add(MapSection(showSections[i]));
    }

    /// <summary>
    /// Fetch the next page of items for a grid-layout section and append
    /// them to its <see cref="PodcastBrowseSection.Items"/> collection.
    /// Updates pagination state so the "Show more" button hides itself once
    /// the server's <c>totalCount</c> is exhausted. Failures are logged and
    /// the loading flag is reset so the user can retry.
    /// </summary>
    public async Task LoadMoreSectionAsync(PodcastBrowseSection grid)
    {
        if (grid is null || grid.IsLoadingMore || !grid.HasMore) return;
        grid.IsLoadingMore = true;
        try
        {
            var dto = await _podcastService.GetPodcastBrowseSectionAsync(
                grid.SectionUri,
                offset: grid.LoadedFromServer,
                limit: 25).ConfigureAwait(true);

            if (dto is null || dto.Items.Count == 0)
            {
                // Server returned nothing despite our HasMore check — treat as
                // end-of-feed to stop the user from spinning forever.
                grid.LoadedFromServer = grid.TotalCount;
                return;
            }

            foreach (var item in dto.Items)
                grid.Items.Add(MapTile(item));

            grid.LoadedFromServer += dto.Items.Count;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to paginate section {Uri}", grid.SectionUri);
        }
        finally
        {
            grid.IsLoadingMore = false;
        }
    }

    /// <summary>
    /// Build a Klankhuis hero slide from a podcast browse tile. Reuses
    /// <see cref="HeroSlideFactory.BuildSlide"/> by wrapping the tile data in
    /// the existing <see cref="HomeSectionItem"/> shape — the factory handles
    /// the Spotify image URL normalisation and accent fallback consistently
    /// with HomePage / BrowsePage carousels.
    /// </summary>
    private static Klankhuis.Hero.Controls.HeroCarouselItem MapToHeroSlide(PodcastBrowseItemDto dto)
    {
        var bridgeItem = new HomeSectionItem
        {
            Uri = dto.Uri,
            Title = dto.Title,
            Subtitle = dto.Subtitle,
            ImageUrl = dto.ImageUrl,
            ColorHex = dto.ColorHex,
        };
        return HeroSlideFactory.BuildSlide(
            bridgeItem,
            eyebrow: "FEATURED",
            primaryCta: "Open",
            secondaryCta: string.Empty,
            primaryCommand: null,
            secondaryCommand: null);
    }

    private void PushBreadcrumb(string? title, string uri)
    {
        if (string.IsNullOrEmpty(uri)) return;
        var displayTitle = string.IsNullOrEmpty(title) ? uri : title!;

        // De-dupe: if the rung at the top of the stack already points to the
        // same URI, don't double it (e.g. RefreshWithParameter re-entering
        // with identical input).
        if (Breadcrumbs.Count > 0 && string.Equals(Breadcrumbs[^1].Uri, uri, StringComparison.Ordinal))
            return;

        // The root rung is special — it's always the first entry. Clear and
        // re-seed if we're navigating to root.
        if (IsRootUri(uri))
        {
            Breadcrumbs.Clear();
        }
        else if (Breadcrumbs.Count == 0)
        {
            // Drilling directly into a section URI without ever visiting root —
            // synthesise the root rung so the breadcrumb stays clickable.
            Breadcrumbs.Add(new BreadcrumbItem { Title = "Podcasts", Uri = RootPodcastsUri });
        }

        Breadcrumbs.Add(new BreadcrumbItem { Title = displayTitle, Uri = uri });
    }

    private static bool IsSectionUri(string uri) =>
        uri.StartsWith("spotify:section:", StringComparison.Ordinal);

    private static bool IsRootUri(string uri) =>
        string.Equals(uri, RootPodcastsUri, StringComparison.Ordinal);

    private void ClearCategorySelection()
    {
        AllCategoriesChip.IsSelected = false;
        foreach (var group in CategoryGroups)
            foreach (var c in group.Chips)
                c.IsSelected = false;
    }

    /// <summary>
    /// Update the chip rail's selected-state to reflect the user's most
    /// recent chip tap. Walks both the pinned "All" chip and every group's
    /// chip collection so exactly one chip can be highlighted at a time.
    /// </summary>
    public void MarkCategorySelected(PodcastBrowseCategoryItem item)
    {
        AllCategoriesChip.IsSelected = ReferenceEquals(AllCategoriesChip, item);
        foreach (var group in CategoryGroups)
            foreach (var c in group.Chips)
                c.IsSelected = ReferenceEquals(c, item);
    }

    private static PodcastBrowseSection MapSection(PodcastBrowseSectionDto dto)
    {
        var section = new PodcastBrowseSection
        {
            Title = dto.Title,
            Subtitle = dto.Subtitle,
            SectionUri = dto.Uri,
            LayoutKind = PickLayoutKind(dto.TypeName),
        };
        foreach (var item in dto.Items)
            section.Items.Add(MapTile(item));
        return section;
    }

    private static PodcastBrowseSectionLayoutKind PickLayoutKind(string? typeName)
    {
        // Spotify's __typename values for browse sections:
        //   BrowseGridSectionData      → categorical tile grid (default look).
        //   BrowseRelatedSectionData   → small CTA pills ("See all categories").
        // Anything else falls through to ArtworkRail until we add a dedicated
        // template.
        return typeName switch
        {
            "BrowseRelatedSectionData" => PodcastBrowseSectionLayoutKind.Cta,
            _                          => PodcastBrowseSectionLayoutKind.ArtworkRail,
        };
    }

    private static PodcastBrowseTile MapTile(PodcastBrowseItemDto dto) => new()
    {
        Title = dto.Title,
        Subtitle = dto.Subtitle,
        ImageUrl = dto.ImageUrl,
        ColorHex = dto.ColorHex,
        NavigationUri = dto.Uri,
    };
}
