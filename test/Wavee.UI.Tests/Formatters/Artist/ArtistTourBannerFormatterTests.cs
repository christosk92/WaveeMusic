using System;
using FluentAssertions;
using Wavee.UI.Formatters.Artist;
using Xunit;

namespace Wavee.UI.Tests.Formatters.Artist;

public sealed class ArtistTourBannerFormatterTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NoConcerts_ReturnsEmpty()
    {
        var banner = ArtistTourBannerFormatter.Format(new ArtistTourSnapshot(
            ArtistName: "Robyn",
            ConcertCount: 0,
            AllFestivals: false,
            FirstConcertTitle: null,
            FirstConcertDateLocal: null,
            FirstConcertDateFormatted: null,
            FirstConcertVenue: null,
            FirstConcertCity: null,
            NowLocal: Now));

        banner.Eyebrow.Should().BeEmpty();
        banner.Headline.Should().BeEmpty();
        banner.IsLive.Should().BeFalse();
    }

    [Fact]
    public void SingleConcert_UpcomingShow()
    {
        var banner = ArtistTourBannerFormatter.Format(new ArtistTourSnapshot(
            ArtistName: "Robyn",
            ConcertCount: 1,
            AllFestivals: false,
            FirstConcertTitle: null,
            FirstConcertDateLocal: Now.AddDays(30),
            FirstConcertDateFormatted: "Fri, Jun 17",
            FirstConcertVenue: "Wembley",
            FirstConcertCity: "London",
            NowLocal: Now));

        banner.Eyebrow.Should().Be("UPCOMING SHOW");
        banner.Headline.Should().Be("Robyn live");
        banner.IconKind.Should().Be(ArtistTourIconKind.Microphone);
        banner.IsLive.Should().BeFalse();
    }

    [Fact]
    public void TwoToThreeConcerts_UpcomingDates()
    {
        var banner = ArtistTourBannerFormatter.Format(new ArtistTourSnapshot(
            ArtistName: "Robyn",
            ConcertCount: 3,
            AllFestivals: false,
            FirstConcertTitle: null,
            FirstConcertDateLocal: Now.AddDays(30),
            FirstConcertDateFormatted: "Fri, Jun 17",
            FirstConcertVenue: null,
            FirstConcertCity: null,
            NowLocal: Now));

        banner.Eyebrow.Should().Be("UPCOMING DATES");
        banner.Headline.Should().Contain("live dates");
        banner.IconKind.Should().Be(ArtistTourIconKind.Calendar);
    }

    [Fact]
    public void FourPlusConcerts_FarFuture_UpcomingTour()
    {
        var banner = ArtistTourBannerFormatter.Format(new ArtistTourSnapshot(
            ArtistName: "Robyn",
            ConcertCount: 12,
            AllFestivals: false,
            FirstConcertTitle: null,
            FirstConcertDateLocal: Now.AddDays(30),
            FirstConcertDateFormatted: "Fri, Jun 17",
            FirstConcertVenue: null,
            FirstConcertCity: null,
            NowLocal: Now));

        banner.Eyebrow.Should().Be("UPCOMING TOUR");
        banner.IsLive.Should().BeFalse();
    }

    [Fact]
    public void FourPlusConcerts_WithinSevenDays_OnTourNow()
    {
        var banner = ArtistTourBannerFormatter.Format(new ArtistTourSnapshot(
            ArtistName: "Robyn",
            ConcertCount: 12,
            AllFestivals: false,
            FirstConcertTitle: null,
            FirstConcertDateLocal: Now.AddDays(5),
            FirstConcertDateFormatted: "Tomorrow",
            FirstConcertVenue: null,
            FirstConcertCity: null,
            NowLocal: Now));

        banner.Eyebrow.Should().Be("ON TOUR NOW");
        banner.IsLive.Should().BeTrue();
    }

    [Fact]
    public void AllFestivals_FestivalAppearances()
    {
        var banner = ArtistTourBannerFormatter.Format(new ArtistTourSnapshot(
            ArtistName: "Robyn",
            ConcertCount: 4,
            AllFestivals: true,
            FirstConcertTitle: null,
            FirstConcertDateLocal: Now.AddDays(60),
            FirstConcertDateFormatted: null,
            FirstConcertVenue: null,
            FirstConcertCity: null,
            NowLocal: Now));

        banner.Eyebrow.Should().Be("FESTIVAL APPEARANCES");
        banner.Headline.Should().Contain("festivals");
        banner.IconKind.Should().Be(ArtistTourIconKind.Festival);
    }

    [Fact]
    public void TourTitleWins_OverGenericHeadline()
    {
        var banner = ArtistTourBannerFormatter.Format(new ArtistTourSnapshot(
            ArtistName: "Taylor Swift",
            ConcertCount: 50,
            AllFestivals: false,
            FirstConcertTitle: "The Eras Tour",
            FirstConcertDateLocal: Now.AddDays(20),
            FirstConcertDateFormatted: "Sat, Jun 6",
            FirstConcertVenue: "SoFi Stadium",
            FirstConcertCity: "Los Angeles",
            NowLocal: Now));

        banner.Headline.Should().Be("The Eras Tour");
    }

    [Fact]
    public void TourTitleSameAsArtist_FallsBackToGenericHeadline()
    {
        var banner = ArtistTourBannerFormatter.Format(new ArtistTourSnapshot(
            ArtistName: "Robyn",
            ConcertCount: 10,
            AllFestivals: false,
            FirstConcertTitle: "Robyn",  // tour title same as artist
            FirstConcertDateLocal: Now.AddDays(20),
            FirstConcertDateFormatted: null,
            FirstConcertVenue: null,
            FirstConcertCity: null,
            NowLocal: Now));

        banner.Headline.Should().Be("Robyn — on tour");
    }

    [Fact]
    public void Subline_ComposesParts()
    {
        var banner = ArtistTourBannerFormatter.Format(new ArtistTourSnapshot(
            ArtistName: "Robyn",
            ConcertCount: 8,
            AllFestivals: false,
            FirstConcertTitle: null,
            FirstConcertDateLocal: Now.AddDays(20),
            FirstConcertDateFormatted: "Sat, Jun 6",
            FirstConcertVenue: "Wembley",
            FirstConcertCity: "London",
            NowLocal: Now));

        banner.Subline.Should().Contain("Next: Sat, Jun 6");
        banner.Subline.Should().Contain("Wembley");
        banner.Subline.Should().Contain("London");
        banner.Subline.Should().Contain("8 dates total");
    }
}
