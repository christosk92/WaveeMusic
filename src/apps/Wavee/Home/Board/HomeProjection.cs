// ── Home/Board/HomeProjection.cs: document × Spotify answer → a flat list of board entries (design 2.4) ─────────────
//
// In order, per group: the group head, then every item. A placed SpotifySection item is matched to the answer (no
// match: a Hidden entry that keeps its place). The SpotifyFeed item expands to every section of the answer that is not
// placed and not hidden (by ref or by family) and has a drawable template, in Spotify's order, as FeedSection entries
// "feed:<key>". The Fill item expands to the shortcut section's cards that are not pinned (or removed from the fill), up
// to Max, as FillCell entries "fill:<pin id>". The fold gathers a run's one-card baseline sections (two or more, wherever they
// stand in it) into ONE Spine Shelf at the first one's place, then the rhythm pass (SectionRhythm) moves a section whose neighbour draws its template to an
// alternate; nothing else is reordered. Two synthetic sections keep what All shows today: the Following release
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

    /// <summary>The blank cards of a placeholder shelf: more than any theme's shelf shows in one row.</summary>
    const int PlaceholderCards = 12;

    /// <summary>The id part of a shortcut fill cell that holds a place while the answer naming the shortcuts is on its way.</summary>
    public const string PendingFill = "pending:";

    /// <summary>The inspector's override of the feed's state (`state kind=loading|error|offline` on the Home route); null: the tables decide.</summary>
    public static readonly Signal<HomeFeedState?> FeedOverride = new(null);

    /// <summary>The feed's state for the All answer <paramref name="h"/> (a subscribing read of the override and the session phase): on hand,
    /// still coming, failed, or offline. The board host passes it to <see cref="Project"/>.</summary>
    public static HomeFeedState FeedStateOf(Home h)
    {
        if (FeedOverride.Value is { } forced) return forced;
        if (h.IsValid && h.Knows(HomeFields.Sections)) return HomeFeedState.Ready;
        var phase = Spotify.Status.Value;
        // Offline is a session that could not connect (Failed). The resting phase a launch begins in (Offline, before the login starts) and the
        // sign-in phases are the answer still coming: reading them as offline drew every placeholder as the offline line first and the loaded
        // size after, two re-packs on each launch (a live open, 2026-11-17).
        if (phase == Spotify.SessionPhase.Failed) return HomeFeedState.Offline;
        if (!h.IsValid || !h.LiveAttemptConcluded) return HomeFeedState.Loading;
        return phase == Spotify.SessionPhase.Online ? HomeFeedState.Error : HomeFeedState.Loading;
    }

    public static BoardModel Project(HomeDoc doc, IReadOnlyList<SectionInput> answer,
        IReadOnlyList<SectionInput>? followingReleases, ChartTilesModel? charts,
        HomeSourceRegistry sources, ZoneTitles? titles, long nowMs, HomeFeedState feedState = HomeFeedState.Ready, bool followingLoading = false)
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
                bool oneCardBaseline = choice.Template == SectionTemplate.SpineShelf && zone.Items.Count == 1;   // folds whether or not its preview landed
                if (choice.Template == SectionTemplate.WideTiles && wideAt < 0) wideAt = feed.Count;
                // A shelf stands before its preview songs land (its tiles are flat placeholders at their size); only a card the
                // fold cannot draw at all falls back to a cover shelf (Drawable).
                var template = choice.Template == SectionTemplate.SpineShelf && zone.Kind == ZoneKind.CoverShelf ? SectionTemplate.CoverShelf : choice.Template;
                feed.Add(new FeedCandidate(RefOf(s), zone.Title ?? "", template, s.Slot, zone, oneCardBaseline, s, choice.Alternates));
            }
            // Synthetic: the Following release list (the music-following-chip answer), at its place after the editorial shelf. Not while
            // the board holds the New releases widget: that IS the Following list (HomeBoardZones), and one list drawn twice is one verb
            // in two homes (a live open, 2026-11-17, once the Following answer was asked). A placed entry of it then takes no room.
            if (!HasWidget(doc, "releases") && FollowingReleases(followingReleases, titles) is { } releases)
                feed.Insert(wideAt >= 0 ? wideAt + 1 : Math.Min(1, feed.Count),
                    new FeedCandidate(new SectionRef(FollowingReleasesKey, Fnv(FollowingReleasesKey), FollowingReleasesKey), releases.Title ?? "",
                        SectionTemplate.ReleaseList, releases.SectionSlot, releases, false, null, []));
            // Its answer still on the way after All's landed, and the list drew last time: its skeleton at its place, at the shape it drew (it lands
            // there; never a list that appears and pushes the feed down).
            else if (!HasWidget(doc, "releases") && followingLoading && ShapeOf(doc, FollowingReleasesKey) is { } fr)
                feed.Insert(wideAt >= 0 ? wideAt + 1 : Math.Min(1, feed.Count),
                    new FeedCandidate(new SectionRef(FollowingReleasesKey, Fnv(FollowingReleasesKey), FollowingReleasesKey), titles.FromArtistsYouFollow,
                        SectionTemplate.ReleaseList, -1, SeedZone(SectionTemplate.ReleaseList, fr.Count, "home:section:" + FollowingReleasesKey, titles.FromArtistsYouFollow),
                        false, null, []) { Seed = true });
            // Synthetic: the Charts block, last.
            if (charts is not null)
                feed.Add(new FeedCandidate(new SectionRef(ChartsKey, Fnv(ChartsKey), ChartsKey), "", SectionTemplate.CoverShelf, -1, null, false, null, []));
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
                    case SpotifyFeedSource when feedState == HomeFeedState.Loading && it.Shape.Length > 0:
                        // Loading, the feed's shape known from the last answer: each section it drew, at its place, as a flat skeleton of its template
                        // at its loaded size (the entry's id is the loaded one: the answer landing swaps each skeleton for its section where it stands).
                        foreach (var sh in it.Shape)
                            if (!placedKeys.Contains(sh.Key))
                                run.Add(SeedEntry(HomeCommands.FeedPrefix + sh.Key, BoardEntryKind.FeedSection, g.Id, def, sh, "", HomeSize.Auto, it.Options));
                        break;
                    case SpotifyFeedSource when feedState != HomeFeedState.Ready:
                        // Loading with no shape on record (a first launch): shelves at their boxes; failed or offline: one line that says so (not one
                        // per missing shelf).
                        for (int k = 0, places = feedState == HomeFeedState.Loading ? FeedPlaceholders : 1; k < places; k++)
                            run.Add(new BoardEntry("placeholder:" + k.ToString(System.Globalization.CultureInfo.InvariantCulture), BoardEntryKind.Placeholder,
                                g.Id, def, null, HomeSize.Full, SectionTemplate.CoverShelf, "", false, false, false, false, false, -1)
                                { Feed = feedState, Zone = SeedZone(SectionTemplate.CoverShelf, PlaceholderCards, "home:placeholder:" + k.ToString(System.Globalization.CultureInfo.InvariantCulture), "") });
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
                        else if (feedState == HomeFeedState.Loading && it.Shape.Length > 0)
                            // Its answer on the way: a flat skeleton of the shape it drew last, at its place (never a hidden entry that appears).
                            run.Add(SeedEntry(it.Id, BoardEntryKind.Item, g.Id, def, it.Shape[0] with { Key = ss.Ref.Key }, ss.TitleHint, it.Size, it.Options) with
                            { Raw = ss });
                        else
                            run.Add(new BoardEntry(it.Id, BoardEntryKind.Item, g.Id, def, ss, it.Size, SectionTemplate.CoverShelf,
                                ss.TitleHint, false, false, false, false, false, -1) { Hidden = true, Options = it.Options });
                        break;
                    }
                    case FillSource fill:
                        Flush(entries, run, titles);
                        int n = 0, max = HomeShortcuts.ClosedByHistory(g) ? Math.Max(0, fill.Max - 1) : fill.Max;
                        // The answer that names the shortcuts is on its way: the fill's cells stand as flat placeholders, so the grid has its
                        // loaded rows from the first frame and the board under it never moves when they land (a live open, 2026-11-17).
                        if (feedState == HomeFeedState.Loading)
                            for (int k = 0; k < max; k++)
                                entries.Add(new BoardEntry(HomeCommands.FillPrefix + PendingFill + k.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                    BoardEntryKind.FillCell, g.Id, def, new PinSource("", "", ""), HomeSize.Auto, SectionTemplate.ShortcutFill, "",
                                    false, false, false, false, true, -1) { Parent = it.Id, Feed = feedState });
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
        SectionInput? Input, ImmutableArray<SectionTemplate> Alternates)
    {
        /// <summary>A section whose answer is still on its way (its zone a seed): its entry draws the loading skeleton.</summary>
        public bool Seed { get; init; }
    }

    /// <summary>The shape the document recorded for the section <paramref name="key"/> (a placed item's or the feed's), or null.</summary>
    static SeedShape? ShapeOf(HomeDoc doc, string key)
    {
        foreach (var g in doc.Groups)
            foreach (var it in g.Items)
                foreach (var sh in it.Shape)
                    if (sh.Key == key) return sh;
        return null;
    }

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

    /// <summary>Does the document place the widget <paramref name="name"/> anywhere (hidden or not)?</summary>
    static bool HasWidget(HomeDoc doc, string name)
    {
        foreach (var g in doc.Groups)
            foreach (var it in g.Items)
                if (it.Source is WidgetSource w && w.Widget == name) return true;
        return false;
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
        {
            Zone = f.Zone, Options = options, Members = f.OneCardBaseline ? [f.Ref] : [], Alternates = f.Alternates.IsDefault ? [] : f.Alternates,
            Feed = f.Seed ? HomeFeedState.Loading : HomeFeedState.Ready,
        };

    /// <summary>Emit a run of section entries, folding two or more neighbouring one-card baseline sections (at their auto
    /// size) into one Spine Shelf, then keeping two neighbours from drawing one template (<see cref="SectionRhythm"/>).
    /// Nothing is reordered.</summary>
    static void Flush(ImmutableArray<BoardEntry>.Builder into, List<BoardEntry> run, ZoneTitles? titles)
    {
        var folded = new List<BoardEntry>(run.Count);
        Fold(folded, run, titles);
        SectionRhythm.Apply(folded);
        into.AddRange(folded);
        run.Clear();
    }

    static void Fold(List<BoardEntry> into, List<BoardEntry> run, ZoneTitles? titles)
    {
        // Every one-card baseline section of the run is ONE family ("Because you like": For fans of / More like / Made for you / Based on
        // your recent listening), whatever Spotify put between them (an episode section split live1's 19 into runs of 8 and 11, which drew
        // two chapters under one title). So the run's foldable sections gather into ONE shelf at the first one's place; the shelf pages
        // them when they are more than one page holds (SpineShelfRules.MaxItems). Everything else keeps its order.
        int foldable = 0, at = -1;
        for (int k = 0; k < run.Count; k++)
            if (Foldable(run[k])) { foldable++; if (at < 0) at = k; }
        for (int k = 0; k < run.Count; k++)
        {
            if (foldable >= 2 && Foldable(run[k]))
            {
                if (k != at) continue;
                var first = run[k];
                var members = ImmutableArray.CreateBuilder<SectionRef>(foldable);
                var inputs = new List<SectionInput>(foldable);
                for (int m = k; m < run.Count; m++)
                {
                    if (!Foldable(run[m])) continue;
                    var r = ((SpotifySectionSource)run[m].Raw!).Ref;
                    members.Add(r);
                    if (t_inputs is not null && t_inputs.TryGetValue(r.Key, out var input)) inputs.Add(input);
                }
                Zone? zone = titles is not null && inputs.Count == foldable ? FoldZone(members[0].Key, inputs, titles)
                    : first.Feed == HomeFeedState.Loading ? SeedFold(members[0].Key, foldable) : first.Zone;
                into.Add(first with
                {
                    Id = HomeCommands.FoldPrefix + members[0].Key, Kind = BoardEntryKind.Fold,
                    Template = zone?.Kind == ZoneKind.CoverShelf ? SectionTemplate.CoverShelf : SectionTemplate.SpineShelf,
                    OnlyHide = true, Members = members.ToImmutable(), Zone = zone, Title = zone?.Title ?? first.Title,
                });
                continue;
            }
            into.Add(run[k] with { Members = [] });
        }
    }

    /// <summary>The fold's one zone: the members' shelf items ("Because you like"), each its card and its preview songs (flat placeholders
    /// until they land), in Spotify's order. Only members with no drawable card at all leave a plain cover shelf.</summary>
    static Zone FoldZone(string firstKey, List<SectionInput> inputs, ZoneTitles titles)
    {
        var clusters = ClusterFold.Fold(inputs, titles);
        if (clusters.Count > 0)
            return new Zone(ZoneKind.SpineShelf, "home:fold:" + firstKey, titles.BecauseYouLike, null, [], Clusters: clusters, SectionSlot: inputs[0].Slot);
        var cards = new List<HomeCard>(inputs.Count);
        var seen = new HashSet<long>();
        foreach (var s in inputs)
            foreach (var c in s.Cards)
                if (SectionRoles.IsUsable(c) && seen.Add(c.DedupeKey)) { cards.Add(c); break; }
        return new Zone(ZoneKind.CoverShelf, "home:fold:" + firstKey, titles.BecauseYouLike, null, cards, SectionSlot: inputs[0].Slot, TotalCount: cards.Count);
    }

    /// <summary>A section whose answer is on its way, as the shape it drew last (<see cref="SeedShape"/>): its template over a seed zone of blank
    /// cards of the template's kind, as many as it showed (a fold member: one blank card the fold gathers).</summary>
    static BoardEntry SeedEntry(string id, BoardEntryKind kind, string groupId, HomeSourceDef? def, in SeedShape sh, string title, HomeSize size,
        ImmutableSortedDictionary<string, string> options)
    {
        var r = new SectionRef(sh.Key, 0, "");
        var template = sh.Fold ? SectionTemplate.SpineShelf : sh.Template;
        return new BoardEntry(id, kind, groupId, def, new SpotifySectionSource(r, title), size, template, title,
            false, false, false, false, kind == BoardEntryKind.FeedSection, -1)
        {
            Zone = SeedZone(template, sh.Fold ? 1 : sh.Count, "home:section:" + sh.Key, title, sh.Head),
            Options = options, Members = sh.Fold ? [r] : [], Feed = HomeFeedState.Loading,
        };
    }

    /// <summary>The seed of a fold whose members' answer is on its way: one blank item per member.</summary>
    static Zone SeedFold(string firstKey, int members) => SeedZone(SectionTemplate.SpineShelf, members, "home:fold:" + firstKey, "");

    /// <summary>A zone of <paramref name="template"/>'s kind over <paramref name="count"/> blank cards of the kind it draws (a Spine Shelf's items are
    /// blank cards with no preview songs yet: flat placeholders at their size).</summary>
    public static Zone SeedZone(SectionTemplate template, int count, string key, string title, bool head = false)
    {
        var kind = template switch
        {
            SectionTemplate.ArtistCircles => HomeCardKind.Artist,
            SectionTemplate.ShowGrid => HomeCardKind.Podcast,
            SectionTemplate.BookShelf => HomeCardKind.Audiobook,
            SectionTemplate.VideoTiles or SectionTemplate.EpisodeRows => HomeCardKind.Episode,
            SectionTemplate.ReleaseList => HomeCardKind.Album,
            _ => HomeCardKind.Playlist,
        };
        int n = Math.Max(1, count);
        var cards = new HomeCard[n];
        for (int i = 0; i < n; i++) cards[i] = HomeCard.Blank(kind, i);
        // A title the deriver draws as its bar (an empty one would draw no header, and the loaded section has one).
        string heading = title.Length > 0 ? title : " ";
        var zoneKind = SectionChooser.KindOf(template);
        if (zoneKind == ZoneKind.SpineShelf)
        {
            var clusters = new ZoneCluster[n];
            for (int i = 0; i < n; i++) clusters[i] = new ZoneCluster(" ", " ", [], Header: cards[i]);
            return new Zone(ZoneKind.SpineShelf, key, heading, null, [], Clusters: clusters, TotalCount: n);
        }
        // A header that named an entity: the same header over blank words and no cover (its cover slot and two lines derive at their size).
        return new Zone(zoneKind, key, heading, null, cards, TotalCount: n) { Header = head ? new HeaderEntity("", " ", null, " ") : null };
    }

    /// <summary>The document with every placed section's and the feed's <see cref="HomeItem.Shape"/> as <paramref name="shown"/> (a model over an
    /// answer on hand) draws them; <paramref name="doc"/> itself when nothing changed or the model is not over an answer.</summary>
    public static HomeDoc WithShapes(HomeDoc doc, BoardModel shown)
    {
        var byId = new Dictionary<string, BoardEntry>(StringComparer.Ordinal);
        var folded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in shown.Entries)
        {
            if (e.Kind == BoardEntryKind.Placeholder || e.Feed != HomeFeedState.Ready) return doc;
            byId[e.Id] = e;
            if (e.Kind == BoardEntryKind.Fold) foreach (var m in e.Members) folded.Add(m.Key);
        }
        var placed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in doc.Groups) foreach (var it in g.Items) if (it.Source is SpotifySectionSource ps) placed.Add(ps.Ref.Key);

        var groups = doc.Groups;
        bool changed = false;
        for (int gi = 0; gi < doc.Groups.Length; gi++)
        {
            var g = doc.Groups[gi];
            var items = g.Items;
            for (int i = 0; i < g.Items.Length; i++)
            {
                var it = g.Items[i];
                ImmutableArray<SeedShape>? shape = null;
                switch (it.Source)
                {
                    case SpotifySectionSource ss when byId.TryGetValue(it.Id, out var e) && !e.Hidden:
                        shape = [new SeedShape(ss.Ref.Key, e.Template, CountOf(e.Zone), false, e.Zone?.Header is not null)];
                        break;
                    case SpotifySectionSource ss when folded.Contains(ss.Ref.Key):
                        shape = [new SeedShape(ss.Ref.Key, SectionTemplate.SpineShelf, 1, true)];
                        break;
                    case SpotifyFeedSource:
                    {
                        var b = ImmutableArray.CreateBuilder<SeedShape>();
                        foreach (var e in shown.Entries)
                        {
                            if (e.GroupId != g.Id || e.Hidden) continue;
                            if (e.Kind == BoardEntryKind.FeedSection && e.Raw is SpotifySectionSource fs)
                                b.Add(new SeedShape(fs.Ref.Key, e.Template, CountOf(e.Zone), false, e.Zone?.Header is not null));
                            else if (e.Kind == BoardEntryKind.Fold)
                                foreach (var m in e.Members)
                                    if (!placed.Contains(m.Key)) b.Add(new SeedShape(m.Key, SectionTemplate.SpineShelf, 1, true));
                        }
                        shape = b.ToImmutable();
                        break;
                    }
                }
                if (shape is not { } sh || sh.AsSpan().SequenceEqual(it.Shape.AsSpan())) continue;
                items = items.SetItem(i, it with { Shape = sh });
            }
            if (items != g.Items) { groups = groups.SetItem(gi, g with { Items = items }); changed = true; }
        }
        return changed ? doc with { Groups = groups } : doc;
    }

    static int CountOf(Zone? z) => z is null ? 0 : z.Clusters is { Count: > 0 } c ? c.Count : z.Items.Count;

    /// <summary>A shelf zone with no item to draw (no usable card) draws its cards as a cover shelf instead.</summary>
    static Zone Drawable(Zone z)
        => z.Kind == ZoneKind.SpineShelf && z.Clusters is not { Count: > 0 } && z.Items.Count > 0
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
