using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Wavee.Sdk.Streams;

/// <summary>How a <see cref="ChunkDiskCache"/> derives its size budget.</summary>
public enum AudioCacheBudgetMode
{
    /// <summary>An explicit byte budget.</summary>
    FixedBytes = 0,
    /// <summary>A percentage of the volume's total size.</summary>
    DriveShare = 1,
    /// <summary>No budget — only the free-space reserve applies.</summary>
    Unlimited = 2,
}

/// <summary>What to do with the existing content when the cache root moves.</summary>
public enum AudioCacheRelocationMode
{
    /// <summary>Copy every verified chunk to the new root, then delete the old one.</summary>
    Move,
    /// <summary>Start the new root empty and delete the old one.</summary>
    StartEmptyDeleteOld,
    /// <summary>Start the new root empty and leave the old one on disk.</summary>
    StartEmptyKeepOld,
}

/// <summary>A snapshot of the cache's root, occupancy and headroom.</summary>
/// <param name="Directory">The active root.</param>
/// <param name="Available">False when the volume could not be inspected.</param>
/// <param name="Bytes">Measured bytes currently stored.</param>
/// <param name="BudgetBytes">The effective budget, or null when unlimited.</param>
/// <param name="FreeBytes">Free bytes on the volume.</param>
/// <param name="ReserveBytes">Free space the cache refuses to consume.</param>
/// <param name="WriteEnabled">False when writes are turned off by policy.</param>
public readonly record struct AudioBodyCacheStatus(
    string Directory,
    bool Available,
    long Bytes,
    long? BudgetBytes,
    long FreeBytes,
    long ReserveBytes,
    bool WriteEnabled);

/// <summary>The settings-free snapshot a <see cref="ChunkDiskCache"/> re-reads on every operation. Hosts that keep
/// their cache configuration in user settings hand the cache a provider that builds one of these on demand.</summary>
/// <param name="WriteEnabled">False = read-only (existing chunks still serve).</param>
/// <param name="Directory">The cache root.</param>
/// <param name="Mode">How the budget is derived.</param>
/// <param name="FixedBytes">The byte budget when <paramref name="Mode"/> is <see cref="AudioCacheBudgetMode.FixedBytes"/>.</param>
/// <param name="Percent">The share of the volume when <paramref name="Mode"/> is <see cref="AudioCacheBudgetMode.DriveShare"/> (0 = auto).</param>
public readonly record struct ChunkCachePolicy(
    bool WriteEnabled,
    string Directory,
    AudioCacheBudgetMode Mode,
    long FixedBytes,
    int Percent);

/// <summary>
/// Sparse on-disk cache of RAW (untransformed) media bytes, addressed by a caller-chosen key and a chunk index.
/// Chunks are committed only after their bytes are durable and are verified with SHA-256 on every disk read, so a
/// ciphertext body may be cached at rest and decrypted later, in memory.
/// </summary>
public sealed class ChunkDiskCache : IDisposable
{
    /// <summary>The fixed chunk granularity (64 KiB). Baked into the on-disk map format.</summary>
    public const int ChunkBytes = 64 * 1024;

    /// <summary>The smallest budget the cache will honour.</summary>
    public const long MinBudgetBytes = 64L << 20;

    /// <summary>The budget used when a caller does not name one.</summary>
    public const long DefaultFixedBudgetBytes = 32L << 30;

    /// <summary>The child directory a relocation base path is resolved into.</summary>
    public const string RelocationChildName = "WaveeAudioCache";

    const int HeaderCoreBytes = 20;             // magic + chunk size + total size + chunk count
    const int HeaderBytes = HeaderCoreBytes + 32;
    const int EntryBytes = 1 + 32;              // committed marker + SHA-256
    const long MinimumReserveBytes = 5L << 30;
    const string MarkerFileName = ".wavee-audio-cache";
    const string MarkerText = "Wavee encrypted audio cache v2";
    const string RootMutexPrefix = "Wavee.AudioCache.";
    static readonly byte[] Magic = "WAC2"u8.ToArray();

    readonly Func<ChunkCachePolicy>? _policyProvider;
    readonly StreamLogger _log;
    readonly string _defaultDirectory;
    readonly object _stateGate = new();
    readonly object _trimLock = new();
    readonly ConcurrentDictionary<string, object> _fileLocks = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, long> _lastTouch = new(StringComparer.OrdinalIgnoreCase);
    string _staticDirectory;
    long _staticBudget;
    string _activeDirectory = "";
    long _approxBytes;
    bool _scanPending;   // a WarmScan is owed for the current _activeDirectory (guarded by _stateGate)
    int _scanGen;        // bumped on every directory switch so a stale scan can't publish over the new root's count

    // ── Background write-through (item 5) ───────────────────────────────────────────────────────────────────────────
    // WriteChunk used to do everything — per-file lock, header re-read, DriveInfo probe, SHA-256, and a
    // flush-to-physical-disk fsync — INLINE, on the caller's thread, which for a RangedHttpSource fetch is the thread
    // the decode worker is blocked on. None of that needs to happen before the fetch returns: the caller only needs
    // its bytes (RAM already has them) and its wake-up. So WriteChunk now just rents a pooled copy of the chunk and
    // hands it to one dedicated low-priority writer thread; the actual disk work (WriteChunkCore) runs there, off the
    // fetch path entirely. Crash consistency doesn't regress: TryReadChunk already re-verifies SHA-256 on every read
    // and discards on mismatch, which is what covered a torn write before this change too.
    readonly BlockingCollection<PendingWrite> _writeQueue = new();
    readonly object _drainGate = new();
    int _pendingWrites;
    Thread? _writerThread;
    int _disposed;   // 0/1 — guards WriteChunk against racing a Dispose() and makes Dispose() itself idempotent

