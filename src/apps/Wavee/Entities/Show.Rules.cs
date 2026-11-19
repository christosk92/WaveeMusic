// ── Entities/Show.Rules.cs ─────────────────────────────────────────────────────────────────────────────────────────
// the show's derived facts the podcast pages read: "new since you were here", the cadence and the date keys (the per-show
// view prefs are `ShowViewPrefs.cs`)
//
// Role: CORE
// Owner: R
// Wave: P1 (the podcast rework)
// Spec: podcast-show-rework-implementation.md §5.2 (the code) · §4 (the interaction contract) · §6.1 (readiness) · the
//   approved prototype `podcast-show-episode-mica.html`
//
// DERIVED FACTS LIVE ON THE MODEL. Each rule is a pure function of numbers the model already holds: pcts (`Episode.Rules.Pct`),
// unix-second dates (0 = unknown) and day keys. Nothing reads a signal, a table or a clock — "the show's last play" comes in
// as an argument — and nothing allocates.

using System.Globalization;

namespace Wavee;

// ══ 1. NEW SINCE YOU WERE HERE ═══════════════════════════════════════════════════════════════════════════════════════

/// <summary>The show's "new since you were here" rule (the followed shows' new mark, <c>User.ShowHasNew</c>).</summary>
public static class ShowLedger
{
    /// <summary>"New since you were here" — UNPLAYED (≤ .01 — a started episode is not news) and published STRICTLY after the
    /// show's last play. A show never played has no "since" (<paramref name="lastPlayedAt"/> 0 ⇒ false), nor has an undated episode.</summary>
    public static bool IsFresh(float pct, int publishedAt, int lastPlayedAt)
        => lastPlayedAt > 0 && publishedAt > lastPlayedAt && pct <= Episode.Rules.InProgressFloor;
}

// ══ 2. THE CADENCE ═══════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>The show's release cadence: "weekly" in the show page's facts line, and the caught-up line's next likely date.
/// <para>The SAMPLE is the newest ≤ <see cref="MaxSample"/> DATED episodes (0 = unknown is skipped), sorted, so an edge
/// that pins a trailer out of date order does not skew it; under <see cref="MinSample"/> dates everything is unknown.
/// The kind is the MEDIAN gap (a skipped week or a bonus drop moves a mean, not a median) against bands centred on the
/// four words the copy has: ≤ 1.5 d Daily · 5-10 d Weekly · 10-20 d Fortnightly · 20-45 d Monthly · anything else
/// Unknown. The 1.5-5 d hole is deliberate: twice or thrice a week has no word, and "weekly" would be a false fact.</para></summary>
public static class ShowCadence
{
    public enum Kind : byte { Unknown, Daily, Weekly, Fortnightly, Monthly }

    public const int MinSample = 4, MaxSample = 8;
    const int DaySeconds = 86_400;
    /// <summary>The bands, in seconds of median gap (type doc).</summary>
    public const int DailyMax = DaySeconds * 3 / 2, WeeklyMin = 5 * DaySeconds, WeeklyMax = 10 * DaySeconds,
        FortnightlyMax = 20 * DaySeconds, MonthlyMax = 45 * DaySeconds;

    public static Kind Of(ReadOnlySpan<int> publishedAtNewestFirst)
    {
        Span<int> dates = stackalloc int[MaxSample];
        int n = 0;
        for (int i = 0; i < publishedAtNewestFirst.Length && n < MaxSample; i++)
            if (publishedAtNewestFirst[i] > 0) dates[n++] = publishedAtNewestFirst[i];
        if (n < MinSample) return Kind.Unknown;
        var sample = dates[..n];
        sample.Sort();                                                // oldest first

        Span<int> gaps = stackalloc int[MaxSample - 1];
        for (int i = 1; i < n; i++) gaps[i - 1] = sample[i] - sample[i - 1];
        var g = gaps[..(n - 1)];
        g.Sort();
        long median = g.Length % 2 == 1 ? g[g.Length / 2] : ((long)g[g.Length / 2 - 1] + g[g.Length / 2]) / 2;
        return KindOf(median);
    }

