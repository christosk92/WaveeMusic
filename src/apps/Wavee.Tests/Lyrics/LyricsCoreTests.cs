using System.Collections.Generic;
using System.Linq;
using Wavee.Backend.Lyrics;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests.Lyrics;

// Pure-core tests for the lyrics feed: parsers (LRC/TTML), normalization, and the reranker decision engine.
// No network — the network sources are exercised separately with a fake ILyricHttp.
public class LyricsCoreTests
{
    // ── parsers ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Lrc_LineSynced_DropsMetadata_AndParsesTimes()
    {
        const string lrc = "[ti:Song]\n[ar:Artist]\n[by:someone]\n[00:01.50]First line\n[00:05.00]Second line\n";
        var doc = LyricsText.ParseLrc(lrc, "t1");

        Assert.True(doc.IsSynced);
        Assert.Equal(LyricsSyncKind.Line, doc.Sync);
        Assert.Equal(2, doc.Lines.Count);
        Assert.Equal(1500, doc.Lines[0].StartMs);
        Assert.Equal("First line", doc.Lines[0].Text);
        Assert.Equal(5000, doc.Lines[1].StartMs);
        Assert.Equal(5000, doc.Lines[0].EndMs);   // derived from next line start
    }

    [Fact]
    public void Lrc_Enhanced_ProducesSyllables()
    {
        const string lrc = "[00:00.00]<00:00.10>Hel<00:00.50>lo <00:00.90>world\n";
        var doc = LyricsText.ParseLrc(lrc, "t1");

        Assert.Equal(LyricsSyncKind.Syllable, doc.Sync);
        var line = doc.Lines[0];
        Assert.True(line.IsWordByWord);
        Assert.Equal("Hello world", line.Text);
        Assert.Equal(3, line.Syllables.Count);
        Assert.Equal(100, line.Syllables[0].StartMs);
        Assert.Equal(500, line.Syllables[0].EndMs);   // next syllable start
        Assert.Equal("world", line.Syllables[2].Text.Trim());
    }

    [Fact]
    public void Lrc_Offset_ShiftsTimestampsEarlier()
    {
        const string lrc = "[offset:500]\n[00:02.00]Line\n";
        var doc = LyricsText.ParseLrc(lrc, "t1");
        Assert.Equal(1500, doc.Lines[0].StartMs);   // 2000 - 500
    }

    [Fact]
    public void Ttml_WordSynced_ParsesSpans()
    {
        const string ttml =
            "<tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:ttm=\"http://www.w3.org/ns/ttml#metadata\">" +
            "<body><div>" +
            "<p begin=\"0:01.000\" end=\"0:03.000\"><span begin=\"0:01.000\" end=\"0:02.000\">Hello</span> <span begin=\"0:02.000\" end=\"0:03.000\">world</span></p>" +
            "<p begin=\"0:03.000\" end=\"0:05.000\">Second line</p>" +
            "</div></body></tt>";
        var doc = LyricsText.ParseTtml(ttml, "t1");

        Assert.Equal(LyricsSyncKind.Syllable, doc.Sync);
        Assert.Equal(2, doc.Lines.Count);
        Assert.Equal(1000, doc.Lines[0].StartMs);
        Assert.Equal(3000, doc.Lines[0].EndMs);
        Assert.True(doc.Lines[0].IsWordByWord);
        Assert.Equal(2, doc.Lines[0].Syllables.Count);
        Assert.Equal("Hello world", doc.Lines[0].Text);
        Assert.False(doc.Lines[1].IsWordByWord);   // plain <p>, no spans
    }

    [Fact]
    public void Credit_Lines_Detected_And_Normalize_Strips()
    {
        Assert.True(LyricsText.IsCreditLine("Lyrics by: Some Writer"));
        Assert.True(LyricsText.IsCreditLine("作词 : 林夕"));
        Assert.False(LyricsText.IsCreditLine("I've written you a love song"));   // 'written' but no separator → real lyric
        Assert.Equal("hello world", LyricsText.Normalize("Hello, World!"));
    }

    // ── reranker ─────────────────────────────────────────────────────────────────────────────────────────────────────

    static LyricSyllable[] Words(params (long s, long e, string t)[] w)
        => w.Select(x => new LyricSyllable(x.s, x.e, x.t)).ToArray();

    static LyricsDocument LineDoc(string provider, params (long ms, string text)[] lines)
        => new("t1", true, lines.Select(l => new LyricLine(l.ms, l.text, System.Array.Empty<LyricSyllable>())).ToList(),
            LyricsSyncKind.Line, provider);