    readonly record struct PendingChunk(byte[] Buffer, int Length);
    readonly record struct PendingWrite(string FileId, int ChunkIndex, string Key, PendingChunk Chunk);

    // Read-your-own-write staging: WriteChunk is now fire-and-forget (the actual disk commit happens later, on the
    // background writer), but a caller that writes a chunk and immediately reads it back — the common in-process
    // pattern, and the shape every existing test uses — must still see it. TryReadChunk checks here FIRST. Keyed by
    // (root, fileId, chunkIndex); last-writer-wins on a key collision (an overwritten predecessor's buffer is simply
    // left for the GC rather than pooled — safe, just not reused, and this race is vanishingly rare in practice: a
    // chunk index is normally flushed exactly once per fetch completion).
    readonly ConcurrentDictionary<string, PendingChunk> _pending = new(StringComparer.Ordinal);

    static string PendingKey(string root, string fileId, int chunkIndex) => root + "\0" + Stem(fileId) + "\0" + chunkIndex;

    // Per-volume DriveInfo probe cache: CanCommit used to call DriveInfo.AvailableFreeSpace on every single 64 KiB
    // chunk write. Free space does not move fast enough to need a live read every time — a few seconds of staleness
    // is fine, and avoiding the syscall is exactly the point (this ran per-chunk, inline, on the fetch path before).
    static readonly ConcurrentDictionary<string, (long Total, long Free, long AtTicks)> _driveCache = new(StringComparer.OrdinalIgnoreCase);
    const long DriveCacheTicks = 5 * TimeSpan.TicksPerSecond;

    readonly record struct MapHeader(long TotalSize, int ChunkCount);

    /// <summary>A cache rooted at a fixed directory with a fixed budget.</summary>
    /// <param name="directory">The cache root (created on demand).</param>
    /// <param name="budgetBytes">Byte budget, clamped up to <see cref="MinBudgetBytes"/>.</param>
    /// <param name="log">Optional logger; <c>default</c> is a no-op.</param>
    /// <param name="defaultDirectory">The host's canonical cache root — a root equal to it counts as owned even when
    /// its marker file is missing. Empty = only the marker file confers ownership.</param>
    public ChunkDiskCache(string directory, long budgetBytes = DefaultFixedBudgetBytes, StreamLogger log = default,
        string? defaultDirectory = null)
    {
        _log = log;
        _defaultDirectory = defaultDirectory ?? "";
        _staticDirectory = Path.GetFullPath(directory);
        _staticBudget = Math.Max(MinBudgetBytes, budgetBytes);
        EnsureActiveDirectory(CurrentPolicy().Directory);
        StartWriterThread();
    }

    /// <summary>A cache whose root and budget are re-read from <paramref name="policyProvider"/> on every operation
    /// (so a settings change takes effect without reconstructing the cache).</summary>
    /// <param name="policyProvider">Called on every operation; must be cheap and never throw.</param>
    /// <param name="log">Optional logger; <c>default</c> is a no-op.</param>
    /// <param name="defaultDirectory">The host's canonical cache root — see the other constructor.</param>
    public ChunkDiskCache(Func<ChunkCachePolicy> policyProvider, StreamLogger log = default, string? defaultDirectory = null)
    {
        _log = log;
        _defaultDirectory = defaultDirectory ?? "";
        _policyProvider = policyProvider;
        var policy = CurrentPolicy();
        _staticDirectory = policy.Directory;
        _staticBudget = policy.FixedBytes;
        EnsureActiveDirectory(policy.Directory);
        StartWriterThread();
    }

