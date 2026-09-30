// ── Wavee.Tests/LyricsCryptoTests.cs — Kugou KRC / QQ QRC decryption and the lyric_download.fcg split ──────────────
//
// All lyric text here is SYNTHETIC (fixture policy), except the Caribbean Queen KRC, which is already a checked-in
// DECRYPTED capture: the test encrypts it with the test-only helper and proves the decrypt → ParseKrc path yields the
// same document as parsing the plain text.
//
// The known-answer vectors were produced once from the ported (Lyricify, Apache-2.0) algorithm and are pinned so a
// change to the DES tables, the key schedule, the keys or the framing fails loudly. They exercise DECRYPT only, so they
// do not depend on the zlib compressor's output (which differs between runtime versions).

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class LyricsCryptoTests
{
    // ── KRC ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    const string KrcText = "[id:$00000000]\r\n[ti:Synthetic]\r\n[0,1000]<0,500,0>Syn<500,500,0>thetic\r\n";

    // base64(EncryptKrcForTests(KrcText)) — what kugou's download `content` field carries.
    const string KrcBase64 = "a3JjMTjb6rkSgyZ20bRpc2A6/CXrp6vLzL6g1Z1SIfKOFX5NIyTBFDRS1CQxIW20qdEMKDhXFMsCMtykQ4A=";

    [Fact]
    public void Krc_known_answer_decrypts_to_the_pinned_text()
        => Assert.Equal(KrcText, Lyrics.Crypto.DecryptKrc(Convert.FromBase64String(KrcBase64)));

    [Fact]
    public void Krc_round_trips_and_carries_the_krc1_header()
    {
        const string text = "[ar:Nobody]\n[1000,2000]<0,400,0>Made <400,600,0>up <1000,1000,0>words\n";
        var enc = Lyrics.Crypto.EncryptKrcForTests(text);
        Assert.Equal("krc1"u8.ToArray(), enc[..4]);
        Assert.Equal(text, Lyrics.Crypto.DecryptKrc(enc));
    }

    [Fact]
    public void Krc_strips_only_a_real_bom_and_keeps_the_opening_bracket()
    {
        Assert.Equal("[id:x]", Lyrics.Crypto.DecryptKrc(Lyrics.Crypto.EncryptKrcForTests("﻿[id:x]")));
        Assert.Equal("[id:x]", Lyrics.Crypto.DecryptKrc(Lyrics.Crypto.EncryptKrcForTests("[id:x]")));
    }

    [Fact]
    public void Krc_decrypt_does_not_mutate_the_input()
    {
        var enc = Lyrics.Crypto.EncryptKrcForTests(KrcText);
        var copy = (byte[])enc.Clone();
        Lyrics.Crypto.DecryptKrc(enc);
        Assert.Equal(copy, enc);
    }

    [Fact]
    public void Krc_malformed_inputs_return_null()
    {
        Assert.Null(Lyrics.Crypto.DecryptKrc([]));
        Assert.Null(Lyrics.Crypto.DecryptKrc([0x6b, 0x72, 0x63, 0x31]));                    // header only
        Assert.Null(Lyrics.Crypto.DecryptKrc("krc1 this is not zlib at all"u8.ToArray()));  // corrupt zlib

        var truncated = Lyrics.Crypto.EncryptKrcForTests(KrcText);
        Assert.Null(Lyrics.Crypto.DecryptKrc(truncated[..(truncated.Length / 2)]));         // cut mid-stream
        Assert.Null(Lyrics.Crypto.DecryptKrc(truncated[..^2]));                             // Adler-32 trailer cut
    }

    [Fact]
    public void Caribbean_queen_fixture_decrypts_and_parses_to_the_same_document_as_the_plain_text()
    {
        string plain = LyricsFixture.Read("kugou-krc-caribbean-queen.krc");
        string? decrypted = Lyrics.Crypto.DecryptKrc(Lyrics.Crypto.EncryptKrcForTests(plain));
        Assert.Equal(plain, decrypted);

        var expected = Lyrics.WordFormats.ParseKrc(plain, LyricsFixture.TrackId);
        var actual = Lyrics.WordFormats.ParseKrc(decrypted!, LyricsFixture.TrackId);
        Assert.NotEmpty(expected.Lines);
        AssertSameDoc(expected, actual);
    }

    // ── QRC ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    const string QrcText = "[0,2000]Synthetic (0,700)line (700,600)one(1300,700)\n[2000,1500]Second (2000,800)line(2800,700)";

    // EncryptQrcForTests(QrcText): zlib, zero-padded to 8 bytes, the QQ triple-DES (E k1 · D k2 · E k3), hex.
    const string QrcHex =
        "527D98D0BC3126CB13342560DF28C5C69EEC62A871B9D61594A1E8F4AA32454B2637714CFFAA1829F3CF70843BBAB1727C0F57" +
        "9799536A2D0E55DCD17201270AA48D249FAD305715C086B4535EA0AECE996D25AB9254C5CB";

    [Fact]
    public void Qrc_known_answer_decrypts_to_the_pinned_text()
    {
        Assert.Equal(QrcText, Lyrics.Crypto.DecryptQrc(QrcHex));
        Assert.Equal(QrcText, Lyrics.Crypto.DecryptQrc(QrcHex.ToLowerInvariant()));
        Assert.Equal(QrcText, Lyrics.Crypto.DecryptQrc("  " + QrcHex[..40] + "\n" + QrcHex[40..] + " \r\n"));
    }

    [Fact]
    public void Qrc_round_trips_through_the_encrypt_mode_key_schedule()
    {
        const string text = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<QrcInfos><LyricInfo LyricCount=\"1\">"
            + "<Lyric_1 LyricType=\"1\" LyricContent=\"[ti:Made up]&#10;[500,1500]Made (500,500)up(1000,1000)\"/>"
            + "</LyricInfo></QrcInfos>";
        string hex = Lyrics.Crypto.EncryptQrcForTests(text);
        Assert.Equal(0, hex.Length % 16);                                   // whole 8-byte blocks
        Assert.Equal(text, Lyrics.Crypto.DecryptQrc(hex));
        Assert.Equal("abc", Lyrics.Crypto.DecryptQrc(Lyrics.Crypto.EncryptQrcForTests("﻿abc")));
    }

    [Fact]
    public void Decrypted_qrc_parses_into_word_timing()
    {
        var doc = Lyrics.WordFormats.ParseQrc(Lyrics.Crypto.DecryptQrc(QrcHex)!, "t");
        Assert.Equal(2, doc.Lines.Count);
        Assert.Equal(0, doc.Lines[0].StartMs);
        Assert.Equal(3, doc.Lines[0].Syllables.Count);
        Assert.Equal(2000, doc.Lines[1].StartMs);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("zz11223344556677")]                    // not hex
    [InlineData("0011223344556677a")]                   // odd digit count
    [InlineData("001122334455")]                        // 6 bytes: not a multiple of 8
    [InlineData("0011223344556677")]                    // one block of garbage: corrupt zlib
    [InlineData("527D98D0BC3126CB13342560DF28C5C6")]    // the known answer truncated to 2 blocks: incomplete zlib
    public void Qrc_malformed_inputs_return_null(string hex)
        => Assert.Null(Lyrics.Crypto.DecryptQrc(hex));

    // ── lyric_download.fcg split ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Split_picks_each_block_by_name_even_when_roma_is_the_longest()
    {
        const string orig = "[0,1000]Orig(0,1000)";
        const string ts = "[0,1000]Translated line";
        const string roma = "[0,1000]Ro(0,300)ma(300,300)ni(600,200)zed(800,200) and a lot longer";
        string o = Lyrics.Crypto.EncryptQrcForTests(orig), t = Lyrics.Crypto.EncryptQrcForTests(ts), r = Lyrics.Crypto.EncryptQrcForTests(roma);
        Assert.True(r.Length > o.Length && r.Length > t.Length);

        string xml = "<?xml version=\"1.0\" encoding=\"GB2312\" ?>\n<!--\n<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
            + "<QrcInfos>\n<lyric>\n"
            + $"<content><![CDATA[{o}]]></content>\n"
            + $"<contentts><![CDATA[{t}]]></contentts>\n"
            + $"<contentroma><![CDATA[{r}]]></contentroma>\n"
            + "</lyric>\n</QrcInfos>\n-->";

        var split = Lyrics.QrcXml.Split(xml);
        Assert.Equal(o, split.Orig);
        Assert.Equal(t, split.Ts);
        Assert.Equal(r, split.Roma);
        Assert.Equal(orig, Lyrics.Crypto.DecryptQrc(split.Orig!));
        Assert.Equal(ts, Lyrics.Crypto.DecryptQrc(split.Ts!));
        Assert.Equal(roma, Lyrics.Crypto.DecryptQrc(split.Roma!));
    }

    [Fact]
    public void Split_matches_whole_tag_names_regardless_of_order()
    {
        const string xml = "<lyric><contentroma>RRRRRRRRRRRR</contentroma>\n<contentts attr=\"1\">TTTT</contentts >"
            + "<content>\n  OOOO  \n</content></lyric>";
        var (orig, ts, roma) = Lyrics.QrcXml.Split(xml);
        Assert.Equal("OOOO", orig);
        Assert.Equal("TTTT", ts);
        Assert.Equal("RRRRRRRRRRRR", roma);
    }

    [Fact]
    public void Split_missing_or_empty_blocks_are_null()
    {
        var onlyOrig = Lyrics.QrcXml.Split("<!--<lyric><content><![CDATA[ABCDEF]]></content></lyric>-->");
        Assert.Equal("ABCDEF", onlyOrig.Orig);
        Assert.Null(onlyOrig.Ts);
        Assert.Null(onlyOrig.Roma);

        var empties = Lyrics.QrcXml.Split("<lyric><content>ABCD</content><contentts/><contentroma><![CDATA[]]></contentroma></lyric>");
        Assert.Equal("ABCD", empties.Orig);
        Assert.Null(empties.Ts);
        Assert.Null(empties.Roma);

        AssertAllNull(Lyrics.QrcXml.Split(""));
        AssertAllNull(Lyrics.QrcXml.Split("not xml at all"));
        AssertAllNull(Lyrics.QrcXml.Split("<content>never closed"));
    }

    [Fact]
    public void Split_decodes_entities_in_plain_text_blocks_and_keeps_cdata_verbatim()
    {
        var plain = Lyrics.QrcXml.Split("<content>[00:01.00]Rock &amp; roll &lt;3</content>"
            + "<contentts><![CDATA[[00:01.00]a &amp; b]]></contentts>");
        Assert.Equal("[00:01.00]Rock & roll <3", plain.Orig);
        Assert.Equal("[00:01.00]a &amp; b", plain.Ts);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────────

    static void AssertAllNull((string? Orig, string? Ts, string? Roma) split)
    {
        Assert.Null(split.Orig);
        Assert.Null(split.Ts);
        Assert.Null(split.Roma);
    }

    static void AssertSameDoc(Lyrics.Doc expected, Lyrics.Doc actual)
    {
        Assert.Equal(expected.TrackId, actual.TrackId);
        Assert.Equal(expected.IsSynced, actual.IsSynced);
        Assert.Equal(expected.Sync, actual.Sync);
        Assert.Equal(expected.Provider, actual.Provider);
        Assert.Equal(expected.OffsetMsApplied, actual.OffsetMsApplied);
        Assert.Equal(expected.Lines.Count, actual.Lines.Count);
        for (int i = 0; i < expected.Lines.Count; i++)
        {
            var e = expected.Lines[i];
            var a = actual.Lines[i];
            Assert.Equal(e.StartMs, a.StartMs);
            Assert.Equal(e.EndMs, a.EndMs);
            Assert.Equal(e.Text, a.Text);
            Assert.Equal(e.Translation, a.Translation);
            Assert.Equal(e.Romanization, a.Romanization);
            Assert.Equal(e.IsWordByWord, a.IsWordByWord);
            Assert.Equal(e.Syllables, a.Syllables);   // Syllable is a value record
        }
    }
}
