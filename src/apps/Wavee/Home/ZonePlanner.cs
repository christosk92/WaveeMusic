// ── Home/ZonePlanner.cs — CORE, pure, engine-free (owner A1, wave 1) ───────────────────────────────────────────────
//
// Role: CORE
// Owner: A1
// Wave: 1
// Budget: n/a
// Spec: docs/plans/wavee/home-redesign-implementation.md "Workstream H" zone order; 06-facet-design.md §3.1-3.2
//
// Turns the server's flat band list (SectionInput[], from Model.cs's SectionReader) into the ordered Zone[] the All /
// Music / Audiobooks screens render. Podcasts is a different regroup entirely (06-facet-design.md §4.3/§4.4) and is
// owned by the sibling agent's PodcastPlanner — this file only recognises a podcasts facet id and hands back an empty
// list so HomeScreen knows to ask PodcastPlanner instead.
//
// Pure and engine-free by construction: every function here takes SectionInput/HomeCard/plain values and returns
// records, so it is unit-testable without the FluentGpu engine or a mounted page (HomeCard itself is a live table
// read, but reading its properties needs no engine — only a committed Entities scope, which is what the test fixtures
// build).
//
// ASSUMPTIONS (documented per the plan's "document assumptions in XML comments" instruction — none of these are
// server contracts, they are read off the captured fixtures in 06-facet-design.md):
//   • "Personal" (daily mixes / Discover Weekly / the daylist / the "Soundtrack your…" `descripto` mixes) is detected by format
//     token ratio (≥50% of a section's usable cards), not by section kind — the server ships these as HomeGeneric
//     bands like everything else.
//   • "Radio" is detected the same way, by the raw format token containing "radio".
//   • "WideEditorial" (New Music Friday-style header-image shelves) is ≥2/3 header-image coverage, matching the
//     plan's WideTiles rule; Release Radar (format "release-radar") is pinned first among the merged items.
//   • "Jump back in" is the first Generic-role section (after Personal/Radio/Cluster/WideEditorial/Releases/Browse
//     have claimed their sections) whose usable cards mix ≥2 distinct HomeCardKinds — the plan's "first mixed
//     section" rule, applied across sections rather than within one, since no single server section is itself typed
//     "mixed".
//   • DJ (format "dj") is dropped everywhere, per the plan's explicit "DJ must NEVER appear anywhere" rule.
//   • A card counts as usable when it is not blank and <c>IsPlayable</c> (A6's decode flag folds UnknownType /
//     unresolved / paywalled cards into "not playable", so this one check covers all three).

using Wavee;

namespace Wavee.HomeUi;

/// <summary>What role one server band plays on the page — the classification every zone in <see cref="ZonePlanner"/>
/// is built from. <see cref="Drop"/> covers a section with nothing usable left after filtering (UnknownType-only,
/// entirely unplayable, or genuinely empty).</summary>
public enum SectionRole : byte
{
    Drop, Recents, Daylist, Personal, WideEditorial, Releases, Radio, Cluster, Browse, Generic,
}

/// <summary>Per-section role classification (pure; see the file banner's ASSUMPTIONS for the ratios/tokens used).</summary>
public static class SectionRoles
{
    /// <summary>Formats that mark a "Made for you" personal mix (06-facet-design.md M1/M2: daily mixes, Discover
    /// Weekly, the daylist itself when it recurs inside a shelf, the topic/mood mixes). The "Soundtrack your…" mood
    /// mixes ship as <c>descripto</c> (the generated-mix token, fixture home.json "Soundtrack your Saturday morning"),
    /// NOT <c>topic-mix</c> — and they now carry desktop header images, so without this token the header ratio below
    /// read them as a 16:9 editorial shelf.</summary>
    static bool IsPersonalFormat(string? f) => f is "daily-mix" or "discover-weekly" or "daylist" or "topic-mix" or "descripto";

    /// <summary>Radio stations: an explicit radio format, or <c>inspiredby-mix</c> — the live feed's "Popular radio" /
    /// "Recommended stations" cards ("&lt;Artist&gt; Radio") all carry that format (2026-09-25 capture).</summary>
    static bool IsRadioFormat(string? f)
        => f is not null && (f == "inspiredby-mix" || f.Contains("radio", StringComparison.OrdinalIgnoreCase));

