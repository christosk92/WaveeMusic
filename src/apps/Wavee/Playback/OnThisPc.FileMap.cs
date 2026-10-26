// ── Playback/OnThisPc.FileMap.cs ────────────────────────────────────────────────────────────────────────────────────
// the persisted (track, quality) → audio file id map behind the "on this PC" row fact
//
// Role: SHELL
// Spec: docs/themes/plan/technical-plan.md §6.3 (ZC2), owner decisions Q29 / NQ4
//
// WHY A MAP. The audio cache is addressed by FILE id (`ChunkDiskCache`, one `.enc` + `.map` per file), and a row knows only
// its TRACK. Which file a track plays from is the ladder's answer (`Spotify.Audio.ChooseFor`), and that needs the track's
// metadata, so a list cannot ask it per row. The ladder already resolves the answer at every open and every prefetch: it is
// recorded here then (`Spotify.Audio.NoteFileChosen`), per quality, because one track has a different file at each rung.
//
// THE FILE. `Cache\audiofiles.tsv` beside `audiokeys.db`: a header line, then `uri<TAB>quality<TAB>fileIdHex` per entry,
// oldest first. Plain text on purpose: it holds no secret (file ids already appear in every `audio.*` log line), it is
// rewritten whole (a few hundred KiB at the cap) through a temp file and a rename, and a file this build cannot read is
// ignored and rewritten, never an error. Bounded at <see cref="MaxEntries"/>: the least recently resolved tenth goes.
//
// THREADING. One `Lock` around the dictionary. Writers are the api threads (the ladder); the reader is the UI thread
// (`OnThisPcFacts.Of`, a dictionary probe under the lock, allocation-free); Load and Save run on background threads.

using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Wavee;

public static partial class Playback
{
    /// <summary>The persisted (track, quality) → audio file id map. Thread-safe; see the file header.</summary>
    public sealed class AudioFileMap
    {
        /// <summary>The most entries kept; above it the least recently resolved tenth is dropped.</summary>
        public const int MaxEntries = 20_000;

        /// <summary>The file's name beside the saved audio keys.</summary>
        public const string FileName = "audiofiles.tsv";

        const string Header = "wavee.audiofiles.v1";

        /// <summary>The map's path under <c>Platform.LocalFolder</c>: `%LOCALAPPDATA%\Wavee\Wavee\Cache\audiofiles.tsv`,
        /// beside `audiokeys.db`. PURE.</summary>
        public static string PathUnder(string localFolder) => Path.Combine(localFolder, "Wavee", "Cache", FileName);

        readonly record struct Key(EntityId Id, byte Quality);

        sealed class Entry(string uri, string fileIdHex, long seq)
        {
            public readonly string Uri = uri;
            public string FileIdHex = fileIdHex;
            public long Seq = seq;
        }

        readonly Lock _gate = new();
        readonly Dictionary<Key, Entry> _map = new();
        long _seq;
        int _version;

        /// <summary>Entries held.</summary>
        public int Count { get { lock (_gate) return _map.Count; } }

        /// <summary>Bumped by every change a reader could see (a new or different file id, a load, an eviction).</summary>
        public int Version => Volatile.Read(ref _version);

        /// <summary>Record that <paramref name="uri"/> at <paramref name="quality"/> (the <c>Spotify.Audio.Quality</c> rung)
        /// plays from <paramref name="fileIdHex"/>. Any thread. True when the answer is new or changed; a repeat only
        /// refreshes the entry's recency. A uri that is not a catalogue track or episode, a quality outside 0-3 or a file
        /// id that is not 8-128 hex digits is ignored.</summary>
        public bool Record(string uri, byte quality, string fileIdHex)
        {
            if (quality > 3 || !ValidFileId(fileIdHex) || !EntityId.TryParseGid(uri, out EntityId id) || !id.IsPlayable) return false;
            var key = new Key(id, quality);
            lock (_gate)
            {
                long seq = ++_seq;
                if (_map.TryGetValue(key, out Entry? entry))
                {
                    entry.Seq = seq;
                    if (string.Equals(entry.FileIdHex, fileIdHex, StringComparison.OrdinalIgnoreCase)) return false;
                    entry.FileIdHex = fileIdHex;
                }
                else
                {
                    _map[key] = new Entry(uri, fileIdHex, seq);
                    if (_map.Count > MaxEntries) EvictLocked();
                }
                Interlocked.Increment(ref _version);
                return true;
            }
        }

