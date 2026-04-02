using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Wavee.Core.Library.Spotify;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Messages;
using Wavee.UI.WinUI.Data.Models;

namespace Wavee.UI.WinUI.Data.Contexts;

/// <summary>
/// Single owner of the library sync lifecycle.
/// Reacts to AuthStatusChangedMessage — triggers sync after any successful auth.
/// Concurrency-safe via SemaphoreSlim. Wires Dealer changes once.
/// </summary>
public sealed class LibrarySyncOrchestrator : IDisposable
{
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly IMessenger _messenger;
    private readonly ILogger? _logger;
    private IDisposable? _dealerSubscription;
    private bool _dealerWired;

    public LibrarySyncOrchestrator(IMessenger messenger, ILogger<LibrarySyncOrchestrator>? logger = null)
    {
        _messenger = messenger;
        _logger = logger;

        // React to auth status — trigger sync when Authenticated
        messenger.Register<AuthStatusChangedMessage>(this, (_, msg) =>
        {
            if (msg.Value == AuthStatus.Authenticated)
            {
                _logger?.LogInformation("Auth status → Authenticated, triggering library sync");
                _ = RunSyncAsync();
            }
        });

        _logger?.LogDebug("LibrarySyncOrchestrator registered for AuthStatusChangedMessage");
    }

    private async Task RunSyncAsync()
    {
        // Non-blocking: skip if already syncing (don't queue)
        if (!await _syncLock.WaitAsync(0))
        {
            _logger?.LogDebug("Library sync already in progress, skipping");
            return;
        }

        try
        {
            // Signal UI: clear stale data, show loading state
            _messenger.Send(new LibrarySyncStartedMessage());

            // Lazy resolve: ISpotifyLibraryService requires connected session, not available at construction
            var libraryService = Ioc.Default.GetService<ISpotifyLibraryService>();
            if (libraryService == null)
            {
                _logger?.LogWarning("ISpotifyLibraryService not available — cannot sync");
                _messenger.Send(new LibrarySyncFailedMessage("Library service not available"));
                return;
            }

            // Capture before-counts for delta calculation
            // Lazy resolve: ITrackLikeService requires DB data
            var likeService = Ioc.Default.GetService<ITrackLikeService>();
            var beforeTracks = likeService?.GetCount(Data.Contracts.SavedItemType.Track) ?? 0;
            var beforeAlbums = likeService?.GetCount(Data.Contracts.SavedItemType.Album) ?? 0;
            var beforeArtists = likeService?.GetCount(Data.Contracts.SavedItemType.Artist) ?? 0;

            _logger?.LogInformation("Starting library sync...");
            bool hadPartialFailure = false;
            string? partialReason = null;
            try
            {
                await libraryService.SyncAllAsync();
                _logger?.LogInformation("Library sync completed");
            }
            catch (Exception syncEx)
            {
                hadPartialFailure = true;
                partialReason = syncEx.Message;
                _logger?.LogWarning(syncEx, "Library sync partially failed — continuing with available data");
            }

            // Reload in-memory cache from freshly synced DB
            if (likeService != null)
            {
                await likeService.InitializeAsync();
                _logger?.LogInformation("TrackLikeService cache initialized");
            }

            // Drain any pending outbox operations
            await libraryService.ProcessOutboxAsync();
            _logger?.LogDebug("Outbox processed");

            // Calculate delta (after - before)
            var afterTracks = likeService?.GetCount(Data.Contracts.SavedItemType.Track) ?? 0;
            var afterAlbums = likeService?.GetCount(Data.Contracts.SavedItemType.Album) ?? 0;
            var afterArtists = likeService?.GetCount(Data.Contracts.SavedItemType.Artist) ?? 0;

            var summary = new LibrarySyncSummary(
                TracksAdded: Math.Max(0, afterTracks - beforeTracks),
                TracksRemoved: Math.Max(0, beforeTracks - afterTracks),
                AlbumsAdded: Math.Max(0, afterAlbums - beforeAlbums),
                AlbumsRemoved: Math.Max(0, beforeAlbums - afterAlbums),
                ArtistsAdded: Math.Max(0, afterArtists - beforeArtists),
                ArtistsRemoved: Math.Max(0, beforeArtists - afterArtists),
                HadPartialFailure: hadPartialFailure,
                PartialFailureReason: partialReason);

            // Signal UI: sync done, refresh with real data
            _messenger.Send(new LibrarySyncCompletedMessage(summary));
            _messenger.Send(new LibraryDataChangedMessage());
            _logger?.LogInformation("Library sync complete — UI notified");

            // Wire Dealer real-time changes → IMessenger (once)
            WireDealerChanges(libraryService);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Library sync failed");
            _messenger.Send(new LibrarySyncFailedMessage(ex.Message));
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private void WireDealerChanges(ISpotifyLibraryService svc)
    {
        if (_dealerWired) return;
        _dealerWired = true;

        if (svc is SpotifyLibraryService concrete)
        {
            _dealerSubscription = concrete.LibraryChanged.Subscribe(evt =>
            {
                _logger?.LogDebug("Dealer library change: set={Set}, items={Count}", evt.Set, evt.Items.Count);
                _messenger.Send(new LibraryDataChangedMessage());
                if (evt.Set is "playlists")
                    _messenger.Send(new PlaylistsChangedMessage());
            });
            _logger?.LogInformation("Subscribed to Dealer library changes");
        }
    }

    public void Dispose()
    {
        _dealerSubscription?.Dispose();
        _syncLock.Dispose();
        _messenger.UnregisterAll(this);
    }
}
