using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Audio;
using Wavee.Sdk.Streams;
using Xunit;

namespace Wavee.Tests.Audio;

/// <summary>The live transport, driven through its scripted connect seam. What these pin is the behaviour that
/// separates a live stream from every other body in the app: it never seeks, it never reports a length, a socket drop is
/// a RECONNECT (with byte continuity) rather than an end, and only an exhausted budget turns into a typed failure.</summary>
public class LiveHttpAudioStreamTests
{
    static readonly LiveHttpOptions Fast = new(
        CapacityBytes: 64 * 1024,
        PrefillBytes: 1,
        ConnectTimeoutMs: 1_000,
        ReadIdleTimeoutMs: 2_000,
        BudgetMs: 400,
        BaseBackoffMs: 1,
        MaxBackoffMs: 5,
        Jitter: 0);

    /// <summary>A body that serves its bytes then HOLDS the end-of-stream until the test opens the gate — so a test can
    /// subscribe to the recovery events before the drop it is about to observe actually happens.</summary>
    sealed class GatedBody(byte[] data, ManualResetEventSlim? gate) : Stream
    {
        int _pos;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos < data.Length)
            {
                int n = Math.Min(count, data.Length - _pos);
                Array.Copy(data, _pos, buffer, offset, n);
                _pos += n;
                return n;
            }
            gate?.Wait(10_000);
            return 0;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    static LiveHttpResponse Response(byte[] body, ManualResetEventSlim? gate = null, params (string Key, string Value)[] headers)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in headers) map[k] = v;
        return new LiveHttpResponse(200, map, new GatedBody(body, gate), "http://station.test/live");
    }

    static byte[] Fill(int n, byte value)
    {
        var b = new byte[n];
        Array.Fill(b, value);
        return b;
    }

    /// <summary>Read exactly <paramref name="count"/> bytes, or as many as arrive before the deadline.</summary>
    static byte[] ReadFully(Stream s, int count, int timeoutMs = 5_000)
    {
        var buf = new byte[count];
        int got = 0;
        long deadline = Environment.TickCount64 + timeoutMs;
        while (got < count && Environment.TickCount64 < deadline)
        {
            int n = s.Read(buf, got, count - got);
            if (n <= 0) break;
            got += n;
        }
        return buf.AsSpan(0, got).ToArray();
    }

    static void AssertAll(ReadOnlySpan<byte> bytes, byte expected)
    {
        for (int i = 0; i < bytes.Length; i++) Assert.Equal(expected, bytes[i]);
    }

    // ── headers ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AdoptsIcyHeaders()
    {
        using var hold = new ManualResetEventSlim(false);
        await using var live = await LiveHttpAudioStream.OpenAsync("http://station.test/live",
            (_, _) => Task.FromResult(Response(Fill(64, 1), hold,
                ("content-type", "audio/aacp"), ("icy-name", "Station X"), ("icy-genre", "Jazz"),
                ("icy-br", "96"), ("icy-metaint", "16"))),
            Fast);

        Assert.Equal("audio/aacp", live.ContentType);
        Assert.Equal("Station X", live.StationName);
        Assert.Equal("Jazz", live.Genre);
        Assert.Equal(96, live.BitrateKbps);
        Assert.Equal(16, live.MetaInt);
        Assert.Equal(0, live.KnownSize);
        Assert.False(live.CanSeek);
        hold.Set();
    }

    [Fact]
    public async Task StripsInterleavedMetadata_AndSurfacesTheTitle()
    {
        const string title = "Miles Davis - So What";
        var payload = Encoding.UTF8.GetBytes($"StreamTitle='{title}';");
        var block = new byte[1 + 48];
        block[0] = 3;                       // 3 × 16 bytes of metadata
        payload.CopyTo(block, 1);

        var wire = new MemoryStream();
        wire.Write(Fill(16, 0xAA));
        wire.Write(block);
        wire.Write(Fill(16, 0xBB));

        using var hold = new ManualResetEventSlim(false);
        await using var live = await LiveHttpAudioStream.OpenAsync("http://station.test/live",
            (_, _) => Task.FromResult(Response(wire.ToArray(), hold, ("icy-metaint", "16"))), Fast);

        var audio = ReadFully(live, 32);

        Assert.Equal(32, audio.Length);
        AssertAll(audio.AsSpan(0, 16), 0xAA);
        AssertAll(audio.AsSpan(16, 16), 0xBB);
        Assert.Equal(title, live.CurrentTitle);
        hold.Set();
    }

    // ── reconnect ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reconnects_WithEventOrderAndByteContinuity()
    {
        using var firstDrop = new ManualResetEventSlim(false);
        int attempt = 0;
        var events = new List<AudioNetworkRecoveryStage>();

        var live = await LiveHttpAudioStream.OpenAsync("http://station.test/live", (_, _) =>
        {
            int n = Interlocked.Increment(ref attempt);
            return n switch
            {
                1 => Task.FromResult(Response(Fill(32, 0xA1), firstDrop)),
                2 => Task.FromResult(Response(Fill(32, 0xB2))),
                _ => Task.FromException<LiveHttpResponse>(new IOException("station gone")),
            };
        }, Fast);
        ((IAudioNetworkRecoverySource)live).NetworkRecovery += e => { lock (events) events.Add(e.Stage); };

        firstDrop.Set();   // only NOW does the first connection end

        var bytes = ReadFully(live, 64);

        Assert.Equal(64, bytes.Length);
        AssertAll(bytes.AsSpan(0, 32), 0xA1);
        AssertAll(bytes.AsSpan(32, 32), 0xB2);

        AudioNetworkRecoveryStage[] stages;
        lock (events) stages = [.. events];
        Assert.Equal(AudioNetworkRecoveryStage.Started, stages[0]);
        Assert.Equal(AudioNetworkRecoveryStage.Attempt, stages[1]);
        Assert.Equal(AudioNetworkRecoveryStage.Recovered, stages[2]);

        await live.DisposeAsync();
    }

    [Fact]
    public async Task ExhaustedBudget_FailsTypedNetwork_AfterDrainingWhatWasBuffered()
    {
        int attempt = 0;
        var live = await LiveHttpAudioStream.OpenAsync("http://station.test/live", (_, _) =>
            Interlocked.Increment(ref attempt) == 1
                ? Task.FromResult(Response(Fill(8, 0xC3)))
                : Task.FromException<LiveHttpResponse>(new IOException("refused")),
            Fast);

        var buffered = ReadFully(live, 8);
        Assert.Equal(8, buffered.Length);

        // The buffered tail is served first; only after it drains does the failure surface.
        var thrown = await Task.Run(() =>
        {
            long deadline = Environment.TickCount64 + 5_000;
            while (Environment.TickCount64 < deadline)
            {
                try { if (live.Read(new byte[16], 0, 16) == 0) return null; }
                catch (AudioRangeFetchException ex) { return ex; }
            }
            return null;
        });

        Assert.NotNull(thrown);
        Assert.Equal(StreamFailureReason.Network, thrown!.Reason);

        await live.DisposeAsync();
    }

    // ── the seek contract ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SeekZero_IsANoOp_AndSkipStreamWrapsIt()
    {
        using var hold = new ManualResetEventSlim(false);
        await using var live = await LiveHttpAudioStream.OpenAsync("http://station.test/live",
            (_, _) => Task.FromResult(Response(Fill(64, 0xD4), hold)), Fast);

        Assert.Equal(0, live.Seek(0, SeekOrigin.Begin));
        Assert.Equal(0, live.Seek(0, SeekOrigin.Current));

        // SkipStream's constructor issues exactly Seek(skip, Begin); with a zero skip that must not throw, and the
        // wrapper must report CanSeek=false so the MP3 gapless probe never touches a live stream.
        var skip = new SkipStream(live, 0);
        Assert.False(skip.CanSeek);
        Assert.Equal(16, ReadFully(skip, 16).Length);
        hold.Set();
    }

    [Fact]
    public async Task NonZeroSeek_Throws()
    {
        using var hold = new ManualResetEventSlim(false);
        await using var live = await LiveHttpAudioStream.OpenAsync("http://station.test/live",
            (_, _) => Task.FromResult(Response(Fill(16, 1), hold)), Fast);

        Assert.Throws<NotSupportedException>(() => live.Seek(5, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => live.Seek(0, SeekOrigin.End));
        Assert.Throws<NotSupportedException>(() => _ = live.Length);
        hold.Set();
    }

    // ── the first connect ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FirstConnectFailure_FaultsOpen_WithoutRetrying()
    {
        int attempts = 0;
        await Assert.ThrowsAsync<IOException>(() => LiveHttpAudioStream.OpenAsync("http://station.test/live", (_, _) =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromException<LiveHttpResponse>(new IOException("connection refused"));
        }, Fast));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task NonSuccessStatus_FaultsOpen()
    {
        var ex = await Assert.ThrowsAsync<IOException>(() => LiveHttpAudioStream.OpenAsync("http://station.test/live",
            (_, _) => Task.FromResult(new LiveHttpResponse(404,
                new Dictionary<string, string>(StringComparer.Ordinal), new MemoryStream(), "http://station.test/live")),
            Fast));

        Assert.Contains("404", ex.Message);
    }

    // ── teardown ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Dispose_WakesABlockedReader()
    {
        using var hold = new ManualResetEventSlim(false);
        var live = await LiveHttpAudioStream.OpenAsync("http://station.test/live",
            (_, _) => Task.FromResult(Response(Fill(4, 9), hold)), Fast);

        Assert.Equal(4, ReadFully(live, 4).Length);
        var reader = Task.Run(() =>
        {
            try { live.Read(new byte[16], 0, 16); return "read"; }
            catch (ObjectDisposedException) { return "disposed"; }
        });
        Assert.False(reader.Wait(150), "the reader must park while the stream is live but starved");

        live.Dispose();

        Assert.True(reader.Wait(5_000));
        Assert.Equal("disposed", reader.Result);
        live.Dispose();   // idempotent
        hold.Set();
    }

    [Fact]
    public async Task PeekHead_DoesNotConsume()
    {
        using var hold = new ManualResetEventSlim(false);
        await using var live = await LiveHttpAudioStream.OpenAsync("http://station.test/live",
            (_, _) => Task.FromResult(Response([1, 2, 3, 4, 5, 6, 7, 8], hold)), Fast);

        var head = new byte[4];
        Assert.Equal(4, live.PeekHead(head));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, head);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, ReadFully(live, 8));
        hold.Set();
    }
}
