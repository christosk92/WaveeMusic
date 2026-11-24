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
// After a resume from sleep every folder is caught up again (the journal where it can, else a warm walk; network folders a
// walk, after the SMB redirector's 10 s); when a volume arrives (a USB drive back in, a new volume mounted) every folder that is
// offline or unwatched is walked and watched again. When the scanner goes idle the change log is compacted into the bin.
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
public static partial class LocalLibrary
{
    static readonly LocalIndex s_index = new();
    static readonly LocalScan s_scan = new(s_index)
    {
        Progressed = NoteProgress,
        RootState = NoteRootState,
        MissingGrace = static () => TimeSpan.FromDays(Math.Clamp(Platform.Settings.Get(Platform.Keys.MusicMissingDays), 7, 365)),
    };
    static readonly ConcurrentDictionary<string, LocalRootState> s_states = new(StringComparer.OrdinalIgnoreCase);
    static readonly Lock s_gate = new();
    static string[] s_folders = [];
    static string? s_binPath;
    static bool s_attached, s_fixture;
    static int s_postPending, s_loaded;
    static Timer? s_post, s_idle, s_reconcile, s_resume, s_resumeNetwork, s_arrival;
    static IDisposable? s_volumes;

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
        s_resume = new Timer(static _ => CatchUpAfterResume(), null, Timeout.Infinite, Timeout.Infinite);
        s_arrival = new Timer(static _ => WalkArrived(), null, Timeout.Infinite, Timeout.Infinite);
        s_resumeNetwork = new Timer(static _ => { foreach (string f in Folders) if (NtDirectory.IsNetworkPath(f)) s_scan.RequestFull(f); }, null, Timeout.Infinite, Timeout.Infinite);
        // Playback.Os holds the PowerSession subscription; a handler attached later is still raised (OS power thread: no blocking).
        try { FluentGpu.WindowsApi.Power.PowerSession.Resumed += static () => s_resume?.Change(5_000, Timeout.Infinite); }
        catch (Exception ex) { Log.Warn("local", "local.power resume subscribe failed", ex); }
        // A volume arrived: give the mount two seconds to settle, then walk what was away (PnP thread: no blocking).
        s_volumes = LocalScan.WatchVolumeArrivals(static () => s_arrival?.Change(2_000, Timeout.Infinite));
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
        if (Platform.Settings.Get(Platform.Keys.MusicWatch)) s_scan.Watch(folder);
    }

    /// <summary>"Watch folders for changes" (S3): on starts a watcher per folder and catches up (the journal where it can);
    /// off stops them. UI thread; persists the setting.</summary>
    public static void SetWatching(bool on)
    {
        Platform.Settings.Set(Platform.Keys.MusicWatch, on);
        if (s_fixture || Volatile.Read(ref s_loaded) == 0) return;
        foreach (string f in Folders)
        {
            if (on) { s_scan.Watch(f); s_scan.RequestStartup(f); }
            else s_scan.Unwatch(f);
        }
    }

    /// <summary>The user's Music folder when it exists and is not already covered by a music folder (the "Add my Music folder"
    /// offer, S4 and S3), else null.</summary>
    public static string? DefaultMusicFolder
    {
        get
        {
            // Fixtures never show this machine's account folder (a render may be published).
            if (s_fixture) return Folders.Count == 0 ? @"C:\Users\Listener\Music" : null;
            string music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            if (music.Length == 0 || !Directory.Exists(music)) return null;
            string folder = Normalize(music);
            foreach (string f in Folders)
                if (string.Equals(f, folder, StringComparison.OrdinalIgnoreCase) || Contains(f, folder)) return null;
            return folder;
        }
    }

    /// <summary>"Show in Explorer" for a music folder (S3). UI thread; never throws.</summary>
    public static void Reveal(string folder)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Log.Warn("local", "local.reveal failed", ex); }
    }

    /// <summary>Add a music folder (UI thread). False with a reason word: "missing" (not a folder), "duplicate", "inside" (it
    /// sits in a folder already added). True with reason "combined" when it holds folders already added: they are replaced by
    /// it (S11 "Combined into …"); otherwise reason "".</summary>
    public static bool AddFolder(string path, out string reason)
    {
        string folder;
        try { folder = Normalize(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { reason = "missing"; return false; }
        if (!Directory.Exists(folder)) { reason = "missing"; return false; }
        var replaced = new List<string>();
        lock (s_gate)
        {
            foreach (string f in s_folders)
            {
                if (string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)) { reason = "duplicate"; return false; }
                if (Contains(f, folder)) { reason = "inside"; return false; }
                if (Contains(folder, f)) replaced.Add(f);
            }
        }
        foreach (string child in replaced) RemoveFolder(child);   // their files come back under the parent's walk
        lock (s_gate)
        {
            s_folders = [.. s_folders, folder];
            Persist();
        }
        reason = replaced.Count > 0 ? "combined" : "";
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

    /// <summary>After a resume: the journal catch-up (else a warm walk) for local folders; a walk for network folders once the
    /// SMB redirector's listing cache (10 s) has passed. Watchers that died with the network reopen on their own.</summary>
    static void CatchUpAfterResume()
    {
        if (s_fixture || Volatile.Read(ref s_loaded) == 0) return;
        Log.Info("local", "local.resume folders=" + Folders.Count);
        foreach (string f in Folders)
        {
            if (!NtDirectory.IsNetworkPath(f)) s_scan.RequestStartup(f);
        }
        s_resumeNetwork?.Change(10_000, Timeout.Infinite);
    }

    /// <summary>A volume arrived: every folder that is offline or has no watcher (a USB drive taken out) is walked and watched.</summary>
    static void WalkArrived()
    {
        if (s_fixture || Volatile.Read(ref s_loaded) == 0) return;
        foreach (string f in Folders)
        {
            if (s_scan.IsWatched(f) && StateOf(f) is not (LocalRootState.Offline or LocalRootState.Unknown)) continue;
            if (!Directory.Exists(f)) continue;
            Log.Info("local", "local.volume arrived; walking a folder that was away");
            StartFolder(f, startup: false);
        }
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
        s_post ??= new Timer(static _ => Playback.ToUi(static () => { Volatile.Write(ref s_postPending, 0); Epoch.Value++; MaybeRestate(); }), null, Timeout.Infinite, Timeout.Infinite);
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

    /// <summary>`state kind=music-folders value=none|scanning|ready|offline|changed|missing|listing`: a fixed in-memory folder list and index (no disk, no
    /// thread, no setting). `changed` is ready plus a background change just restated (+4 added · 1 moved: the chip's "Updated · +4", four new rows
    /// tagged "New", "Ambre" tagged "Moved"); `missing` is ready with three missing songs and no other unplayable file (S6); `listing` is the first
    /// scan's first second (nothing read, no total). Every value but `changed` clears the session's changes, so the diff from the previous fixture
    /// announces nothing.
    /// The next `InspectFixture("none")` empties it again.</summary>
    public static void InspectFixture(string value)
    {
        s_fixture = true;
        s_fixtureProgress = default;
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
            int count = value == "scanning" ? 3 : value == "listing" ? 0 : titles.Length;
            for (int i = 0; i < count; i++)
            {
                var tags = new LocalTagSet(LocalKind.Audio, titles[i], ["Nils Frahm"], "Spaces", "Nils Frahm", i + 1, 1, 2013, 180_000 + i * 31_000, false, 920,
                    i == 0 ? new LocalCover(4096, 210_000, LocalCoverMime.Jpeg, LocalCoverEncoding.Raw, 3) : LocalCover.None, 0, 0, true, 96_000, 24, 2);
                album.Upserts.Add(new LocalEntry(i + 1, 1, $@"Nils Frahm\Spaces\{i + 1:00} {titles[i]}.flac", 0x1234_ABCD, (UInt128)(1000 + i), 30_000_000 + i, now, now, 0x20,
                    Spotify.Audio.Format.Flac, tags, titles[i], LocalState.Present, now, true, "", now));
            }
            s_index.Apply(album);
            var loose = new LocalScanBatch(1, "") { Folder = new LocalFolder(1, "", 100, 0xBEEF, [], true) };
            var untagged = new LocalTagSet(LocalKind.Audio, "", [], "", "", 0, 0, 0, 214_000, true, 0, LocalCover.None, 0, 0, true, 44_100, 0, 2);
            if (value != "listing")
            {
                loose.Upserts.Add(new LocalEntry(20, 1, "voice memo 03.mp3", 0x1234_ABCD, 2000, 3_400_000, now, now, 0x20, Spotify.Audio.Format.Mp3, untagged, "voice memo 03", LocalState.Present, now, true, "", now));
                // `missing` is the review's case (S6): every song the bar counts is missing, so the unplayable demo is not there.
                if (value != "missing")
                    loose.Upserts.Add(new LocalEntry(21, 1, "demo take.wav", 0x1234_ABCD, 2001, 41_000_000, now, now, 0x20, Spotify.Audio.Format.Unknown, untagged with { DurationMs = 0, DurationEstimated = false },
                        "demo take", LocalState.Present, now, false, "No WAV decoder in this build", now));
                loose.Upserts.Add(new LocalEntry(22, 1, "old mix.mp3", 0x1234_ABCD, 2002, 7_000_000, now, now, 0x20, Spotify.Audio.Format.Mp3, untagged, "old mix", LocalState.Missing, now - TimeSpan.TicksPerDay * 3, true, "", now));
            }
            if (value == "missing")
            {
                // Two more gone, the board's (boards240/Missing): a tagged pair last seen 12 and 6 days ago.
                loose.Upserts.Add(new LocalEntry(23, 1, @"Live\Attic Tapes.flac", 0x1234_ABCD, 2003, 31_000_000, now, now, 0x20, Spotify.Audio.Format.Flac,
                    new LocalTagSet(LocalKind.Audio, "Attic Tapes", ["Nova Kite"], "Satellite Heart", "Nova Kite", 3, 1, 2021, 251_000, false, 900, LocalCover.None, 0, 0, true, 44_100, 16, 2),
                    "Attic Tapes", LocalState.Missing, now - TimeSpan.TicksPerDay * 12, true, "", now));
                loose.Upserts.Add(new LocalEntry(24, 1, @"Coral Hours\Rewind.flac", 0x1234_ABCD, 2004, 29_000_000, now, now, 0x20, Spotify.Audio.Format.Flac,
                    new LocalTagSet(LocalKind.Audio, "Rewind", ["Coral Hours"], "Wavee Sessions Vol. 2", "Coral Hours", 4, 1, 2022, 239_000, false, 900, LocalCover.None, 0, 0, true, 44_100, 16, 2),
                    "Rewind", LocalState.Missing, now - TimeSpan.TicksPerDay * 6, true, "", now));
            }
            if (value == "changed")
            {
                // The background change the chip and the pill announce (S5): four new songs in a folder that just arrived.
                string[] fresh = ["Paper Lanterns", "Night Ferry", "Glass Rain", "Static Bloom"];
                string[] by = ["Mia Solace", "Coral Hours", "Nova Kite", "Sable & Sun"];
                for (int i = 0; i < fresh.Length; i++)
                    loose.Upserts.Add(new LocalEntry(40 + i, 1, $@"New\{fresh[i]}.flac", 0x1234_ABCD, (UInt128)(4000 + i), 28_000_000 + i, now, now, 0x20, Spotify.Audio.Format.Flac,
                        new LocalTagSet(LocalKind.Audio, fresh[i], [by[i]], "", by[i], 0, 0, 2024, 201_000 + i * 17_000, false, 900, LocalCover.None, 0, 0, true, 44_100, 16, 2),
                        fresh[i], LocalState.Present, now, true, "", now));
            }
            if (loose.Upserts.Count > 0) s_index.Apply(loose);
            var usbFolder = new LocalScanBatch(2, "Live") { Folder = new LocalFolder(2, "Live", 300, 0xCAFE, [], true) };
            usbFolder.Upserts.Add(new LocalEntry(30, 2, @"Live\Encore.m4a", 0x0BAD_F00D, 0, 9_000_000, now, 0, 0x20, Spotify.Audio.Format.Aac,
                new LocalTagSet(LocalKind.Audio, "Encore", ["Ólafur Arnalds", "Nils Frahm"], "Live", "", 1, 1, 2015, 402_000, false, 256, LocalCover.None, 0, 0, true, 44_100, 0, 2),
                "Encore", LocalState.Present, now, true, "", now));
            usbFolder.Upserts.Add(new LocalEntry(31, 2, @"Live\Unreleased.flac", 0x0BAD_F00D, 0, 52_000_000, now, 0, 0x0040_0020, Spotify.Audio.Format.Unknown,
                new LocalTagSet(LocalKind.Audio, "", [], "", "", 0, 0, 0, 0, false, 0, LocalCover.None, 0, 0, true, 0, 0, 0), "Unreleased", LocalState.CloudOnly, now, true, "", now));
            if (value != "listing") s_index.Apply(usbFolder);
            // The last scan as the board draws it: 2 minutes ago, 1.8 s (ready and offline); none while the first scan runs.
            s_scan.Last.Clear();
            if (value is not ("scanning" or "listing"))
                s_scan.Last[music] = new LocalScanStats { FinishedTicks = DateTime.UtcNow.Ticks - TimeSpan.TicksPerMinute * 2, TotalMs = 1800 };
            s_fixtureProgress = value switch
            {
                "scanning" => new ScanProgress(true, music, ScanPhase.Reading, 412, 4812, 1930, 4812, 1930, 48, 40, 212),
                // The first second of the first scan: the folders are being listed, no total yet (an indeterminate bar).
                "listing" => new ScanProgress(true, music, ScanPhase.Listing, 3, 0, 0, 0, 0, 0, 0, 0),
                _ => default,
            };
            s_states[music] = value switch { "scanning" or "listing" => LocalRootState.Scanning, "offline" => LocalRootState.Offline, _ => LocalRootState.Online };
            s_states[usb] = value == "offline" ? LocalRootState.Offline : LocalRootState.Online;
        }
        Epoch.Value++;
        RestateNow();   // the Local Files list shows the fixture at once (no thread)
        if (value == "changed")
        {
            // A background change as the restate pass counts one (S5): the chip's "Updated · +4", the flyout's "What changed".
            LastChanges = new SessionChanges(4, 1, 0, 0, 0);
            Changes = new SessionChanges(4, 1, 0, 0, 0);
            // What the rows say for the session: the four new ones "New", "Ambre" "Moved" (from where it was before).
            s_new.Clear();
            s_movedFrom.Clear();
            for (long id = 40; id < 44; id++) s_new.Add(id);
            s_movedFrom[8] = music + @"\Unsorted\Ambre.flac";
            Restated.Value++;
        }
        else
        {
            // Every other value is a seeded library, not a change: the diff against the previous fixture announces nothing.
            LastChanges = default;
            Changes = default;
            s_new.Clear();
            s_movedFrom.Clear();
        }
    }
}
