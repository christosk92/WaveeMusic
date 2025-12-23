using Microsoft.Extensions.Logging;
using Wavee.Core.Storage;
using Wavee.Core.Storage.Abstractions;

namespace Wavee.Core.Library;

/// <summary>
/// Default implementation of ILibraryService using the unified MetadataDatabase.
/// </summary>
public sealed class LibraryService : ILibraryService
{
    private readonly IMetadataDatabase _database;
    private readonly ILogger? _logger;
    private bool _disposed;

    public LibraryService(IMetadataDatabase database, ILogger? logger = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _logger = logger;
    }

    #region Library Items

    public async Task<LibraryItem?> GetItemAsync(string id, CancellationToken ct = default)
    {
        var entity = await _database.GetEntityAsync(id, ct);
        return entity != null ? MapToLibraryItem(entity) : null;
    }

    public async Task<LibraryItem> AddOrUpdateItemAsync(LibraryItem item, CancellationToken ct = default)
    {
        await _database.UpsertEntityAsync(
            uri: item.Id,
            entityType: EntityType.Track,  // Default to track, could be improved
            sourceType: item.SourceType,
            title: item.Title,
            artistName: item.Artist,
            albumName: item.Album,
            durationMs: (int?)item.DurationMs,
            imageUrl: item.ImageUrl,
            genre: item.Genre,
            filePath: item.FilePath,
            addedAt: item.AddedAt,
            cancellationToken: ct);

        _logger?.LogDebug("Upserted library item: {Id} ({Title})", item.Id, item.Title);
        return item;
    }

    public async Task<bool> RemoveItemAsync(string id, CancellationToken ct = default)
    {
        await _database.DeleteEntityAsync(id, ct);
        _logger?.LogDebug("Removed library item: {Id}", id);
        return true;
    }

    public async Task<IReadOnlyList<LibraryItem>> SearchAsync(LibrarySearchQuery query, CancellationToken ct = default)
    {
        // Map SortOrder to column name
        var orderBy = query.SortOrder switch
        {
            LibrarySortOrder.Title => "title",
            LibrarySortOrder.Artist => "artist_name",
            LibrarySortOrder.Album => "album_name",
            LibrarySortOrder.Duration => "duration_ms",
            LibrarySortOrder.Year => "release_year",
            LibrarySortOrder.RecentlyAdded => "added_at",
            _ => "added_at"
        };

        var descending = query.SortOrder is LibrarySortOrder.RecentlyAdded or LibrarySortOrder.RecentlyPlayed;

        var entities = await _database.QueryEntitiesAsync(
            entityType: null,
            artistNameContains: query.Artist,
            albumNameContains: query.Album,
            titleContains: query.SearchText,
            orderBy: orderBy,
            descending: descending,
            limit: query.Limit,
            offset: query.Offset,
            cancellationToken: ct);

        return entities.Select(MapToLibraryItem).ToList();
    }

    #endregion

    #region Play History

    public async Task RecordPlayAsync(
        string itemId,
        long durationPlayedMs,
        bool completed,
        string? sourceContext = null,
        CancellationToken ct = default)
    {
        await _database.RecordPlayAsync(itemId, (int)durationPlayedMs, completed, sourceContext, ct);
        _logger?.LogDebug("Recorded play: {ItemId} ({DurationMs}ms, completed={Completed})",
            itemId, durationPlayedMs, completed);
    }

    public async Task RecordPlayAsync(
        LibraryItem item,
        long durationPlayedMs,
        bool completed,
        string? sourceContext = null,
        CancellationToken ct = default)
    {
        // Ensure item exists in library
        await AddOrUpdateItemAsync(item, ct);

        // Record the play
        await _database.RecordPlayAsync(item.Id, (int)durationPlayedMs, completed, sourceContext, ct);
        _logger?.LogDebug("Recorded play: {Title} by {Artist} ({DurationMs}ms, completed={Completed})",
            item.Title, item.Artist, durationPlayedMs, completed);
    }

    public async Task<IReadOnlyList<LibraryItem>> GetRecentlyPlayedAsync(int limit = 50, CancellationToken ct = default)
    {
        var history = await _database.GetPlayHistoryAsync(limit, 0, ct);
        return history
            .Where(h => h.Entity != null)
            .Select(h => MapToLibraryItem(h.Entity!))
            .DistinctBy(i => i.Id)
            .ToList();
    }

    public Task<IReadOnlyList<LibraryItem>> GetMostPlayedAsync(int limit = 50, CancellationToken ct = default)
    {
        // This would require a different query - for now return empty
        // Could be implemented with a group by query in MetadataDatabase
        return Task.FromResult<IReadOnlyList<LibraryItem>>(Array.Empty<LibraryItem>());
    }

    public async Task<IReadOnlyList<PlayHistoryEntry>> GetPlayHistoryAsync(int limit = 50, CancellationToken ct = default)
    {
        var history = await _database.GetPlayHistoryAsync(limit, 0, ct);
        return history.Select(h => new PlayHistoryEntry
        {
            ItemId = h.ItemUri,
            PlayedAt = h.PlayedAt.ToUnixTimeSeconds(),
            DurationPlayedMs = h.DurationPlayedMs,
            Completed = h.Completed,
            SourceContext = h.SourceContext
        }).ToList();
    }

    #endregion

    #region Statistics

    public async Task<LibraryStats> GetStatsAsync(CancellationToken ct = default)
    {
        var stats = await _database.GetStatisticsAsync(ct);
        return new LibraryStats
        {
            TotalItems = (int)stats.EntityCount,
            TotalListeningTimeMs = 0,  // Would need additional query
            TotalPlays = 0             // Would need additional query
        };
    }

    #endregion

    #region Sync State

    public async Task<string?> GetSyncRevisionAsync(string collectionType, CancellationToken ct = default)
    {
        var state = await _database.GetSyncStateAsync(collectionType, ct);
        return state?.Revision;
    }

    public Task SetSyncRevisionAsync(string collectionType, string? revision, int itemCount = 0, CancellationToken ct = default)
    {
        return _database.SetSyncStateAsync(collectionType, revision, itemCount, ct);
    }

    #endregion

    #region Helpers

    private static LibraryItem MapToLibraryItem(CachedEntity entity)
    {
        return new LibraryItem
        {
            Id = entity.Uri,
            SourceType = entity.SourceType,
            Title = entity.Title ?? "",
            Artist = entity.ArtistName,
            Album = entity.AlbumName,
            DurationMs = entity.DurationMs ?? 0,
            ImageUrl = entity.ImageUrl,
            Genre = entity.Genre,
            FilePath = entity.FilePath,
            AddedAt = entity.AddedAt?.ToUnixTimeSeconds() ?? 0,
            UpdatedAt = entity.UpdatedAt.ToUnixTimeSeconds()
        };
    }

    #endregion

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        // Database is disposed separately (DI manages it)
        return ValueTask.CompletedTask;
    }
}
