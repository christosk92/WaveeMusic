using System.Text.RegularExpressions;

namespace Wavee.Core.Http.Lyrics;

/// <summary>
/// Word-level timing entry parsed from enhanced LRC tags.
/// </summary>
public sealed record LrcWordTiming(long StartMs, long EndMs, string Text);

/// <summary>
/// Result of parsing an LRC lyrics string.
/// </summary>
public sealed class LrcParseResult
{
    public List<LyricsLine> Lines { get; init; } = [];

    /// <summary>
    /// Word timings per line index. Only populated for lines with enhanced LRC word tags.
    /// </summary>
    public Dictionary<int, List<LrcWordTiming>> WordTimings { get; init; } = new();

    public bool HasWordTiming => WordTimings.Count > 0;
}

/// <summary>
/// Parses LRC and enhanced LRC format lyrics into <see cref="LyricsLine"/> objects
/// with optional word-level timing.
/// </summary>
public static partial class LrcParser
{
    // [mm:ss.xx] or [mm:ss:xx] — line timestamp
    [GeneratedRegex(@"^\[(\d+):(\d+)[.:](\d+)\](.*)$")]
    private static partial Regex LineTimestampRegex();

    // <mm:ss.xx> or <mm:ss:xx> — inline word timestamp
    [GeneratedRegex(@"<(\d+):(\d+)[.:](\d+)>([^<]*)")]
    private static partial Regex WordTimestampRegex();

    public static LrcParseResult Parse(string lrcText)
    {
        var result = new LrcParseResult();
        if (string.IsNullOrWhiteSpace(lrcText))
            return result;

        var rawLines = lrcText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var parsedLines = new List<(long StartMs, string Words, List<LrcWordTiming>? WordTimings)>();

        foreach (var rawLine in rawLines)
        {
            var trimmed = rawLine.Trim();
            var match = LineTimestampRegex().Match(trimmed);
            if (!match.Success) continue;

            var lineStartMs = ParseTimestamp(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value);
            var content = match.Groups[4].Value;

            // Try to parse inline word tags
            var wordMatches = WordTimestampRegex().Matches(content);
            if (wordMatches.Count > 0)
            {
                var wordTimings = new List<LrcWordTiming>(wordMatches.Count);
                for (var i = 0; i < wordMatches.Count; i++)
                {
                    var wm = wordMatches[i];
                    var wordStartMs = ParseTimestamp(wm.Groups[1].Value, wm.Groups[2].Value, wm.Groups[3].Value);
                    var text = wm.Groups[4].Value;

                    // End time is the start of the next word, or will be set later
                    var wordEndMs = i + 1 < wordMatches.Count
                        ? ParseTimestamp(wordMatches[i + 1].Groups[1].Value,
                                        wordMatches[i + 1].Groups[2].Value,
                                        wordMatches[i + 1].Groups[3].Value)
                        : wordStartMs + 500; // fallback for last word

                    if (!string.IsNullOrEmpty(text))
                        wordTimings.Add(new LrcWordTiming(wordStartMs, wordEndMs, text));
                }

                var fullText = string.Concat(wordTimings.Select(w => w.Text));
                parsedLines.Add((lineStartMs, fullText, wordTimings.Count > 0 ? wordTimings : null));
            }
            else
            {
                // Standard LRC — line-level only
                parsedLines.Add((lineStartMs, content.Trim(), null));
            }
        }

        // Sort by start time
        parsedLines.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));

        // Build LyricsLine objects
        for (var i = 0; i < parsedLines.Count; i++)
        {
            var (startMs, words, wordTimings) = parsedLines[i];

            // Fix last word end time: use next line start
            if (wordTimings is { Count: > 0 } && i + 1 < parsedLines.Count)
            {
                var lastWord = wordTimings[^1];
                if (lastWord.EndMs <= lastWord.StartMs + 500)
                {
                    wordTimings[^1] = lastWord with { EndMs = parsedLines[i + 1].StartMs };
                }
            }

            var line = new LyricsLine
            {
                StartTimeMs = startMs.ToString(),
                EndTimeMs = (i + 1 < parsedLines.Count ? parsedLines[i + 1].StartMs : 0).ToString(),
                Words = words
            };
            result.Lines.Add(line);

            if (wordTimings != null)
                result.WordTimings[i] = wordTimings;
        }

        return result;
    }

    private static long ParseTimestamp(string minutes, string seconds, string fraction)
    {
        var min = long.TryParse(minutes, out var m) ? m : 0;
        var sec = long.TryParse(seconds, out var s) ? s : 0;
        var frac = long.TryParse(fraction, out var f) ? f : 0;

        // Normalize fraction to milliseconds (could be 2 or 3 digits)
        if (fraction.Length == 2) frac *= 10;
        else if (fraction.Length == 1) frac *= 100;

        return min * 60_000 + sec * 1000 + frac;
    }
}
