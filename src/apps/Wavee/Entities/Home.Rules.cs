// ── Entities/Home.Rules.cs ─────────────────────────────────────────────────────────────────────────────────────────
// the ported Home rule sets: both projections, the facet strip, the hero / module / artist-row geometry, the wash
// source, the timeline merge, play routing, card text + identity colour, the section cursor / walk / chart filter /
// routes / near tail, the Charts deck, card navigation (CORE half), and the layout document + reducer + commands + wire
//
// Role: CORE
// Owner: P
// Wave: 5
// Budget: the pre-declared overflow partial of `Home.cs` (WP-5.P contract §1: used because Home.cs passed 2,340 lines)
// Spec: ch 10 §8, ch 11 §8, ch 12 §8; plan §6.1 rows 10-12; 0.2.9 `Features/Home/{HomeLandingProjection, HomeFacetProjection,
//   HomeFacetStrip, HomeHeroLayout, HomeWashSource, HomeModules (:490-804), HomeArtistRowLayout, HomeTimelineMerge,
//   HomeCardPlayRouting, HomeCards (:35-88, :248-264, :478-484, :1081-1088), HomeSectionPaging, BrowseSectionWalk,
//   HomeSectionRoutes, HomeSectionNavigation, HomeBrowseCards, HomeSectionAppendPreloader (:46-54), Persistence/*}.cs`,
//   `Wavee.Core/Home/*`
//
// VERBATIM, with ONE type map (contract §2): `HomeFeed` → `HomeFeedView`, `HomeSection` → `HomeSectionView`,
// `HomeCard`+`HomeCardMeta` → the `HomeCard` handle, a card's `Meta.X` → `card.X`, a card's uri comparison → its
// `DedupeKey` (one entity, one slot — and no allocation), `int?` cursors → the `SectionPaging` sentinels, and a
// `WaveeNotification` subclass → the flat `Notification`. Member names, constants and algorithms are 0.2.9's; where a
// rule had to change, the member's own comment says so (the hero's `ActionsBlock`, the grid chrome's delegation, the
// walk's fold over a LANDED row). Every rule here is pure over its arguments or over the tables — no hooks, no elements.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee;

// ══ 1. THE LANDING PROJECTION (0.2.9 HomeLandingProjection.cs) ═══════════════════════════════════════════════════════

/// <summary>The page's rows, in the prototype's order unless a <see cref="HomeLayoutDoc"/> reorders the authored modules.
/// Chrome rows (Chips / Artists / Timeline / Charts / Sections / Tail) are not user-orderable. Never persisted.</summary>
public enum HomeRow : byte
{
    Chips, Hero, Weekly, Quick, Recents, MixBand, Artists, ChipCards, Radio, EpisodesAndBooks,
    Queue, Books,
    Podcasts, Timeline, Sections, Editorial, Feed, Tail,
    // Charts is CHROME — not a HomeGroupKind, not in home-layout.json, not user-hideable.
    Charts,
}

/// <summary>One app-authored Home module plus the source section that can satisfy its drill-in.</summary>
public sealed record HomeLandingModule(HomeGroup Group, HomeSectionView? PrimarySection);

/// <summary>The finite prototype rhythm projected from a lossless feed: at most one module per kind, the unconsumed
/// identified sections, and the row table after hide + reorder.</summary>
public sealed class HomeLanding
{
    readonly HomeLandingModule?[] _modules = new HomeLandingModule?[(int)HomeGroupKind.PodcastShelf + 1];

    public IReadOnlyList<HomeSectionView> Sections { get; private set; } = Array.Empty<HomeSectionView>();

    /// <summary>The page's rows AFTER hide + reorder.</summary>
    public IReadOnlyList<HomeRow> Rows { get; private set; } = HomeLandingProjection.DefaultRows;

    public HomeLandingModule? Get(HomeGroupKind kind) => _modules[(int)kind];
    internal void Set(HomeGroupKind kind, HomeLandingModule module) => _modules[(int)kind] = module;
    internal void Clear(HomeGroupKind kind) => _modules[(int)kind] = null;
    internal void SetSections(IReadOnlyList<HomeSectionView> sections) => Sections = sections;
    internal void SetRows(IReadOnlyList<HomeRow> rows) => Rows = rows;
}

/// <summary>Pure landing projection. Source groups are concatenated in feed order and de-duplicated by card only for the
/// landing preview; <see cref="HomeFeedView.Sections"/> is never rewritten. A module whose authored shape cannot be
/// satisfied is SUPPRESSED but never eats its cards (the half pair falls into the grid), and the shapeless grid wears a
/// server label when exactly one feeds it.</summary>
public static class HomeLandingProjection
{
    static readonly HomeGroupKind[] AggregatedKinds =
    [
        HomeGroupKind.QuickGrid, HomeGroupKind.Recents, HomeGroupKind.MixBand,
        HomeGroupKind.ChipCards, HomeGroupKind.RadioDial, HomeGroupKind.QueueList,
        HomeGroupKind.RatedShelf, HomeGroupKind.PodcastShelf, HomeGroupKind.Featured,
        HomeGroupKind.DiscoverFeed,
    ];

    /// <summary>The prototype's designed row table.</summary>
    public static readonly HomeRow[] DefaultRows =
    [
        HomeRow.Chips, HomeRow.Hero, HomeRow.Weekly, HomeRow.Quick, HomeRow.Recents, HomeRow.MixBand,
        HomeRow.Artists, HomeRow.ChipCards, HomeRow.Radio, HomeRow.EpisodesAndBooks, HomeRow.Podcasts,
        HomeRow.Timeline, HomeRow.Charts, HomeRow.Sections, HomeRow.Editorial, HomeRow.Feed, HomeRow.Tail,
    ];

    public static HomeLanding Project(HomeFeedView feed, HomeModuleTitles titles) => Project(feed, titles, null);

    /// <summary>Project the feed, then apply hide + reorder BEFORE the page synthesizes rows.</summary>
    public static HomeLanding Project(HomeFeedView feed, HomeModuleTitles titles, HomeLayoutDoc? layout)
    {
        var landing = new HomeLanding();
        var consumedSections = new HashSet<string>(StringComparer.Ordinal);

        var heroes = Groups(feed, HomeGroupKind.Hero);
        if (heroes.Count > 0)
        {
            var hero = heroes[0];
            landing.Set(HomeGroupKind.Hero, new HomeLandingModule(hero, PrimarySection(feed, [hero])));
            MarkConsumed(consumedSections, [hero]);
        }

        var weekly = Groups(feed, HomeGroupKind.WeeklyPair);
        var (pair, pairSources, loneWeekly, loneWeeklySource) = WeeklyPair(weekly);
        if (pair is not null)
        {
            landing.Set(HomeGroupKind.WeeklyPair, new HomeLandingModule(pair, PrimarySection(feed, pairSources)));
            MarkConsumed(consumedSections, pairSources);
        }

        for (int i = 0; i < AggregatedKinds.Length; i++)
        {
            var kind = AggregatedKinds[i];
            var source = Groups(feed, kind);
            var lone = kind == HomeGroupKind.QuickGrid ? loneWeekly : null;
            if (source.Count == 0 && lone is null) continue;
            var cards = UniqueCards(source);
            // FIRST, not appended: the grid renders only its first QuickShown cards.
            if (lone is { } l && !Holds(cards, l)) cards.Insert(0, l);
            if (cards.Count == 0) continue;
            int total = cards.Count;
            for (int g = 0; g < source.Count; g++) total = Math.Max(total, source[g].TotalCount);
            var group = new HomeGroup(kind, Title(kind, source, titles), cards, TotalCount: total);
            IReadOnlyList<HomeGroup> contributors = source;
            if (lone is not null && loneWeeklySource is not null)
            {
                var withLone = new List<HomeGroup>(source.Count + 1);
                withLone.AddRange(source);
                if (!withLone.Contains(loneWeeklySource)) withLone.Add(loneWeeklySource);
                contributors = withLone;
            }
            landing.Set(kind, new HomeLandingModule(group, PrimarySection(feed, contributors)));
            MarkConsumed(consumedSections, contributors);
        }

        landing.SetSections(SectionDirectory(feed, consumedSections));
        ApplyLayout(landing, layout ?? HomeLayoutDoc.Default);
        return landing;
    }

    /// <summary>Hide authored-off modules, then build the row table from the remaining order. Chrome anchors: Artists
    /// after MixBand; Timeline + Charts + Sections after Podcasts. QueueList + RatedShelf collapse when adjacent.</summary>
    public static void ApplyLayout(HomeLanding landing, HomeLayoutDoc layout)
    {
        var defaults = HomeLayoutModules.DefaultOrder;
        for (int i = 0; i < defaults.Length; i++)
            if (layout.IsHidden(defaults[i])) landing.Clear(defaults[i]);

        var visible = layout.VisibleFixedModules();
        var rows = new List<HomeRow>(visible.Count + 6) { HomeRow.Chips };
        bool artists = false, afterPodcasts = false;
        for (int i = 0; i < visible.Count; i++)
        {
            var kind = visible[i];
            if (kind == HomeGroupKind.QueueList)
            {
                bool nextBooks = i + 1 < visible.Count && visible[i + 1] == HomeGroupKind.RatedShelf;
                rows.Add(nextBooks ? HomeRow.EpisodesAndBooks : HomeRow.Queue);
                if (nextBooks) i++;
            }
            else if (kind == HomeGroupKind.RatedShelf) rows.Add(HomeRow.Books);
            else rows.Add(RowOf(kind));

            if (kind == HomeGroupKind.MixBand) { rows.Add(HomeRow.Artists); artists = true; }
            if (kind == HomeGroupKind.PodcastShelf)
            {
                rows.Add(HomeRow.Timeline);
                rows.Add(HomeRow.Charts);
                rows.Add(HomeRow.Sections);
                afterPodcasts = true;
            }
        }

        if (!artists) rows.Add(HomeRow.Artists);
        if (!afterPodcasts)
        {
            rows.Add(HomeRow.Timeline);
            rows.Add(HomeRow.Charts);
            rows.Add(HomeRow.Sections);
        }
        rows.Add(HomeRow.Tail);
        landing.SetRows(rows);
    }

    static HomeRow RowOf(HomeGroupKind kind) => kind switch
    {
        HomeGroupKind.Hero => HomeRow.Hero,
        HomeGroupKind.WeeklyPair => HomeRow.Weekly,
        HomeGroupKind.QuickGrid => HomeRow.Quick,
        HomeGroupKind.Recents => HomeRow.Recents,
        HomeGroupKind.MixBand => HomeRow.MixBand,
        HomeGroupKind.ChipCards => HomeRow.ChipCards,
        HomeGroupKind.RadioDial => HomeRow.Radio,
        HomeGroupKind.QueueList => HomeRow.Queue,
        HomeGroupKind.RatedShelf => HomeRow.Books,
        HomeGroupKind.PodcastShelf => HomeRow.Podcasts,
        HomeGroupKind.Featured => HomeRow.Editorial,
        HomeGroupKind.DiscoverFeed => HomeRow.Feed,
        _ => HomeRow.Tail,
    };

    static List<HomeGroup> Groups(HomeFeedView feed, HomeGroupKind kind)
    {
        var result = new List<HomeGroup>(3);
        for (int i = 0; i < feed.Groups.Count; i++)
            if (feed.Groups[i].Kind == kind && feed.Groups[i].Cards.Count > 0) result.Add(feed.Groups[i]);
        return result;
    }

    static List<HomeCard> UniqueCards(IReadOnlyList<HomeGroup> groups)
    {
        var cards = new List<HomeCard>();
        var seen = new HashSet<long>();
        for (int g = 0; g < groups.Count; g++)
            for (int c = 0; c < groups[g].Cards.Count; c++)
            {
                var card = groups[g].Cards[c];
                if (seen.Add(card.DedupeKey)) cards.Add(card);
            }
        return cards;
    }

