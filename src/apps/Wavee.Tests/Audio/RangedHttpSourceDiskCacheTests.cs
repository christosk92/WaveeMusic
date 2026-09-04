using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using Wavee.Backend.Audio;
using Xunit;
using Wavee.Sdk.Streams;

namespace Wavee.Tests.Audio;

public class RangedHttpSourceDiskCacheTests
{
    // A volume with room to spare, injected so these tests assert the CACHE's behavior rather than the
    // developer's free disk space. Without it they pass or fail on how full C: happens to be — which is
    // exactly how a completely dead cache (reserve 47.6 GiB vs 25.3 GiB free) went unnoticed.
    static Func<string, (long Total, long Free, bool Ready)> FatVolume => _ => (1L << 40, 900L << 30, true);

    // A volume whose free space sits below the cache's floor reserve (max(5 GiB, total/20) — 5 GiB for a
    // 100 GiB volume), so a write must be REFUSED AND REPORTED rather than silently dropped.
    static Func<string, (long Total, long Free, bool Ready)> TightVolume => _ => (100L << 30, 1L << 30, true);

    [Fact]
    public void SecondStream_ServesFromDisk_ZeroAdditionalHttp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wavee-range-disk-" + Guid.NewGuid());
        var disk = new ChunkDiskCache(dir, budgetBytes: 64 << 20, volumeProbe: FatVolume);
        int calls = 0;
        const long size = 200_000;
        const string fileId = "deadbeef";
        var body = A.Bytes(3, (int)size);
        var handler = new RangeAwareHandler(body, () => Interlocked.Increment(ref calls));
        using var http = new HttpClient(handler);

        using (var src1 = new RangedHttpSource(http, fileId, default, 0, null, disk: disk))
        {
            src1.Configure(["http://cdn.test/track"], size);
            src1.EnsureRange(0, AudioBodyDiskCache.ChunkBytes);
        }

        int afterFirst = Volatile.Read(ref calls);
        Assert.True(afterFirst > 0);

        using (var src2 = new RangedHttpSource(http, fileId, default, 0, null, disk: disk))
        {
            src2.Configure(["http://cdn.test/track"], size);
            src2.EnsureRange(0, AudioBodyDiskCache.ChunkBytes);
        }

        Assert.Equal(afterFirst, Volatile.Read(ref calls));