    public static bool IsDj(string? format) => format is not null && format.Equals("dj", StringComparison.OrdinalIgnoreCase);

    /// <summary>A card worth showing at all: not blank, not DJ, and not ruled unplayable/UnknownType by decode.</summary>
    public static bool IsUsable(HomeCard c) => !c.IsBlank && c.IsPlayable && c.Uri.Length > 0 && !IsDj(c.Format);

    static int Count(IReadOnlyList<HomeCard> cards, Func<HomeCard, bool> match)
    {
        int n = 0;
        for (int i = 0; i < cards.Count; i++) if (IsUsable(cards[i]) && match(cards[i])) n++;
        return n;
    }

    /// <summary>The role one band plays. <paramref name="allFacet"/> gates Recents (All only — Music/Audiobooks
    /// never carry a HomeRecentlyPlayed band, but the check is defensive rather than trusting the server never
    /// will).</summary>
    public static SectionRole Of(SectionInput s, long nowMs, bool allFacet)
    {
        int usable = 0;
        for (int i = 0; i < s.Cards.Count; i++) if (IsUsable(s.Cards[i])) usable++;
        if (usable == 0) return SectionRole.Drop;

        if (s.Kind == SectionKind.HomeRecentlyPlayed) return allFacet ? SectionRole.Recents : SectionRole.Drop;
        if (s.Kind == SectionKind.HomeSpotlight) return SectionRole.Daylist;
        if (s.Kind == SectionKind.HomeShorts) return SectionRole.Browse;

        int personal = Count(s.Cards, c => IsPersonalFormat(c.Format));
        if (personal * 2 >= usable) return SectionRole.Personal;

        int radio = Count(s.Cards, c => IsRadioFormat(c.Format));
        if (radio * 2 >= usable) return SectionRole.Radio;

        if (s.Kind == SectionKind.HomeBaseline) return SectionRole.Cluster;

        int headered = Count(s.Cards, c => c.HeaderImageUrl is not null);
        if (usable >= 2 && headered * 3 >= usable * 2) return SectionRole.WideEditorial;

        if (ReleaseDetect.IsReleases(s, nowMs)) return SectionRole.Releases;

        return SectionRole.Generic;
    }
}

/// <summary>"Is this generic band actually a releases shelf?" — the owner's server-sections-only rule for "From
/// artists you follow" (no dedicated fetch; a releases shelf is recognised, not requested).</summary>
public static class ReleaseDetect
{
    const long DayMs = 24 * 60 * 60 * 1000L;
    /// <summary>The plan's window: released at most 42 days before now, or in the future ("Upcoming").</summary>
    const long WindowMs = 42 * DayMs;

    /// <summary>Title phrases that only ever TIE-BREAK (the plan: "title phrases only tie-break") — kept as a
    /// documented, currently-unused hook: every fixture in 06-facet-design.md is decisive on the ratio thresholds
    /// alone, so no case has needed a tie-break yet. A future ambiguous fixture breaks the tie by checking whether
    /// the section's own title contains one of these.</summary>
    public static readonly string[] TitlePhrases = ["new music friday", "new releases", "released this week"];

    public static bool IsReleases(SectionInput s, long nowMs)
    {
        var cards = s.Cards;
        if (cards.Count < 3) return false;

        int usable = 0, albums = 0, inWindow = 0;
        for (int i = 0; i < cards.Count; i++)
        {
            var c = cards[i];
            if (!SectionRoles.IsUsable(c)) continue;
            usable++;
            if (c.Kind == HomeCardKind.Album) albums++;
            long released = c.ReleasedAtMs;
            if (released > 0 && (released > nowMs || nowMs - released <= WindowMs)) inWindow++;
        }
        if (usable < 3) return false;
        if (albums * 4 < usable * 3) return false;             // ≥75% albums
        if (inWindow * 5 < usable * 3) return false;            // ≥60% released in-window (or upcoming)
        return true;
    }
}

