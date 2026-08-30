using System;
using System.IO;
using System.Linq;
using Wavee.Backend.Lyrics;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests.Lyrics;

// Ground truth for the "Caribbean Queen" desync, captured live through the inspector's evidence bundle
// (ISRC GBAHK9700109, 2026-08-11). Both fixtures are the REAL bytes out of ONE macro.subtitles.get response:
//
//   musixmatch-richsync-caribbean-queen.json   track.richsync.get → richsync_body   (word-synced, BROKEN)
//   musixmatch-subtitle-caribbean-queen.lrc    track.subtitles.get → subtitle_body  (line-synced, CORRECT)
//
// The investigation ended here. The richsync entries carry line starts that are perfect — they align 1:1 with Spotify's
// own lyric at zero offset — and line ENDS that are physically impossible: `{"ts":19.48,"te":20.2978}` for "She dashed
// by me in painted on jeans", while the LRC in the same response puts the next event at 22.30. That is not a parser
// mistake; ParseRichsync reproduces the payload faithfully, and these tests exist to prove it stays that way. What the
// app must do about it is the other half: reject the richsync and take the LRC that shipped alongside it.
public class MusixmatchRichsyncFixtureTests
{
    static string Fixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Lyrics", "Fixtures", name));

    static LyricsDocument Richsync() => LyricsWordFormats.ParseRichsync(
        Fixture("musixmatch-richsync-caribbean-queen.json"), "4JEylZNW8SbO4zUyfVrpb7");

    static LyricsDocument Subtitle() => LyricsText.ParseLrc(
        Fixture("musixmatch-subtitle-caribbean-queen.lrc"), "4JEylZNW8SbO4zUyfVrpb7", "musixmatch");

    [Fact]
    public void OurParserReproducesThePayloadFaithfully()
    {
        var doc = Richsync();

        // The values in the file, read straight off it: ts=19.48 / te=20.2978 on the third entry. If a future "fix"
        // starts scaling or inventing these, this is the test that says the payload never said that.
        Assert.Equal(39, doc.Lines.Count);
        var l = doc.Lines[2];
        Assert.Equal("She dashed by me in painted on jeans", l.Text);
        Assert.Equal(19480, l.StartMs);
        Assert.Equal(20297, l.EndMs);
    }

    [Fact]
    public void TheLineSTARTSAreCorrect_OnlyTheEndsAreNot()
    {
        var rich = Richsync();
        var lrc = Subtitle();

        // Every richsync line start matches a subtitle timestamp within 100ms — this is why the reranker scored the
        // broken candidate 0.95 and why the repair keeps the starts instead of throwing the document away.
        var lrcStarts = lrc.Lines.Select(x => x.StartMs).ToList();
        int matched = rich.Lines.Count(r => lrcStarts.Any(s => Math.Abs(s - r.StartMs) <= 100));
        Assert.True(matched >= rich.Lines.Count - 2, $"only {matched}/{rich.Lines.Count} starts line up");
    }

    [Fact]
    public void TheRichsyncIsRejected_AndTheSubtitleInTheSameResponseIsClean()
    {
        Assert.True(LyricsTiming.HasImplausibleWordTiming(Richsync(), out int impossible, out int judged));
        Assert.True(impossible >= judged / 2, $"{impossible}/{judged} lines impossibly fast");

        // Same response, same provider, same track — and perfectly usable. That contrast is the finding: Musixmatch's
        // LINE data for this ISRC is fine and only its generated word timing is junk, so falling back is free.
        //
        // The gate itself never even looks at this one: it judges WORD timing, and a line-synced document has none to
        // judge, so it is unconditionally false (0 judged) — not "passed".
        var subtitle = Subtitle();
        Assert.Equal(LyricsSyncKind.Line, subtitle.Sync);
        Assert.False(LyricsTiming.HasImplausibleWordTiming(subtitle, out _, out int subJudged));
        Assert.Equal(0, subJudged);

        // So check its per-line rates directly. "Usable", not "spotless": real provider data has warts — the closing
        // line's slot is cut short — which is exactly why the rule is a MAJORITY one. An any-line rule would condemn
        // this perfectly good subtitle and leave the track with the broken karaoke instead.
        Assert.True(subtitle.Lines.Count > 20, $"only {subtitle.Lines.Count} lines");
        int fast = subtitle.Lines.Count(LyricsTiming.IsImpossiblyFast);
        Assert.True(fast <= 1, $"{fast} of {subtitle.Lines.Count} subtitle lines are impossibly fast");
    }

