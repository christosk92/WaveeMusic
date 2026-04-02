using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core.Http.Lyrics;

namespace Wavee.UI.WinUI.Services.Lyrics;

/// <summary>
/// A lyrics provider that can search for and return lyrics with optional word-level timing.
/// </summary>
public interface ILyricsProvider
{
    /// <summary>Unique provider ID (e.g. "Musixmatch", "QQMusic").</summary>
    string Id { get; }

    /// <summary>Human-readable display name.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Search for lyrics matching the given track metadata.
    /// </summary>
    /// <param name="title">Track title.</param>
    /// <param name="artist">Artist name.</param>
    /// <param name="album">Album name (may be null).</param>
    /// <param name="durationMs">Track duration in milliseconds.</param>
    /// <param name="trackId">Spotify track ID (used by Spotify provider only).</param>
    /// <param name="imageUri">Album art URI (used by Spotify provider only).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Search result with lyrics, word timings, and match score; or null if not found.</returns>
    Task<LyricsSearchResult?> SearchAsync(
        string title, string artist, string? album,
        double durationMs, string? trackId, string? imageUri,
        CancellationToken ct);
}

/// <summary>
/// Result from a lyrics provider search.
/// </summary>
public sealed record LyricsSearchResult(
    LyricsResponse Response,
    Dictionary<int, List<LrcWordTiming>>? WordTimings,
    int MatchScore);
