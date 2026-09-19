// ── Wavee.Tests/LyricsInspectorRulesTests.cs — the lyrics inspector's pure half (Diagnostics.LyricsReport) ───────────
//
// A14 / ch 22 W17-W19b: the inspector dialog (Screens/Diagnostics.UI.cs) lays out text and inks that are DECIDED in
// `Diagnostics.LyricsReport` (Screens/Diagnostics.cs, CORE). Ported from 0.2.9's `LyricsInspectionExport` + the dialog's
// row rules, so these facts drive the real functions: the report's sections, both truncation clauses of a payload
// caption, the parsed tab's anomaly inks, the time/duration cells (including a line with no end), the blank syllable,
// the bundle's file names, and the invariant culture every number is printed in. No file, no clock, no store.

using System.Globalization;
using Xunit;

namespace Wavee.Tests;

public class LyricsInspectorRulesTests
{
    static Lyrics.Doc Doc(params Lyrics.Line[] lines) => new("t1", true, lines, Lyrics.SyncKind.Line, "amll");

    static Lyrics.Line L(long start, string text, long? end = null, params Lyrics.Syllable[] syllables)
        => new(start, text, syllables, end);

    static Lyrics.SourceTrace Trace(string id, Lyrics.Outcome outcome, double score = 0d, bool winner = false, string reason = "")
        => new(id, outcome, 412, "", Lyrics.SyncKind.Line, 42, score, winner, reason);

    static Lyrics.SearchReport Report(params Lyrics.SourceTrace[] sources)
        => new("t1", "Every night", "Céline", "Album", 274_000, "USSM1", 0, "amll won on timing", sources);

    // ── cells ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0L, "00:00.000")]
    [InlineData(12_480L, "00:12.480")]
    [InlineData(754_321L, "12:34.321")]
    [InlineData(-5L, "00:00.000")]
    public void Ts_IsMinutesSecondsMillis_ClampedAtZero(long ms, string expected)
        => Assert.Equal(expected, Diagnostics.LyricsReport.Ts(ms));

    [Fact]
    public void TimeCell_SpellsBothEnds_AndDashesAMissingEnd()
    {
        Assert.Equal("00:12.480→00:16.020", Diagnostics.LyricsReport.TimeCell(L(12_480, "a", 16_020)));
        Assert.Equal("00:12.480→  --:--.---", Diagnostics.LyricsReport.TimeCell(L(12_480, "a")));
    }

    [Fact]
    public void DurationCell_IsMilliseconds_AndEmptyWithNoEnd()
    {
        Assert.Equal("217ms", Diagnostics.LyricsReport.DurationCell(L(1_000, "a", 1_217)));
        Assert.Equal("", Diagnostics.LyricsReport.DurationCell(L(1_000, "a")));
    }

    [Fact]
    public void SyllableStrip_PrintsSpans_AndABlankSyllableAsTheSpaceGlyph()
    {
        var line = L(12_480, "water", 12_980, new Lyrics.Syllable(12_480, 12_610, "wa"), new Lyrics.Syllable(12_610, 12_980, ""));
        Assert.Equal("wa[12480-12610]  ␣[12610-12980]", Diagnostics.LyricsReport.SyllableStrip(line));
    }

    // ── the parsed tab's inks ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Check_OutOfOrder_IsBad()
    {
        var doc = Doc(L(5_000, "one", 5_900), L(3_000, "two", 3_900));
        Assert.Equal(Diagnostics.LyricsReport.TimeInk.Bad, Diagnostics.LyricsReport.Check(doc, 1).Time);
    }

    [Fact]
    public void Check_EndBeforeStart_IsBad()
    {
        var doc = Doc(L(5_000, "one", 4_000), L(9_000, "two", 9_900));
        Assert.Equal(Diagnostics.LyricsReport.TimeInk.Bad, Diagnostics.LyricsReport.Check(doc, 0).Time);
    }

