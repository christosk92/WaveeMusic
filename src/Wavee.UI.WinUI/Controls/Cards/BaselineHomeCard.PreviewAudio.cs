using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using Wavee.Playback.Contracts;
using Wavee.UI.Contracts;
using Wavee.UI.Models;
using Wavee.UI.Services;

namespace Wavee.UI.WinUI.Controls.Cards;

/// <summary>
/// Preview-audio state machine for <see cref="BaselineHomeCard"/>: schedule /
/// start / stop via <see cref="ICardPreviewPlaybackCoordinator"/>, the
/// "pending" intermediate visual state (progress-bar animation gates the
/// 1-second hover-autoplay delay), the live audio visualiser pump, the
/// track-play button, and auto-advance after the active preview ends.
///
/// <para>Owns the preview-audio mirror of the playback state, the pending-
/// visual versioning that protects against superseded delays from racing,
/// and the visualiser session-id filter that drops frames from previous
/// preview tracks after a fast user nav.</para>
/// </summary>
public sealed partial class BaselineHomeCard
{
    private static readonly TimeSpan PreviewHoverAutoplayDelay = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan PreviewPendingVisualDelay = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan PreviewPendingProgressDuration = PreviewHoverAutoplayDelay - PreviewPendingVisualDelay;

    private bool _isPreviewAudioPending;
    private bool _isPreviewAudioPlaying;
    private int _previewPendingVisualVersion;
    private Storyboard? _previewPendingProgressStoryboard;
    private bool _hasPreviewVisualization;
    private string? _previewVisualizationSessionId;
    private string? _previewVisualizationUrl;

    // ── Preview button + visualiser visual state ─────────────────────────────

    private void UpdatePreviewButtonVisualState()
    {
        if (TrackPlayButtonContent != null)
            TrackPlayButtonContent.IsPending = _isPreviewAudioPending;
    }

    private void UpdatePreviewVisualState(bool hasPreviewAudio)
    {
        var showPreviewVisualization = _isPointerOver && hasPreviewAudio && (_hasPreviewVisualization || _isPreviewAudioPending);
        if (showPreviewVisualization)
            EnsurePreviewVisualizerRealized();

        if (PreviewOverlayRoot != null)
            PreviewOverlayRoot.Visibility = showPreviewVisualization ? Visibility.Visible : Visibility.Collapsed;

        if (PreviewVisualizer != null)
        {
            PreviewVisualizer.Visibility = showPreviewVisualization ? Visibility.Visible : Visibility.Collapsed;
            PreviewVisualizer.SetPending(showPreviewVisualization && _isPreviewAudioPending && !_isPreviewAudioPlaying);
            PreviewVisualizer.SetActive(showPreviewVisualization && _isPreviewAudioPlaying);
        }

        UpdatePreviewPendingProgressBarState();
    }

    private void UpdatePreviewPendingProgressBarState()
    {
        var showPendingProgress = _isPointerOver && _isPreviewAudioPending && !_isPreviewAudioPlaying;
        if (PreviewPendingProgressBar != null)
            PreviewPendingProgressBar.Visibility = showPendingProgress ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Pending visual state (versioned) ─────────────────────────────────────

    private void ClearPreviewPendingVisualState()
    {
        _previewPendingVisualVersion++;
        StopPreviewPendingProgressBarAnimation(resetValue: true);
        if (_isPreviewAudioPending)
            _isPreviewAudioPending = false;

        UpdatePreviewButtonVisualState();
        UpdatePreviewVisualState(!string.IsNullOrWhiteSpace(GetActiveAudioPreviewUrl()));
    }

    private void QueuePreviewPendingVisualState()
    {
        var version = ++_previewPendingVisualVersion;
        _ = ShowPreviewPendingVisualStateAsync(version);
    }

    private async Task ShowPreviewPendingVisualStateAsync(int version)
    {
        await Task.Delay(PreviewPendingVisualDelay);

        if (version != _previewPendingVisualVersion || !_isPointerOver || _isPreviewAudioPlaying)
            return;

        var hasPreviewAudio = !string.IsNullOrWhiteSpace(GetActiveAudioPreviewUrl());
        if (!hasPreviewAudio)
            return;

        _isPreviewAudioPending = true;
        UpdatePreviewButtonVisualState();
        UpdatePreviewVisualState(true);
        StartPreviewPendingProgressBarAnimation();
    }

    private void StartPreviewPendingProgressBarAnimation()
    {
        if (PreviewPendingProgressBar == null)
            return;

        StopPreviewPendingProgressBarAnimation(resetValue: true);

        var storyboard = new Storyboard();
        var animation = new DoubleAnimation
        {
            From = 0,
            To = 100,
            Duration = new Duration(PreviewPendingProgressDuration),
            EnableDependentAnimation = true
        };

        Storyboard.SetTarget(animation, PreviewPendingProgressBar);
        Storyboard.SetTargetProperty(animation, "Value");
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) =>
        {
            if (ReferenceEquals(_previewPendingProgressStoryboard, storyboard))
                _previewPendingProgressStoryboard = null;
        };

        _previewPendingProgressStoryboard = storyboard;
        storyboard.Begin();
    }

