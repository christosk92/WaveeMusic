// ── Home/Board/Hosts/HomeBoardZones.cs — the zones the widget tiles draw (pinned-home-tech.md 1.4, 2.5) ─────────────────────────────
//
// Role: CORE. The Daylist, Recently played and New releases widgets draw today's bodies, which take a `Zone`: the Daylist, RecentGrid and
// ReleaseList zones of the All answer (HomeProjection.WidgetZones, by the section chooser) (the release list from the `music-following-chip` answer `HomeScreen` prefetches). One plan per answer version,
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
    public static Zone RecentsZone(BoardEntry e) => RecentsZone();

    /// <summary>The Recently played zone (the shortcuts grid's other face reads it with no entry of its own).</summary>
    public static Zone RecentsZone() => Current().Recents ?? s_noRecents;

    /// <summary>The Following release list; while its answers are on their way, <see cref="ReleasesSeed"/> (a board sizes the tile by it).</summary>
    public static Zone ReleasesZone(BoardEntry e) => ReleasesZone();

    /// <summary>The Following release list with no entry of its own (its "See all" page reads it).</summary>
    public static Zone ReleasesZone()
        => Current().Releases ?? (ReleasesState() == HomeFeedState.Loading ? ReleasesSeed : s_noReleases);

    /// <summary>The shape a pending release list will have: a full list of blank albums under the widget's title (Spotify's Following answer
    /// fills every row a theme shows), so a placeholder derived from it, and a board's estimate, are the loaded list's own height.</summary>
    public static readonly Zone ReleasesSeed = new(ZoneKind.ReleaseList, "home:widget:releases", null, null, Blanks(HomeCardKind.Album, ReleaseSeedRows));

    /// <summary>The shape a pending daylist will have: its one card, blank.</summary>
    public static readonly Zone DaylistSeed = new(ZoneKind.Daylist, "home:widget:daylist", null, null, Blanks(HomeCardKind.Playlist, 1));

    /// <summary>More rows than any theme's release list shows.</summary>
    const int ReleaseSeedRows = 12;

    static HomeCard[] Blanks(HomeCardKind kind, int n)
    {
        var cards = new HomeCard[n];
        for (int i = 0; i < n; i++) cards[i] = HomeCard.Blank(kind, i);
        return cards;
    }

    /// <summary>Where the daylist widget's data stands: the All answer's state (a subscribing read).</summary>
    public static HomeFeedState DaylistState()
    {
        _ = Entities.ScopeEpoch.Value;
        _ = Home.Changed.Value;                      // the answer landing (Known and the in-flight seat are table columns, not signals)
        return HomeProjection.FeedStateOf(Entities.HomeFeed(""));
    }

    /// <summary>Where the New releases widget's data stands (a subscribing read): the All answer's state, and while All is on hand, still
    /// Loading until the Following answer has concluded too (the list is both answers' albums; showing All's first would grow the tile when
    /// the Following answer lands). A Following answer that failed or never comes leaves the list to All's albums.</summary>
    public static HomeFeedState ReleasesState()
    {
        var all = DaylistState();
        if (all != HomeFeedState.Ready) return all;
        var fh = Entities.HomeFeed("music-following-chip");
        return fh.IsValid && !fh.Knows(HomeFields.Sections) && !fh.LiveAttemptConcluded ? HomeFeedState.Loading : HomeFeedState.Ready;
    }

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
        var (daylist, recents, releases) = HomeProjection.WidgetZones(inputs, following, HomeZoneTitles.Localized(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return s_plan = new Plan(daylist, recents, releases);
    }
}
