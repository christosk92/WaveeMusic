using System;
using System.IO;
using Wavee.Backend.Audio;
using Xunit;

namespace Wavee.Tests.Audio;

/// <summary>ADTS is what an AAC radio stream hands us with no container to ask, so the first frame's header IS the
/// decoder configuration and the frame boundaries are what let a reconnect splice resync.</summary>
public class AdtsFrameParserTests
{
    /// <summary>Build one syntactically valid ADTS frame with a zeroed payload.</summary>
    internal static byte[] Frame(int sampleRateIndex = 4, int channels = 2, int payloadBytes = 20,
        int profile = 1, bool crcAbsent = true, bool mpeg2 = false)
    {
        int headerLength = crcAbsent ? 7 : 9;
        int frameLength = headerLength + payloadBytes;
        var f = new byte[frameLength];
        f[0] = 0xFF;
        f[1] = (byte)(0xF0 | (mpeg2 ? 0x08 : 0x00) | (crcAbsent ? 0x01 : 0x00));   // layer bits stay 00
        f[2] = (byte)(((profile & 3) << 6) | ((sampleRateIndex & 0xF) << 2) | ((channels >> 2) & 1));
        f[3] = (byte)(((channels & 3) << 6) | ((frameLength >> 11) & 3));
        f[4] = (byte)((frameLength >> 3) & 0xFF);
        f[5] = (byte)(((frameLength & 7) << 5) | 0x1F);
        f[6] = 0xFC;                                                              // buffer fullness + 1 raw data block
        return f;
    }

    [Fact]
    public void ParsesA44kStereoLcHeader()
    {
        Assert.True(AdtsFrameParser.TryParseHeader(Frame(), out var h));

        Assert.False(h.Mpeg2);
        Assert.Equal(1, h.Profile);                 // AAC-LC (audio object type 2)
        Assert.Equal(4, h.SamplingFrequencyIndex);
        Assert.Equal(44100, h.SampleRate);
        Assert.Equal(2, h.ChannelConfiguration);
        Assert.True(h.ProtectionAbsent);
        Assert.Equal(7, h.HeaderLength);
        Assert.Equal(27, h.FrameLength);
        Assert.Equal(1, h.RawDataBlocks);
    }

    [Theory]
    [InlineData(0, 96000)]
    [InlineData(3, 48000)]
    [InlineData(4, 44100)]
    [InlineData(6, 24000)]
    [InlineData(11, 8000)]
    public void MapsTheSamplingFrequencyTable(int index, int rate)
    {
        Assert.True(AdtsFrameParser.TryParseHeader(Frame(sampleRateIndex: index), out var h));
        Assert.Equal(rate, h.SampleRate);
    }

    [Fact]
    public void ReservedSamplingIndex_IsRejected()
    {
        Assert.False(AdtsFrameParser.TryParseHeader(Frame(sampleRateIndex: 13), out _));
    }

    [Fact]
    public void CrcPresent_ExtendsTheHeaderToNineBytes()
    {
        Assert.True(AdtsFrameParser.TryParseHeader(Frame(crcAbsent: false), out var h));
        Assert.False(h.ProtectionAbsent);
        Assert.Equal(9, h.HeaderLength);
    }

    [Fact]
    public void AnMp3FrameIsNotADTS()
    {
        // Same 11-bit sync; the LAYER field (01 = Layer III) is what separates them.
        byte[] mp3 = [0xFF, 0xFB, 0x90, 0x00, 0, 0, 0];
        Assert.False(AdtsFrameParser.TryParseHeader(mp3, out _));
    }

    [Fact]
    public void TooShortAndNoSync_AreRejected()
    {
        Assert.False(AdtsFrameParser.TryParseHeader([0xFF, 0xF1, 0x50], out _));
        Assert.False(AdtsFrameParser.TryParseHeader([0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06], out _));
    }

    [Fact]
    public void FindSync_SkipsLeadingJunk()
    {
        var frame = Frame();
        var wire = new byte[5 + frame.Length * 2];
        wire[0] = 0x11; wire[1] = 0x22; wire[2] = 0xFF; wire[3] = 0x00; wire[4] = 0x99;   // includes a bare 0xFF decoy
        frame.CopyTo(wire, 5);
        frame.CopyTo(wire, 5 + frame.Length);

        Assert.Equal(5, AdtsFrameParser.FindSync(wire));
    }

