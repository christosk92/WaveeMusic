using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Lyricify.Lyrics.Models;
using ControlsBaseLyrics = Wavee.Controls.Lyrics.Models.Lyrics.BaseLyrics;
using ControlsLyricsData = Wavee.Controls.Lyrics.Models.Lyrics.LyricsData;
using ControlsLyricsLine = Wavee.Controls.Lyrics.Models.Lyrics.LyricsLine;

namespace Wavee.UI.WinUI.Helpers.Lyrics;

/// <summary>
/// Converts raw lyrics strings (QRC, KRC, LRC, ESLRC, TTML) into the
/// <see cref="ControlsLyricsData"/> model used by the NowPlayingCanvas control.
/// Ported from BetterLyrics' LyricsContentParser.
/// </summary>
internal static class LyricsContentParser
{
    private static readonly XNamespace TtmlMeta = "http://www.w3.org/ns/ttml#metadata";

    internal enum LyricsFormat { Lrc, Eslrc, Qrc, Krc, Ttml }

    public static ControlsLyricsData? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var format = DetectFormat(raw);
        if (format is null)
            return null;

        var result = format switch
        {
            LyricsFormat.Qrc => ParseQrcKrc(Lyricify.Lyrics.Parsers.QrcParser.Parse(raw).Lines),
            LyricsFormat.Krc => ParseQrcKrc(Lyricify.Lyrics.Parsers.KrcParser.ParseLyrics(raw)),
            LyricsFormat.Ttml => ParseTtml(raw),
            LyricsFormat.Lrc or LyricsFormat.Eslrc => ParseLrc(raw),
            _ => null,
        };

        if (result != null)
            EnsureEndMs(result);

