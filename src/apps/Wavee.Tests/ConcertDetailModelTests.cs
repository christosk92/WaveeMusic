// ── Wavee.Tests/ConcertDetailModelTests.cs — offers and detail facts (ch 17 §8), ported from 0.2.9 ────────────────────
//
// Ported VERBATIM from `_old/Wavee.Tests/ConcertDetailModelTests.cs` (13 facts). The offer is now a set of values
// (prices, currency, sale window) instead of 0.2.9's `ConcertOffer` record — the stored `OfferEdge` form is a thin
// adapter over the same functions; the assertions are unchanged.

using System.Globalization;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public sealed class ConcertDetailModelTests
{
    static string? Price(decimal? min = null, decimal? max = null, string? currency = null) =>
        ConcertOffers.PriceLabel(min, max, currency);

    // ── ticket-URL validation ────────────────────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("https://tickets.example/event")]
    [InlineData("http://tickets.example/event")]
    public void ValidTicketUrl_AcceptsAbsoluteWebUrls(string url) =>
        Assert.Equal(url, ConcertOffers.ValidTicketUrl(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/tickets/relative")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///c:/windows")]
    [InlineData("spotify:concert:x")]
    [InlineData("not a url")]
    public void ValidTicketUrl_RejectsEverythingElse(string? url) =>
        Assert.Null(ConcertOffers.ValidTicketUrl(url));

    // ── price formatting ─────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void PriceLabel_RangeUsesInvariantDigitsAndCurrencyCode()
    {
        Assert.Equal("45 - 75 EUR", Price(min: 45m, max: 75m, currency: "EUR"));
        Assert.Equal("45.5 - 75.25 EUR", Price(min: 45.5m, max: 75.25m, currency: "EUR"));
    }

    [Fact]
    public void PriceLabel_SinglePriceCollapsesToOneValue()
    {
        Assert.Equal("45 EUR", Price(min: 45m, max: 45m, currency: "EUR"));
        Assert.Equal("45 EUR", Price(min: 45m, currency: "EUR"));
        Assert.Equal("75 EUR", Price(max: 75m, currency: "EUR"));
    }

    [Fact]
    public void PriceLabel_NoPriceOrNoCurrency()
    {
        Assert.Null(Price());
        Assert.Equal("45", Price(min: 45m));
    }

    [Fact]
    public void PriceLabel_ReversedBoundsAreReordered() =>
        Assert.Equal("45 - 75 EUR", Price(min: 75m, max: 45m, currency: "EUR"));

    // ── availability ─────────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void AvailabilityLabel_MapsKnownStates_AndHidesUnknown()
    {
        Assert.Equal("Available", ConcertOffers.AvailabilityLabel(ConcertOfferAvailability.Available));
        Assert.Equal("Sold out or unavailable", ConcertOffers.AvailabilityLabel(ConcertOfferAvailability.Unavailable));
        Assert.Null(ConcertOffers.AvailabilityLabel(ConcertOfferAvailability.Unknown));
    }

    // ── sale window ──────────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void SaleWindowLabel_FormatsPresentDates_WithPreservedOffset()
    {
        var start = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2030, 8, 31, 23, 30, 0, TimeSpan.FromHours(-4));
        var invariant = CultureInfo.InvariantCulture;

        Assert.Equal("On sale 1 Jan 2030 - 31 Aug 2030", ConcertOffers.SaleWindowLabel(start, end, invariant));
        Assert.Equal("On sale from 1 Jan 2030", ConcertOffers.SaleWindowLabel(start, null, invariant));
        Assert.Equal("On sale until 31 Aug 2030", ConcertOffers.SaleWindowLabel(null, end, invariant));
        Assert.Null(ConcertOffers.SaleWindowLabel(null, null, invariant));
    }

    // ── status ───────────────────────────────────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("UNKNOWN")]
    [InlineData("unknown")]
    [InlineData("CONFIRMED")]
    [InlineData("SCHEDULED")]
    public void DisplayStatus_HidesMissingAndDefaultStates(string? status) =>
        Assert.Null(ConcertDetailInfo.DisplayStatus(status));

    [Theory]
    [InlineData("CANCELLED", "Cancelled")]
    [InlineData("POSTPONED", "Postponed")]
    [InlineData("SOLD_OUT", "Sold out")]
    public void DisplayStatus_HumanizesNotableStates(string status, string expected) =>
        Assert.Equal(expected, ConcertDetailInfo.DisplayStatus(status));

    // ── location line ────────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void LocationLine_JoinsNonEmptySegments()
    {
        Assert.Equal("Example City, Example Region, EX",
            ConcertDetailInfo.LocationLine("Example City", "Example Region", "EX"));
        Assert.Equal("Example City", ConcertDetailInfo.LocationLine("Example City", null, ""));
        Assert.Equal("", ConcertDetailInfo.LocationLine(null, null, null));
    }

    [Fact]
    public void LocationLine_CollapsesCaseInsensitiveDuplicates() =>
        Assert.Equal("Luxembourg", ConcertDetailInfo.LocationLine("Luxembourg", "luxembourg", "LUXEMBOURG"));

    // ── detail breakpoint hysteresis ─────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void DetailWide_EntersAtEnterThreshold_LeavesBelowLeaveThreshold()
    {
        Assert.True(ConcertLayout.DetailWide(920f, wasWide: false, initialized: true));
        Assert.False(ConcertLayout.DetailWide(919f, wasWide: false, initialized: true));
        Assert.True(ConcertLayout.DetailWide(860f, wasWide: true, initialized: true));
        Assert.False(ConcertLayout.DetailWide(859f, wasWide: true, initialized: true));
        Assert.False(ConcertLayout.DetailWide(900f, wasWide: false, initialized: true));
    }
}
