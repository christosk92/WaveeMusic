using System.Collections.Generic;
using System.Windows.Input;
using Wavee.UI.WinUI.Data.DTOs;

namespace Wavee.UI.WinUI.ViewModels.Contracts;

/// <summary>
/// Interface for ViewModels that support the TrackListView control.
/// Provides sorting, selection, and action commands.
/// </summary>
public interface ITrackListViewModel
{
    // Sorting
    /// <summary>
    /// Command to sort by a column. Parameter is the column name (Title, Artist, Album, AddedAt).
    /// </summary>
    ICommand SortByCommand { get; }

    /// <summary>
    /// Glyph for sort direction indicator (chevron up/down).
    /// </summary>
    string SortChevronGlyph { get; }

    /// <summary>
    /// Whether currently sorting by Title column.
    /// </summary>
    bool IsSortingByTitle { get; }

    /// <summary>
    /// Whether currently sorting by Artist column.
    /// </summary>
    bool IsSortingByArtist { get; }

    /// <summary>
    /// Whether currently sorting by Album column.
    /// </summary>
    bool IsSortingByAlbum { get; }

    /// <summary>
    /// Whether currently sorting by Date Added column.
    /// </summary>
    bool IsSortingByAddedAt { get; }

    // Selection (use object to avoid covariance issues with IReadOnlyList<T>)
    /// <summary>
    /// Currently selected items in the track list.
    /// </summary>
    IReadOnlyList<object> SelectedItems { get; set; }

    /// <summary>
    /// Number of selected tracks.
    /// </summary>
    int SelectedCount { get; }

    /// <summary>
    /// Whether any tracks are selected.
    /// </summary>
    bool HasSelection { get; }

    /// <summary>
    /// Header text for selection command bar (e.g., "3 tracks selected").
    /// </summary>
    string SelectionHeaderText { get; }

    // Playback Commands
    /// <summary>
    /// Command to play a specific track. Parameter is the track item.
    /// </summary>
    ICommand PlayTrackCommand { get; }

    /// <summary>
    /// Command to play selected tracks.
    /// </summary>
    ICommand PlaySelectedCommand { get; }

    /// <summary>
    /// Command to play selected tracks after the current track.
    /// </summary>
    ICommand PlayAfterCommand { get; }

    /// <summary>
    /// Command to add selected tracks to the queue.
    /// </summary>
    ICommand AddSelectedToQueueCommand { get; }

    /// <summary>
    /// Command to remove selected tracks (from library, playlist, etc.).
    /// </summary>
    ICommand RemoveSelectedCommand { get; }

    /// <summary>
    /// Command to add selected tracks to a playlist. Parameter is PlaylistSummaryDto.
    /// </summary>
    ICommand AddToPlaylistCommand { get; }

    // Playlist Data
    /// <summary>
    /// Available playlists for the "Add to playlist" flyout.
    /// </summary>
    IReadOnlyList<PlaylistSummaryDto> Playlists { get; }
}
