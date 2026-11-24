// ── Playback/LocalLibrary.Disk.cs ──────────────────────────────────────────────────────────────────────────────────
// the Folders browser's disk verbs (#240 folders): rename, new folder, delete to the Recycle Bin, hide a folder from Local Files
//
// Role: CORE. "Your files are yours": every verb here is one the user picked from a folder's menu and confirmed in a dialog, it runs off the
// UI thread (a disk, a share, the shell) and answers on the UI thread with a plain reason (`FolderOp`). Nothing is ever deleted for good:
// Delete is the shell's move to the Recycle Bin (`SHFileOperationW` with FOF_ALLOWUNDO), refused up front on a drive that has no bin (a
// network share, a USB stick), and the shell's own "delete permanently?" warning stays on as the last guard (FOF_WANTNUKEWARNING).
//
// The index follows the disk the way it follows Explorer: a walk of the music folder after the verb (the watcher's, else ours). A renamed
// folder's files are moves the scanner pairs by file id (LocalScan step 4), so their LocalIds, and every link and decision keyed by them,
// survive. A recycled folder's files turn Missing (S6) and come back under the same ids if it is restored. Hiding a folder (`local.skip`,
// as "Hide from Local Files" on a file) drops its entries and folder records at once and keeps them for the Undo, which puts the same
// ids back.

using System.Runtime.InteropServices;

namespace Wavee;

/// <summary>How a folder verb ended.</summary>
public enum FolderOp : byte { Done, Exists, InUse, Denied, NotFound, Offline, NoRecycleBin, Cancelled, Failed }

public static partial class LocalLibrary
{
    /// <summary>The problem with a folder name the user typed, as a word ("" none): "empty", "chars", "reserved", "dots", "long". Pure.</summary>
    public static string FolderNameProblem(string name)
    {
        string n = name.Trim();
        if (n.Length == 0 || n is "." or "..") return "empty";
        if (n.IndexOfAny(s_badNameChars) >= 0) return "chars";
        foreach (char c in n) if (c < 32) return "chars";
        if (n.EndsWith('.')) return "dots";
        if (n.Length > 240) return "long";
        string stem = n.Split('.')[0].TrimEnd().ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9'))
            return "reserved";
        return "";
    }

    static readonly char[] s_badNameChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>Can a disk verb run on a folder under this music folder now? False while it is away, refused or still being checked: the menu greys
    /// the verbs instead of letting one wait on a dead share. No I/O.</summary>
    public static bool DiskReady(string rootPath) => StateOf(rootPath) is not (LocalRootState.Offline or LocalRootState.SignInNeeded or LocalRootState.NoAccess
        or LocalRootState.Checking or LocalRootState.Waking);

    /// <summary>Rename the folder <paramref name="fullPath"/> to <paramref name="newName"/> (validated by the caller), off the UI thread. <paramref name="done"/>
    /// hears, on the UI thread, how it ended and the new full path. The music folder is walked again after a rename, so the moves pair.</summary>
    public static void RenameFolder(string fullPath, string newName, Action<FolderOp, string> done)
    {
        string name = newName.Trim();
        string parent = Path.GetDirectoryName(fullPath.TrimEnd('\\')) ?? "";
        string target = Path.Combine(parent, name);
        RunDisk("rename", fullPath, () =>
        {
            if (!Directory.Exists(fullPath)) return FolderOp.NotFound;
            bool caseOnly = string.Equals(fullPath.TrimEnd('\\'), target, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly && (Directory.Exists(target) || File.Exists(target))) return FolderOp.Exists;
            if (caseOnly)
            {
                // A change of case only: Windows needs a step through another name.
                string step = target + ".wavee-" + Environment.TickCount64.ToString(System.Globalization.CultureInfo.InvariantCulture);
                Directory.Move(fullPath, step);
                Directory.Move(step, target);
            }
            else Directory.Move(fullPath, target);
            return FolderOp.Done;
        }, op => done(op, op == FolderOp.Done ? target : fullPath));
    }

    /// <summary>Make the folder <paramref name="name"/> inside <paramref name="parentPath"/>, off the UI thread; <paramref name="done"/> hears how it ended
    /// and the new folder's full path (UI thread).</summary>
    public static void CreateFolder(string parentPath, string name, Action<FolderOp, string> done)
    {
        string target = Path.Combine(parentPath, name.Trim());
        RunDisk("create", parentPath, () =>
        {
            if (!Directory.Exists(parentPath)) return FolderOp.NotFound;
            if (Directory.Exists(target) || File.Exists(target)) return FolderOp.Exists;
            Directory.CreateDirectory(target);
            return FolderOp.Done;
        }, op => done(op, target));
    }

