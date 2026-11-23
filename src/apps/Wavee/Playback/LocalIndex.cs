// ── Playback/LocalIndex.cs ──────────────────────────────────────────────────────────────────────────────────────────
// the local music index: every listed folder (with its fingerprint) and every audio file in the music folders, a stable
// LocalId per file, the match maps, and the file on disk (localindex.bin + localindex.log)
//
// Role: CORE (any thread; one Lock)
// Spec: C:\WAVEE\fgpu\handoff\request-plans\240-local-playlist-files-implementation.md §3.3, §3.4 (the walk), D13;
//       the shared types: Playback/Local.Types.cs (240-p1-contract.md)
//
// SHAPE. Entries and folders are immutable records, replaced whole. The scanner commits PER FOLDER (`Apply(LocalScanBatch)`):
// the folder's records and its fingerprint land together, so the UI fills folder by folder and an interrupted scan resumes
// where it stopped (a folder whose batch never landed still has its old fingerprint, so the next scan lists it again).
// Every commit bumps `Version`; readers (the UI thread, the api threads, the audio pump) take the lock for one probe.
//
// IDENTITY (D13). `LocalId` is assigned at first sight from a persisted high-water mark and never reused: a move or a
// rename keeps it (the scanner pairs before and after reading), a deleted file keeps it while it is `Missing`.
//
// STATES. Present; Missing (not found in a folder that was listed successfully under an online root: kept, still matches,
// comes back without a tag read when it reappears unchanged, purged after 60 days by `PurgeMissing`); CloudOnly (a
// placeholder indexed from its listing alone; never opened). Video entries (D19) are indexed but never counted or matched.
//
// THE FILES (beside audiofiles.tsv, under Cache). `localindex.bin`: magic, format version, generation, the id high-water
// mark, the roots, ONE string table (artist and album names repeat), the folders, the entries; written to a temp file and
// moved over the old one. `localindex.log`: an append-only list of records (a folder batch, a root change, a purge), each
// length-prefixed and checksummed, so a one-file change writes a few hundred bytes instead of the whole index. The log
// carries the bin's generation: a log left behind by a compaction that crashed between its two steps is ignored. Compacted
// into the bin when it passes 2 MB and when the scanner goes idle. A file this build cannot read is ignored (the next scan
// rebuilds the index); the index is a cache, outside the entity store (which is reaped on every schema change).

using System.Diagnostics.CodeAnalysis;
using System.IO.Hashing;
using System.Text;

namespace Wavee;

/// <summary>A music folder as the index knows it: its id, its path, and the change-journal cursor of its volume.</summary>
public sealed record LocalRoot(int RootId, string Path, uint VolumeSerial, ulong JournalId, long NextUsn, long LastWalkTicks);

/// <summary>One folder's commit (§3.6 step 7): its records and its fingerprint together.</summary>
public sealed class LocalScanBatch(int rootId, string folderPath)
{
    public int RootId { get; } = rootId;
    /// <summary>The folder, relative to its root ("" = the root itself).</summary>
    public string FolderPath { get; } = folderPath;
    /// <summary>The folder record to store; null keeps the stored one (or none).</summary>
    public LocalFolder? Folder { get; set; }
    /// <summary>The folder is gone: its record is dropped (its files arrive as Missing upserts or removes).</summary>
    public bool RemoveFolder { get; set; }
    /// <summary>New and changed entries, moved ones (same LocalId, new path) and state changes.</summary>
    public List<LocalEntry> Upserts { get; } = new();
    /// <summary>LocalIds dropped for good (a pairing that kept another id, a purge).</summary>
    public List<long> Removes { get; } = new();
    public bool IsEmpty => Folder is null && !RemoveFolder && Upserts.Count == 0 && Removes.Count == 0;
}

/// <summary>What <see cref="LocalIndex.Match"/> found: the entry (null: no match), the tier that answered (1 exact, 2 file
/// name, 3 loose; 0 none) and how many candidates shared the key before the length filter.</summary>
public readonly record struct LocalMatchResult(LocalEntry? Entry, byte Tier, int Candidates);

/// <summary>The local music index (see the file header). Thread-safe.</summary>
public sealed class LocalIndex
{
    public const string FileName = "localindex.bin", LogFileName = "localindex.log";
    /// <summary>Missing entries older than this are purged after a complete walk (§3.3).</summary>
    public static readonly TimeSpan MissingGrace = TimeSpan.FromDays(60);
    const long CompactLogBytes = 2L * 1024 * 1024;
    const uint BinMagic = 0x494C_5657 /*"WVLI"*/, LogMagic = 0x4C4C_5657 /*"WVLL"*/;
    const int FormatVersion = 1;
    const byte RecBatch = 1, RecRoot = 2, RecRootRemoved = 3, RecPurge = 4;

    /// <summary>The bin's path under <c>Platform.LocalFolder</c>, beside <c>audiofiles.tsv</c>. PURE.</summary>
    public static string PathUnder(string localFolder) => Path.Combine(localFolder, "Wavee", "Cache", FileName);

