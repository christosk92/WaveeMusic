using System;
using System.Collections.Generic;
using Wavee.Core;

namespace Wavee.Backend.Lyrics;

/// <summary>The decisions behind "is this row a credit or provider padding rather than a sung line?", in the order they
/// are TRUSTED (issue #16: Kugou/QQ/NetEase open a document with 词：/曲：/编曲： rows that rendered as lyrics and dragged
/// the reranker's text/coverage terms down). A hard-coded word list is deliberately the LAST resort, not the rule:
///
/// <list type="number">
/// <item><b>Structural</b> — <see cref="IsStructuralMetadata"/>. The provider's own format marks the row as metadata:
/// the LRC-family <c>[ti:]/[ar:]/[al:]/[by:]/[offset:]/[language:]</c> tags (Kugou KRC, QQ QRC, NetEase LRC, LRCLIB,
/// Musixmatch) and NetEase YRC's JSON credit objects. Language-free and never wrong, so the parsers apply it and nothing
/// that matches ever becomes a <see cref="LyricLine"/>. It does NOT reach the rows this issue is about: the CJK providers
/// ship their writer credits as ordinary TIMED lines — Kugou even syllable-times "词：YOSKE (Balcony Swim)".</item>
/// <item><b>Reference alignment</b> — <see cref="TrimUnalignedEdges"/>. With the Spotify reference in hand, a
/// candidate's leading/trailing lines that align to NOTHING in it and sit outside the span the reference sings are
/// padding, whatever language they are in. Needs the reference, so it runs at the rank site rather than the fetch
/// chokepoint, and only ever touches the two edges.</item>
/// <item><b>Grammar</b> — <see cref="LooksLikeCreditLine"/>. The SHAPE <c>key: value</c>: a short key, then a value that
/// reads as a list of names (separators, capitalised words) rather than a clause. The known credit keys (词 / 作曲 /
/// 작사 / Lyrics by …) are a confidence BOOST on top: a known key is a credit whatever follows it, and with a full-width
/// colon even mid-document. <see cref="LyricsClean"/> applies this at the fetch chokepoint, positionally.</item>
/// </list>
///
/// <para>Beside those, <see cref="IsProviderBoilerplate"/> matches the literal sentences the CJK providers print in place
/// of lyrics (纯音乐，请欣赏 / 未经许可… / branding). Those ARE fixed strings, so a fixed-string match is the honest rule,
/// and <see cref="IsInstrumentalNotice"/> singles out the ones that say "there are no lyrics" at all.</para>
/// </summary>
public static class LyricsCreditRules
{
    // ── 1. structural ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>True for a RAW payload row the format itself marks as metadata, never a lyric: an LRC-family
    /// <c>[key:value]</c> tag with an alphabetic key (a timed row's key is digits: <c>[00:07.41]</c> / <c>[7416,2087]</c>),
    /// a NetEase YRC JSON object row, or a QRC XML wrapper row. The parsers consult this before building lines; the
    /// cleaner consults it again for a tag that slipped INTO a timed row's text.</summary>
    public static bool IsStructuralMetadata(string? rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine)) return false;
        string s = rawLine.Trim();
        if (s.Length < 3) return false;
        if (s[0] == '[' && s[^1] == ']')
        {
            int colon = s.IndexOf(':');
            // …and nothing after the closing bracket: "[ti:X][00:01.00]sung" is a timed row with a tag glued on.
            return colon > 1 && IsAsciiAlpha(s.AsSpan(1, colon - 1)) && s.IndexOf(']') == s.Length - 1;
        }
        // NetEase YRC interleaves {"t":0,"c":[{"tx":"作词: "},{"tx":"…"}]} credit objects with its timed rows.
        if (s[0] == '{' && s[^1] == '}') return true;
        // QQ's decrypted QRC is XML-wrapped; the wrapper rows are markup.
        if (s[0] == '<' && s[^1] == '>') return true;
        return false;
    }

    static bool IsAsciiAlpha(ReadOnlySpan<char> s)
    {
        foreach (char c in s) if (!char.IsAsciiLetter(c)) return false;
        return s.Length > 0;
    }

    // ── 2. reference alignment ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A reference shorter than this is too thin to say what a candidate's edges "should" align to.</summary>
    public const int MinReferenceLines = 8;
    /// <summary>An unaligned edge run longer than this is a different cut of the song (or a different song), not a
    /// credit block; leave it for the reranker to judge.</summary>
    public const int MaxEdgeRun = 6;
    /// <summary>Between the edges, at least this share of lines must align — otherwise the candidate is not
    /// demonstrably the same text (translation, romanization, wrong song) and "aligns to nothing" means nothing.</summary>
    public const double MinInteriorAgreement = 0.5;
    /// <summary>Timing corroboration: an edge line only counts as padding when it also sits OUTSIDE the span the
    /// reference sings (before its first line / after its last, offset-corrected) by more than this. A differently-worded
    /// first or last lyric sits AT the reference's edge and must survive.</summary>
    public const long EdgeSlackMs = 500;

    /// <summary>Drop the leading/trailing lines of <paramref name="cand"/> that align to no line of
    /// <paramref name="reference"/> and lie outside the reference's sung span. Returns the same instance when nothing
    /// qualifies. Kept lines keep their timestamps; a dropped line's syllables go with it.</summary>
    public static LyricsDocument TrimUnalignedEdges(LyricsDocument cand, LyricsDocument? reference, out int leading, out int trailing)
    {
        leading = trailing = 0;
        if (reference is null || reference.Lines.Count < MinReferenceLines || cand.Lines.Count < 2) return cand;

        int n = cand.Lines.Count;
        var candTokens = new List<string[]>(n);
        foreach (var l in cand.Lines) candTokens.Add(LyricsReranker.Tokens(l.Text));
        var refTokens = new List<string[]>(reference.Lines.Count);
        foreach (var l in reference.Lines) refTokens.Add(LyricsReranker.Tokens(l.Text));

        var matched = new bool[n];
        for (int i = 0; i < n; i++)
        {
            if (candTokens[i].Length == 0) continue;   // an empty row matches nothing (LineMatch calls two empties equal)
            foreach (var r in refTokens)
                if (LyricsReranker.LineMatch(candTokens[i], r)) { matched[i] = true; break; }
        }

        // The constant offset between the two documents, from the in-order alignment (the reranker's own estimate).
        var (_, pairs) = LyricsReranker.LcsAlign(candTokens, refTokens);
        if (pairs.Count < 3) return cand;   // nothing to anchor on
        var deltas = new long[pairs.Count];
        for (int k = 0; k < pairs.Count; k++)
            deltas[k] = cand.Lines[pairs[k].C].StartMs - reference.Lines[pairs[k].R].StartMs;
        Array.Sort(deltas);
        long offset = deltas.Length % 2 == 1 ? deltas[deltas.Length / 2] : (deltas[deltas.Length / 2 - 1] + deltas[deltas.Length / 2]) / 2;
        long refFirst = reference.Lines[0].StartMs, refLast = reference.Lines[^1].StartMs;

        int lead = 0;
        while (lead < n && !matched[lead] && cand.Lines[lead].StartMs - offset < refFirst - EdgeSlackMs) lead++;
        int trail = 0;
        while (trail < n - lead && !matched[n - 1 - trail] && cand.Lines[n - 1 - trail].StartMs - offset > refLast + EdgeSlackMs) trail++;
        if (lead > MaxEdgeRun) lead = 0;
        if (trail > MaxEdgeRun) trail = 0;
        if (lead + trail == 0 || lead + trail >= n) return cand;

        int interior = n - lead - trail, interiorMatched = 0;
        for (int i = lead; i < n - trail; i++) if (matched[i]) interiorMatched++;
        if (interiorMatched / (double)interior < MinInteriorAgreement) return cand;

        leading = lead; trailing = trail;
        var lines = new List<LyricLine>(interior);
        for (int i = lead; i < n - trail; i++) lines.Add(cand.Lines[i]);
        return cand with { Lines = lines };
    }

    // ── 3. grammar ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The key of a credit is short: "Words and Music by" is the longest sane one. A clause that happens to end
    /// in a colon is longer, or has more words, or carries digits/punctuation.</summary>
    public const int MaxKeyChars = 20;
    const int MaxKeyWords = 4;
    /// <summary>A value with no list separator still reads as names when it is this short and every cased word is
    /// capitalised ("Someone Else", "A Third Person").</summary>
    public const int MaxNameListWords = 8;

    /// <summary>True when <paramref name="text"/> has the shape of a credit: <c>key: value</c> with a short key and a
    /// name-list value, a known key with any value, a ℗/© notice, or a known no-colon English form ("Lyrics by X").
    /// <paramref name="knownKey"/> reports the boost (the key is in the credit vocabulary); <paramref name="fullWidthColon"/>
    /// reports the CJK <c>：</c>, which together with a known key is the only combination trusted mid-document.</summary>
    public static bool LooksLikeCreditLine(string? text, out bool knownKey, out bool fullWidthColon)
    {
        knownKey = false; fullWidthColon = false;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string s = StripEnclosure(text.Trim());
        if (s.Length == 0) return false;

        if (s[0] == '℗' || s[0] == '©') { knownKey = true; return true; }   // ℗ / ©

        int colon = IndexOfColon(s, out fullWidthColon);
        if (colon < 0)
        {
            // No colon ⇒ no key/value shape to test; only a form we recognise outright counts.
            knownKey = StartsWithKnownForm(s);
            return knownKey;
        }

        string key = s[..colon].Trim();
        if (key.Length == 0 || key.Length > MaxKeyChars || !KeyShapeOk(key)) return false;
        knownKey = AllPartsKnown(key);
        if (knownKey) return true;
        string value = s[(colon + 1)..].Trim();
        return value.Length > 0 && LooksLikeNameList(value);
    }

    static string StripEnclosure(string s)
    {
        if (s.Length < 2) return s;
        char a = s[0], z = s[^1];
        bool wrapped = (a == '[' && z == ']') || (a == '(' && z == ')') || (a == '（' && z == '）') || (a == '【' && z == '】');
        return wrapped ? s[1..^1].Trim() : s;
    }

    static int IndexOfColon(string s, out bool fullWidth)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == ':') { fullWidth = false; return i; }
            if (s[i] == '：') { fullWidth = true; return i; }
        }
        fullWidth = false;
        return -1;
    }

    static readonly char[] KeySeparators = { '/', '／', '、', '&', '・', '·', '+', ',', '，' };

    static bool KeyShapeOk(string key)
    {
        int words = 1;
        foreach (char c in key)
        {
            if (char.IsWhiteSpace(c)) { words++; continue; }
            if (char.IsDigit(c)) return false;
            if (!char.IsLetter(c) && Array.IndexOf(KeySeparators, c) < 0) return false;
        }
        return words <= MaxKeyWords;
    }

    static bool AllPartsKnown(string key)
    {
        int found = 0;
        foreach (string raw in key.Split(KeySeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            string part = raw.Trim();
            if (part.Length == 0) continue;
            if (part.EndsWith(" and", StringComparison.OrdinalIgnoreCase)) part = part[..^4].TrimEnd();
            if (!IsKnownKey(part)) return false;
            found++;
        }
        return found > 0;
    }

    static bool IsKnownKey(string part)
    {
        if (KnownKeys.Contains(part)) return true;
        // 作词人 / 作曲者 — the role plus a "person" suffix.
        return part.Length > 1 && (part[^1] == '人' || part[^1] == '者') && KnownKeys.Contains(part[..^1]);
    }

    static bool StartsWithKnownForm(string s)
    {
        foreach (string form in NoColonForms)
        {
            if (s.Length < form.Length || !s.StartsWith(form, StringComparison.OrdinalIgnoreCase)) continue;
            if (s.Length == form.Length || char.IsWhiteSpace(s[form.Length])) return true;
        }
        return false;
    }

    static readonly char[] NameSeparators = { '/', '／', '、', '&', '(', ')', '（', '）', ',', '，', '・', '·', ';', '；' };
    static readonly char[] WordBreaks = { ' ', '\t', '　', '/', '／', '、', '&', '(', ')', '（', '）', ',', '，', '・', '·', ';', '；' };
    static readonly char[] TrimPunct = { '.', '\'', '"', '“', '”', '-', '–', '—', '［', '］', '[', ']' };
    // Lower-case words a name list legitimately contains ("Simon and Garfunkel", "Vincent van Gogh").
    static readonly HashSet<string> Connectors = new(StringComparer.OrdinalIgnoreCase)
    { "and", "feat", "ft", "of", "the", "de", "da", "di", "la", "le", "van", "von", "der", "y", "e", "et" };

    static bool LooksLikeNameList(string value)
    {
        if (value[^1] is '?' or '!') return false;   // a sentence, not a list
        bool separated = value.IndexOfAny(NameSeparators) >= 0;
        int content = 0;
        foreach (string w in value.Split(WordBreaks, StringSplitOptions.RemoveEmptyEntries))
        {
            string t = w.Trim(TrimPunct);
            if (t.Length == 0) continue;
            content++;
            // Names are capitalised; scripts without case (CJK, Hangul) pass by construction.
            if (char.IsLower(t[0]) && !Connectors.Contains(t)) return false;
        }
        return content > 0 && (separated || content <= MaxNameListWords);
    }

    // The vocabulary boost. Matched as a WHOLE key part, case-insensitively, after the shape test above — never as a
    // substring of a lyric.
    static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        // Chinese (simplified + traditional)
        "词", "曲", "词曲", "作词", "作曲", "编曲", "制作", "监制", "混音", "母带", "录音", "和声", "配唱", "吉他", "贝斯", "鼓",
        "键盘", "弦乐", "出品", "发行", "演唱", "歌手", "原唱", "翻唱", "版权", "统筹", "企划", "封面", "歌词", "演奏", "op", "sp",
        "詞", "詞曲", "作詞", "編曲", "製作", "監製", "母帶", "錄音", "和聲", "貝斯", "鍵盤", "弦樂", "發行", "版權", "統籌", "企劃", "歌詞",
        // Japanese (作詞 / 作曲 / 編曲 are shared with the traditional-Chinese forms above)
        "歌", "唄", "作詞作曲",
        // Korean
        "작사", "작곡", "편곡", "노래", "프로듀서", "가수", "보컬",
        // English
        "lyrics", "lyric", "lyricist", "lyricists", "lyrics by", "words", "words by", "words and music", "words and music by",
        "words & music", "words & music by", "music", "music by", "music and lyrics", "music and lyrics by",
        "composed", "composed by", "composer", "composers", "composition",
        "arranged", "arranged by", "arranger", "arrangers", "arrangement",
        "produced", "produced by", "producer", "producers", "written", "written by", "writer", "writers",
        "mixed", "mixed by", "mixing", "mix", "mastered", "mastered by", "mastering", "recorded", "recorded by", "engineer",
        "performed", "performed by", "performer", "performers", "vocals", "vocal", "vocals by",
        "publisher", "publishing", "copyright", "label", "feat", "feat.", "featuring",
        "guitar", "bass", "drums", "keys", "keyboards", "strings", "artist", "title", "album",
    };

    // The English forms that stand without a colon ("Lyrics by Someone", "Copyright 2020 Label").
    static readonly string[] NoColonForms =
    {
        "lyrics by", "words by", "words and music by", "words & music by", "music and lyrics by", "music by", "composed by",
        "arranged by", "produced by", "written by", "mixed by", "mastered by", "recorded by", "performed by", "vocals by",
        "copyright", "feat.", "featuring", "ft.",
    };

    // ── provider boilerplate ─────────────────────────────────────────────────────────────────────────────────────────

    // Literal sentences the providers print INSTEAD of lyrics. Matched as substrings, lower-cased (CJK is unaffected).
    static readonly string[] InstrumentalNotices =
    {
        "纯音乐，请欣赏", "纯音乐,请欣赏", "纯音乐 请欣赏", "此歌曲为没有填词的纯音乐", "純音樂，請欣賞",
        "this song is instrumental", "this song is an instrumental", "this track is instrumental",
    };
    static readonly string[] BoilerplateFragments =
    {
        "本歌曲来自", "未经许可不得翻唱或使用", "未经授权", "未經許可", "qq音乐", "酷狗", "网易云", "kugou", "netease",
    };

    /// <summary>True for the provider's "this is an instrumental" sentence — the document has no lyrics at all, whatever
    /// else it carries (NetEase pairs it with the writer credits).</summary>
    public static bool IsInstrumentalNotice(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string lower = text.Trim().ToLowerInvariant();
        foreach (string s in InstrumentalNotices) if (lower.Contains(s, StringComparison.Ordinal)) return true;
        return LyricsText.Normalize(text) == "instrumental";
    }

    /// <summary>True for provider boilerplate — an instrumental notice, a licensing line, or branding.</summary>
    public static bool IsProviderBoilerplate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (IsInstrumentalNotice(text)) return true;
        string lower = text.Trim().ToLowerInvariant();
        foreach (string s in BoilerplateFragments) if (lower.Contains(s, StringComparison.Ordinal)) return true;
        return false;
    }
}
