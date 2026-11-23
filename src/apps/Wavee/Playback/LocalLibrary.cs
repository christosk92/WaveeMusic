// ── Playback/LocalLibrary.cs ────────────────────────────────────────────────────────────────────────────────────────
// the local library's façade: the music folders (one persisted setting), the index, the scanner and its watchers, and the
// facts a surface reads (state, progress, the path of a LocalId)
//
// Role: SHELL (folder verbs and the facts: UI thread; the load, the scans and the compaction: background threads)
// Spec: C:\WAVEE\fgpu\handoff\request-plans\240-local-playlist-files-implementation.md §3.1, §3.6 "When", §3.11, §5
//       (the `music-folders` inspector state); phase P1b (no UI: Settings ▸ Storage ▸ Music on this PC is P2)
//
// LIFE. `Use(localFolder)` at start (never under --fake): the index loads off-thread from Cache\localindex.bin (+ its log),
// then every folder gets a start-up scan (journal catch-up where it can) and a watcher. Adding a folder walks it; removing
// one drops its entries (they are not "missing": the user took the folder away) and stops its watcher at the next folder
// boundary of a running scan; "Rescan" walks (fingerprints make it cheap). Network folders are reconciled every 45 minutes.
// When the scanner goes idle the change log is compacted into the bin.
//
// SIGNALS. `Epoch` (UI thread) moves at most four times a second while a scan commits folders, and once when it ends; a
// surface reads the counters and `Index.Version` under it. Nothing here runs per frame.
//
// FIXTURES. `InspectFixture` (the inspector's `state kind=music-folders value=…`) seeds a fixed in-memory folder list and index:
// no disk, no thread, no setting written.

using System.Collections.Concurrent;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>The local library (see the file header).</summary>
public static class LocalLibrary
{
    static readonly LocalIndex s_index = new();
    static readonly LocalScan s_scan = new(s_index) { Progressed = NoteProgress, RootState = NoteRootState };
    static readonly ConcurrentDictionary<string, LocalRootState> s_states = new(StringComparer.OrdinalIgnoreCase);
    static readonly Lock s_gate = new();
    static string[] s_folders = [];
    static string? s_binPath;
    static bool s_attached, s_fixture;
    static int s_postPending, s_loaded;
    static Timer? s_post, s_idle, s_reconcile;

    /// <summary>Moves (UI thread) when the index or a scan's progress changed: at most four times a second, and once at the end.</summary>
    public static readonly Signal<int> Epoch = new(0);

    public static LocalIndex Index => s_index;
    public static LocalScan Scanner => s_scan;

    /// <summary>The music folders, as the user added them (full paths).</summary>
    public static IReadOnlyList<string> Folders { get { lock (s_gate) return s_folders; } }

    /// <summary>A scan is running or queued.</summary>
    public static bool Scanning => s_fixture ? s_states.Values.Any(static s => s == LocalRootState.Scanning) : s_scan.Busy;
    /// <summary>Folders committed and files read since start (progress counters).</summary>
    public static int FoldersDone => s_scan.FoldersDone;
    public static int FilesRead => s_scan.FilesRead;

    /// <summary>One folder's reachability as last seen (Unknown before its first scan).</summary>
    public static LocalRootState StateOf(string folder) => s_states.TryGetValue(folder, out LocalRootState s) ? s : LocalRootState.Unknown;

    /// <summary>The library in one word (the inspector's `music-folders` vocabulary): none (no folder), scanning, offline
    /// (every folder unreachable), ready.</summary>
    public static string StateWord
    {
        get
        {
            string[] folders;
            lock (s_gate) folders = s_folders;
            if (folders.Length == 0) return "none";
            if (Scanning) return "scanning";
            bool allOffline = true;
            foreach (string f in folders) if (StateOf(f) is not (LocalRootState.Offline or LocalRootState.SignInNeeded)) { allOffline = false; break; }
            return allOffline ? "offline" : "ready";
        }
    }

