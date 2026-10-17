// ── Wavee.Tests/ChunkDiskCacheIndexTests.cs — the cache's completeness index and the (track, quality) → file id map (ZC2) ──
//
// `ChunkDiskCache.IsComplete` is the "bytes" third of the "on this PC" row fact (Playback/OnThisPc.cs): an in-memory index
// the writer, the read-side discard, Invalidate, Trim and ClearAll keep, seeded for a cold root by one read-only sweep. These
// pin that it follows every one of them, that a rewritten chunk is not counted twice, that a fresh instance's sweep finds a
// file another instance completed, and that it announces each edge. The map's file round trip is the other persisted half.
//
// Real files in a TEMP folder, never the profile. A commit needs the volume outside the cache's free-space reserve, so the
// writing tests skip (as AudioStreamTests' disk tests do) on a volume inside it.

using System.Collections.Concurrent;
using Wavee;
using Wavee.Sdk.Streams;
using Xunit;
using Audio = Wavee.Spotify.Audio;

namespace Wavee.Tests;

/// <summary>Runs with the audio stream tests: the trim case writes past the cache's 64 MiB floor, and those tests are
/// timing-honest (AudioStreamCollection).</summary>
[Collection(AudioStreamCollection.Name)]
public sealed class ChunkDiskCacheIndexTests : IDisposable
{
    const int Chunk = ChunkDiskCache.ChunkBytes;
    const string FileA = "0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a";
    const string FileB = "0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b";

    readonly string _dir = Path.Combine(Path.GetTempPath(), "wavee-cacheindex-" + Guid.NewGuid().ToString("N"));

    public ChunkDiskCacheIndexTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────────────────────────

