// ── Home/Board/SectionChooser.cs: one Spotify section in, one template out (pinned-home design 2.3, spec 3.1; Home rhythm A) ─────
//
// Reads structure only: the wire typename, the histogram of card kinds, formats, counts, the header entity, which fields
// are present (header images, video, preview tracks), the station seed in a station's own art path. Never a title. The
// table, first match wins (Home rhythm A, owner 2026-11-16: "the issue is not shorter page its mono all same stuff"):
//
//   HomeShortsSectionData                         → ShortcutFill   (feeds the Pinned group's fill; takes no room)
//   HomeRecentlyPlayedSectionData                 → RecentsData    (the Recently played widget's data)
//   no drawable card                              → None           (omitted, named in the plan with its typename)
//   a daylist playlist in the lead                → DaylistHero
//   HomeFeedBaselineSectionData                   → by its card: an untitled album = ReleaseList, an episode = VideoTiles
//                                                   or EpisodeRows, a book = BookShelf, a show = ShowGrid, else SpineShelf
//                                                   (a run of these is ONE shelf: HomeProjection folds it)
//   mostly episodes                               → VideoTiles (video with a thumbnail) or EpisodeRows (each the other's alternate,
//                                                   the baseline's one episode too: a run of episodes alternates a card and a row)
//   mostly audiobooks                             → BookShelf
//   mostly shows                                  → CappedShelf    (alternate: ShowGrid, the paged shelf)
//   half or more colour-plate mixes (descripto)   → MoodPlates     (alternate: CompactGrid)
//   half or more personal sets (daily-mix, discover-weekly, release-radar), three or more cards
//                                                 → LeadGrid       (alternates: CompactGrid, CoverShelf)
//   half or more generated mixes (topic-mix, artist-mix-reader)
//                                                 → CompactGrid    (alternates: CappedShelf, CoverShelf)
//   half or more stations (inspiredby-mix)        → ArtistCircles when every station is seeded by an artist, else CompactGrid
//                                                   (each the other's alternate)
//   half or more artist sets (artistsets), or four in five cards artists
//                                                 → ArtistCircles  (alternates: CappedShelf, CoverShelf)
//   two thirds or more carry an editorial header  → WideTiles      (alternate: CappedShelf)
//   half or more editorial playlists              → WideTiles when half carry a header, else CappedShelf
//   albums in the release window                  → ReleaseList
//   six or more cards, no kind three in four      → CappedShelf    (a long mixed list; alternates: CompactGrid, CoverShelf)
//   a typename this build knows                   → EntityShelf (with a header entity) or CoverShelf (alternates: CompactGrid, CappedShelf)
//   ANYTHING ELSE, an unknown typename included   → PlainShelf, a cover shelf of whatever cards decode
//
// The alternates are what SectionRhythm takes when a neighbour already draws the primary: never two neighbours alike.
// Counts are the theme's form, not a template: one card is a single wide card, two or three a short row, four or more a
// shelf. Pure.

using System.Collections.Immutable;
using Wavee.HomeUi;

namespace Wavee;

