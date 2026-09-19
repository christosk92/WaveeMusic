// ── Platform/HtmlEntities.cs — CORE: the ONE HTML entity decoder (bios, rich text, the UTF-8 lead) ─────────────────
//
// Role: CORE (engine-free; the span paths allocate nothing)
// Owner: L
// Wave: 5 (0.3 gap fix A3 — "bio shows &#34;")
// Budget: 180 lines
// Spec: DERIVED
//
// WHY ONE CLASS. Before this file three decoders disagreed: `ArtistText.Lead` knew six named entities over UTF-8,
// `ArtistText.StripHtml` decoded nothing at all, and the rich-text parser in Controls.Art.cs kept the only complete
// (numeric + named) table private. A biography from the wire carries `&#34;`, `&#8217;`, `&#x27;`, `&rsquo;` and the
// dashes; whichever path a surface happened to take showed some of them literally. Every caller now asks THIS class,
// so the hero's lead, the about panel's rich text and the string rule spell the same characters.
//
// THE NUMERIC FORMS: `&#NN;` (decimal) and `&#xHH;` / `&#XHH;` (hex), code point 1 … U+10FFFF, never a surrogate.
// THE NAMED SET: amp lt gt quot apos nbsp ndash mdash hellip lsquo rsquo ldquo rdquo — case-sensitive, as HTML is.
// `nbsp` decodes to a PLAIN space (U+0020), as both former decoders did: `Lead` trims ASCII space only and must land
// where `FirstSentence`'s `Trim()` does, and a non-breaking glyph buys a biography nothing.
// EVERYTHING ELSE stays literal: `Decode` answers -1 with `consumed = 0`, and the caller appends the '&' as text.
//
// THE BUFFER INVARIANT (why `Lead` may decode IN PLACE over a buffer sized to the html). A decoded code point never
// needs more UTF-8 bytes than the entity text it replaces:
//     cp <  0x80      → 1 byte;  the shortest entity is 4 chars ("&lt;", "&#9;")
//     cp <  0x800     → 2 bytes; needs ≥ 3 decimal digits ("&#128;" = 6) or ≥ 2 hex digits ("&#x80;" = 6)
//     cp <  0x10000   → 3 bytes; needs ≥ 4 decimal digits ("&#2048;" = 7) or ≥ 3 hex digits ("&#x800;" = 7)
//     cp ≥  0x10000   → 4 bytes; needs ≥ 5 decimal digits ("&#65536;" = 8) or ≥ 5 hex digits ("&#x10000;" = 9)
//     named           → at most 3 bytes (U+2026), every name is ≥ 4 chars with its '&' and ';'
// So a UTF-8 buffer of `html.Length` bytes always holds the decoded text, and byte decoding never grows the buffer.
// `HtmlEntitiesTests.Utf8_of_a_decoded_entity_never_outgrows_the_entity_text` pins this.

using System.Text;

namespace Wavee;

/// <summary>The one HTML entity decoder: numeric (<c>&amp;#NN;</c>, <c>&amp;#xHH;</c>) and the named set a biography
/// carries. Unknown or malformed text is left literal. Decoding into UTF-8 never needs more bytes than the entity
/// text it replaces (see the file header), so an in-place byte pass stays within its buffer.</summary>
public static class HtmlEntities
{
    /// <summary>The longest entity text accepted, '&amp;' and ';' included: <c>&amp;#1114111;</c> is 10. A ';' further
    /// out is not this entity's, and the bound also keeps every accepted numeric form inside an <see cref="int"/>.</summary>
    public const int MaxEntityLength = 10;

    /// <summary>Decode the entity at the head of <paramref name="s"/> (<c>s[0]</c> must be '&amp;'). Returns the code point
    /// and the characters it spanned, or -1 with <paramref name="consumed"/> = 0 when the text is not an entity this
    /// decoder knows.</summary>
    public static int Decode(ReadOnlySpan<char> s, out int consumed)
    {
        consumed = 0;
        if (s.Length < 4 || s[0] != '&') return -1;                                 // "&lt;" is the shortest form
        int limit = Math.Min(s.Length, MaxEntityLength);
        int semi = s[..limit].IndexOf(';');
        if (semi < 2) return -1;                                                     // "&;" or no ';' within the bound
        ReadOnlySpan<char> body = s[1..semi];
        int cp = body[0] == '#' ? Numeric(body[1..]) : Named(body);
        if (cp < 0) return -1;
        consumed = semi + 1;
        return cp;
    }

