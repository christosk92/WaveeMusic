using System;
using System.Threading.Tasks;
using DynamicData;

namespace Wavee.UI.WinUI.Data.Contracts;

/// <summary>
/// The type of library item (track, album, artist).
/// </summary>
public enum SavedItemType { Track, Album, Artist }

/// <summary>
/// In-memory reactive cache of saved/liked item IDs (tracks, albums, artists).
/// Populated from SQLite on startup, kept in sync via Dealer WebSocket deltas.
/// All lookups are synchronous O(1) — no API or database calls.
/// </summary>
public interface ITrackLikeService
{
    /// <summary>
    /// Synchronous O(1) check — purely in-memory, no API/DB call.
    /// </summary>
    bool IsSaved(SavedItemType type, string bareId);

    /// <summary>
    /// Get the count of saved items of a specific type from in-memory cache.
    /// </summary>
    int GetCount(SavedItemType type);

    /// <summary>
    /// Toggle save state: updates in-memory cache immediately, then persists
    /// to DB and enqueues API sync.
    /// </summary>
    void ToggleSave(SavedItemType type, string itemUri, bool currentlySaved);

    /// <summary>
    /// DynamicData observable for a specific item type.
    /// Connect to drive reactive UI updates across all views.
    /// </summary>
    IObservable<IChangeSet<string, string>> Connect(SavedItemType type);

    /// <summary>
    /// Initial population from SQLite. Call once on startup after library sync.
    /// </summary>
    Task InitializeAsync();

    /// <summary>
    /// Fired whenever any saved state changes. Subscribe to refresh UI.
    /// Lightweight — just signals that something changed, check IsSaved() for specifics.
    /// </summary>
    event Action? SaveStateChanged;
}
