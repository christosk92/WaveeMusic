using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.WinUI.Animations;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Wavee.UI.WinUI.Controls.Track.Behaviors;

namespace Wavee.UI.WinUI.Controls.Track;

/// <summary>
/// Partial-class extension on <see cref="TrackItem"/> implementing the now-playing
/// indicator pipeline: per-row playback state (<see cref="_isThisTrackPlaying"/>,
/// <see cref="_isThisTrackPaused"/>, <see cref="_isBuffering"/>), pre-dispatch
/// short-circuit on global <c>TrackStateRefreshMessage</c> broadcasts, the
/// equalizer / buffering-ring / play-button overlay state machine for both
/// Compact and Row modes, the optimistic-buffering local timeout fallback, and
/// the pending border-beam glow.
///
/// Implemented as a partial rather than an external Behavior so virtualized rows
/// pay no extra allocation per realize and the overlay state machine can read
/// per-instance fields owned by the hover and image partials without DP-plumbing
/// every transition.
/// </summary>
public sealed partial class TrackItem
{
    #region Playback State

    private void OnPlaybackStateChanged()
    {
        // Cheap pre-check on the calling thread: skip the dispatch when this
        // row's effective playback state can't have flipped. Across the four
        // events that PlaybackStateChanged fans (CurrentTrackId, IsPlaying,
        // IsBuffering, BufferingTrackId), only the previously-active row, the
        // newly-active row, and the buffering row need to update — every
        // other realized TrackItem is a no-op. At 500 visible rows that's a
        // ~1 ms-per-event drop to a handful of µs. The reads below are
        // lock-free statics + plain instance fields, safe on any thread.
        var track = Track;
        if (track == null) return;

        var trackId = track.Id;
        var isThisTrack = trackId == TrackStateBehavior.CurrentTrackId;
        var nowPlaying = isThisTrack && TrackStateBehavior.IsCurrentlyPlaying;
        var nowPaused = isThisTrack && !TrackStateBehavior.IsCurrentlyPlaying;
        var nowBuffering = trackId == TrackStateBehavior.BufferingTrackId
                           && TrackStateBehavior.IsCurrentlyBuffering;

        if (nowPlaying == _isThisTrackPlaying
            && nowPaused == _isThisTrackPaused
            && nowBuffering == _isBuffering)
            return;

        DispatcherQueue?.TryEnqueue(() =>
        {
            RefreshPlaybackState();
            UpdateOverlayState();
        });
    }

