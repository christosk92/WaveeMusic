// ── Home/Board/Hosts/HomeBoardZones.cs — the zones the widget tiles draw (pinned-home-tech.md 1.4, 2.5) ─────────────────────────────
//
// Role: CORE. The Daylist, Recently played and New releases widgets draw today's bodies, which take a `Zone`: the planner's Daylist, RecentGrid and
// ReleaseList zones of the All answer (the release list from the `music-following-chip` answer `HomeScreen` prefetches). One plan per answer version,
// cached; a subscribing read (the tile hosts re-render when the answer lands). A section tile's zone is the projection's own (`BoardEntry.Zone`).

using System.Collections.Generic;
using Wavee.HomeUi;

namespace Wavee;

/// <summary>The zones the widget tiles read.</summary>
public static class HomeBoardZones
{
    sealed record Plan(Zone? Daylist, Zone? Recents, Zone? Releases);

    static Plan s_plan = new(null, null, null);
    static (uint Epoch, uint V, uint SV, uint FV, uint FSV, uint Edges) s_stamp;
    static readonly Zone s_noDaylist = new(ZoneKind.Daylist, "home:widget:daylist", null, null, []);
    static readonly Zone s_noRecents = new(ZoneKind.RecentGrid, "home:widget:recents", null, null, []);
    static readonly Zone s_noReleases = new(ZoneKind.ReleaseList, "home:widget:releases", null, null, []);

    /// <summary>The daylist zone (empty until the answer lands).</summary>
    public static Zone DaylistZone(BoardEntry e) => Current().Daylist ?? s_noDaylist;

    /// <summary>The Recently played zone.</summary>
    public static Zone RecentsZone(BoardEntry e) => Current().Recents ?? s_noRecents;

    /// <summary>The Following release list.</summary>
    public static Zone ReleasesZone(BoardEntry e) => Current().Releases ?? s_noReleases;

    /// <summary>A section tile's zone: the projection's, else an empty shelf with the entry's title.</summary>
    public static Zone SectionZone(BoardEntry e) => e.Zone ?? new Zone(ZoneKind.CoverShelf, "home:section:" + e.Id, e.Title, null, []);

    static Plan Current()
    {
        uint epoch = Entities.ScopeEpoch.Value;
        _ = Home.Changed.Value;
        uint edges = Entities.Current.Edges.SectionCards.Changed.Value;
        var h = Entities.HomeFeed("");
        var fh = Entities.HomeFeed("music-following-chip");
        var stamp = (epoch, h.Version, h.SectionVersion, fh.Version, fh.SectionVersion, edges);
        if (stamp == s_stamp) return s_plan;
        s_stamp = stamp;
        if (!h.Knows(HomeFields.Sections)) return s_plan = new Plan(null, null, null);
        var inputs = SectionReader.Read(h);
        IReadOnlyList<SectionInput>? following = fh.Knows(HomeFields.Sections) ? SectionReader.Read(fh) : null;
        var zones = ZonePlanner.Plan(inputs, "", HomeScreen.LocalizedTitles(), null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), following,
                                     HomeScreen.LocalizedSubtitles());
        Zone? daylist = null, recents = null, releases = null;
        foreach (var z in zones)
            switch (z.Kind)
            {
                case ZoneKind.Daylist: daylist ??= z; break;
                case ZoneKind.RecentGrid: recents ??= z; break;
                case ZoneKind.ReleaseList: releases ??= z; break;
            }
        return s_plan = new Plan(daylist, recents, releases);
    }
}
