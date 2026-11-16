// ── Home/Board/HomeProjection.cs: document × Spotify answer → a flat list of board entries (design 2.4) ─────────────
//
// In order, per group: the group head, then every item. A placed SpotifySection item is matched to the answer (no
// match: a Hidden entry that keeps its place). The SpotifyFeed item expands to every section of the answer that is not
// placed and not hidden (by ref or by family) and has a drawable template, in Spotify's order, as FeedSection entries
// "feed:<key>". The Fill item expands to the shortcut section's cards that are not pinned (or removed from the fill), up
// to Max, as FillCell entries "fill:<pin id>". Adjacency folds join a run of two or more one-card baseline sections into
// one PreviewGrid entry; nothing is reordered. Two synthetic sections keep what All shows today: the Following release
// list (inside the planner's zones while the bridge runs) and the Charts block, last.
//
// THE BRIDGE (design 2.5): until the data lane's SectionChooser lands, the feed entries come from today's
// ZonePlanner.Plan zones, one entry per zone, each carrying its Zone (BoardEntry.Zone), keyed by the zone's key. The
// daylist and recents zones are the widgets' and take no room in the feed.

using System.Collections.Immutable;
using FluentGpu.Localization;
using Wavee.HomeUi;

namespace Wavee;

public static class HomeProjection
{
    public const string ChartsKey = "wavee:charts", FollowingReleasesKey = "wavee:following-releases";

    /// <summary>A section's identity. The data lane's SectionIdentity.Of replaces this provisional one (key = the uri,
    /// fp and family over the section kind and title) at its merge.</summary>
    public static Func<SectionInput, SectionRef> RefOf { get; set; } = ProvisionalRef;

    static SectionRef ProvisionalRef(SectionInput s)
    {
        string family = s.Kind + "|" + (s.Title ?? "");
        return new SectionRef(s.Uri, Fnv(family), family);
    }

    public static ulong Fnv(string text)
    {
        ulong h = 14695981039346656037UL;
        foreach (char c in text) { h ^= c; h *= 1099511628211UL; }
        return h;
    }

    /// <summary>The document with no answer: heads, widgets, pins and placed sections (Hidden until an answer lands).
    /// The store's stand-in when no board is mounted.</summary>
    public static BoardModel Bare(HomeDoc doc, HomeSourceRegistry sources)
        => Project(doc, [], null, null, sources, null, 0);