    static LyricsDocument WordDoc(string provider, params (long ms, string text)[] lines)
        => new("t1", true, lines.Select(l => new LyricLine(l.ms, l.text,
            Words((l.ms, l.ms + 400, l.text)), l.ms + 800, IsWordByWord: true)).ToList(),
            LyricsSyncKind.Syllable, provider);

    static LyricsCandidate Cand(string id, double prior, MatchBasis basis, LyricsDocument doc)
        => new(id, prior, basis, doc);

    static readonly (long, string)[] RealSong =
    {
        (1000, "hello darkness my old friend"),
        (5000, "ive come to talk with you again"),
        (9000, "because a vision softly creeping"),
        (13000, "left its seeds while i was sleeping"),
    };

    static LyricsDocument Reference() => LineDoc("spotify", RealSong);

    [Fact]
    public void Reranker_WrongSongWordSynced_LosesToCorrectLine()
    {
        var reference = Reference();
        // A grey (metadata-searched) source returned a WRONG song's karaoke; the sync gate must demote it so the correct
        // line lyric wins. (Identity/ISRC sources are exempt — see Reranker_IdentityWordSync_ExemptFromGate.)
        var wrong = Cand("netease", 1.0, MatchBasis.MetadataSearch, WordDoc("netease",
            (1000, "never gonna give you up"), (4000, "never gonna let you down"), (7000, "never gonna run around")));
        var correct = Cand("lrclib", 0.4, MatchBasis.MetadataSearch, LineDoc("lrclib", RealSong));

        var ranked = LyricsReranker.Rank(new[] { wrong, correct }, reference);

        Assert.NotNull(ranked.Winner);
        Assert.Equal("lrclib", ranked.Best!.ProviderId);   // wrong-song syllable demoted by the sync gate (text below the floor)
    }

    [Fact]
    public void Reranker_LooseGate_AcceptsDivergentWordSync()
    {
        var reference = Reference();
        // SAME song, word-synced, but only partial text agreement (a romanized / differently-transcribed grey candidate):
        // 2 of 4 lines match → text ~0.5. The old 0.80 bar demoted this; the 0.15 floor keeps its sync tier.
        var syl = Cand("netease", 0.5, MatchBasis.MetadataSearch, WordDoc("netease",
            (1000, "hello darkness my old friend"), (5000, "ive come to talk with you again"),
            (9000, "totally unrelated filler line"), (13000, "another unrelated filler line")));

        var d = LyricsReranker.Rank(new[] { syl }, reference).All.Single(x => x.ProviderId == "netease");
        Assert.DoesNotContain("sync-gate:demoted", d.Reason);
    }

    [Fact]
    public void Reranker_LooseGate_StillDemotesWrongWordSync()
    {
        var reference = Reference();
        // Different song entirely → text ≈ 0 (below the floor) → demoted even though it is word-synced.
        var wrong = Cand("netease", 0.5, MatchBasis.MetadataSearch, WordDoc("netease",
            (1000, "never gonna give you up"), (4000, "never gonna let you down"), (7000, "never gonna run around")));

        var d = LyricsReranker.Rank(new[] { wrong }, reference).All.Single(x => x.ProviderId == "netease");
        Assert.Contains("sync-gate:demoted", d.Reason);
    }

    [Fact]
    public void Reranker_IdentityWordSync_ExemptFromGate()
    {
        var reference = Reference();
        // AMLL is identity-matched (the exact Spotify track) but its transcription diverges from Spotify's line lyric
        // (text ≈ 0). It must NOT be demoted — it IS the recording. (ISRC-matched Musixmatch is exempt the same way.)
        var amll = Cand("amll", 0.9, MatchBasis.Identity, WordDoc("amll",
            (1000, "totally different transcription aaa"), (5000, "totally different transcription bbb"),
            (9000, "totally different transcription ccc"), (13000, "totally different transcription ddd")));

        var d = LyricsReranker.Rank(new[] { amll }, reference).All.Single(x => x.ProviderId == "amll");
        Assert.DoesNotContain("sync-gate:demoted", d.Reason);
    }

    [Fact]
    public void Reranker_GloballyOffsetLrc_IsCorrectedAgainstReference()
    {
        var reference = Reference();
        // same song, every line +700ms late
        var offsetCand = Cand("lrclib", 0.4, MatchBasis.MetadataSearch,
            LineDoc("lrclib", RealSong.Select(l => (l.Item1 + 700, l.Item2)).ToArray()));

        var ranked = LyricsReranker.Rank(new[] { offsetCand }, reference);

        Assert.Equal(-700, ranked.Best!.AppliedOffsetMs);
        Assert.Equal(1000, ranked.Winner!.Lines[0].StartMs);   // pulled back onto the reference
        Assert.Equal(-700, ranked.Winner.OffsetMsApplied);
    }

