using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI.Animations;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Wavee.Core.Http;
using Wavee.Core.Http.Pathfinder;
using Wavee.Protocol.Metadata;
using Wavee.UI.Library.Local;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Messages;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Services;
using ProtoImage = Wavee.Protocol.Metadata.Image;

namespace Wavee.UI.WinUI.Controls.Local;

public sealed partial class LinkSpotifyTrackFlyout : UserControl
{
    private enum LinkPickerState
    {
        Idle,
        Searching,
        HasResults,
        Empty,
        Error,
        Resolving,
        PreviewReady,
        PreviewError,
    }

    private const int DebounceMs = 300;
    private const int SearchLimit = 8;
    private const int StripDelayMs = 120;
    private const int StripMinShowMs = 200;
    private const int ShimmerMinShowMs = 220;
    private const int CrossfadeOutMs = 180;
    private const int CrossfadeInMs = 220;

    public event EventHandler? RequestClose;

    private ILocalLibraryFacade? _facade;
    private ISearchService? _searchService;
    private IExtendedMetadataClient? _metadataClient;
    private string? _localMusicVideoTrackUri;
    private string? _localFilePath;
    private string? _currentSpotifyTrackUri;

    private readonly ObservableCollection<SearchResultItem> _results = new();

    private readonly DispatcherQueueTimer _searchTimer;
    private readonly DispatcherQueueTimer _stripDelayTimer;

    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _resolveCts;
    private CancellationTokenSource? _linkCts;
    private string _lastQuery = string.Empty;
    private LinkPickerState _state = LinkPickerState.Idle;
    private bool _shimmerLatched;
    private DateTime _shimmerShownAt;
    private bool _stripVisible;
    private DateTime _stripShownAt;
    private TrackPreview? _currentPreview;
    private string? _pendingSpotifyTrackUriToLink;
    private bool _disposed;

    public LinkSpotifyTrackFlyout()
    {
        InitializeComponent();

        ResultsHost.ItemsSource = _results;

        var skeletons = new List<object>();
        for (var i = 0; i < 5; i++) skeletons.Add(new object());
        ShimmerHost.ItemsSource = skeletons;

        var dq = DispatcherQueue.GetForCurrentThread();
        _searchTimer = dq.CreateTimer();
        _searchTimer.Interval = TimeSpan.FromMilliseconds(DebounceMs);
        _searchTimer.IsRepeating = false;
        _searchTimer.Tick += SearchTimer_Tick;

        _stripDelayTimer = dq.CreateTimer();
        _stripDelayTimer.Interval = TimeSpan.FromMilliseconds(StripDelayMs);
        _stripDelayTimer.IsRepeating = false;
        _stripDelayTimer.Tick += StripDelayTimer_Tick;

        Unloaded += OnUnloaded;
    }

    public void Initialize(
        ILocalLibraryFacade facade,
        string localMusicVideoTrackUri,
        string localFilePath,
        string? currentSpotifyTrackUri)
    {
        _facade = facade;
        _localMusicVideoTrackUri = localMusicVideoTrackUri;
        _localFilePath = localFilePath;
        _currentSpotifyTrackUri = currentSpotifyTrackUri;

        _searchService = Ioc.Default.GetService<ISearchService>();
        _metadataClient = Ioc.Default.GetService<IExtendedMetadataClient>();

        if (!string.IsNullOrEmpty(currentSpotifyTrackUri))
        {
            CurrentLinkBanner.Visibility = Visibility.Visible;
            CurrentLinkText.Text = currentSpotifyTrackUri;
            QueryBox.Text = currentSpotifyTrackUri;
        }
    }