/// <summary>Folds the server's <see cref="SectionKind.HomeBaseline"/> bands into grouped-list cards
/// (<see cref="ZoneCluster"/>) — D2 (F32): one cluster per baseline section, its <see cref="ZoneCluster.Rows"/> the
/// section's own <see cref="SectionInput.Preview"/> tracks (<c>feedBaselineLookup</c>), not title-prefix parsing over
/// the section's one card. Pure: takes the already-classified <see cref="SectionRole.Cluster"/> sections and the
/// page's zone-title sheet.</summary>
public static class ClusterFold
{
    /// <summary>The desktop client's own cap: at most four of a card's five preview tracks show on its cluster
    /// tile.</summary>
    public const int MaxRows = 4;

    /// <inheritdoc cref="Fold(IReadOnlyList{SectionInput},ZoneTitles,out IReadOnlyList{HomeCard})"/>
    public static IReadOnlyList<ZoneCluster> Fold(IReadOnlyList<SectionInput> clusterSections, ZoneTitles titles)
        => Fold(clusterSections, titles, out _);

    /// <summary>One cluster per baseline section: <see cref="ZoneCluster.Over"/> is the section's own SERVER title
    /// when it has one (server-localized — no more English prefix parsing), <see cref="ZoneCluster.Name"/> and
    /// <see cref="ZoneCluster.Header"/> are the section's own card (the first usable one — a baseline band is, in
    /// practice, exactly one), <see cref="ZoneCluster.Rows"/> its preview tracks, capped at <see cref="MaxRows"/>.
    /// A section whose preview has not landed yet (<see cref="SectionInput.Preview"/> still empty — the fetch races
    /// the plan) contributes nothing THIS render; the next render after <c>Edges.SectionPreviewTracks</c> answers is
    /// the real one.
    /// <para><paramref name="following"/> pulls every UNTITLED section whose card is an <see cref="HomeCardKind.Album"/>
    /// out as a release row instead — the "music-following-chip" shape (16 untitled one-album sections, F32): a
    /// one-row, title-less "cluster" was never a cluster, it is the Following facet's release feed.</para></summary>
    public static IReadOnlyList<ZoneCluster> Fold(IReadOnlyList<SectionInput> clusterSections, ZoneTitles titles,
        out IReadOnlyList<HomeCard> following)
    {
        var result = new List<ZoneCluster>(clusterSections.Count);
        List<HomeCard>? releases = null;

        foreach (var s in clusterSections)
        {
            if (HeaderCard(s.Cards) is not { } header) continue;

            if (string.IsNullOrEmpty(s.Title) && header.Kind == HomeCardKind.Album)
            {
                releases ??= [];
                releases.Add(header);
                continue;
            }

            var rows = PreviewRows(s.Preview, MaxRows);
            if (rows.Count == 0) continue;
            result.Add(new ZoneCluster(s.Title ?? titles.MoreForYou, header.Title, rows, FooterSectionSlot: s.Slot, Header: header));
        }

        following = releases ?? (IReadOnlyList<HomeCard>)[];
        // "cards ordered by row count desc then server order" — OrderByDescending is a stable sort, so ties keep the
        // server's own order.
        return result.OrderByDescending(static c => c.Rows.Count).ToList();
    }

    static HomeCard? HeaderCard(IReadOnlyList<HomeCard> cards)
    {
        foreach (var c in cards)
            if (SectionRoles.IsUsable(c)) return c;
        return null;
    }

    static List<HomeCard> PreviewRows(IReadOnlyList<HomeCard> preview, int cap)
    {
        var list = new List<HomeCard>(Math.Min(preview.Count, cap));
        foreach (var c in preview)
        {
            if (list.Count >= cap) break;
            if (!SectionRoles.IsUsable(c)) continue;
            list.Add(c);
        }
        return list;
    }
}