    [Fact]
    public void Reranker_TimingFallback_TrustedDivergentText_IsCorrected()
    {
        var reference = Reference();
        var divergent = Cand("musixmatch", 0.9, MatchBasis.Isrc, WordDoc("musixmatch",
            (1500, "totally different transcription aaa"),
            (5500, "totally different transcription bbb"),
            (9500, "totally different transcription ccc"),
            (13500, "totally different transcription ddd")));

        var ranked = LyricsReranker.Rank(new[] { divergent }, reference);

        Assert.Equal(-500, ranked.Best!.AppliedOffsetMs);
        Assert.Contains("timing-fallback", ranked.Best.Reason);
        Assert.Equal(1000, ranked.Winner!.Lines[0].StartMs);
        Assert.Equal(1000, ranked.Winner.Lines[0].Syllables[0].StartMs);
        Assert.Equal(1400, ranked.Winner.Lines[0].Syllables[0].EndMs);
        Assert.Equal(-500, ranked.Winner.OffsetMsApplied);
    }

    [Fact]
    public void Reranker_CorrectWordSynced_BeatsSpotifyLine()
    {
        var reference = Reference();
        var amll = Cand("amll", 1.0, MatchBasis.Identity, WordDoc("amll", RealSong));   // same text + coherent timing
        var spotify = Cand("spotify", 0.5, MatchBasis.Identity, reference);

        var ranked = LyricsReranker.Rank(new[] { spotify, amll }, reference);

        Assert.Equal("amll", ranked.Best!.ProviderId);   // verified word-sync wins the sync tier
        Assert.Equal(LyricsSyncKind.Syllable, ranked.Winner!.Sync);
    }

    // ── word-timing plausibility gate ────────────────────────────────────────────────────────────────────────────────
    // A word-synced body can be STRUCTURALLY perfect — monotonic, every syllable inside its line, line starts that align
    // 1:1 with the reference — and still be unusable: the words all fire in the first fraction of a second and the line
    // then sits dead. Musixmatch richsync did exactly that for "Caribbean Queen" (12 words inside 167ms). The reranker
    // scored it 0.95 on perfect line-start alignment and it beat Spotify's clean line lyric, because nothing looked at
    // line DURATION. The gate is physical (words/second), not comparative, so it needs no reference document.

    static readonly (long, string)[] SixLineSong =
    {
        (1000, "hello darkness my old friend"),
        (5000, "ive come to talk with you again"),
        (9000, "because a vision softly creeping"),
        (13000, "left its seeds while i was sleeping"),
        (17000, "and the vision that was planted"),
        (21000, "in my brain still remains"),
    };

    /// <summary>Word-synced, but every line's words are crammed into <paramref name="spanMs"/> — the shape of the bug.</summary>
    static LyricsDocument BurstWordDoc(string provider, long spanMs, params (long ms, string text)[] lines)
        => new("t1", true, lines.Select(l => new LyricLine(l.ms, l.text,
            Words((l.ms, l.ms + spanMs, l.text)), l.ms + spanMs, IsWordByWord: true)).ToList(),
            LyricsSyncKind.Syllable, provider);

    [Fact]
    public void Reranker_ImpossiblyFastWordSync_IsGatedAndStrippedToLineSync()
    {
        var reference = LineDoc("spotify", SixLineSong);
        // ISRC-matched, so the ORIGINAL sync gate exempts it as "the exact recording" — the word-timing gate must not.
        var burst = Cand("musixmatch", 0.9, MatchBasis.Isrc, BurstWordDoc("musixmatch", 150, SixLineSong));

        var ranked = LyricsReranker.Rank(new[] { burst }, reference);

        Assert.Contains("word-timing-gate", ranked.Best!.Reason);
        // Repaired, not merely demoted: the view must never wipe over timings this document does not really have.
        Assert.Equal(LyricsSyncKind.Line, ranked.Winner!.Sync);
        Assert.All(ranked.Winner.Lines, l => Assert.Empty(l.Syllables));
        Assert.All(ranked.Winner.Lines, l => Assert.False(l.IsWordByWord));
        // …while the part the payload got RIGHT survives untouched.
        Assert.Equal(SixLineSong.Select(l => l.Item1), ranked.Winner.Lines.Select(l => l.StartMs));
        Assert.Equal(SixLineSong.Select(l => l.Item2), ranked.Winner.Lines.Select(l => l.Text));
    }

