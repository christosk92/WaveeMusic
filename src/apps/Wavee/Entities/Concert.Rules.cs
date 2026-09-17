// ── Entities/Concert.Rules.cs ──────────────────────────────────────────────────────────────────────────────────────
// the 807 lines of ported concert rules: layout hysteresis, the editorial art geometry, the hub's filter tuple, the
// schedule shaping (spotlight, month boards, runs, the 6-tile cap, the near-you guard, tile text), offers, detail facts
//
// Role: CORE
// Owner: N (stream N-C)
// Wave: 5
// Budget: shares Concert.cs's 1,050 (plan §2, ch 17 §9 "honest estimate"); a named partial because Concert.cs would
//   otherwise pass 1,365 lines (the columns + staging + commit alone are ~1,000)
// Spec: ch 17 §8 (the pure rules, ported verbatim from 0.2.9 Features/Concerts/{ConcertHubModel, ConcertScheduleModel,
//   ConcertDetailModel, ConcertLayout}.cs), ch 17 §6 (the ICU copy), ch 17 §0.14 (the provider's clock)
//
// WHAT CHANGED IN THE PORT, AND ONLY THIS:
//   · The rules take VALUES (ConcertShow, ConcertPlace, ConcertConcept) instead of 0.2.9's Wavee.Core records — a page
//     builds them from handles (`ConcertShows.From`, Concert.cs); the decisions and their tests are unchanged.
//   · Every number-bearing sentence goes through `ConcertCopy` (ch 17 §6): an ICU plural key in the app, the 0.2.9
//     English in a unit test that loads no culture table. `Concert.InstallPages` swaps in `ConcertCopy.Localized`.
//   · The board's run value is `ConcertBoardRun`, not 0.2.9's `ConcertRun`: that name is the staging cursor the
//     WP-5.N contract (§6) binds.
//   · `MonthBoardColumns` is NOT ported — see the decision block on `ConcertScheduleShaping`.

using System.Globalization;
using FluentGpu.Localization;
using FluentGpu.Pal;

namespace Wavee;

// ── the values the rules take ─────────────────────────────────────────────────────────────────────────────────────

/// <summary>One show as the rules see it (0.2.9's <c>Concert</c> record minus the art). <see cref="Date"/> carries the
/// PROVIDER'S offset — format it as it is, never in the viewer's zone (ch 17 §0.14).</summary>
public sealed record ConcertShow(
    string Uri,
    string? Title,
    string Venue,
    string City,
    DateTimeOffset Date,
    bool IsFestival = false,
    bool IsNearUser = false,
    string? Region = null,
    string? Country = null,
    IReadOnlyList<string>? ArtistNames = null);

/// <summary>A place the user can look from (0.2.9 <c>ConcertPlace</c>). <see cref="Id"/> is the provider's opaque place
/// id (a GeoNames id today) and may be empty for a geohash-only place.</summary>
public sealed record ConcertPlace(
    string Id,
    string Name,
    string? Region = null,
    string? Country = null,
    string? GeoHash = null,
    double? Latitude = null,
    double? Longitude = null);

/// <summary>A weighted genre token for a place (0.2.9 <c>ConcertConcept</c>).</summary>
public sealed record ConcertConcept(string Uri, string Name, double Weight = 0d);

/// <summary>A date window on the wire as <c>{from,to}</c>. Both ends inclusive; a single day has From == To.</summary>
public sealed record ConcertDateRange(DateOnly From, DateOnly To);

/// <summary>The date-filter presets the when-pill offers. Custom is a user-picked span.</summary>
public enum ConcertWhenKind { Any, Today, ThisWeekend, NextWeekend, Custom }

/// <summary>The when-filter tuple the bar edits. <see cref="Name"/> is the fused pill's segment text; <see cref="Range"/>
/// is what goes on the wire AND renders as "Jul 17 – 19".</summary>
public sealed record ConcertWhen(ConcertWhenKind Kind, string Name, ConcertDateRange? Range)
{
    public static readonly ConcertWhen Any = new(ConcertWhenKind.Any, "", null);
}

/// <summary>The complete filter tuple the pinned bar edits and the page turns into ONE feed subject.</summary>
public sealed record ConcertHubFilters(ConcertPlace? Place, int RadiusKm, ConcertWhen When, IReadOnlyList<string> ConceptUris);

/// <summary>One feed request (0.2.9 <c>ConcertFeedQuery</c>). A null radius asks the provider's default; an empty concept
/// list is "all genres"; <see cref="PaginationKey"/> is opaque and replayed unchanged.</summary>
public sealed record ConcertFeedQuery(
    ConcertPlace? Location = null,
    IReadOnlyList<string>? ConceptUris = null,
    int? RadiusKm = ConcertHub.DefaultRadiusKm,
    ConcertDateRange? DateRange = null,
    string? PaginationKey = null);

/// <summary>Normalized availability for an external ticket offer (<see cref="OfferEdge.Availability"/>).</summary>
public enum ConcertOfferAvailability : byte { Unknown, Available, Unavailable }

// ── the provider's clock ──────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Instants stored as unix ms + the provider's own offset (Concert.cs's file header), and the captions that
/// print them. Every caption formats the STORED clock — never <c>ToLocalTime</c>.</summary>
public static class ConcertTime
{
    /// <summary>The provider's local clock for a stored instant.</summary>
    public static DateTimeOffset Local(long unixMs, short offsetMinutes)
        => DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToOffset(TimeSpan.FromMinutes(offsetMinutes));

    /// <summary>"Fri, Aug 21 · 19:00" — the tile's accent eyebrow, in the format's OWN casing (ch 17 §10 item 20).</summary>
    public static string DateCaption(DateTimeOffset date, CultureInfo culture)
        => date.ToString("ddd, MMM d", culture) + " · " + date.ToString("t", culture);

    /// <summary>"Saturday, March 14, 2026 · 19:30" — the detail page's first fact row (ch 17 §10 item 46).</summary>
    public static string DetailDateLine(DateTimeOffset date, CultureInfo culture)
        => date.ToString("dddd, MMMM d, yyyy", culture) + " · " + date.ToString("t", culture);

