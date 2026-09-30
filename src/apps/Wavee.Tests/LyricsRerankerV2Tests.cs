// ── Wavee.Tests/LyricsRerankerV2Tests.cs — reranker v2 (lyrics-grey-sources-implementation.md §2-§7) ───────────────
//
// The v1 expectations stay in `LyricsRerankerTests` (LyricsHostTests.cs) and must keep passing unchanged; this class
// covers what v2 adds: CJK-aware comparison text, the recall term, the offset guard, prior × confidence and the
// metadata-band gate, the consensus reference (and that it never verifies itself), the deterministic stage-two order,
// the gold guard, and the incoherent-word-timing / CJK-rate gates. All lyric text here is SYNTHETIC.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class LyricsRerankerV2Tests
{
    // ── builders (the same shapes LyricsRerankerTests uses) ─────────────────────────────────────────────────────────

    static Lyrics.Doc LineDoc(string provider, params (long Ms, string Text)[] lines)
    {
        var l = new List<Lyrics.Line>(lines.Length);
        foreach (var (ms, text) in lines) l.Add(new Lyrics.Line(ms, text, []));
        return new Lyrics.Doc("t1", true, l, Lyrics.SyncKind.Line, provider);
    }

    static Lyrics.Doc WordDoc(string provider, long spanMs, params (long Ms, string Text)[] lines)
    {
        var l = new List<Lyrics.Line>(lines.Length);
        foreach (var (ms, text) in lines)
            l.Add(new Lyrics.Line(ms, text, [new Lyrics.Syllable(ms, ms + spanMs, text)], ms + spanMs, null, null, true));
        return new Lyrics.Doc("t1", true, l, Lyrics.SyncKind.Syllable, provider);
    }

    static (long, string)[] Shift((long Ms, string Text)[] lines, long by)
    {
        var o = new (long, string)[lines.Length];
        for (int i = 0; i < lines.Length; i++) o[i] = (lines[i].Ms + by, lines[i].Text);
        return o;
    }

    static readonly (long Ms, string Text)[] RealSong =
    [
        (1000, "the lanterns drift along the harbour wall"),
        (5000, "we counted every boat that never came"),
        (9000, "your coat still hanging where you left it"),
        (13000, "the kettle singing softly in the hall"),
        (17000, "and every morning starts the same"),
        (21000, "i keep the window open for the rain"),
    ];

    static readonly (long Ms, string Text)[] WrongSong =
    [
        (1000, "copper wires humming through the night"),
        (5000, "static on the radio again"),
        (9000, "painted doors and paper kites"),
        (13000, "running out of reasons to pretend"),
        (17000, "silver trains that never stop"),
        (21000, "dancing on the kitchen floor"),
    ];

    // Synthetic Chinese lines, no spaces (as a Chinese reference ships them).
    static readonly (long Ms, string Text)[] CjkRef =
    [
        (1000, "我们一起走过的路"),
        (5000, "风吹过那片田野"),
        (9000, "你说你会等着我"),
        (13000, "天空慢慢变蓝了"),
        (17000, "我还记得那首歌"),
        (21000, "我们唱到天亮"),
    ];

    // The same lyric as a Chinese karaoke source prints it: spaces between phrases, full-width punctuation, and one
    // character different on two lines. Whole-line tokens (v1) share NOTHING with the reference.
    static readonly (long Ms, string Text)[] CjkKaraoke =
    [
        (1300, "我们 一起 走过的路！"),
        (5300, "风吹过 那片田野，"),
        (9300, "你说 你会 等着我"),
        (13300, "天空 慢慢 变蓝啦"),
        (17300, "我还 记得 那首歌。"),
        (21300, "我们 唱到 天明"),
    ];

    static readonly (long Ms, string Text)[] CjkRomanized =
    [
        (1000, "wo men yi qi zou guo de lu"),
        (5000, "feng chui guo na pian tian ye"),
        (9000, "ni shuo ni hui deng zhe wo"),
        (13000, "tian kong man man bian lan le"),
        (17000, "wo hai ji de na shou ge"),
        (21000, "wo men chang dao tian liang"),
    ];

    static Lyrics.Decision For(Lyrics.Ranked r, string id)
    {
        foreach (var d in r.All) if (d.ProviderId == id) return d;
        throw new InvalidOperationException("no decision for " + id);
    }

    static IEnumerable<T[]> Permutations<T>(T[] items)
    {
        if (items.Length <= 1) { yield return items; yield break; }
        for (int i = 0; i < items.Length; i++)
        {
            var rest = new T[items.Length - 1];
            for (int j = 0, k = 0; j < items.Length; j++) if (j != i) rest[k++] = items[j];
            foreach (var p in Permutations(rest))
            {
                var o = new T[items.Length];
                o[0] = items[i];
                Array.Copy(p, 0, o, 1, p.Length);
                yield return o;
            }
        }
    }

    // ── §2 CJK-aware comparison text ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Cjk_karaoke_is_verified_against_a_cjk_reference_and_wins_on_tier()
    {
        var reference = LineDoc("spotify", CjkRef);
        var spotify = new Lyrics.Candidate("spotify", 0.5, Lyrics.MatchBasis.Identity, reference);
        var kugou = new Lyrics.Candidate("kugou", 0.5, Lyrics.MatchBasis.MetadataSearch, WordDoc("kugou", 2500, CjkKaraoke))
        { Band = Lyrics.MatchBand.VeryHigh, Confidence = 0.93 };

        Assert.True(Lyrics.Reranker.TextAgreement(kugou.Document, reference) >= 0.5);

        var ranked = Lyrics.Reranker.Rank([spotify, kugou], reference);

        var d = For(ranked, "kugou");
        Assert.True(d.Verified, d.Reason);
        Assert.True(d.Recall >= Lyrics.Reranker.TierRecall);
        Assert.DoesNotContain("sync-gate:demoted", d.Reason, StringComparison.Ordinal);
        Assert.Equal("kugou", ranked.Best!.ProviderId);
        Assert.Equal(Lyrics.SyncKind.Syllable, ranked.Winner!.Sync);
        Assert.Equal(-300, ranked.Best.AppliedOffsetMs);   // a 300 ms skew is still pulled onto the reference
    }

    [Fact]
    public void A_romanized_body_does_not_verify_against_the_original_script()
    {
        var reference = LineDoc("spotify", CjkRef);
        var spotify = new Lyrics.Candidate("spotify", 0.5, Lyrics.MatchBasis.Identity, reference);
        var roman = new Lyrics.Candidate("netease", 0.5, Lyrics.MatchBasis.MetadataSearch, WordDoc("netease", 2500, CjkRomanized))
        { Band = Lyrics.MatchBand.VeryHigh };

        var ranked = Lyrics.Reranker.Rank([roman, spotify], reference);

        var d = For(ranked, "netease");
        Assert.False(d.Verified);
        Assert.Equal(0d, d.TextAgreement);
        Assert.Equal("spotify", ranked.Best!.ProviderId);
    }

    [Fact]
    public void Comparison_text_folds_full_width_and_keeps_latin_exactly_as_before()
    {
        Assert.Equal("love", Lyrics.Text.Normalize("Ｌｏｖｅ！"));
        Assert.Equal("don t stop", Lyrics.Text.Normalize("Don't  Stop!"));
        // Full-width punctuation between CJK characters is stripped like any other punctuation.
        Assert.Equal(Lyrics.Text.Normalize("我们一起"), Lyrics.Text.Normalize("我们，一起！").Replace(" ", "", StringComparison.Ordinal));
    }

    // ── §3 recall ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_trusted_partial_is_verified_only_at_the_line_tier_and_loses_to_the_complete_reference()
    {
        var reference = LineDoc("spotify", RealSong);
        var spotify = new Lyrics.Candidate("spotify", 0.5, Lyrics.MatchBasis.Identity, reference);
        var partial = new Lyrics.Candidate("amll", 1.0, Lyrics.MatchBasis.Identity,
            WordDoc("amll", 2500, RealSong[0], RealSong[1], RealSong[2]));

        var ranked = Lyrics.Reranker.Rank([partial, spotify], reference);

        var d = For(ranked, "amll");
        Assert.True(d.Verified);                       // still the right recording…
        Assert.Equal(0.5, d.Recall, 3);                // …but only half of it
        Assert.Equal(0.6, d.SyncScore, 3);             // counted at the line tier
        Assert.Contains("line tier only", d.Reason, StringComparison.Ordinal);
        Assert.Equal("spotify", ranked.Best!.ProviderId);
    }

    // ── §4 offset guard ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_offset_beyond_the_cap_is_neither_applied_nor_tier_verified()
    {
        var reference = LineDoc("spotify", RealSong);
        var live = new Lyrics.Candidate("lrclib", 0.4, Lyrics.MatchBasis.MetadataSearch, LineDoc("lrclib", Shift(RealSong, 3000)));

        var ranked = Lyrics.Reranker.Rank([live], reference);

        var d = ranked.Best!;
        Assert.True(d.OffsetCapped);
        Assert.False(d.Verified);
        Assert.Equal(0, d.AppliedOffsetMs);
        Assert.Equal(0.4, d.TimingScore, 3);
        Assert.Contains("offset 3.0s > cap (live/remix cut?)", d.Reason, StringComparison.Ordinal);
        Assert.Equal(4000, ranked.Winner!.Lines[0].StartMs);   // not shifted
    }

    [Fact]
    public void A_trusted_word_sync_keeps_its_own_clock_when_the_alignment_is_loose()
    {
        var reference = LineDoc("spotify", RealSong);
        // Deltas 600/1400/600/1400/1000/1000: median +1000, MAD 400 (> 300) — coherent enough to score, too loose to snap.
        long[] jitter = [600, 1400, 600, 1400, 1000, 1000];
        var loose = new (long, string)[RealSong.Length];
        for (int i = 0; i < RealSong.Length; i++) loose[i] = (RealSong[i].Ms + jitter[i], RealSong[i].Text);
        var amll = new Lyrics.Candidate("amll", 0.9, Lyrics.MatchBasis.Identity, WordDoc("amll", 2500, loose));

        var ranked = Lyrics.Reranker.Rank([amll], reference);

        Assert.Equal(0, ranked.Best!.AppliedOffsetMs);
        Assert.False(ranked.Best.OffsetCapped);
        Assert.Contains("own-clock", ranked.Best.Reason, StringComparison.Ordinal);
        Assert.Equal(1600, ranked.Winner!.Lines[0].StartMs);
    }

    [Fact]
    public void A_trusted_word_sync_IS_snapped_when_the_alignment_is_tight()
    {
        var reference = LineDoc("spotify", RealSong);
        var amll = new Lyrics.Candidate("amll", 0.9, Lyrics.MatchBasis.Identity, WordDoc("amll", 2500, Shift(RealSong, 1000)));

        var ranked = Lyrics.Reranker.Rank([amll], reference);

        Assert.Equal(-1000, ranked.Best!.AppliedOffsetMs);   // MAD 0 on 6 pairs
        Assert.Equal(1000, ranked.Winner!.Lines[0].StartMs);
    }

    // ── §5 consensus ────────────────────────────────────────────────────────────────────────────────────────────────

    static Lyrics.Candidate[] ThreeAgreeingAndOneWrongKaraoke() =>
    [
        new("lrclib", 0.4, Lyrics.MatchBasis.MetadataSearch, LineDoc("lrclib", RealSong)) { Band = Lyrics.MatchBand.High },
        new("qq", 0.55, Lyrics.MatchBasis.MetadataSearch, LineDoc("qq", Shift(RealSong, 100))) { Band = Lyrics.MatchBand.VeryHigh },
        new("netease", 0.5, Lyrics.MatchBasis.MetadataSearch, LineDoc("netease", RealSong)) { Band = Lyrics.MatchBand.High },
        new("kugou", 0.5, Lyrics.MatchBasis.MetadataSearch, WordDoc("kugou", 2500, WrongSong)) { Band = Lyrics.MatchBand.VeryHigh },
    ];

    [Fact]
    public void Without_a_reference_three_agreeing_documents_beat_one_wrong_song_karaoke()
    {
        var cs = ThreeAgreeingAndOneWrongKaraoke();

        var ranked = Lyrics.Reranker.Rank(cs, null);

        Assert.NotEqual("kugou", ranked.Best!.ProviderId);
        Assert.Equal(Lyrics.SyncKind.Line, ranked.Winner!.Sync);
        var k = For(ranked, "kugou");
        Assert.False(k.Verified);
        Assert.Equal(0, k.Support);
        Assert.Contains("no support: score only", k.Reason, StringComparison.Ordinal);
        var l = For(ranked, "lrclib");
        Assert.True(l.Verified);
        Assert.Equal(2, l.Support);
    }

    [Fact]
    public void The_consensus_reference_is_picked_from_the_agreeing_majority()
    {
        var cs = ThreeAgreeingAndOneWrongKaraoke();

        var reference = Lyrics.Reranker.ConsensusReference(cs, out int[] support, out int idx);

        Assert.NotNull(reference);
        Assert.Equal(new[] { 2, 2, 2, 0 }, support);
        Assert.Equal("lrclib", cs[idx].ProviderId);   // tie on support/trust/sync/confidence → provider id order
        Assert.Same(cs[idx].Document, reference);

        var ranked = Lyrics.Reranker.Rank(cs, reference);
        Assert.NotEqual("kugou", ranked.Best!.ProviderId);
        Assert.False(For(ranked, "kugou").Verified);
    }

    [Fact]
    public void Nobody_agreeing_is_no_consensus_reference()
    {
        Lyrics.Candidate[] cs =
        [
            new("lrclib", 0.4, Lyrics.MatchBasis.MetadataSearch, LineDoc("lrclib", RealSong)),
            new("kugou", 0.5, Lyrics.MatchBasis.MetadataSearch, WordDoc("kugou", 2500, WrongSong)),
        ];
        Assert.Null(Lyrics.Reranker.ConsensusReference(cs, out int[] support, out int idx));
        Assert.Equal(-1, idx);
        Assert.Equal(new[] { 0, 0 }, support);
    }

    [Fact]
    public void A_lone_metadata_syllable_candidate_is_ranked_by_score_only_never_tier_promoted()
    {
        var kugou = new Lyrics.Candidate("kugou", 0.5, Lyrics.MatchBasis.MetadataSearch, WordDoc("kugou", 2500, WrongSong))
        { Band = Lyrics.MatchBand.VeryHigh, Confidence = 0.93 };
        var mxm = new Lyrics.Candidate("musixmatch", 0.7, Lyrics.MatchBasis.Isrc, LineDoc("musixmatch", RealSong));

        var ranked = Lyrics.Reranker.Rank([kugou, mxm], null);

        var k = For(ranked, "kugou");
        Assert.False(k.Verified);
        Assert.True(k.Score > For(ranked, "musixmatch").Score);   // it would have won on the blended score alone
        Assert.Equal("musixmatch", ranked.Best!.ProviderId);
        Assert.StartsWith("won on tier", ranked.Best.TieBreak, StringComparison.Ordinal);
        Assert.StartsWith("lost to musixmatch on tier", k.TieBreak, StringComparison.Ordinal);
    }

    [Fact]
    public void A_search_match_below_medium_is_never_tier_promoted()
    {
        var reference = LineDoc("spotify", RealSong);
        var spotify = new Lyrics.Candidate("spotify", 0.5, Lyrics.MatchBasis.Identity, reference);
        var weak = new Lyrics.Candidate("kugou", 0.5, Lyrics.MatchBasis.MetadataSearch, WordDoc("kugou", 2500, RealSong))
        { Band = Lyrics.MatchBand.Low, Confidence = 0.4 };

        var ranked = Lyrics.Reranker.Rank([weak, spotify], reference);

        Assert.False(For(ranked, "kugou").Verified);
        Assert.Equal("spotify", ranked.Best!.ProviderId);
    }

    [Fact]
    public void The_consensus_reference_never_verifies_itself()
    {
        // Alone: v1 compared it to itself (text 1, timing 1) and verified it — a wrong song's karaoke promoting itself.
        var lone = new Lyrics.Candidate("kugou", 0.5, Lyrics.MatchBasis.MetadataSearch, WordDoc("kugou", 2500, WrongSong));
        var alone = Lyrics.Reranker.Rank([lone], lone.Document);
        Assert.False(alone.Best!.Verified);
        Assert.StartsWith("consensus-ref without an agreeing peer", alone.Best.Reason, StringComparison.Ordinal);
        Assert.Equal(0, alone.Best.Support);

        // With an agreeing peer from another provider it is measured against THAT peer and verified by the support.
        var kugou = new Lyrics.Candidate("kugou", 0.5, Lyrics.MatchBasis.MetadataSearch, WordDoc("kugou", 2500, RealSong));
        var lrclib = new Lyrics.Candidate("lrclib", 0.4, Lyrics.MatchBasis.MetadataSearch, LineDoc("lrclib", RealSong));
        Lyrics.Candidate[] cs = [lrclib, kugou];
        var reference = Lyrics.Reranker.ConsensusReference(cs, out _, out int idx);
        Assert.Equal("kugou", cs[idx].ProviderId);

        var ranked = Lyrics.Reranker.Rank(cs, reference);
        var self = For(ranked, "kugou");
        Assert.StartsWith("consensus-ref vs lrclib", self.Reason, StringComparison.Ordinal);
        Assert.Equal(1, self.Support);
        Assert.True(self.Verified);
    }

    // ── §7 deterministic order ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_tie_is_decided_the_same_way_whatever_order_the_sources_arrived_in()
    {
        var reference = LineDoc("spotify", RealSong);
        Lyrics.Candidate[] cs =
        [
            new("spotify", 0.5, Lyrics.MatchBasis.Identity, reference),
            new("musixmatch", 0.9, Lyrics.MatchBasis.Isrc, WordDoc("musixmatch", 2500, RealSong)),
            new("amll", 0.9, Lyrics.MatchBasis.Identity, WordDoc("amll", 2500, RealSong)),
        ];

        foreach (var order in Permutations(cs))
        {
            var ranked = Lyrics.Reranker.Rank(order, reference);
            Assert.Equal("amll", ranked.Best!.ProviderId);
            Assert.Equal("won on provider id over musixmatch", ranked.Best.TieBreak);
            Assert.Equal("lost to amll on provider id", For(ranked, "musixmatch").TieBreak);
        }
    }

    [Fact]
    public void Match_confidence_scales_the_prior_and_breaks_an_otherwise_exact_tie()
    {
        var reference = LineDoc("spotify", RealSong);
        Lyrics.Candidate[] cs =
        [
            new("aaa", 0.5, Lyrics.MatchBasis.MetadataSearch, LineDoc("aaa", RealSong)) { Band = Lyrics.MatchBand.Medium, Confidence = 0.6 },
            new("zzz", 0.5, Lyrics.MatchBasis.MetadataSearch, LineDoc("zzz", RealSong)) { Band = Lyrics.MatchBand.Perfect, Confidence = 1.0 },
        ];

        foreach (var order in Permutations(cs))
        {
            var ranked = Lyrics.Reranker.Rank(order, reference);
            Assert.Equal("zzz", ranked.Best!.ProviderId);
            Assert.StartsWith("won on score", ranked.Best.TieBreak, StringComparison.Ordinal);
        }
    }

    // ── correction 17: gold ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gold_needs_trust_word_sync_clean_timing_and_agreement_with_the_reference()
    {
        var reference = LineDoc("spotify", RealSong);
        var amll = new Lyrics.Candidate("amll", 0.9, Lyrics.MatchBasis.Identity, WordDoc("amll", 2500, RealSong));

        Assert.True(Lyrics.Reranker.GoldAllowed(amll, null, 0));
        Assert.True(Lyrics.Reranker.GoldAllowed(amll, reference, Lyrics.Reranker.TextAgreement(amll.Document, reference)));
        Assert.False(Lyrics.Reranker.GoldAllowed(amll, reference, 0.3));

        // Never on basis alone: a search match, a line document, a decoy, impossible timing, a document past the track.
        var search = amll with { Basis = Lyrics.MatchBasis.MetadataSearch };
        Assert.False(Lyrics.Reranker.GoldAllowed(search, null, 1));
        var line = new Lyrics.Candidate("musixmatch", 0.7, Lyrics.MatchBasis.Isrc, LineDoc("musixmatch", RealSong));
        Assert.False(Lyrics.Reranker.GoldAllowed(line, null, 1));
        var decoy = new Lyrics.Candidate("musixmatch", 0.7, Lyrics.MatchBasis.Isrc, WordDoc("musixmatch", 2500, WrongSong));
        Assert.False(Lyrics.Reranker.GoldAllowed(decoy, reference, Lyrics.Reranker.TextAgreement(decoy.Document, reference)));
        var burst = new Lyrics.Candidate("musixmatch", 0.7, Lyrics.MatchBasis.Isrc, WordDoc("musixmatch", 150, RealSong));
        Assert.False(Lyrics.Reranker.GoldAllowed(burst, null, 1));
        Assert.False(Lyrics.Reranker.GoldAllowed(amll, null, 1, durationMs: 10_000));
        Assert.True(Lyrics.Reranker.GoldAllowed(amll, null, 1, durationMs: 200_000));
    }

    // ── word-timing gates ───────────────────────────────────────────────────────────────────────────────────────────

    static Lyrics.Doc OffClockWords()
    {
        // Line starts right, words on another clock: every word lands 5 s after its line (which ends at +2.5 s).
        var l = new List<Lyrics.Line>(RealSong.Length);
        foreach (var (ms, text) in RealSong)
            l.Add(new Lyrics.Line(ms, text, [new Lyrics.Syllable(ms + 5000, ms + 7500, text)], ms + 2500, null, null, true));
        return new Lyrics.Doc("t1", true, l, Lyrics.SyncKind.Syllable, "amll");
    }

    [Fact]
    public void Incoherent_word_timing_is_detected()
    {
        Assert.True(Lyrics.Timing.HasIncoherentWordTiming(OffClockWords(), out int incoherent, out int judged));
        Assert.Equal(6, judged);
        Assert.Equal(6, incoherent);
        Assert.False(Lyrics.Timing.HasIncoherentWordTiming(WordDoc("amll", 2500, RealSong), out _, out _));

        // Words that run backwards inside a line.
        var back = new Lyrics.Line(1000, "a b", [new Lyrics.Syllable(1500, 1800, "a"), new Lyrics.Syllable(1100, 1400, "b")], 2000);
        Assert.True(Lyrics.Timing.IsIncoherentLine(back));
        Assert.Contains("VERDICT", Lyrics.Timing.Describe(OffClockWords()), StringComparison.Ordinal);
    }

    [Fact]
    public void Incoherent_word_timing_is_demoted_to_the_line_tier_and_stripped_even_when_trusted()
    {
        var reference = LineDoc("spotify", RealSong);
        var amll = new Lyrics.Candidate("amll", 0.9, Lyrics.MatchBasis.Identity, OffClockWords());

        var ranked = Lyrics.Reranker.Rank([amll], reference);

        Assert.Contains("lines incoherent", ranked.Best!.Reason, StringComparison.Ordinal);
        Assert.Equal(0.6, ranked.Best.SyncScore, 3);
        Assert.Equal(Lyrics.SyncKind.Line, ranked.Winner!.Sync);
        for (int i = 0; i < RealSong.Length; i++)
        {
            Assert.Empty(ranked.Winner.Lines[i].Syllables);
            Assert.Equal(RealSong[i].Ms, ranked.Winner.Lines[i].StartMs);
        }
    }

    [Theory]
    [InlineData("我们一起走过的路", 8)]
    [InlineData("我爱你，你爱我", 6)]        // the full-width comma between characters is not a unit
    [InlineData("我爱你 baby", 4)]
    [InlineData("ラララ 歌おう", 6)]
    [InlineData("one two three", 3)]
    [InlineData("Ooh, ah", 2)]
    [InlineData("a - b", 3)]                // no CJK: counted exactly as before
    public void Cjk_characters_count_as_units(string text, int expected)
        => Assert.Equal(expected, Lyrics.Timing.WordCount(text));

    static Lyrics.Line Timed(string text, long spanMs)
        => new(0, text, [new Lyrics.Syllable(0, spanMs, text)], spanMs, null, null, true);

    [Fact]
    public void The_rate_limit_is_higher_for_cjk_dominant_lines()
    {
        // 8 units in 800 ms = 10/s: impossible for English words (limit 8), singable for CJK characters (limit 12).
        Assert.True(Lyrics.Timing.IsImpossiblyFast(Timed("one two three four five six seven eight", 800)));
        Assert.False(Lyrics.Timing.IsImpossiblyFast(Timed("我们一起走过的路", 800)));
        // 8 units in 500 ms = 16/s: impossible either way.
        Assert.True(Lyrics.Timing.IsImpossiblyFast(Timed("我们一起走过的路", 500)));
        Assert.Equal(Lyrics.Timing.ImpossibleCjkUnitsPerSecond, Lyrics.Timing.UnitsPerSecondLimit(8, 8));
        Assert.Equal(Lyrics.Timing.ImpossibleWordsPerSecond, Lyrics.Timing.UnitsPerSecondLimit(8, 0));
    }
}
