using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Connect;
using Wavee.Core.Library.Local;
using Wavee.Core.Library.Spotify;
using Wavee.Core.Storage.Abstractions;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Data.Contexts;

/// <summary>
/// In-memory cache of saved/liked item IDs (tracks, albums, artists).
/// Populated from SQLite on startup, kept in sync via Dealer WebSocket deltas.
/// All lookups are synchronous O(1).
/// </summary>
public sealed class TrackLikeService : ITrackLikeService, IDisposable
{
    private static readonly Dictionary<SavedItemType, (string UriPrefix, SpotifyLibraryItemType DbType, string SetFilter)> TypeMap = new()
    {
        [SavedItemType.Track] = ("spotify:track:", SpotifyLibraryItemType.Track, "collection"),
        [SavedItemType.Album] = ("spotify:album:", SpotifyLibraryItemType.Album, "collection"),
        [SavedItemType.Artist] = ("spotify:artist:", SpotifyLibraryItemType.Artist, "artists"),
        [SavedItemType.Show] = ("spotify:show:", SpotifyLibraryItemType.Show, "shows"),
    };

    private readonly Dictionary<SavedItemType, HashSet<string>> _caches = new()
    {
        [SavedItemType.Track] = new(StringComparer.Ordinal),
        [SavedItemType.Album] = new(StringComparer.Ordinal),
        [SavedItemType.Artist] = new(StringComparer.Ordinal),
        [SavedItemType.Show] = new(StringComparer.Ordinal),
    };

    private readonly IMetadataDatabase _database;
    private readonly ISpotifyLibraryService? _libraryService;
    private readonly ILocalLikeService? _localLikeService;
    private readonly ILogger? _logger;
    private readonly CompositeDisposable _disposables = new();
    private bool _initialized;

    public event Action? SaveStateChanged;

    public TrackLikeService(
        IMetadataDatabase database,
        ISpotifyLibraryService? libraryService = null,
        ILocalLikeService? localLikeService = null,
        ILogger<TrackLikeService>? logger = null)
    {
        _database = database;
        _libraryService = libraryService;
        _localLikeService = localLikeService;
        _logger = logger;
    }

    public bool IsSaved(SavedItemType type, string idOrUri)
    {
        var (prefix, _, _) = TypeMap[type];
        var bareId = ExtractBareId(idOrUri, prefix);
        return bareId != null && _caches[type].Contains(bareId);
    }

    public int GetCount(SavedItemType type) =>
        _caches[type].Count;

    public IReadOnlyCollection<string> GetSavedIds(SavedItemType type) =>
        _caches[type].ToList();

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;

        foreach (var (type, (_, dbType, _)) in TypeMap)
        {
            try
            {
                await LoadItemsAsync(type, dbType).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to load {Type} saved items", type);
            }
        }

        // Seed local-track likes into the same Track cache. ExtractBareId
        // returns the URI verbatim when no Spotify prefix matches, so a
        // wavee:local:track:* lookup hits the cache directly without extra
        // branching in IsSaved.
        if (_localLikeService is not null)
        {
            try
            {
                var localLiked = await _localLikeService.GetLikedTrackUrisAsync().ConfigureAwait(false);
                foreach (var uri in localLiked)
                    _caches[SavedItemType.Track].Add(uri);
                // Keep the cache in sync with external mutations
                // (e.g. heart toggled from LocalLibraryPage).
                _disposables.Add(_localLikeService.Changes.Subscribe(change =>
                {
                    if (change.Liked) _caches[SavedItemType.Track].Add(change.TrackUri);
                    else _caches[SavedItemType.Track].Remove(change.TrackUri);
                    SaveStateChanged?.Invoke();
                }));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Local-like cache seed failed");
            }
        }

        _logger?.LogInformation(
            "LibrarySaveService initialized: {Tracks} tracks, {Albums} albums, {Artists} artists, {Shows} shows",
            _caches[SavedItemType.Track].Count,
            _caches[SavedItemType.Album].Count,
            _caches[SavedItemType.Artist].Count,
            _caches[SavedItemType.Show].Count);