    /// <summary>An ISO-8601 instant off the wire (<c>2030-08-20T19:30:00+02:00</c>, <c>…Z</c>, fractional seconds) →
    /// unix ms and the offset it was written in. Allocation-free and thread-safe (a decoder calls it). A value with no
    /// offset reads as UTC — never as the machine's zone. False for anything that is not a full date-time.</summary>
    public static bool TryParseIso(ReadOnlySpan<byte> iso, out long unixMs, out short offsetMinutes)
    {
        unixMs = 0;
        offsetMinutes = 0;
        if (iso.Length < 16 || iso[4] != '-' || iso[7] != '-' || (iso[10] != 'T' && iso[10] != ' ') || iso[13] != ':')
            return false;
        int year = Digits(iso, 0, 4), month = Digits(iso, 5, 2), day = Digits(iso, 8, 2);
        int hour = Digits(iso, 11, 2), minute = Digits(iso, 14, 2), second = 0, i = 16;
        if (year < 1 || month is < 1 or > 12 || day is < 1 or > 31 || hour is < 0 or > 23 || minute is < 0 or > 59) return false;
        if (i < iso.Length && iso[i] == ':')
        {
            if (i + 3 > iso.Length) return false;
            second = Digits(iso, i + 1, 2);
            if (second is < 0 or > 59) return false;
            i += 3;
        }
        if (i < iso.Length && iso[i] == '.') { i++; while (i < iso.Length && iso[i] is >= (byte)'0' and <= (byte)'9') i++; }
        int offset = 0;
        if (i < iso.Length)
        {
            byte sign = iso[i];
            if (sign is (byte)'Z' or (byte)'z') offset = 0;
            else if (sign is (byte)'+' or (byte)'-')
            {
                if (i + 6 > iso.Length || iso[i + 3] != ':') return false;
                int oh = Digits(iso, i + 1, 2), om = Digits(iso, i + 4, 2);
                if (oh is < 0 or > 14 || om is < 0 or > 59) return false;
                offset = (oh * 60 + om) * (sign == '-' ? -1 : 1);
            }
            else return false;
        }
        if (day > DateTime.DaysInMonth(year, month)) return false;
        var local = new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.FromMinutes(offset));
        unixMs = local.ToUnixTimeMilliseconds();
        offsetMinutes = (short)offset;
        return true;
    }

    static int Digits(ReadOnlySpan<byte> s, int at, int count)
    {
        if (at + count > s.Length) return -1;
        int v = 0;
        for (int k = 0; k < count; k++)
        {
            int d = s[at + k] - '0';
            if ((uint)d > 9) return -1;
            v = v * 10 + d;
        }
        return v;
    }
}

// ── the copy (ch 17 §6: every number-bearing sentence is an ICU plural, and six existing keys finally get a reader) ────

/// <summary>Every sentence a concert rule builds. Two tables: <see cref="English"/> is 0.2.9's hard-coded copy verbatim
/// (what a unit test, which loads no culture table, reads); <see cref="Localized"/> reads the loc table — the six keys
/// that existed unread (<c>concerts.setLocation</c>, <c>nearApproximate</c>, <c>genre</c>, <c>available</c>,
/// <c>soldOut</c>, <c>fallbackTitle</c>) and the ICU plural keys this stream added (batch-loc WP-5.N-C.json).
/// <c>Concert.InstallPages</c> installs <see cref="Localized"/>. Every member is a delegate over a static lambda: a
/// call allocates only the string it returns.</summary>
public sealed class ConcertCopy
{
    public required Func<string> SetLocation, Genre, FallbackTitle, Available, SoldOut, Today, Tomorrow;
    public required Func<string, string> NearApproximate, WithOne, OnSaleFrom, OnSaleUntil;
    public required Func<int, string> Shows, Cities, InDays, InWeeks, InMonths, Nights;
    public required Func<string, string, string> WithTwo, OnSaleRange;
    public required Func<string, string, int, string> WithMore;
    public required Func<string, int, string> WhereRadius;

    static string N(int n) => n.ToString(CultureInfo.CurrentCulture);

    /// <summary>0.2.9's copy, verbatim (ch 17 §6 "not localised in 0.2.9").</summary>
    public static ConcertCopy English { get; } = new()
    {
        SetLocation = static () => "Set location",
        Genre = static () => "Genre",
        FallbackTitle = static () => "Concert",
        Available = static () => "Available",
        SoldOut = static () => "Sold out or unavailable",
        Today = static () => "today",
        Tomorrow = static () => "tomorrow",
        NearApproximate = static place => "Near " + place + " (approximate)",
        WithOne = static a => "with " + a,
        OnSaleFrom = static start => "On sale from " + start,
        OnSaleUntil = static end => "On sale until " + end,
        Shows = static n => N(n) + (n == 1 ? " show" : " shows"),
        Cities = static n => N(n) + (n == 1 ? " city" : " cities"),
        InDays = static n => "in " + N(n) + " days",
        InWeeks = static n => n <= 1 ? "in 1 week" : "in " + N(n) + " weeks",
        InMonths = static n => n <= 1 ? "in 1 month" : "in " + N(n) + " months",
        Nights = static n => N(n) + " nights",
        WithTwo = static (a, b) => "with " + a + ", " + b,
        OnSaleRange = static (start, end) => "On sale " + start + " - " + end,
        WithMore = static (a, b, more) => "with " + a + ", " + b + " +" + N(more) + " more",
        WhereRadius = static (place, km) => place + " · " + km.ToString(CultureInfo.InvariantCulture) + " km",
    };

