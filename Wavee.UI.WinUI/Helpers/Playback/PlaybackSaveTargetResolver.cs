using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.Helpers.Playback;

internal static class PlaybackSaveTargetResolver
{
    public static string? GetTrackId(IPlaybackStateService? playbackState)
    {
        if (playbackState is null)
            return null;

        if (playbackState.CurrentTrackIsVideo
            && !string.IsNullOrWhiteSpace(playbackState.CurrentOriginalTrackId))
        {
            return playbackState.CurrentOriginalTrackId;
        }

        if (playbackState.CurrentTrackIsVideo)
            return null;

        return playbackState.CurrentTrackId;
    }

    public static string? GetTrackUri(IPlaybackStateService? playbackState)
    {
        var trackId = GetTrackId(playbackState);
        if (string.IsNullOrWhiteSpace(trackId))
            return null;

        if (trackId.Contains(':', StringComparison.Ordinal)
            && !trackId.StartsWith("spotify:track:", StringComparison.Ordinal)
            && !trackId.StartsWith("wavee:local:track:", StringComparison.Ordinal))
        {
            return null;
        }

        return trackId.Contains(':', StringComparison.Ordinal)
            ? trackId
            : $"spotify:track:{trackId}";
    }

    public static async Task<string?> ResolveTrackUriAsync(
        IPlaybackStateService? playbackState,
        IMusicVideoMetadataService? musicVideoMetadata,
        CancellationToken cancellationToken = default)
    {
        if (playbackState is null)
            return null;

        var known = GetTrackUri(playbackState);
        if (!string.IsNullOrWhiteSpace(known))
            return known;

        if (!playbackState.CurrentTrackIsVideo)
            return null;

        var videoUri = NormalizeTrackUri(playbackState.CurrentTrackId);
        if (string.IsNullOrWhiteSpace(videoUri))
            return null;

        if (!videoUri.StartsWith("spotify:track:", StringComparison.Ordinal))
            return null;

        if (musicVideoMetadata is not null)
        {
            if (musicVideoMetadata.TryGetAudioUri(videoUri, out var cachedAudioUri))
                return cachedAudioUri;

            try
            {
                var resolvedAudioUri = await musicVideoMetadata
                    .TryResolveAudioUriViaExtendedMetadataAsync(videoUri, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(resolvedAudioUri))
                    return resolvedAudioUri;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static string? NormalizeTrackUri(string? trackIdOrUri)
    {
        if (string.IsNullOrWhiteSpace(trackIdOrUri))
            return null;

        return trackIdOrUri.Contains(':', StringComparison.Ordinal)
            ? trackIdOrUri
            : $"spotify:track:{trackIdOrUri}";
    }
}
