using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Wavee.Core.Http.Pathfinder;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Data.Parameters;

namespace Wavee.UI.WinUI.ViewModels;

public sealed partial class HomeViewModel : ObservableObject, ITabBarItemContent, IDisposable
{
    private readonly Wavee.Core.Session.ISession? _session;
    private readonly ISettingsService? _settingsService;
    private readonly Services.HomeFeedCache? _homeFeedCache;
    private readonly Services.RecentlyPlayedService? _recentlyPlayedService;
    private readonly Services.HomeResponseParserFactory _parserFactory;
    private readonly ILogger? _logger;
    private readonly DispatcherQueue _dispatcherQueue;
    private CancellationTokenSource? _baselineEnrichmentCts;
    private int _baselineEnrichmentVersion;
    private bool _isDisposed;

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

    /// <summary>The chips currently displayed in the single row.</summary>
    [ObservableProperty]
    private ObservableCollection<HomeChipViewModel> _displayedChips = [];

    /// <summary>The original main chips (preserved for reverting from sub-chips).</summary>
    private List<HomeChipViewModel>? _mainChips;

    /// <summary>Currently active parent chip when showing sub-chips (null = showing main chips).</summary>
    private HomeChipViewModel? _activeParentChip;

    public TabItemParameter? TabItemParameter { get; private set; }

    public event EventHandler<TabItemParameter>? ContentChanged;

    public HomeViewModel(
        Wavee.Core.Session.ISession? session = null,
        ISettingsService? settingsService = null,
        Services.HomeFeedCache? homeFeedCache = null,
        Services.RecentlyPlayedService? recentlyPlayedService = null,
        Services.HomeResponseParserFactory? parserFactory = null,
        ILogger<HomeViewModel>? logger = null)
    {
        _session = session;
        _settingsService = settingsService;
        _homeFeedCache = homeFeedCache;
        _recentlyPlayedService = recentlyPlayedService;
        _parserFactory = parserFactory ?? new Services.HomeResponseParserFactory();
        _logger = logger;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        if (_recentlyPlayedService != null)
            _recentlyPlayedService.ItemsChanged += OnRecentlyPlayedItemsChanged;

        TabItemParameter = new TabItemParameter(Data.Enums.NavigationPageType.Home, null)
        {
            Title = "Home"
        };
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsLoading) return;
        CancelBaselineEnrichment();
        IsLoading = true;
        HasError = false;
        ErrorMessage = null;