    [Fact]
    public void Check_AZeroStampAfterTheFirstLine_IsWarn_ButTheFirstLineMayStartAtZero()
    {
        var doc = Doc(L(0, "one", 900), L(0, "two", 900), L(2_000, "three", 2_900));
        Assert.Equal(Diagnostics.LyricsReport.TimeInk.Normal, Diagnostics.LyricsReport.Check(doc, 0).Time);
        Assert.Equal(Diagnostics.LyricsReport.TimeInk.Warn, Diagnostics.LyricsReport.Check(doc, 1).Time);
    }

    [Fact]
    public void Check_ASquashedLine_WarnsBothTheTimeAndTheDurationCell()
    {
        // 200 ms spoken inside a 4 s slot: ends inside a quarter of its slot (Lyrics.Timing.IsSquashed).
        var doc = Doc(L(0, "a whole line of words", 200), L(4_000, "next", 4_800));
        var check = Diagnostics.LyricsReport.Check(doc, 0);
        Assert.True(check.Squashed);
        Assert.Equal(Diagnostics.LyricsReport.TimeInk.Warn, check.Time);
        Assert.False(Diagnostics.LyricsReport.Check(doc, 1).Squashed);   // the last line has no slot to be squashed in
    }

    // ── the payload caption: two truncations, named separately ─────────────────────────────────────────────────────

    [Fact]
    public void PayloadCaption_NamesTheCaptureCapAndTheOnScreenCap()
    {
        var p = new Lyrics.RawPayload("amll", "GET https://x/amll/t1.ttml", "ttml", new string('x', 128_000), 412_880);
        string caption = Diagnostics.LyricsReport.PayloadCaption(p, Diagnostics.LyricsReport.OnScreenRawChars);
        Assert.Equal("ttml · 412,880 chars · CAPTURE-TRUNCATED to 128,000 · showing the first 4,000", caption);
    }

    [Fact]
    public void PayloadCaption_ASmallWholePayload_HasNeitherClause()
    {
        var p = new Lyrics.RawPayload("lrclib", "GET https://x", "json", "{}", 2);
        Assert.Equal("json · 2 chars", Diagnostics.LyricsReport.PayloadCaption(p, 2));
    }

    // ── the report ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildReport_WithNothingRecorded_StatesEachAbsence()
    {
        string text = Diagnostics.LyricsReport.BuildReport("t1", null, null);
        Assert.Contains("# Wavee lyrics source report", text);
        Assert.Contains("summary: (no search recorded)", text);
        Assert.Contains("## providers\n(none)", text);
        Assert.Contains("(none — the answer came from a cache, so no provider was contacted)", text);
        Assert.Contains("## parsed candidates\n(none)", text);
    }

    [Fact]
    public void BuildReport_CarriesProviders_Payloads_Candidates_AndTheFinalDocument()
    {
        var doc = Doc(L(1_000, "one", 1_900), L(3_000, "two", 3_900));
        var insp = new Lyrics.Inspection("t1", 0, "a pass",
            [new Lyrics.RawPayload("amll", "GET https://x", "ttml", "<tt/>", 5)],
            [new Lyrics.ParsedCandidate("amll", Lyrics.MatchBasis.Identity, 0.9, doc)],
            doc);
        string text = Diagnostics.LyricsReport.BuildReport("t1",
            Report(Trace("amll", Lyrics.Outcome.Hit, 0.93, winner: true, reason: "timing"), Trace("lrclib", Lyrics.Outcome.Miss)), insp);

        Assert.Contains("title:   Every night  —  Céline", text);
        Assert.Contains("- amll  Hit  412ms", text);
        Assert.Contains("verdict: ★ CHOSEN — reranker score 0.93 (timing)", text);
        Assert.Contains("verdict: not chosen — the provider had nothing for this track", text);
        Assert.Contains("- amll  ttml  5 chars  GET https://x", text);
        Assert.Contains("- amll  Line  2 lines  basis=Identity", text);
        Assert.Contains("# parsed lyrics — final", text);
        Assert.Contains("idx\tstart\tend\tdurMs\tgapToNextMs\twordsPerSec\ttext", text);
    }