    /// <summary>The full path of an indexed file (the play path asks it; any thread, one probe under the index's lock).</summary>
    public static string? PathOf(long localId) => s_index.TryGet(localId, out LocalEntry? e) ? s_index.FullPathOf(e) : null;

    // ── life ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>App start (never under --fake): read the folders, load the index off-thread, then scan and watch each folder.
    /// Opens nothing on the calling thread.</summary>
    public static void Use(string localFolder)
    {
        lock (s_gate)
        {
            if (s_attached) return;
            s_attached = true;
            s_binPath = LocalIndex.PathUnder(localFolder);
            s_folders = Parse(Platform.Settings.Get(Platform.Keys.MusicFolders));
        }
        s_idle = new Timer(static _ => CompactIfIdle(), null, Timeout.Infinite, Timeout.Infinite);
        s_reconcile = new Timer(static _ => ReconcileNetwork(), null, TimeSpan.FromMinutes(45), TimeSpan.FromMinutes(45));
        new Thread(static () =>
        {
            int loaded = s_binPath is { } bin ? s_index.Load(bin) : 0;
            Volatile.Write(ref s_loaded, 1);
            Log.Info("local", "local.index.load entries=" + loaded + " folders=" + Folders.Count + " log=" + s_index.LogBytes);
            foreach (string folder in Folders) StartFolder(folder, startup: true);
            NoteProgress();
        })
        { IsBackground = true, Name = "Wavee.LocalLibrary.Load", Priority = ThreadPriority.BelowNormal }.Start();
    }

    static void StartFolder(string folder, bool startup)
    {
        if (startup) s_scan.RequestStartup(folder);
        else s_scan.RequestFull(folder);
        s_scan.Watch(folder);
    }

