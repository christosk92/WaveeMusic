using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using Wavee.Connect;
using Wavee.Core.Http;
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
        ILogger? logger = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _spClient = spClient ?? throw new ArgumentNullException(nameof(spClient));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _libraryChangeManager = libraryChangeManager;
        _metadataClient = metadataClient;
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
    public async Task SyncAllAsync(CancellationToken ct = default)
    {
        await SyncTracksAsync(ct);
        await SyncAlbumsAsync(ct);
        await SyncArtistsAsync(ct);
        await SyncShowsAsync(ct);
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
    /// Syncs Your Library pinned items from Spotify.
    /// </summary>
    public Task SyncYlPinsAsync(CancellationToken ct = default)
        => SyncCollectionAsync(YlPinSet, SpotifyLibraryItemType.YlPin, "pins", null, ct);

    /// <summary>
    /// Syncs enhanced playlist tracks from Spotify.
    /// </summary>
    public Task SyncEnhancedAsync(CancellationToken ct = default)
        => SyncCollectionAsync(EnhancedSet, SpotifyLibraryItemType.Enhanced, "enhanced", TrackUriPrefix, ct);

    /// <inheritdoc/>
    public async Task SyncPlaylistsAsync(CancellationToken ct = default)
    {
        var username = GetUsername();
        var rootlistUri = $"spotify:user:{username}:rootlist";

        _logger?.LogInformation("Starting playlist sync for {Username}", username);
        _progressSubject.OnNext(new SyncProgress("playlists", 0, 0, "Fetching rootlist..."));

        try
        {
            // 1. Fetch rootlist (contains all playlists)
            var rootlist = await _spClient.GetPlaylistAsync(
                rootlistUri,
                decorate: new[] { "revision", "attributes", "length" },
                cancellationToken: ct);

            // 2. Parse folder structure and extract playlist URIs
            // Folder markers: "spotify:start-group:<id>:<name>" and "spotify:end-group:<id>"
            var playlistsWithFolders = new List<(string Uri, string? FolderPath)>();
            var folderStack = new Stack<string>(); // Stack of folder names for current path

            foreach (var item in rootlist.Contents?.Items ?? Enumerable.Empty<Protocol.Playlist.Item>())
            {
                var uri = item.Uri;

                if (uri.StartsWith("spotify:start-group:", StringComparison.Ordinal))
                {
                    // Format: spotify:start-group:<id>:<folder_name>
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
                    // Build current folder path
                    var folderPath = folderStack.Count > 0
                        ? string.Join("/", folderStack.Reverse())
                        : null;
                    playlistsWithFolders.Add((uri, folderPath));
                }
            }

            _logger?.LogDebug("Found {Count} playlists in rootlist", playlistsWithFolders.Count);
            _progressSubject.OnNext(new SyncProgress("playlists", 0, playlistsWithFolders.Count,
                $"Found {playlistsWithFolders.Count} playlists..."));

            // 3. Fetch metadata for each playlist and store
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

                    // Extract image URL from picture sizes if available
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
                        isPublic: true, // Default - no visibility info in response
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

            // 4. Remove playlists no longer in rootlist
            var existing = await _database.GetAllPlaylistsAsync(ct);
            var playlistUriSet = playlistsWithFolders.Select(p => p.Uri).ToHashSet();
            var toRemove = existing.Where(p => !playlistUriSet.Contains(p.Uri)).ToList();

            foreach (var p in toRemove)
            {
                await _database.DeletePlaylistAsync(p.Uri, ct);
                _logger?.LogDebug("Removed stale playlist: {Uri}", p.Uri);
            }

            // 5. Update sync state
            await _database.SetSyncStateAsync(
                "playlists",
                rootlist.Revision?.Length > 0 ? Convert.ToBase64String(rootlist.Revision.ToByteArray()) : null,
                playlistsWithFolders.Count,
                ct);

            _progressSubject.OnNext(new SyncProgress("playlists", playlistsWithFolders.Count, playlistsWithFolders.Count,
                $"Sync complete: {playlistsWithFolders.Count} playlists"));

            _logger?.LogInformation("Playlist sync completed: {Count} playlists", playlistsWithFolders.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Playlist sync failed");
            _progressSubject.OnNext(new SyncProgress("playlists", 0, 0, $"Sync failed: {ex.Message}"));
            throw;
        }
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

    private async Task<bool> SaveItemAsync(
        string uri,
        string set,
        SpotifyLibraryItemType itemType,
        string displayName,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        var username = GetUsername();

        try
        {
            // Optimistically update local database first
            var addedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await _database.AddToSpotifyLibraryAsync(uri, itemType, addedAt, ct);

            // Then update Spotify
            var item = new CollectionItem
            {
                Uri = uri,
                AddedAt = (int)addedAt,
                IsRemoved = false
            };

            await _spClient.WriteCollectionAsync(username, set, new[] { item }, ct);

            _logger?.LogInformation("{ItemType} saved: {Uri}", displayName, uri);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save {ItemType}: {Uri}", displayName, uri);

            // Rollback local change on failure
            await _database.RemoveFromSpotifyLibraryAsync(uri, ct);
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
        var username = GetUsername();

        try
        {
            // Optimistically update local database first
            await _database.RemoveFromSpotifyLibraryAsync(uri, ct);

            // Then update Spotify
            var item = new CollectionItem
            {
                Uri = uri,
                IsRemoved = true
            };

            await _spClient.WriteCollectionAsync(username, set, new[] { item }, ct);

            _logger?.LogInformation("{ItemType} removed: {Uri}", displayName, uri);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to remove {ItemType}: {Uri}", displayName, uri);

            // Rollback: add back to local database on failure
            var addedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await _database.AddToSpotifyLibraryAsync(uri, itemType, addedAt, ct);
            return false;
        }
    }

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
            _logger?.LogDebug("Processing library change: {Set}, {ItemCount} items",
                changeEvent.Set, changeEvent.Items.Count);

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
            _logger?.LogDebug("Real-time playlist update: {Uri}", changeEvent.PlaylistUri);

            // Refetch playlist metadata from Spotify
            var playlist = await _spClient.GetPlaylistAsync(
                changeEvent.PlaylistUri!,
                decorate: new[] { "revision", "attributes", "length", "owner" });

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

            _logger?.LogInformation("Playlist updated via real-time sync: {Name} ({TrackCount} tracks)",
                spotifyPlaylist.Name, spotifyPlaylist.TrackCount);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to sync playlist {Uri} in real-time", changeEvent.PlaylistUri);
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

        // Create minimal entities for types that don't have extended metadata API
        foreach (var playlistUri in playlists)
        {
            await _database.UpsertEntityAsync(
                uri: playlistUri,
                entityType: EntityType.Playlist,
                title: playlistUri, // Will be updated when playlist is synced
                cancellationToken: ct);
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