    /// <summary>The document over the All answer on hand right now (the tables, no fetch): the store's and the inspector's
    /// stand-in when no board is mounted. Not reactive: it peeks.</summary>
    public static BoardModel Live(HomeDoc doc, HomeSourceRegistry sources)
    {
        var answer = AnswerOnHand("");
        var following = AnswerOnHand("music-following-chip");
        return Project(doc, answer, following.Count > 0 ? following : null, null, sources, HomeZoneTitles.Localized(),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>A facet's sections as the tables hold them now (empty before the first answer).</summary>
    public static IReadOnlyList<SectionInput> AnswerOnHand(string facet)
    {
        var h = Entities.HomeFeed(facet);
        return h.IsValid && h.Knows(HomeFields.Sections) ? SectionReader.Read(h) : [];
    }

    public static BoardModel Project(HomeDoc doc, IReadOnlyList<SectionInput> answer,
        IReadOnlyList<SectionInput>? followingReleases, ChartTilesModel? charts,
        HomeSourceRegistry sources, ZoneTitles? titles, long nowMs)
    {
        // The feed, from the bridge: one entry per planned zone.
        var feed = new List<FeedCandidate>();
        if (answer.Count > 0 && titles is not null)
        {
            var bySlot = new Dictionary<int, SectionInput>();
            foreach (var s in answer) bySlot[s.Slot] = s;
            var zones = ZonePlanner.Plan(answer, "", titles, null, nowMs, followingReleases);
            foreach (var z in zones)
            {
                var template = TemplateOf(z.Kind);
                if (template is SectionTemplate.None or SectionTemplate.DaylistHero or SectionTemplate.RecentsData) continue;
                SectionRef r = z.SectionSlot >= 0 && bySlot.TryGetValue(z.SectionSlot, out var src) && src.Uri.Length > 0
                    ? RefOf(src) with { Key = z.Key }
                    : new SectionRef(z.Key, Fnv(z.Kind.ToString()), z.Kind.ToString());
                feed.Add(new FeedCandidate(r, z.Title ?? "", template, z.SectionSlot, z, false));   // the planner already folds its clusters
            }
            if (charts is not null)
                feed.Add(new FeedCandidate(new SectionRef(ChartsKey, Fnv(ChartsKey), ChartsKey), "", SectionTemplate.CoverShelf, -1, null, false));
        }

        // The shortcuts the fill draws from.
        var shortcuts = new List<PinSource>();
        foreach (var s in answer)
        {
            if (s.Kind != SectionKind.HomeShorts) continue;
            foreach (var c in s.Cards)
            {
                if (c.IsBlank || HomePinId.Canonical(c.Uri) is not { } pid) continue;
                shortcuts.Add(new PinSource(pid, c.Title, c.ImageUrl ?? ""));
            }
            break;
        }

        // What is placed: section keys matched, pin ids pinned.
        var placedKeys = new HashSet<string>(StringComparer.Ordinal);
        var pinned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in doc.Groups)
            foreach (var it in g.Items)
            {
                if (it.Source is SpotifySectionSource ss) placedKeys.Add(ss.Ref.Key);
                else if (it.Source is PinSource p) pinned.Add(p.PinId);
            }

        var feedByKey = new Dictionary<string, FeedCandidate>(StringComparer.Ordinal);
        foreach (var f in feed) feedByKey.TryAdd(f.Ref.Key, f);

        var entries = ImmutableArray.CreateBuilder<BoardEntry>();
        foreach (var g in doc.Groups)
        {
            string title = g.Title ?? (g.TitleKey is { Length: > 0 } tk ? Loc.Get(tk) : "");
            entries.Add(new BoardEntry(g.Id, BoardEntryKind.GroupHead, g.Id, null, null, HomeSize.Auto, SectionTemplate.None,
                title, false, false, false, false, false, -1));
            var run = new List<BoardEntry>();   // feed-like entries since the last placed non-section item: folds stay inside it
            foreach (var it in g.Items)
            {
                var def = sources.Of(it.Source);
                switch (it.Source)
                {
                    case SpotifyFeedSource:
                        foreach (var f in feed)
                        {
                            if (placedKeys.Contains(f.Ref.Key)) continue;
                            // A hidden section keeps an entry that takes no room: the catalogue lists it first, Show brings it back.
                            var fe = FeedEntry(HomeCommands.FeedPrefix + f.Ref.Key, BoardEntryKind.FeedSection, g.Id, def, f, HomeSize.Auto, it.Options);
                            run.Add(HomeCommands.IsHidden(doc, f.Ref) ? fe with { Hidden = true, Members = [] } : fe);
                        }
                        break;
                    case SpotifySectionSource ss:
                    {
                        if (feedByKey.TryGetValue(ss.Ref.Key, out var f))
                            run.Add(FeedEntry(it.Id, BoardEntryKind.Item, g.Id, def, f, it.Size, it.Options));
                        else
                            run.Add(new BoardEntry(it.Id, BoardEntryKind.Item, g.Id, def, ss, it.Size, SectionTemplate.CoverShelf,
                                ss.TitleHint, false, false, false, false, false, -1) { Hidden = true, Options = it.Options });
                        break;
                    }
                    case FillSource fill:
                        Flush(entries, run);
                        int n = 0;
                        foreach (var p in shortcuts)
                        {
                            if (n >= fill.Max) break;
                            if (pinned.Contains(p.PinId) || HomeCommands.IsHidden(doc, new SectionRef(p.PinId, 0, HomeCommands.FillFamily))) continue;
                            if (doc.Find(HomeCommands.FillPrefix + p.PinId).Group >= 0) continue;
                            entries.Add(new BoardEntry(HomeCommands.FillPrefix + p.PinId, BoardEntryKind.FillCell, g.Id, def, p, HomeSize.Auto,
                                SectionTemplate.ShortcutFill, p.NameHint, false, false, false, false, true, -1));
                            n++;
                        }
                        break;
                    default:
                    {
                        Flush(entries, run);
                        bool live = def?.Live == true;
                        bool paused = live && it.Options.TryGetValue("live", out var lv) && lv == "0";
                        string name = it.Source switch
                        {
                            PinSource p => p.NameHint,
                            _ when def is not null => Loc.Get(def.TitleKey),
                            _ => "",
                        };
                        entries.Add(new BoardEntry(it.Id, BoardEntryKind.Item, g.Id, def, it.Source, it.Size, SectionTemplate.None,
                            name, def?.HasOptions == true, live, paused, it.Source is PinSource, false, -1)
                        { Options = it.Options, Hidden = def is null });
                        break;
                    }
                }
            }
            Flush(entries, run);
        }
        return new BoardModel(entries.ToImmutable(), doc.Rev);
    }

    readonly record struct FeedCandidate(SectionRef Ref, string Title, SectionTemplate Template, int Slot, Zone? Zone, bool OneCardBaseline);

    static BoardEntry FeedEntry(string id, BoardEntryKind kind, string groupId, HomeSourceDef? def, in FeedCandidate f, HomeSize size,
        ImmutableSortedDictionary<string, string> options)
        => new(id, kind, groupId, def, new SpotifySectionSource(f.Ref, f.Title), size, f.Template, f.Title, false, false, false, false,
            kind == BoardEntryKind.FeedSection, f.Slot)
        { Zone = f.Zone, Options = options, Members = f.OneCardBaseline ? [f.Ref] : [] };

    /// <summary>Emit a run of section entries, folding two or more neighbouring one-card baseline sections (at their auto
    /// size) into one preview grid. Nothing is reordered.</summary>
    static void Flush(ImmutableArray<BoardEntry>.Builder into, List<BoardEntry> run)
    {
        int i = 0;
        while (i < run.Count)
        {
            int j = i;
            while (j < run.Count && Foldable(run[j])) j++;
            if (j - i >= 2)
            {
                var first = run[i];
                var members = ImmutableArray.CreateBuilder<SectionRef>(j - i);
                for (int k = i; k < j; k++) members.Add(((SpotifySectionSource)run[k].Raw!).Ref);
                into.Add(first with
                {
                    Id = HomeCommands.FoldPrefix + members[0].Key, Kind = BoardEntryKind.Fold, Template = SectionTemplate.PreviewGrid,
                    OnlyHide = true, Members = members.ToImmutable(),
                });
                i = j;
                continue;
            }
            for (int k = i; k < Math.Max(j, i + 1); k++) into.Add(run[k] with { Members = [] });
            i = Math.Max(j, i + 1);
        }
        run.Clear();
    }

    static bool Foldable(BoardEntry e) => !e.Hidden && e.Size == HomeSize.Auto && !e.Members.IsDefaultOrEmpty;

    /// <summary>The bridge's template for a planner zone.</summary>
    public static SectionTemplate TemplateOf(ZoneKind k) => k switch
    {
        ZoneKind.Daylist => SectionTemplate.DaylistHero,
        ZoneKind.RecentGrid => SectionTemplate.RecentsData,
        ZoneKind.CoverShelf or ZoneKind.MixedCovers or ZoneKind.RadioShelf => SectionTemplate.CoverShelf,
        ZoneKind.WideTiles => SectionTemplate.WideTiles,
        ZoneKind.ReleaseList => SectionTemplate.ReleaseList,
        ZoneKind.ClusterCards or ZoneKind.PodcastGroups => SectionTemplate.PreviewGrid,
        ZoneKind.BrowseTiles => SectionTemplate.PlainShelf,
        ZoneKind.ShowGrid => SectionTemplate.ShowGrid,
        ZoneKind.VideoTiles => SectionTemplate.VideoTiles,
        ZoneKind.EpisodeRows or ZoneKind.EpisodeLead or ZoneKind.ContinueEpisodes => SectionTemplate.EpisodeRows,
        _ => SectionTemplate.None,
    };
}

/// <summary>The planner's chapter titles, localized: the board host and the inspector build the projection's titles here.</summary>
public static class HomeZoneTitles
{
    public static ZoneTitles Localized() => new(
        MadeForYou: Loc.Get(Strings.Home.MadeForYou),
        BecauseYouLike: Loc.Get(Strings.Home.Zone.BecauseYouLike),
        MoreForYou: Loc.Get(Strings.Home.Zone.MoreForYou),
        RadioAndMixes: Loc.Get(Strings.Home.Zone.RadioAndMixes),
        Browse: Loc.Get(Strings.Home.Zone.Browse),
        JumpBackIn: Loc.Get(Strings.Home.JumpBackIn),
        RecentlyPlayed: Loc.Get(Strings.Sidebar.Section.RecentlyPlayed),
        NewEpisodes: Loc.Get(Strings.Home.Zone.NewEpisodes),
        ContinueListening: Loc.Get(Strings.Home.Zone.ContinueListening),
        VideosYouMightLike: Loc.Get(Strings.Home.Zone.VideosYouMightLike),
        EpisodesYouMightLike: Loc.Get(Strings.Home.Zone.EpisodesYouMightLike),
        BecauseYouListenTo: Loc.Get(Strings.Home.Zone.BecauseYouListenTo),
        FromArtistsYouFollow: Loc.Get(Strings.Home.Zone.FromArtistsYouFollow),
        YourShows: Loc.Get(Strings.Home.Zone.YourShows),
        ShowsYouMightLike: Loc.Get(Strings.Home.Zone.ShowsYouMightLike));
}
