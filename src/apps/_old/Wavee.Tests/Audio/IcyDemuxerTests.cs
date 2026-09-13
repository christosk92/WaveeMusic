using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Wavee.Backend.Audio;
using Xunit;

namespace Wavee.Tests.Audio;

/// <summary>The ICY interleave is a byte state machine because the socket hands us arbitrary chunk boundaries — these
/// tests are mostly about splitting the SAME wire bytes in every place a real read could split them.</summary>
public class IcyDemuxerTests
{
    sealed class RecordingSink : IByteSink
    {
        readonly MemoryStream _buf = new();
        public void Write(ReadOnlySpan<byte> data) => _buf.Write(data);
        public byte[] Bytes => _buf.ToArray();
    }

    static byte[] Audio(int n, int seed = 0)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)(seed + i);
        return b;
    }

    /// <summary>Encode one metadata block the way a server does: a length byte counting 16-byte units, then the padded
    /// payload. A null payload encodes the "nothing changed" empty block.</summary>
    static byte[] MetaBlock(string? payload, Encoding? encoding = null)
    {
        if (payload is null) return [0];
        var bytes = (encoding ?? Encoding.UTF8).GetBytes(payload);
        int units = (bytes.Length + 15) / 16;
        var block = new byte[1 + units * 16];
        block[0] = (byte)units;
        bytes.CopyTo(block, 1);
        return block;
    }

    static byte[] Concat(params byte[][] parts)
    {
        var ms = new MemoryStream();
        foreach (var p in parts) ms.Write(p, 0, p.Length);
        return ms.ToArray();
    }

    [Fact]
    public void ZeroMetaInt_IsPassThrough()
    {
        var demux = new IcyDemuxer(0);
        var sink = new RecordingSink();
        var wire = Audio(100);
        demux.Push(wire, sink);
        Assert.Equal(wire, sink.Bytes);
        Assert.Null(demux.CurrentTitle);
    }

    [Fact]
    public void StripsMetadata_AndFiresTitle()
    {
        var demux = new IcyDemuxer(16);
        var sink = new RecordingSink();
        var titles = new List<string>();
        demux.StreamTitleChanged += titles.Add;

        var wire = Concat(Audio(16), MetaBlock("StreamTitle='Aphex Twin - Xtal';"), Audio(16, seed: 100));
        demux.Push(wire, sink);

        Assert.Equal(Concat(Audio(16), Audio(16, seed: 100)), sink.Bytes);
        Assert.Equal(["Aphex Twin - Xtal"], titles);
        Assert.Equal("Aphex Twin - Xtal", demux.CurrentTitle);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(17)]
    public void SurvivesEveryChunkBoundary(int chunk)
    {
        var demux = new IcyDemuxer(16);
        var sink = new RecordingSink();
        var titles = new List<string>();
        demux.StreamTitleChanged += titles.Add;

        var wire = Concat(Audio(16), MetaBlock("StreamTitle='Boards of Canada - Roygbiv';"), Audio(16, seed: 100),
            MetaBlock(null), Audio(16, seed: 200));
        for (int i = 0; i < wire.Length; i += chunk)
            demux.Push(wire.AsSpan(i, Math.Min(chunk, wire.Length - i)), sink);

        Assert.Equal(Concat(Audio(16), Audio(16, seed: 100), Audio(16, seed: 200)), sink.Bytes);
        Assert.Equal(["Boards of Canada - Roygbiv"], titles);
    }

    [Fact]
    public void EmptyBlock_EmitsNoTitle_AndConsumesOnlyTheLengthByte()
    {
        var demux = new IcyDemuxer(8);
        var sink = new RecordingSink();
        int fired = 0;
        demux.StreamTitleChanged += _ => fired++;

        demux.Push(Concat(Audio(8), MetaBlock(null), Audio(8, seed: 50)), sink);

        Assert.Equal(Concat(Audio(8), Audio(8, seed: 50)), sink.Bytes);
        Assert.Equal(0, fired);
        Assert.Equal(1, demux.BlocksSeen);
    }

    [Fact]
    public void TwoBlocksInOnePush_BothParsed_SecondWins()
    {
        var demux = new IcyDemuxer(4);
        var sink = new RecordingSink();
        var titles = new List<string>();
        demux.StreamTitleChanged += titles.Add;

        demux.Push(Concat(
            Audio(4), MetaBlock("StreamTitle='One';"),
            Audio(4, seed: 40), MetaBlock("StreamTitle='Two';"),
            Audio(4, seed: 80)), sink);

        Assert.Equal(["One", "Two"], titles);
        Assert.Equal("Two", demux.CurrentTitle);
        Assert.Equal(12, sink.Bytes.Length);
    }

    [Fact]
    public void RepeatedIdenticalTitle_FiresOnce()
    {
        var demux = new IcyDemuxer(4);
        var sink = new RecordingSink();
        int fired = 0;
        demux.StreamTitleChanged += _ => fired++;

        for (int i = 0; i < 3; i++)
            demux.Push(Concat(Audio(4), MetaBlock("StreamTitle='Same';")), sink);

        Assert.Equal(1, fired);
    }

    [Fact]
    public void ApostropheInTitle_IsNotATerminator()
    {
        var demux = new IcyDemuxer(4);
        var sink = new RecordingSink();
        string? title = null;
        demux.StreamTitleChanged += t => title = t;

        demux.Push(Concat(Audio(4), MetaBlock("StreamTitle='Don't Stop Believin'';StreamUrl='';")), sink);

        Assert.Equal("Don't Stop Believin'", title);
    }

    [Fact]
    public void StreamUrlAfterTitle_IsNotSwallowed()
    {
        var demux = new IcyDemuxer(4);
        var sink = new RecordingSink();
        string? title = null;
        demux.StreamTitleChanged += t => title = t;

        demux.Push(Concat(Audio(4), MetaBlock("StreamTitle='Nujabes - Feather';StreamUrl='http://x/y';")), sink);

        Assert.Equal("Nujabes - Feather", title);
    }

    [Fact]
    public void Latin1Payload_DecodesReadably_WhenNotValidUtf8()
    {
        var demux = new IcyDemuxer(4);
        var sink = new RecordingSink();
        string? title = null;
        demux.StreamTitleChanged += t => title = t;

        // 0xE9 alone is a legal Latin-1 'é' but an invalid UTF-8 sequence.
        demux.Push(Concat(Audio(4), MetaBlock("StreamTitle='Café del Mar';", Encoding.Latin1)), sink);

        Assert.Equal("Café del Mar", title);
    }

    [Fact]
    public void Utf8Payload_DecodesAsUtf8()
    {
        var demux = new IcyDemuxer(4);
        var sink = new RecordingSink();
        string? title = null;
        demux.StreamTitleChanged += t => title = t;

        demux.Push(Concat(Audio(4), MetaBlock("StreamTitle='坂本龍一 - Merry Christmas';")), sink);

        Assert.Equal("坂本龍一 - Merry Christmas", title);
    }

    [Theory]
    [InlineData("Aphex Twin - Xtal", "Xtal", "Aphex Twin")]
    [InlineData("no separator here", "no separator here", null)]
    [InlineData("A - B - C", "B - C", "A")]
    [InlineData(" - trailing", "- trailing", null)]   // trimmed first, so the leading separator is not a split
    [InlineData("", "", null)]
    public void SplitStreamTitle_TakesTheFirstSeparator(string raw, string title, string? artist)
    {
        var (t, a) = IcyMetadata.SplitStreamTitle(raw);
        Assert.Equal(title, t);
        Assert.Equal(artist, a);
    }

    [Fact]
    public void ExtractStreamTitle_ReturnsNullWithoutTheKey()
    {
        Assert.Null(IcyMetadata.ExtractStreamTitle("StreamUrl='http://x';"));
        Assert.Null(IcyMetadata.ExtractStreamTitle(""));
    }
}
