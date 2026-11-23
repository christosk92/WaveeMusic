// ── Playback/LocalIndex.cs ──────────────────────────────────────────────────────────────────────────────────────────
// the local music index: every listed folder (with its fingerprint) and every audio file in the music folders, a stable
// LocalId per file, the match maps, and the file on disk (localindex.bin + localindex.log)
//
// Role: CORE (any thread; one Lock)
// Spec: C:\WAVEE\fgpu\handoff\request-plans\240-local-playlist-files-implementation.md §3.3, §3.4 (the walk), D13;
//       the shared types: Playback/Local.Types.cs (240-p1-contract.md, revision 2)
//
// SHAPE. The scanner commits PER FOLDER (`Apply(LocalScanBatch)`): the folder's records and its fingerprint land together, so
// the UI fills folder by folder and an interrupted scan resumes where it stopped (a folder whose batch never landed still has
// its old fingerprint, so the next scan lists it again). Every commit bumps `Version`; readers take the lock for one probe.
//
// MEMORY (about 13-15 MB at 50,000 files). The index does NOT keep a `LocalEntry` object per file: it keeps one compact
// `Row` per file in an array (narrow numbers, the file NAME only, its folder through a shared folder table) and builds the
// `LocalEntry` record when a reader asks (`TryGet`, `EntriesIn`, `Snapshot`, a match's candidates). Repeated strings (album,
// album artist, every artist value, the artist arrays themselves, the reasons) go through one intern table as they are
// applied or loaded. The match maps are open-addressing tables of (64-bit key hash, row slot) pairs; a lookup verifies its
// candidates, so a hash collision never answers a wrong entry. LocalId -> slot is a plain array (ids are dense: assigned in
// order, never reused). The move-pairing maps (file id, size + write time) exist only while a scan needs them
// (`ReleaseScanMaps`). Track, disc, year, bitrate and video sizes are stored clamped to 0-65535.
//
// IDENTITY (D13). `LocalId` is assigned at first sight from a persisted high-water mark and never reused: a move or a
// rename keeps it (the scanner pairs before and after reading), a deleted file keeps it while it is `Missing`.
//
// STATES. Present; Missing (not found in a folder that was listed successfully under an online root: kept, still matches,
// comes back without a tag read when it reappears unchanged, purged after 60 days by `PurgeMissing`); CloudOnly (a
// placeholder indexed from its listing alone; never opened). Video entries (D19) are indexed but never counted or matched.
//
// THE FILES (beside audiofiles.tsv, under Cache). `localindex.bin`: magic, format version, generation, the id high-water
// mark, the roots, ONE string table, the folders, the entries; written to a temp file and moved over the old one.
// `localindex.log`: an append-only list of records (a folder batch, a root change, a purge), each length-prefixed and
// checksummed, so a one-file change writes a few hundred bytes instead of the whole index. The log carries the bin's
// generation: a log left behind by a compaction that crashed between its two steps is ignored. Compacted into the bin when it
// passes 2 MB and when the scanner goes idle. A LOAD builds a whole new store off the lock and swaps it in; a COMPACTION copies
// the records under the lock and writes them off it. A file this build cannot read is ignored (the next scan rebuilds the
// index); the index is a cache, outside the entity store (which is reaped on every schema change).
//
// MATCH LOG. Every `Match` writes one always-on `local.match` line: the tier that answered (or none), the candidate count, and
// the matched FILE NAME only, never its folder (paths hold the account name).

