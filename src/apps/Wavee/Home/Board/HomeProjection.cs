// ── Home/Board/HomeProjection.cs: document × Spotify answer → a flat list of board entries (design 2.4) ─────────────
//
// In order, per group: the group head, then every item. A placed SpotifySection item is matched to the answer (no
// match: a Hidden entry that keeps its place). The SpotifyFeed item expands to every section of the answer that is not
// placed and not hidden (by ref or by family) and has a drawable template, in Spotify's order, as FeedSection entries
// "feed:<key>". The Fill item expands to the shortcut section's cards that are not pinned (or removed from the fill), up
// to Max, as FillCell entries "fill:<pin id>". Adjacency folds join a run of two or more one-card baseline sections into
// one PreviewGrid entry; nothing is reordered. Two synthetic sections keep what All shows today: the Following release
// list (wavee:following-releases, after the editorial shelf) and the Charts block (wavee:charts), last.
//
// Each feed entry carries its Zone (BoardEntry.Zone): SectionChooser.ToZone, keyed by the section uri, so a section tile
// mounts today's Home.Zone.* template for it. The daylist, the recently played section and the shortcuts are the widgets'
// and the fill's, and take no room in the feed.

using System.Collections.Immutable;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.HomeUi;

namespace Wavee;

public static class HomeProjection
{
    public const string ChartsKey = "wavee:charts", FollowingReleasesKey = "wavee:following-releases";

    /// <summary>A section's identity (SectionIdentity.Of: key, fingerprint, family).</summary>
    public static Func<SectionInput, SectionRef> RefOf { get; set; } = SectionIdentity.Of;

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

    /// <summary>How many placeholder sections the feed slot holds while its answer is still coming (a failed or offline answer is one).</summary>
    public const int FeedPlaceholders = 4;

    /// <summary>The inspector's override of the feed's state (`state kind=loading|error|offline` on the Home route); null: the tables decide.</summary>
    public static readonly Signal<HomeFeedState?> FeedOverride = new(null);

    /// <summary>The feed's state for the All answer <paramref name="h"/> (a subscribing read of the override and the session phase): on hand,
    /// still coming, failed, or offline. The board host passes it to <see cref="Project"/>.</summary>
    public static HomeFeedState FeedStateOf(Home h)
    {
        if (FeedOverride.Value is { } forced) return forced;
        if (h.IsValid && h.Knows(HomeFields.Sections)) return HomeFeedState.Ready;
        var phase = Spotify.Status.Value;
        if (!h.IsValid || !h.LiveAttemptConcluded) return phase is >= Spotify.SessionPhase.Offline and <= Spotify.SessionPhase.Minting ? HomeFeedState.Offline : HomeFeedState.Loading;
        return phase == Spotify.SessionPhase.Online ? HomeFeedState.Error : HomeFeedState.Offline;
    }

