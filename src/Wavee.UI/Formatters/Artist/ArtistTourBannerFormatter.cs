using System;
using System.Collections.Generic;

namespace Wavee.UI.Formatters.Artist;

/// <summary>
/// Icon kind for the tour-banner — resolved to a concrete glyph by the WinUI
/// caller so this formatter stays framework-neutral (FluentGlyphs constants
/// live in Wavee.UI.WinUI).
/// </summary>
internal enum ArtistTourIconKind
{
    Calendar = 0,    // multi-date tour
    Microphone = 1,  // single show
    Festival = 2,    // festival appearances
}

/// <summary>
/// Snapshot of an artist's upcoming concerts at a specific instant. All
/// inputs the formatter needs are captured here so the formatter can be
/// driven from a unit test with a fixed clock.
/// </summary>
/// <param name="ArtistName">Artist display name (used in fallback headlines).</param>
/// <param name="ConcertCount">Total number of upcoming concerts (Pathfinder rarely returns past dates).</param>
/// <param name="AllFestivals">True when EVERY concert is a festival appearance.</param>
/// <param name="FirstConcertTitle">Title of the first concert's tour, if any. When distinct from <paramref name="ArtistName"/> it becomes the headline.</param>
/// <param name="FirstConcertDateLocal">Local-time date of the soonest upcoming concert. Used for the proximity check.</param>
/// <param name="FirstConcertDateFormatted">Display string for the soonest upcoming concert ("Fri, Jun 14").</param>
/// <param name="FirstConcertVenue">Venue name of the soonest upcoming concert.</param>
/// <param name="FirstConcertCity">City of the soonest upcoming concert.</param>
/// <param name="NowLocal">Local-time "now" anchor — pass <c>DateTimeOffset.Now.Date</c> from the VM; tests pass a fixed value.</param>
internal sealed record ArtistTourSnapshot(
    string ArtistName,
    int ConcertCount,
    bool AllFestivals,
    string? FirstConcertTitle,
    DateTimeOffset? FirstConcertDateLocal,
    string? FirstConcertDateFormatted,
    string? FirstConcertVenue,
    string? FirstConcertCity,
    DateTimeOffset NowLocal);

internal sealed record ArtistTourBannerText(
    string Headline,
    string Eyebrow,
    string Subline,
    bool IsLive,
    ArtistTourIconKind IconKind);

/// <summary>
/// Derives the headline / eyebrow / subline / live-state / icon for the
/// artist-page tour banner. Decision tree:
/// <list type="bullet">
///   <item>All festivals → "FESTIVAL APPEARANCES"</item>
///   <item>1 concert → "UPCOMING SHOW"</item>
///   <item>2-3 concerts → "UPCOMING DATES"</item>
///   <item>≥4 concerts, first within 7 days → "ON TOUR NOW"</item>
///   <item>≥4 concerts, first &gt;7 days out → "UPCOMING TOUR"</item>
/// </list>
/// </summary>
internal static class ArtistTourBannerFormatter
{
    public const int OnTourNowDayThreshold = 7;

    public static ArtistTourBannerText Format(ArtistTourSnapshot s)
    {
        if (s.ConcertCount == 0)
        {
            return new ArtistTourBannerText(
                Headline: string.Empty,
                Eyebrow: string.Empty,
                Subline: string.Empty,
                IsLive: false,
                IconKind: ArtistTourIconKind.Calendar);
        }

        var eyebrow = ResolveEyebrow(s);
        var headline = ResolveHeadline(s);
        var subline = ResolveSubline(s);
        var isLive = string.Equals(eyebrow, "ON TOUR NOW", StringComparison.Ordinal);
        var icon = ResolveIcon(eyebrow);

        return new ArtistTourBannerText(headline, eyebrow, subline, isLive, icon);
    }

    private static string ResolveEyebrow(ArtistTourSnapshot s)
    {
        if (s.AllFestivals) return "FESTIVAL APPEARANCES";
        if (s.ConcertCount == 1) return "UPCOMING SHOW";
        if (s.ConcertCount <= 3) return "UPCOMING DATES";

        // ≥4 concerts → tour. Differentiate "now" vs "upcoming" by proximity to
        // the first concert. Pathfinder usually only returns future dates, so
        // "first concert within 7 days" is the proxy for "actively touring".
        if (s.FirstConcertDateLocal is not { } firstDate) return "UPCOMING TOUR";
        var days = (firstDate.Date - s.NowLocal.Date).TotalDays;
        return days <= OnTourNowDayThreshold ? "ON TOUR NOW" : "UPCOMING TOUR";
    }

    private static string ResolveHeadline(ArtistTourSnapshot s)
    {
        // Tour title (when distinct from the artist name) wins over the generic
        // fallback — e.g. "The Eras Tour" instead of "Taylor Swift — on tour".
        if (!string.IsNullOrWhiteSpace(s.FirstConcertTitle)
            && !string.Equals(s.FirstConcertTitle, s.ArtistName, StringComparison.OrdinalIgnoreCase))
        {
            return s.FirstConcertTitle!;
        }

        if (s.AllFestivals) return $"Catch {s.ArtistName} at festivals";
        if (s.ConcertCount == 1) return $"{s.ArtistName} live";
        if (s.ConcertCount <= 3) return $"{s.ArtistName} — live dates";
        return $"{s.ArtistName} — on tour";
    }

    private static string ResolveSubline(ArtistTourSnapshot s)
    {
        var parts = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(s.FirstConcertDateFormatted))
            parts.Add($"Next: {s.FirstConcertDateFormatted}");
        if (!string.IsNullOrWhiteSpace(s.FirstConcertVenue)) parts.Add(s.FirstConcertVenue!);
        if (!string.IsNullOrWhiteSpace(s.FirstConcertCity)) parts.Add(s.FirstConcertCity!);
        if (s.ConcertCount > 1) parts.Add($"{s.ConcertCount} dates total");
        return string.Join(" · ", parts);
    }

    private static ArtistTourIconKind ResolveIcon(string eyebrow) => eyebrow switch
    {
        "FESTIVAL APPEARANCES" => ArtistTourIconKind.Festival,
        "UPCOMING SHOW"        => ArtistTourIconKind.Microphone,
        _                      => ArtistTourIconKind.Calendar,
    };
}
