// ── Playback/LocalLibrary.Access.cs ────────────────────────────────────────────────────────────────────────────────
// folders Wavee cannot read: "Can't read this folder" · [Fix permissions] · [Remove] (#240 UX S11, board 15)
//
// Role: CORE (UI thread for the verbs). A music folder that refuses this user is `LocalRootState.NoAccess` (the scan's open was denied; not
// offline: the drive is there). A subfolder the walk could not open or list is in the last scan's `LocalScanStats.Unreadable`; it is shown nested
// under its music folder, where Remove means "skip this subfolder": it stays on disk and is left out of every walk (`local.skip`, the full paths
// joined by '|', read and written only here). Fix permissions opens the folder's Properties at its Security tab (the shell's own page).
// A FILE the user hides from Local Files ("Hide from Local Files" on its row, or in the Review dialog) joins the same set: the same sticky skip,
// one file instead of a folder. It is never deleted from disk; its index entry leaves, and the walk never reads it again until it is un-hidden.

using System.Runtime.InteropServices;

namespace Wavee;

public static partial class LocalLibrary
{
    /// <summary>The unreadable subfolders of a music folder (full paths), from its last scan, less the ones the user skipped. Any thread.</summary>
    public static string[] UnreadableOf(string folder)
    {
        if (!s_scan.Last.TryGetValue(folder, out LocalScanStats? stats) || stats.Unreadable.Length == 0) return [];
        var skipped = Skipped();
        var list = new List<string>(stats.Unreadable.Length);
        foreach (string rel in stats.Unreadable)
        {
            if (rel.Length == 0) continue;                                  // the root itself is the NoAccess state, not a nested line
            string full = LocalIndex.Join(folder, rel);
            if (!skipped.Contains(full)) list.Add(full);
        }
        return list.ToArray();
    }

    /// <summary>"Fix permissions": the folder's Properties at its Security tab. UI thread; returns at once (the shell shows the page).</summary>
    public static void FixPermissions(string path)
    {
        try
        {
            if (!SHObjectProperties(0, ShopFilePath, path, "Security")) Log.Warn("local", "local.access properties did not open");
        }
        catch (Exception ex) { Log.Warn("local", "local.access properties failed", ex); }
    }

    /// <summary>"Remove" on an unreadable subfolder: leave it out of every walk from now on (it stays on disk), and walk its music folder again.</summary>
    public static void SkipFolder(string fullPath)
    {
        var skipped = Skipped();
        if (!skipped.Add(fullPath)) return;
        Platform.Settings.Set(Platform.Keys.MusicSkip, string.Join('|', skipped));
        foreach (string f in Folders)
            if (Contains(f, fullPath))
            {
                if (s_scan.Last.TryGetValue(f, out LocalScanStats? stats))
                    stats.Unreadable = Array.FindAll(stats.Unreadable, rel => !string.Equals(LocalIndex.Join(f, rel), fullPath, StringComparison.OrdinalIgnoreCase));
                Rescan(f);
            }
        NoteProgress();
    }

    /// <summary>"Hide from Local Files" on an indexed file: leave it out of every walk from now on (the sticky skip above; it stays on disk) and drop
    /// its entry, so its row leaves the list and stops needing attention. A MISSING file is removed as the Review's Remove does (nothing is on disk to
    /// skip). Returns the full path it hid (for an Undo), or null when there was nothing to hide. UI thread.</summary>
    public static string? HideFile(long localId)
    {
        if (!s_index.TryGet(localId, out LocalEntry? e)) return null;
        if (e.State == LocalState.Missing) { RemoveMissing(localId); return null; }
        string? full = s_index.FullPathOf(e);
        if (full is null) return null;
        var skipped = Skipped();
        if (skipped.Add(full)) Platform.Settings.Set(Platform.Keys.MusicSkip, string.Join('|', skipped));
        var batch = new LocalScanBatch(e.RootId, LocalIndex.FolderOf(e.RelativePath));
        batch.Removes.Add(localId);
        s_index.Apply(batch);
        Log.Info("local", "local.hide id=" + localId);
        NoteProgress();
        return full;
    }

    /// <summary>Undo of <see cref="HideFile"/>: the file leaves the skip set and its folder is walked again (it comes back under a new id). UI thread.</summary>
    public static void UnhideFile(string fullPath)
    {
        var skipped = Skipped();
        if (!skipped.Remove(fullPath)) return;
        Platform.Settings.Set(Platform.Keys.MusicSkip, string.Join('|', skipped));
        if (ContainerOf(fullPath) is { } folder) Rescan(folder);
        NoteProgress();
    }

    static HashSet<string> Skipped()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string p in Platform.Settings.Get(Platform.Keys.MusicSkip).Split('|', StringSplitOptions.RemoveEmptyEntries)) set.Add(p);
        return set;
    }

    /// <summary>The scan's skip set for one music folder (relative paths: skipped subfolders and hidden files), read as a walk starts (scan thread).</summary>
    static HashSet<string> SkippedUnder(string root)
    {
        var rel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string prefix = root.TrimEnd('\\') + "\\";
        foreach (string p in Skipped())
            if (p.Length > prefix.Length && p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) rel.Add(p[prefix.Length..]);
        return rel;
    }

    const uint ShopFilePath = 2;

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SHObjectProperties(nint hwnd, uint shopObjectType, string pszObjectName, string? pszPropertyPage);
}
