// ── Playback/LocalScan.Watch.cs ─────────────────────────────────────────────────────────────────────────────────────
// live updates: one recursive directory watch per music folder; an event only marks the nearest known folder dirty, and the
// dirty folders are rescanned after a debounce
//
// Role: CORE (one watch thread per root, below normal; the debounce timer posts requests to the scan thread)
// Spec: C:\WAVEE\fgpu\handoff\request-plans\240-local-playlist-files-implementation.md §3.6 "Live updates", §3.16 "Scanning";
//       C:\WAVEE\fgpu\handoff\localindex-research\windows-filesystem.md §3
//
// A WATCHER IS A HINT, THE SCAN IS THE TRUTH (rule 6). `ReadDirectoryChangesExW` with extended information (NTFS: each event
// carries the file's attributes and its parent's id) and plain `ReadDirectoryChangesW` elsewhere (FAT, exFAT, SMB), one
// overlapped 64 KB buffer per root (the SMB maximum). Debounce: 400 ms local, 10 s on a network root (the SMB redirector
// caches listings for 10 s). Overflow (a zero-byte completion or ERROR_NOTIFY_ENUM_DIR) or a failed watch: a full walk of
// the root, cheap with fingerprints; a failed watch reopens with backoff (1 s doubling to 60 s).
//
// USB. An open watch handle blocks "safely remove": on a USB root the handle is registered with the PnP manager
// (`CM_Register_Notification`, DEVICEHANDLE) and closed on query-remove; the root is then offline. A volume arriving (a USB
// drive plugged back in, a new volume mounted) is reported by `WatchVolumeArrivals` (the volume device interface): the
// façade then walks and watches every folder that is offline or unwatched.

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Wavee;

public sealed partial class LocalScan
{
    readonly ConcurrentDictionary<string, Watcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, Debounce> _debounce = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Start watching a root (idempotent).</summary>
    public void Watch(string rootPath)
    {
        if (_watchers.ContainsKey(rootPath)) return;
        var w = new Watcher(this, rootPath);
        if (_watchers.TryAdd(rootPath, w)) w.Start();
    }

    /// <summary>Stop watching a root (a removed folder, a device going away).</summary>
    public void Unwatch(string rootPath)
    {
        if (_watchers.TryRemove(rootPath, out Watcher? w)) w.Stop();
        if (_debounce.TryRemove(rootPath, out Debounce? d)) d.Dispose();
    }

    /// <summary>Roots with a live watch handle.</summary>
    public int WatcherCount => _watchers.Count;

    /// <summary>Is this root watched now (false after a USB drive was removed)?</summary>
    public bool IsWatched(string rootPath) => _watchers.ContainsKey(rootPath);

    /// <summary>Call <paramref name="onArrival"/> (a PnP thread; do not block) whenever a volume arrives: a USB drive plugged
    /// in, a volume mounted. Null when the registration is refused. Dispose to stop.</summary>
    public static IDisposable? WatchVolumeArrivals(Action onArrival) => VolumeArrivals.Register(onArrival);

    sealed unsafe partial class VolumeArrivals : IDisposable
    {
        readonly Action _onArrival;
        GCHandle _self;
        nint _notify;

        VolumeArrivals(Action onArrival) => _onArrival = onArrival;

        public static VolumeArrivals? Register(Action onArrival)
        {
            var v = new VolumeArrivals(onArrival);
            v._self = GCHandle.Alloc(v);
            byte* filter = stackalloc byte[416];   // CM_NOTIFY_FILTER
            new Span<byte>(filter, 416).Clear();
            *(uint*)filter = 416;                  // cbSize
            *(uint*)(filter + 8) = 0;              // CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE
            *(Guid*)(filter + 16) = new Guid(0x53f5630d, 0xb6bf, 0x11d0, 0x94, 0xf2, 0x00, 0xa0, 0xc9, 0x1e, 0xfb, 0x8b);   // GUID_DEVINTERFACE_VOLUME
            nint notify;
            if (CM_Register_Notification(filter, GCHandle.ToIntPtr(v._self), &OnInterface, &notify) != 0)
            {
                v._self.Free();
                return null;
            }
            v._notify = notify;
            return v;
        }

