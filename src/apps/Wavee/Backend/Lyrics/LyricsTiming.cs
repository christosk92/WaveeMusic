using System;
using System.Collections.Generic;
using Wavee.Core;

namespace Wavee.Backend.Lyrics;

/// <summary>The ONE owner of "are these word timings physically possible?".
///
/// <para>A word-synced body can be internally consistent — monotonic, every syllable inside its line, no negative
/// durations — and still be garbage: the words all fire in the first fraction of a second and the line then sits dead
/// until the next one. Nothing else in the pipeline notices. The reranker only aligns LINE STARTS against the reference,
/// so such a candidate scores as a perfect match and wins the sync tier on word timing it does not actually deliver;
/// the view then wipes the whole line in 200ms and shows interlude dots for the remaining eight seconds.</para>
///
/// <para>The test is deliberately PHYSICAL rather than comparative: words per second. Ordinary singing is 1–3 wps and the
/// fastest recorded rap is around 9, so a line over <see cref="ImpossibleWordsPerSecond"/> did not get its real duration
/// — no reference document, no per-provider knowledge and no threshold tuning required. A "the line ends early relative
/// to the gap" test was considered and rejected: an instrumental break makes every legitimate line look squashed.</para>
///
/// <para>Both the reranker gate and the lyrics-source inspector read this class, so the rule has exactly one definition.</para>
/// </summary>
public static class LyricsTiming
{
    /// <summary>Above this, a sung line is not physically possible. Ordinary singing is 1–3 wps; the fastest recorded rap
    /// is ~9 over a short burst, and no full lyric line sustains it.</summary>
    public const double ImpossibleWordsPerSecond = 8.0;

    /// <summary>Short lines ("Ooh, ah", "Caribbean Queen") are excluded — a two-word line legitimately lands inside a few
    /// hundred milliseconds, so its rate says nothing.</summary>
    const int MinWordsToJudge = 3;

    /// <summary>A document needs this many judgeable lines before the verdict means anything — a three-line fragment
    /// must not be able to condemn a provider.</summary>
    const int MinJudgeableLines = 5;

    public static int WordCount(string text)
    {
        int n = 0;
        bool inWord = false;
        foreach (char c in text)
        {
            bool space = char.IsWhiteSpace(c);
            if (!space && !inWord) n++;
            inWord = !space;
        }
        return n;
    }

    /// <summary>How long the WORD timing claims the line takes. Measured across the syllables when there are any (that is
    /// the timing under test), falling back to the line's own end.</summary>
    public static long WordSpanMs(LyricLine line)
    {
        if (line.Syllables.Count > 0)
        {
            long first = line.Syllables[0].StartMs, last = line.Syllables[^1].EndMs;
            if (last > first) return last - first;
        }
        return line.EndMs is { } e && e > line.StartMs ? e - line.StartMs : 0L;
    }

    public static bool IsImpossiblyFast(LyricLine line)
    {
        long span = WordSpanMs(line);
        if (span <= 0) return false;
        int words = WordCount(line.Text);
        return words >= MinWordsToJudge && words * 1000d / span > ImpossibleWordsPerSecond;
    }

    /// <summary>True when a word-synced document's timings are not singable: at least half of its judgeable lines are
    /// impossibly fast. Half (not all) because a handful of legitimately clipped lines is normal, while a body whose
    /// offsets are systematically compressed fails on nearly every line.</summary>
    public static bool HasImplausibleWordTiming(LyricsDocument doc, out int impossible, out int judged)
    {
        impossible = 0;
        judged = 0;
        if (doc.Sync != LyricsSyncKind.Syllable) return false;

        foreach (var l in doc.Lines)
        {
            if (WordSpanMs(l) <= 0 || WordCount(l.Text) < MinWordsToJudge) continue;
            judged++;
            if (IsImpossiblyFast(l)) impossible++;
        }
        return judged >= MinJudgeableLines && impossible * 2 >= judged;
    }

    /// <summary>A line is "squashed" when it ENDS inside a quarter of the time until the next line begins. Reported, but
    /// deliberately NOT part of <see cref="HasImplausibleWordTiming"/>: an instrumental break makes every legitimate line
    /// look squashed, so it is a symptom worth showing a human, not a rule worth gating on.</summary>
    const long SquashDenominator = 4;
    const long SquashMinGapMs = 1500;

