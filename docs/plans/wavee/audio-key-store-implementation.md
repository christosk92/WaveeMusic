<!-- Owner decision 2026-10-02: "port yes but improve where possible". Designed by an Opus pass; implemented by one Sonnet agent. -->
# Persistent audio key store: implementation spec

## 0. Findings

- **0.2.9** (`git show 99e398d7^:src/apps/_old/Wavee/SpotifyLive/Audio/LicenseKeyDiskCache.cs`) kept **PlayPlay license payloads**, not AES keys. The table was `audio_license(fileId TEXT, blob, saved_at)` and each record was DPAPI-wrapped and base64'd (about 250 B per record). It had a 30-day TTL and a 4096-row FIFO cap, with no corruption handling. A disk hit still had to run the native derivation. That design is PlayPlay-shaped and can't come into this repo as it was.
- **0.3 today:**
  - `Spotify.Audio.Key` (`Spotify.Audio.cs:761`) checks a 256-entry FIFO in memory, then the AP, then the `KeyDeriver` seam.
  - `ResetKeyLatch` clears that map on every AP welcome (G-034).
  - `OpenBody` (`Stream.cs:2084`) asks for the key in parallel with the head and the resolve. `Prefetch(in FileChoice)` (`:2197`) already asks for the next track's key.
  - `Body.Publish` (`:1703`) already runs `Ctr.Validates` on chunk 0's **ciphertext**, but only to write a log line.
  - The private host can leave a **native body decryptor** per file (`PlayPlayHost.DecryptorFor` uses an in-memory seed). So a derived key is not always enough to decrypt the file on its own.
- **Settings is already partly wired:**
  - `Settings.LicenseKeyCount` / `Settings.ClearLicenseKeys` are declared but never assigned (G-272, D9).
  - `ClearSavedKeys` calls `File.Delete(LicenseDbPath())`, which would corrupt a WAL database that is open.
  - The census counts only the main file.
  - The en-US loc already has `licenseKeysCount` / `licenseKeysCountOne`.
- **Reusable pieces:** `Store.Delete` (all-or-nothing rename aside, `internal`), `Store.TryDelete`, `Store.IsUnreadableFile`, `StoreHealth.OnFault` / `RecoverySpent`, `DpapiProtector` / `NoOpProtector`, `StoreFiles.DeadTag`.

## 1. Decisions

**Location:** `KeyStoreRules.PathUnder(Platform.LocalFolder)` = `%LOCALAPPDATA%\Wavee\Wavee\Cache\audiokeys.db`. This is 0.2.9's path, it sits beside `Cache\audio`, and it's what the UI already says. An old 0.2.9 file has no `meta` table, so it is detected as `foreign` and recreated, which is logged. Its license payloads are useless without the deriver.

**What is stored:** the final 16-byte AES key, from either the AP or the deriver. The store doesn't know about PlayPlay.

**Encryption at rest: a DPAPI-wrapped data key plus AES-GCM per record.** Chosen over DPAPI per record because:
- There is one `ProtectedData.Unprotect` per process, instead of an LSA round trip on every lookup and write.
- A record is 46 B (version 1, nonce 12, tag 16, ciphertext 17) instead of about 250 B.
- Records are **authenticated and bound** through AAD = `version | scope | fileId`. A corrupted byte, or a row moved to another file id or account, fails its tag and reads as a miss, and the row is deleted.
- "Clear saved keys" deletes the file, so the next open mints a new data key (rotation).

`meta` holds `schema`, `dek_scheme` (the protector's scheme) and `dek`. If the scheme doesn't match or DPAPI can't unwrap (profile copied, user's master key reset), the file is recreated as `dek`. Key material is never logged; file ids already appear in every `audio.*` line and in the body cache's file names.

**Scope: per account.** A file's key is the same for every account. But since the session map is cleared on welcome so keys never outlive the login that fetched them (G-034), a cross-session store should keep that promise too. It also avoids one account replaying a cached rung that only another account was entitled to.
- The scope is `HMAC-SHA256(dek, "wavee.audiokeys.account\0" + username)[..8]`, keyed so the username can't be recovered from the file.
- Logout keeps the keys (the body cache is kept too).
- "Clear saved keys" removes every account's keys.

**Schema:**
```sql
CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS audio_key(scope BLOB NOT NULL, file_id BLOB NOT NULL, sealed BLOB NOT NULL,
  last_used INTEGER NOT NULL, PRIMARY KEY(scope, file_id)) WITHOUT ROWID;
```
- There is no `first_seen` column and no `last_used` index. Nothing decides on first-seen, and the trim is rare, so a one-off sort is fine.
- Pragmas: `journal_mode=WAL; synchronous=NORMAL; busy_timeout=2000`.
- Connection string uses `Pooling=False`, so `Close` really releases the file handle before `Store.Delete`.

