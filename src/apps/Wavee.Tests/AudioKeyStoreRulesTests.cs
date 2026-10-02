// ── Wavee.Tests/AudioKeyStoreRulesTests.cs — the saved audio keys' record format and every decision the store makes ──
//
// `Spotify.Audio.KeyStoreRules` and `KeyRecord` (Spotify/Spotify.Audio.KeyStore.Rules.cs) are PURE: bytes and numbers in,
// a verdict out, no file and no clock. The record is what makes a key safe at rest — AES-GCM under a data key, bound to
// the account scope and the file id by its AAD, so a flipped byte, or a row copied to another file id or another account,
// is a tag failure and reads as a miss. The proof table is what makes a key safe to REUSE: only a key that opened chunk 0
// is written; a stored one that stops opening it is forgotten. The shell facts are AudioKeyStoreTests.
//
// No file, no clock, no sqlite. Fixed data key and nonce, so every assertion is reproducible.

using System.Security.Cryptography;
using Wavee;
using Xunit;
using Audio = Wavee.Spotify.Audio;

namespace Wavee.Tests;

public class AudioKeyStoreRulesTests
{
    static byte[] Bytes(int length, int seed)
    {
        var b = new byte[length];
        for (int i = 0; i < length; i++) b[i] = (byte)(seed + i * 7);
        return b;
    }

    static readonly byte[] Dek = Bytes(32, 1);
    static readonly byte[] Nonce = Bytes(Audio.KeyRecord.NonceBytes, 90);
    static readonly byte[] FileA = Bytes(20, 10);
    static readonly byte[] FileB = Bytes(20, 11);
    static readonly byte[] Key = Bytes(16, 200);

    static byte[] ScopeOf(string account)
    {
        var scope = new byte[Audio.KeyStoreRules.ScopeBytes];
        Audio.KeyRecord.Scope(Dek, account, scope);
        return scope;
    }

    static byte[] Seal(byte[] scope, byte[] fileId, Audio.KeyOrigin origin = Audio.KeyOrigin.Ap)
    {
        using var gcm = new AesGcm(Dek, Audio.KeyRecord.TagBytes);
        var sealedRow = new byte[Audio.KeyRecord.SealedBytes];
        Audio.KeyRecord.Seal(gcm, Nonce, scope, fileId, origin, Key, sealedRow);
        return sealedRow;
    }

    static bool Open(byte[] row, byte[] scope, byte[] fileId, out byte[] key, out Audio.KeyOrigin origin)
    {
        using var gcm = new AesGcm(Dek, Audio.KeyRecord.TagBytes);
        key = new byte[16];
        return Audio.KeyRecord.TryOpen(gcm, row, scope, fileId, key, out origin);
    }

    // ── the record ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Audio.KeyOrigin.Ap)]
    [InlineData(Audio.KeyOrigin.Derived)]
    public void Record_round_trips_the_key_and_its_origin(Audio.KeyOrigin origin)
    {
        byte[] scope = ScopeOf("alice");
        byte[] row = Seal(scope, FileA, origin);

        Assert.Equal(46, row.Length);
        Assert.Equal(Audio.KeyRecord.Version, row[0]);
        Assert.True(Open(row, scope, FileA, out byte[] key, out Audio.KeyOrigin got));
        Assert.Equal(Key, key);
        Assert.Equal(origin, got);
        Assert.True(row.AsSpan().IndexOf(Key.AsSpan()) < 0, "the key must not sit in the row in the clear");
    }

    [Fact]
    public void Record_refuses_another_file_id()
    {
        byte[] scope = ScopeOf("alice");
        byte[] row = Seal(scope, FileA);

        Assert.False(Open(row, scope, FileB, out byte[] key, out _));
        Assert.All(key, static b => Assert.Equal(0, b));            // a refused row writes nothing into the caller's key
    }