public readonly record struct SectionChoice(SectionTemplate Template, ZoneKind Zone, string Reason)
{
    /// <summary>What the section may draw as instead when a neighbour already draws <see cref="Template"/>, best first (SectionRhythm).</summary>
    public ImmutableArray<SectionTemplate> Alternates { get; init; } = ImmutableArray<SectionTemplate>.Empty;
}

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
        int personal = 0, mixes = 0, stations = 0, artistSeeded = 0, sets = 0, editorial = 0, editorialHeaders = 0;
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
            switch (c.Format)
            {
                case "daily-mix" or "discover-weekly" or "release-radar": personal++; break;
                case "topic-mix" or "artist-mix-reader": mixes++; break;
                case "inspiredby-mix": stations++; if (StationSeed.IsArtist(c.ImageUrl)) artistSeeded++; break;
                case "artistsets": sets++; break;
                case "editorial" or "format-shows-shuffle": editorial++; if (c.HeaderImageUrl is not null) editorialHeaders++; break;
            }
            // Every generated mix and station carries a desktop header image now (1.3.8.620 capture), and so does every
            // "This Is" set (format artistsets, live answer 2026-11-16), so a header counts toward wide tiles only on an
            // editorial card: a shelf of mixes, stations or artist sets stays a cover shelf at the standard card size.
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
                    ? Pick(SectionTemplate.VideoTiles, "baseline: a video episode", SectionTemplate.EpisodeRows)
                    : Pick(SectionTemplate.EpisodeRows, "baseline: an episode", SectionTemplate.VideoTiles),
                HomeCardKind.Audiobook => new(SectionTemplate.BookShelf, ZoneKind.BookShelf, "baseline: a book"),
                HomeCardKind.Podcast => new(SectionTemplate.ShowGrid, ZoneKind.ShowGrid, "baseline: a show"),
                _ => new(SectionTemplate.SpineShelf, ZoneKind.SpineShelf,
                    s.Preview.Count > 0 ? "baseline: a card with preview songs" : "baseline: a card (preview songs not landed)"),
            };
        }

        if (episodes * 2 > usable)
            return video * 2 >= episodes
                ? Pick(SectionTemplate.VideoTiles, $"episodes, {video} of {episodes} with video", SectionTemplate.EpisodeRows)
                : Pick(SectionTemplate.EpisodeRows, $"episodes, {video} of {episodes} with video", SectionTemplate.VideoTiles);
        if (books * 2 > usable) return new(SectionTemplate.BookShelf, ZoneKind.BookShelf, $"audiobooks ({books} of {usable})");
        if (shows * 2 > usable) return Pick(SectionTemplate.CappedShelf, $"shows ({shows} of {usable})", SectionTemplate.ShowGrid);
        if (plates * 2 >= usable) return Pick(SectionTemplate.MoodPlates, $"colour-plate mixes ({plates} of {usable})", SectionTemplate.CompactGrid);
        if (usable >= 3 && personal * 2 >= usable)
            return Pick(SectionTemplate.LeadGrid, $"personal sets ({personal} of {usable})", SectionTemplate.CompactGrid, SectionTemplate.CoverShelf);
        if (mixes * 2 >= usable)
            return Pick(SectionTemplate.CompactGrid, $"generated mixes ({mixes} of {usable})", SectionTemplate.CappedShelf, SectionTemplate.CoverShelf);
        if (stations * 2 >= usable)
            return artistSeeded == stations
                ? Pick(SectionTemplate.ArtistCircles, $"artist stations ({stations} of {usable})", SectionTemplate.CompactGrid, SectionTemplate.CappedShelf)
                : Pick(SectionTemplate.CompactGrid, $"stations ({stations} of {usable}, {artistSeeded} seeded by an artist)", SectionTemplate.ArtistCircles, SectionTemplate.CappedShelf);
        if (sets * 2 >= usable || artists * 5 >= usable * 4)
            return Pick(SectionTemplate.ArtistCircles, $"artist sets ({sets} sets, {artists} artists of {usable})", SectionTemplate.CappedShelf, SectionTemplate.CoverShelf);
        if (usable >= 2 && headers * 3 >= usable * 2) return Pick(SectionTemplate.WideTiles, $"header images ({headers} of {usable})", SectionTemplate.CappedShelf);
        if (editorial * 2 >= usable)
            return usable >= 2 && editorialHeaders * 2 >= usable
                ? Pick(SectionTemplate.WideTiles, $"editorial playlists ({editorial} of {usable}, {editorialHeaders} with a header)", SectionTemplate.CappedShelf, SectionTemplate.CoverShelf)
                : Pick(SectionTemplate.CappedShelf, $"editorial playlists ({editorial} of {usable}, {editorialHeaders} with a header)", SectionTemplate.CoverShelf, SectionTemplate.CompactGrid);
        if (ReleaseDetect.IsReleases(s, nowMs)) return new(SectionTemplate.ReleaseList, ZoneKind.ReleaseList, "albums in the release window");

        int top = Math.Max(Math.Max(playlists, albums), Math.Max(Math.Max(artists, episodes), Math.Max(books, shows)));
        if (usable >= MixedListMin && top * 4 < usable * 3)
            return Pick(SectionTemplate.CappedShelf, $"a long mixed list ({top} of {usable} one kind)", SectionTemplate.CompactGrid, SectionTemplate.CoverShelf);

        string counted = usable >= 4 ? "a shelf" : usable == 1 ? "a single card" : "a short row";
        if (type is Generic or Spotlight)
            return s.HeaderUri is not null
                ? new(SectionTemplate.EntityShelf, ZoneKind.CoverShelf, counted + " with a header entity") { Alternates = s_shelfAlternates }
                : new(SectionTemplate.CoverShelf, ZoneKind.CoverShelf, counted) { Alternates = s_shelfAlternates };
        return new(SectionTemplate.PlainShelf, ZoneKind.CoverShelf, "unknown typename " + type + ": " + counted) { Alternates = s_shelfAlternates };
    }

    /// <summary>A list of at least this many cards with no kind three in four is a long mixed list.</summary>
    const int MixedListMin = 6;

    static readonly ImmutableArray<SectionTemplate> s_shelfAlternates = [SectionTemplate.CompactGrid, SectionTemplate.CappedShelf];

    static SectionChoice Pick(SectionTemplate t, string reason, params SectionTemplate[] alternates)
        => new(t, KindOf(t), reason) { Alternates = [.. alternates] };

    /// <summary>The zone kind that draws template <paramref name="t"/> for a full section (the rhythm swaps a zone's kind with its template).</summary>
    public static ZoneKind KindOf(SectionTemplate t) => t switch
    {
        SectionTemplate.LeadGrid => ZoneKind.LeadGrid,
        SectionTemplate.CompactGrid => ZoneKind.CompactGrid,
        SectionTemplate.ArtistCircles => ZoneKind.ArtistCircles,
        SectionTemplate.CappedShelf => ZoneKind.CappedShelf,
        SectionTemplate.MoodPlates => ZoneKind.MoodPlates,
        SectionTemplate.WideTiles => ZoneKind.WideTiles,
        SectionTemplate.ShowGrid => ZoneKind.ShowGrid,
        SectionTemplate.BookShelf => ZoneKind.BookShelf,
        SectionTemplate.ReleaseList => ZoneKind.ReleaseList,
        SectionTemplate.VideoTiles => ZoneKind.VideoTiles,
        SectionTemplate.EpisodeRows => ZoneKind.EpisodeRows,
        SectionTemplate.SpineShelf => ZoneKind.SpineShelf,
        _ => ZoneKind.CoverShelf,
    };

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
            ZoneKind.SpineShelf => new Zone(ZoneKind.SpineShelf, key, title, s.Subtitle, cards,
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

    /// <summary>A format whose desktop header image is a generated design, not an editorial picture: what the server generates per
    /// listener (mixes, stations, the daylist, Discover Weekly, Release Radar) and the per-artist "This Is" sets (artistsets).</summary>
    static bool Generated(string? f) => f is "daily-mix" or "discover-weekly" or "release-radar" or "daylist" or "topic-mix"
        or "descripto" or "inspiredby-mix" or "artist-mix-reader" or "artistsets" or "";

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
