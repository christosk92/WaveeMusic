// ── Wavee.Tests/HtmlEntitiesTests.cs — the one HTML entity decoder (0.3 gap fix A3, "bio shows &#34;") ────────────
//
// Pins the three contracts `Platform/HtmlEntities.cs` makes: numeric, hex and named entities decode over chars AND
// UTF-8 alike; anything malformed or unknown stays literal (-1, consumed 0, DecodeAll hands the text back unchanged);
// and a decoded code point's UTF-8 never outgrows the entity text it replaces — the invariant `ArtistText.Lead` relies
// on to decode in place over a buffer sized to the html.

using System.Text;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class HtmlEntitiesTests
{
    [Theory]
    [InlineData("&#34;", '"', 5)]
    [InlineData("&#39;", '\'', 5)]
    [InlineData("&#x27;", '\'', 6)]
    [InlineData("&#X22;", '"', 6)]
    [InlineData("&#8217;", '’', 7)]
    [InlineData("&#x2019;", '’', 8)]
    [InlineData("&amp;", '&', 5)]
    [InlineData("&lt;", '<', 4)]
    [InlineData("&gt;", '>', 4)]
    [InlineData("&quot;", '"', 6)]
    [InlineData("&apos;", '\'', 6)]
    [InlineData("&nbsp;", ' ', 6)]
    [InlineData("&ndash;", '–', 7)]
    [InlineData("&mdash;", '—', 7)]
    [InlineData("&hellip;", '…', 8)]
    [InlineData("&lsquo;", '‘', 7)]
    [InlineData("&rsquo;", '’', 7)]
    [InlineData("&ldquo;", '“', 7)]
    [InlineData("&rdquo;", '”', 7)]
    public void Decode_reads_numeric_hex_and_named_entities_over_chars_and_utf8(string entity, char expected, int length)
    {
        string text = entity + " and the rest";                             // the ';' bound is the entity's, not the line's

        Assert.Equal((int)expected, HtmlEntities.Decode(text.AsSpan(), out int consumed));
        Assert.Equal(length, consumed);

        Assert.Equal((int)expected, HtmlEntities.Decode(Encoding.UTF8.GetBytes(text), out consumed));
        Assert.Equal(length, consumed);
    }

    [Theory]
    [InlineData("&#128512;", 0x1F600, 9)]
    [InlineData("&#x1F3B5;", 0x1F3B5, 9)]
    [InlineData("&#1114111;", 0x10FFFF, 10)]
    public void Decode_reads_code_points_past_the_basic_plane(string entity, int expected, int length)
    {
        Assert.Equal(expected, HtmlEntities.Decode(entity.AsSpan(), out int consumed));
        Assert.Equal(length, consumed);

        var sb = new StringBuilder();
        HtmlEntities.AppendCodePoint(sb, expected);
        Assert.Equal(char.ConvertFromUtf32(expected), sb.ToString());     // the surrogate pair, not a truncated char
    }

    [Theory]
    [InlineData("&foo;")]
    [InlineData("&Amp;")]           // names are case-sensitive, as HTML's are
    [InlineData("&#;")]
    [InlineData("&#x;")]
    [InlineData("&#xZZ;")]
    [InlineData("&#12a;")]
    [InlineData("&#0;")]
    [InlineData("&#55296;")]        // U+D800, a lone surrogate
    [InlineData("&#57343;")]        // U+DFFF
    [InlineData("&#1114112;")]      // one past U+10FFFF
    [InlineData("&#1234567890;")]   // the ';' sits past MaxEntityLength
    [InlineData("&amp")]            // no ';'
    [InlineData("& ")]
    [InlineData("&")]
    [InlineData("&;")]
    public void Decode_leaves_malformed_and_unknown_entities_literal(string text)
    {
        Assert.Equal(-1, HtmlEntities.Decode(text.AsSpan(), out int consumed));
        Assert.Equal(0, consumed);

        Assert.Equal(-1, HtmlEntities.Decode(Encoding.UTF8.GetBytes(text), out consumed));
        Assert.Equal(0, consumed);

        string sentence = "before " + text + " after";
        Assert.Equal(sentence, HtmlEntities.DecodeAll(sentence));
    }

    [Fact]
    public void Decode_requires_the_ampersand_at_the_head()
    {
        Assert.Equal(-1, HtmlEntities.Decode("x&amp;".AsSpan(), out int consumed));
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void Decode_over_utf8_stops_at_the_first_non_ascii_byte()
    {
        // "&#3" followed by 'é' (0xC3 0xA9) and ";" — no entity, and the widening never reads the multi-byte sequence
        Assert.Equal(-1, HtmlEntities.Decode(Encoding.UTF8.GetBytes("&#3é;"), out int consumed));
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void DecodeAll_walks_a_sentence_and_leaves_the_unknown_literal()
        => Assert.Equal("Nirvana \"Nevermind\" wasn’t small — R&D &bogus; sold & sold…",
                        HtmlEntities.DecodeAll("Nirvana &#34;Nevermind&#34; wasn&#8217;t small &mdash; R&D &bogus; sold &amp; sold&hellip;"));

    [Fact]
    public void DecodeAll_hands_back_the_same_instance_when_nothing_decodes()
    {
        string plain = "no ampersand here";
        Assert.Same(plain, HtmlEntities.DecodeAll(plain));

        string literal = "R&D & &bogus;";
        Assert.Same(literal, HtmlEntities.DecodeAll(literal));
    }

    [Theory]
    [InlineData("&#9;")]
    [InlineData("&#255;")]
    [InlineData("&#8217;")]
    [InlineData("&#128512;")]
    [InlineData("&#x1F3B5;")]
    [InlineData("&#x80;")]
    [InlineData("&#x800;")]
    [InlineData("&#x10000;")]
    [InlineData("&lt;")]
    [InlineData("&hellip;")]
    [InlineData("&rsquo;")]
    public void Utf8_of_a_decoded_entity_never_outgrows_the_entity_text(string entity)
    {
        byte[] text = Encoding.UTF8.GetBytes(entity);
        int cp = HtmlEntities.Decode(text, out int consumed);
        Assert.True(cp >= 0);
        Assert.Equal(text.Length, consumed);

        var into = new byte[4];
        int written = HtmlEntities.EncodeUtf8(cp, into);
        Assert.InRange(written, 1, consumed);                                // never more bytes than the entity spanned
        Assert.Equal(Encoding.UTF8.GetBytes(char.ConvertFromUtf32(cp)), into[..written]);
    }

    [Fact]
    public void EncodeUtf8_writes_nothing_when_the_room_is_short()
    {
        Assert.Equal(0, HtmlEntities.EncodeUtf8('a', Span<byte>.Empty));
        Assert.Equal(0, HtmlEntities.EncodeUtf8(0x2019, new byte[2]));
        Assert.Equal(0, HtmlEntities.EncodeUtf8(0x1F3B5, new byte[3]));

        var room = new byte[3];
        Assert.Equal(3, HtmlEntities.EncodeUtf8(0x2019, room));
        Assert.Equal(new byte[] { 0xE2, 0x80, 0x99 }, room);
    }
}