    /// <summary>The two-up module (both appointments present) or the ONE appointment that does exist (it falls into the
    /// quick grid — half of an authored 1fr 1fr row is a hole, but the card must still reach the landing).</summary>
    static (HomeGroup? Pair, IReadOnlyList<HomeGroup> PairSources, HomeCard? Lone, HomeGroup? LoneSource)
        WeeklyPair(IReadOnlyList<HomeGroup> groups)
    {
        HomeCard? discover = null, release = null;
        HomeGroup? discoverSource = null, releaseSource = null;
        for (int g = 0; g < groups.Count; g++)
            for (int c = 0; c < groups[g].Cards.Count; c++)
            {
                var card = groups[g].Cards[c];
                switch (card.Format)
                {
                    case "discover-weekly" when discover is null: discover = card; discoverSource = groups[g]; break;
                    case "release-radar" when release is null: release = card; releaseSource = groups[g]; break;
                }
            }
        if (discover is { } d && release is { } r)
        {
            IReadOnlyList<HomeGroup> sources = ReferenceEquals(discoverSource, releaseSource)
                ? [discoverSource!]
                : [discoverSource!, releaseSource!];
            return (new HomeGroup(HomeGroupKind.WeeklyPair, null, [d, r], TotalCount: 2), sources, null, null);
        }
        return (null, Array.Empty<HomeGroup>(), discover ?? release, discoverSource ?? releaseSource);
    }

    static void MarkConsumed(HashSet<string> consumed, IReadOnlyList<HomeGroup> groups)
    {
        for (int i = 0; i < groups.Count; i++)
            if (groups[i].Uri is { Length: > 0 } uri) consumed.Add(uri);
    }

    static bool Holds(List<HomeCard> cards, in HomeCard card)
    {
        long key = card.DedupeKey;
        for (int i = 0; i < cards.Count; i++) if (cards[i].DedupeKey == key) return true;
        return false;
    }

    static string? Title(HomeGroupKind kind, IReadOnlyList<HomeGroup> source, HomeModuleTitles titles) => kind switch
    {
        HomeGroupKind.QuickGrid => SoleTitle(source) ?? titles.JumpBackIn,
        HomeGroupKind.Recents => titles.Recents,
        HomeGroupKind.MixBand => FirstTitle(source) ?? titles.MadeForYou,
        HomeGroupKind.ChipCards => titles.TopMixes,
        HomeGroupKind.RadioDial => titles.Radio,
        HomeGroupKind.QueueList => titles.UpNext,
        HomeGroupKind.RatedShelf => titles.Audiobooks,
        HomeGroupKind.PodcastShelf => titles.Podcasts,
        HomeGroupKind.Featured => titles.EditorsPicks,
        HomeGroupKind.DiscoverFeed => titles.BecauseYouListened,
        _ => FirstTitle(source),
    };

    /// <summary>The one label the contributors carry, or null when none — or they disagree. Blank copy is not a label.</summary>
    static string? SoleTitle(IReadOnlyList<HomeGroup> groups)
    {
        string? only = null;
        for (int i = 0; i < groups.Count; i++)
        {
            var title = groups[i].Title;
            if (string.IsNullOrWhiteSpace(title)) continue;
            if (only is null) only = title;
            else if (!string.Equals(only, title, StringComparison.Ordinal)) return null;
        }
        return only;
    }

    static string? FirstTitle(IReadOnlyList<HomeGroup> groups)
    {
        for (int i = 0; i < groups.Count; i++)
            if (groups[i].Title is { Length: > 0 } title) return title;
        return null;
    }

    static HomeSectionView? PrimarySection(HomeFeedView feed, IReadOnlyList<HomeGroup> groups)
    {
        HomeSectionView? best = null;
        int bestTotal = -1;
        if (feed.Sections is { } sections)
            for (int g = 0; g < groups.Count; g++)
            {
                if (groups[g].Uri is not { Length: > 0 } uri) continue;
                for (int s = 0; s < sections.Count; s++)
                    if (string.Equals(sections[s].Uri, uri, StringComparison.Ordinal) && sections[s].TotalCount > bestTotal)
                    {
                        best = sections[s];
                        bestTotal = sections[s].TotalCount;
                    }
            }
        return best;
    }

    static IReadOnlyList<HomeSectionView> SectionDirectory(HomeFeedView feed, HashSet<string> consumed)
    {
        var result = new List<HomeSectionView>();
        var uris = new HashSet<string>(StringComparer.Ordinal);
        if (feed.Sections is { Count: > 0 } sections)
        {
            for (int i = 0; i < sections.Count; i++)
            {
                var section = sections[i];
                if (!HasIdentity(section)) continue;
                if (section.Uri is { Length: > 0 } uri && (consumed.Contains(uri) || !uris.Add(uri))) continue;
                result.Add(section);
            }
            return result;
        }

        // A source that omits the ledger: its identified groups stand in as local drill pages.
        for (int i = 0; i < feed.Groups.Count; i++)
        {
            var group = feed.Groups[i];
            if (group.Title is not { Length: > 0 } && group.Uri is not { Length: > 0 }) continue;
            if (group.Uri is { Length: > 0 } uri && (consumed.Contains(uri) || !uris.Add(uri))) continue;
            result.Add(new HomeSectionView(Table.None, group.Uri, group.Title, group.Subtitle, group.Cards,
                Math.Max(group.TotalCount, group.Cards.Count), group.Cards.Count));
        }
        return result;
    }

    static bool HasIdentity(HomeSectionView section) => section.Uri is { Length: > 0 } || section.Title is { Length: > 0 };
}

// ══ 2. THE FACET PROJECTION (0.2.9 HomeFacetProjection.cs) ═══════════════════════════════════════════════════════════

/// <summary>What one row of a FACETED home page is — shelf shapes, not authored appointments.</summary>
public enum HomeFacetRowKind : byte { Hero, Recents, Podcasts, Audiobooks, Episodes, Feed, Shelf }

/// <summary>One rendered facet row: the shape, the group the module shell renders, and the section it drills into (null
/// for the coalesced baseline feed).</summary>
public sealed record HomeFacetRow(HomeFacetRowKind Kind, HomeGroup Group, HomeSectionView? Section);

/// <summary>A facet page is the SERVER's ordered section list: every titled section survives, in order, wearing its own
/// title — except a run of CONSECUTIVE baseline sections, which folds into one discover feed.</summary>
public static class HomeFacetProjection
{
    static readonly HomeGroup[] NoGroups = Array.Empty<HomeGroup>();

    public static IReadOnlyList<HomeFacetRow> Rows(HomeFeedView feed, HomeModuleTitles titles)
    {
        var rows = new List<HomeFacetRow>();
        var run = new List<HomeCard>();
        var runSeen = new HashSet<long>();

        void CloseRun()
        {
            if (run.Count == 0) return;
            rows.Add(new HomeFacetRow(HomeFacetRowKind.Feed,
                new HomeGroup(HomeGroupKind.DiscoverFeed, titles.BecauseYouListened, run.ToArray(), TotalCount: run.Count), null));
            run.Clear();
            runSeen.Clear();
        }

        void ExtendRun(IReadOnlyList<HomeCard> cards)
        {
            for (int i = 0; i < cards.Count; i++)
                if (runSeen.Add(cards[i].DedupeKey)) run.Add(cards[i]);
        }

        var sections = feed.Sections;
        if (sections is { Count: > 0 })
        {
            var byKey = GroupsByKey(feed.Groups);
            for (int i = 0; i < sections.Count; i++)
            {
                var section = sections[i];
                if (section.Cards.Count == 0) continue;
                var cards = Unique(section.Cards);
                if (cards.Count == 0) continue;
                IReadOnlyList<HomeGroup> groups = byKey.TryGetValue(Key(section.Uri, section.Title), out var found) ? found : NoGroups;

                if (Baseline(groups)) { ExtendRun(cards); continue; }
                CloseRun();

                if (Hero(groups) is { } hero && cards.Count == 1)
                {
                    rows.Add(new HomeFacetRow(HomeFacetRowKind.Hero, hero, section));
                    continue;
                }

                var kind = Classify(groups, cards);
                rows.Add(new HomeFacetRow(kind,
                    new HomeGroup(GroupKind(kind), section.Title, cards, section.Subtitle, section.Uri,
                        Math.Max(section.TotalCount, cards.Count)),
                    section));
            }
        }
        else
        {
            var groups = feed.Groups;
            for (int i = 0; i < groups.Count; i++)
            {
                var group = groups[i];
                if (group.Cards.Count == 0) continue;
                if (group.Kind == HomeGroupKind.DiscoverFeed) { ExtendRun(group.Cards); continue; }
                CloseRun();
                rows.Add(new HomeFacetRow(
                    group.Kind == HomeGroupKind.Hero && group.Cards.Count == 1 ? HomeFacetRowKind.Hero : Classify([group], group.Cards),
                    group, null));
            }
        }

        CloseRun();
        return rows;
    }

    static HomeFacetRowKind Classify(IReadOnlyList<HomeGroup> groups, IReadOnlyList<HomeCard> cards)
    {
        for (int i = 0; i < groups.Count; i++)
            if (groups[i].Kind == HomeGroupKind.Recents) return HomeFacetRowKind.Recents;
        if (All(cards, HomeCardKind.Podcast)) return HomeFacetRowKind.Podcasts;
        if (All(cards, HomeCardKind.Audiobook)) return HomeFacetRowKind.Audiobooks;
        if (All(cards, HomeCardKind.Episode)) return HomeFacetRowKind.Episodes;
        return HomeFacetRowKind.Shelf;
    }

    static HomeGroupKind GroupKind(HomeFacetRowKind kind) => kind switch
    {
        HomeFacetRowKind.Recents => HomeGroupKind.Recents,
        HomeFacetRowKind.Podcasts => HomeGroupKind.PodcastShelf,
        HomeFacetRowKind.Audiobooks => HomeGroupKind.RatedShelf,
        HomeFacetRowKind.Episodes => HomeGroupKind.QueueList,
        _ => HomeGroupKind.Shelf,
    };

    static bool Baseline(IReadOnlyList<HomeGroup> groups)
    {
        for (int i = 0; i < groups.Count; i++) if (groups[i].Kind == HomeGroupKind.DiscoverFeed) return true;
        return false;
    }

    static HomeGroup? Hero(IReadOnlyList<HomeGroup> groups)
    {
        for (int i = 0; i < groups.Count; i++)
            if (groups[i].Kind == HomeGroupKind.Hero && groups[i].Cards.Count > 0) return groups[i];
        return null;
    }

    static bool All(IReadOnlyList<HomeCard> cards, HomeCardKind kind)
    {
        if (cards.Count == 0) return false;
        for (int i = 0; i < cards.Count; i++) if (cards[i].Kind != kind) return false;
        return true;
    }

    static List<HomeCard> Unique(IReadOnlyList<HomeCard> cards)
    {
        var list = new List<HomeCard>(cards.Count);
        var seen = new HashSet<long>();
        for (int i = 0; i < cards.Count; i++) if (seen.Add(cards[i].DedupeKey)) list.Add(cards[i]);
        return list;
    }

    static Dictionary<string, List<HomeGroup>> GroupsByKey(IReadOnlyList<HomeGroup> groups)
    {
        var map = new Dictionary<string, List<HomeGroup>>(StringComparer.Ordinal);
        for (int i = 0; i < groups.Count; i++)
        {
            string key = Key(groups[i].Uri, groups[i].Title);
            if (!map.TryGetValue(key, out var list)) map.Add(key, list = []);
            list.Add(groups[i]);
        }
        return map;
    }

    static string Key(string? uri, string? title) => uri is { Length: > 0 } u ? u : "title:" + (title ?? "");
}

// ══ 3. THE FACET STRIP (0.2.9 HomeFacetStrip.cs) ═════════════════════════════════════════════════════════════════════

/// <summary>What one rendered position of the facet strip IS.</summary>
public enum FacetSlotKind : byte { All, Tab, Fused, Sub }

/// <summary>One rendered position. <see cref="Select"/> is what a tap writes to <see cref="Home.SelectedFacet"/> (null =
/// unfiltered); the Tab and Fused slots of the SAME chip share <see cref="Key"/> — that shared key IS the morph.</summary>
public readonly record struct FacetSlot(FacetSlotKind Kind, HomeChip? Chip, HomeChip? Sub, bool Selected, string Key, string? Select);