using System.Diagnostics.CodeAnalysis;
using System.IO.Hashing;
using System.Runtime.InteropServices;
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
    const byte FlagPlayable = 1, FlagEstimated = 2, FlagHasAudio = 4;

    /// <summary>The bin's path under <c>Platform.LocalFolder</c>, beside <c>audiofiles.tsv</c>. PURE.</summary>
    public static string PathUnder(string localFolder) => Path.Combine(localFolder, "Wavee", "Cache", FileName);

    // ── storage ──────────────────────────────────────────────────────────────────────────────────────────────────────

    readonly record struct PathKey(int Root, string Path);
    sealed class PathKeyComparer : IEqualityComparer<PathKey>
    {
        public static readonly PathKeyComparer Instance = new();
        public bool Equals(PathKey a, PathKey b) => a.Root == b.Root && string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode(PathKey k) => HashCode.Combine(k.Root, StringComparer.OrdinalIgnoreCase.GetHashCode(k.Path));
    }

    /// <summary>One file, compact. <see cref="Name"/> null = a free slot.</summary>
    struct Row
    {
        public long LocalId, Size, Write, Change, LastSeen, CoverOffset;
        public ulong FileIdLo, FileIdHi;
        public string? Name;
        public string Title;
        public Shared Shared;        // album, album artist, artists, reason: one object per distinct set (an album's tracks share it)
        public int Folder, DurationMs, CoverLength;
        public uint Volume, Attributes;
        public ushort Track, Disc, Year, Bitrate, VideoWidth, VideoHeight;
        public byte Format, State, Kind, Mime, Encoding, PictureType, Flags;
    }

    /// <summary>What the tracks of one album (and one reason) have in common, interned: one reference per row instead of four.</summary>
    sealed record Shared(string Album, string AlbumArtist, string[] Artists, string Why);

    /// <summary>One folder: its path (shared by every file in it), its record when listed, its files' slots.</summary>
    sealed class FolderSlot(int root, string rel)
    {
        public readonly int Root = root;
        public readonly string Rel = rel;
        public LocalFolder? Record;
        public List<int>? Members;
    }

    /// <summary>Open addressing over DISTINCT 64-bit keys, each holding one row slot, or (for a key several rows share: an
    /// untagged "Track 01" in every album) a group list. About 16-20 bytes a key; a shared key costs one list, never a probe
    /// cluster.</summary>
    sealed class HashSlots
    {
        ulong[] _keys = new ulong[16];
        int[] _values = Empty(16);      // >= 0: one slot; <= -2: group -(v + 2); -1: empty
        readonly List<List<int>?> _groups = new();
        readonly Stack<int> _freeGroups = new();
        int _count;

        static int[] Empty(int n) { var a = new int[n]; Array.Fill(a, -1); return a; }
        int Home(ulong key) => (int)(((key >> 32) * (ulong)(uint)_values.Length) >> 32);
        int Next(int i) => i + 1 == _values.Length ? 0 : i + 1;

        public void EnsureCapacity(int keys)
        {
            int want = (int)(keys / 0.75) + 1;
            if (want > _values.Length) Rehash(want);
        }

        int Find(ulong key)
        {
            int i = Home(key);
            while (_values[i] != -1)
            {
                if (_keys[i] == key) return i;
                i = Next(i);
            }
            return ~i;   // not there: the complement of the empty cell that ends the run
        }

        public void Add(ulong key, int slot)
        {
            int i = Find(key);
            if (i >= 0)
            {
                int v = _values[i];
                if (v >= 0)
                {
                    if (v == slot) return;
                    int g;
                    if (_freeGroups.Count > 0) { g = _freeGroups.Pop(); _groups[g] = [v, slot]; }
                    else { g = _groups.Count; _groups.Add([v, slot]); }
                    _values[i] = -(g + 2);
                }
                else
                {
                    List<int> group = _groups[-(v + 2)]!;
                    if (!group.Contains(slot)) group.Add(slot);
                }
                return;
            }
            if ((_count + 1) * 4 > _values.Length * 3)
            {
                Rehash((int)((_count + 1) / 0.6) + 1);
                i = Find(key);
            }
            _keys[~i] = key;
            _values[~i] = slot;
            _count++;
        }

        public void Remove(ulong key, int slot)
        {
            int i = Find(key);
            if (i < 0) return;
            int v = _values[i];
            if (v >= 0)
            {
                if (v != slot) return;
                Delete(i);
                return;
            }
            int g = -(v + 2);
            List<int> group = _groups[g]!;
            group.Remove(slot);
            if (group.Count > 1) return;
            _groups[g] = null;
            _freeGroups.Push(g);
            if (group.Count == 1) _values[i] = group[0];
            else Delete(i);
        }

        /// <summary>Backward-shift deletion: pull later members of the run into the hole when their home allows it.</summary>
        void Delete(int i)
        {
            int hole = i, j = i;
            while (true)
            {
                j = Next(j);
                if (_values[j] == -1) break;
                int home = Home(_keys[j]);
                bool movable = hole <= j ? (home <= hole || home > j) : (home <= hole && home > j);
                if (!movable) continue;
                _keys[hole] = _keys[j];
                _values[hole] = _values[j];
                hole = j;
            }
            _values[hole] = -1;
            _count--;
        }

        public void Get(ulong key, List<int> into)
        {
            into.Clear();
            int i = Find(key);
            if (i < 0) return;
            int v = _values[i];
            if (v >= 0) into.Add(v);
            else into.AddRange(_groups[-(v + 2)]!);
        }

        void Rehash(int capacity)
        {
            ulong[] keys = _keys;
            int[] values = _values;
            _keys = new ulong[capacity];
            _values = Empty(capacity);
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] == -1) continue;
                int j = Home(keys[i]);
                while (_values[j] != -1) j = Next(j);
                _keys[j] = keys[i];
                _values[j] = values[i];
            }
        }
    }

    /// <summary>Everything the index holds. A load builds a new one off the lock and swaps it in.</summary>
    sealed class Store
    {
        public Row[] Rows = new Row[64];
        public int RowCount;                               // slots in use or freed (the high-water mark)
        public readonly Stack<int> Free = new();
        public int[] SlotOfId = [];                        // LocalId -> slot + 1 (0 = none)
        public readonly List<FolderSlot> Folders = new();
        public readonly Dictionary<PathKey, int> FolderIndex = new(PathKeyComparer.Instance);
        public readonly HashSlots Exact = new(), Loose = new(), Names = new();
        public HashSlots? ByFileId, BySizeWrite;           // only while a scan pairs moves
        public readonly HashSet<string> Strings = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string[]> ArtistArrays = new(StringComparer.Ordinal);
        public readonly Dictionary<Shared, Shared> SharedSets = new();
        public readonly Dictionary<int, string> MatchTitles = new();   // slot -> match title, only where it is not the rule
        public readonly Dictionary<int, LocalRoot> Roots = new();
        public long NextId = 1;
        public int Audio, Unplayable, Missing, Cloud, Video, Entries;
    }

    readonly Lock _gate = new();
    readonly Lock _fileGate = new();   // held across apply + log append, so the log's order is the memory's order
    Store _s = new();
    int _version;

    string? _binPath, _logPath;
    FileStream? _log;
    ulong _generation;

    // ── reading ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Bumped by every change a reader could see (a commit, a load, a root change, a purge).</summary>
    public int Version => Volatile.Read(ref _version);
    /// <summary>Audio entries held, every state (video entries are not counted, D19).</summary>
    public int Count { get { lock (_gate) return _s.Audio; } }
    /// <summary>Audio entries this build cannot play (no decoder, D7; or unreadable).</summary>
    public int UnplayableCount { get { lock (_gate) return _s.Unplayable; } }
    /// <summary>Audio entries in the Missing state.</summary>
    public int MissingCount { get { lock (_gate) return _s.Missing; } }
    /// <summary>Audio entries that are cloud-only placeholders.</summary>
    public int CloudOnlyCount { get { lock (_gate) return _s.Cloud; } }
    /// <summary>Video entries (indexed for later, D19).</summary>
    public int VideoCount { get { lock (_gate) return _s.Video; } }
    /// <summary>Bytes in the change log (compacted at 2 MB).</summary>
    public long LogBytes { get { lock (_fileGate) return _log?.Length ?? 0; } }

    public bool TryGet(long localId, [NotNullWhen(true)] out LocalEntry? entry)
    {
        lock (_gate)
        {
            int slot = SlotOf(_s, localId);
            entry = slot < 0 ? null : Materialize(_s, slot);
        }
        return entry is not null;
    }

    /// <summary>The entry at <paramref name="relativePath"/> under root <paramref name="rootId"/> (case-insensitive).</summary>
    public bool TryGetByPath(int rootId, string relativePath, [NotNullWhen(true)] out LocalEntry? entry)
    {
        entry = null;
        string folder = FolderOf(relativePath), name = NameOf(relativePath);
        lock (_gate)
        {
            if (!_s.FolderIndex.TryGetValue(new PathKey(rootId, folder), out int f) || _s.Folders[f].Members is not { } members) return false;
            foreach (int slot in members)
                if (string.Equals(_s.Rows[slot].Name, name, StringComparison.OrdinalIgnoreCase)) { entry = Materialize(_s, slot); return true; }
        }
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
            foreach (LocalRoot root in _s.Roots.Values)
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
        lock (_gate) return _s.Roots.TryGetValue(entry.RootId, out LocalRoot? root) ? Join(root.Path, entry.RelativePath) : null;
    }

    public static string Join(string root, string relative) => relative.Length == 0 ? root : root.TrimEnd('\\') + "\\" + relative;

    /// <summary>Every entry (video included), ordered by root then path. Builds a record per file: not for a hot path.</summary>
    public LocalEntry[] Snapshot()
    {
        LocalEntry[] all;
        lock (_gate)
        {
            all = new LocalEntry[_s.Entries];
            int n = 0;
            for (int slot = 0; slot < _s.RowCount; slot++)
                if (_s.Rows[slot].Name is not null) all[n++] = Materialize(_s, slot);
        }
        Array.Sort(all, static (a, b) => a.RootId != b.RootId ? a.RootId.CompareTo(b.RootId) : string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase));
        return all;
    }

    /// <summary>Every listed folder. A copy.</summary>
    public LocalFolder[] Folders()
    {
        var list = new List<LocalFolder>();
        lock (_gate) foreach (FolderSlot f in _s.Folders) if (f.Record is { } r) list.Add(r);
        return list.ToArray();
    }

    /// <summary>The listed folders under one root. A copy.</summary>
    public List<LocalFolder> FoldersOf(int rootId)
    {
        var list = new List<LocalFolder>();
        lock (_gate) foreach (FolderSlot f in _s.Folders) if (f.Root == rootId && f.Record is { } r) list.Add(r);
        return list;
    }

    public bool TryGetFolder(int rootId, string relativePath, [NotNullWhen(true)] out LocalFolder? folder)
    {
        lock (_gate)
        {
            folder = _s.FolderIndex.TryGetValue(new PathKey(rootId, relativePath), out int f) ? _s.Folders[f].Record : null;
            return folder is not null;
        }
    }

    /// <summary>The entries whose file sits directly in a folder. A copy.</summary>
    public List<LocalEntry> EntriesIn(int rootId, string folderPath)
    {
        var list = new List<LocalEntry>();
        lock (_gate)
        {
            if (!_s.FolderIndex.TryGetValue(new PathKey(rootId, folderPath), out int f) || _s.Folders[f].Members is not { } members) return list;
            foreach (int slot in members) list.Add(Materialize(_s, slot));
        }
        return list;
    }

    /// <summary>Does a folder hold a cloud-only entry? (The scanner diffs such a folder even when its fingerprint is equal.)
    /// No record is built.</summary>
    public bool FolderHasCloud(int rootId, string folderPath)
    {
        lock (_gate)
        {
            if (!_s.FolderIndex.TryGetValue(new PathKey(rootId, folderPath), out int f) || _s.Folders[f].Members is not { } members) return false;
            foreach (int slot in members) if (_s.Rows[slot].State == (byte)LocalState.CloudOnly) return true;
        }
        return false;
    }

    /// <summary>The entry that holds a file id on a volume (NTFS/ReFS move identity), if any. Builds the scan-time map on
    /// first use (<see cref="ReleaseScanMaps"/> drops it).</summary>
    public bool TryGetByFileId(uint volumeSerial, UInt128 fileId, [NotNullWhen(true)] out LocalEntry? entry)
    {
        entry = null;
        if (fileId == 0) return false;
        var slots = new List<int>(2);
        lock (_gate)
        {
            Store s = _s;
            if (s.ByFileId is null)
            {
                s.ByFileId = new HashSlots();
                s.ByFileId.EnsureCapacity(s.Entries);
                for (int slot = 0; slot < s.RowCount; slot++)
                    if (s.Rows[slot].Name is not null && (s.Rows[slot].FileIdLo | s.Rows[slot].FileIdHi) != 0) s.ByFileId.Add(HashOf(s.Rows[slot].Volume, s.Rows[slot].FileIdLo, s.Rows[slot].FileIdHi), slot);
            }
            s.ByFileId.Get(HashOf(volumeSerial, (ulong)fileId, (ulong)(fileId >> 64)), slots);
            foreach (int slot in slots)
            {
                ref Row row = ref s.Rows[slot];
                if (row.Volume == volumeSerial && row.FileIdLo == (ulong)fileId && row.FileIdHi == (ulong)(fileId >> 64)) { entry = Materialize(s, slot); return true; }
            }
        }
        return false;
    }

    /// <summary>Entries with this exact size and write time (the move identity where ids are not stable). A copy. Builds the
    /// scan-time map on first use.</summary>
    public List<LocalEntry> BySizeAndWrite(long size, long writeTicks)
    {
        var list = new List<LocalEntry>();
        var slots = new List<int>(2);
        lock (_gate)
        {
            Store s = _s;
            if (s.BySizeWrite is null)
            {
                s.BySizeWrite = new HashSlots();
                s.BySizeWrite.EnsureCapacity(s.Entries);
                for (int slot = 0; slot < s.RowCount; slot++)
                    if (s.Rows[slot].Name is not null) s.BySizeWrite.Add(HashOf(s.Rows[slot].Size, s.Rows[slot].Write), slot);
            }
            s.BySizeWrite.Get(HashOf(size, writeTicks), slots);
            foreach (int slot in slots)
                if (s.Rows[slot].Size == size && s.Rows[slot].Write == writeTicks) list.Add(Materialize(s, slot));
        }
        return list;
    }

    /// <summary>Drop the move-pairing maps a scan built (the scanner calls it when a root's scan ends).</summary>
    public void ReleaseScanMaps()
    {
        lock (_gate) { _s.ByFileId = null; _s.BySizeWrite = null; }
    }

    public LocalRoot[] Roots()
    {
        lock (_gate) { var all = new LocalRoot[_s.Roots.Count]; _s.Roots.Values.CopyTo(all, 0); return all; }
    }

    public bool TryGetRoot(int rootId, [NotNullWhen(true)] out LocalRoot? root)
    {
        lock (_gate) return _s.Roots.TryGetValue(rootId, out root);
    }

    /// <summary>A fresh LocalId: never handed out before, never reused (D13).</summary>
    public long NewId() { lock (_gate) return _s.NextId++; }

    // ── matching (§3.4) ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The §3.4 walk for a <c>spotify:local:</c> row's decoded parts: tier 1 the exact map (first, last and joined
    /// artists), 1 s tolerance (5 s for an estimated length); tier 2, only when the row's artist and album are empty, the file
    /// name map, 1 s; tier 3 the loose map, 2 s. A candidate outside the tolerance is never picked. Missing and cloud-only
    /// entries match too (the row shows them as missing or downloadable); video entries never do. Logs one `local.match` line.</summary>
    public LocalMatchResult Match(string title, string artist, string album, int uriSeconds)
    {
        string exactTitle = LocalMatch.Exact(title), exactArtist = LocalMatch.Exact(artist);
        string exactKey = LocalMatch.Key(exactTitle, exactArtist);
        string looseKey = LocalMatch.Key(LocalMatch.Loose(title), LocalMatch.Loose(artist));
        bool untaggedShape = artist.Length == 0 && album.Length == 0;
        var candidates = new List<LocalEntry>(4);
        LocalMatchResult r = default;
        int keyed = 0;
        lock (_gate)
        {
            bool found = TryTier(_s.Exact, exactKey, 1, uriSeconds, album, candidates, ref keyed, out r)
                         || (untaggedShape && TryTier(_s.Names, exactTitle, 2, uriSeconds, album, candidates, ref keyed, out r))
                         || TryTier(_s.Loose, looseKey, 3, uriSeconds, album, candidates, ref keyed, out r);
            if (!found) r = new LocalMatchResult(null, 0, keyed);
        }
        // Always on (§3.4): which tier answered and how many shared its key; the FILE NAME only, never the folder.
        Log.Info("local", r.Entry is { } e
            ? "local.match tier=" + r.Tier + " candidates=" + r.Candidates + " seconds=" + uriSeconds + " file=" + NameOf(e.RelativePath)
            : "local.match miss keyed=" + keyed + " seconds=" + uriSeconds + " untagged=" + (untaggedShape ? 1 : 0));
        return r;
    }

    bool TryTier(HashSlots map, string key, byte tier, int uriSeconds, string album, List<LocalEntry> candidates, ref int keyed, out LocalMatchResult result)
    {
        result = default;
        if (key.Length == 0) return false;
        var slots = new List<int>(2);
        map.Get(HashOf(key), slots);
        candidates.Clear();
        int sharing = 0;
        foreach (int slot in slots)
        {
            if (!HasKey(_s, slot, tier, key)) continue;   // a hash collision never answers
            sharing++;
            LocalEntry e = Materialize(_s, slot);
            int tolerance = tier switch { 1 => e.Tags.DurationEstimated ? 5 : 1, 2 => 1, _ => 2 };
            if (LocalMatch.DurationAgrees(uriSeconds, e.Tags.DurationMs, tolerance)) candidates.Add(e);
        }
        keyed += sharing;
        if (candidates.Count == 0) return false;
        int pick = LocalMatch.Pick(CollectionsMarshal.AsSpan(candidates), album, uriSeconds);
        if (pick < 0) return false;
        result = new LocalMatchResult(candidates[pick], tier, sharing);
        return true;
    }

    // ── writing ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Commit one folder's batch (§3.6 step 7) and append it to the log. Any thread.</summary>
    public void Apply(LocalScanBatch batch)
    {
        if (batch.IsEmpty) return;
        lock (_fileGate)
        {
            lock (_gate) ApplyTo(_s, batch);
            Interlocked.Increment(ref _version);
            long nextId;
            lock (_gate) nextId = _s.NextId;
            AppendLocked(RecBatch, w => WriteBatch(w, batch, nextId));
        }
        CompactIfLarge();
    }

    /// <summary>Add or update a root (a music folder, its volume and its journal cursor).</summary>
    public void SetRoot(LocalRoot root)
    {
        lock (_fileGate)
        {
            lock (_gate) _s.Roots[root.RootId] = root;
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
            lock (_gate) RemoveRootFrom(_s, rootId);
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
        {
            Store s = _s;
            for (int slot = 0; slot < s.RowCount; slot++)
            {
                ref Row row = ref s.Rows[slot];
                if (row.Name is not null && s.Folders[row.Folder].Root == rootId && row.State == (byte)LocalState.Missing && row.LastSeen < beforeTicks) ids.Add(row.LocalId);
            }
        }
        if (ids.Count == 0) return 0;
        lock (_fileGate)
        {
            lock (_gate) foreach (long id in ids) Remove(_s, id);
            Interlocked.Increment(ref _version);
            AppendLocked(RecPurge, w => { w.Write(ids.Count); foreach (long id in ids) w.Write(id); });
        }
        return ids.Count;
    }

    /// <summary>Empty the index in memory (the inspector's fixtures).</summary>
    public void Clear()
    {
        lock (_gate) _s = new Store();
        Interlocked.Increment(ref _version);
    }

    static void ApplyTo(Store s, LocalScanBatch batch)
    {
        foreach (long id in batch.Removes) Remove(s, id);
        foreach (LocalEntry e in batch.Upserts)
        {
            Remove(s, e.LocalId);
            Insert(s, e);
        }
        if (batch.RemoveFolder)
        {
            if (s.FolderIndex.TryGetValue(new PathKey(batch.RootId, batch.FolderPath), out int f)) s.Folders[f].Record = null;
        }
        else if (batch.Folder is { } folder) s.Folders[FolderSlotOf(s, batch.RootId, batch.FolderPath)].Record = folder with { Images = InternAll(s, folder.Images) };
    }

    static void RemoveRootFrom(Store s, int rootId)
    {
        s.Roots.Remove(rootId);
        var ids = new List<long>();
        for (int slot = 0; slot < s.RowCount; slot++)
            if (s.Rows[slot].Name is not null && s.Folders[s.Rows[slot].Folder].Root == rootId) ids.Add(s.Rows[slot].LocalId);
        foreach (long id in ids) Remove(s, id);
        foreach (FolderSlot f in s.Folders) if (f.Root == rootId) f.Record = null;
    }

    static int FolderSlotOf(Store s, int rootId, string folder)
    {
        var key = new PathKey(rootId, folder);
        if (s.FolderIndex.TryGetValue(key, out int f)) return f;
        f = s.Folders.Count;
        s.Folders.Add(new FolderSlot(rootId, folder));
        s.FolderIndex[key] = f;
        return f;
    }

    static int SlotOf(Store s, long localId) => localId > 0 && localId < s.SlotOfId.Length ? s.SlotOfId[localId] - 1 : -1;

    static void Insert(Store s, LocalEntry e)
    {
        int slot;
        if (s.Free.Count > 0) slot = s.Free.Pop();
        else
        {
            if (s.RowCount == s.Rows.Length) Array.Resize(ref s.Rows, Math.Max(64, s.Rows.Length + s.Rows.Length / 4));
            slot = s.RowCount++;
        }
        if (e.LocalId >= s.SlotOfId.Length) Array.Resize(ref s.SlotOfId, (int)Math.Max(e.LocalId + 1, s.SlotOfId.Length + s.SlotOfId.Length / 4 + 64));
        s.SlotOfId[e.LocalId] = slot + 1;
        if (e.LocalId >= s.NextId) s.NextId = e.LocalId + 1;

        LocalTagSet t = e.Tags;
        string name = NameOf(e.RelativePath);
        int folder = FolderSlotOf(s, e.RootId, FolderOf(e.RelativePath));
        string rule = t.Title.Length > 0 ? t.Title : Path.GetFileNameWithoutExtension(name);
        s.Rows[slot] = new Row
        {
            LocalId = e.LocalId, Size = e.Size, Write = e.WriteTicks, Change = e.ChangeTicks, LastSeen = e.LastSeenTicks, CoverOffset = t.Cover.Offset,
            FileIdLo = (ulong)e.FileId, FileIdHi = (ulong)(e.FileId >> 64),
            Name = name, Title = t.Title, Shared = InternShared(s, t, e.Why),
            Folder = folder, DurationMs = t.DurationMs, CoverLength = t.Cover.Length, Volume = e.VolumeSerial, Attributes = e.Attributes,
            Track = Clamp(t.TrackNumber), Disc = Clamp(t.DiscNumber), Year = Clamp(t.Year), Bitrate = Clamp(t.BitrateKbps),
            VideoWidth = Clamp(t.VideoWidth), VideoHeight = Clamp(t.VideoHeight),
            Format = (byte)e.Format, State = (byte)e.State, Kind = (byte)t.Kind, Mime = (byte)t.Cover.Mime, Encoding = (byte)t.Cover.Encoding,
            PictureType = t.Cover.PictureType,
            Flags = (byte)((e.Playable ? FlagPlayable : 0) | (t.DurationEstimated ? FlagEstimated : 0) | (t.HasAudio ? FlagHasAudio : 0)),
        };
        if (e.MatchTitle != rule) s.MatchTitles[slot] = e.MatchTitle;
        (s.Folders[folder].Members ??= new List<int>(4)).Add(slot);
        s.Entries++;
        ref Row row = ref s.Rows[slot];
        s.ByFileId?.Add(HashOf(row.Volume, row.FileIdLo, row.FileIdHi), slot);
        s.BySizeWrite?.Add(HashOf(row.Size, row.Write), slot);
        if (t.Kind == LocalKind.Video) { s.Video++; return; }
        s.Audio++;
        if (!e.Playable) s.Unplayable++;
        if (e.State == LocalState.Missing) s.Missing++;
        else if (e.State == LocalState.CloudOnly) s.Cloud++;
        foreach (string k in MatchKeys(e.MatchTitle, t.Artists, loose: false)) s.Exact.Add(HashOf(k), slot);
        foreach (string k in MatchKeys(e.MatchTitle, t.Artists, loose: true)) s.Loose.Add(HashOf(k), slot);
        s.Names.Add(HashOf(NameKey(name)), slot);
    }

    static void Remove(Store s, long id)
    {
        int slot = SlotOf(s, id);
        if (slot < 0) return;
        ref Row row = ref s.Rows[slot];
        string title = MatchTitleOf(s, slot);
        s.ByFileId?.Remove(HashOf(row.Volume, row.FileIdLo, row.FileIdHi), slot);
        s.BySizeWrite?.Remove(HashOf(row.Size, row.Write), slot);
        s.Folders[row.Folder].Members!.Remove(slot);
        if (row.Kind == (byte)LocalKind.Video) s.Video--;
        else
        {
            s.Audio--;
            if ((row.Flags & FlagPlayable) == 0) s.Unplayable--;
            if (row.State == (byte)LocalState.Missing) s.Missing--;
            else if (row.State == (byte)LocalState.CloudOnly) s.Cloud--;
            foreach (string k in MatchKeys(title, row.Shared.Artists, loose: false)) s.Exact.Remove(HashOf(k), slot);
            foreach (string k in MatchKeys(title, row.Shared.Artists, loose: true)) s.Loose.Remove(HashOf(k), slot);
            s.Names.Remove(HashOf(NameKey(row.Name!)), slot);
        }
        row = default;
        s.MatchTitles.Remove(slot);
        s.SlotOfId[id] = 0;
        s.Free.Push(slot);
        s.Entries--;
    }

    static LocalEntry Materialize(Store s, int slot)
    {
        ref Row row = ref s.Rows[slot];
        FolderSlot folder = s.Folders[row.Folder];
        string name = row.Name!;
        Shared shared = row.Shared;
        var tags = new LocalTagSet((LocalKind)row.Kind, row.Title, shared.Artists, shared.Album, shared.AlbumArtist, row.Track, row.Disc, row.Year,
            row.DurationMs, (row.Flags & FlagEstimated) != 0, row.Bitrate,
            new LocalCover(row.CoverOffset, row.CoverLength, (LocalCoverMime)row.Mime, (LocalCoverEncoding)row.Encoding, row.PictureType),
            row.VideoWidth, row.VideoHeight, (row.Flags & FlagHasAudio) != 0);
        string matchTitle = MatchTitleOf(s, slot);
        return new LocalEntry(row.LocalId, folder.Root, folder.Rel.Length == 0 ? name : folder.Rel + "\\" + name, row.Volume,
            new UInt128(row.FileIdHi, row.FileIdLo), row.Size, row.Write, row.Change, row.Attributes, (Spotify.Audio.Format)row.Format, tags,
            matchTitle, (LocalState)row.State, row.LastSeen, (row.Flags & FlagPlayable) != 0, shared.Why);
    }

    /// <summary>A row's match title: the side table's when it is not the rule, else the title tag, else the file name without
    /// its extension.</summary>
    static string MatchTitleOf(Store s, int slot)
    {
        if (s.MatchTitles.TryGetValue(slot, out string? special)) return special;
        ref Row row = ref s.Rows[slot];
        return row.Title.Length > 0 ? row.Title : Path.GetFileNameWithoutExtension(row.Name!);
    }

    static Shared InternShared(Store s, in LocalTagSet t, string why)
    {
        var shared = new Shared(Intern(s, t.Album), Intern(s, t.AlbumArtist), InternArtists(s, t.Artists), Intern(s, why));
        if (s.SharedSets.TryGetValue(shared, out Shared? existing)) return existing;
        s.SharedSets[shared] = shared;
        return shared;
    }

    static ushort Clamp(int v) => (ushort)Math.Clamp(v, 0, ushort.MaxValue);

    static string Intern(Store s, string text)
    {
        if (text.Length == 0) return "";
        if (s.Strings.TryGetValue(text, out string? shared)) return shared;
        s.Strings.Add(text);
        return text;
    }

    static string[] InternAll(Store s, string[] texts)
    {
        for (int i = 0; i < texts.Length; i++) texts[i] = Intern(s, texts[i]);
        return texts;
    }

    /// <summary>One shared array per distinct artist list (an album's tracks share theirs), its values interned.</summary>
    static string[] InternArtists(Store s, string[] artists)
    {
        if (artists.Length == 0) return [];
        string key = artists.Length == 1 ? artists[0] : string.Join('\u001f', artists);
        if (s.ArtistArrays.TryGetValue(key, out string[]? shared)) return shared;
        var copy = new string[artists.Length];
        for (int i = 0; i < copy.Length; i++) copy[i] = Intern(s, artists[i]);
        s.ArtistArrays[artists.Length == 1 ? copy[0] : key] = copy;
        return copy;
    }

    /// <summary>The keys an entry is filed under (§3.4/§3.5): its match title with the first artist, the last artist and the
    /// ", "-joined artists (duplicates folded); an entry with no artist is filed under the empty artist.</summary>
    static IEnumerable<string> MatchKeys(string matchTitle, string[] artists, bool loose)
    {
        Func<string, string> norm = loose ? LocalMatch.Loose : LocalMatch.Exact;
        string title = norm(matchTitle);
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
    static string NameKey(string fileName) => LocalMatch.Exact(Path.GetFileNameWithoutExtension(fileName));

    /// <summary>Is <paramref name="key"/> really one of the row's keys for this tier (the check behind a hashed lookup)?</summary>
    static bool HasKey(Store s, int slot, byte tier, string key)
    {
        ref Row row = ref s.Rows[slot];
        if (row.Name is null || row.Kind == (byte)LocalKind.Video) return false;
        if (tier == 2) return NameKey(row.Name) == key;
        foreach (string k in MatchKeys(MatchTitleOf(s, slot), row.Shared.Artists, loose: tier == 3)) if (k == key) return true;
        return false;
    }

    static ulong HashOf(string key) => XxHash3.HashToUInt64(MemoryMarshal.AsBytes(key.AsSpan()));

    static ulong HashOf(long size, long write)
    {
        Span<long> both = [size, write];
        return XxHash3.HashToUInt64(MemoryMarshal.AsBytes(both));
    }

    static ulong HashOf(uint volume, ulong lo, ulong hi)
    {
        Span<ulong> all = [volume, lo, hi];
        return XxHash3.HashToUInt64(MemoryMarshal.AsBytes(all));
    }

    /// <summary>The folder part of a relative file path ("" for a file directly in the root).</summary>
    public static string FolderOf(string relativePath)
    {
        int slash = relativePath.LastIndexOf('\\');
        return slash < 0 ? "" : relativePath[..slash];
    }

    static string NameOf(string relativePath)
    {
        int slash = relativePath.LastIndexOf('\\');
        return slash < 0 ? relativePath : relativePath[(slash + 1)..];
    }

    // ── the files ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Read the bin and replay the log into a NEW store built off the lock, swap it in, then keep the log open for
    /// appends. Background thread; never throws. A file this build cannot read is ignored (and replaced by the next
    /// compaction). Returns the audio entries loaded.</summary>
    public int Load(string binPath)
    {
        string logPath = Path.ChangeExtension(binPath, ".log");
        lock (_fileGate)   // a commit waits for the load; readers keep reading the old store until the swap
        {
            _binPath = binPath;
            _logPath = logPath;
            var store = new Store();
            ulong generation = 0;
            try
            {
                if (File.Exists(binPath))
                {
                    using var stream = new FileStream(binPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                    using var r = new BinaryReader(stream, Encoding.UTF8);
                    if (!ReadBin(store, r, out generation)) { store = new Store(); generation = 0; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException or InvalidDataException or ArgumentException or FormatException or IndexOutOfRangeException)
            {
                Log.Warn("local", "local.index.load bin unreadable; rebuilding (" + ex.GetType().Name + ")");
                store = new Store();
                generation = 0;
            }
            _generation = generation;
            OpenLogLocked(store, replay: true);
            lock (_gate) _s = store;
        }
        Interlocked.Increment(ref _version);
        return Count;
    }

    /// <summary>Write the whole index to the bin (temp file + move) and start an empty log. The records are copied under the
    /// lock; the file is written off it. Any background thread.</summary>
    public bool Compact()
    {
        lock (_fileGate)
        {
            if (_binPath is not { } binPath) return false;
            ulong generation = _generation + 1;
            string tmp = binPath + "." + Environment.ProcessId + ".tmp";
            LocalEntry[] entries;
            LocalFolder[] folders;
            LocalRoot[] roots;
            long nextId;
            lock (_gate)
            {
                Store s = _s;
                entries = new LocalEntry[s.Entries];
                int n = 0;
                for (int slot = 0; slot < s.RowCount; slot++) if (s.Rows[slot].Name is not null) entries[n++] = Materialize(s, slot);
                var fs = new List<LocalFolder>();
                foreach (FolderSlot f in s.Folders) if (f.Record is { } rec) fs.Add(rec);
                folders = fs.ToArray();
                roots = new LocalRoot[s.Roots.Count];
                s.Roots.Values.CopyTo(roots, 0);
                nextId = s.NextId;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);
                using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                using (var w = new BinaryWriter(stream, new UTF8Encoding(false)))
                    WriteBin(w, generation, nextId, roots, folders, entries);
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
            OpenLogLocked(null, replay: false);
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

    void OpenLogLocked(Store? replayInto, bool replay)
    {
        if (_logPath is not { } logPath) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            var log = new FileStream(logPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1 << 12);
            long good = replay && replayInto is not null ? ReplayLog(replayInto, log) : -1;
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

    /// <summary>Replay every whole record of a log of this generation into <paramref name="store"/>; returns the offset after
    /// the last good record, or -1 when the log is not one this index can replay.</summary>
    long ReplayLog(Store store, FileStream log)
    {
        if (log.Length < 16) return -1;
        log.Seek(0, SeekOrigin.Begin);
        using var r = new BinaryReader(log, Encoding.UTF8, leaveOpen: true);
        if (r.ReadUInt32() != LogMagic || r.ReadInt32() != FormatVersion || r.ReadUInt64() != _generation) return -1;
        long good = log.Position;
        int records = 0;
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
                ReplayRecord(store, br);
            }
            catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException or ArgumentException or FormatException)
            {
                break;
            }
            good = log.Position;
            records++;
        }
        if (records > 0) Log.Info("local", "local.index.log replayed records=" + records);
        return good;
    }

    static void ReplayRecord(Store s, BinaryReader r)
    {
        byte type = r.ReadByte();
        switch (type)
        {
            case RecBatch: ApplyTo(s, ReadBatch(s, r)); break;
            case RecRoot: { LocalRoot root = ReadRoot(r); s.Roots[root.RootId] = root; break; }
            case RecRootRemoved: RemoveRootFrom(s, r.ReadInt32()); break;
            case RecPurge: { int n = r.ReadInt32(); for (int i = 0; i < n; i++) Remove(s, r.ReadInt64()); break; }
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

    static void WriteBin(BinaryWriter w, ulong generation, long nextId, LocalRoot[] roots, LocalFolder[] folders, LocalEntry[] entries)
    {
        // One string table for every string the records hold: artist and album names repeat across a library.
        var table = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        void Intern(string s) { if (!table.ContainsKey(s)) { table[s] = order.Count; order.Add(s); } }
        foreach (LocalEntry e in entries)
        {
            Intern(e.RelativePath); Intern(e.Tags.Title); Intern(e.Tags.Album); Intern(e.Tags.AlbumArtist); Intern(e.MatchTitle); Intern(e.Why);
            foreach (string a in e.Tags.Artists) Intern(a);
        }
        foreach (LocalFolder f in folders) { Intern(f.RelativePath); foreach (string i in f.Images) Intern(i); }

        w.Write(BinMagic); w.Write(FormatVersion); w.Write(generation); w.Write(nextId);
        w.Write(roots.Length);
        foreach (LocalRoot root in roots) WriteRoot(w, root);
        w.Write(order.Count);
        foreach (string s in order) w.Write(s);
        Func<string, int> index = s => table[s];
        w.Write(folders.Length);
        foreach (LocalFolder f in folders) WriteFolder(w, f, index);
        w.Write(entries.Length);
        foreach (LocalEntry e in entries) WriteEntry(w, e, index);
        w.Write(BinMagic);   // a trailer: a truncated bin is refused whole
    }

    static bool ReadBin(Store s, BinaryReader r, out ulong generation)
    {
        generation = 0;
        if (r.ReadUInt32() != BinMagic || r.ReadInt32() != FormatVersion) return false;
        generation = r.ReadUInt64();
        long nextId = r.ReadInt64();
        int roots = r.ReadInt32();
        for (int i = 0; i < roots; i++) { LocalRoot root = ReadRoot(r); s.Roots[root.RootId] = root; }
        int strings = r.ReadInt32();
        var table = new string[strings];
        for (int i = 0; i < strings; i++) table[i] = r.ReadString();
        Func<string> next = () => table[r.Read7BitEncodedInt()];
        int folders = r.ReadInt32();
        for (int i = 0; i < folders; i++)
        {
            LocalFolder f = ReadFolder(r, next);
            s.Folders[FolderSlotOf(s, f.RootId, f.RelativePath)].Record = f with { Images = InternAll(s, f.Images) };
        }
        int entries = r.ReadInt32();
        // Sized once for what is coming: no growth slack in the rows, the id array or the match maps.
        s.Rows = new Row[Math.Max(64, entries + entries / 16)];
        s.SlotOfId = new int[Math.Max(nextId + 1, 1)];
        s.Exact.EnsureCapacity(entries + entries / 8);
        s.Loose.EnsureCapacity(entries + entries / 8);
        s.Names.EnsureCapacity(entries);
        for (int i = 0; i < entries; i++) Insert(s, ReadEntry(r, next));
        if (r.ReadUInt32() != BinMagic) return false;
        s.NextId = Math.Max(s.NextId, nextId);
        return true;
    }

    // ── records ──────────────────────────────────────────────────────────────────────────────────────────────────────

    static void WriteRoot(BinaryWriter w, LocalRoot root)
    {
        w.Write(root.RootId); w.Write(root.Path); w.Write(root.VolumeSerial); w.Write(root.JournalId); w.Write(root.NextUsn); w.Write(root.LastWalkTicks);
    }

    static LocalRoot ReadRoot(BinaryReader r)
        => new(r.ReadInt32(), r.ReadString(), r.ReadUInt32(), r.ReadUInt64(), r.ReadInt64(), r.ReadInt64());

    static void WriteBatch(BinaryWriter w, LocalScanBatch batch, long nextId)
    {
        Func<string, int>? inline = null;
        w.Write(batch.RootId); w.Write(batch.FolderPath); w.Write(batch.RemoveFolder); w.Write(nextId);
        w.Write(batch.Folder is not null);
        if (batch.Folder is { } folder) WriteFolder(w, folder, inline);
        w.Write(batch.Upserts.Count);
        foreach (LocalEntry e in batch.Upserts) WriteEntry(w, e, inline);
        w.Write(batch.Removes.Count);
        foreach (long id in batch.Removes) w.Write(id);
    }

    static LocalScanBatch ReadBatch(Store s, BinaryReader r)
    {
        var batch = new LocalScanBatch(r.ReadInt32(), r.ReadString()) { RemoveFolder = r.ReadBoolean() };
        s.NextId = Math.Max(s.NextId, r.ReadInt64());
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