    readonly record struct PathKey(int Root, string Path);
    sealed class PathKeyComparer : IEqualityComparer<PathKey>
    {
        public static readonly PathKeyComparer Instance = new();
        public bool Equals(PathKey a, PathKey b) => a.Root == b.Root && string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode(PathKey k) => HashCode.Combine(k.Root, StringComparer.OrdinalIgnoreCase.GetHashCode(k.Path));
    }
    readonly record struct IdKey(uint Volume, UInt128 Id);

    /// <summary>A 64-bit hash -> LocalIds multimap: one slot per key, a list only for a key that really holds several ids. A
    /// lookup verifies its candidates, so a hash collision can never answer a wrong entry. It keeps the match maps at a few
    /// bytes per entry instead of a key string and a list per key.</summary>
    sealed class IdMultiMap
    {
        readonly Dictionary<ulong, long> _one = new();
        readonly Dictionary<ulong, List<long>> _many = new();

        public void Add(ulong key, long id)
        {
            if (_many.TryGetValue(key, out List<long>? ids)) { if (!ids.Contains(id)) ids.Add(id); return; }
            if (!_one.TryGetValue(key, out long one)) { _one[key] = id; return; }
            if (one == id) return;
            _one.Remove(key);
            _many[key] = [one, id];
        }

        public void Remove(ulong key, long id)
        {
            if (_one.TryGetValue(key, out long one)) { if (one == id) _one.Remove(key); return; }
            if (!_many.TryGetValue(key, out List<long>? ids) || !ids.Remove(id)) return;
            if (ids.Count == 1) { _one[key] = ids[0]; _many.Remove(key); }
        }

        /// <summary>The ids under a key into <paramref name="into"/> (cleared first).</summary>
        public void Get(ulong key, List<long> into)
        {
            into.Clear();
            if (_one.TryGetValue(key, out long one)) into.Add(one);
            else if (_many.TryGetValue(key, out List<long>? ids)) into.AddRange(ids);
        }

        public void Clear() { _one.Clear(); _many.Clear(); }
    }

    static ulong HashOf(string key) => XxHash3.HashToUInt64(System.Runtime.InteropServices.MemoryMarshal.AsBytes(key.AsSpan()));

    static ulong HashOf(long size, long write)
    {
        Span<long> both = [size, write];
        return XxHash3.HashToUInt64(System.Runtime.InteropServices.MemoryMarshal.AsBytes(both));
    }

    readonly Lock _gate = new();
    readonly Lock _fileGate = new();   // held across apply + log append, so the log's order is the memory's order
    readonly Dictionary<long, LocalEntry> _byId = new();
    readonly Dictionary<PathKey, long> _byPath = new(PathKeyComparer.Instance);
    readonly Dictionary<IdKey, long> _byFileId = new();
    readonly IdMultiMap _bySizeWrite = new();
    readonly Dictionary<PathKey, LocalFolder> _folders = new(PathKeyComparer.Instance);
    readonly Dictionary<PathKey, HashSet<long>> _members = new(PathKeyComparer.Instance);
    readonly IdMultiMap _exact = new(), _loose = new(), _names = new();
    readonly Dictionary<int, LocalRoot> _roots = new();
    long _nextId = 1;
    int _version, _audio, _unplayable, _missing, _cloud, _video;

    string? _binPath, _logPath;
    FileStream? _log;
    ulong _generation;

    // ── reading ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Bumped by every change a reader could see (a commit, a load, a root change, a purge).</summary>
    public int Version => Volatile.Read(ref _version);
    /// <summary>Audio entries held, every state (video entries are not counted, D19).</summary>
    public int Count { get { lock (_gate) return _audio; } }
    /// <summary>Audio entries this build cannot play (no decoder, D7; or unreadable).</summary>
    public int UnplayableCount { get { lock (_gate) return _unplayable; } }
    /// <summary>Audio entries in the Missing state.</summary>
    public int MissingCount { get { lock (_gate) return _missing; } }
    /// <summary>Audio entries that are cloud-only placeholders.</summary>
    public int CloudOnlyCount { get { lock (_gate) return _cloud; } }
    /// <summary>Video entries (indexed for later, D19).</summary>
    public int VideoCount { get { lock (_gate) return _video; } }
    /// <summary>Bytes in the change log (compacted at 2 MB).</summary>
    public long LogBytes { get { lock (_fileGate) return _log?.Length ?? 0; } }

    public bool TryGet(long localId, [NotNullWhen(true)] out LocalEntry? entry)
    {
        lock (_gate) return _byId.TryGetValue(localId, out entry);
    }

    /// <summary>The entry at <paramref name="relativePath"/> under root <paramref name="rootId"/> (case-insensitive).</summary>
    public bool TryGetByPath(int rootId, string relativePath, [NotNullWhen(true)] out LocalEntry? entry)
    {
        lock (_gate)
        {
            if (_byPath.TryGetValue(new PathKey(rootId, relativePath), out long id)) return _byId.TryGetValue(id, out entry);
        }
        entry = null;
        return false;
    }

