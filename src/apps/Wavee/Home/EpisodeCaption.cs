// ── Home/EpisodeCaption.cs — podcast episode row captions: date, duration, remaining, progress (Wave 1, agent A2) ──
//
// Role: CORE
// Owner: A2
// Wave: 1
// Spec: docs/plans/wavee/home-redesign/06-facet-design.md §3.3 (P1–P6 captions), §5.2 (episode row / progress hairline)
//
// Pure, engine-free: callers own the clock (`nowMs`), the timezone and the culture — nothing here reads the system
// clock. `Date` reuses `WhenCaption`'s calendar-day classification (Home/Time.cs, same file/namespace, owner A4) for
// its Today/Yesterday/weekday rungs — the episode-row date ladder in `06 §3.3` ("Today / Yesterday / weekday name
// within 6 days / '23 Sep' within the year / '6 May 2025' otherwise") is `WhenCaption`'s own ladder with exactly one
// addition: a year suffix once the release crosses a calendar-year boundary, which `WhenCaption.DateShort` does not
// carry (its own callers — Recents "when" — never need a year, an episode row does: E13/E15 in `podcasts.json` are
// months old but same-year and print without one, and the plan's own "6 May 2025" example is the far-side case).
//
// Localization note (remediation §3.11, reversing this file's original "not reused on purpose" call): `Date` reuses
// `Strings.Detail.Today`/`Strings.Detail.Yesterday` — the Detail page's own keys — rather than minting a Home twin;
// one key per phrase, app-wide. It resolves them through the same caller-supplied `localize` delegate as
// `WhenCaption.Format` (`null` defaults to `Loc.Get`). `Duration`/`Remaining` stay literal English — there is no
// loc key yet for the "27 min" / "1 h 1 min" / "… left" shapes, and minting one is a future loc pass, not this cut.

using System;
using System.Globalization;
using FluentGpu.Localization;

namespace Wavee.HomeUi;

/// <summary>The Podcasts facet's episode-row captions (06 §3.3 P1–P6, §5.2): the release date ladder, the duration
/// and "remaining" strings, and the in-progress hairline's fill fraction/width.</summary>
public static class EpisodeCaption
{
    /// <summary>"Today" / "Yesterday" / a weekday name (2–6 days) / "23 Sep" (this year) / "6 May 2025" (an earlier
    /// year) — `WhenCaption`'s own calendar-day ladder plus the year suffix an episode row needs and a "when played"
    /// caption does not. <paramref name="releasedMs"/> &lt;= 0 (unknown) answers "".</summary>
    public static string Date(long releasedMs, long nowMs, TimeZoneInfo tz, CultureInfo culture, Func<string, string>? localize = null)
    {
        if (releasedMs <= 0) return "";
        Func<string, string> resolve = localize ?? Loc.Get;
        var result = WhenCaption.Classify(releasedMs, nowMs, playingNow: false, tz);
        // An episode's release is a CALENDAR day, not a moment — a same-day release reads "Today", never
        // WhenCaption's own "12 min ago"/"2 h ago" (those exist for a "when played" caption, not a release date).
        if (result.Kind is WhenKind.MinutesAgo or WhenKind.HoursAgo) return resolve(Strings.Detail.Today);
        if (result.Kind != WhenKind.DateShort) return WhenCaption.Format(result, culture, resolve);

        DateTime releasedLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeMilliseconds(releasedMs).UtcDateTime, tz);
        DateTime nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeMilliseconds(nowMs).UtcDateTime, tz);
        string monthDay = WhenCaption.Format(result, culture, resolve);
        return releasedLocal.Year == nowLocal.Year ? monthDay : $"{monthDay} {releasedLocal.Year}";
    }

    /// <summary>"27 min" / "1 h 1 min" / "2 h 10 min" — rounded to the nearest minute (never truncated: a 43.9 min
    /// remainder reads "44 min", matching the capture's own "8 334 ms of 44 min" episode). &lt;= 0 answers "".</summary>
    public static string Duration(long durationMs) => durationMs <= 0 ? "" : MinutesLabel(durationMs);

    /// <summary>"{duration − position} left" — never negative (a position past the duration floors at 0), rounded
    /// like <see cref="Duration"/>.</summary>
    public static string Remaining(long durationMs, long positionMs)
    {
        long left = durationMs - positionMs;
        if (left < 0) left = 0;
        return MinutesLabel(left) + " left";
    }

    /// <summary>The in-progress hairline's fill fraction, 0..1. A non-positive duration (unknown) answers 0.</summary>
    public static double ProgressFraction(long positionMs, long durationMs)
        => durationMs <= 0 ? 0 : Math.Clamp((double)positionMs / durationMs, 0d, 1d);

    static string MinutesLabel(long ms)
    {
        long totalMinutes = Math.Max(1, (long)Math.Round(ms / 60_000d, MidpointRounding.AwayFromZero));
        long hours = totalMinutes / 60, minutes = totalMinutes % 60;
        if (hours <= 0) return $"{minutes} min";
        return minutes == 0 ? $"{hours} h" : $"{hours} h {minutes} min";
    }
}
