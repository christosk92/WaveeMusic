using Wavee.Controls.Lyrics.Models.Lyrics;
using Wavee.UI.Models;

namespace Wavee.UI.Services;

public static class LyricsHoverPreviewBuilder
{
    public static IReadOnlyList<TimelineHoverPreviewItem> Build(LyricsData? lyrics, double durationMs)
    {
        if (lyrics?.LyricsLines is not { Count: > 0 } lines)
            return Array.Empty<TimelineHoverPreviewItem>();

        var duration = ToSafeMilliseconds(durationMs);
        var result = new List<TimelineHoverPreviewItem>(lines.Count);

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var title = line.PrimaryText?.Trim();
            if (string.IsNullOrWhiteSpace(title) || IsDecorativeLine(title))
                continue;

            var startMs = Math.Max(0L, line.StartMs);
            var explicitEndMs = line.EndMs is > 0 ? (long)line.EndMs.Value : 0L;
            var endMs = explicitEndMs;

            if (endMs <= startMs)
                endMs = FindNextStartMs(lines, i + 1, startMs);

            if (endMs <= startMs && duration > startMs)
                endMs = duration;

            if (endMs <= startMs)
                continue;

            result.Add(new TimelineHoverPreviewItem(
                title,
                Subtitle: null,
                StartMilliseconds: startMs,
                StopMilliseconds: endMs));
        }

        return result;
    }

    private static long FindNextStartMs(IReadOnlyList<LyricsLine> lines, int startIndex, long currentStartMs)
    {
        for (var i = startIndex; i < lines.Count; i++)
        {
            var nextStart = Math.Max(0L, lines[i].StartMs);
            if (nextStart > currentStartMs)
                return nextStart;
        }

        return 0;
    }

    private static long ToSafeMilliseconds(double durationMs)
    {
        if (double.IsNaN(durationMs) || double.IsInfinity(durationMs) || durationMs <= 0)
            return 0;

        return (long)Math.Round(durationMs);
    }

    private static bool IsDecorativeLine(string text)
    {
        const string decorativeChars = "♪♫♬♩♭♯·•・";
        return text.All(ch => char.IsWhiteSpace(ch) || decorativeChars.Contains(ch));
    }
}