/// <summary>The strip's layout as a pure function of (server chips, selection).</summary>
public static class HomeFacetStrip
{
    /// <summary>Which top-level chip owns the selection: it IS the selection, or one of its sub-chips is.</summary>
    public static (HomeChip? Parent, HomeChip? Sub) Resolve(IReadOnlyList<HomeChip> chips, string? selected)
    {
        HomeChip? parent = null, sub = null;
        for (int i = 0; i < chips.Count && parent is null; i++)
        {
            var chip = chips[i];
            if (string.Equals(chip.Id, selected, StringComparison.Ordinal)) parent = chip;
            else
                foreach (var candidate in chip.SubChips)
                    if (string.Equals(candidate.Id, selected, StringComparison.Ordinal)) { parent = chip; sub = candidate; break; }
        }
        return (parent, sub);
    }

    /// <summary>All, then every server chip in feed order; a selected parent spills its sub-chips RIGHT AFTER itself; a
    /// selected sub folds its parent into one Fused slot whose Select steps back ONE level (the parent id).</summary>
    public static List<FacetSlot> Slots(IReadOnlyList<HomeChip> chips, string? selected)
    {
        var (parent, sub) = Resolve(chips, selected);
        var slots = new List<FacetSlot>(chips.Count + 3) { new(FacetSlotKind.All, null, null, parent is null, "facet-all", null) };
        foreach (var chip in chips)
        {
            bool on = ReferenceEquals(chip, parent);
            string key = "facet-pill:" + chip.Id;
            if (on && sub is not null) slots.Add(new(FacetSlotKind.Fused, chip, sub, true, key, chip.Id));
            else
            {
                slots.Add(new(FacetSlotKind.Tab, chip, null, on, key, chip.Id));
                if (on)
                    foreach (var child in chip.SubChips)
                        slots.Add(new(FacetSlotKind.Sub, chip, child, false, "facet-sub:" + child.Id, child.Id));
            }
        }
        return slots;
    }
}

// ══ 4. THE HERO GEOMETRY (0.2.9 HomeHeroLayout.cs, with ch 11 §9.2's ActionsBlock fix) ═══════════════════════════════

public enum HomeHeroTier : byte { Narrow, Medium, Wide }

/// <summary>One exact geometry contract shared by the hero renderer and the landing estimator.</summary>
public readonly record struct HomeHeroMetrics(HomeHeroTier Tier, float Height, float CopyPaddingX, float CopyPaddingY, float ArtworkSize)
{
    public bool Stacked => Tier == HomeHeroTier.Narrow;
}

public static class HomeHeroLayout
{
    public const float MediumWidth = 700f;
    public const float WideWidth = 980f;

    public const float CopyPaddingX = Spacing.L + Spacing.XXXL;                 // 48
    public const float CopyPaddingY = Spacing.L + Spacing.XXL + Spacing.XS;    // 44
    public const float ArtworkFade = Spacing.XXXL * 3f;                        // 96

    const float EyebrowBlock = 16f + Spacing.S;                 // Caption 12/16 + an 8 margin
    const float WideTitleBlock = 2f * 60f;                      // ArtistTitle 48/60, two lines
    const float MediumTitleBlock = 2f * 40f;                    // ArtistCompactTitle 32/40, two lines
    const float NarrowTitleBlock = 2f * 36f;                    // PageHero 28/36, two lines
    const float TitleMargin = Spacing.M;
    const float TagsBlock = 20f + Spacing.M;                    // a Caption tag inside 2-DIP padding + a 12 margin
    const float MetaBlock = 20f + Spacing.L;                    // Body 14/20 + a 16 margin
    const float PulseBlock = 28f + Spacing.M;                   // the flip-countdown digit row + a 12 margin
    /// <summary>THE FIX (ch 11 §9.2 trap): the hero's action row is built from the media pill, whose height is
    /// <see cref="Controls.PillHeight"/> = 36 — 0.2.9 reserved 32 (<c>Spacing.XXXL</c>), so its estimator under-reported
    /// the row by 4 DIP. The tier heights are therefore 388 / 348 / 340, not 384 / 344 / 336.</summary>
    const float ActionsBlock = Controls.PillHeight;

    public static HomeHeroMetrics For(float width)
    {
        var tier = width >= WideWidth ? HomeHeroTier.Wide : width >= MediumWidth ? HomeHeroTier.Medium : HomeHeroTier.Narrow;
        float height = ContentHeight(tier);
        // A square whose edge is the surface height keeps the complete cover — never stretched into a banner.
        return new HomeHeroMetrics(tier, height, CopyPaddingX, CopyPaddingY, height);
    }

    public static float HeightFor(float width) => For(width).Height;

    public static float ContentHeight(HomeHeroTier tier)
    {
        float title = tier switch
        {
            HomeHeroTier.Wide => WideTitleBlock,
            HomeHeroTier.Medium => MediumTitleBlock,
            _ => NarrowTitleBlock,
        };
        return 2f * CopyPaddingY + EyebrowBlock + title + TitleMargin + TagsBlock + MetaBlock + PulseBlock + ActionsBlock;
    }
}

// ══ 5. THE WASH SOURCE (0.2.9 HomeWashSource.cs) ═════════════════════════════════════════════════════════════════════

/// <summary>The three cards Home's shell wash is derived from — selected by KIND and ORDINAL, never by copy.</summary>
public readonly record struct HomeWashCards(HomeCard? Hero, HomeCard? Weekly, HomeCard? Mix);

/// <summary>One resolved leg: the FULL-ALPHA colour plus the artwork identity it was resolved from (the layer's key).</summary>
public readonly record struct HomeWashPick(ColorF Color, string Key);

/// <summary>The three legs, in stacking order; any may be null — no card, or no honest colour yet.</summary>
public readonly record struct HomeWashPicks(HomeWashPick? Hero, HomeWashPick? Weekly, HomeWashPick? Mix);

/// <summary>The PURE selector behind Home's shell wash: payload accent first, the graded cover second, NOTHING third.</summary>
public static class HomeWashSource
{
    public static HomeWashCards Sources(HomeFeedView? feed) => new(
        FirstCard(feed, HomeGroupKind.Hero),
        FirstCard(feed, HomeGroupKind.WeeklyPair),
        FirstCard(feed, HomeGroupKind.MixBand));

    public static HomeWashPicks Select(HomeFeedView? feed, Func<string?, Scheme?> schemeFor) => Select(Sources(feed), schemeFor);

    public static HomeWashPicks Select(in HomeWashCards cards, Func<string?, Scheme?> schemeFor)
        => new(Pick(cards.Hero, schemeFor), Pick(cards.Weekly, schemeFor), Pick(cards.Mix, schemeFor));

    /// <summary>One card → its leg, or null. Tier 1 the payload accent, LIFTED; tier 2 the graded cover through the
    /// chrome derivation (a card with no artwork is never asked); there is no tier 3.</summary>
    public static HomeWashPick? Pick(HomeCard? card, Func<string?, Scheme?> schemeFor)
    {
        if (card is not { } c) return null;
        if (c.Accent != 0u)
            return new HomeWashPick(Design.Palette.Lift(Design.Palette.ToColor(c.Accent)) with { A = 1f }, KeyOf(c));
        if (c.ImageUrl is { Length: > 0 } url && schemeFor(url) is { } scheme)
            return new HomeWashPick(Design.Palette.ChromeAccent(scheme) with { A = 1f }, KeyOf(c));
        return null;
    }

    /// <summary>The artwork this card's colour is still WAITING on, or null (already resolved, or never gradeable).</summary>
    public static string? PlaneUrl(HomeCard? card)
        => card is { } c && c.Accent == 0u && c.ImageUrl is { Length: > 0 } url ? url : null;

    /// <summary>The leg's identity: the size-independent artwork key, else the card's uri.</summary>
    public static string KeyOf(HomeCard card)
    {
        if (card.ImageUrl is { Length: > 0 } url)
        {
            var key = Palette.KeyOf(url);
            if (key.Length > 0) return key.ToString();
        }
        return card.Uri;
    }

    /// <summary>A value fingerprint over the three legs (colour + artwork + slot, never alpha).</summary>
    public static int Fingerprint(in HomeWashPicks picks) => HashCode.Combine(Leg(picks.Hero), Leg(picks.Weekly), Leg(picks.Mix));

    static int Leg(HomeWashPick? pick) => pick is { } p ? HashCode.Combine(p.Key, p.Color.R, p.Color.G, p.Color.B) : 0;

    static HomeCard? FirstCard(HomeFeedView? feed, HomeGroupKind kind)
    {
        if (feed is null) return null;
        var groups = feed.Groups;
        for (int i = 0; i < groups.Count; i++)
            if (groups[i].Kind == kind && groups[i].Cards.Count > 0) return groups[i].Cards[0];
        return null;
    }
}

// ══ 6. THE MODULE GEOMETRY (0.2.9 HomeModules.cs:490-804) ════════════════════════════════════════════════════════════

/// <summary>ONE source of truth for module geometry, read by the renderer AND the landing estimator (an estimate that
/// disagrees with the rendered height re-pins the scroll anchor mid-scroll). Tokens map to <c>Design.Size</c> /
/// <c>Spacing</c>; the shelf height and the grid chrome DELEGATE to <see cref="Controls"/> so the two cannot be handed
/// different numbers.</summary>
public static class HomeModuleLayout
{
    public const float FallbackWidth = 1100f;
    public const float ModuleGap = Design.Size.SectionGapWide;
    public const float ModuleGapNarrow = Design.Size.SectionGap;
    public const float HeadGap = Spacing.M;

    public const float SplitEvenMin = 1020f;
    public const float EditorialMin = 980f;

    public const float ShelfCardMin = Design.Size.ShelfCardMin;
    public const float ShelfCardMax = Design.Size.ShelfCardMax;
    public const float ShelfEdgeFade = Design.Size.FadeShelf;

    public const float GridGap = Spacing.M;
    /// <summary>One title line box — <see cref="Controls.GridTitleLineH"/>.</summary>
    public const float GridTitleLineH = Controls.GridTitleLineH;
    /// <summary>The metadata line + its gap — <see cref="Controls.GridSubtitleBlockH"/>.</summary>
    public const float GridSubtitleBlockH = Controls.GridSubtitleBlockH;
    /// <summary>The label block's non-line overhead — <see cref="Controls.GridLabelOverhead"/>. DELEGATED: 0.3's grid card
    /// states 28 here where 0.2.9's MediaCard stated 14, and the estimator must say what the renderer draws.</summary>
    public const float GridLabelOverhead = Controls.GridLabelOverhead;
    /// <summary>The one-title-line, one-metadata-line reserve — reproduced EXACTLY by <c>GridCardChromeFor(1, true)</c>.
    /// 0.2.9's constant was 52; 0.3's grid card states 28 + 20 + 18 = 66, and this follows the card.</summary>
    public const float GridCardChrome = GridLabelOverhead + GridTitleLineH + GridSubtitleBlockH;

    /// <summary>The cell reserve from how many lines the title may wrap to and whether a metadata line renders (0.2.9's
    /// clamp of <paramref name="titleLines"/> at 1 kept; the arithmetic is <see cref="Controls.GridCardChromeFor"/>).</summary>
    public static float GridCardChromeFor(int titleLines, bool hasSubtitle)
        => Controls.GridCardChromeFor(titleLines < 1 ? 1 : titleLines, hasSubtitle);

    // ── the Fold tile ──
    public const float FoldCardHeight = 176f;
    public const float FoldCover = 124f;
    public const float FoldCopyMaxFrac = 0.70f;
    public const float FoldCardMin = 440f;
    public const float FoldCardMax = 9999f;

    /// <summary>Cover i's REST pose in card-local DIP (right:-40 / top:6 / width:250), clamped so a 0-width first frame
    /// cannot park covers at a negative X.</summary>
    public static void FoldRest(int i, float cardW, out float x, out float y, out float rot)
    {
        float left = MathF.Max(0f, cardW - 250f + 40f);
        (float lx, float ly, float r) = i switch
        {
            0 => (0f, 32f, -11f),
            1 => (44f, 16f, 5f),
            _ => (92f, 2f, -2f),
        };
        x = left + lx; y = 6f + ly; rot = r;
    }

