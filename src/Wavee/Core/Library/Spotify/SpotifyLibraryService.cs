using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using Wavee.Connect;
using Wavee.Core.Http;
using Wavee.Core.Playlists;
using Wavee.Core.Session;
using Wavee.Core.Storage;
using Wavee.Core.Storage.Abstractions;
using Wavee.Protocol.Collection;
using Wavee.Protocol.ExtendedMetadata;
using Wavee.Protocol.Metadata;

namespace Wavee.Core.Library.Spotify;

/// <summary>
/// Implementation of ISpotifyLibraryService.
/// Syncs liked songs with Spotify and provides real-time updates via Dealer.
/// Uses the unified MetadataDatabase for storage.
/// </summary>
public sealed class SpotifyLibraryService : ISpotifyLibraryService
{
    // "collection" contains both tracks (spotify:track:) and albums (spotify:album:)
    private const string CollectionSet = "collection";
    private const string ArtistsSet = "artist";  // singular
    private const string ShowsSet = "show";      // singular
    private const string BanSet = "ban";
    private const string ArtistBanSet = "artistban";
    private const string ListenLaterSet = "listenlater";
    private const string YlPinSet = "ylpin";
    private const string EnhancedSet = "enhanced";

    // URI prefixes for filtering collection items
    private const string TrackUriPrefix = "spotify:track:";
    private const string AlbumUriPrefix = "spotify:album:";
    private const string ArtistUriPrefix = "spotify:artist:";

    private readonly IMetadataDatabase _database;
    private readonly SpClient _spClient;
    private readonly ISession _session;
    private readonly LibraryChangeManager? _libraryChangeManager;
    private readonly IExtendedMetadataClient? _metadataClient;
    private readonly IPlaylistCacheService? _playlistCache;
    private readonly ILogger? _logger;
    private readonly Subject<SyncProgress> _progressSubject = new();
    private readonly Subject<LibraryChangeEvent> _libraryChanged = new();
    private IDisposable? _changeSubscription;
    private bool _disposed;

    /// <summary>
    /// Creates a new SpotifyLibraryService.
    /// </summary>
    /// <param name="database">Unified metadata database for persistence.</param>
    /// <param name="spClient">Spotify API client.</param>
    /// <param name="session">Active session for username.</param>
    /// <param name="libraryChangeManager">Optional manager for real-time updates.</param>
    /// <param name="metadataClient">Optional extended metadata client for fetching track details.</param>
    /// <param name="logger">Optional logger.</param>
    public SpotifyLibraryService(
        IMetadataDatabase database,
        SpClient spClient,
        ISession session,
        LibraryChangeManager? libraryChangeManager = null,
        IExtendedMetadataClient? metadataClient = null,
        IPlaylistCacheService? playlistCache = null,
        ILogger? logger = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _spClient = spClient ?? throw new ArgumentNullException(nameof(spClient));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _libraryChangeManager = libraryChangeManager;
        _metadataClient = metadataClient;
        _playlistCache = playlistCache;
        _logger = logger;

        // Subscribe to real-time library changes for all collection types
        if (_libraryChangeManager != null)
        {
            _changeSubscription = _libraryChangeManager.Changes
                .Subscribe(OnLibraryChange);
        }
    }

    /// <inheritdoc/>
    public IObservable<SyncProgress> SyncProgress => _progressSubject;

    /// <summary>
    /// Observable for library changes (from real-time updates).
    /// </summary>
    public IObservable<LibraryChangeEvent> LibraryChanged => _libraryChanged.AsObservable();

    private string GetUsername()
    {
        var userData = _session.GetUserData();
        if (userData == null)
            throw new InvalidOperationException("Not authenticated");
        return userData.Username;
    }

    #region Sync Operations

    /// <inheritdoc/>
    public async Task BackfillMissingMetadataAsync(CancellationToken ct = default)
    {
        if (_metadataClient == null)
        {
            _logger?.LogWarning("Skipping metadata backfill: no metadata client available");
            return;
        }

        var typesToBackfill = new[]
        {
            (SpotifyLibraryItemType.Track, ExtensionKind.TrackV4, "tracks"),
            (SpotifyLibraryItemType.Album, ExtensionKind.AlbumV4, "albums"),
            (SpotifyLibraryItemType.Artist, ExtensionKind.ArtistV4, "artists"),
            (SpotifyLibraryItemType.Show, ExtensionKind.ShowV4, "shows"),
        };

        var totalBackfilled = 0;
        foreach (var (itemType, extensionKind, displayName) in typesToBackfill)
        {
            var missingUris = await _database.GetLibraryUrisMissingMetadataAsync(itemType, ct);
            if (missingUris.Count == 0)
            {
                _logger?.LogDebug("Backfill: all {Type} items have metadata", displayName);
                continue;
            }

            _logger?.LogWarning("Backfill: {Count} {Type} in spotify_library are missing from entities table — fetching metadata",
                missingUris.Count, displayName);

            try
            {
                await FetchAndStoreMetadataAsync(missingUris, extensionKind, displayName, ct);
                totalBackfilled += missingUris.Count;
                _logger?.LogInformation("Backfill: fetched metadata for {Count} {Type}", missingUris.Count, displayName);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Backfill: FAILED to fetch metadata for {Count} {Type}", missingUris.Count, displayName);
            }
        }

        var missingListenLaterUris = await _database.GetLibraryUrisMissingMetadataAsync(SpotifyLibraryItemType.ListenLater, ct);
        if (missingListenLaterUris.Count > 0)
        {
            _logger?.LogWarning("Backfill: {Count} listen later items are missing from entities table - fetching mixed metadata",
                missingListenLaterUris.Count);

            try
            {
                await FetchMixedTypeMetadataAsync(missingListenLaterUris, "listen later", ct);
                totalBackfilled += missingListenLaterUris.Count;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Backfill: FAILED to fetch metadata for {Count} listen later items",
                    missingListenLaterUris.Count);
            }
        }

