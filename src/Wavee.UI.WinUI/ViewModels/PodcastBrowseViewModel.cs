using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Parameters;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// "Podcasts" browse — destination for both the root podcast-browse URI
/// (sidebar entry) and any <c>spotify:section:*</c> URI surfaced by a show's
/// topic chips. Inherits the shared feed-of-shelves base so it reuses
/// <see cref="SectionFeedViewModelBase.Sections"/> and the standard
/// title / subtitle / loading observables that <see cref="SectionShelvesView"/>
/// and the shimmer overlay bind to.
/// </summary>
public sealed partial class PodcastBrowseViewModel : SectionFeedViewModelBase
{
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
    private string _currentUri = string.Empty;

    [ObservableProperty]
    private string? _selectedHeroImageUrl;

    /// <summary>Canonical URI used when the sidebar "Podcasts" entry navigates
    /// here without a parameter. <see cref="LoadAsync"/> falls back to this.</summary>
    public static string RootPodcastsUri { get; set; } = "spotify:internal:pages:podcasts";

    /// <summary>
    /// Called from <c>PodcastBrowsePage.OnEntered</c>. Branches on URI kind:
    /// <c>spotify:section:*</c> hits the single-section endpoint and renders one
    /// shelf; anything else hits the page endpoint and renders the full feed.
    /// </summary>
    public async Task LoadAsync(ContentNavigationParameter? parameter)
    {
        var uri = string.IsNullOrEmpty(parameter?.Uri) ? RootPodcastsUri : parameter!.Uri!;
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
                    Sections.Clear();
                    HasError = true;
                    ErrorMessage = "This topic isn't available right now.";
                    return;
                }

                if (!string.IsNullOrEmpty(section.Title))
                    Title = section.Title;

                Sections.Clear();
                Sections.Add(MapSection(section));
            }
            else
            {
                var page = await _podcastService.GetPodcastBrowsePageAsync(uri).ConfigureAwait(true);
                if (page is null)
                {
                    Sections.Clear();
                    HasError = true;
                    ErrorMessage = "Couldn't load podcast browse.";
                    return;
                }

                if (!string.IsNullOrEmpty(page.Title))
                    Title = page.Title;
                Subtitle = page.Subtitle ?? Subtitle;

                Sections.Clear();
                foreach (var dto in page.Sections)
                {
                    if (dto.Items.Count == 0) continue;
                    Sections.Add(MapSection(dto));
                }

                // First show-bearing shelf's first item drives the page hero
                // image (used by the tab icon via ApplyTabParameter).
                foreach (var dto in page.Sections)
                {
                    if (!dto.HasShows) continue;
                    foreach (var item in dto.Items)
                    {
                        if (item.Kind != PodcastBrowseItemKind.Show) continue;
                        if (!string.IsNullOrEmpty(item.ImageUrl))
                        {
                            SelectedHeroImageUrl ??= item.ImageUrl;
                            break;
                        }
                    }
                    if (!string.IsNullOrEmpty(SelectedHeroImageUrl)) break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "PodcastBrowse load failed for {Uri}", uri);
            Sections.Clear();
            HasError = true;
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Required by <see cref="SectionFeedViewModelBase"/>; delegates to
    /// <see cref="LoadAsync"/> using the current URI so a generic refresh path
    /// (e.g. pull-to-refresh in the future) re-runs the same fetch.</summary>
    public override Task ReloadAsync()
        => LoadAsync(new ContentNavigationParameter { Uri = CurrentUri, Title = Title, Subtitle = Subtitle });

    private static bool IsSectionUri(string uri) =>
        uri.StartsWith("spotify:section:", StringComparison.Ordinal);

    private static HomeSection MapSection(PodcastBrowseSectionDto dto)
    {
        var section = new HomeSection
        {
            Title = dto.Title,
            Subtitle = dto.Subtitle,
            SectionUri = dto.Uri,
            IsPodcastSection = dto.HasShows,
        };
        foreach (var item in dto.Items)
            section.Items.Add(MapItem(item));
        return section;
    }

    private static HomeSectionItem MapItem(PodcastBrowseItemDto dto) => new()
    {
        Uri = dto.Uri,
        Title = dto.Title,
        Subtitle = dto.Subtitle,
        ImageUrl = dto.ImageUrl,
        ColorHex = dto.ColorHex,
    };
}
