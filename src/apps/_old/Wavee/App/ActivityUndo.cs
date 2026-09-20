using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee;

/// <summary>Reverses an invertible <see cref="ActivityEntry"/> through the same <see cref="LibraryBridge"/> mutation seam
/// the forward action used (the store→signal loop then makes the heart / playlist visibly converge). Every inverse runs
/// under <see cref="ActivityLog.SuppressRecording"/> so the undo itself does not log a new entry; success flips the entry
/// to Undone, failure returns false (the caller surfaces the "couldn't undo" toast). See the plan's undo-mapping table.</summary>
public sealed class ActivityUndoExecutor
{
    readonly IUndoTarget _lib;
    readonly IMusicLibrary _library;
    readonly ActivityLog _log;

    public ActivityUndoExecutor(IUndoTarget lib, IMusicLibrary library, ActivityLog log)
    {
        _lib = lib;
        _library = library;
        _log = log;
    }

    public async Task<bool> UndoAsync(ActivityEntry e)
    {
        if (!e.IsUndoable) return false;
        bool ok;
        using (_log.SuppressRecording())
        {
            try { ok = await ApplyInverseAsync(e).ConfigureAwait(false); }
            catch { ok = false; }
        }
        if (ok) _log.MarkUndone(e.Id);
        return ok;
    }

    async Task<bool> ApplyInverseAsync(ActivityEntry e)
    {
        switch (e.Kind)
        {
            case ActivityKind.Save:
                _lib.SetSaved(e.TargetUri, false);
                return true;
            case ActivityKind.Unsave:
                _lib.SetSaved(e.TargetUri, true);
                return true;
            case ActivityKind.PlaylistRename:
                if (e.Payload?.OldName is not { } old) return false;
                await _lib.UpdatePlaylistDetailsAsync(e.TargetUri, old, null, null).ConfigureAwait(false);
                return true;
            case ActivityKind.PlaylistVisibility:
                if (e.Payload?.NewIsPublic is not bool pub) return false;
                await _lib.SetPlaylistVisibilityAsync(e.TargetUri, !pub).ConfigureAwait(false);
                return true;
            case ActivityKind.PlaylistAddTracks:
                return await UndoAddAsync(e).ConfigureAwait(false);
            case ActivityKind.PlaylistRemoveTracks:
                return await UndoRemoveAsync(e).ConfigureAwait(false);
            case ActivityKind.PlaylistMoveTracks:
                return await UndoMoveAsync(e).ConfigureAwait(false);
            default:
                return false;
        }
    }

    // Add → remove the rows we added: resolve the current row (LAST occurrence per uri) by looking up the live playlist.
    async Task<bool> UndoAddAsync(ActivityEntry e)
    {
        var added = e.Payload?.Tracks;
        if (added is null || added.Count == 0) return false;
        var pl = await _library.GetPlaylistAsync(e.TargetUri).ConfigureAwait(false);
        var byUri = new Dictionary<string, PlaylistRowRef>(StringComparer.Ordinal);
        if (pl.Tracks is { } list)
            for (int i = 0; i < list.Count; i++)
                byUri[list[i].Uri] = new PlaylistRowRef(i, list[i].Uri, list[i].ContextUid ?? "");
        var rows = new List<PlaylistRowRef>(added.Count);
        foreach (var t in added) if (byUri.TryGetValue(t.Uri, out var r)) rows.Add(r);
        if (rows.Count == 0) return false;   // rows gone (edited elsewhere) → fail
        await _lib.RemovePlaylistRowsAsync(e.TargetUri, rows).ConfigureAwait(false);
        return true;
    }

    // Remove → re-add the stored tracks (position not restored — appended at END; accepted per plan).
    async Task<bool> UndoRemoveAsync(ActivityEntry e)
    {
        var removed = e.Payload?.Tracks;
        if (removed is null || removed.Count == 0) return false;
        var tracks = new List<Track>(removed.Count);
        foreach (var r in removed)
            tracks.Add(new Track(EntityUri.IdOf(r.Uri), r.Uri, r.Name ?? "",
                Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 0, false, null));
        await _lib.AddTracksAsync(e.TargetUri, tracks).ConfigureAwait(false);
        return true;
    }

    // Move → move the same rows back to their original index, re-resolving current positions by ItemId.
    async Task<bool> UndoMoveAsync(ActivityEntry e)
    {
        var moved = e.Payload?.Tracks;
        if (moved is null || moved.Count == 0 || e.Payload?.FromIndex is not int from) return false;
        var pl = await _library.GetPlaylistAsync(e.TargetUri).ConfigureAwait(false);
        if (pl.Tracks is not { } list) return false;
        var byItemId = new Dictionary<string, PlaylistRowRef>(StringComparer.Ordinal);
        for (int i = 0; i < list.Count; i++)
        {
            var id = list[i].ContextUid;
            if (!string.IsNullOrEmpty(id)) byItemId[id!] = new PlaylistRowRef(i, list[i].Uri, id!);
        }
        var rows = new List<PlaylistRowRef>(moved.Count);
        foreach (var r in moved)
        {
            if (r.ItemId is { Length: > 0 } id && byItemId.TryGetValue(id, out var row)) rows.Add(row);
            else return false;   // ItemId missing → fail
        }
        await _lib.MovePlaylistRowsAsync(e.TargetUri, rows, UndoMoveTarget(list, rows, from)).ConfigureAwait(false);
        return true;
    }

    /// <summary>The pre-move insertion index that puts the block back where it started.
    /// <para>Replaying the recorded <c>FromIndex</c> raw was only right for a move DOWN. The convention is "insert
    /// before the row currently at this index" in the list AS IT IS NOW — and in that list the block sits somewhere
    /// else, so the index that once named its home now names whatever slid into it. Undoing "move the last song to the
    /// top" replayed as "put it back one from the end".</para>
    /// <para>What actually survives a move is the RELATIVE order of the rows that did not move: the block belongs
    /// immediately after the <paramref name="from"/>-th of them (and at the very front when <paramref name="from"/> is
    /// 0). That is the same row the backend derives its keyed anchor from, so this hands it the index it will resolve
    /// to that row — walking back over the moved rows themselves, exactly as it does. Fewer unmoved rows than expected
    /// (the playlist was edited elsewhere meanwhile) → the end, the closest surviving reading of "after all of them".</para></summary>
    internal static int UndoMoveTarget(IReadOnlyList<Track> current, IReadOnlyList<PlaylistRowRef> rows, int from)
    {
        if (from <= 0) return 0;
        var movedIds = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < rows.Count; i++) movedIds.Add(rows[i].ItemId);
        int unmoved = 0;
        for (int i = 0; i < current.Count; i++)
        {
            if (current[i].ContextUid is { Length: > 0 } id && movedIds.Contains(id)) continue;
            if (++unmoved == from) return i + 1;
        }
        return current.Count;
    }

}