    /// <summary>The loc-table copy. New keys are literals (they live in batch-loc until the orchestrator merges them).</summary>
    public static ConcertCopy Localized { get; } = new()
    {
        SetLocation = static () => Loc.Get(Strings.Concerts.SetLocation),
        Genre = static () => Loc.Get(Strings.Concerts.Genre),
        FallbackTitle = static () => Loc.Get(Strings.Concerts.FallbackTitle),
        Available = static () => Loc.Get(Strings.Concerts.Available),
        SoldOut = static () => Loc.Get(Strings.Concerts.SoldOut),
        Today = static () => Loc.Get("concerts.schedule.relative.today"),
        Tomorrow = static () => Loc.Get("concerts.schedule.relative.tomorrow"),
        NearApproximate = static place => Strings.Concerts.NearApproximate(place),
        WithOne = static a => Loc.Format("concerts.schedule.support.one", ("first", a)),
        OnSaleFrom = static start => Loc.Format("concerts.detail.onSaleFrom", ("start", start)),
        OnSaleUntil = static end => Loc.Format("concerts.detail.onSaleUntil", ("end", end)),
        Shows = static n => Strings.Concerts.Schedule.ShowCount(n),
        Cities = static n => Loc.Format("concerts.schedule.cityCount", ("count", n)),
        InDays = static n => Loc.Format("concerts.schedule.relative.days", ("count", n)),
        InWeeks = static n => Loc.Format("concerts.schedule.relative.weeks", ("count", Math.Max(1, n))),
        InMonths = static n => Loc.Format("concerts.schedule.relative.months", ("count", Math.Max(1, n))),
        Nights = static n => Loc.Format("concerts.schedule.nights", ("count", n)),
        WithTwo = static (a, b) => Loc.Format("concerts.schedule.support.two", ("first", a), ("second", b)),
        OnSaleRange = static (start, end) => Loc.Format("concerts.detail.onSaleRange", ("start", start), ("end", end)),
        WithMore = static (a, b, more) => Loc.Format("concerts.schedule.support.more", ("first", a), ("second", b), ("count", more)),
        WhereRadius = static (place, km) => Loc.Format("concerts.filter.whereRadius", ("place", place), ("km", km)),
    };

    static ConcertCopy s_current = English;

    /// <summary>The table every rule reads. Never null.</summary>
    public static ConcertCopy Current
    {
        get => s_current;
        set => s_current = value ?? throw new ArgumentNullException(nameof(value));
    }
}

// ── layout (ConcertLayout.cs, verbatim) ───────────────────────────────────────────────────────────────────────────

/// <summary>Pure responsive decisions for the concert surfaces. Structural changes use separate enter/leave thresholds
/// so a continuously resizing window cannot flap component subtrees around one boundary.</summary>
public static class ConcertLayout
{
    public const float ScheduleEnterWide = 760f;
    public const float ScheduleLeaveWide = 720f;
    public const float EditorialHeroEnterWide = 760f;
    public const float EditorialHeroLeaveWide = 720f;
    public const float DetailEnterWide = 920f;
    public const float DetailLeaveWide = 860f;

    public static bool ScheduleWide(float width, bool wasWide, bool initialized = true) =>
        !initialized ? width >= ScheduleEnterWide
        : wasWide ? width >= ScheduleLeaveWide : width >= ScheduleEnterWide;

    public static bool DetailWide(float width, bool wasWide, bool initialized = true) =>
        !initialized ? width >= DetailEnterWide
        : wasWide ? width >= DetailLeaveWide : width >= DetailEnterWide;

    public static bool EditorialHeroWide(float width, bool wasWide, bool initialized = true) =>
        !initialized ? width >= EditorialHeroEnterWide
        : wasWide ? width >= EditorialHeroLeaveWide : width >= EditorialHeroEnterWide;

    public static EditorialHeroMetrics EditorialHero(bool wide) => wide
        ? new EditorialHeroMetrics(Height: 320f, MediaHeight: 320f, MediaFraction: 0.44f, Padding: 28f)
        : new EditorialHeroMetrics(Height: 0f, MediaHeight: 180f, MediaFraction: 1f, Padding: 20f);

    public static WideEditorialMetrics WideEditorial(float width) => width switch
    {
        >= 900f => new(Height: 288f, ArtworkFraction: 0.38f, ArtworkMin: 280f, ArtworkMax: 420f,
            Padding: 28f, SubtitleLines: 3),
        >= 600f => new(Height: 240f, ArtworkFraction: 0.42f, ArtworkMin: 220f, ArtworkMax: 360f,
            Padding: 24f, SubtitleLines: 2),
        _ => new(Height: 220f, ArtworkFraction: 0.55f, ArtworkMin: 180f, ArtworkMax: 280f,
            Padding: 20f, SubtitleLines: 2),
    };

    /// <summary>The event tile's chrome over its cell width (ch 17 §9 item 5, derived): pad-top 8 + art (cellW − 16) +
    /// gap 8 + text (16 + 3 + 20 + 3 + 16) + pad-bottom 12 = cellW + 70.</summary>
    public const float EventChrome = 70f;
    /// <summary>The all-events grid's minimum column width and its two gaps (ch 17 §3). Row extra = chrome + row gap = 86.</summary>
    public const float GridMinColumn = 240f, GridColumnGap = 12f, GridRowGap = 16f;
    public const float GridRowExtra = EventChrome + GridRowGap;
}

public readonly record struct EditorialHeroMetrics(float Height, float MediaHeight, float MediaFraction, float Padding);

public readonly record struct WideEditorialMetrics(
    float Height,
    float ArtworkFraction,
    float ArtworkMin,
    float ArtworkMax,
    float Padding,
    int SubtitleLines)
{
    public float ArtworkWidth(float availableWidth) =>
        Math.Clamp(availableWidth * ArtworkFraction, ArtworkMin, Math.Min(ArtworkMax, availableWidth));
}

// ── #86 — the procedural promo-card art's geometry (Home consumes it; ch 11) ──────────────────────────────────────────
// BCL-only (no PathData/engine types) so it stays testable: the art component freezes this geometry at mount from its
// own measured width/height (baked into its Key so a resize remounts) and turns it into engine primitives.

public static class EditorialArtGeometry
{
    const double DegToRad = Math.PI / 180.0;

    /// <summary>One circular-arc segment in node-local DIP space.</summary>
    public static ArcSweep Arc(float cx, float cy, float r, float startDeg, float sweepDeg)
    {
        double a0 = startDeg * DegToRad, a1 = (startDeg + sweepDeg) * DegToRad;
        float x0 = cx + r * (float)Math.Cos(a0), y0 = cy + r * (float)Math.Sin(a0);
        float x1 = cx + r * (float)Math.Cos(a1), y1 = cy + r * (float)Math.Sin(a1);
        int largeArc = MathF.Abs(sweepDeg) > 180f ? 1 : 0;
        int sweepFlag = sweepDeg >= 0f ? 1 : 0;
        return new ArcSweep(x0, y0, x1, y1, r, largeArc, sweepFlag);
    }