        try
        {
            if (_session == null || !_session.IsConnected())
            {
                UpdateGreeting();
                return;
            }

            // 1. Serve cached data instantly if available
            if (_homeFeedCache != null && _homeFeedCache.HasData && !_homeFeedCache.IsStale)
            {
                var snapshot = _homeFeedCache.GetCached();
                if (snapshot != null)
                {
                    Greeting = snapshot.Greeting ?? Greeting;
                    var ordered = ApplyPreferences(snapshot.Sections);
                    if (Sections.Count == 0)
                        await PopulateSectionsChunkedAsync(ordered);
                    else
                        Services.HomeFeedCache.ApplyDiff(Sections, ordered, g => Greeting = g ?? Greeting, snapshot.Greeting);
                    ApplyChips(snapshot.Chips);
                    BeginBaselineEnrichment();
                    return;
                }
            }

            // 2. Fetch fresh data
            if (_homeFeedCache != null)
            {
                var snapshot = await _homeFeedCache.FetchFreshAsync(_session);
                Greeting = snapshot.Greeting ?? Greeting;
                var ordered = ApplyPreferences(snapshot.Sections);

                if (Sections.Count == 0)
                    await PopulateSectionsChunkedAsync(ordered);
                else
                    Services.HomeFeedCache.ApplyDiff(Sections, ordered, g => Greeting = g ?? Greeting, snapshot.Greeting);

                ApplyChips(snapshot.Chips);
                BeginBaselineEnrichment();

                // Start background refresh
                _homeFeedCache.StartBackgroundRefresh(_session);
            }
            else
            {
                // No cache service — direct fetch
                var response = await _session.Pathfinder.GetHomeAsync(sectionItemsLimit: 10);
                var result = await Task.Run(() => _parserFactory.Parse(response));
                Greeting = result.Greeting ?? Greeting;
                var ordered = ApplyPreferences(result.Sections);
                await PopulateSectionsChunkedAsync(ordered);
                ApplyChips(result.Chips);
                BeginBaselineEnrichment();
            }

            if (string.IsNullOrEmpty(Greeting))
                UpdateGreeting();

            // Fire-and-forget: load real recently played data (replaces stale Home API section)
            if (_recentlyPlayedService != null)
                _ = _recentlyPlayedService.LoadAsync();
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
    /// <summary>
    /// Pause the 5-minute background refresh. Call from the page's
    /// OnNavigatedFrom so the timer doesn't keep hammering Pathfinder
    /// while the user is on another page (that work was a big chunk of
    /// hot-spot #3 in the reactive-infrastructure plan).
    /// </summary>
    public void SuspendBackgroundRefresh() => _homeFeedCache?.SuspendRefresh();

    /// <summary>
    /// Resume the 5-minute background refresh. Call from OnNavigatedTo.
    /// No-op if refresh was never started.
    /// </summary>
    public void ResumeBackgroundRefresh() => _homeFeedCache?.ResumeRefresh();

    /// <summary>
    /// Pause refresh AND release the parsed feed tree so the Home page's
    /// footprint drops while the user is on another page. The raw home-feed
    /// response stays cached in <see cref="Services.HomeFeedCache"/> (SQLite
    /// + in-memory), so coming back via <see cref="ResumeAndRehydrate"/>
    /// rebuilds the parsed sections without a network round-trip.
    ///
    /// Without this, 127 section items + baseline enrichment (preview tracks,
    /// poster URLs, canvas JSON) stay pinned in <see cref="Sections"/> for
    /// the navigation-cached page's entire lifetime — a few MB per Home
    /// visit that never come back under GC.
    /// </summary>
    public void HibernateForNavigation()
    {
        SuspendBackgroundRefresh();
        CancelBaselineEnrichment();
        Sections.Clear();
        DisplayedChips.Clear();
    }

    /// <summary>
    /// Pair with <see cref="HibernateForNavigation"/>: rebuild sections from
    /// the cached raw feed and resume the background refresh.
    /// </summary>
    public void ResumeAndRehydrate()
    {
        ResumeBackgroundRefresh();
        if (_homeFeedCache?.GetCached() is { } snapshot)
            ApplyBackgroundRefresh(snapshot);
    }

    public void ApplyBackgroundRefresh(Services.HomeFeedSnapshot snapshot)
    {
        CancelBaselineEnrichment();
        var ordered = ApplyPreferences(snapshot.Sections);
        Services.HomeFeedCache.ApplyDiff(Sections, ordered, g => Greeting = g ?? Greeting, snapshot.Greeting);
        ApplyChips(snapshot.Chips);
        BeginBaselineEnrichment();
    }

    /// <summary>
    /// Populates the bound <see cref="Sections"/> collection in small chunks, yielding
    /// to the dispatcher between chunks so the layout engine can paint each chunk's cards
    /// and handle input (scroll, click) before the next batch lands.
    ///
    /// <para>
    /// Without this, assigning a 31-section collection in one shot triggers a single
    /// massive layout pass that realizes all ~4,650 card elements before the UI thread
    /// yields — the root cause of the "heavy" navigation feel. Yielding every
    /// <paramref name="chunkSize"/> sections spreads that work across several frames
    /// so the user sees content stream in progressively.
    /// </para>
    ///
    /// <para>
    /// Uses <see cref="Task.Yield"/> rather than <c>DispatcherQueue.TryEnqueue</c> because
    /// an async method resumed on the UI thread via <c>Task.Yield</c> posts a continuation
    /// at normal dispatcher priority — equivalent to a message-pump tick — which is exactly
    /// what we need to let WinUI paint between chunks.
    /// </para>
    /// </summary>
    private async Task PopulateSectionsChunkedAsync(IList<HomeSection> ordered, int chunkSize = 4)
    {
        // Start from empty: we only call this when Sections.Count == 0, but guard anyway.
        if (Sections.Count > 0) Sections.Clear();

        for (int i = 0; i < ordered.Count; i++)
        {
            Sections.Add(ordered[i]);

            // Yield back to the dispatcher every chunkSize items so realization cost
            // is spread across multiple frames instead of a single 4,650-element burst.
            if ((i + 1) % chunkSize == 0 && i + 1 < ordered.Count)
                await Task.Yield();
        }
    }

    private void ApplyChips(List<HomeChipViewModel>? chips)
    {
        // Only update chips when we receive them (unfaceted responses)
        if (chips == null || chips.Count == 0 || DisplayedChips.Count > 0) return;

        _mainChips = chips;
        _activeParentChip = null;

        // "All" chip starts selected
        foreach (var c in chips) c.IsSelected = string.IsNullOrEmpty(c.Id);
        DisplayedChips = new ObservableCollection<HomeChipViewModel>(chips);
    }

    private void OnRecentlyPlayedItemsChanged()
    {
        if (_isDisposed || _recentlyPlayedService == null) return;

        // Must dispatch to UI thread — this event can fire from background threads
        // and ObservableCollection mutations must happen on the UI thread.
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (_isDisposed) return;

            var items = _recentlyPlayedService.Items;
            if (items.Count == 0) return;

            // Find existing "Recently played" section or create one
            var existing = Sections.FirstOrDefault(s => s.SectionType == HomeSectionType.RecentlyPlayed);
            if (existing != null)
            {
                existing.Items.Clear();
                foreach (var item in items)
                    existing.Items.Add(item);
            }
            else
            {
                var section = new HomeSection
                {
                    Title = "Recently played",
                    SectionType = HomeSectionType.RecentlyPlayed,
                    SectionUri = "recently-played"
                };
                foreach (var item in items)
                    section.Items.Add(item);

                // Insert after Shorts if present, otherwise at index 0
                var insertIdx = Sections.Count > 0 && Sections[0].SectionType == HomeSectionType.Shorts ? 1 : 0;
                Sections.Insert(insertIdx, section);
            }
        });
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

