using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using ReactiveUI;
using Windows.UI;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Helpers.Playback;
using Wavee.UI.Contracts;

namespace Wavee.UI.WinUI.ViewModels;

public enum ShowEpisodeFilter { All, Unplayed, InProgress, Completed }
public enum ShowEpisodeSort { Newest, Oldest }

/// <summary>
/// ViewModel for <c>ShowPage</c>. Loads queryShowMetadataV2 + extended-metadata
/// episode batches + internalLinkRecommenderShow in parallel, builds the
/// theme-aware palette brushes from the show's <c>extractedColorSet</c>, and
/// surfaces the filter/sort/search state for the right column.
/// </summary>
public sealed partial class ShowViewModel : ReactiveObject, ITabBarItemContent, IDisposable
{
    private const long PodcastProgressUiDeltaMs = 5_000;
    private const long PodcastCompletedThresholdMs = 90_000;

    private readonly IPodcastService _podcastService;
    private readonly ITrackLikeService? _likeService;
    private readonly ILibraryDataService? _libraryDataService;
    private readonly IPlaybackStateService _playbackStateService;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly ILogger? _logger;

    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _progressRefreshCts;
    private bool _disposed;
    private bool _playbackEpisodeRefreshScheduled;
    private string? _playbackEpisodeUri;
    private long _playbackEpisodePositionMs;
    private long _playbackEpisodeDurationMs;
    private bool _isDarkTheme;
    private ShowDetailDto? _currentDetail;

    private string _showId = "";
    private string _showUri = "";
    private string _showName = "";
    private string? _publisherName;
    private string? _coverArtUrl;
    private string? _description;
    private bool _isExplicit;
    private bool _isExclusive;
    private bool _isVideoShow;
    private bool _isFollowing;
    private bool _isLoading = true;
    private bool _hasError;
    private string? _errorMessage;
    private string? _ratingLine;
    private string? _shareUrl;
    private double _averageRating;
    private long _totalRatings;
    private bool _showAverageRating;
    private int _totalEpisodes;
    private string _episodeCountLine = "";
    private string _listeningSummaryLine = "";
    private string _archiveSummaryLine = "";
    private string _searchQuery = "";
    private ShowEpisodeFilter _filter = ShowEpisodeFilter.All;
    private ShowEpisodeSort _sort = ShowEpisodeSort.Newest;

    private List<ShowEpisodeDto> _allEpisodes = new();
    private IReadOnlyList<ShowEpisodeDto> _filteredEpisodes = Array.Empty<ShowEpisodeDto>();
    private IReadOnlyList<ShowEpisodeDto> _listenNextEpisodes = Array.Empty<ShowEpisodeDto>();
    private ShowEpisodeDto? _resumeEpisode;
    private IReadOnlyList<ShowEpisodeDto> _upNextEpisodes = Array.Empty<ShowEpisodeDto>();
    private ObservableCollection<ShowTopicDto> _topics = new();
    private ObservableCollection<ShowRecommendationDto> _recommendations = new();

    private Brush? _paletteBackdropBrush;
    private Brush? _paletteAccentPillBrush;
    private Brush? _paletteAccentPillForegroundBrush;
    private Brush? _paletteCoverColorBrush;
    private ShowPaletteDto? _palette;

    public TabItemParameter? TabItemParameter { get; private set; }
    public event EventHandler<TabItemParameter>? ContentChanged;

    public string ShowId { get => _showId; private set => this.RaiseAndSetIfChanged(ref _showId, value); }
    public string ShowUri { get => _showUri; private set => this.RaiseAndSetIfChanged(ref _showUri, value); }
    public string ShowName
    {
        get => _showName;
        private set
        {
            this.RaiseAndSetIfChanged(ref _showName, value);
            this.RaisePropertyChanged(nameof(BreadcrumbItems));
            UpdateTabTitle();
        }
    }
    public string? PublisherName { get => _publisherName; private set => this.RaiseAndSetIfChanged(ref _publisherName, value); }
    public string? CoverArtUrl { get => _coverArtUrl; private set => this.RaiseAndSetIfChanged(ref _coverArtUrl, value); }
    public string? Description { get => _description; private set => this.RaiseAndSetIfChanged(ref _description, value); }
    public bool IsExplicit { get => _isExplicit; private set => this.RaiseAndSetIfChanged(ref _isExplicit, value); }
    public bool IsExclusive { get => _isExclusive; private set => this.RaiseAndSetIfChanged(ref _isExclusive, value); }
    public bool IsVideoShow { get => _isVideoShow; private set => this.RaiseAndSetIfChanged(ref _isVideoShow, value); }
    public bool IsFollowing { get => _isFollowing; set => this.RaiseAndSetIfChanged(ref _isFollowing, value); }
    public bool IsLoading { get => _isLoading; set => this.RaiseAndSetIfChanged(ref _isLoading, value); }
    public bool HasError { get => _hasError; set => this.RaiseAndSetIfChanged(ref _hasError, value); }
    public string? ErrorMessage { get => _errorMessage; set => this.RaiseAndSetIfChanged(ref _errorMessage, value); }
    public string? RatingLine { get => _ratingLine; private set => this.RaiseAndSetIfChanged(ref _ratingLine, value); }
    public bool ShowAverageRating { get => _showAverageRating; private set => this.RaiseAndSetIfChanged(ref _showAverageRating, value); }
    public double AverageRating { get => _averageRating; private set => this.RaiseAndSetIfChanged(ref _averageRating, value); }
    public long TotalRatings { get => _totalRatings; private set => this.RaiseAndSetIfChanged(ref _totalRatings, value); }
    public int TotalEpisodes { get => _totalEpisodes; private set => this.RaiseAndSetIfChanged(ref _totalEpisodes, value); }
    public string EpisodeCountLine { get => _episodeCountLine; private set => this.RaiseAndSetIfChanged(ref _episodeCountLine, value); }
    public string ListeningSummaryLine { get => _listeningSummaryLine; private set => this.RaiseAndSetIfChanged(ref _listeningSummaryLine, value); }
    public string ArchiveSummaryLine { get => _archiveSummaryLine; private set => this.RaiseAndSetIfChanged(ref _archiveSummaryLine, value); }
    public string? ShareUrl
    {
        get => _shareUrl;
        private set
        {
            this.RaiseAndSetIfChanged(ref _shareUrl, value);
            this.RaisePropertyChanged(nameof(CanShare));
        }
    }
    public bool CanShare => !string.IsNullOrEmpty(_shareUrl);

