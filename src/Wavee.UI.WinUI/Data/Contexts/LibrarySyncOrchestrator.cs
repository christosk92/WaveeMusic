using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Wavee.Core.Library.Spotify;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Messages;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Services;

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
    private readonly Wavee.UI.Services.Infra.IChangeBus _changeBus;
    private readonly ISpotifyLibraryService? _libraryService;
    private readonly ITrackLikeService? _likeService;
    private readonly INotificationService? _notificationService;
    private readonly DispatcherQueue? _dispatcher;
    private readonly ILogger? _logger;
    private readonly Func<Task>? _playlistPrefetchTrigger;
    private IDisposable? _dealerSubscription;
    private bool _dealerWired;

    public LibrarySyncOrchestrator(
        IMessenger messenger,
        Wavee.UI.Services.Infra.IChangeBus changeBus,
        ISpotifyLibraryService? libraryService = null,
        ITrackLikeService? likeService = null,
        INotificationService? notificationService = null,
        DispatcherQueue? dispatcher = null,
        IAuthState? authState = null,
        ILogger<LibrarySyncOrchestrator>? logger = null,
        IPlaylistPrefetcher? playlistPrefetcher = null)
    {
        _messenger = messenger;
        _changeBus = changeBus;
        _libraryService = libraryService;
        _likeService = likeService;
        _notificationService = notificationService;
        _dispatcher = dispatcher;
        _logger = logger;
        // Prefetcher injected as optional; keeping it through a delegate
        // means we don't take a strong dependency and can no-op cleanly
        // if DI hasn't registered it (e.g. integration tests).
        _playlistPrefetchTrigger = playlistPrefetcher is null
            ? null
            : () => playlistPrefetcher.PrefetchAllAsync();

        // React to auth status — trigger sync when Authenticated, drop the in-memory
        // save cache when the user signs out so the next account doesn't inherit hearts.
        messenger.Register<AuthStatusChangedMessage>(this, (_, msg) =>
        {
            if (msg.Value == AuthStatus.Authenticated)
            {
                _logger?.LogInformation("Auth status → Authenticated, triggering library sync");
                _ = RunSyncAsync();
            }
            else if (msg.Value is AuthStatus.LoggedOut or AuthStatus.SessionExpired)
            {
                _logger?.LogInformation("Auth status → {Status}, clearing TrackLikeService cache", msg.Value);
                _likeService?.ClearCache();
            }
        });

        // React to explicit sync requests (e.g. Liked Songs page detecting an empty local DB)
        messenger.Register<RequestLibrarySyncMessage>(this, (_, _) =>
        {
            _logger?.LogInformation("RequestLibrarySyncMessage received — triggering library sync");
            _ = RunSyncAsync(); // non-blocking: SemaphoreSlim guard skips if already running
        });

        _logger?.LogDebug("LibrarySyncOrchestrator registered for AuthStatusChangedMessage and RequestLibrarySyncMessage");

        // Cold-start race: this orchestrator is resolved on a Low-priority dispatcher tick
        // after first paint, but stored credentials can drive Auth → Authenticated faster
        // than that, so the messenger registration above misses the only Authenticated
        // broadcast and the sync never runs (sidebar stays empty until LogOut/LogIn).
        // Catch up by inspecting current AuthState on construction.
        if (authState?.Status == AuthStatus.Authenticated)
        {
            _logger?.LogInformation("LibrarySyncOrchestrator constructed after auth already Authenticated — triggering catch-up sync");
            _ = RunSyncAsync();
        }
    }

    private async Task RunSyncAsync()
    {
        // Non-blocking: skip if already syncing (don't queue)
        if (!await _syncLock.WaitAsync(0).ConfigureAwait(false))
        {
            _logger?.LogDebug("Library sync already in progress, skipping");
            return;
        }

        try
        {
            // Signal UI: clear stale data, show loading state
            _messenger.Send(new LibrarySyncStartedMessage());

            if (_libraryService == null)
            {
                _logger?.LogWarning("ISpotifyLibraryService not available — cannot sync");
                _messenger.Send(new LibrarySyncFailedMessage("Library service not available"));
                return;
            }

            // Seed from SQLite before taking the baseline. On cold start the
            // in-memory cache can still be empty even though the local library
            // DB is current, which would make every saved item look newly added.
            if (_likeService != null)
            {
                await _likeService.InitializeAsync().ConfigureAwait(false);
            }

            // Capture before-counts for delta calculation
            var beforeTracks = _likeService?.GetCount(Data.Contracts.SavedItemType.Track) ?? 0;
            var beforeAlbums = _likeService?.GetCount(Data.Contracts.SavedItemType.Album) ?? 0;
            var beforeArtists = _likeService?.GetCount(Data.Contracts.SavedItemType.Artist) ?? 0;

            _logger?.LogInformation("Starting library sync...");
            bool hadPartialFailure = false;
            string? partialReason = null;
            try
            {
                // Inlined SyncAllAsync to emit per-collection progress to the
                // sign-in dialog. The 5 collections below mirror the body of
                // SpotifyLibraryService.SyncAllAsync 1:1.
                var collections = new (string Name, Func<CancellationToken, Task> Run)[]
                {
                    ("tracks",        ct => _libraryService.SyncTracksAsync(ct)),
                    ("albums",        ct => _libraryService.SyncAlbumsAsync(ct)),
                    ("artists",       ct => _libraryService.SyncArtistsAsync(ct)),
                    ("shows",         ct => _libraryService.SyncShowsAsync(ct)),
                    ("listen-later",  ct => _libraryService.SyncListenLaterAsync(ct)),
                    ("ylpin",         ct => _libraryService.SyncYlPinsAsync(ct)),
                    // playlists runs LAST so the Pinned sidebar section's playlist URIs
                    // have placeholder entity rows already seated by SyncYlPinsAsync —
                    // avoids a window where the Pinned section renders titles for a
                    // few hundred ms before playlists are resolved.
                    ("playlists",     ct => _libraryService.SyncPlaylistsAsync(ct)),
                };
                for (int i = 0; i < collections.Length; i++)
                {
                    var (name, run) = collections[i];
                    _messenger.Send(new LibrarySyncProgressMessage(name, i, collections.Length));
                    try
                    {
                        await run(default).ConfigureAwait(false);
                    }
                    catch (Exception colEx)
                    {
                        hadPartialFailure = true;
                        partialReason ??= $"{name}: {colEx.Message}";
                        _logger?.LogWarning(colEx, "Library sync collection {Name} failed", name);
                    }
                }
                _messenger.Send(new LibrarySyncProgressMessage("done", collections.Length, collections.Length));
                _logger?.LogInformation("Library sync completed");
            }
            catch (Exception syncEx)
            {
                hadPartialFailure = true;
                partialReason = syncEx.Message;
                _logger?.LogWarning(syncEx, "Library sync partially failed — continuing with available data");
            }

            // Backfill metadata for any items in spotify_library missing from entities table
            try
            {
                await _libraryService.BackfillMissingMetadataAsync().ConfigureAwait(false);
                _logger?.LogInformation("Metadata backfill completed");
            }
            catch (Exception bfEx)
            {
                _logger?.LogWarning(bfEx, "Metadata backfill failed — some items may be missing");
            }

            // Reload in-memory cache from freshly synced DB
            if (_likeService != null)
            {
                await _likeService.ReloadCacheAsync().ConfigureAwait(false);
                _logger?.LogInformation("TrackLikeService cache reloaded after sync");
            }

            // Drain any pending outbox operations
            var outboxFailures = await _libraryService.ProcessOutboxAsync().ConfigureAwait(false);
            if (outboxFailures > 0)
            {
                hadPartialFailure = true;
                partialReason ??= $"{outboxFailures} outbox operation(s) failed to sync";
                _logger?.LogWarning("Outbox had {Count} failure(s)", outboxFailures);
            }
            else
            {
                _logger?.LogDebug("Outbox processed");
            }

            // Calculate delta (after - before)
            var afterTracks = _likeService?.GetCount(Data.Contracts.SavedItemType.Track) ?? 0;
            var afterAlbums = _likeService?.GetCount(Data.Contracts.SavedItemType.Album) ?? 0;
            var afterArtists = _likeService?.GetCount(Data.Contracts.SavedItemType.Artist) ?? 0;

            // Diagnostic: compare synced counts (what Spotify has) vs loaded counts (what we can display)
            try
            {
                var syncState = await _libraryService.GetSyncStateAsync().ConfigureAwait(false);
                var syncedTracks = syncState.Tracks?.ItemCount ?? 0;
                var syncedAlbums = syncState.Albums?.ItemCount ?? 0;
                var syncedArtists = syncState.Artists?.ItemCount ?? 0;

                _logger?.LogInformation(
                    "Sync integrity check — Synced: {ST} tracks, {SA} albums, {SAr} artists | " +
                    "Loaded: {LT} tracks, {LA} albums, {LAr} artists",
                    syncedTracks, syncedAlbums, syncedArtists,
                    afterTracks, afterAlbums, afterArtists);

                if (afterTracks < syncedTracks || afterAlbums < syncedAlbums || afterArtists < syncedArtists)
                {
                    _logger?.LogWarning(
                        "DATA LOSS: {MissingTracks} tracks, {MissingAlbums} albums, {MissingArtists} artists " +
                        "are in spotify_library but missing metadata in entities table",
                        syncedTracks - afterTracks, syncedAlbums - afterAlbums, syncedArtists - afterArtists);
                    hadPartialFailure = true;
                    partialReason ??= $"Missing metadata: {syncedTracks - afterTracks} tracks, " +
                        $"{syncedAlbums - afterAlbums} albums, {syncedArtists - afterArtists} artists";
                }
            }
            catch (Exception diagEx)
            {
                _logger?.LogDebug(diagEx, "Sync integrity check failed");
            }

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
            _changeBus.Publish(Wavee.UI.Services.Infra.ChangeScope.Library);
            _logger?.LogInformation("Library sync complete — UI notified");

            // Wire Dealer real-time changes → IMessenger (once)
            WireDealerChanges(_libraryService);

            // Kick off playlist prefetch in the background so the sign-in
            // dialog can show "Loading your playlists" progress and the
            // first click on any sidebar playlist is a cache hit. Fire-
            // and-forget — the prefetcher is responsible for its own error
            // handling and message emission.
            if (_playlistPrefetchTrigger is not null)
            {
                _ = Task.Run(async () =>
                {
                    try { await _playlistPrefetchTrigger().ConfigureAwait(false); }
                    catch (Exception ex) { _logger?.LogWarning(ex, "Playlist prefetch failed"); }
                });
            }
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
                _logger?.LogInformation(
                    "[rootlist] orchestrator dealer rx set={Set} items={N} isRootlist={Root} playlistUri={Pu}",
                    evt.Set, evt.Items.Count, evt.IsRootlist, evt.PlaylistUri ?? "<none>");
                _changeBus.Publish(Wavee.UI.Services.Infra.ChangeScope.Library);
                var isPlaylists = string.Equals(evt.Set, "playlists", StringComparison.OrdinalIgnoreCase);
                var isYlpin = string.Equals(evt.Set, "ylpin", StringComparison.OrdinalIgnoreCase);

                if (isPlaylists)
                {
                    _logger?.LogInformation(
                        "[rootlist] orchestrator -> SyncPlaylistsAsync re-run triggered (dealer-driven rootlist refresh)");
                    _changeBus.Publish(Wavee.UI.Services.Infra.ChangeScope.Playlists);

                    // Rootlist dealer pushes carry only a revision bump — no item
                    // list — so the immediate Publish above only causes the
                    // sidebar to re-read the still-stale DB. Run SyncPlaylistsAsync
                    // in the background to fetch the new rootlist and upsert any
                    // added/removed playlists, then publish again so the sidebar
                    // reads the freshly-synced rows. ChangeBus's 150ms coalesce
                    // absorbs the overlap when the sync is fast.
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await svc.SyncPlaylistsAsync().ConfigureAwait(false);
                            _changeBus.Publish(Wavee.UI.Services.Infra.ChangeScope.Library);
                            _changeBus.Publish(Wavee.UI.Services.Infra.ChangeScope.Playlists);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "Dealer-driven playlist sync failed");
                        }
                    });
                }

                // ylpin pushes don't carry an item payload (LibraryChangeManager
                // has no parser for the hm://collection/ylpin/<user> shape), so
                // the pre-emit above triggers a sidebar refresh that reads the
                // still-stale DB. Re-run the incremental sync — cheap because
                // it uses the stored sync token — then publish Library again
                // so the sidebar re-reads the freshly-synced rows. ChangeBus's
                // 150ms coalesce window absorbs the overlap when the sync is
                // fast.
                if (isYlpin)
                {
                    _logger?.LogInformation(
                        "[rootlist] ylpin re-sync triggered (dealer-driven Pinned section refresh)");
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await svc.SyncYlPinsAsync().ConfigureAwait(false);
                            _changeBus.Publish(Wavee.UI.Services.Infra.ChangeScope.Library);
                            _changeBus.Publish(Wavee.UI.Services.Infra.ChangeScope.Pins);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "Dealer-driven ylpin sync failed");
                        }
                    });
                }
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
