using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using ReactiveUI;
using Windows.UI;
using Wavee.UI.Contracts;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Helpers.Playback;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// ViewModel for <c>EpisodePage</c>. Loads a single podcast episode, its chapter
/// list, and (when the parent show URI is known) the show's metadata so the
/// page can render breadcrumb / hero / "More from this show" without a refetch.
/// Mirrors <see cref="ShowViewModel"/>'s palette + cancellation patterns but is
/// scoped to one episode rather than a whole show.
/// </summary>
public sealed partial class EpisodePageViewModel : ReactiveObject, ITabBarItemContent, IDisposable
{
    private const int MaxPodcastCommentLength = 500;
    private const long PodcastProgressUiDeltaMs = 5_000;
    private const long PodcastCompletedThresholdMs = 90_000;

    private readonly IPodcastService _podcastService;
    private readonly ILibraryDataService _libraryDataService;
    private readonly IPlaybackStateService _playbackStateService;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly ILogger? _logger;

    private CancellationTokenSource? _loadCts;
    private bool _disposed;
    private bool _isDarkTheme;
    private ShowPaletteDto? _palette;
    private bool _playbackUiRefreshScheduled;

    private string _episodeUri = "";
    private ShowEpisodeDto? _episode;
    private string? _fullDescription;
    private string? _descriptionHtml;
    private string? _shareUrl;
    private bool _isLoading = true;
    private bool _hasError;
    private string? _errorMessage;

    private string? _parentShowUri;
    private string? _parentShowTitle;
    private string? _parentShowImageUrl;

    private ObservableCollection<EpisodeChapterVm> _chapters = new();
    private ObservableCollection<ShowEpisodeDto> _moreFromShow = new();
    private ObservableCollection<PodcastCommentViewModel> _comments = new();
    private string? _commentsNextPageToken;
    private int _commentsTotalCount;
    private string _commentDraft = "";
    private bool _isSubmittingComment;
    private bool _isLoadingMoreComments;
    private string? _commentStatus;

    private Brush? _paletteBackdropBrush;
    private Brush? _paletteCoverColorBrush;

    public TabItemParameter? TabItemParameter { get; private set; }
    public event EventHandler<TabItemParameter>? ContentChanged;

    public string EpisodeUri
    {
        get => _episodeUri;
        private set
        {
            this.RaiseAndSetIfChanged(ref _episodeUri, value);
            this.RaisePropertyChanged(nameof(CanSubmitComment));
        }
    }

    public ShowEpisodeDto? Episode
    {
        get => _episode;
        private set
        {
            this.RaiseAndSetIfChanged(ref _episode, value);
            this.RaisePropertyChanged(nameof(Title));
            this.RaisePropertyChanged(nameof(MetaLine));
            this.RaisePropertyChanged(nameof(ListenActionText));
            this.RaisePropertyChanged(nameof(IsExplicit));
            this.RaisePropertyChanged(nameof(IsVideo));
            this.RaisePropertyChanged(nameof(CoverArtUrl));
            this.RaisePropertyChanged(nameof(EpisodeNumberTag));
            this.RaisePropertyChanged(nameof(BreadcrumbItems));
            UpdateTabTitle();
        }
    }

    public string Title => _episode?.Title ?? "";
    public string MetaLine => _episode?.MetaLine ?? "";
    public string ListenActionText => _episode?.ListenActionText ?? "Play";
    public bool IsExplicit => _episode?.IsExplicit ?? false;
    public bool IsVideo => _episode?.IsVideo ?? false;
    public string? CoverArtUrl => _episode?.CoverArtUrl;
    public string EpisodeNumberTag => _episode?.EpisodeNumberTag ?? "";

    public string? FullDescription
    {
        get => _fullDescription;
        private set
        {
            this.RaiseAndSetIfChanged(ref _fullDescription, value);
            this.RaisePropertyChanged(nameof(HasDescription));
            this.RaisePropertyChanged(nameof(DescriptionHtml));
        }
    }

