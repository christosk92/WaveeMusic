using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>Pure-rule tests for <see cref="Lyrics.TranscriptPresentation.RoleOf"/> / <c>InkOf</c> (report 8b): a
/// podcast transcript row reads in plain ink (never a translucent wash) except the active line's own sung glyphs
/// (the accent) and a row far enough PAST the active line that the row's opacity has already carried it dim.</summary>
public sealed class TranscriptPresentationTests
{
    [Fact]
    public void ActiveLineSungPartIsAccentUnsungPartIsPrimary()
    {
        int packed = Lyrics.Emphasis.Pack(index: 5, active: 5, reserveLine: -1);
        Assert.Equal(Lyrics.LineRole.Active, Lyrics.TranscriptPresentation.RoleOf(packed));
        Assert.Equal(Lyrics.InkRung.Accent, Lyrics.TranscriptPresentation.InkOf(Lyrics.LineRole.Active, sungPart: true));
        Assert.Equal(Lyrics.InkRung.Primary, Lyrics.TranscriptPresentation.InkOf(Lyrics.LineRole.Active, sungPart: false));
    }

    [Fact]
    public void UpcomingLinesAlwaysReadPrimaryRegardlessOfDistance()
    {
        for (int index = 6; index < 20; index++)
        {
            int packed = Lyrics.Emphasis.Pack(index, active: 5, reserveLine: -1);
            Assert.Equal(Lyrics.LineRole.Upcoming, Lyrics.TranscriptPresentation.RoleOf(packed));
            Assert.Equal(Lyrics.InkRung.Primary, Lyrics.TranscriptPresentation.InkOf(Lyrics.LineRole.Upcoming, sungPart: true));
            Assert.Equal(Lyrics.InkRung.Primary, Lyrics.TranscriptPresentation.InkOf(Lyrics.LineRole.Upcoming, sungPart: false));
        }
    }

    [Fact]
    public void APastLineInsideTheOpacityRingStillReadsPrimary()
    {
        // dist == PastInkRing (1): the row's OWN opacity fade (Emphasis.OpacityOf) already carries this distance —
        // its ink stays full so a transcript with many rows visible at once does not read as a flat grey wall the
        // instant a line falls behind.
        int packed = Lyrics.Emphasis.Pack(index: 4, active: 5, reserveLine: -1);
        Assert.Equal(1, Lyrics.Emphasis.DistOf(packed));
        Assert.Equal(Lyrics.LineRole.Upcoming, Lyrics.TranscriptPresentation.RoleOf(packed));
        Assert.Equal(Lyrics.InkRung.Primary, Lyrics.TranscriptPresentation.InkOf(Lyrics.TranscriptPresentation.RoleOf(packed), sungPart: false));
    }

    [Fact]
    public void APastLineBeyondTheRingDimsToSecondary()
    {
        // dist == PastInkRing + 1 (2), still inside Emphasis.MaxBucket so PastBit is set.
        int packed = Lyrics.Emphasis.Pack(index: 3, active: 5, reserveLine: -1);
        Assert.Equal(2, Lyrics.Emphasis.DistOf(packed));
        Assert.Equal(Lyrics.LineRole.Past, Lyrics.TranscriptPresentation.RoleOf(packed));
        Assert.Equal(Lyrics.InkRung.Secondary, Lyrics.TranscriptPresentation.InkOf(Lyrics.LineRole.Past, sungPart: true));
        Assert.Equal(Lyrics.InkRung.Secondary, Lyrics.TranscriptPresentation.InkOf(Lyrics.LineRole.Past, sungPart: false));
    }

    [Fact]
    public void NoActiveLineNeverClassifiesAsPast()
    {
        // Browsing with no resolved active line (active < 0): Emphasis.Pack never sets PastBit, so every row reads
        // Upcoming/Primary rather than a permanently dim Past — matches the neutral (not-owning-playback) case where
        // the rail deliberately renders a note instead, but keeps this rule correct standalone.
        for (int index = 0; index < 8; index++)
        {
            int packed = Lyrics.Emphasis.Pack(index, active: -1, reserveLine: -1);
            Assert.NotEqual(Lyrics.LineRole.Past, Lyrics.TranscriptPresentation.RoleOf(packed));
        }
    }

    [Fact]
    public void ASaturatedFarPastRowIsNotMarkedPastEitherSameAsTheOpacityLadderTreatsIt()
    {
        // Emphasis.Pack only sets PastBit while bucket < MaxBucket — a row this far away is "visually identical to
        // one further" either direction, and RoleOf inherits that same saturation instead of inventing a distinction
        // opacity does not carry.
        int packed = Lyrics.Emphasis.Pack(index: 0, active: 20, reserveLine: -1);
        Assert.Equal(Lyrics.Emphasis.MaxBucket, Lyrics.Emphasis.DistOf(packed));
        Assert.NotEqual(Lyrics.LineRole.Past, Lyrics.TranscriptPresentation.RoleOf(packed));
    }
}