    /// <summary>The band a median gap (seconds) falls in (type doc).</summary>
    public static Kind KindOf(long medianGapSeconds)
        => medianGapSeconds <= DailyMax ? Kind.Daily
         : medianGapSeconds < WeeklyMin ? Kind.Unknown
         : medianGapSeconds <= WeeklyMax ? Kind.Weekly
         : medianGapSeconds <= FortnightlyMax ? Kind.Fortnightly
         : medianGapSeconds <= MonthlyMax ? Kind.Monthly
         : Kind.Unknown;
}

// ══ 3. THE DAY KEYS ══════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>THE date key an episode row mints, and the ONLY decoders allowed to turn one back into a date: the <b>day key</b>
/// = <c>year*10000 + month*100 + day</c> (20260915), DECIMAL so a key reads as the date it is — in a log, a debugger, a crash
/// dump — and sorting in calendar order (<see cref="DayKeyOfLocal"/>, the episode row's date). Mint through this type or not
/// at all.
/// <para>EVERY DECODER IS TOTAL. A key this type did not mint — a truncated one, a zero month, a February 30th — answers
/// <see cref="None"/>, <c>false</c> or "" and never throws: a <c>DateTime</c> constructor thrown on the RENDER path is a
/// crashed app loop, not a red squiggle. That is the whole contract: a render path must not be able to throw on data.</para></summary>
public static class DateKeys
{
    /// <summary>"no date": what an undated row and EVERY rejected key read as.</summary>
    public const int None = -1;

    /// <summary>The year range a <see cref="DateTime"/> can actually hold. A key outside it is not a date.</summary>
    const int MinYear = 1, MaxYear = 9999;

    // ── minting ──

    /// <summary>The day key of a year, month and day, or <see cref="None"/> when that is not a day (a year outside
    /// <see cref="DateTime"/>'s range, a zero month, a zero day, a February 30th).</summary>
    public static int DayKey(int year, int month, int day)
        => (uint)(year - MinYear) <= MaxYear - MinYear && (uint)(month - 1) < 12u && (uint)(day - 1) < (uint)DateTime.DaysInMonth(year, month)
            ? year * 10000 + month * 100 + day
            : None;

    /// <summary>The day key of a unix-seconds stamp in the LISTENER'S clock (a row's date is the date they saw it
    /// published); <see cref="None"/> for an unknown date.</summary>
    public static int DayKeyOfLocal(int unixSeconds)
    {
        if (unixSeconds <= 0) return None;
        var d = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime();
        return DayKey(d.Year, d.Month, d.Day);
    }

    // ── decoding (total) ──

    /// <summary>Splits a day key; false for anything that is not one.</summary>
    public static bool TryDay(int dayKey, out int year, out int month, out int day)
    {
        if (dayKey > 0)
        {
            int y = dayKey / 10000, m = dayKey / 100 % 100, d = dayKey % 100;
            if (DayKey(y, m, d) == dayKey)
            {
                (year, month, day) = (y, m, d);
                return true;
            }
        }
        (year, month, day) = (None, 0, 0);
        return false;
    }

    /// <summary>Is this a key this type could have minted?</summary>
    public static bool IsDayKey(int dayKey) => TryDay(dayKey, out _, out _, out _);

    /// <summary>The day a day key names; false leaves <paramref name="date"/> at <c>default</c> — the one place a
    /// <see cref="DateTime"/> is built from a key, and it is built only from components this type has already validated.</summary>
    public static bool TryDayDate(int dayKey, out DateTime date)
    {
        if (TryDay(dayKey, out int year, out int month, out int day))
        {
            date = new DateTime(year, month, day);
            return true;
        }
        date = default;
        return false;
    }

    // ── labels (the render path's entry points: a label, never an exception) ──

    /// <summary>"MMM d" of a day key in <paramref name="culture"/>; "" for a key that is not one.</summary>
    public static string DayLabel(int dayKey, CultureInfo culture)
        => TryDayDate(dayKey, out var date) ? date.ToString("MMM d", culture) : "";
}
