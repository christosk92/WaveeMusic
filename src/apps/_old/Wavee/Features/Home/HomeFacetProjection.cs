using System;
using System.Collections.Generic;
using Wavee.Core;

namespace Wavee;

/// <summary>What one row of a FACETED home page is. A facet is not the landing page with a filter applied: the landing
/// is an authored rhythm (one module per kind, plus app chrome), and applying it to "Podcasts" merged four separately
/// titled show shelves into one module and still offered "Your top artists". A facet is the SERVER's ordered list of
/// sections, so the row kinds here name shelf shapes, not authored appointments.</summary>
internal enum HomeFacetRowKind : byte { Hero, Recents, Podcasts, Audiobooks, Episodes, Feed, Shelf }

/// <summary>One rendered row of a facet page: the shape, the group the module shell renders, and the SOURCE section
/// that row drills into (null when the row is not one section — the coalesced baseline feed).</summary>
internal sealed record HomeFacetRow(HomeFacetRowKind Kind, HomeGroup Group, HomeSection? Section);

/// <summary>The faceted Home page, as a pure function of (feed, titles). Engine-free on purpose — System +
/// <see cref="Wavee.Core"/> only — so the rule that the eye actually reads (every titled server section survives, in
/// server order, wearing its own title) is unit-testable without a page, a service or a window.
///
/// <para>The walk is over <see cref="HomeFeed.Sections"/>, the lossless per-section ledger, and never over the composed
/// groups: the groups are a PRESENTATION view that splits one section per card kind and merges same-kind sections, which
/// is exactly the flattening a facet must not do. The groups are still consulted — by URI — to learn what the composer
/// decided a section IS (a spotlight hero, a baseline recommendation, the recently-played rail), because that decision
/// reads response fields (<c>__typename</c>) the section ledger does not carry.</para>
///
/// <para>The one thing that IS coalesced is a run of CONSECUTIVE baseline sections. Those are the single-card
/// "because you listened to X" recommendations: twenty of them in a row is twenty one-card shelves, which is not a page.
/// A run folds into one paged discover feed wearing the app's own copy; a run interrupted by a titled section closes,
/// so two runs stay two rows and the server's order is never rewritten.</para></summary>
internal static class HomeFacetProjection
{
    static readonly HomeGroup[] NoGroups = Array.Empty<HomeGroup>();

    public static IReadOnlyList<HomeFacetRow> Rows(HomeFeed feed, HomeModuleTitles titles)
    {
        var rows = new List<HomeFacetRow>();
        var run = new List<HomeCard>();                                   // the OPEN baseline run
        var runSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void CloseRun()
        {
            if (run.Count == 0) return;
            rows.Add(new HomeFacetRow(HomeFacetRowKind.Feed,
                new HomeGroup(HomeGroupKind.DiscoverFeed, titles.BecauseYouListened, run.ToArray(),
                    TotalCount: run.Count),
                null));
            run.Clear();
            runSeen.Clear();
        }

        void ExtendRun(IReadOnlyList<HomeCard> cards)
        {
            for (int i = 0; i < cards.Count; i++)
                if (runSeen.Add(cards[i].Uri)) run.Add(cards[i]);
        }

        var sections = feed.Sections;
        if (sections is { Count: > 0 })
        {
            var byKey = GroupsByKey(feed.Groups);
            for (int i = 0; i < sections.Count; i++)
            {
                var section = sections[i];
                if (section.Cards.Count == 0) continue;                   // an empty section is not a row
                var cards = Unique(section.Cards);
                if (cards.Count == 0) continue;
                IReadOnlyList<HomeGroup> groups =
                    byKey.TryGetValue(Key(section.Uri, section.Title), out var found) ? found : NoGroups;

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
            // No ledger (a source that publishes groups only, or a seed). The groups ARE the order; each one is its own
            // row, and there is no section to drill into.
            var groups = feed.Groups;
            for (int i = 0; i < groups.Count; i++)
            {
                var group = groups[i];
                if (group.Cards.Count == 0) continue;
                if (group.Kind == HomeGroupKind.DiscoverFeed) { ExtendRun(group.Cards); continue; }
                CloseRun();
                var one = new[] { group };
                rows.Add(new HomeFacetRow(
                    group.Kind == HomeGroupKind.Hero && group.Cards.Count == 1
                        ? HomeFacetRowKind.Hero
                        : Classify(one, group.Cards),
                    group, null));
            }
        }

        CloseRun();
        return rows;
    }

    /// <summary>What the composer said this section is, read off the groups that carry its URI. The card-kind fallback
    /// is what makes an UNKNOWN section type still render as something honest rather than being dropped.</summary>
    static HomeFacetRowKind Classify(IReadOnlyList<HomeGroup> groups, IReadOnlyList<HomeCard> cards)
    {
        for (int i = 0; i < groups.Count; i++)
            if (groups[i].Kind == HomeGroupKind.Recents) return HomeFacetRowKind.Recents;
        if (All(cards, HomeCardKind.Podcast)) return HomeFacetRowKind.Podcasts;
        if (All(cards, HomeCardKind.Audiobook)) return HomeFacetRowKind.Audiobooks;
        if (All(cards, HomeCardKind.Episode)) return HomeFacetRowKind.Episodes;
        return HomeFacetRowKind.Shelf;
    }

    /// <summary>The module shell each row shape drives. A generic section is the source-neutral plain
    /// <see cref="HomeGroupKind.Shelf"/> — the one kind the landing never emits, precisely because it is the shape a
    /// section that named no shape gets.</summary>
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
        for (int i = 0; i < groups.Count; i++)
            if (groups[i].Kind == HomeGroupKind.DiscoverFeed) return true;
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
        for (int i = 0; i < cards.Count; i++)
            if (cards[i].Kind != kind) return false;
        return true;
    }

    static List<HomeCard> Unique(IReadOnlyList<HomeCard> cards)
    {
        var list = new List<HomeCard>(cards.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < cards.Count; i++)
            if (seen.Add(cards[i].Uri)) list.Add(cards[i]);
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

    /// <summary>A section's identity as its composed groups carry it. The URI is the real link (the composer copies
    /// <c>section.Uri</c> onto every group it emits for that section); the title is the fallback for the sections that
    /// arrive without one, where matching on a null URI would otherwise pool every such section together.</summary>
    static string Key(string? uri, string? title)
        => uri is { Length: > 0 } u ? u : "title:" + (title ?? "");
}