    /// <summary>The entry at a full path, resolved through the root whose path contains it.</summary>
    public bool TryGetByPath(string fullPath, [NotNullWhen(true)] out LocalEntry? entry)
    {
        entry = null;
        return TryRelative(fullPath, out int rootId, out string relative) && TryGetByPath(rootId, relative, out entry);
    }

    /// <summary>Split a full path into the root that holds it and the path under that root (the longest root wins).</summary>
    public bool TryRelative(string fullPath, out int rootId, out string relative)
    {
        rootId = 0;
        relative = "";
        int best = -1;
        lock (_gate)
        {
            foreach (LocalRoot root in _roots.Values)
            {
                string r = root.Path.TrimEnd('\\');
                if (r.Length <= best) continue;
                if (fullPath.Length == r.Length && fullPath.Equals(r, StringComparison.OrdinalIgnoreCase)) { rootId = root.RootId; relative = ""; best = r.Length; }
                else if (fullPath.Length > r.Length && fullPath[r.Length] == '\\' && fullPath.StartsWith(r, StringComparison.OrdinalIgnoreCase))
                {
                    rootId = root.RootId;
                    relative = fullPath[(r.Length + 1)..];
                    best = r.Length;
                }
            }
        }
        return best >= 0;
    }

    /// <summary>The full path of an entry (its root's path + its relative path), or null when its root is gone.</summary>
    public string? FullPathOf(LocalEntry entry)
    {
        lock (_gate) return _roots.TryGetValue(entry.RootId, out LocalRoot? root) ? Join(root.Path, entry.RelativePath) : null;
    }

    public static string Join(string root, string relative) => relative.Length == 0 ? root : root.TrimEnd('\\') + "\\" + relative;