    public static BoardModel Project(HomeDoc doc, IReadOnlyList<SectionInput> answer,
        IReadOnlyList<SectionInput>? followingReleases, ChartTilesModel? charts,
        HomeSourceRegistry sources, ZoneTitles? titles, long nowMs, HomeFeedState feedState = HomeFeedState.Ready)
    {
        // An answer that is not on hand (or that the inspector says failed) draws nothing of it: the feed slot holds placeholders.
        if (feedState != HomeFeedState.Ready) { answer = []; followingReleases = null; }
        // The feed: one entry per section the chooser gives a drawable template, in Spotify's order (design 2.3, 2.4). The
        // shortcuts feed the fill, the recently played section the recents widget, the daylist the daylist widget: no room.
        var feed = new List<FeedCandidate>();
        if (answer.Count > 0 && titles is not null)
        {
            int wideAt = -1;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in answer)
            {
                // A section's identity is its key: an answer that repeats a uri (the synthetic fixture does, by design) shows it once, where it
                // first appears; the repeat is dropped (two tiles of one identity are one entry twice).
                if (s.Uri.Length > 0 && !keys.Add(s.Uri)) continue;
                var choice = SectionChooser.Choose(s, nowMs);
                if (choice.Template is SectionTemplate.None or SectionTemplate.ShortcutFill or SectionTemplate.RecentsData or SectionTemplate.DaylistHero) continue;
                var zone = Drawable(SectionChooser.ToZone(s, choice, titles));
                bool oneCardBaseline = choice.Template == SectionTemplate.PreviewCard && zone.Items.Count == 1;   // folds whether or not its preview landed
                if (choice.Template == SectionTemplate.WideTiles && wideAt < 0) wideAt = feed.Count;
                // A preview card whose preview has not landed draws as a shelf (Drawable): its box is the shelf's.
                var template = choice.Template == SectionTemplate.PreviewCard && zone.Kind == ZoneKind.CoverShelf ? SectionTemplate.CoverShelf : choice.Template;
                feed.Add(new FeedCandidate(RefOf(s), zone.Title ?? "", template, s.Slot, zone, oneCardBaseline, s));
            }
            // Synthetic: the Following release list (the music-following-chip answer), at its place after the editorial shelf.
            if (FollowingReleases(followingReleases, titles) is { } releases)
                feed.Insert(wideAt >= 0 ? wideAt + 1 : Math.Min(1, feed.Count),
                    new FeedCandidate(new SectionRef(FollowingReleasesKey, Fnv(FollowingReleasesKey), FollowingReleasesKey), releases.Title ?? "",
                        SectionTemplate.ReleaseList, releases.SectionSlot, releases, false, null));
            // Synthetic: the Charts block, last.
            if (charts is not null)
                feed.Add(new FeedCandidate(new SectionRef(ChartsKey, Fnv(ChartsKey), ChartsKey), "", SectionTemplate.CoverShelf, -1, null, false, null));
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
        t_inputs ??= new Dictionary<string, SectionInput>(StringComparer.Ordinal);
        t_inputs.Clear();
        foreach (var f in feed) if (f.Input is { } input) t_inputs.TryAdd(f.Ref.Key, input);

        var entries = ImmutableArray.CreateBuilder<BoardEntry>();
        int fillCursor = 0;   // the shortcuts are dealt in order across every Fill item: a fill split around a pin continues where the first part stopped
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
                    case SpotifyFeedSource when feedState != HomeFeedState.Ready:
                        // Loading: shelves at their boxes; failed or offline: one line that says so (not one per missing shelf).
                        for (int k = 0, places = feedState == HomeFeedState.Loading ? FeedPlaceholders : 1; k < places; k++)
                            run.Add(new BoardEntry("placeholder:" + k.ToString(System.Globalization.CultureInfo.InvariantCulture), BoardEntryKind.Placeholder,
                                g.Id, def, null, HomeSize.Full, SectionTemplate.CoverShelf, "", false, false, false, false, false, -1) { Feed = feedState });
                        break;
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
                        Flush(entries, run, titles);
                        int n = 0, max = HomeShortcuts.ClosedByHistory(g) ? Math.Max(0, fill.Max - 1) : fill.Max;
                        while (n < max && fillCursor < shortcuts.Count)
                        {
                            var p = shortcuts[fillCursor++];
                            if (pinned.Contains(p.PinId) || HomeCommands.IsHidden(doc, new SectionRef(p.PinId, 0, HomeCommands.FillFamily))) continue;
                            if (doc.Find(HomeCommands.FillPrefix + p.PinId).Group >= 0) continue;
                            entries.Add(new BoardEntry(HomeCommands.FillPrefix + p.PinId, BoardEntryKind.FillCell, g.Id, def, p, HomeSize.Auto,
                                SectionTemplate.ShortcutFill, p.NameHint, false, false, false, false, true, -1) { Parent = it.Id });
                            n++;
                        }
                        break;
                    default:
                    {
                        Flush(entries, run, titles);
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
            Flush(entries, run, titles);
        }
        return new BoardModel(entries.ToImmutable(), doc.Rev);
    }

    readonly record struct FeedCandidate(SectionRef Ref, string Title, SectionTemplate Template, int Slot, Zone? Zone, bool OneCardBaseline,
        SectionInput? Input);

