using System;
using System.Collections.Generic;
using Wavee.Backend.Hydration;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

public class TrackHydrationCensusTests
{
    static Track Thin(string id) =>
        new(id, "spotify:track:" + id, "spotify:track:" + id, [], new AlbumRef("", "", ""), 0, false, null);

    static Track Openish(string id, Image? image = null, int year = 2014, long playCount = 0, double? bpm = 90,
                         IReadOnlyList<string>? tags = null) =>
        new(id, "spotify:track:" + id, "Title " + id,
            [new ArtistRef("a", "spotify:artist:a", "Artist")],
            new AlbumRef("al", "spotify:album:al", "Album"),
            180_000, false, image, PlayCount: playCount, TempoBpm: bpm, Tags: tags, Year: year);

    [Fact]
    public void ThinSeedTitleIsTheMajorityReasonForMissingTitles()
    {
        var report = TrackHydrationCensus.Scan([Thin("a"), Thin("b")], TraitSet.None);
        Assert.True(report.HasGaps);
        Assert.Equal(2, report.Title);
        Assert.Equal(2, report.None);
        Assert.Equal(TrackHydrationCensus.ThinSeed, report.TitleReason);
        Assert.Equal(2, report.Samples.Count);
        Assert.Equal("title", report.Samples[0].Gap);
    }

    [Fact]
    public void ImageMissOnANamedTrackIsOpenPredicate()
    {
        var named = Openish("a", image: null);
        Assert.Equal(HydrationLevel.Identity, HydrationLevels.Of(named)); // named, but Open wants a usable image

        var report = TrackHydrationCensus.Scan([named], TraitSet.RowBundle);
        Assert.Equal(1, report.Image);
        Assert.Equal(TrackHydrationCensus.OpenPredicate, report.ImageReason);
        Assert.Equal(0, report.Title);
    }

    [Fact]
    public void Scan_NamedRowWithZeroDuration_DurationReasonIsOpenPredicate()
    {
        // Named (title/artist/album/image all present) but DurationMs still at zero — same shape as
        // ImageMissOnANamedTrackIsOpenPredicate above, mirrored onto the duration gap.
        var named = Openish("a", image: new Image("https://i.scdn.co/image/abc")) with { DurationMs = 0 };
        Assert.Equal(HydrationLevel.Identity, HydrationLevels.Of(named)); // named, but Open also wants a nonzero duration

        var report = TrackHydrationCensus.Scan([named], TraitSet.RowBundle);
        Assert.Equal(1, report.Duration);
        Assert.Equal(TrackHydrationCensus.OpenPredicate, report.DurationReason);
        Assert.Equal(0, report.Title);
    }

    [Fact]
    public void Scan_UnnamedRow_DurationReasonIsThinSeed()
    {
        // Thin() is title==uri (unnamed) AND DurationMs==0 — the duration gap coincides with the title gap, so the
        // majority reason is "the row itself was never named", not "the row is named but Open's duration wasn't met".
        var report = TrackHydrationCensus.Scan([Thin("a")], TraitSet.None);
        Assert.Equal(1, report.Duration);
        Assert.Equal(TrackHydrationCensus.ThinSeed, report.DurationReason);
    }

    [Fact]
    public void PlaycountReasonIsSurfaceEmptyWhenTheSurfaceDidNotWantIt()
    {
        // A REAL top-level surface (Recents-like: no PlayCount) — distinct from a ladder's own SubAsk repair, which
        // never wants the whole trait pass and gets TraitNotAsked instead (see the SubAsk test below).
        var row = Openish("a", image: new Image("https://i.scdn.co/image/abc"), playCount: 0);
        var report = TrackHydrationCensus.Scan([row], TraitSet.RowBundle);
        Assert.Equal(1, report.Playcount);
        Assert.Equal(TrackHydrationCensus.TraitSurfaceEmpty, report.PlaycountReason);
    }

    [Fact]
    public void PlaycountReasonIsNotAskedWhenTheGapIsALaddersOwnSubAsk()
    {
        // Fix 5: a ladder's internal identity/ref-repair (AlbumHydration/PlaylistHydration/ArtistHydration's `sub`,
        // PlayableHydration's ref-closure `background`) deliberately carries no trait surface at all — that is NOT the
        // same bug as a real caller forgetting to attribute one, so the census must not conflate the two labels.
        var row = Openish("a", image: new Image("https://i.scdn.co/image/abc"), playCount: 0);
        var report = TrackHydrationCensus.Scan([row], TraitSet.RowBundle, subAsk: true);
        Assert.Equal(1, report.Playcount);
        Assert.Equal(TrackHydrationCensus.TraitNotAsked, report.PlaycountReason);
    }

    [Fact]
    public void PlaycountReasonPrefersNegativeOverUnansweredWhenThatIsTheMajority()
    {
        var row = Openish("a", image: new Image("https://i.scdn.co/image/abc"), playCount: 0);
        var report = TrackHydrationCensus.Scan([row], TraitSet.PlayCount,
            playcount: new TrackHydrationCensus.TraitTallies(Unanswered: 1, Negative: 8, NotResident: 0));
        Assert.Equal(TrackHydrationCensus.TraitNegative, report.PlaycountReason);
    }

    [Fact]
    public void TempoReasonIsUnansweredWhenThePostOmittedTheKind()
    {
        var row = Openish("a", image: new Image("https://i.scdn.co/image/abc"), bpm: null);
        var report = TrackHydrationCensus.Scan([row], TraitSet.AudioAttributes,
            tempo: new TrackHydrationCensus.TraitTallies(Unanswered: 4, Negative: 0, NotResident: 0));
        Assert.Equal(TrackHydrationCensus.TraitUnanswered, report.TempoReason);
    }

    [Fact]
    public void NoLineWhenEveryAskedFieldIsPresent()
    {
        var row = Openish("a", image: new Image("https://i.scdn.co/image/abc"), playCount: 12, bpm: 120,
            tags: ["Pop"], year: 2014);
        row = row with { Availability = Availability.Playable };
        var report = TrackHydrationCensus.Scan([row], TraitSet.RowBundle | TraitSet.PlayCount);
        Assert.False(report.HasGaps);
        Assert.Equal(0, report.Title);
        Assert.Equal(0, report.Image);
        Assert.Equal(0, report.Playcount);
        Assert.Equal(0, report.Tempo);
        Assert.Equal(0, report.Year);
        Assert.Equal(0, report.TagsNull);
        Assert.Equal(0, report.TagsEmpty);
    }

    [Fact]
    public void SamplesCapAtThreeDistinctUris()
    {
        Track?[] rows = [Thin("a"), Thin("b"), Thin("c"), Thin("d")];
        var report = TrackHydrationCensus.Scan(rows, TraitSet.None);
        Assert.Equal(TrackHydrationCensus.SampleCap, report.Samples.Count);
    }
}
