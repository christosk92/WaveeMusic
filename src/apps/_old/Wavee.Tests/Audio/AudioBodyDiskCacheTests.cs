using System;
using System.IO;
using System.Threading;
using System.Collections.Generic;
using Wavee.Backend.Audio;
using Xunit;
using Wavee.Sdk.Streams;

namespace Wavee.Tests.Audio;

public class AudioBodyDiskCacheTests
{
    static string TempDir() => Path.Combine(Path.GetTempPath(), "wavee-body-cache-" + Guid.NewGuid());

    [Fact]
    public void WriteThenRead_RoundTripsChunk()
    {
        var dir = TempDir();
        using var cache = new ChunkDiskCache(dir);
        var data = A.Bytes(3, AudioBodyDiskCache.ChunkBytes);
        cache.SetSize("fileA", data.Length);
        cache.WriteChunk("fileA", 0, data);
        var buf = new byte[AudioBodyDiskCache.ChunkBytes];
        Assert.True(cache.TryReadChunk("fileA", 0, buf, out int len));
        Assert.Equal(data.Length, len);
        Assert.Equal(data, buf.AsSpan(0, len).ToArray());
        cache.ClearAll();
        Directory.Delete(dir, true);
    }

    [Fact]
    public void SparseChunks_MissesGap()
    {
        var dir = TempDir();
        using var cache = new ChunkDiskCache(dir);
        cache.SetSize("f", AudioBodyDiskCache.ChunkBytes * 3L);
        cache.WriteChunk("f", 0, A.Bytes(1, AudioBodyDiskCache.ChunkBytes));
        cache.WriteChunk("f", 2, A.Bytes(2, AudioBodyDiskCache.ChunkBytes));
        var buf = new byte[AudioBodyDiskCache.ChunkBytes];
        Assert.True(cache.TryReadChunk("f", 0, buf, out _));
        Assert.False(cache.TryReadChunk("f", 1, buf, out _));
        Assert.True(cache.TryReadChunk("f", 2, buf, out _));
        cache.ClearAll();
        Directory.Delete(dir, true);
    }

