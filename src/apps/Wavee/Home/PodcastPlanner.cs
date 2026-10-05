// ── Home/PodcastPlanner.cs — the Podcasts facet's regroup rules (Wave 1, agent A2) ─────────────────────────────────
//
// Role: CORE
// Owner: A2
// Wave: 1
// Spec: docs/plans/wavee/home-redesign/06-facet-design.md §4.4 rules 1–12, §3.3 (P0–P8 layout reference)
//
// The Podcasts facet document arrives as ~30 one-off "baseline" sections (one card each, titled by a fixed server
// phrase) plus a handful of real multi-card shelves ("Your shows", "Shows you might like", a "More like {episode}"
// shelf). Spotify never regroups these server-side — the client must fold same-titled baselines into one shelf,
// pull every in-progress episode into Continue listening from wherever it lives, and drop what nobody may play.
// This class is that fold: pure, engine-free, deterministic given the section list and the clock. It knows nothing
// about the entity tables beyond the read-only `HomeCard` handle contract; it never mutates a card, only reorders
// and re-buckets copies of the input lists.
//
// Rule order matters and is NOT the zone output order: in-progress extraction (rule 2) runs before everything else
// because an in-progress episode is invisible to every other rule; small-group folding (rules 5/6) runs before the
// video top-up (rule 3) because the video pool draws from the SAME "Episodes you might like" pool those folds feed;
// the page-wide URI dedupe (rule 10) runs last, in zone-output order, because it is defined in terms of that order.

using System;
using System.Collections.Generic;
using System.Linq;
using Wavee;

namespace Wavee.HomeUi;

/// <summary>The server's fixed English phrases for the Podcasts facet's one-off baseline sections and named shelves
/// (06 §4.4). A section's <see cref="SectionInput.Title"/> is matched against these verbatim — the server always
/// emits exactly these words, only the interpolated show/person/episode name after them varies. §4.4 note: English
/// only for now (Q4 in the plan's "Remaining assumptions") — a locale whose server titles differ degrades every
/// grouped rule to <see cref="ZoneKind.EpisodeRows"/>/dropped, never to invented English.</summary>
public static class PodcastPhrases
{
    public const string NewEpisodeFrom = "New episode from";
    public const string CatchUpOnYourShows = "Catch up on your shows";
    public const string VideosYouMightLike = "Videos you might like";
    public const string SimilarToYourInterests = "Similar to your interests";
    public const string EpisodesYouMightLike = "Episodes you might like";
    public const string PopularWithListenersOf = "Popular with listeners of";
    public const string MoreEpisodesFeaturing = "More episodes featuring";
    public const string MoreLike = "More like";
    public const string YourShows = "Your shows";
    public const string ShowsYouMightLike = "Shows you might like";
}

/// <summary>The Podcasts facet's zone planner: §4.4 rules 1–12, verbatim. <see cref="Plan"/> is the only entry
/// point; everything else is a private step of the fold.</summary>
public static class PodcastPlanner
{
    /// <summary>0 unknown, 1 NotStarted, 2 InProgress, 3 FullyPlayed — <see cref="Wavee.HomeCard.PlayedState"/>'s own
    /// scale (Entities/Home.cs `StagedCardFact.PlayedState`), repeated here as named constants for readability.</summary>
    const byte PlayedInProgress = 2;

    const int NewEpisodesCap = 5;          // rule 1: 1 lead + 4 rows
    const int NewEpisodesRowCap = 4;
    const int VideoCap = 4;                // rule 3
    const int EpisodeRowsCap = 6;          // rule 4: 3 × 2-col
    const int GroupRowCap = 4;             // rule 8: 4 rows max per card
    const int FooterShowCap = 6;           // §3.3 P6: 6 thumbs + "See all N"
    const int GroupFoldThreshold = 3;      // rule 5/6: a key with < 3 episodes folds into rule 4

