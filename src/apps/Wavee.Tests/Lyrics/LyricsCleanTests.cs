using System;
using System.IO;
using System.Linq;
using Wavee.Backend.Lyrics;
using Wavee.Backend.Lyrics.Sources;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests.Lyrics;

// LyricsClean, driven by the REAL captured payloads under Fixtures/. Providers pad their documents with rows that are
// not lyrics — blanks, ♪ instrumental markers, credits, and the Kugou/QQ "Title - Artist" header — and those rows both
// render as junk and inflate the line COUNT the reranker's `coverage` divides by.
public class LyricsCleanTests
{
    const string TrackId = "4JEylZNW8SbO4zUyfVrpb7";
    const string Title = "Caribbean Queen (No More Love On the Run)";
    const string Artist = "Billy Ocean";

    static string Fixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Lyrics", "Fixtures", name));

    static LyricsDocument Spotify() => SpotifyNativeLyricsSource.Parse(
        Fixture("spotify-colorlyrics-caribbean-queen.json"), TrackId)!;
    static LyricsDocument MusixmatchLrc() => LyricsText.ParseLrc(
        Fixture("musixmatch-subtitle-caribbean-queen.lrc"), TrackId, "musixmatch");
    static LyricsDocument KugouKrc() => LyricsWordFormats.ParseKrc(
        Fixture("kugou-krc-caribbean-queen.krc"), TrackId);

    // ── the three families, on real data ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Spotify_LosesItsMusicNotesAndBlanks()
    {
        var raw = Spotify();
        Assert.Equal(62, raw.Lines.Count);
        Assert.Equal(23, raw.Lines.Count(l => LyricsClean.IsSymbolOnly(l.Text)));   // 18 ♪ + 5 blank

        var clean = LyricsClean.Apply(raw, Title, Artist);

        Assert.Equal(39, clean.Lines.Count);
        Assert.DoesNotContain(clean.Lines, l => LyricsClean.IsSymbolOnly(l.Text));
        Assert.Equal("She's simply awesome", clean.Lines[0].Text);
    }

    [Fact]
    public void MusixmatchLrc_LosesItsBlanks()
    {
        var clean = LyricsClean.Apply(MusixmatchLrc(), Title, Artist);

        Assert.Equal(39, clean.Lines.Count);
        Assert.DoesNotContain(clean.Lines, l => LyricsClean.IsSymbolOnly(l.Text));
    }

    [Fact]
    public void KugouKrc_LosesItsTitleHeader()
    {
        var raw = KugouKrc();
        Assert.StartsWith("Caribbean Queen", raw.Lines[0].Text, StringComparison.Ordinal);
        Assert.Contains(" - ", raw.Lines[0].Text, StringComparison.Ordinal);

        var clean = LyricsClean.Apply(raw, Title, Artist);

        Assert.Equal(raw.Lines.Count - 1, clean.Lines.Count);
        Assert.Equal("She's simply awesome", clean.Lines[0].Text);
    }

    // ── the timing a dropped row carries must survive it ─────────────────────────────────────────────────────────────

    [Fact]
    public void ADroppedMarkerBecomesThePrecedingLinesEnd()
    {
        // Spotify: "She dashed by me…" at 19480, ♪ at 22300, "And all heads turned…" at 27980. Dropping the ♪ without
        // folding its timestamp in would stretch the lyric across the whole 8.5s instrumental — and erase the gap the
        // interlude dots are detected from.
        var clean = LyricsClean.Apply(Spotify(), Title, Artist);
        var line = clean.Lines.Single(l => l.Text.StartsWith("She dashed by me", StringComparison.Ordinal));

        Assert.Equal(22300, line.EndMs);
    }

    [Fact]
    public void AnAuthoredEndIsNeverOverwritten()
    {
        // Word-synced lines already know when they stop being sung; a following marker must not move that.
        var doc = new LyricsDocument(TrackId, true,
        [
            new LyricLine(1000, "real lyric here now", [new LyricSyllable(1000, 2000, "real lyric here now")], 2000, IsWordByWord: true),
            new LyricLine(5000, "♪", Array.Empty<LyricSyllable>()),
            new LyricLine(9000, "another real lyric", [new LyricSyllable(9000, 10000, "another real lyric")], 10000, IsWordByWord: true),
        ], LyricsSyncKind.Syllable, "kugou");

        var clean = LyricsClean.Apply(doc);

        Assert.Equal(2, clean.Lines.Count);
        Assert.Equal(2000, clean.Lines[0].EndMs);   // NOT 5000
    }