    /// <summary>Cover i's hover DELTA on the rest pose (WhileHover is additive).</summary>
    public static void FoldFan(int i, out float dx, out float dy, out float drot)
        => (dx, dy, drot) = i switch
        {
            0 => (-10f, 6f, -5f),
            1 => (2f, -6f, 3f),
            _ => (10f, 0f, 3f),
        };

    /// <summary>32 header + card + 12 lift + 12 shadow clearance.</summary>
    public const float FoldExtent = 32f + FoldCardHeight + 2f * Spacing.M;
    /// <summary>Empty / failed Charts: ONE named constant, so the row never estimates 0.</summary>
    public const float FoldStateExtent = 32f + 96f + Spacing.XXL;

    public const int QuickShown = 8;
    public const int ChipCardsShown = 6;
    public const int RadioShown = 12;
    public const int QueueShown = 6;
    public const int BooksShown = 6;
    public const int EditorialCompanions = 3;

    /// <summary>The module gap for a row width — compute ONCE from the AVAILABLE width and pass it down (ch 10 §9 real
    /// defect #1: the renderer read the outer row width and the estimator the available one).</summary>
    public static float Gap(float width) => width >= 1080f ? ModuleGap : ModuleGapNarrow;

    public static float RadioColMin => Design.Size.Thumb32 + 2f * Spacing.S + Spacing.M + 3f * Design.Size.Thumb64;

    public static int RadioColumns(float width)
    {
        float gap = Spacing.XXL;
        int n = (int)MathF.Floor((MathF.Max(0f, width) + gap) / (RadioColMin + gap));
        return Math.Clamp(n, 1, 2);
    }

    /// <summary>Column count per module at a row width — the prototype's container queries, verbatim.</summary>
    public static int Columns(HomeGroupKind kind, float width) => kind switch
    {
        HomeGroupKind.MixBand => width > 1080f ? 6 : width > 620f ? 3 : 2,
        HomeGroupKind.QuickGrid => width > 1120f ? 4 : width > 780f ? 3 : 2,
        HomeGroupKind.ChipCards => width > 1020f ? 3 : width > 680f ? 2 : 1,
        HomeGroupKind.RadioDial => RadioColumns(width),
        HomeGroupKind.WeeklyPair => width > 760f ? 2 : 1,
        _ => 1,
    };

    public static float HeroHeight(float width) => HomeHeroLayout.HeightFor(width);

    public static float ShelfCardHeight(float cardW) => Controls.ShelfHeight(cardW);

    /// <summary>32 chevron header + card + 12 lift + 12 shadow clearance, at the fitted card width.</summary>
    public static float ShelfExtent(float width)
    {
        var (_, cardW) = FluentGpu.Scene.FillRowVirtualLayout.Fit(width, ShelfCardMin, ShelfCardMax, Spacing.M);
        return Controls.ShelfExtent(cardW);
    }

    /// <summary>Per-card row height — each arm the skin's own arithmetic in the skin's own tokens.</summary>
    public static float CardHeight(HomeGroupKind kind) => kind switch
    {
        HomeGroupKind.QuickGrid => Design.Size.Thumb56,
        HomeGroupKind.WeeklyPair => Design.Size.Thumb56 + 2f * Spacing.L,
        HomeGroupKind.MixBand => 2f * Spacing.L + 36f + Spacing.XS + 16f + 3f * 16f + 3f * Spacing.XXS,
        HomeGroupKind.ChipCards => 20f + 20f + 16f + 2f * Spacing.S + 2f * Spacing.M,
        HomeGroupKind.RadioDial => 48f,
        HomeGroupKind.QueueList => 20f + 16f + 2f * Spacing.S + 1f,
        HomeGroupKind.RatedShelf => Design.Size.Thumb48 + 2f * Spacing.S,
        _ => 56f,
    };

    /// <summary>ONE row gap for every wrapped module grid; the audiobook stack keeps its dense 2.</summary>
    public static float RowGap(HomeGroupKind kind) => kind switch
    {
        HomeGroupKind.QuickGrid or HomeGroupKind.WeeklyPair or HomeGroupKind.ChipCards => Spacing.M,
        HomeGroupKind.RatedShelf => Spacing.XXS,
        _ => 0f,
    };

    /// <summary>Display count per module on the landing — the estimator sizes what is SHOWN.</summary>
    public static int Shown(HomeGroupKind kind, int count) => kind switch
    {
        HomeGroupKind.Hero => Math.Min(count, 1),
        HomeGroupKind.QuickGrid => Math.Min(count, QuickShown),
        HomeGroupKind.ChipCards => Math.Min(count, ChipCardsShown),
        HomeGroupKind.RadioDial => Math.Min(count, RadioShown),
        HomeGroupKind.QueueList => Math.Min(count, QueueShown),
        HomeGroupKind.RatedShelf => Math.Min(count, BooksShown),
        HomeGroupKind.Featured => Math.Min(count, 1 + EditorialCompanions),
        _ => count,
    };

    /// <summary>The rendered height of a module's CONTENT (no head) at this width.</summary>
    public static float ContentExtent(HomeGroupKind kind, float width, int count)
    {
        int shown = Shown(kind, count);
        if (shown <= 0) return 0f;
        if (kind == HomeGroupKind.Hero) return HeroHeight(width);
        if (kind is HomeGroupKind.Recents or HomeGroupKind.PodcastShelf or HomeGroupKind.DiscoverFeed) return ShelfExtent(width);
        if (kind == HomeGroupKind.Featured) return FeaturedExtent(width, shown);
        int columns = Columns(kind, width);
        int rows = (shown + columns - 1) / columns;
        float gap = RowGap(kind);
        float h = rows * CardHeight(kind) + Math.Max(0, rows - 1) * gap;
        // The band draws a 1px hairline between wrapped rows and sits inside a 1px contour.
        if (kind == HomeGroupKind.MixBand) h += Math.Max(0, rows - 1) + 2f;
        return h;
    }

    /// <summary>The editorial break: a 148-cover feature beside (≥ <see cref="EditorialMin"/>) or above its companions.</summary>
    public static float FeaturedExtent(float width, int shown)
    {
        const float feature = 2f * Spacing.XL + 148f;
        int companions = Math.Max(0, shown - 1);
        float companionColumn = companions == 0 ? 0f
            : companions * (2f * Spacing.M + Design.Size.Thumb48) + (companions - 1) * Spacing.S;
        if (companions == 0) return feature;
        return width >= EditorialMin ? MathF.Max(feature, companionColumn) : feature + Spacing.L + companionColumn;
    }

    static readonly string[] s_kindNames =
    [
        "Hero", "QuickGrid", "Shelf", "Featured", "MixBand", "WeeklyPair", "ChipCards", "RadioDial", "RatedShelf",
        "QueueList", "DiscoverFeed", "Recents", "Topic", "SectionEntry", "PodcastShelf",
    ];

    static string NameOf(HomeGroupKind kind) => (int)kind < s_kindNames.Length ? s_kindNames[(int)kind] : "Shelf";

    public static string RowKey(HomeGroupKind kind, string uri) => "home-" + NameOf(kind) + ":" + uri;

    /// <summary>A card's key inside its group: the group's identity plus the card's <see cref="HomeCard.DedupeKey"/>.</summary>
    public static string SourceCardKey(HomeGroup group, HomeCard card)
        => (group.Uri ?? group.Title ?? NameOf(group.Kind)) + "\u001F" + card.DedupeKey.ToString(CultureInfo.InvariantCulture);

    // Memoized per INSTANCE (0.2.9's ConditionalWeakTable, kept): KeyAt runs per realized row and again for the list's
    // own lookup. A group is immutable, and the composer returns NEW groups whenever a section's Version or CardVersion
    // moves, so the key changes exactly when the rendered STRUCTURE does; a card's own text needs no key change because the
    // handle reads it live (the fingerprint therefore covers card identity, not card copy).
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<HomeGroup, string> SourceGroupKeys = new();
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<HomeSectionView>, string> SectionSetKeys = new();

    public static string SourceGroupKey(HomeGroup group)
        => SourceGroupKeys.GetValue(group, static g => "home-source:" + Fingerprint(g).ToString("X16", CultureInfo.InvariantCulture));

    public static string SectionSetKey(IReadOnlyList<HomeSectionView> sections)
        => SectionSetKeys.GetValue(sections, static s => ComputeSectionSetKey(s));

    static string ComputeSectionSetKey(IReadOnlyList<HomeSectionView> sections)
    {
        ulong h = Text(Offset, "sections");
        for (int i = 0; i < sections.Count; i++)
        {
            var s = sections[i];
            h = Text(Text(Text(h, s.Uri), s.Title), s.Subtitle);
            h = Value(Value(h, unchecked((ulong)s.TotalCount)), unchecked((ulong)s.Cards.Count));
            for (int c = 0; c < s.Cards.Count; c++) h = Value(h, unchecked((ulong)s.Cards[c].DedupeKey));
        }
        return "home-section-set:" + h.ToString("X16", CultureInfo.InvariantCulture);
    }

    const ulong Offset = 14695981039346656037UL;
    const ulong Prime = 1099511628211UL;

    static ulong Value(ulong h, ulong value)
    {
        for (int i = 0; i < 8; i++) { h ^= (byte)(value >> (i * 8)); h *= Prime; }
        return h;
    }

    static ulong Text(ulong h, string? value)
    {
        h = Value(h, unchecked((ulong)(value?.Length ?? -1)));
        if (value is null) return h;
        for (int i = 0; i < value.Length; i++) h = Value(h, value[i]);
        return h;
    }

    static ulong Fingerprint(HomeGroup group)
    {
        ulong h = Value(Offset, (ulong)group.Kind);
        h = Text(Text(Text(h, group.Title), group.Subtitle), group.Uri);
        h = Value(Value(h, unchecked((ulong)group.TotalCount)), unchecked((ulong)group.Cards.Count));
        for (int i = 0; i < group.Cards.Count; i++)
            h = Value(Value(h, unchecked((ulong)group.Cards[i].DedupeKey)), unchecked((ulong)group.Cards[i].SectionSlot));
        return h;
    }
}

// ══ 7. THE ARTIST ROW (0.2.9 HomeArtistRowLayout.cs, verbatim) ═══════════════════════════════════════════════════════

/// <summary>The top-artist podium's Wide / Spine tier (900, asymmetric 24-DIP hysteresis), its fill-the-width ramp, its
/// PRIVATE module gap and the hand-rolled double click. The landing estimator reads <see cref="ModuleGap"/> and
/// <see cref="RampScaleFor"/> too (ch 10 §9 real defects #2 and #3).</summary>
public static class HomeArtistRowLayout
{
    public const float TierHysteresisDip = 24f;

    public const int TierWide = 0;
    public const int TierSpine = 1;

    public static int NominalTierFor(float w) => w <= 0f ? TierWide : w >= 900f ? TierWide : TierSpine;

    public static int InitialTierForViewport(float viewportWidth) => NominalTierFor(viewportWidth);

    public static int TierFor(float w, int prev, bool initialized = true)
    {
        if (w <= 0f) return prev;
        if (!initialized) return NominalTierFor(w);
        int nominal = NominalTierFor(w);
        if (nominal >= prev) return nominal;
        int dipped = NominalTierFor(w - TierHysteresisDip);
        return dipped < prev ? dipped : prev;
    }

    public static float BaseArtSize(int i) => i == 0 ? 76f : i < 3 ? 60f : 46f;

    public const float MinArtScale = 1f;
    public const float MaxArtScale = 1.6f;

    /// <summary>The scale that stretches the ramp to fill <paramref name="count"/> × the fitted column width, solved
    /// against the ramp's own AVERAGE box and clamped [1.0, 1.6].</summary>
    public static float RampScaleFor(float fittedColumnWidth, int count, float podChrome)
    {
        if (count <= 0 || fittedColumnWidth <= 0f) return MinArtScale;
        float sumDefault = 0f;
        for (int i = 0; i < count; i++) sumDefault += BaseArtSize(i) + podChrome;
        float avgDefault = sumDefault / count;
        if (avgDefault <= 0f) return MinArtScale;
        return Math.Clamp(fittedColumnWidth / avgDefault, MinArtScale, MaxArtScale);
    }

