using System;
using System.Collections.Generic;
using Wavee.Core;

namespace Wavee;

/// <summary>What a timeline row IS — which badge it wears and which click it performs. One row shape, two sources.</summary>
public enum HomeTimelineKind
{
    /// <summary>A what's-new album/episode from a followed artist or show.</summary>
    Release,
    /// <summary>A Spotify-category concert / live-show announcement (including a "days away" reminder).</summary>
    Concert,
}

/// <summary>One row: the source notification, plus the two fields the merge sorts and counts on lifted out of it so the
/// grouping arithmetic never has to type-test.</summary>
public readonly record struct HomeTimelineRow(HomeTimelineKind Kind, WaveeNotification Source, string Id, long Timestamp, bool IsUnread)
{
    public NewReleaseNotification? Release => Source as NewReleaseNotification;
    public SocialNotification? Update => Source as SocialNotification;
}

/// <summary>One day group, newest day first, rows within it newest first. <paramref name="DayTicks"/> is LOCAL midnight
/// (a <see cref="DateTime"/> tick count) — the feed's timestamps are UTC epoch ms, and bucketing them without the
/// local-midnight conversion puts an evening item under "yesterday" for anyone east of UTC.</summary>
public readonly record struct HomeTimelineGroup(long DayTicks, HomeTimelineRow[] Rows);

/// <summary>The whole module's data: the capped, grouped rows plus the "<c>N</c> unheard of <c>M</c>" pair, which counts
/// the UNCAPPED eligible set (the header describes the feed, not the eight rows on screen).</summary>
public readonly record struct HomeTimelineFeed(HomeTimelineGroup[] Groups, int Shown, int Total, int Unread)
{
    public static readonly HomeTimelineFeed Empty = new([], 0, 0, 0);
    public bool IsEmpty => Groups.Length == 0;
}

/// <summary>The timeline's pure merge: which notifications belong on Home's what's-new module, in what order, in which
/// day group, and what the header's counter says. Engine-free and clock-injectable so it is unit-testable on its own —
/// the component is the thin render over it.
///
/// <para><b>The gate is on KIND, never on the category pill.</b> What's-new items are all eligible. Spotify-category
/// (gander) items are eligible ONLY when <see cref="SpotifyUpdates.IsConcert"/> says so — followers, generic
/// announcements, app updates and local activity are not timeline material and never appear here. That is why the
/// module is unchanged, byte for byte, on an account whose Spotify category holds nothing but follows.</para>
///
/// <para><b>A reminder sorts by its own instant, not by the event's.</b> "Just days away: … on Sat, Aug 15" is NEWS
/// that arrived when it arrived; filing it under the concert date would put a future row at the top of a page whose
/// whole spine reads backwards from today. So the sort key is the notification timestamp the center itself
/// displays.</para></summary>
public static class HomeTimelineMerge
{
    /// <summary>How many rows the module renders. The page's row-height estimate is derived from this same number, so
    /// changing it means changing <c>HomePage</c>'s <c>HomeRow.Timeline</c> estimate with it.</summary>
    public const int MaxRows = 8;

    /// <summary>Build the module's data from the notification center's merged list.</summary>
    /// <param name="items">The center's items (any order — this re-sorts, so it cannot inherit a caller's ordering bug).</param>
    /// <param name="maxRows">Row cap; the counts are deliberately NOT capped.</param>
    /// <param name="dayOf">Local-midnight bucketer; defaults to <see cref="LocalDay"/>. Injectable for tests.</param>
    public static HomeTimelineFeed Build(IReadOnlyList<WaveeNotification>? items, int maxRows = MaxRows, Func<long, long>? dayOf = null)
    {
        if (items is null || items.Count == 0 || maxRows <= 0) return HomeTimelineFeed.Empty;
        dayOf ??= LocalDay;

        var eligible = new List<HomeTimelineRow>(Math.Min(items.Count, 64));
        int unread = 0;
        for (int i = 0; i < items.Count; i++)
        {
            if (Eligible(items[i]) is not { } row) continue;
            eligible.Add(row);
            if (row.IsUnread) unread++;
        }
        if (eligible.Count == 0) return HomeTimelineFeed.Empty;

        // Newest first, with the id as the tie-break so two items minted in the same millisecond order deterministically
        // (the center's own list is already sorted, but a stable answer is what makes the grouping testable).
        eligible.Sort(static (a, b) =>
        {
            int c = b.Timestamp.CompareTo(a.Timestamp);
            return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
        });

        int shown = Math.Min(maxRows, eligible.Count);
        var groups = new List<HomeTimelineGroup>(4);
        var run = new List<HomeTimelineRow>(shown);
        long day = 0;
        for (int i = 0; i < shown; i++)
        {
            long d = dayOf(eligible[i].Timestamp);
            if (run.Count > 0 && d != day)
            {
                groups.Add(new HomeTimelineGroup(day, run.ToArray()));
                run.Clear();
            }
            day = d;
            run.Add(eligible[i]);
        }
        if (run.Count > 0) groups.Add(new HomeTimelineGroup(day, run.ToArray()));

        return new HomeTimelineFeed(groups.ToArray(), shown, eligible.Count, unread);
    }

    /// <summary>The row a notification contributes, or null when it is not timeline material.</summary>
    public static HomeTimelineRow? Eligible(WaveeNotification? n) => n switch
    {
        NewReleaseNotification r => new HomeTimelineRow(HomeTimelineKind.Release, r, r.Id, r.Timestamp, r.IsUnread),
        SocialNotification s when SpotifyUpdates.IsConcert(s) => new HomeTimelineRow(HomeTimelineKind.Concert, s, s.Id, s.Timestamp, s.IsUnread),
        _ => null,
    };

    /// <summary>Local midnight for a UTC epoch-ms instant, as <see cref="DateTime"/> ticks.</summary>
    public static long LocalDay(long unixMs)
        => DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().Date.Ticks;
}
