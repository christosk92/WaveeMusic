using Wavee;
using Xunit;

namespace Wavee.Tests;

public sealed class PodcastTranscriptLyricsTests
{
    [Fact]
    public void CapturedCharacterRunsPreserveTextAndAbsoluteTiming()
    {
        var transcript = Spotify.Podcasts.DecodeTranscript("""
            {"language":"en","section":[{"startMs":80,"title":{}},
            {"startMs":80,"text":{"sentence":{"text":"Last week","highlight":[
            {"startMs":80,"numChars":4},{"startMs":600,"numChars":1},{"startMs":600,"numChars":4}]}}},
            {"startMs":1200,"text":{"sentence":{"text":"Next sentence."}}}]}
            """u8.ToArray());
        Assert.Equal(2, transcript.Lines.Length);
        Assert.Equal(new Spotify.Podcasts.TranscriptSpan(600, 5, 4), transcript.Lines[0].Highlights![2]);
        var doc = Lyrics.FromTranscript("episode-a", transcript, 2000);
        Assert.Equal(Lyrics.SyncKind.Syllable, doc.Sync);
        var first = doc.Lines[0];
        Assert.True(first.IsWordByWord);
        Assert.Equal("Last week", string.Concat(first.Syllables.Select(s => s.Text)));
        Assert.Equal(new Lyrics.Syllable(80, 600, "Last"), first.Syllables[0]);
        Assert.Equal(new Lyrics.Syllable(600, 1200, " week"), first.Syllables[1]);
        Assert.False(doc.Lines[1].IsWordByWord);
    }