    /// <summary>The Concerts treatment's outer sweep — a broad arc low in the pane (a stage horizon).</summary>
    public static ArcSweep ConcertArcOuter(float width, float height)
    {
        float cx = width * 0.5f, cy = height * 0.62f;
        float r = MathF.Min(width, height) * 0.62f;
        return Arc(cx, cy, r, startDeg: -150f, sweepDeg: 120f);
    }

    /// <summary>The Concerts treatment's inner counter-sweep, swept the OPPOSITE direction.</summary>
    public static ArcSweep ConcertArcInner(float width, float height)
    {
        float cx = width * 0.5f, cy = height * 0.62f;
        float r = MathF.Min(width, height) * 0.46f;
        return Arc(cx, cy, r, startDeg: 40f, sweepDeg: -110f);
    }

    /// <summary>The Browse treatment's category mosaic: three rounded tiles at fixed relative fractions of the pane.</summary>
    public static BrowseTile[] BrowseTiles(float width, float height) =>
    [
        new BrowseTile(width * 0.10f, height * 0.14f, width * 0.40f, height * 0.46f),
        new BrowseTile(width * 0.42f, height * 0.30f, width * 0.34f, height * 0.40f),
        new BrowseTile(width * 0.30f, height * 0.58f, width * 0.30f, height * 0.34f),
    ];
}

/// <summary>One circular-arc segment. <see cref="ToPathData"/> is the SVG path-data fragment the engine's path parser
/// consumes directly.</summary>
public readonly record struct ArcSweep(float X0, float Y0, float X1, float Y1, float Radius, int LargeArc, int SweepFlag)
{
    public string ToPathData() => string.Format(
        CultureInfo.InvariantCulture,   // a decimal-comma locale must never corrupt the token stream
        "M{0:F2},{1:F2} A{2:F2},{2:F2} 0 {3},{4} {5:F2},{6:F2}",
        X0, Y0, Radius, LargeArc, SweepFlag, X1, Y1);
}

public readonly record struct BrowseTile(float X, float Y, float Width, float Height);

// ── the hub (ConcertHubModel.cs) ──────────────────────────────────────────────────────────────────────────────────

/// <summary>Pure decisions for the Concert Hub: concept toggle/survival, preset windows, the rest-state token set, the
/// fused pill's range caption, the where label, concept casing, feed emptiness.
///
/// <para><b>The four test-only rules of 0.2.9 (ch 17 §8), as decisions.</b> <see cref="LocationLabel"/> is WIRED: the
/// where pill states it through <see cref="WhereLabel"/>, so an IP-guessed place reads "Near X (approximate)" (parity
/// 71). <see cref="ReconcileConcept"/> is WIRED: <see cref="ReconcileConcepts"/> folds it over the multi-select strip. The
/// single-select <see cref="ToggleConcept(string?,string)"/> is WIRED as the "tap the active one clears" primitive the
/// multi-select toggle applies per selected uri. <c>MonthBoardColumns</c> is NOT ported (see
/// <see cref="ConcertScheduleShaping"/>).</para></summary>
public static class ConcertHub
{
    /// <summary>The radius the pill does not print (ch 17 §6: " · N km" whenever the radius is not 100).</summary>
    public const int DefaultRadiusKm = 100;

    /// <summary>The where flyout's three radius rows (ch 17 W9).</summary>
    public static ReadOnlySpan<int> RadiusOptionsKm => [25, 50, 100];

    /// <summary>How many concept tokens the strip shows at rest.</summary>
    public const int TopConceptCount = 3;

    /// <summary>Zero-or-one selection: tapping the active concept clears it, tapping another selects it.</summary>
    public static string? ToggleConcept(string? selected, string tapped) =>
        string.Equals(selected, tapped, StringComparison.Ordinal) ? null : tapped;

    /// <summary>Multi-select toggle: a NEW list with the uri removed if present, else appended (existing order kept, so the
    /// strip does not reshuffle). Each existing entry is run through the single-select rule: "tapping the active one
    /// clears" is exactly <c>ToggleConcept(existing, uri) == null</c>.</summary>
    public static IReadOnlyList<string> ToggleConcept(IReadOnlyList<string> selected, string uri)
    {
        var result = new List<string>(selected.Count + 1);
        bool removed = false;
        foreach (string existing in selected)
        {
            if (ToggleConcept(existing, uri) is null) { removed = true; continue; }
            result.Add(existing);
        }
        if (!removed) result.Add(uri);
        return result;
    }

    /// <summary>The wire/render window for a preset, against <paramref name="now"/>. Any/Custom carry none; Today is the
    /// single current day; a weekend is Fri–Sun — ThisWeekend contains or follows now (Sat/Sun stay in the current one),
    /// NextWeekend is the one after.</summary>
    public static ConcertDateRange? PresetRange(ConcertWhenKind kind, DateTimeOffset now)
    {
        switch (kind)
        {
            case ConcertWhenKind.Today:
                var day = DateOnly.FromDateTime(now.Date);
                return new ConcertDateRange(day, day);
            case ConcertWhenKind.ThisWeekend:
                var weekend = Weekend(now);
                return new ConcertDateRange(weekend.From, weekend.To);
            case ConcertWhenKind.NextWeekend:
                var next = Weekend(now);
                return new ConcertDateRange(next.From.AddDays(7), next.To.AddDays(7));
            default:
                return null;
        }
    }