    /// <summary>The fold, start to finish. <paramref name="sections"/> is the facet document's sections in server
    /// order; <paramref name="titles"/> supplies the zone chapter titles (already localized by the caller);
    /// <paramref name="nowMs"/> is unused by the fold itself (kept for signature symmetry with the other planners
    /// and for a future "new since" rule) but threaded through so a caller never needs a second entry point once one
    /// is added.</summary>
    public static IReadOnlyList<Zone> Plan(IReadOnlyList<SectionInput> sections, ZoneTitles titles, long nowMs)
    {
        _ = nowMs;
        if (sections.Count == 0) return EmptyResult();

        // ── rule 11: drop what nobody may show, up front, everywhere ────────────────────────────────────────────
        var live = new List<(SectionInput Section, List<HomeCard> Cards)>(sections.Count);
        foreach (var section in sections)
        {
            var kept = new List<HomeCard>(section.Cards.Count);
            foreach (var card in section.Cards)
                if (IsUsable(card)) kept.Add(card);
            if (kept.Count > 0) live.Add((section, kept));
        }
        if (live.Count == 0) return EmptyResult();

        // ── rule 2a: every in-progress episode, from ANY section, pulled first and removed from its home list ────
        var continueItems = new List<HomeCard>();
        var removed = new HashSet<string>();
        foreach (var (_, cards) in live)
            for (int i = 0; i < cards.Count; i++)
                if (cards[i].PlayedState == PlayedInProgress && removed.Add(cards[i].Uri))
                    continueItems.Add(cards[i]);
        if (removed.Count > 0)
            foreach (var (_, cards) in live)
                cards.RemoveAll(c => removed.Contains(c.Uri));

        // ── classify what is left ───────────────────────────────────────────────────────────────────────────────
        var newEpisodes = new List<HomeCard>();
        var catchUp = new List<HomeCard>();
        var yourShows = new List<HomeCard>();
        var showsYouMightLike = new List<HomeCard>();
        var videoRaw = new List<HomeCard>();
        var episodePool = new List<HomeCard>();          // rule 4's pool: Similar + EpisodesYouMightLike + folds
        int episodesYouMightLikeSlot = -1, episodesYouMightLikeTotal = 0;

        // rule 5/6/7 candidates, keyed by the exact section title (one key = one group).
        var groupOrder = new List<string>();
        var groupOver = new Dictionary<string, string>();
        var groupName = new Dictionary<string, string>();
        var groupEpisodes = new Dictionary<string, List<HomeCard>>();
        var groupShowCards = new Dictionary<string, List<HomeCard>>();
        var groupShowSlot = new Dictionary<string, int>();
        var groupShowTotal = new Dictionary<string, int>();

        foreach (var (section, cards) in live)
        {
            string? title = section.Title;
            if (title is null) continue;

            if (title == PodcastPhrases.YourShows) { yourShows.AddRange(cards); continue; }
            if (title == PodcastPhrases.ShowsYouMightLike) { showsYouMightLike.AddRange(cards); continue; }
            if (title == PodcastPhrases.CatchUpOnYourShows) { catchUp.AddRange(cards); continue; }
            if (title == PodcastPhrases.VideosYouMightLike) { videoRaw.AddRange(cards); continue; }
            if (title == PodcastPhrases.SimilarToYourInterests || title == PodcastPhrases.EpisodesYouMightLike)
            {
                episodePool.AddRange(cards);
                if (title == PodcastPhrases.EpisodesYouMightLike) { episodesYouMightLikeSlot = section.Slot; episodesYouMightLikeTotal = section.TotalCount; }
                continue;
            }
            if (title.StartsWith(PodcastPhrases.NewEpisodeFrom, StringComparison.Ordinal)) { newEpisodes.AddRange(cards); continue; }

            if (TryGroup(title, PodcastPhrases.PopularWithListenersOf, out var popularKey))
            {
                EnsureGroup(popularKey, PodcastPhrases.PopularWithListenersOf, popularKey);
                // A "Popular with listeners of X" section carries either episode singles OR a shows shelf under the
                // same exact title (§4.4 rule 5) — split this section's cards by kind rather than by section.
                foreach (var card in cards)
                    if (card.Kind is HomeCardKind.Podcast or HomeCardKind.Audiobook) groupShowCards[popularKey].Add(card);
                    else groupEpisodes[popularKey].Add(card);
                if (groupShowCards[popularKey].Count > 0 && groupShowSlot[popularKey] < 0)
                {
                    groupShowSlot[popularKey] = section.Slot;
                    groupShowTotal[popularKey] = section.TotalCount;
                }
                continue;
            }
            if (TryGroup(title, PodcastPhrases.MoreEpisodesFeaturing, out var featuringKey))
            {
                EnsureGroup(featuringKey, "Featuring", featuringKey);
                groupEpisodes[featuringKey].AddRange(cards);
                continue;
            }
            if (TryGroup(title, PodcastPhrases.MoreLike, out var moreLikeKey))
            {
                // §4.4 rule 7: an episode-only shelf, no footer. Its own overflow ("See all 9") is the source
                // section's `TotalCount` minus the row cap — the `ZoneCluster` contract's footer fields are the
                // rule-5 SHOWS footer specifically, so a caller wanting that count reads it off the same-titled
                // `SectionInput.TotalCount` directly rather than through this cluster (a known, documented gap).
                EnsureGroup(moreLikeKey, PodcastPhrases.MoreLike, moreLikeKey);
                groupEpisodes[moreLikeKey].AddRange(cards);
                continue;
            }
            // Anything else (an unrecognised title, or a section with no title) carries no podcast rule and is
            // dropped rather than invented into a zone (§4.4 rule 12 / plan "nothing invented").
        }

        void EnsureGroup(string key, string over, string name)
        {
            if (groupOrder.Contains(key)) return;
            groupOrder.Add(key);
            groupOver[key] = over;
            groupName[key] = name;
            groupEpisodes[key] = [];
            groupShowCards[key] = [];
            groupShowSlot[key] = -1;
            groupShowTotal[key] = 0;
        }

        // ── rule 5/6: fold groups under the threshold into the rule-4 pool ─────────────────────────────────────────
        var clusters = new List<ZoneCluster>();
        var showsOnlyZones = new List<Zone>();
        foreach (var key in groupOrder)
        {
            var episodes = groupEpisodes[key];
            var showCards = groupShowCards[key];
            if (episodes.Count >= GroupFoldThreshold)
            {
                var rows = episodes.Take(GroupRowCap).ToList();
                IReadOnlyList<HomeCard>? footer = null;
                if (showCards.Count > 0) footer = showCards.Take(FooterShowCap).ToList();
                clusters.Add(new ZoneCluster(groupOver[key], groupName[key], rows, footer,
                    footer is null ? -1 : groupShowSlot[key], footer is null ? 0 : Math.Max(groupShowTotal[key], showCards.Count)));
            }
            else
            {
                episodePool.AddRange(episodes);
                if (showCards.Count > 0)
                {
                    // A key with episodes below the threshold (or none) but a shows shelf stays a shows-only shelf
                    // (§4.4 rule 5, "a key with shows only stays a shows shelf" — the same holds once its episodes
                    // fold away): its own ShowGrid zone, titled by the group's own phrase.
                    string zoneTitle = groupOver[key] + " " + groupName[key];
                    showsOnlyZones.Add(new Zone(ZoneKind.ShowGrid, "podcasts:shows-only:" + key, zoneTitle, null,
                        showCards, SectionSlot: groupShowSlot[key], TotalCount: Math.Max(groupShowTotal[key], showCards.Count)));
                }
            }
        }

        // ── rule 1: New episodes — newest first; lead = newest with a video thumb, else the newest overall ────────
        newEpisodes = newEpisodes.OrderByDescending(c => c.ReleasedAtMs).ToList();
        HomeCard? lead = null;
        var newEpisodeRows = new List<HomeCard>();
        if (newEpisodes.Count > 0)
        {
            int leadIndex = newEpisodes.FindIndex(c => c.VideoThumbUrl is not null);
            if (leadIndex < 0) leadIndex = 0;
            lead = newEpisodes[leadIndex];
            for (int i = 0; i < newEpisodes.Count && newEpisodeRows.Count < NewEpisodesRowCap; i++)
                if (i != leadIndex) newEpisodeRows.Add(newEpisodes[i]);
        }

        // ── rule 3: Videos — cap at 4; overflow beyond 4 feeds the rule-4 pool; a shortfall pulls from it ──────────
        List<HomeCard> videos;
        if (videoRaw.Count > VideoCap)
        {
            videos = videoRaw.Take(VideoCap).ToList();
            episodePool.AddRange(videoRaw.Skip(VideoCap));
        }
        else
        {
            videos = videoRaw;
            int need = VideoCap - videos.Count;
            if (need > 0)
            {
                var topUp = episodePool.Where(c => c.VideoThumbUrl is not null)
                    .OrderByDescending(c => c.ReleasedAtMs)
                    .Take(need)
                    .ToList();
                foreach (var card in topUp)
                {
                    videos.Add(card);
                    episodePool.RemoveAll(c => c.Uri == card.Uri);
                }
            }
        }

        // ── rule 4: Episodes you might like — dedupe by uri, then by (title, releasedAtMs) ──────────────────────
        var episodeRowsFull = DedupeEpisodePool(episodePool);
        var episodeRows = episodeRowsFull.Take(EpisodeRowsCap).ToList();

        // ── assemble zones in output order, applying rule 10 (page-wide dedupe) as we go ───────────────────────────
        var seen = new HashSet<string>();
        var zones = new List<Zone>();

        if (lead is not null || newEpisodeRows.Count > 0)
        {
            var rows = TakeUnseen(newEpisodeRows, seen, includeLead: lead);
            if (rows.Count > 0 || lead is not null)
                zones.Add(new Zone(ZoneKind.EpisodeLead, "podcasts:new-episodes", titles.NewEpisodes, null,
                    rows, lead, TotalCount: newEpisodes.Count));
        }

        var continueAll = new List<HomeCard>(continueItems.Count + catchUp.Count);
        continueAll.AddRange(continueItems);
        continueAll.AddRange(catchUp);
        AddIfAny(zones, ZoneKind.ContinueEpisodes, "podcasts:continue", titles.ContinueListening,
            TakeUnseen(continueAll, seen));

        // "Your shows" is identity, never deduped against itself or removed by a later zone's dedupe (§4.4 rule 9) —
        // but it DOES seed the seen-set, so every later zone still excludes a show already claimed here.
        if (yourShows.Count > 0)
        {
            foreach (var card in yourShows) seen.Add(card.Uri);
            zones.Add(new Zone(ZoneKind.ShowGrid, "podcasts:your-shows", titles.YourShows, null, yourShows,
                TotalCount: yourShows.Count));
        }

        AddIfAny(zones, ZoneKind.VideoTiles, "podcasts:videos", titles.VideosYouMightLike, TakeUnseen(videos, seen));
        AddIfAny(zones, ZoneKind.EpisodeRows, "podcasts:episode-rows", titles.EpisodesYouMightLike,
            TakeUnseen(episodeRows, seen), totalCount: episodeRowsFull.Count > 0 ? episodeRowsFull.Count : episodesYouMightLikeTotal,
            sectionSlot: episodesYouMightLikeSlot);

        if (clusters.Count > 0)
        {
            var keptClusters = new List<ZoneCluster>();
            foreach (var cluster in clusters)
            {
                var rows = TakeUnseen(cluster.Rows, seen);
                // Footer thumbs are navigation, not placements (§4.4 rule 10): checked against `seen` so a show
                // already in "Your shows" never appears there, but NOT added to `seen` themselves.
                IReadOnlyList<HomeCard>? footer = cluster.FooterShows is null ? null
                    : cluster.FooterShows.Where(c => !seen.Contains(c.Uri)).ToList();
                if (rows.Count == 0 && (footer is null || footer.Count == 0)) continue;
                keptClusters.Add(cluster with { Rows = rows, FooterShows = footer is { Count: 0 } ? null : footer });
            }
            if (keptClusters.Count > 0)
                zones.Add(new Zone(ZoneKind.PodcastGroups, "podcasts:groups", titles.BecauseYouListenTo, null,
                    [], Clusters: keptClusters));
        }

        AddIfAny(zones, ZoneKind.ShowGrid, "podcasts:shows-you-might-like", titles.ShowsYouMightLike, TakeUnseen(showsYouMightLike, seen));

        foreach (var zone in showsOnlyZones)
        {
            var rows = TakeUnseen(zone.Items, seen);
            if (rows.Count > 0) zones.Add(zone with { Items = rows });
        }

        // ── rule 12: nothing rendered anywhere → the single empty-facet zone ────────────────────────────────────
        return zones.Count > 0 ? zones : EmptyResult();
    }