/// <summary>THE PEEK's rules (podcast-episode-peek §2): when the transcript pane is gated, how many of the real
/// opening lines it shows, and the band ladder that carries the dissolve — sharp at the top, blurred and nearly
/// transparent at the bottom, where the play gate sits.</summary>
public sealed class TranscriptPeekTests
{
    [Theory]
    [InlineData(true, false, 40, true)]    // a reader on an episode that is NOT playing: gated
    [InlineData(true, true, 40, false)]    // this episode IS the one playing (from anywhere): the gate is gone
    [InlineData(true, false, 0, false)]    // no transcript at all: the quiet line, no veil and no gate
    [InlineData(true, true, 0, false)]
    [InlineData(false, false, 40, false)]  // song lyrics are never gated
    public void Gated_needs_a_browsed_podcast_and_something_to_peek_at(bool podcast, bool owns, int lines, bool expected)
        => Assert.Equal(expected, Lyrics.TranscriptPeek.Gated(podcast, owns, lines));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-4, 0)]
    [InlineData(3, 3)]
    [InlineData(400, Lyrics.TranscriptPeek.MaxLines)]
    public void LineCount_is_an_excerpt_never_the_whole_transcript(int available, int expected)
        => Assert.Equal(expected, Lyrics.TranscriptPeek.LineCount(available));

    [Fact]
    public void The_opening_lines_stay_sharp_and_every_later_line_is_veiled()
    {
        int count = Lyrics.TranscriptPeek.LineCount(40);
        for (int i = 0; i < Lyrics.TranscriptPeek.SharpLines; i++)
        {
            Assert.Equal(0, Lyrics.TranscriptPeek.BandOf(i, count));
            Assert.Equal(0f, Lyrics.TranscriptPeek.SigmaOf(0));
            Assert.Equal(1f, Lyrics.TranscriptPeek.OpacityOf(0));
        }
        for (int i = Lyrics.TranscriptPeek.SharpLines; i < count; i++)
            Assert.True(Lyrics.TranscriptPeek.BandOf(i, count) > 0, "line " + i + " should be veiled");
    }

    [Fact]
    public void Bands_never_run_backwards_and_never_pass_the_last_one()
    {
        for (int count = 1; count <= Lyrics.TranscriptPeek.MaxLines; count++)
        {
            int previous = 0;
            for (int i = 0; i < count; i++)
            {
                int band = Lyrics.TranscriptPeek.BandOf(i, count);
                Assert.InRange(band, previous, Lyrics.TranscriptPeek.Bands - 1);
                previous = band;
            }
        }
    }

    [Fact]
    public void A_transcript_no_longer_than_the_sharp_head_never_dissolves_at_all()
    {
        for (int i = 0; i < Lyrics.TranscriptPeek.SharpLines; i++)
            Assert.Equal(0, Lyrics.TranscriptPeek.BandOf(i, Lyrics.TranscriptPeek.SharpLines));
    }

    [Fact]
    public void The_ladder_runs_monotonically_from_sharp_and_opaque_to_the_deepest_blur()
    {
        float sigma = -1f, opacity = 2f;
        for (int band = 0; band < Lyrics.TranscriptPeek.Bands; band++)
        {
            float s = Lyrics.TranscriptPeek.SigmaOf(band);
            float o = Lyrics.TranscriptPeek.OpacityOf(band);
            Assert.True(s > sigma, "sigma must rise at band " + band);
            Assert.True(o < opacity, "opacity must fall at band " + band);
            Assert.InRange(o, Lyrics.TranscriptPeek.MinOpacity, 1f);
            sigma = s; opacity = o;
        }
        Assert.Equal(Lyrics.TranscriptPeek.MaxSigma, Lyrics.TranscriptPeek.SigmaOf(Lyrics.TranscriptPeek.Bands - 1), 3);
        Assert.Equal(Lyrics.TranscriptPeek.MinOpacity, Lyrics.TranscriptPeek.OpacityOf(Lyrics.TranscriptPeek.Bands - 1), 3);
        // A band index past the last one saturates rather than running off the ladder.
        Assert.Equal(Lyrics.TranscriptPeek.MaxSigma, Lyrics.TranscriptPeek.SigmaOf(Lyrics.TranscriptPeek.Bands + 5), 3);
    }

    [Theory]
    [InlineData("en", "EN")]
    [InlineData("en-US", "EN")]
    [InlineData("pt_BR", "PT")]
    [InlineData("  de  ", "DE")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("notalanguagetag", "")]
    public void LanguageLabel_is_the_primary_subtag_upper_cased(string? tag, string expected)
        => Assert.Equal(expected, Lyrics.TranscriptPeek.LanguageLabel(tag));

    [Theory]
    [InlineData("3 hr 14 min", "EN", "3 hr 14 min · EN")]
    [InlineData("3 hr 14 min", "", "3 hr 14 min")]
    [InlineData("", "EN", "EN")]
    [InlineData("", "", "")]
    public void MetaLine_joins_what_it_knows_and_drops_what_it_does_not(string duration, string language, string expected)
        => Assert.Equal(expected, Lyrics.TranscriptPeek.MetaLine(duration, language));
}
