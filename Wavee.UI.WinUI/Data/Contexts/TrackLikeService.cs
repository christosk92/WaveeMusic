using System;
using System.Collections.Generic;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Threading.Tasks;
using DynamicData;
using Microsoft.Extensions.Logging;
using Wavee.Connect;
using Wavee.Core.Library.Spotify;
using Wavee.Core.Storage.Abstractions;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Data.Contexts;

/// <summary>
/// In-memory reactive cache of saved/liked item IDs (tracks, albums, artists).
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
    };

    private readonly Dictionary<SavedItemType, SourceCache<string, string>> _caches = new()
    {
        [SavedItemType.Track] = new(id => id),
        [SavedItemType.Album] = new(id => id),
        [SavedItemType.Artist] = new(id => id),
    };

    private readonly IMetadataDatabase _database;
    private readonly ISpotifyLibraryService? _libraryService;
    private readonly ILogger? _logger;
    private readonly CompositeDisposable _disposables = new();
    private bool _initialized;

    public event Action? SaveStateChanged;

    public TrackLikeService(
        IMetadataDatabase database,
        ISpotifyLibraryService? libraryService = null,
        ILogger<TrackLikeService>? logger = null)
    {
        _database = database;
        _libraryService = libraryService;
        _logger = logger;
    }

    public bool IsSaved(SavedItemType type, string bareId) =>
        _caches[type].Lookup(bareId).HasValue;

    public int GetCount(SavedItemType type) =>
        _caches[type].Count;

    public IObservable<IChangeSet<string, string>> Connect(SavedItemType type) =>
        _caches[type].Connect();

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;

        foreach (var (type, (_, dbType, _)) in TypeMap)
        {
            try
            {
                await LoadItemsAsync(type, dbType);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to load {Type} saved items", type);
            }
        }

        _logger?.LogInformation(
            "LibrarySaveService initialized: {Tracks} tracks, {Albums} albums, {Artists} artists",
            _caches[SavedItemType.Track].Count,
            _caches[SavedItemType.Album].Count,
            _caches[SavedItemType.Artist].Count);

        SubscribeToLibraryChanges();
    }

    public void ToggleSave(SavedItemType type, string itemUri, bool currentlySaved)
    {
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
            cache.RemoveKey(bareId);
        else
            cache.AddOrUpdate(bareId);

        _logger?.LogDebug("ToggleSave: cache updated, new IsSaved={IsSaved}", cache.Lookup(bareId).HasValue);

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
                        (SavedItemType.Track, true) => await _libraryService.RemoveTrackAsync(itemUri),
                        (SavedItemType.Track, false) => await _libraryService.SaveTrackAsync(itemUri),
                        (SavedItemType.Album, true) => await _libraryService.RemoveAlbumAsync(itemUri),
                        (SavedItemType.Album, false) => await _libraryService.SaveAlbumAsync(itemUri),
                        (SavedItemType.Artist, true) => await _libraryService.UnfollowArtistAsync(itemUri),
                        (SavedItemType.Artist, false) => await _libraryService.FollowArtistAsync(itemUri),
                        _ => false
                    };
                    _logger?.LogInformation("ToggleSave: library service returned success={Success}", success);

                    if (!success)
                    {
                        _logger?.LogWarning("ToggleSave: API call failed, reverting cache for {Uri}", itemUri);
                        if (currentlySaved)
                            cache.AddOrUpdate(bareId);
                        else
                            cache.RemoveKey(bareId);
                        SaveStateChanged?.Invoke();
                    }
                }
                else
                {
                    _logger?.LogWarning("ToggleSave: ISpotifyLibraryService not in DI — writing directly to DB");
                    var (_, dbType, _) = TypeMap[type];
                    if (currentlySaved)
                    {
                        await _database.RemoveFromSpotifyLibraryAsync(itemUri);
                        _logger?.LogInformation("ToggleSave: removed {Uri} from DB", itemUri);
                    }
                    else
                    {
                        await _database.AddToSpotifyLibraryAsync(itemUri, dbType,
                            DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                        _logger?.LogInformation("ToggleSave: added {Uri} to DB", itemUri);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to persist save toggle for {Uri}", itemUri);
                // Rollback in-memory state
                if (currentlySaved)
                    cache.AddOrUpdate(bareId);
                else
                    cache.RemoveKey(bareId);
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
            var entities = await _database.GetSpotifyLibraryItemsAsync(dbType, pageSize, offset);
            if (entities.Count == 0) break;

            cache.Edit(c =>
            {
                foreach (var entity in entities)
                {
                    var bareId = ExtractBareId(entity.Uri, prefix);
                    if (bareId != null)
                        c.AddOrUpdate(bareId);
                }
            });

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
            cache.Edit(c =>
            {
                foreach (var item in changeEvent.Items)
                {
                    if (!item.ItemUri.StartsWith(prefix, StringComparison.Ordinal))
                        continue;

                    var bareId = ExtractBareId(item.ItemUri, prefix);
                    if (bareId == null) continue;

                    if (item.IsRemoved)
                        c.Remove(bareId);
                    else
                        c.AddOrUpdate(bareId);
                    changed = true;
                }
            });
        }

        if (changed) SaveStateChanged?.Invoke();
    }

    private static string? ExtractBareId(string uri, string prefix)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        return uri.StartsWith(prefix, StringComparison.Ordinal) ? uri[prefix.Length..] : uri;
    }

    public void Dispose()
    {
        _disposables.Dispose();
        foreach (var cache in _caches.Values)
            cache.Dispose();
    }
}
