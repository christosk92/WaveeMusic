using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Wavee.UI.Contracts;
using Wavee.UI.Helpers;
using Wavee.UI.Models;
using Wavee.UI.Services;
using Wavee.UI.WinUI.Controls.Track.Behaviors;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Helpers.Playback;

namespace Wavee.UI.WinUI.Controls.Track;

/// <summary>
/// Partial-class extension on <see cref="TrackItem"/> covering tap-to-play,
/// double-tap-to-play, heart toggling, the play button, optimistic-buffering
/// dispatch, and the per-artist / album hyperlink builders used by Row mode.
///
/// Split out from <c>TrackItem.xaml.cs</c> purely for source-layout — every
/// method here still lives on the same partial class, so virtualized-row
/// realize/recycle pays zero extra cost.
/// </summary>
public sealed partial class TrackItem
{
    #region Click / Play

    private void OnPlayButtonClick(object sender, RoutedEventArgs e)
    {
        var track = Track;
        if (track == null) return;

        if (track.Id == TrackStateBehavior.CurrentTrackId)
        {
            _playbackStateService?.PlayPause();
        }
        else
        {
            ExecutePlayCommandWithPending(track);
        }
    }

    // The hover "…" button — opens the same context menu a right-click / hold
    // would, anchored just below the button. The pointer path for users who
    // don't right-click (trackpad, touch without long-press).
    private void OnRowMoreButtonClick(object sender, RoutedEventArgs e)
    {
        if (Track is null || RowMoreButton is null) return;
        var origin = RowMoreButton
            .TransformToVisual(this)
            .TransformPoint(new Windows.Foundation.Point(0, RowMoreButton.ActualHeight));
        ShowContextMenu(origin);
    }

    private void OnHeartClicked()
        => _ = OnHeartClickedAsync();

    private async Task OnHeartClickedAsync()
    {
        var track = Track;
        if (track == null)
        {
            _logger?.LogWarning("HeartButton clicked but no track is bound");
            return;
        }
        if (_likeService == null)
        {
            _logger?.LogWarning("HeartButton clicked but ITrackLikeService is not available");
            return;
        }

        var uri = IsCurrentPlaybackVideoTrack(track)
            ? await PlaybackSaveTargetResolver
                .ResolveTrackUriAsync(_playbackStateService, _musicVideoMetadata)
                .ConfigureAwait(true)
            : GetImmediateSaveTargetUri(track);
        if (string.IsNullOrEmpty(uri))
            return;

        var wasLiked = _likeService.IsSaved(SavedItemType.Track, uri);
        _logger?.LogInformation("HeartButton: ToggleSave uri={Uri}, currentlyLiked={IsLiked}", uri, wasLiked);

        // Just tell the service - it updates the cache, fires SaveStateChanged,
        // and ALL hearts across the app react via OnSaveStateChanged.
        _likeService.ToggleSave(SavedItemType.Track, uri, wasLiked);
    }

    private string? GetImmediateSaveTargetUri(ITrackItem track)
    {
        if (IsCurrentPlaybackVideoTrack(track))
            return PlaybackSaveTargetResolver.GetTrackUri(_playbackStateService);

        if (IsSpotifyEpisodeUri(track.Uri))
            return null;

        if (!string.IsNullOrEmpty(track.Uri))
            return track.Uri;

        return string.IsNullOrEmpty(track.Id)
            ? null
            : SpotifyUriHelper.ToUri(SpotifyEntityKind.Track, track.Id);
    }

    private static bool IsSpotifyEpisodeUri(string? uri)
        => SpotifyUriHelper.IsKind(uri, SpotifyEntityKind.Episode);