    public static float ArtSize(int i, float scale) => BaseArtSize(i) * scale;

    /// <summary>This row's OWN module-bottom gap: 32 / 24 at the same 1080 boundary the shared helper uses.</summary>
    public static float ModuleGap(float width) => width >= 1080f ? 32f : 24f;

    public const long DoubleClickWindowMs = 400;

    /// <summary>A second click on the same uri inside the window (ticks out of order never read as a double).</summary>
    public static bool IsDoubleClick(string uri, string? lastUri, long lastTick, long nowTick)
        => uri.Length > 0 && string.Equals(uri, lastUri, StringComparison.Ordinal)
           && nowTick >= lastTick && nowTick - lastTick <= DoubleClickWindowMs;
}

// ══ 8. THE WHAT'S-NEW TIMELINE (0.2.9 HomeTimelineMerge.cs) ══════════════════════════════════════════════════════════

/// <summary>What a timeline row IS — its badge and its click.</summary>
public enum HomeTimelineKind : byte { Release, Concert }

/// <summary>One row: the source notification plus the fields the merge sorts and counts on.</summary>
public readonly record struct HomeTimelineRow(HomeTimelineKind Kind, Notification Source, string Id, long Timestamp, bool IsUnread);

/// <summary>One day group, newest day first. <see cref="DayTicks"/> is LOCAL midnight as <see cref="DateTime"/> ticks.</summary>
public readonly record struct HomeTimelineGroup(long DayTicks, HomeTimelineRow[] Rows);

/// <summary>The module's data: the capped, grouped rows plus the UNCAPPED "N unheard of M" pair.</summary>
public readonly record struct HomeTimelineFeed(HomeTimelineGroup[] Groups, int Shown, int Total, int Unread)
{
    public static readonly HomeTimelineFeed Empty = new([], 0, 0, 0);
    public bool IsEmpty => Groups.Length == 0;
}

/// <summary>Which notifications are timeline material — gated on KIND, never on the category pill — in what order and
/// day group, and what the header counter says.</summary>
public static class HomeTimelineMerge
{
    public const int MaxRows = 8;

    public static HomeTimelineFeed Build(IReadOnlyList<Notification>? items, int maxRows = MaxRows, Func<long, long>? dayOf = null)
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

    /// <summary>The row a notification contributes, or null when it is not timeline material: every what's-new release,
    /// and a Spotify-category item only when it is a concert announcement.</summary>
    public static HomeTimelineRow? Eligible(in Notification n) => n.Category switch
    {
        NotifyCategory.NewRelease => new HomeTimelineRow(HomeTimelineKind.Release, n, n.Id, n.TimestampMs, n.IsUnread),
        NotifyCategory.Social when SpotifyUpdates.IsConcert(in n)
            => new HomeTimelineRow(HomeTimelineKind.Concert, n, n.Id, n.TimestampMs, n.IsUnread),
        _ => null,
    };

    /// <summary>Local midnight for a UTC epoch-ms instant, as <see cref="DateTime"/> ticks.</summary>
    public static long LocalDay(long unixMs) => DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().Date.Ticks;
}

// ══ 9. PLAY ROUTING, CARD TEXT, CARD COLOUR (0.2.9 HomeCardPlayRouting.cs, HomeCards.cs) ════════════════════════════

/// <summary>How a card's ▶ starts playback.</summary>
public static class HomeCardPlayRouting
{
    /// <summary>True for the kinds that are ONE playable item (a track, an episode); every other kind is a context.</summary>
    public static bool PlaysAsItem(HomeCardKind kind) => kind is HomeCardKind.Track or HomeCardKind.Episode;
}

/// <summary>The card vocabulary's number / duration / blurb formats (0.2.9 <c>HomeCards</c> helpers, culture-aware).</summary>
public static class HomeCardText
{
    /// <summary>"1 hr 5 min" / "45 min" (minutes rounded, at least 1); "" for no duration.</summary>
    public static string Duration(long ms)
    {
        if (ms <= 0) return "";
        int totalMin = (int)Math.Round(ms / 60000d);
        int h = totalMin / 60, m = totalMin % 60;
        return h > 0 ? Strings.Detail.DurationHrMin(h, m) : Strings.Detail.DurationMin(Math.Max(1, m));
    }

    /// <summary>The prototype's <c>hrs()</c>: "1.3 h" past the hour, "45 m" under it (audiobook lengths).</summary>
    public static string Hours(long ms)
    {
        if (ms <= 0) return "";
        var c = CultureInfo.CurrentCulture;
        return ms >= 3600000 ? (ms / 3600000d).ToString("0.0", c) + " h" : Math.Round(ms / 60000d).ToString("0", c) + " m";
    }

    /// <summary>Play / listener counts: "1.2B", "3.4M", "5.6K", else the grouped number — culture-aware on the value.</summary>
    public static string CompactNumber(long n)
    {
        var c = CultureInfo.CurrentCulture;
        return n >= 1_000_000_000 ? (n / 1_000_000_000d).ToString("0.#", c) + "B"
             : n >= 1_000_000 ? (n / 1_000_000d).ToString("0.#", c) + "M"
             : n >= 1_000 ? (n / 1_000d).ToString("0.#", c) + "K"
             : n.ToString("N0", c);
    }

    /// <summary>The first sentence of a blurb: STRIP FIRST (a raw fragment's first '.' is inside
    /// <c>spotify:playlist:…</c>), then cut at the first <c>". "</c> past index 20.</summary>
    public static string FirstSentence(string? html)
    {
        var plain = PlainText(html);
        if (string.IsNullOrWhiteSpace(plain)) return "";
        int end = plain.IndexOf(". ", StringComparison.Ordinal);
        return end > 20 ? plain[..(end + 1)] : plain;
    }

    /// <summary>0.2.9 <c>SpotifyExportMapper.ToPlainText</c>: drop real tags (a letter, '/' or '!' after '&lt;'), keep the
    /// text around them, collapse whitespace. The markup-free common case returns the input.</summary>
    public static string? PlainText(string? html)
    {
        if (string.IsNullOrEmpty(html) || html.IndexOf('<') < 0) return html;
        var sb = new System.Text.StringBuilder(html.Length);
        bool lastSpace = false;
        for (int i = 0; i < html.Length; i++)
        {
            char c = html[i];
            if (c == '<' && i + 1 < html.Length && IsTagNameStart(html[i + 1]))
            {
                int close = html.IndexOf('>', i + 1);
                if (close >= 0) { i = close; continue; }
            }
            if (char.IsWhiteSpace(c))
            {
                if (!lastSpace && sb.Length > 0) { sb.Append(' '); lastSpace = true; }
                continue;
            }
            sb.Append(c);
            lastSpace = false;
        }
        return sb.ToString().TrimEnd();

        static bool IsTagNameStart(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '/' or '!';
    }
}

/// <summary>A card's identity colour — the ONE derivation (0.2.9 <c>HomeCards.RawAccent/Accent/SpineFallback/
/// SpineAccent/AccentOrChrome</c>): the payload accent, else the graded cover's tinted base, else NOTHING. The hash
/// palette only RE-HUES a real near-neutral seed; it never invents a colour for a card that has none.</summary>
public static class HomeCardAccent
{
    /// <summary>The payload accent alone, or null.</summary>
    public static ColorF? Raw(in HomeCard c) => c.Accent != 0u ? Design.Palette.ToColor(c.Accent) : null;

    /// <summary>The identity seed: payload accent → the graded <c>BackgroundTintedBase</c> (else <c>BackgroundBase</c>) of
    /// the card's cover → null. A card with no artwork is never asked.</summary>
    public static ColorF? Seed(in HomeCard c, Func<string?, Scheme?> schemeFor)
    {
        if (Raw(in c) is { } payload) return payload;
        if (c.ImageUrl is not { Length: > 0 } url || schemeFor(url) is not { } g) return null;
        uint tone = g.BackgroundTintedBase != 0u ? g.BackgroundTintedBase : g.BackgroundBase;
        return tone != 0u ? Design.Palette.ToColor(tone) : null;
    }

    /// <summary>The lifted identity colour, or null ("not graded yet" — paint nothing).</summary>
    public static ColorF? Accent(in HomeCard c, Func<string?, Scheme?> schemeFor)
        => Seed(in c, schemeFor) is { } seed ? Design.Palette.Lift(seed) : null;

    /// <summary>The 2-DIP spine: null without a seed; a near-neutral seed is re-hued from the card's stable hash; then the
    /// contrast-solved hairline (3.25:1, 3.55:1 hovered).</summary>
    public static ColorF? SpineAccent(in HomeCard c, bool hovered, Func<string?, Scheme?> schemeFor)
    {
        if (Seed(in c, schemeFor) is not { } seed) return null;
        var (_, saturation, _) = seed.ToHsv();
        if (saturation <= Design.Palette.NeutralS) seed = SpineFallback(in c);
        return hovered ? Design.Palette.HairlineHover(seed) : Design.Palette.Hairline(seed);
    }

    /// <summary>For chrome that must paint regardless (the hero's wash and Play): the identity colour, else
    /// <paramref name="chrome"/> (the app accent the caller passes).</summary>
    public static ColorF AccentOrChrome(in HomeCard c, ColorF chrome, Func<string?, Scheme?> schemeFor)
        => Accent(in c, schemeFor) ?? chrome;

    /// <summary>The theme-aware semantic role a near-neutral cover re-hues to, by the card identity's stable hash.</summary>
    public static ColorF SpineFallback(in HomeCard c) => (StableHash(in c) & 3u) switch
    {
        0u => Tok.AccentDefault,
        1u => Tok.SystemFillSuccess,
        2u => Tok.SystemFillCaution,
        _ => Tok.SystemFillCritical,
    };

    /// <summary>FNV-1a over the identity: the gid bytes, or the text form's characters — deterministic across runs.</summary>
    public static uint StableHash(in HomeCard c)
    {
        uint h = 2166136261;
        if (c.IsBlank) return h;
        var id = c.Id;
        if (id.Form == EntityForm.Gid)
        {
            Span<byte> gid = stackalloc byte[16];
            id.WriteGid(gid);
            for (int i = 0; i < gid.Length; i++) h = (h ^ gid[i]) * 16777619;
        }
        else
        {
            string text = Entities.Strings.Resolve(id.TextId);
            for (int i = 0; i < text.Length; i++) h = (h ^ text[i]) * 16777619;
        }
        return h;
    }
}

// ══ 10. SECTIONS: THE CURSOR, THE WALK, THE CHART FILTER, THE ROUTES, THE TAIL (ch 12 §8) ═══════════════════════════

/// <summary>The "Show all" cursor arithmetic over a section view (0.2.9 <c>HomeSectionPaging</c>): RAW vs DEDUPED, and
/// termination by the cursor, never by the total. The <c>int?</c> cursors are <see cref="SectionPaging.NoCursor"/> (no
/// paging info) and <see cref="SectionPaging.Complete"/> (an explicit terminator).</summary>
public static class HomeSectionPaging
{
    /// <summary>The raw number of items handed us, floored at the card count.</summary>
    public static int NextOffset(HomeSectionView s) => Math.Max(s.RawItemCount, s.Cards.Count);

    /// <summary>Does "Show all" stay armed? The server cursor wins (compared with <c>&gt;=</c>: a cursor EQUAL to our raw
    /// position means "ask for that next"); only with no cursor is the total an arming hint.</summary>
    public static bool HasMore(HomeSectionView s, int serverNextOffset = SectionPaging.NoCursor)
        => serverNextOffset == SectionPaging.NoCursor ? s.TotalCount > NextOffset(s)
         : serverNextOffset != SectionPaging.Complete && serverNextOffset >= NextOffset(s);