    /// <summary>
    /// Refresh playback state from TrackStateBehavior. Can be called externally
    /// by TrackListView for optimized per-row updates.
    /// </summary>
    public void RefreshPlaybackState()
    {
        var track = Track;
        if (track == null)
        {
            _isThisTrackPlaying = false;
            _isThisTrackPaused = false;
            _isBuffering = false;
            CancelLocalBufferingTimeout();
            StopPendingBeam();
            return;
        }

        var wasBuffering = _isBuffering;
        var isThisTrack = track.Id == TrackStateBehavior.CurrentTrackId;
        _isThisTrackPlaying = isThisTrack && TrackStateBehavior.IsCurrentlyPlaying;
        _isThisTrackPaused = isThisTrack && !TrackStateBehavior.IsCurrentlyPlaying;
        _isBuffering = track.Id == TrackStateBehavior.BufferingTrackId
                       && TrackStateBehavior.IsCurrentlyBuffering;

        if (!_isBuffering)
            CancelLocalBufferingTimeout();

        if (wasBuffering && !_isBuffering && isThisTrack)
            ResetHoverVisualState();

        // Title accent color. In Light mode, AccentTextFillColorPrimaryBrush
        // resolves to a saturated bright accent (red on default Windows accent),
        // which overpowers neighboring rows. Use the secondary variant in Light
        // for de-emphasis; Dark keeps primary so the active row still pops.
        var accentResource = ActualTheme == ElementTheme.Light
            ? "AccentTextFillColorSecondaryBrush"
            : "AccentTextFillColorPrimaryBrush";
        var accentBrush = _themeColors?.AccentText ?? (Brush)Application.Current.Resources[accentResource];
        var normalBrush = _themeColors?.TextPrimary ?? (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

        if (Mode == TrackItemDisplayMode.Compact)
        {
            CompactTitle.Foreground = (isThisTrack || _isBuffering) ? accentBrush : normalBrush;
        }
        else
        {
            RowTitle.Foreground = (isThisTrack || _isBuffering) ? accentBrush : normalBrush;
        }
    }

    private void UpdateOverlayState()
    {
        if (Track == null)
        {
            StopPendingBeam();
            return;
        }

        if (Mode == TrackItemDisplayMode.Compact)
            UpdateCompactOverlay();
        else
            UpdateRowOverlay();

        UpdatePendingBeam();
    }

    private void UpdateCompactOverlay()
    {
        if (_isBuffering)
        {
            CompactPlayButton.Opacity = 0;
            CompactPlayButton.Visibility = Visibility.Collapsed;
            CompactPlayButton.IsHitTestVisible = false;

            CompactNowPlaying.Visibility = Visibility.Collapsed;
            SetCompactEqualizer(false, false);
            CompactBufferingRing.IsActive = true;
            CompactBufferingRing.Visibility = Visibility.Visible;
        }
        else if (_isHovered)
        {
            CompactNowPlaying.Visibility = Visibility.Collapsed;
            SetCompactEqualizer(false, false);
            CompactBufferingRing.IsActive = false;
            CompactBufferingRing.Visibility = Visibility.Collapsed;
            if (CompactPlayContent != null)
                CompactPlayContent.IsPlaying = _isThisTrackPlaying;

            CompactPlayButton.Visibility = Visibility.Visible;
            CompactPlayButton.IsHitTestVisible = true;
            if (CompactPlayButton.Opacity < 0.99)
            {
                AnimationBuilder.Create()
                    .Opacity(to: 1, duration: TimeSpan.FromMilliseconds(100))
                    .Start(CompactPlayButton);
            }
        }
        else
        {
            CompactPlayButton.IsHitTestVisible = false;
            if (CompactPlayButton.Visibility == Visibility.Visible && CompactPlayButton.Opacity > 0.01)
            {
                AnimationBuilder.Create()
                    .Opacity(to: 0, duration: TimeSpan.FromMilliseconds(85))
                    .Start(CompactPlayButton);
                _ = CollapseCompactPlayButtonAfterDelayAsync(90);
            }
            else
            {
                CompactPlayButton.Opacity = 0;
                CompactPlayButton.Visibility = Visibility.Collapsed;
            }

            if (_isThisTrackPlaying)
            {
                CompactBufferingRing.IsActive = false;
                CompactBufferingRing.Visibility = Visibility.Collapsed;
                CompactNowPlaying.Visibility = Visibility.Visible;
                CompactNowPlaying.Opacity = 1.0;
                SetCompactEqualizer(true, true);
            }
            else if (_isThisTrackPaused)
            {
                CompactBufferingRing.IsActive = false;
                CompactBufferingRing.Visibility = Visibility.Collapsed;
                CompactNowPlaying.Visibility = Visibility.Visible;
                CompactNowPlaying.Opacity = 0.7;
                SetCompactEqualizer(true, false);
            }
            else
            {
                SetCompactEqualizer(false, false);
                CompactBufferingRing.IsActive = false;
                CompactBufferingRing.Visibility = Visibility.Collapsed;
                CompactNowPlaying.Visibility = Visibility.Collapsed;
            }
        }
    }

    private async Task CollapseCompactPlayButtonAfterDelayAsync(int delayMs)
    {
        await Task.Delay(delayMs);
        if (!_isHovered && !_isBuffering && CompactPlayButton.Opacity <= 0.05)
        {
            CompactPlayButton.Opacity = 0;
            CompactPlayButton.Visibility = Visibility.Collapsed;
            CompactPlayButton.IsHitTestVisible = false;
        }
    }

    private void UpdateRowOverlay()
    {
        if (_isBuffering)
        {
            RowIndexText.Visibility = Visibility.Collapsed;
            RowPlayButton.Visibility = Visibility.Collapsed;
            SetRowEqualizer(false, false);
            RowBufferingRing.IsActive = true;
            RowBufferingRing.Visibility = Visibility.Visible;
        }
        else if (_isHovered)
        {
            RowIndexText.Visibility = Visibility.Collapsed;
            SetRowEqualizer(false, false);
            RowBufferingRing.IsActive = false;
            RowBufferingRing.Visibility = Visibility.Collapsed;
            RowPlayButton.Visibility = Visibility.Visible;
            if (RowPlayContent != null)
                RowPlayContent.IsPlaying = _isThisTrackPlaying;
        }
        else if (_isThisTrackPlaying)
        {
            RowIndexText.Visibility = Visibility.Collapsed;
            RowPlayButton.Visibility = Visibility.Collapsed;
            RowBufferingRing.IsActive = false;
            RowBufferingRing.Visibility = Visibility.Collapsed;
            SetRowEqualizer(true, true);
        }
        else if (_isThisTrackPaused)
        {
            RowIndexText.Visibility = Visibility.Collapsed;
            RowPlayButton.Visibility = Visibility.Collapsed;
            RowBufferingRing.IsActive = false;
            RowBufferingRing.Visibility = Visibility.Collapsed;
            SetRowEqualizer(true, false);
        }
        else
        {
            RowIndexText.Visibility = Visibility.Visible;
            SetRowEqualizer(false, false);
            RowPlayButton.Visibility = Visibility.Collapsed;
            RowBufferingRing.IsActive = false;
            RowBufferingRing.Visibility = Visibility.Collapsed;
        }
    }

    private void SetCompactEqualizer(bool visible, bool active)
    {
        if (visible && CompactNowPlayingEqualizer == null)
            FindName("CompactNowPlayingEqualizer");
        if (CompactNowPlayingEqualizer == null) return;

        CompactNowPlayingEqualizer.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        CompactNowPlayingEqualizer.IsActive = visible && active;
    }

    private void SetRowEqualizer(bool visible, bool active)
    {
        if (visible && RowNowPlayingEqualizer == null)
            FindName("RowNowPlayingEqualizer");
        if (RowNowPlayingEqualizer == null) return;

        RowNowPlayingEqualizer.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        RowNowPlayingEqualizer.IsActive = visible && active;
    }

    private void UpdatePendingBeam()
    {
        if (_isBuffering && !IsLoading)
            StartPendingBeam();
        else
            StopPendingBeam();
    }

    private void StartPendingBeam()
    {
        if (PlaybackPendingBeam == null)
            this.FindName("PlaybackPendingBeam");
        PlaybackPendingBeam?.Start();
    }

    private void StopPendingBeam()
    {
        PlaybackPendingBeam?.Stop();
    }

    private void StartLocalBufferingTimeout(string trackId)
    {
        CancelLocalBufferingTimeout();

        var cts = new CancellationTokenSource();
        _localBufferingTimeoutCts = cts;
        _localBufferingTimeoutTrackId = trackId;
        _ = ClearLocalBufferingAfterTimeoutAsync(trackId, cts.Token);
    }

    private async Task ClearLocalBufferingAfterTimeoutAsync(string trackId, CancellationToken ct)
    {
        try
        {
            await Task.Delay(OptimisticPlayPendingTimeoutMs, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        DispatcherQueue?.TryEnqueue(() =>
        {
            if (ct.IsCancellationRequested
                || !_isBuffering
                || !string.Equals(_localBufferingTimeoutTrackId, trackId, StringComparison.Ordinal)
                || !string.Equals(Track?.Id, trackId, StringComparison.Ordinal))
            {
                return;
            }

            if (TrackStateBehavior.IsCurrentlyBuffering
                && string.Equals(TrackStateBehavior.BufferingTrackId, trackId, StringComparison.Ordinal))
            {
                return;
            }

            CancelLocalBufferingTimeout();
            _isBuffering = false;
            UpdateOverlayState();
        });
    }

    private void CancelLocalBufferingTimeout()
    {
        var cts = _localBufferingTimeoutCts;
        _localBufferingTimeoutCts = null;
        _localBufferingTimeoutTrackId = null;

        if (cts is null)
            return;

        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }
        cts.Dispose();
    }

    #endregion
}