        var rawSections = Services.HomeRawJsonHelper.GetRawSectionJsonByIndex(response);
        var sectionIndex = -1;

        foreach (var entry in apiSections)
        {
            sectionIndex++;
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
                SectionUri = entry.Uri ?? "",
                RawSpotifyJson = sectionIndex < rawSections.Count ? rawSections[sectionIndex] : null
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

        var result = content.TypeName switch
        {
            "ArtistResponseWrapper" => MapArtist(entry.Uri, content),
            "PlaylistResponseWrapper" => MapPlaylist(entry.Uri, content),
            "AlbumResponseWrapper" => MapAlbum(entry.Uri, content),
            "PodcastOrAudiobookResponseWrapper" => MapPodcast(entry.Uri, content),
            _ => (HomeSectionItem?)null
        };

        // If typed deserialization failed or returned incomplete data, try raw JsonElement extraction
        if (result == null || result.Title == null)
        {
            var hasData = content.Data.HasValue;
            var kind = hasData ? content.Data!.Value.ValueKind : (System.Text.Json.JsonValueKind?)null;
            System.Diagnostics.Debug.WriteLine(
                $"[MapSectionItem] Fallback for {entry.Uri}: result={result != null}, title={result?.Title}, hasData={hasData}, kind={kind}");

            if (hasData && content.Data!.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                // Skip items the API marks as "NotFound" — not available for this platform
                if (content.Data!.Value.TryGetProperty("__typename", out var tn)
                    && tn.GetString() == "NotFound")
                {
                    System.Diagnostics.Debug.WriteLine($"[MapSectionItem] Skipping NotFound item: {entry.Uri}");
                    return null;
                }

                result ??= new HomeSectionItem { Uri = entry.Uri, ContentType = InferContentType(entry.Uri) };
                EnrichFromRawJson(result, content.Data!.Value);
            }
        }

        return result ?? MapUnknownType(entry.Uri);
    }

    /// <summary>
    /// Extracts common fields directly from the raw JsonElement when typed deserialization fails.
    /// </summary>
    private static void EnrichFromRawJson(HomeSectionItem item, System.Text.Json.JsonElement raw)
    {
        if (raw.ValueKind != System.Text.Json.JsonValueKind.Object)
            return;

        // Diagnostic: log the actual properties in the JsonElement
        System.Diagnostics.Debug.WriteLine(
            $"[EnrichFromRawJson] uri={item.Uri}, rawText={raw.GetRawText()[..Math.Min(200, raw.GetRawText().Length)]}");

        if (item.Title == null && raw.TryGetProperty("name", out var name))
            item.Title = name.GetString();

        if (item.Uri == null && raw.TryGetProperty("uri", out var uri))
            item.Uri = uri.GetString();

        if (item.ImageUrl == null)
            item.ImageUrl = ExtractImageUrlFromJson(raw);

        if (item.Subtitle == null && raw.TryGetProperty("description", out var desc))
        {
            var descStr = Helpers.SpotifyHtmlHelper.StripHtml(desc.GetString());
            if (!string.IsNullOrEmpty(descStr))
                item.Subtitle = descStr;
        }

        if (item.ColorHex == null)
            item.ColorHex = ExtractColorFromJson(raw);

        // If top-level extraction found nothing, try nested "data" wrapper (double-wrapped items)
        if (item.Title == null && raw.TryGetProperty("data", out var nested)
            && nested.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            EnrichFromRawJson(item, nested);
        }
    }