    /// <summary>Can a fetched page's cursor carry us forward from the offset that produced it? A missing or explicit
    /// terminator, or a value at or behind the request (a complete section answers <c>nextOffset: 0</c>), says no.</summary>
    public static bool CanAdvance(int requestedOffset, int nextOffset)
        => nextOffset != SectionPaging.NoCursor && nextOffset != SectionPaging.Complete && nextOffset > requestedOffset;

    /// <summary>Fold a page in: append the cards not seen, advance the raw cursor by the FULL page, never lower the total.</summary>
    public static HomeSectionView Append(HomeSectionView current, IReadOnlyList<HomeCard> pageCards, int pageTotal)
    {
        int raw = NextOffset(current);
        var seen = new HashSet<long>();
        var cards = new List<HomeCard>(current.Cards.Count + pageCards.Count);
        foreach (var card in current.Cards) { seen.Add(card.DedupeKey); cards.Add(card); }
        int duplicates = 0;
        for (int i = 0; i < pageCards.Count; i++)
        {
            if (seen.Add(pageCards[i].DedupeKey)) cards.Add(pageCards[i]);
            else duplicates++;
        }
        return current with
        {
            Cards = cards,
            TotalCount = Math.Max(current.TotalCount, pageTotal),
            RawItemCount = raw + pageCards.Count,
            DuplicateCount = current.DuplicateCount + duplicates,
        };
    }

    /// <summary>How far along an eager walk is, 0..1. A total at or below what we hold is worth one assumed page more —
    /// never a finished bar.</summary>
    public static float WalkFraction(HomeSectionView s, int pageAssumed)
    {
        int have = s.Cards.Count;
        int total = s.TotalCount > have ? s.TotalCount : have + Math.Max(1, pageAssumed);
        return total <= 0 ? 0f : have / (float)total;
    }

    /// <summary>Did a fold put anything new on screen? A TERMINATION signal (a page the dedupe ate whole).</summary>
    public static bool Progressed(HomeSectionView before, HomeSectionView after) => after.Cards.Count > before.Cards.Count;

    /// <summary>A browse section with no server cursor: offset + page count versus the total; <see cref="SectionPaging.Complete"/>
    /// when the total is exhausted (or unknown).</summary>
    public static int BrowseNextOffset(int requestedOffset, int pageCount, int total)
    {
        int loaded = requestedOffset + pageCount;
        return total > loaded ? loaded : SectionPaging.Complete;
    }

    /// <summary>A fetched browse page's next cursor: a real offset passes through, the explicit terminator is final even
    /// against a total that claims more, and ONLY a page with no paging info falls back to the synthesized cursor.</summary>
    public static int BrowseSectionNextOffset(int requestedOffset, int pageNextOffset, int pageCount, int pageTotal)
        => pageNextOffset == SectionPaging.Complete ? SectionPaging.Complete
         : pageNextOffset != SectionPaging.NoCursor ? pageNextOffset
         : BrowseNextOffset(requestedOffset, pageCount, pageTotal);
}

/// <summary>The Charts drill walk (0.2.9 <c>BrowseSectionWalk</c>): how it begins from an optional seed, and how one page
/// folds. In 0.3 a fetched page LANDS ON THE ROW (<c>ReplacePage</c>), so <see cref="Fold"/> compares the view before
/// the landing with the view after it rather than appending a page itself.</summary>
public static class BrowseSectionWalk
{
    public readonly record struct Start(HomeSectionView? Current, int Offset, bool Exhausted, bool Publish, bool FetchFirst);

    /// <summary><see cref="NextOffset"/> is <see cref="SectionPaging.Complete"/> when exhausted.</summary>
    public readonly record struct Step(HomeSectionView Section, int NextOffset, bool Exhausted);

    /// <summary>A seed with cards is PUBLISHED before the next offset is asked (never treated as complete — the total
    /// under-reports); an empty seed that still has more fetches offset 0 unpublished; an empty finished seed stops.</summary>
    public static Start Begin(HomeSectionView? seed)
    {
        if (seed is null) return new Start(null, 0, false, false, true);
        if (seed.Cards.Count == 0)
            return HomeSectionPaging.HasMore(seed)
                ? new Start(null, 0, false, false, true)
                : new Start(seed, 0, true, true, false);
        return new Start(seed, HomeSectionPaging.NextOffset(seed), false, true, false);
    }

    /// <summary>One landed page: <paramref name="after"/> is the row's view once the page at
    /// <paramref name="requestedOffset"/> landed. An empty page, a cursor that cannot advance and a page that put nothing
    /// new on screen all latch exhausted.</summary>
    public static Step Fold(HomeSectionView before, HomeSectionView after, int requestedOffset)
    {
        int pageCount = after.RawItemCount - requestedOffset;
        if (pageCount <= 0) return new Step(after, SectionPaging.Complete, true);
        int next = HomeSectionPaging.BrowseSectionNextOffset(requestedOffset, after.NextOffset, pageCount, after.TotalCount);
        bool exhausted = !HomeSectionPaging.CanAdvance(requestedOffset, next) || !HomeSectionPaging.Progressed(before, after);
        return new Step(after, exhausted ? SectionPaging.Complete : next, exhausted);
    }
}

/// <summary>The Charts grid's title filter: ordinal-ignore-case, first occurrence; the span is what the pill paints.</summary>
public static class ChartTitleMatch
{
    public static bool TryFind(string? title, string? query, out int start, out int length)
    {
        start = 0;
        length = 0;
        if (title is not { Length: > 0 } || string.IsNullOrWhiteSpace(query)) return false;
        string q = query.Trim();
        int i = title.IndexOf(q, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return false;
        start = i;
        length = q.Length;
        return true;
    }

    public static IReadOnlyList<HomeCard> Filter(IReadOnlyList<HomeCard> cards, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return cards;
        var hits = new List<HomeCard>(cards.Count);
        for (int i = 0; i < cards.Count; i++)
            if (TryFind(cards[i].Title, query, out _, out _)) hits.Add(cards[i]);
        return hits;
    }
}

/// <summary>The <c>home-section:</c> drill route. The ROUTE PREFIX, never the uri, selects the endpoint.</summary>
public static class HomeSectionRoutes
{
    public const string Prefix = "home-section:";
    /// <summary>A client-minted section identity: addresses nothing on any server, never a paging argument.</summary>
    public const string LocalPrefix = "wavee:local:";

    public static string Page(string sectionUri) => Prefix + sectionUri;
    public static bool Is(string route) => route.StartsWith(Prefix, StringComparison.Ordinal);
    public static string UriOf(string route) => Is(route) ? route[Prefix.Length..] : "";
    public static bool IsLocal(string? uri) => uri is not null && uri.StartsWith(LocalPrefix, StringComparison.Ordinal);
}

/// <summary>The <c>browse-section:</c> drill route (paged through <c>browseSection</c>, whatever its uri looks like).</summary>
public static class BrowseSectionRoutes
{
    public const string Prefix = "browse-section:";
    public static string Page(string sectionUri) => Prefix + sectionUri;
    public static bool Is(string route) => route.StartsWith(Prefix, StringComparison.Ordinal);
    public static string UriOf(string route) => Is(route) ? route[Prefix.Length..] : "";
}

/// <summary>The infinite-scroll grammar's pure half (0.2.9 <c>HomeSectionAppendPreloader.NearTailWatch</c>, :46-54).</summary>
public static class HomeNearTail
{
    /// <summary>The scroll-geometry projection key: the offset floored to 24 px, XOR the content height floored to 48 px —
    /// so an append's own growth re-evaluates nearness.</summary>
    public static long Project(float offsetY, float viewportH, float contentH)
        => ((long)(offsetY / 24f) << 20) ^ (long)(contentH / 48f);

    /// <summary>Within 1.5 viewports of the content end.</summary>
    public static bool IsNear(float offsetY, float viewportH, float contentH)
        => offsetY + viewportH >= contentH - 1.5f * viewportH;
}

// ══ 11. THE CHARTS DECK (0.2.9 HomeBrowseCards.cs — the browse → home boundary) ══════════════════════════════════════

/// <summary>The Charts Fold deck over the five chart section rows.</summary>
public static class HomeBrowseCards
{
    /// <summary>Three blank Fold tiles — the Charts row's shimmer seed (title a single space, never shown).</summary>
    public static readonly IReadOnlyList<HomeSectionView> ChartDeckSeed = [BlankFold(0), BlankFold(1), BlankFold(2)];

    static HomeSectionView BlankFold(int i) => new(Table.None, null, " ", null,
        [HomeCard.Blank(HomeCardKind.Playlist, 900 + i * 3), HomeCard.Blank(HomeCardKind.Playlist, 901 + i * 3),
         HomeCard.Blank(HomeCardKind.Playlist, 902 + i * 3)], 3, 3);

    /// <summary>Ask every chart section row (as BROWSE sections) — the deck's demand.</summary>
    public static void EnsureChartDeck()
    {
        var uris = ChartSections.All;
        for (int i = 0; i < uris.Count; i++) Home.EnsureSection(Entities.BrowseSection(uris[i]), browse: true);
    }

    static Scope? s_scope;
    static ulong s_key = ulong.MaxValue;
    static bool s_live;
    static HomeLoad s_state;
    static IReadOnlyList<HomeSectionView> s_deck = Array.Empty<HomeSectionView>();

    /// <summary>One section view per <c>ChartSections.All</c> uri that answered with cards, Featured first; later null or
    /// card-less sections are omitted. <paramref name="state"/>: <see cref="HomeLoad.Pending"/> while any section is
    /// unanswered (in flight or not yet asked) and Featured has not failed; <see cref="HomeLoad.Failed"/> when Featured's
    /// ask concluded without an answer and <paramref name="hasLiveCatalog"/> (0.2.9's fail-loud throw);
    /// <see cref="HomeLoad.Ready"/> otherwise — an EMPTY deck when there is no live catalog and Featured never answered.
    /// Memoized on every row's Version + CardVersion. UI thread only.</summary>
    public static IReadOnlyList<HomeSectionView> ChartDeck(bool hasLiveCatalog, out HomeLoad state)
    {
        var uris = ChartSections.All;
        var scope = Entities.Current;
        var t = scope.Sections;
        ulong key = 14695981039346656037UL;
        for (int i = 0; i < uris.Count; i++)
        {
            var s = Entities.BrowseSection(uris[i]);
            key = (key ^ s.Version) * 1099511628211UL;
            key = (key ^ s.CardVersion) * 1099511628211UL;
            key = (key ^ t.Inflight[s.Slot]) * 1099511628211UL;
            key = (key ^ t.Asked[s.Slot]) * 1099511628211UL;
        }
        if (ReferenceEquals(scope, s_scope) && key == s_key && hasLiveCatalog == s_live) { state = s_state; return s_deck; }

        var featured = Entities.BrowseSection(uris[0]);
        bool featuredKnown = featured.Knows(SectionFields.Identity);
        bool featuredConcluded = !featuredKnown && t.Inflight[featured.Slot] == 0
                                 && (t.Asked[featured.Slot] & (uint)SectionFields.Identity) != 0;
        IReadOnlyList<HomeSectionView> deck = Array.Empty<HomeSectionView>();

        if (!featuredKnown)
        {
            state = featuredConcluded ? (hasLiveCatalog ? HomeLoad.Failed : HomeLoad.Ready)
                  : hasLiveCatalog ? HomeLoad.Pending : HomeLoad.Ready;
        }
        else
        {
            bool pending = false;
            var list = new List<HomeSectionView>(uris.Count);
            for (int i = 0; i < uris.Count; i++)
            {
                var s = Entities.BrowseSection(uris[i]);
                if (!s.Knows(SectionFields.Identity))
                {
                    if (t.Inflight[s.Slot] != 0 || (t.Asked[s.Slot] & (uint)SectionFields.Identity) == 0) pending = true;
                    continue;
                }
                var view = HomeSectionView.Of(s);
                if (view.Cards.Count > 0) list.Add(view);
            }
            state = pending && hasLiveCatalog ? HomeLoad.Pending : HomeLoad.Ready;
            deck = list;
        }

        s_scope = scope;
        s_key = key;
        s_live = hasLiveCatalog;
        s_state = state;
        s_deck = deck;
        return deck;
    }