        if (totalBackfilled > 0)
            _logger?.LogInformation("Metadata backfill complete: {Total} items repaired", totalBackfilled);
        else
            _logger?.LogDebug("Metadata backfill: no missing items found");
    }

    /// <inheritdoc/>
    public async Task SyncAllAsync(CancellationToken ct = default)
    {
        await SyncTracksAsync(ct);
        await SyncAlbumsAsync(ct);
        await SyncArtistsAsync(ct);
        await SyncShowsAsync(ct);
        await SyncListenLaterAsync(ct);
    }

    /// <inheritdoc/>
    public Task SyncTracksAsync(CancellationToken ct = default)
        => SyncCollectionAsync(CollectionSet, SpotifyLibraryItemType.Track, "tracks", TrackUriPrefix, ct);

    /// <inheritdoc/>
    public Task SyncAlbumsAsync(CancellationToken ct = default)
        => SyncCollectionAsync(CollectionSet, SpotifyLibraryItemType.Album, "albums", AlbumUriPrefix, ct);

    /// <inheritdoc/>
    public Task SyncArtistsAsync(CancellationToken ct = default)
        => SyncCollectionAsync(ArtistsSet, SpotifyLibraryItemType.Artist, "artists", null, ct);

    /// <summary>
    /// Syncs subscribed podcast shows from Spotify.
    /// </summary>
    public Task SyncShowsAsync(CancellationToken ct = default)
        => SyncCollectionAsync(ShowsSet, SpotifyLibraryItemType.Show, "shows", null, ct);

    /// <summary>
    /// Syncs banned tracks from Spotify.
    /// </summary>
    public Task SyncBansAsync(CancellationToken ct = default)
        => SyncCollectionAsync(BanSet, SpotifyLibraryItemType.Ban, "bans", TrackUriPrefix, ct);

    /// <summary>
    /// Syncs banned artists from Spotify.
    /// </summary>
    public Task SyncArtistBansAsync(CancellationToken ct = default)
        => SyncCollectionAsync(ArtistBanSet, SpotifyLibraryItemType.ArtistBan, "artist bans", ArtistUriPrefix, ct);

    /// <summary>
    /// Syncs listen later queue from Spotify.
    /// </summary>
    public Task SyncListenLaterAsync(CancellationToken ct = default)
        => SyncCollectionAsync(ListenLaterSet, SpotifyLibraryItemType.ListenLater, "listen later", null, ct);

    /// <summary>
    /// Syncs Your Library pinned items from Spotify, then backfills playlist
    /// metadata for any pinned playlist whose entities-table row still has the
    /// URI placeholder title (e.g. items added under the old code path that
    /// only wrote a placeholder, or pinned playlists the user doesn't follow
    /// and that therefore never came through the rootlist sync).
    /// </summary>
    public async Task SyncYlPinsAsync(CancellationToken ct = default)
    {
        await SyncCollectionAsync(YlPinSet, SpotifyLibraryItemType.YlPin, "pins", null, ct);
        await BackfillPinnedPlaylistMetadataAsync(ct);
    }

    /// <summary>
    /// Resolves the entity row for a single pinned playlist URI by calling
    /// SpClient and writing the real title + cover into the entities table.
    /// Catches its own exceptions — a missing playlist (gone, private) must
    /// not kill the whole sync.
    /// </summary>
    private async Task ResolvePinnedPlaylistEntityAsync(string playlistUri, CancellationToken ct)
    {
        try
        {
            var playlist = await _spClient.GetPlaylistAsync(
                playlistUri,
                decorate: new[] { "attributes", "length", "owner" },
                cancellationToken: ct);

            string? imageUrl = null;
            if (playlist.Attributes?.PictureSize.Count > 0)
            {
                imageUrl = playlist.Attributes.PictureSize
                    .FirstOrDefault(p => p.TargetName == "default")?.Url
                    ?? playlist.Attributes.PictureSize.FirstOrDefault()?.Url;
            }

            var name = playlist.Attributes?.Name;
            await _database.UpsertEntityAsync(
                uri: playlistUri,
                entityType: EntityType.Playlist,
                title: !string.IsNullOrWhiteSpace(name) ? name : playlistUri,
                imageUrl: imageUrl,
                trackCount: playlist.Length,
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to resolve pinned playlist metadata for {Uri} — leaving placeholder", playlistUri);
            // Fall back to placeholder so the INNER JOIN in
            // GetSpotifyLibraryItemsAsync still returns the row; sidebar will
            // render the raw URI until the next sync's retry succeeds.
            await _database.UpsertEntityAsync(
                uri: playlistUri,
                entityType: EntityType.Playlist,
                title: playlistUri,
                cancellationToken: ct);
        }
    }

    /// <summary>
    /// Backfill pass for pinned playlists whose entities-table row was seated
    /// before <see cref="ResolvePinnedPlaylistEntityAsync"/> existed (placeholder
    /// title equal to the URI). Resolves them via SpClient so the sidebar shows
    /// the real name and cover on the next observe.
    /// </summary>
    private async Task BackfillPinnedPlaylistMetadataAsync(CancellationToken ct)
    {
        try
        {
            var entities = await _database.GetSpotifyLibraryItemsAsync(
                SpotifyLibraryItemType.YlPin, int.MaxValue, 0, ct);

            var stale = entities
                .Where(e => e.Uri.StartsWith("spotify:playlist:", StringComparison.Ordinal)
                            && (string.IsNullOrWhiteSpace(e.Title) || e.Title == e.Uri))
                .Select(e => e.Uri)
                .ToList();

            if (stale.Count == 0) return;

            _logger?.LogDebug("Backfilling metadata for {Count} pinned playlists with placeholder titles", stale.Count);
            foreach (var uri in stale)
            {
                ct.ThrowIfCancellationRequested();
                await ResolvePinnedPlaylistEntityAsync(uri, ct);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Pinned-playlist backfill failed; will retry on next sync");
        }
    }

    /// <summary>
    /// Syncs enhanced playlist tracks from Spotify.
    /// </summary>
    public Task SyncEnhancedAsync(CancellationToken ct = default)
        => SyncCollectionAsync(EnhancedSet, SpotifyLibraryItemType.Enhanced, "enhanced", TrackUriPrefix, ct);

    /// <inheritdoc/>
    public async Task SyncPlaylistsAsync(CancellationToken ct = default)
    {
        var username = GetUsername();
        var storedSyncState = await _database.GetSyncStateAsync("playlists", ct);
        var storedRev = storedSyncState?.Revision ?? "<none>";
        _logger?.LogInformation(
            "[rootlist] SyncPlaylistsAsync START username={Username} storedRev={Stored}",
            username, storedRev);
        _progressSubject.OnNext(new SyncProgress("playlists", 0, 0, "Fetching rootlist..."));

        try
        {
            if (_playlistCache != null)
            {
                // Single source of truth: PlaylistCacheService owns the rootlist
                // fetch (will use /diff when there's a prior revision, full GET
                // otherwise). The snapshot's Decorations carry Name/Owner/
                // ImageUrl/Length per playlist, so no per-playlist round-trips
                // are needed — the rootlist response already has the metadata.
                var snapshot = await _playlistCache.GetRootlistAsync(forceRefresh: true, ct);
                await ApplyRootlistSnapshotAsync(username, snapshot, storedRev, ct);
            }
            else
            {
                // Standalone path: Wavee.Console doesn't register IPlaylistCacheService
                // yet, so it falls through to the original N+1 fetch implementation
                // here. When the console gains its own IPlaylistCacheService
                // registration, this branch can be deleted.
                await SyncPlaylistsStandaloneAsync(username, storedRev, ct);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[rootlist] SyncPlaylistsAsync FAILED");
            _progressSubject.OnNext(new SyncProgress("playlists", 0, 0, $"Sync failed: {ex.Message}"));
            throw;
        }
    }

    private async Task ApplyRootlistSnapshotAsync(
        string username,
        RootlistSnapshot snapshot,
        string storedRev,
        CancellationToken ct)
    {
        var responseRev = snapshot.Revision.Length > 0
            ? Convert.ToBase64String(snapshot.Revision)
            : "<none>";
        _logger?.LogInformation(
            "[rootlist] snapshot received responseRev={Rev} items={Items}",
            responseRev, snapshot.Items.Count);

        // Walk snapshot.Items reconstructing folder paths from
        // RootlistFolderStart / RootlistFolderEnd. Same logic as the standalone
        // path used to do against raw protobuf — just on the typed snapshot now.
        var playlistsWithFolders = new List<(string Uri, string? FolderPath)>();
        var folderStack = new Stack<string>();

        foreach (var entry in snapshot.Items)
        {
            switch (entry)
            {
                case RootlistFolderStart start:
                    folderStack.Push(start.Name);
                    break;
                case RootlistFolderEnd:
                    if (folderStack.Count > 0) folderStack.Pop();
                    break;
                case RootlistPlaylist playlist:
                    var folderPath = folderStack.Count > 0
                        ? string.Join("/", folderStack.Reverse())
                        : null;
                    playlistsWithFolders.Add((playlist.Uri, folderPath));
                    break;
            }
        }

        var folderMarkerCount = snapshot.Items.Count - playlistsWithFolders.Count;
        _logger?.LogInformation(
            "[rootlist] discoveredPlaylists={N} folderMarkers={F}",
            playlistsWithFolders.Count, folderMarkerCount);
        _progressSubject.OnNext(new SyncProgress("playlists", 0, playlistsWithFolders.Count,
            $"Found {playlistsWithFolders.Count} playlists..."));

        // Upsert each playlist using decorations from the rootlist response —
        // zero additional network round-trips.
        var processed = 0;
        foreach (var (uri, folderPath) in playlistsWithFolders)
        {
            ct.ThrowIfCancellationRequested();

            snapshot.Decorations.TryGetValue(uri, out var decoration);

            var revB64 = decoration?.Revision.Length > 0
                ? Convert.ToBase64String(decoration.Revision)
                : null;

            var spotifyPlaylist = SpotifyPlaylist.Create(
                uri: uri,
                name: decoration?.Name ?? "Unknown",
                ownerId: decoration?.OwnerUsername ?? string.Empty,
                description: decoration?.Description,
                imageUrl: decoration?.ImageUrl,
                trackCount: decoration?.Length ?? 0,
                isPublic: decoration?.IsPublic ?? true,
                isCollaborative: decoration?.IsCollaborative ?? false,
                isOwned: decoration?.OwnerUsername == username,
                revision: revB64,
                folderPath: folderPath
            );

            await _database.UpsertPlaylistAsync(spotifyPlaylist, ct);
            _logger?.LogInformation(
                "[rootlist] db.UpsertPlaylist {Uri} title=\"{Title}\"",
                uri, decoration?.Name ?? "<unknown>");
            processed++;

            if (processed % 10 == 0 || processed == playlistsWithFolders.Count)
            {
                _progressSubject.OnNext(new SyncProgress("playlists", processed, playlistsWithFolders.Count,
                    $"Syncing playlists... ({processed}/{playlistsWithFolders.Count})"));
            }
        }

        // Remove playlists no longer in rootlist.
        var existing = await _database.GetAllPlaylistsAsync(ct);
        var playlistUriSet = playlistsWithFolders.Select(p => p.Uri).ToHashSet();
        var toRemove = existing.Where(p => !playlistUriSet.Contains(p.Uri)).ToList();

        foreach (var p in toRemove)
        {
            await _database.DeletePlaylistAsync(p.Uri, ct);
            _logger?.LogDebug("Removed stale playlist: {Uri}", p.Uri);
        }

        // Update sync state.
        await _database.SetSyncStateAsync(
            "playlists",
            responseRev == "<none>" ? null : responseRev,
            playlistsWithFolders.Count,
            ct);
        _logger?.LogInformation(
            "[rootlist] sync_state['playlists'] {Old} -> {New} count={Count} removed={Removed}",
            storedRev, responseRev, playlistsWithFolders.Count, toRemove.Count);

        _progressSubject.OnNext(new SyncProgress("playlists", playlistsWithFolders.Count, playlistsWithFolders.Count,
            $"Sync complete: {playlistsWithFolders.Count} playlists"));

        _logger?.LogInformation("[rootlist] SyncPlaylistsAsync DONE count={Count}", playlistsWithFolders.Count);
    }

    // Standalone N+1 implementation kept for the Wavee.Console scenario where
    // IPlaylistCacheService isn't registered. Deletable once console DI adds it.
    private async Task SyncPlaylistsStandaloneAsync(string username, string storedRev, CancellationToken ct)
    {
        var rootlistUri = $"spotify:user:{username}:rootlist";

        var rootlist = await _spClient.GetPlaylistAsync(
            rootlistUri,
            decorate: new[] { "revision", "attributes", "length" },
            cancellationToken: ct);

        var responseRev = rootlist.Revision?.Length > 0
            ? Convert.ToBase64String(rootlist.Revision.ToByteArray())
            : "<none>";
        _logger?.LogInformation(
            "[rootlist] (standalone) rootlist GET ok responseRev={Rev} contentItems={Items} length={Length}",
            responseRev, rootlist.Contents?.Items?.Count ?? 0, rootlist.Length);

        var playlistsWithFolders = new List<(string Uri, string? FolderPath)>();
        var folderStack = new Stack<string>();

        foreach (var item in rootlist.Contents?.Items ?? Enumerable.Empty<Protocol.Playlist.Item>())
        {
            var uri = item.Uri;

            if (uri.StartsWith("spotify:start-group:", StringComparison.Ordinal))
            {
                var parts = uri.Split(':', 4);
                var folderName = parts.Length >= 4 ? parts[3] : "Folder";
                folderStack.Push(folderName);
            }
            else if (uri.StartsWith("spotify:end-group:", StringComparison.Ordinal))
            {
                if (folderStack.Count > 0)
                    folderStack.Pop();
            }
            else if (uri.StartsWith("spotify:playlist:", StringComparison.Ordinal))
            {
                var folderPath = folderStack.Count > 0
                    ? string.Join("/", folderStack.Reverse())
                    : null;
                playlistsWithFolders.Add((uri, folderPath));
            }
        }

        _progressSubject.OnNext(new SyncProgress("playlists", 0, playlistsWithFolders.Count,
            $"Found {playlistsWithFolders.Count} playlists..."));

        var processed = 0;
        foreach (var (uri, folderPath) in playlistsWithFolders)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var playlist = await _spClient.GetPlaylistAsync(
                    uri,
                    decorate: new[] { "revision", "attributes", "length", "owner" },
                    cancellationToken: ct);

                string? imageUrl = null;
                if (playlist.Attributes?.PictureSize.Count > 0)
                {
                    imageUrl = playlist.Attributes.PictureSize
                        .FirstOrDefault(p => p.TargetName == "default")?.Url
                        ?? playlist.Attributes.PictureSize.FirstOrDefault()?.Url;
                }

                var spotifyPlaylist = SpotifyPlaylist.Create(
                    uri: uri,
                    name: playlist.Attributes?.Name ?? "Unknown",
                    ownerId: playlist.OwnerUsername,
                    description: playlist.Attributes?.Description,
                    imageUrl: imageUrl,
                    trackCount: playlist.Length,
                    isPublic: true,
                    isCollaborative: playlist.Attributes?.Collaborative ?? false,
                    isOwned: playlist.OwnerUsername == username,
                    revision: playlist.Revision?.Length > 0
                        ? Convert.ToBase64String(playlist.Revision.ToByteArray())
                        : null,
                    folderPath: folderPath
                );

                await _database.UpsertPlaylistAsync(spotifyPlaylist, ct);
                processed++;

                if (processed % 10 == 0 || processed == playlistsWithFolders.Count)
                {
                    _progressSubject.OnNext(new SyncProgress("playlists", processed, playlistsWithFolders.Count,
                        $"Syncing playlists... ({processed}/{playlistsWithFolders.Count})"));
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to sync playlist {Uri}, continuing...", uri);
            }
        }

        var existing = await _database.GetAllPlaylistsAsync(ct);
        var playlistUriSet = playlistsWithFolders.Select(p => p.Uri).ToHashSet();
        var toRemove = existing.Where(p => !playlistUriSet.Contains(p.Uri)).ToList();

        foreach (var p in toRemove)
        {
            await _database.DeletePlaylistAsync(p.Uri, ct);
            _logger?.LogDebug("Removed stale playlist: {Uri}", p.Uri);
        }

        var newRev = rootlist.Revision?.Length > 0 ? Convert.ToBase64String(rootlist.Revision.ToByteArray()) : null;
        await _database.SetSyncStateAsync(
            "playlists",
            newRev,
            playlistsWithFolders.Count,
            ct);
        _logger?.LogInformation(
            "[rootlist] (standalone) sync_state['playlists'] {Old} -> {New} count={Count} removed={Removed}",
            storedRev, newRev ?? "<none>", playlistsWithFolders.Count, toRemove.Count);

        _progressSubject.OnNext(new SyncProgress("playlists", playlistsWithFolders.Count, playlistsWithFolders.Count,
            $"Sync complete: {playlistsWithFolders.Count} playlists"));

        _logger?.LogInformation("[rootlist] (standalone) SyncPlaylistsAsync DONE count={Count}", playlistsWithFolders.Count);
    }

    private async Task SyncCollectionAsync(string set, SpotifyLibraryItemType itemType, string displayName, string? uriFilter, CancellationToken ct)
    {
        var username = GetUsername();
        _logger?.LogInformation("Starting {Collection} sync for {Username}", displayName, username);

        _progressSubject.OnNext(new SyncProgress(displayName, 0, 0, "Starting sync..."));

        try
        {
            // Use itemType-specific revision key when filtering (tracks/albums share "collection" set)
            var revisionKey = uriFilter != null ? $"{set}:{itemType}" : set;
            var syncState = await _database.GetSyncStateAsync(revisionKey, ct);
            var lastRevision = syncState?.Revision;

            if (!string.IsNullOrEmpty(lastRevision))
            {
                var success = await TryIncrementalSyncAsync(username, set, lastRevision, itemType, displayName, uriFilter, ct);
                if (success)
                {
                    _logger?.LogInformation("{Collection} incremental sync completed successfully", displayName);
                    return;
                }
                _logger?.LogInformation("{Collection} incremental sync not possible, falling back to full sync", displayName);
            }

            await FullSyncAsync(username, set, itemType, displayName, uriFilter, ct);

            _logger?.LogInformation("{Collection} full sync completed", displayName);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "{Collection} sync failed", displayName);
            _progressSubject.OnNext(new SyncProgress(displayName, 0, 0, $"Sync failed: {ex.Message}"));
            throw;
        }
    }

    private async Task<bool> TryIncrementalSyncAsync(
        string username,
        string set,
        string lastRevision,
        SpotifyLibraryItemType itemType,
        string displayName,
        string? uriFilter,
        CancellationToken ct)
    {
        try
        {
            var deltaResponse = await _spClient.GetCollectionDeltaAsync(username, set, lastRevision, ct);

            if (!deltaResponse.DeltaUpdatePossible)
            {
                return false;
            }

            // Filter items by URI prefix if specified
            var items = uriFilter != null
                ? deltaResponse.Items.Where(i => i.Uri.StartsWith(uriFilter, StringComparison.Ordinal)).ToList()
                : deltaResponse.Items.ToList();

            _progressSubject.OnNext(new SyncProgress(displayName, 0, items.Count, "Processing changes..."));

            var addCount = 0;
            var removeCount = 0;

            foreach (var item in items)
            {
                if (item.IsRemoved)
                {
                    await _database.RemoveFromSpotifyLibraryAsync(item.Uri, ct);
                    removeCount++;
                }
                else
                {
                    await _database.AddToSpotifyLibraryAsync(item.Uri, itemType, item.AddedAt, ct);
                    addCount++;
                }
            }

            // Fetch metadata for newly added items so they appear in INNER JOIN queries
            if (addCount > 0 && _metadataClient != null)
            {
                var addedUris = items.Where(i => !i.IsRemoved).Select(i => i.Uri).ToList();
                if (itemType == SpotifyLibraryItemType.YlPin || itemType == SpotifyLibraryItemType.ListenLater)
                {
                    _logger?.LogDebug("Fetching mixed metadata for {Count} new {Type} items", addedUris.Count, displayName);
                    await FetchMixedTypeMetadataAsync(addedUris, displayName, ct);
                }
                else
                {
                    var extensionKind = itemType switch
                    {
                        SpotifyLibraryItemType.Track => ExtensionKind.TrackV4,
                        SpotifyLibraryItemType.Album => ExtensionKind.AlbumV4,
                        SpotifyLibraryItemType.Artist => ExtensionKind.ArtistV4,
                        SpotifyLibraryItemType.Show => ExtensionKind.ShowV4,
                        _ => (ExtensionKind?)null
                    };
                    if (extensionKind.HasValue)
                    {
                        _logger?.LogDebug("Fetching metadata for {Count} new {Type} items", addedUris.Count, displayName);
                        await FetchAndStoreMetadataAsync(addedUris, extensionKind.Value, displayName, ct);
                    }
                }
            }

            var revisionKey = uriFilter != null ? $"{set}:{itemType}" : set;
            var itemCount = await _database.GetSpotifyLibraryCountAsync(itemType, ct);
            await _database.SetSyncStateAsync(revisionKey, deltaResponse.SyncToken, itemCount, ct);

            _progressSubject.OnNext(new SyncProgress(displayName, items.Count, items.Count,
                $"Sync complete: {addCount} added, {removeCount} removed"));

            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "{Collection} incremental sync failed, will try full sync", displayName);
            return false;
        }
    }

    private async Task FullSyncAsync(
        string username,
        string set,
        SpotifyLibraryItemType itemType,
        string displayName,
        string? uriFilter,
        CancellationToken ct)
    {
        var allItems = new List<(string uri, long addedAt)>();
        string? paginationToken = null;
        string? syncToken = null;
        var pageCount = 0;

        // Phase 1: Fetch all item URIs from collection
        do
        {
            var pageResponse = await _spClient.GetCollectionPageAsync(username, set, paginationToken, 300, ct);
            pageCount++;

            foreach (var item in pageResponse.Items)
            {
                // Filter by URI prefix if specified
                if (!item.IsRemoved && (uriFilter == null || item.Uri.StartsWith(uriFilter, StringComparison.Ordinal)))
                {
                    allItems.Add((item.Uri, item.AddedAt));
                }
            }

            paginationToken = string.IsNullOrEmpty(pageResponse.NextPageToken) ? null : pageResponse.NextPageToken;
            syncToken = pageResponse.SyncToken;

            _progressSubject.OnNext(new SyncProgress(displayName, allItems.Count, 0,
                $"Fetching page {pageCount}... ({allItems.Count} items)"));

        } while (paginationToken != null);

        // Phase 2: Fetch metadata (stores directly in entities table via ExtendedMetadataClient)
        // This populates the entities table which spotify_library FKs to
        if (allItems.Count > 0 && _metadataClient != null)
        {
            var uris = allItems.Select(x => x.uri).ToList();

            // For mixed-type collections (YlPin, ListenLater), group by URI type and fetch each
            if (itemType == SpotifyLibraryItemType.YlPin || itemType == SpotifyLibraryItemType.ListenLater)
            {
                await FetchMixedTypeMetadataAsync(uris, displayName, ct);
            }
            else
            {
                // Determine extension kind based on item type (works for both filtered and dedicated endpoints)
                var extensionKind = itemType switch
                {
                    SpotifyLibraryItemType.Track => ExtensionKind.TrackV4,
                    SpotifyLibraryItemType.Album => ExtensionKind.AlbumV4,
                    SpotifyLibraryItemType.Artist => ExtensionKind.ArtistV4,
                    SpotifyLibraryItemType.Show => ExtensionKind.ShowV4,
                    SpotifyLibraryItemType.Ban => ExtensionKind.TrackV4,
                    SpotifyLibraryItemType.ArtistBan => ExtensionKind.ArtistV4,
                    SpotifyLibraryItemType.Enhanced => ExtensionKind.TrackV4,
                    _ => (ExtensionKind?)null
                };

                if (extensionKind.HasValue)
                    await FetchAndStoreMetadataAsync(uris, extensionKind.Value, displayName, ct);
            }
        }

        // Phase 3: Add to spotify_library (entities already populated in Phase 2)
        _progressSubject.OnNext(new SyncProgress(displayName, 0, allItems.Count, "Adding to library..."));

        // Clear existing items of this type first to avoid duplicates
        await _database.ClearSpotifyLibraryAsync(itemType, ct);

        var processed = 0;
        foreach (var (uri, addedAt) in allItems)
        {
            await _database.AddToSpotifyLibraryAsync(uri, itemType, addedAt, ct);
            processed++;

            if (processed % 100 == 0)
            {
                _progressSubject.OnNext(new SyncProgress(displayName, processed, allItems.Count,
                    $"Adding to library... ({processed}/{allItems.Count})"));
            }
        }

        var revisionKey = uriFilter != null ? $"{set}:{itemType}" : set;
        await _database.SetSyncStateAsync(revisionKey, syncToken, allItems.Count, ct);

        _progressSubject.OnNext(new SyncProgress(displayName, allItems.Count, allItems.Count,
            $"Sync complete: {allItems.Count} {displayName}"));
    }

    #endregion

    #region Read Operations

    /// <inheritdoc/>
    public async Task<IReadOnlyList<LibraryItem>> GetLikedSongsAsync(int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        // GetSpotifyLibraryItemsAsync returns CachedEntity with full metadata joined from entities table
        var entities = await _database.GetSpotifyLibraryItemsAsync(SpotifyLibraryItemType.Track, limit, offset, ct);

        // Map to LibraryItem for backwards compatibility
        return entities.Select(e => new LibraryItem
        {
            Id = e.Uri,
            SourceType = SourceType.Spotify,
            Title = e.Title ?? ExtractIdFromUri(e.Uri),
            Artist = e.ArtistName,
            Album = e.AlbumName,
            DurationMs = e.DurationMs ?? 0,
            ImageUrl = e.ImageUrl,
            AddedAt = e.AddedAt?.ToUnixTimeSeconds() ?? 0,
            UpdatedAt = e.UpdatedAt.ToUnixTimeSeconds()
        }).ToList();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<LibraryItem>> GetSavedAlbumsAsync(int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        var entities = await _database.GetSpotifyLibraryItemsAsync(SpotifyLibraryItemType.Album, limit, offset, ct);

        return entities.Select(e => new LibraryItem
        {
            Id = e.Uri,
            SourceType = SourceType.Spotify,
            Title = e.Title ?? ExtractIdFromUri(e.Uri),
            Artist = e.ArtistName,
            DurationMs = 0,  // Albums don't have duration
            ImageUrl = e.ImageUrl,
            AddedAt = e.AddedAt?.ToUnixTimeSeconds() ?? 0,
            UpdatedAt = e.UpdatedAt.ToUnixTimeSeconds()
        }).ToList();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SpotifyPlaylist>> GetPlaylistsAsync(CancellationToken ct = default)
    {
        var playlists = await _database.GetAllPlaylistsAsync(ct);
        return playlists;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<LibraryItem>> GetFollowedArtistsAsync(CancellationToken ct = default)
    {
        var entities = await _database.GetSpotifyLibraryItemsAsync(SpotifyLibraryItemType.Artist, 1000, 0, ct);

        return entities.Select(e => new LibraryItem
        {
            Id = e.Uri,
            SourceType = SourceType.Spotify,
            Title = e.Title ?? ExtractIdFromUri(e.Uri),
            ImageUrl = e.ImageUrl,
            AddedAt = e.AddedAt?.ToUnixTimeSeconds() ?? 0,
            UpdatedAt = e.UpdatedAt.ToUnixTimeSeconds()
        }).ToList();
    }

    /// <inheritdoc/>
    public async Task<bool> IsTrackLikedAsync(string trackUri, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackUri);
        return await _database.IsInSpotifyLibraryAsync(trackUri, ct);
    }

    /// <inheritdoc/>
    public async Task<bool> IsAlbumSavedAsync(string albumUri, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(albumUri);
        return await _database.IsInSpotifyLibraryAsync(albumUri, ct);
    }

    #endregion

    #region Write Operations

    /// <inheritdoc/>
    public Task<bool> SaveTrackAsync(string trackUri, CancellationToken ct = default)
        => SaveItemAsync(trackUri, CollectionSet, SpotifyLibraryItemType.Track, "track", ct);

    /// <inheritdoc/>
    public Task<bool> RemoveTrackAsync(string trackUri, CancellationToken ct = default)
        => RemoveItemAsync(trackUri, CollectionSet, SpotifyLibraryItemType.Track, "track", ct);

    /// <inheritdoc/>
    public Task<bool> SaveAlbumAsync(string albumUri, CancellationToken ct = default)
        => SaveItemAsync(albumUri, CollectionSet, SpotifyLibraryItemType.Album, "album", ct);

    /// <inheritdoc/>
    public Task<bool> RemoveAlbumAsync(string albumUri, CancellationToken ct = default)
        => RemoveItemAsync(albumUri, CollectionSet, SpotifyLibraryItemType.Album, "album", ct);

    /// <inheritdoc/>
    public Task<bool> FollowArtistAsync(string artistUri, CancellationToken ct = default)
        => SaveItemAsync(artistUri, ArtistsSet, SpotifyLibraryItemType.Artist, "artist", ct);

    /// <inheritdoc/>
    public Task<bool> UnfollowArtistAsync(string artistUri, CancellationToken ct = default)
        => RemoveItemAsync(artistUri, ArtistsSet, SpotifyLibraryItemType.Artist, "artist", ct);

    /// <summary>
    /// Subscribes to a podcast show.
    /// </summary>
    public Task<bool> SubscribeShowAsync(string showUri, CancellationToken ct = default)
        => SaveItemAsync(showUri, ShowsSet, SpotifyLibraryItemType.Show, "show", ct);

    /// <summary>
    /// Unsubscribes from a podcast show.
    /// </summary>
    public Task<bool> UnsubscribeShowAsync(string showUri, CancellationToken ct = default)
        => RemoveItemAsync(showUri, ShowsSet, SpotifyLibraryItemType.Show, "show", ct);

    /// <inheritdoc/>
    public async Task<bool> PinToSidebarAsync(string uri, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);

        var addedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // 1. Optimistic local write so the sidebar reflects the pin immediately.
        try
        {
            await _database.AddToSpotifyLibraryAsync(uri, SpotifyLibraryItemType.YlPin, addedAt, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Local pin write failed for {Uri}", uri);
            return false;
        }

        // 2. Resolve entity metadata so the INNER-JOIN read in
        //    GetSpotifyLibraryItemsAsync returns the new row with a real
        //    title + cover (not a placeholder). Best-effort — a metadata
        //    fetch failure shouldn't fail the pin itself.
        try
        {
            await ResolvePinnedEntityMetadataAsync(uri, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Pin metadata fetch failed for {Uri} — row will show placeholder until next sync", uri);
        }

        // 3. Server write. If it fails, roll back the local DB row so the
        //    sidebar doesn't keep showing a pin that didn't land server-side.
        //    Caller (UI) surfaces a toast on the false return.
        try
        {
            var username = GetUsername();
            var item = new CollectionItem
            {
                Uri = uri,
                AddedAt = (int)addedAt,
                IsRemoved = false
            };
            await _spClient.WriteCollectionAsync(username, YlPinSet, new[] { item }, ct);
            _logger?.LogInformation("Pin: {Uri}", uri);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Server pin failed — rolling back local write for {Uri}", uri);
            try
            {
                await _database.RemoveFromSpotifyLibraryAsync(uri, SpotifyLibraryItemType.YlPin, ct);
            }
            catch (Exception rbEx)
            {
                _logger?.LogError(rbEx, "Pin rollback failed for {Uri} — local DB now inconsistent with server", uri);
            }
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> UnpinFromSidebarAsync(string uri, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);

        // 1. Optimistic local delete (type-scoped so a Liked Song that's also
        //    pinned keeps its Track row).
        try
        {
            await _database.RemoveFromSpotifyLibraryAsync(uri, SpotifyLibraryItemType.YlPin, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Local unpin write failed for {Uri}", uri);
            return false;
        }

        // 2. Server write. On failure, re-add the row so the sidebar restores
        //    the pin. The exact AddedAt is lost on rollback (would need a
        //    type-scoped library read API to preserve it) — using "now" means
        //    the restored pin may briefly jump to the top of the sort until the
        //    next dealer push reconciles, which is acceptable for an error case.
        try
        {
            var username = GetUsername();
            var item = new CollectionItem
            {
                Uri = uri,
                AddedAt = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                IsRemoved = true
            };
            await _spClient.WriteCollectionAsync(username, YlPinSet, new[] { item }, ct);
            _logger?.LogInformation("Unpin: {Uri}", uri);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Server unpin failed — restoring local row for {Uri}", uri);
            try
            {
                await _database.AddToSpotifyLibraryAsync(
                    uri,
                    SpotifyLibraryItemType.YlPin,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ct);
            }
            catch (Exception rbEx)
            {
                _logger?.LogError(rbEx, "Unpin rollback failed for {Uri} — local DB now inconsistent with server", uri);
            }
            return false;
        }
    }

    private async Task ResolvePinnedEntityMetadataAsync(string uri, CancellationToken ct)
    {
        if (uri.StartsWith("spotify:playlist:", StringComparison.Ordinal))
        {
            await ResolvePinnedPlaylistEntityAsync(uri, ct);
        }
        else if (uri.StartsWith(TrackUriPrefix, StringComparison.Ordinal))
        {
            await FetchAndStoreMetadataAsync([uri], ExtensionKind.TrackV4, "pin", ct);
        }
        else if (uri.StartsWith(AlbumUriPrefix, StringComparison.Ordinal))
        {
            await FetchAndStoreMetadataAsync([uri], ExtensionKind.AlbumV4, "pin", ct);
        }
        else if (uri.StartsWith(ArtistUriPrefix, StringComparison.Ordinal))
        {
            await FetchAndStoreMetadataAsync([uri], ExtensionKind.ArtistV4, "pin", ct);
        }
        else if (uri.StartsWith("spotify:show:", StringComparison.Ordinal))
        {
            await FetchAndStoreMetadataAsync([uri], ExtensionKind.ShowV4, "pin", ct);
        }
        else if (uri.StartsWith("spotify:episode:", StringComparison.Ordinal))
        {
            await FetchAndStoreMetadataAsync([uri], ExtensionKind.EpisodeV4, "pin", ct);
        }
        else
        {
            // Pseudo-URIs (spotify:collection, spotify:collection:your-episodes,
            // spotify:user:*:collection) — seat a placeholder so the join returns
            // the row. The UI layer in LibraryDataService.GetPinnedItemsAsync
            // synthesizes the real title.
            await _database.UpsertEntityAsync(
                uri: uri,
                entityType: EntityType.Playlist,
                title: uri,
                cancellationToken: ct);
        }
    }

    private async Task<bool> SaveItemAsync(
        string uri,
        string set,
        SpotifyLibraryItemType itemType,
        string displayName,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);

        try
        {
            // 1. Optimistically update local database (instant UI feedback)
            var addedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await _database.AddToSpotifyLibraryAsync(uri, itemType, addedAt, ct);

            // 2. Ensure entity metadata exists so INNER JOIN queries find this item
            var extensionKind = itemType switch
            {
                SpotifyLibraryItemType.Track => ExtensionKind.TrackV4,
                SpotifyLibraryItemType.Album => ExtensionKind.AlbumV4,
                SpotifyLibraryItemType.Artist => ExtensionKind.ArtistV4,
                SpotifyLibraryItemType.Show => ExtensionKind.ShowV4,
                _ => (ExtensionKind?)null
            };
            if (extensionKind.HasValue)
            {
                try
                {
                    await FetchAndStoreMetadataAsync([uri], extensionKind.Value, displayName, ct);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to fetch metadata for {Uri}, library entry still saved", uri);
                }
            }

            // 3. Enqueue for background API sync (no rollback — local state is source of truth)
            await _database.EnqueueLibraryOpAsync(uri, itemType, LibraryOutboxOperation.Save, ct);

            _logger?.LogInformation("{ItemType} saved locally + enqueued: {Uri}", displayName, uri);

            // 4. Try immediate sync (fire-and-forget, don't block the caller)
            _ = ProcessOutboxAsync();

            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save {ItemType}: {Uri}", displayName, uri);
            return false;
        }
    }

    private async Task<bool> RemoveItemAsync(
        string uri,
        string set,
        SpotifyLibraryItemType itemType,
        string displayName,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);

        try
        {
            // 1. Optimistically update local database (instant UI feedback)
            await _database.RemoveFromSpotifyLibraryAsync(uri, ct);

            // 2. Enqueue for background API sync
            await _database.EnqueueLibraryOpAsync(uri, itemType, LibraryOutboxOperation.Remove, ct);

            _logger?.LogInformation("{ItemType} removed locally + enqueued: {Uri}", displayName, uri);

            // 3. Try immediate sync
            _ = ProcessOutboxAsync();

            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to remove {ItemType}: {Uri}", displayName, uri);
            return false;
        }
    }

    /// <summary>
    /// Processes pending outbox operations, syncing local changes to Spotify API.
    /// Safe to call multiple times — operations are idempotent.
    /// </summary>
    public async Task<int> ProcessOutboxAsync()
    {
        var failedCount = 0;
        try
        {
            var username = GetUsername();
            var ops = await _database.DequeueLibraryOpsAsync(50);
            if (ops.Count == 0) return 0;

            foreach (var op in ops)
            {
                try
                {
                    // Drop permanently failed entries (bad data, invalid URIs, etc.)
                    if (op.RetryCount >= 10)
                    {
                        _logger?.LogWarning("Outbox op exceeded max retries, dropping: {Uri}", op.ItemUri);
                        await _database.CompleteLibraryOpAsync(op.Id);
                        failedCount++;
                        continue;
                    }

                    // Fast-fail malformed URIs that could only fail at the
                    // server. Anything that's not a Spotify URI shouldn't be
                    // here — it was queued by mistake (e.g. a wavee:local:*
                    // URI from before the like-routing fix that wrongly
                    // prefixed "spotify:track:" onto a non-Spotify trackId).
                    // Dropping immediately prevents a retry loop.
                    if (string.IsNullOrEmpty(op.ItemUri) || !op.ItemUri.StartsWith("spotify:", StringComparison.Ordinal))
                    {
                        _logger?.LogWarning("Outbox op has non-Spotify URI, dropping: {Uri}", op.ItemUri);
                        await _database.CompleteLibraryOpAsync(op.Id);
                        failedCount++;
                        continue;
                    }
                    if (op.ItemUri.IndexOf(":wavee:", StringComparison.Ordinal) > 0)
                    {
                        _logger?.LogWarning("Outbox op contains nested wavee URI (legacy bug), dropping: {Uri}", op.ItemUri);
                        await _database.CompleteLibraryOpAsync(op.Id);
                        failedCount++;
                        continue;
                    }

                    var set = GetSetForItemType(op.ItemType);
                    var item = new CollectionItem
                    {
                        Uri = op.ItemUri,
                        AddedAt = (int)op.CreatedAt,
                        IsRemoved = op.Operation == LibraryOutboxOperation.Remove
                    };

                    await _spClient.WriteCollectionAsync(username, set, new[] { item });
                    await _database.CompleteLibraryOpAsync(op.Id);

                    _logger?.LogDebug("Outbox synced: {Op} {Uri}", op.Operation, op.ItemUri);
                }
                catch (Exception ex)
                {
                    failedCount++;
                    _logger?.LogWarning(ex, "Outbox op failed (retry {Count}): {Uri}", op.RetryCount + 1, op.ItemUri);
                    await _database.FailLibraryOpAsync(op.Id, ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Outbox processing failed");
            failedCount++;
        }

        return failedCount;
    }

    private static string GetSetForItemType(SpotifyLibraryItemType itemType) => itemType switch
    {
        SpotifyLibraryItemType.Track => CollectionSet,
        SpotifyLibraryItemType.Album => CollectionSet,
        SpotifyLibraryItemType.Artist => ArtistsSet,
        SpotifyLibraryItemType.Show => ShowsSet,
        SpotifyLibraryItemType.YlPin => YlPinSet,
        _ => CollectionSet
    };

    #endregion

    #region State

    /// <inheritdoc/>
    public async Task<SpotifySyncState> GetSyncStateAsync(CancellationToken ct = default)
    {
        // Tracks and albums share the "collection" set but use different revision keys
        var tracksState = await _database.GetSyncStateAsync($"{CollectionSet}:{SpotifyLibraryItemType.Track}", ct);
        var tracksCount = await _database.GetSpotifyLibraryCountAsync(SpotifyLibraryItemType.Track, ct);

        var albumsState = await _database.GetSyncStateAsync($"{CollectionSet}:{SpotifyLibraryItemType.Album}", ct);
        var albumsCount = await _database.GetSpotifyLibraryCountAsync(SpotifyLibraryItemType.Album, ct);

        var artistsState = await _database.GetSyncStateAsync(ArtistsSet, ct);
        var artistsCount = await _database.GetSpotifyLibraryCountAsync(SpotifyLibraryItemType.Artist, ct);

        var showsState = await _database.GetSyncStateAsync(ShowsSet, ct);
        var showsCount = await _database.GetSpotifyLibraryCountAsync(SpotifyLibraryItemType.Show, ct);

        var bansState = await _database.GetSyncStateAsync($"{BanSet}:{SpotifyLibraryItemType.Ban}", ct);
        var bansCount = await _database.GetSpotifyLibraryCountAsync(SpotifyLibraryItemType.Ban, ct);

        var artistBansState = await _database.GetSyncStateAsync(ArtistBanSet, ct);
        var artistBansCount = await _database.GetSpotifyLibraryCountAsync(SpotifyLibraryItemType.ArtistBan, ct);

        var listenLaterState = await _database.GetSyncStateAsync(ListenLaterSet, ct);
        var listenLaterCount = await _database.GetSpotifyLibraryCountAsync(SpotifyLibraryItemType.ListenLater, ct);

        var ylPinsState = await _database.GetSyncStateAsync(YlPinSet, ct);
        var ylPinsCount = await _database.GetSpotifyLibraryCountAsync(SpotifyLibraryItemType.YlPin, ct);

        var enhancedState = await _database.GetSyncStateAsync(EnhancedSet, ct);
        var enhancedCount = await _database.GetSpotifyLibraryCountAsync(SpotifyLibraryItemType.Enhanced, ct);

        return new SpotifySyncState
        {
            Tracks = new CollectionSyncState
            {
                Revision = tracksState?.Revision,
                ItemCount = tracksCount,
                LastSyncAt = tracksState?.LastSyncAt
            },
            Albums = new CollectionSyncState
            {
                Revision = albumsState?.Revision,
                ItemCount = albumsCount,
                LastSyncAt = albumsState?.LastSyncAt
            },
            Artists = new CollectionSyncState
            {
                Revision = artistsState?.Revision,
                ItemCount = artistsCount,
                LastSyncAt = artistsState?.LastSyncAt
            },
            Shows = new CollectionSyncState
            {
                Revision = showsState?.Revision,
                ItemCount = showsCount,
                LastSyncAt = showsState?.LastSyncAt
            },
            Bans = new CollectionSyncState
            {
                Revision = bansState?.Revision,
                ItemCount = bansCount,
                LastSyncAt = bansState?.LastSyncAt
            },
            ArtistBans = new CollectionSyncState
            {
                Revision = artistBansState?.Revision,
                ItemCount = artistBansCount,
                LastSyncAt = artistBansState?.LastSyncAt
            },
            ListenLater = new CollectionSyncState
            {
                Revision = listenLaterState?.Revision,
                ItemCount = listenLaterCount,
                LastSyncAt = listenLaterState?.LastSyncAt
            },
            YlPins = new CollectionSyncState
            {
                Revision = ylPinsState?.Revision,
                ItemCount = ylPinsCount,
                LastSyncAt = ylPinsState?.LastSyncAt
            },
            Enhanced = new CollectionSyncState
            {
                Revision = enhancedState?.Revision,
                ItemCount = enhancedCount,
                LastSyncAt = enhancedState?.LastSyncAt
            }
        };
    }

    #endregion

    #region Real-time Updates

    private void OnLibraryChange(LibraryChangeEvent changeEvent)
    {
        try
        {
            var newRevB64 = changeEvent.NewRevision is { Length: > 0 } nr
                ? Convert.ToBase64String(nr)
                : "<none>";
            _logger?.LogInformation(
                "[rootlist] OnLibraryChange set={Set} isRootlist={Root} playlistUri={Pu} items={N} newRev={Rev}",
                changeEvent.Set, changeEvent.IsRootlist, changeEvent.PlaylistUri ?? "<none>",
                changeEvent.Items.Count, newRevB64);

            // Forward the event
            _libraryChanged.OnNext(changeEvent);

            // Handle playlist changes specially - refetch metadata
            if (changeEvent.Set == "playlists" && !string.IsNullOrEmpty(changeEvent.PlaylistUri))
            {
                _ = Task.Run(async () => await OnPlaylistChangedAsync(changeEvent));
                return;
            }

            // Process collection changes in background (don't block the observable)
            _ = Task.Run(async () =>
            {
                try
                {
                    foreach (var item in changeEvent.Items)
                    {
                        // Verification log: confirm the binary PubSubUpdate path
                        // actually carries spotify:track:... URIs (and not raw
                        // GIDs that would silently no-op the SQLite write below).
                        // Strip if logs come back consistently populated.
                        _logger?.LogDebug("Library change item: uri={Uri} removed={IsRemoved}",
                            item.ItemUri, item.IsRemoved);

                        // Determine item type from URI prefix for "collection" set,
                        // otherwise use set name
                        var itemType = GetItemTypeFromUri(item.ItemUri, changeEvent.Set);

                        if (item.IsRemoved)
                        {
                            await _database.RemoveFromSpotifyLibraryAsync(item.ItemUri);
                        }
                        else
                        {
                            var addedAt = item.AddedAt?.ToUnixTimeSeconds() ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                            await _database.AddToSpotifyLibraryAsync(item.ItemUri, itemType, addedAt);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to process library change");
                }
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error handling library change event");
        }
    }

    private async Task OnPlaylistChangedAsync(LibraryChangeEvent changeEvent)
    {
        try
        {
            _logger?.LogInformation(
                "[playlist-diff] OnPlaylistChangedAsync {Uri} reason=dealer-push",
                changeEvent.PlaylistUri);

            // Refetch playlist metadata from Spotify
            var playlist = await _spClient.GetPlaylistAsync(
                changeEvent.PlaylistUri!,
                decorate: new[] { "revision", "attributes", "length", "owner" });

            var refetchedRevB64 = playlist.Revision?.Length > 0
                ? Convert.ToBase64String(playlist.Revision.ToByteArray())
                : "<none>";
            _logger?.LogInformation(
                "[playlist-diff] OnPlaylistChangedAsync re-fetched {Uri} rev={Rev} length={Length}",
                changeEvent.PlaylistUri, refetchedRevB64, playlist.Length);

            var username = GetUsername();

            // Extract image URL from picture sizes
            string? imageUrl = playlist.Attributes?.PictureSize
                .FirstOrDefault(p => p.TargetName == "default")?.Url
                ?? playlist.Attributes?.PictureSize.FirstOrDefault()?.Url;

            // Get existing playlist to preserve folder path (set during full sync)
            var existing = await _database.GetPlaylistAsync(changeEvent.PlaylistUri!);

            var spotifyPlaylist = SpotifyPlaylist.Create(
                uri: changeEvent.PlaylistUri!,
                name: playlist.Attributes?.Name ?? "Unknown",
                ownerId: playlist.OwnerUsername,
                description: playlist.Attributes?.Description,
                imageUrl: imageUrl,
                trackCount: playlist.Length,
                isPublic: true,
                isCollaborative: playlist.Attributes?.Collaborative ?? false,
                isOwned: playlist.OwnerUsername == username,
                revision: playlist.Revision?.Length > 0
                    ? Convert.ToBase64String(playlist.Revision.ToByteArray())
                    : null,
                folderPath: existing?.FolderPath
            );

            await _database.UpsertPlaylistAsync(spotifyPlaylist);

            _logger?.LogInformation(
                "[playlist-diff] OnPlaylistChangedAsync db.UpsertPlaylist {Uri} name=\"{Name}\" tracks={TrackCount}",
                changeEvent.PlaylistUri, spotifyPlaylist.Name, spotifyPlaylist.TrackCount);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[playlist-diff] OnPlaylistChangedAsync FAILED {Uri}", changeEvent.PlaylistUri);
        }
    }

    private static SpotifyLibraryItemType GetItemTypeFromUri(string uri, string set)
    {
        // The "collection" set contains both tracks and albums - detect from URI
        if (set == CollectionSet)
        {
            if (uri.StartsWith(TrackUriPrefix, StringComparison.Ordinal))
                return SpotifyLibraryItemType.Track;
            if (uri.StartsWith(AlbumUriPrefix, StringComparison.Ordinal))
                return SpotifyLibraryItemType.Album;
        }

        // Otherwise use set name
        return set switch
        {
            "artist" => SpotifyLibraryItemType.Artist,
            "show" => SpotifyLibraryItemType.Show,
            "listenlater" => SpotifyLibraryItemType.ListenLater,
            "ylpin" => SpotifyLibraryItemType.YlPin,
            "enhanced" => SpotifyLibraryItemType.Enhanced,
            _ => SpotifyLibraryItemType.Track // Default to track
        };
    }

    #endregion

    #region Helpers

    private static string ExtractIdFromUri(string uri)
    {
        // "spotify:track:xxx" -> "xxx"
        var parts = uri.Split(':');
        return parts.Length >= 3 ? parts[2] : uri;
    }

    /// <summary>
    /// Fetches metadata for mixed-type collections (YlPin, ListenLater) by grouping URIs by type.
    /// </summary>
    private async Task FetchMixedTypeMetadataAsync(
        IReadOnlyList<string> uris,
        string displayName,
        CancellationToken ct)
    {
        if (_metadataClient == null || uris.Count == 0)
            return;

        // Group URIs by type
        var tracks = uris.Where(u => u.StartsWith(TrackUriPrefix)).ToList();
        var albums = uris.Where(u => u.StartsWith(AlbumUriPrefix)).ToList();
        var artists = uris.Where(u => u.StartsWith(ArtistUriPrefix)).ToList();
        var playlists = uris.Where(u => u.StartsWith("spotify:playlist:")).ToList();
        var shows = uris.Where(u => u.StartsWith("spotify:show:")).ToList();
        var episodes = uris.Where(u => u.StartsWith("spotify:episode:")).ToList();
        var users = uris.Where(u => u.StartsWith("spotify:user:")).ToList();

        _logger?.LogDebug("Mixed-type metadata fetch: {Tracks} tracks, {Albums} albums, {Artists} artists, {Playlists} playlists, {Shows} shows, {Episodes} episodes, {Users} users",
            tracks.Count, albums.Count, artists.Count, playlists.Count, shows.Count, episodes.Count, users.Count);

        // Fetch each type with extended metadata
        if (tracks.Count > 0)
            await FetchAndStoreMetadataAsync(tracks, ExtensionKind.TrackV4, displayName, ct);
        if (albums.Count > 0)
            await FetchAndStoreMetadataAsync(albums, ExtensionKind.AlbumV4, displayName, ct);
        if (artists.Count > 0)
            await FetchAndStoreMetadataAsync(artists, ExtensionKind.ArtistV4, displayName, ct);
        if (shows.Count > 0)
            await FetchAndStoreMetadataAsync(shows, ExtensionKind.ShowV4, displayName, ct);
        if (episodes.Count > 0)
            await FetchAndStoreMetadataAsync(episodes, ExtensionKind.EpisodeV4, displayName, ct);

        // Playlists don't have an ExtensionKind-backed bulk fetch, so resolve
        // each one against SpClient.GetPlaylistAsync. Without this, pinned
        // playlists the user doesn't *follow* (e.g. editorial 37i9... playlists)
        // would never see their name+cover written into the entities table —
        // the sidebar would render the raw URI string instead of the title.
        // Per-playlist try/catch so one missing playlist doesn't kill the sync.
        foreach (var playlistUri in playlists)
        {
            await ResolvePinnedPlaylistEntityAsync(playlistUri, ct);
        }

        foreach (var userUri in users)
        {
            await _database.UpsertEntityAsync(
                uri: userUri,
                entityType: EntityType.User,
                title: userUri,
                cancellationToken: ct);
        }

        // Handle any unknown URI types (collections, folders, stations, etc.)
        var knownPrefixes = new[] { TrackUriPrefix, AlbumUriPrefix, ArtistUriPrefix,
            "spotify:playlist:", "spotify:show:", "spotify:episode:", "spotify:user:" };
        var unknown = uris.Where(u => !knownPrefixes.Any(p => u.StartsWith(p))).ToList();

        if (unknown.Count > 0)
        {
            _logger?.LogDebug("Creating placeholder entities for {Count} unknown URI types: {Uris}",
                unknown.Count, string.Join(", ", unknown.Take(5)));

            foreach (var uri in unknown)
            {
                // Determine entity type from URI if possible, default to Track
                var entityType = uri switch
                {
                    var u when u.StartsWith("spotify:collection:") => EntityType.Playlist,
                    var u when u.StartsWith("spotify:folder:") => EntityType.Playlist,
                    _ => EntityType.Track
                };

                await _database.UpsertEntityAsync(
                    uri: uri,
                    entityType: entityType,
                    title: uri,
                    cancellationToken: ct);
            }
        }
    }

    /// <summary>
    /// Fetches and stores metadata in batches using ExtendedMetadataClient.
    /// The client automatically stores entities in the unified database.
    /// </summary>
    private async Task FetchAndStoreMetadataAsync(
        IReadOnlyList<string> uris,
        ExtensionKind extensionKind,
        string displayName,
        CancellationToken ct)
    {
        if (_metadataClient == null || uris.Count == 0)
        {
            _logger?.LogDebug("Skipping metadata fetch: metadataClient={HasClient}, count={Count}",
                _metadataClient != null, uris.Count);
            return;
        }

        const int batchSize = 100;  // Spotify API limit per request
        var totalBatches = (uris.Count + batchSize - 1) / batchSize;

        _logger?.LogInformation("Fetching {Kind} metadata for {Count} items in {Batches} batches",
            extensionKind, uris.Count, totalBatches);

        for (int i = 0; i < uris.Count; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();

            var batch = uris.Skip(i).Take(batchSize).ToList();
            var batchNumber = (i / batchSize) + 1;

            _progressSubject.OnNext(new SyncProgress(
                displayName,
                i,
                uris.Count,
                $"Fetching metadata batch {batchNumber}/{totalBatches}..."));

            try
            {
                // Create request tuples for batch fetch
                var requests = batch.Select(uri => (
                    EntityUri: uri,
                    Extensions: new[] { extensionKind }.AsEnumerable()
                ));

                // GetBatchedExtensionsAsync automatically:
                // 1. Checks cache (skips already cached)
                // 2. Fetches from API
                // 3. Stores in unified entities table in MetadataDatabase
                await _metadataClient.GetBatchedExtensionsAsync(requests, ct);

                _logger?.LogDebug("Fetched metadata batch {Batch}/{Total}: {Count} items",
                    batchNumber, totalBatches, batch.Count);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to fetch metadata batch {Batch}, continuing...", batchNumber);
                // Continue with other batches even if one fails
            }
        }
    }

    #endregion

    #region Disposal

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _changeSubscription?.Dispose();
        _progressSubject.OnCompleted();
        _progressSubject.Dispose();
        _libraryChanged.OnCompleted();
        _libraryChanged.Dispose();

        await Task.CompletedTask;
    }

    #endregion
}
