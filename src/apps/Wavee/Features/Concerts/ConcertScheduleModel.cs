using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using FluentGpu.Localization;
using FluentGpu.Pal;
using Wavee.Core;

namespace Wavee.Features.Concerts;

/// <summary>Pure (engine-free) shaping for the artist-schedule surface. Kept dependency-light (Wavee.Core + BCL) so the
/// sorting/dedup logic is unit-tested without mounting an engine window.</summary>
public static class ConcertSchedules
{
    /// <summary>Order a schedule chronologically (earliest first) and drop duplicate canonical concert URIs, keeping the
    /// first occurrence. <see cref="List{T}.OrderBy"/> is stable, so same-instant events preserve their source order.</summary>
    public static IReadOnlyList<Concert> Chronological(IReadOnlyList<Concert>? concerts)
    {
        if (concerts is null || concerts.Count == 0) return Array.Empty<Concert>();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var deduped = new List<Concert>(concerts.Count);
        foreach (var c in concerts)
            if (!string.IsNullOrEmpty(c.Uri) && seen.Add(c.Uri)) deduped.Add(c);

        return deduped.OrderBy(c => c.Date).ToArray();
    }
}

/// <summary>Maps a one-shot geolocation outcome to a human, panel-facing message. Distinct strings per failure so the
/// picker can explain WHY location failed and steer the user to manual search. Success/Canceled surface no error.</summary>
public static class LocationErrors
{
    public static string? ForStatus(GeolocationStatus status) => status switch
    {
        GeolocationStatus.Success => null,
        GeolocationStatus.Canceled => null,
        GeolocationStatus.PermissionDenied => Loc.Get(Strings.Concerts.Location.PermissionDenied),
        GeolocationStatus.Unavailable => Loc.Get(Strings.Concerts.Location.Unavailable),
        GeolocationStatus.TimedOut => Loc.Get(Strings.Concerts.Location.TimedOut),
        _ => Loc.Get(Strings.Concerts.Location.Failed),
    };
}

/// <summary>A run of one or more CONSECUTIVE-day concerts at the SAME venue+city — a multi-night residency collapses into
/// a single month-board tile. A standalone show is a run of one. <see cref="Nights"/> is chronological.</summary>
public sealed record ConcertRun(IReadOnlyList<Concert> Nights)
{
    public Concert First => Nights[0];
    public Concert Last => Nights[Nights.Count - 1];
    public bool IsMultiNight => Nights.Count > 1;
    public int NightCount => Nights.Count;
    /// <summary>The run's navigation identity — the first night's canonical URI.</summary>
    public string Uri => First.Uri;
}

/// <summary>A calendar-month board: the collapsed <see cref="Runs"/> (tiles) whose PRESERVED-offset date falls in one
/// year+month. <see cref="Key"/> is the stable "yyyy-MM" identity used to key per-month UI state and the shy-pill anchor.</summary>
public sealed record ConcertMonthGroup(int Year, int Month, string Key, IReadOnlyList<ConcertRun> Runs)
{
    /// <summary>Total shows in the month — a run counts as its night count (the "N shows" header value).</summary>
    public int ShowCount
    {
        get { int n = 0; for (int i = 0; i < Runs.Count; i++) n += Runs[i].NightCount; return n; }
    }

    /// <summary>The board overflows the tile cap (a run counts as ONE tile), so it collapses behind a "Show all" footer.</summary>
    public bool OverflowsCap => Runs.Count > ConcertScheduleShaping.TileCap;
}

/// <summary>A selected month's visible runs split into balanced chronological columns. Every run in Left precedes every
/// run in Right, so visual top-to-bottom order, tab order, and screen-reader order remain chronological.</summary>
public readonly record struct ConcertRunColumns(IReadOnlyList<ConcertRun> Left, IReadOnlyList<ConcertRun> Right);

/// <summary>Derived tour statistics for the hero stats line: show count, DISTINCT non-empty city count, and the
/// first→last date span.</summary>
public readonly record struct ConcertTourStats(int Shows, int Cities, DateTimeOffset First, DateTimeOffset Last, bool HasCities);

/// <summary>Pure (engine-free) shaping for the R3 artist-schedule rework: next-show spotlight selection, month-board
/// grouping with consecutive-night run collapse, the 6-tile cap, hero stats, relative-time phrasing, and the responsive
/// board column count. Every time-dependent helper takes <c>now</c> explicitly so it is deterministic under test.</summary>
public static class ConcertScheduleShaping
{
    /// <summary>Max tiles a board shows before collapsing behind a "Show all" footer (a run counts as one tile).</summary>
    public const int TileCap = 6;