    static IReadOnlyList<Zone> EmptyResult() => [new Zone(ZoneKind.EmptyFacet, "podcasts:empty", null, null, [])];

    static bool IsUsable(HomeCard card) => !card.Id.IsEmpty && card.IsPlayable;   // Id, not Uri: Uri formats a string for a gid id

    /// <summary>A section titled "{phrase} {rest}" (one space between them, as the server always writes it) yields
    /// <paramref name="rest"/>; anything else answers false.</summary>
    static bool TryGroup(string title, string phrase, out string rest)
    {
        if (title.Length > phrase.Length + 1 && title.StartsWith(phrase, StringComparison.Ordinal) && title[phrase.Length] == ' ')
        {
            rest = title[(phrase.Length + 1)..];
            return true;
        }
        rest = "";
        return false;
    }

    /// <summary>Rule 4's dedupe: first by uri (keep the first occurrence), then by (title, releasedAtMs) — the two
    /// Economist feeds that republish one episode under two uris.</summary>
    static List<HomeCard> DedupeEpisodePool(List<HomeCard> pool)
    {
        var byUri = new HashSet<string>();
        var byTitleDate = new HashSet<(string, long)>();
        var result = new List<HomeCard>(pool.Count);
        foreach (var card in pool)
        {
            if (!byUri.Add(card.Uri)) continue;
            var key = (card.Title, card.ReleasedAtMs);
            if (!byTitleDate.Add(key)) continue;
            result.Add(card);
        }
        return result;
    }

    /// <summary>Filters <paramref name="cards"/> to those not yet in <paramref name="seen"/>, adding every kept uri
    /// to it, then returns the survivors. When <paramref name="includeLead"/> is given it is checked/added too (but
    /// not returned — the caller already holds it separately as the zone's <see cref="Zone.Lead"/>).</summary>
    static List<HomeCard> TakeUnseen(IReadOnlyList<HomeCard> cards, HashSet<string> seen, HomeCard? includeLead = null)
    {
        if (includeLead is { } lead && seen.Add(lead.Uri)) { /* claims the uri for later zones */ }
        var result = new List<HomeCard>(cards.Count);
        foreach (var card in cards)
            if (seen.Add(card.Uri)) result.Add(card);
        return result;
    }

    static void AddIfAny(List<Zone> zones, ZoneKind kind, string key, string? title, List<HomeCard> cards,
        int totalCount = 0, int sectionSlot = -1)
    {
        if (cards.Count == 0) return;
        zones.Add(new Zone(kind, key, title, null, cards, SectionSlot: sectionSlot, TotalCount: Math.Max(totalCount, cards.Count)));
    }
}
