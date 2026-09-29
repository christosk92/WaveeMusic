// ── Home/Time.cs — daypart classification, the daylist countdown, the Recents week sparkline and "when" captions ────
//
// Four small, unrelated clock-and-calendar rule sets that every Home zone needs and none of them owns:
// `DaypartRules` (the daylist card's five-segment timeline + countdown, row 3), `WeekSummary` (the listening-history
// item's 7-bar sparkline, row 21), `WhenCaption` (the Recently played "when", row 4 / episode row dates, row 16) and
// `DaylistNext` (the next-window arrival caption).
// Engine-free, pure: callers own the clock (`nowMs`) and the timezone/culture; nothing here reads the system clock
// or is a signal. `Home/Time.cs` never allocates in the hot paths (`FormatCountdown` writes into a caller buffer).
//
// Sixth pass (docs/plans/wavee/home-rebuild-implementation.md, "Sixth pass — prototype visuals on the shared
// controls"): `RecentsWeek`/`WeekSummary` are RESTORED verbatim from commit a4842b9f — the history tile in
// `Zones.UI.cs`'s recents grid needs the 7-day sparkline again.
//
// Role: CORE
// Owner: A4
// Wave: 1
// Spec: docs/plans/wavee/home-redesign-implementation.md (Workstream H) + Components.dc.html rows 3, 4, 16
//
// Localization note (remediation §3.11): `home.when.playingNow`/`minAgo`/`hAgo` landed in Wave 1
// (`assets/loc/en-US.json`), and `Yesterday` reuses the Detail page's own `Strings.Detail.Yesterday` rather than a
// Home twin — one key per phrase, app-wide. `WhenCaption` still splits into a pure, fully testable `Classify` (a
// `WhenResult` token: which rung of the ladder, plus its raw numbers) and a `Format` that resolves the fixed-phrase
// rungs through a caller-supplied `localize` delegate (`Entities/Recents.cs`'s own `DayBucketLabel` shape: `null`
// defaults to `Loc.Get`), using `CultureInfo` only for weekday/month names, which carry no loc key of their own.

using System;
using System.Globalization;
using FluentGpu.Localization;

namespace Wavee.HomeUi;

/// <summary>The daylist card's five-segment timeline window (row 3: "Early morning, Morning, Afternoon, Evening,
/// Night").</summary>
public enum Daypart : byte { EarlyMorning, Morning, Afternoon, Evening, Night }

/// <summary>Pure daypart classification: from a local wall-clock hour, or parsed off the trailing word(s) of a
/// daylist title ("scream teen pop friday morning" → <see cref="Daypart.Morning"/>).</summary>
public static class DaypartRules
{
    /// <summary>Hour windows per the plan's Q3 answer: 4–7 early morning, 7–12 morning, 12–17 afternoon, 17–21
    /// evening, 21–4 night. <paramref name="localHour"/> is normalised into 0..23 first, so callers may pass any
    /// integer (e.g. an hour arithmetic result that went negative or past 24).</summary>
    public static Daypart OfHour(int localHour)
    {
        int h = ((localHour % 24) + 24) % 24;
        if (h >= 4 && h < 7) return Daypart.EarlyMorning;
        if (h >= 7 && h < 12) return Daypart.Morning;
        if (h >= 12 && h < 17) return Daypart.Afternoon;
        if (h >= 17 && h < 21) return Daypart.Evening;
        return Daypart.Night; // 21:00–23:59 and 00:00–3:59
    }

    /// <summary>Reads the trailing daypart token off a daylist title. "Early morning" and "late night" are checked
    /// before their shorter suffixes ("morning", "night") so they aren't mis-parsed as the bare word. Returns false
    /// (and leaves <paramref name="daypart"/> at its default) when the title is empty or carries no recognised
    /// trailing token — the daylist title token wins over the hour-of-day derivation (plan Q3), but only when it's
    /// actually present.</summary>
    public static bool TryFromTitle(string? title, out Daypart daypart)
    {
        daypart = default;
        if (string.IsNullOrEmpty(title)) return false;

        ReadOnlySpan<char> t = title.AsSpan().TrimEnd();
        if (t.EndsWith("early morning", StringComparison.OrdinalIgnoreCase)) { daypart = Daypart.EarlyMorning; return true; }
        if (t.EndsWith("late night", StringComparison.OrdinalIgnoreCase)) { daypart = Daypart.Night; return true; }
        if (t.EndsWith("morning", StringComparison.OrdinalIgnoreCase)) { daypart = Daypart.Morning; return true; }
        if (t.EndsWith("afternoon", StringComparison.OrdinalIgnoreCase)) { daypart = Daypart.Afternoon; return true; }
        if (t.EndsWith("evening", StringComparison.OrdinalIgnoreCase)) { daypart = Daypart.Evening; return true; }
        if (t.EndsWith("night", StringComparison.OrdinalIgnoreCase)) { daypart = Daypart.Night; return true; }
        return false;
    }