    [Fact]
    public void Record_refuses_another_account_scope()
    {
        byte[] row = Seal(ScopeOf("alice"), FileA);

        Assert.False(Open(row, ScopeOf("bob"), FileA, out _, out _));
        Assert.True(Open(row, ScopeOf("alice"), FileA, out _, out _));
    }

    [Fact]
    public void Record_refuses_any_flipped_byte()
    {
        byte[] scope = ScopeOf("alice");
        byte[] row = Seal(scope, FileA);

        for (int i = 0; i < row.Length; i++)
        {
            byte[] bad = row.ToArray();
            bad[i] ^= 0x01;
            Assert.False(Open(bad, scope, FileA, out _, out _), "byte " + i);
        }
    }

    [Fact]
    public void Record_refuses_a_wrong_length_or_version()
    {
        byte[] scope = ScopeOf("alice");
        byte[] row = Seal(scope, FileA);

        Assert.False(Open(row[..^1], scope, FileA, out _, out _));
        Assert.False(Open([.. row, 0], scope, FileA, out _, out _));
        Assert.False(Open([], scope, FileA, out _, out _));
        byte[] v2 = row.ToArray();
        v2[0] = 2;
        Assert.False(Open(v2, scope, FileA, out _, out _));

        Assert.False(Open(row, scope, [], out _, out _));                                  // no file id
        Assert.False(Open(row, scope, new byte[Audio.KeyRecord.MaxFileId + 1], out _, out _));
    }

    [Fact]
    public void Record_refuses_a_data_key_that_did_not_seal_it()
    {
        byte[] scope = ScopeOf("alice");
        byte[] row = Seal(scope, FileA);
        using var other = new AesGcm(Bytes(32, 77), Audio.KeyRecord.TagBytes);

        Assert.False(Audio.KeyRecord.TryOpen(other, row, scope, FileA, new byte[16], out _));
    }

    [Fact]
    public void Scope_is_stable_per_account_and_differs_across_accounts_and_keys()
    {
        byte[] a1 = ScopeOf("alice"), a2 = ScopeOf("alice"), b = ScopeOf("bob");
        var otherKey = new byte[Audio.KeyStoreRules.ScopeBytes];
        Audio.KeyRecord.Scope(Bytes(32, 99), "alice", otherKey);

        Assert.Equal(a1, a2);
        Assert.NotEqual(a1, b);
        Assert.NotEqual(a1, otherKey);                                  // keyed: the same name under another data key is another scope
        Assert.Equal(Audio.KeyStoreRules.ScopeBytes, a1.Length);
    }

    // ── the proof table ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Proof_persists_a_validated_new_key_and_keeps_a_stored_one()
    {
        foreach (bool recognizable in new[] { true, false })
        {
            Assert.Equal(Audio.KeyProofVerdict.Persist, Audio.KeyStoreRules.OnProof(Audio.KeyOrigin.Ap, false, recognizable, true));
            Assert.Equal(Audio.KeyProofVerdict.Persist, Audio.KeyStoreRules.OnProof(Audio.KeyOrigin.Derived, false, recognizable, true));
            Assert.Equal(Audio.KeyProofVerdict.Keep, Audio.KeyStoreRules.OnProof(Audio.KeyOrigin.Disk, false, recognizable, true));
            Assert.Equal(Audio.KeyProofVerdict.Keep, Audio.KeyStoreRules.OnProof(Audio.KeyOrigin.Ap, true, recognizable, true));
        }
    }

    [Fact]
    public void Proof_forgets_a_stored_key_that_fails_a_recognizable_container()
    {
        Assert.Equal(Audio.KeyProofVerdict.Forget, Audio.KeyStoreRules.OnProof(Audio.KeyOrigin.Disk, false, true, false));
        Assert.Equal(Audio.KeyProofVerdict.Forget, Audio.KeyStoreRules.OnProof(Audio.KeyOrigin.Ap, true, true, false));
    }