    [Fact]
    public void TornWrite_DataWithoutMapBit_IsMiss()
    {
        var dir = TempDir();
        using var cache = new ChunkDiskCache(dir);
        string enc = Path.Combine(dir, "torn.enc");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(enc, A.Bytes(9, AudioBodyDiskCache.ChunkBytes));
        var buf = new byte[AudioBodyDiskCache.ChunkBytes];
        Assert.False(cache.TryReadChunk("torn", 0, buf, out _));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void SetSize_PersistsAcrossInstances()
    {
        var dir = TempDir();
        using (var cache1 = new ChunkDiskCache(dir)) cache1.SetSize("sz", 1_234_567);
        using var cache2 = new ChunkDiskCache(dir);
        Assert.Equal(1_234_567, cache2.KnownSize("sz"));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void TailChunk_RoundTripsExactLength()
    {
        var dir = TempDir();
        const int tail = 137;
        long size = AudioBodyDiskCache.ChunkBytes + tail;
        using var cache = new ChunkDiskCache(dir);
        cache.SetSize("tail", size);
        var data = A.Bytes(7, tail);
        cache.WriteChunk("tail", 1, data);
        // WriteChunk is fire-and-forget; without this, TryReadChunk below is served straight out of _pending and never
        // exercises WriteChunkCore's tail-length guard (`if (data.Length != expected) return;`) — the exact thing this
        // test exists to prove.
        cache.WaitForPendingWrites();
        var buffer = new byte[AudioBodyDiskCache.ChunkBytes];
        Assert.True(cache.TryReadChunk("tail", 1, buffer, out int length));
        Assert.Equal(tail, length);
        Assert.Equal(data, buffer[..length]);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void CorruptCiphertext_IsRejectedByDigest()
    {
        var dir = TempDir();
        using var cache = new ChunkDiskCache(dir);
        cache.SetSize("corrupt", AudioBodyDiskCache.ChunkBytes);
        cache.WriteChunk("corrupt", 0, A.Bytes(4, AudioBodyDiskCache.ChunkBytes));
        // WriteChunk is fire-and-forget now: without this wait, (a) the .enc below may not exist yet (GetFiles throws
        // "Sequence contains no elements"), and (b) even if it did, TryReadChunk would still be served out of _pending
        // and never see the on-disk corruption at all. The writer removes the _pending entry (ChunkDiskCache.cs:193)
        // BEFORE decrementing the pending counter (:195), so once this returns the chunk is both on disk and out of
        // the in-memory map — do not remove this wait.
        cache.WaitForPendingWrites();
        string enc = Directory.GetFiles(dir, "*.enc", SearchOption.AllDirectories).Single();
        using (var fs = new FileStream(enc, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        { fs.Position = 17; fs.WriteByte(0xff); }
        var buffer = new byte[AudioBodyDiskCache.ChunkBytes];
        Assert.False(cache.TryReadChunk("corrupt", 0, buffer, out _));
        Assert.False(cache.TryReadChunk("corrupt", 0, buffer, out _));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void DisabledPolicy_StopsWritesButKeepsReads_AndResumesLive()
    {
        var parent = TempDir();
        var settings = new MutableSettings();
        settings.Set(WaveeSettings.AudioBodyCacheBasePath, parent);
        settings.Set(WaveeSettings.AudioBodyCacheBudgetMode, (int)AudioCacheBudgetMode.Unlimited);
        settings.Set(WaveeSettings.AudioBodyCacheEnabled, true);
        using var cache = AudioBodyDiskCache.FromSettings(settings);
        var data = A.Bytes(5, AudioBodyDiskCache.ChunkBytes);

        cache.SetSize("enabled", data.Length);
        cache.WriteChunk("enabled", 0, data);
        settings.Set(WaveeSettings.AudioBodyCacheEnabled, false);
        var read = new byte[data.Length];
        Assert.True(cache.TryReadChunk("enabled", 0, read, out _));

        cache.SetSize("disabled", data.Length);
        cache.WriteChunk("disabled", 0, data);
        Assert.Null(cache.KnownSize("disabled"));

        settings.Set(WaveeSettings.AudioBodyCacheEnabled, true);
        cache.SetSize("disabled", data.Length);
        cache.WriteChunk("disabled", 0, data);
        Assert.True(cache.TryReadChunk("disabled", 0, read, out _));
        Directory.Delete(parent, true);
    }

    [Fact]
    public void Constructor_DoesNotScan_WarmScanReconciles()
    {
        var dir = TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "ab"));
        string staleTmp = Path.Combine(dir, "ab", "stale.tmp");
        File.WriteAllBytes(staleTmp, new byte[16]);
        File.SetLastWriteTimeUtc(staleTmp, DateTime.UtcNow - TimeSpan.FromHours(1));
        string freshTmp = Path.Combine(dir, "ab", "fresh.tmp");
        File.WriteAllBytes(freshTmp, new byte[16]);
        string orphanEnc = Path.Combine(dir, "ab", "0123456789abcdef.enc");
        File.WriteAllBytes(orphanEnc, new byte[32]);
        string badMap = Path.Combine(dir, "ab", "deadbeefdeadbeef.map");
        File.WriteAllBytes(badMap, new byte[8]);

        using var cache = new ChunkDiskCache(dir);
        // Construction must stay CHEAP — the ctor-time sweep was the 16–31 s "Starting audio" login stall
        // (golive.audio_ms). The crashed-session leftovers are still on disk until the off-path WarmScan runs.
        Assert.True(File.Exists(staleTmp));
        Assert.True(File.Exists(orphanEnc));
        Assert.True(File.Exists(badMap));

        var data = A.Bytes(6, AudioBodyDiskCache.ChunkBytes);
        cache.SetSize("live", data.Length);
        cache.WriteChunk("live", 0, data);
        // WriteChunk is fire-and-forget; without this wait, WarmScan below would enumerate whatever happened to have
        // landed on disk yet (racing the background writer) instead of deterministically exercising a real .map/.enc
        // pair alongside the reaped leftovers.
        cache.WaitForPendingWrites();

        cache.WarmScan();
        Assert.False(File.Exists(staleTmp));    // crashed-session leftovers reaped…
        Assert.False(File.Exists(orphanEnc));
        Assert.False(File.Exists(badMap));
        Assert.True(File.Exists(freshTmp));     // …but a fresh tmp (a possibly-live EnsureMap intermediate) survives
        var buf = new byte[AudioBodyDiskCache.ChunkBytes];
        Assert.True(cache.TryReadChunk("live", 0, buf, out int len));   // valid pairs untouched
        Assert.Equal(data.Length, len);

        // Idempotent: the owed scan was claimed above, so a second call is a no-op (a new orphan is NOT reaped).
        string orphan2 = Path.Combine(dir, "cd", "fedcba9876543210.enc");
        Directory.CreateDirectory(Path.Combine(dir, "cd"));
        File.WriteAllBytes(orphan2, new byte[32]);
        cache.WarmScan();
        Assert.True(File.Exists(orphan2));
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Relocation_MoveCopiesVerifiedChunksAndDeletesOldPairs()
    {
        var oldRoot = TempDir();
        var newParent = TempDir();
        using var cache = new ChunkDiskCache(oldRoot);
        var data = A.Bytes(12, AudioBodyDiskCache.ChunkBytes);
        cache.SetSize("move-me", data.Length);
        cache.WriteChunk("move-me", 0, data);

        Assert.True(await cache.PrepareRelocationAsync(newParent, AudioCacheRelocationMode.Move));
        Assert.Empty(Directory.GetFiles(oldRoot, "*.enc", SearchOption.AllDirectories));
        using var relocated = new ChunkDiskCache(AudioBodyDiskCache.ResolveDirectory(newParent));
        var read = new byte[data.Length];
        Assert.True(relocated.TryReadChunk("move-me", 0, read, out int length));
        Assert.Equal(data, read[..length]);
        Directory.Delete(oldRoot, true);
        Directory.Delete(newParent, true);
    }

    sealed class MutableSettings : IAppSettings
    {
        readonly Dictionary<string, object> _values = new();
        public T Get<T>(SettingKey<T> key) => _values.TryGetValue(key.Name, out var value) && value is T typed ? typed : key.Default;
        public void Set<T>(SettingKey<T> key, T value) { if (value is not null) _values[key.Name] = value; }
    }
}