    /// <summary>Fraction of the daypart window elapsed, clamped to 0..1 (row 3: the segment fill / ring fraction).
    /// A non-positive or already-past window returns 1 rather than dividing by zero or going negative.</summary>
    public static double Elapsed(long createdAtMs, long expiresAtMs, long nowMs)
    {
        long span = expiresAtMs - createdAtMs;
        if (span <= 0) return nowMs >= expiresAtMs ? 1.0 : 0.0;
        double e = (nowMs - createdAtMs) / (double)span;
        return e < 0.0 ? 0.0 : e > 1.0 ? 1.0 : e;
    }

    /// <summary>Formats a remaining-time countdown as "hh:mm:ss" (row 2: "01:35:05") into <paramref name="dst"/>
    /// with zero heap allocation, returning the number of characters written. Negative remainders clamp to zero.
    /// <paramref name="dst"/> must be at least 8 chars long for the common case (&lt;100 h remaining); the daylist
    /// window is always well under that, but the digit count grows past 8 for larger inputs rather than throwing —
    /// callers with an unbounded remainder should size their buffer accordingly.</summary>
    public static int FormatCountdown(long remainingMs, Span<char> dst)
    {
        long totalSec = remainingMs > 0 ? remainingMs / 1000 : 0;
        long hh = totalSec / 3600;
        long mm = (totalSec % 3600) / 60;
        long ss = totalSec % 60;

        int pos = WriteFixedDigits(dst, hh, 2);
        dst[pos++] = ':';
        pos += WriteFixedDigits(dst.Slice(pos), mm, 2);
        dst[pos++] = ':';
        pos += WriteFixedDigits(dst.Slice(pos), ss, 2);
        return pos;
    }

    static int WriteFixedDigits(Span<char> dst, long value, int minDigits)
    {
        int digits = 1;
        long probe = 10;
        while (value >= probe) { digits++; probe *= 10; }
        int width = digits < minDigits ? minDigits : digits;
        for (int i = width - 1; i >= 0; i--)
        {
            dst[i] = (char)('0' + (int)(value % 10));
            value /= 10;
        }
        return width;
    }

    /// <summary>The daypart succeeding <paramref name="current"/>, wrapping <see cref="Daypart.Night"/> back to
    /// <see cref="Daypart.EarlyMorning"/> — the five-segment timeline's own cycle order. A general-purpose step
    /// function (kept independent of any particular clock reading) for a caller reasoning about "what comes after a
    /// known daypart" without re-deriving it from an hour.</summary>
    public static Daypart Next(Daypart current) => (Daypart)(((int)current + 1) % 5);

    /// <summary>Allocating convenience over <see cref="FormatCountdown"/> for call sites that want a plain string
    /// (tests, a one-shot caption) rather than a caller-owned buffer — the daylist card's own tick path still writes
    /// into its own <c>char[8]</c> via <see cref="FormatCountdown"/> directly, never through this.</summary>
    public static string Countdown(long remainingMs)
    {
        Span<char> buf = stackalloc char[16];
        int len = FormatCountdown(remainingMs, buf);
        return new string(buf[..len]);
    }
}

/// <summary>The listening-history item's 7-bar sparkline (row 21): today plus the six days before it, local-day
/// bucketed. <see cref="D0Today"/> is today; <see cref="D6"/> is six days ago (the oldest bar). Indexable by
/// days-ago (0..6) for callers that iterate.</summary>
public readonly record struct RecentsWeek(int Total, int D0Today, int D1, int D2, int D3, int D4, int D5, int D6)
{
    public int this[int daysAgo] => daysAgo switch
    {
        0 => D0Today,
        1 => D1,
        2 => D2,
        3 => D3,
        4 => D4,
        5 => D5,
        6 => D6,
        _ => throw new ArgumentOutOfRangeException(nameof(daysAgo), daysAgo, "days-ago must be 0..6"),
    };

    /// <summary>The tallest bar's count — the sparkline's normalisation denominator.</summary>
    public int Max => Math.Max(D0Today, Math.Max(D1, Math.Max(D2, Math.Max(D3, Math.Max(D4, Math.Max(D5, D6))))));
}

