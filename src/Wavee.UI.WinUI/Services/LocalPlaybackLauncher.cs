using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Data.Contexts;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Central launch path for local-file playback. The PlaybackOrchestrator is
/// attached to ConnectCommandExecutor at runtime, not registered directly as
/// IPlaybackEngine, so local UI surfaces should enter through the executor.
/// </summary>
internal static class LocalPlaybackLauncher
{
    public const string LibraryContextUri = "wavee:local:library";

    public static void PlayOne(
        string trackUri,
        string contextUri = LibraryContextUri,
        string? contextName = "Local files")
        => PlayQueue(new[] { trackUri }, 0, contextUri, contextName);

    public static void PlayQueue(
        IEnumerable<string> trackUris,
        int startIndex,
        string contextUri = LibraryContextUri,
        string? contextName = "Local files")
    {
        var uris = trackUris
            .Where(uri => !string.IsNullOrWhiteSpace(uri))
            .ToList();
        if (uris.Count == 0) return;

        var boundedStartIndex = Math.Clamp(startIndex, 0, uris.Count - 1);
        var executor = Ioc.Default.GetService<IPlaybackCommandExecutor>();
        var logger = Ioc.Default.GetService<ILoggerFactory>()?.CreateLogger("LocalPlaybackLauncher");

        if (executor is null)
        {
            logger?.LogWarning("Local playback dropped: IPlaybackCommandExecutor is not registered");
            return;
        }

        var context = new PlaybackContextInfo
        {
            ContextUri = contextUri,
            Type = PlaybackContextType.LikedSongs,
            Name = contextName,
        };

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await executor
                    .PlayTracksAsync(uris, boundedStartIndex, context, richTracks: null, CancellationToken.None)
                    .ConfigureAwait(false);

                if (!result.IsSuccess)
                {
                    logger?.LogWarning(
                        "Local playback failed for {TrackUri}: {Error}",
                        uris[boundedStartIndex],
                        result.ErrorMessage ?? result.ErrorKind?.ToString() ?? "unknown error");
                }
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Local playback failed for {TrackUri}", uris[boundedStartIndex]);
            }
        });
    }
}
