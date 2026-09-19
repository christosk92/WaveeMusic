namespace Wavee;

/// <summary>Chapter semantics remain full precision. Pixel coalescing changes only decoration, never lookup/seeks.</summary>
public static class PodcastChapterTimeline
{
    public readonly record struct Interval(string Title, int StartMs, int EndMs);
    public static Interval[] Normalize(IReadOnlyList<Spotify.Podcasts.Chapter> source, int durationMs)
    {
        if (durationMs <= 0 || source.Count == 0) return [];
        var sorted = source.Where(x => x.StartMs >= 0 && x.StartMs < durationMs)
            .OrderBy(x => x.StartMs).GroupBy(x => x.StartMs)
            .Select(g => g.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Title)) ?? g.First()).ToArray();
        var result = new Interval[sorted.Length];
        for (int i = 0; i < sorted.Length; i++)
        {
            var chapter = sorted[i];
            int limit = i + 1 < sorted.Length ? sorted[i + 1].StartMs : durationMs;
            int end = chapter.EndMs > chapter.StartMs ? Math.Min(chapter.EndMs, limit) : limit;
            result[i] = new(chapter.Title, chapter.StartMs, end);
        }
        return result;
    }

    public static int At(ReadOnlySpan<Interval> chapters, int positionMs)
    {
        int low = 0, high = chapters.Length - 1, found = -1;
        while (low <= high)
        {
            int mid = low + (high - low) / 2;
            if (chapters[mid].StartMs <= positionMs) { found = mid; low = mid + 1; }
            else high = mid - 1;
        }
        return found >= 0 && (positionMs < chapters[found].EndMs
            || found == chapters.Length - 1 && positionMs == chapters[found].EndMs) ? found : -1;
    }

    public static int HoverTime(float fraction, int durationMs)
        => durationMs <= 0 || !float.IsFinite(fraction) ? 0 : (int)Math.Round(Math.Clamp((double)fraction, 0, 1) * durationMs);

    public static float[] MarkerFractions(ReadOnlySpan<Interval> chapters, int durationMs, float width, float minimumGap = 4)
    {
        if (durationMs <= 0 || !float.IsFinite(width) || width <= 0) return [];
        var result = new List<float>();
        float previous = 0;
        foreach (var chapter in chapters)
        {
            float fraction = chapter.StartMs / (float)durationMs, x = fraction * width;
            if (chapter.StartMs <= 0 || chapter.StartMs >= durationMs || x - previous < minimumGap || width - x < minimumGap) continue;
            result.Add(fraction); previous = x;
        }
        return result.ToArray();
    }
}