        disk.ClearAll();
        // Drain and stop the writer thread BEFORE the directory goes away: a queued chunk committing after the delete
        // would recreate the tree and leave temp litter behind (Dispose itself never throws).
        disk.Dispose();
        Directory.Delete(dir, true);
    }

    [Fact]
    public void PartialCoverage_FetchesOnlyGap()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wavee-range-gap-" + Guid.NewGuid());
        var disk = new ChunkDiskCache(dir, budgetBytes: 64 << 20, volumeProbe: FatVolume);
        int calls = 0;
        const long size = AudioBodyDiskCache.ChunkBytes * 3;
        const string fileId = "partial";
        var handler = new RangeAwareHandler(A.Bytes(7, (int)size), () => Interlocked.Increment(ref calls));
        using var http = new HttpClient(handler);

        using (var src1 = new RangedHttpSource(http, fileId, default, 0, null, disk: disk))
        {
            src1.Configure(["http://cdn.test/track"], size);
            src1.EnsureRange(AudioBodyDiskCache.ChunkBytes, AudioBodyDiskCache.ChunkBytes);
        }

        int afterPartial = Volatile.Read(ref calls);
        Assert.True(afterPartial > 0);

        using (var src2 = new RangedHttpSource(http, fileId, default, 0, null, disk: disk))
        {
            src2.Configure(["http://cdn.test/track"], size);
            src2.EnsureRange(0, AudioBodyDiskCache.ChunkBytes * 3);
        }

        // Exact count, not just "increased": with the disk cache actually working, chunk 1 (fetched above) is served
        // from disk via LoadCachedChunks, leaving exactly two gaps — [0, ChunkBytes) and [2*ChunkBytes, size) — each
        // its own HTTP call. Before the disk write path worked, chunk 1 would never be found on disk, the whole
        // [0, size) span would be one contiguous gap, and this fetch would cost a SINGLE call — a total cache miss
        // that still satisfied ">", which is exactly how this test stayed green while the cache did nothing.
        Assert.Equal(afterPartial + 2, Volatile.Read(ref calls));

        disk.ClearAll();
        // Drain and stop the writer thread BEFORE the directory goes away: a queued chunk committing after the delete
        // would recreate the tree and leave temp litter behind (Dispose itself never throws).
        disk.Dispose();
        Directory.Delete(dir, true);
    }

    [Fact]
    public void TailChunk_SecondStreamUsesDiskWithoutHttp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wavee-range-tail-" + Guid.NewGuid());
        var disk = new ChunkDiskCache(dir, budgetBytes: 64 << 20, volumeProbe: FatVolume);
        int calls = 0;
        int size = AudioBodyDiskCache.ChunkBytes + 211;
        var body = A.Bytes(11, size);
        using var http = new HttpClient(new RangeAwareHandler(body, () => Interlocked.Increment(ref calls)));

        using (var first = new RangedHttpSource(http, "tail-file", default, 0, null, disk: disk))
        {
            first.Configure(["http://cdn.test/track"], size);
            first.EnsureRange(AudioBodyDiskCache.ChunkBytes, 211);
        }
        int afterFirst = calls;
        using (var second = new RangedHttpSource(http, "tail-file", default, 0, null, disk: disk))
        {
            second.Configure(["http://cdn.test/track"], size);
            second.EnsureRange(AudioBodyDiskCache.ChunkBytes, 211);
            var read = new byte[211];
            second.ReadRaw(AudioBodyDiskCache.ChunkBytes, read, 0, read.Length);
            Assert.Equal(body.AsSpan(AudioBodyDiskCache.ChunkBytes, 211).ToArray(), read);
        }
        Assert.Equal(afterFirst, calls);
        // Drain and stop the writer thread BEFORE the directory goes away: a queued chunk committing after the delete
        // would recreate the tree and leave temp litter behind (Dispose itself never throws).
        disk.Dispose();
        Directory.Delete(dir, true);
    }

    [Fact]
    public void TightVolume_SecondStreamStillHitsHttp_AndCacheReportsBelowReserve()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wavee-range-tight-" + Guid.NewGuid());
        var disk = new ChunkDiskCache(dir, budgetBytes: 64 << 20, volumeProbe: TightVolume);
        int calls = 0;
        const long size = 200_000;
        const string fileId = "no-room";
        var body = A.Bytes(3, (int)size);
        using var http = new HttpClient(new RangeAwareHandler(body, () => Interlocked.Increment(ref calls)));

        using (var src1 = new RangedHttpSource(http, fileId, default, 0, null, disk: disk))
        {
            src1.Configure(["http://cdn.test/track"], size);
            src1.EnsureRange(0, AudioBodyDiskCache.ChunkBytes);
        }
        int afterFirst = calls;

        // This is the exact bug shape: CanCommit refused every chunk write below the reserve, so the second stream
        // gets a real cache MISS (not a hit) and must re-fetch over HTTP — unlike SecondStream_ServesFromDisk_
        // ZeroAdditionalHttp above, calls here go UP.
        using (var src2 = new RangedHttpSource(http, fileId, default, 0, null, disk: disk))
        {
            src2.Configure(["http://cdn.test/track"], size);
            src2.EnsureRange(0, AudioBodyDiskCache.ChunkBytes);
        }
        Assert.True(Volatile.Read(ref calls) > afterFirst);

        // And the refusal must be REPORTED, not silent: this is the assertion the original bug needed.
        Assert.Equal(ChunkAdmission.BelowFreeSpaceReserve, disk.Status().Admission);
        Assert.Empty(Directory.GetFiles(dir, "*.enc", SearchOption.AllDirectories));

        disk.ClearAll();
        disk.Dispose();
        Directory.Delete(dir, true);
    }

    sealed class RangeAwareHandler(byte[] body, Action onCall) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onCall();
            long from = request.Headers.Range?.Ranges.FirstOrDefault()?.From ?? 0;
            long to = request.Headers.Range?.Ranges.FirstOrDefault()?.To ?? body.Length - 1;
            to = Math.Min(to, body.Length - 1);
            int length = checked((int)(to - from + 1));
            var slice = body.AsSpan((int)from, length).ToArray();
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(slice) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, body.Length);
            return Task.FromResult(response);
        }
    }
}
