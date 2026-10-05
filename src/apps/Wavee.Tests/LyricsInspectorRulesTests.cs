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

    // ── v2 (W3-G): the reference line ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ReferenceOf_Spotify_IsASource_AndReadsByProductName()
    {
        var r = Diagnostics.LyricsReport.ReferenceOf("3/7 returned; ref=spotify; winner=amll (Syllable, score 0.91, offset 0ms)");
        Assert.Equal(Diagnostics.LyricsReport.ReferenceKind.Source, r.Kind);
        Assert.Equal(new[] { "spotify" }, r.Sources);
        Assert.Equal("Reference: Spotify", Diagnostics.LyricsReport.ReferenceLine(r));
    }

    [Fact]
    public void ReferenceOf_Consensus_ListsTheReferenceThenItsPeers()
    {
        var r = Diagnostics.LyricsReport.ReferenceOf("4/7 returned; ref=consensus(kugou,qq,netease); winner=kugou (Syllable, score 0.84, offset 0ms, unverified)");
        Assert.Equal(Diagnostics.LyricsReport.ReferenceKind.Consensus, r.Kind);
        Assert.Equal(new[] { "kugou", "qq", "netease" }, r.Sources);
        Assert.Equal("Reference: consensus of kugou, qq, netease", Diagnostics.LyricsReport.ReferenceLine(r));
    }

    [Theory]
    [InlineData("2/7 returned; ref=none; winner=lrclib (Line, score 0.62, offset 0ms, unverified)")]
    [InlineData("2/7 returned; ref=none")]
    [InlineData("2/7 returned; ref=none — background complete")]
    public void ReferenceOf_None_ReadsNoReference(string summary)
    {
        var r = Diagnostics.LyricsReport.ReferenceOf(summary);
        Assert.Equal(Diagnostics.LyricsReport.ReferenceKind.None, r.Kind);
        Assert.Equal("No reference", Diagnostics.LyricsReport.ReferenceLine(r));
    }

    [Theory]
    [InlineData("0/7 sources returned lyrics — no match anywhere")]
    [InlineData("served from the on-disk cache (saved 2026-09-29 10:00:00Z); winner=amll (Syllable, 42 lines)")]
    [InlineData("")]
    [InlineData(null)]
    public void ReferenceOf_ASummaryWithoutTheToken_IsAbsent_AndPrintsNothing(string? summary)
    {
        var r = Diagnostics.LyricsReport.ReferenceOf(summary);
        Assert.Equal(Diagnostics.LyricsReport.ReferenceKind.Absent, r.Kind);
        Assert.Equal("", Diagnostics.LyricsReport.ReferenceLine(r));
    }

    [Fact]
    public void ReferenceOf_ASuffixAfterTheToken_IsNotPartOfTheSourceId()
        => Assert.Equal(new[] { "spotify" }, Diagnostics.LyricsReport.ReferenceOf("1/7 returned; ref=spotify — background complete").Sources);

    // ── v2: the stage-0 match line ──────────────────────────────────────────────────────────────────────────────────

    static Lyrics.SourceTrace V2(string id, Lyrics.Outcome outcome = Lyrics.Outcome.Hit, string detail = "", double score = 0.8,
        bool winner = false, string reason = "", Lyrics.MatchBand band = Lyrics.MatchBand.None, double conf = 0d, string match = "",
        double recall = 0d, int support = 0)
        => new(id, outcome, 300, detail, Lyrics.SyncKind.Line, 40, score, winner, reason,
            Band: band, Confidence: conf, Match: match, Recall: recall, Support: support);

    [Fact]
    public void MatchText_ASearchMatchedSource_NamesBandConfidenceAndBreadcrumb()
        => Assert.Equal("VeryHigh · conf 0.93 · a1b2 VeryHigh title=7 artist=7 dur=+320ms",
            Diagnostics.LyricsReport.MatchText(V2("kugou", band: Lyrics.MatchBand.VeryHigh, conf: 0.9312,
                match: "a1b2 VeryHigh title=7 artist=7 dur=+320ms")));

    [Fact]
    public void MatchText_AnIdentityMatch_HasNoLine()
    {
        // Identity/ISRC candidates keep the defaults (Perfect / 1.0) and no breadcrumb: there was no search to explain.
        var t = V2("amll", band: Lyrics.MatchBand.Perfect, conf: 1d);
        Assert.Equal("", Diagnostics.LyricsReport.MatchText(t));
        Assert.Equal("", Diagnostics.LyricsReport.MatchText(t, Lyrics.MatchBasis.Identity));
    }

    [Fact]
    public void MatchText_ASourceWithNoCandidate_HasNoLine()
        => Assert.Equal("", Diagnostics.LyricsReport.MatchText(V2("qq", Lyrics.Outcome.Miss)));

    [Fact]
    public void MatchText_ASearchBasisWithoutABreadcrumb_StillShowsTheBand()
        => Assert.Equal("Perfect · conf 1.00",
            Diagnostics.LyricsReport.MatchText(V2("lrclib", band: Lyrics.MatchBand.Perfect, conf: 1d), Lyrics.MatchBasis.MetadataSearch));

    [Theory]
    [InlineData(0.925, "0.93")]
    [InlineData(1d, "1.00")]
    [InlineData(0d, "0.00")]
    public void ConfidenceText_IsTwoDecimalsInvariant(double conf, string expected)
        => Assert.Equal(expected, Diagnostics.LyricsReport.ConfidenceText(conf));

    // ── v2: the rerank line ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RerankText_AnAlignedCandidate_ReadsRecallSupportAndTheAppliedOffset()
    {
        var t = V2("kugou", reason: "ref-align lcs=38/40 off=320ms", recall: 0.95, support: 2);
        var facts = Diagnostics.LyricsReport.FactsOf(t);
        Assert.True(facts.Ranked);
        Assert.True(facts.Aligned);
        Assert.Equal(320L, facts.OffsetMs);
        Assert.False(facts.OffsetCapped);
        Assert.Equal("recall 0.95 · support 2 · offset +320 ms", Diagnostics.LyricsReport.RerankText(facts));
    }

    [Fact]
    public void RerankText_ANegativeOffset_KeepsItsSign()
        => Assert.Equal("recall 0.80 · support 0 · offset -1099 ms", Diagnostics.LyricsReport.RerankText(
            Diagnostics.LyricsReport.FactsOf(V2("amll", reason: "ref-align lcs=30/36 off=-1099ms", recall: 0.8))));

    [Fact]
    public void RerankText_AnOverCapOffset_SaysItWasNotApplied()
    {
        var t = V2("netease", reason: "ref-align lcs=20/40 off=0ms [offset 3.2s > cap (live/remix cut?)]", recall: 0.7, support: 1);
        var facts = Diagnostics.LyricsReport.FactsOf(t);
        Assert.True(facts.OffsetCapped);
        Assert.Equal("3.2", facts.CappedOffset);
        Assert.Equal("recall 0.70 · support 1 · offset 3.2 s not applied [capped]", Diagnostics.LyricsReport.RerankText(facts));
    }

    [Fact]
    public void RerankText_APartialDocument_FlagsRecallUnderTheTierBar()
        => Assert.Equal("recall 0.40 [< 0.60] · support 0 · offset 0 ms", Diagnostics.LyricsReport.RerankText(
            Diagnostics.LyricsReport.FactsOf(V2("amll", reason: "ref-align lcs=16/40 off=0ms [recall 0.40 < 0.60: line tier only]", recall: 0.4))));

    [Fact]
    public void RerankText_WithoutAReference_SaysRecallDoesNotApply()
        => Assert.Equal("recall n/a (no reference) · support 2", Diagnostics.LyricsReport.RerankText(
            Diagnostics.LyricsReport.FactsOf(V2("qq", reason: "no-reference [consensus support=2]", support: 2))));

    [Fact]
    public void RerankText_ASourceTheRerankerNeverSaw_HasNoLine()
    {
        Assert.Equal("", Diagnostics.LyricsReport.RerankText(Diagnostics.LyricsReport.FactsOf(V2("qq", Lyrics.Outcome.Miss, score: 0d))));
        Assert.Equal("", Diagnostics.LyricsReport.RerankText(Diagnostics.LyricsReport.FactsOf(V2("qq", Lyrics.Outcome.Hit, score: 0d))));
    }

    // ── v2: the tie-break ───────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("lost to amll on tier syllable > line", "lost to amll on tier (syllable > line)")]
    [InlineData("lost to kugou on verified", "lost to kugou on verification")]
    [InlineData("lost to qq on score 0.912 > 0.880", "lost to qq on score (0.912 > 0.880)")]
    [InlineData("lost to amll on prior×confidence 0.550 > 0.500", "lost to amll on prior × confidence (0.550 > 0.500)")]
    [InlineData("lost to amll on provider id", "lost to amll on provider id (a full tie)")]
    [InlineData("won on tier syllable > line over qq", "won on tier (syllable > line) over qq")]
    [InlineData("won on provider id over musixmatch", "won on provider id (a full tie) over musixmatch")]
    [InlineData("only candidate", "the only candidate")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void TieBreakPhrase_ReadsTheStageTwoKeyInProse(string? tieBreak, string expected)
        => Assert.Equal(expected, Diagnostics.LyricsReport.TieBreakPhrase(tieBreak));

    [Fact]
    public void Verdict_ALosingHit_EndsWithWhoItLostToAndOnWhat()
        => Assert.Equal("not chosen — it returned lyrics but lost the rerank: score 0.74 against the winner's 0.88 — lost to amll on tier (syllable > line)",
            Diagnostics.LyricsReport.Verdict(Trace("qq", Lyrics.Outcome.Hit, 0.74), 0.88, "lost to amll on tier syllable > line"));

    [Fact]
    public void Verdict_TheWinner_SaysWhatItWonOn()
        => Assert.Equal("★ CHOSEN — reranker score 0.91, won on score (0.912 > 0.880) over qq (ref-align)",
            Diagnostics.LyricsReport.Verdict(Trace("amll", Lyrics.Outcome.Hit, 0.91, winner: true, reason: "ref-align"), 0.91,
                "won on score 0.912 > 0.880 over qq"));

    // ── v2: guarded skips ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SkipPhrase_AnOpenBreaker_NamesUntilWhenAndWhy()
    {
        var t = V2("musixmatch", Lyrics.Outcome.Miss, "no match — skipped: breaker open until 14:32 (captcha)", score: 0d);
        Assert.Equal("skipped — breaker open until 14:32 (captcha)", Diagnostics.LyricsReport.SkipPhrase(t));
        Assert.Equal("skipped — breaker open until 14:32 (captcha)", Diagnostics.LyricsReport.Verdict(t, 0.9));
    }

    [Theory]
    [InlineData("no match — cached miss NoLyricForSong 3h ago", "skipped — known miss (no lyric) 3 h ago")]
    [InlineData("no match — cached miss SongNotFound 10min ago; other note", "skipped — known miss (song not found) 10 min ago")]
    public void SkipPhrase_ARememberedMiss_NamesTheKindAndItsAge(string detail, string expected)
        => Assert.Equal(expected, Diagnostics.LyricsReport.SkipPhrase(V2("kugou", Lyrics.Outcome.Miss, detail, score: 0d)));

    [Fact]
    public void SkipPhrase_ADiskHit_SaysNoSourceWasQueried()
        => Assert.Equal("skipped — not queried — served from the local lyrics cache", Diagnostics.LyricsReport.SkipPhrase(
            V2("qq", Lyrics.Outcome.Skipped, "not queried — served from the local lyrics cache", score: 0d)));

    [Fact]
    public void SkipPhrase_AnOrdinaryMissOrTheWinner_IsEmpty()
    {
        Assert.Equal("", Diagnostics.LyricsReport.SkipPhrase(V2("qq", Lyrics.Outcome.Miss, "no match — search 'x' → 0 hit(s), 0 new", score: 0d)));
        Assert.Equal("", Diagnostics.LyricsReport.SkipPhrase(V2("qq", detail: "skipped: breaker open until 14:32 (captcha)", winner: true)));
    }

    [Fact]
    public void Verdict_AFetchThatTrippedTheBreaker_SaysSo()
        => Assert.Equal("not chosen — the request failed; it tripped the breaker for 2 h (captcha)",
            Diagnostics.LyricsReport.Verdict(V2("musixmatch", Lyrics.Outcome.Error, "HttpRequestException: x — breaker tripped for 2h: captcha", score: 0d), 0.9));

    // ── v2: the score breakdown uses the reranker's real prior term ─────────────────────────────────────────────────

    [Fact]
    public void Blend_MatchesTheRerankersOwnScore_IncludingPriorTimesConfidence()
    {
        var doc = new Lyrics.Doc("t1", true,
            [L(1_000, "one two three", 1_900), L(3_000, "four five six", 3_900), L(5_000, "seven eight", 5_900)],
            Lyrics.SyncKind.Line, "kugou");
        var cand = new Lyrics.Candidate("kugou", 0.5, Lyrics.MatchBasis.MetadataSearch, doc)
            { Confidence = 0.8, Band = Lyrics.MatchBand.High };
        var d = Lyrics.Reranker.Rank([cand], null).All[0];
        double rebuilt = Diagnostics.LyricsReport.Blend(d.TextAgreement, d.SyncScore, d.TimingScore, d.Coverage,
            Diagnostics.LyricsReport.PriorTerm(0.5, 0.8));
        Assert.Equal(d.Score, rebuilt, 12);
    }

    [Fact]
    public void ScoreBreakdown_NamesPriorAndConfidence_OrRecoversTheirProduct()
    {
        var t = new Lyrics.SourceTrace("kugou", Lyrics.Outcome.Hit, 300, "", Lyrics.SyncKind.Line, 40,
            Diagnostics.LyricsReport.Blend(1d, 0.6d, 1d, 1d, 0.4d), false, "", Text: 1d, Coverage: 1d, Timing: 1d, SyncScore: 0.6d,
            Band: Lyrics.MatchBand.High, Confidence: 0.8d);
        Assert.EndsWith("coverage 1.00 × .10  +  prior 0.50 × conf 0.80 × .05", Diagnostics.LyricsReport.ScoreBreakdown(t, 0.5));
        Assert.EndsWith("coverage 1.00 × .10  +  prior × conf 0.40 × .05", Diagnostics.LyricsReport.ScoreBreakdown(t, null));
    }

    [Fact]
    public void BuildReport_CarriesTheReference_MatchAndRerankLines()
    {
        var src = V2("kugou", reason: "ref-align lcs=38/40 off=320ms", band: Lyrics.MatchBand.VeryHigh, conf: 0.93,
            match: "a1b2 VeryHigh title=7", recall: 0.95, support: 2);
        var report = new Lyrics.SearchReport("t1", "Song", "Artist", "Album", 200_000, null, 0,
            "2/7 returned; ref=consensus(kugou,qq); winner=kugou (Line, score 0.80, offset 320ms)", [src]);
        string text = Diagnostics.LyricsReport.BuildReport("t1", report, null);
        Assert.Contains("ref:     Reference: consensus of kugou, qq\n", text);
        Assert.Contains("    match:   VeryHigh · conf 0.93 · a1b2 VeryHigh title=7\n", text);
        Assert.Contains("    rerank:  recall 0.95 · support 2 · offset +320 ms\n", text);
        Assert.DoesNotContain("prior × .05", text);
    }

    // ── the on-device AI card and its report section ────────────────────────────────────────────────────────────────

    static AiLyrics.Status AiStatus(bool enabled = true, AiLyrics.SetupPhase phase = AiLyrics.SetupPhase.Ready)
        => AiLyrics.Status.Unknown with { Phase = phase, Availability = AiLyrics.Availability.Available, Enabled = enabled, NpuName = "Hexagon", NpuDriver = "31.0" };

    static AiLyrics.JobInfo AiJob(string outcome, string track = "t1", string detail = "")
        => new(track, "en", 40, 12, 240, 61.5, 3.14, 2.06, 6200, outcome == "cached", "results/t1.v1.json", outcome, detail);

    static Diagnostics.LyricsReport.AiFacts Ai(AiLyrics.JobInfo? job = null, Lyrics.Doc? doc = null, AiLyrics.TrackStatus track = default,
        AiLyrics.Status? status = null, long bytes = Diagnostics.LyricsReport.AiResultMissing)
        => Diagnostics.LyricsReport.AiFactsFor("t1", status ?? AiStatus(), track, job, doc, "results/t1.v1.json", bytes);

    static Lyrics.Doc Shown(bool generated, string provider = "amll", Lyrics.SyncKind sync = Lyrics.SyncKind.Line)
        => new("t1", true, [L(1_000, "a", 1_900)], sync, provider, Generated: generated);

    [Fact]
    public void AiFacts_DropTheJobAndTrackStateOfAnotherTrack()
    {
        var f = Diagnostics.LyricsReport.AiFactsFor("t1", AiStatus(), new AiLyrics.TrackStatus("t2", AiLyrics.TrackPhase.Working,
            AiLyrics.SkipReason.None, "en", 1, 2, 0, false), AiJob("working", track: "t2"), null, "p", -1);
        Assert.Null(f.Job);
        Assert.Null(f.Track);
        Assert.Equal(Diagnostics.LyricsReport.AiLine.Idle, Diagnostics.LyricsReport.AiLineOf(f));
    }

    [Fact]
    public void AiCard_Shows_WhenEnabled_OrWhenTheTrackHasAiData()
    {
        Assert.True(Diagnostics.LyricsReport.ShowsAi(Ai()));
        var off = AiStatus(enabled: false, phase: AiLyrics.SetupPhase.Off);
        Assert.False(Diagnostics.LyricsReport.ShowsAi(Ai(status: off)));
        Assert.True(Diagnostics.LyricsReport.ShowsAi(Ai(status: off, doc: Shown(generated: true))));
        Assert.True(Diagnostics.LyricsReport.ShowsAi(Ai(status: off, bytes: 1200)));
        Assert.True(Diagnostics.LyricsReport.ShowsAi(Ai(status: off, job: AiJob("done"))));
    }

    [Fact]
    public void AiLine_FollowsTheJob_TheDocumentOnScreen_AndTheTrackState()
    {
        Assert.Equal(Diagnostics.LyricsReport.AiLine.Working, Diagnostics.LyricsReport.AiLineOf(Ai(AiJob("working"), Shown(true))));
        Assert.Equal(Diagnostics.LyricsReport.AiLine.OnScreen, Diagnostics.LyricsReport.AiLineOf(Ai(AiJob("done"), Shown(true))));
        Assert.Equal(Diagnostics.LyricsReport.AiLine.Cached, Diagnostics.LyricsReport.AiLineOf(Ai(AiJob("cached"), Shown(true))));
        Assert.Equal(Diagnostics.LyricsReport.AiLine.Replaced, Diagnostics.LyricsReport.AiLineOf(Ai(AiJob("done"), Shown(false))));
        Assert.Equal(Diagnostics.LyricsReport.AiLine.Failed, Diagnostics.LyricsReport.AiLineOf(Ai(AiJob("failed", detail: "IOException: x"))));
        Assert.Equal(Diagnostics.LyricsReport.AiLine.Cancelled, Diagnostics.LyricsReport.AiLineOf(Ai(AiJob("cancelled"))));
        Assert.Equal(Diagnostics.LyricsReport.AiLine.NotReady,
            Diagnostics.LyricsReport.AiLineOf(Ai(status: AiStatus(phase: AiLyrics.SetupPhase.NeedsSetup))));
        var skipped = new AiLyrics.TrackStatus("t1", AiLyrics.TrackPhase.Skipped, AiLyrics.SkipReason.TooLong, "en", 0, 4, 0, false);
        var f = Ai(track: skipped);
        Assert.Equal(Diagnostics.LyricsReport.AiLine.Skipped, Diagnostics.LyricsReport.AiLineOf(f));
        Assert.Equal("TooLong", Diagnostics.LyricsReport.AiSkipReason(f));
        Assert.Equal(Diagnostics.LyricsReport.AiInk.Grey, Diagnostics.LyricsReport.AiInkOf(Diagnostics.LyricsReport.AiLine.Skipped));
    }

    [Fact]
    public void AiLine_PeopleMadeWordTiming_OutranksOurs_AndNamesItsProvider()
    {
        var stopped = Ai(AiJob("stopped", detail: "amll"), Shown(false, "amll", Lyrics.SyncKind.Syllable));
        Assert.Equal(Diagnostics.LyricsReport.AiLine.Outranked, Diagnostics.LyricsReport.AiLineOf(stopped));
        Assert.Equal("amll", Diagnostics.LyricsReport.AiProvider(stopped));

        var already = new AiLyrics.TrackStatus("t1", AiLyrics.TrackPhase.Skipped, AiLyrics.SkipReason.AlreadyWordByWord, "en", 0, 4, 0, false);
        var f = Ai(doc: Shown(false, "spotify", Lyrics.SyncKind.Syllable), track: already);
        Assert.Equal(Diagnostics.LyricsReport.AiLine.Outranked, Diagnostics.LyricsReport.AiLineOf(f));
        Assert.Equal("spotify", Diagnostics.LyricsReport.AiProvider(f));
    }

    [Fact]
    public void AiNumbers_OneDecimal_AndDashWithoutAJob()
    {
        var f = Ai(AiJob("working"));
        Assert.Equal(new Diagnostics.LyricsReport.AiProgress("12", "40", "61.5", "240.0"), Diagnostics.LyricsReport.AiProgressOf(f));
        Assert.Equal(new Diagnostics.LyricsReport.AiTimes("3.1", "2.1", "6.2", "en"), Diagnostics.LyricsReport.AiTimesOf(f));
        Assert.Equal(new Diagnostics.LyricsReport.AiTimes("—", "—", "—", "—"), Diagnostics.LyricsReport.AiTimesOf(Ai()));
    }

    [Fact]
    public void BuildReport_CarriesTheAiSection_OnlyWhenGivenItsFacts()
    {
        Assert.DoesNotContain("## on-device ai", Diagnostics.LyricsReport.BuildReport("t1", null, null));
        string text = Diagnostics.LyricsReport.BuildReport("t1", null, null, Ai(AiJob("done"), Shown(true), bytes: 2048));
        Assert.Contains("## on-device ai\nstate:   OnScreen   setup=Ready   enabled=True   pack=v1\n", text);
        Assert.Contains("job:     done   lines=12/40   timed=61.5s of 240.0s   language=en\n", text);
        Assert.Contains("times:   separate 3.1s · align 2.1s · total 6.2s   fromCache=False\n", text);
        Assert.Contains("npu:     Hexagon   driver=31.0\n", text);
        Assert.Contains("result:  results/t1.v1.json   2 KB\n", text);
        Assert.Contains("doc:     provider amll   sync=Line   generated=True\n", text);
        Assert.Contains("result:  results/t1.v1.json   (not saved)\n", Diagnostics.LyricsReport.BuildReport("t1", null, null, Ai()));
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