    [Theory]
    [InlineData(Lyrics.Outcome.Timeout, "not chosen — it did not answer inside the per-source budget")]
    [InlineData(Lyrics.Outcome.Error, "not chosen — the request failed")]
    [InlineData(Lyrics.Outcome.Skipped, "not chosen — it never ran to completion (a faster match closed the window)")]
    public void Verdict_NamesWhyALoserLost(Lyrics.Outcome outcome, string expected)
        => Assert.Equal(expected, Diagnostics.LyricsReport.Verdict(Trace("x", outcome), 0.9));

    [Fact]
    public void Verdict_AHitThatLostTheRerank_QuotesBothScores()
        => Assert.Equal("not chosen — it returned lyrics but lost the rerank: score 0.74 against the winner's 0.88",
            Diagnostics.LyricsReport.Verdict(Trace("x", Lyrics.Outcome.Hit, 0.74), 0.88));

    [Fact]
    public void EveryNumber_IsInvariant_UnderACommaDecimalCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            // The repo builds with InvariantGlobalization (no named cultures), so the comma culture is hand-made.
            var comma = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            comma.NumberFormat.NumberDecimalSeparator = ",";
            comma.NumberFormat.NumberGroupSeparator = ".";
            CultureInfo.CurrentCulture = comma;
            var doc = Doc(L(1_000, "one two three", 1_500));
            var candidate = new Lyrics.ParsedCandidate("amll", Lyrics.MatchBasis.Isrc, 0.9, doc);
            Assert.Equal("parsed: Line, 1 lines, 0 syllables, matched by Isrc, prior 0.90", Diagnostics.LyricsReport.ParsedLine(candidate));
            var p = new Lyrics.RawPayload("amll", "GET", "ttml", new string('x', 5_000), 5_000);
            Assert.Contains("5,000 chars", Diagnostics.LyricsReport.PayloadCaption(p, 4_000));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    // ── the helpers the tabs read ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RawSources_AreDistinct_InCaptureOrder_AndCountsFollow()
    {
        var insp = new Lyrics.Inspection("t1", 0, "",
            [
                new Lyrics.RawPayload("lrclib", "a", "json", "1", 1),
                new Lyrics.RawPayload("amll", "b", "ttml", "2", 1),
                new Lyrics.RawPayload("lrclib", "c", "json", "3", 1),
            ],
            [], null);
        Assert.Equal(new[] { "lrclib", "amll" }, Diagnostics.LyricsReport.RawSources(insp));
        Assert.Equal(2, Diagnostics.LyricsReport.RawCount(insp, "lrclib"));
        Assert.Equal(0, Diagnostics.LyricsReport.RawCount(null, "lrclib"));
        Assert.Null(Diagnostics.LyricsReport.CandidateFor(insp, "amll"));
        Assert.Null(Diagnostics.LyricsReport.CandidateFor(insp, ""));
    }

    [Fact]
    public void WinnerScore_IsTheChosenSources_OrZero()
    {
        Assert.Equal(0.93, Diagnostics.LyricsReport.WinnerScore(Report(Trace("a", Lyrics.Outcome.Miss), Trace("b", Lyrics.Outcome.Hit, 0.93, winner: true))));
        Assert.Equal(0d, Diagnostics.LyricsReport.WinnerScore(null));
    }

    // ── the bundle's names ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BundleNames_ArePathSafe_Sortable_AndKeepTheRealExtension()
    {
        Assert.Equal("20260913-181500-spotify_track_4uLU", Diagnostics.LyricsReport.BundleFolderName("spotify:track:4uLU",
            new DateTime(2026, 9, 13, 18, 15, 0, DateTimeKind.Utc)));
        Assert.Equal("raw-amll-2.ttml", Diagnostics.LyricsReport.RawFileName("amll", 2, "ttml"));
        Assert.Equal("raw-qq_music-1.txt", Diagnostics.LyricsReport.RawFileName("qq music", 1, "qrc"));
        Assert.Equal("parsed-final.tsv", Diagnostics.LyricsReport.ParsedFileName("final"));
        Assert.Equal("unknown", Diagnostics.LyricsReport.SafeName(""));
    }
}