    /// <summary>The Undo of <see cref="CreateFolder"/>: remove the folder only while it is still empty (anything put in it since stays).</summary>
    public static void RemoveEmptyFolder(string fullPath, Action<FolderOp> done)
        => RunDisk("uncreate", fullPath, () =>
        {
            if (!Directory.Exists(fullPath)) return FolderOp.NotFound;
            if (Directory.EnumerateFileSystemEntries(fullPath).Any()) return FolderOp.Exists;
            Directory.Delete(fullPath, recursive: false);
            return FolderOp.Done;
        }, done);

    /// <summary>Move the folder <paramref name="fullPath"/> to the Recycle Bin (never a permanent delete), off the UI thread. Refused with
    /// <see cref="FolderOp.NoRecycleBin"/> where the drive keeps none.</summary>
    public static void RecycleFolder(string fullPath, Action<FolderOp> done)
        => RunDisk("recycle", fullPath, () => Recycle(fullPath), done, sta: true);

    /// <summary>One disk verb on its own thread (STA for the shell's), the outcome mapped to a plain reason, then the music folder walked again.</summary>
    static void RunDisk(string verb, string path, Func<FolderOp> work, Action<FolderOp> done, bool sta = false)
    {
        if (s_fixture) { Playback.ToUi(() => done(FolderOp.Failed)); return; }
        string? root = ContainerOf(path) ?? FolderOf(path);
        var t = new Thread(() =>
        {
            FolderOp op;
            try { op = work(); }
            catch (Exception ex) { op = OpOf(ex); }
            Log.Info("local", "local.disk " + verb + " -> " + op);
            Playback.ToUi(() =>
            {
                if (root is not null && op is FolderOp.Done or FolderOp.NotFound) Rescan(root);
                done(op);
            });
        })
        { IsBackground = true, Name = "Wavee.LocalLibrary.Disk", Priority = ThreadPriority.BelowNormal };
        if (sta) t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    /// <summary>The music folder <paramref name="path"/> is itself, or null.</summary>
    static string? FolderOf(string path)
    {
        foreach (string f in Folders) if (string.Equals(f, path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return f;
        return null;
    }

    static FolderOp OpOf(Exception ex) => ex switch
    {
        DirectoryNotFoundException or FileNotFoundException => FolderOp.NotFound,
        UnauthorizedAccessException => FolderOp.Denied,
        IOException io => (io.HResult & 0xFFFF) switch
        {
            32 or 33 => FolderOp.InUse,                 // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION
            5 => FolderOp.Denied,
            80 or 183 => FolderOp.Exists,               // ERROR_FILE_EXISTS, ERROR_ALREADY_EXISTS
            2 or 3 => FolderOp.NotFound,
            53 or 64 or 67 or 1231 => FolderOp.Offline, // the network path is gone
            _ => FolderOp.Failed,
        },
        _ => FolderOp.Failed,
    };

    // ── the Recycle Bin ─────────────────────────────────────────────────────────────────────────────────────────────

    const uint FoDelete = 3;
    const ushort FofSilent = 0x4, FofNoConfirmation = 0x10, FofAllowUndo = 0x40, FofNoErrorUi = 0x400, FofWantNukeWarning = 0x4000;
    const uint DriveFixed = 3;

    static unsafe FolderOp Recycle(string fullPath)
    {
        string full = fullPath.TrimEnd('\\');
        if (NtDirectory.IsNetworkPath(full)) return FolderOp.NoRecycleBin;
        string? volume = Path.GetPathRoot(full);
        // Only a fixed drive keeps a Recycle Bin; a removable or network one would delete for good.
        if (volume is null || GetDriveTypeW(volume) != DriveFixed) return FolderOp.NoRecycleBin;
        if (!Directory.Exists(full)) return FolderOp.NotFound;
        // pFrom is a list of paths, each ending in a null, the list ending in one more.
        string from = full + "\0\0";
        fixed (char* p = from)
        {
            var op = new ShFileOp
            {
                Func = FoDelete,
                From = p,
                Flags = (ushort)(FofSilent | FofNoConfirmation | FofAllowUndo | FofNoErrorUi | FofWantNukeWarning),
            };
            int rc = SHFileOperationW(&op);
            bool still = Directory.Exists(full);
            Log.Info("local", "local.disk recycle rc=0x" + rc.ToString("X", System.Globalization.CultureInfo.InvariantCulture) + " aborted=" + op.AnyAborted + " still=" + (still ? 1 : 0));
            if (op.AnyAborted != 0 || rc is 0x75 or 1223) return FolderOp.Cancelled;   // DE_OPCANCELLED, ERROR_CANCELLED
            if (rc == 0) return still ? FolderOp.Failed : FolderOp.Done;
            // The shell's codes are its own (DE_*), not Win32 errors, and a folder with a file held open answers one that reads "not found". A
            // folder that is still there was refused: in use or no permission (the move into the bin is one rename, so nothing of it went).
            if (!still) return FolderOp.NotFound;
            return rc is 32 or 33 ? FolderOp.InUse : FolderOp.Denied;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe struct ShFileOp
    {
        public nint Hwnd;
        public uint Func;
        public char* From;
        public char* To;
        public ushort Flags;
        public int AnyAborted;
        public nint NameMappings;
        public char* ProgressTitle;
    }

    [LibraryImport("shell32.dll")]
    private static unsafe partial int SHFileOperationW(ShFileOp* op);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetDriveTypeW(string rootPathName);

    // ── hide a folder from Local Files (local.skip) ─────────────────────────────────────────────────────────────────

    /// <summary>What a hidden folder took out of the index, kept for its Undo.</summary>
    public sealed record HiddenFolder(string FullPath, int RootId, LocalFolder[] Folders, LocalEntry[] Entries);

    /// <summary>"Hide from Local Files" on a folder inside a music folder: leave it out of every walk from now on (`local.skip`; it stays on disk)
    /// and drop its entries and folder records now. The dropped records come back with the Undo (<see cref="UnhideFolder"/>). UI thread.</summary>
    public static HiddenFolder? HideFolder(string fullPath)
    {
        string full = fullPath.TrimEnd('\\');
        if (!s_index.TryRelative(full, out int rootId, out string rel) || rel.Length == 0) return null;
        var skipped = Skipped();
        if (skipped.Add(full)) Platform.Settings.Set(Platform.Keys.MusicSkip, string.Join('|', skipped));
        string prefix = rel + "\\";
        var folders = new List<LocalFolder>();
        foreach (LocalFolder f in s_index.FoldersOf(rootId))
            if (string.Equals(f.RelativePath, rel, StringComparison.OrdinalIgnoreCase) || f.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) folders.Add(f);
        var entries = new List<LocalEntry>();
        foreach (LocalEntry e in s_index.Snapshot())
            if (e.RootId == rootId && e.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) entries.Add(e);
        foreach (LocalFolder f in folders)
        {
            var batch = new LocalScanBatch(rootId, f.RelativePath) { RemoveFolder = true };
            foreach (LocalEntry e in entries) if (string.Equals(LocalIndex.FolderOf(e.RelativePath), f.RelativePath, StringComparison.OrdinalIgnoreCase)) batch.Removes.Add(e.LocalId);
            s_index.Apply(batch);
        }
        Log.Info("local", "local.hide-folder folders=" + folders.Count + " entries=" + entries.Count);
        NoteProgress();
        return new HiddenFolder(full, rootId, folders.ToArray(), entries.ToArray());
    }

    /// <summary>The Undo of <see cref="HideFolder"/>: out of the skip set, its records and entries back under their own ids, then a walk of its music
    /// folder (unchanged folders cost one listing each). UI thread.</summary>
    public static void UnhideFolder(HiddenFolder hidden)
    {
        var skipped = Skipped();
        if (skipped.Remove(hidden.FullPath)) Platform.Settings.Set(Platform.Keys.MusicSkip, string.Join('|', skipped));
        foreach (LocalFolder f in hidden.Folders)
        {
            var batch = new LocalScanBatch(hidden.RootId, f.RelativePath) { Folder = f };
            foreach (LocalEntry e in hidden.Entries) if (string.Equals(LocalIndex.FolderOf(e.RelativePath), f.RelativePath, StringComparison.OrdinalIgnoreCase)) batch.Upserts.Add(e);
            s_index.Apply(batch);
        }
        if (ContainerOf(hidden.FullPath) is { } root) Rescan(root);
        NoteProgress();
    }
}
