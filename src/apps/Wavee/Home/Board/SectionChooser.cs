// ── Home/Board/SectionChooser.cs: one Spotify section in, one template out (pinned-home design 2.3, spec 3.1) ─────
//
// Reads structure only: the wire typename, the histogram of card kinds, formats, counts, the header entity, which fields
// are present (header images, video, preview tracks). Never a title. The table, first match wins:
//
//   HomeShortsSectionData                         → ShortcutFill   (feeds the Pinned group's fill; takes no room)
//   HomeRecentlyPlayedSectionData                 → RecentsData    (the Recently played widget's data)
//   no drawable card                              → None           (omitted, named in the plan with its typename)
//   a daylist playlist in the lead                → DaylistHero
//   HomeFeedBaselineSectionData                   → by its card: an untitled album = ReleaseList, an episode = VideoTiles
//                                                   or EpisodeRows, a book = BookShelf, a show = ShowGrid, else PreviewCard
//   mostly episodes                               → VideoTiles (video with a thumbnail) or EpisodeRows
//   mostly audiobooks / mostly shows              → BookShelf / ShowGrid
//   half or more colour-plate mixes (descripto)   → MoodPlates
//   two thirds or more carry an editorial header  → WideTiles (a generated mix's header image does not count)
//   albums in the release window                  → ReleaseList
//   a typename this build knows                   → EntityShelf (with a header entity) or CoverShelf
//   ANYTHING ELSE, an unknown typename included   → PlainShelf, a cover shelf of whatever cards decode
//
// MoodPlates and BookShelf draw their own zone kinds (`Home.Zone.MoodPlates`, `Home.Zone.BookShelf`; home-gap P6, P8).
// Counts are the theme's form, not a template: one card is a single wide card, two or three a short row, four or more a
// shelf. Pure.

using Wavee.HomeUi;

namespace Wavee;

public readonly record struct SectionChoice(SectionTemplate Template, ZoneKind Zone, string Reason);

/// <summary>One line of the planner dump (`home do=plan`): a section, its identity, its counts and the chooser's verdict.</summary>
public sealed record SectionPlan(int Index, string Key, ulong Fp, string Family, string TypeName, int Cards, int Usable,
    int Unsupported, int Duplicates, int Total, SectionTemplate Template, ZoneKind Zone, string Reason);

public static class SectionChooser
{
    /// <summary>The typenames this build knows. Anything else reaches the table's last row.</summary>
    public const string Shorts = "HomeShortsSectionData", Generic = "HomeGenericSectionData",
        Recents = "HomeRecentlyPlayedSectionData", Baseline = "HomeFeedBaselineSectionData", Spotlight = "HomeSpotlightSectionData";

    public static SectionChoice Choose(SectionInput s, long nowMs)
    {
        string type = s.TypeName.Length > 0 ? s.TypeName : TypeNameOf(s.Kind);
        if (type == Shorts) return new(SectionTemplate.ShortcutFill, ZoneKind.RecentGrid, "shorts: the Pinned group's fill");
        if (type == Recents) return new(SectionTemplate.RecentsData, ZoneKind.RecentGrid, "recently played: the recents widget's data");

        int usable = 0, playlists = 0, albums = 0, artists = 0, episodes = 0, video = 0, books = 0, shows = 0, plates = 0, headers = 0;
        HomeCard? lead = null;
        for (int i = 0; i < s.Cards.Count; i++)
        {
            var c = s.Cards[i];
            if (!SectionRoles.IsUsable(c)) continue;
            usable++;
            lead ??= c;
            switch (c.Kind)
            {
                case HomeCardKind.Playlist or HomeCardKind.Liked: playlists++; break;
                case HomeCardKind.Album: albums++; break;
                case HomeCardKind.Artist: artists++; break;
                case HomeCardKind.Episode: episodes++; if (c.HasVideo && c.VideoThumbUrl is not null) video++; break;
                case HomeCardKind.Audiobook: books++; break;
                case HomeCardKind.Podcast: shows++; break;
            }
            if (c.Format == "descripto") plates++;
            // Every generated mix and station carries a desktop header image now (1.3.8.620 capture), so a header counts
            // toward wide tiles only on an editorial card: a shelf of mixes or stations stays a cover shelf.
            if (c.HeaderImageUrl is not null && !Generated(c.Format)) headers++;
        }
        if (usable == 0) return new(SectionTemplate.None, ZoneKind.EmptyFacet, "no drawable card (" + type + ")");

        if (lead is { } l && l.Kind == HomeCardKind.Playlist && l.Format is { } f && f.StartsWith("daylist", StringComparison.Ordinal))
            return new(SectionTemplate.DaylistHero, ZoneKind.Daylist, "a daylist in the lead (" + l.Uri + ")");

        if (type == Baseline)
        {
            var c = lead!.Value;
            return c.Kind switch
            {
                HomeCardKind.Album when string.IsNullOrEmpty(s.Title) => new(SectionTemplate.ReleaseList, ZoneKind.ReleaseList, "baseline: an untitled album (a followed release)"),
                HomeCardKind.Episode => c.HasVideo && c.VideoThumbUrl is not null
                    ? new(SectionTemplate.VideoTiles, ZoneKind.VideoTiles, "baseline: a video episode")
                    : new(SectionTemplate.EpisodeRows, ZoneKind.EpisodeRows, "baseline: an episode"),
                HomeCardKind.Audiobook => new(SectionTemplate.BookShelf, ZoneKind.BookShelf, "baseline: a book"),
                HomeCardKind.Podcast => new(SectionTemplate.ShowGrid, ZoneKind.ShowGrid, "baseline: a show"),
                _ => new(SectionTemplate.PreviewCard, ZoneKind.ClusterCards,
                    s.Preview.Count > 0 ? "baseline: a card with preview tracks" : "baseline: a card (preview tracks not landed)"),
            };
        }

        if (episodes * 2 > usable)
            return video * 2 >= episodes
                ? new(SectionTemplate.VideoTiles, ZoneKind.VideoTiles, $"episodes, {video} of {episodes} with video")
                : new(SectionTemplate.EpisodeRows, ZoneKind.EpisodeRows, $"episodes, {video} of {episodes} with video");
        if (books * 2 > usable) return new(SectionTemplate.BookShelf, ZoneKind.BookShelf, $"audiobooks ({books} of {usable})");
        if (shows * 2 > usable) return new(SectionTemplate.ShowGrid, ZoneKind.ShowGrid, $"shows ({shows} of {usable})");
        if (plates * 2 >= usable) return new(SectionTemplate.MoodPlates, ZoneKind.MoodPlates, $"colour-plate mixes ({plates} of {usable})");
        if (usable >= 2 && headers * 3 >= usable * 2) return new(SectionTemplate.WideTiles, ZoneKind.WideTiles, $"header images ({headers} of {usable})");
        if (ReleaseDetect.IsReleases(s, nowMs)) return new(SectionTemplate.ReleaseList, ZoneKind.ReleaseList, "albums in the release window");

        string counted = usable >= 4 ? "a shelf" : usable == 1 ? "a single card" : "a short row";
        if (type is Generic or Spotlight)
            return s.HeaderUri is not null
                ? new(SectionTemplate.EntityShelf, ZoneKind.CoverShelf, counted + " with a header entity")
                : new(SectionTemplate.CoverShelf, ZoneKind.CoverShelf, counted);
        return new(SectionTemplate.PlainShelf, ZoneKind.CoverShelf, "unknown typename " + type + ": " + counted);
    }

