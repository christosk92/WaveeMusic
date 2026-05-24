using System;
using System.Text;

namespace Wavee.UI.WinUI.Services;

internal static class LyricsAiOutputNormalizer
{
    internal static string NormalizeLyricsMeaningOutput(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return string.Empty;

        var lines = s.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length <= 1)
            return s.Trim();

        var allBullets = true;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!TryStripListPrefix(lines[i], out _))
            {
                allBullets = false;
                break;
            }
        }

        if (!allBullets)
            return s.Trim();

        var normalized = string.Empty;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!TryStripListPrefix(lines[i], out var content) || string.IsNullOrWhiteSpace(content))
                continue;

            normalized = string.IsNullOrEmpty(normalized)
                ? content.Trim()
                : normalized + " " + content.Trim();
        }

        return normalized.Trim();
    }

    // Older prompts asked Phi Silica to emit verbatim lyric quotes prefixed
    // "EVIDENCE:" before the actual paragraph. The current prompts are
    // quote-free to avoid moderation false positives on multilingual lyrics,
    // but keep this cleanup for cached or non-compliant model output.
    internal static string StripEvidenceLines(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return string.Empty;

        var lines = s.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.AsSpan().TrimStart().StartsWith("EVIDENCE:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(line);
        }

        return sb.ToString().Trim();
    }

    private static bool TryStripListPrefix(string line, out string content)
    {
        content = line.Trim();
        if (content.Length < 2)
            return false;

        if (content[0] is '-' or '*')
        {
            content = content[1..].TrimStart();
            return content.Length > 0;
        }

        var dotIndex = content.IndexOf('.');
        if (dotIndex is > 0 and <= 2 && int.TryParse(content[..dotIndex], out _))
        {
            content = content[(dotIndex + 1)..].TrimStart();
            return content.Length > 0;
        }

        return false;
    }
}