    // A Saturday/Sunday "now" resolves to the CURRENT weekend's Friday, otherwise the upcoming Friday (Fri included).
    static (DateOnly From, DateOnly To) Weekend(DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.Date);
        DateOnly friday = (int)now.DayOfWeek switch
        {
            6 => today.AddDays(-1),
            0 => today.AddDays(-2),
            var dow => today.AddDays(5 - dow),
        };
        return (friday, friday.AddDays(2));
    }

    /// <summary>Rest-state tokens: the provider's weight order is the response order, so take the first
    /// <paramref name="top"/> and append any selected concept outside that head (original order); all when expanded.</summary>
    public static IReadOnlyList<ConcertConcept> TopConcepts(IReadOnlyList<ConcertConcept> all,
        IReadOnlyList<string> selected, bool expanded, int top = TopConceptCount)
    {
        if (expanded) return all;
        int head = Math.Min(top, all.Count);
        var result = new List<ConcertConcept>(head);
        for (int i = 0; i < head; i++) result.Add(all[i]);
        for (int i = head; i < all.Count; i++)
            if (Contains(selected, all[i].Uri)) result.Add(all[i]);
        return result;
    }

    /// <summary>"Jul 17 – 19" within a month, "Jul 31 – Aug 2" across months, "Jul 14" for one day. EN DASH.</summary>
    public static string WhenLabel(ConcertDateRange range, CultureInfo culture)
    {
        culture ??= CultureInfo.CurrentCulture;
        string fromMonth = range.From.ToString("MMM", culture);
        string fromDay = range.From.Day.ToString(culture);
        if (range.From == range.To)
            return fromMonth + " " + fromDay;
        if (range.From.Year == range.To.Year && range.From.Month == range.To.Month)
            return fromMonth + " " + fromDay + EnDash + range.To.Day.ToString(culture);
        return fromMonth + " " + fromDay + EnDash + range.To.ToString("MMM", culture) + " " + range.To.Day.ToString(culture);
    }

    const string EnDash = " – ";

    static bool Contains(IReadOnlyList<string> uris, string uri)
    {
        foreach (string value in uris)
            if (string.Equals(value, uri, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>A selected concept survives a location change ONLY while the new location still offers it.</summary>
    public static string? ReconcileConcept(string? selected, IReadOnlyList<ConcertConcept> offered)
    {
        if (selected is null) return null;
        foreach (var concept in offered)
            if (string.Equals(concept.Uri, selected, StringComparison.Ordinal)) return selected;
        return null;
    }

    /// <summary>The multi-select strip's reconcile (0.2.9 <c>ConcertHubPage.PublishConcepts</c>): every selected uri kept
    /// through <see cref="ReconcileConcept"/>. Answers the SAME instance when nothing vanished — which is the page's
    /// "the set actually shrank, so requery" test (ch 17 §9 item 3).</summary>
    public static IReadOnlyList<string> ReconcileConcepts(IReadOnlyList<string> selected, IReadOnlyList<ConcertConcept> offered)
    {
        if (selected.Count == 0) return selected;
        List<string>? kept = null;
        for (int i = 0; i < selected.Count; i++)
        {
            bool survives = ReconcileConcept(selected[i], offered) is not null;
            if (survives) { kept?.Add(selected[i]); continue; }
            if (kept is null)
            {
                kept = new List<string>(selected.Count);
                for (int k = 0; k < i; k++) kept.Add(selected[k]);
            }
        }
        return kept is null ? selected : kept;
    }

    /// <summary>The location label. An inferred (IP-derived) place is marked approximate; no resolvable place prompts.</summary>
    public static string LocationLabel(ConcertPlace? place, bool? inferred)
    {
        var copy = ConcertCopy.Current;
        if (place is null || string.IsNullOrWhiteSpace(place.Name)) return copy.SetLocation();
        return inferred == true ? copy.NearApproximate(place.Name) : place.Name;
    }

    /// <summary>THE where pill's label (decision 1 above): <see cref="LocationLabel"/>, plus " · N km" whenever the radius
    /// is not <see cref="DefaultRadiusKm"/> (0.2.9 <c>ConcertFilterBar.WhereLabel</c>'s suffix rule).</summary>
    public static string WhereLabel(ConcertPlace? place, bool? inferred, int radiusKm)
    {
        string label = LocationLabel(place, inferred);
        return radiusKm != DefaultRadiusKm ? ConcertCopy.Current.WhereRadius(label, radiusKm) : label;
    }

    /// <summary>Provider concept names are machine labels; present them as UI labels keeping music abbreviations.</summary>
    public static string ConceptLabel(string? name, string? fallback = null)
    {
        string value = name?.Trim() ?? string.Empty;
        if (value.Length == 0) return fallback ?? ConcertCopy.Current.Genre();
        return value.ToLowerInvariant() switch
        {
            "edm" => "EDM",
            "r&b" => "R&B",
            "rnb" => "R&B",
            "hip hop" => "Hip Hop",
            _ => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(value.ToLower(CultureInfo.CurrentCulture)),
        };
    }

    /// <summary>A feed renders nothing when its whole concert list and its whole promo list are empty (0.2.9
    /// <c>IsFeedEmpty</c>; in 0.3 a section with neither is never stored, so the two lists are the whole question).</summary>
    public static bool IsFeedEmpty(ReadOnlySpan<int> concerts, ReadOnlySpan<int> promotions)
        => concerts.IsEmpty && promotions.IsEmpty;
}

// ── the schedule (ConcertScheduleModel.cs) ────────────────────────────────────────────────────────────────────────

/// <summary>Chronological order + URI dedupe for a schedule.</summary>
public static class ConcertSchedules
{
    /// <summary>Earliest first, duplicate URIs dropped (first occurrence kept); same-instant events keep source order.</summary>
    public static IReadOnlyList<ConcertShow> Chronological(IReadOnlyList<ConcertShow>? concerts)
    {
        if (concerts is null || concerts.Count == 0) return Array.Empty<ConcertShow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var deduped = new List<ConcertShow>(concerts.Count);
        foreach (var c in concerts)
            if (!string.IsNullOrEmpty(c.Uri) && seen.Add(c.Uri)) deduped.Add(c);
        return StableByDate(deduped);
    }

    /// <summary>A stable date sort (the index breaks ties), because a same-instant pair must keep its source order.</summary>
    internal static ConcertShow[] StableByDate(IReadOnlyList<ConcertShow> items)
    {
        var order = new int[items.Count];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) =>
        {
            int c = items[a].Date.CompareTo(items[b].Date);
            return c != 0 ? c : a.CompareTo(b);
        });
        var result = new ConcertShow[order.Length];
        for (int i = 0; i < order.Length; i++) result[i] = items[order[i]];
        return result;
    }
}

/// <summary>A one-shot geolocation outcome → the picker's message. Distinct per failure; success/cancel say nothing.</summary>
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

/// <summary>A run of one or more CONSECUTIVE-day shows at the SAME venue+city — one month-board tile (0.2.9
/// <c>ConcertRun</c>; renamed because the WP-5.N contract binds that name to the staging cursor).</summary>
public sealed record ConcertBoardRun(IReadOnlyList<ConcertShow> Nights)
{
    public ConcertShow First => Nights[0];
    public ConcertShow Last => Nights[Nights.Count - 1];
    public bool IsMultiNight => Nights.Count > 1;
    public int NightCount => Nights.Count;
    /// <summary>The run's navigation identity — the first night's URI.</summary>
    public string Uri => First.Uri;
}

