namespace Wavee.Connect.Playback;

/// <summary>
/// Client-side sorting and filtering for context tracks.
/// </summary>
public static class ContextSorter
{
    /// <summary>
    /// Sorts tracks according to the specified order.
    /// </summary>
    /// <param name="tracks">Tracks to sort.</param>
    /// <param name="sortOrder">Sort order to apply.</param>
    /// <returns>Sorted track list (new list, original unchanged).</returns>
    public static IReadOnlyList<QueueTrack> Sort(
        IEnumerable<QueueTrack> tracks,
        ContextSortOrder sortOrder)
    {
        return sortOrder switch
        {
            ContextSortOrder.Default => tracks.ToList(),

            ContextSortOrder.NameAsc => tracks
                .OrderBy(t => t.Title ?? "", StringComparer.CurrentCultureIgnoreCase)
                .ToList(),

            ContextSortOrder.NameDesc => tracks
                .OrderByDescending(t => t.Title ?? "", StringComparer.CurrentCultureIgnoreCase)
                .ToList(),

            ContextSortOrder.ArtistNameAsc => tracks
                .OrderBy(t => t.Artist ?? "", StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(t => t.Title ?? "", StringComparer.CurrentCultureIgnoreCase)
                .ToList(),

            ContextSortOrder.ArtistNameDesc => tracks
                .OrderByDescending(t => t.Artist ?? "", StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(t => t.Title ?? "", StringComparer.CurrentCultureIgnoreCase)
                .ToList(),

            ContextSortOrder.AlbumNameAsc => tracks
                .OrderBy(t => t.Album ?? "", StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(t => t.Title ?? "", StringComparer.CurrentCultureIgnoreCase)
                .ToList(),

            ContextSortOrder.AlbumNameDesc => tracks
                .OrderByDescending(t => t.Album ?? "", StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(t => t.Title ?? "", StringComparer.CurrentCultureIgnoreCase)
                .ToList(),

            ContextSortOrder.AlbumArtistNameAsc => tracks
                .OrderBy(t => t.Artist ?? "", StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(t => t.Album ?? "", StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(t => t.Title ?? "", StringComparer.CurrentCultureIgnoreCase)
                .ToList(),

            ContextSortOrder.AlbumArtistNameDesc => tracks
                .OrderByDescending(t => t.Artist ?? "", StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(t => t.Album ?? "", StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(t => t.Title ?? "", StringComparer.CurrentCultureIgnoreCase)
                .ToList(),

            ContextSortOrder.AddTimeAsc => tracks
                .OrderBy(t => t.AddedAt ?? long.MaxValue)
                .ToList(),

            ContextSortOrder.AddTimeDesc => tracks
                .OrderByDescending(t => t.AddedAt ?? 0)
                .ToList(),

            ContextSortOrder.DurationAsc => tracks
                .OrderBy(t => t.DurationMs ?? 0)
                .ToList(),

            ContextSortOrder.DurationDesc => tracks
                .OrderByDescending(t => t.DurationMs ?? 0)
                .ToList(),

            // For track/disc number sorting, null values go to the end
            ContextSortOrder.TrackNumberAsc => tracks
                .OrderBy(t => t.Uri)  // Secondary sort by URI for consistency
                .ToList(),

            ContextSortOrder.TrackNumberDesc => tracks
                .OrderByDescending(t => t.Uri)
                .ToList(),

            ContextSortOrder.DiscNumberAsc => tracks
                .OrderBy(t => t.Uri)
                .ToList(),

            ContextSortOrder.DiscNumberDesc => tracks
                .OrderByDescending(t => t.Uri)
                .ToList(),

            _ => tracks.ToList()  // Unknown sort order - return as-is
        };
    }

    /// <summary>
    /// Filters tracks according to the specified filters.
    /// </summary>
    /// <param name="tracks">Tracks to filter.</param>
    /// <param name="filter">Filter flags to apply.</param>
    /// <returns>Filtered track list (new list, original unchanged).</returns>
    public static IReadOnlyList<QueueTrack> Filter(
        IEnumerable<QueueTrack> tracks,
        ContextFilter filter)
    {
        if (filter == ContextFilter.None)
            return tracks.ToList();

        IEnumerable<QueueTrack> result = tracks;

        if (filter.HasFlag(ContextFilter.Available))
        {
            result = result.Where(t => t.IsPlayable);
        }

        if (filter.HasFlag(ContextFilter.NotExplicit))
        {
            result = result.Where(t => !t.IsExplicit);
        }

        if (filter.HasFlag(ContextFilter.NotEpisode))
        {
            result = result.Where(t => !t.Uri.Contains(":episode:"));
        }

        return result.ToList();
    }

    /// <summary>
    /// Applies both sorting and filtering to tracks.
    /// </summary>
    /// <param name="tracks">Tracks to process.</param>
    /// <param name="sortOrder">Sort order to apply.</param>
    /// <param name="filter">Filter flags to apply.</param>
    /// <returns>Sorted and filtered track list.</returns>
    public static IReadOnlyList<QueueTrack> SortAndFilter(
        IEnumerable<QueueTrack> tracks,
        ContextSortOrder sortOrder,
        ContextFilter filter)
    {
        // Filter first, then sort (more efficient)
        var filtered = Filter(tracks, filter);
        return Sort(filtered, sortOrder);
    }
}