/// <summary>Folds a Recents snapshot into the week's per-day play counts (row 21's sparkline + "142 plays this
/// week"). See <c>Entities/Recents.cs</c> for <see cref="RecentsEdge"/>: a <b>Group</b> row's declared
/// <see cref="RecentsEdge.ChildCount"/> is the number of plays it collapsed, so it counts as that many; a
/// <b>Saved</b> row (library add, not a play) counts as zero; every other (Single, Played) row counts as one play.</summary>
public static class WeekSummary
{
    /// <summary>Buckets <paramref name="rows"/> by the local calendar day (per <paramref name="tz"/>) each play
    /// landed on, relative to <paramref name="today"/>. Rows outside the 7-day window (today .. 6 days ago, and any
    /// row whose local day is in the future relative to <paramref name="today"/>) are ignored — the caller passing
    /// a wider snapshot than one week is expected and harmless.</summary>
    public static RecentsWeek Of(ReadOnlySpan<RecentsEdge> rows, DateOnly today, TimeZoneInfo tz)
    {
        Span<int> perDay = stackalloc int[7];

        for (int i = 0; i < rows.Length; i++)
        {
            RecentsEdge row = rows[i];
            int contribution = row.Why == RecentsReason.Saved ? 0
                : row.Shape == RecentsRowKind.Group ? Math.Max(row.ChildCount, 0)
                : 1;
            if (contribution <= 0) continue;

            DateTime playedUtc = DateTimeOffset.FromUnixTimeMilliseconds(row.PlayedAtMs).UtcDateTime;
            DateTime playedLocal = TimeZoneInfo.ConvertTimeFromUtc(playedUtc, tz);
            DateOnly playedDate = DateOnly.FromDateTime(playedLocal);

            int daysAgo = today.DayNumber - playedDate.DayNumber;
            if (daysAgo is >= 0 and <= 6)
                perDay[daysAgo] += contribution;
        }

        int total = 0;
        for (int i = 0; i < 7; i++) total += perDay[i];
        return new RecentsWeek(total, perDay[0], perDay[1], perDay[2], perDay[3], perDay[4], perDay[5], perDay[6]);
    }
}

/// <summary>Which rung of the Recently played / episode-row "when" ladder a timestamp lands on (row 4: "yesterday",
/// row 16: "Today / Yesterday / weekday within 6 days / '23 Sep' / '6 May 2025'").</summary>
public enum WhenKind : byte { PlayingNow, MinutesAgo, HoursAgo, Yesterday, Weekday, DateShort }

/// <summary>The classified ladder rung plus its raw payload — enough for a caller to format in any locale/voice
/// without re-deriving the classification.</summary>
public readonly record struct WhenResult(WhenKind Kind, int Minutes, int Hours, DayOfWeek Weekday, int Month, int Day);

/// <summary>Formats when something was played/listened to (row 4's "yesterday" caption, row 16's episode dates).
/// <see cref="Classify"/> is the pure, fully testable rule; <see cref="Format"/>/<see cref="Of"/> resolve the
/// fixed-phrase rungs (PlayingNow/MinutesAgo/HoursAgo/Yesterday) through a caller-supplied <c>localize</c> (the
/// same optional-delegate-defaulting-to-<c>Loc.Get</c> shape as `Entities/Recents.cs`'s own `DayBucketLabel`);
/// Weekday/DateShort need no loc key — <see cref="CultureInfo.DateTimeFormat"/> already localizes weekday/month
/// names. `Yesterday` reuses <c>Strings.Detail.Yesterday</c> (the Detail page's own key) rather than a Home twin
/// (remediation §3.11).</summary>
public static class WhenCaption
{
    /// <summary>The calendar-day-based ladder: same local day and under an hour → minutes; same local day and under
    /// a day → hours; local day is exactly one before today → Yesterday; within the six days before that → the
    /// weekday name; older → a short date. "Yesterday"/weekday are calendar-relative (per <paramref name="tz"/>),
    /// not elapsed-time-relative, so a play from 23:59 shows "Yesterday" moments after midnight — same behaviour as
    /// Windows Photos/Mail's date grouping.</summary>
    public static WhenResult Classify(long playedMs, long nowMs, bool playingNow, TimeZoneInfo tz)
    {
        if (playingNow) return new WhenResult(WhenKind.PlayingNow, 0, 0, default, 0, 0);

        long diffMs = nowMs - playedMs;
        if (diffMs < 0) diffMs = 0;

        DateTime playedLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeMilliseconds(playedMs).UtcDateTime, tz);
        DateTime nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeMilliseconds(nowMs).UtcDateTime, tz);
        int daysAgo = DateOnly.FromDateTime(nowLocal).DayNumber - DateOnly.FromDateTime(playedLocal).DayNumber;

        long minutes = diffMs / 60_000;
        long hours = diffMs / 3_600_000;

        if (daysAgo <= 0 && minutes < 60)
            return new WhenResult(WhenKind.MinutesAgo, (int)Math.Max(minutes, 0), 0, default, 0, 0);
        if (daysAgo <= 0 && hours < 24)
            return new WhenResult(WhenKind.HoursAgo, 0, (int)hours, default, 0, 0);
        if (daysAgo == 1)
            return new WhenResult(WhenKind.Yesterday, 0, 0, default, 0, 0);
        if (daysAgo is > 1 and <= 6)
            return new WhenResult(WhenKind.Weekday, 0, 0, playedLocal.DayOfWeek, 0, 0);
        return new WhenResult(WhenKind.DateShort, 0, 0, default, playedLocal.Month, playedLocal.Day);
    }

    /// <summary>Renders "Playing now" / "12 min ago" / "2 h ago" / "Yesterday" / a weekday name / "23 Sep".
    /// <paramref name="localize"/> resolves a plain <c>Strings.*</c> key to its localized text (<c>null</c>
    /// defaults to <see cref="Loc.Get"/>, same as production call sites); the two ICU rungs (MinutesAgo/HoursAgo)
    /// resolve their <c>"{n} …"</c> template through it and substitute the count locally — this file stays clock/
    /// culture-only otherwise, no <c>FluentGpu</c> reactive dependency beyond the delegate itself.</summary>
    public static string Format(WhenResult result, CultureInfo culture, Func<string, string>? localize = null)
    {
        Func<string, string> resolve = localize ?? Loc.Get;
        return result.Kind switch
        {
            WhenKind.PlayingNow => resolve(Strings.Home.When.PlayingNow),
            WhenKind.MinutesAgo => resolve(Strings.Home.When.MinAgoKey).Replace("{n}", Math.Max(result.Minutes, 1).ToString(culture)),
            WhenKind.HoursAgo => resolve(Strings.Home.When.HAgoKey).Replace("{n}", Math.Max(result.Hours, 1).ToString(culture)),
            WhenKind.Yesterday => resolve(Strings.Detail.Yesterday),
            WhenKind.Weekday => culture.DateTimeFormat.GetDayName(result.Weekday),
            WhenKind.DateShort => $"{result.Day} {culture.DateTimeFormat.GetAbbreviatedMonthName(result.Month)}",
            _ => "",
        };
    }

    /// <summary>Convenience: <see cref="Classify"/> then <see cref="Format"/>.</summary>
    public static string Of(long playedMs, long nowMs, bool playingNow, TimeZoneInfo tz, CultureInfo culture, Func<string, string>? localize = null)
        => Format(Classify(playedMs, nowMs, playingNow, tz), culture, localize);
}