    /// <summary>The first UPCOMING concert (the chronological list is already earliest-first). Returns <c>null</c> when
    /// every date is in the past — the boards then show everything and no spotlight is drawn.</summary>
    public static Concert? Spotlight(IReadOnlyList<Concert>? chronological, DateTimeOffset now)
    {
        if (chronological is null) return null;
        for (int i = 0; i < chronological.Count; i++)
            if (chronological[i].Date >= now) return chronological[i];
        return null;
    }

    /// <summary>The concerts placed on the month boards: the whole chronological list MINUS the spotlighted URI (so no
    /// event ever appears twice). With no spotlight the full list is returned unchanged.</summary>
    public static IReadOnlyList<Concert> BoardConcerts(IReadOnlyList<Concert>? chronological, Concert? spotlight)
    {
        if (chronological is null || chronological.Count == 0) return Array.Empty<Concert>();
        if (spotlight is null) return chronological;
        var list = new List<Concert>(chronological.Count);
        for (int i = 0; i < chronological.Count; i++)
            if (!string.Equals(chronological[i].Uri, spotlight.Uri, StringComparison.Ordinal))
                list.Add(chronological[i]);
        return list;
    }

    /// <summary>Group concerts into month boards by the year+month of the PRESERVED local-offset date, collapsing
    /// consecutive-night same-venue+city runs within each month. Input is sorted defensively so the result is stable
    /// regardless of caller order; a Dec→Jan pair yields two distinct boards.</summary>
    public static IReadOnlyList<ConcertMonthGroup> GroupByMonth(IReadOnlyList<Concert>? concerts)
    {
        if (concerts is null || concerts.Count == 0) return Array.Empty<ConcertMonthGroup>();

        var sorted = concerts.OrderBy(c => c.Date).ToList();
        var groups = new List<ConcertMonthGroup>();
        int i = 0;
        while (i < sorted.Count)
        {
            int year = sorted[i].Date.Year, month = sorted[i].Date.Month;
            var monthConcerts = new List<Concert>();
            while (i < sorted.Count && sorted[i].Date.Year == year && sorted[i].Date.Month == month)
                monthConcerts.Add(sorted[i++]);
            groups.Add(new ConcertMonthGroup(year, month, MonthKey(year, month), DetectRuns(monthConcerts)));
        }
        return groups;
    }

    /// <summary>Collapse a month's (chronological) concerts into runs: a run extends while each next concert is the SAME
    /// venue+city AND the very NEXT calendar day. A one-day gap or a venue change starts a new run.</summary>
    public static IReadOnlyList<ConcertRun> DetectRuns(IReadOnlyList<Concert> monthConcerts)
    {
        var runs = new List<ConcertRun>();
        int i = 0;
        while (i < monthConcerts.Count)
        {
            var nights = new List<Concert> { monthConcerts[i] };
            int j = i + 1;
            while (j < monthConcerts.Count && IsNextNight(monthConcerts[j - 1], monthConcerts[j]))
                nights.Add(monthConcerts[j++]);
            runs.Add(new ConcertRun(nights));
            i = j;
        }
        return runs;
    }

    static bool IsNextNight(Concert prev, Concert next) =>
        SameVenueCity(prev, next) && next.Date.Date == prev.Date.Date.AddDays(1);

    static bool SameVenueCity(Concert a, Concert b) =>
        string.Equals(a.Venue ?? "", b.Venue ?? "", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.City ?? "", b.City ?? "", StringComparison.OrdinalIgnoreCase);

    /// <summary>The stable "yyyy-MM" month identity.</summary>
    public static string MonthKey(int year, int month) =>
        year.ToString("D4", CultureInfo.InvariantCulture) + "-" + month.ToString("D2", CultureInfo.InvariantCulture);

    /// <summary>Tour stats over the WHOLE schedule (spotlight included): show count, distinct non-empty city count, and
    /// the first/last date span.</summary>
    public static ConcertTourStats Stats(IReadOnlyList<Concert>? concerts)
    {
        if (concerts is null || concerts.Count == 0)
            return new ConcertTourStats(0, 0, default, default, false);

        var cities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DateTimeOffset first = concerts[0].Date, last = concerts[0].Date;
        for (int i = 0; i < concerts.Count; i++)
        {
            var c = concerts[i];
            if (!string.IsNullOrWhiteSpace(c.City)) cities.Add(c.City.Trim());
            if (c.Date < first) first = c.Date;
            if (c.Date > last) last = c.Date;
        }
        return new ConcertTourStats(concerts.Count, cities.Count, first, last, cities.Count > 0);
    }

