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
}