        [UnmanagedCallersOnly]
        static uint OnInterface(nint notify, nint context, uint action, nint data, uint size)
        {
            if (action == 0 /*DEVICEINTERFACEARRIVAL*/ && GCHandle.FromIntPtr(context).Target is VolumeArrivals v)
            {
                try { v._onArrival(); }
                catch (Exception ex) { Log.Warn("local", "local.volume arrival handler failed", ex); }
            }
            return 0;
        }

        public void Dispose()
        {
            nint notify = Interlocked.Exchange(ref _notify, 0);
            if (notify != 0) CM_Unregister_Notification(notify);
            if (_self.IsAllocated) _self.Free();
        }

        [LibraryImport("cfgmgr32.dll")]
        private static partial uint CM_Register_Notification(void* filter, nint context, delegate* unmanaged<nint, nint, uint, nint, uint, uint> callback, nint* notify);
        [LibraryImport("cfgmgr32.dll")] private static partial uint CM_Unregister_Notification(nint notify);
    }

    sealed class Debounce(LocalScan scan, string rootPath, int delayMs) : IDisposable
    {
        readonly Lock _gate = new();
        readonly List<(string Rel, UInt128 Parent)> _changes = new();
        Timer? _timer;
        bool _full;

        public void Note(string rel, UInt128 parentId)
        {
            lock (_gate) { if (_changes.Count < 10_000) _changes.Add((rel, parentId)); else _full = true; }
            Arm();   // quiet for the whole delay before anything is listed
        }

        public void Full()
        {
            lock (_gate) _full = true;
            Arm();
        }

        void Arm()
        {
            lock (_gate) _timer ??= new Timer(static s => ((Debounce)s!).Fire(), this, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(delayMs, Timeout.Infinite);
        }

        void Fire()
        {
            List<(string Rel, UInt128 Parent)> changes;
            bool full;
            lock (_gate) { changes = new(_changes); _changes.Clear(); full = _full; _full = false; }
            if (full) { scan.RequestFull(rootPath); return; }
            if (changes.Count == 0) return;
            LocalIndex index = scan._index;
            LocalRoot? root = null;
            foreach (LocalRoot r in index.Roots()) if (string.Equals(r.Path, rootPath, StringComparison.OrdinalIgnoreCase)) root = r;
            if (root is null) { scan.RequestFull(rootPath); return; }
            var dirty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<UInt128, string>? byId = null;
            foreach ((string rel, UInt128 parent) in changes)
            {
                // The parent's id when the event carried it (NTFS): exact even when the name came as an 8.3 short name.
                if (parent != 0)
                {
                    if (byId is null)
                    {
                        byId = new Dictionary<UInt128, string>();
                        foreach (LocalFolder f in index.FoldersOf(root.RootId)) byId[f.FileId] = f.RelativePath;
                    }
                    if (byId.TryGetValue(parent, out string? known)) { dirty.Add(known); continue; }
                }
                // Else the nearest folder the index knows. A changed folder (a rename, a delete, a new one) dirties its parent,
                // whose listing finds it gone or walks it as new.
                string folder = LocalIndex.FolderOf(rel);
                while (folder.Length > 0 && !index.TryGetFolder(root.RootId, folder, out _)) folder = LocalIndex.FolderOf(folder);
                dirty.Add(folder);
            }
            scan.RequestDirty(rootPath, dirty);
        }

        public void Dispose() { lock (_gate) _timer?.Dispose(); }
    }

    Debounce DebounceFor(string rootPath)
        => _debounce.GetOrAdd(rootPath, p => new Debounce(this, p, NtDirectory.IsNetworkPath(p) ? 10_000 : 400));

    sealed unsafe partial class Watcher(LocalScan owner, string root)
    {
        readonly LocalScan scan = owner;
        readonly string rootPath = root;
        const int BufferBytes = 64 * 1024;
        const uint Filter = 0x1 /*FILE_NAME*/ | 0x2 /*DIR_NAME*/ | 0x4 /*ATTRIBUTES*/ | 0x8 /*SIZE*/ | 0x10 /*LAST_WRITE*/ | 0x40 /*CREATION*/;
        readonly nint _stop = CreateEventW(0, 1, 0, 0);
        Thread? _thread;
        volatile bool _stopping;
        nint _deviceNotify;
        GCHandle _self;