    private static string? ExtractImageUrlFromJson(System.Text.Json.JsonElement raw)
    {
        if (raw.ValueKind != System.Text.Json.JsonValueKind.Object)
            return null;

        // Playlist: images.items[0].sources
        if (raw.TryGetProperty("images", out var images)
            && images.TryGetProperty("items", out var items)
            && items.ValueKind == System.Text.Json.JsonValueKind.Array
            && items.GetArrayLength() > 0)
        {
            var url = GetLargestSourceUrl(items[0]);
            if (url != null) return url;
        }

        // Album/Podcast: coverArt.sources
        if (raw.TryGetProperty("coverArt", out var coverArt))
        {
            var url = GetLargestSourceUrl(coverArt);
            if (url != null) return url;
        }

        // Artist: visuals.avatarImage.sources
        if (raw.TryGetProperty("visuals", out var visuals)
            && visuals.TryGetProperty("avatarImage", out var avatar))
        {
            var url = GetLargestSourceUrl(avatar);
            if (url != null) return url;
        }

        return null;
    }

    private static string? GetLargestSourceUrl(System.Text.Json.JsonElement container)
    {
        if (container.ValueKind != System.Text.Json.JsonValueKind.Object)
            return null;

        if (!container.TryGetProperty("sources", out var sources)
            || sources.ValueKind != System.Text.Json.JsonValueKind.Array
            || sources.GetArrayLength() == 0)
            return null;

        string? bestUrl = null;
        int maxWidth = -1;
        foreach (var source in sources.EnumerateArray())
        {
            if (source.ValueKind != System.Text.Json.JsonValueKind.Object)
                continue;

            var width = source.TryGetProperty("width", out var w) && w.ValueKind == System.Text.Json.JsonValueKind.Number
                ? w.GetInt32() : 0;
            if (width > maxWidth || bestUrl == null)
            {
                maxWidth = width;
                bestUrl = source.TryGetProperty("url", out var url) ? url.GetString() : null;
            }
        }
        return bestUrl;
    }

    private static string? ExtractColorFromJson(System.Text.Json.JsonElement raw)
    {
        if (raw.ValueKind != System.Text.Json.JsonValueKind.Object)
            return null;

        // Try images.items[0].extractedColors.colorDark.hex
        if (raw.TryGetProperty("images", out var images)
            && images.TryGetProperty("items", out var items)
            && items.ValueKind == System.Text.Json.JsonValueKind.Array
            && items.GetArrayLength() > 0
            && items[0].ValueKind == System.Text.Json.JsonValueKind.Object
            && items[0].TryGetProperty("extractedColors", out var ec)
            && ec.ValueKind == System.Text.Json.JsonValueKind.Object
            && ec.TryGetProperty("colorDark", out var cd)
            && cd.ValueKind == System.Text.Json.JsonValueKind.Object
            && cd.TryGetProperty("hex", out var hex))
            return hex.GetString();

        // Try coverArt.extractedColors.colorDark.hex
        if (raw.TryGetProperty("coverArt", out var coverArt)
            && coverArt.ValueKind == System.Text.Json.JsonValueKind.Object
            && coverArt.TryGetProperty("extractedColors", out var ec2)
            && ec2.ValueKind == System.Text.Json.JsonValueKind.Object
            && ec2.TryGetProperty("colorDark", out var cd2)
            && cd2.ValueKind == System.Text.Json.JsonValueKind.Object
            && cd2.TryGetProperty("hex", out var hex2))
            return hex2.GetString();

        return null;
    }

    private static HomeContentType InferContentType(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return HomeContentType.Unknown;
        if (uri.Contains(":playlist:", StringComparison.Ordinal)) return HomeContentType.Playlist;
        if (uri.Contains(":album:", StringComparison.Ordinal)) return HomeContentType.Album;
        if (uri.Contains(":artist:", StringComparison.Ordinal)) return HomeContentType.Artist;
        if (uri.Contains(":show:", StringComparison.Ordinal)) return HomeContentType.Podcast;
        if (uri.Contains(":episode:", StringComparison.Ordinal)) return HomeContentType.Episode;
        return HomeContentType.Unknown;
    }

