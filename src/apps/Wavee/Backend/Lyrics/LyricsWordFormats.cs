using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Wavee.Core;

namespace Wavee.Backend.Lyrics;

/// <summary>Parsers for the CJK word-synced lyric body formats (KRC/YRC/QRC), produced by the grey providers after
/// decrypt. All carry per-syllable timing → <see cref="LyricsSyncKind.Syllable"/>. Pure; reranked + normalized like any
/// other candidate. (Adapted from Lyricify.Lyrics.Helper's parser grammar.)</summary>
public static partial class LyricsWordFormats
{
    // Line header: [lineStartMs,lineDurationMs]
    [GeneratedRegex(@"^\[(\d+),(\d+)\]", RegexOptions.Compiled)]
    private static partial Regex LineHeadRx();

    // KRC word: <offsetMs,durationMs,0>word  — offset RELATIVE to the line start.
    [GeneratedRegex(@"<(\d+),(\d+),-?\d+>([^<]*)", RegexOptions.Compiled)]
    private static partial Regex KrcWordRx();

    /// <summary>Kugou KRC body → word-synced document. Each line <c>[lineStart,lineDur]&lt;wOff,wDur,0&gt;word…</c>; word
    /// times are relative to the line start. <c>[lang:]/[ti:]/…</c> metadata lines are skipped.</summary>
    public static LyricsDocument ParseKrc(string krc, string trackId, string provider = "kugou")
        => BuildDoc(krc, trackId, provider, ParseKrcLine);

    static (long Start, string Text, List<LyricSyllable> Syl)? ParseKrcLine(string line)
    {
        var head = LineHeadRx().Match(line);
        if (!head.Success) return null;
        long lineStart = long.Parse(head.Groups[1].Value);
        string body = line[head.Length..];
        var syl = new List<LyricSyllable>();
        foreach (Match m in KrcWordRx().Matches(body))
        {
            long off = long.Parse(m.Groups[1].Value), dur = long.Parse(m.Groups[2].Value);
            string w = m.Groups[3].Value;
            if (w.Length == 0) continue;
            syl.Add(new LyricSyllable(lineStart + off, lineStart + off + dur, w));
        }
        if (syl.Count == 0) return null;
        return (lineStart, string.Concat(syl.Select(s => s.Text)).Trim(), syl);
    }

    // NetEase YRC word: (startMs,durationMs,0)word — ABSOLUTE times. (Metadata JSON lines start with '{' and are skipped
    // by BuildDoc, which only keeps lines beginning with '['.)
    [GeneratedRegex(@"\((\d+),(\d+),-?\d+\)([^(]*)", RegexOptions.Compiled)]
    private static partial Regex YrcWordRx();

    /// <summary>NetEase YRC body → word-synced document. Lines <c>[lineStart,lineDur](wStart,wDur,0)word…</c>; word times
    /// are ABSOLUTE.</summary>
    public static LyricsDocument ParseYrc(string yrc, string trackId, string provider = "netease")
        => BuildDoc(yrc, trackId, provider, ParseYrcLine);

    static (long Start, string Text, List<LyricSyllable> Syl)? ParseYrcLine(string line)
    {
        var head = LineHeadRx().Match(line);
        if (!head.Success) return null;
        long lineStart = long.Parse(head.Groups[1].Value);
        var syl = new List<LyricSyllable>();
        foreach (Match m in YrcWordRx().Matches(line[head.Length..]))
        {
            long s = long.Parse(m.Groups[1].Value), d = long.Parse(m.Groups[2].Value);
            string w = m.Groups[3].Value;
            if (w.Length == 0) continue;
            syl.Add(new LyricSyllable(s, s + d, w));
        }
        if (syl.Count == 0) return null;
        return (lineStart, string.Concat(syl.Select(s => s.Text)).Trim(), syl);
    }

    // QQ QRC word: word(startMs,durationMs) — timing AFTER the word, ABSOLUTE.
    [GeneratedRegex(@"([^()]*?)\((\d+),(\d+)\)", RegexOptions.Compiled)]
    private static partial Regex QrcWordRx();

    /// <summary>QQ QRC body → word-synced document. The decrypted QRC is usually XML-wrapped
    /// (<c>&lt;Lyric_1 LyricContent="[ls,ld]word(s,d)…"/&gt;</c>); the attribute is extracted, then each line
    /// <c>[lineStart,lineDur]word(wStart,wDur)…</c> is parsed with ABSOLUTE word times.</summary>
    public static LyricsDocument ParseQrc(string qrc, string trackId, string provider = "qq")
        => BuildDoc(ExtractQrcContent(qrc), trackId, provider, ParseQrcLine);