    /// <summary>The zones the three data widgets draw, from the All answer by the chooser (the sections they take room from in the feed):
    /// the daylist (the section a daylist leads, its lead card), Recently played (the recently played section, capped at the planner's RecentsCap) and New releases (the release-window sections' albums, then the Following answer's, deduped).</summary>
    public static (Zone? Daylist, Zone? Recents, Zone? Releases) WidgetZones(IReadOnlyList<SectionInput> answer,
        IReadOnlyList<SectionInput>? followingReleases, ZoneTitles titles, long nowMs)
    {
        Zone? daylist = null, recents = null;
        // The daylist card wherever the answer put it (DaylistSource: a spotlight band's, else the first daylist card in any shelf).
        if (DaylistSource.Find(answer) is { } found)
        {
            SectionInput? from = null;
            foreach (var s in answer) if (s.Slot == found.SectionSlot) { from = s; break; }
            daylist = new Zone(ZoneKind.Daylist, "home:daylist", from?.Title, from?.Subtitle, [found.Card], found.Card,
                SectionSlot: found.SectionSlot, TotalCount: from?.TotalCount ?? 0);
        }
        var releaseCards = new List<HomeCard>();
        var seen = new HashSet<long>();
        int releaseSlot = -1;
        foreach (var s in answer)
        {
            var choice = SectionChooser.Choose(s, nowMs);
            switch (choice.Template)
            {
                case SectionTemplate.DaylistHero when daylist is null:
                    daylist = SectionChooser.ToZone(s, choice, titles);
                    break;
                case SectionTemplate.RecentsData when recents is null:
                {
                    // As the planner had it: up to RecentsCap cards, the daylist's card left to the daylist.
                    var cards = new List<HomeCard>(ZonePlanner.RecentsCap);
                    long daylistKey = daylist?.Lead is { } lead ? lead.DedupeKey : long.MinValue;
                    foreach (var c in s.Cards)
                        if (SectionRoles.IsUsable(c) && c.DedupeKey != daylistKey && cards.Count < ZonePlanner.RecentsCap) cards.Add(c);
                    if (cards.Count > 0)
                        recents = new Zone(ZoneKind.RecentGrid, "home:recents", titles.RecentlyPlayed, s.Subtitle, cards, SectionSlot: s.Slot, TotalCount: s.TotalCount);
                    break;
                }
                case SectionTemplate.ReleaseList:
                    if (releaseSlot < 0) releaseSlot = s.Slot;
                    foreach (var c in s.Cards) if (SectionRoles.IsUsable(c) && seen.Add(c.DedupeKey)) releaseCards.Add(c);
                    break;
            }
        }
        if (followingReleases is { Count: > 0 })
        {
            if (releaseSlot < 0) releaseSlot = followingReleases[0].Slot;
            foreach (var s in followingReleases)
                foreach (var c in s.Cards)
                    if (SectionRoles.IsUsable(c) && c.Kind == HomeCardKind.Album && seen.Add(c.DedupeKey)) releaseCards.Add(c);
        }
        Zone? releases = releaseCards.Count > 0
            ? new Zone(ZoneKind.ReleaseList, "home:releases", titles.FromArtistsYouFollow, null, releaseCards, SectionSlot: releaseSlot, TotalCount: releaseCards.Count)
            : null;
        return (daylist, recents, releases);
    }

    /// <summary>The Following release list: every album card of the music-following-chip answer, deduped, as one release list.</summary>
    static Zone? FollowingReleases(IReadOnlyList<SectionInput>? following, ZoneTitles titles)
    {
        if (following is not { Count: > 0 }) return null;
        var seen = new HashSet<long>();
        var cards = new List<HomeCard>();
        foreach (var s in following)
            foreach (var c in s.Cards)
                if (SectionRoles.IsUsable(c) && c.Kind == HomeCardKind.Album && seen.Add(c.DedupeKey)) cards.Add(c);
        if (cards.Count == 0) return null;
        return new Zone(ZoneKind.ReleaseList, "home:section:" + FollowingReleasesKey, titles.FromArtistsYouFollow, null, cards,
            SectionSlot: following[0].Slot, TotalCount: cards.Count);
    }

