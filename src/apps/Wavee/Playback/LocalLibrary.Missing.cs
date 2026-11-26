// ── Playback/LocalLibrary.Missing.cs ───────────────────────────────────────────────────────────────────────────────
// the missing songs' two verbs: Remove one, and re-pair one with the file the user points at (#240 UX S6, flow F4, board 12)
//
// Role: CORE (UI thread for the verbs; the index commits under its own lock)
// Spec: 240-local-files-ux.md S6 ("per row [Locate…] (pick the file or the folder it moved to; Wavee re-pairs and keeps the row) and [Remove]")
//
// REMOVE drops the entry (its LocalId is never reused): its row leaves Local Files at the next restate, and a playlist row that listed it
// is unmatched again (it falls back to the matcher, as any spotify:local: row does).
// RELOCATE keeps the LocalId (so queues, playlists and play counts keep pointing at it) and moves the entry to the picked file:
//   · the file is inside a music folder: read its tags now and commit the entry there; an entry the scan made for that path under another
//     id (it found the file as new) is dropped in the same batch, so the library never lists it twice;
//   · the file is outside every music folder: its folder is added as a music folder and the pair waits (`s_pending`) until the scan has
//     indexed the file; the scan's own move pairing (the same file id) usually pairs it first, and then the pending pair is simply dropped.

using System.Collections.Concurrent;

namespace Wavee;

public static partial class LocalLibrary
{
    /// <summary>What <see cref="Relocate"/> did.</summary>
    public enum RelocateResult : byte
    {
        /// <summary>The entry now points at the file.</summary>
        Paired,
        /// <summary>The file's folder was added as a music folder; the pair completes once the scan has indexed it.</summary>
        AddedFolder,
        /// <summary>The entry is gone (removed or purged meanwhile).</summary>
        Gone,
        /// <summary>The file is not one Wavee reads (not audio, or unreadable).</summary>
        NotAudio,
        /// <summary>The folder could not be added (the reason word of <see cref="AddFolder"/>).</summary>
        Refused,
    }

    /// <summary>Full path → the LocalId waiting to be paired with it once a scan indexes it.</summary>
    static readonly ConcurrentDictionary<string, long> s_pending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"Remove" in the missing songs' review: drop a missing entry from the index. Only a Missing entry is removed (a file that came back
    /// meanwhile stays). UI thread.</summary>
    public static bool RemoveMissing(long localId)
    {
        if (!s_index.TryGet(localId, out LocalEntry? e) || e.State != LocalState.Missing) return false;
        var batch = new LocalScanBatch(e.RootId, LocalIndex.FolderOf(e.RelativePath));
        batch.Removes.Add(localId);
        s_index.Apply(batch);
        Log.Info("local", "local.missing removed id=" + localId);
        NoteProgress();
        return true;
    }

    /// <summary>"Locate…" in the missing songs' review: point entry <paramref name="localId"/> at <paramref name="fullPath"/> (see the file header).
    /// UI thread; reads one file's header.</summary>
    public static RelocateResult Relocate(long localId, string fullPath)
    {
        if (!s_index.TryGet(localId, out LocalEntry? old)) return RelocateResult.Gone;
        string path;
        try { path = Path.GetFullPath(fullPath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return RelocateResult.NotAudio; }
        if (!s_index.TryRelative(path, out int rootId, out string rel) || ContainerOf(path) is null)
        {
            string folder = Path.GetDirectoryName(path) ?? path;
            if (!AddFolder(folder, out string reason)) { Log.Info("local", "local.missing locate refused reason=" + reason); return RelocateResult.Refused; }
            s_pending[path] = localId;
            Log.Info("local", "local.missing locate pending id=" + localId + " folder added");
            return RelocateResult.AddedFolder;
        }
        if (!LocalTags.TryRead(path, out Spotify.Audio.Format format, out LocalTagSet tags, out string why)) return RelocateResult.NotAudio;
        var info = new FileInfo(path);
        uint volume = old.VolumeSerial;
        foreach (LocalRoot r in s_index.Roots()) if (r.RootId == rootId) { volume = r.VolumeSerial; break; }
        bool playable = format != Spotify.Audio.Format.Unknown;
        var batch = new LocalScanBatch(rootId, LocalIndex.FolderOf(rel));
        if (s_index.TryGetByPath(rootId, rel, out LocalEntry? twin) && twin.LocalId != localId) batch.Removes.Add(twin.LocalId);
        // FileId 0 and ChangeTicks 0: the next walk compares size and write time, reads the file once more under this same id and records them.
        batch.Upserts.Add(new LocalEntry(localId, rootId, rel, volume, 0, info.Length, info.LastWriteTimeUtc.Ticks, 0, (uint)info.Attributes, format, tags,
            LocalTags.TitleOrFileName(tags, Path.GetFileName(path)), LocalState.Present, DateTime.UtcNow.Ticks, playable, playable ? "" : why,
            old.FirstSeenTicks));
        s_index.Apply(batch);
        s_pending.TryRemove(path, out _);
        Log.Info("local", "local.missing relocated id=" + localId + (batch.Removes.Count > 0 ? " twin=" + batch.Removes[0] : ""));
        NoteProgress();
        return RelocateResult.Paired;
    }

    /// <summary>UI thread, after each progress post: a pending pair whose file the scan has indexed under another id is paired now; one the scan
    /// paired itself (same id), or whose entry is gone, is dropped.</summary>
    static void PairPending()
    {
        if (s_pending.IsEmpty) return;
        foreach (var (path, id) in s_pending)
        {
            if (!s_index.TryGet(id, out _)) { s_pending.TryRemove(path, out _); continue; }
            if (!s_index.TryGetByPath(path, out LocalEntry? found)) continue;
            if (found.LocalId == id) { s_pending.TryRemove(path, out _); continue; }
            Relocate(id, path);
        }
    }
}
