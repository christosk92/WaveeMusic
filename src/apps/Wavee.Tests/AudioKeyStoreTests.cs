// ── Wavee.Tests/AudioKeyStoreTests.cs — the saved audio keys on disk (G-123) ───────────────────────────────────────────
//
// `Spotify.Audio.KeyStore` (Spotify/Spotify.Audio.KeyStore.cs) is the shell over the pure rules AudioKeyStoreRulesTests
// pins: a real sqlite file in a TEMP folder, `NoOpProtector` (so nothing touches DPAPI or the profile), and an injected
// setting. The facts worth a file are the ones a user would feel: a key that has opened its own track survives a restart
// and is read BEFORE the deriver; a key that has not is never written; the toggle stops reads and writes but keeps the
// file; Clear works while a writer is mid-flight and mints a new data key; another account cannot see a key; and every
// way a file can be wrong at open (0.2.9's audio_license file, garbage, a data key this machine cannot unwrap) ends as a
// recreated file and a working store, never a thrown exception.
//
// The store is process-wide static, like the audio caches beside it, so these run alone with SpotifyAudioSeamTests (the
// other user of `Spotify.Audio.Key` and the session map): AudioKeyCollection. Every test ends with Shutdown.

using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Wavee;
using Xunit;
using Audio = Wavee.Spotify.Audio;

namespace Wavee.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AudioKeyCollection
{
    /// <summary>The key store and the session key map are process-wide statics; their tests run alone.</summary>
    public const string Name = "audio-keys";
}