    [Fact]
    public void Reranker_PlausibleWordSync_IsUntouched()
    {
        var reference = LineDoc("spotify", SixLineSong);
        var real = Cand("amll", 0.9, MatchBasis.Identity, BurstWordDoc("amll", 2500, SixLineSong));   // ~2 words/second

        var ranked = LyricsReranker.Rank(new[] { real }, reference);

        Assert.DoesNotContain("word-timing-gate", ranked.Best!.Reason);
        Assert.Equal(LyricsSyncKind.Syllable, ranked.Winner!.Sync);
        Assert.NotEmpty(ranked.Winner.Lines[0].Syllables);
    }

    // ── verified word-sync outranks a line lyric (the Kugou case) ────────────────────────────────────────────────────
    // The live failure, with its real numbers: on "Caribbean Queen", Musixmatch's LINE lyric scored 0.885 (a perfect
    // 62/62 text match) while a genuinely word-synced Kugou KRC scored ~0.74 — so the karaoke lost to the paragraph.
    // Sync is weighted 0.25, making syllable-over-line worth only +0.10, which any text-agreement edge erases. Trust and
    // quality are different judgements; the reranker now answers them separately.

    /// <summary>Word-synced with realistic ~2 words/second timing — passes the plausibility gate.</summary>
    static LyricsDocument RealWordDoc(string provider, params (long ms, string text)[] lines)
        => new("t1", true, lines.Select(l => new LyricLine(l.ms, l.text,
            Words((l.ms, l.ms + 2500, l.text)), l.ms + 2500, IsWordByWord: true)).ToList(),
            LyricsSyncKind.Syllable, provider);

    [Fact]
    public void Reranker_VerifiedWordSync_BeatsAHigherScoringLineLyric()
    {
        var reference = LineDoc("spotify", SixLineSong);
        // Musixmatch: the reference text verbatim, ISRC-matched, top prior → the highest SCORE of the two.
        var line = Cand("musixmatch", 0.7, MatchBasis.Isrc, LineDoc("musixmatch", SixLineSong));
        // Kugou: really word-synced, but a fuzzy metadata match whose transcription differs on two lines → lower score.
        var word = Cand("kugou", 0.5, MatchBasis.MetadataSearch, RealWordDoc("kugou",
            (1000, "hello darkness my old friend"),
            (5000, "ive come to talk with you again"),
            (9000, "because a vision softly creeping"),
            (13000, "left its seeds while i was sleeping"),
            (17000, "a differently transcribed line here"),
            (21000, "another differently transcribed line")));

        var ranked = LyricsReranker.Rank(new[] { line, word }, reference);

        var lineDec = ranked.All.Single(d => d.ProviderId == "musixmatch");
        var wordDec = ranked.All.Single(d => d.ProviderId == "kugou");
        Assert.True(lineDec.Score > wordDec.Score, "the line lyric should still SCORE higher — that is the whole point");
        Assert.Equal("kugou", ranked.Best!.ProviderId);            // …and still lose, on tier
        Assert.Equal(LyricsSyncKind.Syllable, ranked.Winner!.Sync);
        Assert.NotEmpty(ranked.Winner.Lines[0].Syllables);
    }

    [Fact]
    public void Reranker_WeaklyCorroboratedWordSync_IsNotPromotedOnTier()
    {
        var reference = LineDoc("spotify", SixLineSong);
        var spotify = Cand("spotify", 0.55, MatchBasis.Identity, reference);
        // Same line starts (so timing aligns) but only ONE line of six shares text — a romanized or mis-matched
        // transcription. It clears the loose sync gate, which is why that bar must NOT be the one governing promotion:
        // it used to score ~0.51 against Spotify's 0.877 and lose, and it must keep losing.
        var weak = Cand("netease", 0.5, MatchBasis.MetadataSearch, RealWordDoc("netease",
            (1000, "hello darkness my old friend"),
            (5000, "aaaa bbbb cccc dddd eeee"),
            (9000, "ffff gggg hhhh iiii jjjj"),
            (13000, "kkkk llll mmmm nnnn oooo"),
            (17000, "pppp qqqq rrrr ssss tttt"),
            (21000, "uuuu vvvv wwww xxxx yyyy")));

        var ranked = LyricsReranker.Rank(new[] { weak, spotify }, reference);

        Assert.Equal("spotify", ranked.Best!.ProviderId);
        Assert.Equal(LyricsSyncKind.Line, ranked.Winner!.Sync);
    }

