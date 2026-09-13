using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.Core;

/// <summary>The mutation surface an <c>ActivityUndoExecutor</c> drives to reverse an entry — the subset of the app's
/// LibraryBridge the undo path needs. Kept engine-free (Wavee.Core) so both the executor and its tests need no engine
/// reference; the app's LibraryBridge implements it, so an undo goes through the SAME optimistic + recording path a
/// forward mutation does (recording is skipped via <see cref="ActivityLog.SuppressRecording"/>).</summary>
public interface IUndoTarget
{
    void SetSaved(string uri, bool saved);
    Task UpdatePlaylistDetailsAsync(string playlistUri, string? name, string? description, bool? collaborative, string? previousName = null, CancellationToken ct = default);
    Task SetPlaylistVisibilityAsync(string playlistUri, bool isPublic, CancellationToken ct = default);
    Task AddTracksAsync(string playlistUri, IReadOnlyList<Track> tracks, CancellationToken ct = default);
    Task RemovePlaylistRowsAsync(string playlistUri, IReadOnlyList<PlaylistRowRef> rows, IReadOnlyList<ActivityTrackRef>? removedTracks = null, CancellationToken ct = default);
    Task MovePlaylistRowsAsync(string playlistUri, IReadOnlyList<PlaylistRowRef> rows, int toIndex, CancellationToken ct = default);
}