[Collection(AudioKeyCollection.Name)]
public sealed class AudioKeyStoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "wavee-audiokeys-" + Guid.NewGuid().ToString("n"));
    readonly string _path;
    volatile bool _on = true;
    long _clock = 1_800_000_000;

    public AudioKeyStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, Audio.KeyStoreRules.FileName);
        Audio.ResetKeyLatch();
    }

    public void Dispose()
    {
        Audio.KeyStore.Shutdown();
        Audio.ResetKeyLatch();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────────────────────────

    static byte[] Bytes(int length, int seed)
    {
        var b = new byte[length];
        for (int i = 0; i < length; i++) b[i] = (byte)(seed * 31 + i * 7 + 1);
        return b;
    }

    static byte[] FileId(int n) => Bytes(20, n);
    static byte[] KeyOf(int n) => Bytes(16, 100 + n);
    static string Hex(byte[] id) => Convert.ToHexString(id).ToLowerInvariant();

    /// <summary>The first 256 bytes of a file whose plaintext has `OggS` at 0xa7, encrypted under <paramref name="key"/> —
    /// the only vector that can exist without shipping audio, and exactly what `Ctr.Validates` looks for.</summary>
    static byte[] OggCipher(byte[] key)
    {
        var plain = new byte[256];
        "OggS"u8.CopyTo(plain.AsSpan(Audio.Ctr.HeaderBytes));
        return Audio.Ctr.Decrypt(plain, key, 0);
    }

    void Use(ICredentialProtector? protector = null, int max = Audio.KeyStoreRules.MaxKeys, Func<long>? clock = null)
        => Audio.KeyStore.Use(_path, protector ?? new NoOpProtector(), () => _on, max, clock);

    /// <summary>One track's key held by the session and put through chunk 0's proof — the call `Body.Publish` makes.</summary>
    static Audio.KeyProofVerdict Prove(int n, Audio.KeyOrigin origin = Audio.KeyOrigin.Ap, Audio.Format fmt = Audio.Format.OggVorbis320)
    {
        byte[] id = FileId(n), key = KeyOf(n);
        Audio.Remember(Hex(id), id, key, origin);
        return Audio.KeyProof(Hex(id), fmt, OggCipher(key), key, out _);
    }

    static byte[]? Read(int n)
    {
        var buf = new byte[Spotify.AudioKey.KeyLength];
        return Audio.KeyStore.TryGet(FileId(n), buf) ? buf : null;
    }

    SqliteConnection Raw()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    static void Exec(SqliteConnection c, string sql)
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static object? Scalar(SqliteConnection c, string sql)
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    /// <summary>The wrapped data key as stored (plaintext here: `NoOpProtector`).</summary>
    byte[] ReadDek()
    {
        using SqliteConnection c = Raw();
        return (byte[])Scalar(c, "SELECT value FROM meta WHERE key='dek';")!;
    }

    /// <summary>Wraps by XOR, and can be told it has lost its master key (a copied profile, a reset DPAPI key).</summary>
    sealed class XorProtector(string scheme, bool failUnprotect = false) : ICredentialProtector
    {
        public string Scheme => scheme;
        public byte[] Protect(byte[] plaintext)
        {
            byte[] o = (byte[])plaintext.Clone();
            for (int i = 0; i < o.Length; i++) o[i] ^= 0x5A;
            return o;
        }
        public byte[] Unprotect(byte[] ciphertext)
            => failUnprotect ? throw new CryptographicException("master key reset") : Protect(ciphertext);
    }

    /// <summary>Holds the writer inside its open (the data key is wrapped under the store's lock) until released.</summary>
    sealed class GateProtector : ICredentialProtector
    {
        public readonly ManualResetEventSlim Entered = new(false), Release = new(false);
        public string Scheme => "gate";
        public byte[] Protect(byte[] plaintext) { Entered.Set(); Release.Wait(10_000); return plaintext; }
        public byte[] Unprotect(byte[] ciphertext) => ciphertext;
    }

    // ── survives a restart; read before the deriver ─────────────────────────────────────────────────────────────────

    [Fact]
    public void A_proven_key_survives_a_restart()
    {
        Use();
        Audio.KeyStore.UseAccount("alice");
        Assert.Equal(Audio.KeyProofVerdict.Persist, Prove(1));
        Audio.KeyStore.Flush(5000);
        Assert.Equal(1, Audio.KeyStore.Read().Writes);

        Use();                                                          // a restart: the old store drained, checkpointed and closed
        Audio.KeyStore.UseAccount("alice");
        byte[]? got = Read(1);

        Assert.NotNull(got);
        Assert.Equal(KeyOf(1), got);
        Assert.Equal("opened", Audio.KeyStore.Read().Outcome);
        Assert.Equal(1, Audio.KeyStore.Count());
    }

    [Fact]
    public void Key_reads_the_disk_before_the_deriver()
    {
        Func<Audio.KeyRequest, CancellationToken, byte[]?>? saved = Audio.KeyDeriver;
        try
        {
            byte[] id = FileId(2), key = KeyOf(2);
            int calls = 0;
            Audio.KeyDeriver = (_, _) => { Interlocked.Increment(ref calls); return key; };
            Use();
            Audio.KeyStore.UseAccount("alice");

            var buf = new byte[16];
            Assert.Equal(Audio.Fault.None, Audio.Key(Hex(id), id, new byte[16], buf, apEligible: false, CancellationToken.None));
            Assert.Equal(1, calls);
            Assert.Equal("derived", Audio.KeyOriginText(Hex(id)));
            Assert.Equal(Audio.KeyProofVerdict.Persist, Audio.KeyProof(Hex(id), Audio.Format.Flac, OggCipher(key), buf, out bool validates));
            Assert.True(validates);
            Audio.KeyStore.Flush(5000);

            Audio.ResetKeyLatch();                                      // an AP welcome: the session map is gone (G-034)
            Assert.Equal("-", Audio.KeyOriginText(Hex(id)));
            buf = new byte[16];
            Assert.Equal(Audio.Fault.None, Audio.Key(Hex(id), id, new byte[16], buf, apEligible: false, CancellationToken.None));

            Assert.Equal(1, calls);                                     // the disk answered; the deriver was not asked again
            Assert.Equal(key, buf);
            Assert.Equal("disk", Audio.KeyOriginText(Hex(id)));
        }
        finally { Audio.KeyDeriver = saved; }
    }

    // ── only proven keys are written ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_unproven_key_is_never_written()
    {
        Use();
        Audio.KeyStore.UseAccount("alice");

        // A key that only works through a native decryptor: chunk 0 does not open under it.
        byte[] id = FileId(3), key = KeyOf(3);
        Audio.Remember(Hex(id), id, key, Audio.KeyOrigin.Derived);
        Assert.Equal(Audio.KeyProofVerdict.Skip, Audio.KeyProof(Hex(id), Audio.Format.Flac, OggCipher(KeyOf(99)), key, out bool validates));
        Assert.False(validates);

        // An MP3 cannot be proven: a derived key is skipped (an AP key is trusted — the next fact).
        byte[] id4 = FileId(4), key4 = KeyOf(4);
        Audio.Remember(Hex(id4), id4, key4, Audio.KeyOrigin.Derived);
        Assert.Equal(Audio.KeyProofVerdict.Skip, Audio.KeyProof(Hex(id4), Audio.Format.Mp3, Bytes(256, 9), key4, out _));

        // Too little to prove anything: kept in the session, written nowhere.
        byte[] id5 = FileId(5), key5 = KeyOf(5);
        Audio.Remember(Hex(id5), id5, key5, Audio.KeyOrigin.Ap);
        Assert.Equal(Audio.KeyProofVerdict.Keep, Audio.KeyProof(Hex(id5), Audio.Format.OggVorbis320, OggCipher(key5)[..0x80], key5, out _));

        Audio.KeyStore.Flush(5000);
        Assert.Equal(0, Audio.KeyStore.Read().Writes);
        Assert.Null(Read(3));
        Assert.Null(Read(5));
        Assert.Equal(0, Audio.KeyStore.Count());
    }

    [Fact]
    public void An_mp3_key_from_the_ap_is_trusted_and_written()
    {
        Use();
        Audio.KeyStore.UseAccount("alice");
        byte[] id = FileId(6), key = KeyOf(6);
        Audio.Remember(Hex(id), id, key, Audio.KeyOrigin.Ap);

        // Noise: an MP3 has no magic to check, and an AP key is the one thing trusted without one.
        Assert.Equal(Audio.KeyProofVerdict.Persist, Audio.KeyProof(Hex(id), Audio.Format.Mp3, Bytes(256, 9), key, out bool validates));
        Assert.False(validates);
        Audio.KeyStore.Flush(5000);

        Assert.Equal(key, Read(6));
    }

    [Fact]
    public void A_stored_key_that_fails_its_cached_chunk_is_forgotten_and_not_served_again()
    {
        Use();
        Audio.KeyStore.UseAccount("alice");
        Prove(7);
        Audio.KeyStore.Flush(5000);
        Assert.NotNull(Read(7));

        // The key comes back off disk, and the track's cached chunk 0 says it is not the one (a replaced file).
        Audio.ResetKeyLatch();
        byte[] id = FileId(7), key = KeyOf(7);
        Audio.Remember(Hex(id), id, key, Audio.KeyOrigin.Disk);
        Assert.Equal(Audio.KeyProofVerdict.Forget, Audio.KeyProof(Hex(id), Audio.Format.OggVorbis320, OggCipher(KeyOf(98)), key, out _));

        Assert.Equal("-", Audio.KeyOriginText(Hex(id)));
        Assert.Null(Read(7));                                           // BEFORE the queued delete ran: the re-ask must not get it back
        Audio.KeyStore.Flush(5000);
        Assert.Null(Read(7));
        Assert.Equal(1, Audio.KeyStore.Read().Forgotten);
        Assert.Equal(0, Audio.KeyStore.Count());
    }

    [Fact]
    public void A_corrupt_row_reads_as_a_miss_and_is_deleted()
    {
        Use();
        Audio.KeyStore.UseAccount("alice");
        Prove(8);
        Audio.KeyStore.Flush(5000);
        Audio.KeyStore.Shutdown();

        using (SqliteConnection c = Raw())
            Exec(c, "UPDATE audio_key SET sealed = zeroblob(46);");

        Use();
        Audio.KeyStore.UseAccount("alice");
        Assert.Null(Read(8));
        Assert.Equal(1, Audio.KeyStore.Read().BadRecords);
        Audio.KeyStore.Flush(5000);
        Assert.Equal(0, Audio.KeyStore.Count());
        Assert.Equal("opened", Audio.KeyStore.Read().Outcome);          // a bad row is not a bad file
    }

    // ── the toggle ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Off_reads_nothing_writes_nothing_and_keeps_the_file()
    {
        Use();
        Audio.KeyStore.UseAccount("alice");
        Prove(10);
        Audio.KeyStore.Flush(5000);
        long bytes = Audio.KeyStore.DiskBytes();
        Assert.True(bytes > 0);

        _on = false;
        Assert.Null(Read(10));                                          // no reads
        Assert.Equal(Audio.KeyStorePhase.Off, Audio.KeyStore.Read().Phase);
        Assert.Null(Audio.KeyStore.Count());
        Assert.Equal(Audio.KeyProofVerdict.Persist, Prove(11));         // the proof is the same; the write is refused
        Audio.KeyStore.Flush(5000);
        Assert.Equal(1, Audio.KeyStore.Read().Writes);                  // no writes
        Assert.True(File.Exists(_path));                                // the file is kept
        Assert.True(Audio.KeyStore.DiskBytes() > 0);

        _on = true;
        Assert.Equal(KeyOf(10), Read(10));                              // back on: the file is reopened lazily and still there
        Assert.Null(Read(11));
        Assert.Equal(1, Audio.KeyStore.Count());
    }

    [Fact]
    public void Clear_while_off_deletes_the_file_and_stays_off()
    {
        Use();
        Audio.KeyStore.UseAccount("alice");
        Prove(12);
        Audio.KeyStore.Flush(5000);
        _on = false;
        Assert.Null(Read(12));

        Audio.KeyStore.Clear();

        Assert.False(File.Exists(_path));
        Assert.Equal(Audio.KeyStorePhase.Off, Audio.KeyStore.Read().Phase);
        Assert.Equal(0, Audio.KeyStore.DiskBytes());
    }

    // ── clear ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Clear_empties_the_store_and_the_session_map_and_mints_a_new_file()
    {
        Use();
        Audio.KeyStore.UseAccount("alice");
        Prove(20);
        Prove(21);
        Audio.KeyStore.Flush(5000);
        Assert.Equal(2, Audio.KeyStore.Count());
        byte[] dekBefore = ReadDek();

        Audio.KeyStore.Clear();

        Assert.False(File.Exists(_path));                               // the whole set went, not just the rows
        Assert.Equal("-", Audio.KeyOriginText(Hex(FileId(20))));        // a cleared key cannot be written back from memory
        Assert.Equal(0, Audio.KeyStore.Count());
        Assert.False(File.Exists(_path));                               // counting does not create the file
        Assert.Null(Read(20));
        Assert.True(File.Exists(_path));                                // the next use mints a fresh one…
        Assert.NotEqual(dekBefore, ReadDek());                          // …under a fresh data key

        Prove(22);                                                      // and it works
        Audio.KeyStore.Flush(5000);
        Use();
        Audio.KeyStore.UseAccount("alice");
        Assert.NotNull(Read(22));
        Assert.Null(Read(20));
    }

    [Fact]
    public async Task Queued_writes_before_a_clear_are_dropped()
    {
        var gate = new GateProtector();
        Use(gate);
        Audio.KeyStore.UseAccount("alice");                             // the writer starts, and stops inside its first open
        Assert.True(gate.Entered.Wait(5000));

        for (int i = 30; i < 33; i++) Assert.Equal(Audio.KeyProofVerdict.Persist, Prove(i));   // queued behind the open

        var clear = Task.Run(Audio.KeyStore.Clear);                     // bumps the generation, then waits for the store
        Thread.Sleep(200);
        gate.Release.Set();
        await clear.WaitAsync(TimeSpan.FromSeconds(10));
        Audio.KeyStore.Flush(5000);

        Assert.Equal(0, Audio.KeyStore.Read().Writes);                  // nothing queued before the clear was ever written
        Assert.Equal(0, Audio.KeyStore.Count());
        for (int i = 30; i < 33; i++) Assert.Null(Read(i));
    }

    // ── accounts ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Another_account_does_not_see_the_key()
    {
        Use();
        Audio.KeyStore.UseAccount("alice");
        Prove(40);
        Audio.KeyStore.Flush(5000);
        Assert.NotNull(Read(40));

        Audio.KeyStore.UseAccount("bob");
        Assert.Null(Read(40));                                          // another account's scope: a miss, not a replay

        Audio.KeyStore.UseAccount("alice");                             // logout keeps the keys
        Assert.Equal(KeyOf(40), Read(40));
        Assert.Equal(1, Audio.KeyStore.Count());
    }

    [Fact]
    public void Nothing_is_read_or_written_before_an_account_is_known()
    {
        Use();
        Prove(41);                                                      // no welcome yet
        Audio.KeyStore.Flush(5000);

        Assert.Null(Read(41));
        Assert.Equal(0, Audio.KeyStore.Read().Writes);
    }

    // ── the file at open ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_029_audio_license_file_is_recreated_as_foreign()
    {
        using (SqliteConnection c = Raw())
            Exec(c, "CREATE TABLE audio_license(fileId TEXT PRIMARY KEY, blob BLOB, saved_at INTEGER); "
                    + "INSERT INTO audio_license VALUES('abcd', x'0102', 1);");

        Use();
        Audio.KeyStore.UseAccount("alice");
        Assert.Null(Read(50));

        Assert.Equal("recreated:foreign", Audio.KeyStore.Read().Outcome);
        Assert.Equal(Audio.KeyStorePhase.Open, Audio.KeyStore.Read().Phase);
        using (SqliteConnection c = Raw())
            Assert.Equal(0L, Scalar(c, "SELECT count(*) FROM sqlite_master WHERE name='audio_license';"));

        Prove(50);                                                      // and it is a working store
        Audio.KeyStore.Flush(5000);
        Assert.NotNull(Read(50));
    }

    [Fact]
    public void A_garbage_file_is_recreated()
    {
        File.WriteAllBytes(_path, Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("this is not a database. ", 500))));

        Use();
        Audio.KeyStore.UseAccount("alice");
        Assert.Null(Read(51));

        Assert.StartsWith("recreated:unreadable:", Audio.KeyStore.Read().Outcome);
        Assert.Equal(Audio.KeyStorePhase.Open, Audio.KeyStore.Read().Phase);
        Prove(51);
        Audio.KeyStore.Flush(5000);
        Assert.NotNull(Read(51));
    }

    [Fact]
    public void A_protector_scheme_change_recreates_as_dek()
    {
        Use(new XorProtector("one"));
        Audio.KeyStore.UseAccount("alice");
        Prove(52);
        Audio.KeyStore.Flush(5000);
        Assert.Equal("created", Audio.KeyStore.Read().Outcome);

        Use(new XorProtector("two"));                                   // a profile moved to a machine with another scheme
        Audio.KeyStore.UseAccount("alice");
        Assert.Null(Read(52));
        Assert.Equal("recreated:dek", Audio.KeyStore.Read().Outcome);

        Prove(53);
        Audio.KeyStore.Flush(5000);
        Use(new XorProtector("two"));                                   // the new file keeps its own keys
        Audio.KeyStore.UseAccount("alice");
        Assert.NotNull(Read(53));
        Assert.Equal("opened", Audio.KeyStore.Read().Outcome);
    }

    [Fact]
    public void A_data_key_that_cannot_be_unwrapped_recreates_as_dek()
    {
        Use(new XorProtector("one"));
        Audio.KeyStore.UseAccount("alice");
        Prove(54);
        Audio.KeyStore.Flush(5000);

        Use(new XorProtector("one", failUnprotect: true));              // the same scheme, but the master key was reset
        Audio.KeyStore.UseAccount("alice");
        Assert.Null(Read(54));

        Assert.Equal("recreated:dek", Audio.KeyStore.Read().Outcome);
    }

    [Fact]
    public void A_leftover_dead_member_of_this_set_is_cleaned_at_open()
    {
        string dead = _path + "-wal.dead-0123abcd";                     // what a delete that could not finish leaves behind
        string theirs = Path.Combine(_dir, "library.db.dead-0123abcd");
        string decoy = _path + ".bak";
        foreach (string f in new[] { dead, theirs, decoy }) File.WriteAllText(f, "x");

        Use();
        Audio.KeyStore.UseAccount("alice");
        Assert.Null(Read(55));

        Assert.False(File.Exists(dead));
        Assert.True(File.Exists(theirs));                               // not this set's: never touched
        Assert.True(File.Exists(decoy));
    }

    // ── the cap, the doors, the exit ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_cap_trims_oldest_by_last_used()
    {
        Use(max: 10, clock: () => Interlocked.Increment(ref _clock));
        Audio.KeyStore.UseAccount("alice");
        for (int i = 60; i < 71; i++) Assert.Equal(Audio.KeyProofVerdict.Persist, Prove(i));    // 11 keys over a cap of 10
        Audio.KeyStore.Flush(5000);

        Assert.Equal(9, Audio.KeyStore.Count());                        // trimmed to 90 %, however the writer batched them
        Assert.Null(Read(60));
        Assert.Null(Read(61));
        for (int i = 62; i < 71; i++) Assert.Equal(KeyOf(i), Read(i));
    }

    [Fact]
    public void Detached_without_a_path_does_nothing_and_throws_nothing()
    {
        Audio.KeyStore.Use(null, new NoOpProtector(), () => true);
        Audio.KeyStore.UseAccount("alice");
        Prove(70);

        Assert.Equal(Audio.KeyStorePhase.Detached, Audio.KeyStore.Read().Phase);
        Assert.Null(Read(70));
        Assert.Null(Audio.KeyStore.Count());
        Audio.KeyStore.Flush(100);
        Audio.KeyStore.Clear();
        Audio.KeyStore.Shutdown();
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void Shutdown_drains_the_queue_and_checkpoints_the_wal()
    {
        Use();
        Audio.KeyStore.UseAccount("alice");
        for (int i = 80; i < 84; i++) Prove(i);                         // no Flush: Shutdown is the drain

        Audio.KeyStore.Shutdown();

        Assert.Equal(Audio.KeyStorePhase.Detached, Audio.KeyStore.Read().Phase);
        Assert.False(File.Exists(_path + "-wal") && new FileInfo(_path + "-wal").Length > 0, "the WAL was folded into the main file");
        Use();
        Audio.KeyStore.UseAccount("alice");
        for (int i = 80; i < 84; i++) Assert.NotNull(Read(i));
    }

    [Fact]
    public void The_whole_wal_set_is_counted()
    {
        Use();
        Audio.KeyStore.UseAccount("alice");
        Prove(90);
        Audio.KeyStore.Flush(5000);

        long expected = 0;
        foreach (string suffix in Audio.KeyStoreRules.MemberSuffixes)
            if (File.Exists(_path + suffix)) expected += new FileInfo(_path + suffix).Length;

        Assert.Equal(expected, Audio.KeyStore.DiskBytes());
        Assert.True(expected > 0);
    }
}
