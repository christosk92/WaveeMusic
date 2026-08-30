using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Audio;
using Xunit;
using Wavee.Sdk.Streams;

namespace Wavee.Tests.Audio;

public class SpotifyAudioStreamTests
{
    static byte[] ReadAll(Stream s)
    {
        using var ms = new MemoryStream();
        var buf = new byte[4096];
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
        return ms.ToArray();
    }

    [Fact]
    public async Task HeadOnly_ReadsClearHead_WithoutCdnRequest()
    {
        var key = A.Key16(2);
        var plaintext = A.Bytes(1, 600);
        var cdn = SpotifyAesCtr.Decrypt(plaintext, key, 0);
        const int N = 120;
        var head = plaintext.AsSpan(0, N).ToArray();
        var handler = new RangeCdnHandler(cdn);
        var http = new HttpClient(handler);

        using var stream = SpotifyAudioStream.CreateHeadOnly(http, head, N);
        var gotHead = new byte[N];

        Assert.Equal(N, stream.Read(gotHead, 0, gotHead.Length));
        Assert.Equal(head, gotHead);
        Assert.Empty(handler.Requests);

        await stream.AttachBodyAsync(key, new[] { "https://cdn/a" }, cdn.Length, CancellationToken.None);

        Assert.Equal(plaintext.AsSpan(N).ToArray(), ReadAll(stream));
        Assert.All(handler.Ranges, r => Assert.True(r.From.HasValue));
    }

    [Fact]
    public async Task Composes_ClearHead_Plus_DecryptedBody_WithRangeRequestsOnly()
    {
        var key = A.Key16(2);
        var plaintext = A.Bytes(1, 600);
        var cdn = SpotifyAesCtr.Decrypt(plaintext, key, 0);
        const int N = 120;
        var head = plaintext.AsSpan(0, N).ToArray();
        var handler = new RangeCdnHandler(cdn);
        var http = new HttpClient(handler);

        using var stream = await SpotifyAudioStream.CreateAsync(http, head, N, key, new[] { "https://cdn/a" }, cdn.Length, CancellationToken.None);

        Assert.Equal(plaintext, ReadAll(stream));
        Assert.Equal(0, handler.FullGetCount);
        Assert.All(handler.Ranges, r => Assert.True(r.From.HasValue));
    }

    [Fact]
    public async Task NoHead_DecryptsWholeBodyFromZero()
    {
        var key = A.Key16(4);
        var plaintext = A.Bytes(3, 300);
        var cdn = SpotifyAesCtr.Decrypt(plaintext, key, 0);
        var http = new HttpClient(new RangeCdnHandler(cdn));

        using var stream = await SpotifyAudioStream.CreateAsync(
            http, ReadOnlyMemory<byte>.Empty, 0, key, new[] { "https://cdn/a" }, cdn.Length, CancellationToken.None);

        Assert.Equal(plaintext, ReadAll(stream));
    }

