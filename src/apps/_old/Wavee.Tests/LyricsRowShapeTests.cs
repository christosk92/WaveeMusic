using System.Collections.Generic;
using Wavee;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// The pure rule behind "may a lyrics document upgrade keep the mounted rows?". The case that matters is the one the
/// 2026-09-10 log captured verbatim: Spotify's LINE-synced transcription wins first, Kugou's WORD-synced document arrives
/// a second later with byte-identical text on every line (<c>text=1.00 lcs=69/69</c>). Rows freeze their line at mount,
/// so keeping them meant the whole track played without a karaoke wipe.
/// </summary>
public class LyricsRowShapeTests
{
    static LyricLine Line(long start, string text, params (long s, long e, string t)[] syl)
    {
        var list = new List<LyricSyllable>(syl.Length);
        foreach (var (s, e, t) in syl) list.Add(new LyricSyllable(s, e, t));
        return new LyricLine(start, text, list, EndMs: syl.Length > 0 ? syl[^1].e : null, IsWordByWord: syl.Length > 0);
    }

    static LyricsDocument Doc(string track, LyricsSyncKind sync, params LyricLine[] lines)
        => new(track, true, lines, sync);

    static readonly LyricsDocument LineSynced = Doc("t1", LyricsSyncKind.Line,
        Line(631, "Lately I've been I've been losing sleep"),
        Line(5340, "Dreaming about the things that we could be"));

    static readonly LyricsDocument WordSynced = Doc("t1", LyricsSyncKind.Syllable,
        Line(631, "Lately I've been I've been losing sleep", (631, 1703, "Lately "), (1703, 4292, "I've been I've been losing sleep")),
        Line(5340, "Dreaming about the things that we could be", (5340, 5982, "Dreaming "), (5982, 8832, "about the things that we could be")));

    [Fact]
    public void LineSynced_To_WordSynced_WithIdenticalText_RebuildsTheRows()
        => Assert.False(LyricsRowShape.SameRows(LineSynced, WordSynced));

    [Fact]
    public void IdenticalDocuments_KeepTheRows()
    {
        Assert.True(LyricsRowShape.SameRows(WordSynced, WordSynced));
        var copy = WordSynced with { Provider = "another-provider", OffsetMsApplied = -159 };
        Assert.True(LyricsRowShape.SameRows(WordSynced, copy));   // provider / offset are not row state
    }

    [Fact]
    public void LineStartOrEnd_Alone_KeepsTheRows()
    {
        // Timings the VIEW reads live from the document (starts, ends) are not frozen into a row.
        var lines = new List<LyricLine>();
        foreach (var l in WordSynced.Lines) lines.Add(l with { StartMs = l.StartMs - 159, EndMs = (l.EndMs ?? 0) - 159 });
        Assert.True(LyricsRowShape.SameRows(WordSynced, WordSynced with { Lines = lines }));
    }

    [Fact]
    public void RicherSyllableSplit_RebuildsTheRows()
    {
        var lines = new List<LyricLine>(WordSynced.Lines);
        lines[0] = Line(631, "Lately I've been I've been losing sleep",
            (631, 1703, "Lately "), (1703, 2236, "I've "), (2236, 4292, "been I've been losing sleep"));
        Assert.False(LyricsRowShape.SameRows(WordSynced, WordSynced with { Lines = lines }));
    }

    [Fact]
    public void SecondaryLayer_Appearing_RebuildsTheRows()
    {
        var lines = new List<LyricLine>(WordSynced.Lines);
        lines[1] = lines[1] with { Translation = "Dromen over de dingen die we zouden kunnen zijn" };
        Assert.False(LyricsRowShape.SameRows(WordSynced, WordSynced with { Lines = lines }));
        lines[1] = WordSynced.Lines[1] with { Romanization = "x" };
        Assert.False(LyricsRowShape.SameRows(WordSynced, WordSynced with { Lines = lines }));
    }

    [Fact]
    public void DifferentTrack_LineCount_OrText_RebuildsTheRows()
    {
        Assert.False(LyricsRowShape.SameRows(WordSynced, WordSynced with { TrackId = "t2" }));
        Assert.False(LyricsRowShape.SameRows(WordSynced, WordSynced with { Lines = new[] { WordSynced.Lines[0] } }));
        var lines = new List<LyricLine>(WordSynced.Lines);
        lines[0] = lines[0] with { Text = "Lately I have been losing sleep" };
        Assert.False(LyricsRowShape.SameRows(WordSynced, WordSynced with { Lines = lines }));
    }
}