    void StartWriterThread()
    {
        var thread = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "Wavee.ChunkDiskCache.Writer",
            Priority = ThreadPriority.BelowNormal,
        };
        _writerThread = thread;
        thread.Start();
    }

    void WriterLoop()
    {
        foreach (var item in _writeQueue.GetConsumingEnumerable())
        {
            try { WriteChunkCore(item.FileId, item.ChunkIndex, item.Chunk.Buffer.AsSpan(0, item.Chunk.Length)); }
            catch { }
            finally
            {
                // Only pool the buffer if we're still the CURRENT pending entry for this key (a newer WriteChunk for
                // the same chunk may already have replaced us in _pending — see WriteChunk). If it did, our buffer is
                // simply left for the GC instead of pooled: safe (nobody else can reach it once superseded), just not
                // reused.
                if (_pending.TryRemove(new KeyValuePair<string, PendingChunk>(item.Key, item.Chunk)))
                    ArrayPool<byte>.Shared.Return(item.Chunk.Buffer);
                if (Interlocked.Decrement(ref _pendingWrites) <= 0)
                    lock (_drainGate) Monitor.PulseAll(_drainGate);
            }
        }
    }

    /// <summary>Block (briefly — this is for a deterministic shutdown/handoff, never the fetch path) until every chunk
    /// queued so far by <see cref="WriteChunk"/> has been written, or <paramref name="timeoutMs"/> elapses. Note this
    /// drains the WHOLE shared writer (every fileId currently queued on this cache instance), not just one stream's
    /// chunks — precise-enough for "give a just-fetched track's bytes a chance to land before this stream goes away"
    /// without a per-fileId tracking structure.</summary>
    public bool WaitForPendingWrites(int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        lock (_drainGate)
        {
            while (Volatile.Read(ref _pendingWrites) > 0)
            {
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0) return false;
                Monitor.Wait(_drainGate, (int)Math.Min(remaining, int.MaxValue));
            }
        }
        return true;
    }

    /// <summary>A picker stores a PARENT directory; the cache owns only its dedicated child beneath it.</summary>
    /// <param name="selectedBasePath">The user-picked parent, or null/blank for <paramref name="defaultDirectory"/>.</param>
    /// <param name="defaultDirectory">The host's canonical cache root.</param>
    public static string ResolveDirectory(string? selectedBasePath, string defaultDirectory) =>
        string.IsNullOrWhiteSpace(selectedBasePath)
            ? Path.GetFullPath(defaultDirectory)
            : Path.Combine(Path.GetFullPath(selectedBasePath), RelocationChildName);

    /// <summary>The root currently in force (creating it if needed).</summary>
    public string CurrentDirectory => EnsureActiveDirectory(CurrentPolicy().Directory);

    ChunkCachePolicy CurrentPolicy() => _policyProvider?.Invoke()
        ?? new ChunkCachePolicy(true, _staticDirectory, AudioCacheBudgetMode.FixedBytes, Volatile.Read(ref _staticBudget), 0);

    // Activation is CHEAP by contract: create-directory + marker only. The reconcile/measure sweep that used to run here
    // (five recursive enumerations, an open+SHA per .map) sat synchronously inside the caller's construction — i.e. on
    // the login splash's "Starting audio" step — and cost 16–31 s against a cold-NTFS 3.5k-file cache. It is owed
    // instead (armed via _scanPending) and paid off-path by WarmScan. Until the scan lands, _approxBytes undercounts:
    // reads never consult it, and the write-side budget check self-corrects on the next TrimInternal (which re-measures).
    string EnsureActiveDirectory(string directory)
    {
        directory = Path.GetFullPath(directory);
        lock (_stateGate)
        {
            if (string.Equals(_activeDirectory, directory, StringComparison.OrdinalIgnoreCase)) return directory;
            _activeDirectory = directory;
            _scanGen++;
            _scanPending = true;
            _approxBytes = 0;
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, MarkerFileName), MarkerText);
            }
            catch { }
            return directory;
        }
    }

    /// <summary>Run the owed reconcile + measure sweep for the active root, if any. Idempotent per directory switch;
    /// runs on the CALLER's thread (callers background it). This is where torn .tmp files, invalid maps and orphan
    /// bodies from crashed sessions get cleaned, and where the budget byte-count is seeded. Never throws.</summary>
    public void WarmScan()
    {
        string root;
        int gen;
        lock (_stateGate)
        {
            if (!_scanPending) return;
            _scanPending = false;
            root = _activeDirectory;
            gen = _scanGen;
        }
        long start = Environment.TickCount64;
        long bytes = 0;
        int files = 0, dropped = 0;
        try { (bytes, files, dropped) = ReconcileAndMeasure(root); }
        catch { }
        lock (_stateGate)
        {
            if (gen == _scanGen) Interlocked.Exchange(ref _approxBytes, bytes);
        }
        _log.Event(StreamLogLevel.Info, "audio.cache.scan", "Audio body cache reconciled + measured",
            Environment.TickCount64 - start,
            [
                StreamLogField.Of("files", files),
                StreamLogField.Of("bytes", bytes),
                StreamLogField.Of("dropped", dropped),
            ]);
    }

    /// <summary>Reconcile + Measure folded into ONE enumeration pass. .tmp files belonging to THIS process, or younger
    /// than 10 minutes, are live EnsureMap intermediates (the scan runs concurrently with traffic) — skipped, not reaped.</summary>
    (long Bytes, int Files, int Dropped) ReconcileAndMeasure(string root)
    {
        if (!Directory.Exists(root)) return (0, 0, 0);
        long bytes = 0;
        int files = 0, dropped = 0;
        string pidMarker = "." + Environment.ProcessId + ".";
        var maps = new List<string>();
        var encs = new List<string>();
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                if (file.Contains(pidMarker, StringComparison.Ordinal)) continue;
                try { if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromMinutes(10)) continue; } catch { }
                TryDelete(file);
                dropped++;
            }
            else if (file.EndsWith(".map", StringComparison.OrdinalIgnoreCase)) maps.Add(file);
            else if (file.EndsWith(".enc", StringComparison.OrdinalIgnoreCase)) encs.Add(file);
            else bytes += SafeLength(file);   // the marker file
        }
        var validStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string map in maps)
        {
            if (!ValidMapFile(map))
            {
                TryDelete(map);
                TryDelete(Path.ChangeExtension(map, ".enc"));
                dropped++;
                continue;
            }
            validStems.Add(Path.ChangeExtension(map, null));
            bytes += SafeLength(map);
            files++;
        }
        foreach (string enc in encs)
        {
            if (!validStems.Contains(Path.ChangeExtension(enc, null))) { TryDelete(enc); dropped++; continue; }
            bytes += SafeLength(enc);
            files++;
        }
        RemoveEmptyDirectories(root);
        return (bytes, files, dropped);
    }

    static string Stem(string fileId)
    {
        string id = (fileId ?? "").Trim().ToLowerInvariant();
        bool safe = id.Length is >= 8 and <= 128 && id.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
        return safe ? id : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
    }

    static string EntryDirectory(string root, string fileId) => Path.Combine(root, Stem(fileId)[..2]);
    static string EncPath(string root, string fileId) => Path.Combine(EntryDirectory(root, fileId), Stem(fileId) + ".enc");
    static string MapPath(string root, string fileId) => Path.Combine(EntryDirectory(root, fileId), Stem(fileId) + ".map");

    static FileStream OpenShared(string path, FileMode mode) =>
        new(path, mode, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);

    object FileLock(string root, string fileId) => _fileLocks.GetOrAdd(root + "\0" + Stem(fileId), static _ => new object());

    static IDisposable? TryAcquireRoot(string root, int timeoutMs = 250)
    {
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant())));
        var mutex = new Mutex(false, RootMutexPrefix + key);
        try
        {
            try { if (!mutex.WaitOne(timeoutMs)) { mutex.Dispose(); return null; } }
            catch (AbandonedMutexException) { }
            return new MutexLease(mutex);
        }
        catch { mutex.Dispose(); return null; }
    }

    sealed class MutexLease(Mutex mutex) : IDisposable
    {
        public void Dispose() { try { mutex.ReleaseMutex(); } catch { } mutex.Dispose(); }
    }

    /// <summary>The stored total size for <paramref name="fileId"/>, or null when nothing is cached.</summary>
    public long? KnownSize(string fileId)
    {
        var policy = CurrentPolicy();
        string root = EnsureActiveDirectory(policy.Directory);
        try
        {
            using var lease = TryAcquireRoot(root, 25);
            if (lease is null) return null;
            lock (FileLock(root, fileId)) return ReadHeader(root, fileId)?.TotalSize;
        }
        catch { return null; }
    }

    /// <summary>Declare the total size of <paramref name="fileId"/>, allocating its chunk map. A different size
    /// discards the existing entry. No-op when writes are disabled.</summary>
    public void SetSize(string fileId, long size)
    {
        if (size <= 0 || size > int.MaxValue) return;
        var policy = CurrentPolicy();
        if (!policy.WriteEnabled) return;
        string root = EnsureActiveDirectory(policy.Directory);
        try
        {
            using var lease = TryAcquireRoot(root);
            if (lease is null || !CurrentPolicy().WriteEnabled) return;
            lock (FileLock(root, fileId)) EnsureMap(root, fileId, size);
        }
        catch { }
    }

    /// <summary>Read one committed chunk, verifying its SHA-256. False (with <paramref name="length"/> 0) on any miss;
    /// a digest mismatch clears the entry so the next fetch refills it.</summary>
    public bool TryReadChunk(string fileId, int chunkIndex, Span<byte> destination, out int length)
    {
        length = 0;
        if (chunkIndex < 0) return false;
        var policy = CurrentPolicy();
        string root = EnsureActiveDirectory(policy.Directory);

        // Read-your-own-write: a chunk WriteChunk queued but the background writer hasn't committed yet is still
        // served correctly, straight out of memory — no need to wait for (or race) the disk commit.
        if (_pending.TryGetValue(PendingKey(root, fileId, chunkIndex), out var pending))
        {
            if (destination.Length < pending.Length) return false;
            pending.Buffer.AsSpan(0, pending.Length).CopyTo(destination);
            length = pending.Length;
            return true;
        }

        try
        {
            using var lease = TryAcquireRoot(root, 25);
            if (lease is null) return false;
            lock (FileLock(root, fileId))
            {
                var header = ReadHeader(root, fileId);
                if (header is null || chunkIndex >= header.Value.ChunkCount) return false;
                int expected = ExpectedLength(header.Value.TotalSize, chunkIndex);
                if (destination.Length < expected || !TryReadEntry(root, fileId, chunkIndex, out var digest)) return false;

                string enc = EncPath(root, fileId);
                if (!File.Exists(enc)) { ClearEntry(root, fileId, chunkIndex); return false; }
                using var fs = OpenShared(enc, FileMode.Open);
                long offset = (long)chunkIndex * ChunkBytes;
                if (fs.Length < offset + expected) { ClearEntry(root, fileId, chunkIndex); return false; }
                fs.Position = offset;
                fs.ReadExactly(destination[..expected]);
                Span<byte> actual = stackalloc byte[32];
                SHA256.HashData(destination[..expected], actual);
                if (!CryptographicOperations.FixedTimeEquals(actual, digest))
                {
                    ClearEntry(root, fileId, chunkIndex);
                    return false;
                }
                length = expected;
                TouchMap(root, fileId);
                return true;
            }
        }
        catch { return false; }
    }

    /// <summary>Queue one chunk for a durable, background write + digest commit. Returns immediately — no per-file
    /// lock, header re-read, DriveInfo probe, SHA-256 or disk flush on THIS thread; all of that happens later, off the
    /// caller's thread, in <see cref="WriteChunkCore"/> on the cache's single background writer. The chunk is staged
    /// into <see cref="_pending"/> synchronously first, so <see cref="TryReadChunk"/> can serve it before the disk
    /// commit lands (read-your-own-write). Silently drops the chunk (same as before) when writes are disabled — the
    /// actual "was there room / was the size right" checks are deferred to the writer, since they need the (possibly
    /// stale) on-disk header anyway.</summary>
    public void WriteChunk(string fileId, int chunkIndex, ReadOnlySpan<byte> data)
    {
        if (chunkIndex < 0 || data.IsEmpty) return;
        if (Volatile.Read(ref _disposed) != 0) return;   // Dispose() owns the queue from here on — drop, don't race it
        var policy = CurrentPolicy();
        if (!policy.WriteEnabled) return;
        string root = EnsureActiveDirectory(policy.Directory);
        var pooled = ArrayPool<byte>.Shared.Rent(data.Length);
        data.CopyTo(pooled);
        var mine = new PendingChunk(pooled, data.Length);
        string key = PendingKey(root, fileId, chunkIndex);
        _pending[key] = mine;
        Interlocked.Increment(ref _pendingWrites);
        bool queued;
        try { queued = _writeQueue.TryAdd(new PendingWrite(fileId, chunkIndex, key, mine)); }
        catch (ObjectDisposedException) { queued = false; }        // lost the race with Dispose() disposing the queue
        catch (InvalidOperationException) { queued = false; }      // lost the race with Dispose()'s CompleteAdding()
        if (!queued)
        {
            if (_pending.TryRemove(new KeyValuePair<string, PendingChunk>(key, mine)))
                ArrayPool<byte>.Shared.Return(pooled);
            if (Interlocked.Decrement(ref _pendingWrites) <= 0)
                lock (_drainGate) Monitor.PulseAll(_drainGate);
        }
    }

    /// <summary>Stops the background writer and releases its queue. Idempotent, and safe even with a write in flight:
    /// setting <see cref="_disposed"/> first stops any new chunk from being queued (<see cref="WriteChunk"/> checks it
    /// before touching <see cref="_writeQueue"/>), so by the time <see cref="BlockingCollection{T}.CompleteAdding"/>
    /// runs, nothing can still be racing an add against it. Never throws — this runs from ordinary teardown paths
    /// (including finalizer-adjacent `using` blocks), where an exception would just mask whatever caused it.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { WaitForPendingWrites(); } catch { }
        try { _writeQueue.CompleteAdding(); } catch { }
        bool joined = true;
        try { if (_writerThread is { IsAlive: true } thread) joined = thread.Join(2000); } catch { }
        // Only dispose the collection once we know the writer thread is done pulling from it — GetConsumingEnumerable()
        // can throw ObjectDisposedException on that thread if the collection is disposed out from under a still-running
        // enumerator. A timed-out Join leaks the BlockingCollection (same leaked-thread shape as before this change),
        // which beats crashing the writer thread.
        if (joined) try { _writeQueue.Dispose(); } catch { }
    }

    /// <summary>Durably store one chunk and commit its digest — the actual disk work <see cref="WriteChunk"/> used to
    /// do inline, now run only on the background writer thread. No <c>fs.Flush(true)</c>: a flush-to-physical-disk
    /// fsync per 64 KiB chunk cost tens of ms on a loaded drive for no correctness gain — <see cref="TryReadChunk"/>
    /// already re-verifies the SHA-256 on every read and discards on mismatch, which is what actually covers a torn
    /// write (crash mid-write, power loss), flushed or not.</summary>
    void WriteChunkCore(string fileId, int chunkIndex, ReadOnlySpan<byte> data)
    {
        var policy = CurrentPolicy();
        if (!policy.WriteEnabled) return;
        string root = EnsureActiveDirectory(policy.Directory);
        try
        {
            using var lease = TryAcquireRoot(root);
            if (lease is null || !CurrentPolicy().WriteEnabled) return;
            lock (FileLock(root, fileId))
            {
                var header = ReadHeader(root, fileId);
                if (header is null || chunkIndex >= header.Value.ChunkCount) return;
                int expected = ExpectedLength(header.Value.TotalSize, chunkIndex);
                if (data.Length != expected) return;
                long growth = ProjectedGrowth(root, fileId, chunkIndex, expected);
                if (!CanCommit(policy, root, growth)) return;

                string enc = EncPath(root, fileId);
                Directory.CreateDirectory(Path.GetDirectoryName(enc)!);
                using (var fs = OpenShared(enc, FileMode.OpenOrCreate))
                {
                    long offset = (long)chunkIndex * ChunkBytes;
                    fs.Position = offset;
                    fs.Write(data);
                }

                Span<byte> digest = stackalloc byte[32];
                SHA256.HashData(data, digest);
                CommitEntry(root, fileId, chunkIndex, digest);
                Interlocked.Add(ref _approxBytes, growth);
                TouchMap(root, fileId, force: true);
            }
        }
        catch { }
    }

    static int ExpectedLength(long totalSize, int chunkIndex)
    {
        long offset = (long)chunkIndex * ChunkBytes;
        return (int)Math.Min(ChunkBytes, Math.Max(0, totalSize - offset));
    }

    long ProjectedGrowth(string root, string fileId, int chunkIndex, int expected)
    {
        string enc = EncPath(root, fileId);
        long old = 0;
        try { if (File.Exists(enc)) old = new FileInfo(enc).Length; } catch { }
        long next = Math.Max(old, (long)chunkIndex * ChunkBytes + expected);
        return Math.Max(0, next - old);
    }

    bool CanCommit(ChunkCachePolicy policy, string root, long growth)
    {
        var capacity = Capacity(root, policy);
        if (!capacity.Available || capacity.FreeBytes - growth < capacity.ReserveBytes) return false;
        if (capacity.BudgetBytes is not { } budget) return true;
        if (Volatile.Read(ref _approxBytes) + growth <= budget) return true;
        TrimInternal(root, budget);
        return Volatile.Read(ref _approxBytes) + growth <= budget;
    }

    /// <summary>Root, occupancy, budget and headroom right now (measures the directory).</summary>
    public AudioBodyCacheStatus Status()
    {
        var policy = CurrentPolicy();
        string root = EnsureActiveDirectory(policy.Directory);
        var cap = Capacity(root, policy);
        return new AudioBodyCacheStatus(root, cap.Available, DirectoryBytes(), cap.BudgetBytes,
            cap.FreeBytes, cap.ReserveBytes, policy.WriteEnabled);
    }

    readonly record struct CapacityState(bool Available, long FreeBytes, long ReserveBytes, long? BudgetBytes);

    // DriveInfo.AvailableFreeSpace is a syscall; Capacity used to make one per chunk write (potentially thousands per
    // track). Free space doesn't move fast enough to need a live read every time, so this caches per volume for a few
    // seconds — see the field doc on _driveCache.
    static (long Total, long Free) ReadDriveInfoCached(string volumeRoot)
    {
        long now = DateTime.UtcNow.Ticks;
        if (_driveCache.TryGetValue(volumeRoot, out var cached) && now - cached.AtTicks < DriveCacheTicks)
            return (cached.Total, cached.Free);
        var drive = new DriveInfo(volumeRoot);
        if (!drive.IsReady) throw new IOException($"drive '{volumeRoot}' not ready");
        var fresh = (drive.TotalSize, drive.AvailableFreeSpace, now);
        _driveCache[volumeRoot] = fresh;
        return (fresh.TotalSize, fresh.AvailableFreeSpace);
    }

    static CapacityState Capacity(string root, ChunkCachePolicy policy)
    {
        try
        {
            string? volumeRoot = Path.GetPathRoot(Path.GetFullPath(root));
            if (string.IsNullOrEmpty(volumeRoot)) return new(false, 0, MinimumReserveBytes, null);
            long total, free;
            try { (total, free) = ReadDriveInfoCached(volumeRoot); }
            catch { return new(false, 0, MinimumReserveBytes, null); }
            long reserve = Math.Max(MinimumReserveBytes, total / 20);
            long? budget = policy.Mode switch
            {
                AudioCacheBudgetMode.Unlimited => null,
                AudioCacheBudgetMode.FixedBytes => Math.Max(MinBudgetBytes, policy.FixedBytes),
                _ when policy.Percent == 0 => Math.Clamp(total / 10, 16L << 30, 128L << 30),
                _ => Math.Max(MinBudgetBytes, (long)(total * (policy.Percent / 100d))),
            };
            return new(true, free, reserve, budget);
        }
        catch { return new(false, 0, MinimumReserveBytes, null); }
    }

    void EnsureMap(string root, string fileId, long totalSize)
    {
        var current = ReadHeader(root, fileId);
        if (current?.TotalSize == totalSize) return;
        if (current is not null) DeletePair(root, fileId);
        int chunks = checked((int)((totalSize + ChunkBytes - 1) / ChunkBytes));
        string map = MapPath(root, fileId);
        Directory.CreateDirectory(Path.GetDirectoryName(map)!);
        string tmp = map + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N") + ".tmp";
        byte[] header = new byte[HeaderBytes];
        Magic.CopyTo(header, 0);
        BitConverter.TryWriteBytes(header.AsSpan(4, 4), ChunkBytes);
        BitConverter.TryWriteBytes(header.AsSpan(8, 8), totalSize);
        BitConverter.TryWriteBytes(header.AsSpan(16, 4), chunks);
        SHA256.HashData(header.AsSpan(0, HeaderCoreBytes), header.AsSpan(HeaderCoreBytes, 32));
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(header);
                fs.SetLength(HeaderBytes + (long)chunks * EntryBytes);
                fs.Flush(true);
            }
            File.Move(tmp, map, true);
            Interlocked.Add(ref _approxBytes, new FileInfo(map).Length);
        }
        finally { TryDelete(tmp); }
    }

    MapHeader? ReadHeader(string root, string fileId)
    {
        string map = MapPath(root, fileId);
        if (!File.Exists(map)) return null;
        try
        {
            Span<byte> header = stackalloc byte[HeaderBytes];
            using var fs = OpenShared(map, FileMode.Open);
            if (fs.Length < HeaderBytes) { DeletePair(root, fileId); return null; }
            fs.ReadExactly(header);
            if (!header[..4].SequenceEqual(Magic)) { DeletePair(root, fileId); return null; }
            int chunkBytes = BitConverter.ToInt32(header.Slice(4, 4));
            long total = BitConverter.ToInt64(header.Slice(8, 8));
            int chunks = BitConverter.ToInt32(header.Slice(16, 4));
            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(header[..HeaderCoreBytes], digest);
            long expectedFile = HeaderBytes + (long)chunks * EntryBytes;
            if (chunkBytes != ChunkBytes || total <= 0 || total > int.MaxValue || chunks <= 0 ||
                chunks != (total + ChunkBytes - 1) / ChunkBytes || fs.Length != expectedFile ||
                !CryptographicOperations.FixedTimeEquals(digest, header.Slice(HeaderCoreBytes, 32)))
            {
                DeletePair(root, fileId);
                return null;
            }
            return new(total, chunks);
        }
        catch { return null; }
    }

    bool TryReadEntry(string root, string fileId, int index, out byte[] digest)
    {
        digest = Array.Empty<byte>();
        string map = MapPath(root, fileId);
        using var fs = OpenShared(map, FileMode.Open);
        fs.Position = HeaderBytes + (long)index * EntryBytes;
        Span<byte> entry = stackalloc byte[EntryBytes];
        fs.ReadExactly(entry);
        if (entry[0] != 1) return false;
        digest = entry[1..].ToArray();
        return true;
    }

    void CommitEntry(string root, string fileId, int index, ReadOnlySpan<byte> digest)
    {
        string map = MapPath(root, fileId);
        using var fs = OpenShared(map, FileMode.Open);
        fs.Position = HeaderBytes + (long)index * EntryBytes;
        Span<byte> entry = stackalloc byte[EntryBytes];
        entry[0] = 1;
        digest.CopyTo(entry[1..]);
        fs.Write(entry);
        // No fs.Flush(true) here either — see WriteChunkCore: the SHA-256 verify-and-discard on read already covers
        // a torn commit-entry write, so paying an fsync per chunk buys nothing.
    }

    void ClearEntry(string root, string fileId, int index)
    {
        try
        {
            string map = MapPath(root, fileId);
            using var fs = OpenShared(map, FileMode.Open);
            fs.Position = HeaderBytes + (long)index * EntryBytes;
            fs.WriteByte(0);
            fs.Flush(true);
        }
        catch { }
    }

    /// <summary>Drop everything cached for <paramref name="fileId"/>.</summary>
    public void Invalidate(string fileId)
    {
        string root = EnsureActiveDirectory(CurrentPolicy().Directory);
        try { using var lease = TryAcquireRoot(root); if (lease is not null) lock (FileLock(root, fileId)) DeletePair(root, fileId); }
        catch { }
    }

    void DeletePair(string root, string fileId)
    {
        TryDelete(EncPath(root, fileId));
        TryDelete(MapPath(root, fileId));
    }

    /// <summary>Delete every cached body + map under the active root (the root must be owned).</summary>
    public void ClearAll()
    {
        // Same hazard PrepareRelocation guards against (see its call to WaitForPendingWrites): a chunk WriteChunk
        // queued before this call but that the background writer hadn't committed yet would otherwise land AFTER the
        // delete pass below and resurrect a file the caller just believed cleared. Draining first also empties
        // _pending as a side effect (the writer removes its entry once it finishes, win or lose — see WriterLoop).
        WaitForPendingWrites();
        string root = EnsureActiveDirectory(CurrentPolicy().Directory);
        try
        {
            using var lease = TryAcquireRoot(root, 2000);
            if (lease is null || !IsOwnedRoot(root)) return;
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                if (file.EndsWith(".enc", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".map", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    TryDelete(file);
            RemoveEmptyDirectories(root);
            Interlocked.Exchange(ref _approxBytes, Measure(root));
        }
        catch { }
    }

    /// <summary>Measure the active root on disk.</summary>
    public long DirectoryBytes() => Measure(EnsureActiveDirectory(CurrentPolicy().Directory));

    /// <summary>Set the fixed budget (ignored while a policy provider is in force) and trim down to it.</summary>
    public void SetBudget(long budgetBytes)
    {
        if (budgetBytes <= 0) return;
        Interlocked.Exchange(ref _staticBudget, Math.Max(MinBudgetBytes, budgetBytes));
        TrimToBudget(budgetBytes);
    }

    /// <summary>Evict least-recently-used entries until the root fits <paramref name="budgetBytes"/>. Returns bytes freed.</summary>
    public long TrimToBudget(long budgetBytes) => TrimInternal(EnsureActiveDirectory(CurrentPolicy().Directory), Math.Max(MinBudgetBytes, budgetBytes));

    /// <summary>Trim to the policy's current budget (no-op when unlimited). Returns bytes freed.</summary>
    public long Trim()
    {
        var policy = CurrentPolicy();
        string root = EnsureActiveDirectory(policy.Directory);
        var budget = Capacity(root, policy).BudgetBytes;
        return budget is null ? 0 : TrimInternal(root, budget.Value);
    }

    long TrimInternal(string root, long budget)
    {
        if (!Monitor.TryEnter(_trimLock)) return 0;
        try
        {
            using var lease = TryAcquireRoot(root, 2000);
            if (lease is null) return 0;
            var maps = Directory.Exists(root)
                ? Directory.EnumerateFiles(root, "*.map", SearchOption.AllDirectories).Select(static p => new FileInfo(p)).ToList()
                : [];
            long total = Measure(root);
            if (total <= budget) { Interlocked.Exchange(ref _approxBytes, total); return 0; }
            maps.Sort(static (a, b) => a.LastAccessTimeUtc.CompareTo(b.LastAccessTimeUtc));
            long target = (long)(budget * 0.9);
            long freed = 0;
            foreach (var map in maps)
            {
                if (total <= target) break;
                string enc = Path.ChangeExtension(map.FullName, ".enc");
                long bytes = map.Length + SafeLength(enc);
                TryDelete(enc);
                TryDelete(map.FullName);
                total -= bytes;
                freed += bytes;
            }
            RemoveEmptyDirectories(root);
            Interlocked.Exchange(ref _approxBytes, Math.Max(0, total));
            return freed;
        }
        catch { return 0; }
        finally { Monitor.Exit(_trimLock); }
    }

    /// <summary>Prepares a new owned root. The caller persists the new base path only after this succeeds.</summary>
    /// <param name="newBasePath">The new PARENT directory (the cache owns its child beneath it).</param>
    /// <param name="mode">What happens to the existing content.</param>
    /// <param name="ct">Cancels a long copy.</param>
    public Task<bool> PrepareRelocationAsync(string newBasePath, AudioCacheRelocationMode mode, CancellationToken ct = default)
        => Task.Run(() => PrepareRelocation(newBasePath, mode, ct), ct);

    bool PrepareRelocation(string newBasePath, AudioCacheRelocationMode mode, CancellationToken ct)
    {
        // Relocation walks the ON-DISK .map/.enc pairs directly (CopyValidatedPair / DeleteOwnedContents) — it must
        // never race the background writer, or it can silently miss a chunk that was WriteChunk'd moments ago but
        // hadn't committed yet (or, worse, have that late commit resurrect a file into a root relocation just cleared).
        WaitForPendingWrites();
        string oldRoot = EnsureActiveDirectory(CurrentPolicy().Directory);
        string newRoot = ResolveDirectory(newBasePath, _defaultDirectory);
        if (string.Equals(oldRoot, newRoot, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            Directory.CreateDirectory(newRoot);
            File.WriteAllText(Path.Combine(newRoot, MarkerFileName), MarkerText);
            using var oldLease = TryAcquireRoot(oldRoot, 5000);
            using var newLease = TryAcquireRoot(newRoot, 5000);
            if (oldLease is null || newLease is null) return false;
            if (mode == AudioCacheRelocationMode.Move)
            {
                foreach (string map in Directory.EnumerateFiles(oldRoot, "*.map", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    CopyValidatedPair(oldRoot, newRoot, Path.GetFileNameWithoutExtension(map), ct);
                }
                DeleteOwnedContents(oldRoot);
            }
            else
            {
                DeleteOwnedContents(newRoot);
                File.WriteAllText(Path.Combine(newRoot, MarkerFileName), MarkerText);
                if (mode == AudioCacheRelocationMode.StartEmptyDeleteOld) DeleteOwnedContents(oldRoot);
            }
            return true;
        }
        catch { return false; }
    }

    void CopyValidatedPair(string oldRoot, string newRoot, string stem, CancellationToken ct)
    {
        string oldMap = Directory.EnumerateFiles(oldRoot, stem + ".map", SearchOption.AllDirectories).FirstOrDefault() ?? "";
        if (oldMap.Length == 0) return;
        string oldEnc = Path.ChangeExtension(oldMap, ".enc");
        if (!File.Exists(oldEnc)) return;
        string shard = stem[..2];
        string newDir = Path.Combine(newRoot, shard);
        Directory.CreateDirectory(newDir);
        string newMap = Path.Combine(newDir, stem + ".map");
        string newEnc = Path.Combine(newDir, stem + ".enc");
        using var srcMap = OpenShared(oldMap, FileMode.Open);
        Span<byte> headerBytes = stackalloc byte[HeaderBytes];
        srcMap.ReadExactly(headerBytes);
        if (!headerBytes[..4].SequenceEqual(Magic)) return;
        long total = BitConverter.ToInt64(headerBytes.Slice(8, 8));
        int chunks = BitConverter.ToInt32(headerBytes.Slice(16, 4));
        using var dstMap = new FileStream(newMap, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        dstMap.Write(headerBytes);
        dstMap.SetLength(HeaderBytes + (long)chunks * EntryBytes);
        using var srcEnc = OpenShared(oldEnc, FileMode.Open);
        using var dstEnc = OpenShared(newEnc, FileMode.OpenOrCreate);
        byte[] buffer = new byte[ChunkBytes];
        byte[] entryBytes = new byte[EntryBytes];
        byte[] digestBytes = new byte[32];
        for (int i = 0; i < chunks; i++)
        {
            ct.ThrowIfCancellationRequested();
            srcMap.Position = HeaderBytes + (long)i * EntryBytes;
            Span<byte> entry = entryBytes;
            entry.Clear();
            srcMap.ReadExactly(entry);
            if (entry[0] != 1) continue;
            int len = ExpectedLength(total, i);
            srcEnc.Position = (long)i * ChunkBytes;
            srcEnc.ReadExactly(buffer.AsSpan(0, len));
            Span<byte> digest = digestBytes;
            SHA256.HashData(buffer.AsSpan(0, len), digest);
            if (!CryptographicOperations.FixedTimeEquals(digest, entry[1..])) continue;
            dstEnc.Position = (long)i * ChunkBytes;
            dstEnc.Write(buffer, 0, len);
            dstMap.Position = HeaderBytes + (long)i * EntryBytes;
            dstMap.Write(entry);
        }
        dstEnc.Flush(true);
        dstMap.Flush(true);
    }

    static bool ValidMapFile(string map)
    {
        try
        {
            Span<byte> header = stackalloc byte[HeaderBytes];
            using var fs = OpenShared(map, FileMode.Open);
            if (fs.Length < HeaderBytes) return false;
            fs.ReadExactly(header);
            if (!header[..4].SequenceEqual(Magic)) return false;
            int chunkBytes = BitConverter.ToInt32(header.Slice(4, 4));
            long total = BitConverter.ToInt64(header.Slice(8, 8));
            int chunks = BitConverter.ToInt32(header.Slice(16, 4));
            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(header[..HeaderCoreBytes], digest);
            return chunkBytes == ChunkBytes && total > 0 && total <= int.MaxValue && chunks > 0 &&
                   chunks == (total + ChunkBytes - 1) / ChunkBytes && fs.Length == HeaderBytes + (long)chunks * EntryBytes &&
                   CryptographicOperations.FixedTimeEquals(digest, header.Slice(HeaderCoreBytes, 32));
        }
        catch { return false; }
    }

    void TouchMap(string root, string fileId, bool force = false)
    {
        string map = MapPath(root, fileId);
        long now = DateTime.UtcNow.Ticks;
        string key = root + "\0" + Stem(fileId);
        long prior = _lastTouch.GetOrAdd(key, 0);
        if (!force && now - prior < TimeSpan.FromHours(1).Ticks) return;
        if (!_lastTouch.TryUpdate(key, now, prior) && prior != 0) return;
        try { File.SetLastAccessTimeUtc(map, new DateTime(now, DateTimeKind.Utc)); } catch { }
    }

    bool IsOwnedRoot(string root) => File.Exists(Path.Combine(root, MarkerFileName)) ||
                                     (_defaultDirectory.Length > 0 &&
                                      string.Equals(Path.GetFullPath(root), Path.GetFullPath(_defaultDirectory), StringComparison.OrdinalIgnoreCase));

    void DeleteOwnedContents(string root)
    {
        if (!IsOwnedRoot(root) || !Directory.Exists(root)) return;
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            if (file.EndsWith(".enc", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".map", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) TryDelete(file);
        RemoveEmptyDirectories(root);
    }

    static void RemoveEmptyDirectories(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (string dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(static d => d.Length))
            try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
    }

    static long Measure(string root)
    {
        long total = 0;
        try { if (Directory.Exists(root)) foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) total += SafeLength(file); }
        catch { }
        return total;
    }

    static long SafeLength(string path) { try { return File.Exists(path) ? new FileInfo(path).Length : 0; } catch { return 0; } }
    static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