    /// <summary>The section inputs behind the feed entries (by section key): a fold builds its one cluster zone from them.</summary>
    [ThreadStatic] static Dictionary<string, SectionInput>? t_inputs;

    static BoardEntry FeedEntry(string id, BoardEntryKind kind, string groupId, HomeSourceDef? def, in FeedCandidate f, HomeSize size,
        ImmutableSortedDictionary<string, string> options)
        => new(id, kind, groupId, def, new SpotifySectionSource(f.Ref, f.Title), size, f.Template, f.Title, false, false, false, false,
            kind == BoardEntryKind.FeedSection, f.Slot)
        { Zone = f.Zone, Options = options, Members = f.OneCardBaseline ? [f.Ref] : [] };

    /// <summary>Emit a run of section entries, folding two or more neighbouring one-card baseline sections (at their auto
    /// size) into one preview grid. Nothing is reordered.</summary>
    static void Flush(ImmutableArray<BoardEntry>.Builder into, List<BoardEntry> run, ZoneTitles? titles)
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
                var inputs = new List<SectionInput>(j - i);
                for (int k = i; k < j; k++)
                {
                    var r = ((SpotifySectionSource)run[k].Raw!).Ref;
                    members.Add(r);
                    if (t_inputs is not null && t_inputs.TryGetValue(r.Key, out var input)) inputs.Add(input);
                }
                Zone? zone = titles is not null && inputs.Count == j - i ? FoldZone(members[0].Key, inputs, titles) : first.Zone;
                into.Add(first with
                {
                    Id = HomeCommands.FoldPrefix + members[0].Key, Kind = BoardEntryKind.Fold,
                    Template = zone?.Kind == ZoneKind.CoverShelf ? SectionTemplate.CoverShelf : SectionTemplate.PreviewGrid,
                    OnlyHide = true, Members = members.ToImmutable(), Zone = zone, Title = zone?.Title ?? first.Title,
                });
                i = j;
                continue;
            }
            for (int k = i; k < Math.Max(j, i + 1); k++) into.Add(run[k] with { Members = [] });
            i = Math.Max(j, i + 1);
        }
        run.Clear();
    }

    /// <summary>The fold's one zone: the members' clusters ("Because you like"), each its card and its preview tracks. Before any member's
    /// preview has landed there is no cluster to draw: the members' cards stand as one shelf under the same title (never a heading over
    /// nothing), and the clusters replace it when the previews land.</summary>
    static Zone FoldZone(string firstKey, List<SectionInput> inputs, ZoneTitles titles)
    {
        var clusters = ClusterFold.Fold(inputs, titles);
        if (clusters.Count > 0)
            return new Zone(ZoneKind.ClusterCards, "home:fold:" + firstKey, titles.BecauseYouLike, null, [], Clusters: clusters, SectionSlot: inputs[0].Slot);
        var cards = new List<HomeCard>(inputs.Count);
        var seen = new HashSet<long>();
        foreach (var s in inputs)
            foreach (var c in s.Cards)
                if (SectionRoles.IsUsable(c) && seen.Add(c.DedupeKey)) { cards.Add(c); break; }
        return new Zone(ZoneKind.CoverShelf, "home:fold:" + firstKey, titles.BecauseYouLike, null, cards, SectionSlot: inputs[0].Slot, TotalCount: cards.Count);
    }

    /// <summary>A cluster zone with no cluster to draw (its preview tracks have not landed) draws its cards as a shelf instead.</summary>
    static Zone Drawable(Zone z)
        => z.Kind == ZoneKind.ClusterCards && z.Clusters is not { Count: > 0 } && z.Items.Count > 0
            ? z with { Kind = ZoneKind.CoverShelf, Clusters = null }
            : z;

    static bool Foldable(BoardEntry e) => !e.Hidden && e.Size == HomeSize.Auto && !e.Members.IsDefaultOrEmpty;
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