    /// <summary>The native-decryptor case: a key that only works through the private host's decryptor fails AES-CTR
    /// validation, so it is never written — a stored key always works on its own.</summary>
    [Fact]
    public void Proof_skips_an_unproven_derived_key()
    {
        Assert.Equal(Audio.KeyProofVerdict.Skip, Audio.KeyStoreRules.OnProof(Audio.KeyOrigin.Derived, false, true, false));
        Assert.Equal(Audio.KeyProofVerdict.Skip, Audio.KeyStoreRules.OnProof(Audio.KeyOrigin.Ap, false, true, false));
        Assert.Equal(Audio.KeyProofVerdict.Skip, Audio.KeyStoreRules.OnProof(Audio.KeyOrigin.Derived, false, false, false));
    }

    [Fact]
    public void Proof_trusts_only_the_ap_for_mp3_and_aac()
    {
        Assert.Equal(Audio.KeyProofVerdict.Persist, Audio.KeyStoreRules.OnProof(Audio.KeyOrigin.Ap, false, false, false));
        Assert.Equal(Audio.KeyProofVerdict.Skip, Audio.KeyStoreRules.OnProof(Audio.KeyOrigin.Derived, false, false, false));
        Assert.Equal(Audio.KeyProofVerdict.Keep, Audio.KeyStoreRules.OnProof(Audio.KeyOrigin.Disk, false, false, false));   // unverifiable: never forgotten
        Assert.Equal(Audio.KeyProofVerdict.Keep, Audio.KeyStoreRules.OnProof(Audio.KeyOrigin.Ap, true, false, false));
    }

    [Fact]
    public void CanProve_needs_0xc0_bytes()
    {
        Assert.Equal(0xc0, Audio.KeyStoreRules.ProofBytes);
        Assert.False(Audio.KeyStoreRules.CanProve(0));
        Assert.False(Audio.KeyStoreRules.CanProve(0xbf));
        Assert.True(Audio.KeyStoreRules.CanProve(0xc0));
        Assert.True(Audio.KeyStoreRules.CanProve(64 * 1024));
    }

    [Fact]
    public void Recognizable_is_ogg_and_flac_only()
    {
        var recognizable = new[]
        {
            Audio.Format.OggVorbis96, Audio.Format.OggVorbis160, Audio.Format.OggVorbis320, Audio.Format.Flac, Audio.Format.Flac24,
        };
        foreach (Audio.Format f in Enum.GetValues<Audio.Format>())
            Assert.Equal(Array.IndexOf(recognizable, f) >= 0, Audio.KeyStoreRules.Recognizable(f));
        Assert.False(Audio.KeyStoreRules.Recognizable(Audio.Format.Mp3));
        Assert.False(Audio.KeyStoreRules.Recognizable(Audio.Format.Aac));
        Assert.False(Audio.KeyStoreRules.Recognizable(Audio.Format.Unknown));
    }

    // ── the cap, the touch ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Evict_is_zero_at_the_cap_and_trims_to_ninety_percent()
    {
        Assert.Equal(0, Audio.KeyStoreRules.EvictCount(0));
        Assert.Equal(0, Audio.KeyStoreRules.EvictCount(Audio.KeyStoreRules.MaxKeys));
        Assert.Equal(2001, Audio.KeyStoreRules.EvictCount(Audio.KeyStoreRules.MaxKeys + 1));     // 20001 → 18000
        Assert.Equal(0, Audio.KeyStoreRules.EvictCount(10, max: 10));
        Assert.Equal(2, Audio.KeyStoreRules.EvictCount(11, max: 10));                           // 11 → 9
        Assert.Equal(11, Audio.KeyStoreRules.EvictCount(101, max: 100));                        // 101 → 90
    }

    [Fact]
    public void Touch_is_daily_and_resets_on_a_clock_that_went_back()
    {
        const long now = 1_800_000_000;
        Assert.False(Audio.KeyStoreRules.ShouldTouch(now, now));
        Assert.False(Audio.KeyStoreRules.ShouldTouch(now - 86_399, now));
        Assert.True(Audio.KeyStoreRules.ShouldTouch(now - 86_400, now));
        Assert.True(Audio.KeyStoreRules.ShouldTouch(now - 10 * 86_400, now));
        Assert.True(Audio.KeyStoreRules.ShouldTouch(now + 1, now));                              // the clock went back
    }

