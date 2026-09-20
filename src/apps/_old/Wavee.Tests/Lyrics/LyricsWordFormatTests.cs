using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Wavee.Backend.Lyrics;
using Wavee.Backend.Lyrics.Lyricify;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests.Lyrics;

// The grey-provider word-synced body parsers (KRC/YRC/QRC/richsync) + the KRC decrypter — pure, no network.
public class LyricsWordFormatTests
{
    [Fact]
    public void Krc_ParsesRelativeWordTiming()
    {
        var doc = LyricsWordFormats.ParseKrc("[1000,2000]<0,500,0>Hel<500,500,0>lo\n", "t");
        Assert.Equal(LyricsSyncKind.Syllable, doc.Sync);
        Assert.Single(doc.Lines);
        Assert.Equal(1000, doc.Lines[0].StartMs);
        Assert.Equal("Hello", doc.Lines[0].Text);
        Assert.Equal(2, doc.Lines[0].Syllables.Count);
        Assert.Equal(1000, doc.Lines[0].Syllables[0].StartMs);   // lineStart + 0 (relative)
        Assert.Equal(1500, doc.Lines[0].Syllables[1].StartMs);   // lineStart + 500
    }

    [Fact]
    public void Yrc_ParsesAbsoluteWordTiming_AndSkipsJsonMeta()
    {
        var doc = LyricsWordFormats.ParseYrc("{\"t\":1000,\"c\":[]}\n[1000,2000](1000,500,0)Hel(1500,500,0)lo\n", "t");
        Assert.Equal(LyricsSyncKind.Syllable, doc.Sync);
        Assert.Single(doc.Lines);                                 // the {..} metadata line is skipped
        Assert.Equal(1000, doc.Lines[0].Syllables[0].StartMs);    // absolute
        Assert.Equal(1500, doc.Lines[0].Syllables[1].StartMs);
    }

    [Fact]
    public void Qrc_ParsesWordThenTiming()
    {
        var doc = LyricsWordFormats.ParseQrc("[1000,2000]Hel(1000,500)lo(1500,500)\n", "t");
        Assert.Equal(LyricsSyncKind.Syllable, doc.Sync);
        Assert.Equal("Hello", doc.Lines[0].Text);
        Assert.Equal(1000, doc.Lines[0].Syllables[0].StartMs);    // absolute
        Assert.Equal(1500, doc.Lines[0].Syllables[1].StartMs);
    }

    [Fact]
    public void Qrc_ExtractsFromXmlWrapper()
    {
        var doc = LyricsWordFormats.ParseQrc("<QrcInfos><LyricInfo><Lyric_1 LyricContent=\"[1000,500]Hi(1000,500)\"/></LyricInfo></QrcInfos>", "t");
        Assert.Single(doc.Lines);
        Assert.Equal("Hi", doc.Lines[0].Text);
        Assert.Equal(1000, doc.Lines[0].Syllables[0].StartMs);
    }

    [Fact]
    public void Richsync_ParsesCharOffsets_InSeconds()
    {
        var doc = LyricsWordFormats.ParseRichsync(
            "[{\"ts\":1.0,\"te\":3.0,\"x\":\"Hello\",\"l\":[{\"c\":\"Hel\",\"o\":0.0},{\"c\":\"lo\",\"o\":0.5}]}]", "t");
        Assert.Equal(LyricsSyncKind.Syllable, doc.Sync);
        Assert.Equal(1000, doc.Lines[0].StartMs);
        Assert.Equal(3000, doc.Lines[0].EndMs);
        Assert.Equal("Hello", doc.Lines[0].Text);
        Assert.Equal(1000, doc.Lines[0].Syllables[0].StartMs);    // (ts + o)·1000
        Assert.Equal(1500, doc.Lines[0].Syllables[1].StartMs);
    }

    [Fact]
    public void Richsync_FoldsWhitespaceTokens_IntoPreviousVisibleSyllable()
    {
        var doc = LyricsWordFormats.ParseRichsync(
            "[{\"ts\":1.0,\"te\":3.0,\"x\":\"Hello world\",\"l\":[{\"c\":\"Hello\",\"o\":0.0},{\"c\":\" \",\"o\":0.5},{\"c\":\"world\",\"o\":0.8}]}]", "t");

        var line = doc.Lines[0];
        Assert.Equal(2, line.Syllables.Count);
        Assert.Equal("Hello ", line.Syllables[0].Text);
        Assert.Equal(1000, line.Syllables[0].StartMs);
        Assert.Equal(1800, line.Syllables[0].EndMs);
        Assert.Equal("world", line.Syllables[1].Text);
        Assert.DoesNotContain(line.Syllables, s => string.IsNullOrWhiteSpace(s.Text));
    }

    [Fact]
    public void Krc_DecryptRoundTrip()
    {
        const string payload = "X[1000,2000]<0,500,0>Hi";   // the leading char is dropped by the decrypter
        string b64 = EncryptKrc(payload);
        Assert.Equal("[1000,2000]<0,500,0>Hi", LyricCrypto.DecryptKrc(b64));
    }

    static string EncryptKrc(string s)
    {
        byte[] key = { 0x40, 0x47, 0x61, 0x77, 0x5e, 0x32, 0x74, 0x47, 0x51, 0x36, 0x31, 0x2d, 0xce, 0xd2, 0x6e, 0x69 };
        byte[] raw = Encoding.UTF8.GetBytes(s);
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionMode.Compress, leaveOpen: true)) z.Write(raw, 0, raw.Length);
        byte[] comp = ms.ToArray();
        for (int i = 0; i < comp.Length; i++) comp[i] ^= key[i % key.Length];
        byte[] withHeader = new byte[comp.Length + 4];   // 4-byte "krc1" header the decrypter drops
        Array.Copy(comp, 0, withHeader, 4, comp.Length);
        return Convert.ToBase64String(withHeader);
    }
}