    [Fact]
    public void FindSync_RequiresASecondSyncWhenItCanCheck()
    {
        // A lone plausible header followed by garbage where the next frame should be: rejected.
        var frame = Frame(payloadBytes: 8);
        var wire = new byte[frame.Length + 16];
        frame.CopyTo(wire, 0);
        for (int i = frame.Length; i < wire.Length; i++) wire[i] = 0x5A;

        Assert.Equal(-1, AdtsFrameParser.FindSync(wire));
    }

    [Fact]
    public void FindSync_AcceptsACandidateItCannotYetVerify()
    {
        var frame = Frame(payloadBytes: 8);
        Assert.Equal(0, AdtsFrameParser.FindSync(frame));   // buffer ends exactly at the frame: nothing to verify against
    }

    [Fact]
    public void WritesTheAudioSpecificConfig()
    {
        Assert.True(AdtsFrameParser.TryParseHeader(Frame(sampleRateIndex: 4, channels: 2, profile: 1), out var h));
        Span<byte> asc = stackalloc byte[2];

        Assert.Equal(2, AdtsFrameParser.WriteAudioSpecificConfig(h, asc));

        // AOT 2 (LC) | sfi 4 | channels 2  =>  00010 0100 0010 000
        Assert.Equal(0x12, asc[0]);
        Assert.Equal(0x10, asc[1]);
    }

    [Fact]
    public void AudioSpecificConfig_TreatsChannelConfigZeroAsStereo()
    {
        Assert.True(AdtsFrameParser.TryParseHeader(Frame(channels: 0), out var h));
        Span<byte> asc = stackalloc byte[2];
        AdtsFrameParser.WriteAudioSpecificConfig(h, asc);
        Assert.Equal(2, (asc[1] >> 3) & 0x0F);
    }

    // ── AdtsFrameReader ──────────────────────────────────────────────────────────────────────────────────────────────

    static byte[] Concat(params byte[][] parts)
    {
        var ms = new MemoryStream();
        foreach (var p in parts) ms.Write(p, 0, p.Length);
        return ms.ToArray();
    }

    [Fact]
    public void Reader_WalksEveryFrame()
    {
        var wire = Concat(Frame(payloadBytes: 10), Frame(payloadBytes: 20), Frame(payloadBytes: 30));
        var reader = new AdtsFrameReader(new MemoryStream(wire));

        Assert.True(reader.TryReadFrame(out var f1, out var h1));
        Assert.Equal(17, f1.Length);
        Assert.Equal(17, h1.FrameLength);
        Assert.True(reader.TryReadFrame(out var f2, out _));
        Assert.Equal(27, f2.Length);
        Assert.True(reader.TryReadFrame(out var f3, out _));
        Assert.Equal(37, f3.Length);
        Assert.False(reader.TryReadFrame(out _, out _));
    }

    [Fact]
    public void Reader_ResyncsOverASplice()
    {
        // What a reconnect leaves behind: a truncated frame, then junk, then the new connection's frames.
        var good = Frame(payloadBytes: 12);
        var wire = Concat(good, good.AsSpan(0, 5).ToArray(), new byte[9], good, good);
        var reader = new AdtsFrameReader(new MemoryStream(wire));

        Assert.True(reader.TryReadFrame(out var first, out _));
        Assert.Equal(good.Length, first.Length);
        Assert.True(reader.TryReadFrame(out var next, out _));
        Assert.Equal(good.Length, next.Length);
        Assert.True(reader.ResyncSkipped > 0);
    }

    [Fact]
    public void Reader_SurvivesAStreamThatOnlyEverReturnsOneByte()
    {
        var wire = Concat(Frame(payloadBytes: 10), Frame(payloadBytes: 10));
        var reader = new AdtsFrameReader(new DripStream(wire));

        Assert.True(reader.TryReadFrame(out var f1, out _));
        Assert.Equal(17, f1.Length);
        Assert.True(reader.TryReadFrame(out var f2, out _));
        Assert.Equal(17, f2.Length);
    }

    /// <summary>A stream that hands back at most one byte per Read — the pathological chunking a socket can produce.</summary>
    sealed class DripStream(byte[] data) : Stream
    {
        int _pos;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= data.Length || count <= 0) return 0;
            buffer[offset] = data[_pos++];
            return 1;
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
}