    [Theory]
    [InlineData(1)] // would split the surrogate pair
    [InlineData(4)] // runs past the text
    public void InvalidUnicodeRangeFallsBackToWholeSentence(int count)
    {
        string json = """
            {"section":[{"startMs":100,"text":{"sentence":{"text":"\ud83d\ude00a","highlight":[{"startMs":100,"numChars":COUNT}]}}}]}
            """.Replace("COUNT", count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var transcript = Spotify.Podcasts.DecodeTranscript(System.Text.Encoding.UTF8.GetBytes(json));
        var line = Assert.Single(Lyrics.FromTranscript("episode", transcript, 1000).Lines);
        Assert.Equal("\U0001F600a", line.Text);
        Assert.Empty(line.Syllables);
        Assert.False(line.IsWordByWord);
    }

    [Fact]
    public void ValidUnicodeRangesNeverSplitSurrogates()
    {
        var transcript = Spotify.Podcasts.DecodeTranscript("""
            {"section":[{"startMs":100,"text":{"sentence":{"text":"\ud83d\ude00a","highlight":[
            {"startMs":100,"numChars":2},{"startMs":400,"numChars":1}]}}}]}
            """u8.ToArray());
        var line = Assert.Single(Lyrics.FromTranscript("episode", transcript, 1000).Lines);
        Assert.True(line.IsWordByWord);
        Assert.Equal("\U0001F600", line.Syllables[0].Text);
        Assert.Equal("a", line.Syllables[1].Text);
    }

    [Fact]
    public void UnknownFinalBoundaryDoesNotInventFineTiming()
    {
        var transcript = new Spotify.Podcasts.Transcript("en", [new(0, "Hello", false,
            [new(0, 0, 5)])], 200);
        var line = Assert.Single(Lyrics.FromTranscript("episode", transcript).Lines);
        Assert.False(line.IsWordByWord);
        Assert.Empty(line.Syllables);
        Assert.Null(line.EndMs);
    }

    [Fact]
    public void BackwardTimingStaysWholeLineButAPartialTrailingRangeKeepsItsWord()
    {
        var transcript = Spotify.Podcasts.DecodeTranscript("""
            {"section":[{"startMs":100,"text":{"sentence":{"text":"hello","highlight":[
            {"startMs":100,"numChars":2},{"startMs":90,"numChars":3}]}}},
            {"startMs":1000,"text":{"sentence":{"text":"world","highlight":[{"startMs":1000,"numChars":3}]}}}]}
            """u8.ToArray());
        var doc = Lyrics.FromTranscript("episode", transcript, 2000);
        // "hello"'s second highlight runs BACKWARD in time (90 < 100) — genuinely corrupt, still a whole Line row.
        Assert.False(doc.Lines[0].IsWordByWord);
        Assert.Empty(doc.Lines[0].Syllables);
        // "world" only highlights its first 3 chars ("wor"); the answer just left "ld" uncovered — a plain GAP, not
        // corruption — so it keeps "wor" as its own word run and "ld" as one filler run sharing the rest of the line's
        // window, instead of the whole sentence falling back to Line sync.
        Assert.True(doc.Lines[1].IsWordByWord);
        Assert.Equal("world", string.Concat(doc.Lines[1].Syllables.Select(s => s.Text)));
        Assert.Equal(new Lyrics.Syllable(1000, 1600, "wor"), doc.Lines[1].Syllables[0]);
        Assert.Equal(new Lyrics.Syllable(1600, 2000, "ld"), doc.Lines[1].Syllables[1]);
        // At least one line resolved to word timing, so the DOCUMENT is Syllable-synced.
        Assert.Equal(Lyrics.SyncKind.Syllable, doc.Sync);
    }

    [Fact]
    public void PartialHighlightsCoveringSomeWordsKeepThoseWordsAndFillTheGapsAsOneRunEach()
    {
        // "One two three four five" — highlights only cover words 1 ("One"), 2 ("two") and 4 ("four"); "three" and
        // "five" have no highlight of their own at all.
        var transcript = new Spotify.Podcasts.Transcript("en",
        [
            new(1000, "One two three four five", false,
            [
                new(1000, 0, 3),    // "One"
                new(2000, 4, 3),    // "two"
                new(4000, 14, 4),   // "four"
            ], EndMs: 6000),
        ], 200);
        var doc = Lyrics.FromTranscript("episode", transcript);
        var line = Assert.Single(doc.Lines);
        Assert.True(line.IsWordByWord);
        Assert.Equal(Lyrics.SyncKind.Syllable, doc.Sync);
        // The covered words wipe on their own captured timing…
        Assert.Equal(6, line.Syllables.Count);
        Assert.Equal(new Lyrics.Syllable(1000, 1750, "One"), line.Syllables[0]);
        Assert.Equal(new Lyrics.Syllable(2000, 2600, "two"), line.Syllables[2]);
        Assert.Equal(new Lyrics.Syllable(4000, 4889, "four"), line.Syllables[4]);
        // …and every uncovered stretch — however many words it skips — becomes ONE line-synced filler run, so the
        // reconstructed text is exactly the source sentence with no words dropped.
        Assert.Equal(new Lyrics.Syllable(1750, 2000, " "), line.Syllables[1]);
        Assert.Equal(new Lyrics.Syllable(2600, 4000, " three "), line.Syllables[3]);
        Assert.Equal(new Lyrics.Syllable(4889, 6000, " five"), line.Syllables[5]);
        Assert.Equal(line.Text, string.Concat(line.Syllables.Select(s => s.Text)));
    }

    [Theory]
    [InlineData(.5)]
    [InlineData(1.5)]
    [InlineData(3)]
    public void ContentClockUsesSourceRate(double rate)
    {
        var clock = new Lyrics.MediaClock(1000);
        clock.OnSample(10000, 1000, true, rate);
        Assert.Equal(10000 + (long)(1000 * rate), clock.At(2000));
        clock.OnSample(15000, 2500, false, rate);
        Assert.Equal(15000, clock.At(10000));
        clock.OnSample(15000, 10000, true, rate);
        Assert.Equal(15000 + (long)(500 * rate), clock.At(10500));
    }

    [Fact]
    public void RateChangeAndBackwardSeekReanchorContentClock()
    {
        var clock = new Lyrics.MediaClock(1000);
        clock.OnSample(1000, 1000, true);
        Assert.Equal(2000, clock.At(2000));
        Assert.False(clock.OnSample(2000, 2000, true, 2));
        Assert.Equal(4000, clock.At(3000));
        Assert.True(clock.OnSample(500, 3000, true, .5));
        Assert.Equal(1000, clock.At(4000));
    }

    [Fact]
    public void PodcastVideoKeepsItsTranscriptMusicVideoRemainsSuppressed()
    {
        Assert.False(Lyrics.SyncGate.SyncSuppressed(true, podcast: true));
        Assert.True(Lyrics.SyncGate.SyncSuppressed(true));
        Assert.False(Lyrics.SyncGate.SyncSuppressed(false));
    }

    [Fact]
    public void SharedResourceIdentitySeparatesAccountsEpisodesLanguagesAndVariants()
    {
        var episode = EntityId.ForGid(EntityKind.Episode, (UInt128)1);
        var key = new Spotify.Podcasts.TranscriptKey(episode, "en", true);
        var identity = new Lyrics.TranscriptIdentity(1, "account-a", key, "https://example.test/transcript");
        Assert.Equal(identity, new Lyrics.TranscriptIdentity(1, "account-a", key, identity.Url));
        Assert.NotEqual(identity, identity with { Account = "account-b" });
        Assert.NotEqual(identity, identity with { Epoch = 2 });
        Assert.NotEqual(identity, identity with { Key = key with { Language = "ko" } });
        Assert.NotEqual(identity, identity with { Key = key with { ReadAlong = false } });
        Assert.NotEqual(identity, identity with { Key = key with { Episode = EntityId.ForGid(EntityKind.Episode, (UInt128)2) } });
    }
    [Theory]
    [InlineData(600, false, true)]
    [InlineData(640, false, false)]
    [InlineData(640, true, true)]
    [InlineData(700, true, false)]
    public void SavedRowsUseHysteresisAcrossCompactBoundary(float width, bool previous, bool expected)
        => Assert.Equal(expected, SavedEpisodeLayout.Compact(width, previous));
    [Fact]
    public void TranscriptPublishesReadyOnlyAfterOwnerRowsArePrepared()
    {
        var doc = Lyrics.FromTranscript("episode", new("en", [new(100, "First sentence", false)], 200), 1000);
        var shown = FluentGpu.Signals.Loadable<Lyrics.Doc>.Pending();
        Lyrics.Doc? prepared = null;
        Lyrics.TranscriptPresentation.Publish(shown, doc, next =>
        {
            Assert.False(shown.IsReady);
            prepared = next;
        });
        Assert.True(shown.IsReady);
        Assert.Same(prepared, shown.Value.Peek());
    }

    [Fact]
    public void PlayingTranscriptUsesLyricsEmphasisWhileOtherEpisodeBrowseStaysNeutral()
    {
        var line = new Lyrics.Line(100, "Hello", [new(100, 500, "Hello")], 500, IsWordByWord: true);
        for (int index = 0; index < 20; index++)
        {
            int emphasis = Lyrics.Emphasis.Pack(index, 3, -1);
            Assert.Equal(1f, Lyrics.TranscriptPresentation.RowOpacity(true, false, emphasis));
            Assert.Equal(Lyrics.Emphasis.OpacityOf(emphasis), Lyrics.TranscriptPresentation.RowOpacity(true, true, emphasis));
            Assert.False(Lyrics.TranscriptPresentation.Active(true, false, emphasis));
            Assert.False(Lyrics.TranscriptPresentation.WordHighlight(true, false, line));
            bool active = Lyrics.TranscriptPresentation.Active(true, true, emphasis);
            Assert.Equal(index == 3, active);
            Assert.True(Lyrics.TranscriptPresentation.WordHighlight(true, true, line));
            Assert.Equal(Lyrics.Emphasis.OpacityOf(emphasis), Lyrics.TranscriptPresentation.RowOpacity(false, true, emphasis));
        }
        Assert.False(Lyrics.TranscriptPresentation.WordHighlight(true, true, line with { Syllables = [], IsWordByWord = false }));
    }

    [Fact]
    public void SourceClockSeekAndRateSelectActualTranscriptSentenceAndWord()
    {
        var transcript = new Spotify.Podcasts.Transcript("en",
        [new(0, "One two", false, [new(0, 0, 4), new(500, 4, 3)]),
         new(1000, "Three", false, [new(1000, 0, 5)])], 200);
        var doc = Lyrics.FromTranscript("episode", transcript, 2000);
        var clock = new Lyrics.MediaClock(1000);
        clock.OnSample(0, 1000, true, 2);
        long now = clock.At(1300);
        Assert.Equal(0, Lyrics.ResolveLine(doc.Lines, now));
        Assert.InRange(Lyrics.Wipe.ComputeSplit(doc.Lines[0], now), .57f, 1f);
        now = clock.At(1600);
        Assert.Equal(1, Lyrics.ResolveLine(doc.Lines, now));
        clock.OnSample(100, 1600, false, 2);
        Assert.Equal(0, Lyrics.ResolveLine(doc.Lines, clock.At(9000)));
        Assert.InRange(Lyrics.Wipe.ComputeSplit(doc.Lines[0], clock.At(9000)), 0f, .57f);
    }

    [Theory]
    [InlineData(false, 16f, 24f, 22f)]
    [InlineData(true, 20f, 30f, 64f)]
    public void TranscriptTypographyUsesReadingProfileForTimedUntimedAndDerivedShimmer(bool large, float size, float lineHeight, float gutter)
    {
        var timed = Lyrics.Surface.Timed(large, podcast: true);
        Assert.Equal(new Lyrics.RowMetrics(size, lineHeight, 6f, gutter, 500), timed);
        Assert.Equal(lineHeight + 12f, timed.Estimate);
        Assert.Equal(timed, Lyrics.Surface.Unsynced(large, podcast: true));
        Assert.Equal(Lyrics.Surface.Timed(large).SidePad, timed.SidePad);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContentProfileCanSwitchToTranscriptAndBackWithoutRetainingMusicType(bool large)
    {
        var music = Lyrics.Surface.Timed(large);
        var speech = Lyrics.Surface.Timed(large, podcast: true);
        Assert.NotEqual(music, speech);
        Assert.NotEqual(music.Estimate, speech.Estimate);
        Assert.Equal((ushort)700, music.Weight);
        Assert.Equal((ushort)500, speech.Weight);
        Assert.Equal(music, Lyrics.Surface.Timed(large, podcast: false));
    }

    [Fact]
    public void PlaybackOwnershipAloneControlsNeutralTranscriptPresentationIncludingPause()
    {
        int active = Lyrics.Emphasis.Pack(2, 2, -1);
        int distant = Lyrics.Emphasis.Pack(12, 2, -1);
        Assert.False(Lyrics.TranscriptPresentation.Neutral(true, true));
        Assert.True(Lyrics.TranscriptPresentation.Neutral(true, false));
        Assert.False(Lyrics.TranscriptPresentation.Neutral(false, false));
        Assert.True(Lyrics.TranscriptPresentation.Active(true, true, active));
        Assert.False(Lyrics.TranscriptPresentation.Active(true, false, active));
        Assert.Equal(Lyrics.Emphasis.OpacityOf(distant), Lyrics.TranscriptPresentation.RowOpacity(true, true, distant));
        Assert.Equal(1f, Lyrics.TranscriptPresentation.RowOpacity(true, false, distant));
        // Pausing retains the same episode identity and its emphasis; no IsPlaying flag enters this presentation.
        Assert.Equal(1f, Lyrics.TranscriptPresentation.RowOpacity(true, true, active));
        Assert.Equal(1f, Lyrics.Surface.ScaleFor(active: false, reducedMotion: true));
    }

}