    private void StopPreviewPendingProgressBarAnimation(bool resetValue)
    {
        if (_previewPendingProgressStoryboard != null)
        {
            _previewPendingProgressStoryboard.Stop();
            _previewPendingProgressStoryboard = null;
        }

        if (resetValue && PreviewPendingProgressBar != null)
            PreviewPendingProgressBar.Value = 0;
    }

    // ── Live visualiser frames ───────────────────────────────────────────────

    private void StartPreviewVisualization(bool hasLiveVisualization)
    {
        _hasPreviewVisualization = hasLiveVisualization;
        var previewUrl = GetActiveAudioPreviewUrl();
        if (!_isPointerOver || string.IsNullOrWhiteSpace(previewUrl) || !hasLiveVisualization)
        {
            UpdatePreviewVisualState(!string.IsNullOrWhiteSpace(previewUrl));
            return;
        }

        EnsurePreviewVisualizerRealized();
        ClearPreviewPendingVisualState();
        if (PreviewVisualizer != null)
        {
            PreviewVisualizer.Visibility = Visibility.Visible;
            PreviewVisualizer.Reset();
            PreviewVisualizer.SetPending(false);
            PreviewVisualizer.SetActive(true);
        }

        _previewVisualizationUrl = previewUrl;
        UpdatePreviewVisualState(true);
    }

    private void StopPreviewVisualization(bool preservePendingState = false)
    {
        _hasPreviewVisualization = false;
        _previewVisualizationSessionId = null;
        _previewVisualizationUrl = null;
        if (!preservePendingState)
            ClearPreviewPendingVisualState();

        if (PreviewVisualizer != null)
            PreviewVisualizer.Reset();

        UpdatePreviewVisualState(!string.IsNullOrWhiteSpace(GetActiveAudioPreviewUrl()));
    }

    private void OnPreviewVisualizationFrame(PreviewVisualizationFrame frame)
    {
        if (!_isPointerOver || !_isPreviewAudioPlaying || PreviewVisualizer == null)
            return;

        if (!string.IsNullOrWhiteSpace(_previewVisualizationSessionId) &&
            !string.Equals(frame.SessionId, _previewVisualizationSessionId, StringComparison.Ordinal))
            return;

        if (frame.Completed)
        {
            PreviewVisualizer.Complete();
            return;
        }

        PreviewVisualizer.PushLevels(frame.Amplitudes);
    }

    private void OnPreviewAudioCompleted()
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (!_isPointerOver)
                return;