    // ── the negatives: what must NOT be eaten ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AChorusLineThatIsTheSongsTitle_Survives()
    {
        // "Caribbean Queen" is sung repeatedly. Only the LEADING line can be a header, so the chorus is untouched.
        var clean = LyricsClean.Apply(KugouKrc(), Title, Artist);
        Assert.Contains(clean.Lines, l => l.Text.Trim() == "Caribbean Queen");
    }

    [Fact]
    public void ALeadingTitleLineWithNoSeparatorAndNoGap_Survives()
    {
        // Same text as a header, but the singing starts immediately — that is an opening lyric, not a pre-roll.
        var doc = new LyricsDocument(TrackId, true,
        [
            new LyricLine(1000, "Caribbean Queen", Array.Empty<LyricSyllable>()),
            new LyricLine(2000, "no more love on the run", Array.Empty<LyricSyllable>()),
        ], LyricsSyncKind.Line, "lrclib");

        Assert.Equal(2, LyricsClean.Apply(doc, Title, Artist).Lines.Count);
    }

    [Fact]
    public void AMidSongCreditLikeLine_Survives()
    {
        // The credit sweep only walks the leading and trailing runs, so a lyric that happens to read like a credit
        // cannot be eaten from the middle of a song.
        var doc = new LyricsDocument(TrackId, true,
        [
            new LyricLine(1000, "first real lyric line", Array.Empty<LyricSyllable>()),
            new LyricLine(2000, "this song was written by: my heart", Array.Empty<LyricSyllable>()),
            new LyricLine(3000, "last real lyric line", Array.Empty<LyricSyllable>()),
        ], LyricsSyncKind.Line, "lrclib");

        Assert.Equal(3, LyricsClean.Apply(doc).Lines.Count);
    }

    [Fact]
    public void LeadingAndTrailingCredits_AreDropped()
    {
        var doc = new LyricsDocument(TrackId, true,
        [
            new LyricLine(500, "作词 : Someone", Array.Empty<LyricSyllable>()),
            new LyricLine(800, "Composed by: Someone Else", Array.Empty<LyricSyllable>()),
            new LyricLine(2000, "the only real lyric", Array.Empty<LyricSyllable>()),
            new LyricLine(9000, "Mixed by: A Third Person", Array.Empty<LyricSyllable>()),
        ], LyricsSyncKind.Line, "kugou");

        var clean = LyricsClean.Apply(doc);

        Assert.Single(clean.Lines);
        Assert.Equal("the only real lyric", clean.Lines[0].Text);
    }

    [Fact]
    public void ADocumentThatIsEntirelyJunk_CleansToNothing()
    {
        // The caller (AggregatingLyricsProvider.FetchOne) turns this into a MISS rather than a zero-line "hit".
        var doc = new LyricsDocument(TrackId, true,
        [
            new LyricLine(1000, "♪", Array.Empty<LyricSyllable>()),
            new LyricLine(5000, "", Array.Empty<LyricSyllable>()),
            new LyricLine(9000, "...", Array.Empty<LyricSyllable>()),
        ], LyricsSyncKind.Line, "spotify");

        Assert.Empty(LyricsClean.Apply(doc).Lines);
    }

    [Fact]
    public void ACleanDocumentIsReturnedUNCHANGED()
    {
        // Identity, not a copy: the common case must not allocate a new document or disturb reference equality.
        var doc = LyricsClean.Apply(Spotify(), Title, Artist);
        Assert.Same(doc, LyricsClean.Apply(doc, Title, Artist));
    }

    // ── issue #16: credit headers ────────────────────────────────────────────────────────────────────────────────────

    static LyricLine Krc(long start, long end, string text, params (long s, long e, string t)[] syl)
        => new(start, text, syl.Select(x => new LyricSyllable(x.s, x.e, x.t)).ToArray(), end, IsWordByWord: true);

    static LyricLine Plain(long start, string text) => new(start, text, Array.Empty<LyricSyllable>());

    static LyricsDocument Doc(string provider, LyricsSyncKind sync, params LyricLine[] lines)
        => new(TrackId, true, lines, sync, provider);

