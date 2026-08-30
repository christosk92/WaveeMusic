using System;
using System.Collections.Generic;
using System.Globalization;
using Wavee.Core;

namespace Wavee.Features.Concerts;

/// <summary>Pure (engine-free) shaping for ticket offers. Kept dependency-light (Wavee.Core + BCL) so the URL
/// validation and price/sale formatting are unit-tested without mounting an engine window.</summary>
public static class ConcertOffers
{
    /// <summary>The offer's external ticket URL, or null unless it is an absolute http/https link. Anything else
    /// (missing, relative, javascript:, file:, custom schemes) renders the offer non-actionable — never a dead button
    /// and never a non-web launch through the OS OpenUri seam.</summary>
    public static string? ValidTicketUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url, UriKind.Absolute, out var parsed)
        && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
            ? url
            : null;

    /// <summary>"45 EUR" / "45 - 75 EUR": invariant-culture digits with the provider's currency CODE appended — never a
    /// locale currency-symbol guess (the provider's code is authoritative; the user's locale is not). Null when the
    /// offer carries no price at all.</summary>
    public static string? PriceLabel(ConcertOffer offer)
    {
        static string N(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        decimal? min = offer.MinPrice;
        decimal? max = offer.MaxPrice;
        if (min is null && max is null) return null;

        string code = string.IsNullOrWhiteSpace(offer.Currency) ? "" : " " + offer.Currency;
        return min is { } lo && max is { } hi && hi != lo
            ? N(Math.Min(lo, hi)) + " - " + N(Math.Max(lo, hi)) + code
            : N(min ?? max!.Value) + code;
    }

    /// <summary>Human availability. Unknown maps to null so an unremarkable state adds no line.</summary>
    public static string? AvailabilityLabel(ConcertOfferAvailability availability) => availability switch
    {
        ConcertOfferAvailability.Available => "Available",
        ConcertOfferAvailability.Unavailable => "Sold out or unavailable",
        _ => null,
    };

    /// <summary>The sale window ("On sale 1 Jan 2030 - 31 Aug 2030"), formatted with each timestamp's PRESERVED offset
    /// (DateTimeOffset formats its stored clock time — no machine-local conversion). Null when no sale date is present.</summary>
    public static string? SaleWindowLabel(ConcertOffer offer, CultureInfo? culture = null)
    {
        var c = culture ?? CultureInfo.CurrentCulture;
        static string D(DateTimeOffset d, CultureInfo c) => d.ToString("d MMM yyyy", c);
        return (offer.SaleStartsAt, offer.SaleEndsAt) switch
        {
            ({ } start, { } end) => "On sale " + D(start, c) + " - " + D(end, c),
            ({ } start, null) => "On sale from " + D(start, c),
            (null, { } end) => "On sale until " + D(end, c),
            _ => null,
        };
    }
}

/// <summary>Pure (engine-free) shaping for the concert-detail facts.</summary>
public static class ConcertDetailInfo
{
    // Provider states that are the unremarkable default — the UI shows status ONLY for a notable state
    // (cancelled / postponed / rescheduled / ...), never a "confirmed" badge on every event.
    static readonly string[] DefaultStatuses = ["UNKNOWN", "CONFIRMED", "SCHEDULED"];

    /// <summary>Humanized status for a NON-default state ("CANCELLED" → "Cancelled", "SOLD_OUT" → "Sold out");
    /// null for missing/default states so the fact line is omitted entirely.</summary>
    public static string? DisplayStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return null;
        string trimmed = status.Trim();
        foreach (var d in DefaultStatuses)
            if (string.Equals(trimmed, d, StringComparison.OrdinalIgnoreCase)) return null;
        string words = trimmed.Replace('_', ' ').ToLowerInvariant();
        return char.ToUpperInvariant(words[0]) + words[1..];
    }

    /// <summary>"City, Region, Country" with empty and case-insensitively duplicate segments removed (a city-state like
    /// "Luxembourg, Luxembourg" collapses to one segment).</summary>
    public static string LocationLine(string? city, string? region, string? country)
    {
        var parts = new List<string>(3);
        void Add(string? p)
        {
            if (!string.IsNullOrWhiteSpace(p) &&
                !parts.Exists(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase)))
                parts.Add(p!);
        }
        Add(city);
        Add(region);
        Add(country);
        return string.Join(", ", parts);
    }
}