/// <summary>Merges every Radio-role section into one shelf: Recommended Stations, then Popular radio, then whatever
/// top-mix leftovers were also classified Radio — approximated by keeping the server's own section order, since the
/// server already emits "Recommended Stations" before "Popular radio" in every captured fixture. The lead is the
/// first item that carries a header image (06-facet-design.md M9's "Physical Radio" 2-cell lead).</summary>
public static class RadioMerge
{
    public static Zone? Merge(IReadOnlyList<SectionInput> radioSections, string title, HashSet<long> placed, string? fallbackSubtitle = null)
    {
        if (radioSections.Count == 0) return null;

        var items = new List<HomeCard>();
        HomeCard? lead = null;
        int leadSlot = -1;
        foreach (var s in radioSections)
        {
            foreach (var c in s.Cards)
            {
                if (!SectionRoles.IsUsable(c)) continue;
                if (!placed.Add(c.DedupeKey)) continue;
                if (lead is null && c.HeaderImageUrl is not null) { lead = c; leadSlot = s.Slot; }
                items.Add(c);
            }
        }
        if (items.Count == 0) return null;
        if (lead is { } l) items.Remove(l);
        return new Zone(ZoneKind.RadioShelf, "home:radio", title, fallbackSubtitle, items, lead,
            SectionSlot: leadSlot >= 0 ? leadSlot : radioSections[0].Slot);
    }
}

/// <summary>The page-wide top-down dedupe every zone applies before it claims cards: once a card's
/// <see cref="HomeCard.DedupeKey"/> has been placed, it never appears again lower on the page (06-facet-design.md's
/// "page-wide URI dedupe applied top-down" rule, e.g. M3/M4/M6/M7/M10's folded baseline singles).</summary>
public static class PageDedupe
{
    /// <summary><paramref name="cap"/> ≤ 0 means unbounded.</summary>
    public static IReadOnlyList<HomeCard> Place(HashSet<long> placed, IReadOnlyList<HomeCard> cards, int cap)
    {
        var result = new List<HomeCard>(cap > 0 ? Math.Min(cards.Count, cap) : cards.Count);
        foreach (var c in cards)
        {
            if (cap > 0 && result.Count >= cap) break;
            if (!SectionRoles.IsUsable(c)) continue;
            if (!placed.Add(c.DedupeKey)) continue;
            result.Add(c);
        }
        return result;
    }
}

/// <summary>Data work #6's per-zone-kind fallback subtitle sheet ("Daily Mixes and moods picked for you", …, the
/// <c>home.zoneSub.*</c> loc keys) — used ONLY when the server band itself carried no subtitle
/// (<see cref="SectionInput.Subtitle"/> null). A zone kind with no field here (podcast-only kinds, which
/// <c>PodcastPlanner</c> owns) simply never gets a fallback. Deliberately a small record local to
/// <see cref="ZonePlanner"/> rather than a new field on <see cref="ZoneTitles"/> (owned by a different wave-1
/// file) — "a similar pure input", per the rebuild plan's data-work item 6.</summary>
public sealed record ZoneSubtitles(string? MadeForYou = null, string? NewMusic = null, string? BecauseYouLike = null,
    string? JumpBackIn = null, string? Radio = null, string? Browse = null, string? NewEpisodes = null);

/// <summary>Projects a facet's <see cref="SectionInput"/> list into the ordered <see cref="Zone"/> list the screen
/// renders. Podcasts is delegated to the sibling PodcastPlanner (see the file banner); every other facet is planned
/// here.</summary>
public static class ZonePlanner
{
    enum Facet { All, Music, Podcasts, Audiobooks }

    /// <summary>The 8th Recents grid cell is always the fixed HistoryItem, never an 8th played card (plan §4/§8).</summary>
    public const int RecentsCap = 8;

    /// <summary>Browse shows three rows of four navigation tiles (canvas ⑧), however many bands fold into it.</summary>
    public const int BrowseTilesMax = 12;

    static Facet FacetOf(string facet) => facet switch
    {
        "" => Facet.All,
        "music-chip" or "music-following-chip" => Facet.Music,
        "podcasts-chip" or "podcasts-following-chip" => Facet.Podcasts,
        "audiobooks-chip" => Facet.Audiobooks,
        _ => Facet.All,
    };