    /// <summary>The section as one zone of its template's kind, under its own server title (the neutral "More for you"
    /// when it has none). The zone key is the section uri, so a section keeps its node across answers.</summary>
    public static Zone ToZone(SectionInput s, in SectionChoice choice, ZoneTitles titles)
    {
        var cards = new List<HomeCard>(s.Cards.Count);
        for (int i = 0; i < s.Cards.Count; i++) if (SectionRoles.IsUsable(s.Cards[i])) cards.Add(s.Cards[i]);
        string key = "home:section:" + s.Uri;
        string? title = s.Title ?? (choice.Template is SectionTemplate.ShortcutFill or SectionTemplate.RecentsData or SectionTemplate.DaylistHero ? null : titles.MoreForYou);
        int total = Math.Max(s.TotalCount, cards.Count);
        return choice.Zone switch
        {
            ZoneKind.ClusterCards => new Zone(ZoneKind.ClusterCards, key, title, s.Subtitle, cards,
                Clusters: ClusterFold.Fold([s], titles), SectionSlot: s.Slot, TotalCount: total),
            ZoneKind.Daylist => new Zone(ZoneKind.Daylist, key, s.Title, s.Subtitle, cards, cards.Count > 0 ? cards[0] : null,
                SectionSlot: s.Slot, TotalCount: total),
            _ => new Zone(choice.Zone, key, title, s.Subtitle, cards, SectionSlot: s.Slot, TotalCount: total) { Header = HeaderEntity.Of(s) },
        };
    }

    /// <summary>The planner dump for one answer: every section with its identity, counts, template and reason, in server
    /// order. Nothing is dropped as unknown; a section with no drawable card says so (Template None).</summary>
    public static IReadOnlyList<SectionPlan> Plan(IReadOnlyList<SectionInput> answer, long nowMs)
    {
        var plan = new SectionPlan[answer.Count];
        for (int i = 0; i < answer.Count; i++)
        {
            var s = answer[i];
            var r = SectionIdentity.Of(s);
            var choice = Choose(s, nowMs);
            int usable = 0;
            for (int k = 0; k < s.Cards.Count; k++) if (SectionRoles.IsUsable(s.Cards[k])) usable++;
            var row = new Section(s.Slot);
            bool live = row.IsValid;
            plan[i] = new SectionPlan(i, r.Key, r.Fp, r.Family, s.TypeName.Length > 0 ? s.TypeName : TypeNameOf(s.Kind), s.Cards.Count, usable,
                live ? row.Unsupported : 0, live ? row.Duplicates : 0, s.TotalCount, choice.Template, choice.Zone, choice.Reason);
        }
        return plan;
    }

    /// <summary>A format the server generates per listener (mixes, stations, the daylist, Discover Weekly, Release Radar).</summary>
    static bool Generated(string? f) => f is "daily-mix" or "discover-weekly" or "release-radar" or "daylist" or "topic-mix"
        or "descripto" or "inspiredby-mix" or "artist-mix-reader" or "";

    /// <summary>The typename a band folded without one (an older fold, a fixture) stands for.</summary>
    static string TypeNameOf(SectionKind kind) => kind switch
    {
        SectionKind.HomeShorts => Shorts,
        SectionKind.HomeRecentlyPlayed => Recents,
        SectionKind.HomeBaseline => Baseline,
        SectionKind.HomeSpotlight => Spotlight,
        _ => Generic,
    };
}