    // ── the file at open ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RecreateWhy_names_the_reason_a_file_cannot_be_kept()
    {
        string schema = Audio.KeyStoreRules.Schema;

        Assert.Equal("unreadable:11", Audio.KeyStoreRules.RecreateWhy(11, false, false, null, false));
        Assert.Equal("unreadable:26", Audio.KeyStoreRules.RecreateWhy(26, true, true, schema, true));
        Assert.Equal("foreign", Audio.KeyStoreRules.RecreateWhy(0, hasMeta: false, hasTables: true, null, false));    // 0.2.9's audio_license file
        Assert.Equal("stale", Audio.KeyStoreRules.RecreateWhy(0, true, true, "audiokeys.v0", true));
        Assert.Equal("stale", Audio.KeyStoreRules.RecreateWhy(0, true, true, null, true));
        Assert.Equal("dek", Audio.KeyStoreRules.RecreateWhy(0, true, true, schema, false));
        Assert.Null(Audio.KeyStoreRules.RecreateWhy(0, hasMeta: false, hasTables: false, null, false));               // brand new
        Assert.Null(Audio.KeyStoreRules.RecreateWhy(0, true, true, schema, true));                                     // keep
        Assert.Null(Audio.KeyStoreRules.RecreateWhy(5, true, true, schema, true));                                     // BUSY is not a verdict on the file
    }

    // ── the phase machine ───────────────────────────────────────────────────────────────────────────────────────────

    static readonly Audio.KeyStorePhase[] Phases = Enum.GetValues<Audio.KeyStorePhase>();
    static readonly Audio.KeyStoreSignal[] Signals = Enum.GetValues<Audio.KeyStoreSignal>();

    [Fact]
    public void Phase_machine_detached_absorbs_everything()
    {
        foreach (Audio.KeyStoreSignal s in Signals)
            Assert.Equal(Audio.KeyStorePhase.Detached, Audio.KeyStoreRules.Next(Audio.KeyStorePhase.Detached, s));
    }

    [Fact]
    public void Phase_machine_setting_off_and_shutdown_win_from_every_attached_phase()
    {
        foreach (Audio.KeyStorePhase p in Phases)
        {
            if (p == Audio.KeyStorePhase.Detached) continue;
            Assert.Equal(Audio.KeyStorePhase.Off, Audio.KeyStoreRules.Next(p, Audio.KeyStoreSignal.SettingOff));
        }
        foreach (Audio.KeyStorePhase p in Phases)
            Assert.Equal(Audio.KeyStorePhase.Detached, Audio.KeyStoreRules.Next(p, Audio.KeyStoreSignal.Shutdown));
    }

    [Fact]
    public void Phase_machine_off_turns_on_to_idle_and_a_clear_while_off_stays_off()
    {
        Assert.Equal(Audio.KeyStorePhase.Idle, Audio.KeyStoreRules.Next(Audio.KeyStorePhase.Off, Audio.KeyStoreSignal.SettingOn));
        Assert.Equal(Audio.KeyStorePhase.Off, Audio.KeyStoreRules.Next(Audio.KeyStorePhase.Off, Audio.KeyStoreSignal.Cleared));
        Assert.Equal(Audio.KeyStorePhase.Off, Audio.KeyStoreRules.Next(Audio.KeyStorePhase.Off, Audio.KeyStoreSignal.Opened));
        Assert.Equal(Audio.KeyStorePhase.Off, Audio.KeyStoreRules.Next(Audio.KeyStorePhase.Off, Audio.KeyStoreSignal.Corrupt));
    }