        /// <summary>The file id <paramref name="id"/> plays from at <paramref name="quality"/>. Any thread; allocation-free.</summary>
        public bool TryGet(EntityId id, byte quality, [NotNullWhen(true)] out string? fileIdHex)
        {
            lock (_gate)
            {
                if (_map.TryGetValue(new Key(id, quality), out Entry? entry)) { fileIdHex = entry.FileIdHex; return true; }
            }
            fileIdHex = null;
            return false;
        }

        /// <summary><see cref="TryGet(EntityId, byte, out string?)"/> by uri text (tests, diagnostics).</summary>
        public bool TryGet(string uri, byte quality, [NotNullWhen(true)] out string? fileIdHex)
        {
            fileIdHex = null;
            return EntityId.TryParseGid(uri, out EntityId id) && TryGet(id, quality, out fileIdHex);
        }

        /// <summary>Forget every entry (a test; the map is otherwise only ever bounded, never cleared).</summary>
        public void Clear()
        {
            lock (_gate) { _map.Clear(); _seq = 0; }
            Interlocked.Increment(ref _version);
        }

        /// <summary>8-128 hex digits: the shape of a catalogue file id, and the one the cache stores without hashing.</summary>
        public static bool ValidFileId(string? fileIdHex)
        {
            if (fileIdHex is null || fileIdHex.Length is < 8 or > 128) return false;
            foreach (char c in fileIdHex)
                if (!char.IsAsciiHexDigit(c)) return false;
            return true;
        }

        void EvictLocked()
        {
            var entries = new KeyValuePair<Key, Entry>[_map.Count];
            int i = 0;
            foreach (var pair in _map) entries[i++] = pair;
            Array.Sort(entries, static (a, b) => a.Value.Seq.CompareTo(b.Value.Seq));
            int drop = _map.Count - MaxEntries * 9 / 10;
            for (int j = 0; j < drop; j++) _map.Remove(entries[j].Key);
        }

        // ── the file ─────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Write every entry to <paramref name="path"/>, oldest first, through a temp file and a rename. Any
        /// background thread; never throws (false on failure).</summary>
        public bool Save(string path)
        {
            var text = new StringBuilder(64 * 1024);
            text.Append(Header).Append('\n');
            lock (_gate)
            {
                var entries = new KeyValuePair<Key, Entry>[_map.Count];
                int i = 0;
                foreach (var pair in _map) entries[i++] = pair;
                Array.Sort(entries, static (a, b) => a.Value.Seq.CompareTo(b.Value.Seq));   // oldest first: a load keeps the order
                foreach (var pair in entries)
                    text.Append(pair.Value.Uri).Append('\t').Append(pair.Key.Quality).Append('\t').Append(pair.Value.FileIdHex).Append('\n');
            }
            string tmp = path + "." + Environment.ProcessId + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(tmp, text.ToString(), new UTF8Encoding(false));
                File.Move(tmp, path, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(tmp); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
                return false;
            }
        }

        /// <summary>Read <paramref name="path"/> into the map (entries already recorded this session win). Returns the
        /// entries read; 0 for a missing file or one this build does not recognise. Any background thread; never throws.</summary>
        public int Load(string path)
        {
            string[] lines;
            try
            {
                if (!File.Exists(path)) return 0;
                lines = File.ReadAllLines(path, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
            if (lines.Length == 0 || !string.Equals(lines[0], Header, StringComparison.Ordinal)) return 0;
            int read = 0;
            lock (_gate)
            {
                // Loaded entries are OLDER than anything recorded this session: shift the session's recency above them.
                long baseSeq = _seq;
                foreach (var pair in _map) pair.Value.Seq += lines.Length;
                long seq = 0;
                for (int i = 1; i < lines.Length; i++)
                {
                    ReadOnlySpan<char> line = lines[i];
                    int a = line.IndexOf('\t');
                    if (a <= 0) continue;
                    int b = line[(a + 1)..].IndexOf('\t');
                    if (b <= 0) continue;
                    ReadOnlySpan<char> uri = line[..a], q = line.Slice(a + 1, b), file = line[(a + 2 + b)..];
                    if (q.Length != 1 || q[0] is < '0' or > '3') continue;
                    string fileIdHex = file.ToString();
                    if (!ValidFileId(fileIdHex) || !EntityId.TryParseGid(uri, out EntityId id) || !id.IsPlayable) continue;
                    var key = new Key(id, (byte)(q[0] - '0'));
                    seq++;
                    if (_map.ContainsKey(key)) continue;
                    _map[key] = new Entry(uri.ToString(), fileIdHex, seq);
                    read++;
                }
                _seq = baseSeq + lines.Length;
                if (_map.Count > MaxEntries) EvictLocked();
            }
            Interlocked.Increment(ref _version);
            return read;
        }
    }
}