    // The exact rows Kugou's KRC for "Close to Me" (ANTON, spotify:track:7MCeiN7GRhf6x2YV79uaFt) opens with, syllable
    // timing included, as persisted by the disk cache: three writer credits, then the first Korean lyric.
    static LyricsDocument CloseToMe() => Doc("kugou", LyricsSyncKind.Syllable,
        Krc(7416, 9503, "词：YOSKE (Balcony Swim)",
            (7416, 8232, "词：YOSKE ("), (8232, 8864, "Balcony "), (8864, 9503, "Swim)")),
        Krc(9503, 14849, "曲：YOSKE (Balcony Swim)/최수환 (Balcony Swim)/HOVE (Balcony Swim)",
            (9503, 10615, "曲：YOSKE ("), (10615, 11311, "Balcony "), (11311, 12343, "Swim)/최수환 ("), (12343, 13047, "Balcony "),
            (13047, 13703, "Swim)/HOVE ("), (13703, 14263, "Balcony "), (14263, 14849, "Swim)")),
        Krc(14849, 20418, "编曲：YOSKE (Balcony Swim)/최수환 (Balcony Swim)/HOVE (Balcony Swim)",
            (14849, 15511, "编曲：YOSKE ("), (15511, 16271, "Balcony "), (16271, 17063, "Swim)/최수환 ("), (17063, 18008, "Balcony "),
            (18008, 18696, "Swim)/HOVE ("), (18696, 19441, "Balcony "), (19441, 20418, "Swim)")),
        Krc(21088, 22025, "우연처럼", (21088, 22025, "우연처럼")),
        Krc(22025, 24000, "또 마주친 밤 태연한 척 웃지만", (22025, 22287, "또 "), (22287, 24000, "마주친 밤 태연한 척 웃지만")));

    [Fact]
    public void TheKugouCreditHeader_IsDroppedWhole_AndTheFirstLyricKeepsItsTiming()
    {
        var clean = LyricsClean.Apply(CloseToMe(), "Close to Me", "ANTON", out int credits);

        Assert.Equal(3, credits);
        Assert.Equal(2, clean.Lines.Count);
        Assert.DoesNotContain(clean.Lines, l => l.Text.Contains("YOSKE", StringComparison.Ordinal));
        // A syllable-synced row goes with its syllables; the kept rows are carried over untouched.
        Assert.Equal(LyricsSyncKind.Syllable, clean.Sync);
        Assert.Equal("우연처럼", clean.Lines[0].Text);
        Assert.Equal(21088, clean.Lines[0].StartMs);
        Assert.Equal(22025, clean.Lines[0].EndMs);
        Assert.Single(clean.Lines[0].Syllables);
        Assert.Equal(21088, clean.Lines[0].Syllables[0].StartMs);
        Assert.True(clean.Lines[0].IsWordByWord);
        Assert.Equal(2, clean.Lines[1].Syllables.Count);
    }

    [Fact]
    public void AJapaneseHeader_IsDropped()
    {
        var doc = Doc("netease", LyricsSyncKind.Line,
            Plain(0, "作詞：秋元康"),
            Plain(0, "作曲・編曲：Nao"),
            Plain(0, "歌：乃木坂46"),
            Plain(12000, "君の名前を呼ぶ"),
            Plain(15000, "夜の風の中で"));

        var clean = LyricsClean.Apply(doc, null, null, out int credits);

        Assert.Equal(3, credits);
        Assert.Equal(2, clean.Lines.Count);
        Assert.Equal(12000, clean.Lines[0].StartMs);
    }

    [Fact]
    public void AnEnglishHeader_IsDropped_WithAndWithoutColon()
    {
        var doc = Doc("lrclib", LyricsSyncKind.Line,
            Plain(0, "Lyrics by Someone"),
            Plain(0, "Composed by: Someone Else"),
            Plain(0, "Words and Music by: A Third Person & Another"),
            Plain(9000, "the first real lyric"),
            Plain(12000, "the second real lyric"));

        var clean = LyricsClean.Apply(doc, null, null, out int credits);

        Assert.Equal(3, credits);
        Assert.Equal("the first real lyric", clean.Lines[0].Text);
    }

    [Fact]
    public void ATrailingCopyrightRun_IsDropped()
    {
        var doc = Doc("qq", LyricsSyncKind.Line,
            Plain(1000, "the first real lyric"),
            Plain(4000, "the last real lyric"),
            Plain(200000, "℗ 2024 Some Label Ltd."),
            Plain(200000, "© 2024 Some Label Ltd."));

        var clean = LyricsClean.Apply(doc, null, null, out int credits);

        Assert.Equal(2, credits);
        Assert.Equal(2, clean.Lines.Count);
        Assert.Equal("the last real lyric", clean.Lines[^1].Text);
    }