    public static bool IsSquashed(LyricsDocument doc, int i)
    {
        if (i + 1 >= doc.Lines.Count) return false;
        var l = doc.Lines[i];
        long dur = (l.EndMs ?? l.StartMs) - l.StartMs;
        if (dur <= 0) return false;
        long gap = doc.Lines[i + 1].StartMs - l.StartMs;
        return gap >= SquashMinGapMs && dur * SquashDenominator < gap;
    }

    /// <summary>One human-readable verdict on a document's timings — the string the inspector shows and every saved
    /// bundle records. Two families: STRUCTURAL (timestamps that go backwards, never got set, or end before they start;
    /// syllables outside their own line) and PLAUSIBILITY (timings that are internally consistent but far too compressed
    /// to be sung). The second family is what catches a scale mistake in a body parser.</summary>
    public static string Describe(LyricsDocument doc)
    {
        if (doc.Sync == LyricsSyncKind.Unsynced)
            return "unsynced document — it carries no timings at all, so the UI cannot follow it";

        int outOfOrder = 0, noStamp = 0, badEnd = 0, syllOutside = 0, syllOutOfOrder = 0, squashed = 0, tooFast = 0;
        long prev = long.MinValue;

        for (int i = 0; i < doc.Lines.Count; i++)
        {
            var l = doc.Lines[i];
            if (prev != long.MinValue && l.StartMs < prev) outOfOrder++;
            prev = l.StartMs;
            if (i > 0 && l.StartMs == 0) noStamp++;
            if (l.EndMs is { } e && e < l.StartMs) badEnd++;

            long ps = long.MinValue;
            foreach (var s in l.Syllables)
            {
                if (ps != long.MinValue && s.StartMs < ps) syllOutOfOrder++;
                ps = s.StartMs;
                if (s.EndMs < s.StartMs || s.StartMs < l.StartMs || (l.EndMs is { } le && s.EndMs > le)) syllOutside++;
            }

            if (IsSquashed(doc, i)) squashed++;
            if (IsImpossiblyFast(l)) tooFast++;
        }

        var parts = new List<string>(8);
        if (outOfOrder > 0) parts.Add($"{outOfOrder} line(s) start BEFORE the line above them");
        if (noStamp > 0) parts.Add($"{noStamp} line(s) after the first have a 0ms timestamp");
        if (badEnd > 0) parts.Add($"{badEnd} line(s) end before they start");
        if (squashed > 0)
            parts.Add($"{squashed} line(s) END inside a quarter of their slot — the words burst at the line start and the "
                + "line then sits dead until the next one (compressed line-end / syllable offsets)");
        if (tooFast > 0)
            parts.Add($"{tooFast} line(s) would have to be sung faster than {ImpossibleWordsPerSecond:0} words/second");
        if (syllOutOfOrder > 0) parts.Add($"{syllOutOfOrder} syllable(s) run backwards");
        if (syllOutside > 0) parts.Add($"{syllOutside} syllable(s) fall outside their own line");
        if (HasImplausibleWordTiming(doc, out _, out _))
            parts.Add("VERDICT: the word-timing gate rejects this — the reranker demotes it to the line tier and the "
                + "winner's syllables are stripped, so the view falls back to line-level highlighting on the (correct) starts");

        return parts.Count == 0
            ? "clean — monotonic lines, plausible durations, every timestamp inside its line"
            : string.Join(" · ", parts);
    }

    /// <summary>Keep what the payload got RIGHT and drop what it got wrong. The line STARTS of such a document are
    /// typically perfect (they align 1:1 with the Spotify reference); it is only the word offsets and line ends that are
    /// compressed. Stripping the syllables and clearing the ends leaves a document shaped exactly like a natively
    /// line-synced one — which the view already renders correctly — instead of a karaoke wipe over invented timings.</summary>
    public static LyricsDocument StripWordTiming(LyricsDocument doc)
    {
        var lines = new List<LyricLine>(doc.Lines.Count);
        foreach (var l in doc.Lines)
            lines.Add(l with { Syllables = Array.Empty<LyricSyllable>(), IsWordByWord = false, EndMs = null });
        return doc with { Lines = lines, Sync = LyricsSyncKind.Line };
    }
}
