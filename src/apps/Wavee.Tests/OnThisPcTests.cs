// ── Wavee.Tests/OnThisPcTests.cs — the "on this PC" row fact (ZC2, owner Q29 / NQ4) ────────────────────────────────────
//
// `Playback.OnThisPcFacts` (Playback/OnThisPc.cs) over its seams: a real `ChunkDiskCache` in a TEMP folder, a real
// `AudioFileMap`, and a fake key question, key-store version and preferred quality. The poster and the background runner
// run inline, so a key answer lands in the same call that asked it. The fact is true only when all three hold: the file
// at the CURRENT quality is known, it is complete in the cache, and its key is saved; it flips back on an eviction, on a
// forgotten key and on a quality change, and every flip is announced on `Changed`. Indicator only: nothing here plays.

using Wavee;
using Wavee.Sdk.Streams;
using Xunit;
using Audio = Wavee.Spotify.Audio;

namespace Wavee.Tests;

public sealed class OnThisPcTests : IDisposable
{
    const int Chunk = ChunkDiskCache.ChunkBytes;
    const string Uri = "spotify:track:4uLU6hMCjMI75M1A2tKUQC";
    const string Other = "spotify:track:7ouMYWpwJ422jRcDASZB7P";
    const string File320 = "1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a";
    const string File160 = "2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b";
    const byte High160 = (byte)Audio.Quality.High160, VeryHigh320 = (byte)Audio.Quality.VeryHigh320;

