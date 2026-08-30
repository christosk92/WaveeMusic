using System;
using System.Collections.Generic;
using Wavee.Core;
using Wavee.Core.Home;

namespace Wavee;

/// <summary>The page's rows, in the prototype's order unless a <see cref="Wavee.Core.Home.HomeLayoutDoc"/> reorders
/// the authored modules. Chrome rows (Chips / Artists / Timeline / Charts / Sections / Tail) are not user-orderable in
/// v1. <see cref="Queue"/> / <see cref="Books"/> exist so a split pair that the user pulled apart does not render twice
/// inside <see cref="EpisodesAndBooks"/>.</summary>
enum HomeRow : byte
{
    Chips, Hero, Weekly, Quick, Recents, MixBand, Artists, ChipCards, Radio, EpisodesAndBooks,
    Queue, Books,
    Podcasts, Timeline, Sections, Editorial, Feed, Tail,
    // Charts is CHROME — not a HomeGroupKind, not in home-layout.json v1, not user-hideable. Appended rather than
    // inserted: the enum is `: byte` and is never persisted, so its ordinal position carries no compatibility
    // contract and a new member is free to land wherever reads best.
    Charts,
}

/// <summary>One app-authored Home module plus the source section that can satisfy a server-side drill-in. The
/// module is a landing-page projection only: the source ledger remains untouched and owns drill-in/accounting.</summary>
internal sealed record HomeLandingModule(HomeGroup Group, HomeSection? PrimarySection);

/// <summary>The finite prototype rhythm projected from a lossless <see cref="HomeFeed"/>. Each visual kind has at most
/// one landing module; identified source sections not already represented by one of those modules remain available in
/// <see cref="Sections"/>.</summary>
internal sealed class HomeLanding
{
    readonly HomeLandingModule?[] _modules = new HomeLandingModule?[Enum.GetValues<HomeGroupKind>().Length];

    public IReadOnlyList<HomeSection> Sections { get; private set; } = Array.Empty<HomeSection>();

    /// <summary>The page's rows AFTER hide + reorder. Chrome rows (chips / artists / timeline / sections / tail)
    /// stay at their designed anchors; a hidden module is omitted so it cannot leave a hole.</summary>
    public IReadOnlyList<HomeRow> Rows { get; private set; } = HomeLandingProjection.DefaultRows;

    public HomeLandingModule? Get(HomeGroupKind kind) => _modules[(int)kind];
    internal void Set(HomeGroupKind kind, HomeLandingModule module) => _modules[(int)kind] = module;
    internal void Clear(HomeGroupKind kind) => _modules[(int)kind] = null;
    internal void SetSections(IReadOnlyList<HomeSection> sections) => Sections = sections;
    internal void SetRows(IReadOnlyList<HomeRow> rows) => Rows = rows;
}

/// <summary>Pure, engine-free Home landing projection. Source groups are concatenated in feed order and de-duplicated
/// by card URI only for the landing preview; <see cref="HomeFeed.Sections"/> is never rewritten or de-duplicated by
/// card, so the same recommendation can still occur for two different server reasons on their drill pages.
/// <para>Two rules this file owns, both landing-only (the feed keeps its kinds, order and titles either way): a module
/// whose authored shape cannot be satisfied is SUPPRESSED but never eats its cards — the half-pair falls through to the
/// shapeless grid; and the shapeless grid wears a server section's label when exactly one such label feeds it.</para></summary>
internal static class HomeLandingProjection
{
    static readonly HomeGroupKind[] AggregatedKinds =
    [
        HomeGroupKind.QuickGrid, HomeGroupKind.Recents, HomeGroupKind.MixBand,
        HomeGroupKind.ChipCards, HomeGroupKind.RadioDial, HomeGroupKind.QueueList,
        HomeGroupKind.RatedShelf, HomeGroupKind.PodcastShelf, HomeGroupKind.Featured,
        HomeGroupKind.DiscoverFeed,
    ];

    /// <summary>The prototype's designed row table — chips first, tail last, chrome anchored after MixBand /
    /// Podcasts. Used when no layout is supplied and as the chrome skeleton <see cref="ApplyLayout"/> fills.</summary>
    public static readonly HomeRow[] DefaultRows =
    [
        HomeRow.Chips, HomeRow.Hero, HomeRow.Weekly, HomeRow.Quick, HomeRow.Recents, HomeRow.MixBand,
        HomeRow.Artists, HomeRow.ChipCards, HomeRow.Radio, HomeRow.EpisodesAndBooks, HomeRow.Podcasts,
        HomeRow.Timeline, HomeRow.Charts, HomeRow.Sections, HomeRow.Editorial, HomeRow.Feed, HomeRow.Tail,
    ];

