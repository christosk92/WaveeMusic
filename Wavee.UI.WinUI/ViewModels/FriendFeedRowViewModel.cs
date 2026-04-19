using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wavee.Core.Http.Presence;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers.Navigation;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// Row view-model for a single friend-activity entry.
/// Static fields (name, track, avatar, click target) are computed once at
/// construction time; time-dependent fields (<see cref="IsCurrentlyListening"/>
/// and <see cref="TrailingText"/>) are refreshed by the service on a timer so
/// rows correctly transition from "playing now" to "X min" as time passes.
/// </summary>
public sealed partial class FriendFeedRowViewModel : ObservableObject
{
    // Matches Spotify's own Friend Activity panel: equalizer/presence-dot
    // disappears within a few minutes of the last timestamp push.
    private const int ActiveWindowMinutes = 2;

    public string UserUri { get; }
    public string DisplayUsername { get; }
    public string? AvatarUrl { get; }
    public string AvatarFallbackInitial { get; }

    // Track line is split into separately-navigable parts.
    public string TrackName { get; }
    public string? TrackUri { get; }
    public string? ArtistName { get; }
    public string? ArtistUri { get; }
    public bool HasArtist => !string.IsNullOrEmpty(ArtistUri);

    // Third-line context (typically an album or playlist).
    public string ContextName { get; }
    public string? ContextUri { get; }
    public bool HasContext { get; }

    // Album used when a track click should fall back to "open album".
    public string? AlbumUri { get; }
    public string? AlbumName { get; }

    public DateTimeOffset Timestamp { get; }

    [ObservableProperty] private bool _isCurrentlyListening;
    [ObservableProperty] private string? _trailingText;

    public FriendFeedRowViewModel(FriendFeedEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(entry.Timestamp);

        UserUri = entry.User?.Uri ?? string.Empty;
        DisplayUsername = !string.IsNullOrWhiteSpace(entry.User?.Name)
            ? entry.User!.Name!
            : "Unknown";

        AvatarUrl = entry.User?.ImageUrl;
        AvatarFallbackInitial = !string.IsNullOrWhiteSpace(DisplayUsername)
            ? DisplayUsername[..1].ToUpperInvariant()
            : "?";

        TrackName = entry.Track?.Name ?? string.Empty;
        TrackUri = entry.Track?.Uri;
        ArtistName = entry.Track?.Artist?.Name;
        ArtistUri = entry.Track?.Artist?.Uri;
        AlbumName = entry.Track?.Album?.Name;
        AlbumUri = entry.Track?.Album?.Uri;

        ContextName = entry.Track?.Context?.Name
                       ?? entry.Track?.Album?.Name
                       ?? string.Empty;
        ContextUri = entry.Track?.Context?.Uri
                      ?? entry.Track?.Album?.Uri;
        HasContext = !string.IsNullOrWhiteSpace(ContextName);

        _trackImageUrl = entry.Track?.ImageUrl;

        Refresh();
    }

    private readonly string? _trackImageUrl;

    public bool HasClickableTrack => !string.IsNullOrEmpty(AlbumUri) || !string.IsNullOrEmpty(ContextUri);
    public bool HasClickableContext => !string.IsNullOrEmpty(ContextUri) || !string.IsNullOrEmpty(AlbumUri);

    [RelayCommand]
    private void NavigateToArtist()
    {
        if (string.IsNullOrEmpty(ArtistUri)) return;
        var param = new ContentNavigationParameter
        {
            Uri = ArtistUri,
            Title = ArtistName,
            ImageUrl = _trackImageUrl
        };
        NavigationHelpers.OpenArtist(param, ArtistName ?? "Artist", NavigationHelpers.IsCtrlPressed());
    }

    [RelayCommand]
    private void NavigateToTrack()
    {
        // No dedicated track page — open the album as the best approximation.
        var targetUri = AlbumUri ?? ContextUri;
        var targetName = AlbumName ?? ContextName;
        if (string.IsNullOrEmpty(targetUri)) return;

        var param = new ContentNavigationParameter
        {
            Uri = targetUri,
            Title = targetName,
            ImageUrl = _trackImageUrl
        };

        var openInNewTab = NavigationHelpers.IsCtrlPressed();
        if (targetUri.StartsWith("spotify:album:", StringComparison.Ordinal))
            NavigationHelpers.OpenAlbum(param, targetName ?? "Album", openInNewTab);
        else if (targetUri.StartsWith("spotify:playlist:", StringComparison.Ordinal))
            NavigationHelpers.OpenPlaylist(param, targetName ?? "Playlist", openInNewTab);
    }

    [RelayCommand]
    private void NavigateToContext()
    {
        var targetUri = ContextUri ?? AlbumUri;
        var targetName = !string.IsNullOrEmpty(ContextName) ? ContextName : AlbumName;
        if (string.IsNullOrEmpty(targetUri)) return;

        var param = new ContentNavigationParameter
        {
            Uri = targetUri,
            Title = targetName,
            ImageUrl = _trackImageUrl
        };

        var openInNewTab = NavigationHelpers.IsCtrlPressed();
        if (targetUri.StartsWith("spotify:album:", StringComparison.Ordinal))
            NavigationHelpers.OpenAlbum(param, targetName ?? "Album", openInNewTab);
        else if (targetUri.StartsWith("spotify:playlist:", StringComparison.Ordinal))
            NavigationHelpers.OpenPlaylist(param, targetName ?? "Playlist", openInNewTab);
        else if (targetUri.StartsWith("spotify:artist:", StringComparison.Ordinal))
            NavigationHelpers.OpenArtist(param, targetName ?? "Artist", openInNewTab);
    }

    /// <summary>
    /// Recomputes the time-dependent fields (<see cref="IsCurrentlyListening"/>
    /// and <see cref="TrailingText"/>) against the current clock. Called by the
    /// service on a periodic timer so rows transition from "now" → "X min" →
    /// "X hr" without needing a dealer push.
    /// </summary>
    public void Refresh()
    {
        var age = DateTimeOffset.UtcNow - Timestamp;
        var live = age.TotalMinutes <= ActiveWindowMinutes && age >= TimeSpan.Zero;

        IsCurrentlyListening = live;
        TrailingText = live ? null : FormatRelative(age);
    }

    private static string FormatRelative(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1)) return "now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours} hr";
        return $"{(int)age.TotalDays} d";
    }
}
