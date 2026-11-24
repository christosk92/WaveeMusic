// ── Playback/LocalScan.cs ───────────────────────────────────────────────────────────────────────────────────────────
// the scan: list the music folders with the NT walker, skip unchanged folders by fingerprint, pair moves before and after
// reading, read new and changed files, mark the rest missing, commit folder by folder
//
// Role: CORE (one coordinator thread "Wavee.LocalScan" below normal; per-root list and read workers below normal; never the
//       UI or audio thread, never the thread pool)
// Spec: C:\WAVEE\fgpu\handoff\request-plans\240-local-playlist-files-implementation.md §3.6 (the 7 steps, cloud files, live
//       updates, targets), §3.16 (the reachability probe and the SMB debounce), D13, D14;
//       C:\WAVEE\fgpu\handoff\localindex-research\README.md (the ten rules)
//
// A SCAN OF ONE ROOT, in order (§3.6):
//   1. load        the index is loaded once by `LocalLibrary` (background) before the first scan; the app reads it at once.
//   2. catch up    local NTFS/ReFS at start-up: the unprivileged USN journal from the root's stored (JournalId, NextUsn)
//                  (`LocalScan.Journal.cs`); each record's parent id marks a folder dirty. A changed journal id, purged
//                  entries, a refused call or too many records: a full warm walk instead. The walk is always correct.
//   3. walk        list every folder (or only the dirty ones and any folder they hold that the index does not know), hash
//                  the listing (the fingerprint), compare with the stored one: equal = nothing more, not a file touched.
//                  Different: diff by name; an exact (Size, WriteTicks, ChangeTicks) keeps its entry, never "newer than".
//   4. pair before a new path is first a move: the same volume + file id (NTFS/ReFS), else the same (size, write, extension)
//                  (FAT/exFAT/SMB). The same path with a new file id is a tag editor's safe-save: a modification.
//   5. read        new and changed files through `LocalTags.TryRead` on a handle opened relative to the folder's handle,
//                  read workers per D14 (separate from the list workers). Cloud placeholders are never opened.
//   6. pair after  what vanished and was not paired: a file id still on the volume is found by `OpenFileById` +
//                  `GetFinalPathNameByHandleW` (re-pointed inside a music folder, "moved to" outside); else the same tags
//                  key or the same file name with a length within 1 s, the longest shared path tail first.
//   7. mark        the rest Missing (only in a folder that was listed successfully under an online root), commit each
//                  folder (records + fingerprint, `Complete` only if every read succeeded), purge 60-day missing after a
//                  complete walk, store the journal cursor taken at the scan's start.
// One summary line per scan, always on: `local.scan ...` (folders listed, skipped by fingerprint, files read, bytes read,
// moves paired, missing marked, cloud-only, errors, ms per phase). Paths are never logged (they hold the account name).
//
// COMMITS. Each folder commits as soon as its listing and its reads are done (first rows visible within the first folders).
// A new file that step 6 later pairs with a vanished one is committed under a fresh id first and re-keyed to the old id at the
// end of the scan; nothing durable points at a seconds-old id.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Wavee;

/// <summary>The numbers of one root's scan (the summary line, the probe and the inspector verb print them).</summary>
public sealed class LocalScanStats
{
    public string Mode = "full";
    public int RootId;
    public NtMedium Medium;
    public string FileSystem = "";
    public bool Offline, Complete, Cancelled, ExtdFallback;
    public string Reach = "", Journal = "";
    public int FoldersListed, FoldersSkipped, FoldersFailed, FoldersGone, DirtyFolders;
    public int ToRead;
    public long StartedTicks, FinishedTicks;
    public int FilesSeen, FilesRead, FilesUnchanged, MovesPaired, MovesById, MovedOutside, PairedAfterRead, MissingMarked, Reappeared,
        CloudOnly, Errors, Purged, JournalRecords, ListWorkers, ReadWorkers;
    public long BytesRead;
    public double JournalMs, WalkMs, FinishMs, TotalMs, FirstCommitMs = -1;

    public string Summary()
    {
        var c = CultureInfo.InvariantCulture;
        return string.Create(c, $"local.scan root={RootId} mode={Mode} medium={Medium} fs={FileSystem} complete={(Complete ? 1 : 0)} offline={(Offline ? 1 : 0)}"
            + $" folders={FoldersListed} skipped={FoldersSkipped} failed={FoldersFailed} gone={FoldersGone} dirty={DirtyFolders}"
            + $" files={FilesSeen} read={FilesRead} unchanged={FilesUnchanged} bytes={BytesRead} moves={MovesPaired} byId={MovesById}"
            + $" outside={MovedOutside} pairedAfter={PairedAfterRead} missing={MissingMarked} back={Reappeared} cloud={CloudOnly}"
            + $" errors={Errors} purged={Purged} journal={Journal}:{JournalRecords} workers={ListWorkers}/{ReadWorkers}"
            + $" ms={TotalMs:0.0} journalMs={JournalMs:0.0} walkMs={WalkMs:0.0} finishMs={FinishMs:0.0} firstMs={FirstCommitMs:0.0}");
    }
}

/// <summary>The scanner of one <see cref="LocalIndex"/> (see the file header). Thread-safe entry points; the work runs on
/// its own threads.</summary>
public sealed partial class LocalScan
{
    static readonly string[] s_imageExtensions = [".jpg", ".jpeg", ".png", ".webp"];
    const int StatusObjectNameNotFound = unchecked((int)0xC000_0034), StatusObjectPathNotFound = unchecked((int)0xC000_003A);