    static string ExtractQrcContent(string qrc)
    {
        int i = qrc.IndexOf("LyricContent=\"", StringComparison.Ordinal);
        if (i < 0) return qrc;
        i += "LyricContent=\"".Length;
        int j = qrc.IndexOf('"', i);
        return j > i ? System.Net.WebUtility.HtmlDecode(qrc[i..j]) : qrc;
    }

    static (long Start, string Text, List<LyricSyllable> Syl)? ParseQrcLine(string line)
    {
        var head = LineHeadRx().Match(line);
        if (!head.Success) return null;
        long lineStart = long.Parse(head.Groups[1].Value);
        var syl = new List<LyricSyllable>();
        foreach (Match m in QrcWordRx().Matches(line[head.Length..]))
        {
            string w = m.Groups[1].Value;
            long s = long.Parse(m.Groups[2].Value), d = long.Parse(m.Groups[3].Value);
            if (w.Length == 0) continue;
            syl.Add(new LyricSyllable(s, s + d, w));
        }
        if (syl.Count == 0) return null;
        return (lineStart, string.Concat(syl.Select(s => s.Text)).Trim(), syl);
    }

    /// <summary>Musixmatch richsync body (a JSON array <c>[{ts,te,l:[{c,o}],x}]</c>): line start/end <c>ts/te</c> and
    /// per-character offsets <c>o</c> are in SECONDS (offset is from the line start). Produces character-level syllables.</summary>
    public static LyricsDocument ParseRichsync(string richsyncJson, string trackId, string provider = "musixmatch")
    {
        var lines = new List<LyricLine>();
        try
        {
            using var d = System.Text.Json.JsonDocument.Parse(richsyncJson);
            if (d.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return Empty(trackId, provider);
            foreach (var e in d.RootElement.EnumerateArray())
            {
                double ts = e.TryGetProperty("ts", out var tsv) ? tsv.GetDouble() : 0;
                double te = e.TryGetProperty("te", out var tev) ? tev.GetDouble() : ts;
                string text = e.TryGetProperty("x", out var xv) && xv.ValueKind == System.Text.Json.JsonValueKind.String ? xv.GetString()! : "";
                var syl = new List<LyricSyllable>();
                if (e.TryGetProperty("l", out var l) && l.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var arr = l.EnumerateArray()
                        .Select(x => (
                            Text: x.TryGetProperty("c", out var cv) && cv.ValueKind == System.Text.Json.JsonValueKind.String ? cv.GetString()! : "",
                            Offset: x.TryGetProperty("o", out var ov) ? ov.GetDouble() : 0d))
                        .ToList();
                    for (int i = 0; i < arr.Count; i++)
                    {
                        var (c, o) = arr[i];
                        if (c.Length == 0 || string.IsNullOrWhiteSpace(c)) continue;

                        string chunk = c;
                        int next = i + 1;
                        while (next < arr.Count && string.IsNullOrWhiteSpace(arr[next].Text))
                        {
                            chunk += arr[next].Text;
                            next++;
                        }

                        double oNext = next < arr.Count ? arr[next].Offset : te - ts;
                        long startMs = (long)((ts + o) * 1000);
                        long endMs = Math.Max(startMs + 1, (long)((ts + Math.Max(o, oNext)) * 1000));
                        syl.Add(new LyricSyllable(startMs, endMs, chunk));
                    }
                }
                lines.Add(new LyricLine((long)(ts * 1000), text.Trim(), syl, (long)(te * 1000), IsWordByWord: syl.Count > 0));
            }
        }
        catch { return Empty(trackId, provider); }
        lines.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
        bool synced = lines.Count > 0;
        bool word = lines.Any(l => l.IsWordByWord);
        return new LyricsDocument(trackId, synced, lines, word ? LyricsSyncKind.Syllable : synced ? LyricsSyncKind.Line : LyricsSyncKind.Unsynced, provider);
    }

    static LyricsDocument Empty(string trackId, string provider)
        => new(trackId, false, Array.Empty<LyricLine>(), LyricsSyncKind.Unsynced, provider);

    // ── shared shaping ───────────────────────────────────────────────────────────────────────────────────────────────

    internal static LyricsDocument BuildDoc(string text, string trackId, string provider,
        Func<string, (long Start, string Text, List<LyricSyllable> Syl)?> parseLine)
    {
        var lines = new List<LyricLine>();
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] != '[') continue;
            var parsed = parseLine(line);
            if (parsed is not { } p) continue;
            long end = p.Syl.Count > 0 ? p.Syl[^1].EndMs : p.Start;
            lines.Add(new LyricLine(p.Start, p.Text, p.Syl, end, IsWordByWord: p.Syl.Count > 0));
        }
        lines.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
        bool synced = lines.Count > 0;
        return new LyricsDocument(trackId, synced, lines, synced ? LyricsSyncKind.Syllable : LyricsSyncKind.Unsynced, provider);
    }
}