    [Fact]
    public async Task FailsOverToSecondMirror()
    {
        var key = A.Key16(5);
        var plaintext = A.Bytes(7, 400);
        var cdn = SpotifyAesCtr.Decrypt(plaintext, key, 0);
        var handler = new RangeCdnHandler(cdn, url => url.Contains("bad", StringComparison.OrdinalIgnoreCase));
        var http = new HttpClient(handler);

        using var stream = await SpotifyAudioStream.CreateAsync(http, ReadOnlyMemory<byte>.Empty, 0, key,
            new[] { "https://cdn/bad", "https://cdn/good" }, cdn.Length, CancellationToken.None);

        Assert.Equal(plaintext, ReadAll(stream));
        Assert.Contains(handler.Requests, r => r.Contains("bad", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(handler.Requests, r => r.Contains("good", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AllMirrorsFail_Throws()
    {
        var http = new HttpClient(new RangeCdnHandler(Array.Empty<byte>(), _ => true));
        var policy = new RangedHttpRecoveryPolicy(VisibilityMs: 0, BudgetMs: 100,
            AttemptTimeoutMs: 25, Jitter: _ => 0);

        var error = await Assert.ThrowsAsync<AudioRangeFetchException>(() => SpotifyAudioStream.CreateAsync(
            http, ReadOnlyMemory<byte>.Empty, 0, A.Key16(1), new[] { "https://cdn/a", "https://cdn/b" }, null,
            CancellationToken.None, policy));

        Assert.Equal(StreamFailureReason.Network, error.Reason);
    }

    [Fact]
    public async Task RejectsFullBodyOk_WhenCdnIgnoresRange()
    {
        var key = A.Key16(8);
        var plaintext = A.Bytes(9, 512);
        var cdn = SpotifyAesCtr.Decrypt(plaintext, key, 0);
        var http = new HttpClient(new RangeCdnHandler(cdn) { IgnoreRangeAndReturnOk = true });

        await Assert.ThrowsAsync<CdnPermanentException>(() => SpotifyAudioStream.CreateAsync(
            http, ReadOnlyMemory<byte>.Empty, 0, key, new[] { "https://cdn/a" }, cdn.Length, CancellationToken.None));
    }

    [Fact]
    public async Task Seek_FetchesRequestedRange_AndReadsCorrectBytes()
    {
        var key = A.Key16(6);
        var plaintext = A.Bytes(2, 2_000_000);
        var cdn = SpotifyAesCtr.Decrypt(plaintext, key, 0);
        var handler = new RangeCdnHandler(cdn);
        var http = new HttpClient(handler);
        using var stream = await SpotifyAudioStream.CreateAsync(
            http, plaintext.AsSpan(0, 64).ToArray(), 64, key, new[] { "https://cdn/a" }, cdn.Length, CancellationToken.None);

        stream.Seek(1_000_000, SeekOrigin.Begin);
        var mid = new byte[16];
        int r = stream.Read(mid, 0, 16);

        Assert.Equal(16, r);
        Assert.Equal(plaintext.AsSpan(1_000_000, 16).ToArray(), mid);
        Assert.Contains(handler.Ranges, r => r.From == 1_000_000);
        Assert.Equal(0, handler.FullGetCount);
    }

    [Fact]
    public async Task ReadAtHeadBoundary_BlocksUntilBodyIsAttached()
    {
        var key = A.Key16(11);
        var plaintext = A.Bytes(12, 8_000);
        var cdn = SpotifyAesCtr.Decrypt(plaintext, key, 0);
        const int N = 256;
        var stream = SpotifyAudioStream.CreateHeadOnly(new HttpClient(new RangeCdnHandler(cdn)), plaintext.AsSpan(0, N).ToArray(), N);
        stream.Position = N;
        var buf = new byte[64];

        var readTask = Task.Run(() => stream.Read(buf, 0, buf.Length));
        await Task.Delay(75);
        Assert.False(readTask.IsCompleted);

        await stream.AttachBodyAsync(key, new[] { "https://cdn/a" }, cdn.Length, CancellationToken.None);

        Assert.Equal(buf.Length, await readTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(plaintext.AsSpan(N, buf.Length).ToArray(), buf);
        stream.Dispose();
    }

    [Fact]
    public async Task LazyAttach_ReturnsBeforeSlowCdnRangeFetchCompletes()
    {
        var key = A.Key16(26);
        var plaintext = A.Bytes(141, 64_000);
        var cdn = SpotifyAesCtr.Decrypt(plaintext, key, 0);
        const int N = 512;
        var handler = new RangeCdnHandler(cdn) { ResponseDelayMs = 500 };
        using var stream = SpotifyAudioStream.CreateHeadOnly(
            new HttpClient(handler), plaintext.AsSpan(0, N).ToArray(), N);

        var sw = Stopwatch.StartNew();
        await stream.AttachBodyLazyAsync(key, new[] { "https://cdn/a" }, cdn.Length, CancellationToken.None);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 250, $"lazy attach took {sw.ElapsedMilliseconds}ms");
        Assert.True(stream.IsBodyAttached);

        stream.Position = N;
        var buf = new byte[37];
        Assert.Equal(buf.Length, await Task.Run(() => stream.Read(buf, 0, buf.Length)).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(plaintext.AsSpan(N, buf.Length).ToArray(), buf);
        Assert.All(handler.Ranges, r => Assert.True(r.From.HasValue));
    }

    [Fact]
    public async Task PausedReadAhead_DoesNotPrefetchFromProbeRead()
    {
        var key = A.Key16(27);
        var plaintext = A.Bytes(151, 2_000_000);
        var cdn = SpotifyAesCtr.Decrypt(plaintext, key, 0);
        var handler = new RangeCdnHandler(cdn);
        using var stream = SpotifyAudioStream.CreateHeadOnly(
            new HttpClient(handler), ReadOnlyMemory<byte>.Empty, 0);
        using var pause = stream.PauseReadAhead();

        await stream.AttachBodyLazyAsync(key, new[] { "https://cdn/a" }, cdn.Length, CancellationToken.None);
        stream.Position = 1_000_000;
        var buf = new byte[32];

        Assert.Equal(buf.Length, stream.Read(buf, 0, buf.Length));
        await Task.Delay(150);

        var ranges = handler.Ranges;
        Assert.Single(ranges);
        Assert.Equal(1_000_000, ranges[0].From);
        Assert.Equal(1_000_000 + 64 * 1024 - 1, ranges[0].To);
        Assert.Equal(plaintext.AsSpan(1_000_000, buf.Length).ToArray(), buf);
    }

    [Fact]
    public void RapidSeeksWithinClearHead_ReadImmediatelyWithoutCdn()
    {
        var plaintext = A.Bytes(23, 2_048);
        var handler = new RangeCdnHandler(Array.Empty<byte>());
        using var stream = SpotifyAudioStream.CreateHeadOnly(new HttpClient(handler), plaintext.AsSpan(0, 1_024).ToArray(), 1_024);
        var offsets = new[] { 512L, 32L, 900L, 0L, 777L, 128L };
        var buf = new byte[19];

        foreach (var offset in offsets)
        {
            Assert.Equal(offset, stream.Seek(offset, SeekOrigin.Begin));
            Assert.Equal(buf.Length, stream.Read(buf, 0, buf.Length));
            Assert.Equal(plaintext.AsSpan((int)offset, buf.Length).ToArray(), buf);
        }

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SeekBackIntoClearHead_InterruptsPendingBodyRead()
    {
        var plaintext = A.Bytes(33, 4_096);
        var handler = new RangeCdnHandler(Array.Empty<byte>());
        using var stream = SpotifyAudioStream.CreateHeadOnly(new HttpClient(handler), plaintext.AsSpan(0, 512).ToArray(), 512);
        stream.Position = 1_500;
        var buf = new byte[31];

        var readTask = Task.Run(() => stream.Read(buf, 0, buf.Length));
        await Task.Delay(75);
        Assert.False(readTask.IsCompleted);

        stream.Position = 123;

        Assert.Equal(buf.Length, await readTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(plaintext.AsSpan(123, buf.Length).ToArray(), buf);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SeekWhileBlockedBeyondHead_UsesLatestBodyOffsetWhenBodyAttaches()
    {
        var key = A.Key16(12);
        var plaintext = A.Bytes(43, 8_000);
        var cdn = SpotifyAesCtr.Decrypt(plaintext, key, 0);
        var handler = new RangeCdnHandler(cdn);
        using var stream = SpotifyAudioStream.CreateHeadOnly(new HttpClient(handler), plaintext.AsSpan(0, 512).ToArray(), 512);
        stream.Position = 1_500;
        var buf = new byte[41];

        var readTask = Task.Run(() => stream.Read(buf, 0, buf.Length));
        await Task.Delay(75);
        Assert.False(readTask.IsCompleted);

        stream.Position = 3_000;
        await Task.Delay(75);
        Assert.False(readTask.IsCompleted);

        await stream.AttachBodyAsync(key, new[] { "https://cdn/a" }, cdn.Length, CancellationToken.None);

        Assert.Equal(buf.Length, await readTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(plaintext.AsSpan(3_000, buf.Length).ToArray(), buf);
        Assert.Contains(handler.Ranges, r => r.From <= 3_000 && r.To >= 3_000 + buf.Length - 1);
    }

    [Fact]
    public async Task AbortBody_UnblocksPendingReadWithOriginalFailure()
    {
        using var stream = SpotifyAudioStream.CreateHeadOnly(new HttpClient(new RangeCdnHandler(Array.Empty<byte>())), ReadOnlyMemory<byte>.Empty, 0);
        var readTask = Task.Run(() => stream.Read(new byte[1], 0, 1));
        await Task.Delay(75);
        Assert.False(readTask.IsCompleted);

        stream.AbortBody(new IOException("license failed"));

        var ex = await Assert.ThrowsAsync<IOException>(() => readTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("license failed", ex.Message);
    }

    [Fact]
    public async Task Dispose_UnblocksPendingReadWithObjectDisposed()
    {
        var stream = SpotifyAudioStream.CreateHeadOnly(new HttpClient(new RangeCdnHandler(Array.Empty<byte>())), ReadOnlyMemory<byte>.Empty, 0);
        var readTask = Task.Run(() => stream.Read(new byte[1], 0, 1));
        await Task.Delay(75);
        Assert.False(readTask.IsCompleted);

        stream.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => readTask.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task RapidSeekSequence_ReadsCorrectBytes_WithoutFullGets()
    {
        var key = A.Key16(13);
        var plaintext = A.Bytes(21, 3_000_000);
        var cdn = SpotifyAesCtr.Decrypt(plaintext, key, 0);
        var handler = new RangeCdnHandler(cdn);
        using var stream = await SpotifyAudioStream.CreateAsync(
            new HttpClient(handler), plaintext.AsSpan(0, 128).ToArray(), 128, key, new[] { "https://cdn/a" }, cdn.Length, CancellationToken.None);

        var offsets = new[] { 128L, 750_000L, 256L, 1_900_000L, 65_536L, 2_500_000L, 512L };
        var buf = new byte[37];
        foreach (var offset in offsets)
        {
            Array.Clear(buf);
            Assert.Equal(offset, stream.Seek(offset, SeekOrigin.Begin));
            Assert.Equal(buf.Length, stream.Read(buf, 0, buf.Length));
            Assert.Equal(plaintext.AsSpan((int)offset, buf.Length).ToArray(), buf);
        }

        Assert.Equal(0, handler.FullGetCount);
        Assert.Contains(handler.Ranges, r => r.From == 750_000);
        Assert.Contains(handler.Ranges, r => r.From == 1_900_000);
        Assert.Contains(handler.Ranges, r => r.From == 2_500_000);
    }

    [Fact]
    public async Task PartialRangeResponses_AreContinuedByLaterReads()
    {
        var key = A.Key16(14);
        var plaintext = A.Bytes(31, 120_000);
        var cdn = SpotifyAesCtr.Decrypt(plaintext, key, 0);
        var handler = new RangeCdnHandler(cdn) { MaxBytesPerResponse = 4096 };
        using var stream = await SpotifyAudioStream.CreateAsync(
            new HttpClient(handler), ReadOnlyMemory<byte>.Empty, 0, key, new[] { "https://cdn/a" }, cdn.Length, CancellationToken.None);

        var got = new byte[10_000];
        var total = 0;
        while (total < got.Length)
            total += stream.Read(got, total, got.Length - total);

        Assert.Equal(10_000, total);
        Assert.Equal(plaintext.AsSpan(0, got.Length).ToArray(), got);
        Assert.True(handler.Ranges.Count >= 3);
        Assert.All(handler.Ranges, r => Assert.True(r.From.HasValue));
    }

    [Fact]
    public async Task NativeDecryptor_UsesAbsoluteOffsetsForBodyReads()
    {
        var plaintext = A.Bytes(41, 20_000);
        var cdn = XorByOffset(plaintext);
        const int N = 300;
        var offsets = new List<long>();
        CdnDecryptor decryptor = (buffer, offset) =>
        {
            offsets.Add(offset);
            XorByOffsetInPlace(buffer, offset);
        };

        using var stream = await SpotifyAudioStream.CreateWithNativeDecryptorAsync(
            new HttpClient(new RangeCdnHandler(cdn)), plaintext.AsSpan(0, N).ToArray(), N, decryptor, new[] { "https://cdn/a" }, cdn.Length, CancellationToken.None);

        Assert.Equal(plaintext, ReadAll(stream));
        Assert.Contains(N, offsets);
    }

    [Fact]
    public async Task MissingContentRangeTotal_ThrowsWhenSizeIsUnknown()
    {
        var key = A.Key16(15);
        var plaintext = A.Bytes(51, 2_048);
        var cdn = SpotifyAesCtr.Decrypt(plaintext, key, 0);
        var http = new HttpClient(new RangeCdnHandler(cdn) { OmitContentRangeLength = true });

        await Assert.ThrowsAsync<CdnPermanentException>(() => SpotifyAudioStream.CreateAsync(
            http, ReadOnlyMemory<byte>.Empty, 0, key, new[] { "https://cdn/a" }, null, CancellationToken.None));
    }

    [Fact]
    public async Task MissingContentRangeTotal_SucceedsWhenKnownSizeIsSupplied()
    {
        var key = A.Key16(16);
        var plaintext = A.Bytes(61, 2_048);
        var cdn = SpotifyAesCtr.Decrypt(plaintext, key, 0);
        var http = new HttpClient(new RangeCdnHandler(cdn) { OmitContentRangeLength = true });

        using var stream = await SpotifyAudioStream.CreateAsync(
            http, ReadOnlyMemory<byte>.Empty, 0, key, new[] { "https://cdn/a" }, cdn.Length, CancellationToken.None);

        Assert.Equal(plaintext, ReadAll(stream));
    }

    [Fact]
    public async Task MissingContentRangeHeader_ThrowsWhenSizeIsUnknown()
    {
        var key = A.Key16(17);
        var plaintext = A.Bytes(71, 2_048);
        var cdn = SpotifyAesCtr.Decrypt(plaintext, key, 0);
        var http = new HttpClient(new RangeCdnHandler(cdn) { OmitContentRange = true });

        await Assert.ThrowsAsync<CdnPermanentException>(() => SpotifyAudioStream.CreateAsync(
            http, ReadOnlyMemory<byte>.Empty, 0, key, new[] { "https://cdn/a" }, null, CancellationToken.None));
    }

    [Fact]
    public async Task UnexpectedContentRangeStart_Throws()
    {
        var key = A.Key16(18);
        var plaintext = A.Bytes(81, 2_048);
        var cdn = SpotifyAesCtr.Decrypt(plaintext, key, 0);
        var http = new HttpClient(new RangeCdnHandler(cdn) { RangeStartDelta = 1 });

        await Assert.ThrowsAsync<CdnPermanentException>(() => SpotifyAudioStream.CreateAsync(
            http, ReadOnlyMemory<byte>.Empty, 0, key, new[] { "https://cdn/a" }, cdn.Length, CancellationToken.None));
    }

    [Fact]
    public async Task ChangedCdnTotalSize_Throws()
    {
        var key = A.Key16(19);
        var plaintext = A.Bytes(91, 2_048);
        var cdn = SpotifyAesCtr.Decrypt(plaintext, key, 0);
        var http = new HttpClient(new RangeCdnHandler(cdn) { TotalLengthDelta = 1 });

        await Assert.ThrowsAsync<CdnPermanentException>(() => SpotifyAudioStream.CreateAsync(
            http, ReadOnlyMemory<byte>.Empty, 0, key, new[] { "https://cdn/a" }, cdn.Length, CancellationToken.None));
    }

    [Fact]
    public async Task AttachBody_IsIdempotentAfterSuccess()
    {
        var key = A.Key16(20);
        var plaintext = A.Bytes(101, 10_000);
        var cdn = SpotifyAesCtr.Decrypt(plaintext, key, 0);
        var handler = new RangeCdnHandler(cdn);
        using var stream = SpotifyAudioStream.CreateHeadOnly(new HttpClient(handler), ReadOnlyMemory<byte>.Empty, 0);

        await stream.AttachBodyAsync(key, new[] { "https://cdn/good" }, cdn.Length, CancellationToken.None);
        var requestCount = handler.Requests.Count;
        await stream.AttachBodyAsync(A.Key16(99), new[] { "https://cdn/bad" }, cdn.Length, CancellationToken.None);

        Assert.Equal(requestCount, handler.Requests.Count);
        Assert.Equal(plaintext, ReadAll(stream));
    }

    [Fact]
    public void AttachBody_RejectsInvalidKeyLengthSynchronously()
    {
        using var stream = SpotifyAudioStream.CreateHeadOnly(new HttpClient(new RangeCdnHandler(Array.Empty<byte>())), ReadOnlyMemory<byte>.Empty, 0);

        Assert.Throws<ArgumentException>(() =>
        {
            _ = stream.AttachBodyAsync(new byte[15], new[] { "https://cdn/a" }, 1, CancellationToken.None);
        });
    }

    [Fact]
    public async Task AttachBody_RejectsEmptyOrWhitespaceUrls()
    {
        using var stream = SpotifyAudioStream.CreateHeadOnly(new HttpClient(new RangeCdnHandler(Array.Empty<byte>())), ReadOnlyMemory<byte>.Empty, 0);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            stream.AttachBodyAsync(A.Key16(21), new[] { "", " " }, 1, CancellationToken.None));
    }

    [Fact]
    public async Task SeekEndAndNegativeSeek_AreClampedAndReadable()
    {
        var key = A.Key16(22);
        var plaintext = A.Bytes(111, 4_096);
        var cdn = SpotifyAesCtr.Decrypt(plaintext, key, 0);
        using var stream = await SpotifyAudioStream.CreateAsync(
            new HttpClient(new RangeCdnHandler(cdn)), plaintext.AsSpan(0, 100).ToArray(), 100, key, new[] { "https://cdn/a" }, cdn.Length, CancellationToken.None);

        Assert.Equal(0, stream.Seek(-500, SeekOrigin.Begin));
        var first = new byte[8];
        Assert.Equal(first.Length, stream.Read(first, 0, first.Length));
        Assert.Equal(plaintext.AsSpan(0, first.Length).ToArray(), first);

        Assert.Equal(cdn.Length - 10, stream.Seek(-10, SeekOrigin.End));
        var tail = new byte[10];
        Assert.Equal(tail.Length, stream.Read(tail, 0, tail.Length));
        Assert.Equal(plaintext.AsSpan(plaintext.Length - 10, 10).ToArray(), tail);
        Assert.Equal(0, stream.Read(tail, 0, tail.Length));
    }

    [Fact]
    public void ZeroLengthRead_DoesNotWaitForBodyOrHitCdn()
    {
        var handler = new RangeCdnHandler(Array.Empty<byte>());
        using var stream = SpotifyAudioStream.CreateHeadOnly(new HttpClient(handler), ReadOnlyMemory<byte>.Empty, 0);

        Assert.Equal(0, stream.Read(new byte[1], 0, 0));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CanSeek_IsTrueDuringHeadOnlyPhase_SoFastStartVorbisReaderIsSeekable()
    {
        var key = A.Key16(24);
        var plaintext = A.Bytes(121, 4_096);
        var cdn = SpotifyAesCtr.Decrypt(plaintext, key, 0);
        using var stream = SpotifyAudioStream.CreateHeadOnly(
            new HttpClient(new RangeCdnHandler(cdn)), plaintext.AsSpan(0, 256).ToArray(), 256);

        Assert.True(stream.CanSeek);

        await stream.AttachBodyAsync(key, new[] { "https://cdn/a" }, cdn.Length, CancellationToken.None);

        Assert.True(stream.CanSeek);
    }

    [Fact]
    public async Task ReadAcrossSparseChunkBoundary_ReturnsCorrectBytes_WithoutFullGet()
    {
        var key = A.Key16(25);
        var plaintext = A.Bytes(131, 200_000);
        var cdn = SpotifyAesCtr.Decrypt(plaintext, key, 0);
        var handler = new RangeCdnHandler(cdn);
        using var stream = await SpotifyAudioStream.CreateAsync(
            new HttpClient(handler), ReadOnlyMemory<byte>.Empty, 0, key, new[] { "https://cdn/a" }, cdn.Length, CancellationToken.None);
        var offset = 64 * 1024 - 17;
        var buf = new byte[64];

        stream.Position = offset;
        Assert.Equal(buf.Length, stream.Read(buf, 0, buf.Length));

        Assert.Equal(plaintext.AsSpan(offset, buf.Length).ToArray(), buf);
        Assert.Equal(0, handler.FullGetCount);
        Assert.Contains(handler.Ranges, r => r.From == 64 * 1024);
    }

    static byte[] XorByOffset(byte[] plaintext)
    {
        var output = plaintext.ToArray();
        XorByOffsetInPlace(output, 0);
        return output;
    }

    static void XorByOffsetInPlace(Span<byte> buffer, long offset)
    {
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] ^= (byte)(((offset + i) * 31 + 7) & 0xff);
    }

    sealed class RangeCdnHandler : HttpMessageHandler
    {
        readonly byte[] _body;
        readonly Func<string, bool> _fail;
        readonly object _lock = new();
        readonly List<string> _requests = [];
        readonly List<RangeItemHeaderValue> _ranges = [];
        int _fullGetCount;

        public IReadOnlyList<string> Requests { get { lock (_lock) return _requests.ToArray(); } }
        public IReadOnlyList<RangeItemHeaderValue> Ranges { get { lock (_lock) return _ranges.ToArray(); } }
        public int FullGetCount { get { lock (_lock) return _fullGetCount; } }
        public bool IgnoreRangeAndReturnOk;
        public bool OmitContentRange;
        public bool OmitContentRangeLength;
        public int RangeStartDelta;
        public int TotalLengthDelta;
        public int MaxBytesPerResponse;
        public int ResponseDelayMs;

        public RangeCdnHandler(byte[] body, Func<string, bool>? fail = null)
        {
            _body = body;
            _fail = fail ?? (_ => false);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (_lock) _requests.Add(url);
            if (ResponseDelayMs > 0)
                await Task.Delay(ResponseDelayMs, cancellationToken).ConfigureAwait(false);

            if (_fail(url))
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new ByteArrayContent(Array.Empty<byte>()),
                };

            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            if (range is null)
            {
                lock (_lock) _fullGetCount++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(_body),
                };
            }

            lock (_lock) _ranges.Add(range);
            if (IgnoreRangeAndReturnOk)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(_body),
                };

            var from = Math.Max(0, range.From ?? 0);
            if (from >= _body.Length)
                return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);

            var to = Math.Min(range.To ?? _body.Length - 1, _body.Length - 1);
            var len = (int)(to - from + 1);
            if (MaxBytesPerResponse > 0) len = Math.Min(len, MaxBytesPerResponse);
            var slice = new byte[len];
            Array.Copy(_body, (int)from, slice, 0, len);
            var content = new ByteArrayContent(slice);
            var responseFrom = from + RangeStartDelta;
            var responseTo = Math.Min(responseFrom + len - 1, _body.Length - 1);
            if (!OmitContentRange)
            {
                content.Headers.ContentRange = OmitContentRangeLength
                    ? new ContentRangeHeaderValue(responseFrom, responseTo)
                    : new ContentRangeHeaderValue(responseFrom, responseTo, _body.Length + TotalLengthDelta);
            }
            content.Headers.ContentLength = len;

            return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
        }
    }
}
