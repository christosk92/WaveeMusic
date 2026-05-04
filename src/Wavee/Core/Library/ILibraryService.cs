namespace Wavee.Core.Library;

/// <summary>
/// Service for managing the unified media library.
/// </summary>
/// <remarks>
/// Provides a single interface for:
/// - Adding/updating/removing items from any source
/// - Recording play history
/// - Searching and browsing the library
/// - Getting statistics
/// </remarks>
public interface ILibraryService : IAsyncDisposable
{
    #region Library Items

    /// <summary>
    /// Gets a library item by ID.
    /// </summary>
    /// <param name="id">The item ID (URI).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The library item, or null if not found.</returns>
    Task<LibraryItem?> GetItemAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Adds or updates a library item.
    /// </summary>
    /// <param name="item">The item to add/update.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated item.</returns>
    Task<LibraryItem> AddOrUpdateItemAsync(LibraryItem item, CancellationToken ct = default);

    /// <summary>
    /// Removes an item from the library.
    /// </summary>
    /// <param name="id">The item ID to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the item was removed.</returns>
    Task<bool> RemoveItemAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Searches the library with optional filters.
    /// </summary>
    /// <param name="query">Search query with filters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of matching items.</returns>
    Task<IReadOnlyList<LibraryItem>> SearchAsync(LibrarySearchQuery query, CancellationToken ct = default);

    #endregion

    #region Play History

    /// <summary>
    /// Records a play event for an item.
    /// </summary>
    /// <remarks>
    /// If the item doesn't exist in the library, it will be auto-added.
    /// </remarks>
    /// <param name="itemId">The item ID that was played.</param>
    /// <param name="durationPlayedMs">How long the item was played.</param>
    /// <param name="completed">Whether the item was played to completion.</param>
    /// <param name="sourceContext">Optional context (e.g., playlist URI).</param>
    /// <param name="ct">Cancellation token.</param>
    Task RecordPlayAsync(
        string itemId,
        long durationPlayedMs,
        bool completed,
        string? sourceContext = null,
        CancellationToken ct = default);

    /// <summary>
    /// Records a play with item metadata (creates/updates item automatically).
    /// </summary>
    /// <param name="item">The item that was played.</param>
    /// <param name="durationPlayedMs">How long the item was played.</param>
    /// <param name="completed">Whether the item was played to completion.</param>
    /// <param name="sourceContext">Optional context (e.g., playlist URI).</param>
    /// <param name="ct">Cancellation token.</param>
    Task RecordPlayAsync(
        LibraryItem item,
        long durationPlayedMs,
        bool completed,
        string? sourceContext = null,
        CancellationToken ct = default);

    /// <summary>
    /// Gets recently played items.
    /// </summary>
    /// <param name="limit">Maximum number of items to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of recently played items (most recent first).</returns>
    Task<IReadOnlyList<LibraryItem>> GetRecentlyPlayedAsync(int limit = 50, CancellationToken ct = default);

    /// <summary>
    /// Gets most played items.
    /// </summary>
    /// <param name="limit">Maximum number of items to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of most played items (highest play count first).</returns>
    Task<IReadOnlyList<LibraryItem>> GetMostPlayedAsync(int limit = 50, CancellationToken ct = default);

    /// <summary>
    /// Gets raw play history entries.
    /// </summary>
    /// <param name="limit">Maximum number of entries to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of play history entries (most recent first).</returns>
    Task<IReadOnlyList<PlayHistoryEntry>> GetPlayHistoryAsync(int limit = 50, CancellationToken ct = default);

    #endregion

    #region Statistics

    /// <summary>
    /// Gets library statistics.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Library statistics.</returns>
    Task<LibraryStats> GetStatsAsync(CancellationToken ct = default);

    #endregion

    #region Sync State

    /// <summary>
    /// Gets the sync revision for a collection type.
    /// </summary>
    /// <param name="collectionType">The collection type (e.g., "tracks", "albums").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The revision string, or null if never synced.</returns>
    Task<string?> GetSyncRevisionAsync(string collectionType, CancellationToken ct = default);

    /// <summary>
    /// Sets the sync revision for a collection type.
    /// </summary>
    /// <param name="collectionType">The collection type.</param>
    /// <param name="revision">The new revision.</param>
    /// <param name="itemCount">The number of items in the collection.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SetSyncRevisionAsync(string collectionType, string? revision, int itemCount = 0, CancellationToken ct = default);

    #endregion
}