    static void SkipWhenTheVolumeIsInsideTheReserve(string directory, long bytes = 64L << 20)
    {
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!);
        Assert.SkipUnless(Audio.DiskCache.CanCommit(drive.AvailableFreeSpace, bytes, drive.TotalSize),
            "the temp volume is inside the cache's max(5 GiB, 5 %) free-space reserve, so every commit is refused by design");
    }

    static byte[] ChunkOf(long total, int index, int seed)
    {
        int length = (int)Math.Min(Chunk, total - (long)index * Chunk);
        var bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = (byte)(seed * 31 + index * 7 + i);
        return bytes;
    }

    /// <summary>Declare <paramref name="total"/> bytes for <paramref name="fileId"/> and commit the chunks named.</summary>
    static void Write(ChunkDiskCache cache, string fileId, long total, params int[] chunks)
    {
        cache.SetSize(fileId, total);
        foreach (int index in chunks) cache.WriteChunk(fileId, index, ChunkOf(total, index, fileId[1]));
        Assert.True(cache.WaitForPendingWrites(60_000));
    }

    static int ChunkCount(long total) => (int)((total + Chunk - 1) / Chunk);

    static int[] All(long total) => Enumerable.Range(0, ChunkCount(total)).ToArray();

    sealed class Edges
    {
        public readonly ConcurrentQueue<string?> Seen = new();
        public void On(string? stem) => Seen.Enqueue(stem);
    }

    // ── the index follows the writer ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_file_is_complete_only_when_its_last_chunk_commits_and_the_edge_is_announced_once()
    {
        SkipWhenTheVolumeIsInsideTheReserve(_dir);
        using var cache = new ChunkDiskCache(_dir);
        cache.BuildIndex();
        var edges = new Edges();
        cache.CompletionChanged += edges.On;
        long total = 2L * Chunk + 100;

        Write(cache, FileA, total, 0, 1);
        Assert.False(cache.IsComplete(FileA));
        Assert.Empty(edges.Seen);

        Write(cache, FileA, total, 2);
        Assert.True(cache.IsComplete(FileA));
        Assert.True(cache.IsComplete(FileA.ToUpperInvariant()));      // a file id in either case is the same file
        Assert.Equal(new string?[] { FileA }, edges.Seen.ToArray());
        Assert.False(cache.IsComplete(FileB));
    }

    [Fact]
    public void A_rewritten_chunk_is_not_counted_twice()
    {
        SkipWhenTheVolumeIsInsideTheReserve(_dir);
        using var cache = new ChunkDiskCache(_dir);
        cache.BuildIndex();
        long total = 3L * Chunk;

        Write(cache, FileA, total, 0, 0, 1, 1);
        Assert.False(cache.IsComplete(FileA));                         // 2 of 3, however often the first two landed
        Write(cache, FileA, total, 2);
        Assert.True(cache.IsComplete(FileA));
    }

    [Fact]
    public void A_new_size_starts_the_file_over()
    {
        SkipWhenTheVolumeIsInsideTheReserve(_dir);
        using var cache = new ChunkDiskCache(_dir);
        cache.BuildIndex();
        Write(cache, FileA, Chunk + 10L, All(Chunk + 10L));
        Assert.True(cache.IsComplete(FileA));

        cache.SetSize(FileA, 2L * Chunk + 10);                         // a different size discards the entry
        Assert.False(cache.IsComplete(FileA));
    }

    // ── … and every way a file goes ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Invalidate_drops_the_file_and_announces_it()
    {
        SkipWhenTheVolumeIsInsideTheReserve(_dir);
        using var cache = new ChunkDiskCache(_dir);
        cache.BuildIndex();
        Write(cache, FileA, Chunk + 1L, All(Chunk + 1L));
        Write(cache, FileB, Chunk + 1L, All(Chunk + 1L));
        var edges = new Edges();
        cache.CompletionChanged += edges.On;

        cache.Invalidate(FileA);

        Assert.False(cache.IsComplete(FileA));
        Assert.True(cache.IsComplete(FileB));
        Assert.Equal(new string?[] { FileA }, edges.Seen.ToArray());
    }

    [Fact]
    public void ClearAll_empties_the_index_and_says_anything_may_have_changed()
    {
        SkipWhenTheVolumeIsInsideTheReserve(_dir);
        using var cache = new ChunkDiskCache(_dir);
        cache.BuildIndex();
        Write(cache, FileA, Chunk + 1L, All(Chunk + 1L));
        Write(cache, FileB, Chunk + 1L, All(Chunk + 1L));
        var edges = new Edges();
        cache.CompletionChanged += edges.On;
        int before = cache.CompletionVersion;

        cache.ClearAll();

        Assert.False(cache.IsComplete(FileA));
        Assert.False(cache.IsComplete(FileB));
        Assert.True(cache.IndexReady);                                 // the empty index IS the truth after a clear
        Assert.Contains(null, edges.Seen);
        Assert.True(cache.CompletionVersion > before);
    }

    [Fact]
    public void A_trim_that_evicts_a_complete_file_flips_it()
    {
        long big = ChunkDiskCache.MinBudgetBytes + 4L * Chunk;          // crosses the smallest budget the cache honours
        SkipWhenTheVolumeIsInsideTheReserve(_dir, big + (8L << 20));
        using var cache = new ChunkDiskCache(_dir, ChunkDiskCache.MinBudgetBytes);
        cache.BuildIndex();
        Write(cache, FileA, Chunk + 1L, All(Chunk + 1L));
        Assert.True(cache.IsComplete(FileA));
        var edges = new Edges();
        cache.CompletionChanged += edges.On;

        // The write that crosses the budget trims least-recently-used first: FileA was written before every chunk of FileB.
        Write(cache, FileB, big, All(big));
        cache.Trim();

        Assert.False(cache.IsComplete(FileA));
        Assert.Contains(FileA, edges.Seen);
    }

    [Fact]
    public void A_chunk_that_fails_its_digest_on_read_takes_the_file_out_of_the_index()
    {
        SkipWhenTheVolumeIsInsideTheReserve(_dir);
        using var cache = new ChunkDiskCache(_dir);
        cache.BuildIndex();
        long total = 2L * Chunk;
        Write(cache, FileA, total, All(total));
        Assert.True(cache.IsComplete(FileA));

        string enc = Path.Combine(_dir, FileA[..2], FileA + ".enc");
        using (var fs = new FileStream(enc, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            fs.Position = Chunk + 5;
            int b = fs.ReadByte();
            fs.Position = Chunk + 5;
            fs.WriteByte((byte)(b ^ 0xFF));
        }

        Assert.False(cache.TryReadChunk(FileA, 1, new byte[Chunk], out _));
        Assert.False(cache.IsComplete(FileA));

        Write(cache, FileA, total, 1);                                 // refetched: complete again, counted once
        Assert.True(cache.IsComplete(FileA));
    }

    // ── the sweep ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_fresh_instance_finds_what_an_earlier_one_completed_and_skips_what_it_did_not()
    {
        SkipWhenTheVolumeIsInsideTheReserve(_dir);
        using (var first = new ChunkDiskCache(_dir))
        {
            Write(first, FileA, 2L * Chunk + 3, All(2L * Chunk + 3));
            Write(first, FileB, 2L * Chunk + 3, 0, 2);                  // one chunk short
        }

        using var second = new ChunkDiskCache(_dir);
        var edges = new Edges();
        second.CompletionChanged += edges.On;
        second.BuildIndex();

        Assert.True(second.IndexReady);
        Assert.True(second.IsComplete(FileA));
        Assert.False(second.IsComplete(FileB));
        Assert.Contains(null, edges.Seen);                             // the sweep landing is announced

        Write(second, FileB, 2L * Chunk + 3, 1);                       // the writer continues the sweep's count
        Assert.True(second.IsComplete(FileB));
    }

    [Fact]
    public void The_sweep_never_deletes_and_ignores_a_map_it_cannot_read()
    {
        string shard = Path.Combine(_dir, "0c");
        Directory.CreateDirectory(shard);
        string junk = Path.Combine(shard, "0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c.map");
        File.WriteAllBytes(junk, [1, 2, 3, 4]);

        using var cache = new ChunkDiskCache(_dir);
        cache.BuildIndex();

        Assert.True(cache.IndexReady);
        Assert.False(cache.IsComplete("0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c"));
        Assert.True(File.Exists(junk));                                // the reconcile is WarmScan's job, not the sweep's
    }

    // ── the persisted (track, quality) → file id map ────────────────────────────────────────────────────────────────

    const string TrackOne = "spotify:track:4uLU6hMCjMI75M1A2tKUQC";
    const string TrackTwo = "spotify:track:7ouMYWpwJ422jRcDASZB7P";
    const string Episode = "spotify:episode:512ojhOuo1ktJprKbVcKyQ";

    [Fact]
    public void The_map_answers_per_quality_and_reports_only_real_changes()
    {
        var map = new Playback.AudioFileMap();
        Assert.True(map.Record(TrackOne, 2, FileA));
        Assert.True(map.Record(TrackOne, 3, FileB));                   // the lossless rung is a different file
        Assert.False(map.Record(TrackOne, 2, FileA.ToUpperInvariant())); // the same answer, in another case
        int version = map.Version;
        Assert.True(map.Record(TrackOne, 2, FileB));
        Assert.True(map.Version > version);

        Assert.True(map.TryGet(TrackOne, 3, out string? lossless));
        Assert.Equal(FileB, lossless);
        Assert.False(map.TryGet(TrackOne, 1, out _));
        Assert.False(map.TryGet(TrackTwo, 2, out _));

        Assert.False(map.Record("spotify:album:4uLU6hMCjMI75M1A2tKUQC", 2, FileA));   // not a playable
        Assert.False(map.Record(TrackTwo, 4, FileA));                                  // not a rung
        Assert.False(map.Record(TrackTwo, 2, "ext1234abcd"));                          // an external body's id
        Assert.Equal(2, map.Count);
    }

    [Fact]
    public void The_map_round_trips_through_its_file_and_keeps_what_the_session_already_recorded()
    {
        string path = Path.Combine(_dir, "Cache", Playback.AudioFileMap.FileName);
        var saved = new Playback.AudioFileMap();
        saved.Record(TrackOne, 2, FileA);
        saved.Record(TrackOne, 3, FileB);
        saved.Record(Episode, 1, FileB);
        Assert.True(saved.Save(path));

        var loaded = new Playback.AudioFileMap();
        loaded.Record(TrackTwo, 2, FileA);                              // this session's answer
        loaded.Record(TrackOne, 2, FileB);                              // newer than the file's: it wins
        Assert.Equal(2, loaded.Load(path));

        Assert.True(loaded.TryGet(TrackOne, 2, out string? q2));
        Assert.Equal(FileB, q2);
        Assert.True(loaded.TryGet(TrackOne, 3, out string? q3));
        Assert.Equal(FileB, q3);
        Assert.True(loaded.TryGet(Episode, 1, out string? ep));
        Assert.Equal(FileB, ep);
        Assert.True(loaded.TryGet(TrackTwo, 2, out string? two));
        Assert.Equal(FileA, two);
        Assert.True(EntityId.TryParseGid(TrackOne, out EntityId id));
        Assert.True(loaded.TryGet(id, 3, out _));                       // the row's key: the packed id, no text
    }

    [Fact]
    public void A_map_file_this_build_does_not_recognise_is_ignored_and_bad_lines_are_skipped()
    {
        string path = Path.Combine(_dir, Playback.AudioFileMap.FileName);
        File.WriteAllText(path, "something.else.v9\n" + TrackOne + "\t2\t" + FileA + "\n");
        var map = new Playback.AudioFileMap();
        Assert.Equal(0, map.Load(path));
        Assert.Equal(0, map.Count);

        File.WriteAllText(path, string.Join('\n',
            "wavee.audiofiles.v1",
            TrackOne + "\t2\t" + FileA,
            TrackTwo + "\t9\t" + FileA,                                  // not a rung
            TrackTwo + "\t2\tnot-hex",                                   // not a file id
            "garbage",
            "spotify:artist:4uLU6hMCjMI75M1A2tKUQC\t2\t" + FileA,        // not a playable
            Episode + "\t0\t" + FileB) + "\n");
        Assert.Equal(2, map.Load(path));
        Assert.True(map.TryGet(TrackOne, 2, out _));
        Assert.True(map.TryGet(Episode, 0, out _));
        Assert.False(map.TryGet(TrackTwo, 2, out _));
        Assert.Equal(0, new Playback.AudioFileMap().Load(Path.Combine(_dir, "missing.tsv")));
    }
}