            await AutoAdvancePreviewAfterAudioEndedAsync();
        });
    }

    // ── Track-play button ────────────────────────────────────────────────────

    private async void TrackPlayButton_Click(object sender, RoutedEventArgs e)
    {
        var playback = _playbackService;
        var playbackState = _playbackStateService;
        if (playback == null) return;

        var item = Item;
        var track = GetActivePreviewTrack();
        if (item == null || string.IsNullOrEmpty(item.Uri)) return;

        StopPreviewAudio();

        try
        {
            SetPlaybackPending(true);
            playbackState?.NotifyBuffering(null);

            var options = !string.IsNullOrEmpty(track?.Uri)
                ? new PlayContextOptions { StartTrackUri = track.Uri }
                : null;
            var result = await Task.Run(async () => await playback.PlayContextAsync(item.Uri, options));
            if (!result.IsSuccess)
            {
                SetPlaybackPending(false);
                playbackState?.ClearBuffering();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetPlaybackPending(false);
            playbackState?.ClearBuffering();
            Debug.WriteLine($"[BaselineHomeCard] Track play failed: {ex.Message}");
        }
    }

    // ── Coordinator-facing scheduling / start / stop ─────────────────────────

    private Task SchedulePreviewAudioAsync()
    {
        var request = CreatePreviewRequest();
        if (request == null || _previewPlaybackCoordinator == null)
        {
            TraceCard("SchedulePreviewAudioAsync skipped: no request or coordinator");
            return Task.CompletedTask;
        }

        TraceCard($"SchedulePreviewAudioAsync owner={request.OwnerId}");
        return _previewPlaybackCoordinator.ScheduleHover(request);
    }

    private Task StartPreviewAudioAsync()
    {
        var request = CreatePreviewRequest();
        if (request == null || _previewPlaybackCoordinator == null)
        {
            TraceCard("StartPreviewAudioAsync skipped: no request or coordinator");
            return Task.CompletedTask;
        }

        TraceCard($"StartPreviewAudioAsync owner={request.OwnerId}");
        return _previewPlaybackCoordinator.StartImmediate(request);
    }

    private CardPreviewRequest? CreatePreviewRequest()
    {
        var previewUrl = GetActiveAudioPreviewUrl();
        if (string.IsNullOrWhiteSpace(previewUrl))
            return null;

        return new CardPreviewRequest(
            _previewOwnerId,
            previewUrl,
            OnPreviewVisualizationFrame,
            OnPreviewPlaybackStateChanged,
            OnPreviewAudioCompleted,
            CanStartHoverPlayback);
    }

    private void OnPreviewPlaybackStateChanged(CardPreviewPlaybackState state)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => OnPreviewPlaybackStateChanged(state));
            return;
        }

        TraceCard(
            $"OnPreviewPlaybackStateChanged pending={state.IsPending} playing={state.IsPlaying} " +
            $"hasViz={state.HasVisualization} session='{state.SessionId ?? "<null>"}'");

        _isPreviewAudioPlaying = state.IsPlaying;
        _previewVisualizationSessionId = state.IsPlaying && state.HasVisualization ? state.SessionId : null;
        _previewVisualizationUrl = state.IsPlaying ? GetActiveAudioPreviewUrl() : null;

        if (state.IsPending)
            QueuePreviewPendingVisualState();
        else
            ClearPreviewPendingVisualState();

        if (state.IsPlaying && state.HasVisualization)
            StartPreviewVisualization(true);
        else
            StopPreviewVisualization(preservePendingState: state.IsPending);
    }

    private void StopPreviewAudio()
    {
        TraceCard("StopPreviewAudio");
        _isPreviewAudioPlaying = false;
        StopPreviewVisualization();

        if (_previewPlaybackCoordinator != null)
            _ = _previewPlaybackCoordinator.CancelOwner(_previewOwnerId);
    }

    private void UnregisterPreviewAudio()
    {
        _isPreviewAudioPlaying = false;
        StopPreviewVisualization();

        if (_previewPlaybackCoordinator != null)
            _ = _previewPlaybackCoordinator.UnregisterOwner(_previewOwnerId);
    }

    private async Task AutoAdvancePreviewAfterAudioEndedAsync()
    {
        var item = Item;
        if (!_isPointerOver || item == null || item.PreviewTracks.Count <= 1)
        {
            StopPreviewAudio();
            return;
        }

        await ChangePreviewTrackAsync(1);
    }
}
