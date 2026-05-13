using System.Collections.Generic;
using System.Linq;
using Wavee.Local.Models;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Pure helpers for picking which on-disk episode of a local show should
/// play next. Centralises the season-ordered + on-disk-only filter that
/// both the hero Play button (LocalShowDetailPage) and the Up-Next overlay
/// (VideoPlayerPage) used to redo locally.
/// </summary>
internal static class LocalShowEpisodeQueue
{
    /// <summary>
    /// Flatten every season's episodes in (Season ASC, Episode ASC) order,
    /// keeping only on-disk rows that have a playable <c>TrackUri</c>.
    /// Missing-from-disk roster entries are dropped — they can't be played.
    /// </summary>
    public static IReadOnlyList<LocalEpisode> BuildPlayableQueue(IEnumerable<LocalSeason> seasons)
        => seasons
            .OrderBy(s => s.SeasonNumber)
            .SelectMany(s => s.Episodes.OrderBy(ep => ep.Episode))
            .Where(ep => ep.IsOnDisk && !string.IsNullOrEmpty(ep.TrackUri))
            .ToList();

    /// <summary>
    /// First on-disk episode that hasn't been watched yet, or — if every
    /// playable episode has been watched — the very first playable episode.
    /// Used by the hero Play button to mean "continue where I left off, or
    /// restart from the top".
    /// </summary>
    public static LocalEpisode? PickFirstUnwatched(IEnumerable<LocalSeason> seasons)
    {
        var queue = BuildPlayableQueue(seasons);
        var firstUnwatched = queue.FirstOrDefault(ep => ep.WatchedAt is null);
        return firstUnwatched ?? queue.FirstOrDefault();
    }

    /// <summary>
    /// The next playable episode after <paramref name="current"/> in season
    /// order. Returns <c>null</c> when <paramref name="current"/> is the
    /// final on-disk episode of the show (no season N+1, or season N+1 has
    /// no on-disk episodes). Match is by <c>TrackUri</c>; if the current
    /// episode isn't in the queue (e.g. user deleted the file mid-play),
    /// returns <c>null</c>.
    /// </summary>
    public static LocalEpisode? GetNextEpisode(LocalEpisode current, IEnumerable<LocalSeason> seasons)
    {
        if (string.IsNullOrEmpty(current.TrackUri)) return null;
        var queue = BuildPlayableQueue(seasons);
        var idx = -1;
        for (var i = 0; i < queue.Count; i++)
        {
            if (string.Equals(queue[i].TrackUri, current.TrackUri, System.StringComparison.Ordinal))
            {
                idx = i;
                break;
            }
        }
        if (idx < 0 || idx >= queue.Count - 1) return null;
        return queue[idx + 1];
    }
}