        public void Start()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "Wavee.LocalScan.Watch", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        public void Stop()
        {
            _stopping = true;
            SetEvent(_stop);
            if (_thread is { } t && t != Thread.CurrentThread && !t.Join(3000)) return;   // a stuck watch keeps its event
            CloseHandle(_stop);
        }

        void Run()
        {
            int backoffMs = 1000;
            bool firstOpen = true;
            while (!_stopping)
            {
                // A network root is probed first: an open on a dead share blocks for a minute or more.
                if (NtDirectory.IsNetworkPath(rootPath) && ProbeReach(rootPath) is not (LocalRootState.Online or LocalRootState.Waking or LocalRootState.Slow))
                {
                    if (Wait(backoffMs)) break;
                    backoffMs = Math.Min(backoffMs * 2, 60_000);
                    continue;
                }
                SafeFileHandle dir = NtDirectory.CreateFileW(rootPath, 0x1 /*LIST_DIRECTORY*/, 0x7, 0, 3, 0x0200_0000 /*BACKUP_SEMANTICS*/ | 0x4000_0000 /*OVERLAPPED*/, 0);
                if (dir.IsInvalid)
                {
                    dir.Dispose();
                    if (Wait(backoffMs)) break;
                    backoffMs = Math.Min(backoffMs * 2, 60_000);
                    continue;
                }
                if (!firstOpen) DebounceFor().Full();   // reopened after a failure: what happened meanwhile is unknown
                firstOpen = false;
                if (NtDirectory.MediumOf(rootPath) == NtMedium.Usb) RegisterDeviceRemoval(dir);
                bool failed = Pump(dir);
                UnregisterDeviceRemoval();
                dir.Dispose();
                if (_stopping) break;
                if (failed) { if (Wait(backoffMs)) break; backoffMs = Math.Min(backoffMs * 2, 60_000); }
                else backoffMs = 1000;
            }
        }

        Debounce DebounceFor() => scan.DebounceFor(rootPath);

        bool Wait(int ms) => WaitForSingleObject(_stop, (uint)ms) == 0;

        /// <summary>Read changes until stopped or failed. True: the watch failed (reopen with backoff).</summary>
        bool Pump(SafeFileHandle dir)
        {
            byte* buffer = (byte*)NativeMemory.AlignedAlloc(BufferBytes, 8);
            var overlapped = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
            nint ioEvent = CreateEventW(0, 1, 0, 0);
            bool extended = true;
            try
            {
                nint* waits = stackalloc nint[2];
                waits[0] = ioEvent;
                waits[1] = _stop;
                while (!_stopping)
                {
                    ResetEvent(ioEvent);
                    *overlapped = default;
                    overlapped->EventHandle = ioEvent;
                    uint ignored;
                    int ok = extended
                        ? ReadDirectoryChangesExW(dir, buffer, BufferBytes, 1, Filter, &ignored, overlapped, 0, 2 /*ReadDirectoryNotifyExtendedInformation*/)
                        : ReadDirectoryChangesW(dir, buffer, BufferBytes, 1, Filter, &ignored, overlapped, 0);
                    if (ok == 0)
                    {
                        int error = Marshal.GetLastPInvokeError();
                        if (extended && error is 1 or 50 or 87) { extended = false; continue; }   // not NTFS: the plain call
                        return true;
                    }
                    uint which = WaitForMultipleObjects(2, waits, 0, 0xFFFF_FFFF);
                    if (which != 0)
                    {
                        CancelIoEx(dir, overlapped);
                        GetOverlappedResult(dir, overlapped, &ignored, 1);
                        return false;
                    }
                    uint bytes;
                    if (GetOverlappedResult(dir, overlapped, &bytes, 0) == 0)
                    {
                        int error = Marshal.GetLastPInvokeError();
                        if (error == 1022 /*ERROR_NOTIFY_ENUM_DIR*/) { DebounceFor().Full(); continue; }
                        DebounceFor().Full();
                        return true;
                    }
                    if (bytes == 0) { DebounceFor().Full(); continue; }   // the kernel's buffer overflowed: walk the root
                    Parse(buffer, extended);
                }
                return false;
            }
            finally
            {
                CloseHandle(ioEvent);
                NativeMemory.Free(overlapped);
                NativeMemory.AlignedFree(buffer);
            }
        }

