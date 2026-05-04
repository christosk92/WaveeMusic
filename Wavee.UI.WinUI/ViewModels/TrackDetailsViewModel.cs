using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Wavee.Core.Http;
using Wavee.Core.Http.Pathfinder;
using Wavee.UI.Contracts;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// View model for the track/artist details right panel.
/// Subscribes to <see cref="IPlaybackStateService"/> for track changes,
/// fetches data via the queryNpvArtist Pathfinder endpoint.
/// </summary>
public sealed partial class TrackDetailsViewModel : ObservableObject, IDisposable
{
    private const int MaxPodcastCommentLength = 500;

    private readonly IPlaybackStateService _playbackState;
    private readonly IPathfinderClient _pathfinder;
    private readonly ITrackCreditsService _creditsService;
    private readonly ILibraryDataService _libraryDataService;
    private readonly IMediaOverrideService _mediaOverrideService;
    private readonly ILogger? _logger;

    private string? _loadedTrackId;
    private CancellationTokenSource? _fetchCts;
    private bool _disposed;

    // ── Loading state ──

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasData;

    [ObservableProperty]
    private string? _errorMessage;

    // ── Artist section ──

    [ObservableProperty]
    private string? _artistName;

    [ObservableProperty]
    private string? _artistAvatarUrl;

    [ObservableProperty]
    private bool _isVerified;

    [ObservableProperty]
    private string? _followers;

    [ObservableProperty]
    private string? _monthlyListeners;

    [ObservableProperty]
    private string? _biographyText;

    [ObservableProperty]
    private bool _isBioExpanded;

    [ObservableProperty]
    private string? _artistUri;

    [ObservableProperty]
    private List<ExternalLinkVm>? _externalLinks;

    // ── Credits section ──

    [ObservableProperty]
    private ObservableCollection<CreditGroupVm> _creditGroups = [];

    [ObservableProperty]
    private string? _recordLabel;

    // ── Concerts section ──

    [ObservableProperty]
    private ObservableCollection<ConcertVm> _concerts = [];

    [ObservableProperty]
    private bool _hasConcerts;

    // ── Canvas section ──

    [ObservableProperty]
    private string? _canvasUrl;

    [ObservableProperty]
    private string? _upstreamCanvasUrl;

    [ObservableProperty]
    private string? _pendingCanvasUrl;

    [ObservableProperty]
    private string? _canvasType;

    [ObservableProperty]
    private bool _hasCanvas;

    [ObservableProperty]
    private bool _hasPendingCanvasUpdate;

    [ObservableProperty]
    private bool _isUsingCanvasSnapshot;

    [ObservableProperty]
    private bool _isManualCanvasOverride;

    [ObservableProperty]
    private string? _currentTrackUri;

    // ── Related Videos section ──

    [ObservableProperty]
    private ObservableCollection<RelatedVideoVm> _relatedVideos = [];

    [ObservableProperty]
    private bool _hasRelatedVideos;

    // Podcast episode section