    [Fact]
    public void AMidSongLineThatReadsLikeACredit_Survives_UnlessKnownKeyAndFullWidthColon()
    {
        // In the middle of a song only the unambiguous CJK form ("作曲：X") is trusted. Korean 작곡 as lyric text, and
        // even "작곡: …" with an ASCII colon, stay: the run rule, not the vocabulary, is what keeps the middle safe.
        var doc = Doc("kugou", LyricsSyncKind.Line,
            Plain(1000, "first real lyric line"),
            Plain(2000, "작곡 없이 부르는 노래"),
            Plain(3000, "작곡: 너와 나의 이야기"),
            Plain(4000, "作曲：Someone"),
            Plain(5000, "last real lyric line"));

        var clean = LyricsClean.Apply(doc, null, null, out int credits);

        Assert.Equal(1, credits);
        Assert.Equal(new[] { "first real lyric line", "작곡 없이 부르는 노래", "작곡: 너와 나의 이야기", "last real lyric line" },
            clean.Lines.Select(l => l.Text).ToArray());
    }

    [Fact]
    public void ADocumentThatIsOnlyCredits_KeepsItsLines()
    {
        // Credits alone never empty a document: if every line is credit-shaped, the shape is the document's idiom.
        var doc = Doc("netease", LyricsSyncKind.Line, Plain(0, "作词：A"), Plain(1000, "作曲：B"));

        var clean = LyricsClean.Apply(doc, null, null, out int credits);

        Assert.Equal(0, credits);
        Assert.Equal(2, clean.Lines.Count);
    }

    [Fact]
    public void ProviderBoilerplateAndStrayTags_AreDroppedAnywhere()
    {
        var doc = Doc("qq", LyricsSyncKind.Line,
            Plain(1000, "first real lyric line"),
            Plain(2000, "未经许可不得翻唱或使用"),
            Plain(3000, "[offset:500]"),
            Plain(4000, "middle real lyric line"),
            Plain(5000, "本歌曲来自QQ音乐"),
            Plain(6000, "last real lyric line"));

        var clean = LyricsClean.Apply(doc, null, null, out int credits);

        Assert.Equal(0, credits);
        Assert.Equal(3, clean.Lines.Count);
        Assert.Equal(2000, clean.Lines[0].EndMs);   // the dropped row still ends the line above it
    }

    [Fact]
    public void AnInstrumentalNotice_EmptiesTheDocument_CreditsIncluded()
    {
        // NetEase's instrumental: writer credits plus "纯音乐，请欣赏". Showing the credits as the lyrics would be worse
        // than the miss the caller turns an empty document into.
        var doc = Doc("netease", LyricsSyncKind.Line, Plain(0, "作词 : A"), Plain(0, "作曲 : B"), Plain(1000, "纯音乐，请欣赏"));

        Assert.Empty(LyricsClean.Apply(doc).Lines);
    }

    [Fact]
    public void TheGrammarIsNotTheVocabulary_AnUnknownKeyWithANameListIsACredit_AClauseIsNot()
    {
        var doc = Doc("lrclib", LyricsSyncKind.Line,
            Plain(0, "Réalisation：Jean Dupont / Marie Curie"),   // unknown key, name-list value → credit
            Plain(1000, "Girl: you know I love you"),             // short key but a clause → lyric, ends the run
            Plain(4000, "another real lyric"));

        var clean = LyricsClean.Apply(doc, null, null, out int credits);

        Assert.Equal(1, credits);
        Assert.Equal("Girl: you know I love you", clean.Lines[0].Text);
    }

    // ── why this exists: the reranker comparison ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void CleaningIsWhatMakesCoverageMeanSomething()
    {
        var reference = Spotify();
        var kugou = KugouKrc();

        var dirty = LyricsReranker.Rank([new LyricsCandidate("kugou", 0.5, MatchBasis.MetadataSearch, kugou)], reference)
            .All.Single();
        var clean = LyricsReranker.Rank(
                [new LyricsCandidate("kugou", 0.5, MatchBasis.MetadataSearch, LyricsClean.Apply(kugou, Title, Artist))],
                LyricsClean.Apply(reference, Title, Artist))
            .All.Single();

        // 35-vs-62 line counts made a near-perfect match look like half a document.
        Assert.InRange(dirty.Coverage, 0.55, 0.58);
        Assert.InRange(clean.Coverage, 0.86, 0.89);
        Assert.True(clean.TextAgreement >= dirty.TextAgreement);
        Assert.Equal(1.0, clean.TextAgreement, 3);
    }