/// <summary>A calendar-month board: the collapsed runs whose PRESERVED-offset date falls in one year+month.</summary>
public sealed record ConcertMonthGroup(int Year, int Month, string Key, IReadOnlyList<ConcertBoardRun> Runs)
{
    /// <summary>Total shows in the month — a run counts as its night count.</summary>
    public int ShowCount
    {
        get { int n = 0; for (int i = 0; i < Runs.Count; i++) n += Runs[i].NightCount; return n; }
    }

    /// <summary>The board overflows the tile cap (a run is ONE tile).</summary>
    public bool OverflowsCap => Runs.Count > ConcertScheduleShaping.TileCap;
}

/// <summary>Balanced chronological columns: every run in Left precedes every run in Right.</summary>
public readonly record struct ConcertRunColumns(IReadOnlyList<ConcertBoardRun> Left, IReadOnlyList<ConcertBoardRun> Right);

/// <summary>Hero stats: show count, DISTINCT non-empty city count, the first→last span.</summary>
public readonly record struct ConcertTourStats(int Shows, int Cities, DateTimeOffset First, DateTimeOffset Last, bool HasCities);

/// <summary>What a month-board tile prints (<see cref="ConcertScheduleShaping.TileText"/>).</summary>
public readonly record struct ConcertTileText(string Primary, string Secondary, bool CityIsPrimary);

/// <summary>Pure shaping for the artist schedule: spotlight, month boards with run collapse, the 6-tile cap, hero stats,
/// relative time, month tabs, balanced columns, the near-you guard and tile text. Every clocked helper takes
/// <c>now</c> explicitly.
///
/// <para><b>Decision (ch 17 §8's fourth test-only rule): <c>MonthBoardColumns</c> is NOT ported.</b> The board renders one
/// or two columns from the schedule's own width hysteresis (<see cref="ConcertLayout.ScheduleWide"/>) and
/// <see cref="BalancedColumns"/>, exactly as 0.2.9's <c>MonthBoard</c> did; the fit maths had no caller and — despite the
/// chapter's claim — no fact in <c>ConcertSchedulePageTests</c>.</para></summary>
public static class ConcertScheduleShaping
{
    /// <summary>Max tiles a board shows before collapsing behind "Show all" (a run is one tile).</summary>
    public const int TileCap = 6;

    /// <summary>The first UPCOMING show of a chronological list; null when every date is past.</summary>
    public static ConcertShow? Spotlight(IReadOnlyList<ConcertShow>? chronological, DateTimeOffset now)
    {
        if (chronological is null) return null;
        for (int i = 0; i < chronological.Count; i++)
            if (chronological[i].Date >= now) return chronological[i];
        return null;
    }

    /// <summary>The boards' shows: the chronological list MINUS the spotlight (so nothing appears twice).</summary>
    public static IReadOnlyList<ConcertShow> BoardConcerts(IReadOnlyList<ConcertShow>? chronological, ConcertShow? spotlight)
    {
        if (chronological is null || chronological.Count == 0) return Array.Empty<ConcertShow>();
        if (spotlight is null) return chronological;
        var list = new List<ConcertShow>(chronological.Count);
        for (int i = 0; i < chronological.Count; i++)
            if (!string.Equals(chronological[i].Uri, spotlight.Uri, StringComparison.Ordinal))
                list.Add(chronological[i]);
        return list;
    }

    /// <summary>Month boards by the PRESERVED-offset year+month, runs collapsed per month; a Dec→Jan pair is two boards.</summary>
    public static IReadOnlyList<ConcertMonthGroup> GroupByMonth(IReadOnlyList<ConcertShow>? concerts)
    {
        if (concerts is null || concerts.Count == 0) return Array.Empty<ConcertMonthGroup>();
        var sorted = ConcertSchedules.StableByDate(concerts);
        var groups = new List<ConcertMonthGroup>();
        int i = 0;
        while (i < sorted.Length)
        {
            int year = sorted[i].Date.Year, month = sorted[i].Date.Month;
            var monthConcerts = new List<ConcertShow>();
            while (i < sorted.Length && sorted[i].Date.Year == year && sorted[i].Date.Month == month)
                monthConcerts.Add(sorted[i++]);
            groups.Add(new ConcertMonthGroup(year, month, MonthKey(year, month), DetectRuns(monthConcerts)));
        }
        return groups;
    }

    /// <summary>A run extends while the next show is the SAME venue+city AND the very NEXT calendar day.</summary>
    public static IReadOnlyList<ConcertBoardRun> DetectRuns(IReadOnlyList<ConcertShow> monthConcerts)
    {
        var runs = new List<ConcertBoardRun>();
        int i = 0;
        while (i < monthConcerts.Count)
        {
            var nights = new List<ConcertShow> { monthConcerts[i] };
            int j = i + 1;
            while (j < monthConcerts.Count && IsNextNight(monthConcerts[j - 1], monthConcerts[j]))
                nights.Add(monthConcerts[j++]);
            runs.Add(new ConcertBoardRun(nights));
            i = j;
        }
        return runs;
    }

    static bool IsNextNight(ConcertShow prev, ConcertShow next) =>
        SameVenueCity(prev, next) && next.Date.Date == prev.Date.Date.AddDays(1);

    static bool SameVenueCity(ConcertShow a, ConcertShow b) =>
        string.Equals(a.Venue ?? "", b.Venue ?? "", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.City ?? "", b.City ?? "", StringComparison.OrdinalIgnoreCase);

    /// <summary>The stable "yyyy-MM" month identity (the remembered tab key, ch 17 §6).</summary>
    public static string MonthKey(int year, int month) =>
        year.ToString("D4", CultureInfo.InvariantCulture) + "-" + month.ToString("D2", CultureInfo.InvariantCulture);

