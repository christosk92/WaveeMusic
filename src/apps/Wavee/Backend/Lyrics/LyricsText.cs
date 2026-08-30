using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Wavee.Core;

namespace Wavee.Backend.Lyrics;

/// <summary>Format parsers + normalization (docs/lyrics-aggregator-reranker-plan.md §8). Pure (no HTTP, no state) so it is
/// fully unit-testable: text → <see cref="LyricsDocument"/>, plus credit-line detection and the comparison-text normalizer
/// the reranker aligns on. Adapted in spirit from Lyricify's parser/info-line helpers, rewritten for Wavee's model.</summary>
public static partial class LyricsText
{
    // ── LRC (incl. enhanced/A2 word timing) ──────────────────────────────────────────────────────────────────────────
    //
    // `[mm:ss.xx]text`  • multiple stamps per line `[t1][t2]text`  • metadata `[ti:][ar:][al:][by:][offset:]`
    // • enhanced word timing `[line] <mm:ss.xx>wo<mm:ss.xx>rd …` → per-syllable timing.

    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled)]
    private static partial Regex LineStampRx();

    [GeneratedRegex(@"<(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?>", RegexOptions.Compiled)]
    private static partial Regex WordStampRx();

    [GeneratedRegex(@"^\[([a-zA-Z]+):(.*)\]\s*$", RegexOptions.Compiled)]
    private static partial Regex MetaRx();

    /// <summary>Parse an LRC payload. Word-synced if any line carries <c>&lt;ts&gt;</c> word markers, else line-synced.
    /// <c>[offset:N]</c> is consumed and applied to every timestamp (positive = lyrics earlier).</summary>
    public static LyricsDocument ParseLrc(string lrc, string trackId, string? provider = "lrclib")
    {
        long offset = 0;
        var rawLines = new List<LyricLine>();
        bool anyWord = false;

        foreach (var line in SplitLines(lrc))
        {
            var meta = MetaRx().Match(line);
            if (meta.Success && !LineStampRx().IsMatch(line))
            {
                if (meta.Groups[1].Value.Equals("offset", StringComparison.OrdinalIgnoreCase)
                    && long.TryParse(meta.Groups[2].Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var off))
                    offset = off;
                continue;   // drop [ti:]/[ar:]/[al:]/[by:]/[length:]/[offset:] tags
            }

            var stamps = LineStampRx().Matches(line);
            if (stamps.Count == 0) continue;   // no timing → not a synced line (free leading text / blank)

            // Text is everything after the LAST leading stamp.
            int textStart = stamps[stamps.Count - 1].Index + stamps[stamps.Count - 1].Length;
            string rest = line.Substring(textStart);

            var (text, syllables) = ParseEnhancedWords(rest);
            if (syllables is { Count: > 0 }) anyWord = true;

            foreach (Match s in stamps)
            {
                long baseMs = StampMs(s.Groups[1].Value, s.Groups[2].Value, s.Groups[3].Value);
                // word stamps are absolute in enhanced LRC, so they already include the line base — only the line start shifts.
                rawLines.Add(new LyricLine(baseMs, text, (IReadOnlyList<LyricSyllable>?)syllables ?? Array.Empty<LyricSyllable>(), IsWordByWord: syllables is { Count: > 0 }));
            }
        }

        rawLines.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
        var shifted = offset == 0 ? rawLines : rawLines.Select(l => ShiftLine(l, offset)).ToList();
        var withEnds = DeriveEnds(shifted);
        bool synced = withEnds.Count > 0;
        var sync = anyWord ? LyricsSyncKind.Syllable : synced ? LyricsSyncKind.Line : LyricsSyncKind.Unsynced;
        return new LyricsDocument(trackId, synced, withEnds, sync, provider);
    }

    // `<00:01.50>Hel<00:01.90>lo` → text "Hello" + two syllables. Plain text (no <ts>) → no syllables.
    static (string Text, List<LyricSyllable>? Syllables) ParseEnhancedWords(string rest)
    {
        var stamps = WordStampRx().Matches(rest);
        if (stamps.Count == 0) return (rest.Trim(), null);

        var syl = new List<LyricSyllable>();
        var sb = new StringBuilder();
        for (int i = 0; i < stamps.Count; i++)
        {
            long start = StampMs(stamps[i].Groups[1].Value, stamps[i].Groups[2].Value, stamps[i].Groups[3].Value);
            int from = stamps[i].Index + stamps[i].Length;
            int to = i + 1 < stamps.Count ? stamps[i + 1].Index : rest.Length;
            string word = rest.Substring(from, to - from);
            long end = i + 1 < stamps.Count
                ? StampMs(stamps[i + 1].Groups[1].Value, stamps[i + 1].Groups[2].Value, stamps[i + 1].Groups[3].Value)
                : start + Math.Max(120, word.Trim().Length * 90);
            if (word.Length == 0) continue;
            syl.Add(new LyricSyllable(start, end, word));
            sb.Append(word);
        }
        return (sb.ToString().Trim(), syl);
    }

    static long StampMs(string mm, string ss, string frac)
    {
        long m = long.Parse(mm, CultureInfo.InvariantCulture);
        long s = long.Parse(ss, CultureInfo.InvariantCulture);
        long f = 0;
        if (!string.IsNullOrEmpty(frac))
        {
            // 2-digit = centiseconds, 3-digit = ms (pad/truncate to ms).
            string g = frac.Length switch { 1 => frac + "00", 2 => frac + "0", _ => frac.Substring(0, 3) };
            f = long.Parse(g, CultureInfo.InvariantCulture);
        }
        return (m * 60 + s) * 1000 + f;
    }

    static LyricLine ShiftLine(LyricLine l, long offset)
    {
        long Shift(long t) => Math.Max(0, t - offset);
        var syl = l.Syllables.Count == 0 ? l.Syllables
            : l.Syllables.Select(s => new LyricSyllable(Shift(s.StartMs), Shift(s.EndMs), s.Text)).ToList();
        return l with { StartMs = Shift(l.StartMs), Syllables = syl };
    }

    // ── TTML (AMLL — Apple-Music-Like-Lyrics word-synced) ────────────────────────────────────────────────────────────
    //
    // `<p begin="0:01.20" end="0:04.50"> <span begin=… end=…>word</span> … </p>`, namespace-agnostic. A `<p>` with child
    // timed `<span>`s is word-synced; a `<p>` with only text is line-synced. `ttm:role="x-translation"/"x-roman"` spans
    // become the line's Translation/Romanization.

    public static LyricsDocument ParseTtml(string ttml, string trackId, string? provider = "amll")
    {
        XDocument doc;
        try { doc = XDocument.Parse(ttml, LoadOptions.PreserveWhitespace); }
        catch { return new LyricsDocument(trackId, false, Array.Empty<LyricLine>(), LyricsSyncKind.Unsynced, provider); }

        var lines = new List<LyricLine>();
        bool anyWord = false;
        foreach (var p in doc.Descendants().Where(e => e.Name.LocalName == "p"))
        {
            long? begin = ParseTtmlTime(Attr(p, "begin"));
            long? end = ParseTtmlTime(Attr(p, "end"));
            if (begin is null) continue;

            var syl = new List<LyricSyllable>();
            string? translation = null, romanization = null;
            var main = new StringBuilder();

            foreach (var node in p.Nodes())
                AppendTtmlContent(node, main, syl, ref translation, ref romanization, ref anyWord);

            string text = CollapseWs(main.ToString());
            if (text.Length == 0 && syl.Count == 0) continue;
            lines.Add(new LyricLine(begin.Value, text, syl, end, translation, romanization, IsWordByWord: syl.Count > 0));
        }

        lines.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
        var withEnds = DeriveEnds(lines);
        var sync = anyWord ? LyricsSyncKind.Syllable : withEnds.Count > 0 ? LyricsSyncKind.Line : LyricsSyncKind.Unsynced;
        return new LyricsDocument(trackId, withEnds.Count > 0, withEnds, sync, provider);
    }

    static string? Attr(XElement e, string localName)
        => e.Attributes().FirstOrDefault(a => a.Name.LocalName == localName)?.Value;

    static void AppendTtmlContent(
        XNode node,
        StringBuilder main,
        List<LyricSyllable> syl,
        ref string? translation,
        ref string? romanization,
        ref bool anyWord)
    {
        if (node is XText t) { main.Append(t.Value); return; }
        if (node is not XElement span || span.Name.LocalName != "span") return;

        string role = Attr(span, "role") ?? "";   // ttm:role
        if (role.Contains("translation", StringComparison.OrdinalIgnoreCase)) { translation = span.Value.Trim(); return; }
        if (role.Contains("roman", StringComparison.OrdinalIgnoreCase)) { romanization = span.Value.Trim(); return; }
        if (role.Contains("bg", StringComparison.OrdinalIgnoreCase)) return;

        if (string.Equals(Attr(span, "ruby"), "container", StringComparison.OrdinalIgnoreCase))
        {
            var baseSpan = span.Descendants().FirstOrDefault(e => string.Equals(Attr(e, "ruby"), "base", StringComparison.OrdinalIgnoreCase));
            var textSpans = span.Descendants().Where(e => string.Equals(Attr(e, "ruby"), "text", StringComparison.OrdinalIgnoreCase)).ToList();
            string rubyText = baseSpan?.Value ?? span.Value;
            long? rb = ParseTtmlTime(Attr(textSpans.FirstOrDefault() ?? span, "begin")) ?? ParseTtmlTime(Attr(span, "begin"));
            long? re = ParseTtmlTime(Attr(textSpans.LastOrDefault() ?? span, "end")) ?? ParseTtmlTime(Attr(span, "end"));
            if (rb is not null && rubyText.Length > 0)
            {
                syl.Add(new LyricSyllable(rb.Value, re ?? rb.Value + 200, rubyText));
                anyWord = true;
            }
            main.Append(rubyText);
            return;
        }

        long? sb = ParseTtmlTime(Attr(span, "begin"));
        long? se = ParseTtmlTime(Attr(span, "end"));
        if (sb is null && span.Elements().Any(e => e.Name.LocalName == "span"))
        {
            foreach (var child in span.Nodes())
                AppendTtmlContent(child, main, syl, ref translation, ref romanization, ref anyWord);
            return;
        }

        string spanText = span.Value;
        if (sb is not null && spanText.Length > 0)
        {
            syl.Add(new LyricSyllable(sb.Value, se ?? sb.Value + 200, spanText));
            anyWord = true;
        }
        main.Append(spanText);
    }

    /// <summary>TTML time: <c>hh:mm:ss.mmm</c>, <c>mm:ss.mmm</c>, <c>ss.mmm</c>, or offset form <c>12.5s</c>/<c>900ms</c>.</summary>
    public static long? ParseTtmlTime(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        v = v.Trim();
        if (v.EndsWith("ms", StringComparison.OrdinalIgnoreCase) && double.TryParse(v[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)) return (long)ms;
        if (v.EndsWith("s", StringComparison.OrdinalIgnoreCase) && double.TryParse(v[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) return (long)(s * 1000);
        var parts = v.Split(':');
        try
        {
            double total = 0;
            foreach (var part in parts) total = total * 60 + double.Parse(part, CultureInfo.InvariantCulture);
            return (long)(total * 1000);
        }
        catch { return null; }
    }

    // ── shared shaping ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Fill each line's EndMs (last syllable end → else next line start → else a conservative +4s) so the wipe
    /// has a bound, and drop credit/metadata lines that have NO real word timing (a timed line is never a header).</summary>
    static List<LyricLine> DeriveEnds(List<LyricLine> lines)
    {
        var outLines = new List<LyricLine>(lines.Count);
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (l.Syllables.Count == 0 && IsCreditLine(l.Text)) continue;   // header/credit with no timing → drop
            long sylEnd = l.Syllables.Count > 0 ? l.Syllables[^1].EndMs : 0L;
            long end = l.EndMs ?? (sylEnd > l.StartMs ? sylEnd
                : i + 1 < lines.Count ? lines[i + 1].StartMs
                : l.StartMs + 4000);
            outLines.Add(l with { EndMs = end });
        }
        return outLines;
    }

    static IEnumerable<string> SplitLines(string s)
        => s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    static string CollapseWs(string s) => WsRx().Replace(s, " ").Trim();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex WsRx();

    [GeneratedRegex(@"[\p{P}\p{S}]", RegexOptions.Compiled)]
    private static partial Regex PunctRx();

    /// <summary>Comparison-safe text: lowercase, punctuation stripped, whitespace collapsed. The reranker aligns
    /// candidates on this (NOT the display text), so romanization/punctuation/case differences do not look like mismatches.</summary>
    public static string Normalize(string text)
        => CollapseWs(PunctRx().Replace(text ?? "", " ").ToLowerInvariant());

    // Credit / header detection (Lyricify InfoLines, plus Wavee additions). Matches "Lyrics by …", "作词 : …", etc.
    static readonly string[] CreditMarkers =
    {
        "lyrics by", "lyricist", "composed by", "composer", "produced by", "producer", "arranged by", "arranger",
        "written by", "writer", "mixed by", "mastered by", "vocals by", "performed by", "feat.",
        "作词", "作曲", "編曲", "编曲", "制作", "監製", "监制", "出品", "录音", "混音", "母带",
    };

    /// <summary>True for a credit/metadata line (writer/composer/producer credits, bracketed role tags). Only used to drop
    /// a line that carries NO real word timing — a timed line is treated as a real lyric even if it looks like a credit.</summary>
    public static bool IsCreditLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim();
        // pure bracket tag, e.g. "[Chorus]" alone is a section marker, not a lyric — but keep it (some renderers show it).
        string lower = t.ToLowerInvariant();
        foreach (var m in CreditMarkers)
        {
            int idx = lower.IndexOf(m, StringComparison.Ordinal);
            if (idx < 0) continue;
            // require a separator (": " / "：") near it so "written" inside a real lyric line doesn't trip it.
            if (t.Contains(':') || t.Contains('：') || idx <= 2) return true;
        }
        return false;
    }
}