    private static HomeSectionItem? MapUnknownType(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return null;

        if (uri.Contains(":collection", StringComparison.OrdinalIgnoreCase))
        {
            return new HomeSectionItem
            {
                Uri = uri,
                Title = "Liked Songs",
                ContentType = HomeContentType.Playlist
            };
        }

        var parts = uri.Split(':');
        if (parts.Length < 2) return null;

        var type = parts[1];
        return new HomeSectionItem
        {
            Uri = uri,
            Title = type switch
            {
                "artist" => "Artist",
                "album" => "Album",
                "playlist" => "Playlist",
                _ => null
            },
            ContentType = type switch
            {
                "artist" => HomeContentType.Artist,
                "album" => HomeContentType.Album,
                "playlist" => HomeContentType.Playlist,
                _ => HomeContentType.Unknown
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
        // Customization removed — pass through directly
        return apiSections;

        var settings = _settingsService;
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
        var settings = _settingsService;
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
            var cache = _homeFeedCache;
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
        var settings = _settingsService;
        var pref = settings?.Settings.HomeSettings.Sections.FirstOrDefault(s => s.SectionUri == sectionUri);
        if (pref != null)
            SetSectionVisibility(sectionUri, !pref.IsVisible);
    }

    [RelayCommand]
    private void ToggleSectionPin(string sectionUri)
    {
        var settings = _settingsService;
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
        var settings = _settingsService;
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
        var settings = _settingsService;
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
        var settings = _settingsService;
        if (settings == null) return;

        settings.Settings.HomeSettings = new HomeSectionSettings();
        settings.Update(a => { });
        await LoadAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        // Force cache to be stale so LoadAsync fetches fresh data
        var cache = _homeFeedCache;
        cache?.Invalidate();
        await LoadAsync();
    }

    // ── Chip selection ──

    [RelayCommand]
    private async Task SelectChipAsync(HomeChipViewModel? chip)
    {
        if (chip == null) return;

        System.Diagnostics.Debug.WriteLine($"[SelectChipAsync] chip={chip.Label}, id={chip.Id}, isBack={chip.IsBackChip}");

        // Back chip → revert to main chips, refetch with no facet
        if (chip.IsBackChip)
        {
            _activeParentChip = null;
            if (_mainChips != null)
            {
                // Select "All" chip
                foreach (var c in _mainChips) c.IsSelected = string.IsNullOrEmpty(c.Id);
                DisplayedChips = new ObservableCollection<HomeChipViewModel>(_mainChips);
            }
            await RefetchWithFacet(null);
            return;
        }

        // "All" chip → refetch with no facet, stay on main chips
        if (string.IsNullOrEmpty(chip.Id))
        {
            _activeParentChip = null;
            foreach (var c in DisplayedChips) c.IsSelected = c == chip;
            await RefetchWithFacet(null);
            return;
        }

        // Parent chip with sub-chips → morph into sub-chips row
        if (chip.SubChips is { Count: > 0 })
        {
            _activeParentChip = chip;

            // Build morphed row: [✕ Parent] [Sub1] [Sub2] ...
            var backChip = new HomeChipViewModel
            {
                Id = chip.Id,
                Label = chip.Label,
                IsBackChip = true,
                IsSelected = true
            };

            var morphed = new ObservableCollection<HomeChipViewModel> { backChip };
            foreach (var sc in chip.SubChips)
            {
                sc.IsSelected = false;
                morphed.Add(sc);
            }

            DisplayedChips = morphed;
            await RefetchWithFacet(chip.Id);
            return;
        }

        // Regular chip (no sub-chips) → select it, refetch
        foreach (var c in DisplayedChips) c.IsSelected = c == chip;
        await RefetchWithFacet(chip.Id);
    }

    private async Task RefetchWithFacet(string? facet)
    {
        if (_homeFeedCache == null || _session == null || !_session.IsConnected()) return;

        CancelBaselineEnrichment();
        _homeFeedCache.CurrentFacet = string.IsNullOrEmpty(facet) ? null : facet;
        _homeFeedCache.Invalidate();

        System.Diagnostics.Debug.WriteLine($"[RefetchWithFacet] facet={facet ?? "(null)"}, cache invalidated, about to fetch");
        _logger?.LogDebug("Refetching home with facet: {Facet}", facet ?? "(none)");

        // Manage IsLoading directly — bypasses LoadAsync's guard to ensure the refetch always runs
        IsLoading = true;
        HasError = false;
        ErrorMessage = null;

        try
        {
            var snapshot = await _homeFeedCache.FetchFreshAsync(_session);
            System.Diagnostics.Debug.WriteLine($"[RefetchWithFacet] Got {snapshot.Sections.Count} sections, greeting={snapshot.Greeting}");
            Greeting = snapshot.Greeting ?? Greeting;
            var ordered = ApplyPreferences(snapshot.Sections);

            if (Sections.Count == 0)
                Sections = new ObservableCollection<HomeSection>(ordered);
            else
                Services.HomeFeedCache.ApplyDiff(Sections, ordered,
                    g => Greeting = g ?? Greeting, snapshot.Greeting);

            System.Diagnostics.Debug.WriteLine($"[RefetchWithFacet] After diff: {Sections.Count} sections displayed");
            BeginBaselineEnrichment();

            if (string.IsNullOrEmpty(Greeting))
                UpdateGreeting();

            if (_recentlyPlayedService != null)
                _ = _recentlyPlayedService.LoadAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RefetchWithFacet] ERROR: {ex.Message}");
            HasError = true;
            ErrorMessage = ex.Message;
            _logger?.LogError(ex, "Failed to refetch with facet {Facet}", facet);
        }
        finally
        {
            IsLoading = false;
        }
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
            case "user" when item.Uri.Contains(":collection", StringComparison.OrdinalIgnoreCase):
                Helpers.Navigation.NavigationHelpers.OpenLikedSongs(openInNewTab);
                break;
        }
    }