/// <summary>The daylist card's "· friday afternoon arrives at 13:04" caption (row 3, `Home/Daylist.UI.cs`'s
/// `DaypartTimeline`) — self-contained from <paramref name="expiresAtMs"/> alone: the instant a window ends IS the
/// instant the next edition starts, so the weekday/daypart named are simply that instant's own local calendar day and
/// <see cref="DaypartRules.OfHour"/> bucket, never a separately-tracked "current" daypart plus
/// <see cref="DaypartRules.Next"/> step (which would need one more input than this caption's signature carries and
/// can disagree with the real arrival hour at a window that doesn't land exactly on an <c>OfHour</c> boundary).</summary>
public static class DaylistNext
{
    /// <summary>"{weekday} {daypart} arrives at {HH:mm}", both lowercase, local to <paramref name="tz"/>
    /// (<paramref name="localize"/> resolves the <c>Strings.Home.Daypart.*</c> word only — <c>null</c> defaults to
    /// <see cref="Loc.Get"/>, the same optional-delegate shape as <see cref="WhenCaption.Format"/>; the outer
    /// "arrives at" template always goes through the real <c>Strings.Home.Daylist.ArrivesAt</c>, which carries no
    /// override seam of its own). <paramref name="expiresAtMs"/> &lt;= 0 (no window) returns "".</summary>
    public static string Caption(long expiresAtMs, TimeZoneInfo tz, CultureInfo culture, Func<string, string>? localize = null)
    {
        if (expiresAtMs <= 0) return "";
        Func<string, string> resolve = localize ?? Loc.Get;

        DateTime local = TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeMilliseconds(expiresAtMs).UtcDateTime, tz);
        string day = culture.DateTimeFormat.GetDayName(local.DayOfWeek).ToLower(culture);
        string part = DaypartLabel(DaypartRules.OfHour(local.Hour), resolve).ToLower(culture);
        string time = local.ToString("HH:mm", culture);
        return Strings.Home.Daylist.ArrivesAt(day + " " + part, time);
    }

    static string DaypartLabel(Daypart part, Func<string, string> resolve) => part switch
    {
        Daypart.EarlyMorning => resolve(Strings.Home.Daypart.Early),
        Daypart.Morning => resolve(Strings.Home.Daypart.Morning),
        Daypart.Afternoon => resolve(Strings.Home.Daypart.Afternoon),
        Daypart.Evening => resolve(Strings.Home.Daypart.Evening),
        _ => resolve(Strings.Home.Daypart.Night),
    };
}