    /// <summary>Every entry (video included), ordered by root then path. A copy.</summary>
    public LocalEntry[] Snapshot()
    {
        LocalEntry[] all;
        lock (_gate) { all = new LocalEntry[_byId.Count]; _byId.Values.CopyTo(all, 0); }
        Array.Sort(all, static (a, b) => a.RootId != b.RootId ? a.RootId.CompareTo(b.RootId) : string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase));
        return all;
    }

    /// <summary>Every listed folder. A copy.</summary>
    public LocalFolder[] Folders()
    {
        lock (_gate) { var all = new LocalFolder[_folders.Count]; _folders.Values.CopyTo(all, 0); return all; }
    }

    /// <summary>The listed folders under one root. A copy.</summary>
    public List<LocalFolder> FoldersOf(int rootId)
    {
        var list = new List<LocalFolder>();
        lock (_gate)
            foreach (LocalFolder f in _folders.Values)
                if (f.RootId == rootId) list.Add(f);
        return list;
    }

    public bool TryGetFolder(int rootId, string relativePath, [NotNullWhen(true)] out LocalFolder? folder)
    {
        lock (_gate) return _folders.TryGetValue(new PathKey(rootId, relativePath), out folder);
    }

    /// <summary>The entries whose file sits directly in a folder. A copy.</summary>
    public List<LocalEntry> EntriesIn(int rootId, string folderPath)
    {
        var list = new List<LocalEntry>();
        lock (_gate)
        {
            if (!_members.TryGetValue(new PathKey(rootId, folderPath), out HashSet<long>? ids)) return list;
            foreach (long id in ids)
                if (_byId.TryGetValue(id, out LocalEntry? e)) list.Add(e);
        }
        return list;
    }

    /// <summary>The entry that holds a file id on a volume (NTFS/ReFS move identity), if any.</summary>
    public bool TryGetByFileId(uint volumeSerial, UInt128 fileId, [NotNullWhen(true)] out LocalEntry? entry)
    {
        entry = null;
        if (fileId == 0) return false;
        lock (_gate) return _byFileId.TryGetValue(new IdKey(volumeSerial, fileId), out long id) && _byId.TryGetValue(id, out entry);
    }

    /// <summary>Entries with this exact size and write time (the move identity where ids are not stable). A copy.</summary>
    public List<LocalEntry> BySizeAndWrite(long size, long writeTicks)
    {
        var list = new List<LocalEntry>();
        lock (_gate)
        {
            var ids = new List<long>(2);
            _bySizeWrite.Get(HashOf(size, writeTicks), ids);
            foreach (long id in ids)
                if (_byId.TryGetValue(id, out LocalEntry? e) && e.Size == size && e.WriteTicks == writeTicks) list.Add(e);
        }
        return list;
    }

    public LocalRoot[] Roots()
    {
        lock (_gate) { var all = new LocalRoot[_roots.Count]; _roots.Values.CopyTo(all, 0); return all; }
    }

    public bool TryGetRoot(int rootId, [NotNullWhen(true)] out LocalRoot? root)
    {
        lock (_gate) return _roots.TryGetValue(rootId, out root);
    }

    /// <summary>A fresh LocalId: never handed out before, never reused (D13).</summary>
    public long NewId() { lock (_gate) return _nextId++; }

    // ── matching (§3.4) ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The §3.4 walk for a <c>spotify:local:</c> row's decoded parts: tier 1 the exact map (first, last and joined
    /// artists), 1 s tolerance (5 s for an estimated length); tier 2, only when the row's artist and album are empty, the file
    /// name map, 1 s; tier 3 the loose map, 2 s. A candidate outside the tolerance is never picked. Missing and cloud-only
    /// entries match too (the row shows them as missing or downloadable); video entries never do.</summary>
    public LocalMatchResult Match(string title, string artist, string album, int uriSeconds)
    {
        string exactTitle = LocalMatch.Exact(title), exactArtist = LocalMatch.Exact(artist);
        string exactKey = LocalMatch.Key(exactTitle, exactArtist);
        string looseKey = LocalMatch.Key(LocalMatch.Loose(title), LocalMatch.Loose(artist));
        bool untaggedShape = artist.Length == 0 && album.Length == 0;
        var candidates = new List<LocalEntry>(4);
        lock (_gate)
        {
            if (TryTier(_exact, exactKey, 1, uriSeconds, album, candidates, out LocalMatchResult r)) return r;
            if (untaggedShape && TryTier(_names, exactTitle, 2, uriSeconds, album, candidates, out r)) return r;
            if (TryTier(_loose, looseKey, 3, uriSeconds, album, candidates, out r)) return r;
        }
        return new LocalMatchResult(null, 0, 0);
    }

    bool TryTier(IdMultiMap map, string key, byte tier, int uriSeconds, string album, List<LocalEntry> candidates, out LocalMatchResult result)
    {
        result = default;
        if (key.Length == 0) return false;
        var ids = new List<long>(2);
        map.Get(HashOf(key), ids);
        if (ids.Count == 0) return false;
        candidates.Clear();
        foreach (long id in ids)
        {
            if (!_byId.TryGetValue(id, out LocalEntry? e) || !HasKey(e, tier, key)) continue;   // a hash collision never answers
            int tolerance = tier switch { 1 => e.Tags.DurationEstimated ? 5 : 1, 2 => 1, _ => 2 };
            if (LocalMatch.DurationAgrees(uriSeconds, e.Tags.DurationMs, tolerance)) candidates.Add(e);
        }
        if (candidates.Count == 0) return false;
        int pick = LocalMatch.Pick(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(candidates), album, uriSeconds);
        if (pick < 0) return false;
        result = new LocalMatchResult(candidates[pick], tier, ids.Count);
        return true;
    }

    // ── writing ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Commit one folder's batch (§3.6 step 7) and append it to the log. Any thread.</summary>
    public void Apply(LocalScanBatch batch)
    {
        if (batch.IsEmpty) return;
        lock (_fileGate)
        {
            lock (_gate) ApplyLocked(batch);
            Interlocked.Increment(ref _version);
            AppendLocked(RecBatch, w => WriteBatch(w, batch));
        }
        CompactIfLarge();
    }

    /// <summary>Add or update a root (a music folder, its volume and its journal cursor).</summary>
    public void SetRoot(LocalRoot root)
    {
        lock (_fileGate)
        {
            lock (_gate) _roots[root.RootId] = root;
            Interlocked.Increment(ref _version);
            AppendLocked(RecRoot, w => WriteRoot(w, root));
        }
    }

    /// <summary>Forget a root and everything under it (the user removed the music folder: its files leave the index, they are
    /// not "missing").</summary>
    public void RemoveRoot(int rootId)
    {
        lock (_fileGate)
        {
            lock (_gate) RemoveRootLocked(rootId);
            Interlocked.Increment(ref _version);
            AppendLocked(RecRootRemoved, w => w.Write(rootId));
        }
    }

    /// <summary>Drop Missing entries of <paramref name="rootId"/> last seen before <paramref name="beforeTicks"/>. Call only
    /// after a COMPLETE walk of an online root (never after a partial, cancelled or failed one). Returns how many.</summary>
    public int PurgeMissing(int rootId, long beforeTicks)
    {
        var ids = new List<long>();
        lock (_gate)
            foreach (LocalEntry e in _byId.Values)
                if (e.RootId == rootId && e.State == LocalState.Missing && e.LastSeenTicks < beforeTicks) ids.Add(e.LocalId);
        if (ids.Count == 0) return 0;
        lock (_fileGate)
        {
            lock (_gate) foreach (long id in ids) RemoveLocked(id);
            Interlocked.Increment(ref _version);
            AppendLocked(RecPurge, w => { w.Write(ids.Count); foreach (long id in ids) w.Write(id); });
        }
        return ids.Count;
    }

    /// <summary>Empty the index in memory (the inspector's fixtures; a load that found nothing usable).</summary>
    public void Clear()
    {
        lock (_gate) ClearLocked();
        Interlocked.Increment(ref _version);
    }

    void ClearLocked()
    {
        _byId.Clear(); _byPath.Clear(); _byFileId.Clear(); _bySizeWrite.Clear(); _folders.Clear(); _members.Clear();
        _exact.Clear(); _loose.Clear(); _names.Clear(); _roots.Clear();
        _nextId = 1; _audio = _unplayable = _missing = _cloud = _video = 0;
    }

    void ApplyLocked(LocalScanBatch batch)
    {
        foreach (long id in batch.Removes) RemoveLocked(id);
        foreach (LocalEntry e in batch.Upserts)
        {
            RemoveLocked(e.LocalId);
            InsertLocked(e);
        }
        var key = new PathKey(batch.RootId, batch.FolderPath);
        if (batch.RemoveFolder) _folders.Remove(key);
        else if (batch.Folder is { } folder) _folders[key] = folder;
    }

    void RemoveRootLocked(int rootId)
    {
        _roots.Remove(rootId);
        var ids = new List<long>();
        foreach (LocalEntry e in _byId.Values) if (e.RootId == rootId) ids.Add(e.LocalId);
        foreach (long id in ids) RemoveLocked(id);
        var folders = new List<PathKey>();
        foreach (PathKey k in _folders.Keys) if (k.Root == rootId) folders.Add(k);
        foreach (PathKey k in folders) _folders.Remove(k);
    }

    void InsertLocked(LocalEntry e)
    {
        _byId[e.LocalId] = e;
        if (e.LocalId >= _nextId) _nextId = e.LocalId + 1;
        _byPath[new PathKey(e.RootId, e.RelativePath)] = e.LocalId;
        if (e.FileId != 0) _byFileId[new IdKey(e.VolumeSerial, e.FileId)] = e.LocalId;
        _bySizeWrite.Add(HashOf(e.Size, e.WriteTicks), e.LocalId);
        var folder = new PathKey(e.RootId, FolderOf(e.RelativePath));
        if (!_members.TryGetValue(folder, out HashSet<long>? members)) _members[folder] = members = new HashSet<long>();
        members.Add(e.LocalId);
        if (e.Tags.Kind == LocalKind.Video) { _video++; return; }
        _audio++;
        if (!e.Playable) _unplayable++;
        if (e.State == LocalState.Missing) _missing++;
        else if (e.State == LocalState.CloudOnly) _cloud++;
        foreach (string k in MatchKeys(e, loose: false)) _exact.Add(HashOf(k), e.LocalId);
        foreach (string k in MatchKeys(e, loose: true)) _loose.Add(HashOf(k), e.LocalId);
        _names.Add(HashOf(NameKey(e)), e.LocalId);
    }

    void RemoveLocked(long id)
    {
        if (!_byId.Remove(id, out LocalEntry? e)) return;
        var pathKey = new PathKey(e.RootId, e.RelativePath);
        if (_byPath.TryGetValue(pathKey, out long at) && at == id) _byPath.Remove(pathKey);
        var idKey = new IdKey(e.VolumeSerial, e.FileId);
        if (e.FileId != 0 && _byFileId.TryGetValue(idKey, out long holder) && holder == id) _byFileId.Remove(idKey);
        _bySizeWrite.Remove(HashOf(e.Size, e.WriteTicks), id);
        var folder = new PathKey(e.RootId, FolderOf(e.RelativePath));
        if (_members.TryGetValue(folder, out HashSet<long>? members)) { members.Remove(id); if (members.Count == 0) _members.Remove(folder); }
        if (e.Tags.Kind == LocalKind.Video) { _video--; return; }
        _audio--;
        if (!e.Playable) _unplayable--;
        if (e.State == LocalState.Missing) _missing--;
        else if (e.State == LocalState.CloudOnly) _cloud--;
        foreach (string k in MatchKeys(e, loose: false)) _exact.Remove(HashOf(k), id);
        foreach (string k in MatchKeys(e, loose: true)) _loose.Remove(HashOf(k), id);
        _names.Remove(HashOf(NameKey(e)), id);
    }

    /// <summary>The keys an entry is filed under (§3.4/§3.5): its match title with the first artist, the last artist and the
    /// ", "-joined artists (duplicates folded); an entry with no artist is filed under the empty artist.</summary>
    static IEnumerable<string> MatchKeys(LocalEntry e, bool loose)
    {
        Func<string, string> norm = loose ? LocalMatch.Loose : LocalMatch.Exact;
        string title = norm(e.MatchTitle);
        string[] artists = e.Tags.Artists;
        if (artists.Length == 0) { yield return LocalMatch.Key(title, ""); yield break; }
        string first = LocalMatch.Key(title, norm(artists[0]));
        yield return first;
        if (artists.Length == 1) yield break;
        string last = LocalMatch.Key(title, norm(artists[^1]));
        if (last != first) yield return last;
        string joined = LocalMatch.Key(title, norm(string.Join(", ", artists)));
        if (joined != first && joined != last) yield return joined;
    }

    /// <summary>The file-name key (tier 2): <c>Exact</c> of the name without its extension.</summary>
    static string NameKey(LocalEntry e) => LocalMatch.Exact(Path.GetFileNameWithoutExtension(e.RelativePath));

    /// <summary>Is <paramref name="key"/> really one of the entry's keys for this tier (the check behind a hashed lookup)?</summary>
    static bool HasKey(LocalEntry e, byte tier, string key)
    {
        if (tier == 2) return NameKey(e) == key;
        foreach (string k in MatchKeys(e, loose: tier == 3)) if (k == key) return true;
        return false;
    }

    /// <summary>The folder part of a relative file path ("" for a file directly in the root).</summary>
    public static string FolderOf(string relativePath)
    {
        int slash = relativePath.LastIndexOf('\\');
        return slash < 0 ? "" : relativePath[..slash];
    }

    // ── the files ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Read the bin and replay the log into memory, then keep the log open for appends. Background thread; never
    /// throws. A file this build cannot read is ignored (and replaced by the next compaction). Returns the entries loaded.</summary>
    public int Load(string binPath)
    {
        string logPath = Path.ChangeExtension(binPath, ".log");
        lock (_fileGate)
        {
            _binPath = binPath;
            _logPath = logPath;
            ulong generation = 0;
            lock (_gate)
            {
                ClearLocked();
                try
                {
                    if (File.Exists(binPath))
                    {
                        using var stream = new FileStream(binPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                        using var r = new BinaryReader(stream, Encoding.UTF8);
                        if (!ReadBinLocked(r, out generation)) { ClearLocked(); generation = 0; }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException or InvalidDataException or ArgumentException or FormatException)
                {
                    Log.Warn("local", "local.index.load bin unreadable; rebuilding (" + ex.GetType().Name + ")");
                    ClearLocked();
                    generation = 0;
                }
            }
            _generation = generation;
            OpenLogLocked(replay: true);
        }
        Interlocked.Increment(ref _version);
        return Count;
    }

    /// <summary>Write the whole index to the bin (temp file + move) and start an empty log. Any background thread.</summary>
    public bool Compact()
    {
        lock (_fileGate)
        {
            if (_binPath is not { } binPath) return false;
            ulong generation = _generation + 1;
            string tmp = binPath + "." + Environment.ProcessId + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);
                using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                using (var w = new BinaryWriter(stream, new UTF8Encoding(false)))
                {
                    lock (_gate) WriteBinLocked(w, generation);
                }
                File.Move(tmp, binPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(tmp); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
                Log.Warn("local", "local.index.compact failed (the log keeps every change)", ex);
                return false;
            }
            _generation = generation;
            _log?.Dispose();
            _log = null;
            OpenLogLocked(replay: false);
            return true;
        }
    }

    void CompactIfLarge()
    {
        if (LogBytes > CompactLogBytes) Compact();
    }

    /// <summary>Close the log (the process is ending, or a fixture takes over).</summary>
    public void Detach()
    {
        lock (_fileGate) { _log?.Dispose(); _log = null; _binPath = _logPath = null; }
    }

    void OpenLogLocked(bool replay)
    {
        if (_logPath is not { } logPath) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            var log = new FileStream(logPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1 << 12);
            long good = replay ? ReplayLog(log) : -1;
            if (good < 0)
            {
                // No usable log (none, another generation, or a fresh start after a compaction): a header for this generation.
                log.SetLength(0);
                using (var w = new BinaryWriter(log, Encoding.UTF8, leaveOpen: true)) { w.Write(LogMagic); w.Write(FormatVersion); w.Write(_generation); }
                log.Flush(flushToDisk: true);
            }
            else if (good < log.Length) log.SetLength(good);   // a torn tail: cut it
            log.Seek(0, SeekOrigin.End);
            _log = log;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("local", "local.index.log unavailable (changes stay in memory until the next compaction)", ex);
            _log = null;
        }
    }

    /// <summary>Replay every whole record of a log of this generation; returns the offset after the last good record, or -1
    /// when the log is not one this index can replay (another generation, another format, empty).</summary>
    long ReplayLog(FileStream log)
    {
        if (log.Length < 16) return -1;
        log.Seek(0, SeekOrigin.Begin);
        using var r = new BinaryReader(log, Encoding.UTF8, leaveOpen: true);
        if (r.ReadUInt32() != LogMagic || r.ReadInt32() != FormatVersion || r.ReadUInt64() != _generation) return -1;
        long good = log.Position;
        int records = 0;
        lock (_gate)
        {
            while (log.Length - log.Position >= 9)
            {
                int length = r.ReadInt32();
                if (length <= 0 || length > log.Length - log.Position - 4) break;
                byte[] body = r.ReadBytes(length);
                uint sum = r.ReadUInt32();
                if (body.Length != length || (uint)XxHash3.HashToUInt64(body) != sum) break;
                try
                {
                    using var br = new BinaryReader(new MemoryStream(body), Encoding.UTF8);
                    ReplayRecordLocked(br);
                }
                catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException or ArgumentException or FormatException)
                {
                    break;
                }
                good = log.Position;
                records++;
            }
        }
        if (records > 0) Log.Info("local", "local.index.log replayed records=" + records);
        return good;
    }

    void ReplayRecordLocked(BinaryReader r)
    {
        byte type = r.ReadByte();
        switch (type)
        {
            case RecBatch: ApplyLocked(ReadBatch(r)); break;
            case RecRoot: { LocalRoot root = ReadRoot(r); _roots[root.RootId] = root; break; }
            case RecRootRemoved: RemoveRootLocked(r.ReadInt32()); break;
            case RecPurge: { int n = r.ReadInt32(); for (int i = 0; i < n; i++) RemoveLocked(r.ReadInt64()); break; }
            default: throw new InvalidDataException("unknown log record " + type);
        }
    }

    void AppendLocked(byte type, Action<BinaryWriter> write)
    {
        if (_log is null) return;
        var body = new MemoryStream(256);
        using (var w = new BinaryWriter(body, new UTF8Encoding(false), leaveOpen: true)) { w.Write(type); write(w); }
        ReadOnlySpan<byte> bytes = body.GetBuffer().AsSpan(0, (int)body.Length);
        try
        {
            Span<byte> head = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(head, bytes.Length);
            _log.Write(head);
            _log.Write(bytes);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(head, (uint)XxHash3.HashToUInt64(bytes));
            _log.Write(head);
            _log.Flush();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("local", "local.index.log append failed; compacting instead", ex);
            _log.Dispose();
            _log = null;
        }
    }

    // ── the bin ──────────────────────────────────────────────────────────────────────────────────────────────────────

    void WriteBinLocked(BinaryWriter w, ulong generation)
    {
        // One string table for every string the entries hold: artist and album names repeat across a library.
        var table = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        void Intern(string s) { if (!table.ContainsKey(s)) { table[s] = order.Count; order.Add(s); } }
        foreach (LocalEntry e in _byId.Values)
        {
            Intern(e.RelativePath); Intern(e.Tags.Title); Intern(e.Tags.Album); Intern(e.Tags.AlbumArtist); Intern(e.MatchTitle); Intern(e.Why);
            foreach (string a in e.Tags.Artists) Intern(a);
        }
        foreach (LocalFolder f in _folders.Values) { Intern(f.RelativePath); foreach (string i in f.Images) Intern(i); }

        w.Write(BinMagic); w.Write(FormatVersion); w.Write(generation); w.Write(_nextId);
        w.Write(_roots.Count);
        foreach (LocalRoot root in _roots.Values) WriteRoot(w, root);
        w.Write(order.Count);
        foreach (string s in order) w.Write(s);
        Func<string, int> index = s => table[s];
        w.Write(_folders.Count);
        foreach (LocalFolder f in _folders.Values) WriteFolder(w, f, index);
        w.Write(_byId.Count);
        foreach (LocalEntry e in _byId.Values) WriteEntry(w, e, index);
        w.Write(BinMagic);   // a trailer: a truncated bin is refused whole
    }

    bool ReadBinLocked(BinaryReader r, out ulong generation)
    {
        generation = 0;
        if (r.ReadUInt32() != BinMagic || r.ReadInt32() != FormatVersion) return false;
        generation = r.ReadUInt64();
        long nextId = r.ReadInt64();
        int roots = r.ReadInt32();
        for (int i = 0; i < roots; i++) { LocalRoot root = ReadRoot(r); _roots[root.RootId] = root; }
        int strings = r.ReadInt32();
        var table = new string[strings];
        for (int i = 0; i < strings; i++) table[i] = r.ReadString();
        Func<string> next = () => table[r.Read7BitEncodedInt()];
        int folders = r.ReadInt32();
        for (int i = 0; i < folders; i++) { LocalFolder f = ReadFolder(r, next); _folders[new PathKey(f.RootId, f.RelativePath)] = f; }
        int entries = r.ReadInt32();
        for (int i = 0; i < entries; i++) InsertLocked(ReadEntry(r, next));
        if (r.ReadUInt32() != BinMagic) return false;
        _nextId = Math.Max(_nextId, nextId);
        return true;
    }

    // ── records ──────────────────────────────────────────────────────────────────────────────────────────────────────

    static void WriteRoot(BinaryWriter w, LocalRoot root)
    {
        w.Write(root.RootId); w.Write(root.Path); w.Write(root.VolumeSerial); w.Write(root.JournalId); w.Write(root.NextUsn); w.Write(root.LastWalkTicks);
    }

    static LocalRoot ReadRoot(BinaryReader r)
        => new(r.ReadInt32(), r.ReadString(), r.ReadUInt32(), r.ReadUInt64(), r.ReadInt64(), r.ReadInt64());

    void WriteBatch(BinaryWriter w, LocalScanBatch batch)
    {
        Func<string, int>? inline = null;
        w.Write(batch.RootId); w.Write(batch.FolderPath); w.Write(batch.RemoveFolder); w.Write(_nextId);
        w.Write(batch.Folder is not null);
        if (batch.Folder is { } folder) WriteFolder(w, folder, inline);
        w.Write(batch.Upserts.Count);
        foreach (LocalEntry e in batch.Upserts) WriteEntry(w, e, inline);
        w.Write(batch.Removes.Count);
        foreach (long id in batch.Removes) w.Write(id);
    }

    LocalScanBatch ReadBatch(BinaryReader r)
    {
        var batch = new LocalScanBatch(r.ReadInt32(), r.ReadString()) { RemoveFolder = r.ReadBoolean() };
        _nextId = Math.Max(_nextId, r.ReadInt64());
        Func<string> inline = r.ReadString;
        if (r.ReadBoolean()) batch.Folder = ReadFolder(r, inline);
        int upserts = r.ReadInt32();
        for (int i = 0; i < upserts; i++) batch.Upserts.Add(ReadEntry(r, inline));
        int removes = r.ReadInt32();
        for (int i = 0; i < removes; i++) batch.Removes.Add(r.ReadInt64());
        return batch;
    }

    /// <summary>A string through the table (<paramref name="index"/> set: the bin) or inline (null: the log).</summary>
    static void Str(BinaryWriter w, string s, Func<string, int>? index)
    {
        if (index is null) w.Write(s);
        else w.Write7BitEncodedInt(index(s));
    }

    static void WriteFolder(BinaryWriter w, LocalFolder f, Func<string, int>? index)
    {
        w.Write(f.RootId); Str(w, f.RelativePath, index); w.Write((ulong)f.FileId); w.Write((ulong)(f.FileId >> 64)); w.Write(f.Fingerprint);
        w.Write(f.Images.Length);
        foreach (string i in f.Images) Str(w, i, index);
        w.Write(f.Complete);
    }

    static LocalFolder ReadFolder(BinaryReader r, Func<string> str)
    {
        int root = r.ReadInt32();
        string path = str();
        ulong lo = r.ReadUInt64(), hi = r.ReadUInt64();
        ulong fingerprint = r.ReadUInt64();
        var images = new string[r.ReadInt32()];
        for (int i = 0; i < images.Length; i++) images[i] = str();
        return new LocalFolder(root, path, new UInt128(hi, lo), fingerprint, images, r.ReadBoolean());
    }

    static void WriteEntry(BinaryWriter w, LocalEntry e, Func<string, int>? index)
    {
        w.Write(e.LocalId); w.Write(e.RootId); Str(w, e.RelativePath, index);
        w.Write(e.VolumeSerial); w.Write((ulong)e.FileId); w.Write((ulong)(e.FileId >> 64));
        w.Write(e.Size); w.Write(e.WriteTicks); w.Write(e.ChangeTicks); w.Write(e.Attributes); w.Write((byte)e.Format);
        LocalTagSet t = e.Tags;
        w.Write((byte)t.Kind); Str(w, t.Title, index);
        w.Write(t.Artists.Length);
        foreach (string a in t.Artists) Str(w, a, index);
        Str(w, t.Album, index); Str(w, t.AlbumArtist, index);
        w.Write(t.TrackNumber); w.Write(t.DiscNumber); w.Write(t.Year); w.Write(t.DurationMs); w.Write(t.DurationEstimated); w.Write(t.BitrateKbps);
        w.Write(t.Cover.Offset); w.Write(t.Cover.Length); w.Write((byte)t.Cover.Mime); w.Write((byte)t.Cover.Encoding); w.Write(t.Cover.PictureType);
        w.Write(t.VideoWidth); w.Write(t.VideoHeight); w.Write(t.HasAudio);
        Str(w, e.MatchTitle, index); w.Write((byte)e.State); w.Write(e.LastSeenTicks); w.Write(e.Playable); Str(w, e.Why, index);
    }

    static LocalEntry ReadEntry(BinaryReader r, Func<string> str)
    {
        long localId = r.ReadInt64();
        int rootId = r.ReadInt32();
        string path = str();
        uint volume = r.ReadUInt32();
        ulong lo = r.ReadUInt64(), hi = r.ReadUInt64();
        long size = r.ReadInt64(), write = r.ReadInt64(), change = r.ReadInt64();
        uint attributes = r.ReadUInt32();
        var format = (Spotify.Audio.Format)r.ReadByte();
        var kind = (LocalKind)r.ReadByte();
        string title = str();
        var artists = new string[r.ReadInt32()];
        for (int i = 0; i < artists.Length; i++) artists[i] = str();
        string album = str(), albumArtist = str();
        int track = r.ReadInt32(), disc = r.ReadInt32(), year = r.ReadInt32(), duration = r.ReadInt32();
        bool estimated = r.ReadBoolean();
        int bitrate = r.ReadInt32();
        var cover = new LocalCover(r.ReadInt64(), r.ReadInt32(), (LocalCoverMime)r.ReadByte(), (LocalCoverEncoding)r.ReadByte(), r.ReadByte());
        int vw = r.ReadInt32(), vh = r.ReadInt32();
        bool hasAudio = r.ReadBoolean();
        var tags = new LocalTagSet(kind, title, artists, album, albumArtist, track, disc, year, duration, estimated, bitrate, cover, vw, vh, hasAudio);
        string matchTitle = str();
        var state = (LocalState)r.ReadByte();
        long lastSeen = r.ReadInt64();
        bool playable = r.ReadBoolean();
        string why = str();
        return new LocalEntry(localId, rootId, path, volume, new UInt128(hi, lo), size, write, change, attributes, format, tags, matchTitle, state, lastSeen, playable, why);
    }
}