    public ObservableCollection<ShowTopicDto> Topics
    {
        get => _topics;
        private set => this.RaiseAndSetIfChanged(ref _topics, value);
    }
    public bool HasTopics => Topics.Count > 0;

    public ObservableCollection<ShowRecommendationDto> Recommendations
    {
        get => _recommendations;
        private set => this.RaiseAndSetIfChanged(ref _recommendations, value);
    }
    public bool HasRecommendations => Recommendations.Count > 0;

    public IReadOnlyList<string> BreadcrumbItems
    {
        get
        {
            var showName = string.IsNullOrWhiteSpace(ShowName) ? "Show" : ShowName;
            return new[] { "Podcasts", showName };
        }
    }

    public IReadOnlyList<ShowEpisodeDto> FilteredEpisodes
    {
        get => _filteredEpisodes;
        private set
        {
            this.RaiseAndSetIfChanged(ref _filteredEpisodes, value);
            this.RaisePropertyChanged(nameof(HasEpisodes));
            this.RaisePropertyChanged(nameof(NoEpisodesMatch));
        }
    }
    public bool HasEpisodes => FilteredEpisodes.Count > 0;
    public bool NoEpisodesMatch => !IsLoading && _allEpisodes.Count > 0 && FilteredEpisodes.Count == 0;

    public IReadOnlyList<ShowEpisodeDto> ListenNextEpisodes
    {
        get => _listenNextEpisodes;
        private set
        {
            this.RaiseAndSetIfChanged(ref _listenNextEpisodes, value);
            this.RaisePropertyChanged(nameof(HasListenNextEpisodes));
        }
    }
    public bool HasListenNextEpisodes => ListenNextEpisodes.Count > 0;

    /// <summary>The single in-progress episode promoted out of the listen-next list to drive
    /// the cinematic Resume banner. Null when the show has no resumable episode in scope.</summary>
    public ShowEpisodeDto? ResumeEpisode
    {
        get => _resumeEpisode;
        private set
        {
            this.RaiseAndSetIfChanged(ref _resumeEpisode, value);
            this.RaisePropertyChanged(nameof(HasResumeEpisode));
        }
    }
    public bool HasResumeEpisode => _resumeEpisode is not null;