    /// <summary>Add a music folder (UI thread). False with a reason word: "missing" (not a folder), "duplicate", "inside" (it
    /// sits in a folder already added), "contains" (it holds one already added: remove that one first).</summary>
    public static bool AddFolder(string path, out string reason)
    {
        string folder;
        try { folder = Normalize(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { reason = "missing"; return false; }
        if (!Directory.Exists(folder)) { reason = "missing"; return false; }
        lock (s_gate)
        {
            foreach (string f in s_folders)
            {
                if (string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)) { reason = "duplicate"; return false; }
                if (Contains(f, folder)) { reason = "inside"; return false; }
                if (Contains(folder, f)) { reason = "contains"; return false; }
            }
            s_folders = [.. s_folders, folder];
            Persist();
        }
        reason = "";
        if (s_attached && Volatile.Read(ref s_loaded) != 0) StartFolder(folder, startup: false);
        NoteProgress();
        return true;
    }

    /// <summary>Remove a music folder (UI thread): its watcher stops, a running scan of it stops at the next folder boundary, and
    /// its entries leave the index (background).</summary>
    public static bool RemoveFolder(string path)
    {
        string folder = Normalize(path);
        lock (s_gate)
        {
            int at = Array.FindIndex(s_folders, f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
            if (at < 0) return false;
            folder = s_folders[at];
            s_folders = [.. s_folders[..at], .. s_folders[(at + 1)..]];
            Persist();
        }
        s_states.TryRemove(folder, out _);
        if (!s_fixture)
        {
            s_scan.Forget(folder);
            new Thread(() =>
            {
                s_scan.Unwatch(folder);
                // A running scan of it stops at its next folder boundary; drop the root only once it has.
                for (int i = 0; i < 600 && s_scan.IsRunning(folder); i++) Thread.Sleep(50);
                foreach (LocalRoot r in s_index.Roots())
                    if (string.Equals(r.Path, folder, StringComparison.OrdinalIgnoreCase)) s_index.RemoveRoot(r.RootId);
                NoteProgress();
            })
            { IsBackground = true, Name = "Wavee.LocalLibrary.Remove", Priority = ThreadPriority.BelowNormal }.Start();
        }
        NoteProgress();
        return true;
    }

    /// <summary>"Rescan now": a warm walk of one folder or of all of them (unchanged folders cost one listing each).</summary>
    public static void Rescan(string? folder = null)
    {
        if (s_fixture || Volatile.Read(ref s_loaded) == 0) return;
        foreach (string f in Folders)
            if (folder is null || string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)) StartFolder(f, startup: false);
    }

    static void ReconcileNetwork()
    {
        if (s_fixture || Volatile.Read(ref s_loaded) == 0) return;
        foreach (string f in Folders) if (NtDirectory.IsNetworkPath(f)) s_scan.RequestFull(f);
    }

    static void CompactIfIdle()
    {
        if (s_scan.Busy) { s_idle?.Change(30_000, Timeout.Infinite); return; }
        if (s_index.LogBytes > 64) s_index.Compact();
    }

    // ── progress ─────────────────────────────────────────────────────────────────────────────────────────────────────

    static void NoteProgress()
    {
        // At most four posts a second; the post bumps the signal on the UI thread.
        if (Interlocked.Exchange(ref s_postPending, 1) != 0) return;
        s_post ??= new Timer(static _ => Playback.ToUi(static () => { Volatile.Write(ref s_postPending, 0); Epoch.Value++; }), null, Timeout.Infinite, Timeout.Infinite);
        s_post.Change(250, Timeout.Infinite);
        if (!s_scan.Busy) s_idle?.Change(30_000, Timeout.Infinite);   // idle: compact the log in 30 s unless a scan starts
    }

    static void NoteRootState(string folder, LocalRootState state)
    {
        s_states[folder] = state;
        NoteProgress();
    }

    // ── the setting ──────────────────────────────────────────────────────────────────────────────────────────────────

    static string[] Parse(string text)
    {
        if (text.Length == 0) return [];
        var list = new List<string>();
        foreach (string part in text.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!list.Exists(p => string.Equals(p, part, StringComparison.OrdinalIgnoreCase))) list.Add(part);
        return list.ToArray();
    }

    static void Persist()
    {
        if (s_fixture) return;   // fixtures never write the setting
        Platform.Settings.Set(Platform.Keys.MusicFolders, string.Join('|', s_folders));
    }

    /// <summary>A folder as stored: full, no trailing separator (a drive root keeps its backslash).</summary>
    public static string Normalize(string path)
    {
        string full = Path.GetFullPath(path.Trim());
        return full.Length > 3 ? full.TrimEnd('\\', '/') : full;
    }

    static bool Contains(string outer, string inner)
        => inner.Length > outer.Length && inner.StartsWith(outer, StringComparison.OrdinalIgnoreCase)
           && (outer.EndsWith('\\') || inner[outer.Length] == '\\');

    // ── the inspector's fixtures (§5) ───────────────────────────────────────────────────────────────────────────────

    /// <summary>`state kind=music-folders value=none|scanning|ready|offline`: a fixed in-memory folder list and index (no disk, no
    /// thread, no setting). The next `InspectFixture("none")` empties it again.</summary>
    public static void InspectFixture(string value)
    {
        s_fixture = true;
        s_index.Detach();
        s_index.Clear();
        s_states.Clear();
        const string music = @"C:\Users\Listener\Music", usb = @"E:\Music";
        lock (s_gate) s_folders = value == "none" ? [] : [music, usb];
        if (value != "none")
        {
            s_index.SetRoot(new LocalRoot(1, music, 0x1234_ABCD, 0, 0, DateTime.UtcNow.Ticks));
            s_index.SetRoot(new LocalRoot(2, usb, 0x0BAD_F00D, 0, 0, DateTime.UtcNow.Ticks));
            long now = DateTime.UtcNow.Ticks;
            var album = new LocalScanBatch(1, @"Nils Frahm\Spaces") { Folder = new LocalFolder(1, @"Nils Frahm\Spaces", 101, 0xF00D, ["cover.jpg"], true) };
            string[] titles = ["An Aborted Beginning", "Says", "Hammers", "Improvisation for Coughs and a Cell Phone", "Familiar", "Ode", "Over There, It's Raining", "Ambre"];
            int count = value == "scanning" ? 3 : titles.Length;
            for (int i = 0; i < count; i++)
            {
                var tags = new LocalTagSet(LocalKind.Audio, titles[i], ["Nils Frahm"], "Spaces", "Nils Frahm", i + 1, 1, 2013, 180_000 + i * 31_000, false, 920,
                    i == 0 ? new LocalCover(4096, 210_000, LocalCoverMime.Jpeg, LocalCoverEncoding.Raw, 3) : LocalCover.None, 0, 0, true);
                album.Upserts.Add(new LocalEntry(i + 1, 1, $@"Nils Frahm\Spaces\{i + 1:00} {titles[i]}.flac", 0x1234_ABCD, (UInt128)(1000 + i), 30_000_000 + i, now, now, 0x20,
                    Spotify.Audio.Format.Flac, tags, titles[i], LocalState.Present, now, true, ""));
            }
            s_index.Apply(album);
            var loose = new LocalScanBatch(1, "") { Folder = new LocalFolder(1, "", 100, 0xBEEF, [], true) };
            var untagged = new LocalTagSet(LocalKind.Audio, "", [], "", "", 0, 0, 0, 214_000, true, 0, LocalCover.None, 0, 0, true);
            loose.Upserts.Add(new LocalEntry(20, 1, "voice memo 03.mp3", 0x1234_ABCD, 2000, 3_400_000, now, now, 0x20, Spotify.Audio.Format.Mp3, untagged, "voice memo 03", LocalState.Present, now, true, ""));
            loose.Upserts.Add(new LocalEntry(21, 1, "demo take.wav", 0x1234_ABCD, 2001, 41_000_000, now, now, 0x20, Spotify.Audio.Format.Unknown, untagged with { DurationMs = 0, DurationEstimated = false },
                "demo take", LocalState.Present, now, false, "No WAV decoder in this build"));
            loose.Upserts.Add(new LocalEntry(22, 1, "old mix.mp3", 0x1234_ABCD, 2002, 7_000_000, now, now, 0x20, Spotify.Audio.Format.Mp3, untagged, "old mix", LocalState.Missing, now - TimeSpan.TicksPerDay * 3, true, ""));
            s_index.Apply(loose);
            var usbFolder = new LocalScanBatch(2, "Live") { Folder = new LocalFolder(2, "Live", 300, 0xCAFE, [], true) };
            usbFolder.Upserts.Add(new LocalEntry(30, 2, @"Live\Encore.m4a", 0x0BAD_F00D, 0, 9_000_000, now, 0, 0x20, Spotify.Audio.Format.Aac,
                new LocalTagSet(LocalKind.Audio, "Encore", ["Ólafur Arnalds", "Nils Frahm"], "Live", "", 1, 1, 2015, 402_000, false, 256, LocalCover.None, 0, 0, true),
                "Encore", LocalState.Present, now, true, ""));
            usbFolder.Upserts.Add(new LocalEntry(31, 2, @"Live\Unreleased.flac", 0x0BAD_F00D, 0, 52_000_000, now, 0, 0x0040_0020, Spotify.Audio.Format.Unknown,
                new LocalTagSet(LocalKind.Audio, "", [], "", "", 0, 0, 0, 0, false, 0, LocalCover.None, 0, 0, true), "Unreleased", LocalState.CloudOnly, now, true, ""));
            s_index.Apply(usbFolder);
            s_states[music] = value switch { "scanning" => LocalRootState.Scanning, "offline" => LocalRootState.Offline, _ => LocalRootState.Online };
            s_states[usb] = value == "offline" ? LocalRootState.Offline : LocalRootState.Online;
        }
        Epoch.Value++;
    }
}
