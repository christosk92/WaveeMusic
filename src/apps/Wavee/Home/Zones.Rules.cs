// ── Home/Zones.Rules.cs — pure rules Zones.UI.cs builds its zone bodies from (Wave 3, agent B2) ────────────────────────
//
// Role: CORE
// Owner: B2
// Wave: 3
// Spec: docs/plans/wavee/home-redesign-implementation.md "Workstream H", "Zone → controls → metrics" sheet
//        (ReleaseList row 9 .rel "type badge" + "date col 88"; CoverShelf/RadioShelf row 6 "two-cell wide lead").
//
// Two small pure fragments pulled out of Zones.UI.cs so they are unit-testable with no engine running (the
// "no source-text tests" rule — these are exercised directly, not by grepping the UI file):
//   • ReleaseListRules — the Releases list row's type badge label and trailing date column text.
//   • ShelfLead        — merges a Zone's separately-carried Lead card back into its Items array for a PagedShelf's
//                         item list, tolerating either ZonePlanner shape (AddCoverShelf keeps the lead AT items[0]
//                         already; RadioMerge removes it from items and carries it only via Zone.Lead).
//
// Engine-free, pure: no hooks, no elements, no I/O. HomeCardKind/HomeCard are plain data reads (Entities/Home.cs),
// the same convention Home/ZonePlanner.cs already uses for its own pure rules.

using System;
using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Foundation;

namespace Wavee.HomeUi;

/// <summary>The Releases list row's two caller-owned text cells (<see cref="Items.ListRow"/>'s <c>typeLabel</c>/
/// <c>dateLabel</c> params) — kept out of Zones.UI.cs so the ladder-vs-short-date choice is independently
/// testable.</summary>
public static class ReleaseListRules
{
    /// <summary>The row's type badge (sheet row 9 .rel: a small filled pill naming what kind of release this is).</summary>
    public static string TypeLabel(HomeCardKind kind) => kind switch
    {
        HomeCardKind.Album => "Album",
        HomeCardKind.Playlist => "Playlist",
        HomeCardKind.Artist => "Artist",
        HomeCardKind.Track => "Song",
        HomeCardKind.Episode => "Episode",
        HomeCardKind.Podcast => "Podcast",
        HomeCardKind.Audiobook => "Audiobook",
        _ => "",
    };

    /// <summary>The trailing 88-wide date column. <paramref name="releasedAtMs"/> ≤ 0 (unknown) renders blank. A
    /// FUTURE release (<see cref="HomeReleaseDetect"/>'s "Upcoming" case: the date is after <paramref name="nowMs"/>)
    /// renders a short calendar date directly rather than through <see cref="WhenCaption"/>'s played-relative ladder,
    /// which has no rung for a date that hasn't happened yet; a past release reuses that same ladder (so a release
    /// from a few days ago reads "3 d ago"-shaped, consistent with every other "when" caption on the page).</summary>
    public static string DateLabel(long releasedAtMs, long nowMs, TimeZoneInfo tz, CultureInfo culture)
    {
        if (releasedAtMs <= 0) return "";
        if (releasedAtMs > nowMs)
        {
            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(
                DateTimeOffset.FromUnixTimeMilliseconds(releasedAtMs).UtcDateTime, tz);
            return $"{local.Day} {culture.DateTimeFormat.GetAbbreviatedMonthName(local.Month)}";
        }
        return WhenCaption.Of(releasedAtMs, nowMs, playingNow: false, tz, culture);
    }
}

/// <summary>Merges a <see cref="Zone"/>'s separately-carried <see cref="Zone.Lead"/> back into the item list a
/// PagedShelf renders, tolerating either shape <c>Home/ZonePlanner.cs</c> produces: <c>AddCoverShelf</c> (Made for
/// you) inserts the lead INTO <c>Items[0]</c> already; <c>RadioMerge</c> (Radio &amp; mixes) removes it from
/// <c>Items</c> and carries it only via <see cref="Zone.Lead"/>. A caller that always fed <c>Zone.Items</c> straight
/// to a shelf would render the lead twice for the first shape and not at all for the second — this normalises both
/// to "lead at index 0, exactly once".</summary>
public static class ShelfLead
{
    /// <summary>The side of one of the <paramref name="span"/> ordinary cover squares a two-cell-wide lead sits
    /// beside, given the lead's own column width <paramref name="leadW"/> and the shelf's inter-cell
    /// <paramref name="gap"/>: the <paramref name="span"/> squares plus their <c>span − 1</c> internal gaps fill the
    /// same <paramref name="leadW"/> the lead column occupies.</summary>
    public static float SquareWidth(float leadW, float gap, int span) => (leadW - gap * (span - 1)) / span;

    /// <summary>The lead cover's own aspect ratio so its rendered height exactly matches the stacked squares'
    /// height (row 6's "two-cell wide lead"): both covers share the same <paramref name="cardPad"/> inset, so
    /// <c>(leadW − 2·pad) / aspect == squareWidth − 2·pad</c> solved for <c>aspect</c>.</summary>
    public static float LeadAspect(float leadW, float gap, int span, float cardPad)
        => (leadW - 2f * cardPad) / (SquareWidth(leadW, gap, span) - 2f * cardPad);

    public static IReadOnlyList<HomeCard> Merge(IReadOnlyList<HomeCard> items, HomeCard? lead)
    {
        if (lead is not { } l) return items;
        if (items.Count > 0 && items[0].Equals(l)) return items;

        var merged = new HomeCard[items.Count + 1];
        merged[0] = l;
        for (int i = 0; i < items.Count; i++) merged[i + 1] = items[i];
        return merged;
    }
}