    /// <summary><paramref name="isHidden"/> is the customizer's per-zone-kind visibility (null = nothing hidden).
    /// A hidden zone is planned and then dropped, so the dedupe/lead/cluster logic never has to special-case it.
    /// <para><paramref name="following"/> (data work #3) is the "music-following-chip" document's own sections —
    /// the All feed never lists per-followed-artist release rows itself, so HomeScreen prefetches that document
    /// separately once All opens and hands its sections in here once they've answered; omitted (null) until then,
    /// so the very first All render — before the prefetch lands — plans exactly as it did with no `following` input
    /// at all. Ignored for every facet but All.</para>
    /// <para><paramref name="subs"/> (data work #6) is the fallback subtitle sheet applied only to a zone whose own
    /// server band carried no subtitle.</para></summary>
    public static IReadOnlyList<Zone> Plan(IReadOnlyList<SectionInput> sections, string facet, ZoneTitles titles,
        Func<ZoneKind, bool>? isHidden, long nowMs, IReadOnlyList<SectionInput>? following = null, ZoneSubtitles? subs = null)
    {
        var facetKind = FacetOf(facet);
        if (facetKind == Facet.Podcasts) return [];    // PodcastPlanner owns Podcasts (06-facet-design.md §4.3/§4.4)

        var placed = new HashSet<long>();
        var zones = new List<Zone>();
        switch (facetKind)
        {
            case Facet.All: PlanAll(sections, titles, placed, zones, nowMs, following, subs); break;
            case Facet.Music: PlanMusic(sections, titles, placed, zones, nowMs, subs); break;
            case Facet.Audiobooks: PlanAudiobooks(sections, zones, placed); break;
        }

        if (isHidden is null) return zones;
        var kept = new List<Zone>(zones.Count);
        foreach (var z in zones)
            if (!isHidden(z.Kind)) kept.Add(z);
        return kept;
    }

    // ── All ──────────────────────────────────────────────────────────────────────────────────────────────────────

    static void PlanAll(IReadOnlyList<SectionInput> sections, ZoneTitles titles, HashSet<long> placed, List<Zone> zones, long nowMs,
        IReadOnlyList<SectionInput>? following = null, ZoneSubtitles? subs = null)
    {
        var roles = Classify(sections, nowMs, allFacet: true);

        // D6: DaylistSource looks across every section (not only a HomeSpotlight band) for the daylist card — the
        // live All feed folds it into a Made-for-you shelf instead. Claiming its DedupeKey here, before
        // AddCoverShelf/AddWideAndReleases/etc. run their own PageDedupe passes, is what pulls it out of that shelf.
        if (DaylistSource.Find(sections) is { } found && placed.Add(found.Card.DedupeKey))
        {
            var section = SectionBySlot(sections, found.SectionSlot);
            zones.Add(new Zone(ZoneKind.Daylist, "home:daylist", section?.Title, section?.Subtitle,
                [found.Card], found.Card, SectionSlot: found.SectionSlot, TotalCount: section?.TotalCount ?? 0));
        }

        // Jump back in is deduped against everything placed above it EXCEPT the Recents grid: the two overlap by
        // design (the grid is the last few plays, Jump back in the longer tail), and deduping against the grid
        // leaves it a stub.
        var placedBeforeRecents = new HashSet<long>(placed);
        if (FirstOf(sections, roles, SectionRole.Recents) is { } rc)
        {
            // Recently played caps at 7 cards — the 8th grid cell is always the fixed HistoryItem
            // (Items.HistoryItem / Cards.HistoryItem), not another played card (plan §4/§8).
            var cards = PageDedupe.Place(placed, rc.Cards, RecentsCap);
            if (cards.Count > 0)
                zones.Add(new Zone(ZoneKind.RecentGrid, "home:recents", titles.RecentlyPlayed, rc.Subtitle, cards, SectionSlot: rc.Slot, TotalCount: rc.TotalCount));
        }
        var recentsKeys = new HashSet<long>(placed);
        recentsKeys.ExceptWith(placedBeforeRecents);

        AddCoverShelf(AllOf(sections, roles, SectionRole.Personal), titles.MadeForYou, "home:made-for-you", placed, zones,
            leadIsDiscoverWeekly: true, fallbackSubtitle: subs?.MadeForYou);

        AddWideAndReleases(sections, roles, titles, placed, zones, following, subs?.NewMusic);

        AddClusterCards(AllOf(sections, roles, SectionRole.Cluster), titles, "home:cluster", placed, zones, subs?.BecauseYouLike);

        var jbi = FirstMixed(sections, roles);
        if (jbi is { } j)
        {
            var jbiPlaced = new HashSet<long>(placed);
            jbiPlaced.ExceptWith(recentsKeys);
            var cards = PageDedupe.Place(jbiPlaced, j.Cards, 0);
            foreach (var c in cards) placed.Add(c.DedupeKey);
            if (cards.Count > 0)
                zones.Add(new Zone(ZoneKind.MixedCovers, "home:jump-back-in", titles.JumpBackIn, j.Subtitle ?? subs?.JumpBackIn, cards, SectionSlot: j.Slot));
        }

        if (RadioMerge.Merge(AllOf(sections, roles, SectionRole.Radio), titles.RadioAndMixes, placed, subs?.Radio) is { } radio) zones.Add(radio);

        // The canvas's All page ends on a fixed rhythm (…Jump back in → Radio & mixes → Browse; "no two neighbouring
        // shelves share a template"): every other generic band — the feed's editorial and topical rows — folds into
        // Browse's navigation tiles after the server's own browse band, in server order, instead of each becoming
        // one more 6-up cover shelf.
        var browse = AllOf(sections, roles, SectionRole.Browse);
        foreach (var s in sections)
            if (roles[s.Slot] == SectionRole.Generic && !(jbi is { } jj && jj.Slot == s.Slot)) browse.Add(s);
        AddBrowse(browse, titles.Browse, "home:browse", placed, zones, subs?.Browse);
    }

