using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Wavee.Core.Http.Pathfinder;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Data.Parameters;

namespace Wavee.UI.WinUI.ViewModels;

public sealed partial class HomeViewModel : ObservableObject, ITabBarItemContent
{
    private readonly ILogger? _logger;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string _greeting = "Good morning";

    [ObservableProperty]
    private ObservableCollection<HomeSection> _sections = [];

    [ObservableProperty]
    private ObservableCollection<HomeSectionPref> _sectionPreferences = [];

    [ObservableProperty]
    private int _newSectionCount;

    [ObservableProperty]
    private bool _isCustomizeFlyoutOpen;

    public TabItemParameter? TabItemParameter { get; private set; }

    public event EventHandler<TabItemParameter>? ContentChanged;

    public HomeViewModel(ILogger<HomeViewModel>? logger = null)
    {
        _logger = logger;

        TabItemParameter = new TabItemParameter(Data.Enums.NavigationPageType.Home, null)
        {
            Title = "Home"
        };
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        HasError = false;
        ErrorMessage = null;

        try
        {
            var session = Ioc.Default.GetService<Wavee.Core.Session.ISession>();
            if (session == null || !session.IsConnected())
            {
                UpdateGreeting();
                return;
            }

            var cache = Ioc.Default.GetService<Services.HomeFeedCache>();

            // 1. Serve cached data instantly if available
            if (cache != null && cache.HasData && !cache.IsStale)
            {
                var snapshot = cache.GetCached();
                if (snapshot != null)
                {
                    Greeting = snapshot.Greeting ?? Greeting;
                    var ordered = ApplyPreferences(snapshot.Sections);
                    if (Sections.Count == 0)
                        Sections = new ObservableCollection<HomeSection>(ordered);
                    else
                        Services.HomeFeedCache.ApplyDiff(Sections, ordered, g => Greeting = g ?? Greeting, snapshot.Greeting);
                    return;
                }
            }

            // 2. Fetch fresh data
            if (cache != null)
            {
                var snapshot = await cache.FetchFreshAsync(session);
                Greeting = snapshot.Greeting ?? Greeting;
                var ordered = ApplyPreferences(snapshot.Sections);

                if (Sections.Count == 0)
                    Sections = new ObservableCollection<HomeSection>(ordered);
                else
                    Services.HomeFeedCache.ApplyDiff(Sections, ordered, g => Greeting = g ?? Greeting, snapshot.Greeting);

                // Start background refresh
                cache.StartBackgroundRefresh(session);
            }
            else
            {
                // No cache service — direct fetch
                var response = await session.Pathfinder.GetHomeAsync(sectionItemsLimit: 10);
                var apiGreeting = response.Data?.Home?.Greeting?.TransformedLabel;
                Greeting = apiGreeting ?? Greeting;
                var apiSections = MapSectionsFromResponse(response);
                var ordered = ApplyPreferences(apiSections);
                Sections = new ObservableCollection<HomeSection>(ordered);
            }

            if (string.IsNullOrEmpty(Greeting))
                UpdateGreeting();
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = ex.Message;
            _logger?.LogError(ex, "Failed to load home page content");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Called by HomePage when background refresh completes. Applies diff on UI thread.
    /// </summary>
    public void ApplyBackgroundRefresh(Services.HomeFeedSnapshot snapshot)
    {
        var ordered = ApplyPreferences(snapshot.Sections);
        Services.HomeFeedCache.ApplyDiff(Sections, ordered, g => Greeting = g ?? Greeting, snapshot.Greeting);
    }

    [RelayCommand]
    private async Task RetryAsync()
    {
        HasError = false;
        ErrorMessage = null;
        await LoadAsync();
    }

    private void UpdateGreeting()
    {
        var hour = DateTime.Now.Hour;
        Greeting = hour switch
        {
            < 12 => "Good morning",
            < 18 => "Good afternoon",
            _ => "Good evening"
        };
    }

    // ── Section mapping ──

    internal static List<HomeSection> MapSectionsFromResponse(HomeResponse response)
    {
        var sections = new List<HomeSection>();
        var apiSections = response.Data?.Home?.SectionContainer?.Sections?.Items;
        if (apiSections == null) return sections;

        foreach (var entry in apiSections)
        {
            var sectionType = entry.Data?.TypeName switch
            {
                "HomeShortsSectionData" => HomeSectionType.Shorts,
                "HomeRecentlyPlayedSectionData" => HomeSectionType.RecentlyPlayed,
                "HomeFeedBaselineSectionData" => HomeSectionType.Baseline,
                _ => HomeSectionType.Generic
            };

            var rawTitle = entry.Data?.Title?.TransformedLabel;
            // Fallback title for sections with no name
            var title = !string.IsNullOrWhiteSpace(rawTitle) ? rawTitle : sectionType switch
            {
                HomeSectionType.Shorts => "Quick access",
                HomeSectionType.RecentlyPlayed => "Recently played",
                HomeSectionType.Baseline => entry.Data?.TypeName ?? "Recommended",
                _ => "Untitled section"
            };

            var section = new HomeSection
            {
                Title = title,
                Subtitle = entry.Data?.Subtitle?.TransformedLabel,
                SectionType = sectionType,
                SectionUri = entry.Uri ?? ""
            };

            if (entry.SectionItems?.Items != null)
            {
                foreach (var itemEntry in entry.SectionItems.Items)
                {
                    var item = MapSectionItem(itemEntry);
                    if (item != null)
                        section.Items.Add(item);
                }
            }

            if (section.Items.Count > 0)
                sections.Add(section);
        }

        return sections;
    }

    private static HomeSectionItem? MapSectionItem(HomeSectionItemEntry entry)
    {
        var content = entry.Content;
        if (content == null) return null;

        return content.TypeName switch
        {
            "ArtistResponseWrapper" => MapArtist(entry.Uri, content),
            "PlaylistResponseWrapper" => MapPlaylist(entry.Uri, content),
            "AlbumResponseWrapper" => MapAlbum(entry.Uri, content),
            "PodcastOrAudiobookResponseWrapper" => MapPodcast(entry.Uri, content),
            _ => new HomeSectionItem
            {
                Uri = entry.Uri,
                ContentType = HomeContentType.Unknown
            }
        };
    }

    private static HomeSectionItem? MapArtist(string? uri, HomeItemContent content)
    {
        var data = content.GetArtistData();
        if (data == null) return null;

        var imageUrl = data.Visuals?.AvatarImage?.Sources?
            .OrderByDescending(s => s.Width ?? 0)
            .FirstOrDefault()?.Url;

        var colorHex = data.Visuals?.AvatarImage?.ExtractedColors?.ColorDark?.Hex;

        return new HomeSectionItem
        {
            Uri = data.Uri ?? uri,
            Title = data.Profile?.Name,
            Subtitle = "Artist",
            ImageUrl = imageUrl,
            ContentType = HomeContentType.Artist,
            ColorHex = colorHex
        };
    }

    private static HomeSectionItem? MapPlaylist(string? uri, HomeItemContent content)
    {
        var data = content.GetPlaylistData();
        if (data == null) return null;

        var imageUrl = data.Images?.Items?.FirstOrDefault()?.Sources?
            .OrderByDescending(s => s.Width ?? 0)
            .FirstOrDefault()?.Url;

        var colorHex = data.Images?.Items?.FirstOrDefault()?.ExtractedColors?.ColorDark?.Hex;

        return new HomeSectionItem
        {
            Uri = data.Uri ?? uri,
            Title = data.Name,
            Subtitle = Helpers.SpotifyHtmlHelper.StripHtml(data.Description) is { Length: > 0 } desc
                ? desc
                : data.OwnerV2?.Data?.Name,
            ImageUrl = imageUrl,
            ContentType = HomeContentType.Playlist,
            ColorHex = colorHex
        };
    }

    private static HomeSectionItem? MapAlbum(string? uri, HomeItemContent content)
    {
        var data = content.GetAlbumData();
        if (data == null) return null;

        var imageUrl = data.CoverArt?.Sources?
            .OrderByDescending(s => s.Width ?? 0)
            .FirstOrDefault()?.Url;

        var colorHex = data.CoverArt?.ExtractedColors?.ColorDark?.Hex;
        var artistName = data.Artists?.Items?.FirstOrDefault()?.Profile?.Name;

        return new HomeSectionItem
        {
            Uri = data.Uri ?? uri,
            Title = data.Name,
            Subtitle = artistName ?? "Album",
            ImageUrl = imageUrl,
            ContentType = HomeContentType.Album,
            ColorHex = colorHex
        };
    }

    private static HomeSectionItem? MapPodcast(string? uri, HomeItemContent content)
    {
        var data = content.GetPodcastData();
        if (data == null) return null;

        var imageUrl = data.CoverArt?.Sources?
            .OrderByDescending(s => s.Width ?? 0)
            .FirstOrDefault()?.Url;

        return new HomeSectionItem
        {
            Uri = data.Uri ?? uri,
            Title = data.Name,
            Subtitle = data.Publisher?.Name,
            ImageUrl = imageUrl,
            ContentType = HomeContentType.Podcast
        };
    }

    // ── Section preferences ──

    private List<HomeSection> ApplyPreferences(List<HomeSection> apiSections)
    {
        var settings = Ioc.Default.GetService<ISettingsService>();
        if (settings == null) return apiSections;

        var homeSettings = settings.Settings.HomeSettings;

        if (!homeSettings.Initialized)
        {
            // First load: seed all sections as visible
            homeSettings.Sections = apiSections.Select(s => new HomeSectionPref
            {
                SectionUri = s.SectionUri,
                Title = s.Title,
                IsVisible = true
            }).ToList();
            homeSettings.Initialized = true;
            settings.Update(a => a.HomeSettings = homeSettings);

            SectionPreferences = new ObservableCollection<HomeSectionPref>(homeSettings.Sections);
            NewSectionCount = 0;
            return apiSections;
        }

        // Check for new sections not in preferences
        var knownUris = new HashSet<string>(homeSettings.Sections.Select(s => s.SectionUri));
        var newSections = apiSections.Where(s => !knownUris.Contains(s.SectionUri)).ToList();
        NewSectionCount = newSections.Count;

        // Add new sections to preferences as hidden
        foreach (var ns in newSections)
        {
            homeSettings.Sections.Add(new HomeSectionPref
            {
                SectionUri = ns.SectionUri,
                Title = ns.Title,
                IsVisible = false
            });
        }

        if (newSections.Count > 0)
            settings.Update(a => a.HomeSettings = homeSettings);

        SectionPreferences = new ObservableCollection<HomeSectionPref>(homeSettings.Sections);

        // Build lookup from API sections
        var apiLookup = apiSections.ToDictionary(s => s.SectionUri);

        // Order by preferences, pinned first (after shorts)
        var result = new List<HomeSection>();

        // Shorts always come first, regardless of preferences
        var shorts = apiSections.Where(s => s.SectionType == HomeSectionType.Shorts).ToList();
        result.AddRange(shorts);

        // Then pinned sections
        foreach (var pref in homeSettings.Sections.Where(p => p.IsPinned && p.IsVisible))
        {
            if (apiLookup.TryGetValue(pref.SectionUri, out var section) && section.SectionType != HomeSectionType.Shorts)
                result.Add(section);
        }

        // Then remaining visible sections in preference order
        foreach (var pref in homeSettings.Sections.Where(p => !p.IsPinned && p.IsVisible))
        {
            if (apiLookup.TryGetValue(pref.SectionUri, out var section) && section.SectionType != HomeSectionType.Shorts)
                result.Add(section);
        }

        return result;
    }

    // ── Customization commands ──

    /// <summary>
    /// Sets a section's visibility to a specific value (not a toggle).
    /// Called from the checkbox Checked/Unchecked events to avoid double-toggle bugs.
    /// </summary>
    public void SetSectionVisibility(string sectionUri, bool visible)
    {
        var settings = Ioc.Default.GetService<ISettingsService>();
        if (settings == null) return;

        var pref = settings.Settings.HomeSettings.Sections.FirstOrDefault(s => s.SectionUri == sectionUri);
        if (pref == null || pref.IsVisible == visible) return;

        pref.IsVisible = visible;
        settings.Update(a => { });

        if (!visible)
        {
            var matching = Sections.FirstOrDefault(s => s.SectionUri == sectionUri);
            if (matching != null)
                Sections.Remove(matching);
        }
        else
        {
            // Re-add from cache at the correct position
            var cache = Ioc.Default.GetService<Services.HomeFeedCache>();
            var cachedSections = cache?.GetCached()?.Sections;
            if (cachedSections != null)
            {
                var section = cachedSections.FirstOrDefault(s => s.SectionUri == sectionUri);
                if (section != null)
                {
                    var prefOrder = settings.Settings.HomeSettings.Sections
                        .Where(p => p.IsVisible)
                        .Select(p => p.SectionUri)
                        .ToList();
                    var targetIdx = prefOrder.IndexOf(sectionUri);
                    var insertAt = Math.Min(Math.Max(0, targetIdx), Sections.Count);
                    Sections.Insert(insertAt, section);
                }
            }
        }
    }

    [RelayCommand]
    private void ToggleSectionVisibility(string sectionUri)
    {
        var settings = Ioc.Default.GetService<ISettingsService>();
        var pref = settings?.Settings.HomeSettings.Sections.FirstOrDefault(s => s.SectionUri == sectionUri);
        if (pref != null)
            SetSectionVisibility(sectionUri, !pref.IsVisible);
    }

    [RelayCommand]
    private void ToggleSectionPin(string sectionUri)
    {
        var settings = Ioc.Default.GetService<ISettingsService>();
        if (settings == null) return;

        var pref = settings.Settings.HomeSettings.Sections.FirstOrDefault(s => s.SectionUri == sectionUri);
        if (pref != null)
        {
            pref.IsPinned = !pref.IsPinned;
            settings.Update(a => { });
            SectionPreferences = new ObservableCollection<HomeSectionPref>(settings.Settings.HomeSettings.Sections);
        }
    }

    [RelayCommand]
    private void MoveSectionUp(string sectionUri)
    {
        var settings = Ioc.Default.GetService<ISettingsService>();
        if (settings == null) return;

        var list = settings.Settings.HomeSettings.Sections;
        var idx = list.FindIndex(s => s.SectionUri == sectionUri);
        if (idx > 0)
        {
            (list[idx], list[idx - 1]) = (list[idx - 1], list[idx]);
            settings.Update(a => { });
            SectionPreferences = new ObservableCollection<HomeSectionPref>(list);
        }
    }

    [RelayCommand]
    private void MoveSectionDown(string sectionUri)
    {
        var settings = Ioc.Default.GetService<ISettingsService>();
        if (settings == null) return;

        var list = settings.Settings.HomeSettings.Sections;
        var idx = list.FindIndex(s => s.SectionUri == sectionUri);
        if (idx >= 0 && idx < list.Count - 1)
        {
            (list[idx], list[idx + 1]) = (list[idx + 1], list[idx]);
            settings.Update(a => { });
            SectionPreferences = new ObservableCollection<HomeSectionPref>(list);
        }
    }

    [RelayCommand]
    private async Task ResetSectionPreferencesAsync()
    {
        var settings = Ioc.Default.GetService<ISettingsService>();
        if (settings == null) return;

        settings.Settings.HomeSettings = new HomeSectionSettings();
        settings.Update(a => { });
        await LoadAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        // Force cache to be stale so LoadAsync fetches fresh data
        var cache = Ioc.Default.GetService<Services.HomeFeedCache>();
        cache?.Invalidate();
        await LoadAsync();
    }

    // ── Navigation helpers (called from code-behind) ──

    public static void NavigateToItem(HomeSectionItem item, bool openInNewTab = false)
    {
        if (string.IsNullOrEmpty(item.Uri)) return;

        var parts = item.Uri.Split(':');
        if (parts.Length < 3) return;

        var type = parts[1]; // artist, playlist, album, show, etc.
        var id = item.Uri;   // full URI as ID

        var param = new Data.Parameters.ContentNavigationParameter
        {
            Uri = id,
            Title = item.Title,
            Subtitle = item.Subtitle,
            ImageUrl = item.ImageUrl
        };

        switch (type)
        {
            case "artist":
                Helpers.Navigation.NavigationHelpers.OpenArtist(param, item.Title ?? "Artist", openInNewTab);
                break;
            case "album":
                Helpers.Navigation.NavigationHelpers.OpenAlbum(param, item.Title ?? "Album", openInNewTab);
                break;
            case "playlist":
                Helpers.Navigation.NavigationHelpers.OpenPlaylist(param, item.Title ?? "Playlist", openInNewTab);
                break;
        }
    }
}

public enum HomeSectionType { Shorts, Generic, RecentlyPlayed, Baseline }
public enum HomeContentType { Artist, Playlist, Album, Podcast, Episode, Unknown }

public sealed class HomeSection
{
    public string? Title { get; set; }
    public string? Subtitle { get; set; }
    public HomeSectionType SectionType { get; set; }
    public string SectionUri { get; set; } = "";
    public ObservableCollection<HomeSectionItem> Items { get; set; } = [];
}

public sealed class HomeSectionItem
{
    public string? Uri { get; set; }
    public string? Title { get; set; }
    public string? Subtitle { get; set; }
    public string? ImageUrl { get; set; }
    public HomeContentType ContentType { get; set; }
    public string? ColorHex { get; set; }
}