    private bool IsCurrentPlaybackVideoTrack(ITrackItem track)
    {
        if (_playbackStateService?.CurrentTrackIsVideo != true)
            return false;

        var currentTrackId = _playbackStateService.CurrentTrackId;
        if (string.IsNullOrEmpty(currentTrackId))
            return false;

        var currentTrackUri = currentTrackId.Contains(':', StringComparison.Ordinal)
            ? currentTrackId
            : SpotifyUriHelper.ToUri(SpotifyEntityKind.Track, currentTrackId);

        return string.Equals(track.Id, currentTrackId, StringComparison.Ordinal)
               || string.Equals(track.Uri, currentTrackUri, StringComparison.Ordinal);
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        // Don't handle taps on interactive elements (buttons, links, checkbox)
        if (IsInteractiveElement(e.OriginalSource as DependencyObject))
            return;

        // In selection mode a plain tap toggles this row's selection and never
        // plays. e.Handled stops the tap reaching the ItemContainer, so its
        // native Extended-mode select-replace doesn't wipe the multi-selection.
        if (IsSelectionMode)
        {
            SelectionToggleRequested?.Invoke(this, !IsSelected);
            e.Handled = true;
            return;
        }

        if (IsCtrlOrShiftDown())
            return;

        var settings = TryGetSettings();
        if (settings?.Settings.TrackClickBehavior != "SingleTap") return;

        HandleTrackPlay();
        e.Handled = true;
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // Don't handle double-taps on interactive elements
        if (IsInteractiveElement(e.OriginalSource as DependencyObject))
            return;

        // Selection mode: a tap already toggled selection via OnTapped — the
        // double-tap must not fall through to play.
        if (IsSelectionMode)
        {
            e.Handled = true;
            return;
        }

        if (IsCtrlOrShiftDown())
            return;

        var settings = TryGetSettings();
        if (settings?.Settings.TrackClickBehavior == "SingleTap") return;

        HandleTrackPlay();
        e.Handled = true;
    }

    private void HandleTrackPlay()
    {
        var track = Track;
        if (track == null) return;
        ExecutePlayCommandWithPending(track);
    }

    private void ExecutePlayCommandWithPending(ITrackItem track)
    {
        if (PlayCommand?.CanExecute(track) != true) return;

        if (track.Id != TrackStateBehavior.CurrentTrackId)
        {
            _playbackStateService?.NotifyBuffering(track.Id);
            ResetHoverVisualState();
            _isThisTrackPlaying = false;
            _isThisTrackPaused = false;
            _isBuffering = true;
            StartLocalBufferingTimeout(track.Id);
            UpdateOverlayState();
        }

        PlayCommand.Execute(track);
    }