    public string? DescriptionHtml
    {
        get => _descriptionHtml ?? _fullDescription;
        private set
        {
            this.RaiseAndSetIfChanged(ref _descriptionHtml, value);
            this.RaisePropertyChanged(nameof(HasDescription));
        }
    }

    public bool HasDescription => !string.IsNullOrWhiteSpace(DescriptionHtml);

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

    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            this.RaiseAndSetIfChanged(ref _isLoading, value);
            this.RaisePropertyChanged(nameof(HasNoComments));
        }
    }
    public bool HasError { get => _hasError; set => this.RaiseAndSetIfChanged(ref _hasError, value); }
    public string? ErrorMessage { get => _errorMessage; set => this.RaiseAndSetIfChanged(ref _errorMessage, value); }

    public string? ParentShowUri
    {
        get => _parentShowUri;
        private set
        {
            this.RaiseAndSetIfChanged(ref _parentShowUri, value);
            this.RaisePropertyChanged(nameof(HasParentShow));
            this.RaisePropertyChanged(nameof(BreadcrumbItems));
        }
    }

    public string? ParentShowTitle
    {
        get => _parentShowTitle;
        private set
        {
            this.RaiseAndSetIfChanged(ref _parentShowTitle, value);
            this.RaisePropertyChanged(nameof(HasParentShow));
            this.RaisePropertyChanged(nameof(BreadcrumbItems));
        }
    }

    public string? ParentShowImageUrl
    {
        get => _parentShowImageUrl;
        private set => this.RaiseAndSetIfChanged(ref _parentShowImageUrl, value);
    }

    public bool HasParentShow => !string.IsNullOrEmpty(_parentShowUri) && !string.IsNullOrEmpty(_parentShowTitle);

    /// <summary>Podcast hierarchy breadcrumb. Includes the parent show when it
    /// is known, then resolves the current episode title as metadata arrives.</summary>
    public IReadOnlyList<string> BreadcrumbItems
    {
        get
        {
            var crumbs = new List<string>(3) { "Podcasts" };
            if (HasParentShow) crumbs.Add(_parentShowTitle!);
            crumbs.Add(string.IsNullOrWhiteSpace(Title) ? "Episode" : Title);
            return crumbs;
        }
    }

    public ObservableCollection<EpisodeChapterVm> Chapters
    {
        get => _chapters;
        private set
        {
            this.RaiseAndSetIfChanged(ref _chapters, value);
            this.RaisePropertyChanged(nameof(HasChapters));
        }
    }
    public bool HasChapters => Chapters.Count > 0;

    public ObservableCollection<ShowEpisodeDto> MoreFromShow
    {
        get => _moreFromShow;
        private set
        {
            this.RaiseAndSetIfChanged(ref _moreFromShow, value);
            this.RaisePropertyChanged(nameof(HasMoreFromShow));
        }
    }
    public bool HasMoreFromShow => MoreFromShow.Count > 0;

    public ObservableCollection<PodcastCommentViewModel> Comments
    {
        get => _comments;
        private set
        {
            this.RaiseAndSetIfChanged(ref _comments, value);
            RaiseCommentStateChanged();
        }
    }

    public bool HasComments => Comments.Count > 0;
    public bool HasNoComments => !IsLoading && Comments.Count == 0;
    public bool HasMoreComments => !string.IsNullOrWhiteSpace(_commentsNextPageToken);
    public string CommentsCountLabel => _commentsTotalCount switch
    {
        <= 0 => "Comments",
        1 => "1 comment",
        _ => $"{_commentsTotalCount:N0} comments"
    };

    public string CommentDraft
    {
        get => _commentDraft;
        set
        {
            this.RaiseAndSetIfChanged(ref _commentDraft, value ?? "");
            this.RaisePropertyChanged(nameof(CommentCharacterCount));
            this.RaisePropertyChanged(nameof(CanSubmitComment));
        }
    }

    public string CommentCharacterCount => $"{Math.Min(CommentDraft.Length, MaxPodcastCommentLength)}/{MaxPodcastCommentLength}";

    public bool IsSubmittingComment
    {
        get => _isSubmittingComment;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isSubmittingComment, value);
            this.RaisePropertyChanged(nameof(CanSubmitComment));
        }
    }

    public bool IsLoadingMoreComments
    {
        get => _isLoadingMoreComments;
        private set => this.RaiseAndSetIfChanged(ref _isLoadingMoreComments, value);
    }

    public string? CommentStatus
    {
        get => _commentStatus;
        private set
        {
            this.RaiseAndSetIfChanged(ref _commentStatus, value);
            this.RaisePropertyChanged(nameof(HasCommentStatus));
        }
    }

    public bool HasCommentStatus => !string.IsNullOrWhiteSpace(CommentStatus);
    public bool CanSubmitComment
    {
        get
        {
            var text = NormalizeCommentText(CommentDraft);
            return !IsSubmittingComment
                   && text.Length > 0
                   && text.Length <= MaxPodcastCommentLength
                   && !string.IsNullOrWhiteSpace(EpisodeUri);
        }
    }

    public Brush? PaletteBackdropBrush { get => _paletteBackdropBrush; private set => this.RaiseAndSetIfChanged(ref _paletteBackdropBrush, value); }
    public Brush? PaletteCoverColorBrush { get => _paletteCoverColorBrush; private set => this.RaiseAndSetIfChanged(ref _paletteCoverColorBrush, value); }

    public EpisodePageViewModel(
        IPodcastService podcastService,
        ILibraryDataService libraryDataService,
        IPlaybackStateService playbackStateService,
        ILogger<EpisodePageViewModel>? logger = null)
    {
        _podcastService = podcastService ?? throw new ArgumentNullException(nameof(podcastService));
        _libraryDataService = libraryDataService ?? throw new ArgumentNullException(nameof(libraryDataService));
        _playbackStateService = playbackStateService ?? throw new ArgumentNullException(nameof(playbackStateService));
        _logger = logger;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        AttachLongLivedServices();
    }

    // Long-lived singleton subscriptions are attached lazily and detached on
    // Dispose so the (Transient) VM is not pinned across navigations.
    private bool _longLivedAttached;

    private void AttachLongLivedServices()
    {
        if (_longLivedAttached) return;
        _longLivedAttached = true;
        _playbackStateService.PropertyChanged += OnPlaybackStateChanged;
        _libraryDataService.PodcastEpisodeProgressChanged += OnPodcastEpisodeProgressChanged;
    }

    private void DetachLongLivedServices()
    {
        if (!_longLivedAttached) return;
        _longLivedAttached = false;
        _playbackStateService.PropertyChanged -= OnPlaybackStateChanged;
        _libraryDataService.PodcastEpisodeProgressChanged -= OnPodcastEpisodeProgressChanged;
    }

    /// <summary>Entry-point from <c>EpisodePage.OnNavigatedTo</c>.</summary>
    public void Activate(EpisodeNavigationParameter parameter)
    {
        AttachLongLivedServices();
        if (parameter is null || string.IsNullOrWhiteSpace(parameter.EpisodeUri))
            return;

        var episodeUri = parameter.EpisodeUri;
        if (string.Equals(_episodeUri, episodeUri, StringComparison.Ordinal) && _episode is not null && !_hasError)
            return;

        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();

        ResetState(parameter);
        TabItemParameter = new TabItemParameter(NavigationPageType.Episode, episodeUri)
        {
            Title = parameter.EpisodeTitle ?? "Episode"
        };

        _ = LoadAsync(parameter, _loadCts.Token);
    }

    private void ResetState(EpisodeNavigationParameter parameter)
    {
        EpisodeUri = parameter.EpisodeUri;

        // Pre-fill the parent show with what the caller passed so the breadcrumb
        // and "From show" line render in the first frame instead of waiting for
        // the network round-trip.
        ParentShowUri = parameter.ShowUri;
        ParentShowTitle = parameter.ShowTitle;
        ParentShowImageUrl = parameter.ShowImageUrl;

        // Synthesise a minimal Episode DTO from the navigation parameter so the
        // hero (cover + title) paints during load. The full DTO replaces this
        // once the metadata fetch resolves.
        if (!string.IsNullOrWhiteSpace(parameter.EpisodeTitle) || !string.IsNullOrWhiteSpace(parameter.EpisodeImageUrl))
        {
            Episode = new ShowEpisodeDto
            {
                Uri = parameter.EpisodeUri,
                Title = parameter.EpisodeTitle ?? "",
                CoverArtUrl = parameter.EpisodeImageUrl,
                ShowUri = parameter.ShowUri,
                ShowName = parameter.ShowTitle,
                ShowImageUrl = parameter.ShowImageUrl,
            };
        }
        else
        {
            Episode = null;
        }

        FullDescription = null;
        DescriptionHtml = null;
        ShareUrl = null;
        Chapters.Clear();
        this.RaisePropertyChanged(nameof(HasChapters));
        MoreFromShow.Clear();
        this.RaisePropertyChanged(nameof(HasMoreFromShow));
        Comments.Clear();
        _commentsNextPageToken = null;
        _commentsTotalCount = 0;
        CommentDraft = "";
        CommentStatus = null;
        IsSubmittingComment = false;
        IsLoadingMoreComments = false;
        RaiseCommentStateChanged();

        _palette = null;
        ApplyTheme(_isDarkTheme);

        IsLoading = true;
        HasError = false;
        ErrorMessage = null;
    }

    private async Task LoadAsync(EpisodeNavigationParameter parameter, CancellationToken ct)
    {
        try
        {
            // Episode metadata + chapters in parallel — both target the same URI
            // and neither blocks the other.
            var episodesTask = _podcastService.GetEpisodesAsync(new[] { parameter.EpisodeUri }, ct);
            var chaptersTask = _podcastService.GetEpisodeChaptersAsync(parameter.EpisodeUri, ct);
            var detailTask = _libraryDataService.GetPodcastEpisodeDetailAsync(parameter.EpisodeUri, ct);

            try
            {
                await Task.WhenAll(episodesTask, chaptersTask, detailTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "EpisodePageViewModel parallel fetch had a partial failure for {Uri}", parameter.EpisodeUri);
            }

            if (ct.IsCancellationRequested) return;

            var episodes = episodesTask.IsCompletedSuccessfully ? episodesTask.Result : Array.Empty<ShowEpisodeDto>();
            var chapters = chaptersTask.IsCompletedSuccessfully ? chaptersTask.Result : Array.Empty<EpisodeChapterVm>();
            var episodeDetail = detailTask.IsCompletedSuccessfully ? detailTask.Result : null;

            var episode = episodes.FirstOrDefault();
            if (episode is null)
            {
                _dispatcherQueue.TryEnqueue(() =>
                {
                    HasError = true;
                    ErrorMessage = "We couldn't load this episode.";
                    IsLoading = false;
                });
                return;
            }

            var resolvedShowUri = episodeDetail?.ShowUri ?? episode.ShowUri ?? parameter.ShowUri;
            if (episodeDetail is not null)
            {
                episode = episode.WithPlaybackProgress(
                    (long)Math.Max(0, episodeDetail.PlayedPosition.TotalMilliseconds),
                    episodeDetail.PlayedState);
            }

            var commentsPage = episodeDetail is null
                ? null
                : new PodcastEpisodeCommentsPageDto
                {
                    Items = episodeDetail.Comments,
                    NextPageToken = episodeDetail.CommentsNextPageToken,
                    TotalCount = episodeDetail.CommentsTotalCount
                };

            _dispatcherQueue.TryEnqueue(() =>
            {
                Episode = episode;
                FullDescription = episodeDetail?.Description ?? episode.FullDescription ?? episode.DescriptionPreview;
                DescriptionHtml = episodeDetail?.HtmlDescription
                                  ?? episodeDetail?.Description
                                  ?? episode.DescriptionHtml
                                  ?? episode.FullDescription
                                  ?? episode.DescriptionPreview;
                if (!string.IsNullOrWhiteSpace(episodeDetail?.ShareUrl))
                    ShareUrl = episodeDetail.ShareUrl;

                Chapters = new ObservableCollection<EpisodeChapterVm>(chapters);
                this.RaisePropertyChanged(nameof(HasChapters));
                RefreshChapterTimeline();
                ApplyCommentsPage(commentsPage, replace: true);

                var detailShowTitle = episodeDetail?.ShowName ?? episode.ShowName;
                var detailShowImageUrl = episodeDetail?.ShowImageUrl ?? episode.ShowImageUrl;
                if (string.IsNullOrEmpty(ParentShowTitle) && !string.IsNullOrEmpty(detailShowTitle))
                    ParentShowTitle = detailShowTitle;
                if (string.IsNullOrEmpty(ParentShowImageUrl) && !string.IsNullOrEmpty(detailShowImageUrl))
                    ParentShowImageUrl = detailShowImageUrl;
                if (string.IsNullOrEmpty(ParentShowUri) && !string.IsNullOrEmpty(resolvedShowUri))
                    ParentShowUri = resolvedShowUri;
            });

            // Show detail (palette + sibling episodes) — only fires if we know
            // the parent. Failure is non-fatal; the page still renders without
            // the "More from this show" rail.
            if (!string.IsNullOrWhiteSpace(resolvedShowUri))
            {
                try
                {
                    var showDetail = await _podcastService.GetShowDetailAsync(resolvedShowUri!, ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested) return;
                    if (showDetail is not null)
                    {
                        var siblingUris = showDetail.EpisodeUris
                            .Where(uri => !string.Equals(uri, parameter.EpisodeUri, StringComparison.Ordinal))
                            .Take(8)
                            .ToList();

                        IReadOnlyList<ShowEpisodeDto> siblings = Array.Empty<ShowEpisodeDto>();
                        if (siblingUris.Count > 0)
                        {
                            try
                            {
                                siblings = await _podcastService.GetEpisodesAsync(siblingUris, ct).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) { return; }
                            catch (Exception ex)
                            {
                                _logger?.LogDebug(ex, "Sibling episodes fetch failed for show {ShowUri}", resolvedShowUri);
                            }
                        }

                        _dispatcherQueue.TryEnqueue(() =>
                        {
                            if (string.IsNullOrEmpty(ShareUrl))
                                ShareUrl = showDetail.ShareUrl;
                            ParentShowUri = showDetail.Uri;
                            ParentShowTitle = showDetail.Name;
                            if (!string.IsNullOrEmpty(showDetail.CoverArtUrl))
                                ParentShowImageUrl = showDetail.CoverArtUrl;

                            _palette = showDetail.Palette;
                            ApplyTheme(_isDarkTheme);

                            MoreFromShow = new ObservableCollection<ShowEpisodeDto>(siblings
                                .OrderByDescending(e => e.ReleaseDate ?? DateTimeOffset.MinValue)
                                .Take(6));
                            this.RaisePropertyChanged(nameof(HasMoreFromShow));
                        });
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Show detail fetch failed for episode {Uri}", parameter.EpisodeUri);
                }
            }

            _dispatcherQueue.TryEnqueue(() => IsLoading = false);
        }
        catch (OperationCanceledException)
        {
            // Expected when navigating away mid-load.
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "EpisodePageViewModel.LoadAsync failed for {Uri}", parameter.EpisodeUri);
            _dispatcherQueue.TryEnqueue(() =>
            {
                HasError = true;
                ErrorMessage = ex.Message;
                IsLoading = false;
            });
        }
    }

    private void ApplyCommentsPage(PodcastEpisodeCommentsPageDto? page, bool replace)
    {
        if (replace)
            Comments.Clear();

        if (page is not null)
        {
            foreach (var comment in page.Items)
                Comments.Add(new PodcastCommentViewModel(comment, _libraryDataService, _logger));

            _commentsNextPageToken = page.NextPageToken;
            _commentsTotalCount = Math.Max(page.TotalCount, Comments.Count);
        }
        else
        {
            _commentsNextPageToken = null;
            _commentsTotalCount = Comments.Count;
        }

        RaiseCommentStateChanged();
    }

    private void RaiseCommentStateChanged()
    {
        this.RaisePropertyChanged(nameof(HasComments));
        this.RaisePropertyChanged(nameof(HasNoComments));
        this.RaisePropertyChanged(nameof(HasMoreComments));
        this.RaisePropertyChanged(nameof(CommentsCountLabel));
    }

    public void ApplyTheme(bool isDark)
    {
        _isDarkTheme = isDark;
        var tier = _palette is null
            ? null
            : (isDark
                ? (_palette.HigherContrast ?? _palette.HighContrast)
                : (_palette.HighContrast ?? _palette.HigherContrast));

        if (tier is null)
        {
            PaletteBackdropBrush = null;
            PaletteCoverColorBrush = null;
            return;
        }

        var bg = Color.FromArgb(255, tier.BackgroundR, tier.BackgroundG, tier.BackgroundB);
        var bgTint = Color.FromArgb(255, tier.BackgroundTintedR, tier.BackgroundTintedG, tier.BackgroundTintedB);

        // Light mode: blend toward white before applying alpha so dark covers
        // don't drag the page dark. Dark mode unchanged.
        var washColor = isDark ? bg : TintColorHelper.LightTint(bg);
        PaletteBackdropBrush = new SolidColorBrush(Color.FromArgb(
            (byte)(isDark ? 60 : 38), washColor.R, washColor.G, washColor.B));

        var accentBase = TintColorHelper.BrightenForTint(bgTint, targetMax: 210);
        PaletteCoverColorBrush = new SolidColorBrush(accentBase);
    }

    private void UpdateTabTitle()
    {
        if (TabItemParameter != null && !string.IsNullOrEmpty(Title))
        {
            TabItemParameter.Title = Title;
            ContentChanged?.Invoke(this, TabItemParameter);
        }
    }

    private void OnPlaybackStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;

        if (e.PropertyName == nameof(IPlaybackStateService.Position) ||
            e.PropertyName == nameof(IPlaybackStateService.Duration) ||
            e.PropertyName == nameof(IPlaybackStateService.CurrentTrackId) ||
            e.PropertyName == nameof(IPlaybackStateService.CurrentContext) ||
            e.PropertyName == nameof(IPlaybackStateService.CurrentAlbumId) ||
            e.PropertyName == nameof(IPlaybackStateService.IsPlaying))
        {
            SchedulePlaybackUiRefresh();
        }
    }

    private void OnPodcastEpisodeProgressChanged(object? sender, PodcastEpisodeProgressChangedEventArgs e)
    {
        if (_disposed)
            return;

        _dispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed)
                return;

            ApplyPodcastEpisodeProgress(e);
        });
    }

    private void SchedulePlaybackUiRefresh()
    {
        if (_playbackUiRefreshScheduled) return;
        _playbackUiRefreshScheduled = true;
        if (!_dispatcherQueue.TryEnqueue(() =>
            {
                _playbackUiRefreshScheduled = false;
                ApplyCurrentPlaybackProgress();
                RefreshChapterTimeline();
            }))
        {
            _playbackUiRefreshScheduled = false;
        }
    }

    private void ApplyPodcastEpisodeProgress(PodcastEpisodeProgressChangedEventArgs e)
    {
        var positionMs = (long)Math.Max(0, e.Progress.PlayedPosition.TotalMilliseconds);
        if (e.Matches(_episodeUri))
            UpdateCurrentEpisodeProgress(positionMs, e.Progress.PlayedState, minPositionDeltaMs: 0);

        for (var i = 0; i < MoreFromShow.Count; i++)
        {
            var sibling = MoreFromShow[i];
            if (e.Matches(sibling.Uri))
                MoreFromShow[i] = sibling.WithPlaybackProgress(positionMs, e.Progress.PlayedState);
        }
    }

    private void ApplyCurrentPlaybackProgress()
    {
        var currentEpisodeUri = PlaybackSaveTargetResolver.GetEpisodeUri(_playbackStateService);
        if (string.IsNullOrEmpty(currentEpisodeUri))
            return;

        var positionMs = NormalizePlaybackMilliseconds(_playbackStateService.Position);
        var durationMs = Math.Max(
            NormalizePlaybackMilliseconds(_playbackStateService.Duration),
            Episode?.DurationMs ?? 0);
        var state = ResolvePlaybackOverlayState(positionMs, durationMs, _playbackStateService.IsPlaying);
        if (string.Equals(currentEpisodeUri, _episodeUri, StringComparison.Ordinal))
            UpdateCurrentEpisodeProgress(positionMs, state, PodcastProgressUiDeltaMs);

        for (var i = 0; i < MoreFromShow.Count; i++)
        {
            var sibling = MoreFromShow[i];
            if (string.Equals(sibling.Uri, currentEpisodeUri, StringComparison.Ordinal))
                MoreFromShow[i] = sibling.WithPlaybackProgress(positionMs, state);
        }
    }

    private void UpdateCurrentEpisodeProgress(long positionMs, string? playedState, long minPositionDeltaMs)
    {
        if (Episode is not { } current)
            return;

        var updated = current.WithPlaybackProgress(positionMs, playedState);
        var positionDelta = Math.Abs(current.PlayedPositionMs - updated.PlayedPositionMs);
        if (string.Equals(current.PlayedState, updated.PlayedState, StringComparison.Ordinal) &&
            positionDelta < minPositionDeltaMs &&
            string.Equals(current.MetaLine, updated.MetaLine, StringComparison.Ordinal))
        {
            return;
        }

        Episode = updated;
    }

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

    private void RefreshChapterTimeline()
    {
        if (Chapters.Count == 0 || string.IsNullOrEmpty(_episodeUri))
            return;

        // Only highlight the active chapter when the user is actually playing
        // this episode — otherwise the rail would always show chapter 1 active.
        var isCurrentEpisode = string.Equals(
            _playbackStateService.CurrentTrackId,
            _episodeUri,
            StringComparison.Ordinal);

        var positionMs = isCurrentEpisode ? Math.Max(0, _playbackStateService.Position) : 0;
        var durationMs = Math.Max(
            isCurrentEpisode ? Math.Max(0, _playbackStateService.Duration) : 0,
            _episode?.DurationMs ?? 0);

        for (var i = 0; i < Chapters.Count; i++)
        {
            var chapter = Chapters[i];
            var startMs = Math.Max(0, chapter.StartMilliseconds);
            var nextStartMs = i + 1 < Chapters.Count
                ? Math.Max(startMs, Chapters[i + 1].StartMilliseconds)
                : 0;
            var stopMs = chapter.StopMilliseconds > startMs ? chapter.StopMilliseconds : 0;
            var endMs = nextStartMs > startMs
                ? nextStartMs
                : stopMs > startMs
                    ? stopMs
                    : durationMs > startMs
                        ? durationMs
                        : startMs + 1;

            var isCompleted = isCurrentEpisode && positionMs >= endMs;
            var isActive = isCurrentEpisode && positionMs >= startMs && positionMs < endMs;
            var progress = !isCurrentEpisode || positionMs < startMs
                ? 0
                : isCompleted
                    ? 1
                    : 0.5 + (Math.Clamp((double)(positionMs - startMs) / Math.Max(1, endMs - startMs), 0, 1) * 0.5);

            chapter.SetTimelineState(isActive, isCompleted, progress);
        }
    }

    [RelayCommand]
    private void Play()
    {
        if (string.IsNullOrEmpty(_episodeUri)) return;

        // Single-item queue with an Episode context — same shape NavigationHelpers.PlayEpisode
        // uses, but we already have the metadata so we can carry title/duration/cover.
        var item = new QueueItem
        {
            TrackId = _episodeUri,
            Title = _episode?.Title ?? "",
            ArtistName = _parentShowTitle ?? "",
            AlbumArt = _episode?.CoverArtUrl ?? _parentShowImageUrl,
            DurationMs = _episode?.DurationMs ?? 0,
            IsUserQueued = false,
            AlbumName = _parentShowTitle,
            AlbumUri = string.IsNullOrEmpty(_parentShowUri) ? null : _parentShowUri,
        };

        var context = new PlaybackContextInfo
        {
            ContextUri = _episodeUri,
            Type = PlaybackContextType.Episode,
            Name = _episode?.Title ?? "",
            ImageUrl = _episode?.CoverArtUrl ?? _parentShowImageUrl,
        };

        _playbackStateService.LoadQueue(new[] { item }, context, 0);
    }

    [RelayCommand]
    private void SeekToChapter(EpisodeChapterVm? chapter)
    {
        if (chapter is null) return;

        // Only meaningful while this episode is the active playback target —
        // otherwise the seek would land on whatever else is playing. In that
        // case, start playback at the chapter's timestamp by re-loading the
        // queue (mirrors the in-app "play from chapter" path).
        var isCurrentEpisode = string.Equals(
            _playbackStateService.CurrentTrackId,
            _episodeUri,
            StringComparison.Ordinal);

        var target = Math.Max(0, chapter.StartMilliseconds);
        if (isCurrentEpisode)
        {
            _playbackStateService.Seek(target);
        }
        else
        {
            // Start the episode then jump — seek immediately after LoadQueue
            // because the player accepts the position before audio starts.
            Play();
            _playbackStateService.Seek(target);
        }

        RefreshChapterTimeline();
    }

    [RelayCommand]
    private void OpenShow()
    {
        if (string.IsNullOrEmpty(_parentShowUri)) return;
        NavigationHelpers.OpenShowPage(_parentShowUri!, _parentShowTitle ?? "Show", openInNewTab: NavigationHelpers.IsCtrlPressed());
    }

    [RelayCommand]
    private void OpenSiblingEpisode(ShowEpisodeDto? sibling)
    {
        if (sibling is null || string.IsNullOrEmpty(sibling.Uri)) return;
        NavigationHelpers.OpenEpisodePage(
            sibling.Uri,
            sibling.Title,
            sibling.CoverArtUrl,
            _parentShowUri,
            _parentShowTitle,
            _parentShowImageUrl,
            NavigationHelpers.IsCtrlPressed());
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
    private async Task SubmitCommentAsync()
    {
        var text = NormalizeCommentText(CommentDraft);
        if (text.Length == 0 || text.Length > MaxPodcastCommentLength || string.IsNullOrWhiteSpace(_episodeUri))
            return;

        IsSubmittingComment = true;
        CommentStatus = null;
        try
        {
            var comment = await _libraryDataService
                .CreatePodcastEpisodeCommentAsync(_episodeUri, text)
                .ConfigureAwait(true);

            Comments.Insert(0, new PodcastCommentViewModel(comment, _libraryDataService, _logger));
            _commentsTotalCount = Math.Max(_commentsTotalCount + 1, Comments.Count);
            CommentDraft = "";
            CommentStatus = "Comment saved locally. Spotify posting is not wired yet.";
            RaiseCommentStateChanged();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to add local podcast comment for {EpisodeUri}", _episodeUri);
            CommentStatus = "Could not add the comment.";
        }
        finally
        {
            IsSubmittingComment = false;
        }
    }

    [RelayCommand]
    private async Task LoadMoreCommentsAsync()
    {
        var token = _commentsNextPageToken;
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(_episodeUri))
            return;

        IsLoadingMoreComments = true;
        CommentStatus = null;
        try
        {
            var page = await _libraryDataService
                .GetPodcastEpisodeCommentsPageAsync(_episodeUri, token)
                .ConfigureAwait(true);

            ApplyCommentsPage(page, replace: false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load more podcast comments for {EpisodeUri}", _episodeUri);
            CommentStatus = "Could not load more comments.";
        }
        finally
        {
            IsLoadingMoreComments = false;
        }
    }

    private static string NormalizeCommentText(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? ""
            : Regex.Replace(text.Trim(), @"\s+", " ");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;

        DetachLongLivedServices();

        Chapters.Clear();
        MoreFromShow.Clear();
        Comments.Clear();
    }
}