        void Parse(byte* buffer, bool extended)
        {
            Debounce d = DebounceFor();
            int at = 0;
            while (true)
            {
                byte* r = buffer + at;
                uint next = *(uint*)r;
                string name;
                UInt128 parent = 0;
                bool relevant = true;
                if (extended)
                {
                    bool isDir = (*(uint*)(r + 56) & NtDirectory.AttrDirectory) != 0;
                    name = new string((char*)(r + 84), 0, (int)(*(uint*)(r + 80) / 2));
                    parent = new UInt128(0, *(ulong*)(r + 72));
                    // A file that is neither audio nor a folder image changes nothing the index holds.
                    relevant = isDir || LocalTags.IsScanExtension(name) || IsImage(name);
                }
                else name = new string((char*)(r + 12), 0, (int)(*(uint*)(r + 8) / 2));
                if (relevant) d.Note(name, parent);
                if (next == 0) return;
                at += (int)next;
            }
        }

        // ── USB safe removal ─────────────────────────────────────────────────────────────────────────────────────────

        void RegisterDeviceRemoval(SafeFileHandle dir)
        {
            if (!_self.IsAllocated) _self = GCHandle.Alloc(this);
            byte* filter = stackalloc byte[416];   // CM_NOTIFY_FILTER
            new Span<byte>(filter, 416).Clear();
            *(uint*)filter = 416;                  // cbSize
            *(uint*)(filter + 8) = 1;              // CM_NOTIFY_FILTER_TYPE_DEVICEHANDLE
            *(nint*)(filter + 16) = dir.DangerousGetHandle();
            nint notify;
            if (CM_Register_Notification(filter, GCHandle.ToIntPtr(_self), &OnDevice, &notify) == 0) _deviceNotify = notify;
        }

        void UnregisterDeviceRemoval()
        {
            nint notify = Interlocked.Exchange(ref _deviceNotify, 0);
            if (notify != 0) CM_Unregister_Notification(notify);
            if (_self.IsAllocated) _self.Free();
        }

        [UnmanagedCallersOnly]
        static uint OnDevice(nint notify, nint context, uint action, nint data, uint size)
        {
            // Query-remove, remove-pending, remove-complete: let go of the handle now so "safely remove" succeeds.
            if (action is 2 or 4 or 5 && GCHandle.FromIntPtr(context).Target is Watcher w)
            {
                w._stopping = true;
                SetEvent(w._stop);
                w._thread?.Join(2000);
                w.scan._watchers.TryRemove(w.rootPath, out _);
                w.scan.RootState?.Invoke(w.rootPath, LocalRootState.Offline);
            }
            return 0;
        }

        [LibraryImport("kernel32.dll")] private static partial nint CreateEventW(nint security, int manualReset, int initialState, nint name);
        [LibraryImport("kernel32.dll")] private static partial int SetEvent(nint ev);
        [LibraryImport("kernel32.dll")] private static partial int ResetEvent(nint ev);
        [LibraryImport("kernel32.dll")] private static partial int CloseHandle(nint handle);
        [LibraryImport("kernel32.dll")] private static partial uint WaitForSingleObject(nint handle, uint ms);
        [LibraryImport("kernel32.dll")] private static partial uint WaitForMultipleObjects(uint count, nint* handles, int waitAll, uint ms);
        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial int ReadDirectoryChangesExW(SafeFileHandle dir, void* buffer, uint length, int subtree, uint filter, uint* returned,
            NativeOverlapped* overlapped, nint completion, int infoClass);
        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial int ReadDirectoryChangesW(SafeFileHandle dir, void* buffer, uint length, int subtree, uint filter, uint* returned,
            NativeOverlapped* overlapped, nint completion);
        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial int GetOverlappedResult(SafeFileHandle file, NativeOverlapped* overlapped, uint* bytes, int wait);
        [LibraryImport("kernel32.dll")] private static partial int CancelIoEx(SafeFileHandle file, NativeOverlapped* overlapped);
        [LibraryImport("cfgmgr32.dll")]
        private static partial uint CM_Register_Notification(void* filter, nint context, delegate* unmanaged<nint, nint, uint, nint, uint, uint> callback, nint* notify);
        [LibraryImport("cfgmgr32.dll")] private static partial uint CM_Unregister_Notification(nint notify);
    }
}
