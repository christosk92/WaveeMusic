using System;
using Wavee.UI.Contracts;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Helpers.Playback;

namespace Wavee.UI.WinUI.Helpers.Navigation;

internal static class PodcastPlaybackNavigation
{
    public static bool TryOpenCurrentEpisode(
        IPlaybackStateService? playbackState,
        string? episodeTitle = null,
        string? episodeImageUrl = null,
        string? showTitle = null,
        string? showImageUrl = null,
        bool openInNewTab = false)
    {
        var episodeUri = PlaybackSaveTargetResolver.GetEpisodeUri(playbackState);
        if (string.IsNullOrWhiteSpace(episodeUri))
            return false;

        var showUri = ResolveShowUri(playbackState);
        var showContext = playbackState?.CurrentContext;
        if (showContext?.Type == PlaybackContextType.Show &&
            string.Equals(showContext.ContextUri, showUri, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(showTitle))
                showTitle = showContext.Name;
            if (string.IsNullOrWhiteSpace(showImageUrl))
                showImageUrl = showContext.ImageUrl;
        }

        NavigationHelpers.OpenEpisodePage(
            episodeUri,
            episodeTitle,
            episodeImageUrl,
            showUri,
            showTitle,
            showImageUrl,
            openInNewTab);
        return true;
    }

    private static string? ResolveShowUri(IPlaybackStateService? playbackState)
    {
        if (playbackState is null)
            return null;

        if (playbackState.CurrentAlbumId?.StartsWith("spotify:show:", StringComparison.Ordinal) == true)
            return playbackState.CurrentAlbumId;

        var context = playbackState.CurrentContext;
        return context is { Type: PlaybackContextType.Show } &&
               context.ContextUri.StartsWith("spotify:show:", StringComparison.Ordinal)
            ? context.ContextUri
            : null;
    }
}