    readonly LocalIndex _index;
    readonly Lock _gate = new();
    readonly Dictionary<string, RootWork> _pending = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _removed = new(StringComparer.OrdinalIgnoreCase);
    readonly AutoResetEvent _wake = new(false);
    Thread? _thread;
    LocalScanStats? _current;
    string? _currentRoot;

    /// <summary>The running scan's live numbers (null when idle) and its folder. Read on any thread; the numbers move.</summary>
    public (LocalScanStats? Stats, string? Root) Current
    {
        get { lock (_gate) return (_current, _currentRoot); }
    }
    string? _running;
    int _foldersDone, _filesRead, _busy;

    /// <summary>Called (any scan thread) after each folder commit and when a scan starts or ends; the façade coalesces it
    /// into one UI-thread signal bump at most four times a second.</summary>
    public Action? Progressed { get; set; }
    /// <summary>Called (scan thread) when a root's reachability is known: (root path, state word).</summary>
    public Action<string, LocalRootState>? RootState { get; set; }
    /// <summary>The last finished scan's numbers, per root path.</summary>
    public ConcurrentDictionary<string, LocalScanStats> Last { get; } = new(StringComparer.OrdinalIgnoreCase);

    public LocalScan(LocalIndex index) => _index = index;

    /// <summary>How long a missing file is kept after a complete walk (the "Keep missing songs for" setting).</summary>
    public Func<TimeSpan> MissingGrace { get; set; } = static () => LocalIndex.MissingGrace;

    public LocalIndex Index => _index;
    /// <summary>Folders committed since the façade started (progress counter).</summary>
    public int FoldersDone => Volatile.Read(ref _foldersDone);
    /// <summary>Files read since the façade started (progress counter).</summary>
    public int FilesRead => Volatile.Read(ref _filesRead);
    /// <summary>A scan is running or queued.</summary>
    public bool Busy { get { lock (_gate) return Volatile.Read(ref _busy) != 0 || _pending.Count > 0; } }

    sealed class RootWork
    {
        public bool Full, Startup;
        public HashSet<string>? Dirty;
    }

    // ── requests ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>At start, after the load: catch up through the journal where it can, else a warm walk.</summary>
    public void RequestStartup(string root) => Enqueue(root, w => w.Startup = true);

    /// <summary>A full (warm, fingerprinted) walk of a root: a folder add, "Rescan now", a watcher overflow, a reconnect.</summary>
    public void RequestFull(string root) => Enqueue(root, w => w.Full = true);

