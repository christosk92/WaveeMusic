// ── Wavee.Tests/AiLyricsAlignTests.cs — the forced alignment's graph, Viterbi, fill and line assembly ─────────────────
//
// Synthetic log-probabilities stand in for the model: every 20 ms frame has one class at log 1 (0) and every other class
// at a small penalty, so the cheapest path is decided by how many frames it must spend on a class that is not there.
// Pure: no NPU, no audio.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class AiLyricsAlignTests
{
    const int Blank = 0, Sep = 1, A = 2, B = 3, O = 4, H = 5, Classes = 6;

    static AiLyrics.Align.Vocab Vocab() => new(new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["<pad>"] = Blank, ["|"] = Sep, ["a"] = A, ["b"] = B, ["o"] = O, ["h"] = H,
    });

    /// <summary>One row per frame: the named class at 0, the rest at -0.8. At that penalty emitting "oh" over frames
    /// that do not carry it (three stolen frames: o, h and the extra "|") costs 2.4: more than the skip cost (2.0), but
    /// less than the skip plus a second separator (2.8) that the skip edge used to need.</summary>
    static float[] Frames(params int[] classes)
    {
        var logp = new float[classes.Length * Classes];
        Array.Fill(logp, -0.8f);
        for (int f = 0; f < classes.Length; f++) logp[f * Classes + classes[f]] = 0f;
        return logp;
    }

    static (AiLyrics.Align.Graph G, AiLyrics.Align.OnlineViterbi V) Line(string text)
    {
        var g = AiLyrics.Align.Graph.Build([AiLyrics.Align.Words(text)], Vocab());
        return (g, new AiLyrics.Align.OnlineViterbi(g, Blank));
    }

    [Fact]
    public void The_skip_edge_starts_before_the_optional_words_own_separator()
    {
        // tokens: a b | o h | b a  (the optional word's letters are tokens 3 and 4, its separator token 2)
        var (g, _) = Line("ab (oh) ba");
        Assert.Equal([A, B, Sep, O, H, Sep, B, A], g.Tokens);
        Assert.Equal([2 * 2], g.SkipSrc);
        Assert.Equal([2 * 5], g.SkipDst);
    }

    [Fact]
    public void A_first_optional_word_skips_from_the_first_blank()
    {
        var (g, _) = Line("(oh) ab");
        Assert.Equal([0], g.SkipSrc);
        Assert.Equal([2 * 2], g.SkipDst);
    }

    [Fact]
    public void An_optional_word_whose_letters_are_absent_is_skipped_at_the_cost_of_one_separator()
    {
        var (_, v) = Line("ab (oh) ba");
        var frames = Frames(Blank, A, A, Blank, B, B, Blank, Sep, Blank, B, B, Blank, A, A, Blank);
        v.Feed(frames, Classes);
        v.Commit(0, final: true);
        Assert.Null(v.Span(3));
        Assert.Null(v.Span(4));
        Assert.NotNull(v.Span(0));
        Assert.NotNull(v.Span(7));
    }

    [Fact]
    public void An_optional_word_whose_letters_are_present_is_kept()
    {
        var (_, v) = Line("ab (oh) ba");
        var frames = Frames(Blank, A, A, B, B, Sep, O, O, H, H, Sep, B, B, A, A, Blank);
        v.Feed(frames, Classes);
        v.Commit(0, final: true);
        Assert.Equal((6, 7), v.Span(3));
        Assert.Equal((8, 9), v.Span(4));
    }

    [Fact]
    public void The_skip_survives_an_intermediate_commit_of_the_word_before()
    {
        var (_, v) = Line("ab (oh) ba");
        v.Feed(Frames(Blank, A, A, Blank, B, B, Blank, Sep, Blank), Classes);
        v.Commit(0);
        Assert.True(v.Committed >= 2);                                          // "ab" is decided
        v.Feed(Frames(B, B, Blank, A, A, Blank), Classes);
        v.Commit(0, final: true);
        Assert.Null(v.Span(3));
        Assert.Null(v.Span(4));
        Assert.NotNull(v.Span(6));
    }

    // ── Fill ────────────────────────────────────────────────────────────────────────────────────────────────────

    static AiLyrics.Align.WordTime W(double s, double e) => new(s, e);
    static readonly AiLyrics.Align.WordTime None = AiLyrics.Align.WordTime.None;

    [Fact]
    public void A_leading_untimed_run_never_starts_before_the_line()
    {
        var words = new[] { None, None, W(2.0, 2.5) };
        Assert.True(AiLyrics.Align.Fill(words, lineStart: 1.8, nextStart: null));
        Assert.Equal(1.8, words[0].Start, 6);
        Assert.Equal(2.0, words[1].End, 6);
        Assert.True(words[0].End <= words[1].Start + 1e-9);
    }

    [Fact]
    public void A_leading_untimed_run_takes_a_natural_pace_when_the_line_started_long_before()
    {
        var words = new[] { None, None, W(2.0, 2.5) };
        AiLyrics.Align.Fill(words, lineStart: 0.0, nextStart: null);
        Assert.Equal(1.4, words[0].Start, 6);                                     // 0.3 s per word back from the first timed one
        Assert.Equal(1.7, words[1].Start, 6);
    }

    [Fact]
    public void A_line_start_after_the_first_timed_word_collapses_the_run_onto_it()
    {
        var words = new[] { None, W(2.0, 2.5) };
        AiLyrics.Align.Fill(words, lineStart: 2.2, nextStart: null);
        Assert.Equal(2.0, words[0].Start, 6);
        Assert.Equal(2.0, words[0].End, 6);
    }

    [Fact]
    public void A_line_with_nothing_timed_starts_at_the_line_and_stops_at_the_next()
    {
        var words = new[] { None, None, None };
        AiLyrics.Align.Fill(words, lineStart: 5.0, nextStart: 5.6);
        Assert.Equal(5.0, words[0].Start, 6);
        Assert.Equal(5.6, words[2].End, 6);
    }

    [Fact]
    public void A_line_without_words_is_not_filled() => Assert.False(AiLyrics.Align.Fill([], 0, null));

    // ── WordSyncedLine ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void No_word_starts_before_the_previous_line()
    {
        var src = new Lyrics.Line(1200, "hello there", []);
        var words = AiLyrics.Align.Words(src.Text);
        var line = AiLyrics.Align.WordSyncedLine(src, words, [W(0.9, 1.3), W(1.3, 1.8)], null, prevStartMs: 1000);
        Assert.Equal(1000, line.StartMs);
        Assert.Equal(1000, line.Syllables[0].StartMs);
        Assert.Equal(1300, line.Syllables[1].StartMs);
        Assert.Equal(src.Text, string.Concat(line.Syllables.Select(s => s.Text)));
    }

    [Fact]
    public void A_word_pushed_past_its_end_by_the_clamp_keeps_a_non_negative_length()
    {
        var src = new Lyrics.Line(0, "hi you", []);
        var line = AiLyrics.Align.WordSyncedLine(src, AiLyrics.Align.Words(src.Text), [W(0.5, 0.6), W(0.6, 0.9)], null, prevStartMs: 800);
        Assert.All(line.Syllables, s => Assert.True(s.EndMs >= s.StartMs));
        Assert.All(line.Syllables, s => Assert.True(s.StartMs >= 800));
    }

    [Fact]
    public void Without_a_previous_line_the_times_are_kept_and_the_last_word_stops_at_the_next_line()
    {
        var src = new Lyrics.Line(0, "hello there", []);
        var line = AiLyrics.Align.WordSyncedLine(src, AiLyrics.Align.Words(src.Text), [W(0.9, 1.3), W(1.3, 2.5)], nextStartS: 2.0);
        Assert.Equal(900, line.StartMs);
        Assert.Equal(2000, line.Syllables[1].EndMs);
        Assert.True(line.IsWordByWord);
    }
}