    /// <summary>A browse card's kind from its uri's kind; anything the parser cannot name reads as a playlist (deliberate).</summary>
    public static HomeCardKind KindOf(EntityKind kind) => kind switch
    {
        EntityKind.Artist => HomeCardKind.Artist,
        EntityKind.Album => HomeCardKind.Album,
        EntityKind.Show => HomeCardKind.Podcast,
        EntityKind.Episode => HomeCardKind.Episode,
        EntityKind.Track => HomeCardKind.Track,
        _ => HomeCardKind.Playlist,
    };
}

// ══ 12. CARD NAVIGATION — the CORE half (0.2.9 HomeSectionNavigation.cs; the UI half is Home.UI.cs) ═══════════════════

public static partial class HomeCardNav
{
    /// <summary>Ch 12 §0.19: a BROWSE section of exactly one card opens that card, never a one-tile section page. A Home
    /// section has no such rule.</summary>
    public static bool OneCardOpensCard(int cardCount, bool browse) => browse && cardCount == 1;

    /// <summary>Where a card goes: Liked → liked; artist / album / show (podcast and audiobook) / playlist → its page with
    /// the title as the frame-one arg; a track or an episode PLAYS instead (<see cref="Shell.Route.None"/>). Composed by
    /// <see cref="Shell.For"/> — the one "go to this thing" composer — so a Home card and a sidebar row mint the same key,
    /// and the arg is interned by the shell rather than borrowed from a column that a later answer may release.</summary>
    public static Shell.Route RouteFor(in HomeCard card)
    {
        if (card.IsBlank || HomeCardPlayRouting.PlaysAsItem(card.Kind) || card.Id.IsEmpty) return Shell.Route.None;
        return Shell.For(new EntityUri(card.Id), card.Title);
    }

    /// <summary>A section's drill route — <c>browse-section:</c> or <c>home-section:</c> by the CALLER's family, with the
    /// section title as the arg so the masthead paints on frame one. <see cref="Shell.Route.None"/> for a view with no
    /// row (a seed tile); a section with no uri has no route in 0.3 (0.2.9 minted a <c>wavee:local:</c> preview id, which
    /// the 0.3 uri parser reads as a LOCAL-provider entity).</summary>
    public static Shell.Route SectionRoute(HomeSectionView s, bool browse)
    {
        if (s.Slot <= Table.None || s.Slot >= Entities.Current.Sections.Count) return Shell.Route.None;
        var section = new Section(s.Slot);
        if (section.Id.IsEmpty) return Shell.Route.None;
        var title = s.Title.AsSpan().Trim();
        return new Shell.Route(browse ? Shell.RouteKind.BrowseSection : Shell.RouteKind.HomeSection,
            new EntityUri(section.Id), title.IsEmpty ? StringId.Empty : Entities.Strings.Intern(title));
    }
}

// ══ 13. THE LAYOUT DOCUMENT (0.2.9 Wavee.Core/Home/HomeLayoutModel.cs + HomeLayoutReducer.cs + HomeLayoutCommands.cs) ═

/// <summary>One authored Home module. Kinds are persisted as strings — append only, never renumber.</summary>
public sealed record HomeModuleSpec(HomeGroupKind Kind, bool Hidden = false);

/// <summary>What home-layout.json carries: per-module visibility + order over the FIXED landing modules, plus an ordered
/// deck-id list reserved for dynamic section-deck customization.</summary>
public sealed record HomeLayoutDoc(IReadOnlyList<HomeModuleSpec> Modules, IReadOnlyList<string>? DeckOrder = null)
{
    public static readonly HomeLayoutDoc Empty = new(Array.Empty<HomeModuleSpec>());

    /// <summary>Every fixed landing module visible, in the prototype's order. First run and Reset land here.</summary>
    public static HomeLayoutDoc Default { get; } = HomeLayoutModules.BuildDefault();

    public IReadOnlyList<string> DeckList => DeckOrder ?? Array.Empty<string>();

    public int ModuleCount => Modules.Count;

    /// <summary>True when this kind is authored hidden; a kind the document never mentioned is visible.</summary>
    public bool IsHidden(HomeGroupKind kind)
    {
        for (int i = 0; i < Modules.Count; i++) if (Modules[i].Kind == kind) return Modules[i].Hidden;
        return false;
    }

    public int IndexOf(HomeGroupKind kind)
    {
        for (int i = 0; i < Modules.Count; i++) if (Modules[i].Kind == kind) return i;
        return -1;
    }

    /// <summary>Fixed landing kinds left visible, in authored order.</summary>
    public IReadOnlyList<HomeGroupKind> VisibleFixedModules()
    {
        var list = new List<HomeGroupKind>(Modules.Count);
        for (int i = 0; i < Modules.Count; i++)
        {
            var m = Modules[i];
            if (!m.Hidden && HomeLayoutModules.IsFixedLanding(m.Kind)) list.Add(m.Kind);
        }
        return list;
    }
}

/// <summary>Per-kind facts the reducer, the wire, the projection and the customizer all need in ONE place.</summary>
public static class HomeLayoutModules
{
    /// <summary>The FIXED landing modules, in the prototype's designed rhythm.</summary>
    public static readonly HomeGroupKind[] DefaultOrder =
    [
        HomeGroupKind.Hero,
        HomeGroupKind.WeeklyPair,
        HomeGroupKind.QuickGrid,
        HomeGroupKind.Recents,
        HomeGroupKind.MixBand,
        HomeGroupKind.ChipCards,
        HomeGroupKind.RadioDial,
        HomeGroupKind.QueueList,
        HomeGroupKind.RatedShelf,
        HomeGroupKind.PodcastShelf,
        HomeGroupKind.Featured,
        HomeGroupKind.DiscoverFeed,
    ];

    public static HomeLayoutDoc BuildDefault()
    {
        var modules = new HomeModuleSpec[DefaultOrder.Length];
        for (int i = 0; i < modules.Length; i++) modules[i] = new HomeModuleSpec(DefaultOrder[i]);
        return new HomeLayoutDoc(modules);
    }

    public static bool IsFixedLanding(HomeGroupKind kind)
    {
        var all = DefaultOrder;
        for (int i = 0; i < all.Length; i++) if (all[i] == kind) return true;
        return false;
    }

    /// <summary>Wire kind strings. Persisted — never rename one.</summary>
    public static string KindName(HomeGroupKind kind) => kind switch
    {
        HomeGroupKind.Hero => "hero",
        HomeGroupKind.QuickGrid => "quickGrid",
        HomeGroupKind.Shelf => "shelf",
        HomeGroupKind.Featured => "featured",
        HomeGroupKind.MixBand => "mixBand",
        HomeGroupKind.WeeklyPair => "weeklyPair",
        HomeGroupKind.ChipCards => "chipCards",
        HomeGroupKind.RadioDial => "radioDial",
        HomeGroupKind.RatedShelf => "ratedShelf",
        HomeGroupKind.QueueList => "queueList",
        HomeGroupKind.DiscoverFeed => "discoverFeed",
        HomeGroupKind.Recents => "recents",
        HomeGroupKind.Topic => "topic",
        HomeGroupKind.SectionEntry => "sectionEntry",
        HomeGroupKind.PodcastShelf => "podcastShelf",
        _ => "shelf",
    };

    /// <summary>Parse a wire kind; false for a kind THIS build does not know (the caller carries the raw blob).</summary>
    public static bool TryParseKind(string? s, out HomeGroupKind kind)
    {
        switch (s)
        {
            case "hero": kind = HomeGroupKind.Hero; return true;
            case "quickGrid": kind = HomeGroupKind.QuickGrid; return true;
            case "shelf": kind = HomeGroupKind.Shelf; return true;
            case "featured": kind = HomeGroupKind.Featured; return true;
            case "mixBand": kind = HomeGroupKind.MixBand; return true;
            case "weeklyPair": kind = HomeGroupKind.WeeklyPair; return true;
            case "chipCards": kind = HomeGroupKind.ChipCards; return true;
            case "radioDial": kind = HomeGroupKind.RadioDial; return true;
            case "ratedShelf": kind = HomeGroupKind.RatedShelf; return true;
            case "queueList": kind = HomeGroupKind.QueueList; return true;
            case "discoverFeed": kind = HomeGroupKind.DiscoverFeed; return true;
            case "recents": kind = HomeGroupKind.Recents; return true;
            case "topic": kind = HomeGroupKind.Topic; return true;
            case "sectionEntry": kind = HomeGroupKind.SectionEntry; return true;
            case "podcastShelf": kind = HomeGroupKind.PodcastShelf; return true;
            default: kind = HomeGroupKind.Shelf; return false;
        }
    }
}

/// <summary>Undo/redo label loc keys, one per command shape (unrendered in 0.2.9 and 0.3 — ch 12 §0.20).</summary>
public static class HomeLayoutUndoLabels
{
    public const string HideModule = "home.customizer.undo.hide";
    public const string ShowModule = "home.customizer.undo.show";
    public const string MoveModule = "home.customizer.undo.move";
    public const string Reset = "home.customizer.undo.reset";
}

/// <summary>The only way a <see cref="HomeLayoutDoc"/> changes. In-memory only, never serialized.</summary>
public abstract record HomeLayoutCommand
{
    public abstract string LabelLocKey { get; }
}

public sealed record SetHomeModuleHidden(HomeGroupKind Kind, bool Hidden) : HomeLayoutCommand
{
    public override string LabelLocKey => Hidden ? HomeLayoutUndoLabels.HideModule : HomeLayoutUndoLabels.ShowModule;
}

/// <summary>Reorder a module. <paramref name="ToIndex"/> is interpreted AFTER the removal (the Reorderable contract).</summary>
public sealed record MoveHomeModule(int FromIndex, int ToIndex) : HomeLayoutCommand
{
    public override string LabelLocKey => HomeLayoutUndoLabels.MoveModule;
}

public sealed record ResetHomeLayout() : HomeLayoutCommand
{
    public override string LabelLocKey => HomeLayoutUndoLabels.Reset;
}

/// <summary>The reducer's verdict. <c>Changed == false</c> ⇒ the caller does NOT autosave.</summary>
public readonly record struct HomeLayoutCommandResult(HomeLayoutDoc Layout, bool Changed, HomeLayoutRejectReason Reason)
{
    public static HomeLayoutCommandResult Ok(HomeLayoutDoc layout) => new(layout, true, HomeLayoutRejectReason.None);
    public static HomeLayoutCommandResult Reject(HomeLayoutDoc layout, HomeLayoutRejectReason reason) => new(layout, false, reason);
}

/// <summary>Why a command changed nothing. Append only.</summary>
public enum HomeLayoutRejectReason : byte
{
    None = 0,
    UnknownModule = 1,
    NoChange = 2,
    CapReached = 3,
}

/// <summary>The PURE layout reducer: one entry point, one verdict, zero side effects; rejections are DATA.</summary>
public static class HomeLayoutReducer
{
    public const int MaxModules = 24;

    public static HomeLayoutCommandResult Apply(HomeLayoutDoc layout, HomeLayoutCommand command)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(command);
        return command switch
        {
            SetHomeModuleHidden c => DoSetHidden(layout, c),
            MoveHomeModule c => DoMove(layout, c),
            ResetHomeLayout => HomeLayoutCommandResult.Ok(HomeLayoutDoc.Default),
            _ => HomeLayoutCommandResult.Reject(layout, HomeLayoutRejectReason.NoChange),
        };
    }

    static HomeLayoutCommandResult DoSetHidden(HomeLayoutDoc layout, SetHomeModuleHidden c)
    {
        if (!HomeLayoutModules.IsFixedLanding(c.Kind)) return HomeLayoutCommandResult.Reject(layout, HomeLayoutRejectReason.UnknownModule);
        int at = layout.IndexOf(c.Kind);
        if (at < 0)
        {
            if (layout.ModuleCount >= MaxModules) return HomeLayoutCommandResult.Reject(layout, HomeLayoutRejectReason.CapReached);
            var grown = new List<HomeModuleSpec>(layout.Modules) { new(c.Kind, c.Hidden) };
            return HomeLayoutCommandResult.Ok(layout with { Modules = grown });
        }
        var current = layout.Modules[at];
        if (current.Hidden == c.Hidden) return HomeLayoutCommandResult.Reject(layout, HomeLayoutRejectReason.NoChange);
        var next = new List<HomeModuleSpec>(layout.Modules);
        next[at] = current with { Hidden = c.Hidden };
        return HomeLayoutCommandResult.Ok(layout with { Modules = next });
    }