    /// <summary>The hero stats line: "N shows · M cities · MMM – MMM yyyy". The cities segment appears only when it
    /// adds information — 2 ≤ distinct cities &lt; shows ("18 shows · 18 cities" is redundant; "5 shows · 1 city" is
    /// noise); a single-month span collapses to "MMM yyyy".</summary>
    public static string StatsLine(in ConcertTourStats s)
    {
        if (s.Shows == 0) return "";
        var sb = new StringBuilder();
        sb.Append(s.Shows).Append(s.Shows == 1 ? " show" : " shows");
        if (s.HasCities && s.Cities >= 2 && s.Cities < s.Shows)
            sb.Append(" · ").Append(s.Cities).Append(" cities");
        sb.Append(" · ").Append(SpanLabel(s.First, s.Last));
        return sb.ToString();
    }

    static string SpanLabel(DateTimeOffset first, DateTimeOffset last)
    {
        var c = CultureInfo.CurrentCulture;
        if (first.Year == last.Year && first.Month == last.Month)
            return first.ToString("MMM yyyy", c);
        if (first.Year == last.Year)
            return first.ToString("MMM", c) + " – " + last.ToString("MMM yyyy", c);
        return first.ToString("MMM yyyy", c) + " – " + last.ToString("MMM yyyy", c);
    }

    /// <summary>Human relative-time phrasing for the spotlight caption, relative to <paramref name="now"/> — "today",
    /// "tomorrow", "in 5 days", "in 6 weeks", "in 3 months". Day-granular on the preserved-offset calendar date.</summary>
    public static string RelativeTime(DateTimeOffset date, DateTimeOffset now)
    {
        int days = (int)(date.Date - now.Date).TotalDays;
        if (days <= 0) return "today";
        if (days == 1) return "tomorrow";
        if (days < 7) return "in " + days.ToString(CultureInfo.CurrentCulture) + " days";
        if (days < 60)
        {
            int weeks = (int)Math.Round(days / 7.0, MidpointRounding.AwayFromZero);
            return weeks <= 1 ? "in 1 week" : "in " + weeks.ToString(CultureInfo.CurrentCulture) + " weeks";
        }
        int months = (int)Math.Round(days / 30.0, MidpointRounding.AwayFromZero);
        return months <= 1 ? "in 1 month" : "in " + months.ToString(CultureInfo.CurrentCulture) + " months";
    }

    /// <summary>The responsive month-board column count: as many ≥ <paramref name="minColumnWidth"/> columns as fit,
    /// clamped to [1, boardCount] so a lone board never leaves empty tracks. 1 ⇒ the boards stack vertically.</summary>
    public static int MonthBoardColumns(float width, float minColumnWidth, float gap, int boardCount)
    {
        if (boardCount <= 0) return 1;
        if (width <= 0f || minColumnWidth <= 0f) return 1;
        int fit = (int)MathF.Floor((width + gap) / (minColumnWidth + gap));
        return Math.Clamp(fit, 1, boardCount);
    }

    /// <summary>Resolve the controlled month selection after a reload. A still-valid user choice wins; otherwise the
    /// spotlight's month wins when that month has more dates, then the first remaining month.</summary>
    public static int ResolveMonthIndex(IReadOnlyList<ConcertMonthGroup>? groups, string? requestedKey, Concert? spotlight)
    {
        if (groups is null || groups.Count == 0) return -1;
        if (!string.IsNullOrWhiteSpace(requestedKey))
            for (int i = 0; i < groups.Count; i++)
                if (string.Equals(groups[i].Key, requestedKey, StringComparison.Ordinal)) return i;

        if (spotlight is not null)
            for (int i = 0; i < groups.Count; i++)
                if (groups[i].Year == spotlight.Date.Year && groups[i].Month == spotlight.Date.Month) return i;
        return 0;
    }

    /// <summary>Compact selector labels: the first month and each year boundary include an abbreviated year; intervening
    /// months carry only the localized abbreviated month.</summary>
    public static IReadOnlyList<string> MonthTabLabels(IReadOnlyList<ConcertMonthGroup>? groups, CultureInfo? culture = null)
    {
        if (groups is null || groups.Count == 0) return Array.Empty<string>();
        var c = culture ?? CultureInfo.CurrentCulture;
        var labels = new string[groups.Count];
        int previousYear = int.MinValue;
        for (int i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            var first = new DateTime(g.Year, g.Month, 1);
            labels[i] = g.Year != previousYear ? first.ToString("MMM ’yy", c) : first.ToString("MMM", c);
            previousYear = g.Year;
        }
        return labels;
    }

    /// <summary>Split the first <paramref name="visibleCount"/> runs into two balanced chronological columns. Narrow
    /// callers ignore Right and render the original run list directly.</summary>
    public static ConcertRunColumns BalancedColumns(IReadOnlyList<ConcertRun> runs, int visibleCount)
    {
        int count = Math.Clamp(visibleCount, 0, runs.Count);
        int leftCount = (count + 1) / 2;
        var left = new ConcertRun[leftCount];
        var right = new ConcertRun[count - leftCount];
        for (int i = 0; i < left.Length; i++) left[i] = runs[i];
        for (int i = 0; i < right.Length; i++) right[i] = runs[leftCount + i];
        return new ConcertRunColumns(left, right);
    }