    [Fact]
    public void Reranker_GatedWordSync_CannotClaimTheTierItDoesNotDeliver()
    {
        var reference = LineDoc("spotify", SixLineSong);
        // The real shape: Musixmatch ISRC richsync with fabricated word timing, against Spotify's clean line lyric.
        var burst = Cand("musixmatch", 0.7, MatchBasis.Isrc, BurstWordDoc("musixmatch", 150, SixLineSong));
        var spotify = Cand("spotify", 0.55, MatchBasis.Identity, reference);

        var ranked = LyricsReranker.Rank(new[] { burst, spotify }, reference);

        // Whoever wins, the user must not get a karaoke wipe over invented timings.
        Assert.Equal(LyricsSyncKind.Line, ranked.Winner!.Sync);
        Assert.All(ranked.Winner.Lines, l => Assert.Empty(l.Syllables));
    }

    [Fact]
    public void WordTimingGate_NeedsEnoughLinesToJudge()
    {
        // Three squashed lines is a fragment, not evidence — a short document must not be able to condemn a provider.
        var doc = BurstWordDoc("musixmatch", 150,
            (1000, "hello darkness my old friend"),
            (5000, "ive come to talk with you again"),
            (9000, "because a vision softly creeping"));

        Assert.False(LyricsTiming.HasImplausibleWordTiming(doc, out _, out int judged));
        Assert.Equal(3, judged);
    }

    [Fact]
    public void WordTimingGate_IgnoresShortLines()
    {
        // "Ooh, ah" legitimately lands inside a few hundred ms; a one- or two-word line's rate says nothing.
        var doc = BurstWordDoc("musixmatch", 150,
            (1000, "ooh ah"), (5000, "yeah"), (9000, "oh"), (13000, "mm hm"), (17000, "ah"), (21000, "ooh"));

        Assert.False(LyricsTiming.HasImplausibleWordTiming(doc, out _, out int judged));
        Assert.Equal(0, judged);
    }

    /// <summary>A Musixmatch richsync body with <paramref name="spanSeconds"/> per line, offsets spread evenly across it
    /// — the real payload shape, end to end through the real parser.</summary>
    static string Richsync(double spanSeconds, int lineCount)
    {
        string[] words = { "and", "all", "heads", "turned", "cause", "she", "was" };
        var sb = new System.Text.StringBuilder("[");
        for (int i = 0; i < lineCount; i++)
        {
            double ts = 10d + i * 8d;
            if (i > 0) sb.Append(',');
            sb.Append("{\"ts\":").Append(Num(ts)).Append(",\"te\":").Append(Num(ts + spanSeconds))
              .Append(",\"x\":\"").Append(string.Join(' ', words)).Append("\",\"l\":[");
            for (int w = 0; w < words.Length; w++)
            {
                if (w > 0) sb.Append(',');
                sb.Append("{\"c\":\"").Append(words[w]).Append(" \",\"o\":")
                  .Append(Num(spanSeconds * w / words.Length)).Append('}');
            }
            sb.Append("]}");
        }
        return sb.Append(']').ToString();

        static string Num(double v) => v.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void Richsync_CompressedOffsets_AreDetectedEndToEnd()
    {
        var doc = LyricsWordFormats.ParseRichsync(Richsync(0.349, 6), "t1");

        Assert.Equal(LyricsSyncKind.Syllable, doc.Sync);          // it parses, and looks fine structurally…
        Assert.Equal(6, doc.Lines.Count);
        Assert.True(LyricsTiming.HasImplausibleWordTiming(doc, out int impossible, out int judged));
        Assert.Equal(6, impossible);                               // …but no one sings 7 words in 349ms
        Assert.Equal(6, judged);
    }

    [Fact]
    public void Richsync_RealisticOffsets_PassTheGate()
    {
        var doc = LyricsWordFormats.ParseRichsync(Richsync(3.5, 6), "t1");

        Assert.Equal(LyricsSyncKind.Syllable, doc.Sync);
        Assert.False(LyricsTiming.HasImplausibleWordTiming(doc, out int impossible, out _));
        Assert.Equal(0, impossible);
    }

    [Fact]
    public void Reranker_NoCandidates_ReturnsNull()
    {
        var ranked = LyricsReranker.Rank(System.Array.Empty<LyricsCandidate>(), null);
        Assert.Null(ranked.Winner);
    }
}