        SubscribeToLibraryChanges();
    }

    public void ClearCache()
    {
        foreach (var cache in _caches.Values)
            cache.Clear();

        // Reset so a future InitializeAsync (after next sign-in) actually runs.
        _initialized = false;
        _logger?.LogInformation("TrackLikeService cache cleared (sign-out)");
        SaveStateChanged?.Invoke();
    }

    public async Task ReloadCacheAsync()
    {
        foreach (var (type, (_, dbType, _)) in TypeMap)
        {
            _caches[type].Clear();
            await LoadItemsAsync(type, dbType).ConfigureAwait(false);
        }

        _logger?.LogInformation(
            "Cache reloaded: {Tracks} tracks, {Albums} albums, {Artists} artists, {Shows} shows",
            _caches[SavedItemType.Track].Count,
            _caches[SavedItemType.Album].Count,
            _caches[SavedItemType.Artist].Count,
            _caches[SavedItemType.Show].Count);

        SaveStateChanged?.Invoke();
    }

    public void ToggleSave(SavedItemType type, string itemUri, bool currentlySaved)
    {
        // Local-track likes never round-trip through Spotify's collection
        // API — they're a separate state column on the local entity row.
        // Without this branch, a wavee:local:track:* URI would get queued to
        // SpotifyLibraryService's Outbox and rejected with "Invalid write
        // request" on every retry. The local-like service writes
        // entities.is_locally_liked and is the source of truth for the heart
        // state on local cards.
        if (type == SavedItemType.Track
            && _localLikeService is not null
            && LocalUri.IsTrack(itemUri))
        {
            ToggleLocalTrackLike(itemUri, currentlySaved);
            return;
        }

        var (prefix, _, _) = TypeMap[type];
        var bareId = ExtractBareId(itemUri, prefix);
        if (bareId == null)
        {
            _logger?.LogWarning("ToggleSave: could not extract bare ID from {Uri}", itemUri);
            return;
        }

        var cache = _caches[type];

        _logger?.LogInformation("ToggleSave: type={Type}, uri={Uri}, bareId={BareId}, currentlySaved={Saved}, cacheCount={Count}",
            type, itemUri, bareId, currentlySaved, cache.Count);

        // 1. Update in-memory cache immediately (instant UI feedback)
        if (currentlySaved)
            cache.Remove(bareId);
        else
            cache.Add(bareId);

        _logger?.LogDebug("ToggleSave: cache updated, new IsSaved={IsSaved}", cache.Contains(bareId));

        SaveStateChanged?.Invoke();

        // 2. Persist to DB + API (fire-and-forget)
        _ = Task.Run(async () =>
        {
            try
            {
                if (_libraryService != null)
                {
                    _logger?.LogInformation("ToggleSave: using ISpotifyLibraryService (outbox path)");
                    var success = (type, currentlySaved) switch
                    {
                        (SavedItemType.Track, true) => await _libraryService.RemoveTrackAsync(itemUri).ConfigureAwait(false),
                        (SavedItemType.Track, false) => await _libraryService.SaveTrackAsync(itemUri).ConfigureAwait(false),
                        (SavedItemType.Album, true) => await _libraryService.RemoveAlbumAsync(itemUri).ConfigureAwait(false),
                        (SavedItemType.Album, false) => await _libraryService.SaveAlbumAsync(itemUri).ConfigureAwait(false),
                        (SavedItemType.Artist, true) => await _libraryService.UnfollowArtistAsync(itemUri).ConfigureAwait(false),
                        (SavedItemType.Artist, false) => await _libraryService.FollowArtistAsync(itemUri).ConfigureAwait(false),
                        (SavedItemType.Show, true) => await _libraryService.UnsubscribeShowAsync(itemUri).ConfigureAwait(false),
                        (SavedItemType.Show, false) => await _libraryService.SubscribeShowAsync(itemUri).ConfigureAwait(false),
                        _ => false
                    };
                    _logger?.LogInformation("ToggleSave: library service returned success={Success}", success);

                    if (success)
                    {
                        // DB write complete — notify again so list views can reload with data available
                        SaveStateChanged?.Invoke();
                    }
                    else
                    {
                        _logger?.LogWarning("ToggleSave: API call failed, reverting cache for {Uri}", itemUri);
                        if (currentlySaved)
                            cache.Add(bareId);
                        else
                            cache.Remove(bareId);
                        SaveStateChanged?.Invoke();
                    }
                }
                else
                {
                    _logger?.LogWarning("ToggleSave: ISpotifyLibraryService not in DI — writing directly to DB");
                    var (_, dbType, _) = TypeMap[type];
                    if (currentlySaved)
                    {
                        await _database.RemoveFromSpotifyLibraryAsync(itemUri).ConfigureAwait(false);
                        _logger?.LogInformation("ToggleSave: removed {Uri} from DB", itemUri);
                    }
                    else
                    {
                        await _database.AddToSpotifyLibraryAsync(itemUri, dbType,
                            DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ConfigureAwait(false);
                        _logger?.LogInformation("ToggleSave: added {Uri} to DB", itemUri);
                    }

                    // DB write complete — notify again so list views can reload
                    SaveStateChanged?.Invoke();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to persist save toggle for {Uri}", itemUri);
                // Rollback in-memory state
                if (currentlySaved)
                    cache.Add(bareId);
                else
                    cache.Remove(bareId);
                SaveStateChanged?.Invoke();
            }
        });
    }

    private async Task LoadItemsAsync(SavedItemType type, SpotifyLibraryItemType dbType)
    {
        var cache = _caches[type];
        var (prefix, _, _) = TypeMap[type];
        var offset = 0;
        const int pageSize = 500;

        while (true)
        {
            var entities = await _database.GetSpotifyLibraryItemsAsync(dbType, pageSize, offset).ConfigureAwait(false);
            if (entities.Count == 0) break;

            foreach (var entity in entities)
            {
                var bareId = ExtractBareId(entity.Uri, prefix);
                if (bareId != null)
                    cache.Add(bareId);
            }

            offset += entities.Count;
            if (entities.Count < pageSize) break;
        }
    }

    private void SubscribeToLibraryChanges()
    {
        try
        {
            if (_libraryService is not SpotifyLibraryService libraryService)
            {
                _logger?.LogDebug("SpotifyLibraryService not available — no real-time updates");
                return;
            }

            libraryService.LibraryChanged
                .Subscribe(OnLibraryChanged)
                .DisposeWith(_disposables);

            _logger?.LogDebug("LibrarySaveService subscribed to real-time library changes");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to subscribe to library changes");
        }
    }

    private void OnLibraryChanged(LibraryChangeEvent changeEvent)
    {
        var changed = false;
        foreach (var (type, (prefix, _, setFilter)) in TypeMap)
        {
            if (!changeEvent.Set.Contains(setFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            var cache = _caches[type];
            foreach (var item in changeEvent.Items)
            {
                if (!item.ItemUri.StartsWith(prefix, StringComparison.Ordinal))
                    continue;

                var bareId = ExtractBareId(item.ItemUri, prefix);
                if (bareId == null) continue;

                if (item.IsRemoved)
                    cache.Remove(bareId);
                else
                    cache.Add(bareId);
                changed = true;
            }
        }

        if (changed) SaveStateChanged?.Invoke();
    }

    private static string? ExtractBareId(string uri, string prefix)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        return uri.StartsWith(prefix, StringComparison.Ordinal) ? uri[prefix.Length..] : uri;
    }

    /// <summary>
    /// Mirrors the Spotify ToggleSave flow for a local track URI:
    /// optimistic in-memory cache update + persist via
    /// <see cref="ILocalLikeService"/>. Cache key is the full
    /// <c>wavee:local:track:*</c> URI (ExtractBareId returns the URI verbatim
    /// when no Spotify prefix matches), so <see cref="IsSaved"/> hits without
    /// any extra branching.
    /// </summary>
    private void ToggleLocalTrackLike(string itemUri, bool currentlySaved)
    {
        if (_localLikeService is null) return;
        var cache = _caches[SavedItemType.Track];

        if (currentlySaved) cache.Remove(itemUri);
        else cache.Add(itemUri);

        _logger?.LogInformation("ToggleSave (local): uri={Uri}, wasLiked={WasLiked} → {NewLiked}",
            itemUri, currentlySaved, !currentlySaved);

        SaveStateChanged?.Invoke();

        _ = Task.Run(async () =>
        {
            try
            {
                await _localLikeService.SetLikedAsync(itemUri, !currentlySaved).ConfigureAwait(false);
                SaveStateChanged?.Invoke();
            }
            catch (Exception ex)
            {
                // Revert the optimistic update on failure.
                _logger?.LogWarning(ex, "Local like persist failed for {Uri} — reverting", itemUri);
                if (currentlySaved) cache.Add(itemUri);
                else cache.Remove(itemUri);
                SaveStateChanged?.Invoke();
            }
        });
    }

    public void Dispose()
    {
        _disposables.Dispose();
    }
}