    /// <summary>Listen-next episodes minus the one promoted into the Resume banner. Drives
    /// the auto-fit "UP NEXT" grid below the banner.</summary>
    public IReadOnlyList<ShowEpisodeDto> UpNextEpisodes
    {
        get => _upNextEpisodes;
        private set
        {
            this.RaiseAndSetIfChanged(ref _upNextEpisodes, value);
            this.RaisePropertyChanged(nameof(HasUpNextEpisodes));
        }
    }
    public bool HasUpNextEpisodes => _upNextEpisodes.Count > 0;

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            var old = _searchQuery;
            this.RaiseAndSetIfChanged(ref _searchQuery, value ?? "");
            if (old != _searchQuery) ApplyFilterAndSort();
        }
    }

    public ShowEpisodeFilter Filter
    {
        get => _filter;
        set
        {
            var old = _filter;
            this.RaiseAndSetIfChanged(ref _filter, value);
            if (old != value) ApplyFilterAndSort();
        }
    }

    public ShowEpisodeSort Sort
    {
        get => _sort;
        set
        {
            var old = _sort;
            this.RaiseAndSetIfChanged(ref _sort, value);
            if (old != value) ApplyFilterAndSort();
        }
    }

    public Brush? PaletteBackdropBrush { get => _paletteBackdropBrush; private set => this.RaiseAndSetIfChanged(ref _paletteBackdropBrush, value); }
    public Brush? PaletteAccentPillBrush { get => _paletteAccentPillBrush; private set => this.RaiseAndSetIfChanged(ref _paletteAccentPillBrush, value); }
    public Brush? PaletteAccentPillForegroundBrush { get => _paletteAccentPillForegroundBrush; private set => this.RaiseAndSetIfChanged(ref _paletteAccentPillForegroundBrush, value); }

    /// <summary>
    /// Solid full-opacity brush of the cover's dominant tone (Spotify's
    /// <c>BackgroundBase</c> from <c>extractedColorSet</c>). Use this for surfaces
    /// that should be coloured BY the show — Resume banner, up-next number tags,
    /// hero gradients. <see cref="PaletteAccentPillBrush"/> is Spotify's
    /// <c>TextBrightAccent</c>, which ships as Spotify-brand green for many shows
    /// and shouldn't be used as the show's identity colour.
    /// </summary>
    public Brush? PaletteCoverColorBrush { get => _paletteCoverColorBrush; private set => this.RaiseAndSetIfChanged(ref _paletteCoverColorBrush, value); }

    public ShowViewModel(
        IPodcastService podcastService,
        IPlaybackStateService playbackStateService,
        ITrackLikeService? likeService = null,
        ILibraryDataService? libraryDataService = null,
        ILogger<ShowViewModel>? logger = null)
    {
        _podcastService = podcastService ?? throw new ArgumentNullException(nameof(podcastService));
        _playbackStateService = playbackStateService ?? throw new ArgumentNullException(nameof(playbackStateService));
        _likeService = likeService;
        _libraryDataService = libraryDataService;
        _logger = logger;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        AttachLongLivedServices();
    }

    // Long-lived singleton subscriptions are attached lazily and detached on
    // Dispose so the (Transient) VM is not pinned by singleton invocation lists
    // across navigations.
    private bool _longLivedAttached;

    private void AttachLongLivedServices()
    {
        if (_longLivedAttached) return;
        _longLivedAttached = true;
        if (_likeService != null)
            _likeService.SaveStateChanged += OnSaveStateChanged;
        _playbackStateService.PropertyChanged += OnPlaybackStateChanged;
        if (_libraryDataService != null)
        {
            _libraryDataService.DataChanged += OnLibraryDataChanged;
            _libraryDataService.PodcastEpisodeProgressChanged += OnPodcastEpisodeProgressChanged;
        }
    }

    private void DetachLongLivedServices()
    {
        if (!_longLivedAttached) return;
        _longLivedAttached = false;
        if (_likeService != null)
            _likeService.SaveStateChanged -= OnSaveStateChanged;
        _playbackStateService.PropertyChanged -= OnPlaybackStateChanged;
        if (_libraryDataService != null)
        {
            _libraryDataService.DataChanged -= OnLibraryDataChanged;
            _libraryDataService.PodcastEpisodeProgressChanged -= OnPodcastEpisodeProgressChanged;
        }
    }

    /// <summary>Entry-point from <c>ShowPage.OnNavigatedTo</c>.</summary>
    public void Activate(string showUriOrId)
    {
        AttachLongLivedServices();
        if (string.IsNullOrWhiteSpace(showUriOrId)) return;

        var showUri = NormalizeShowUri(showUriOrId);
        if (string.Equals(_showUri, showUri, StringComparison.Ordinal) && _allEpisodes.Count > 0 && !_hasError)
        {
            // Same show — keep what we already have and just refresh follow state.
            RefreshFollowState();
            return;
        }

        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();

        ResetState(showUri);
        TabItemParameter = new TabItemParameter(NavigationPageType.Show, showUri) { Title = "Show" };

        _ = LoadAsync(showUri, _loadCts.Token);
    }

    public void PrefillFrom(ContentNavigationParameter parameter)
    {
        if (parameter is null)
            return;

        if (!string.IsNullOrWhiteSpace(parameter.Title))
            ShowName = parameter.Title;
        if (!string.IsNullOrWhiteSpace(parameter.Subtitle))
            PublisherName = parameter.Subtitle;
        if (!string.IsNullOrWhiteSpace(parameter.ImageUrl))
            CoverArtUrl = parameter.ImageUrl;
        if (TabItemParameter != null && !string.IsNullOrWhiteSpace(parameter.Title))
        {
            TabItemParameter.Title = parameter.Title;
            ContentChanged?.Invoke(this, TabItemParameter);
        }
    }

    private void ResetState(string showUri)
    {
        _progressRefreshCts?.Cancel();
        _progressRefreshCts?.Dispose();
        _progressRefreshCts = null;

        ShowUri = showUri;
        ShowId = ExtractIdFromUri(showUri);
        ShowName = "";
        PublisherName = null;
        CoverArtUrl = null;
        Description = null;
        IsExplicit = false;
        IsExclusive = false;
        IsVideoShow = false;
        ShareUrl = null;
        RatingLine = null;
        ShowAverageRating = false;
        AverageRating = 0;
        TotalRatings = 0;
        TotalEpisodes = 0;
        EpisodeCountLine = "";
        ListeningSummaryLine = "";
        ArchiveSummaryLine = "";

        Topics.Clear();
        this.RaisePropertyChanged(nameof(HasTopics));
        Recommendations.Clear();
        this.RaisePropertyChanged(nameof(HasRecommendations));

        _allEpisodes = new List<ShowEpisodeDto>();
        FilteredEpisodes = Array.Empty<ShowEpisodeDto>();
        ListenNextEpisodes = Array.Empty<ShowEpisodeDto>();
        ResumeEpisode = null;
        UpNextEpisodes = Array.Empty<ShowEpisodeDto>();

        _palette = null;
        ApplyTheme(_isDarkTheme);

        IsLoading = true;
        HasError = false;
        ErrorMessage = null;
    }

    private async Task LoadAsync(string showUri, CancellationToken ct)
    {
        try
        {
            // Hero metadata first — without it we can't render the page header
            // or kick off the episode batch (which needs the URI list).
            var detail = await _podcastService.GetShowDetailAsync(showUri, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return;

            if (detail is null)
            {
                _dispatcherQueue.TryEnqueue(() =>
                {
                    HasError = true;
                    ErrorMessage = "We couldn't load this show.";
                    IsLoading = false;
                });
                return;
            }

            _currentDetail = detail;
            _dispatcherQueue.TryEnqueue(() => ApplyDetail(detail));

            // Episodes + recommendations in parallel — neither blocks the other.
            var episodesTask = detail.EpisodeUris.Count > 0
                ? LoadEpisodesWithRetryAsync(detail.EpisodeUris, ct)
                : Task.FromResult<IReadOnlyList<ShowEpisodeDto>>(Array.Empty<ShowEpisodeDto>());
            var recommendationsTask = _podcastService.GetRecommendedShowsAsync(showUri, ct);

            try
            {
                await Task.WhenAll(episodesTask, recommendationsTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "ShowViewModel parallel fetch had a partial failure for {Uri}", showUri);
            }

            if (ct.IsCancellationRequested) return;

            var episodes = episodesTask.IsCompletedSuccessfully ? episodesTask.Result : Array.Empty<ShowEpisodeDto>();
            var recs = recommendationsTask.IsCompletedSuccessfully ? recommendationsTask.Result : Array.Empty<ShowRecommendationDto>();

            _dispatcherQueue.TryEnqueue(() =>
            {
                _allEpisodes = episodes.ToList();
                EpisodeCountLine = FormatEpisodeCount(_allEpisodes.Count, detail.TotalEpisodes);
                ApplyPlaybackStateToEpisodes(rebuild: false);
                UpdateListenNextEpisodes();
                ApplyFilterAndSort();

                Recommendations.Clear();
                foreach (var r in recs) Recommendations.Add(r);
                this.RaisePropertyChanged(nameof(HasRecommendations));

                IsLoading = false;
            });
        }
        catch (OperationCanceledException)
        {
            // Expected when navigating away mid-load.
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "ShowViewModel.LoadAsync failed for {Uri}", showUri);
            _dispatcherQueue.TryEnqueue(() =>
            {
                HasError = true;
                ErrorMessage = ex.Message;
                IsLoading = false;
            });
        }
    }

    private async Task<IReadOnlyList<ShowEpisodeDto>> LoadEpisodesWithRetryAsync(
        IReadOnlyList<string> episodeUris,
        CancellationToken ct)
    {
        const int maxEmptyRetries = 2;
        Exception? lastError = null;

        for (var attempt = 0; attempt <= maxEmptyRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var episodes = await _podcastService.GetEpisodesAsync(episodeUris, ct).ConfigureAwait(false);
                if (episodes.Count > 0 || episodeUris.Count == 0 || attempt == maxEmptyRetries)
                    return episodes;

                _logger?.LogDebug(
                    "Episode archive resolved 0 of {RequestedCount} episodes; retrying attempt {Attempt}/{MaxAttempts}",
                    episodeUris.Count,
                    attempt + 1,
                    maxEmptyRetries + 1);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < maxEmptyRetries)
            {
                lastError = ex;
                _logger?.LogDebug(
                    ex,
                    "Episode archive load failed; retrying attempt {Attempt}/{MaxAttempts}",
                    attempt + 1,
                    maxEmptyRetries + 1);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), ct).ConfigureAwait(false);
        }

        if (lastError is not null)
            throw lastError;

        return Array.Empty<ShowEpisodeDto>();
    }

    private void ApplyDetail(ShowDetailDto detail)
    {
        ShowName = detail.Name;
        PublisherName = detail.PublisherName;
        if (!string.IsNullOrEmpty(detail.CoverArtUrl))
            CoverArtUrl = detail.CoverArtUrl;
        Description = detail.PlainDescription;
        IsExplicit = detail.IsExplicit;
        IsExclusive = detail.IsExclusive;
        IsVideoShow = string.Equals(detail.MediaType, "VIDEO", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(detail.MediaType, "MIXED", StringComparison.OrdinalIgnoreCase);
        ShareUrl = detail.ShareUrl;
        ShowAverageRating = detail.ShowAverageRating;
        AverageRating = detail.AverageRating;
        TotalRatings = detail.TotalRatings;
        RatingLine = BuildRatingLine(detail);
        TotalEpisodes = detail.TotalEpisodes;

        Topics.Clear();
        foreach (var topic in detail.Topics) Topics.Add(topic);
        this.RaisePropertyChanged(nameof(HasTopics));

        _palette = detail.Palette;
        ApplyTheme(_isDarkTheme);

        // Server flag is the source of truth for whether *this account* follows
        // the show; the local cache catches up on the next library sync. Use it
        // to seed the heart so the page paints accurately on first render.
        _isFollowing = detail.IsSavedOnServer;
        RefreshFollowState();
    }

    public void ApplyTheme(bool isDark)
    {
        _isDarkTheme = isDark;
        var tier = _palette is null
            ? null
            : (isDark
                ? (_palette.HigherContrast ?? _palette.HighContrast)
                : (_palette.HighContrast ?? _palette.HigherContrast));

        if (tier == null)
        {
            PaletteBackdropBrush = null;
            PaletteAccentPillBrush = null;
            PaletteAccentPillForegroundBrush = null;
            PaletteCoverColorBrush = null;
            return;
        }

        var bg = Color.FromArgb(255, tier.BackgroundR, tier.BackgroundG, tier.BackgroundB);
        var bgTint = Color.FromArgb(255, tier.BackgroundTintedR, tier.BackgroundTintedG, tier.BackgroundTintedB);

        PaletteBackdropBrush = new SolidColorBrush(Color.FromArgb(
            (byte)(isDark ? 60 : 38), bg.R, bg.G, bg.B));

        // Cover-derived accent. Mirrors ArtistViewModel.ApplyTheme: Spotify's
        // TextBrightAccent often resolves to brand green (#1DB954) regardless of
        // the cover photo, which made every show's accent look identical and
        // disconnected from the visual. BackgroundTinted lifted to a target max
        // of 210 keeps the show's actual hue while staying legible as a CTA fill.
        var accentBase = TintColorHelper.BrightenForTint(bgTint, targetMax: 210);
        PaletteAccentPillBrush = new SolidColorBrush(accentBase);
        var accentLuma = (accentBase.R * 299 + accentBase.G * 587 + accentBase.B * 114) / 1000;
        PaletteAccentPillForegroundBrush = new SolidColorBrush(
            accentLuma > 160 ? Color.FromArgb(255, 0, 0, 0) : Color.FromArgb(255, 255, 255, 255));

        // Same lifted cover tone — exposed as a separate brush so future surfaces
        // can pick up the show's identity colour without going through the
        // "pill / button" naming.
        PaletteCoverColorBrush = new SolidColorBrush(accentBase);
    }

    private void UpdateTabTitle()
    {
        if (TabItemParameter != null && !string.IsNullOrEmpty(_showName))
        {
            TabItemParameter.Title = _showName;
            ContentChanged?.Invoke(this, TabItemParameter);
        }
    }

    private void ApplyFilterAndSort()
    {
        IEnumerable<ShowEpisodeDto> result = _allEpisodes;

        result = Filter switch
        {
            ShowEpisodeFilter.Unplayed => result.Where(e => e.PlayedState == "NOT_STARTED"),
            ShowEpisodeFilter.InProgress => result.Where(e => e.PlayedState == "IN_PROGRESS"),
            ShowEpisodeFilter.Completed => result.Where(e => e.PlayedState == "COMPLETED"),
            _ => result,
        };

        var query = SearchQuery?.Trim();
        if (!string.IsNullOrEmpty(query))
        {
            result = result.Where(e =>
                (!string.IsNullOrEmpty(e.Title) && e.Title.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(e.DescriptionPreview) && e.DescriptionPreview!.Contains(query, StringComparison.OrdinalIgnoreCase)));
        }

        result = Sort switch
        {
            ShowEpisodeSort.Oldest => result.OrderBy(e => e.ReleaseDate ?? DateTimeOffset.MinValue),
            _ => result.OrderByDescending(e => e.ReleaseDate ?? DateTimeOffset.MinValue),
        };

        var list = result.ToList();
        FilteredEpisodes = list;
        ArchiveSummaryLine = BuildArchiveSummaryLine(list.Count, _allEpisodes.Count);
    }

    private void OnPlaybackStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed)
            return;

        if (e.PropertyName == nameof(IPlaybackStateService.CurrentTrackId) ||
            e.PropertyName == nameof(IPlaybackStateService.CurrentContext) ||
            e.PropertyName == nameof(IPlaybackStateService.CurrentAlbumId) ||
            e.PropertyName == nameof(IPlaybackStateService.Position) ||
            e.PropertyName == nameof(IPlaybackStateService.Duration) ||
            e.PropertyName == nameof(IPlaybackStateService.IsPlaying))
        {
            SchedulePlaybackEpisodeRefresh();
        }
    }

    private void SchedulePlaybackEpisodeRefresh()
    {
        if (_playbackEpisodeRefreshScheduled)
            return;

        _playbackEpisodeRefreshScheduled = true;
        if (!_dispatcherQueue.TryEnqueue(() =>
            {
                _playbackEpisodeRefreshScheduled = false;
                ApplyPlaybackStateToEpisodes();
            }))
        {
            _playbackEpisodeRefreshScheduled = false;
        }
    }

    private void OnLibraryDataChanged(object? sender, EventArgs e)
    {
        if (_disposed || _libraryDataService is null)
            return;

        _dispatcherQueue.TryEnqueue(StartEpisodeProgressRefresh);
    }

    private void OnPodcastEpisodeProgressChanged(object? sender, PodcastEpisodeProgressChangedEventArgs e)
    {
        if (_disposed)
            return;

        _dispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed)
                return;

            var changed = ApplyPodcastEpisodeProgress(e);
            changed |= ApplyPlaybackStateToEpisodes(rebuild: false);
            if (changed)
                RebuildEpisodeViews();
        });
    }

    private bool ApplyPodcastEpisodeProgress(PodcastEpisodeProgressChangedEventArgs e)
    {
        var positionMs = (long)Math.Max(0, e.Progress.PlayedPosition.TotalMilliseconds);
        var changed = TryUpdateEpisodeProgress(e.EpisodeUri, positionMs, e.Progress.PlayedState, minPositionDeltaMs: 0);
        if (!string.IsNullOrWhiteSpace(e.AliasUri))
            changed |= TryUpdateEpisodeProgress(e.AliasUri, positionMs, e.Progress.PlayedState, minPositionDeltaMs: 0);
        return changed;
    }

    private void StartEpisodeProgressRefresh()
    {
        if (_disposed || _libraryDataService is null || _allEpisodes.Count == 0)
            return;

        var showUri = _showUri;
        var episodeUris = _allEpisodes
            .Select(static e => e.Uri)
            .Where(static uri => !string.IsNullOrEmpty(uri))
            .ToList();
        if (episodeUris.Count == 0)
            return;

        _progressRefreshCts?.Cancel();
        _progressRefreshCts?.Dispose();
        _progressRefreshCts = new CancellationTokenSource();
        var cts = _progressRefreshCts;

        _ = RefreshEpisodeProgressAsync(showUri, episodeUris, cts.Token);
    }

    private async Task RefreshEpisodeProgressAsync(
        string showUri,
        IReadOnlyList<string> episodeUris,
        CancellationToken ct)
    {
        if (_libraryDataService is null)
            return;

        try
        {
            var updates = new List<(string Uri, long PositionMs, string? State)>();
            foreach (var uri in episodeUris)
            {
                ct.ThrowIfCancellationRequested();

                var progress = await _libraryDataService
                    .GetPodcastEpisodeProgressAsync(uri, ct)
                    .ConfigureAwait(false);
                if (progress is null)
                    continue;

                var positionMs = (long)Math.Max(0, progress.PlayedPosition.TotalMilliseconds);
                updates.Add((uri, positionMs, progress.PlayedState));
                if (!string.IsNullOrEmpty(progress.Uri) &&
                    !string.Equals(progress.Uri, uri, StringComparison.Ordinal))
                {
                    updates.Add((progress.Uri, positionMs, progress.PlayedState));
                }
            }

            _dispatcherQueue.TryEnqueue(() =>
            {
                if (_disposed || ct.IsCancellationRequested || !string.Equals(showUri, _showUri, StringComparison.Ordinal))
                    return;

                var changed = false;
                foreach (var update in updates)
                    changed |= TryUpdateEpisodeProgress(update.Uri, update.PositionMs, update.State, minPositionDeltaMs: 0);

                changed |= ApplyPlaybackStateToEpisodes(rebuild: false);
                if (changed)
                    RebuildEpisodeViews();
            });
        }
        catch (OperationCanceledException)
        {
            // Expected when navigating away or when a newer progress refresh wins.
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to refresh podcast progress for show {ShowUri}", showUri);
        }
    }

    private bool ApplyPlaybackStateToEpisodes(bool rebuild = true)
    {
        var currentEpisodeUri = GetCurrentPlaybackEpisodeUri();
        var currentPositionMs = NormalizePlaybackMilliseconds(_playbackStateService.Position);
        var currentDurationMs = NormalizePlaybackMilliseconds(_playbackStateService.Duration);
        var changed = false;

        if (!string.IsNullOrEmpty(_playbackEpisodeUri) &&
            !string.Equals(_playbackEpisodeUri, currentEpisodeUri, StringComparison.Ordinal))
        {
            var previousState = ResolvePlaybackOverlayState(
                _playbackEpisodePositionMs,
                _playbackEpisodeDurationMs,
                isPlaying: false);
            changed |= TryUpdateEpisodeProgress(
                _playbackEpisodeUri,
                _playbackEpisodePositionMs,
                previousState,
                minPositionDeltaMs: 0);
        }

        if (!string.IsNullOrEmpty(currentEpisodeUri))
        {
            var currentState = ResolvePlaybackOverlayState(
                currentPositionMs,
                currentDurationMs,
                _playbackStateService.IsPlaying);
            changed |= TryUpdateEpisodeProgress(
                currentEpisodeUri,
                currentPositionMs,
                currentState,
                PodcastProgressUiDeltaMs);
        }

        _playbackEpisodeUri = currentEpisodeUri;
        _playbackEpisodePositionMs = currentPositionMs;
        _playbackEpisodeDurationMs = currentDurationMs;

        if (changed && rebuild)
            RebuildEpisodeViews();

        return changed;
    }

    private string? GetCurrentPlaybackEpisodeUri()
        => PlaybackSaveTargetResolver.GetEpisodeUri(_playbackStateService);

    private static long NormalizePlaybackMilliseconds(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return 0;

        return (long)Math.Max(0, value);
    }

    private static string ResolvePlaybackOverlayState(long positionMs, long durationMs, bool isPlaying)
    {
        if (durationMs > 0 && positionMs > 0 && durationMs - positionMs <= PodcastCompletedThresholdMs)
            return "COMPLETED";

        if (isPlaying || positionMs > 0)
            return "IN_PROGRESS";

        return "NOT_STARTED";
    }

    private bool TryUpdateEpisodeProgress(
        string? episodeUri,
        long positionMs,
        string? playedState,
        long minPositionDeltaMs)
    {
        if (string.IsNullOrEmpty(episodeUri) || _allEpisodes.Count == 0)
            return false;

        var index = _allEpisodes.FindIndex(e => string.Equals(e.Uri, episodeUri, StringComparison.Ordinal));
        if (index < 0)
            return false;

        var current = _allEpisodes[index];
        var updated = current.WithPlaybackProgress(positionMs, playedState);
        var positionDelta = Math.Abs(current.PlayedPositionMs - updated.PlayedPositionMs);
        if (string.Equals(current.PlayedState, updated.PlayedState, StringComparison.Ordinal) &&
            positionDelta < minPositionDeltaMs &&
            string.Equals(current.DurationOrRemainingText, updated.DurationOrRemainingText, StringComparison.Ordinal))
        {
            return false;
        }

        _allEpisodes[index] = updated;
        return true;
    }

    private void RebuildEpisodeViews()
    {
        UpdateListenNextEpisodes();
        ApplyFilterAndSort();
    }

    private void UpdateListenNextEpisodes()
    {
        if (_allEpisodes.Count == 0)
        {
            ListenNextEpisodes = Array.Empty<ShowEpisodeDto>();
            ResumeEpisode = null;
            UpNextEpisodes = Array.Empty<ShowEpisodeDto>();
            ListeningSummaryLine = "";
            return;
        }

        const double nearCompleteProgressThreshold = 0.90;

        var chronological = _allEpisodes
            .OrderBy(e => e.ReleaseDate ?? DateTimeOffset.MinValue)
            .ToList();
        // Assign 1-indexed chronological position so cards can render "#N" tags
        // without needing a converter or DTO clone. OrderBy().ToList() reorders
        // references; the assignment mutates the same DTOs the archive list binds to.
        for (var i = 0; i < chronological.Count; i++)
            chronological[i].EpisodeNumber = i + 1;
        var newestFirst = chronological
            .OrderByDescending(e => e.ReleaseDate ?? DateTimeOffset.MinValue)
            .ToList();
        var hasAnyProgress = _allEpisodes.Any(e =>
            e.IsCompleted ||
            e.IsInProgress ||
            e.Progress > 0 ||
            e.PlayedPositionMs > 0);

        var listenNext = new List<ShowEpisodeDto>(4);

        var activeEpisode = !string.IsNullOrEmpty(_playbackEpisodeUri)
            ? chronological.FirstOrDefault(e =>
                string.Equals(e.Uri, _playbackEpisodeUri, StringComparison.Ordinal) &&
                !e.IsCompleted)
            : null;

        var resumeEpisode = activeEpisode ?? newestFirst.FirstOrDefault(e =>
            e.IsInProgress && e.Progress < nearCompleteProgressThreshold);
        AddUnique(listenNext, resumeEpisode);

        var anchorIndex = -1;
        for (var i = chronological.Count - 1; i >= 0; i--)
        {
            var episode = chronological[i];
            if (episode.IsCompleted || episode.Progress >= nearCompleteProgressThreshold)
            {
                anchorIndex = i;
                break;
            }
        }

        if (anchorIndex < 0 && resumeEpisode is not null)
            anchorIndex = chronological.FindIndex(e => string.Equals(e.Uri, resumeEpisode.Uri, StringComparison.Ordinal));

        if (anchorIndex >= 0)
        {
            for (var i = anchorIndex + 1; i < chronological.Count && listenNext.Count < 4; i++)
            {
                var episode = chronological[i];
                if (episode.IsCompleted || episode.Progress >= nearCompleteProgressThreshold)
                    continue;

                AddUnique(listenNext, episode);
            }
        }

        if (listenNext.Count == 0 && !hasAnyProgress)
        {
            foreach (var episode in chronological)
            {
                if (listenNext.Count >= 4)
                    break;

                AddUnique(listenNext, episode);
            }
        }

        if (listenNext.Count == 0)
            listenNext.AddRange(newestFirst.Where(e => !e.IsCompleted).Take(4));

        if (listenNext.Count < 4)
        {
            var fillSource = hasAnyProgress ? newestFirst : chronological;
            foreach (var episode in fillSource)
            {
                if (listenNext.Count >= 4)
                    break;

                if (!episode.IsCompleted)
                    AddUnique(listenNext, episode);
            }
        }

        if (listenNext.Count == 0)
            listenNext.AddRange(newestFirst.Take(4));

        ListenNextEpisodes = listenNext;

        // Promote the first in-progress episode (with real progress) into the
        // Resume banner; everything else falls into the Up Next grid. When there's
        // no resumable episode, the banner is hidden and all listen-next entries
        // render in the grid.
        var firstInProgress = listenNext.FirstOrDefault(e => e.IsInProgress && e.HasProgress);
        ResumeEpisode = firstInProgress;
        UpNextEpisodes = firstInProgress is null
            ? listenNext.ToList()
            : listenNext.Where(e => !ReferenceEquals(e, firstInProgress)).ToList();

        var inProgressCount = _allEpisodes.Count(e => e.IsInProgress);
        var unplayedCount = _allEpisodes.Count(e => e.PlayedState == "NOT_STARTED");
        var completedCount = _allEpisodes.Count(e => e.IsCompleted);
        ListeningSummaryLine = BuildListeningSummaryLine(
            inProgressCount,
            unplayedCount,
            completedCount,
            anchorIndex >= 0,
            !hasAnyProgress);
    }

    private static void AddUnique(ICollection<ShowEpisodeDto> target, ShowEpisodeDto? episode)
    {
        if (episode is null)
            return;

        if (target.Any(e => string.Equals(e.Uri, episode.Uri, StringComparison.Ordinal)))
            return;

        target.Add(episode);
    }

    private static string FormatEpisodeCount(int loaded, int total)
    {
        if (total <= 0) return loaded == 1 ? "1 episode" : $"{loaded} episodes";
        var word = total == 1 ? "episode" : "episodes";
        return $"{total.ToString("N0", CultureInfo.CurrentCulture)} {word}";
    }

    private static string BuildArchiveSummaryLine(int visible, int total)
    {
        if (total <= 0) return "";
        if (visible == total) return $"{total.ToString("N0", CultureInfo.CurrentCulture)} available";
        return $"{visible.ToString("N0", CultureInfo.CurrentCulture)} of {total.ToString("N0", CultureInfo.CurrentCulture)} shown";
    }

    private static string BuildListeningSummaryLine(
        int inProgress,
        int unplayed,
        int completed,
        bool hasProgressAnchor,
        bool startsFromBeginning)
    {
        var parts = new List<string>(4);
        if (startsFromBeginning) parts.Add("Start from the beginning");
        else if (hasProgressAnchor) parts.Add("Up next from your progress");
        if (inProgress > 0) parts.Add($"{inProgress.ToString("N0", CultureInfo.CurrentCulture)} in progress");
        if (unplayed > 0) parts.Add($"{unplayed.ToString("N0", CultureInfo.CurrentCulture)} unplayed");
        if (completed > 0) parts.Add($"{completed.ToString("N0", CultureInfo.CurrentCulture)} played");
        return parts.Count == 0 ? "Replay recent episodes" : string.Join(" | ", parts);
    }

    private static string? BuildRatingLine(ShowDetailDto detail)
    {
        if (!detail.ShowAverageRating || detail.AverageRating <= 0) return null;
        var avg = detail.AverageRating.ToString("0.0", CultureInfo.CurrentCulture);
        var count = detail.TotalRatings.ToString("N0", CultureInfo.CurrentCulture);
        return detail.TotalRatings > 0 ? $"★ {avg}  ·  {count} ratings" : $"★ {avg}";
    }

    [RelayCommand]
    private void PlayShow()
    {
        if (ListenNextEpisodes.FirstOrDefault() is { } next)
        {
            BuildQueueAndPlayFromChronological(next);
            return;
        }

        var startIndex = ChooseStartIndex();
        BuildQueueAndPlay(startIndex);
    }

    [RelayCommand]
    private void PlayEpisode(ShowEpisodeDto? episode)
    {
        if (episode is null) return;
        if (ListenNextEpisodes.Any(e => string.Equals(e.Uri, episode.Uri, StringComparison.Ordinal)))
        {
            BuildQueueAndPlayFromChronological(episode);
            return;
        }

        var visible = FilteredEpisodes.ToList();
        var index = visible.FindIndex(e => string.Equals(e.Uri, episode.Uri, StringComparison.Ordinal));
        if (index >= 0)
        {
            BuildQueueAndPlay(visible, index);
            return;
        }

        index = _allEpisodes.FindIndex(e => string.Equals(e.Uri, episode.Uri, StringComparison.Ordinal));
        BuildQueueAndPlay(_allEpisodes, index >= 0 ? index : 0);
    }

    private void BuildQueueAndPlayFromChronological(ShowEpisodeDto episode)
    {
        var chronological = _allEpisodes
            .OrderBy(e => e.ReleaseDate ?? DateTimeOffset.MinValue)
            .ToList();
        var index = chronological.FindIndex(e => string.Equals(e.Uri, episode.Uri, StringComparison.Ordinal));
        BuildQueueAndPlay(chronological, index >= 0 ? index : 0);
    }

    private int ChooseStartIndex()
    {
        if (_allEpisodes.Count == 0) return 0;

        // Prefer the most-recent in-progress episode, then the newest unplayed,
        // then fall back to the newest. That matches the typical "Play" CTA UX
        // on Spotify desktop.
        var ordered = FilteredEpisodes;
        for (var i = 0; i < ordered.Count; i++)
            if (ordered[i].PlayedState == "IN_PROGRESS") return i;
        for (var i = 0; i < ordered.Count; i++)
            if (ordered[i].PlayedState == "NOT_STARTED") return i;
        return 0;
    }

    private void BuildQueueAndPlay(int startIndex)
        => BuildQueueAndPlay(FilteredEpisodes, startIndex);

    private void BuildQueueAndPlay(IReadOnlyList<ShowEpisodeDto> episodes, int startIndex)
    {
        if (episodes.Count == 0) return;
        startIndex = Math.Clamp(startIndex, 0, episodes.Count - 1);

        var queue = episodes.Select(e => new QueueItem
        {
            TrackId = e.Uri,
            Title = e.Title,
            ArtistName = ShowName,
            AlbumArt = e.CoverArtUrl ?? CoverArtUrl,
            DurationMs = e.DurationMs,
            IsUserQueued = false,
        }).ToList();

        var context = new PlaybackContextInfo
        {
            ContextUri = ShowUri,
            Type = PlaybackContextType.Show,
            Name = ShowName,
            ImageUrl = CoverArtUrl,
        };

        _playbackStateService.LoadQueue(queue, context, startIndex);
    }

    [RelayCommand]
    private void ToggleFollow()
    {
        if (_likeService == null || string.IsNullOrEmpty(ShowUri)) return;
        var wasSaved = _likeService.IsSaved(SavedItemType.Show, ShowUri);
        IsFollowing = !wasSaved;
        _likeService.ToggleSave(SavedItemType.Show, ShowUri, wasSaved);
    }

    [RelayCommand(CanExecute = nameof(CanShare))]
    private void Share()
    {
        if (string.IsNullOrEmpty(ShareUrl)) return;
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(ShareUrl);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    [RelayCommand]
    private void OpenRecommendation(ShowRecommendationDto? rec)
    {
        if (rec is null || string.IsNullOrEmpty(rec.Uri)) return;
        NavigationHelpers.OpenShowPage(rec.Uri, rec.Name, openInNewTab: NavigationHelpers.IsCtrlPressed());
    }

    private void RefreshFollowState()
    {
        if (_likeService == null || string.IsNullOrEmpty(ShowUri)) return;
        var saved = _likeService.IsSaved(SavedItemType.Show, ShowUri);
        // Server flag wins over a stale empty cache on first paint, but local
        // toggles immediately update IsFollowing through ToggleSave.
        if (saved) IsFollowing = true;
        else if (_currentDetail is null || !_currentDetail.IsSavedOnServer) IsFollowing = false;
    }

    private void OnSaveStateChanged()
    {
        _dispatcherQueue.TryEnqueue(RefreshFollowState);
    }

    private static string NormalizeShowUri(string showIdOrUri)
    {
        const string prefix = "spotify:show:";
        if (showIdOrUri.StartsWith(prefix, StringComparison.Ordinal))
            return showIdOrUri;
        return $"{prefix}{showIdOrUri}";
    }

    private static string ExtractIdFromUri(string uri)
    {
        if (string.IsNullOrEmpty(uri)) return "";
        var idx = uri.LastIndexOf(':');
        return idx >= 0 && idx < uri.Length - 1 ? uri[(idx + 1)..] : uri;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        _progressRefreshCts?.Cancel();
        _progressRefreshCts?.Dispose();
        _progressRefreshCts = null;

        DetachLongLivedServices();

        _allEpisodes.Clear();
        FilteredEpisodes = Array.Empty<ShowEpisodeDto>();
        Topics.Clear();
        Recommendations.Clear();
    }
}