    // ── Music ────────────────────────────────────────────────────────────────────────────────────────────────────

    static void PlanMusic(IReadOnlyList<SectionInput> sections, ZoneTitles titles, HashSet<long> placed, List<Zone> zones, long nowMs,
        ZoneSubtitles? subs = null)
    {
        var roles = Classify(sections, nowMs, allFacet: false);

        foreach (var s in sections)
        {
            var role = roles[s.Slot];
            if (role is not (SectionRole.Personal or SectionRole.WideEditorial or SectionRole.Releases or SectionRole.Generic)) continue;
            var cards = PageDedupe.Place(placed, s.Cards, 0);
            if (cards.Count == 0) continue;

            HomeCard? lead = role == SectionRole.Personal ? FindDiscoverWeeklyLead(cards) : null;
            // Every Music shelf keeps the SERVER's title, so only the server's own subtitle fits it — a zone-subtitle
            // fallback describes one of OUR zones ("Made for you"), never an arbitrary server band.
            zones.Add(new Zone(ZoneKind.CoverShelf, "home:music:" + s.Slot, s.Title, s.Subtitle, cards, lead, SectionSlot: s.Slot, TotalCount: s.TotalCount));
        }

        AddClusterCards(AllOf(sections, roles, SectionRole.Cluster), titles, "home:music:cluster", placed, zones, subs?.BecauseYouLike);

        if (RadioMerge.Merge(AllOf(sections, roles, SectionRole.Radio), titles.RadioAndMixes, placed, subs?.Radio) is { } radio) zones.Add(radio);

        AddBrowse(AllOf(sections, roles, SectionRole.Browse), titles.Browse, "home:music:browse", placed, zones, subs?.Browse);
    }

    // ── Audiobooks ───────────────────────────────────────────────────────────────────────────────────────────────

    static void PlanAudiobooks(IReadOnlyList<SectionInput> sections, List<Zone> zones, HashSet<long> placed)
    {
        bool any = false;
        foreach (var s in sections)
        {
            var cards = PageDedupe.Place(placed, s.Cards, 0);
            if (cards.Count == 0) continue;
            any = true;
            zones.Add(new Zone(ZoneKind.CoverShelf, "home:audiobooks:" + s.Slot, s.Title, s.Subtitle, cards, SectionSlot: s.Slot, TotalCount: s.TotalCount));
        }
        if (!any) zones.Add(new Zone(ZoneKind.EmptyFacet, "home:audiobooks:empty", null, null, []));
    }

