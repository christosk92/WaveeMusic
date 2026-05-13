using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Wavee.UI.Contracts;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Extensions;
using Wavee.UI.WinUI.Helpers.Navigation;

namespace Wavee.UI.WinUI.ViewModels;

public enum PodcastLibraryStage
{
    Shows,
    Episodes
}

public enum PodcastEpisodeScope
{
    Saved,
    Latest
}

public sealed partial class YourEpisodesViewModel : ObservableObject, IDisposable
{
    private const string PodcastShowsColumnWidthKey = "podcasts.showsColumn";
    private const string PodcastEpisodesColumnWidthKey = "podcasts.episodesColumn";
    private const string PodcastDetailsColumnWidthKey = "podcasts.detailsColumn";
    private const string PodcastPreferencesTabKey = "podcasts";
    private const string PodcastLatestScopePreference = "Latest";
    private const string PodcastSavedScopePreference = "Saved";
    private const string AllPodcastsShowId = "wavee:podcasts:all";
    private const string RecentlyPlayedShowId = "wavee:pseudo:recently-played-podcasts";
    private const int PodcastArchivePreviewLimit = 50;

    private readonly ILibraryDataService _libraryDataService;
    private readonly IPodcastService? _podcastService;
    private readonly ISettingsService? _settingsService;
    private readonly IPlaybackStateService _playbackStateService;
    private readonly ILogger? _logger;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly List<LibraryEpisodeDto> _allEpisodes = [];
    private readonly List<LibraryEpisodeDto> _recentEpisodes = [];
    private readonly object _showArchivePreviewCacheGate = new();
    private readonly Dictionary<string, ShowArchivePreview> _showArchivePreviewCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ShowDetailDto> _showDetailCache = new(StringComparer.Ordinal);
    private CancellationTokenSource? _episodeDetailCts;
    private CancellationTokenSource? _episodeProgressCts;
    private CancellationTokenSource? _showArchiveCts;
    private PodcastEpisodeScope _podcastEpisodeScope = PodcastEpisodeScope.Latest;
    private bool _disposed;
    private bool _syncAlreadyRequested;
    private bool _pendingAutoSelectFirstEpisode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInitialLoading))]
    [NotifyPropertyChangedFor(nameof(ShowEpisodeEmptyState))]
    [NotifyPropertyChangedFor(nameof(ShowEpisodesShimmer))]
    [NotifyPropertyChangedFor(nameof(ShowLoadError))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLoadError))]
    [NotifyPropertyChangedFor(nameof(ShowEpisodeEmptyState))]
    private bool _hasError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoadErrorDescription))]
    private string? _errorMessage;

    [ObservableProperty]
    private int _totalPodcasts;

    [ObservableProperty]
    private int _totalEpisodes;

    [ObservableProperty]
    private string _totalDuration = "";

    [ObservableProperty]
    private string _searchQuery = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortDirectionGlyph))]
    private LibrarySortDirection _sortDirection = LibrarySortDirection.Descending;

    [ObservableProperty]
    private LibrarySortBy _sortBy = LibrarySortBy.RecentlyAdded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEpisodeGroupHeaders))]
    private LibraryPodcastShowDto? _selectedShow;

    [ObservableProperty]
    private string _selectedShowName = "Podcasts";

    [ObservableProperty]
    private string _selectedShowMetadata = "";

    [ObservableProperty]
    private string? _selectedShowImageUrl;

    [ObservableProperty]
    private string? _selectedShowDescription;

    [ObservableProperty]
    private string _selectedShowPlaceholderGlyph = "\uEC05";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedEpisode))]
    [NotifyPropertyChangedFor(nameof(SelectedEpisodeTitle))]
    [NotifyPropertyChangedFor(nameof(SelectedEpisodeImageUrl))]
    [NotifyPropertyChangedFor(nameof(SelectedEpisodeShowName))]
    [NotifyPropertyChangedFor(nameof(SelectedEpisodeMetadata))]
    [NotifyPropertyChangedFor(nameof(SelectedEpisodeMetaLine))]
    [NotifyPropertyChangedFor(nameof(SelectedEpisodeDescription))]
    [NotifyPropertyChangedFor(nameof(HasSelectedEpisodeDescription))]
    [NotifyPropertyChangedFor(nameof(SelectedEpisodeDurationFormatted))]
    [NotifyPropertyChangedFor(nameof(HasShowName))]
    [NotifyPropertyChangedFor(nameof(HasExplicit))]
    [NotifyCanExecuteChangedFor(nameof(CopyEpisodeLinkCommand))]
    private LibraryEpisodeDto? _selectedEpisode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedEpisodeTitle))]
    [NotifyPropertyChangedFor(nameof(SelectedEpisodeImageUrl))]
    [NotifyPropertyChangedFor(nameof(SelectedEpisodeShowName))]
    [NotifyPropertyChangedFor(nameof(SelectedEpisodeMetadata))]
    [NotifyPropertyChangedFor(nameof(SelectedEpisodeMetaLine))]
    [NotifyPropertyChangedFor(nameof(SelectedEpisodeDescription))]
    [NotifyPropertyChangedFor(nameof(HasSelectedEpisodeDescription))]
    [NotifyPropertyChangedFor(nameof(SelectedEpisodeAvailability))]
    [NotifyPropertyChangedFor(nameof(SelectedEpisodeTranscriptSummary))]
    [NotifyPropertyChangedFor(nameof(SelectedEpisodeReleaseDate))]
    [NotifyPropertyChangedFor(nameof(SelectedEpisodeAddedAt))]
    [NotifyPropertyChangedFor(nameof(SelectedEpisodeDurationFormatted))]
    [NotifyPropertyChangedFor(nameof(SelectedEpisodeResumeText))]
    [NotifyPropertyChangedFor(nameof(HasShowName))]
    [NotifyPropertyChangedFor(nameof(HasShareUrl))]
    [NotifyPropertyChangedFor(nameof(HasResumeProgress))]
    [NotifyPropertyChangedFor(nameof(ResumeProgressPercent))]
    [NotifyPropertyChangedFor(nameof(HasExplicit))]
    [NotifyPropertyChangedFor(nameof(HasVideo))]
    [NotifyPropertyChangedFor(nameof(HasTranscript))]
    [NotifyPropertyChangedFor(nameof(HasPaywalled))]
    [NotifyPropertyChangedFor(nameof(HasPreviewOnly))]
    [NotifyPropertyChangedFor(nameof(TranscriptLanguagesTooltip))]
    [NotifyCanExecuteChangedFor(nameof(CopyEpisodeLinkCommand))]
    private PodcastEpisodeDetailDto? _selectedEpisodeDetail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWideLayout))]
    [NotifyPropertyChangedFor(nameof(IsNarrowLayout))]
    [NotifyPropertyChangedFor(nameof(ShowWidePodcastBrowser))]
    [NotifyPropertyChangedFor(nameof(ShowNarrowShowsStage))]
    [NotifyPropertyChangedFor(nameof(ShowNarrowEpisodesStage))]
    private bool _useNarrowLayout;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNarrowShowsStage))]
    [NotifyPropertyChangedFor(nameof(ShowNarrowEpisodesStage))]
    private PodcastLibraryStage _narrowStage = PodcastLibraryStage.Shows;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEpisodeEmptyState))]
    [NotifyPropertyChangedFor(nameof(EpisodeEmptyTitle))]
    [NotifyPropertyChangedFor(nameof(EpisodeEmptyDescription))]
    [NotifyPropertyChangedFor(nameof(SelectedShowDetailArchiveSummary))]
    [NotifyPropertyChangedFor(nameof(ShowEpisodesShimmer))]
    private bool _isSelectedShowArchiveLoading;

    [ObservableProperty]
    private ShowDetailDto? _selectedShowDetail;

    public ObservableCollection<LibraryPodcastShowDto> PodcastShows { get; } = [];
    public ObservableCollection<LibraryPodcastShowDto> FilteredShows { get; } = [];
    public ObservableCollection<PodcastEpisodeGroupViewModel> EpisodeGroups { get; } = [];
    public ObservableCollection<LibraryEpisodeDto> VisibleEpisodes { get; } = [];
    public ObservableCollection<string> BreadcrumbItems { get; } = [];
    public ObservableCollection<ShowTopicDto> SelectedShowTopics { get; } = [];
    public Func<ITrackItem, object?> PodcastEpisodeGroupKeySelector { get; } = static item =>
        item is LibraryEpisodeDto episode ? EpisodeShowKey(episode) : null;
    public Func<ITrackItem, object> PodcastEpisodeGroupHeaderSelector { get; } = static item =>
    {
        if (item is not LibraryEpisodeDto episode)
            return "";

        return new PodcastEpisodeHeaderRow(
            EpisodeShowName(episode),
            episode.ImageUrl);
    };
    public Func<int, string> PodcastEpisodeGroupCountFormatter { get; } = static count =>
        count == 1 ? "1 episode" : $"{count:N0} episodes";

    public bool IsWideLayout => !UseNarrowLayout;
    public bool IsNarrowLayout => UseNarrowLayout;
    public bool ShowWidePodcastBrowser => IsWideLayout;
    public bool ShowNarrowShowsStage => UseNarrowLayout && NarrowStage == PodcastLibraryStage.Shows;
    public bool ShowNarrowEpisodesStage => UseNarrowLayout && NarrowStage == PodcastLibraryStage.Episodes;
    public string SortDirectionGlyph => SortDirection == LibrarySortDirection.Ascending ? "\uE74A" : "\uE74B";
    public bool HasEpisodeGroups => VisibleEpisodes.Count > 0;
    public bool ShowEpisodeGroupHeaders => SelectedShow is { IsAllPodcasts: true } or { IsRecentlyPlayed: true };
    public bool HasSelectedEpisode => SelectedEpisode is not null;
    public bool HasSelectedShowTopics => SelectedShowTopics.Count > 0;
    public bool IsInitialLoading => IsLoading && PodcastShows.Count == 0;
    public bool ShowEpisodesShimmer => IsLoading || IsSelectedShowArchiveLoading;
    public bool ShowLoadError => HasError && !IsLoading;
    public int PodcastEpisodeScopeIndex => _podcastEpisodeScope == PodcastEpisodeScope.Latest ? 1 : 0;
    public bool CanSwitchPodcastEpisodeScope => CanLoadArchiveForShow(SelectedShow);
    public bool ShowEpisodeEmptyState => !HasError && !HasEpisodeGroups && !IsSelectedShowArchiveLoading && !IsLoading;
    public string LoadErrorDescription => string.IsNullOrWhiteSpace(ErrorMessage)
        ? "Podcasts could not be loaded. Try again in a moment."
        : ErrorMessage;
    public string EpisodeEmptyTitle => IsSelectedShowArchiveLoading
        ? "Loading episodes"
        : CanSwitchPodcastEpisodeScope && _podcastEpisodeScope == PodcastEpisodeScope.Saved && SelectedShow is { HasSavedEpisodes: false }
            ? "No saved episodes yet"
            : "No saved episodes";

    public string EpisodeEmptyDescription => IsSelectedShowArchiveLoading
        ? "Fetching the latest episodes from this show."
            : CanSwitchPodcastEpisodeScope && _podcastEpisodeScope == PodcastEpisodeScope.Saved
                ? "Switch to Latest to browse this followed show's recent archive."
            : CanSwitchPodcastEpisodeScope
                ? "Open the show page for the full archive, recommendations, and show details."
            : "Followed shows appear here even before you save an episode.";

    public string SelectedEpisodeTitle => SelectedEpisodeDetail?.Title ?? SelectedEpisode?.Title ?? "Select an episode";
    public string? SelectedEpisodeImageUrl => SelectedEpisodeDetail?.ImageUrl ?? SelectedEpisode?.ImageUrl;
    public string SelectedEpisodeShowName => SelectedEpisodeDetail?.ShowName ?? SelectedEpisode?.AlbumName ?? "";
    public string SelectedEpisodeMetadata => SelectedEpisodeDetail?.Metadata
        ?? (SelectedEpisode is null ? "" : $"{SelectedEpisode.AlbumName} - {SelectedEpisode.DurationFormatted}");
    public string? SelectedEpisodeDescription => SelectedEpisodeDetail?.HtmlDescription
                                                 ?? SelectedEpisodeDetail?.Description
                                                 ?? SelectedEpisode?.Description;
    public bool HasSelectedEpisodeDescription => !string.IsNullOrWhiteSpace(SelectedEpisodeDescription);
    public string SelectedEpisodeAvailability => SelectedEpisodeDetail?.Availability ?? "";
    public string SelectedEpisodeTranscriptSummary => SelectedEpisodeDetail?.TranscriptSummary ?? "";

    public string SelectedEpisodeReleaseDate => SelectedEpisodeDetail?.ReleaseDateFormatted ?? "";
    public string SelectedEpisodeAddedAt => SelectedEpisodeDetail?.AddedAtFormatted ?? "";
    public string SelectedEpisodeDurationFormatted => SelectedEpisodeDetail?.DurationFormatted
        ?? SelectedEpisode?.DurationFormatted ?? "";

    public string SelectedEpisodeMetaLine
    {
        get
        {
            var parts = new List<string>(3);
            var release = SelectedEpisodeReleaseDate;
            if (!string.IsNullOrWhiteSpace(release)) parts.Add(release);
            var duration = SelectedEpisodeDurationFormatted;
            if (!string.IsNullOrWhiteSpace(duration)) parts.Add(duration);
            var added = SelectedEpisodeAddedAt;
            if (!string.IsNullOrWhiteSpace(added)) parts.Add($"Added {added}");
            return string.Join(" • ", parts);
        }
    }

    public bool HasShowName => !string.IsNullOrWhiteSpace(SelectedEpisodeShowName);
    public bool HasShareUrl => !string.IsNullOrWhiteSpace(SelectedEpisodeDetail?.ShareUrl);

    public bool HasResumeProgress => SelectedEpisodeDetail is { } d
        && d.Duration.TotalSeconds > 0
        && d.PlayedPosition.TotalSeconds > 0
        && d.PlayedPosition < d.Duration;

    public double ResumeProgressPercent
    {
        get
        {
            if (SelectedEpisodeDetail is not { } d || d.Duration.TotalSeconds <= 0) return 0;
            var ratio = d.PlayedPosition.TotalSeconds / d.Duration.TotalSeconds;
            return Math.Clamp(ratio, 0, 1) * 100.0;
        }
    }

    public string SelectedEpisodeResumeText
    {
        get
        {
            if (SelectedEpisodeDetail is not { } d || !HasResumeProgress) return "";
            var remaining = d.Duration - d.PlayedPosition;
            if (remaining.TotalHours >= 1) return $"{(int)remaining.TotalHours} hr {remaining.Minutes} min left";
            if (remaining.TotalMinutes >= 1) return $"{(int)remaining.TotalMinutes} min left";
            return $"{(int)remaining.TotalSeconds} sec left";
        }
    }

    public bool HasExplicit => SelectedEpisodeDetail?.IsExplicit ?? SelectedEpisode?.IsExplicit ?? false;
    public bool HasVideo => SelectedEpisodeDetail?.MediaTypes
        .Any(m => string.Equals(m, "video", StringComparison.OrdinalIgnoreCase)) ?? false;
    public bool HasTranscript => SelectedEpisodeDetail is { } d && d.TranscriptLanguages.Count > 0;
    public bool HasPaywalled => SelectedEpisodeDetail?.IsPaywalled ?? false;
    public bool HasPreviewOnly => SelectedEpisodeDetail is { IsPlayable: false } d
        && !string.IsNullOrWhiteSpace(d.PreviewUrl);
    public string TranscriptLanguagesTooltip => SelectedEpisodeDetail is { } d && d.TranscriptLanguages.Count > 0
        ? "Transcript: " + string.Join(", ", d.TranscriptLanguages)
        : "";

    public bool CanOpenSelectedShow => SelectedShow is { IsAllPodcasts: false } show
        && !show.IsRecentlyPlayed
        && show.Id.StartsWith("spotify:show:", StringComparison.Ordinal);

    public string? SelectedShowDetailImageUrl => SelectedShowDetail?.CoverArtUrl ?? SelectedShowImageUrl;
    public string SelectedShowDetailTitle => SelectedShowDetail?.Name ?? SelectedShowName;
    public string? SelectedShowDetailPublisher => SelectedShowDetail?.PublisherName ?? SelectedShow?.Publisher;
    public bool HasSelectedShowDetailPublisher => !string.IsNullOrWhiteSpace(SelectedShowDetailPublisher);
    public string? SelectedShowDetailDescription => SelectedShowDetail?.PlainDescription ?? SelectedShowDescription;
    public bool HasSelectedShowDetailDescription => !string.IsNullOrWhiteSpace(SelectedShowDetailDescription);
    public string SelectedShowDetailEpisodeCountLine
    {
        get
        {
            var total = SelectedShowDetail?.TotalEpisodes ?? SelectedShow?.EpisodeCount ?? GetVisibleEpisodes().Count;
            if (total <= 0)
            {
                var loaded = GetVisibleEpisodes().Count;
                return loaded == 1 ? "1 episode" : $"{loaded:N0} episodes";
            }

            return total == 1 ? "1 episode" : $"{total:N0} episodes";
        }
    }

    public string? SelectedShowDetailRatingLine => BuildShowRatingLine(SelectedShowDetail);
    public bool HasSelectedShowDetailRating => !string.IsNullOrWhiteSpace(SelectedShowDetailRatingLine);
    public bool SelectedShowDetailIsExplicit => SelectedShowDetail?.IsExplicit ?? false;
    public bool SelectedShowDetailIsExclusive => SelectedShowDetail?.IsExclusive ?? false;
    public bool SelectedShowDetailIsVideo =>
        string.Equals(SelectedShowDetail?.MediaType, "VIDEO", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SelectedShowDetail?.MediaType, "MIXED", StringComparison.OrdinalIgnoreCase);

    public string SelectedShowDetailArchiveSummary
    {
        get
        {
            if (IsSelectedShowArchiveLoading)
                return "Loading episodes";

            var visible = GetVisibleEpisodes().Count;
            if (visible <= 0)
                return "";

            var total = SelectedShowDetail?.TotalEpisodes ?? visible;
            if (total > visible)
                return $"{visible:N0} of {total:N0} shown";

            return $"{visible:N0} available";
        }
    }

    public (double Shows, double Episodes)? GetPodcastWideColumnWidths()
    {
        var widths = _settingsService?.Settings.PanelWidths;
        if (widths is null)
            return null;

        return widths.TryGetValue(PodcastShowsColumnWidthKey, out var shows)
               && widths.TryGetValue(PodcastEpisodesColumnWidthKey, out var episodes)
               && shows > 0
               && episodes > 0
            ? (shows, episodes)
            : null;
    }

    public void SavePodcastWideColumnWidths(double shows, double episodes, double details)
    {
        if (_settingsService is null || shows <= 0 || episodes <= 0 || details <= 0)
            return;

        _settingsService.Update(settings =>
        {
            settings.PanelWidths[PodcastShowsColumnWidthKey] = Math.Round(shows);
            settings.PanelWidths[PodcastEpisodesColumnWidthKey] = Math.Round(episodes);
            settings.PanelWidths[PodcastDetailsColumnWidthKey] = Math.Round(details);
        });
    }

    public void SetPodcastEpisodeScope(PodcastEpisodeScope scope)
    {
        if (_podcastEpisodeScope == scope)
            return;

        _podcastEpisodeScope = scope;
        OnPropertyChanged(nameof(PodcastEpisodeScopeIndex));
        OnPropertyChanged(nameof(EpisodeEmptyTitle));
        OnPropertyChanged(nameof(EpisodeEmptyDescription));

        SavePodcastEpisodeScopePreference(scope);
        CancelSelectedShowArchiveLoad();
        BuildEpisodeGroups();

        if (ShouldLoadArchivePreview(SelectedShow))
            _ = LoadSelectedShowArchivePreviewAsync(SelectedShow!);
    }

    private static string? NormalizeSpotifyShowUri(string? showUri)
    {
        if (string.IsNullOrWhiteSpace(showUri))
            return null;

        const string prefix = "spotify:show:";
        return showUri.StartsWith(prefix, StringComparison.Ordinal)
            ? showUri
            : $"{prefix}{showUri}";
    }

    private PodcastEpisodeScope LoadPodcastEpisodeScopePreference()
    {
        var prefs = _settingsService?.Settings.LibraryTabs;
        if (prefs is null || !prefs.TryGetValue(PodcastPreferencesTabKey, out var saved) || saved is null)
            return PodcastEpisodeScope.Latest;

        return string.Equals(saved.ViewMode, PodcastSavedScopePreference, StringComparison.OrdinalIgnoreCase)
            ? PodcastEpisodeScope.Saved
            : PodcastEpisodeScope.Latest;
    }

    private void SavePodcastEpisodeScopePreference(PodcastEpisodeScope scope)
    {
        if (_settingsService is null)
            return;

        _settingsService.Update(settings =>
        {
            if (!settings.LibraryTabs.TryGetValue(PodcastPreferencesTabKey, out var entry) || entry is null)
            {
                entry = new LibraryTabPreferences();
                settings.LibraryTabs[PodcastPreferencesTabKey] = entry;
            }

            entry.ViewMode = scope == PodcastEpisodeScope.Latest
                ? PodcastLatestScopePreference
                : PodcastSavedScopePreference;
        });

        _ = _settingsService.SaveAsync();
    }

    public YourEpisodesViewModel(
        ILibraryDataService libraryDataService,
        IPlaybackStateService playbackStateService,
        IPodcastService? podcastService = null,
        ISettingsService? settingsService = null,
        ILogger<YourEpisodesViewModel>? logger = null)
    {
        _libraryDataService = libraryDataService;
        _podcastService = podcastService;
        _settingsService = settingsService;
        _playbackStateService = playbackStateService;
        _logger = logger;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _podcastEpisodeScope = LoadPodcastEpisodeScopePreference();
        AttachLongLivedServices();
    }

    private bool _longLivedAttached;

    private void AttachLongLivedServices()
    {
        if (_longLivedAttached) return;
        _longLivedAttached = true;
        _libraryDataService.DataChanged += OnLibraryDataChanged;
        _libraryDataService.PodcastEpisodeProgressChanged += OnPodcastEpisodeProgressChanged;
    }

    private void DetachLongLivedServices()
    {
        if (!_longLivedAttached) return;
        _longLivedAttached = false;
        _libraryDataService.DataChanged -= OnLibraryDataChanged;
        _libraryDataService.PodcastEpisodeProgressChanged -= OnPodcastEpisodeProgressChanged;
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsLoading)
            return;

        if (!HasError && (PodcastShows.Count > 0 || _allEpisodes.Count > 0 || _recentEpisodes.Count > 0))
            return;

        await LoadDataAsync();
    }

    private async Task LoadDataAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        HasError = false;
        ErrorMessage = null;

        var previousShowId = SelectedShow?.Id;

        try
        {
            var showsTask = _libraryDataService.GetPodcastShowsAsync();
            var episodesTask = _libraryDataService.GetYourEpisodesAsync();
            var recentEpisodesTask = _libraryDataService.GetRecentlyPlayedPodcastEpisodesAsync();
            await Task.WhenAll(showsTask, episodesTask, recentEpisodesTask);

            var shows = (await showsTask)
                .OrderByDescending(static s => s.SortDate)
                .ThenBy(static s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var episodes = (await episodesTask)
                .OrderByDescending(static e => e.AddedAt)
                .Select((episode, index) => episode with { OriginalIndex = index + 1 })
                .ToList();
            var recentEpisodes = (await recentEpisodesTask)
                .OrderByDescending(static e => e.AddedAt)
                .Select((episode, index) => episode with { OriginalIndex = index + 1 })
                .ToList();

            PodcastShows.ReplaceWith(shows);
            _allEpisodes.Clear();
            _allEpisodes.AddRange(episodes);
            _recentEpisodes.Clear();
            _recentEpisodes.AddRange(recentEpisodes);

            TotalPodcasts = PodcastShows.Count;
            TotalEpisodes = _allEpisodes.Count;
            TotalDuration = FormatDuration(_allEpisodes.Sum(static e => e.Duration.TotalSeconds));

            ApplyShowFilter();
            var firstRealShow = FilteredShows.FirstOrDefault(s => !s.IsAllPodcasts && !s.IsRecentlyPlayed);
            var defaultShow = firstRealShow
                              ?? (_allEpisodes.Count == 0 && _recentEpisodes.Count > 0
                                  ? FilteredShows.FirstOrDefault(show => string.Equals(show.Id, RecentlyPlayedShowId, StringComparison.OrdinalIgnoreCase))
                                  : FilteredShows.FirstOrDefault());

            SelectedShow = !string.IsNullOrEmpty(previousShowId)
                ? FilteredShows.FirstOrDefault(show => string.Equals(show.Id, previousShowId, StringComparison.OrdinalIgnoreCase))
                    ?? defaultShow
                : defaultShow;

            if (!UseNarrowLayout && SelectedEpisode is null)
            {
                _pendingAutoSelectFirstEpisode = true;
                TryAutoSelectFirstEpisode();
            }

            if (_allEpisodes.Count == 0 && PodcastShows.Count == 0 && _recentEpisodes.Count == 0 && !_syncAlreadyRequested)
            {
                _syncAlreadyRequested = true;
                _logger?.LogInformation("Podcasts library is empty - requesting library sync");
                _libraryDataService.RequestSyncIfEmpty();
            }

            RefreshEpisodeProgressInBackground();
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = ex.Message;
            _logger?.LogError(ex, "Failed to load Podcasts");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void PlayAll()
    {
        BuildQueueAndPlay(0, shuffle: false);
    }

    [RelayCommand]
    private void Shuffle()
    {
        _playbackStateService.SetShuffle(true);
        BuildQueueAndPlay(0, shuffle: true);
    }

    [RelayCommand]
    private void PlayEpisode(object? episode)
    {
        if (episode is not LibraryEpisodeDto item) return;
        var visible = GetVisibleEpisodes();
        var index = visible.FindIndex(e => string.Equals(e.Uri, item.Uri, StringComparison.Ordinal));
        BuildQueueAndPlay(index >= 0 ? index : 0, shuffle: false);
    }

    [RelayCommand]
    private void SelectEpisode(object? episode)
    {
        if (episode is LibraryEpisodeDto item)
        {
            if (UseNarrowLayout)
                OpenEpisodePage(item);
            else
                ShowEpisodeDetails(item);
        }
    }

    [RelayCommand]
    private void PlaySelectedEpisode()
    {
        if (SelectedEpisode is not null)
            PlayEpisode(SelectedEpisode);
    }

    [RelayCommand]
    private void OpenSelectedEpisodeShow()
    {
        var showUri = SelectedEpisodeDetail?.ShowUri ?? SelectedEpisode?.AlbumId;
        var showName = SelectedEpisodeDetail?.ShowName ?? SelectedEpisode?.AlbumName;
        if (string.IsNullOrWhiteSpace(showUri) ||
            !showUri.StartsWith("spotify:show:", StringComparison.Ordinal))
        {
            return;
        }

        NavigationHelpers.OpenShowPage(showUri, showName ?? "Podcast", openInNewTab: NavigationHelpers.IsCtrlPressed());
    }

    [RelayCommand]
    private void OpenSelectedEpisodeDetails()
    {
        OpenEpisodePage(SelectedEpisode);
    }

    private void OpenEpisodePage(LibraryEpisodeDto? episode)
    {
        if (episode is null || string.IsNullOrWhiteSpace(episode.Uri))
            return;

        var detail = string.Equals(SelectedEpisode?.Uri, episode.Uri, StringComparison.Ordinal)
            ? SelectedEpisodeDetail
            : null;

        NavigationHelpers.OpenEpisodePage(
            episode.Uri,
            detail?.Title ?? episode.Title,
            detail?.ImageUrl ?? episode.ImageUrl,
            detail?.ShowUri ?? episode.AlbumId,
            detail?.ShowName ?? episode.AlbumName,
            detail?.ShowImageUrl ?? episode.ImageUrl,
            NavigationHelpers.IsCtrlPressed());
    }

    [RelayCommand(CanExecute = nameof(HasShareUrl))]
    private void CopyEpisodeLink()
    {
        if (SelectedEpisodeDetail?.ShareUrl is not { Length: > 0 } url) return;

        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(url);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    [RelayCommand]
    private void OpenSelectedShow()
    {
        var show = SelectedShow;
        if (show is null ||
            show.IsAllPodcasts ||
            show.IsRecentlyPlayed ||
            !show.Id.StartsWith("spotify:show:", StringComparison.Ordinal))
        {
            return;
        }

        NavigationHelpers.OpenShowPage(
            show.Id,
            show.Name,
            show.Publisher,
            show.ImageUrl,
            NavigationHelpers.IsCtrlPressed());
    }

    [RelayCommand]
    private void ToggleSortDirection()
    {
        SortDirection = SortDirection == LibrarySortDirection.Ascending
            ? LibrarySortDirection.Descending
            : LibrarySortDirection.Ascending;
    }

    public void SetSortBy(LibrarySortBy sortBy)
    {
        if (SortBy == sortBy)
        {
            ToggleSortDirection();
            return;
        }

        SortBy = sortBy;
    }

    partial void OnSearchQueryChanged(string value) => ApplyShowFilter();

    partial void OnSortByChanged(LibrarySortBy value) => ApplyShowFilter();

    partial void OnSortDirectionChanged(LibrarySortDirection value) => ApplyShowFilter();

    partial void OnSelectedShowChanged(LibraryPodcastShowDto? value)
    {
        SelectedShowName = value?.Name ?? "Podcasts";
        SelectedShowMetadata = value?.Metadata ?? "";
        SelectedShowImageUrl = value?.ImageUrl;
        SelectedShowDescription = value?.Description;
        SelectedShowPlaceholderGlyph = value?.PlaceholderGlyph ?? "\uEC05";
        ApplySelectedShowDetail(null);

        CancelSelectedShowArchiveLoad();
        ClearSelectedEpisode();
        BuildEpisodeGroups();
        UpdateBreadcrumbs();
        OnPropertyChanged(nameof(CanOpenSelectedShow));
        OnPropertyChanged(nameof(CanSwitchPodcastEpisodeScope));
        OnPropertyChanged(nameof(PodcastEpisodeScopeIndex));
        OnPropertyChanged(nameof(ShowEpisodeEmptyState));
        OnPropertyChanged(nameof(EpisodeEmptyTitle));
        OnPropertyChanged(nameof(EpisodeEmptyDescription));

        if (ShouldLoadArchivePreview(value))
            _ = LoadSelectedShowArchivePreviewAsync(value!);

        if (UseNarrowLayout && value == null)
            NarrowStage = PodcastLibraryStage.Shows;
    }

    partial void OnSelectedShowDetailChanged(ShowDetailDto? value)
    {
        SelectedShowTopics.ReplaceWith(value?.Topics ?? []);
        RefreshSelectedShowDetailProperties();
    }

    public void SetNarrowLayout(bool isNarrow, bool preserveContext)
    {
        if (UseNarrowLayout == isNarrow)
        {
            if (isNarrow)
                SetNarrowStage(GetPreservedNarrowStage(preserveContext));
            else
                UpdateBreadcrumbs();
            return;
        }

        UseNarrowLayout = isNarrow;
        SetNarrowStage(isNarrow ? GetPreservedNarrowStage(preserveContext) : PodcastLibraryStage.Shows);
    }

    private PodcastLibraryStage GetPreservedNarrowStage(bool preserveContext)
    {
        if (!preserveContext)
            return PodcastLibraryStage.Shows;
        if (SelectedShow is not null)
            return PodcastLibraryStage.Episodes;
        return PodcastLibraryStage.Shows;
    }

    public void ShowPodcastsRoot()
    {
        if (!UseNarrowLayout)
            SelectedShow = FilteredShows.FirstOrDefault(show => show.IsAllPodcasts)
                           ?? FilteredShows.FirstOrDefault();
        ClearSelectedEpisode();
        SetNarrowStage(PodcastLibraryStage.Shows);
    }

    public void ShowSelectedShowEpisodes(LibraryPodcastShowDto? show = null)
    {
        if (show != null)
            SelectedShow = show;

        if (SelectedShow == null)
            return;

        ClearSelectedEpisode();
        SetNarrowStage(PodcastLibraryStage.Episodes);
    }

    public void ShowEpisodeDetails(LibraryEpisodeDto episode)
    {
        SelectedEpisode = episode;
        SelectedEpisodeDetail = PodcastEpisodeDetailDto.FromEpisode(episode);

        if (UseNarrowLayout)
        {
            OpenEpisodePage(episode);
            return;
        }

        UpdateBreadcrumbs();

        _episodeDetailCts?.Cancel();
        _episodeDetailCts?.Dispose();
        _episodeDetailCts = new CancellationTokenSource();
        _ = LoadEpisodeDetailAsync(episode.Uri, _episodeDetailCts.Token);
    }

    private void SetNarrowStage(PodcastLibraryStage stage)
    {
        NarrowStage = stage;
        UpdateBreadcrumbs();
    }

    private void TryAutoSelectFirstEpisode()
    {
        if (!_pendingAutoSelectFirstEpisode || UseNarrowLayout || SelectedEpisode is not null)
            return;

        var first = GetVisibleEpisodes().FirstOrDefault();
        if (first is null)
            return;

        _pendingAutoSelectFirstEpisode = false;
        ShowEpisodeDetails(first);
    }

    private async Task LoadEpisodeDetailAsync(string episodeUri, CancellationToken ct)
    {
        try
        {
            var detail = await _libraryDataService.GetPodcastEpisodeDetailAsync(episodeUri, ct);
            if (ct.IsCancellationRequested || detail is null)
                return;

            if (!string.Equals(SelectedEpisode?.Uri, episodeUri, StringComparison.Ordinal))
                return;

            ApplyEpisodeDetailProgress(SelectedEpisode, detail);
            SelectedEpisodeDetail = MergeEpisodeDetail(detail, SelectedEpisode);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load podcast episode detail for {EpisodeUri}", episodeUri);
        }
    }

    private void RefreshEpisodeProgressInBackground()
    {
        _episodeProgressCts?.Cancel();
        _episodeProgressCts?.Dispose();
        _episodeProgressCts = new CancellationTokenSource();
        _ = RefreshEpisodeProgressAsync(_episodeProgressCts.Token);
    }

    private async Task RefreshEpisodeProgressAsync(CancellationToken ct)
    {
        try
        {
            var episodes = _allEpisodes
                .Concat(_recentEpisodes)
                .GroupBy(static episode => episode.Uri, StringComparer.Ordinal)
                .Select(static group => group.First())
                .ToList();
            foreach (var episode in episodes)
            {
                if (ct.IsCancellationRequested)
                    return;

                var progress = await _libraryDataService.GetPodcastEpisodeProgressAsync(episode.Uri, ct);
                if (ct.IsCancellationRequested || progress is null)
                    continue;

                episode.ApplyPlaybackProgress(progress.PlayedPosition, progress.PlayedState);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to refresh podcast episode progress");
        }
    }

    private static PodcastEpisodeDetailDto MergeEpisodeDetail(
        PodcastEpisodeDetailDto detail,
        LibraryEpisodeDto? cachedEpisode)
    {
        if (cachedEpisode is null)
            return detail;

        return detail with
        {
            AddedAt = cachedEpisode.AddedAt,
            ImageUrl = detail.ImageUrl ?? cachedEpisode.ImageUrl,
            ShowImageUrl = detail.ShowImageUrl ?? cachedEpisode.ImageUrl,
            ShowUri = detail.ShowUri ?? cachedEpisode.AlbumId,
            ShowName = detail.ShowName ?? cachedEpisode.AlbumName,
            Description = detail.Description ?? cachedEpisode.Description,
            HtmlDescription = detail.HtmlDescription ?? detail.Description ?? cachedEpisode.Description,
            ShareUrl = detail.ShareUrl ?? cachedEpisode.ShareUrl,
            PreviewUrl = detail.PreviewUrl ?? cachedEpisode.PreviewUrl,
            Duration = detail.Duration > TimeSpan.Zero ? detail.Duration : cachedEpisode.Duration,
            PlayedState = cachedEpisode.PlayedState,
            PlayedPosition = cachedEpisode.PlayedPosition
        };
    }

    private static void ApplyEpisodeDetailProgress(
        LibraryEpisodeDto? episode,
        PodcastEpisodeDetailDto detail)
    {
        if (episode is null)
            return;

        if (string.IsNullOrWhiteSpace(detail.PlayedState))
            return;

        if (!ShouldApplyDetailProgress(episode, detail))
            return;

        episode.ApplyPlaybackProgress(detail.PlayedPosition, detail.PlayedState);
    }

    private static bool ShouldApplyDetailProgress(
        LibraryEpisodeDto episode,
        PodcastEpisodeDetailDto detail)
    {
        if (episode.HasPlaybackProgressError)
            return true;

        var existingProgress = episode.PlaybackProgress ?? 0d;
        var detailDuration = detail.Duration > TimeSpan.Zero ? detail.Duration : episode.Duration;
        var detailProgress = CalculateProgress(detail.PlayedPosition, detailDuration);
        var detailIsPlayed = string.Equals(detail.PlayedState, "COMPLETED", StringComparison.OrdinalIgnoreCase) ||
                             detailProgress >= 0.995d;

        if (string.Equals(detail.PlayedState, "NOT_STARTED", StringComparison.OrdinalIgnoreCase))
            return true;

        if (detailIsPlayed)
            return true;

        if (detail.PlayedPosition <= TimeSpan.Zero)
            return existingProgress <= 0.001d;

        if (existingProgress <= 0.001d)
            return true;

        return detailProgress >= existingProgress;
    }

    private static double CalculateProgress(TimeSpan playedPosition, TimeSpan duration)
    {
        if (duration.TotalMilliseconds <= 0 || playedPosition <= TimeSpan.Zero)
            return 0d;

        return Math.Clamp(playedPosition.TotalMilliseconds / duration.TotalMilliseconds, 0d, 1d);
    }

    private void ClearSelectedEpisode()
    {
        _episodeDetailCts?.Cancel();
        _episodeDetailCts?.Dispose();
        _episodeDetailCts = null;
        SelectedEpisode = null;
        SelectedEpisodeDetail = null;
    }

    private void ApplyShowFilter()
    {
        var previousId = SelectedShow?.Id;
        var query = SearchQuery.Trim();

        IEnumerable<LibraryPodcastShowDto> shows = PodcastShows;
        if (!string.IsNullOrWhiteSpace(query))
        {
            shows = shows.Where(show =>
                show.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (show.Publisher?.Contains(query, StringComparison.OrdinalIgnoreCase) == true));
        }

        var all = CreateAllPodcastsShow();
        var recent = CreateRecentlyPlayedShow();
        var sorted = SortShows(shows).ToList();
        IEnumerable<LibraryPodcastShowDto> leadingShows = recent is null
            ? new[] { all }
            : new[] { all, recent! };
        FilteredShows.ReplaceWith(leadingShows.Concat(sorted));

        PreserveSelectedShowAfterFilter(previousId, fallbackToFirst: true);
        QueueSelectedShowResync(previousId);
    }

    private void PreserveSelectedShowAfterFilter(string? selectedId, bool fallbackToFirst)
    {
        if (string.IsNullOrEmpty(selectedId))
            return;

        var selected = FilteredShows.FirstOrDefault(show =>
            string.Equals(show.Id, selectedId, StringComparison.OrdinalIgnoreCase));

        if (selected != null && !ReferenceEquals(SelectedShow, selected))
        {
            SelectedShow = selected;
        }
        else if (selected == null
                 && fallbackToFirst
                 && string.Equals(SelectedShow?.Id, selectedId, StringComparison.OrdinalIgnoreCase))
        {
            SelectedShow = FilteredShows.FirstOrDefault();
        }
    }

    private void QueueSelectedShowResync(string? selectedId)
    {
        if (string.IsNullOrEmpty(selectedId))
            return;

        _dispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed)
                return;

            if (SelectedShow is null || string.Equals(SelectedShow.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                PreserveSelectedShowAfterFilter(selectedId, fallbackToFirst: false);
        });
    }

    private IEnumerable<LibraryPodcastShowDto> SortShows(IEnumerable<LibraryPodcastShowDto> source)
    {
        var descending = SortDirection == LibrarySortDirection.Descending;
        return SortBy switch
        {
            LibrarySortBy.Alphabetical => descending
                ? source.OrderByDescending(static s => s.Name, StringComparer.OrdinalIgnoreCase)
                : source.OrderBy(static s => s.Name, StringComparer.OrdinalIgnoreCase),
            LibrarySortBy.Creator => descending
                ? source.OrderByDescending(static s => s.Publisher ?? "", StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(static s => s.Name, StringComparer.OrdinalIgnoreCase)
                : source.OrderBy(static s => s.Publisher ?? "", StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static s => s.Name, StringComparer.OrdinalIgnoreCase),
            _ => descending
                ? source.OrderByDescending(static s => s.SortDate).ThenBy(static s => s.Name, StringComparer.OrdinalIgnoreCase)
                : source.OrderBy(static s => s.SortDate).ThenBy(static s => s.Name, StringComparer.OrdinalIgnoreCase)
        };
    }

    private LibraryPodcastShowDto CreateAllPodcastsShow() => new()
    {
        Id = AllPodcastsShowId,
        Name = "All Podcasts",
        Publisher = TotalPodcasts == 1 ? "1 podcast" : $"{TotalPodcasts} podcasts",
        EpisodeCount = TotalEpisodes,
        SavedEpisodeCount = TotalEpisodes,
        AddedAt = _allEpisodes.FirstOrDefault()?.AddedAt ?? DateTime.Now,
        LastEpisodeAddedAt = _allEpisodes.FirstOrDefault()?.AddedAt,
        IsAllPodcasts = true
    };

    private LibraryPodcastShowDto? CreateRecentlyPlayedShow()
    {
        if (_recentEpisodes.Count == 0)
            return null;

        return new LibraryPodcastShowDto
        {
            Id = RecentlyPlayedShowId,
            Name = "Recently played",
            Publisher = "From listening history",
            EpisodeCount = _recentEpisodes.Count,
            SavedEpisodeCount = _recentEpisodes.Count,
            AddedAt = _recentEpisodes.FirstOrDefault()?.AddedAt ?? DateTime.Now,
            LastEpisodeAddedAt = _recentEpisodes.FirstOrDefault()?.AddedAt,
            IsRecentlyPlayed = true
        };
    }

    private void BuildEpisodeGroups()
    {
        var show = SelectedShow;
        if (show == null)
        {
            VisibleEpisodes.Clear();
            EpisodeGroups.Clear();
            OnPropertyChanged(nameof(HasEpisodeGroups));
            OnPropertyChanged(nameof(ShowEpisodeEmptyState));
            OnPropertyChanged(nameof(SelectedShowDetailArchiveSummary));
            return;
        }

        List<LibraryEpisodeDto> episodes;
        if (show.IsAllPodcasts)
        {
            episodes = _allEpisodes
                .OrderByDescending(static episode => episode.AddedAt)
                .ThenBy(static episode => episode.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        else if (show.IsRecentlyPlayed)
        {
            episodes = _recentEpisodes
                .OrderByDescending(static episode => episode.AddedAt)
                .ThenBy(static episode => episode.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        else if (_podcastEpisodeScope == PodcastEpisodeScope.Latest && CanLoadArchiveForShow(show))
        {
            ShowArchivePreview? preview = null;
            lock (_showArchivePreviewCacheGate)
                _showArchivePreviewCache.TryGetValue(show.Id, out preview);

            if (preview is not null)
            {
                ApplySelectedShowDetail(preview.Detail);
                episodes = preview.Episodes.ToList();
            }
            else
            {
                episodes = [];
            }
        }
        else
        {
            var selectedKey = PodcastShowKey(show);
            episodes = _allEpisodes
                .Where(episode => string.Equals(EpisodeShowKey(episode), selectedKey, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(static episode => episode.AddedAt)
                .ThenBy(static episode => episode.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        VisibleEpisodes.ReplaceWith(episodes.Select((episode, index) => episode with { OriginalIndex = index + 1 }));
        EpisodeGroups.Clear();
        OnPropertyChanged(nameof(HasEpisodeGroups));
        OnPropertyChanged(nameof(ShowEpisodeEmptyState));
        OnPropertyChanged(nameof(SelectedShowDetailArchiveSummary));
        TryAutoSelectFirstEpisode();
    }

    private bool ShouldLoadArchivePreview(LibraryPodcastShowDto? show)
    {
        if (_podcastEpisodeScope != PodcastEpisodeScope.Latest || !CanLoadArchiveForShow(show))
            return false;
        if (_showArchiveCts is not null && IsSelectedShowArchiveLoading)
            return false;

        lock (_showArchivePreviewCacheGate)
            return !_showArchivePreviewCache.ContainsKey(show.Id);
    }

    private bool TryApplyCachedShowDetail(LibraryPodcastShowDto show)
    {
        if (!TryGetCachedShowDetail(show.Id, out var detail))
            return false;

        ApplySelectedShowDetail(detail);
        return true;
    }

    private bool TryGetCachedShowDetail(string showUri, out ShowDetailDto? detail)
    {
        lock (_showArchivePreviewCacheGate)
        {
            if (_showArchivePreviewCache.TryGetValue(showUri, out var preview))
            {
                detail = preview.Detail;
                return true;
            }

            if (_showDetailCache.TryGetValue(showUri, out detail))
                return true;
        }

        detail = null;
        return false;
    }

    private void CacheShowDetail(ShowDetailDto detail)
    {
        var showUri = NormalizeSpotifyShowUri(detail.Uri);
        if (string.IsNullOrWhiteSpace(showUri))
            showUri = NormalizeSpotifyShowUri(detail.Id);
        if (string.IsNullOrWhiteSpace(showUri))
            return;

        lock (_showArchivePreviewCacheGate)
            _showDetailCache[showUri] = detail;
    }

    private void ApplySelectedShowDetail(ShowDetailDto? detail)
    {
        if (detail is not null)
            CacheShowDetail(detail);

        SelectedShowDetail = detail;
        if (detail is null)
            SelectedShowTopics.Clear();

        RefreshSelectedShowDetailProperties();
    }

    private void RefreshSelectedShowDetailProperties()
    {
        OnPropertyChanged(nameof(HasSelectedShowTopics));
        OnPropertyChanged(nameof(SelectedShowDetailImageUrl));
        OnPropertyChanged(nameof(SelectedShowDetailTitle));
        OnPropertyChanged(nameof(SelectedShowDetailPublisher));
        OnPropertyChanged(nameof(HasSelectedShowDetailPublisher));
        OnPropertyChanged(nameof(SelectedShowDetailDescription));
        OnPropertyChanged(nameof(HasSelectedShowDetailDescription));
        OnPropertyChanged(nameof(SelectedShowDetailEpisodeCountLine));
        OnPropertyChanged(nameof(SelectedShowDetailRatingLine));
        OnPropertyChanged(nameof(HasSelectedShowDetailRating));
        OnPropertyChanged(nameof(SelectedShowDetailIsExplicit));
        OnPropertyChanged(nameof(SelectedShowDetailIsExclusive));
        OnPropertyChanged(nameof(SelectedShowDetailIsVideo));
        OnPropertyChanged(nameof(SelectedShowDetailArchiveSummary));
    }

    private bool CanLoadArchiveForShow(LibraryPodcastShowDto? show)
        => _podcastService is not null
           && show is { IsAllPodcasts: false, IsRecentlyPlayed: false }
           && show.Id.StartsWith("spotify:show:", StringComparison.Ordinal);

    private async Task LoadSelectedShowArchivePreviewAsync(LibraryPodcastShowDto show)
    {
        _showArchiveCts = new CancellationTokenSource();
        var cts = _showArchiveCts;
        var ct = cts.Token;

        IsSelectedShowArchiveLoading = true;
        await Task.Yield();

        try
        {
            var detail = await _podcastService!.GetShowDetailAsync(show.Id, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested || detail is null)
                return;

            CacheShowDetail(detail);

            _dispatcherQueue.TryEnqueue(() =>
            {
                if (_disposed || ct.IsCancellationRequested || !IsSelectedShow(show))
                    return;

                ApplySelectedShowDetail(detail);
            });

            if (detail.EpisodeUris.Count == 0)
                return;

            var previewUris = detail.EpisodeUris
                .Where(static uri => uri.StartsWith("spotify:episode:", StringComparison.Ordinal))
                .Take(PodcastArchivePreviewLimit)
                .ToList();
            if (previewUris.Count == 0)
                return;

            var showEpisodes = await _podcastService.GetEpisodesAsync(previewUris, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested || showEpisodes.Count == 0)
                return;

            var episodes = showEpisodes
                .Select((episode, index) => MapArchiveEpisode(show, detail, episode, index + 1))
                .ToList();

            var preview = new ShowArchivePreview(detail, episodes, Math.Max(detail.TotalEpisodes, detail.EpisodeUris.Count));
            lock (_showArchivePreviewCacheGate)
                _showArchivePreviewCache[show.Id] = preview;

            _dispatcherQueue.TryEnqueue(() =>
            {
                if (_disposed || ct.IsCancellationRequested || !IsSelectedShow(show))
                    return;

                ApplyArchivePreviewGroup(show, preview);
                ApplySelectedShowDetail(detail);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load podcast archive preview for {ShowUri}", show.Id);
        }
        finally
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (ReferenceEquals(_showArchiveCts, cts))
                {
                    IsSelectedShowArchiveLoading = false;
                    _showArchiveCts?.Dispose();
                    _showArchiveCts = null;
                }
            });
        }
    }

    private void ApplyArchivePreviewGroup(LibraryPodcastShowDto show, ShowArchivePreview preview)
    {
        if (!IsSelectedShow(show) || HasEpisodeGroups)
            return;

        VisibleEpisodes.ReplaceWith(preview.Episodes.Select((episode, index) => episode with { OriginalIndex = index + 1 }));
        EpisodeGroups.Clear();
        OnPropertyChanged(nameof(HasEpisodeGroups));
        OnPropertyChanged(nameof(ShowEpisodeEmptyState));
        OnPropertyChanged(nameof(SelectedShowDetailArchiveSummary));
        TryAutoSelectFirstEpisode();
    }

    private PodcastEpisodeGroupViewModel CreateArchivePreviewGroup(
        LibraryPodcastShowDto show,
        ShowArchivePreview preview)
    {
        var summary = preview.TotalEpisodes > preview.Episodes.Count
            ? $"Latest {preview.Episodes.Count:N0} of {preview.TotalEpisodes:N0} episodes"
            : preview.Episodes.Count == 1
                ? "1 episode"
                : $"{preview.Episodes.Count:N0} episodes";

        return new PodcastEpisodeGroupViewModel(
            $"Latest from {show.Name}",
            show.ImageUrl ?? preview.Episodes.FirstOrDefault()?.ImageUrl,
            preview.Episodes,
            PlayEpisodeCommand,
            SelectEpisodeCommand,
            summary,
            showHeader: false)
        {
            IsExpanded = true
        };
    }

    private static LibraryEpisodeDto MapArchiveEpisode(
        LibraryPodcastShowDto show,
        ShowDetailDto detail,
        ShowEpisodeDto episode,
        int originalIndex)
    {
        var id = ExtractIdFromUri(episode.Uri);
        var duration = TimeSpan.FromMilliseconds(Math.Max(0, episode.DurationMs));
        var dto = new LibraryEpisodeDto
        {
            Id = id,
            Uri = episode.Uri,
            Title = episode.Title,
            ArtistName = detail.PublisherName ?? show.Publisher ?? show.Name,
            ArtistId = show.Id,
            AlbumName = detail.Name.Length > 0 ? detail.Name : show.Name,
            AlbumId = detail.Uri.Length > 0 ? detail.Uri : show.Id,
            ImageUrl = episode.CoverArtUrl ?? detail.CoverArtUrl ?? show.ImageUrl,
            Description = episode.DescriptionPreview,
            ReleaseDate = episode.ReleaseDate,
            ShareUrl = id.Length > 0 ? $"https://open.spotify.com/episode/{id}" : null,
            MediaTypes = episode.IsVideo ? ["video"] : ["audio"],
            Duration = duration,
            AddedAt = episode.ReleaseDate?.LocalDateTime ?? DateTime.Now,
            IsExplicit = episode.IsExplicit,
            IsPlayable = episode.IsPlayable,
            OriginalIndex = originalIndex,
            IsLiked = false
        };

        dto.ApplyPlaybackProgress(
            TimeSpan.FromMilliseconds(Math.Max(0, episode.PlayedPositionMs)),
            episode.PlayedState);
        return dto;
    }

    private void CancelSelectedShowArchiveLoad()
    {
        _showArchiveCts?.Cancel();
        _showArchiveCts?.Dispose();
        _showArchiveCts = null;
        IsSelectedShowArchiveLoading = false;
    }

    private bool IsSelectedShow(LibraryPodcastShowDto show)
        => SelectedShow is { } selected &&
           string.Equals(selected.Id, show.Id, StringComparison.Ordinal);

    private List<LibraryEpisodeDto> GetVisibleEpisodes()
        => VisibleEpisodes.ToList();

    private void BuildQueueAndPlay(int startIndex, bool shuffle)
    {
        var episodes = GetVisibleEpisodes();
        if (episodes.Count == 0) return;

        var items = episodes.Select(e => new QueueItem
        {
            TrackId = e.Uri,
            Title = e.Title,
            ArtistName = e.ArtistName,
            AlbumArt = e.ImageUrl,
            DurationMs = e.Duration.TotalMilliseconds,
            IsUserQueued = false
        }).ToList();

        if (shuffle)
        {
            items.Shuffle();
            startIndex = 0;
        }

        var show = SelectedShow;
        var contextUri = show is { IsAllPodcasts: false } && show.Id.StartsWith("spotify:show:", StringComparison.Ordinal)
            ? show.Id
            : "spotify:collection:your-episodes";

        var context = new PlaybackContextInfo
        {
            ContextUri = contextUri,
            Type = contextUri.StartsWith("spotify:show:", StringComparison.Ordinal)
                ? PlaybackContextType.Show
                : PlaybackContextType.Episode,
            Name = show?.Name ?? "Podcasts"
        };

        _playbackStateService.LoadQueue(items, context, startIndex);
    }

    private void UpdateBreadcrumbs()
    {
        BreadcrumbItems.Clear();
        BreadcrumbItems.Add("Podcasts");

        var includeSelectedShow = !UseNarrowLayout ||
                                  NarrowStage == PodcastLibraryStage.Episodes ||
                                  SelectedEpisode is not null;
        if (includeSelectedShow && SelectedShow is { } selectedShow)
        {
            BreadcrumbItems.Add(selectedShow.Name);
        }
        else if (SelectedEpisode is not null)
        {
            var parentShowName = SelectedEpisodeDetail?.ShowName ?? SelectedEpisode.AlbumName;
            if (!string.IsNullOrWhiteSpace(parentShowName))
                BreadcrumbItems.Add(parentShowName);
        }

        if (SelectedEpisode != null)
            BreadcrumbItems.Add(SelectedEpisode.Title);
    }

    private void OnLibraryDataChanged(object? sender, EventArgs e)
    {
        _dispatcherQueue.TryEnqueue(async () =>
        {
            if (_disposed || IsLoading)
                return;

            await LoadDataAsync();
        });
    }

    private void OnPodcastEpisodeProgressChanged(object? sender, PodcastEpisodeProgressChangedEventArgs e)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed)
                return;

            ApplyPodcastEpisodeProgress(e);
        });
    }

    private void ApplyPodcastEpisodeProgress(PodcastEpisodeProgressChangedEventArgs e)
    {
        foreach (var episode in _allEpisodes.Concat(_recentEpisodes).Concat(VisibleEpisodes))
        {
            if (e.Matches(episode.Uri))
                episode.ApplyPlaybackProgress(e.Progress.PlayedPosition, e.Progress.PlayedState);
        }

        if (SelectedEpisode is { } selectedEpisode && e.Matches(selectedEpisode.Uri))
        {
            selectedEpisode.ApplyPlaybackProgress(e.Progress.PlayedPosition, e.Progress.PlayedState);
            if (SelectedEpisodeDetail is { } detail)
            {
                SelectedEpisodeDetail = detail with
                {
                    PlayedPosition = e.Progress.PlayedPosition,
                    PlayedState = e.Progress.PlayedState
                };
            }
        }
    }

    private static string PodcastShowKey(LibraryPodcastShowDto show)
        => show.Id.StartsWith("spotify:show:", StringComparison.OrdinalIgnoreCase)
            ? show.Id
            : FallbackShowKey(show.Name);

    private static string EpisodeShowKey(LibraryEpisodeDto episode)
        => episode.AlbumId.StartsWith("spotify:show:", StringComparison.OrdinalIgnoreCase)
            ? episode.AlbumId
            : FallbackShowKey(EpisodeShowName(episode));

    private static string EpisodeShowName(LibraryEpisodeDto episode)
    {
        if (!string.IsNullOrWhiteSpace(episode.AlbumName))
            return episode.AlbumName;
        if (!string.IsNullOrWhiteSpace(episode.ArtistName))
            return episode.ArtistName;
        return "Unknown podcast";
    }

    private static string FallbackShowKey(string name)
        => "podcast:show:" + Uri.EscapeDataString((string.IsNullOrWhiteSpace(name) ? "Unknown podcast" : name).ToLowerInvariant());

    private static string ExtractIdFromUri(string uri)
    {
        if (string.IsNullOrEmpty(uri)) return "";
        var idx = uri.LastIndexOf(':');
        return idx >= 0 && idx < uri.Length - 1 ? uri[(idx + 1)..] : uri;
    }

    private static string FormatDuration(double totalSeconds)
    {
        var ts = TimeSpan.FromSeconds(totalSeconds);
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours} hr {ts.Minutes} min";
        return $"{ts.Minutes} min";
    }

    private static string? BuildShowRatingLine(ShowDetailDto? detail)
    {
        if (detail is not { ShowAverageRating: true } || detail.AverageRating <= 0)
            return null;

        var average = detail.AverageRating.ToString("0.0");
        return detail.TotalRatings > 0
            ? $"{average} rating - {detail.TotalRatings:N0} ratings"
            : $"{average} rating";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _episodeDetailCts?.Cancel();
        _episodeDetailCts?.Dispose();
        _episodeProgressCts?.Cancel();
        _episodeProgressCts?.Dispose();
        _showArchiveCts?.Cancel();
        _showArchiveCts?.Dispose();
        DetachLongLivedServices();
    }
}

public sealed partial class PodcastEpisodeGroupViewModel : ObservableObject
{
    public PodcastEpisodeGroupViewModel(
        string title,
        string? imageUrl,
        IReadOnlyList<LibraryEpisodeDto> episodes,
        ICommand playEpisodeCommand,
        ICommand selectEpisodeCommand,
        string? summaryOverride = null,
        bool showHeader = true)
    {
        Title = title;
        ImageUrl = imageUrl;
        Episodes = new ObservableCollection<LibraryEpisodeDto>(episodes);
        PlayEpisodeCommand = playEpisodeCommand;
        SelectEpisodeCommand = selectEpisodeCommand;
        EpisodeCount = episodes.Count;
        LastEpisodeAddedAt = episodes.Count == 0 ? DateTime.MinValue : episodes.Max(static episode => episode.AddedAt);
        TotalDuration = FormatDuration(episodes.Sum(static episode => episode.Duration.TotalSeconds));
        SummaryOverride = summaryOverride;
        ShowHeader = showHeader;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandGlyph))]
    private bool _isExpanded;

    public string Title { get; }
    public string? ImageUrl { get; }
    public ObservableCollection<LibraryEpisodeDto> Episodes { get; }
    public ICommand PlayEpisodeCommand { get; }
    public ICommand SelectEpisodeCommand { get; }
    public int EpisodeCount { get; }
    public DateTime LastEpisodeAddedAt { get; }
    public string TotalDuration { get; }
    public bool ShowHeader { get; }
    private string? SummaryOverride { get; }

    public string Summary => SummaryOverride ?? (EpisodeCount == 1
        ? $"1 episode - {TotalDuration}"
        : $"{EpisodeCount} episodes - {TotalDuration}");

    public string ExpandGlyph => IsExpanded ? "\uE70E" : "\uE70D";

    [RelayCommand]
    private void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
    }

    private static string FormatDuration(double totalSeconds)
    {
        var ts = TimeSpan.FromSeconds(totalSeconds);
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours} hr {ts.Minutes} min";
        return $"{ts.Minutes} min";
    }
}

internal sealed record ShowArchivePreview(
    ShowDetailDto Detail,
    IReadOnlyList<LibraryEpisodeDto> Episodes,
    int TotalEpisodes);

public sealed record PodcastEpisodeHeaderRow(
    string Title,
    string? ImageUrl);
