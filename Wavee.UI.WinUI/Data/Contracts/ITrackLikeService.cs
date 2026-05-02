using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Wavee.UI.WinUI.Data.Contracts;

/// <summary>
/// The type of library item (track, album, artist).
/// </summary>
public enum SavedItemType { Track, Album, Artist, Show }

/// <summary>
/// In-memory cache of saved/liked item IDs (tracks, albums, artists).
/// Populated from SQLite on startup, kept in sync via Dealer WebSocket deltas.
/// All lookups are synchronous O(1) — no API or database calls.
/// </summary>
public interface ITrackLikeService
{
    /// <summary>
    /// Synchronous O(1) check — purely in-memory, no API/DB call.
    /// Accepts both bare IDs and full Spotify URIs.
    /// </summary>
    bool IsSaved(SavedItemType type, string idOrUri);

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
    /// Initial population from SQLite. Call once on startup after library sync.
    /// </summary>
    Task InitializeAsync();

    /// <summary>
    /// Clear and reload all caches from SQLite. Call after sync completes
    /// to pick up newly synced data.
    /// </summary>
    Task ReloadCacheAsync();

    /// <summary>
    /// Drop all in-memory saved state. Call on sign-out so hearts don't
    /// linger as "saved" for the next user until their sync completes.
    /// </summary>
    void ClearCache();

    /// <summary>
    /// Returns all saved bare IDs for a given item type from in-memory cache.
    /// </summary>
    IReadOnlyCollection<string> GetSavedIds(SavedItemType type);

    /// <summary>
    /// Fired whenever any saved state changes. Subscribe to refresh UI.
    /// Lightweight — just signals that something changed, check IsSaved() for specifics.
    /// </summary>
    event Action? SaveStateChanged;
}
