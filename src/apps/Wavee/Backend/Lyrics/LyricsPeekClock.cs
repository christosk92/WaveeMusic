using System.Collections.Generic;
using Wavee.Core;

namespace Wavee.Backend.Lyrics;

/// <summary>NPV lyrics-peek clock: which line is active and which one peeks, from line <see cref="LyricLine.StartMs"/>
/// only. Same binary search as the full lyrics view, plus the lead-in / last-line rules the reel needs. Engine-free so
/// Wavee.Tests can drive it without FluentGpu.</summary>
public static class LyricsPeekClock
{
    /// <summary>Matches <c>LyricsView.LeadMs</c> — the reel ticks with the full lyrics view so the same line is rising
    /// as the first syllable lands. Duplicated here because Backend cannot reference the view.</summary>
    public const long LeadMs = 140;

    public static bool ShouldShow(LyricsDocument? doc)
        => doc is { Lines.Count: > 0, Sync: LyricsSyncKind.Line or LyricsSyncKind.Syllable };

    /// <summary>Last line whose <see cref="LyricLine.StartMs"/> is ≤ <paramref name="nowMs"/>, or −1 before the first
    /// line (and on an empty list).</summary>
    public static int ResolveLine(IReadOnlyList<LyricLine> lines, long nowMs)
    {
        if (lines.Count == 0 || nowMs < lines[0].StartMs) return -1;
        int lo = 0, hi = lines.Count - 1, ans = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (lines[mid].StartMs <= nowMs) { ans = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return ans;
    }

    /// <summary>Active + peek indices for the NPV reel. <paramref name="nowMs"/> is the raw playback clock; the lead is
    /// applied here. Before the first line: active −1, peek 0 (faded first line). After the last: hold the last, peek
    /// −1. Hidden documents return (−1, −1).</summary>
    public static (int Active, int Peek) ActiveAndPeek(LyricsDocument? doc, long nowMs)
    {
        if (!ShouldShow(doc)) return (-1, -1);
        var lines = doc!.Lines;
        int active = ResolveLine(lines, nowMs + LeadMs);
        if (active < 0) return (-1, 0);
        int peek = active + 1 < lines.Count ? active + 1 : -1;
        return (active, peek);
    }
}