    // ── the whole saga, end to end, on four real payloads ────────────────────────────────────────────────────────────
    // Spotify's colour-lyrics (the reference), Musixmatch's LRC (line, correct), and Kugou's KRC (word-synced, real).
    // This is the outcome the user should get: genuine karaoke, from the provider that actually has it.

    static LyricsDocument SpotifyReference() => Wavee.Backend.Lyrics.Sources.SpotifyNativeLyricsSource.Parse(
        Fixture("spotify-colorlyrics-caribbean-queen.json"), "4JEylZNW8SbO4zUyfVrpb7")!;

    static LyricsDocument KugouKrc() => LyricsWordFormats.ParseKrc(
        Fixture("kugou-krc-caribbean-queen.krc"), "4JEylZNW8SbO4zUyfVrpb7");

    [Fact]
    public void KugouKrcCarriesGenuineWordTiming()
    {
        var doc = KugouKrc();

        Assert.Equal(LyricsSyncKind.Syllable, doc.Sync);
        Assert.False(LyricsTiming.HasImplausibleWordTiming(doc, out _, out _));
        // Musixmatch claimed this line took 818ms; Kugou says 3800ms, which is what a human would sing.
        var l = doc.Lines.Single(x => x.Text.StartsWith("She dashed by me", StringComparison.Ordinal));
        Assert.InRange(LyricsTiming.WordSpanMs(l), 3000, 4200);
    }

    [Fact]
    public void RealKugouWordSync_BeatsMusixmatchsCorrectLineLyric()
    {
        // Cleaned, exactly as AggregatingLyricsProvider.FetchOne delivers them — including the reference, which is
        // itself a candidate. With the raw documents the tier bar would refuse Kugou on coverage alone (0.565).
        const string title = "Caribbean Queen (No More Love On the Run)", artist = "Billy Ocean";
        var reference = LyricsClean.Apply(SpotifyReference(), title, artist);
        var candidates = new[]
        {
            new LyricsCandidate("musixmatch", 0.7, MatchBasis.Isrc, LyricsClean.Apply(Subtitle(), title, artist)),
            new LyricsCandidate("kugou", 0.5, MatchBasis.MetadataSearch, LyricsClean.Apply(KugouKrc(), title, artist)),
        };

        var ranked = LyricsReranker.Rank(candidates, reference);

        // The numbers the whole design rests on: a near-perfect match now READS as one.
        var kugouDec = ranked.All.Single(d => d.ProviderId == "kugou");
        Assert.Equal(1.0, kugouDec.TextAgreement, 3);
        Assert.InRange(kugouDec.Coverage, 0.86, 0.89);

        // Kugou is a fuzzy metadata match against a perfect-text ISRC candidate, and it has FEWER lines than the
        // reference (35 vs 62 — Spotify emits blank interlude lines). It wins anyway, because it is corroborated
        // (text 0.97, timing 0.85) and it delivers the richer tier.
        Assert.Equal("kugou", ranked.Best!.ProviderId);
        Assert.Equal(LyricsSyncKind.Syllable, ranked.Winner!.Sync);
        Assert.NotEmpty(ranked.Winner.Lines[0].Syllables);
    }

    [Fact]
    public void TheRepairKeepsTheStartsAndDropsTheInventedWordTiming()
    {
        var rich = Richsync();
        var repaired = LyricsTiming.StripWordTiming(rich);

        Assert.Equal(LyricsSyncKind.Line, repaired.Sync);
        Assert.All(repaired.Lines, l => Assert.Empty(l.Syllables));
        Assert.Equal(rich.Lines.Select(l => l.StartMs), repaired.Lines.Select(l => l.StartMs));
        Assert.Equal(rich.Lines.Select(l => l.Text), repaired.Lines.Select(l => l.Text));
    }
}