    // ── shared zone builders ────────────────────────────────────────────────────────────────────────────────────

    static void AddCoverShelf(IReadOnlyList<SectionInput> personalSections, string title, string key, HashSet<long> placed, List<Zone> zones,
        bool leadIsDiscoverWeekly, string? fallbackSubtitle = null)
    {
        if (personalSections.Count == 0) return;
        var items = new List<HomeCard>();
        HomeCard? lead = null;
        foreach (var s in personalSections)
            foreach (var c in PageDedupe.Place(placed, s.Cards, 0))
            {
                if (leadIsDiscoverWeekly && lead is null && IsDiscoverWeekly(c)) lead = c;
                items.Add(c);
            }
        if (lead is { } l) { items.Remove(l); items.Insert(0, l); }
        if (items.Count > 0)
            zones.Add(new Zone(ZoneKind.CoverShelf, key, title, personalSections[0].Subtitle ?? fallbackSubtitle, items, lead, SectionSlot: personalSections[0].Slot));
    }

    static void AddWideAndReleases(IReadOnlyList<SectionInput> sections, Dictionary<int, SectionRole> roles, ZoneTitles titles,
        HashSet<long> placed, List<Zone> zones, IReadOnlyList<SectionInput>? following = null, string? fallbackSubtitle = null)
    {
        var wide = AllOf(sections, roles, SectionRole.WideEditorial);
        if (wide.Count > 0)
        {
            var items = new List<HomeCard>();
            foreach (var s in wide) items.AddRange(PageDedupe.Place(placed, s.Cards, 0));
            int rr = items.FindIndex(static c => c.Format == "release-radar");
            if (rr > 0) { var card = items[rr]; items.RemoveAt(rr); items.Insert(0, card); }
            if (items.Count > 0)
                // The server names this shelf ("It's New Music Friday!"), so only the server's own subtitle fits it: the
                // New-music fallback line would mislabel an editorial band that is not about new music. (The
                // "Soundtrack your…" mood mixes never land here — they are `descripto`, Personal, Made for you.)
                zones.Add(new Zone(ZoneKind.WideTiles, "home:wide", wide[0].Title, wide[0].Subtitle, items, SectionSlot: wide[0].Slot));
        }

        var releases = AllOf(sections, roles, SectionRole.Releases);
        var releaseItems = new List<HomeCard>();
        foreach (var s in releases) releaseItems.AddRange(PageDedupe.Place(placed, s.Cards, 0));

        // Data work #3: the "From artists you follow" list under New Music Friday also takes the
        // "music-following-chip" document's own sections, once HomeScreen has prefetched them — the All feed
        // itself never carries a per-followed-artist release row. Folded in AFTER the server's own releases-role
        // sections (server order first), through the same page-wide `placed` dedupe, so a card the All feed
        // already placed elsewhere never doubles up here.
        if (following is { Count: > 0 })
            foreach (var s in following) releaseItems.AddRange(PageDedupe.Place(placed, s.Cards, 0));

        if (releaseItems.Count > 0)
        {
            int slot = releases.Count > 0 ? releases[0].Slot : following is { Count: > 0 } ? following[0].Slot : -1;
            string? subtitle = (releases.Count > 0 ? releases[0].Subtitle : null) ?? fallbackSubtitle;
            zones.Add(new Zone(ZoneKind.ReleaseList, "home:releases", titles.FromArtistsYouFollow, subtitle, releaseItems, SectionSlot: slot));
        }
    }

