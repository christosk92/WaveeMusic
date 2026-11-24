// ── Playback/LocalLibrary.Added.cs ──────────────────────────────────────────────────────────────────────────────────
// "Added files": a file dropped into Local Files (or picked with "Play a file") from outside the music folders is a library
// entry like any other: indexed with a stable LocalId, kept in localindex.bin across restarts, on Home, in the counts, the
// albums and artists, Find on Spotify and Track details. It sits under one index root of its own (LocalIndex.AddedRoot: no
// path, each entry's relative path is its full path) that the scanner never walks; "Remove from Local Files" takes it out
// again (with Undo), where a music-folder file has "Hide from Local Files". A dropped file that is inside a music folder is
// that folder's entry (found, or added under its root for the scan to keep).
//
// Role: SHELL (UI thread)

namespace Wavee;

public static partial class LocalLibrary
{
    /// <summary>Is the indexed file one of the "Added files" (dropped from outside the music folders)? Any thread.</summary>
    public static bool IsAdded(long localId) => s_index.TryGet(localId, out LocalEntry? e) && e.RootId == LocalIndex.AddedRoot;

    /// <summary>Add a dropped file to the library and stage its row now (a drop plays or queues it at once), or find it when the index already
    /// has it (a music-folder file, an earlier drop; a missing one comes back under its old id, with its link and rename). The tags were read
    /// off the UI thread (<paramref name="read"/>: the reader understood the file; <paramref name="format"/>: what it decodes as). Returns the
    /// row's track, or default when the path is not a file. UI thread.</summary>
    public static Track AddFile(string path, Spotify.Audio.Format format, in LocalTagSet tags, bool read, string why)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return default; }
        var info = new FileInfo(full);
        if (!info.Exists) return default;
        EnsureAddedRoot();
        if (!s_index.TryRelative(full, out int rootId, out string rel)) return default;
        LocalEntry? known = s_index.TryGetByPath(rootId, rel, out LocalEntry? k) ? k : null;
        if (known is null || known.State == LocalState.Missing)
        {
            long now = DateTime.UtcNow.Ticks;
            bool video = tags.Kind == LocalKind.Video;
            // A video plays through the video path (its own row's attachment, the local-video lane): it is playable when the reader knew it.
            bool playable = read && (format != Spotify.Audio.Format.Unknown || video);
            var entry = new LocalEntry(known?.LocalId ?? s_index.NewId(), rootId, rel, 0, 0, info.Length, info.LastWriteTimeUtc.ToFileTimeUtc(), 0,
                (uint)info.Attributes, format, tags, LocalTags.TitleOrFileName(tags, info.Name), LocalState.Present, now, playable, playable ? "" : why,
                known is { FirstSeenTicks: > 0 } ? known.FirstSeenTicks : now);
            var batch = new LocalScanBatch(rootId, DirOf(rel));
            batch.Upserts.Add(entry);
            s_index.Apply(batch);
            known = entry;
            Log.Info("local", "local.added root=" + (rootId == LocalIndex.AddedRoot ? "added" : "folder") + " video=" + (video ? 1 : 0));
        }
        var track = StageNow(known);
        Epoch.Value++;
        MaybeRestate();   // the membership (in album order) and the collection follow on the next pass
        return track;
    }

    /// <summary>A file dropped in (the shell's drop, "Play a file", the inspector's drop): <see cref="AddFile"/>, and a video is attached to its
    /// own row, so it plays as video (the attached-video path, until the local-video lane plays local videos itself). UI thread.</summary>
    public static Track AddDropped(string path, Spotify.Audio.Format format, in LocalTagSet tags, bool read, string why)
    {
        Track track = AddFile(path, format, in tags, read, why);
        if (track.IsValid && Playlist.IsLocalVideoFile(path)) Video.Overrides.Attach(track.Uri.Text, path, path, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return track;
    }

    /// <summary>"Remove from Local Files" on an added file: it leaves the library (the file stays on disk). Returns the entry, for
    /// <see cref="RestoreAdded"/>, or null when it is not an added file. UI thread.</summary>
    public static LocalEntry? RemoveAdded(long localId)
    {
        if (!s_index.TryGet(localId, out LocalEntry? e) || e.RootId != LocalIndex.AddedRoot) return null;
        var batch = new LocalScanBatch(e.RootId, DirOf(e.RelativePath));
        batch.Removes.Add(localId);
        s_index.Apply(batch);
        Epoch.Value++;
        MaybeRestate();
        return e;
    }

    /// <summary>Undo of <see cref="RemoveAdded"/>: the file comes back under its own id (its link, rename and default are kept). UI thread.</summary>
    public static void RestoreAdded(LocalEntry entry)
    {
        EnsureAddedRoot();
        var batch = new LocalScanBatch(entry.RootId, DirOf(entry.RelativePath));
        batch.Upserts.Add(entry);
        s_index.Apply(batch);
        Epoch.Value++;
        MaybeRestate();
    }

    static void EnsureAddedRoot()
    {
        foreach (LocalRoot r in s_index.Roots()) if (r.RootId == LocalIndex.AddedRoot) return;
        s_index.SetRoot(new LocalRoot(LocalIndex.AddedRoot, "", 0, 0, 0, 0));
    }

    /// <summary>Background, after the index loads: an added file that is gone from disk is Missing (kept, as a music-folder file is, so a
    /// drive that comes back brings it back); one that is back is Present. No walk: only the added files are looked at.</summary>
    static void CheckAdded()
    {
        LocalScanBatch? batch = null;
        foreach (LocalEntry e in s_index.Snapshot())
        {
            if (e.RootId != LocalIndex.AddedRoot) continue;
            bool there = File.Exists(e.RelativePath);
            if (there == (e.State != LocalState.Missing)) continue;
            batch ??= new LocalScanBatch(LocalIndex.AddedRoot, "");
            batch.Upserts.Add(e with { State = there ? LocalState.Present : LocalState.Missing, LastSeenTicks = there ? DateTime.UtcNow.Ticks : e.LastSeenTicks });
        }
        if (batch is not null) s_index.Apply(batch);
    }

    /// <summary>The folder part of a relative path ("" at the root).</summary>
    static string DirOf(string rel)
    {
        int at = rel.LastIndexOf('\\');
        return at < 0 ? "" : rel[..at];
    }

    /// <summary>Stage one entry's row now (its face by the one rule) and map its id for the audio pump. UI thread.</summary>
    static Track StageNow(LocalEntry e)
    {
        var offline = new HashSet<int>();
        Face face = FaceOf(e, LocalLink.Shown, LocalLink.Renames, offline);
        StageChunk([face], 0, 1);
        Entities.Publish();
        var scope = Entities.Current;
        if (!scope.Tracks.TryGetSlot(UriOf(e.LocalId).AsSpan(), out int slot)) return default;
        lock (s_pathGate) s_pathMap = new Dictionary<EntityId, long>(s_pathMap) { [new Track(slot).Id] = e.LocalId };
        return new Track(slot);
    }
}