    // ── near-you guard ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Near-you is a DIFFERENTIATOR, not a theme: the tint/pin is applied only when it marks a minority of the
    /// tour — <c>0 &lt; near ≤ max(3, ⌈shows / 3⌉)</c>. A response whose Nearby branch covers most (or all) of the
    /// schedule would otherwise paint an information-free accent wall over every tile.</summary>
    public static bool NearIsInformative(int nearCount, int showCount) =>
        nearCount > 0 && nearCount <= Math.Max(3, (int)Math.Ceiling(showCount / 3.0));

    /// <summary>The guarded near set the boards actually consume: the union of the Nearby branch URIs and per-concert
    /// <see cref="Concert.IsNearUser"/> flags among <paramref name="boardConcerts"/> — or EMPTY when the count fails
    /// <see cref="NearIsInformative"/> (so NO tile gets tinted/pinned on a near-everything response).</summary>
    public static HashSet<string> GuardedNearSet(IReadOnlyList<Concert> boardConcerts, IReadOnlyList<Concert>? nearby)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (nearby is not null)
            for (int i = 0; i < nearby.Count; i++)
                if (!string.IsNullOrEmpty(nearby[i].Uri)) set.Add(nearby[i].Uri);

        int near = 0;
        for (int i = 0; i < boardConcerts.Count; i++)
        {
            var c = boardConcerts[i];
            if (c.IsNearUser && !string.IsNullOrEmpty(c.Uri)) set.Add(c.Uri);
            if (!string.IsNullOrEmpty(c.Uri) && set.Contains(c.Uri)) near++;
        }
        return NearIsInformative(near, boardConcerts.Count) ? set : new HashSet<string>(StringComparer.Ordinal);
    }

    // ── tile text ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>What a month-board tile prints. ArtistConcerts events routinely have NO venue (only a city), and their
    /// title is usually the page artist's own name — so the primary line is venue when present, else the CITY, and the
    /// title is the primary fallback ONLY when both are empty (never the artist's name repeated down the board).
    /// <see cref="CityIsPrimary"/> lets the run tile swap its secondary composition accordingly.</summary>
    public readonly record struct ConcertTileText(string Primary, string Secondary, bool CityIsPrimary);

    /// <summary>Resolve a tile's primary/secondary lines. Venue-primary ⇒ secondary is "City · HH:mm"; city-primary ⇒
    /// secondary is "HH:mm" plus the support acts when informative ("19:30 · with Band of Silver"); title-fallback ⇒
    /// secondary is just the time. <paramref name="scheduleArtistName"/> is the page artist, excluded from support acts.</summary>
    public static ConcertTileText TileText(Concert concert, string? scheduleArtistName)
    {
        var culture = CultureInfo.CurrentCulture;
        string time = concert.Date.ToString("t", culture);

        if (!string.IsNullOrWhiteSpace(concert.Venue))
        {
            string secondary = string.IsNullOrWhiteSpace(concert.City) ? time : concert.City + " · " + time;
            return new ConcertTileText(concert.Venue, secondary, CityIsPrimary: false);
        }

        if (!string.IsNullOrWhiteSpace(concert.City))
        {
            string? support = SupportActs(concert, scheduleArtistName);
            return new ConcertTileText(concert.City, support is null ? time : time + " · " + support, CityIsPrimary: true);
        }

        string title = string.IsNullOrWhiteSpace(concert.Title) ? "Concert" : concert.Title!;
        return new ConcertTileText(title, time, CityIsPrimary: false);
    }

    /// <summary>"with A" / "with A, B" / "with A, B +n more" from the lineup names minus the page artist
    /// (case-insensitive, deduped, max 2 named). Null when nothing informative remains.</summary>
    public static string? SupportActs(Concert concert, string? scheduleArtistName)
    {
        if (concert.Artists is not { Count: > 0 } artists) return null;

        var names = new List<string>(artists.Count);
        for (int i = 0; i < artists.Count; i++)
        {
            string? raw = artists[i].Name;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string name = raw.Trim();
            if (scheduleArtistName is { Length: > 0 } self &&
                string.Equals(name, self.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            bool dup = false;
            for (int j = 0; j < names.Count; j++)
                if (string.Equals(names[j], name, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
            if (!dup) names.Add(name);
        }
        if (names.Count == 0) return null;
        if (names.Count == 1) return "with " + names[0];
        if (names.Count == 2) return "with " + names[0] + ", " + names[1];
        return "with " + names[0] + ", " + names[1] + " +" + (names.Count - 2).ToString(CultureInfo.CurrentCulture) + " more";
    }
}