    [ObservableProperty]
    private bool _isPodcastEpisode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PodcastEpisodeTitle))]
    [NotifyPropertyChangedFor(nameof(PodcastEpisodeImageUrl))]
    [NotifyPropertyChangedFor(nameof(PodcastShowName))]
    [NotifyPropertyChangedFor(nameof(PodcastPublisherLine))]
    [NotifyPropertyChangedFor(nameof(PodcastEpisodeMetadata))]
    [NotifyPropertyChangedFor(nameof(PodcastEpisodeDescription))]
    [NotifyPropertyChangedFor(nameof(HasPodcastDescription))]
    [NotifyPropertyChangedFor(nameof(HasPodcastTranscript))]
    [NotifyPropertyChangedFor(nameof(PodcastTranscriptLabel))]
    private PodcastEpisodeDetailDto? _podcastEpisodeDetail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPodcastChapters))]
    private ObservableCollection<EpisodeChapterVm> _podcastChapters = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPodcastComments))]
    private ObservableCollection<PodcastCommentViewModel> _podcastComments = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMorePodcastComments))]
    [NotifyCanExecuteChangedFor(nameof(LoadMorePodcastCommentsCommand))]
    private string? _podcastCommentsNextPageToken;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PodcastCommentsCountLabel))]
    private int _podcastCommentsTotalCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PodcastCommentCharacterCount))]
    [NotifyCanExecuteChangedFor(nameof(SubmitPodcastCommentCommand))]
    private string _podcastCommentDraft = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitPodcastCommentCommand))]
    private bool _isSubmittingPodcastComment;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMorePodcastCommentsCommand))]
    private bool _isLoadingMorePodcastComments;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPodcastCommentStatus))]
    private string? _podcastCommentStatus;

    public IPlaybackStateService PlaybackState => _playbackState;

    public string PodcastEpisodeTitle => PodcastEpisodeDetail?.Title ?? "";
    public string? PodcastEpisodeImageUrl => PodcastEpisodeDetail?.ImageUrl ?? PodcastEpisodeDetail?.ShowImageUrl;
    public string PodcastShowName => PodcastEpisodeDetail?.ShowName ?? "";
    public string PodcastPublisherLine => PodcastEpisodeDetail?.ShowName ?? "";
    public string PodcastEpisodeMetadata => PodcastEpisodeDetail?.Metadata ?? "";
    public string? PodcastEpisodeDescription => PodcastEpisodeDetail?.Description;
    public bool HasPodcastDescription => !string.IsNullOrWhiteSpace(PodcastEpisodeDescription);
    public bool HasPodcastChapters => PodcastChapters.Count > 0;
    public bool HasPodcastComments => PodcastComments.Count > 0;
    public bool HasMorePodcastComments => !string.IsNullOrWhiteSpace(PodcastCommentsNextPageToken);
    public bool HasPodcastCommentStatus => !string.IsNullOrWhiteSpace(PodcastCommentStatus);
    public bool HasPodcastTranscript => PodcastEpisodeDetail?.TranscriptLanguages.Count > 0;
    public string PodcastTranscriptLabel => PodcastEpisodeDetail?.TranscriptSummary ?? "";
    public string PodcastCommentCharacterCount => $"{Math.Min(PodcastCommentDraft.Length, MaxPodcastCommentLength)}/{MaxPodcastCommentLength}";
    public string PodcastCommentsCountLabel => PodcastCommentsTotalCount switch
    {
        <= 0 => "Comments",
        1 => "1 comment",
        _ => $"{PodcastCommentsTotalCount:N0} comments"
    };

    public TrackDetailsViewModel(
        IPlaybackStateService playbackState,
        IPathfinderClient pathfinder,
        ITrackCreditsService creditsService,
        ILibraryDataService libraryDataService,
        IMediaOverrideService mediaOverrideService,
        ILogger<TrackDetailsViewModel>? logger = null)
    {
        _playbackState = playbackState;
        _pathfinder = pathfinder;
        _creditsService = creditsService;
        _libraryDataService = libraryDataService;
        _mediaOverrideService = mediaOverrideService;
        _logger = logger;

        _playbackState.PropertyChanged += OnPlaybackStateChanged;
    }

    private void OnPlaybackStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IPlaybackStateService.CurrentTrackId)
            or nameof(IPlaybackStateService.CurrentArtistId)
            or nameof(IPlaybackStateService.CurrentContext))
        {
            // Episodes do not have an artist NPV payload; tracks still need both ids.
            if (TryGetCurrentEpisodeUri(out _)
                || (!string.IsNullOrEmpty(_playbackState.CurrentTrackId)
                    && !string.IsNullOrEmpty(_playbackState.CurrentArtistId)))
            {
                _ = DeferredLoadDetailsAsync();
            }
        }

        if (e.PropertyName is nameof(IPlaybackStateService.Position)
            or nameof(IPlaybackStateService.Duration)
            or nameof(IPlaybackStateService.IsPlaying))
        {
            UpdatePodcastChapterTimeline();
        }
    }

    private async Task DeferredLoadDetailsAsync()
    {
        await Task.Yield(); // Let the current dispatch frame render first
        await LoadDetailsAsync();
    }

    [RelayCommand]
    private void ToggleBioExpanded()
    {
        IsBioExpanded = !IsBioExpanded;
    }

    public async Task LoadDetailsAsync()
    {
        if (TryGetCurrentEpisodeUri(out var episodeUri))
        {
            await LoadPodcastEpisodeDetailsAsync(episodeUri);
            return;
        }

        var trackId = _playbackState.CurrentTrackId;
        var artistId = _playbackState.CurrentArtistId;

        if (string.IsNullOrEmpty(trackId) || string.IsNullOrEmpty(artistId))
        {
            ClearData();
            return;
        }

        if (trackId.Contains(':', StringComparison.Ordinal)
            && !trackId.StartsWith("spotify:track:", StringComparison.Ordinal))
        {
            ClearData();
            return;
        }

        if (trackId == _loadedTrackId) return;
        _loadedTrackId = trackId;

        ClearPodcastData();

        _fetchCts?.Cancel();
        _fetchCts = new CancellationTokenSource();
        var ct = _fetchCts.Token;

        IsLoading = true;
        HasData = false;
        ErrorMessage = null;

        try
        {
            // CurrentArtistId is already in "spotify:artist:xxx" format
            // CurrentTrackId is the bare ID, needs "spotify:track:" prefix
            var trackUri = trackId.StartsWith("spotify:track:", StringComparison.Ordinal)
                ? trackId
                : $"spotify:track:{trackId}";

            // Fetch NpvArtist and credits in parallel
            var npvTask = _pathfinder.GetNpvArtistAsync(artistId, trackUri, ct: ct);
            var creditsTask = _creditsService.GetCreditsAsync(trackUri, ct);

            await Task.WhenAll(npvTask, creditsTask);
            if (ct.IsCancellationRequested) return;

            await MapResponseAsync(npvTask.Result, trackUri, ct);
            MapCredits(creditsTask.Result);
            HasData = true;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug("[TrackDetails] Load cancelled for {TrackId}", trackId);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load details for track {TrackId}", trackId);
            ErrorMessage = AppLocalization.GetString("TrackDetails_LoadFailed");
        }
        finally
        {
            if (!ct.IsCancellationRequested)
                IsLoading = false;
        }
    }

    private bool TryGetCurrentEpisodeUri(out string episodeUri)
    {
        var trackId = _playbackState.CurrentTrackId;
        if (!string.IsNullOrWhiteSpace(trackId))
        {
            if (trackId.StartsWith("spotify:episode:", StringComparison.Ordinal))
            {
                episodeUri = trackId;
                return true;
            }

            if (!trackId.Contains(':', StringComparison.Ordinal)
                && _playbackState.CurrentContext?.Type is PlaybackContextType.Show or PlaybackContextType.Episode)
            {
                episodeUri = $"spotify:episode:{trackId}";
                return true;
            }
        }

        var contextUri = _playbackState.CurrentContext?.ContextUri;
        if (!string.IsNullOrWhiteSpace(contextUri)
            && contextUri.StartsWith("spotify:episode:", StringComparison.Ordinal))
        {
            episodeUri = contextUri;
            return true;
        }

        episodeUri = "";
        return false;
    }

    private async Task LoadPodcastEpisodeDetailsAsync(string episodeUri)
    {
        if (string.Equals(episodeUri, _loadedTrackId, StringComparison.Ordinal))
            return;

        _loadedTrackId = episodeUri;

        _fetchCts?.Cancel();
        _fetchCts = new CancellationTokenSource();
        var ct = _fetchCts.Token;

        IsPodcastEpisode = true;
        ClearMusicData();
        IsLoading = true;
        HasData = false;
        ErrorMessage = null;

        try
        {
            await Task.Yield();

            var npvTask = FetchOptionalDetailsAsync(
                () => _pathfinder.GetNpvEpisodeAsync(episodeUri, numberOfChapters: 10, ct),
                "episode NPV");
            var detailTask = FetchOptionalDetailsAsync(
                () => _libraryDataService.GetPodcastEpisodeDetailAsync(episodeUri, ct),
                "episode detail");

            await Task.WhenAll(npvTask, detailTask);
            if (ct.IsCancellationRequested) return;

            MapPodcastEpisode(npvTask.Result, detailTask.Result, episodeUri);
            if (PodcastEpisodeDetail is null)
            {
                ErrorMessage = "Episode details are unavailable.";
                HasData = false;
                return;
            }

            HasData = true;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug("[TrackDetails] Podcast load cancelled for {EpisodeUri}", episodeUri);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load podcast details for episode {EpisodeUri}", episodeUri);
            ErrorMessage = AppLocalization.GetString("TrackDetails_LoadFailed");
        }
        finally
        {
            if (!ct.IsCancellationRequested)
                IsLoading = false;
        }
    }

    private async Task<T?> FetchOptionalDetailsAsync<T>(Func<Task<T>> fetch, string label)
        where T : class
    {
        try
        {
            return await fetch();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[TrackDetails] Optional {Label} fetch failed", label);
            return null;
        }
    }

    private void MapPodcastEpisode(
        GetEpisodeOrChapterResponse? npvResponse,
        PodcastEpisodeDetailDto? detail,
        string episodeUri)
    {
        var episode = npvResponse?.Data?.EpisodeUnionV2;
        var show = episode?.PodcastV2?.Data;

        if (detail is null && episode is not null)
        {
            detail = CreatePodcastEpisodeDetailFromNpv(episode, episodeUri);
        }
        else if (detail is not null
                 && string.IsNullOrWhiteSpace(detail.Description)
                 && !string.IsNullOrWhiteSpace(episode?.HtmlDescription))
        {
            detail = detail with { Description = StripHtml(episode.HtmlDescription) };
        }

        if (detail is not null && string.IsNullOrWhiteSpace(detail.ShowName) && !string.IsNullOrWhiteSpace(show?.Name))
        {
            detail = detail with
            {
                ShowName = show!.Name,
                ShowUri = string.IsNullOrWhiteSpace(detail.ShowUri) ? show.Uri : detail.ShowUri,
                ShowImageUrl = string.IsNullOrWhiteSpace(detail.ShowImageUrl)
                    ? BestImageUrl(show.CoverArt?.Sources)
                    : detail.ShowImageUrl
            };
        }

        PodcastEpisodeDetail = detail;

        var chapters = (episode?.DisplaySegments?.Segments?.Items?
                .Where(static segment => !string.IsNullOrWhiteSpace(segment.Title))
                .Select(static (segment, index) => new EpisodeChapterVm
                {
                    Number = index + 1,
                    Title = segment.Title ?? "",
                    Subtitle = segment.Subtitle,
                    StartMilliseconds = Math.Max(0, segment.SeekStart?.Milliseconds ?? 0),
                    StopMilliseconds = Math.Max(0, segment.SeekStop?.Milliseconds ?? 0)
                })
                .Take(10)
            ?? []).ToList();
        MarkChapterEdges(chapters);
        PodcastChapters = new ObservableCollection<EpisodeChapterVm>(chapters);

        PodcastComments = new ObservableCollection<PodcastCommentViewModel>(
            (detail?.Comments ?? [])
                .Select(comment => new PodcastCommentViewModel(comment, _libraryDataService, _logger)));
        PodcastCommentsNextPageToken = detail?.CommentsNextPageToken;
        PodcastCommentsTotalCount = Math.Max(detail?.CommentsTotalCount ?? 0, PodcastComments.Count);
        PodcastCommentDraft = "";
        PodcastCommentStatus = null;

        OnPropertyChanged(nameof(HasPodcastChapters));
        OnPropertyChanged(nameof(HasPodcastComments));
        OnPropertyChanged(nameof(HasMorePodcastComments));
        OnPropertyChanged(nameof(PodcastCommentsCountLabel));
        UpdatePodcastChapterTimeline();
    }

    private static PodcastEpisodeDetailDto CreatePodcastEpisodeDetailFromNpv(
        PathfinderEpisode episode,
        string fallbackUri)
    {
        var show = episode.PodcastV2?.Data;
        var uri = string.IsNullOrWhiteSpace(episode.Uri) ? fallbackUri : episode.Uri!;
        var description = StripHtml(episode.HtmlDescription) ?? episode.Description;

        return new PodcastEpisodeDetailDto
        {
            Uri = uri,
            Title = episode.Name ?? "Unknown episode",
            ShowUri = show?.Uri,
            ShowName = show?.Name,
            ImageUrl = BestImageUrl(episode.CoverArt?.Sources) ?? BestImageUrl(show?.CoverArt?.Sources),
            ShowImageUrl = BestImageUrl(show?.CoverArt?.Sources) ?? BestImageUrl(episode.CoverArt?.Sources),
            Description = description,
            HtmlDescription = episode.HtmlDescription,
            Duration = TimeSpan.FromMilliseconds(Math.Max(0, episode.Duration?.TotalMilliseconds ?? 0)),
            ReleaseDate = ParseDate(episode.ReleaseDate?.IsoString),
            AddedAt = DateTime.Now,
            IsExplicit = string.Equals(episode.ContentRating?.Label, "EXPLICIT", StringComparison.OrdinalIgnoreCase),
            IsPlayable = episode.Playability?.Playable ?? true,
            IsPaywalled = episode.Restrictions?.PaywallContent ?? false,
            ShareUrl = episode.SharingInfo?.ShareUrl,
            PreviewUrl = episode.PreviewPlayback?.AudioPreview?.CdnUrl,
            MediaTypes = DistinctNonEmpty(episode.MediaTypes),
            TranscriptLanguages = DistinctNonEmpty(episode.Transcripts?.Items?.Select(static transcript => transcript.Language))
        };
    }

    private async Task MapResponseAsync(NpvArtistResponse response, string trackUri, CancellationToken ct)
    {
        var artist = response.Data?.ArtistUnion;
        var track = response.Data?.TrackUnion;

        // ── Artist ──
        ArtistName = artist?.Profile?.Name;
        ArtistUri = artist?.Uri;
        IsVerified = artist?.Profile?.Verified ?? false;
        Followers = FormatNumber(artist?.Stats?.Followers ?? 0);
        MonthlyListeners = FormatNumber(artist?.Stats?.MonthlyListeners ?? 0);
        BiographyText = StripHtml(artist?.Profile?.Biography?.Text);
        IsBioExpanded = false;

        ArtistAvatarUrl = artist?.Visuals?.AvatarImage?.Sources
            ?.OrderByDescending(s => s.Width ?? 0)
            .FirstOrDefault()?.Url;

        ExternalLinks = artist?.Profile?.ExternalLinks?.Items?
            .Select(l => new ExternalLinkVm { Name = l.Name, Url = l.Url })
            .ToList();

        // Credits + record label are now set by MapCredits() from the dedicated service

        // ── Canvas ──
        CanvasType = track?.Canvas?.Type;
        await ApplyCanvasOverrideStateAsync(trackUri, track?.Canvas?.Url, ct);

        // ── Concerts ──
        var concertList = new ObservableCollection<ConcertVm>();
        if (artist?.Goods?.Concerts?.Items is { Count: > 0 } concertItems)
        {
            foreach (var item in concertItems.Take(5))
            {
                var data = item.Data;
                if (data == null) continue;
                concertList.Add(new ConcertVm
                {
                    Title = data.Title,
                    Venue = data.Location?.Name,
                    City = data.Location?.City,
                    DateFormatted = FormatConcertDate(data.StartDateIsoString),
                    Uri = data.Uri,
                });
            }
        }
        Concerts = concertList;
        HasConcerts = Concerts.Count > 0;

        // ── Related Videos ──
        var videos = new ObservableCollection<RelatedVideoVm>();
        if (track?.RelatedVideos?.Items is { Count: > 0 } videoItems)
        {
            foreach (var v in videoItems.Take(10))
            {
                var vData = v.TrackOfVideo?.Data;
                if (vData == null) continue;
                videos.Add(new RelatedVideoVm
                {
                    Title = vData.Name,
                    ThumbnailUrl = vData.AlbumOfTrack?.CoverArt?.Sources
                        ?.FirstOrDefault()?.Url,
                    ArtistName = vData.Artists?.Items?.FirstOrDefault()?.Profile?.Name,
                    Uri = v.Uri,
                });
            }
        }
        RelatedVideos = videos;
        HasRelatedVideos = RelatedVideos.Count > 0;
    }

    private async Task ApplyCanvasOverrideStateAsync(string trackUri, string? upstreamCanvasUrl, CancellationToken ct)
    {
        var resolved = await _mediaOverrideService.ResolveTrackCanvasAsync(trackUri, upstreamCanvasUrl, ct);
        if (ct.IsCancellationRequested)
            return;

        ApplyResolvedCanvasState(trackUri, resolved);
    }

    private void ApplyResolvedCanvasState(string trackUri, ResolvedMediaOverrideResult resolved)
    {
        CurrentTrackUri = trackUri;
        UpstreamCanvasUrl = resolved.UpstreamAssetUrl;
        PendingCanvasUrl = resolved.PendingAssetUrl;
        HasPendingCanvasUpdate = resolved.HasPendingUpdate;
        IsUsingCanvasSnapshot = resolved.IsUsingLocalSnapshot;
        IsManualCanvasOverride = resolved.IsManualOverride;
        CanvasUrl = resolved.EffectiveAssetUrl;
        HasCanvas = !string.IsNullOrEmpty(CanvasUrl);
    }

    public async Task SetManualCanvasUrlAsync(string canvasUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(CurrentTrackUri))
            return;

        var resolved = await _mediaOverrideService.SetManualTrackCanvasUrlAsync(CurrentTrackUri, canvasUrl, ct);
        if (ct.IsCancellationRequested)
            return;

        ApplyResolvedCanvasState(CurrentTrackUri, resolved);
    }

    public async Task ImportManualCanvasFileAsync(string sourceFilePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(CurrentTrackUri))
            return;

        var resolved = await _mediaOverrideService.ImportManualTrackCanvasFileAsync(CurrentTrackUri, sourceFilePath, ct);
        if (ct.IsCancellationRequested)
            return;

        ApplyResolvedCanvasState(CurrentTrackUri, resolved);
    }

    public async Task ResetCanvasToUpstreamAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(CurrentTrackUri))
            return;

        var resolved = await _mediaOverrideService.ResetTrackCanvasToUpstreamAsync(CurrentTrackUri, ct);
        if (ct.IsCancellationRequested)
            return;

        ApplyResolvedCanvasState(CurrentTrackUri, resolved);
    }

    public async Task AcceptPendingCanvasUpdateAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(CurrentTrackUri))
            return;

        var resolved = await _mediaOverrideService.AcceptPendingTrackCanvasUpdateAsync(CurrentTrackUri, ct);
        if (ct.IsCancellationRequested)
            return;

        ApplyResolvedCanvasState(CurrentTrackUri, resolved);
    }

    public async Task RejectPendingCanvasUpdateAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(CurrentTrackUri))
            return;

        var resolved = await _mediaOverrideService.RejectPendingTrackCanvasUpdateAsync(CurrentTrackUri, ct);
        if (ct.IsCancellationRequested)
            return;

        ApplyResolvedCanvasState(CurrentTrackUri, resolved);
    }

    private void MapCredits(TrackCreditsResult credits)
    {
        var groups = new ObservableCollection<CreditGroupVm>();
        foreach (var group in credits.Groups)
        {
            groups.Add(new CreditGroupVm
            {
                RoleName = group.RoleName,
                Contributors = group.Contributors.Select(c => new ContributorVm
                {
                    Name = c.Name,
                    Uri = c.ArtistUri,
                    ImageUrl = c.ImageUrl,
                    Roles = c.Roles,
                }).ToList()
            });
        }
        CreditGroups = groups;
        RecordLabel = credits.RecordLabel;
    }

    [RelayCommand]
    private void OpenPodcastShow()
    {
        var detail = PodcastEpisodeDetail;
        if (detail is null || string.IsNullOrWhiteSpace(detail.ShowUri))
            return;

        NavigationHelpers.OpenShowPage(
            detail.ShowUri,
            detail.ShowName ?? "Podcast",
            openInNewTab: NavigationHelpers.IsCtrlPressed());
    }

    [RelayCommand]
    private void OpenPodcastEpisodeDetail()
    {
        var detail = PodcastEpisodeDetail;
        if (detail is not null)
        {
            try
            {
                Ioc.Default
                    .GetService<LibraryPageViewModel>()
                    ?.YourEpisodes
                    .ShowEpisodeDetails(CreateLibraryEpisode(detail));
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Could not preselect podcast episode detail for {EpisodeUri}", detail.Uri);
            }
        }

        NavigationHelpers.OpenPodcasts(NavigationHelpers.IsCtrlPressed());
    }

    [RelayCommand]
    private void SeekPodcastChapter(EpisodeChapterVm? chapter)
    {
        if (chapter is null)
            return;

        var target = Math.Max(0, chapter.StartMilliseconds);
        _logger?.LogInformation("[TrackDetails] Seeking podcast chapter '{Title}' at {PositionMs}ms",
            chapter.Title,
            target);

        UpdatePodcastChapterTimeline(target);
        _playbackState.Seek(target);
    }

    public void UpdatePodcastChapterTimeline(double? positionOverrideMs = null)
    {
        if (!IsPodcastEpisode || PodcastChapters.Count == 0)
            return;

        var positionMs = Math.Max(0, positionOverrideMs ?? _playbackState.Position);
        var durationMs = Math.Max(
            Math.Max(0, _playbackState.Duration),
            Math.Max(0, PodcastEpisodeDetail?.Duration.TotalMilliseconds ?? 0));

        for (var i = 0; i < PodcastChapters.Count; i++)
        {
            var chapter = PodcastChapters[i];
            var startMs = Math.Max(0, chapter.StartMilliseconds);
            var nextStartMs = i + 1 < PodcastChapters.Count
                ? Math.Max(startMs, PodcastChapters[i + 1].StartMilliseconds)
                : 0;
            var stopMs = chapter.StopMilliseconds > startMs ? chapter.StopMilliseconds : 0;
            var endMs = nextStartMs > startMs
                ? nextStartMs
                : stopMs > startMs
                    ? stopMs
                    : durationMs > startMs
                        ? durationMs
                        : startMs + 1;

            var isCompleted = positionMs >= endMs;
            var isActive = positionMs >= startMs && positionMs < endMs;
            var progress = positionMs < startMs
                ? 0
                : isCompleted
                    ? 1
                    : 0.5 + (Math.Clamp((positionMs - startMs) / Math.Max(1, endMs - startMs), 0, 1) * 0.5);

            chapter.SetTimelineState(isActive, isCompleted, progress);
        }
    }

    private static void MarkChapterEdges(IReadOnlyList<EpisodeChapterVm> chapters)
    {
        for (var i = 0; i < chapters.Count; i++)
        {
            chapters[i].IsFirst = i == 0;
            chapters[i].IsLast = i == chapters.Count - 1;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSubmitPodcastComment))]
    private async Task SubmitPodcastCommentAsync()
    {
        var detail = PodcastEpisodeDetail;
        var text = NormalizeCommentText(PodcastCommentDraft);
        if (detail is null || text.Length == 0 || text.Length > MaxPodcastCommentLength)
            return;

        IsSubmittingPodcastComment = true;
        PodcastCommentStatus = null;
        try
        {
            var comment = await _libraryDataService
                .CreatePodcastEpisodeCommentAsync(detail.Uri, text)
                .ConfigureAwait(true);

            PodcastComments.Insert(0, new PodcastCommentViewModel(comment, _libraryDataService, _logger));
            PodcastCommentsTotalCount = Math.Max(PodcastCommentsTotalCount + 1, PodcastComments.Count);
            PodcastCommentDraft = "";
            PodcastCommentStatus = "Comment saved locally. Spotify posting is not wired yet.";
            OnPropertyChanged(nameof(HasPodcastComments));
            OnPropertyChanged(nameof(PodcastCommentsCountLabel));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to add local podcast comment for {EpisodeUri}", detail.Uri);
            PodcastCommentStatus = "Could not add the comment.";
        }
        finally
        {
            IsSubmittingPodcastComment = false;
        }
    }

    private bool CanSubmitPodcastComment()
    {
        var text = NormalizeCommentText(PodcastCommentDraft);
        return PodcastEpisodeDetail is not null
               && !IsSubmittingPodcastComment
               && text.Length > 0
               && text.Length <= MaxPodcastCommentLength;
    }

    [RelayCommand(CanExecute = nameof(CanLoadMorePodcastComments))]
    private async Task LoadMorePodcastCommentsAsync()
    {
        var detail = PodcastEpisodeDetail;
        var token = PodcastCommentsNextPageToken;
        if (detail is null || string.IsNullOrWhiteSpace(token))
            return;

        IsLoadingMorePodcastComments = true;
        PodcastCommentStatus = null;
        try
        {
            var page = await _libraryDataService
                .GetPodcastEpisodeCommentsPageAsync(detail.Uri, token)
                .ConfigureAwait(true);

            if (page is null)
                return;

            foreach (var comment in page.Items)
            {
                PodcastComments.Add(new PodcastCommentViewModel(comment, _libraryDataService, _logger));
            }

            PodcastCommentsNextPageToken = page.NextPageToken;
            PodcastCommentsTotalCount = Math.Max(page.TotalCount, PodcastComments.Count);
            OnPropertyChanged(nameof(HasPodcastComments));
            OnPropertyChanged(nameof(PodcastCommentsCountLabel));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load more podcast comments for {EpisodeUri}", detail.Uri);
            PodcastCommentStatus = "Could not load more comments.";
        }
        finally
        {
            IsLoadingMorePodcastComments = false;
        }
    }

    private bool CanLoadMorePodcastComments()
        => !IsLoadingMorePodcastComments
           && PodcastEpisodeDetail is not null
           && !string.IsNullOrWhiteSpace(PodcastCommentsNextPageToken);

    private void ClearData()
    {
        _loadedTrackId = null;
        HasData = false;
        IsLoading = false;
        ErrorMessage = null;
        ClearMusicData();
        ClearPodcastData();
    }

    private void ClearMusicData()
    {
        ArtistName = null;
        ArtistAvatarUrl = null;
        IsVerified = false;
        Followers = null;
        MonthlyListeners = null;
        BiographyText = null;
        ExternalLinks = null;
        CreditGroups = [];
        RecordLabel = null;
        CanvasUrl = null;
        UpstreamCanvasUrl = null;
        PendingCanvasUrl = null;
        CurrentTrackUri = null;
        HasCanvas = false;
        HasPendingCanvasUpdate = false;
        IsUsingCanvasSnapshot = false;
        IsManualCanvasOverride = false;
        Concerts = [];
        HasConcerts = false;
        RelatedVideos = [];
        HasRelatedVideos = false;
    }

    private void ClearPodcastData()
    {
        IsPodcastEpisode = false;
        PodcastEpisodeDetail = null;
        PodcastChapters = [];
        PodcastComments = [];
        PodcastCommentsNextPageToken = null;
        PodcastCommentsTotalCount = 0;
        PodcastCommentDraft = "";
        PodcastCommentStatus = null;
        IsSubmittingPodcastComment = false;
        IsLoadingMorePodcastComments = false;
    }

    /// <summary>
    /// Forces a reload on the next track change (e.g. after invalidation).
    /// </summary>
    public void InvalidateTrack()
    {
        _loadedTrackId = null;
    }

    private static string FormatNumber(long value)
    {
        return value switch
        {
            >= 1_000_000_000 => $"{value / 1_000_000_000.0:F1}B",
            >= 1_000_000 => $"{value / 1_000_000.0:F1}M",
            >= 1_000 => $"{value / 1_000.0:F1}K",
            _ => value.ToString("N0", CultureInfo.InvariantCulture)
        };
    }

    private static string NormalizeCommentText(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? ""
            : Regex.Replace(text.Trim(), @"\s+", " ");

    private static LibraryEpisodeDto CreateLibraryEpisode(PodcastEpisodeDetailDto detail)
    {
        var episode = new LibraryEpisodeDto
        {
            Id = ExtractBareId(detail.Uri, "spotify:episode:"),
            Uri = detail.Uri,
            Title = detail.Title,
            ArtistName = detail.ShowName ?? "",
            ArtistId = "",
            AlbumName = detail.ShowName ?? "",
            AlbumId = detail.ShowUri ?? "",
            ImageUrl = detail.ImageUrl ?? detail.ShowImageUrl,
            Description = detail.Description,
            Duration = detail.Duration,
            ReleaseDate = detail.ReleaseDate,
            ShareUrl = detail.ShareUrl,
            PreviewUrl = detail.PreviewUrl,
            MediaTypes = detail.MediaTypes,
            AddedAt = detail.AddedAt == default ? DateTime.Now : detail.AddedAt,
            IsExplicit = detail.IsExplicit,
            IsPlayable = detail.IsPlayable,
            IsLiked = false
        };

        episode.ApplyPlaybackProgress(detail.PlayedPosition, detail.PlayedState);
        return episode;
    }

    private static string ExtractBareId(string uri, string prefix)
        => uri.StartsWith(prefix, StringComparison.Ordinal)
            ? uri[prefix.Length..]
            : uri;

    private static string? BestImageUrl(IReadOnlyList<ArtistImageSource>? sources)
        => sources?
            .Where(static source => !string.IsNullOrWhiteSpace(source.Url))
            .OrderByDescending(static source => source.Width ?? 0)
            .FirstOrDefault()
            ?.Url;

    private static IReadOnlyList<string> DistinctNonEmpty(IEnumerable<string?>? values)
        => values?
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
           ?? [];

    private static DateTimeOffset? ParseDate(string? isoDate)
        => DateTimeOffset.TryParse(
            isoDate,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces,
            out var parsed)
            ? parsed
            : null;

    private static string? StripHtml(string? html)
    {
        if (string.IsNullOrEmpty(html)) return null;
        // Replace <br>, <br/>, <br /> with newlines
        var text = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        // Strip remaining HTML tags
        text = Regex.Replace(text, @"<[^>]+>", "");
        // Decode all HTML entities (named, decimal, hex — e.g. &amp; &#39; &#x1f90d;)
        text = System.Net.WebUtility.HtmlDecode(text);
        return text.Trim();
    }

    private static string? FormatConcertDate(string? isoDate)
    {
        if (string.IsNullOrEmpty(isoDate)) return null;
        if (DateTimeOffset.TryParse(isoDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
            return dto.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
        return isoDate;
    }

    private static int GetRoleGroupOrder(string roleGroup) => roleGroup switch
    {
        "Artist" => 0,
        "Composition & Lyrics" => 1,
        "Production & Engineering" => 2,
        _ => 3
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _playbackState.PropertyChanged -= OnPlaybackStateChanged;
        _fetchCts?.Cancel();
        _fetchCts?.Dispose();
    }
}

// ── View Models / Models ──

public sealed class CreditGroupVm
{
    public string? RoleName { get; init; }
    public List<ContributorVm>? Contributors { get; init; }
}

public sealed class ContributorVm
{
    public string? Name { get; init; }
    public string? Uri { get; init; }
    public string? ImageUrl { get; init; }
    public List<string>? Roles { get; init; }
    public string RolesText => Roles != null ? string.Join(", ", Roles) : "";
}

public sealed class RelatedVideoVm
{
    public string? Title { get; init; }
    public string? ThumbnailUrl { get; init; }
    public string? ArtistName { get; init; }
    public string? Uri { get; init; }
}

public sealed class EpisodeChapterVm : ObservableObject
{
    private bool _isActive;
    private bool _isCompleted;
    private double _timelineCellProgress;

    public int Number { get; init; }
    public bool IsFirst { get; set; }
    public bool IsLast { get; set; }
    public string Title { get; init; } = "";
    public string? Subtitle { get; init; }
    public long StartMilliseconds { get; init; }
    public long StopMilliseconds { get; init; }

    public string StartTime => FormatTime(StartMilliseconds);
    public string ChapterLabel => Number > 0 ? $"Chapter {Number}" : "Chapter";
    public bool IsActive => _isActive;
    public bool IsCompleted => _isCompleted;
    public double TimelineCellProgress => _timelineCellProgress;
    public double ContentOpacity => IsActive ? 1 : IsCompleted ? 0.86 : 0.66;
    public double ChromeOpacity => IsActive ? 1 : IsCompleted ? 0.82 : 0.58;

    public string TimeRange
    {
        get
        {
            if (StopMilliseconds > StartMilliseconds)
                return $"{FormatTime(StartMilliseconds)} - {FormatTime(StopMilliseconds)}";

            return FormatTime(StartMilliseconds);
        }
    }

    private static string FormatTime(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    public void SetTimelineState(bool isActive, bool isCompleted, double progress)
    {
        var clamped = Math.Clamp(progress, 0, 1);
        var changed = SetProperty(ref _isActive, isActive, nameof(IsActive));
        changed |= SetProperty(ref _isCompleted, isCompleted, nameof(IsCompleted));
        changed |= SetProperty(ref _timelineCellProgress, clamped, nameof(TimelineCellProgress));

        if (!changed)
            return;

        OnPropertyChanged(nameof(ContentOpacity));
        OnPropertyChanged(nameof(ChromeOpacity));
    }
}

public sealed class ExternalLinkVm
{
    public string? Name { get; init; }
    public string? Url { get; init; }
}