    /// <summary>Rescan these folders of a root (relative paths; the watcher's debounced dirty set).</summary>
    public void RequestDirty(string root, IEnumerable<string> folders) => Enqueue(root, w =>
    {
        if (w.Full) return;
        w.Dirty ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string f in folders) w.Dirty.Add(f);
    });

    /// <summary>A removed folder: dropped from the queue, and a running scan of it stops at the next folder boundary.</summary>
    public void Forget(string root)
    {
        lock (_gate)
        {
            _pending.Remove(root);
            if (string.Equals(_running, root, StringComparison.OrdinalIgnoreCase)) _removed.Add(root);
        }
    }

    void Enqueue(string root, Action<RootWork> merge)
    {
        lock (_gate)
        {
            _removed.Remove(root);
            if (!_pending.TryGetValue(root, out RootWork? work)) _pending[root] = work = new RootWork();
            merge(work);   // a request for a root already queued merges into it
            if (_thread is null)
            {
                _thread = new Thread(Loop) { IsBackground = true, Name = "Wavee.LocalScan", Priority = ThreadPriority.BelowNormal };
                _thread.Start();
            }
        }
        _wake.Set();
    }

    void Loop()
    {
        NtDirectory.ExposePlaceholders();
        while (true)
        {
            string root;
            RootWork work;
            lock (_gate)
            {
                using var e = _pending.GetEnumerator();
                if (!e.MoveNext()) { root = ""; work = null!; }
                else { root = e.Current.Key; work = e.Current.Value; _pending.Remove(root); _running = root; Volatile.Write(ref _busy, 1); }
            }
            if (work is null) { _wake.WaitOne(); continue; }
            Progressed?.Invoke();
            try
            {
                LocalScanStats stats = ScanRoot(root, work.Startup && !work.Full, work.Full ? null : work.Dirty);
                Last[root] = stats;
                Log.Info("local", stats.Summary());
            }
            catch (Exception ex)
            {
                // Never let one root's failure end the scan thread: the next request walks it again.
                Log.Warn("local", "local.scan failed", ex);
            }
            lock (_gate) { _running = null; _removed.Remove(root); Volatile.Write(ref _busy, 0); }
            Progressed?.Invoke();
        }
    }

    bool Cancelled(string root)
    {
        lock (_gate) return _removed.Contains(root);
    }

    /// <summary>Is a scan of this root running now (the façade waits for it to stop before dropping a removed root)?</summary>
    public bool IsRunning(string root)
    {
        lock (_gate) return string.Equals(_running, root, StringComparison.OrdinalIgnoreCase);
    }

    // ── one root ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Scan one root now, on the calling thread (its workers are threads of their own). <paramref name="startup"/>:
    /// try the journal first; <paramref name="dirty"/>: only these folders (and folders inside them the index does not know);
    /// neither: a full warm walk. The probe and the inspector verb call it directly on a private index.</summary>
    public LocalScanStats ScanRoot(string rootPath, bool startup, IReadOnlyCollection<string>? dirty)
    {
        var total = Stopwatch.StartNew();
        var stats = new LocalScanStats { Mode = dirty is not null ? "dirty" : startup ? "startup" : "full", StartedTicks = DateTime.UtcNow.Ticks };
        lock (_gate) { _current = stats; _currentRoot = rootPath; }
        try { return ScanRootCore(rootPath, startup, dirty, stats, total); }
        finally
        {
            stats.FinishedTicks = DateTime.UtcNow.Ticks;
            lock (_gate) { if (ReferenceEquals(_current, stats)) { _current = null; _currentRoot = null; } }
        }
    }

    LocalScanStats ScanRootCore(string rootPath, bool startup, IReadOnlyCollection<string>? dirty, LocalScanStats stats, Stopwatch total)
    {
        long ioStart = ProcessReadBytes();
        LocalRoot root = RootFor(rootPath);
        stats.RootId = root.RootId;

        // Reachability first: a dead SMB server can block an open for 60-240 s, so a network root is probed on its own thread.
        LocalRootState reach = NtDirectory.IsNetworkPath(rootPath) ? ProbeReach(rootPath) : LocalRootState.Online;
        stats.Reach = reach.ToString();
        SafeFileHandle? rootHandle = reach is LocalRootState.Offline or LocalRootState.SignInNeeded ? null : NtDirectory.OpenDirectory(null, rootPath, out int openStatus);
        if (rootHandle is null)
        {
            stats.Offline = true;   // an offline root marks nothing: its entries keep their state
            RootState?.Invoke(rootPath, reach == LocalRootState.SignInNeeded ? reach : LocalRootState.Offline);
            stats.TotalMs = total.Elapsed.TotalMilliseconds;
            return stats;
        }
        RootState?.Invoke(rootPath, reach == LocalRootState.Online ? LocalRootState.Scanning : reach);
        try
        {
            NtVolume volume = NtDirectory.VolumeOf(rootHandle, rootPath);
            stats.Medium = volume.Medium;
            stats.FileSystem = volume.FileSystem;
            UInt128 rootId = NtDirectory.FileIdOf(rootHandle);

            // The journal cursor is taken BEFORE the walk: a change made while walking is then read again next time, never lost.
            ulong journalId = 0;
            long journalNext = 0;
            var journalClock = Stopwatch.StartNew();
            int journalError = 0;
            bool haveJournal = volume.SupportsJournal && TryQueryJournal(rootPath, out journalId, out _, out journalNext, out journalError);
            stats.Journal = !volume.SupportsJournal ? "unsupported" : haveJournal ? "query-ok" : "query-error-" + journalError.ToString(CultureInfo.InvariantCulture);
            HashSet<string>? targets = dirty is null ? null : new HashSet<string>(dirty, StringComparer.OrdinalIgnoreCase);
            if (startup && targets is null && haveJournal)
            {
                if (root.LastWalkTicks == 0) stats.Journal = "no-complete-walk";
                else if (root.JournalId != journalId || root.VolumeSerial != volume.Serial) stats.Journal = "journal-id-changed";
                else
                {
                    targets = JournalDirty(rootPath, root, journalId, journalNext, stats);
                    stats.Mode = targets is null ? "startup-walk" : "journal";
                }
            }
            stats.JournalMs = journalClock.Elapsed.TotalMilliseconds;
            if (targets is not null)
            {
                // A folder whose last listing failed a read is retried whatever the journal says.
                foreach (LocalFolder f in _index.FoldersOf(root.RootId)) if (!f.Complete) targets.Add(f.RelativePath);
                stats.DirtyFolders = targets.Count;
            }

            var walk = new Walk(this, root, rootPath, rootHandle, rootId, volume, stats, targets);
            try { walk.Run(); }
            finally { _index.ReleaseScanMaps(); }   // the move-pairing maps live only while a scan pairs
            stats.Cancelled = Cancelled(rootPath);
            stats.Complete = !stats.Cancelled && stats.FoldersFailed == 0 && targets is null;
            if (!stats.Cancelled)
            {
                if (stats.FoldersFailed == 0 && haveJournal)
                    _index.SetRoot(root with { VolumeSerial = volume.Serial, JournalId = journalId, NextUsn = journalNext, LastWalkTicks = DateTime.UtcNow.Ticks });
                else if (stats.FoldersFailed == 0)
                    _index.SetRoot(root with { VolumeSerial = volume.Serial, JournalId = 0, NextUsn = 0, LastWalkTicks = DateTime.UtcNow.Ticks });
                if (stats.Complete) stats.Purged = _index.PurgeMissing(root.RootId, DateTime.UtcNow.Ticks - MissingGrace().Ticks);
            }
            RootState?.Invoke(rootPath, reach == LocalRootState.Online ? LocalRootState.Online : reach);
        }
        finally
        {
            NtDirectory.CloseLater(rootHandle);
        }
        stats.BytesRead = Math.Max(0, ProcessReadBytes() - ioStart);
        stats.TotalMs = total.Elapsed.TotalMilliseconds;
        return stats;
    }

    /// <summary>The root record for a path: the stored one, or a new root with the next id.</summary>
    LocalRoot RootFor(string rootPath)
    {
        int max = 0;
        foreach (LocalRoot r in _index.Roots())
        {
            if (string.Equals(r.Path, rootPath, StringComparison.OrdinalIgnoreCase)) return r;
            max = Math.Max(max, r.RootId);
        }
        var root = new LocalRoot(max + 1, rootPath, 0, 0, 0, 0);
        _index.SetRoot(root);
        return root;
    }

    void NoteCommitted(int files)
    {
        Interlocked.Increment(ref _foldersDone);
        if (files > 0) Interlocked.Add(ref _filesRead, files);
        Progressed?.Invoke();
    }

    static bool IsImage(ReadOnlySpan<char> name)
    {
        ReadOnlySpan<char> ext = Path.GetExtension(name);
        foreach (string e in s_imageExtensions) if (ext.Equals(e, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static bool IsVideoName(ReadOnlySpan<char> name)
    {
        ReadOnlySpan<char> ext = Path.GetExtension(name);
        return ext.Equals(".mkv", StringComparison.OrdinalIgnoreCase) || ext.Equals(".webm", StringComparison.OrdinalIgnoreCase) || ext.Equals(".m4v", StringComparison.OrdinalIgnoreCase);
    }

    static LocalTagSet EmptyTags(ReadOnlySpan<char> name)
        => new(IsVideoName(name) ? LocalKind.Video : LocalKind.Audio, "", [], "", "", 0, 0, 0, 0, false, 0, LocalCover.None, 0, 0, true, 0, 0, 0);

    static string Rel(string folder, string name) => folder.Length == 0 ? name : folder + "\\" + name;

    // ── the walk of one root ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A directory handle shared by the jobs that open children relative to it and the reads of its files: closed
    /// (on the close pool) when the last holder lets go. The root's handle is never closed here.</summary>
    sealed class DirHandle(SafeFileHandle handle, bool owns)
    {
        int _refs = 1;
        public readonly SafeFileHandle Handle = handle;
        public DirHandle AddRef() { Interlocked.Increment(ref _refs); return this; }
        public void Release() { if (Interlocked.Decrement(ref _refs) == 0 && owns) NtDirectory.CloseLater(Handle); }
    }

    /// <summary>A folder still to list: opened relative to <see cref="Parent"/> (a held reference) or, with
    /// <see cref="Handle"/> set, already open.</summary>
    sealed record DirJob(DirHandle? Parent, string OpenName, string RelPath, UInt128 FileId, bool Recurse, DirHandle? Handle = null);

    /// <summary>A listed folder waiting for its reads; it commits when the last one is done.</summary>
    sealed class FolderWork(LocalScanBatch batch, DirHandle handle, int reads)
    {
        public readonly LocalScanBatch Batch = batch;
        public readonly DirHandle Handle = handle;
        public int Pending = reads;
        public int ReadFailures;
        public readonly List<LocalEntry> Read = new();
    }

    sealed record ReadJob(FolderWork Folder, NtEntry File, string RelPath, long LocalId, LocalEntry? Previous);

    sealed class Walk(LocalScan scan, LocalRoot root, string rootPath, SafeFileHandle rootHandle, UInt128 rootFileId, NtVolume volume,
                      LocalScanStats stats, HashSet<string>? targets)
    {
        readonly LocalIndex _index = scan._index;
        readonly ConcurrentQueue<DirJob> _dirs = new();
        readonly ConcurrentQueue<ReadJob> _reads = new();
        readonly SemaphoreSlim _dirSignal = new(0), _readSignal = new(0);
        readonly ConcurrentDictionary<long, LocalEntry> _vanished = new();
        readonly ConcurrentDictionary<long, byte> _claimed = new();
        readonly ConcurrentDictionary<string, byte> _failed = new(StringComparer.OrdinalIgnoreCase);
        readonly ConcurrentBag<string> _goneRoots = new();
        readonly ConcurrentBag<LocalEntry> _fresh = new();   // new files read under a fresh id (step 6 may re-key them)
        readonly Dictionary<string, List<string>> _children = new(StringComparer.OrdinalIgnoreCase);
        readonly Stopwatch _clock = Stopwatch.StartNew();
        readonly long _now = DateTime.UtcNow.Ticks;

        /// <summary>A re-read or re-pointed file keeps the time its LocalId was first indexed ("Recently added" orders by it); a new
        /// file is first seen now.</summary>
        long FirstSeen(LocalEntry? previous) => previous is { FirstSeenTicks: > 0 } p ? p.FirstSeenTicks : _now;
        readonly DirHandle _root = new(rootHandle, owns: false);
        int _outstanding;
        volatile bool _done;
        int _listWorkers, _readWorkers;

        public void Run()
        {
            // The index's known folders, by parent: a listing compares its subfolders with them (a folder gone from disk).
            foreach (LocalFolder f in _index.FoldersOf(root.RootId))
            {
                if (f.RelativePath.Length == 0) continue;
                string parent = LocalIndex.FolderOf(f.RelativePath);
                if (!_children.TryGetValue(parent, out List<string>? list)) _children[parent] = list = new List<string>();
                list.Add(f.RelativePath);
            }

            _listWorkers = targets is { Count: < 4 } ? 1 : volume.ListWorkers;
            _readWorkers = volume.ReadWorkers;
            if (targets is null) Push(new DirJob(null, "", "", rootFileId, Recurse: true, Handle: _root.AddRef()));
            else
                foreach (string folder in targets)
                {
                    // Only the dirty folder itself: its known subfolders are not dirty; an unknown one is walked whole.
                    if (folder.Length == 0) Push(new DirJob(null, "", "", rootFileId, Recurse: false, Handle: _root.AddRef()));
                    else Push(new DirJob(_root.AddRef(), folder, folder, _index.TryGetFolder(root.RootId, folder, out LocalFolder? f) ? f.FileId : 0, Recurse: false));
                }
            var threads = new List<Thread>();
            if (Volatile.Read(ref _outstanding) == 0)
            {
                // Nothing dirty (a journal catch-up that found no change): no worker starts, nothing to wait for.
                _done = true;
                _listWorkers = _readWorkers = 0;
            }
            stats.ListWorkers = _listWorkers;
            stats.ReadWorkers = _readWorkers;
            for (int i = 0; i < _listWorkers; i++) threads.Add(Start(ListLoop, "Wavee.LocalScan.List"));
            for (int i = 0; i < _readWorkers; i++) threads.Add(Start(ReadLoop, "Wavee.LocalScan.Read"));
            foreach (Thread t in threads) t.Join();
            stats.WalkMs = _clock.Elapsed.TotalMilliseconds;

            var finish = Stopwatch.StartNew();
            if (!scan.Cancelled(rootPath)) Finish();
            stats.FinishMs = finish.Elapsed.TotalMilliseconds;
        }

        static Thread Start(ThreadStart body, string name)
        {
            var t = new Thread(body) { IsBackground = true, Name = name, Priority = ThreadPriority.BelowNormal };
            t.Start();
            return t;
        }

        void Push(DirJob job)
        {
            Interlocked.Increment(ref _outstanding);
            _dirs.Enqueue(job);
            _dirSignal.Release();
        }

        void Done()
        {
            if (Interlocked.Decrement(ref _outstanding) != 0) return;
            _done = true;
            _dirSignal.Release(_listWorkers);
            _readSignal.Release(_readWorkers);
        }

        void ListLoop()
        {
            var listing = new List<NtEntry>(256);
            while (true)
            {
                _dirSignal.Wait();
                if (_dirs.TryDequeue(out DirJob? job))
                {
                    try { List(job, listing); }
                    catch (Exception ex)
                    {
                        // A folder that threw is a failed listing: nothing under it is marked missing, the walk is not complete.
                        _failed.TryAdd(job.RelPath, 0);
                        Interlocked.Increment(ref stats.FoldersFailed);
                        Log.Warn("local", "local.scan list failed", ex);
                    }
                    finally { Done(); }
                    continue;
                }
                if (_done) return;
            }
        }

        void ReadLoop()
        {
            while (true)
            {
                _readSignal.Wait();
                if (_reads.TryDequeue(out ReadJob? job))
                {
                    try { Read(job); }
                    catch (Exception ex)
                    {
                        // A reader that threw: the file keeps its old entry and its folder is retried next scan.
                        Interlocked.Increment(ref stats.Errors);
                        Interlocked.Increment(ref job.Folder.ReadFailures);
                        Log.Warn("local", "local.scan read failed", ex);
                    }
                    finally { ReadDone(job.Folder); }
                    continue;
                }
                if (_done) return;
            }
        }

        // ── step 3: list one folder, fingerprint, diff; step 4: pair before reading ─────────────────────────────────

        void List(DirJob job, List<NtEntry> listing)
        {
            DirHandle? dir = job.Handle;
            int status = 0;
            if (dir is null)
            {
                SafeFileHandle? opened = NtDirectory.OpenDirectory(job.Parent!.Handle, job.OpenName, out status);
                job.Parent.Release();
                if (opened is not null) dir = new DirHandle(opened, owns: true);
            }
            if (dir is null)
            {
                // A dirty folder that no longer exists is not a failure: its parent's listing (also dirty) finds it gone.
                if (targets is not null && status is StatusObjectNameNotFound or StatusObjectPathNotFound) return;
                _failed.TryAdd(job.RelPath, 0);
                Interlocked.Increment(ref stats.FoldersFailed);
                return;
            }
            SafeFileHandle handle = dir.Handle;
            try
            {
                if (scan.Cancelled(rootPath)) return;
                if (!NtDirectory.List(handle, volume.Serial, listing, out status))
                {
                    _failed.TryAdd(job.RelPath, 0);
                    Interlocked.Increment(ref stats.FoldersFailed);
                    return;
                }
                Interlocked.Increment(ref stats.FoldersListed);

                // Subfolders: walk into them (all of them, or only the ones the index does not know in a targeted pass), and
                // notice the known ones that are gone.
                HashSet<string>? present = _children.ContainsKey(job.RelPath) ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : null;
                var files = new List<NtEntry>(listing.Count);
                foreach (NtEntry e in listing)
                {
                    if (e.IsDirectory)
                    {
                        if (!NtDirectory.WalksInto(e)) continue;
                        string childRel = Rel(job.RelPath, e.Name);
                        present?.Add(childRel);
                        if (job.Recurse || !_index.TryGetFolder(root.RootId, childRel, out _))
                            Push(new DirJob(dir.AddRef(), e.Name, childRel, e.FileId, Recurse: true));
                        continue;
                    }
                    if (LocalTags.IsScanExtension(e.Name) || IsImage(e.Name)) files.Add(e);
                }
                if (present is not null)
                    foreach (string child in _children[job.RelPath])
                        if (!present.Contains(child)) _goneRoots.Add(child);

                files.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                ulong fingerprint = Fingerprint(files);
                var images = new List<string>();
                bool anyCloud = false;
                foreach (NtEntry f in files)
                {
                    if (IsImage(f.Name)) images.Add(f.Name);
                    else if (f.IsCloudOnly) anyCloud = true;
                }

                bool known = _index.TryGetFolder(root.RootId, job.RelPath, out LocalFolder? stored);
                List<LocalEntry>? existing = null;
                if (known && stored!.Fingerprint == fingerprint && stored.Complete && !anyCloud)
                {
                    // Equal fingerprint: nothing read, nothing compared. Unless a cloud entry was hydrated without its times
                    // moving (the fingerprint holds names, sizes and times, not the cloud bits): those folders are diffed.
                    if (!_index.FolderHasCloud(root.RootId, job.RelPath))
                    {
                        Interlocked.Increment(ref stats.FoldersSkipped);
                        Interlocked.Add(ref stats.FilesSeen, files.Count - images.Count);
                        Interlocked.Add(ref stats.FilesUnchanged, files.Count - images.Count);
                        if (stored.FileId != job.FileId && job.FileId != 0)
                            Commit(new LocalScanBatch(root.RootId, job.RelPath) { Folder = stored with { FileId = job.FileId } }, 0);
                        return;
                    }
                }
                existing ??= _index.EntriesIn(root.RootId, job.RelPath);

                var batch = new LocalScanBatch(root.RootId, job.RelPath)
                {
                    Folder = new LocalFolder(root.RootId, job.RelPath, job.FileId, fingerprint, images.ToArray(), Complete: true),
                };
                var byName = new Dictionary<string, LocalEntry>(existing.Count, StringComparer.OrdinalIgnoreCase);
                foreach (LocalEntry e in existing) byName[Path.GetFileName(e.RelativePath)] = e;
                var toRead = new List<(NtEntry File, string Rel, long Id, LocalEntry? Previous)>();
                foreach (NtEntry f in files)
                {
                    if (IsImage(f.Name)) continue;
                    Interlocked.Increment(ref stats.FilesSeen);
                    string rel = Rel(job.RelPath, f.Name);
                    bool cloud = f.IsCloudOnly;
                    if (cloud) Interlocked.Increment(ref stats.CloudOnly);
                    if (byName.Remove(f.Name, out LocalEntry? e))
                    {
                        bool sameKey = e.Size == f.Size && e.WriteTicks == f.WriteTicks && e.ChangeTicks == f.ChangeTicks
                                       && (!volume.StableIds || e.FileId == 0 || e.FileId == f.FileId);
                        bool sameCloud = (e.State == LocalState.CloudOnly) == cloud;
                        if (sameKey && sameCloud)
                        {
                            Interlocked.Increment(ref stats.FilesUnchanged);
                            if (e.State == LocalState.Missing)
                            {
                                // Back where it was, unchanged: no tag read.
                                Interlocked.Increment(ref stats.Reappeared);
                                batch.Upserts.Add(e with { State = cloud ? LocalState.CloudOnly : LocalState.Present, LastSeenTicks = _now, FileId = f.FileId, Attributes = f.Attributes, VolumeSerial = volume.Serial });
                            }
                            else if (e.Attributes != f.Attributes || e.RelativePath != rel) batch.Upserts.Add(e with { Attributes = f.Attributes, RelativePath = rel });
                            continue;
                        }
                        // Changed (a new key, or a new file id at the same path: a safe-save): same LocalId, read again.
                        if (cloud) batch.Upserts.Add(CloudEntry(e.LocalId, rel, f, e));
                        else toRead.Add((f, rel, e.LocalId, e));
                        continue;
                    }

                    // A path the index does not know: a move first (step 4), else a new file.
                    if (PairBefore(f, rel, out LocalEntry? moved))
                    {
                        Interlocked.Increment(ref stats.MovesPaired);
                        // Unchanged bytes whose tags were read before (not a placeholder's empty tags): re-pointed, no read.
                        bool sameKey = moved.Size == f.Size && moved.WriteTicks == f.WriteTicks && moved.ChangeTicks == f.ChangeTicks
                                       && moved.State != LocalState.CloudOnly;
                        if (cloud) batch.Upserts.Add(CloudEntry(moved.LocalId, rel, f, moved));
                        else if (sameKey) batch.Upserts.Add(moved with { RootId = root.RootId, RelativePath = rel, VolumeSerial = volume.Serial, FileId = f.FileId, Attributes = f.Attributes, State = LocalState.Present, LastSeenTicks = _now });
                        else toRead.Add((f, rel, moved.LocalId, moved));
                        continue;
                    }
                    if (cloud) batch.Upserts.Add(CloudEntry(_index.NewId(), rel, f, null));
                    else toRead.Add((f, rel, 0, null));
                }
                // What the listing no longer holds: vanished, decided at the end of the walk (a move into a folder listed later).
                foreach (LocalEntry gone in byName.Values)
                    if (gone.State != LocalState.Missing) _vanished.TryAdd(gone.LocalId, gone);

                if (toRead.Count == 0)
                {
                    Commit(batch, 0);
                    return;
                }
                var work = new FolderWork(batch, dir.AddRef(), toRead.Count);
                Interlocked.Increment(ref _outstanding);   // the folder stays open until its last read commits it
                Interlocked.Add(ref stats.ToRead, toRead.Count);
                foreach (var r in toRead) _reads.Enqueue(new ReadJob(work, r.File, r.Rel, r.Id, r.Previous));
                _readSignal.Release(Math.Min(toRead.Count, _readWorkers));
            }
            finally
            {
                dir.Release();
            }
        }

        LocalEntry CloudEntry(long id, string rel, NtEntry f, LocalEntry? previous)
        {
            // Indexed from the listing alone (§3.6 "Cloud files"): never opened, title from the file name, length unknown.
            LocalTagSet tags = previous?.Tags ?? EmptyTags(f.Name);
            return new LocalEntry(id, root.RootId, rel, volume.Serial, f.FileId, f.Size, f.WriteTicks, f.ChangeTicks, f.Attributes,
                previous?.Format ?? Spotify.Audio.Format.Unknown, tags, previous?.MatchTitle ?? LocalTags.TitleOrFileName(tags, f.Name),
                LocalState.CloudOnly, _now, previous?.Playable ?? true, previous?.Why ?? "", FirstSeen(previous));
        }

        /// <summary>Step 4: is the new path <paramref name="rel"/> an indexed file that moved? The same file id on the volume
        /// (NTFS/ReFS), else the same (size, write time, extension) (FAT/exFAT/SMB). The old entry must be gone from its place:
        /// Missing, vanished in this walk, or no longer on disk (a folder not listed yet). Claimed once.</summary>
        bool PairBefore(NtEntry f, string rel, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out LocalEntry? moved)
        {
            moved = null;
            if (volume.StableIds && f.FileId != 0)
            {
                if (_index.TryGetByFileId(volume.Serial, f.FileId, out LocalEntry? c) && Gone(c, rel) && _claimed.TryAdd(c.LocalId, 0))
                {
                    _vanished.TryRemove(c.LocalId, out _);
                    moved = c;
                    Interlocked.Increment(ref stats.MovesById);
                    return true;
                }
                return false;
            }
            LocalEntry? best = null;
            int bestTail = -1;
            ReadOnlySpan<char> ext = Path.GetExtension(f.Name.AsSpan());
            foreach (LocalEntry c in _index.BySizeAndWrite(f.Size, f.WriteTicks))
            {
                if (!Path.GetExtension(c.RelativePath.AsSpan()).Equals(ext, StringComparison.OrdinalIgnoreCase) || !Gone(c, rel)) continue;
                int tail = SharedTail(c.RelativePath, rel);
                if (tail > bestTail) { best = c; bestTail = tail; }
            }
            if (best is null || !_claimed.TryAdd(best.LocalId, 0)) return false;
            _vanished.TryRemove(best.LocalId, out _);
            moved = best;
            return true;
        }

        bool Gone(LocalEntry c, string rel)
        {
            if (c.RootId == root.RootId && string.Equals(c.RelativePath, rel, StringComparison.OrdinalIgnoreCase)) return false;
            if (_claimed.ContainsKey(c.LocalId)) return false;
            if (c.State == LocalState.Missing || _vanished.ContainsKey(c.LocalId)) return true;
            // Present at a place not listed yet: a hardlink or a copy keeps the old file; only a file that is gone moved.
            return _index.FullPathOf(c) is { } old && !File.Exists(old);
        }

        static int SharedTail(string a, string b)
        {
            int n = 0;
            for (int i = a.Length - 1, j = b.Length - 1; i >= 0 && j >= 0 && char.ToUpperInvariant(a[i]) == char.ToUpperInvariant(b[j]); i--, j--) n++;
            return n;
        }

        /// <summary>xxHash3-64 over the sorted (name, size, write, change) of a folder's audio and image entries.</summary>
        static ulong Fingerprint(List<NtEntry> files)
        {
            var hash = new XxHash3();
            Span<long> numbers = stackalloc long[3];
            foreach (NtEntry f in files)
            {
                hash.Append(MemoryMarshal.AsBytes(f.Name.AsSpan()));
                numbers[0] = f.Size; numbers[1] = f.WriteTicks; numbers[2] = f.ChangeTicks;
                hash.Append(MemoryMarshal.AsBytes(numbers));
            }
            return hash.GetCurrentHashAsUInt64();
        }

        // ── step 5: read ─────────────────────────────────────────────────────────────────────────────────────────────

        void Read(ReadJob job)
        {
            if (scan.Cancelled(rootPath)) return;
            NtEntry f = job.File;
            SafeFileHandle? file = NtDirectory.OpenFileForRead(job.Folder.Handle.Handle, f.Name, out int status);
            bool ok;
            Spotify.Audio.Format format;
            LocalTagSet tags;
            string why;
            if (file is null)
            {
                ok = false;
                format = Spotify.Audio.Format.Unknown;
                tags = EmptyTags(f.Name);
                why = "Could not open the file (error " + NtDirectory.RtlNtStatusToDosError(status).ToString(CultureInfo.InvariantCulture) + ")";
            }
            else
            {
                ok = LocalTags.TryRead(file, f.Size, f.Name, out format, out tags, out why);
                NtDirectory.CloseLater(file);
            }
            Interlocked.Increment(ref stats.FilesRead);
            if (!ok)
            {
                Interlocked.Increment(ref stats.Errors);
                Interlocked.Increment(ref job.Folder.ReadFailures);
                // A file that was readable before keeps its entry until a read succeeds (its folder is retried next scan).
                if (job.Previous is not null && job.Previous.State != LocalState.Missing) return;
            }
            bool fresh = job.LocalId == 0;
            long id = fresh ? _index.NewId() : job.LocalId;
            bool playable = ok && format != Spotify.Audio.Format.Unknown;
            var entry = new LocalEntry(id, root.RootId, job.RelPath, volume.Serial, f.FileId, f.Size, f.WriteTicks, f.ChangeTicks, f.Attributes,
                format, tags, LocalTags.TitleOrFileName(tags, f.Name), LocalState.Present, _now, playable, playable ? "" : why, FirstSeen(job.Previous));
            lock (job.Folder.Read) job.Folder.Read.Add(entry);
            if (fresh && ok) _fresh.Add(entry);
        }

        void ReadDone(FolderWork work)
        {
            if (Interlocked.Decrement(ref work.Pending) != 0) return;
            try
            {
                LocalScanBatch batch = work.Batch;
                batch.Upserts.AddRange(work.Read);
                if (Volatile.Read(ref work.ReadFailures) > 0 && batch.Folder is { } folder) batch.Folder = folder with { Complete = false };
                if (!scan.Cancelled(rootPath)) Commit(batch, work.Read.Count);
            }
            finally
            {
                work.Handle.Release();
                Done();
            }
        }

        void Commit(LocalScanBatch batch, int read)
        {
            if (batch.IsEmpty || scan.Cancelled(rootPath)) return;   // a removed folder commits nothing more
            _index.Apply(batch);
            if (stats.FirstCommitMs < 0 && batch.Upserts.Count > 0) stats.FirstCommitMs = _clock.Elapsed.TotalMilliseconds;
            scan.NoteCommitted(read);
        }

        // ── steps 6 and 7: what vanished ─────────────────────────────────────────────────────────────────────────────

        void Finish()
        {
            // Folders gone from disk (a known subfolder missing from its listed parent): their files vanished, their records go.
            var goneFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!_goneRoots.IsEmpty)
                foreach (LocalFolder f in _index.FoldersOf(root.RootId))
                    foreach (string gone in _goneRoots)
                        if (f.RelativePath.Equals(gone, StringComparison.OrdinalIgnoreCase) || f.RelativePath.StartsWith(gone + "\\", StringComparison.OrdinalIgnoreCase))
                        {
                            goneFolders.Add(f.RelativePath);
                            break;
                        }
            foreach (string folder in goneFolders)
                foreach (LocalEntry e in _index.EntriesIn(root.RootId, folder))
                    if (e.State != LocalState.Missing && !_claimed.ContainsKey(e.LocalId)) _vanished.TryAdd(e.LocalId, e);
            stats.FoldersGone = goneFolders.Count;

            var batches = new Dictionary<string, LocalScanBatch>(StringComparer.OrdinalIgnoreCase);
            LocalScanBatch BatchFor(int rootId, string folder)
            {
                string key = rootId.ToString(CultureInfo.InvariantCulture) + "|" + folder;
                if (!batches.TryGetValue(key, out LocalScanBatch? b)) batches[key] = b = new LocalScanBatch(rootId, folder);
                return b;
            }

            var left = new List<LocalEntry>();
            foreach (LocalEntry v in _vanished.Values)
                if (!_claimed.ContainsKey(v.LocalId)) left.Add(v);

            // A file id still on the volume: where is it now? (moved elsewhere on the drive; one call, no scan, no admin)
            if (volume.StableIds && left.Count > 0)
            {
                for (int i = left.Count - 1; i >= 0; i--)
                {
                    LocalEntry v = left[i];
                    if (v.FileId == 0 || NtDirectory.PathOfFileId(rootHandle, v.FileId) is not { } now) continue;
                    if (now.Contains("\\$Recycle.Bin\\", StringComparison.OrdinalIgnoreCase)) continue;   // deleted to the bin: missing
                    if (_index.TryRelative(now, out int rootId, out string rel))
                    {
                        // Re-pointed inside a music folder (one this walk did not list); its folder's next listing reads it if it changed.
                        BatchFor(rootId, LocalIndex.FolderOf(rel)).Upserts.Add(v with { RootId = rootId, RelativePath = rel, State = LocalState.Present, LastSeenTicks = _now });
                        stats.MovesPaired++;
                        stats.MovesById++;
                    }
                    else
                    {
                        // Outside every music folder: kept as missing with where it went, so the row can offer "Moved to ..." + Add that folder.
                        BatchFor(v.RootId, LocalIndex.FolderOf(v.RelativePath)).Upserts.Add(v with { State = LocalState.Missing, LastSeenTicks = _now, Why = "Moved to " + now });
                        stats.MovedOutside++;
                    }
                    left.RemoveAt(i);
                }
            }

            // Step 6: pair after reading (a file that changed AND moved): the same tags key, else the same file name with a length
            // within 1 s; the longest shared path tail wins.
            if (left.Count > 0 && !_fresh.IsEmpty)
            {
                var fresh = new List<LocalEntry>(_fresh);
                var used = new HashSet<long>();
                for (int i = left.Count - 1; i >= 0; i--)
                {
                    LocalEntry v = left[i];
                    LocalEntry? best = null;
                    int bestScore = -1;
                    foreach (LocalEntry n in fresh)
                    {
                        if (used.Contains(n.LocalId) || Math.Abs(n.Tags.DurationMs - v.Tags.DurationMs) > 1000) continue;
                        bool byTags = v.Tags.Title.Length > 0 && LocalMatch.Exact(n.Tags.Title) == LocalMatch.Exact(v.Tags.Title)
                                      && LocalMatch.Exact(n.Tags.FirstArtist) == LocalMatch.Exact(v.Tags.FirstArtist)
                                      && LocalMatch.Exact(n.Tags.Album) == LocalMatch.Exact(v.Tags.Album);
                        bool byName = string.Equals(Path.GetFileName(n.RelativePath), Path.GetFileName(v.RelativePath), StringComparison.OrdinalIgnoreCase);
                        if (!byTags && !byName) continue;
                        int score = (byTags ? 1 << 20 : 0) + SharedTail(n.RelativePath, v.RelativePath);
                        if (score > bestScore) { best = n; bestScore = score; }
                    }
                    if (best is null) continue;
                    used.Add(best.LocalId);
                    LocalScanBatch b = BatchFor(best.RootId, LocalIndex.FolderOf(best.RelativePath));
                    b.Removes.Add(best.LocalId);
                    b.Upserts.Add(best with { LocalId = v.LocalId });   // the old identity survives (D13)
                    stats.PairedAfterRead++;
                    stats.MovesPaired++;
                    left.RemoveAt(i);
                }
            }

            // Step 7: the rest is missing (its folder was listed successfully, or the folder itself is gone under a listed parent).
            foreach (LocalEntry v in left)
            {
                BatchFor(v.RootId, LocalIndex.FolderOf(v.RelativePath)).Upserts.Add(v with { State = LocalState.Missing, LastSeenTicks = _now });
                stats.MissingMarked++;
            }
            foreach (string folder in goneFolders) BatchFor(root.RootId, folder).RemoveFolder = true;
            foreach (LocalScanBatch b in batches.Values) Commit(b, 0);
        }
    }

    // ── a little shared native help ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Bytes this process has read from files and devices so far (the summary's "bytes"; process-wide).</summary>
    static unsafe long ProcessReadBytes()
    {
        ulong* counters = stackalloc ulong[6];
        return GetProcessIoCounters(Process.GetCurrentProcess().SafeHandle, counters) != 0 ? (long)counters[3] : 0;
    }

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int GetProcessIoCounters(SafeProcessHandle process, ulong* counters);
}