        return result;
    }

    // ── Format detection ──

    internal static LyricsFormat? DetectFormat(string raw)
    {
        if (Regex.IsMatch(raw, @"<tt\b[^>]*\bxmlns\s*=\s*[""']http://www\.w3\.org/ns/ttml[""']",
                RegexOptions.IgnoreCase))
            return LyricsFormat.Ttml;

        if (Regex.IsMatch(raw, @"^\[\d+,\d+\](<\d+,\d+,0>.+)+", RegexOptions.Multiline))
            return LyricsFormat.Krc;

        if (Regex.IsMatch(raw, @"^\[\d+,\d+\].*?\(\d+,\d+\)", RegexOptions.Multiline))
            return LyricsFormat.Qrc;

        if (Regex.IsMatch(raw, @"\[\d{1,2}:\d{2}") ||
            Regex.IsMatch(raw, @"<\d{1,2}:\d{2}\.\d{2,3}>"))
            return LyricsFormat.Lrc;

        return null;
    }

    // ── QRC / KRC ──

    private static ControlsLyricsData? ParseQrcKrc(List<ILineInfo>? lines)
    {
        lines = lines?.Where(x => x.Text != string.Empty).ToList();
        if (lines is not { Count: > 0 })
            return null;

        var lyricsLines = new List<ControlsLyricsLine>(lines.Count);

        foreach (var lineRead in lines)
        {
            var lineWrite = new ControlsLyricsLine
            {
                StartMs = lineRead.StartTime ?? 0,
                PrimaryText = lineRead.Text,
                IsPrimaryHasRealSyllableInfo = true,
            };

            var syllables = (lineRead as SyllableLineInfo)?.Syllables;
            if (syllables != null)
            {
                int startIndex = 0;
                foreach (var syllable in syllables)
                {
                    lineWrite.PrimarySyllables.Add(new ControlsBaseLyrics
                    {
                        StartMs = syllable.StartTime,
                        EndMs = syllable.EndTime,
                        Text = syllable.Text,
                        StartIndex = startIndex,
                    });
                    startIndex += syllable.Text.Length;
                }
            }

            lyricsLines.Add(lineWrite);
        }

        return new ControlsLyricsData { LyricsLines = lyricsLines };
    }

    // ── LRC / ESLRC ──

    private static readonly Regex LrcTimestampRegex = new(@"\[(\d*):(\d*)(\.|\:)(\d*)\]");
    private static readonly Regex SyllableTimingRegex = new(@"(\[|\<)(\d*):(\d*)\.(\d*)(\]|\>)([^\[\]\<\>]*)");

    private static ControlsLyricsData? ParseLrc(string raw)
    {
        var lines = raw.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        var lrcLines = new List<ControlsLyricsLine>();

        foreach (var line in lines)
        {
            var matches = SyllableTimingRegex.Matches(line);
            var syllables = new List<ControlsBaseLyrics>();

            int startIndex = 0;
            foreach (Match match in matches)
            {
                int min = int.Parse(match.Groups[2].Value);
                int sec = int.Parse(match.Groups[3].Value);
                int ms = int.Parse(match.Groups[4].Value.PadRight(3, '0'));
                int totalMs = min * 60_000 + sec * 1000 + ms;
                string text = match.Groups[6].Value;

                syllables.Add(new ControlsBaseLyrics
                {
                    StartMs = totalMs,
                    Text = text,
                    StartIndex = startIndex,
                });
                startIndex += text.Length;
            }

            if (syllables.Count > 1)
            {
                // Enhanced LRC with syllable timing
                lrcLines.Add(new ControlsLyricsLine
                {
                    StartMs = syllables[0].StartMs,
                    PrimaryText = string.Concat(syllables.Select(s => s.Text)),
                    PrimarySyllables = syllables,
                    IsPrimaryHasRealSyllableInfo = true,
                });
            }
            else
            {
                // Standard LRC line
                var bracketMatches = LrcTimestampRegex.Matches(line);
                if (bracketMatches.Count > 0)
                {
                    var match = bracketMatches[0];
                    int min = int.Parse(match.Groups[1].Value);
                    int sec = int.Parse(match.Groups[2].Value);
                    int ms = int.Parse(match.Groups[4].Value.PadRight(3, '0'));
                    int lineStartMs = min * 60_000 + sec * 1000 + ms;

                    string content = LrcTimestampRegex.Replace(line, "").Trim();
                    if (content == "//") content = "";

                    lrcLines.Add(new ControlsLyricsLine
                    {
                        StartMs = lineStartMs,
                        PrimaryText = content,
                        IsPrimaryHasRealSyllableInfo = false,
                    });
                }
            }
        }

        if (lrcLines.Count == 0)
            return null;

        // Group by start time and take the first language track only
        var grouped = lrcLines.GroupBy(l => l.StartMs).OrderBy(g => g.Key).ToList();
        var primaryLines = grouped.Select(g => g.First()).ToList();

        return new ControlsLyricsData { LyricsLines = primaryLines };
    }

    // ── TTML ──

    private static ControlsLyricsData? ParseTtml(string raw)
    {
        try
        {
            var originalLines = new List<ControlsLyricsLine>();
            var xdoc = XDocument.Parse(raw, LoadOptions.PreserveWhitespace);
            var body = xdoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "body");
            if (body == null) return null;

            var ps = body.Descendants().Where(e => e.Name.LocalName == "p");

            foreach (var p in ps)
            {
                ParseTtmlSegment(p, originalLines, isBackground: false);

                var bgSpans = p.Elements()
                    .Where(s => s.Attribute(TtmlMeta + "role")?.Value == "x-bg");

                foreach (var bgSpan in bgSpans)
                    ParseTtmlSegment(bgSpan, originalLines, isBackground: true);
            }

            return originalLines.Count > 0
                ? new ControlsLyricsData { LyricsLines = originalLines }
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void ParseTtmlSegment(
        XElement container, List<ControlsLyricsLine> dest, bool isBackground)
    {
        int containerStartMs = ParseTtmlTime(container.Attribute("begin")?.Value);
        int containerEndMs = ParseTtmlTime(container.Attribute("end")?.Value);

        var contentSpans = container.Elements()
            .Where(s => s.Name.LocalName == "span")
            .Where(s => s.Attribute(TtmlMeta + "role")?.Value == null)
            .ToList();

        // Merge trailing text nodes into their preceding span
        for (int i = 0; i < contentSpans.Count; i++)
        {
            var span = contentSpans[i];
            var nextNode = span.NodesAfterSelf().FirstOrDefault();
            if (nextNode is XText textNode)
                span.Value += textNode.Value;
        }

        var syllables = new List<ControlsBaseLyrics>();
        int startIndex = 0;
        var sbText = new StringBuilder();

        foreach (var span in contentSpans)
        {
            int sStartMs = ParseTtmlTime(span.Attribute("begin")?.Value);
            int sEndMs = ParseTtmlTime(span.Attribute("end")?.Value);
            string text = span.Value;

            syllables.Add(new ControlsBaseLyrics
            {
                StartMs = sStartMs,
                EndMs = sEndMs,
                StartIndex = startIndex,
                Text = text,
            });

            sbText.Append(text);
            startIndex += text.Length;
        }

        string fullText = contentSpans.Count == 0 ? container.Value : sbText.ToString();

        dest.Add(new ControlsLyricsLine
        {
            StartMs = containerStartMs,
            EndMs = containerEndMs,
            PrimaryText = fullText,
            PrimarySyllables = syllables,
            IsPrimaryHasRealSyllableInfo = syllables.Count > 0,
        });
    }

    private static int ParseTtmlTime(string? t)
    {
        if (string.IsNullOrWhiteSpace(t))
            return 0;

        t = t.Trim();

        if (t.EndsWith('s'))
        {
            if (double.TryParse(t.TrimEnd('s'),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double seconds))
                return (int)(seconds * 1000);
        }
        else
        {
            var parts = t.Split(':');
            if (parts.Length == 3)
            {
                int h = int.Parse(parts[0]);
                int m = int.Parse(parts[1]);
                double s = double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
                return (int)((h * 3600 + m * 60 + s) * 1000);
            }
            else if (parts.Length == 2)
            {
                int m = int.Parse(parts[0]);
                double s = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                return (int)((m * 60 + s) * 1000);
            }
            else if (parts.Length == 1)
            {
                if (double.TryParse(parts[0],
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double s))
                    return (int)(s * 1000);
            }
        }

        return 0;
    }

    // ── Post-processing ──

    private static void EnsureEndMs(ControlsLyricsData data)
    {
        for (int i = 0; i < data.LyricsLines.Count; i++)
        {
            var line = data.LyricsLines[i];

            // Fill line EndMs from next line's start
            if (line.EndMs is null or 0 && i + 1 < data.LyricsLines.Count)
                line.EndMs = data.LyricsLines[i + 1].StartMs;

            // Fill syllable EndMs from next syllable's start
            for (int j = 0; j < line.PrimarySyllables.Count; j++)
            {
                var syl = line.PrimarySyllables[j];
                if (syl.EndMs is null or 0)
                {
                    syl.EndMs = j + 1 < line.PrimarySyllables.Count
                        ? line.PrimarySyllables[j + 1].StartMs
                        : line.EndMs;
                }
            }

            // Ensure single-syllable fallback for lines without syllable data
            if (line.PrimarySyllables.Count == 0)
            {
                line.PrimarySyllables.Add(new ControlsBaseLyrics
                {
                    StartMs = line.StartMs,
                    EndMs = line.EndMs,
                    Text = line.PrimaryText,
                    StartIndex = 0,
                });
            }
        }
    }
}
