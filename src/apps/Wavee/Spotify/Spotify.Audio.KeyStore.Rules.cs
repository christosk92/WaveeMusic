// ── Spotify/Spotify.Audio.KeyStore.Rules.cs ────────────────────────────────────────────────────────────────────────────
// the saved audio keys' record format and every decision the store makes — engine-free, file-free, clock-free
//
// Role: CORE (pure)
// Spec: docs/plans/wavee/audio-key-store-implementation.md §2 (G-123)
//
// WHAT THIS IS FOR. 0.2.9 kept PlayPlay LICENSE PAYLOADS in `Cache\audiokeys.db` and never read them back usefully; 0.3
// shipped the Settings row ("Saved license keys — 0 B") and nothing that wrote the file, so every replay of a track
// already in the audio cache still asked for its key again. The store (`Spotify.Audio.KeyStore.cs`) now keeps the FINAL
// 16-byte AES key, from the AP or from the deriver, and this file is everything it decides: what a row looks like on disk
// and how it is authenticated, when a key may be saved (only after it has opened chunk 0), when a saved one is forgotten,
// how big the file may grow, when a hit rewrites its timestamp, why a file found at open cannot be kept, and the store's
// phase machine. Nothing here opens a file, reads a clock or touches a lock, so KeyStoreRulesTests pins every rule
// without a database on disk.
//
// KEY MATERIAL IS NEVER LOGGED. A row is [version | nonce 12 | tag 16 | ciphertext 17] under a data key that is itself
// DPAPI-wrapped in the file's `meta`; the AAD binds a row to its account scope and its file id, so a row copied to
// another file id or another account fails its tag and reads as a miss.

using System.Security.Cryptography;
using System.Text;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Audio
    {
        /// <summary>Where a session key came from. Only Ap and Derived are written; Disk marks a key read back.</summary>
        public enum KeyOrigin : byte { Ap = 1, Derived = 2, Disk = 3 }

        /// <summary>What chunk 0's proof says to do with a key (<see cref="KeyStoreRules.OnProof"/>).</summary>
        public enum KeyProofVerdict : byte { Keep, Persist, Skip, Forget }

        /// <summary>The store's lifecycle: Detached (no path — --fake, headless, a unit test), Idle (a path, not opened),
        /// Open, Off (the setting), Broken (memory-only for the rest of the session).</summary>
        public enum KeyStorePhase : byte { Detached, Idle, Open, Off, Broken }

        public enum KeyStoreSignal : byte { Opened, OpenFailed, SettingOff, SettingOn, Corrupt, CorruptAgain, Cleared, Shutdown }

        public static class KeyStoreRules
        {
            public const string FileName = "audiokeys.db", Schema = "audiokeys.v1";

            /// <summary><c>ProofBytes</c> is what <see cref="Ctr.Validates"/> reads: the first 0xc0 bytes of the file.</summary>
            public const int MaxKeys = 20_000, ScopeBytes = 8, ProofBytes = 0xc0;

            /// <summary>A hit rewrites <c>last_used</c> at most once a day.</summary>
            public const long TouchSeconds = 86_400;

            /// <summary>WAL first, main file last — <see cref="Store"/>'s delete order.</summary>
            public static readonly string[] MemberSuffixes = ["-wal", "-shm", ""];

            /// <summary>0.2.9's path, beside <c>Cache\audio</c>: <c>%LOCALAPPDATA%\Wavee\Wavee\Cache\audiokeys.db</c> under the
            /// profile's <paramref name="localFolder"/> (<c>Platform.LocalFolder</c>). PURE.</summary>
            public static string PathUnder(string localFolder) => Path.Combine(localFolder, "Wavee", "Cache", FileName);

            /// <summary>Containers whose chunk 0 names its own key (`OggS` at 0xa7, `fLaC` at 0).</summary>
            public static bool Recognizable(Format fmt) => fmt is Format.OggVorbis96 or Format.OggVorbis160
                or Format.OggVorbis320 or Format.Flac or Format.Flac24;

            public static bool CanProve(int cipherBytes) => cipherBytes >= ProofBytes;

            /// <summary>Verify before persist. A key that opens chunk 0 is kept across sessions; one that does not is never
            /// written, and a STORED one that does not is forgotten. An unverifiable container (MP3, AAC) trusts only the AP.</summary>
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

            /// <summary>Why a file found at open cannot be kept, or null (keep it, or it is brand new). <paramref name="sqliteCode"/>
            /// is the primary result code of the first statement that failed (0 when none did).</summary>
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

            /// <summary>A `.dead-&lt;8 hex&gt;` member of THIS set that <see cref="Store"/>'s delete renamed aside and could not
            /// remove.</summary>
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

            /// <summary>A Spotify file id is 20 bytes; the AAD buffer is sized for this many.</summary>
            public const int MaxFileId = 64;

            public static void Seal(AesGcm gcm, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> scope, ReadOnlySpan<byte> fileId,
                KeyOrigin origin, ReadOnlySpan<byte> key16, Span<byte> sealedOut)
            {
                if (fileId.Length is 0 or > MaxFileId) throw new ArgumentException("a file id is 1 to 64 bytes", nameof(fileId));
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

            /// <summary>The account scope: <c>HMAC-SHA256(dek, "wavee.audiokeys.account\0" + account)[..8]</c>. Keyed by the
            /// data key, so the username cannot be recovered from the file.</summary>
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
