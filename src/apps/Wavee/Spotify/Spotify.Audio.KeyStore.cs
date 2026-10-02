// ── Spotify/Spotify.Audio.KeyStore.cs ──────────────────────────────────────────────────────────────────────────────────
// the session key map, the proof that decides what is saved, and the saved audio keys on disk
//
// Role: SHELL
// Spec: docs/plans/wavee/audio-key-store-implementation.md §1-§4 (G-123; the Settings seams G-192 / G-272)
//
// WHAT THIS DELIVERS. `Spotify.Audio.Key` asks, in order: the session map (256, FIFO — here), the saved keys on disk
// (`KeyStore`, here), the AP, the deriver. A key is SAVED only after it has proved itself against the track's own bytes
// (`KeyProof`): the first time a body sees chunk 0's ciphertext, `Ctr.Validates` decides. A key that opens chunk 0 is
// written; one that only works through a native decryptor fails AES-CTR validation and is never written, so a saved key
// always works on its own; an MP3 or AAC cannot be verified, so only an AP key is written for those.
//
// THE FILE. `Cache\audiokeys.db` (0.2.9's path, beside `Cache\audio`): sqlite in WAL mode, `meta` + `audio_key`. A row is
// 46 bytes — AES-GCM under a random data key, itself DPAPI-wrapped in `meta`, with the account scope and the file id as
// AAD — so a flipped byte, or a row moved to another file or another account, fails its tag and reads as a miss. Key
// material is never logged; file ids already appear in every `audio.*` line.
//
// THREADING. `KeyGate` guards the session map. `DbGate` guards the ONE connection and is held for about 50 µs per lookup.
// Writes go through a bounded queue (256, dropped when full and counted) to the `Wavee.AudioKeys` thread at BelowNormal,
// which commits up to 64 operations per transaction: the fetch task only ever ENQUEUES, it never waits on sqlite. The file
// is opened by the first lookup or by the writer's `Warm` at the AP welcome — `Use` records the path and opens nothing, so
// boot costs zero and `--fake` / headless stay detached.
//
// FAILURE. WAL + NORMAL sync: a crash loses at most the queued keys, which are fetched again. A file that cannot be kept at
// open is renamed aside and recreated (`Store.Delete`; dropped in place when another process holds it); mid-session the first
// fault that says the file is damaged rebuilds it, a second one in the same session turns the store off for the session
// (`StoreHealth`), and every other failure is counted. Always-on lines: `audio.keys.open`, `audio.keys.fault`,
// `audio.keys.clear`, `audio.keys.stale`, `audio.keys.stats`.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Audio
    {
        // ── 1. the session map (moved from Spotify.Audio.cs §5) ──────────────────────────────────────────────────────

        const int KeyCacheMax = 256;
        static readonly Lock KeyGate = new();
        static readonly Dictionary<string, KeyEntry> KeyCache = new(KeyCacheMax, StringComparer.OrdinalIgnoreCase);
        static readonly Queue<string> KeyOrder = new(KeyCacheMax);

        /// <summary>One key this session holds, with where it came from and whether it is already on disk.</summary>
        sealed class KeyEntry(byte[] fileId, byte[] key, KeyOrigin origin)
        {
            public readonly byte[] FileId = fileId, Key = key;
            public readonly KeyOrigin Origin = origin;
            public volatile bool Persisted = origin == KeyOrigin.Disk;
        }

        /// <summary>Public, not internal: this assembly has no <c>InternalsVisibleTo</c>, so the key store's tests drive the
        /// session map through it.</summary>
        public static void Remember(string fileIdHex, ReadOnlySpan<byte> fileId, ReadOnlySpan<byte> key16, KeyOrigin origin)
        {
            var entry = new KeyEntry(fileId.ToArray(), key16.ToArray(), origin);
            lock (KeyGate)
            {
                if (!KeyCache.ContainsKey(fileIdHex)) KeyOrder.Enqueue(fileIdHex);
                KeyCache[fileIdHex] = entry;
                while (KeyCache.Count > KeyCacheMax && KeyOrder.TryDequeue(out string? oldest)) KeyCache.Remove(oldest);
            }
        }

        static void ForgetSessionKeys() { lock (KeyGate) { KeyCache.Clear(); KeyOrder.Clear(); } }

        /// <summary>For `audio.head`'s keySrc= and `audio.key`'s origin=: ap | derived | disk | "-".</summary>
        public static string KeyOriginText(string fileIdHex)
        {
            lock (KeyGate)
                return !KeyCache.TryGetValue(fileIdHex, out KeyEntry? e) ? "-"
                     : e.Origin switch { KeyOrigin.Ap => "ap", KeyOrigin.Derived => "derived", _ => "disk" };
        }

        // ── 2. the proof ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Chunk 0's CIPHERTEXT has been seen with <paramref name="key16"/>: persist, keep, skip or forget the key
        /// (<see cref="KeyStoreRules.OnProof"/>). Once per body. Never blocks: a persist is an enqueue. Fewer than
        /// <see cref="KeyStoreRules.ProofBytes"/> bytes prove nothing and answer Keep.</summary>
        public static KeyProofVerdict KeyProof(string fileIdHex, Format fmt, ReadOnlySpan<byte> cipher, ReadOnlySpan<byte> key16,
            out bool validates)
        {
            validates = false;
            if (!KeyStoreRules.CanProve(cipher.Length)) return KeyProofVerdict.Keep;
            KeyEntry? e;
            lock (KeyGate) KeyCache.TryGetValue(fileIdHex, out e);
            validates = Ctr.Validates(cipher, key16);
            if (e is null || !e.Key.AsSpan().SequenceEqual(key16)) return KeyProofVerdict.Keep;   // cleared, evicted or not this key
            KeyProofVerdict v = KeyStoreRules.OnProof(e.Origin, e.Persisted, KeyStoreRules.Recognizable(fmt), validates);
            if (v == KeyProofVerdict.Persist) { e.Persisted = true; KeyStore.Put(e.FileId, e.Key, e.Origin); }
            else if (v == KeyProofVerdict.Forget)
            {
                lock (KeyGate) KeyCache.Remove(fileIdHex);
                KeyStore.Forget(e.FileId);
                Log.Warn("audio", "audio.keys.stale file=" + fileIdHex + " (a stored key did not open chunk 0; forgotten)");
            }
            return v;
        }

        // ── 3. the store ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The saved keys: `Cache\audiokeys.db`, per account, authenticated per row. Static like the audio
        /// caches beside it; a test points it at a temp folder with <see cref="Use"/> and an injected setting.</summary>
        public static class KeyStore
        {
            /// <summary>The counters behind the `audio.keys.stats` line. <c>Outcome</c> is how the last open ended:
            /// created | opened | recreated:&lt;why&gt; | memory-only:&lt;why&gt;, or "" before any open.</summary>
            public readonly record struct Stats(KeyStorePhase Phase, long Hits, long Misses, long Writes, long Forgotten,
                long BadRecords, long Dropped, int Queued, string Outcome);

            enum OpKind : byte { Put, Touch, Forget, Warm, Barrier }

            readonly record struct Op(OpKind Kind, int Generation, string Account, byte[]? FileId, byte[]? Key = null,
                KeyOrigin Origin = default, long Now = 0, ManualResetEventSlim? Done = null);

            const int QueueCapacity = 256, MaxBatch = 64, DekBytes = 32;
            const string Pragmas = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=2000;";
            const string Ddl =
                "CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value) WITHOUT ROWID; "
                + "CREATE TABLE IF NOT EXISTS audio_key(scope BLOB NOT NULL, file_id BLOB NOT NULL, sealed BLOB NOT NULL, "
                + "last_used INTEGER NOT NULL, PRIMARY KEY(scope, file_id)) WITHOUT ROWID;";

            static readonly Lock DbGate = new();
            static readonly Lock QueueGate = new();
            static readonly Lock ForgetGate = new();
            static string? s_path;
            static ICredentialProtector s_protector = new NoOpProtector();
            static Func<bool> s_enabled = static () => false;
            static Func<long> s_now = static () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            static int s_max = KeyStoreRules.MaxKeys;
            static volatile KeyStorePhase s_phase;                 // Detached
            static string? s_account;                              // volatile; set by the welcome
            static int s_generation;
            static bool s_recovered;                               // DbGate
            static string s_outcome = "";                          // DbGate
            static SqliteConnection? s_db; static AesGcm? s_gcm; static byte[]? s_dek;   // DbGate
            static string? s_scopeFor; static readonly byte[] s_scope = new byte[KeyStoreRules.ScopeBytes];   // DbGate
            static BlockingCollection<Op>? s_queue; static Thread? s_writer;               // QueueGate
            static readonly HashSet<string> s_forgetting = new(StringComparer.Ordinal);    // ForgetGate
            static int s_forgettingCount;
            static long s_hits, s_misses, s_writes, s_forgotten, s_bad, s_dropped;

            public static string DefaultPath() => KeyStoreRules.PathUnder(Platform.LocalFolder);

            /// <summary>App.Main (after the instance gate, never under --fake) or a test. Records the path; opens NOTHING.
            /// <paramref name="maxKeys"/> and <paramref name="nowUnix"/> are test seams (the cap, the clock).</summary>
            public static void Use(string? path, ICredentialProtector protector, Func<bool> enabled,
                int maxKeys = KeyStoreRules.MaxKeys, Func<long>? nowUnix = null)
            {
                Shutdown();
                lock (DbGate)
                {
                    s_path = string.IsNullOrEmpty(path) ? null : path;
                    s_protector = protector; s_enabled = enabled; s_recovered = false;
                    s_max = maxKeys; s_now = nowUnix ?? (static () => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    s_outcome = "";
                    s_hits = s_misses = s_writes = s_forgotten = s_bad = s_dropped = 0;
                    s_phase = s_path is null ? KeyStorePhase.Detached : enabled() ? KeyStorePhase.Idle : KeyStorePhase.Off;
                }
            }

            /// <summary>UI thread (the welcome effect): the account rows are scoped to; pre-opens the file off-thread.</summary>
            public static void UseAccount(string account)
            {
                Volatile.Write(ref s_account, account.Length == 0 ? null : account);
                if (account.Length > 0) Enqueue(new Op(OpKind.Warm, Volatile.Read(ref s_generation), account, null));
            }

            /// <summary>A point lookup on the caller's thread (an api thread or the pump — never the UI thread). False when
            /// the store is off, detached, unscoped or broken, and for a key that is being forgotten.</summary>
            public static bool TryGet(ReadOnlySpan<byte> fileId, Span<byte> key16)
            {
                if (s_phase == KeyStorePhase.Detached || Volatile.Read(ref s_account) is not { } account) return false;
                if (fileId.Length is 0 or > KeyRecord.MaxFileId || IsForgetting(fileId)) return false;
                lock (DbGate)
                {
                    if (!ServesLocked()) return false;
                    try
                    {
                        ScopeLocked(account);
                        using SqliteCommand cmd = s_db!.CreateCommand();
                        cmd.CommandText = "SELECT sealed, last_used FROM audio_key WHERE scope=$s AND file_id=$f;";
                        cmd.Parameters.AddWithValue("$s", s_scope);
                        cmd.Parameters.AddWithValue("$f", fileId.ToArray());
                        using SqliteDataReader r = cmd.ExecuteReader();
                        if (!r.Read()) { s_misses++; return false; }
                        byte[] row = r.GetFieldValue<byte[]>(0);
                        long lastUsed = r.GetInt64(1);
                        if (!KeyRecord.TryOpen(s_gcm!, row, s_scope, fileId, key16, out _))
                        {
                            s_bad++;
                            Enqueue(new Op(OpKind.Forget, Volatile.Read(ref s_generation), account, fileId.ToArray()));
                            return false;
                        }
                        long now = s_now();
                        if (KeyStoreRules.ShouldTouch(lastUsed, now))
                            Enqueue(new Op(OpKind.Touch, Volatile.Read(ref s_generation), account, fileId.ToArray(), Now: now));
                        s_hits++;
                        return true;
                    }
                    catch (SqliteException ex) { FaultLocked("read", ex); return false; }
                }
            }

            /// <summary>A proven key, to be written (the proof, <see cref="KeyProof"/>, is the only caller). An enqueue.</summary>
            internal static void Put(byte[] fileId, byte[] key16, KeyOrigin origin)
            {
                if (fileId.Length is 0 or > KeyRecord.MaxFileId || key16.Length != AudioKey.KeyLength) return;
                if (Volatile.Read(ref s_account) is { } a && s_enabled())
                    Enqueue(new Op(OpKind.Put, Volatile.Read(ref s_generation), a, fileId, key16, origin, s_now()));
            }

            /// <summary>A saved key that did not open its own file. Reads miss it at once (the delete itself is queued).</summary>
            internal static void Forget(byte[] fileId)
            {
                if (Volatile.Read(ref s_account) is not { } a || fileId.Length is 0 or > KeyRecord.MaxFileId) return;
                string hex = Convert.ToHexString(fileId);
                lock (ForgetGate) { s_forgetting.Add(hex); Volatile.Write(ref s_forgettingCount, s_forgetting.Count); }
                if (!Enqueue(new Op(OpKind.Forget, Volatile.Read(ref s_generation), a, fileId))) Unforget(hex);
            }

            static bool IsForgetting(ReadOnlySpan<byte> fileId)
            {
                if (Volatile.Read(ref s_forgettingCount) == 0) return false;
                string hex = Convert.ToHexString(fileId);
                lock (ForgetGate) return s_forgetting.Contains(hex);
            }

            static void Unforget(string hex)
            {
                lock (ForgetGate) { s_forgetting.Remove(hex); Volatile.Write(ref s_forgettingCount, s_forgetting.Count); }
            }

            /// <summary>The setting, read per call, folded into the phase; an Idle store opens here. Under DbGate.</summary>
            static bool ServesLocked()
            {
                bool on = s_enabled();
                if (!on && s_phase is not (KeyStorePhase.Off or KeyStorePhase.Detached))
                {
                    Move(KeyStoreSignal.SettingOff); CloseLocked(); Interlocked.Increment(ref s_generation);
                    Log.Info("audio", "audio.keys.off (setting): no reads, no writes; the file is kept until cleared");
                }
                else if (on && s_phase == KeyStorePhase.Off) Move(KeyStoreSignal.SettingOn);
                if (s_phase == KeyStorePhase.Idle) OpenLocked();
                return s_phase == KeyStorePhase.Open;
            }

            static void Move(KeyStoreSignal signal) => s_phase = KeyStoreRules.Next(s_phase, signal);

            // ── the writer ───────────────────────────────────────────────────────────────────────────────────────────

            /// <summary>Queue one operation; false when it was not queued (detached, off, broken, full, shutting down). Starts
            /// the writer thread on first use and never blocks.</summary>
            static bool Enqueue(in Op op)
            {
                if (s_phase is KeyStorePhase.Detached or KeyStorePhase.Off or KeyStorePhase.Broken) return false;
                BlockingCollection<Op>? queue;
                lock (QueueGate)
                {
                    queue = s_queue;
                    if (queue is null)
                    {
                        BlockingCollection<Op> q = queue = new BlockingCollection<Op>(QueueCapacity);
                        var thread = new Thread(() => WriterLoop(q))
                            { IsBackground = true, Name = "Wavee.AudioKeys", Priority = ThreadPriority.BelowNormal };
                        s_queue = q; s_writer = thread;
                        thread.Start();
                    }
                }
                try
                {
                    if (queue.TryAdd(op)) return true;
                    Interlocked.Increment(ref s_dropped);               // full: the key is fetched again next time
                    return false;
                }
                // CompleteAdding raced in (Shutdown), or the collection was disposed — ObjectDisposedException derives
                // from InvalidOperationException, so the one clause covers both.
                catch (InvalidOperationException) { return false; }
            }

            /// <summary>One operation, then up to 63 more, in one transaction, under DbGate; each <c>Done</c> is set when its
            /// batch is over.</summary>
            static void WriterLoop(BlockingCollection<Op> queue)
            {
                var batch = new List<Op>(MaxBatch);
                foreach (Op first in queue.GetConsumingEnumerable())
                {
                    batch.Add(first);
                    while (batch.Count < MaxBatch && queue.TryTake(out Op next)) batch.Add(next);
                    try { lock (DbGate) RunBatchLocked(batch); }
                    catch (Exception ex) { Log.Warn("audio", "audio.keys.write faulted (" + ex.GetType().Name + ")"); }   // a writer must not take the process down
                    finally
                    {
                        foreach (Op op in batch)
                        {
                            if (op.Kind == OpKind.Forget && op.FileId is not null) Unforget(Convert.ToHexString(op.FileId));
                            op.Done?.Set();
                        }
                        batch.Clear();
                    }
                }
            }

            static void RunBatchLocked(List<Op> batch)
            {
                int gen = Volatile.Read(ref s_generation);
                bool live = false;
                foreach (Op op in batch)
                    if (op.Kind != OpKind.Barrier && op.Generation == gen) { live = true; break; }
                if (!live || !ServesLocked()) return;                    // a Clear or a toggle-off dropped them; off / broken drops too
                try
                {
                    using SqliteTransaction tx = s_db!.BeginTransaction();
                    int puts = 0;
                    Span<byte> nonce = stackalloc byte[KeyRecord.NonceBytes];
                    foreach (Op op in batch)
                    {
                        if (op.Generation != gen || op.FileId is null) continue;     // queued before a Clear / a toggle-off, or a Warm / Barrier
                        ScopeLocked(op.Account);
                        using SqliteCommand cmd = s_db.CreateCommand();
                        cmd.Transaction = tx;
                        cmd.Parameters.AddWithValue("$s", s_scope);
                        cmd.Parameters.AddWithValue("$f", op.FileId);
                        switch (op.Kind)
                        {
                            case OpKind.Put:
                                var sealedRow = new byte[KeyRecord.SealedBytes];
                                RandomNumberGenerator.Fill(nonce);
                                KeyRecord.Seal(s_gcm!, nonce, s_scope, op.FileId, op.Origin, op.Key!, sealedRow);
                                cmd.CommandText = "INSERT OR REPLACE INTO audio_key(scope,file_id,sealed,last_used) VALUES($s,$f,$b,$t);";
                                cmd.Parameters.AddWithValue("$b", sealedRow);
                                cmd.Parameters.AddWithValue("$t", op.Now);
                                puts++;
                                break;
                            case OpKind.Touch:
                                cmd.CommandText = "UPDATE audio_key SET last_used=$t WHERE scope=$s AND file_id=$f;";
                                cmd.Parameters.AddWithValue("$t", op.Now);
                                break;
                            case OpKind.Forget:
                                cmd.CommandText = "DELETE FROM audio_key WHERE scope=$s AND file_id=$f;";
                                s_forgotten++;
                                break;
                            default:
                                continue;
                        }
                        cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                    s_writes += puts;
                    if (puts > 0) TrimLocked();
                }
                catch (SqliteException ex) { FaultLocked("write", ex); }
            }

            /// <summary>Above the cap, drop the oldest by last_used down to 90 % (<see cref="KeyStoreRules.EvictCount"/>).</summary>
            static void TrimLocked()
            {
                int drop = KeyStoreRules.EvictCount(Scalar("SELECT count(*) FROM audio_key;"), s_max);
                if (drop > 0)
                    Exec($"DELETE FROM audio_key WHERE (scope,file_id) IN (SELECT scope,file_id FROM audio_key ORDER BY last_used LIMIT {drop});");
            }

            /// <summary>The scope bytes for <paramref name="account"/>, memoised for the last account.</summary>
            static void ScopeLocked(string account)
            {
                if (string.Equals(s_scopeFor, account, StringComparison.Ordinal)) return;
                KeyRecord.Scope(s_dek!, account, s_scope);
                s_scopeFor = account;
            }

            // ── open, fault, clear ───────────────────────────────────────────────────────────────────────────────────

            static void OpenLocked()
            {
                long t0 = Stopwatch.GetTimestamp();
                string path = s_path!;
                SqliteConnection? c = null;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    ReapLeftovers(path);                                 // KeyStoreRules.IsLeftover → Store.TryDelete
                    c = Connect(path);
                    c.Open();
                    string? why = Inspect(c, out bool created, out byte[]? dek);
                    if (why is not null) { c.Dispose(); c = Recreate(path); dek = null; }
                    if (dek is null) dek = StampNewDek(c);               // DDL + meta(schema, dek_scheme, dek = Protect(32 random))
                    else Exec(c, Ddl);
                    s_db = c; s_dek = dek; s_gcm = new AesGcm(dek, KeyRecord.TagBytes); s_scopeFor = null;
                    Move(KeyStoreSignal.Opened);
                    LogOpen(why is null ? (created ? "created" : "opened") : "recreated:" + why, t0, warn: why is not null);
                }
                catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or CryptographicException)
                {
                    try { c?.Dispose(); } catch (Exception) { }
                    CloseLocked();
                    Move(KeyStoreSignal.OpenFailed);
                    LogOpen("memory-only:" + ex.GetType().Name, t0, warn: true);
                }
            }

            static SqliteConnection Connect(string path)
                => new(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());

            /// <summary>Pragmas, the file's facts and the data key's unwrap, folded through
            /// <see cref="KeyStoreRules.RecreateWhy"/>: the reason the file cannot be kept, or null. The pragmas are the first
            /// statements that touch the file, so a damaged one throws here, as a verdict.</summary>
            static string? Inspect(SqliteConnection c, out bool created, out byte[]? dek)
            {
                created = false; dek = null;
                int code = 0;
                bool hasMeta = false, hasTables = false;
                string? schema = null, scheme = null;
                byte[]? wrapped = null;
                try
                {
                    Exec(c, Pragmas);
                    using (SqliteCommand cmd = c.CreateCommand())
                    {
                        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
                        using SqliteDataReader r = cmd.ExecuteReader();
                        while (r.Read()) { if (r.GetString(0) == "meta") hasMeta = true; else hasTables = true; }
                    }
                    if (hasMeta)
                    {
                        try
                        {
                            using SqliteCommand cmd = c.CreateCommand();
                            cmd.CommandText = "SELECT key, value FROM meta;";
                            using SqliteDataReader r = cmd.ExecuteReader();
                            while (r.Read())
                            {
                                object v = r.GetValue(1);
                                switch (r.GetString(0))
                                {
                                    case "schema": schema = v as string; break;
                                    case "dek_scheme": scheme = v as string; break;
                                    case "dek": wrapped = v as byte[]; break;
                                }
                            }
                        }
                        catch (SqliteException ex) when (ex.SqliteErrorCode == 1) { }   // a `meta` with other columns: not ours, schema stays null
                    }
                }
                catch (SqliteException ex) when (Store.IsUnreadableFile(ex.SqliteErrorCode & 0xFF))
                {
                    code = ex.SqliteErrorCode & 0xFF;
                }
                bool dekOk = TryUnwrap(scheme, wrapped, out byte[]? unwrapped);
                string? why = KeyStoreRules.RecreateWhy(code, hasMeta, hasTables, schema, dekOk);
                if (why is null) { created = !hasMeta; dek = hasMeta ? unwrapped : null; }
                return why;
            }

            /// <summary>The stored data key, when this protector wrote it and can still unwrap it (a copied profile or a reset
            /// DPAPI master key cannot: that file is recreated as <c>dek</c>).</summary>
            static bool TryUnwrap(string? scheme, byte[]? wrapped, out byte[]? dek)
            {
                dek = null;
                if (wrapped is null || !string.Equals(scheme, s_protector.Scheme, StringComparison.Ordinal)) return false;
                try
                {
                    byte[] plain = s_protector.Unprotect(wrapped);
                    if (plain.Length != DekBytes) return false;
                    dek = plain;
                    return true;
                }
                catch (CryptographicException) { return false; }
            }

            /// <summary>The schema, and a fresh random data key wrapped by the protector. Returns the unwrapped key.</summary>
            static byte[] StampNewDek(SqliteConnection c)
            {
                Exec(c, Ddl);
                byte[] dek = RandomNumberGenerator.GetBytes(DekBytes);
                byte[] wrapped = s_protector.Protect(dek);
                using SqliteTransaction tx = c.BeginTransaction();
                SetMeta(c, tx, "schema", KeyStoreRules.Schema);
                SetMeta(c, tx, "dek_scheme", s_protector.Scheme);
                SetMeta(c, tx, "dek", wrapped);
                tx.Commit();
                return dek;
            }

            static void SetMeta(SqliteConnection conn, SqliteTransaction trans, string name, object value)
            {
                using SqliteCommand cmd = conn.CreateCommand();
                cmd.Transaction = trans;
                cmd.CommandText = "INSERT OR REPLACE INTO meta(key,value) VALUES($k,$v);";
                cmd.Parameters.AddWithValue("$k", name);
                cmd.Parameters.AddWithValue("$v", value);
                cmd.ExecuteNonQuery();
            }

            /// <summary>The set moved aside (<see cref="Store"/>'s all-or-nothing delete) and a fresh file created at the path;
            /// when another process holds it, every table is dropped in place instead. Throws when even that fails.</summary>
            static SqliteConnection Recreate(string path)
            {
                bool gone = Store.Delete(path);
                SqliteConnection fresh = Connect(path);
                try
                {
                    fresh.Open();
                    Exec(fresh, Pragmas);
                    if (!gone) DropEverything(fresh);
                    return fresh;
                }
                catch
                {
                    fresh.Dispose();
                    throw;
                }
            }

            static void DropEverything(SqliteConnection c)
            {
                var tables = new List<string>(4);
                using (SqliteCommand cmd = c.CreateCommand())
                {
                    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
                    using SqliteDataReader r = cmd.ExecuteReader();
                    while (r.Read()) tables.Add(r.GetString(0));
                }
                foreach (string table in tables)
                    Exec(c, "DROP TABLE IF EXISTS \"" + table.Replace("\"", "\"\"") + "\";");
            }

            /// <summary><c>.dead-*</c> members of THIS set that an earlier delete renamed aside and could not remove.</summary>
            static void ReapLeftovers(string path)
            {
                string dir = Path.GetDirectoryName(path)!;
                foreach (string file in Directory.GetFiles(dir, KeyStoreRules.FileName + "*"))
                    if (KeyStoreRules.IsLeftover(Path.GetFileName(file))) Store.TryDelete(file);
            }

            static void LogOpen(string outcome, long t0, bool warn)
            {
                s_outcome = outcome;
                long keys = -1;
                try { if (s_db is not null) keys = Scalar("SELECT count(*) FROM audio_key;"); } catch (SqliteException) { }
                Log.Event(warn ? WaveeLogLevel.Warning : WaveeLogLevel.Info, "audio", "audio.keys.open", "", null, -1, null,
                    WaveeLogField.Of("file", KeyStoreRules.FileName), WaveeLogField.Of("outcome", outcome),
                    WaveeLogField.Of("keys", keys), WaveeLogField.Of("ms", (long)Stopwatch.GetElapsedTime(t0).TotalMilliseconds));
            }

            static void CloseLocked()
            {
                s_gcm?.Dispose(); s_gcm = null;
                if (s_dek is { } dek) CryptographicOperations.ZeroMemory(dek);
                s_dek = null; s_scopeFor = null;
                SqliteConnection? db = s_db;
                s_db = null;
                try { db?.Dispose(); } catch (SqliteException) { }       // Pooling=False: this really releases the file handle
            }

            static void FaultLocked(string step, SqliteException ex)
            {
                int code = ex.SqliteErrorCode & 0xFF;
                bool spent = StoreHealth.RecoverySpent(code, s_recovered);
                bool recover = StoreHealth.OnFault(code, s_recovered) == StoreFaultVerdict.Recover;
                Log.Event(WaveeLogLevel.Warning, "audio", "audio.keys.fault", "", null, -1, null,
                    WaveeLogField.Of("step", step), WaveeLogField.Of("code", code),
                    WaveeLogField.Of("verdict", recover ? "rebuild" : spent ? "memory-only" : "count"));
                if (recover) { s_recovered = true; CloseLocked(); Store.Delete(s_path!); Move(KeyStoreSignal.Corrupt); }
                else if (spent) { CloseLocked(); Move(KeyStoreSignal.CorruptAgain); }
            }

            /// <summary>Settings › Clear saved keys (off the UI thread). Safe while playing: a Body holds its own key copy.
            /// Bumps the generation (queued writes are dropped), empties the session map (a cleared key cannot be written back
            /// from memory), then deletes the file set under DbGate — or drops its tables and VACUUMs when another process
            /// holds it — and goes Idle: the next use gets a fresh file and a fresh data key.</summary>
            public static void Clear()
            {
                Interlocked.Increment(ref s_generation);
                ForgetSessionKeys();
                lock (ForgetGate) { s_forgetting.Clear(); Volatile.Write(ref s_forgettingCount, 0); }
                bool deleted, emptied = true;
                lock (DbGate)
                {
                    if (s_phase == KeyStorePhase.Detached || s_path is null) return;
                    CloseLocked();
                    deleted = Store.Delete(s_path);
                    if (!deleted) emptied = EmptyInPlace(s_path);
                    if (emptied) { s_recovered = false; Move(KeyStoreSignal.Cleared); }
                    else if (s_phase != KeyStorePhase.Off) s_phase = KeyStorePhase.Broken;   // neither deleted nor emptied: memory-only for the session, never a stale yes
                }
                Log.Event(deleted || emptied ? WaveeLogLevel.Info : WaveeLogLevel.Warning, "audio", "audio.keys.clear", "", null, -1, null,
                    WaveeLogField.Of("deleted", deleted), WaveeLogField.Of("emptied", emptied));
            }

            static bool EmptyInPlace(string path)
            {
                try
                {
                    using SqliteConnection c = Connect(path);
                    c.Open();
                    Exec(c, Pragmas);
                    DropEverything(c);
                    Exec(c, "VACUUM;");
                    return true;
                }
                catch (SqliteException) { return false; }
            }

            // ── the doors ────────────────────────────────────────────────────────────────────────────────────────────

            /// <summary>Settings' LicenseKeyCount seam; null when off, detached or broken (the row then shows its size only).
            /// Counts every account's keys, as Clear removes every account's keys. Never creates the file just to count it.</summary>
            public static int? Count()
            {
                lock (DbGate)
                {
                    if (s_phase == KeyStorePhase.Idle && s_enabled() && !File.Exists(s_path!)) return 0;
                    if (!ServesLocked()) return null;
                    try { return (int)Scalar("SELECT count(*) FROM audio_key;"); }
                    catch (SqliteException ex) { FaultLocked("count", ex); return null; }
                }
            }

            /// <summary>The whole WAL set's bytes (the census used to count the main file only).</summary>
            public static long DiskBytes()
            {
                string p = s_path ?? DefaultPath();
                long total = 0;
                foreach (string suffix in KeyStoreRules.MemberSuffixes) total += LengthOf(p + suffix);
                return total;
            }

            static long LengthOf(string file)
            {
                try { var info = new FileInfo(file); return info.Exists ? info.Length : 0; }
                catch (IOException) { return 0; }
                catch (UnauthorizedAccessException) { return 0; }
            }

            /// <summary>Drain the queue (a test, <see cref="Shutdown"/>'s sibling): enqueues a barrier and waits for it, so
            /// every operation queued before this call has been committed — or dropped — when it returns.</summary>
            public static void Flush(int timeoutMs)
            {
                using var done = new ManualResetEventSlim(false);
                if (Enqueue(new Op(OpKind.Barrier, Volatile.Read(ref s_generation), "", null, Done: done))) done.Wait(timeoutMs);
            }

            /// <summary>App exit tail, after Playback.Audio.Shutdown: drain the queue, checkpoint(TRUNCATE), close, one
            /// <c>audio.keys.stats</c> line, and go Detached (a later <see cref="Use"/> starts over).</summary>
            public static void Shutdown()
            {
                BlockingCollection<Op>? queue;
                Thread? writer;
                lock (QueueGate) { queue = s_queue; writer = s_writer; s_queue = null; s_writer = null; }
                if (queue is not null)
                {
                    try { queue.CompleteAdding(); } catch (ObjectDisposedException) { }
                    if (writer is not null && writer != Thread.CurrentThread && writer.Join(TimeSpan.FromSeconds(5))) queue.Dispose();
                }
                Volatile.Write(ref s_account, null);
                lock (ForgetGate) { s_forgetting.Clear(); Volatile.Write(ref s_forgettingCount, 0); }
                Stats last;
                bool wasAttached;
                lock (DbGate)
                {
                    wasAttached = s_phase != KeyStorePhase.Detached;
                    if (s_db is not null)
                        try { Exec("PRAGMA wal_checkpoint(TRUNCATE);"); } catch (SqliteException) { }
                    CloseLocked();
                    Move(KeyStoreSignal.Shutdown);
                    last = Read();
                }
                if (wasAttached)
                    Log.Event(WaveeLogLevel.Info, "audio", "audio.keys.stats", "", null, -1, null,
                        WaveeLogField.Of("hits", last.Hits), WaveeLogField.Of("misses", last.Misses), WaveeLogField.Of("writes", last.Writes),
                        WaveeLogField.Of("forgotten", last.Forgotten), WaveeLogField.Of("bad", last.BadRecords),
                        WaveeLogField.Of("dropped", last.Dropped));
            }

            public static Stats Read()
            {
                BlockingCollection<Op>? q = s_queue;
                return new Stats(s_phase, Interlocked.Read(ref s_hits), Interlocked.Read(ref s_misses), Interlocked.Read(ref s_writes),
                    Interlocked.Read(ref s_forgotten), Interlocked.Read(ref s_bad), Interlocked.Read(ref s_dropped), q?.Count ?? 0, s_outcome);
            }

            static void Exec(SqliteConnection c, string sql)
            {
                using SqliteCommand cmd = c.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }

            static void Exec(string sql) => Exec(s_db!, sql);

            static long Scalar(string sql)
            {
                using SqliteCommand cmd = s_db!.CreateCommand();
                cmd.CommandText = sql;
                return cmd.ExecuteScalar() is long v ? v : 0;
            }
        }
    }
}
