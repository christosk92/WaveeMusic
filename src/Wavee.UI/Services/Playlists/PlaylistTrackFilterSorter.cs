using System;
using System.Collections.Generic;
using System.Linq;
using Wavee.UI.Models;

namespace Wavee.UI.Services.Playlists;

/// <summary>
/// Framework-neutral playlist-track filter + sort pipeline. Pulled out of
/// <c>PlaylistViewModel.BuildFilteredAndSortedTracks</c> so the same logic
/// can be reused (tests, alternate UI shells, future Wavee.Console surfaces)
/// without dragging WinUI in.
///
/// <para>Stateless singleton — DI-registered. Callers pass in the full track
/// snapshot plus the current filter / sort state; result is a fresh list
/// ready to bind.</para>
/// </summary>
public sealed class PlaylistTrackFilterSorter
{
    /// <summary>
    /// Applies the playlist's current filter + sort state to a track snapshot.
    /// </summary>
    /// <param name="source">All tracks for the playlist.</param>
    /// <param name="searchQuery">Optional search query — matched case-insensitively
    /// against title / artist / album. Null or whitespace skips the filter.</param>
    /// <param name="videosOnly">When true, restricts to tracks where
    /// <see cref="PlaylistTrackDto.HasVideo"/> is true.</param>
    /// <param name="sortColumn">Column to sort by. <see cref="PlaylistSortColumn.Custom"/>
    /// preserves the authored order via <see cref="PlaylistTrackDto.OriginalIndex"/>.</param>
    /// <param name="sortDescending">Reverses the sort direction.</param>
    public IReadOnlyList<PlaylistTrackDto> FilterAndSort(
        IEnumerable<PlaylistTrackDto> source,
        string? searchQuery,
        bool videosOnly,
        PlaylistSortColumn sortColumn,
        bool sortDescending)
    {
        if (source is null) return Array.Empty<PlaylistTrackDto>();

        var query = searchQuery?.Trim();
        IEnumerable<PlaylistTrackDto> filtered = source;

        if (!string.IsNullOrEmpty(query))
        {
            filtered = filtered.Where(t =>
                t.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                t.ArtistName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                t.AlbumName.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        if (videosOnly)
            filtered = filtered.Where(static t => t.HasVideo);

        var sorted = (sortColumn, sortDescending) switch
        {
            (PlaylistSortColumn.Custom, false) => filtered.OrderBy(t => t.OriginalIndex),
            (PlaylistSortColumn.Custom, true) => filtered.OrderByDescending(t => t.OriginalIndex),
            (PlaylistSortColumn.Title, false) => filtered.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase),
            (PlaylistSortColumn.Title, true) => filtered.OrderByDescending(t => t.Title, StringComparer.OrdinalIgnoreCase),
            (PlaylistSortColumn.Artist, false) => filtered.OrderBy(t => t.ArtistName, StringComparer.OrdinalIgnoreCase),
            (PlaylistSortColumn.Artist, true) => filtered.OrderByDescending(t => t.ArtistName, StringComparer.OrdinalIgnoreCase),
            (PlaylistSortColumn.Album, false) => filtered.OrderBy(t => t.AlbumName, StringComparer.OrdinalIgnoreCase),
            (PlaylistSortColumn.Album, true) => filtered.OrderByDescending(t => t.AlbumName, StringComparer.OrdinalIgnoreCase),
            (PlaylistSortColumn.AddedAt, false) => filtered.OrderBy(t => t.AddedAt),
            (PlaylistSortColumn.AddedAt, true) => filtered.OrderByDescending(t => t.AddedAt),
            _ => filtered.OrderBy(t => t.OriginalIndex)
        };

        return sorted.ToList();
    }
}
