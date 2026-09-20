// G-128's pure halves: the bounded live ring, the ICY metaint demux, the reconnecting live stream over a scripted
// connect, the ADTS parser and reader, the AAC sniff, the channel conform, and the `--fake` module's silent MP3 body.
// The Media Foundation decode itself is not driven here (no MFT in a unit test); the refusals before it are. Ported in
// spirit from 0.2.9 LiveRingBufferTests / IcyDemuxerTests / LiveHttpAudioStreamTests / AdtsFrameParserTests, whose
// subjects now live in `Modules.Host.cs`.

using System.Text;
using FluentGpu.Media;
using Xunit;
using static Wavee.Modules;

namespace Wavee.Tests;

public class ModuleLiveAudioTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── the ring ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ring_ReadsWhatWasWritten_InOrder()
    {
        using var ring = new LiveRing(1024);
        ring.Write("hello "u8);
        ring.Write("world"u8);
        var buf = new byte[32];
        int n = ring.Read(buf, 11);
        Assert.Equal("hello world", Encoding.ASCII.GetString(buf, 0, n));
        Assert.Equal(0, ring.Available);
    }

    [Fact]
    public void Ring_AnOverrunDropsTheOldestBytes_AndCountsThem()
    {
        using var ring = new LiveRing(1024);
        ring.Write(new byte[1000]);
        ring.Write(Enumerable.Repeat((byte)7, 100).ToArray());

        Assert.Equal(1024, ring.Available);
        Assert.Equal(76, ring.TotalDropped);
        var buf = new byte[1024];
        Assert.Equal(1024, ring.Read(buf));
        Assert.Equal(7, buf[^1]);
    }

    [Fact]
    public void Ring_ACleanCompletionDrainsThenReadsZero()
    {
        using var ring = new LiveRing(1024);
        ring.Write("abc"u8);
        ring.Complete();
        var buf = new byte[8];
        Assert.Equal(3, ring.Read(buf));
        Assert.Equal(0, ring.Read(buf));
    }

    [Fact]
    public void Ring_AFaultedCompletionDrainsThenThrows()
    {
        using var ring = new LiveRing(1024);
        ring.Write("abc"u8);
        ring.Complete(new IOException("lost"));
        var buf = new byte[8];
        Assert.Equal(3, ring.Read(buf));   // the last buffered second still plays
        Assert.Throws<IOException>(() => ring.Read(buf));
    }

    [Fact]
    public void Ring_ADisposedRingWakesTheReaderWithObjectDisposed()
    {
        var ring = new LiveRing(1024);
        ring.Dispose();
        Assert.Throws<ObjectDisposedException>(() => ring.Read(new byte[4]));
        Assert.Equal(0, ring.Peek(new byte[4], 1, 0));
    }

    // ── the ICY demux ────────────────────────────────────────────────────────────────────────────────────────────────

    static byte[] IcyBody(int metaInt, params (string Audio, string? Title)[] runs)
    {
        var ms = new MemoryStream();
        foreach (var (audio, title) in runs)
        {
            byte[] a = Encoding.ASCII.GetBytes(audio);
            Assert.Equal(metaInt, a.Length);
            ms.Write(a);
            if (title is null) { ms.WriteByte(0); continue; }
            byte[] block = Encoding.UTF8.GetBytes("StreamTitle='" + title + "';");
            int blocks = (block.Length + 15) / 16;
            ms.WriteByte((byte)blocks);
            ms.Write(block);
            ms.Write(new byte[blocks * 16 - block.Length]);
        }
        return ms.ToArray();
    }

    [Fact]
    public void Demux_SeparatesAudioFromTitles_EvenOneByteAtATime()
    {
        byte[] body = IcyBody(4, ("AAAA", "Artist - Song"), ("BBBB", null), ("CCCC", "Don't Stop"));
        using var ring = new LiveRing(1024);
        var demux = new IcyDemux(4);
        var titles = new List<string>();
        demux.TitleChanged += titles.Add;

        foreach (byte b in body) demux.Push([b], ring);

        var buf = new byte[64];
        int n = ring.Read(buf, 12);
        Assert.Equal("AAAABBBBCCCC", Encoding.ASCII.GetString(buf, 0, n));
        Assert.Equal(["Artist - Song", "Don't Stop"], titles);
    }

    [Fact]
    public void Demux_ARepeatedTitleIsRaisedOnce_AndNoMetaintIsPassThrough()
    {
        var titles = new List<string>();
        using var ring = new LiveRing(1024);
        var demux = new IcyDemux(4);
        demux.TitleChanged += titles.Add;
        demux.Push(IcyBody(4, ("AAAA", "Same"), ("BBBB", "Same")), ring);
        Assert.Single(titles);

        using var raw = new LiveRing(1024);
        new IcyDemux(0).Push("StreamTitle='x';"u8, raw);
        Assert.Equal(16, raw.Available);
    }

    // ── the live stream ──────────────────────────────────────────────────────────────────────────────────────────────

    static LiveResponse Response(byte[] body, int status = 200, int metaInt = 0)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["content-type"] = "audio/mpeg", ["icy-name"] = "Test FM" };
        if (metaInt > 0) headers["icy-metaint"] = metaInt.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new LiveResponse(status, headers, new MemoryStream(body), "http://radio.test/stream");
    }

    static readonly LiveHttpOptions Fast = new(CapacityBytes: 4096, PrefillBytes: 1, ConnectTimeoutMs: 1000, ReadIdleTimeoutMs: 2000,
        BudgetMs: 300, BaseBackoffMs: 1, MaxBackoffMs: 2);

    [Fact]
    public async Task LiveStream_ADropReconnects_AndTheTrackKeepsPlaying_ThenTheBudgetEndsItWithAnError()
    {
        LiveResponse first = Response(IcyBody(4, ("AAAA", "One")));
        LiveResponse second = Response(IcyBody(4, ("BBBB", "Two")), metaInt: 4);
        var reconnectGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int connects = 0;
        async Task<LiveResponse> Connect(string url, CancellationToken ct)
        {
            int attempt = Interlocked.Increment(ref connects);
            if (attempt == 1) return first;
            await reconnectGate.Task.WaitAsync(ct);   // the reconnect waits until the test is listening for titles
            return attempt == 2 ? second : throw new IOException("refused");
        }

        // The first response declares no metaint: its bytes pass through verbatim, the length byte and block included.
        using LiveHttpStream stream = await LiveHttpStream.OpenAsync("http://radio.test/stream", (u, c) => Connect(u, c), Fast, Ct);
        Assert.Equal("audio/mpeg", stream.ContentType);
        Assert.Equal("Test FM", stream.StationName);
        Assert.False(stream.CanSeek);

        var titles = new List<string>();
        stream.TitleChanged += t => { lock (titles) titles.Add(t); };
        reconnectGate.SetResult();

        var got = new MemoryStream();
        var buf = new byte[256];
        Exception? ended = null;
        try
        {
            while (true)
            {
                int n = stream.Read(buf);
                if (n <= 0) break;
                got.Write(buf, 0, n);
            }
        }
        catch (IOException ex) { ended = ex; }

        Assert.NotNull(ended);   // a live body never reads a clean zero: the budget ends it as an error
        string text = Encoding.ASCII.GetString(got.ToArray());
        Assert.StartsWith("AAAA", text, StringComparison.Ordinal);
        Assert.EndsWith("BBBB", text, StringComparison.Ordinal);
        Assert.True(connects >= 3);
        lock (titles) Assert.Equal(["Two"], titles);
    }

    [Fact]
    public async Task LiveStream_ARefusedFirstConnectIsAnImmediateFailure()
    {
        await Assert.ThrowsAsync<IOException>(() => LiveHttpStream.OpenAsync("http://radio.test/stream",
            (_, _) => Task.FromResult(Response([], status: 404)), Fast, Ct));
    }

    [Theory]
    [InlineData(0, 500)]
    [InlineData(1, 2000)]
    [InlineData(2, 8000)]
    [InlineData(9, 8000)]
    public void LiveStream_BackoffGrowsByFour_AndCaps(int attempt, int expected)
        => Assert.Equal(expected, LiveHttpStream.BackoffMs(attempt, LiveHttpOptions.Default));

    // ── ADTS ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>An AAC-LC, 44.1 kHz, stereo ADTS frame of <paramref name="length"/> bytes (header included).</summary>
    static byte[] AdtsFrame(int length)
    {
        var f = new byte[length];
        f[0] = 0xFF;
        f[1] = 0xF1;                                        // MPEG-4, layer 00, no CRC
        f[2] = (1 << 6) | (4 << 2);                         // profile LC (AOT 2), rate index 4 (44.1 kHz), channel bit 0
        f[3] = (byte)((2 << 6) | ((length >> 11) & 0x03));  // channels 2, length top bits
        f[4] = (byte)((length >> 3) & 0xFF);
        f[5] = (byte)(((length & 0x07) << 5) | 0x1F);
        f[6] = 0xFC;                                        // one raw data block
        return f;
    }

    [Fact]
    public void Adts_ParsesTheHeader_AndSynthesisesTheAudioSpecificConfig()
    {
        Assert.True(Adts.TryParseHeader(AdtsFrame(300), out AdtsHeader h));
        Assert.Equal(44100, h.SampleRate);
        Assert.Equal(2, h.ChannelConfiguration);
        Assert.Equal(300, h.FrameLength);
        Assert.Equal(7, h.HeaderLength);
        Assert.Equal(1, h.RawDataBlocks);
        Assert.False(h.Mpeg2);

        Span<byte> asc = stackalloc byte[2];
        Assert.Equal(2, Adts.WriteAudioSpecificConfig(in h, asc));
        Assert.Equal(0x12, asc[0]);   // AOT 2, rate index 4 …
        Assert.Equal(0x10, asc[1]);   // … channels 2
    }

    [Fact]
    public void Adts_AnMp3FrameIsNotAnAdtsFrame()
    {
        byte[] mp3 = new byte[16];
        SilentMp3.Fill(0, mp3);
        Assert.False(Adts.TryParseHeader(mp3, out _));
        Assert.False(LooksAac(null, mp3));
        Assert.True(LooksAac(null, AdtsFrame(64).AsSpan(0, 64)));
    }

    [Fact]
    public void AdtsReader_ResyncsOverJunk_AndReadsWholeFrames()
    {
        var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("junk!"));
        for (int i = 0; i < 3; i++) ms.Write(AdtsFrame(200 + i));
        ms.Position = 0;

        var reader = new AdtsFrameReader(ms);
        var lengths = new List<int>();
        while (reader.TryReadFrame(out ReadOnlySpan<byte> frame, out AdtsHeader header))
        {
            Assert.Equal(header.FrameLength, frame.Length);
            lengths.Add(frame.Length);
        }

        Assert.Equal([200, 201, 202], lengths);
        Assert.Equal(5, reader.ResyncSkipped);
    }

    [Fact]
    public void AacDecoder_ABodyWithNoAdtsFrameIsRefusedBeforeTheMftIsTouched()
    {
        var decoder = new AacAudioDecoder(0f);
        Assert.False(decoder.TryOpen(new BytesSource(Encoding.ASCII.GetBytes("definitely not aac, just words")), new MixFormat(48000, 2), out _));
        Assert.Equal(-1, decoder.Read(new float[64]));
    }

    [Fact]
    public void ConformInto_DuplicatesMono_AndDownmixesStereo()
    {
        float[] stereo = new float[4];
        AacAudioDecoder.ConformInto([0.5f, -0.25f], 1, stereo, 2, 2f);
        Assert.Equal([1f, 1f, -0.5f, -0.5f], stereo);

        float[] mono = new float[2];
        AacAudioDecoder.ConformInto([0.2f, 0.4f, 1f, 0f], 2, mono, 1, 1f);
        Assert.Equal(0.3f, mono[0], 5);
        Assert.Equal(0.5f, mono[1], 5);
    }

    // ── the silent MP3 body ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SilentMp3_IsWholeFramesOfTheSameHeader_AndSniffsAsMp3()
    {
        var body = new SilentMp3(2);
        long frames = (long)Math.Ceiling(2 * 44100.0 / 1152);
        Assert.Equal(frames * SilentMp3.FrameBytes, body.Length);
        Assert.Equal(frames * 1152 * 1000 / 44100, SilentMp3.DurationMs(2));

        var span = new byte[SilentMp3.FrameBytes * 2 + 10];
        SilentMp3.Fill(SilentMp3.FrameBytes - 2, span);   // straddle a frame boundary
        Assert.Equal(0, span[0]);
        Assert.Equal(0xFF, span[2]);
        Assert.Equal(0xFB, span[3]);
        Assert.Equal(0xFF, span[2 + SilentMp3.FrameBytes]);
        Assert.Equal(Spotify.Audio.Format.Mp3, Playback.Audio.SniffFormat(span.AsSpan(2)));
    }

    /// <summary>A seekable in-memory <see cref="IMediaByteSource"/>.</summary>
    sealed class BytesSource(byte[] bytes) : IMediaByteSource
    {
        long _pos;
        public bool TryOpen(in DataSpec spec) { _pos = Math.Max(0, spec.Position); return true; }
        public int Read(Span<byte> dst)
        {
            int n = (int)Math.Min(dst.Length, bytes.Length - _pos);
            if (n <= 0) return 0;
            bytes.AsSpan((int)_pos, n).CopyTo(dst);
            _pos += n;
            return n;
        }
        public long Seek(long offset) => _pos = Math.Clamp(offset, 0, bytes.Length);
        public long? Length => bytes.Length;
        public SourceCaps Caps => new() { Seekable = true, KnownLength = true };
        public void Cancel() { }
        public void Close() { }
    }
}