    // ── Baseline enrichment ──

    private void CancelBaselineEnrichment()
    {
        _baselineEnrichmentVersion++;
        _baselineEnrichmentCts?.Cancel();
        _baselineEnrichmentCts?.Dispose();
        _baselineEnrichmentCts = null;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_recentlyPlayedService != null)
            _recentlyPlayedService.ItemsChanged -= OnRecentlyPlayedItemsChanged;

        CancelBaselineEnrichment();
    }

    private void BeginBaselineEnrichment()
    {
        if (_session == null || !_session.IsConnected()) return;

        CancelBaselineEnrichment();

        var baselineItems = Sections
            .Where(section => section.SectionType == HomeSectionType.Baseline)
            .SelectMany(section => section.Items)
            .Where(item => !item.HasBaselinePreview
                           && !string.IsNullOrWhiteSpace(item.Uri)
                           && item.ContentType is HomeContentType.Playlist or HomeContentType.Album)
            .ToList();

        if (baselineItems.Count == 0)
        {
            ClearLoadingForBaselineItems(Sections);
            return;
        }

        foreach (var item in baselineItems)
            item.IsBaselineLoading = true;

        var uris = baselineItems
            .Select(item => item.Uri!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var version = ++_baselineEnrichmentVersion;
        var cts = new CancellationTokenSource();
        _baselineEnrichmentCts = cts;
        _ = EnrichBaselineItemsAsync(uris, version, cts.Token);
    }

    private async Task EnrichBaselineItemsAsync(List<string> uris, int version, CancellationToken ct)
    {
        try
        {
            var response = await _session!.Pathfinder.GetFeedBaselineLookupAsync(uris, ct).ConfigureAwait(false);
            var lookup = BuildBaselineEnrichmentLookup(response);

            _dispatcherQueue.TryEnqueue(() =>
            {
                if (ct.IsCancellationRequested || version != _baselineEnrichmentVersion)
                    return;

                ApplyBaselineEnrichment(Sections, lookup);

                var cached = _homeFeedCache?.GetCached();
                if (cached != null)
                    ApplyBaselineEnrichment(cached.Sections, lookup);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to enrich home baseline sections");
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (version == _baselineEnrichmentVersion)
                    ClearLoadingForBaselineItems(Sections);
            });
        }
    }

    private static Dictionary<string, HomeBaselineEnrichment> BuildBaselineEnrichmentLookup(
        FeedBaselineLookupResponse response)
    {
        var result = new Dictionary<string, HomeBaselineEnrichment>(StringComparer.Ordinal);
        var entries = response.Data?.Lookup;
        if (entries == null) return result;

        foreach (var entry in entries)
        {
            var previewItems = entry.TypeName switch
            {
                "PlaylistResponseWrapper" => entry.GetPlaylistData()?.PreviewItems,
                "AlbumResponseWrapper" => entry.GetAlbumData()?.PreviewItems,
                _ => null
            };

            var tracks = previewItems?.Items?
                .Select(wrapper => wrapper.Data)
                .Where(track => track != null)
                .Select(track => MapBaselinePreviewTrack(track!))
                .Where(track => !string.IsNullOrWhiteSpace(track.Uri) || !string.IsNullOrWhiteSpace(track.Name))
                .ToList() ?? [];

            var uri = entry.TypeName switch
            {
                "PlaylistResponseWrapper" => entry.GetPlaylistData()?.Uri ?? entry.Uri,
                "AlbumResponseWrapper" => entry.GetAlbumData()?.Uri ?? entry.Uri,
                _ => entry.Uri
            };

            if (string.IsNullOrWhiteSpace(uri))
                continue;

            var primary = tracks.FirstOrDefault();
            result[uri] = new HomeBaselineEnrichment(
                uri,
                tracks,
                primary?.CanvasThumbnailUrl ?? primary?.CoverArtUrl,
                primary?.ColorHex,
                primary?.CanvasUrl,
                primary?.CanvasThumbnailUrl,
                primary?.AudioPreviewUrl);
        }

        return result;
    }

    private static HomeBaselinePreviewTrack MapBaselinePreviewTrack(FeedBaselineTrackData track)
    {
        var cover = track.AlbumOfTrack?.CoverArt;
        var coverUrl = cover?.Sources?
            .OrderByDescending(source => source.Width ?? 0)
            .FirstOrDefault()?.Url;

        var canvasThumbnail = PickCanvasThumbnail(track.Canvas?.Thumbnail?.Sources);

        return new HomeBaselinePreviewTrack
        {
            Uri = track.Uri,
            Name = track.Name,
            CoverArtUrl = coverUrl,
            ColorHex = cover?.ExtractedColors?.ColorDark?.Hex,
            CanvasUrl = track.Canvas?.Url,
            CanvasThumbnailUrl = canvasThumbnail,
            AudioPreviewUrl = track.Previews?.AudioPreviews?.Items?.FirstOrDefault()?.Url
        };
    }

    private static string? PickCanvasThumbnail(IReadOnlyList<FeedBaselineCanvasThumbnailSource>? sources)
    {
        if (sources == null || sources.Count == 0) return null;

        return sources.FirstOrDefault(source =>
                   source.Url?.Contains("288x512", StringComparison.OrdinalIgnoreCase) == true)?.Url
               ?? sources.LastOrDefault(source => !string.IsNullOrWhiteSpace(source.Url))?.Url
               ?? sources.FirstOrDefault()?.Url;
    }

    private static void ApplyBaselineEnrichment(
        IEnumerable<HomeSection> sections,
        IReadOnlyDictionary<string, HomeBaselineEnrichment> lookup)
    {
        foreach (var item in sections
                     .Where(section => section.SectionType == HomeSectionType.Baseline)
                     .SelectMany(section => section.Items))
        {
            if (item.Uri != null && lookup.TryGetValue(item.Uri, out var enrichment))
            {
                item.PreviewTracks = enrichment.PreviewTracks;
                item.HeroImageUrl = enrichment.HeroImageUrl ?? item.ImageUrl;
                item.HeroColorHex = enrichment.HeroColorHex ?? item.ColorHex;
                item.CanvasUrl = enrichment.CanvasUrl;
                item.CanvasThumbnailUrl = enrichment.CanvasThumbnailUrl;
                item.AudioPreviewUrl = enrichment.AudioPreviewUrl;
                item.HasBaselinePreview = enrichment.PreviewTracks.Count > 0;
            }
            else
            {
                item.HeroImageUrl ??= item.ImageUrl;
                item.HeroColorHex ??= item.ColorHex;
            }

            item.IsBaselineLoading = false;
        }
    }

    private static void ClearLoadingForBaselineItems(IEnumerable<HomeSection> sections)
    {
        foreach (var item in sections
                     .Where(section => section.SectionType == HomeSectionType.Baseline)
                     .SelectMany(section => section.Items))
        {
            item.HeroImageUrl ??= item.ImageUrl;
            item.HeroColorHex ??= item.ColorHex;
            item.IsBaselineLoading = false;
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
    public string? RawSpotifyJson { get; set; }

#if DEBUG
    public bool IsDebugVisible => true;
#else
    public bool IsDebugVisible => false;
#endif

    /// <summary>
    /// Header entity name (e.g. artist name for "More like X" sections).
    /// </summary>
    public string? HeaderEntityName { get; set; }

    /// <summary>
    /// Header entity image (e.g. artist avatar for "More like X" sections).
    /// </summary>
    public string? HeaderEntityImageUrl { get; set; }

    /// <summary>
    /// Header entity URI for navigation.
    /// </summary>
    public string? HeaderEntityUri { get; set; }
}

public sealed class HomeSectionItem : ObservableObject
{
    private string? _uri;
    private string? _title;
    private string? _subtitle;
    private string? _imageUrl;
    private HomeContentType _contentType;
    private string? _colorHex;
    private string? _placeholderGlyph;
    private bool _isBaselineLoading;
    private bool _hasBaselinePreview;
    private string? _heroImageUrl;
    private string? _heroColorHex;
    private string? _canvasUrl;
    private string? _canvasThumbnailUrl;
    private string? _audioPreviewUrl;
    private string? _baselineGroupTitle;
    private List<HomeBaselinePreviewTrack> _previewTracks = [];

    public string? Uri
    {
        get => _uri;
        set => SetProperty(ref _uri, value);
    }

    public string? Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string? Subtitle
    {
        get => _subtitle;
        set => SetProperty(ref _subtitle, value);
    }

    public string? ImageUrl
    {
        get => _imageUrl;
        set => SetProperty(ref _imageUrl, value);
    }

    public HomeContentType ContentType
    {
        get => _contentType;
        set => SetProperty(ref _contentType, value);
    }

    public string? ColorHex
    {
        get => _colorHex;
        set => SetProperty(ref _colorHex, value);
    }

    public string? PlaceholderGlyph
    {
        get => _placeholderGlyph;
        set => SetProperty(ref _placeholderGlyph, value);
    }

    public bool IsBaselineLoading
    {
        get => _isBaselineLoading;
        set => SetProperty(ref _isBaselineLoading, value);
    }

    public bool HasBaselinePreview
    {
        get => _hasBaselinePreview;
        set => SetProperty(ref _hasBaselinePreview, value);
    }

    public string? HeroImageUrl
    {
        get => _heroImageUrl;
        set => SetProperty(ref _heroImageUrl, value);
    }

    public string? HeroColorHex
    {
        get => _heroColorHex;
        set => SetProperty(ref _heroColorHex, value);
    }

    public string? CanvasUrl
    {
        get => _canvasUrl;
        set => SetProperty(ref _canvasUrl, value);
    }

    public string? CanvasThumbnailUrl
    {
        get => _canvasThumbnailUrl;
        set => SetProperty(ref _canvasThumbnailUrl, value);
    }

    public string? AudioPreviewUrl
    {
        get => _audioPreviewUrl;
        set => SetProperty(ref _audioPreviewUrl, value);
    }

    public string? BaselineGroupTitle
    {
        get => _baselineGroupTitle;
        set => SetProperty(ref _baselineGroupTitle, value);
    }

    public List<HomeBaselinePreviewTrack> PreviewTracks
    {
        get => _previewTracks;
        set => SetProperty(ref _previewTracks, value);
    }
}

internal sealed record HomeBaselineEnrichment(
    string Uri,
    List<HomeBaselinePreviewTrack> PreviewTracks,
    string? HeroImageUrl,
    string? HeroColorHex,
    string? CanvasUrl,
    string? CanvasThumbnailUrl,
    string? AudioPreviewUrl);

public sealed class HomeBaselinePreviewTrack
{
    public string? Uri { get; init; }
    public string? Name { get; init; }
    public string? CoverArtUrl { get; init; }
    public string? ColorHex { get; init; }
    public string? CanvasUrl { get; init; }
    public string? CanvasThumbnailUrl { get; init; }
    public string? AudioPreviewUrl { get; init; }
}

public sealed partial class HomeChipViewModel : ObservableObject
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public List<HomeChipViewModel> SubChips { get; set; } = [];

    /// <summary>True for the "✕ Parent" chip that reverts to main chips.</summary>
    public bool IsBackChip { get; set; }

    [ObservableProperty]
    private bool _isSelected;
}