    static void AddClusterCards(IReadOnlyList<SectionInput> clusterSections, ZoneTitles titles, string key, HashSet<long> placed, List<Zone> zones,
        string? fallbackSubtitle = null)
    {
        var clusters = ClusterFold.Fold(clusterSections, titles, out var following);

        // D2 (F32): the Following facet's untitled one-album baseline sections are a release list, not clusters.
        if (following.Count > 0)
        {
            var releaseRows = PageDedupe.Place(placed, following, 0);
            if (releaseRows.Count > 0)
                zones.Add(new Zone(ZoneKind.ReleaseList, key + ":following", titles.FromArtistsYouFollow, null, releaseRows));
        }

        if (clusters.Count == 0) return;

        // A cluster's preview ROWS are navigation inside its card (like the podcast footer thumbs, F32 §3.12.5) —
        // never deduped page-wide. Only its HEADER card — what the tile itself opens to — competes for a page slot.
        var keptClusters = new List<ZoneCluster>(clusters.Count);
        var items = new List<HomeCard>();
        foreach (var c in clusters)
        {
            if (c.Header is { } header)
            {
                if (!placed.Add(header.DedupeKey)) continue;
                items.Add(header);
            }
            keptClusters.Add(c);
        }
        if (keptClusters.Count > 0)
            zones.Add(new Zone(ZoneKind.ClusterCards, key, titles.BecauseYouLike, fallbackSubtitle, items, Clusters: keptClusters));
    }

    static void AddBrowse(IReadOnlyList<SectionInput> browseSections, string title, string key, HashSet<long> placed, List<Zone> zones,
        string? fallbackSubtitle = null)
    {
        if (browseSections.Count == 0) return;
        var items = new List<HomeCard>();
        foreach (var s in browseSections)
        {
            if (items.Count >= BrowseTilesMax) break;
            items.AddRange(PageDedupe.Place(placed, s.Cards, BrowseTilesMax - items.Count));
        }
        if (items.Count > 0) zones.Add(new Zone(ZoneKind.BrowseTiles, key, title, fallbackSubtitle, items, SectionSlot: browseSections[0].Slot));
    }

    // ── classification plumbing ─────────────────────────────────────────────────────────────────────────────────

    static Dictionary<int, SectionRole> Classify(IReadOnlyList<SectionInput> sections, long nowMs, bool allFacet)
    {
        var map = new Dictionary<int, SectionRole>(sections.Count);
        foreach (var s in sections) map[s.Slot] = SectionRoles.Of(s, nowMs, allFacet);
        return map;
    }

    static List<SectionInput> AllOf(IReadOnlyList<SectionInput> sections, Dictionary<int, SectionRole> roles, SectionRole role)
    {
        var list = new List<SectionInput>();
        foreach (var s in sections) if (roles[s.Slot] == role) list.Add(s);
        return list;
    }

    static SectionInput? FirstOf(IReadOnlyList<SectionInput> sections, Dictionary<int, SectionRole> roles, SectionRole role)
    {
        foreach (var s in sections) if (roles[s.Slot] == role) return s;
        return null;
    }

    /// <summary>The section <see cref="DaylistSource.Find"/> hoisted its card out of — carries over that section's
    /// own server title/subtitle/total-count for the Daylist zone (D6).</summary>
    static SectionInput? SectionBySlot(IReadOnlyList<SectionInput> sections, int slot)
    {
        foreach (var s in sections) if (s.Slot == slot) return s;
        return null;
    }

    /// <summary>The first Generic-role section whose usable cards mix ≥2 distinct <see cref="HomeCardKind"/>s — the
    /// plan's "Jump back in" rule (see the file banner's ASSUMPTIONS).</summary>
    static SectionInput? FirstMixed(IReadOnlyList<SectionInput> sections, Dictionary<int, SectionRole> roles)
    {
        foreach (var s in sections)
        {
            if (roles[s.Slot] != SectionRole.Generic) continue;
            HomeCardKind? first = null;
            foreach (var c in s.Cards)
            {
                if (!SectionRoles.IsUsable(c)) continue;
                if (first is null) first = c.Kind;
                else if (c.Kind != first) return s;
            }
        }
        return null;
    }

    static bool IsDiscoverWeekly(HomeCard c) => c.Format == "discover-weekly" && c.HeaderImageUrl is not null;

    static HomeCard? FindDiscoverWeeklyLead(IReadOnlyList<HomeCard> cards)
    {
        foreach (var c in cards) if (IsDiscoverWeekly(c)) return c;
        return null;
    }
}