    /// <summary>Called by the presenter after the hosting Flyout has opened.</summary>
    public void OnFlyoutOpened()
    {
        QueryBox.Focus(FocusState.Programmatic);
        QueryBox.SelectAll();

        // Defer the first transition by one dispatcher tick so layout settles
        // before any opacity animation runs. Prevents acrylic first-frame ghosting.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_disposed) return;
            EvaluateCurrentQuery(initial: true);
        });
    }

    public void OnFlyoutClosed()
    {
        _disposed = true;
        _searchTimer.Stop();
        _stripDelayTimer.Stop();
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
        _resolveCts?.Cancel();
        _resolveCts?.Dispose();
        _resolveCts = null;
        _linkCts?.Cancel();
        _linkCts?.Dispose();
        _linkCts = null;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => OnFlyoutClosed();

    // ── Input handlers ──────────────────────────────────────────────────

    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_disposed) return;
        UpdateTrailingAffordance();
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void UpdateTrailingAffordance()
    {
        var searching = _state == LinkPickerState.Searching || _state == LinkPickerState.Resolving;
        SearchSpinner.IsActive = searching;
        SearchSpinner.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void QueryBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_disposed) return;
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Enter:
                e.Handled = true;
                _searchTimer.Stop();
                if (LocalItemContextMenuPresenter.TryNormalizeSpotifyTrackUri(QueryBox.Text, out var uri))
                {
                    await LinkAsync(uri);
                }
                else if (_state == LinkPickerState.HasResults && ResultsHost.SelectedItem is SearchResultItem selected)
                {
                    await LinkAsync(selected.Uri);
                }
                else if (_state == LinkPickerState.HasResults && _results.Count > 0)
                {
                    await LinkAsync(_results[0].Uri);
                }
                else
                {
                    EvaluateCurrentQuery();
                }
                break;
            case Windows.System.VirtualKey.Down:
                if (_state == LinkPickerState.HasResults && _results.Count > 0)
                {
                    e.Handled = true;
                    var i = ResultsHost.SelectedIndex;
                    ResultsHost.SelectedIndex = Math.Min(i + 1, _results.Count - 1);
                    ResultsHost.ScrollIntoView(ResultsHost.SelectedItem);
                }
                break;
            case Windows.System.VirtualKey.Up:
                if (_state == LinkPickerState.HasResults && _results.Count > 0)
                {
                    e.Handled = true;
                    var i = ResultsHost.SelectedIndex;
                    ResultsHost.SelectedIndex = Math.Max(i - 1, 0);
                    ResultsHost.ScrollIntoView(ResultsHost.SelectedItem);
                }
                break;
            case Windows.System.VirtualKey.Escape:
                e.Handled = true;
                RequestClose?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        QueryBox.Text = string.Empty;
        QueryBox.Focus(FocusState.Programmatic);
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        EvaluateCurrentQuery(force: true);
        await Task.CompletedTask;
    }

    private async void ResultsHost_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SearchResultItem item)
        {
            await LinkAsync(item.Uri);
        }
    }

    private async void PreviewLinkButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPreview is { Uri: { Length: > 0 } uri })
        {
            await LinkAsync(uri);
        }
        else if (_pendingSpotifyTrackUriToLink is { Length: > 0 } fallback)
        {
            await LinkAsync(fallback);
        }
    }

    private async void PreviewLinkByUriButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingSpotifyTrackUriToLink is { Length: > 0 } uri)
        {
            await LinkAsync(uri);
        }
    }

    private void SearchTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        EvaluateCurrentQuery();
    }

    private void StripDelayTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (_state == LinkPickerState.Searching)
        {
            ShowStrip();
        }
    }

    // ── Routing ─────────────────────────────────────────────────────────

    private void EvaluateCurrentQuery(bool initial = false, bool force = false)
    {
        if (_disposed) return;
        var text = QueryBox.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(text))
        {
            CancelSearch();
            CancelResolve();
            TransitionTo(LinkPickerState.Idle);
            return;
        }

        if (LocalItemContextMenuPresenter.TryNormalizeSpotifyTrackUri(text, out var normalizedUri))
        {
            // Cancel any in-flight live search; switch to preview track flow.
            CancelSearch();
            if (!force && _pendingSpotifyTrackUriToLink == normalizedUri
                && (_state == LinkPickerState.PreviewReady
                    || _state == LinkPickerState.Resolving
                    || _state == LinkPickerState.PreviewError))
                return;
            _pendingSpotifyTrackUriToLink = normalizedUri;
            _ = ResolvePreviewAsync(normalizedUri);
            return;
        }

        // Not a URI — live search path. Cancel any preview resolve.
        CancelResolve();
        _pendingSpotifyTrackUriToLink = null;
        if (text.Length < 2)
        {
            TransitionTo(LinkPickerState.Idle);
            return;
        }
        if (!force && text == _lastQuery && _state == LinkPickerState.HasResults) return;

        _ = SearchAsync(text);
    }

    // ── Live search ─────────────────────────────────────────────────────

    private async Task SearchAsync(string query)
    {
        if (_searchService is null) return;
        _lastQuery = query;

        CancelSearch();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        var hadResults = _state == LinkPickerState.HasResults && _results.Count > 0;
        TransitionTo(LinkPickerState.Searching, hadPriorResults: hadResults);

        try
        {
            var page = await _searchService.SearchTracksAsync(query, 0, SearchLimit, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested || _disposed) return;

            var tracks = page.Items
                .Where(it => it.Type == SearchResultType.Track
                             && LocalItemContextMenuPresenter.TryNormalizeSpotifyTrackUri(it.Uri, out _))
                .Take(SearchLimit)
                .ToList();

            await EnsureShimmerMinShowAsync();
            if (cts.IsCancellationRequested || _disposed) return;

            ReconcileResults(tracks);

            if (tracks.Count == 0)
            {
                TransitionTo(LinkPickerState.Empty);
            }
            else
            {
                TransitionTo(LinkPickerState.HasResults);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!cts.IsCancellationRequested && !_disposed)
            {
                ErrorText.Text = "Search failed.";
                TransitionTo(LinkPickerState.Error);
            }
        }
    }

    private void ReconcileResults(IReadOnlyList<SearchResultItem> incoming)
    {
        // In-place reconciliation keyed by Uri — keeps existing containers/decoders warm.
        var existing = _results;
        var max = Math.Max(existing.Count, incoming.Count);
        for (var i = 0; i < max; i++)
        {
            if (i >= incoming.Count)
            {
                // Surplus old items beyond new length — trim from end.
                continue;
            }

            if (i >= existing.Count)
            {
                existing.Add(incoming[i]);
                continue;
            }

            if (!string.Equals(existing[i].Uri, incoming[i].Uri, StringComparison.Ordinal))
            {
                existing[i] = incoming[i];
            }
        }

        while (existing.Count > incoming.Count)
        {
            existing.RemoveAt(existing.Count - 1);
        }
    }

    // ── Preview resolution ──────────────────────────────────────────────

    private async Task ResolvePreviewAsync(string spotifyTrackUri)
    {
        CancelResolve();
        var cts = new CancellationTokenSource();
        _resolveCts = cts;

        TransitionTo(LinkPickerState.Resolving);

        if (_metadataClient is null)
        {
            // No metadata client available — degrade immediately to "link by URI".
            if (!cts.IsCancellationRequested && !_disposed)
            {
                _currentPreview = null;
                TransitionTo(LinkPickerState.PreviewError);
            }
            return;
        }

        try
        {
            var track = await _metadataClient.GetTrackAsync(spotifyTrackUri, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested || _disposed) return;
            if (track is null)
            {
                _currentPreview = null;
                TransitionTo(LinkPickerState.PreviewError);
                return;
            }

            var preview = BuildPreview(track, spotifyTrackUri);
            ApplyPreview(preview);
            _currentPreview = preview;
            TransitionTo(LinkPickerState.PreviewReady);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!cts.IsCancellationRequested && !_disposed)
            {
                _currentPreview = null;
                TransitionTo(LinkPickerState.PreviewError);
            }
        }
    }

    private static TrackPreview BuildPreview(Protocol.Metadata.Track track, string uri)
    {
        var artists = track.Artist.Count > 0
            ? string.Join(", ", track.Artist.Select(a => a.Name))
            : null;
        var album = track.Album?.Name;
        var duration = (int)track.Duration;

        string? imageUri = null;
        var images = track.Album?.CoverGroup?.Image;
        if (images is { Count: > 0 })
        {
            var img = images.FirstOrDefault(i => i.Size == ProtoImage.Types.Size.Default)
                      ?? images.FirstOrDefault();
            if (img is not null && img.FileId.Length > 0)
            {
                imageUri = "spotify:image:" + Convert.ToHexString(img.FileId.ToByteArray()).ToLowerInvariant();
            }
        }

        return new TrackPreview(uri, track.Name, artists, album, duration, imageUri);
    }

    private void ApplyPreview(TrackPreview preview)
    {
        PreviewTitle.Text = preview.Title ?? string.Empty;
        var sub = preview.Artists ?? string.Empty;
        PreviewSubtitle.Text = sub;
        var metaParts = new List<string>();
        if (!string.IsNullOrEmpty(preview.Album)) metaParts.Add(preview.Album!);
        if (preview.DurationMs > 0) metaParts.Add(FormatDuration(preview.DurationMs));
        PreviewMeta.Text = string.Join("  ·  ", metaParts);

        var httpsUrl = SpotifyImageHelper.ToHttpsUrl(preview.ImageUri);
        if (!string.IsNullOrEmpty(httpsUrl) && Uri.TryCreate(httpsUrl, UriKind.Absolute, out var u))
        {
            PreviewArt.Source = new BitmapImage(u) { DecodePixelWidth = 144, DecodePixelType = DecodePixelType.Logical };
        }
        else
        {
            PreviewArt.Source = null;
        }
    }

    // ── State machine ───────────────────────────────────────────────────

    private void TransitionTo(LinkPickerState next, bool hadPriorResults = false)
    {
        if (_disposed) return;
        var prev = _state;
        _state = next;

        switch (next)
        {
            case LinkPickerState.Idle:
                HideAllResultPanels();
                HideStrip();
                CrossfadeIn(IdlePanel);
                break;
            case LinkPickerState.Searching:
                HideIdle();
                if (hadPriorResults)
                {
                    ResultsHost.SelectedIndex = -1;
                    // Warm — keep results visible; show top strip after StripDelayMs.
                    ShowResultsHost(animate: false);
                    HideShimmer();
                    HideEmpty();
                    HideError();
                    HidePreviewPanels();
                    _stripDelayTimer.Stop();
                    _stripDelayTimer.Start();
                }
                else
                {
                    // Cold — show shimmer, hide everything else.
                    HideResultsHost();
                    HideEmpty();
                    HideError();
                    HidePreviewPanels();
                    HideStrip();
                    ShowShimmer();
                }
                break;
            case LinkPickerState.HasResults:
                HideIdle();
                HideStrip();
                HideShimmer();
                HideEmpty();
                HideError();
                HidePreviewPanels();
                ShowResultsHost(animate: prev == LinkPickerState.Searching);
                break;
            case LinkPickerState.Empty:
                HideIdle();
                HideStrip();
                HideResultsHost();
                HideShimmer();
                HideError();
                HidePreviewPanels();
                CrossfadeIn(EmptyPanel);
                break;
            case LinkPickerState.Error:
                HideIdle();
                HideStrip();
                HideResultsHost();
                HideShimmer();
                HideEmpty();
                HidePreviewPanels();
                CrossfadeIn(ErrorPanel);
                break;
            case LinkPickerState.Resolving:
                HideIdle();
                HideStrip();
                HideResultsHost();
                HideShimmer();
                HideEmpty();
                HideError();
                ShowPreview(PreviewResolvingCard);
                break;
            case LinkPickerState.PreviewReady:
                HideIdle();
                ShowPreview(PreviewReadyCard);
                break;
            case LinkPickerState.PreviewError:
                HideIdle();
                ShowPreview(PreviewErrorCard);
                break;
        }

        UpdateTrailingAffordance();
    }

    private void HideAllResultPanels()
    {
        HideResultsHost();
        HideShimmer();
        HideEmpty();
        HideError();
        HidePreviewPanels();
    }

    private void HideIdle()
    {
        IdlePanel.Visibility = Visibility.Collapsed;
        IdlePanel.Opacity = 0;
    }

    private void ShowResultsHost(bool animate)
    {
        ResultsHost.Visibility = Visibility.Visible;
        if (animate)
        {
            AnimationBuilder.Create()
                .Opacity(from: 0, to: 1,
                         duration: TimeSpan.FromMilliseconds(CrossfadeInMs),
                         layer: FrameworkLayer.Composition)
                .Translation(from: new System.Numerics.Vector2(0, 6),
                             to: System.Numerics.Vector2.Zero,
                             duration: TimeSpan.FromMilliseconds(CrossfadeInMs),
                             layer: FrameworkLayer.Composition)
                .Start(ResultsHost);
        }
        else
        {
            ResultsHost.Opacity = 1;
        }
    }

    private void HideResultsHost()
    {
        ResultsHost.Visibility = Visibility.Collapsed;
        ResultsHost.SelectedIndex = -1;
    }

    private void ShowShimmer()
    {
        ShimmerHost.Visibility = Visibility.Visible;
        ShimmerHost.Opacity = 1;
        _shimmerLatched = true;
        _shimmerShownAt = DateTime.UtcNow;
    }

    private void HideShimmer()
    {
        if (!_shimmerLatched)
        {
            ShimmerHost.Visibility = Visibility.Collapsed;
            return;
        }
        AnimationBuilder.Create()
            .Opacity(from: 1, to: 0,
                     duration: TimeSpan.FromMilliseconds(CrossfadeOutMs),
                     layer: FrameworkLayer.Composition)
            .Start(ShimmerHost);
        var token = ShimmerHost;
        _ = Task.Delay(CrossfadeOutMs + 20).ContinueWith(_ =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_state == LinkPickerState.Searching) return; // re-armed
                token.Visibility = Visibility.Collapsed;
            });
        });
        _shimmerLatched = false;
    }

    private async Task EnsureShimmerMinShowAsync()
    {
        if (!_shimmerLatched) return;
        var elapsed = DateTime.UtcNow - _shimmerShownAt;
        var remaining = ShimmerMinShowMs - (int)elapsed.TotalMilliseconds;
        if (remaining > 0)
        {
            await Task.Delay(remaining).ConfigureAwait(true);
        }
    }

    private void HideEmpty()
    {
        EmptyPanel.Visibility = Visibility.Collapsed;
        EmptyPanel.Opacity = 0;
    }

    private void HideError()
    {
        ErrorPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Opacity = 0;
    }

    private void HidePreviewPanels()
    {
        PreviewPanel.Visibility = Visibility.Collapsed;
        PreviewResolvingCard.Visibility = Visibility.Collapsed;
        PreviewReadyCard.Visibility = Visibility.Collapsed;
        PreviewErrorCard.Visibility = Visibility.Collapsed;
    }

    private void ShowPreview(FrameworkElement card)
    {
        PreviewPanel.Visibility = Visibility.Visible;
        PreviewPanel.Opacity = 1;
        PreviewResolvingCard.Visibility = card == PreviewResolvingCard ? Visibility.Visible : Visibility.Collapsed;
        PreviewReadyCard.Visibility = card == PreviewReadyCard ? Visibility.Visible : Visibility.Collapsed;
        PreviewErrorCard.Visibility = card == PreviewErrorCard ? Visibility.Visible : Visibility.Collapsed;

        CrossfadeIn(card);
    }

    private static void CrossfadeIn(FrameworkElement element)
    {
        element.Visibility = Visibility.Visible;
        AnimationBuilder.Create()
            .Opacity(from: 0, to: 1,
                     duration: TimeSpan.FromMilliseconds(CrossfadeInMs),
                     layer: FrameworkLayer.Composition)
            .Translation(from: new System.Numerics.Vector2(0, 6),
                         to: System.Numerics.Vector2.Zero,
                         duration: TimeSpan.FromMilliseconds(CrossfadeInMs),
                         layer: FrameworkLayer.Composition)
            .Start(element);
    }

    private void ShowStrip()
    {
        if (_stripVisible) return;
        InProgressStrip.Opacity = 1;
        _stripVisible = true;
        _stripShownAt = DateTime.UtcNow;
    }

    private void HideStrip()
    {
        if (!_stripVisible)
        {
            InProgressStrip.Opacity = 0;
            return;
        }
        var elapsed = DateTime.UtcNow - _stripShownAt;
        var remaining = StripMinShowMs - (int)elapsed.TotalMilliseconds;
        if (remaining <= 0)
        {
            FadeStripOut();
            return;
        }
        _ = Task.Delay(remaining).ContinueWith(_ =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_state == LinkPickerState.Searching) return; // back to in-flight, leave visible
                FadeStripOut();
            });
        });
    }

    private void FadeStripOut()
    {
        AnimationBuilder.Create()
            .Opacity(from: 1, to: 0,
                     duration: TimeSpan.FromMilliseconds(CrossfadeOutMs),
                     layer: FrameworkLayer.Composition)
            .Start(InProgressStrip);
        _stripVisible = false;
    }

    private void CancelSearch()
    {
        _searchTimer.Stop();
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
    }

    private void CancelResolve()
    {
        _resolveCts?.Cancel();
        _resolveCts?.Dispose();
        _resolveCts = null;
    }

    // ── Link action ─────────────────────────────────────────────────────

    private async Task LinkAsync(string spotifyTrackUri)
    {
        if (_facade is null || string.IsNullOrEmpty(_localMusicVideoTrackUri)) return;
        if (!LocalItemContextMenuPresenter.TryNormalizeSpotifyTrackUri(spotifyTrackUri, out var normalized))
            return;

        _linkCts?.Cancel();
        _linkCts?.Dispose();
        _linkCts = new CancellationTokenSource();

        try
        {
            await _facade.LinkMusicVideoToSpotifyTrackAsync(_localMusicVideoTrackUri, normalized);
            var metadata = Ioc.Default.GetService<IMusicVideoMetadataService>();
            metadata?.NoteVideoUri(normalized, _localMusicVideoTrackUri);
            Ioc.Default.GetService<IMessenger>()?
                .Send(new MusicVideoAvailabilityMessage(normalized, true));
        }
        catch
        {
            // Surface link failures by leaving the flyout open; future improvement
            // could show a status row. For now, fall through to close so the menu
            // doesn't appear stuck.
        }

        // Best-effort: pull the linked track's title / artist / cover and lay
        // them on top of the local row as overrides so the card refreshes to
        // the Spotify metadata. Failures (offline, unknown track) are swallowed
        // inside the facade — the link itself already stands.
        if (!string.IsNullOrEmpty(_localFilePath))
        {
            try
            {
                await _facade.EnrichLinkedMusicVideoFromSpotifyAsync(
                    _localMusicVideoTrackUri,
                    _localFilePath!,
                    normalized);
            }
            catch { /* belt-and-braces — facade already swallows */ }
        }

        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    // ── x:Bind helpers ──────────────────────────────────────────────────

    public static string FormatDuration(long durationMs)
    {
        if (durationMs <= 0) return string.Empty;
        var ts = TimeSpan.FromMilliseconds(durationMs);
        return ts.TotalHours >= 1
            ? string.Format("{0}:{1:00}:{2:00}", (int)ts.TotalHours, ts.Minutes, ts.Seconds)
            : string.Format("{0}:{1:00}", ts.Minutes, ts.Seconds);
    }

    private sealed record TrackPreview(
        string Uri,
        string? Title,
        string? Artists,
        string? Album,
        int DurationMs,
        string? ImageUri);
}