    readonly string _dir = Path.Combine(Path.GetTempPath(), "wavee-onthispc-" + Guid.NewGuid().ToString("N"));
    readonly ChunkDiskCache _cache;
    readonly Playback.AudioFileMap _map = new();
    readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);
    readonly Playback.OnThisPcFacts _facts;
    long _keyVersion = 1;
    byte _quality = VeryHigh320;
    int _keyQuestions;
    readonly EntityId _track;

    public OnThisPcTests()
    {
        Directory.CreateDirectory(_dir);
        _cache = new ChunkDiskCache(_dir);
        _cache.BuildIndex();
        _facts = new Playback.OnThisPcFacts(_map, () => _cache, KeyPresent, () => Interlocked.Read(ref _keyVersion), () => _quality,
            post: static a => a(), background: static a => a());
        Assert.True(EntityId.TryParseGid(Uri, out _track));
    }

    public void Dispose()
    {
        _cache.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    bool KeyPresent(string fileIdHex)
    {
        Interlocked.Increment(ref _keyQuestions);
        lock (_keys) return _keys.Contains(fileIdHex);
    }

    void SaveKey(string fileIdHex)
    {
        lock (_keys) _keys.Add(fileIdHex);
        Interlocked.Increment(ref _keyVersion);
        _facts.NoteKeysChanged();
    }

    void ForgetKey(string fileIdHex)
    {
        lock (_keys) _keys.Remove(fileIdHex);
        Interlocked.Increment(ref _keyVersion);
        _facts.NoteKeysChanged();
    }

    void SkipWhenTheVolumeIsInsideTheReserve()
    {
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_dir))!);
        Assert.SkipUnless(Audio.DiskCache.CanCommit(drive.AvailableFreeSpace, 64L << 20, drive.TotalSize),
            "the temp volume is inside the cache's max(5 GiB, 5 %) free-space reserve, so every commit is refused by design");
    }

    /// <summary>Cache <paramref name="fileId"/> (2 chunks + a tail), all of it or all but the last chunk.</summary>
    void Cache(string fileId, bool complete = true)
    {
        long total = 2L * Chunk + 17;
        _cache.SetSize(fileId, total);
        int chunks = complete ? 3 : 2;
        for (int i = 0; i < chunks; i++)
        {
            int length = (int)Math.Min(Chunk, total - (long)i * Chunk);
            var bytes = new byte[length];
            for (int j = 0; j < length; j++) bytes[j] = (byte)(i * 13 + j);
            _cache.WriteChunk(fileId, i, bytes);
        }
        Assert.True(_cache.WaitForPendingWrites(30_000));
    }

    /// <summary>The whole fact, ready: the file is the 320 answer, it is complete, its key is saved.</summary>
    void Ready()
    {
        _map.Record(Uri, VeryHigh320, File320);
        Cache(File320);
        SaveKey(File320);
    }

    // ── true only when all three hold ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_complete_file_at_the_current_quality_with_its_key_saved_is_on_this_pc()
    {
        SkipWhenTheVolumeIsInsideTheReserve();
        Ready();
        Assert.True(_facts.Of(_track));
        Assert.True(EntityId.TryParseGid(Other, out EntityId other));
        Assert.False(_facts.Of(other));                                 // a track the map has never seen
        Assert.False(_facts.Of(default));
    }

    [Fact]
    public void Without_the_saved_key_it_is_not()
    {
        SkipWhenTheVolumeIsInsideTheReserve();
        _map.Record(Uri, VeryHigh320, File320);
        Cache(File320);
        Assert.False(_facts.Of(_track));
        Assert.False(_facts.Of(_track));
        Assert.Equal(1, _keyQuestions);                                 // the "no" is remembered until the store moves

        SaveKey(File320);
        Assert.True(_facts.Of(_track));
    }

    [Fact]
    public void An_incomplete_file_is_not_and_its_last_chunk_flips_it()
    {
        SkipWhenTheVolumeIsInsideTheReserve();
        _map.Record(Uri, VeryHigh320, File320);
        SaveKey(File320);
        Cache(File320, complete: false);
        Assert.False(_facts.Of(_track));
        Assert.Equal(0, _keyQuestions);                                 // the key store is asked only about complete files

        uint before = _facts.Changed.Peek();
        Cache(File320);
        Assert.True(_facts.Changed.Peek() > before);                    // the completion edge is announced
        Assert.True(_facts.Of(_track));
    }

    [Fact]
    public void Only_the_file_of_the_current_quality_counts()
    {
        SkipWhenTheVolumeIsInsideTheReserve();
        Ready();                                                        // complete and keyed at 320
        _map.Record(Uri, High160, File160);                              // 160 is a different file, not cached
        Assert.True(_facts.Of(_track));

        _quality = High160;
        uint before = _facts.Changed.Peek();
        Assert.False(_facts.Of(_track));
        Assert.True(_facts.Changed.Peek() > before);                    // a quality change makes every row ask again

        _quality = VeryHigh320;
        Assert.True(_facts.Of(_track));

        _map.Record(Other, VeryHigh320, File160);                        // a file the map knows but the cache does not
        Assert.True(EntityId.TryParseGid(Other, out EntityId other));
        Assert.False(_facts.Of(other));
    }

    // ── … and it flips back ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_eviction_flips_it_and_is_announced()
    {
        SkipWhenTheVolumeIsInsideTheReserve();
        Ready();
        Assert.True(_facts.Of(_track));
        uint before = _facts.Changed.Peek();

        _cache.Invalidate(File320);

        Assert.True(_facts.Changed.Peek() > before);
        Assert.False(_facts.Of(_track));
    }

    [Fact]
    public void Clearing_the_cache_flips_it()
    {
        SkipWhenTheVolumeIsInsideTheReserve();
        Ready();
        Assert.True(_facts.Of(_track));

        _cache.ClearAll();

        Assert.False(_facts.Of(_track));
    }

    [Fact]
    public void A_forgotten_key_flips_it()
    {
        SkipWhenTheVolumeIsInsideTheReserve();
        Ready();
        Assert.True(_facts.Of(_track));
        uint before = _facts.Changed.Peek();

        ForgetKey(File320);

        Assert.True(_facts.Changed.Peek() > before);
        Assert.False(_facts.Of(_track));
    }

    [Fact]
    public void A_steady_row_asks_nothing_new()
    {
        SkipWhenTheVolumeIsInsideTheReserve();
        Ready();
        Assert.True(_facts.Of(_track));
        int questions = _keyQuestions;
        uint changed = _facts.Changed.Peek();

        for (int i = 0; i < 100; i++) Assert.True(_facts.Of(_track));

        Assert.Equal(questions, _keyQuestions);                         // the answer is held until the key store moves
        Assert.Equal(changed, _facts.Changed.Peek());                   // and a read announces nothing
    }

    [Fact]
    public void Detached_answers_false()
    {
        Assert.True(EntityId.TryParseGid(Uri, out EntityId id));
        if (Playback.OnThisPc.Facts is null) Assert.False(Playback.OnThisPc.Of(id));
    }
}
