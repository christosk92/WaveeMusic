using Microsoft.Extensions.Logging;

namespace Wavee.Core.Library;

/// <summary>
/// Default implementation of ILibraryService using LibraryDatabase.
/// </summary>
public sealed class LibraryService : ILibraryService
{
    private readonly LibraryDatabase _database;
    private readonly ILogger? _logger;
    private bool _disposed;

    public LibraryService(LibraryDatabase database, ILogger? logger = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _logger = logger;
    }

    /// <summary>
    /// Creates a LibraryService with a new database at the default path.
    /// </summary>
    public static LibraryService Create(string? databasePath = null, ILogger? logger = null)
    {
        var path = databasePath ?? GetDefaultDatabasePath();
        var db = new LibraryDatabase(path, logger);
        return new LibraryService(db, logger);
    }

    /// <summary>
    /// Gets the default database path (%APPDATA%/Wavee/library.db).
    /// </summary>
    public static string GetDefaultDatabasePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "Wavee", "library.db");
    }

    #region Library Items

    public Task<LibraryItem?> GetItemAsync(string id, CancellationToken ct = default)
    {
        return _database.GetItemAsync(id, ct);
    }

    public async Task<LibraryItem> AddOrUpdateItemAsync(LibraryItem item, CancellationToken ct = default)
    {
        var result = await _database.UpsertItemAsync(item, ct);
        _logger?.LogDebug("Upserted library item: {Id} ({Title})", item.Id, item.Title);
        return result;
    }

    public async Task<bool> RemoveItemAsync(string id, CancellationToken ct = default)
    {
        var result = await _database.DeleteItemAsync(id, ct);
        if (result)
        {
            _logger?.LogDebug("Removed library item: {Id}", id);
        }
        return result;
    }

    public Task<IReadOnlyList<LibraryItem>> SearchAsync(LibrarySearchQuery query, CancellationToken ct = default)
    {
        return _database.SearchAsync(query, ct);
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
        // Check if item exists
        var item = await _database.GetItemAsync(itemId, ct);
        if (item == null)
        {
            _logger?.LogWarning("Recording play for unknown item: {ItemId}. Item will not be auto-created.", itemId);
        }

        await _database.RecordPlayAsync(itemId, durationPlayedMs, completed, sourceContext, ct);
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
        await _database.UpsertItemAsync(item, ct);

        // Record the play
        await _database.RecordPlayAsync(item.Id, durationPlayedMs, completed, sourceContext, ct);
        _logger?.LogDebug("Recorded play: {Title} by {Artist} ({DurationMs}ms, completed={Completed})",
            item.Title, item.Artist, durationPlayedMs, completed);
    }

    public Task<IReadOnlyList<LibraryItem>> GetRecentlyPlayedAsync(int limit = 50, CancellationToken ct = default)
    {
        return _database.GetRecentlyPlayedItemsAsync(limit, ct);
    }

    public Task<IReadOnlyList<LibraryItem>> GetMostPlayedAsync(int limit = 50, CancellationToken ct = default)
    {
        return _database.GetMostPlayedItemsAsync(limit, ct);
    }

    public Task<IReadOnlyList<PlayHistoryEntry>> GetPlayHistoryAsync(int limit = 50, CancellationToken ct = default)
    {
        return _database.GetRecentPlaysAsync(limit, ct);
    }

    #endregion

    #region Statistics

    public Task<LibraryStats> GetStatsAsync(CancellationToken ct = default)
    {
        return _database.GetStatsAsync(ct);
    }

    #endregion

    #region Sync State

    public Task<string?> GetSyncRevisionAsync(string collectionType, CancellationToken ct = default)
    {
        return _database.GetSyncRevisionAsync(collectionType, ct);
    }

    public Task SetSyncRevisionAsync(string collectionType, string? revision, int itemCount = 0, CancellationToken ct = default)
    {
        return _database.SetSyncRevisionAsync(collectionType, revision, itemCount, ct);
    }

    #endregion

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _database.DisposeAsync();
    }
}