    /// <summary>Tour stats over the WHOLE schedule (spotlight included).</summary>
    public static ConcertTourStats Stats(IReadOnlyList<ConcertShow>? concerts)
    {
        if (concerts is null || concerts.Count == 0) return new ConcertTourStats(0, 0, default, default, false);
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

    /// <summary>"N shows · M cities · MMM – MMM yyyy". The cities segment only while 2 ≤ cities &lt; shows; a single-month
    /// span collapses to "MMM yyyy" (ch 17 §11 item 30).</summary>
    public static string StatsLine(in ConcertTourStats s)
    {
        if (s.Shows == 0) return "";
        var copy = ConcertCopy.Current;
        string line = copy.Shows(s.Shows);
        if (s.HasCities && s.Cities >= 2 && s.Cities < s.Shows) line += " · " + copy.Cities(s.Cities);
        return line + " · " + SpanLabel(s.First, s.Last);
    }

    static string SpanLabel(DateTimeOffset first, DateTimeOffset last)
    {
        var c = CultureInfo.CurrentCulture;
        if (first.Year == last.Year && first.Month == last.Month) return first.ToString("MMM yyyy", c);
        if (first.Year == last.Year) return first.ToString("MMM", c) + " – " + last.ToString("MMM yyyy", c);
        return first.ToString("MMM yyyy", c) + " – " + last.ToString("MMM yyyy", c);
    }

    /// <summary>"today" / "tomorrow" / "in 5 days" / "in 6 weeks" / "in 3 months", day-granular on the preserved-offset
    /// calendar date.</summary>
    public static string RelativeTime(DateTimeOffset date, DateTimeOffset now)
    {
        var copy = ConcertCopy.Current;
        int days = (int)(date.Date - now.Date).TotalDays;
        if (days <= 0) return copy.Today();
        if (days == 1) return copy.Tomorrow();
        if (days < 7) return copy.InDays(days);
        if (days < 60) return copy.InWeeks((int)Math.Round(days / 7.0, MidpointRounding.AwayFromZero));
        return copy.InMonths((int)Math.Round(days / 30.0, MidpointRounding.AwayFromZero));
    }

    /// <summary>Resolve the selected month after a reload: a still-valid remembered key wins, then the spotlight's month,
    /// then the first month.</summary>
    public static int ResolveMonthIndex(IReadOnlyList<ConcertMonthGroup>? groups, string? requestedKey, ConcertShow? spotlight)
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

    /// <summary>Compact tab labels: the first month and every year boundary carry "MMM ’yy", the rest "MMM".</summary>
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

    /// <summary>Split the first <paramref name="visibleCount"/> runs into two balanced chronological columns.</summary>
    public static ConcertRunColumns BalancedColumns(IReadOnlyList<ConcertBoardRun> runs, int visibleCount)
    {
        int count = Math.Clamp(visibleCount, 0, runs.Count);
        int leftCount = (count + 1) / 2;
        var left = new ConcertBoardRun[leftCount];
        var right = new ConcertBoardRun[count - leftCount];
        for (int i = 0; i < left.Length; i++) left[i] = runs[i];
        for (int i = 0; i < right.Length; i++) right[i] = runs[leftCount + i];
        return new ConcertRunColumns(left, right);
    }

    /// <summary>Near-you is a DIFFERENTIATOR: tinted only when <c>0 &lt; near ≤ max(3, ⌈shows / 3⌉)</c>.</summary>
    public static bool NearIsInformative(int nearCount, int showCount) =>
        nearCount > 0 && nearCount <= Math.Max(3, (int)Math.Ceiling(showCount / 3.0));

    /// <summary>The guarded near set: the Nearby URIs ∪ per-show near flags among the board shows — or EMPTY when the count
    /// fails <see cref="NearIsInformative"/>.</summary>
    public static HashSet<string> GuardedNearSet(IReadOnlyList<ConcertShow> boardConcerts, IReadOnlyList<ConcertShow>? nearby)
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

    /// <summary>A tile's lines: venue-primary ⇒ "City · HH:mm"; city-primary ⇒ "HH:mm" + support acts; title-fallback
    /// only when both venue and city are empty (never the page artist's name repeated down the board).</summary>
    public static ConcertTileText TileText(ConcertShow concert, string? scheduleArtistName)
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
        string title = string.IsNullOrWhiteSpace(concert.Title) ? ConcertCopy.Current.FallbackTitle() : concert.Title!;
        return new ConcertTileText(title, time, CityIsPrimary: false);
    }