    /// <summary><see cref="Decode(ReadOnlySpan{char}, out int)"/> over UTF-8: the ASCII head is widened into a stack
    /// buffer, stopping at the first byte ≥ 0x80 (no entity contains one). <paramref name="consumed"/> is in bytes.</summary>
    public static int Decode(ReadOnlySpan<byte> utf8, out int consumed)
    {
        Span<char> widened = stackalloc char[MaxEntityLength];
        int n = Math.Min(utf8.Length, MaxEntityLength);
        int i = 0;
        for (; i < n; i++)
        {
            byte b = utf8[i];
            if (b >= 0x80) break;
            widened[i] = (char)b;
        }
        return Decode(widened[..i], out consumed);
    }

    /// <summary>Decode every entity in <paramref name="s"/>; unknown ones stay literal. Returns the SAME instance when the
    /// text has no '&amp;' (or nothing in it decodes), so a plain string costs no allocation.</summary>
    public static string DecodeAll(string s)
    {
        int amp = s.IndexOf('&');
        if (amp < 0) return s;
        var result = new StringBuilder(s.Length);
        result.Append(s, 0, amp);
        bool decoded = false;
        for (int i = amp; i < s.Length;)
        {
            char c = s[i];
            if (c == '&')
            {
                int cp = Decode(s.AsSpan(i), out int consumed);
                if (cp >= 0)
                {
                    AppendCodePoint(result, cp);
                    i += consumed;
                    decoded = true;
                    continue;
                }
            }
            result.Append(c);
            i++;
        }
        return decoded ? result.ToString() : s;
    }

    /// <summary>Append <paramref name="cp"/> as one char, or a surrogate pair past U+FFFF.</summary>
    public static void AppendCodePoint(StringBuilder into, int cp)
    {
        if (cp <= 0xFFFF)
        {
            into.Append((char)cp);
            return;
        }
        int v = cp - 0x10000;
        into.Append((char)(0xD800 + (v >> 10)));
        into.Append((char)(0xDC00 + (v & 0x3FF)));
    }

    /// <summary>Write <paramref name="cp"/> as UTF-8 into <paramref name="into"/>. Returns the bytes written, 0 when there
    /// is no room for the whole sequence (nothing is written then).</summary>
    public static int EncodeUtf8(int cp, Span<byte> into)
    {
        if (cp < 0x80)
        {
            if (into.Length < 1) return 0;
            into[0] = (byte)cp;
            return 1;
        }
        if (cp < 0x800)
        {
            if (into.Length < 2) return 0;
            into[0] = (byte)(0xC0 | (cp >> 6));
            into[1] = (byte)(0x80 | (cp & 0x3F));
            return 2;
        }
        if (cp < 0x10000)
        {
            if (into.Length < 3) return 0;
            into[0] = (byte)(0xE0 | (cp >> 12));
            into[1] = (byte)(0x80 | ((cp >> 6) & 0x3F));
            into[2] = (byte)(0x80 | (cp & 0x3F));
            return 3;
        }
        if (into.Length < 4) return 0;
        into[0] = (byte)(0xF0 | (cp >> 18));
        into[1] = (byte)(0x80 | ((cp >> 12) & 0x3F));
        into[2] = (byte)(0x80 | ((cp >> 6) & 0x3F));
        into[3] = (byte)(0x80 | (cp & 0x3F));
        return 4;
    }

    // ── the two tables ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The digits after "&amp;#": decimal, or hex after 'x' / 'X'. -1 when empty, non-digit, zero, a surrogate or
    /// past U+10FFFF. <see cref="MaxEntityLength"/> caps the run at 7 decimal / 6 hex digits, so no overflow is possible.</summary>
    static int Numeric(ReadOnlySpan<char> digits)
    {
        if (digits.IsEmpty) return -1;
        int radix = 10;
        if (digits[0] is 'x' or 'X')
        {
            radix = 16;
            digits = digits[1..];
            if (digits.IsEmpty) return -1;
        }
        int cp = 0;
        foreach (char c in digits)
        {
            int v = c is >= '0' and <= '9' ? c - '0'
                  : radix == 16 && c is >= 'a' and <= 'f' ? c - 'a' + 10
                  : radix == 16 && c is >= 'A' and <= 'F' ? c - 'A' + 10
                  : -1;
            if (v < 0) return -1;
            cp = cp * radix + v;
        }
        return cp is > 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF) ? cp : -1;
    }

    /// <summary>The named set, case-sensitive. <c>nbsp</c> is a plain space on purpose (file header).</summary>
    static int Named(ReadOnlySpan<char> name) => name switch
    {
        "amp" => '&',
        "lt" => '<',
        "gt" => '>',
        "quot" => '"',
        "apos" => '\'',
        "nbsp" => ' ',
        "ndash" => '–',
        "mdash" => '—',
        "hellip" => '…',
        "lsquo" => '‘',
        "rsquo" => '’',
        "ldquo" => '“',
        "rdquo" => '”',
        _ => -1,
    };
}
