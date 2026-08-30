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
    [Fact]
    public void SecondStream_ServesFromDisk_ZeroAdditionalHttp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wavee-range-disk-" + Guid.NewGuid());
        var disk = new ChunkDiskCache(dir, budgetBytes: 64 << 20);
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
        Directory.Delete(dir, true);
    }

    [Fact]
    public void PartialCoverage_FetchesOnlyGap()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wavee-range-gap-" + Guid.NewGuid());
        var disk = new ChunkDiskCache(dir, budgetBytes: 64 << 20);
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

        Assert.True(Volatile.Read(ref calls) > afterPartial);

        disk.ClearAll();
        Directory.Delete(dir, true);
    }

    [Fact]
    public void TailChunk_SecondStreamUsesDiskWithoutHttp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wavee-range-tail-" + Guid.NewGuid());
        var disk = new ChunkDiskCache(dir, budgetBytes: 64 << 20);
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