    static HomeLayoutCommandResult DoMove(HomeLayoutDoc layout, MoveHomeModule c)
    {
        var modules = layout.Modules;
        if (c.FromIndex < 0 || c.FromIndex >= modules.Count) return HomeLayoutCommandResult.Reject(layout, HomeLayoutRejectReason.UnknownModule);
        var items = new List<HomeModuleSpec>(modules);
        var moving = items[c.FromIndex];
        items.RemoveAt(c.FromIndex);
        int at = Math.Clamp(c.ToIndex, 0, items.Count);
        if (at == c.FromIndex) return HomeLayoutCommandResult.Reject(layout, HomeLayoutRejectReason.NoChange);
        items.Insert(at, moving);
        return HomeLayoutCommandResult.Ok(layout with { Modules = items });
    }
}

// ══ 14. THE LAYOUT WIRE (0.2.9 Features/Home/Persistence/HomeLayoutDoc.cs) ════════════════════════════════════════════

/// <summary>The versioned home-layout.json document. No polymorphic JSON; unknown members / kinds survive a round trip.</summary>
public sealed class HomeLayoutDocDto
{
    public int Version { get; set; }
    public long UpdatedAtMs { get; set; }
    public string? AppVersion { get; set; }
    public HomeModuleDto[]? Modules { get; set; }
    /// <summary>Ordered dynamic section-deck ids (on the schema, unedited by v1).</summary>
    public string[]? DeckOrder { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class HomeModuleDto
{
    public string? Kind { get; set; }
    public bool? Hidden { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(HomeLayoutDocDto))]
public sealed partial class HomeLayoutJsonCtx : JsonSerializerContext { }

/// <summary>The forward-compatibility carry <see cref="HomeLayoutWire.Read"/> produces: unknown kinds and unknown members
/// on known modules are re-emitted on the next save.</summary>
public sealed class HomeLayoutWireCarry
{
    public static readonly HomeLayoutWireCarry Empty = new();

    internal readonly Dictionary<string, HomeModuleDto> Raw = new(StringComparer.Ordinal);
    internal readonly List<KeyValuePair<int, HomeModuleDto>> Unknown = new();
    internal Dictionary<string, JsonElement>? DocExtra;

    public int UnknownModuleCount => Unknown.Count;
    public bool IsEmpty => Raw.Count == 0 && Unknown.Count == 0 && DocExtra is null;

    public void CaptureDoc(HomeLayoutDocDto? doc) => DocExtra = doc?.Extra;

    public void ReattachDoc(HomeLayoutDocDto? doc)
    {
        if (doc is null) return;
        doc.Extra ??= DocExtra;
    }
}

public readonly record struct HomeLayoutRead(HomeLayoutDoc Layout, HomeLayoutWireCarry Carry);

public static class HomeLayoutWire
{
    public static HomeLayoutRead Read(HomeLayoutDocDto? dto)
    {
        var carry = new HomeLayoutWireCarry();
        if (dto is null) return new HomeLayoutRead(HomeLayoutDoc.Default, carry);
        carry.DocExtra = dto.Extra;

        var modules = new List<HomeModuleSpec>(dto.Modules?.Length ?? 0);
        var seen = new HashSet<HomeGroupKind>();
        var raw = dto.Modules;
        if (raw is not null)
            for (int i = 0; i < raw.Length; i++)
            {
                var m = raw[i];
                if (m is null) continue;
                if (!HomeLayoutModules.TryParseKind(m.Kind, out var kind) || !HomeLayoutModules.IsFixedLanding(kind))
                {
                    carry.Unknown.Add(new KeyValuePair<int, HomeModuleDto>(i, m));
                    continue;
                }
                if (!seen.Add(kind)) continue;
                if (!string.IsNullOrEmpty(m.Kind) && !carry.Raw.ContainsKey(m.Kind)) carry.Raw[m.Kind] = m;
                modules.Add(new HomeModuleSpec(kind, m.Hidden ?? false));
            }

        // A kind this build knows that the file never mentioned is APPENDED visible (preserve-don't-destroy).
        var defaults = HomeLayoutModules.DefaultOrder;
        for (int i = 0; i < defaults.Length; i++)
            if (seen.Add(defaults[i])) modules.Add(new HomeModuleSpec(defaults[i]));

        IReadOnlyList<string>? deck = null;
        if (dto.DeckOrder is { Length: > 0 } ids)
        {
            var list = new List<string>(ids.Length);
            var deckSeen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < ids.Length; i++)
                if (ids[i] is { Length: > 0 } id && deckSeen.Add(id)) list.Add(id);
            if (list.Count > 0) deck = list;
        }

        return new HomeLayoutRead(new HomeLayoutDoc(modules, deck), carry);
    }

    public static HomeLayoutDocDto Write(HomeLayoutDoc layout, HomeLayoutWireCarry? carry)
    {
        carry ??= HomeLayoutWireCarry.Empty;
        var list = new List<HomeModuleDto>(layout.Modules.Count + carry.Unknown.Count);
        for (int i = 0; i < layout.Modules.Count; i++)
        {
            var spec = layout.Modules[i];
            string name = HomeLayoutModules.KindName(spec.Kind);
            var dto = new HomeModuleDto { Kind = name, Hidden = spec.Hidden ? true : null };
            if (carry.Raw.TryGetValue(name, out var raw)) dto.Extra ??= raw.Extra;
            list.Add(dto);
        }

        if (carry.Unknown.Count > 0)
        {
            var pending = new List<KeyValuePair<int, HomeModuleDto>>(carry.Unknown);
            pending.Sort(static (a, b) => a.Key.CompareTo(b.Key));
            for (int i = 0; i < pending.Count; i++)
            {
                int at = Math.Clamp(pending[i].Key, 0, list.Count);
                list.Insert(at, pending[i].Value);
            }
        }

        string[]? deck = null;
        if (layout.DeckOrder is { Count: > 0 } ids)
        {
            deck = new string[ids.Count];
            for (int i = 0; i < deck.Length; i++) deck[i] = ids[i];
        }

        return new HomeLayoutDocDto
        {
            Version = HomeLayoutStore.CurrentVersion,
            Modules = list.ToArray(),
            DeckOrder = deck,
            Extra = carry.DocExtra,
        };
    }
}

// ══ 15. THE CUSTOMIZER'S PURE HALF (moved out of Home.Customizer.cs, tested in HomeLayoutTests) ══════════════════════

public readonly partial struct Home
{
    /// <summary>Ch 12 W16 + §9 trap 4: the load-fault banner stands while the load faulted OR the store still blocks
    /// writes (a "Start fresh" whose rename threw clears the fault but never unblocks — 0.2.9 hid the banner and then
    /// silently dropped every later edit), until the user dismisses it for this mount.</summary>
    public static bool CustomizerShowsFaultBanner(HomeLayoutLoadFault fault, bool writesBlocked, bool dismissed)
        => !dismissed && (fault != HomeLayoutLoadFault.None || writesBlocked);

    /// <summary>0.2.9 <c>HomeCustomizeLabels.Of</c>, verbatim: the row label per fixed landing kind; the two kinds with no
    /// module title take their own customizer strings, and anything else its wire name.</summary>
    public static string CustomizerLabelOf(HomeGroupKind kind, HomeModuleTitles t, string hero, string weeklyPair) => kind switch
    {
        HomeGroupKind.Hero => hero,
        HomeGroupKind.WeeklyPair => weeklyPair,
        HomeGroupKind.QuickGrid => t.JumpBackIn,
        HomeGroupKind.Recents => t.Recents,
        HomeGroupKind.MixBand => t.MadeForYou,
        HomeGroupKind.ChipCards => t.TopMixes,
        HomeGroupKind.RadioDial => t.Radio,
        HomeGroupKind.QueueList => t.UpNext,
        HomeGroupKind.RatedShelf => t.Audiobooks,
        HomeGroupKind.PodcastShelf => t.Podcasts,
        HomeGroupKind.Featured => t.EditorsPicks,
        HomeGroupKind.DiscoverFeed => t.BecauseYouListened,
        _ => HomeLayoutModules.KindName(kind),
    };
}

// ══ THE LANDING'S SMALL RULES (moved to CORE from the UI files at WP-5.P reconciliation) ════════════════════════════

/// <summary>Which greeting the page says: the server's own label, or a local-clock bucket.</summary>
public enum HomeGreetingWord : byte { Server, Morning, Afternoon, Evening }

/// <summary>The landing page's arithmetic and text decisions that the renderer AND the estimators both read, so a test
/// pins them instead of a component body (ch 10 §9 defects #1, #4, #5; ch 10 W3, W5).</summary>
public static class HomeLandingRules
{
    /// <summary>The module row width a shell receives for a list cross size: capped at the page max, then the two 36-DIP
    /// gutters off (a zero/unmeasured cross reads the 1,100 fallback). The renderer's module GAP is computed from THIS,
    /// exactly as the estimators compute it — ch 10 §9 defect #1, fixed (0.2.9 took the gap from the outer width).</summary>
    public static float Available(float cross)
        => MathF.Max(1f, MathF.Min(cross > 1f ? cross : HomeModuleLayout.FallbackWidth, Design.Size.PageMaxW) - 2f * Spacing.PageWide);

    /// <summary>The greeting word: the server's transformed label wins (localized for the ACCOUNT and bucketed against the
    /// request's timezone); the local clock (&lt; 5 evening, &lt; 12 morning, &lt; 18 afternoon, else evening) serves only a
    /// source that published none (ch 10 §9, 0.2.9 <c>HomePage.cs:1020-1033</c>).</summary>
    public static HomeGreetingWord GreetingWord(string? serverGreeting, int localHour)
        => !string.IsNullOrWhiteSpace(serverGreeting) ? HomeGreetingWord.Server
         : localHour < 5 ? HomeGreetingWord.Evening
         : localHour < 12 ? HomeGreetingWord.Morning
         : localHour < 18 ? HomeGreetingWord.Afternoon
         : HomeGreetingWord.Evening;

    /// <summary>Is <paramref name="name"/> an account HANDLE rather than a display name (≥ 20 chars, no space)? Greeting
    /// someone by a user-id hash is worse than not greeting them by name (ch 10 W3).</summary>
    public static bool LooksLikeHandle(string name) => name.Length >= 20 && name.IndexOf(' ') < 0;

    /// <summary>The editorial destination's tiers (0.2.9 <c>ConcertLayout.WideEditorial</c>): (height, art fraction, art
    /// min, art max, padding, subtitle lines). The tail row's estimate reads the same height as its render.</summary>
    public static (float Height, float ArtFraction, float ArtMin, float ArtMax, float Padding, int Lines) WideEditorial(float width)
        => width >= 900f ? (288f, 0.38f, 280f, 420f, 28f, 3)
         : width >= 600f ? (240f, 0.42f, 220f, 360f, 24f, 2)
         : (220f, 0.55f, 180f, 280f, 20f, 2);

    /// <summary>The tail's two destinations and the 20-DIP gap between them, at a module width.</summary>
    public static float TailExtent(float available) => 2f * WideEditorial(available).Height + Spacing.XL;

    /// <summary>The greeting / chip row's content height from the SAME arms the greeting block composes: the standalone
    /// greeting (84) only without a hero (defect #4), the strip (40) only with chips (defect #5), and never less than
    /// the 28-DIP customize entry.</summary>
    public static float ChipsContent(bool hasHero, int chips)
    {
        const float Greeting = 84f, Strip = 40f, Entry = 28f;
        float body = hasHero
            ? (chips > 0 ? Strip : 0f)
            : (chips > 0 ? Greeting + Spacing.M + Strip : Greeting);
        return MathF.Max(body, Entry);
    }
}