    [Fact]
    public void Phase_machine_opens_fails_and_recovers()
    {
        Assert.Equal(Audio.KeyStorePhase.Open, Audio.KeyStoreRules.Next(Audio.KeyStorePhase.Idle, Audio.KeyStoreSignal.Opened));
        Assert.Equal(Audio.KeyStorePhase.Broken, Audio.KeyStoreRules.Next(Audio.KeyStorePhase.Idle, Audio.KeyStoreSignal.OpenFailed));
        Assert.Equal(Audio.KeyStorePhase.Idle, Audio.KeyStoreRules.Next(Audio.KeyStorePhase.Open, Audio.KeyStoreSignal.Corrupt));        // rebuilt on the next use
        Assert.Equal(Audio.KeyStorePhase.Broken, Audio.KeyStoreRules.Next(Audio.KeyStorePhase.Open, Audio.KeyStoreSignal.CorruptAgain));
        Assert.Equal(Audio.KeyStorePhase.Idle, Audio.KeyStoreRules.Next(Audio.KeyStorePhase.Broken, Audio.KeyStoreSignal.Cleared));     // the file is gone: try again
        Assert.Equal(Audio.KeyStorePhase.Idle, Audio.KeyStoreRules.Next(Audio.KeyStorePhase.Open, Audio.KeyStoreSignal.Cleared));
        Assert.Equal(Audio.KeyStorePhase.Broken, Audio.KeyStoreRules.Next(Audio.KeyStorePhase.Broken, Audio.KeyStoreSignal.Opened));     // broken stays broken for the session
        Assert.Equal(Audio.KeyStorePhase.Broken, Audio.KeyStoreRules.Next(Audio.KeyStorePhase.Broken, Audio.KeyStoreSignal.Corrupt));
        Assert.Equal(Audio.KeyStorePhase.Open, Audio.KeyStoreRules.Next(Audio.KeyStorePhase.Open, Audio.KeyStoreSignal.Opened));
    }

    // ── the paths ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PathUnder_is_beside_the_audio_cache()
    {
        string root = Path.Combine(Path.GetTempPath(), "wavee-keystore-rules");
        string path = Audio.KeyStoreRules.PathUnder(root);

        Assert.Equal("audiokeys.db", Path.GetFileName(path));
        Assert.Equal(Path.GetDirectoryName(Audio.DiskCache.DirectoryUnder(root)), Path.GetDirectoryName(path));
        Assert.StartsWith(root, path);
    }

    [Fact]
    public void IsLeftover_names_only_this_sets_dead_members()
    {
        Assert.True(Audio.KeyStoreRules.IsLeftover("audiokeys.db.dead-0123abcd"));
        Assert.True(Audio.KeyStoreRules.IsLeftover("audiokeys.db-wal.dead-0123ABCD"));
        Assert.True(Audio.KeyStoreRules.IsLeftover("AUDIOKEYS.DB-shm.dead-ffffffff"));

        Assert.False(Audio.KeyStoreRules.IsLeftover("audiokeys.db"));
        Assert.False(Audio.KeyStoreRules.IsLeftover("audiokeys.db-wal"));
        Assert.False(Audio.KeyStoreRules.IsLeftover("library.db.dead-0123abcd"));                 // the store's, not ours
        Assert.False(Audio.KeyStoreRules.IsLeftover("audiokeys.db.dead-0123abc"));                // 7 digits
        Assert.False(Audio.KeyStoreRules.IsLeftover("audiokeys.db.dead-0123abcg"));               // not hex
        Assert.False(Audio.KeyStoreRules.IsLeftover("audiokeys.db.bak.dead-0123abcd"));           // another file's member
        Assert.False(Audio.KeyStoreRules.IsLeftover("audiokeys.dbx.dead-0123abcd"));
        Assert.False(Audio.KeyStoreRules.IsLeftover("audiokeys.db.dead-0123abcd0"));              // the tag must be the END
        Assert.False(Audio.KeyStoreRules.IsLeftover(".dead-0123abcd"));
        Assert.False(Audio.KeyStoreRules.IsLeftover(""));
    }
}
