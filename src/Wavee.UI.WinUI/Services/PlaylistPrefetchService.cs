using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Messages;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Warms the playlist cache after sign-in by fetching the full track list
/// of every playlist in the user's rootlist via the same code path the
/// sidebar click uses. The first time the user clicks a playlist after
/// sign-in is then a cache hit instead of a network round-trip.
///
/// Drives the "Loading your playlists" phase in <c>SpotifyConnectDialog</c>
/// via <see cref="PlaylistPrefetchStartedMessage"/> and
/// <see cref="PlaylistPrefetchProgressMessage"/>.
///
/// Concurrency-limited (<see cref="MaxConcurrent"/>) so a 100-playlist user
/// doesn't slam Spotify's Pathfinder gateway with simultaneous queries.
/// </summary>
public interface IPlaylistPrefetcher
{
    /// <summary>
    /// Iterates the user's rootlist and pre-fetches every playlist's
    /// content into the cache. Idempotent — concurrent calls are
    /// coalesced (subsequent invocations no-op while one is running).
    /// </summary>
    Task PrefetchAllAsync(CancellationToken ct = default);
}

internal sealed class PlaylistPrefetchService : IPlaylistPrefetcher
{
    private const int MaxConcurrent = 4;

    private readonly ILibraryDataService _libraryDataService;
    private readonly IMessenger _messenger;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    public PlaylistPrefetchService(
        ILibraryDataService libraryDataService,
        IMessenger messenger,
        ILogger<PlaylistPrefetchService>? logger = null)
    {
        _libraryDataService = libraryDataService;
        _messenger = messenger;
        _logger = logger;
    }

    public async Task PrefetchAllAsync(CancellationToken ct = default)
    {
        if (!await _runLock.WaitAsync(0, ct).ConfigureAwait(false))
        {
            _logger?.LogDebug("Playlist prefetch already running — skipping");
            return;
        }

        try
        {
            var playlists = await _libraryDataService.GetUserPlaylistsAsync(ct).ConfigureAwait(false);
            var total = playlists.Count;
            if (total == 0)
            {
                _logger?.LogInformation("Playlist prefetch: rootlist empty, nothing to do");
                _messenger.Send(new PlaylistPrefetchStartedMessage(0));
                _messenger.Send(new PlaylistPrefetchProgressMessage(string.Empty, 0, 0));
                return;
            }

            _logger?.LogInformation("Playlist prefetch: warming {Total} playlist(s) (concurrency={Max})",
                total, MaxConcurrent);
            _messenger.Send(new PlaylistPrefetchStartedMessage(total));

            using var gate = new SemaphoreSlim(MaxConcurrent, MaxConcurrent);
            var done = 0;
            var tasks = new List<Task>(total);

            foreach (var summary in playlists)
            {
                ct.ThrowIfCancellationRequested();
                await gate.WaitAsync(ct).ConfigureAwait(false);
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await _libraryDataService.GetPlaylistAsync(summary.Id, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "Playlist prefetch failed for {Name} ({Id})", summary.Name, summary.Id);
                    }
                    finally
                    {
                        var current = Interlocked.Increment(ref done);
                        _messenger.Send(new PlaylistPrefetchProgressMessage(summary.Name ?? "(unnamed)", current, total));
                        _logger?.LogDebug("Playlist prefetch: {Name} [{Done}/{Total}]", summary.Name, current, total);
                        gate.Release();
                    }
                }, ct));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
            _logger?.LogInformation("Playlist prefetch: complete ({Total} playlists warmed)", total);
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug("Playlist prefetch cancelled");
        }
        finally
        {
            _runLock.Release();
        }
    }
}