    private static bool IsCtrlOrShiftDown()
    {
        try
        {
            var ctrlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
            var shiftState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
            const Windows.UI.Core.CoreVirtualKeyStates down = Windows.UI.Core.CoreVirtualKeyStates.Down;
            return (ctrlState & down) == down || (shiftState & down) == down;
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region Navigation Links (Row mode)

    private void OnArtistLinkClick(object sender, RoutedEventArgs e)
    {
        if (sender is HyperlinkButton link && link.Tag is string artistId && !string.IsNullOrEmpty(artistId))
        {
            // Pull the visible artist name from the inner TextBlock so navigation
            // labels match what was clicked rather than the row's flattened
            // ArtistName string (which may comma-join multiple names).
            var displayName = (link.Content as TextBlock)?.Text
                ?? link.Content as string
                ?? "";
            Wavee.UI.WinUI.Diagnostics.NavigationDiagnostics.RecordClickIntent("TrackItem.Artist");
            ArtistClicked?.Invoke(this, artistId);
            NavigationHelpers.OpenArtist(artistId, displayName);
        }
    }

    /// <summary>
    /// Rebuild the per-artist hyperlink stack inside <see cref="RowArtistsHost"/>.
    /// Renders one HyperlinkButton per artist with comma separators between them
    /// when the track carries a rich <see cref="ITrackItem.Artists"/> list; falls
    /// back to a single link from <c>(ArtistName, ArtistId)</c> for legacy DTOs
    /// (LikedSongDto, PlaylistTrackDto, ...) that haven't been upgraded yet.
    /// </summary>
    private void RebuildArtistsSubline(ITrackItem track)
    {
        var signature = BuildArtistsSignature(track);
        if (string.Equals(signature, _rowArtistsSignature, StringComparison.Ordinal))
            return;

        _rowArtistsSignature = signature;
        RowArtistsHost.Children.Clear();

        var captionStyle = (Microsoft.UI.Xaml.Style)Application.Current.Resources["CaptionTextBlockStyle"];
        var subduedBrush = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

        var artists = track.Artists;
        if (artists == null || artists.Count == 0)
        {
            // Single-link fallback. Empty ArtistId is fine — OnArtistLinkClick
            // checks for empty before navigating, matching the legacy behaviour.
            var name = track.ArtistName ?? "";
            if (string.IsNullOrEmpty(name)) return;
            RowArtistsHost.Children.Add(BuildArtistLink(name, track.ArtistId ?? "", captionStyle, subduedBrush));
            return;
        }

        for (var i = 0; i < artists.Count; i++)
        {
            if (i > 0)
            {
                RowArtistsHost.Children.Add(new TextBlock
                {
                    Text = ", ",
                    Style = captionStyle,
                    Foreground = subduedBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            var a = artists[i];
            RowArtistsHost.Children.Add(BuildArtistLink(a.Name, a.Uri, captionStyle, subduedBrush));
        }
    }

    private static string BuildArtistsSignature(ITrackItem track)
    {
        var artists = track.Artists;
        if (artists == null || artists.Count == 0)
            return $"{track.ArtistName}|{track.ArtistId}";

        var sb = new StringBuilder(artists.Count * 32);
        for (var i = 0; i < artists.Count; i++)
        {
            if (i > 0) sb.Append('|');
            sb.Append(artists[i].Name).Append('@').Append(artists[i].Uri);
        }
        return sb.ToString();
    }

    private HyperlinkButton BuildArtistLink(
        string name,
        string artistTag,
        Microsoft.UI.Xaml.Style captionStyle,
        Brush subduedBrush)
    {
        var link = new HyperlinkButton
        {
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Tag = artistTag,
            Content = new TextBlock
            {
                Text = name,
                Style = captionStyle,
                Foreground = subduedBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
            }
        };
        link.Click += OnArtistLinkClick;
        return link;
    }

    private void OnAlbumLinkClick(object sender, RoutedEventArgs e)
    {
        if (sender is HyperlinkButton link && link.Tag is string albumId && !string.IsNullOrEmpty(albumId))
        {
            Wavee.UI.WinUI.Diagnostics.NavigationDiagnostics.RecordClickIntent("TrackItem.Album");
            AlbumClicked?.Invoke(this, albumId);
            var param = new Data.Parameters.ContentNavigationParameter
            {
                Uri = albumId,
                Title = link.Content as string ?? "",
                ImageUrl = Track?.ImageUrl
            };
            NavigationHelpers.OpenAlbum(param, param.Title);
        }
    }

    #endregion

    #region Helpers (shared by tap-to-play and other entry points)

    /// <summary>
    /// Checks if a visual tree element is an interactive control (button, link)
    /// that should not trigger row-level tap-to-play.
    /// </summary>
    private static bool IsInteractiveElement(DependencyObject? element)
    {
        while (element != null)
        {
            // ButtonBase covers Button, HyperlinkButton and the row's
            // CheckBox (a ToggleButton) — all should swallow row tap-to-play
            // / tap-to-toggle so they handle their own input.
            if (element is ButtonBase) return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private static ISettingsService? TryGetSettings()
    {
        if (_cachedSettingsService != null) return _cachedSettingsService;
        try { return _cachedSettingsService = Ioc.Default.GetService<ISettingsService>(); }
        catch (Exception ex) { Debug.WriteLine($"Failed to resolve ISettingsService: {ex.Message}"); return null; }
    }

    #endregion
}