    /// <summary>"with A" / "with A, B" / "with A, B +n more" — lineup names minus the page artist (case-insensitive,
    /// deduped, two named). Null when nothing informative remains.</summary>
    public static string? SupportActs(ConcertShow concert, string? scheduleArtistName)
    {
        if (concert.ArtistNames is not { Count: > 0 } artists) return null;
        var names = new List<string>(artists.Count);
        for (int i = 0; i < artists.Count; i++)
        {
            string? raw = artists[i];
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string name = raw.Trim();
            if (scheduleArtistName is { Length: > 0 } self &&
                string.Equals(name, self.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            bool dup = false;
            for (int j = 0; j < names.Count; j++)
                if (string.Equals(names[j], name, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
            if (!dup) names.Add(name);
        }
        var copy = ConcertCopy.Current;
        return names.Count switch
        {
            0 => null,
            1 => copy.WithOne(names[0]),
            2 => copy.WithTwo(names[0], names[1]),
            _ => copy.WithMore(names[0], names[1], names.Count - 2),
        };
    }
}

// ── the detail (ConcertDetailModel.cs) ────────────────────────────────────────────────────────────────────────────

/// <summary>Ticket offers: URL validation and price / availability / sale-window captions.</summary>
public static class ConcertOffers
{
    /// <summary>The offer's URL, or null unless it is an absolute http/https link — never a dead button, never a non-web
    /// launch through the OS open seam.</summary>
    public static string? ValidTicketUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url, UriKind.Absolute, out var parsed)
        && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
            ? url
            : null;

    /// <summary>"45 EUR" / "45 - 75 EUR": invariant digits + the provider's currency CODE; null with no price at all.</summary>
    public static string? PriceLabel(decimal? min, decimal? max, string? currency)
    {
        static string N(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        if (min is null && max is null) return null;
        string code = string.IsNullOrWhiteSpace(currency) ? "" : " " + currency;
        return min is { } lo && max is { } hi && hi != lo
            ? N(Math.Min(lo, hi)) + " - " + N(Math.Max(lo, hi)) + code
            : N(min ?? max!.Value) + code;
    }

    /// <summary><see cref="PriceLabel(decimal?,decimal?,string?)"/> over a stored offer (prices in minor units).</summary>
    public static string? PriceLabel(in OfferEdge offer)
    {
        decimal? min = (offer.Flags & OfferEdge.HasMin) != 0 ? offer.MinPriceCents / 100m : null;
        decimal? max = (offer.Flags & OfferEdge.HasMax) != 0 ? offer.MaxPriceCents / 100m : null;
        return PriceLabel(min, max, Entities.Strings.Resolve(offer.Currency));
    }

    /// <summary>Human availability; Unknown adds no line.</summary>
    public static string? AvailabilityLabel(ConcertOfferAvailability availability) => availability switch
    {
        ConcertOfferAvailability.Available => ConcertCopy.Current.Available(),
        ConcertOfferAvailability.Unavailable => ConcertCopy.Current.SoldOut(),
        _ => null,
    };

    /// <summary>"On sale 1 Jan 2030 - 31 Aug 2030", each timestamp in its PRESERVED offset; null with no sale date.</summary>
    public static string? SaleWindowLabel(DateTimeOffset? start, DateTimeOffset? end, CultureInfo? culture = null)
    {
        var c = culture ?? CultureInfo.CurrentCulture;
        static string D(DateTimeOffset d, CultureInfo c) => d.ToString("d MMM yyyy", c);
        var copy = ConcertCopy.Current;
        return (start, end) switch
        {
            ({ } s, { } e) => copy.OnSaleRange(D(s, c), D(e, c)),
            ({ } s, null) => copy.OnSaleFrom(D(s, c)),
            (null, { } e) => copy.OnSaleUntil(D(e, c)),
            _ => null,
        };
    }

    /// <summary><see cref="SaleWindowLabel(DateTimeOffset?,DateTimeOffset?,CultureInfo?)"/> over a stored offer.</summary>
    public static string? SaleWindowLabel(in OfferEdge offer, CultureInfo? culture = null)
    {
        DateTimeOffset? start = (offer.Flags & OfferEdge.HasSaleStart) != 0
            ? ConcertTime.Local(offer.SaleStart * 1000L, offer.SaleStartOffsetMinutes) : null;
        DateTimeOffset? end = (offer.Flags & OfferEdge.HasSaleEnd) != 0
            ? ConcertTime.Local(offer.SaleEnd * 1000L, offer.SaleEndOffsetMinutes) : null;
        return SaleWindowLabel(start, end, culture);
    }
}

/// <summary>The detail facts: the status line and the location line.</summary>
public static class ConcertDetailInfo
{
    // The unremarkable defaults — status shows ONLY for a notable state, never a "confirmed" badge on every event.
    static readonly string[] DefaultStatuses = ["UNKNOWN", "CONFIRMED", "SCHEDULED"];

    /// <summary>"CANCELLED" → "Cancelled", "SOLD_OUT" → "Sold out"; null for missing/default states.</summary>
    public static string? DisplayStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return null;
        string trimmed = status.Trim();
        foreach (var d in DefaultStatuses)
            if (string.Equals(trimmed, d, StringComparison.OrdinalIgnoreCase)) return null;
        string words = trimmed.Replace('_', ' ').ToLowerInvariant();
        return char.ToUpperInvariant(words[0]) + words[1..];
    }

    /// <summary>"City, Region, Country" with empty and case-insensitively duplicate segments removed.</summary>
    public static string LocationLine(string? city, string? region, string? country)
    {
        var parts = new List<string>(3);
        void Add(string? p)
        {
            if (!string.IsNullOrWhiteSpace(p) && !parts.Exists(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase)))
                parts.Add(p!);
        }
        Add(city);
        Add(region);
        Add(country);
        return string.Join(", ", parts);
    }
}

// ── the feed subject's identity (ch 17 DATA GAPS "the filter tuple → subject identity") ───────────────────────────

/// <summary>ONE feed subject per filter tuple. Changing the place, the radius, the window or the concept set IS a new
/// subject with its own edges and its own cursor — "reset pagination on a filter change" by construction.
///
/// <para>The DEFAULT subject (no window, no concepts) is exactly <c>placeKey|radius</c>, the same key the sidebar's
/// concerts source already mints (<c>Sidebar.Host.cs</c> <c>ResolveFeedSlot</c>), so the hub and the sidebar share
/// one feed. A window appends <c>|yyyy-MM-dd..yyyy-MM-dd</c>; concepts append <c>|</c> + the ordinally SORTED uris
/// joined by <c>,</c> — the same set in any tap order is the same subject.</para></summary>
public static class ConcertFeedKey
{
    /// <summary>A place's key: its provider id, else <c>geo:&lt;hash&gt;</c>, else empty (no place).</summary>
    public static string PlaceKey(string? id, string? geoHash)
        => !string.IsNullOrWhiteSpace(id) ? id!.Trim()
         : !string.IsNullOrWhiteSpace(geoHash) ? "geo:" + geoHash!.Trim()
         : "";

    /// <inheritdoc cref="PlaceKey(string?,string?)"/>
    public static string PlaceKey(ConcertPlace? place) => place is null ? "" : PlaceKey(place.Id, place.GeoHash);

    /// <summary>The subject key for a filter tuple.</summary>
    public static string For(string placeKey, int radiusKm, ConcertDateRange? range, IReadOnlyList<string>? conceptUris)
    {
        string key = placeKey + "|" + radiusKm.ToString(CultureInfo.InvariantCulture);
        if (range is not null)
            key += "|" + range.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                 + ".." + range.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (conceptUris is { Count: > 0 })
        {
            var sorted = new string[conceptUris.Count];
            for (int i = 0; i < sorted.Length; i++) sorted[i] = conceptUris[i];
            Array.Sort(sorted, StringComparer.Ordinal);
            key += "|" + string.Join(",", sorted);
        }
        return key;
    }

    /// <inheritdoc cref="For(string,int,ConcertDateRange?,IReadOnlyList{string}?)"/>
    public static string For(in ConcertHubFilters filters)
        => For(PlaceKey(filters.Place), filters.RadiusKm, filters.When.Range, filters.ConceptUris);
}
