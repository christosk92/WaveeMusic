using System;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.Controls.Cards;

/// <summary>
/// Now-playing highlight surface for <see cref="ContentCard"/>: subscription to
/// <see cref="NowPlayingHighlightService"/>, <c>IsPlaying</c> / <c>IsContextPaused</c>
/// visual state propagation, play-button click, and pending-buffer timeout.
///
/// <para>The subscription model uses the shared <see cref="NowPlayingHighlightService"/>
/// singleton — a single observer of <c>NowPlayingChangedMessage</c> that fans out
/// to every card via a plain C# event. This replaces a previous per-card
/// <c>WeakReferenceMessenger</c> registration that paid bookkeeping cost during
/// HomePage realization (~310 cards). See <c>NowPlayingHighlightService</c> for
/// the rationale.</para>
/// </summary>
public sealed partial class ContentCard
{
    private bool _isPlaybackPending;
    private int _playbackPendingVersion;

    // ── Highlight propagation ────────────────────────────────────────────────

    private void OnHighlightServiceChanged(string? contextUri, string? albumUri, bool playing)
        => ApplyHighlight(contextUri, albumUri, playing);

    private void ApplyHighlight(string? contextUri, string? albumUri, bool playing)
    {
        // Do the cheap string comparison BEFORE scheduling a dispatcher callback.
        // This avoids queuing 20-50 TryEnqueue calls when only 0-1 cards actually match.
        var navUri = NavigationUri; // read once — safe, DependencyProperty reads are thread-safe for strings
        // Match on context OR album URI — so an album card lights up whenever the
        // currently-playing track belongs to that album, not only when playback
        // was launched from the album itself.
        var isMatch = !string.IsNullOrEmpty(navUri)
            && ((!string.IsNullOrEmpty(contextUri)
                 && string.Equals(navUri, contextUri, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(albumUri)
                    && string.Equals(navUri, albumUri, StringComparison.OrdinalIgnoreCase)));

        // Only dispatch if state actually changed
        var wasPlaying = IsPlaying;
        var wasPaused = IsContextPaused;
        var newPlaying = isMatch && playing;
        var newPaused = isMatch && !playing;
        var shouldClearPending = _isPlaybackPending && (!isMatch || playing);
        if (newPlaying == wasPlaying && newPaused == wasPaused && !shouldClearPending) return;

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isPlaybackPending && isMatch && playing)
                _isPointerOver = false;

            IsPlaying = newPlaying;
            IsContextPaused = newPaused;
            if (_isPlaybackPending && (!isMatch || playing))
                SetPlaybackPending(false);
        });
    }

    private void SyncInitialPlaybackState()
    {
        if (_highlightService != null)
        {
            var (contextUri, albumUri, playing) = _highlightService.Current;
            ApplyHighlight(contextUri, albumUri, playing);
            return;
        }

        ApplyHighlightFromPlaybackStateService();
    }

    private void ApplyHighlightFromPlaybackStateService()
    {
        var ps = Ioc.Default.GetService<IPlaybackStateService>();
        if (ps == null) return;
        ApplyHighlight(ps.CurrentContext?.ContextUri, ps.CurrentAlbumId, ps.IsPlaying);
    }

    // ── Play button click + pending-state machine ────────────────────────────

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        PlayRequested?.Invoke(this, EventArgs.Empty);

        var playback = Ioc.Default.GetService<IPlaybackService>();
        if (playback == null) return;
        var playbackState = Ioc.Default.GetService<IPlaybackStateService>();

        try
        {
            var navUri = NavigationUri;
            if (IsPlaying)
            {
                await Task.Run(async () => await playback.PauseAsync());
            }
            else if (IsContextPaused)
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
            else if (!string.IsNullOrEmpty(navUri))
            {
                SetPlaybackPending(true);
                playbackState?.NotifyBuffering(null);
                var result = await Task.Run(async () => await playback.PlayContextAsync(navUri));
                if (!result.IsSuccess)
                {
                    SetPlaybackPending(false);
                    playbackState?.ClearBuffering();
                }
            }
        }
        catch(Exception x)
        {
            SetPlaybackPending(false);
            playbackState?.ClearBuffering();
            Debug.WriteLine(x.ToString());
            // Playback errors surface via IPlaybackService.Errors observable
        }
    }

    private bool IsPlayButtonSource(object? source)
    {
        var current = source as DependencyObject;
        while (current != null)
        {
            if (ReferenceEquals(current, SquarePlayButton)
                || ReferenceEquals(current, CirclePlayButton)
                || ReferenceEquals(current, SquareExternalButton))
                return true;

            current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private void SetPlaybackPending(bool pending)
    {
        if (_isPlaybackPending == pending) return;

        _isPlaybackPending = pending;
        _playbackPendingVersion++;
        if (pending)
        {
            EnsurePlayOverlayRealized();
            StartPendingBeam();
            _ = ClearPlaybackPendingAfterTimeoutAsync(_playbackPendingVersion);
        }
        else
        {
            StopPendingBeam();
        }

        UpdatePlayingState();
    }

    private void ResetPlaybackVisualStateForNewItem()
    {
        _isPointerOver = false;
        _isPlaybackPending = false;
        _playbackPendingVersion++;
        StopPendingBeam();
        if (SquarePlayButton != null)
            SquarePlayButton.Visibility = Visibility.Collapsed;
        if (CirclePlayButton != null)
            CirclePlayButton.Visibility = Visibility.Collapsed;
        if (SquareExternalButton != null)
            SquareExternalButton.Visibility = Visibility.Collapsed;
        IsPlaying = false;
        IsContextPaused = false;
    }

    private void StartPendingBeam()
    {
        if (PendingBeam == null)
            this.FindName("PendingBeam");
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
                Ioc.Default.GetService<IPlaybackStateService>()?.ClearBuffering();
            }
        });
    }

    // ── Visual state composition ─────────────────────────────────────────────

    private void UpdatePlayingState()
    {
        var isPlaying = IsPlaying;
        var isPaused = IsContextPaused;
        var isActiveContext = isPlaying || isPaused;
        var isPending = _isPlaybackPending;
        var showPlaybackChrome = ShowPlaybackOverlay && !IsExternal;

        var showSquarePlaying = showPlaybackChrome && (isPlaying || isPaused) && !isPending && !IsCircularImage;
        var showCirclePlaying = showPlaybackChrome && (isPlaying || isPaused) && !isPending && IsCircularImage;
        var showPlayButton = showPlaybackChrome && (_isPointerOver || isPaused || isPending);

        if (showSquarePlaying && SquarePlayingIndicator == null)
            this.FindName("SquarePlayingIndicator");
        if (showCirclePlaying)
            EnsureCircleRealized();
        if (showPlayButton)
            EnsurePlayOverlayRealized();

        // Null-guard every access — all overlays are x:Load-deferred, so any of them
        // may be null on a card that hasn't yet realized its subtree.
        if (SquarePlayingIndicator != null)
            SquarePlayingIndicator.Visibility = showSquarePlaying
                ? Visibility.Visible
                : Visibility.Collapsed;
        if (SquarePlayingEqualizer != null)
            SquarePlayingEqualizer.IsActive = showSquarePlaying && isPlaying;

        if (CirclePlayingIndicator != null)
            CirclePlayingIndicator.Visibility = showCirclePlaying
                ? Visibility.Visible
                : Visibility.Collapsed;
        if (CirclePlayingEqualizer != null)
            CirclePlayingEqualizer.IsActive = showCirclePlaying && isPlaying;

        if (IsExternal)
        {
            // External cards have no play / pending / paused notion — overlay
            // visibility is purely hover-driven, handled in Card_PointerEntered/Exited.
            // Keep the play button collapsed in case a card flipped from non-external
            // to external while realized.
            if (SquarePlayButton != null) SquarePlayButton.Visibility = Visibility.Collapsed;
            if (CirclePlayButton != null) CirclePlayButton.Visibility = Visibility.Collapsed;
        }
        else if (!ShowPlaybackOverlay)
        {
            if (SquarePlayButton != null) SquarePlayButton.Visibility = Visibility.Collapsed;
            if (CirclePlayButton != null) CirclePlayButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            var playBtn = IsCircularImage ? CirclePlayButton : SquarePlayButton;
            var playAction = IsCircularImage ? CirclePlayAction : SquarePlayAction;
            if (playBtn != null && playAction != null)
            {
                playAction.IsPlaying = isPlaying;
                playAction.IsPending = isPending;

                playBtn.Visibility = (isPending || showPlayButton)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                if (playBtn.Visibility == Visibility.Visible)
                    playBtn.Opacity = 1;
            }
        }

        // Accent color on title when this is the active context
        if (isActiveContext)
            TitleText.Foreground = _themeColorService?.AccentText
                ?? GetThemeBrush("AccentTextFillColorPrimaryBrush");
        else
            TitleText.ClearValue(TextBlock.ForegroundProperty);
    }
}