    // ── tier 2: the reference decides, in any language ───────────────────────────────────────────────────────────────

    static LyricsDocument EightLineReference() => Doc("spotify", LyricsSyncKind.Line,
        Plain(10000, "alpha bravo charlie"), Plain(13000, "delta echo foxtrot"), Plain(16000, "golf hotel india"),
        Plain(19000, "juliet kilo lima"), Plain(22000, "mike november oscar"), Plain(25000, "papa quebec romeo"),
        Plain(28000, "sierra tango uniform"), Plain(31000, "victor whiskey xray"));

    [Fact]
    public void TrimUnalignedEdges_DropsLeadingAndTrailingPadding_TheReferenceNeverSings()
    {
        // Lines share no words on purpose: LineMatch is a token-set overlap, so look-alike test lines would cross-pair and
        // skew the offset. The candidate runs 200 ms late and pads both ends with rows the reference has no counterpart for — the shape of
        // a search-matched KRC (credits up front, a stray "***" at the end), in no particular language.
        var cand = Doc("kugou", LyricsSyncKind.Line,
            Plain(1000, "ABC DEF"), Plain(4000, "GHI JKL MNO"),
            Plain(10200, "alpha bravo charlie"), Plain(13200, "delta echo foxtrot"), Plain(16200, "golf hotel india"),
            Plain(19200, "juliet kilo lima"), Plain(22200, "mike november oscar"), Plain(25200, "papa quebec romeo"),
            Plain(28200, "sierra tango uniform"), Plain(31200, "victor whiskey xray"),
            Plain(40000, "PQR STU"));

        var trimmed = LyricsCreditRules.TrimUnalignedEdges(cand, EightLineReference(), out int leading, out int trailing);

        Assert.Equal(2, leading);
        Assert.Equal(1, trailing);
        Assert.Equal(8, trimmed.Lines.Count);
        Assert.Equal("alpha bravo charlie", trimmed.Lines[0].Text);
        Assert.Equal(10200, trimmed.Lines[0].StartMs);          // kept rows keep their own timestamps
        Assert.Equal(31200, trimmed.Lines[^1].StartMs);
    }

    [Fact]
    public void TrimUnalignedEdges_KeepsADifferentlyWordedFirstLyric_ThatSitsAtTheReferenceEdge()
    {
        // Same song, first line worded differently by this provider: it aligns to nothing but it starts exactly where the
        // reference starts, so it is a lyric, not padding. Timing corroboration is what keeps it.
        var cand = Doc("lrclib", LyricsSyncKind.Line,
            Plain(10000, "yankee zulu opening"), Plain(13000, "delta echo foxtrot"), Plain(16000, "golf hotel india"),
            Plain(19000, "juliet kilo lima"), Plain(22000, "mike november oscar"), Plain(25000, "papa quebec romeo"),
            Plain(28000, "sierra tango uniform"), Plain(31000, "victor whiskey xray"));

        var trimmed = LyricsCreditRules.TrimUnalignedEdges(cand, EightLineReference(), out int leading, out int trailing);

        Assert.Equal(0, leading);
        Assert.Equal(0, trailing);
        Assert.Same(cand, trimmed);
    }

    [Fact]
    public void TrimUnalignedEdges_NeedsAReferenceWorthTrusting()
    {
        var cand = Doc("kugou", LyricsSyncKind.Line, Plain(1000, "ABC DEF"), Plain(10200, "alpha bravo charlie"), Plain(13200, "delta echo foxtrot"));
        var thin = Doc("spotify", LyricsSyncKind.Line, Plain(10000, "alpha bravo charlie"), Plain(13000, "delta echo foxtrot"));

        Assert.Same(cand, LyricsCreditRules.TrimUnalignedEdges(cand, null, out _, out _));
        Assert.Same(cand, LyricsCreditRules.TrimUnalignedEdges(cand, thin, out _, out _));   // below MinReferenceLines
    }
}