    public static HomeLanding Project(HomeFeed feed, HomeModuleTitles titles)
        => Project(feed, titles, null);

    /// <summary>Project the feed, then apply hide + reorder BEFORE the page synthesizes rows. A hidden Hero is
    /// cleared (no empty slot) and <see cref="HomeLanding.Rows"/> follows the authored module order.</summary>
    public static HomeLanding Project(HomeFeed feed, HomeModuleTitles titles, HomeLayoutDoc? layout)
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
            landing.Set(HomeGroupKind.WeeklyPair,
                new HomeLandingModule(pair, PrimarySection(feed, pairSources)));
            MarkConsumed(consumedSections, pairSources);
        }

        for (int i = 0; i < AggregatedKinds.Length; i++)
        {
            var kind = AggregatedKinds[i];
            var source = Groups(feed, kind);
            // A suppressed two-up hands its one card to the shapeless grid rather than dropping it (see WeeklyPair).
            var lone = kind == HomeGroupKind.QuickGrid ? loneWeekly : null;
            if (source.Count == 0 && lone is null) continue;
            var cards = UniqueCards(source);
            // FIRST, not appended: the grid renders only its first HomeModuleLayout.QuickShown cards on the landing,
            // so appending would re-hide the very card this fallback exists to save.
            if (lone is not null && !Holds(cards, lone.Uri)) cards.Insert(0, lone);
            if (cards.Count == 0) continue;
            int total = cards.Count;
            for (int g = 0; g < source.Count; g++) total = Math.Max(total, source[g].TotalCount);
            var group = new HomeGroup(kind, Title(kind, source, titles), cards,
                TotalCount: total);
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

    /// <summary>Hide authored-off modules, then build the row table from the remaining order. Chrome rows that
    /// are not HomeGroupKind modules stay at their designed anchors (Artists after MixBand, Timeline + Sections
    /// after Podcasts). QueueList + RatedShelf collapse to <see cref="HomeRow.EpisodesAndBooks"/> when adjacent.</summary>
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
            else if (kind == HomeGroupKind.RatedShelf)
            {
                rows.Add(HomeRow.Books);
            }
            else
            {
                rows.Add(RowOf(kind));
            }

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

    static List<HomeGroup> Groups(HomeFeed feed, HomeGroupKind kind)
    {
        var result = new List<HomeGroup>(3);
        for (int i = 0; i < feed.Groups.Count; i++)
            if (feed.Groups[i].Kind == kind && feed.Groups[i].Cards.Count > 0)
                result.Add(feed.Groups[i]);
        return result;
    }

    static List<HomeCard> UniqueCards(IReadOnlyList<HomeGroup> groups)
    {
        var cards = new List<HomeCard>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int g = 0; g < groups.Count; g++)
            for (int c = 0; c < groups[g].Cards.Count; c++)
            {
                var card = groups[g].Cards[c];
                if (seen.Add(card.Uri)) cards.Add(card);
            }
        return cards;
    }

    /// <summary>The two-up module (both appointments present) or, failing that, the ONE appointment that does exist.</summary>
    static (HomeGroup? Pair, IReadOnlyList<HomeGroup> PairSources, HomeCard? Lone, HomeGroup? LoneSource)
        WeeklyPair(IReadOnlyList<HomeGroup> groups)
    {
        HomeCard? discover = null, release = null;
        HomeGroup? discoverSource = null, releaseSource = null;
        for (int g = 0; g < groups.Count; g++)
            for (int c = 0; c < groups[g].Cards.Count; c++)
            {
                var card = groups[g].Cards[c];
                switch (card.Meta?.Format)
                {
                    case "discover-weekly" when discover is null:
                        discover = card; discoverSource = groups[g]; break;
                    case "release-radar" when release is null:
                        release = card; releaseSource = groups[g]; break;
                }
            }
        // The prototype is a standing APPOINTMENT PAIR, so a singleton still gets NO two-up: half of an authored 1fr 1fr
        // row is a hole, not a module. Suppressing the module must not delete the CARD, though — the composer routes both
        // formats EXCLUSIVELY to WeeklyPair, and a young account routinely has Discover Weekly weeks before its first
        // Release Radar, so the card would otherwise reach no landing module at all. It falls through to the shapeless
        // quick grid (where a card whose format named no module lands anyway); the feed's own WeeklyPair group, its kind
        // and its ordinal are untouched. Once shown by that fallback module, its source is deliberately not duplicated
        // in the section directory.
        if (discover is not null && release is not null)
        {
            IReadOnlyList<HomeGroup> sources = ReferenceEquals(discoverSource, releaseSource)
                ? [discoverSource!]
                : [discoverSource!, releaseSource!];
            return (new HomeGroup(HomeGroupKind.WeeklyPair, null, [discover, release], TotalCount: 2),
                sources, null, null);
        }
        return (null, Array.Empty<HomeGroup>(), discover ?? release, discoverSource ?? releaseSource);
    }

    static void MarkConsumed(HashSet<string> consumed, IReadOnlyList<HomeGroup> groups)
    {
        for (int i = 0; i < groups.Count; i++)
            if (groups[i].Uri is { Length: > 0 } uri)
                consumed.Add(uri);
    }

    static bool Holds(List<HomeCard> cards, string uri)
    {
        for (int i = 0; i < cards.Count; i++)
            if (string.Equals(cards[i].Uri, uri, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static string? Title(HomeGroupKind kind, IReadOnlyList<HomeGroup> source, HomeModuleTitles titles) => kind switch
    {
        // The grid is the SHAPELESS bucket: a card lands here precisely because nothing in its content named a module,
        // so the server's own section label is the only honest explanation of the row — and the composer gives a
        // generic section's title to exactly this group when the grid is its dominant module. Every other aggregate
        // names a SHAPE the app can label ("Radio", "Podcasts"); this one cannot. So when one label, and only one,
        // feeds the grid, wear it verbatim (never matched, never re-translated); a grid fed by two differently
        // labelled sections — or by the app's own library quick picks alongside a server section — can honestly wear
        // neither and falls back to the app's copy.
        HomeGroupKind.QuickGrid => SoleTitle(source) ?? titles.JumpBackIn,
        HomeGroupKind.Recents => titles.Recents,
        // The server commonly personalizes this one ("Made For Christos"); retain that localized label when present.
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

    /// <summary>The one label carried by the contributing groups, or null when they carry none — or disagree. Blank
    /// copy is not a label: the skeleton seed titles its groups with a placeholder space, and a module must fall back to
    /// its real header there so the shimmer is derived from the silhouette the loaded page actually has.</summary>
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

    static HomeSection? PrimarySection(HomeFeed feed, IReadOnlyList<HomeGroup> groups)
    {
        HomeSection? best = null;
        int bestTotal = -1;
        if (feed.Sections is { } sections)
            for (int g = 0; g < groups.Count; g++)
            {
                var group = groups[g];
                if (group.Uri is not { Length: > 0 } uri) continue;
                for (int s = 0; s < sections.Count; s++)
                    if (string.Equals(sections[s].Uri, uri, StringComparison.Ordinal)
                        && sections[s].TotalCount > bestTotal)
                    {
                        best = sections[s];
                        bestTotal = sections[s].TotalCount;
                    }
            }
        return best;
    }

    static IReadOnlyList<HomeSection> SectionDirectory(HomeFeed feed, HashSet<string> consumed)
    {
        var result = new List<HomeSection>();
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

        // Non-composer/fake sources may omit the optional ledger. Preserve their identified groups as local drill pages.
        for (int i = 0; i < feed.Groups.Count; i++)
        {
            var group = feed.Groups[i];
            if (group.Title is not { Length: > 0 } && group.Uri is not { Length: > 0 }) continue;
            if (group.Uri is { Length: > 0 } uri && (consumed.Contains(uri) || !uris.Add(uri))) continue;
            result.Add(new HomeSection(group.Uri, group.Title, group.Subtitle, group.Cards,
                Math.Max(group.TotalCount, group.Cards.Count), group.Cards.Count));
        }
        return result;
    }

    static bool HasIdentity(HomeSection section) =>
        section.Uri is { Length: > 0 } || section.Title is { Length: > 0 };
}