**Lookup order:** memory (the existing 256-entry map) → disk (a point lookup on the caller's thread, which may block) → AP → deriver.
- A disk hit works even while the AP is reconnecting.
- Pre-warming the next track needs no new code: `Prefetch` → `Key()` → disk.

**Write path:** keys are persisted **only after proof**. The first time a body sees chunk 0's ciphertext, `Ctr.Validates` decides:
- A key that validates is persisted.
- A key that only works through a native decryptor fails AES-CTR validation and is never stored, so a stored key always works on its own.
- MP3 and AAC can't be verified. For those, only an AP key is persisted.

**Concurrency:**
- `KeyGate` guards the session map.
- `DbGate` guards the one connection and is held for about 50 µs per lookup.
- Writes go through a bounded queue (256, drop when full and count it) to a `Wavee.AudioKeys` thread at BelowNormal priority. It commits up to 64 operations per transaction.
- The fetch task only ever enqueues; it never waits on SQLite.

**Startup:** `Use()` only records the path. The file is opened by a `Warm` operation on the writer thread when the AP welcome arrives, or lazily on the first lookup. Boot cost is zero, and `--fake` / headless stay detached.

**Crash safety:** WAL with NORMAL sync. A crash can lose keys still in the queue, which just get fetched again. Shutdown drains the queue and runs `wal_checkpoint(TRUNCATE)`.

**Corruption:**
- At open: `RecreateWhy` returns `unreadable:11/26`, `foreign`, `stale` or `dek`. The file set is renamed aside and deleted (`Store.Delete`); if another process holds it, the tables are dropped in place. Leftover `.dead-*` files are cleaned at the next open.
- Mid-session: the first fault that says the file is damaged rebuilds it, a second one in the same session switches the store off for the session (memory only) via `StoreHealth`, and every other failure is only counted.
- All of it is logged as `audio.keys.open` / `audio.keys.fault`.

**Eviction:** above `MaxKeys = 20_000` rows (about 3 MB, more than a 128 GiB body cache holds), trim to 90% by `last_used`. `last_used` is updated at most once a day per key. There is no time-based expiry: keys are immutable, and 0.2.9's 30-day TTL only forced the key to be fetched again.

**Body-cache clear does not prune keys.** A key without a body still saves the round trip when streaming, the cap bounds it, and the two Clear buttons stay independent.

**Toggle (`audio.cache.keys.enabled`), read on every call:**
- Off: no reads and no writes. The connection closes, queued writes are dropped (generation bump), and **the file is kept**. That matches the body-cache toggle and makes an accidental flip harmless. The row still shows the size, and "Clear saved keys" still deletes the file.
- On: the store reopens lazily on the next use.

**Clear saved keys (safe while playing):**
1. Bump the generation, so anything already queued is dropped.
2. Empty the session map, so a cleared key can't be written back from memory.
3. Under `DbGate`: close, `Store.Delete`, or drop the tables and `VACUUM` if the file is held.
4. Move to Idle: the next use gets a fresh file and a fresh data key.

A playing `Body` holds its own `_key` copy, so playback is unaffected.

## 2. Pure rules: NEW `Spotify/Spotify.Audio.KeyStore.Rules.cs`

```csharp
// ── Spotify/Spotify.Audio.KeyStore.Rules.cs ── PURE (#n): the key store's record format and every decision it makes.
using System.Security.Cryptography;
using System.Text;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Audio
    {
        /// <summary>Where a session key came from. Only Ap and Derived are written; Disk marks a key read back.</summary>
        public enum KeyOrigin : byte { Ap = 1, Derived = 2, Disk = 3 }
        public enum KeyProofVerdict : byte { Keep, Persist, Skip, Forget }
        public enum KeyStorePhase : byte { Detached, Idle, Open, Off, Broken }
        public enum KeyStoreSignal : byte { Opened, OpenFailed, SettingOff, SettingOn, Corrupt, CorruptAgain, Cleared, Shutdown }

        public static class KeyStoreRules
        {
            public const string FileName = "audiokeys.db", Schema = "audiokeys.v1";
            public const int MaxKeys = 20_000, ScopeBytes = 8, ProofBytes = 0xc0;   // ProofBytes: what Ctr.Validates reads
            public const long TouchSeconds = 86_400;
            /// <summary>WAL first, main file last — Store.Delete's order.</summary>
            public static readonly string[] MemberSuffixes = ["-wal", "-shm", ""];

            public static string PathUnder(string localFolder) => Path.Combine(localFolder, "Wavee", "Cache", FileName);

            /// <summary>Containers whose chunk 0 names its own key (`OggS` at 0xa7, `fLaC` at 0).</summary>
            public static bool Recognizable(Format fmt) => fmt is Format.OggVorbis96 or Format.OggVorbis160
                or Format.OggVorbis320 or Format.Flac or Format.Flac24;

            public static bool CanProve(int cipherBytes) => cipherBytes >= ProofBytes;

            /// <summary>Verify before persist. A key that opens chunk 0 is kept across sessions; one that does not is never
            /// written, and a STORED one that does not is forgotten. An unverifiable container trusts only the AP.</summary>
            public static KeyProofVerdict OnProof(KeyOrigin origin, bool persisted, bool recognizable, bool validates)
            {
                bool stored = persisted || origin == KeyOrigin.Disk;
                if (validates) return stored ? KeyProofVerdict.Keep : KeyProofVerdict.Persist;
                if (recognizable) return stored ? KeyProofVerdict.Forget : KeyProofVerdict.Skip;
                if (stored) return KeyProofVerdict.Keep;
                return origin == KeyOrigin.Ap ? KeyProofVerdict.Persist : KeyProofVerdict.Skip;
            }

            /// <summary>Rows to drop: none at or under the cap, else down to 90 % (hysteresis — the trim is rare).</summary>
            public static int EvictCount(long rows, int max = MaxKeys) => rows <= max ? 0 : (int)(rows - max * 9L / 10);

            /// <summary>Coarse LRU: a hit rewrites last_used at most daily; a clock that went back resets it.</summary>
            public static bool ShouldTouch(long lastUsedUnix, long nowUnix)
                => lastUsedUnix > nowUnix || nowUnix - lastUsedUnix >= TouchSeconds;

            /// <summary>Why a file found at open cannot be kept, or null (keep it, or it is brand new).</summary>
            public static string? RecreateWhy(int sqliteCode, bool hasMeta, bool hasTables, string? schema, bool dekOk)
            {
                if (Store.IsUnreadableFile(sqliteCode)) return "unreadable:" + sqliteCode;
                if (!hasMeta) return hasTables ? "foreign" : null;          // 0.2.9's audio_license file is foreign
                if (!string.Equals(schema, Schema, StringComparison.Ordinal)) return "stale";
                return dekOk ? null : "dek";
            }

            public static KeyStorePhase Next(KeyStorePhase p, KeyStoreSignal s) => (p, s) switch
            {
                (KeyStorePhase.Detached, _) => KeyStorePhase.Detached,      // no path: --fake, headless, a unit test
                (_, KeyStoreSignal.Shutdown) => KeyStorePhase.Detached,
                (_, KeyStoreSignal.SettingOff) => KeyStorePhase.Off,
                (KeyStorePhase.Off, KeyStoreSignal.SettingOn) => KeyStorePhase.Idle,
                (KeyStorePhase.Off, _) => KeyStorePhase.Off,                 // a Clear while off deletes, stays off
                (_, KeyStoreSignal.Cleared) => KeyStorePhase.Idle,           // the file is gone: even Broken may try again
                (KeyStorePhase.Broken, _) => KeyStorePhase.Broken,
                (KeyStorePhase.Idle, KeyStoreSignal.Opened) => KeyStorePhase.Open,
                (KeyStorePhase.Idle, KeyStoreSignal.OpenFailed) => KeyStorePhase.Broken,
                (KeyStorePhase.Open, KeyStoreSignal.Corrupt) => KeyStorePhase.Idle,      // rebuilt on the next use
                (KeyStorePhase.Open, KeyStoreSignal.CorruptAgain) => KeyStorePhase.Broken,
                _ => p,
            };

            /// <summary>A `.dead-<8 hex>` member of THIS set that Store.Delete renamed aside and could not remove.</summary>
            public static bool IsLeftover(string name)
            {
                int at = name.Length - StoreFiles.DeadTag.Length - StoreFiles.DeadTagHexDigits;
                if (at < FileName.Length || !name.AsSpan(at, StoreFiles.DeadTag.Length).Equals(StoreFiles.DeadTag, StringComparison.OrdinalIgnoreCase))
                    return false;
                foreach (char c in name.AsSpan(at + StoreFiles.DeadTag.Length)) if (!char.IsAsciiHexDigit(c)) return false;
                ReadOnlySpan<char> member = name.AsSpan(0, at);
                foreach (string suffix in MemberSuffixes)
                    if (member.Equals(FileName + suffix, StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
        }

        /// <summary>One sealed row: [version | nonce 12 | tag 16 | ct 17], plaintext [origin | key 16], AAD
        /// [version | scope 8 | file id]. PURE given the nonce.</summary>
        public static class KeyRecord
        {
            public const byte Version = 1;
            public const int NonceBytes = 12, TagBytes = 16, PlainBytes = 1 + AudioKey.KeyLength;
            public const int SealedBytes = 1 + NonceBytes + TagBytes + PlainBytes;          // 46
            const int MaxFileId = 64;

            public static void Seal(AesGcm gcm, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> scope, ReadOnlySpan<byte> fileId,
                KeyOrigin origin, ReadOnlySpan<byte> key16, Span<byte> sealedOut)
            {
                Span<byte> plain = stackalloc byte[PlainBytes];
                plain[0] = (byte)origin;
                key16[..AudioKey.KeyLength].CopyTo(plain[1..]);
                Span<byte> aad = stackalloc byte[1 + KeyStoreRules.ScopeBytes + MaxFileId];
                int n = Aad(aad, scope, fileId);
                sealedOut[0] = Version;
                nonce.CopyTo(sealedOut[1..]);
                gcm.Encrypt(nonce, plain, sealedOut[(1 + NonceBytes + TagBytes)..], sealedOut.Slice(1 + NonceBytes, TagBytes), aad[..n]);
                CryptographicOperations.ZeroMemory(plain);
            }

            public static bool TryOpen(AesGcm gcm, ReadOnlySpan<byte> sealedIn, ReadOnlySpan<byte> scope, ReadOnlySpan<byte> fileId,
                Span<byte> key16, out KeyOrigin origin)
            {
                origin = default;
                if (sealedIn.Length != SealedBytes || sealedIn[0] != Version || fileId.Length is 0 or > MaxFileId) return false;
                Span<byte> plain = stackalloc byte[PlainBytes];
                Span<byte> aad = stackalloc byte[1 + KeyStoreRules.ScopeBytes + MaxFileId];
                int n = Aad(aad, scope, fileId);
                try { gcm.Decrypt(sealedIn.Slice(1, NonceBytes), sealedIn[(1 + NonceBytes + TagBytes)..], sealedIn.Slice(1 + NonceBytes, TagBytes), plain, aad[..n]); }
                catch (CryptographicException) { return false; }             // tag mismatch: corrupt, moved or foreign
                bool ok = plain[0] is (byte)KeyOrigin.Ap or (byte)KeyOrigin.Derived;
                if (ok) { origin = (KeyOrigin)plain[0]; plain[1..].CopyTo(key16); }
                CryptographicOperations.ZeroMemory(plain);
                return ok;
            }

            public static void Scope(ReadOnlySpan<byte> dek, string account, Span<byte> scope8)
            {
                Span<byte> mac = stackalloc byte[32];
                HMACSHA256.HashData(dek, Encoding.UTF8.GetBytes("wavee.audiokeys.account\0" + account), mac);
                mac[..KeyStoreRules.ScopeBytes].CopyTo(scope8);
            }

            static int Aad(Span<byte> aad, ReadOnlySpan<byte> scope, ReadOnlySpan<byte> fileId)
            {
                aad[0] = Version;
                scope.CopyTo(aad[1..]);
                fileId.CopyTo(aad[(1 + scope.Length)..]);
                return 1 + scope.Length + fileId.Length;
            }
        }
    }
}
```

## 3. Shell: NEW `Spotify/Spotify.Audio.KeyStore.cs`

This file holds the session map (moved out of §5 of `Spotify.Audio.cs`, which keeps that file under its 1200-line budget), `KeyProof`, and the `KeyStore` class. Key methods:

```csharp
// session map (moved from Spotify.Audio.cs §5) — FIFO, 256
const int KeyCacheMax = 256;
static readonly Lock KeyGate = new();
static readonly Dictionary<string, KeyEntry> KeyCache = new(KeyCacheMax, StringComparer.OrdinalIgnoreCase);
static readonly Queue<string> KeyOrder = new(KeyCacheMax);

sealed class KeyEntry(byte[] fileId, byte[] key, KeyOrigin origin)
{
    public readonly byte[] FileId = fileId, Key = key;
    public readonly KeyOrigin Origin = origin;
    public volatile bool Persisted = origin == KeyOrigin.Disk;
}

static void Remember(string fileIdHex, ReadOnlySpan<byte> fileId, ReadOnlySpan<byte> key16, KeyOrigin origin)
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

/// <summary>For `audio.head`'s keySrc=: ap | derived | disk | "-".</summary>
internal static string KeyOriginText(string fileIdHex)
{
    lock (KeyGate)
        return !KeyCache.TryGetValue(fileIdHex, out KeyEntry? e) ? "-"
             : e.Origin switch { KeyOrigin.Ap => "ap", KeyOrigin.Derived => "derived", _ => "disk" };
}

/// <summary>Chunk 0's CIPHERTEXT has been seen with <paramref name="key16"/>: persist, keep, skip or forget the key
/// (KeyStoreRules.OnProof). Once per body. Never blocks: a persist is an enqueue.</summary>
internal static KeyProofVerdict KeyProof(string fileIdHex, Format fmt, ReadOnlySpan<byte> cipher, ReadOnlySpan<byte> key16,
    out bool validates)
{
    validates = false;
    if (!KeyStoreRules.CanProve(cipher.Length)) return KeyProofVerdict.Keep;
    KeyEntry? e;
    lock (KeyGate) KeyCache.TryGetValue(fileIdHex, out e);
    validates = Ctr.Validates(cipher, key16);
    if (e is null || !e.Key.AsSpan().SequenceEqual(key16)) return KeyProofVerdict.Keep;   // cleared/evicted/not this key
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

public static class KeyStore
{
    public readonly record struct Stats(KeyStorePhase Phase, long Hits, long Misses, long Writes, long Forgotten,
        long BadRecords, long Dropped, int Queued);

    enum OpKind : byte { Put, Touch, Forget, Warm, Barrier }
    readonly record struct Op(OpKind Kind, int Generation, string Account, byte[]? FileId, byte[]? Key = null,
        KeyOrigin Origin = default, long Now = 0, ManualResetEventSlim? Done = null);

    static readonly Lock DbGate = new();
    static string? s_path;
    static ICredentialProtector s_protector = new NoOpProtector();
    static Func<bool> s_enabled = static () => false;
    static volatile KeyStorePhase s_phase;                 // Detached
    static string? s_account;                              // Volatile; set by the welcome
    static int s_generation;
    static bool s_recovered;                               // DbGate
    static SqliteConnection? s_db; static AesGcm? s_gcm; static byte[]? s_dek;   // DbGate
    static string? s_scopeFor; static readonly byte[] s_scope = new byte[KeyStoreRules.ScopeBytes];
    static BlockingCollection<Op>? s_queue; static Thread? s_writer;
    static long s_hits, s_misses, s_writes, s_forgotten, s_bad, s_dropped;

    public static string DefaultPath() => KeyStoreRules.PathUnder(Platform.LocalFolder);

    /// <summary>App.Main (after the instance gate, never under --fake) or a test. Records the path; opens NOTHING.</summary>
    public static void Use(string? path, ICredentialProtector protector, Func<bool> enabled)
    {
        Shutdown();
        lock (DbGate)
        {
            s_path = string.IsNullOrEmpty(path) ? null : path;
            s_protector = protector; s_enabled = enabled; s_recovered = false;
            s_phase = s_path is null ? KeyStorePhase.Detached : enabled() ? KeyStorePhase.Idle : KeyStorePhase.Off;
        }
    }

    /// <summary>UI thread (the welcome effect): the account rows are scoped to; pre-opens the file off-thread.</summary>
    public static void UseAccount(string account)
    {
        Volatile.Write(ref s_account, account.Length == 0 ? null : account);
        if (account.Length > 0) Enqueue(new Op(OpKind.Warm, Volatile.Read(ref s_generation), account, null));
    }

    /// <summary>A point lookup on the caller's thread (an api thread or the pump — never the UI thread).</summary>
    public static bool TryGet(ReadOnlySpan<byte> fileId, Span<byte> key16)
    {
        if (s_phase == KeyStorePhase.Detached || Volatile.Read(ref s_account) is not { } account) return false;
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
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (KeyStoreRules.ShouldTouch(lastUsed, now))
                    Enqueue(new Op(OpKind.Touch, Volatile.Read(ref s_generation), account, fileId.ToArray(), Now: now));
                s_hits++;
                return true;
            }
            catch (SqliteException ex) { FaultLocked("read", ex); return false; }
        }
    }

    internal static void Put(byte[] fileId, byte[] key16, KeyOrigin origin)
    {
        if (Volatile.Read(ref s_account) is { } a && s_enabled())
            Enqueue(new Op(OpKind.Put, Volatile.Read(ref s_generation), a, fileId, key16, origin, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    }

    internal static void Forget(byte[] fileId)
    {
        if (Volatile.Read(ref s_account) is { } a) Enqueue(new Op(OpKind.Forget, Volatile.Read(ref s_generation), a, fileId));
    }

    /// <summary>The setting, read per call, folded into the phase; an Idle store opens here.</summary>
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

    static void RunBatchLocked(List<Op> batch)                       // writer thread, under DbGate
    {
        int gen = Volatile.Read(ref s_generation);
        if (!ServesLocked()) return;                                  // off / broken: queued writes are DROPPED
        try
        {
            using SqliteTransaction tx = s_db!.BeginTransaction();
            int puts = 0;
            Span<byte> nonce = stackalloc byte[KeyRecord.NonceBytes];
            foreach (Op op in batch)
            {
                if (op.Generation != gen || op.FileId is null) continue;     // queued before a Clear / a toggle-off
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
                        KeyRecord.Seal(s_gcm!, nonce, s_scope, op.FileId, op.Origin, op.Key, sealedRow);
                        cmd.CommandText = "INSERT OR REPLACE INTO audio_key(scope,file_id,sealed,last_used) VALUES($s,$f,$b,$t);";
                        cmd.Parameters.AddWithValue("$b", sealedRow); cmd.Parameters.AddWithValue("$t", op.Now);
                        puts++; break;
                    case OpKind.Touch:
                        cmd.CommandText = "UPDATE audio_key SET last_used=$t WHERE scope=$s AND file_id=$f;";
                        cmd.Parameters.AddWithValue("$t", op.Now); break;
                    case OpKind.Forget:
                        cmd.CommandText = "DELETE FROM audio_key WHERE scope=$s AND file_id=$f;"; s_forgotten++; break;
                }
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            s_writes += puts;
            if (puts > 0 && KeyStoreRules.EvictCount(Scalar("SELECT count(*) FROM audio_key;")) is > 0 and int drop)
                Exec($"DELETE FROM audio_key WHERE (scope,file_id) IN (SELECT scope,file_id FROM audio_key ORDER BY last_used LIMIT {drop});");
        }
        catch (SqliteException ex) { FaultLocked("write", ex); }
    }
```

Open, fault, clear and the rest:

```csharp
    static void OpenLocked()
    {
        long t0 = Stopwatch.GetTimestamp();
        string path = s_path!;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            ReapLeftovers(path);                                     // KeyStoreRules.IsLeftover → Store.TryDelete
            SqliteConnection c = Connect(path);
            string? why = Inspect(c, out bool created, out byte[]? dek);   // pragmas, facts, DPAPI unwrap → RecreateWhy
            if (why is not null) { c.Dispose(); c = Recreate(path); dek = null; }   // Store.Delete, else drop tables in place
            dek ??= StampNewDek(c);                                  // DDL + meta(schema, dek_scheme, dek = Protect(32 random))
            s_db = c; s_dek = dek; s_gcm = new AesGcm(dek, KeyRecord.TagBytes); s_scopeFor = null;
            Move(KeyStoreSignal.Opened);
            LogOpen(why is null ? (created ? "created" : "opened") : "recreated:" + why, t0, warn: why is not null);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or CryptographicException)
        {
            CloseLocked();
            Move(KeyStoreSignal.OpenFailed);
            LogOpen("memory-only:" + ex.GetType().Name, t0, warn: true);
        }
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

    /// <summary>Settings › Clear saved keys (off the UI thread). Safe while playing: a Body holds its own key copy.</summary>
    public static void Clear()
    {
        Interlocked.Increment(ref s_generation);
        ForgetSessionKeys();
        bool deleted;
        lock (DbGate)
        {
            if (s_phase == KeyStorePhase.Detached || s_path is null) return;
            CloseLocked();
            deleted = Store.Delete(s_path);
            if (!deleted) EmptyInPlace(s_path);                      // DROP both tables + VACUUM
            Move(KeyStoreSignal.Cleared);
        }
        Log.Event(WaveeLogLevel.Info, "audio", "audio.keys.clear", "", null, -1, null, WaveeLogField.Of("deleted", deleted));
    }

    /// <summary>Settings' LicenseKeyCount seam; null when off/detached/broken (the row then shows its size only).</summary>
    public static int? Count() { lock (DbGate) { if (!ServesLocked()) return null; try { return (int)Scalar("SELECT count(*) FROM audio_key;"); } catch (SqliteException ex) { FaultLocked("count", ex); return null; } } }

    /// <summary>The whole WAL set's bytes (the census used to count the main file only).</summary>
    public static long DiskBytes() { string p = s_path ?? DefaultPath(); long t = 0; foreach (string s in KeyStoreRules.MemberSuffixes) t += LengthOf(p + s); return t; }

    /// <summary>Drain the queue (tests, Shutdown). Enqueues a Barrier and waits for it.</summary>
    public static void Flush(int timeoutMs) { ... }

    /// <summary>App exit tail, after Playback.Audio.Shutdown: drain, checkpoint(TRUNCATE), close, one audio.keys.stats line.</summary>
    public static void Shutdown() { ... }

    public static Stats Read() => new(s_phase, Interlocked.Read(ref s_hits), ...);
}
```

The writer loop takes one operation, then `TryTake`s up to 63 more, runs `RunBatchLocked` under `DbGate`, and sets each `Done`. `Enqueue` returns immediately when the phase is Detached, Off or Broken, starts the thread on first use, counts a failed `TryAdd` as dropped, and catches the `InvalidOperationException` that `CompleteAdding` can race into. `ScopeLocked(account)` memoises `KeyRecord.Scope(s_dek, account, s_scope)` for the last account.

## 4. Edits to existing files

**`Spotify/Spotify.Audio.cs` §5:** remove the map fields and `Remember` (they move to §3). `ResetKeyLatch` calls `ForgetSessionKeys()`. The new `Key()`:

```csharp
lock (KeyGate)
    if (KeyCache.TryGetValue(fileIdHex, out KeyEntry? cached)) { cached.Key.CopyTo(key16); return Fault.None; }

// The disk (#n): a point lookup; false when off, detached, unscoped or broken. Before the AP on purpose — a cached
// replay pays no round trip, and a reconnecting AP no longer fails it.
if (KeyStore.TryGet(fileId, key16)) { Remember(fileIdHex, fileId, key16, KeyOrigin.Disk); return Fault.None; }

if (apEligible && !s_apKeysDisabled)
{
    AudioKeyResult outcome = RequestAudioKey(fileId, trackGid, key16);
    if (outcome == AudioKeyResult.Ok) { Remember(fileIdHex, fileId, key16, KeyOrigin.Ap); return Fault.None; }
    // … unchanged …
}
// … deriver unchanged, ending:
derived.CopyTo(key16);
Remember(fileIdHex, fileId, key16, KeyOrigin.Derived);
return Fault.None;
```

Persisting is not done here; it happens on proof.

**`Spotify/Spotify.Audio.Stream.cs`:**
- `OpenBody`: keep `int cachedChunk0Len` (the `n` that `TryReadChunk` returned). After `pending.Wait(ct)`:
```csharp
// The key's proof over a cached chunk 0 (#n): a stored key that does not open its own cached bytes is forgotten and asked
// for again here, once, before a byte is decrypted with it.
if (fault == Fault.None && cachedChunk0 is not null)
{
    var c0 = cachedChunk0.AsSpan(0, cachedChunk0Len);
    if (KeyProof(fileIdHex, choice.Fmt, c0, key, out _) == KeyProofVerdict.Forget)
    {
        fault = seams.Key(fileIdHex, choice.FileId, choice.TrackGid, key, ApEligible(choice.Fmt), ct);
        if (fault == Fault.None) KeyProof(fileIdHex, choice.Fmt, c0, key, out _);
    }
}
```
  Add `keySrc={KeyOriginText(fileIdHex)}` to the `audio.head` line.
- `Body.Publish`: add a field `int _keyProved`. This replaces both of the current `audio.key` lines with one:
```csharp
WriteThrough(at, bytes);
if (at == 0 && _key is not null && KeyStoreRules.CanProve(bytes.Length) && Interlocked.Exchange(ref _keyProved, 1) == 0)
{
    KeyProofVerdict proof = KeyProof(FileIdHex, Fmt, bytes, _key, out bool validates);   // CIPHERTEXT, before decrypt
    Log.Info("audio", $"audio.key file={FileIdHex} native={(_decrypt is null ? 0 : 1)} validates={(validates ? 1 : 0)} "
                      + $"origin={KeyOriginText(FileIdHex)} proof={proof}");
}
if (_decrypt is not null) _decrypt(bytes, at); else if (_key is not null) Ctr.DecryptInPlace(bytes, _key, at);
Land(in req, at, bytes);
```

**`Spotify/Spotify.Session.cs` `AdoptWelcome`:** right after `string account = …`, add `Audio.KeyStore.UseAccount(account);   // #n: rows are scoped per account; G-034 holds across sessions`.

**`App.cs`:**
- Inside the `!Platform.Args.Fake` block, after `Store.Use`:
  `Spotify.Audio.KeyStore.Use(Spotify.Audio.KeyStore.DefaultPath(), OperatingSystem.IsWindows() ? new DpapiProtector() : new NoOpProtector(), static () => Platform.Settings.Get(Platform.Keys.AudioKeyCacheEnabled));`
- Next to `ClearMetadataCache`: `Settings.LicenseKeyCount = Spotify.Audio.KeyStore.Count; Settings.ClearLicenseKeys = Spotify.Audio.KeyStore.Clear;`
- In the exit tail, after `Playback.Audio.Shutdown();`: `Spotify.Audio.KeyStore.Shutdown();`

**`Screens/Settings.Host.cs`:**
- Delete `LicenseDbPath()`.
- In the census: `long keys = Spotify.Audio.KeyStore.DiskBytes();`
- `ClearSavedKeys() => ClearThenRecount(static () => ClearLicenseKeys?.Invoke(), …LicenseKeysCleared)`, with no `File.Delete`.
- Update the seams' doc comments.

**`Screens/Settings.UI.Storage.cs`:** the row's folder becomes `Path.GetDirectoryName(Spotify.Audio.KeyStore.DefaultPath()) ?? audioDir`. The count sub already works once the seam is assigned.

**`assets/loc/en-US.json`:**
- `cacheKeysSub`: "Reuse decryption keys across sessions so tracks you've played start sooner".
- `clearKeysBody`: "Remove all saved license keys from disk? Tracks will ask for their keys again next time they play."

The old copy said "obfuscated keys", which is no longer true.

**`Wavee.Tests/SpotifyAudioTests.cs`:** add `[Collection(AudioKeyCollection.Name)]` to `SpotifyAudioSeamTests`. It and the new shell tests share the `Spotify.Audio` statics.

## 5. Tests

**`Wavee.Tests/AudioKeyStoreRulesTests.cs`** (pure):
1. `Record_round_trips_the_key_and_its_origin` (fixed data key and nonce).
2. `Record_refuses_another_file_id`
3. `Record_refuses_another_account_scope`
4. `Record_refuses_any_flipped_byte` (loop over all 46 bytes)
5. `Record_refuses_a_wrong_length_or_version`
6. `Scope_is_stable_per_account_and_differs_across_accounts_and_keys`
7. `Proof_persists_a_validated_new_key_and_keeps_a_stored_one`
8. `Proof_forgets_a_stored_key_that_fails_a_recognizable_container`
9. `Proof_skips_an_unproven_derived_key` (covers the native-decryptor case)
10. `Proof_trusts_only_the_ap_for_mp3_and_aac`
11. `CanProve_needs_0xc0_bytes`
12. `Recognizable_is_ogg_and_flac_only`
13. `Evict_is_zero_at_the_cap_and_trims_to_ninety_percent`
14. `Touch_is_daily_and_resets_on_a_clock_that_went_back`
15. `RecreateWhy` cases: unreadable 11/26, foreign (0.2.9 shape), stale, dek, new, keep
16. Phase machine: Detached absorbs; Off on SettingOff from every phase; Off+SettingOn→Idle; Off+Cleared stays Off; Broken+Cleared→Idle; Corrupt→Idle; CorruptAgain→Broken; Shutdown→Detached
17. `PathUnder_is_beside_the_audio_cache` (same parent as `DiskCache.DirectoryUnder`)
18. `IsLeftover_names_only_this_sets_dead_members`

**`Wavee.Tests/AudioKeyStoreTests.cs`** (shell, temp folder, `NoOpProtector`, injected `enabled`; defines `AudioKeyCollection` with `DisableParallelization = true`):
1. `A_proven_key_survives_a_restart` (`Use` → `UseAccount` → `Remember` + `KeyProof` over a real CTR-encrypted Ogg prefix → `Flush` → `Use` again → `TryGet`)
2. `Key_reads_the_disk_before_the_deriver` (fake deriver counter stays at 1 after `ResetKeyLatch`)
3. `An_unproven_key_is_never_written`
4. `Off_reads_nothing_writes_nothing_and_keeps_the_file`
5. `Clear_empties_the_store_and_the_session_map_and_mints_a_new_file`
6. `Another_account_does_not_see_the_key`
7. `A_029_audio_license_file_is_recreated_as_foreign`
8. `A_garbage_file_is_recreated`
9. `A_protector_scheme_change_recreates_as_dek`
10. `Queued_writes_before_a_clear_are_dropped`
11. `The_cap_trims_oldest_by_last_used` (with a small max via `EvictCount`)

All of these are file and behaviour tests; none reads source text.

## 6. Ownership, changelog, risks

**One Sonnet implementer owns:**
- NEW `src/apps/Wavee/Spotify/Spotify.Audio.KeyStore.Rules.cs`
- NEW `src/apps/Wavee/Spotify/Spotify.Audio.KeyStore.cs`
- NEW `src/apps/Wavee.Tests/AudioKeyStoreRulesTests.cs`
- NEW `src/apps/Wavee.Tests/AudioKeyStoreTests.cs`
- EDIT `src/apps/Wavee/Spotify/Spotify.Audio.cs` (§5), `src/apps/Wavee/Spotify/Spotify.Audio.Stream.cs` (OpenBody, Body.Publish), `src/apps/Wavee/Spotify/Spotify.Session.cs` (AdoptWelcome), `src/apps/Wavee/App.cs`, `src/apps/Wavee/Screens/Settings.Host.cs`, `src/apps/Wavee/Screens/Settings.UI.Storage.cs`, `src/apps/Wavee/assets/loc/en-US.json`, `src/apps/Wavee.Tests/SpotifyAudioTests.cs` (one attribute), `CHANGELOG.md`, `docs/plans/wavee/wavee-0.3-gap-register.md` (G-123 fixed; LicenseKey seams in G-192/G-272 assigned)
- Optionally: write this spec to `docs/plans/wavee/audio-key-store-implementation.md`, per the "plans with real code" rule.

The private repo needs no change.

**CHANGELOG, under `### Fixed`:**
"- **Saved license keys were never saved.** Settings › Storage showed "Saved license keys — 0 B" because nothing wrote the file, so every replay of a track already in the audio cache still had to fetch its key again. Keys for tracks you play are now kept on disk, encrypted for your Windows account, and checked against the track's own audio before they are saved, so a cached track starts without that round trip. The row shows how many keys are saved, "Clear saved keys" works even while music is playing, and turning "Cache license keys" off stops using them. (#n)"

The commit body carries `Fixes #n`.

**Risks:**
1. **Shared static state in tests:** the new collection covers it. The linked private `Wavee.PlayPlay/Tests` already share `KeyDeriver`.
2. **DPAPI profile moves or master-key resets** lose the keys. That is logged as `recreated:dek` and harmless.
3. **`Platform.Settings.Get` is a registry read per call.** It happens once per lookup or proof, which is per track and negligible.
4. **A 0.2.9 process holding the same file** forces the drop-in-place path. This is rare, and packaged installs live in separate LocalCache folders.
5. **A real track whose chunk 0 doesn't validate:** its key is never stored (Skip). That is no worse than today.
6. **Stale-key healing is inline only for cached bodies.** On a streamed body a Forget fixes the next play, not the current one. A wrong key can only reach disk through the AP-MP3 exception.
7. **Not delivered:**
   - Playing offline: `Open` still requires Online and the metadata.
   - A public-only build playing stored FLAC keys: `LosslessFallback.Effective` is unchanged.
   - Wiring the headless `--profile` host: possible follow-ups.

### Critical Files for Implementation
- C:\wavee\waveemusic\src\apps\Wavee\Spotify\Spotify.Audio.cs
- C:\wavee\waveemusic\src\apps\Wavee\Spotify\Spotify.Audio.Stream.cs
- C:\wavee\waveemusic\src\apps\Wavee\Entities\Store.cs (Delete/TryDelete/IsUnreadableFile reused) and C:\wavee\waveemusic\src\apps\Wavee\Entities\Store.Files.cs (StoreHealth, DeadTag)
- C:\wavee\waveemusic\src\apps\Wavee\Screens\Settings.Host.cs
- C:\wavee\waveemusic\src\apps\Wavee\App.cs
