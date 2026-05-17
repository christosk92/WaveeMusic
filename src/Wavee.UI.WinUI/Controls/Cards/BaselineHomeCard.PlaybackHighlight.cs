using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.Controls.Cards;

/// <summary>
/// Now-playing highlight + context-play button surface for
/// <see cref="BaselineHomeCard"/>: <see cref="NowPlayingHighlightService"/>
/// subscription that resolves whether the card's URI matches the active
/// playback context (or the currently-playing track's album), the
/// "context playing" / "context paused" / "pending" visual states, the
/// large bottom-right context-play button, and the pending-beam border
/// that pulses while a play request is in flight.
///
/// <para>Uses the singleton <see cref="NowPlayingHighlightService"/> rather
/// than a per-card <c>WeakReferenceMessenger</c> registration — the
/// service performs a single subscription and fans out via a plain C#
/// event, so HomePage realization doesn't pay messenger bookkeeping cost
/// per card.</para>
/// </summary>
public sealed partial class BaselineHomeCard
{
    private bool _isContextPlaying;
    private bool _isContextPaused;
    private bool _isPlaybackPending;
    private int _playbackPendingVersion;

    // ── Highlight propagation ────────────────────────────────────────────────

    private void OnHighlightServiceChanged(string? contextUri, string? albumUri, bool playing)
        => ApplyHighlight(contextUri, albumUri, playing);

    private void ApplyHighlight(string? contextUri, string? albumUri, bool playing)
    {
        var itemUri = Item?.Uri;
        // Match on either the playback context (e.g. user launched this playlist)
        // OR the currently-playing track's album URI (catches the case where the
        // track is from this album but was launched from a different context like
        // a playlist, search result, or radio).
        var isMatch = !string.IsNullOrEmpty(itemUri)
            && ((!string.IsNullOrEmpty(contextUri)
                 && string.Equals(itemUri, contextUri, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(albumUri)
                    && string.Equals(itemUri, albumUri, StringComparison.OrdinalIgnoreCase)));

        var newPlaying = isMatch && playing;
        var newPaused = isMatch && !playing;
        var shouldClearPending = _isPlaybackPending && (!isMatch || playing);
        if (newPlaying == _isContextPlaying && newPaused == _isContextPaused && !shouldClearPending)
            return;

        DispatcherQueue.TryEnqueue(() =>
        {
            _isContextPlaying = newPlaying;
            _isContextPaused = newPaused;
            if (_isPlaybackPending && (!isMatch || playing))
                SetPlaybackPending(false);
            UpdatePlayingState();
        });
    }

    private void UpdatePlayingState()
    {
        var isActiveContext = _isContextPlaying || _isContextPaused;
        var isPending = _isPlaybackPending;
        var showPlayingIndicator = isActiveContext && !isPending;
        var showContextPlayButton = _isPointerOver || _isContextPaused || isPending || isActiveContext;

        if (showPlayingIndicator && PlayingIndicator == null)
            FindName(nameof(PlayingIndicator));

        // Defer ContextPlayButton realization to avoid layout disruption during
        // pointer-enter processing. FindName on x:Load elements can trigger a layout
        // pass that produces a phantom PointerExited, cancelling the preview start.
        if (showContextPlayButton && ContextPlayButton == null)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isPointerOver || _isContextPlaying || _isContextPaused || _isPlaybackPending)
                {
                    EnsureContextPlayButtonRealized();
                    UpdatePlayingState();
                }
            });
            return;
        }

        if (PlayingIndicator != null)
            PlayingIndicator.Visibility = showPlayingIndicator ? Visibility.Visible : Visibility.Collapsed;
        if (PlayingEqualizer != null)
            PlayingEqualizer.IsActive = showPlayingIndicator && _isContextPlaying;

        if (ContextPlayButton != null)
        {
            if (ContextPlayButtonContent != null)
            {
                ContextPlayButtonContent.IsPlaying = _isContextPlaying;
                ContextPlayButtonContent.IsPending = isPending;
            }

            ContextPlayButton.Visibility = showContextPlayButton ? Visibility.Visible : Visibility.Collapsed;
            if (ContextPlayButton.Visibility == Visibility.Visible)
                ContextPlayButton.Opacity = 1;
        }
    }

    // ── Pending state machine ────────────────────────────────────────────────

    private void SetPlaybackPending(bool pending)
    {
        if (_isPlaybackPending == pending) return;

        _isPlaybackPending = pending;
        _playbackPendingVersion++;
        if (pending)
        {
            EnsureContextPlayButtonRealized();
            StartPendingBeam();
            _ = ClearPlaybackPendingAfterTimeoutAsync(_playbackPendingVersion);
        }
        else
        {
            StopPendingBeam();
        }

        UpdatePlayingState();
    }

    private void StartPendingBeam()
    {
        if (PendingBeam == null)
            FindName(nameof(PendingBeam));
        PendingBeam?.Start();
    }

    private void StopPendingBeam()
    {
        PendingBeam?.Stop();
    }

    private async Task ClearPlaybackPendingAfterTimeoutAsync(int version)
    {
        await Task.Delay(TimeSpan.FromSeconds(8));
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isPlaybackPending && _playbackPendingVersion == version)
            {
                SetPlaybackPending(false);
                _playbackStateService?.ClearBuffering();
            }
        });
    }

    // ── Context play button ──────────────────────────────────────────────────

    private async void ContextPlayButton_Click(object sender, RoutedEventArgs e)
    {
        var playback = _playbackService;
        var playbackState = _playbackStateService;
        if (playback == null) return;

        try
        {
            if (_isContextPlaying)
            {
                await Task.Run(async () => await playback.PauseAsync());
            }
            else if (_isContextPaused)
            {
                SetPlaybackPending(true);
                playbackState?.NotifyBuffering(null);
                var result = await Task.Run(async () => await playback.ResumeAsync());
                if (!result.IsSuccess)
                {
                    SetPlaybackPending(false);
                    playbackState?.ClearBuffering();
                }
            }
            else
            {
                var item = Item;
                if (item == null || string.IsNullOrEmpty(item.Uri)) return;

                StopPreviewAudio();

                SuppressLocalHomeVideoAutoNavigation(item.Uri);
                SetPlaybackPending(true);
                playbackState?.NotifyBuffering(null);
                var result = await Task.Run(async () => await playback.PlayContextAsync(item.Uri));
                if (!result.IsSuccess)
                {
                    SetPlaybackPending(false);
                    playbackState?.ClearBuffering();
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetPlaybackPending(false);
            playbackState?.ClearBuffering();
            Debug.WriteLine($"[BaselineHomeCard] Context play failed: {ex.Message}");
        }
    }

    private static void SuppressLocalHomeVideoAutoNavigation(string uri)
    {
        if (!uri.StartsWith("wavee:local:", StringComparison.Ordinal))
            return;

        VideoAutoNavigationSuppressor.SuppressNextLocalVideoNavigation(uri);
    }
}
