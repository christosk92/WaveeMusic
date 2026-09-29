// ── Home/Model.cs — CORE (owner A1, wave 1) ─────────────────────────────────────────────────────────────────────────
//
// Role: CORE
// Owner: A1
// Wave: 1
// Budget: n/a
// Spec: docs/plans/wavee/home-redesign-implementation.md "Workstream H", Appendix A/B;
//        docs/plans/wavee/home-redesign/06-facet-design.md
//
// Ground-up rebuild of the Home presentation model (plan: no reference to the old HomeComposer / HomeFeedView /
// HomeGroup / HomeSectionView / … types — read only to learn data contracts). This file is the pure data shape every
// other Wavee.HomeUi file builds on:
//   • SectionInput — one server band, read straight off SectionTable/HomeTable through SectionReader.
//   • Zone         — one rendered chapter of the screen (ZonePlanner's / PodcastPlanner's output).
//   • ScreenModel  — the whole screen for one facet.
// SectionReader is the ONLY code in this namespace that touches the entity tables directly; ZonePlanner,
// PodcastPlanner and the UI layer consume SectionInput/Zone/HomeCard and never read a table column themselves.

using Wavee;

namespace Wavee.HomeUi;

/// <summary>What a rendered chapter of the Home screen is. One member per template on the zone→controls sheet
/// (plan "Zone → controls → metrics").</summary>
public enum ZoneKind : byte
{
    Daylist, RecentGrid, CoverShelf, WideTiles, ReleaseList, ClusterCards, MixedCovers, RadioShelf,
    BrowseTiles, EpisodeLead, ContinueEpisodes, ShowGrid, VideoTiles, EpisodeRows, PodcastGroups, EmptyFacet,
}

/// <summary>One server band, read straight off <see cref="SectionTable"/> by <see cref="SectionReader"/>. Never
/// mutated after it is built — a new server answer is a new array from <see cref="SectionReader.Read"/>.
/// <para><see cref="Preview"/> is D2's <c>feedBaselineLookup</c> enrichment: up to five thin Track cards for a
/// <see cref="SectionKind.HomeBaseline"/> band (<c>Edges.SectionPreviewTracks</c>), empty until the lookup answers
/// and empty forever for every other band kind. <see cref="ZonePlanner"/>'s <c>ClusterFold</c> is the one reader.</para></summary>
public sealed record SectionInput(int Slot, string Uri, string? Title, string? Subtitle, SectionKind Kind,
    IReadOnlyList<HomeCard> Cards, int TotalCount, IReadOnlyList<HomeCard> Preview);

/// <summary>One rendered chapter of the Home screen. <see cref="Lead"/> is the wide/2-cell lead item (Discover
/// Weekly, the first radio station with a header image, a podcast's newest video episode); <see cref="Clusters"/> is
/// only set for <see cref="ZoneKind.ClusterCards"/>/<see cref="ZoneKind.PodcastGroups"/>.</summary>
public sealed record Zone(ZoneKind Kind, string Key, string? Title, string? Subtitle, IReadOnlyList<HomeCard> Items,
    HomeCard? Lead = null, IReadOnlyList<ZoneCluster>? Clusters = null, int SectionSlot = -1, int TotalCount = 0);

/// <summary>One grouped-list card inside a <see cref="ZoneKind.ClusterCards"/>/<see cref="ZoneKind.PodcastGroups"/>
/// zone — "More like {Name}" / "For fans of {Name}" / "Popular with listeners of {Name}". Named <c>ZoneCluster</c>,
/// not <c>Cluster</c>: a protobuf <c>Cluster</c> type already exists in this codebase.
/// <para><see cref="Header"/> (D2) is the card the cluster itself opens to — the baseline section's own card (a
/// playlist or an album); <see cref="Rows"/> is what plays FROM that context (D2's preview tracks for a
/// <see cref="SectionKind.HomeBaseline"/> band, a show's episodes for a podcast group). Null for the pre-D2 rows a
/// singleton/untitled fold produced, which open nothing of their own.</para></summary>
public sealed record ZoneCluster(string Over, string Name, IReadOnlyList<HomeCard> Rows,
    IReadOnlyList<HomeCard>? FooterShows = null, int FooterSectionSlot = -1, int FooterTotal = 0, HomeCard? Header = null);

/// <summary>One facet-pivot word (or its Following sub-chip), read off <see cref="Home.ChipIds"/>/etc.</summary>
public sealed record ChipInput(string Id, string Label, string? SubId, string? SubLabel);

/// <summary>The whole Home screen for one facet — <see cref="ZonePlanner.Plan"/>'s (or PodcastPlanner's) output plus
/// the chrome the facet row needs.</summary>
public sealed record ScreenModel(string Facet, IReadOnlyList<Zone> Zones, string Greeting, IReadOnlyList<ChipInput> Chips);

/// <summary>Every zone chapter title / caption the plan's copy sheet names, so <see cref="ZonePlanner"/> and
/// PodcastPlanner never hardcode an English string (locale-ready; the loc catalog hands one of these in).</summary>
public sealed record ZoneTitles(
    string MadeForYou, string BecauseYouLike, string MoreForYou, string RadioAndMixes, string Browse,
    string JumpBackIn, string RecentlyPlayed, string NewEpisodes, string ContinueListening,
    string VideosYouMightLike, string EpisodesYouMightLike, string BecauseYouListenTo, string FromArtistsYouFollow,
    string YourShows, string ShowsYouMightLike);

/// <summary>Reads <see cref="SectionInput"/>/<see cref="ChipInput"/> off the live tables. The only file in
/// Wavee.HomeUi that touches <c>HomeTable</c>/<c>SectionTable</c> columns; everything downstream is pure.</summary>
public static class SectionReader
{
    static readonly SectionInput[] s_none = [];
    static readonly ChipInput[] s_noChips = [];

    sealed class RowCache
    {
        public uint HomeVersion = uint.MaxValue;
        public uint SectionEdgeVersion = uint.MaxValue;
        public uint CardFold = uint.MaxValue;
        public SectionInput[] Sections = [];
    }

    sealed class SectionCache
    {
        public uint CardVersion = uint.MaxValue;
        public HomeCard[] Cards = [];
    }

    // Keyed by row slot: one Home row per facet, one Section row per band. Small (≤ a handful of facets, ≤ ~40
    // sections each), so a dictionary miss is the cold path and a hit allocates nothing.
    static readonly Dictionary<int, RowCache> s_rows = new();
    static readonly Dictionary<int, SectionCache> s_sections = new();
    static Scope? s_scope;

    // Slots and versions restart with every Entities.Boot (sign-out/in, a test's fresh scope), so a memo from the
    // previous scope would hand back the old store's cards under a matching (slot, version).
    static void EnsureScope()
    {
        if (ReferenceEquals(s_scope, Entities.Current)) return;
        s_rows.Clear();
        s_sections.Clear();
        s_scope = Entities.Current;
    }

    /// <summary>This facet's bands, in server order. Memoized per (row slot, <see cref="Home.Version"/>,
    /// <see cref="Home.SectionVersion"/>): a render that hits between two feed answers gets the same array back with
    /// no rebuild, and a feed refresh that touches nothing (a duplicate poll) is a no-op past the version check.</summary>
    public static IReadOnlyList<SectionInput> Read(Home h)
    {
        if (!h.IsValid) return s_none;
        EnsureScope();
        if (!s_rows.TryGetValue(h.Slot, out var cache)) s_rows[h.Slot] = cache = new RowCache();

        uint hv = h.Version, sv = h.SectionVersion;
        var slots = h.SectionSlots;
        // A band's cards land on the SectionCards edge after the Home row itself publishes, without bumping either
        // Home version — so the memo key folds every band's card version in too. D2's previews land later still,
        // off a SEPARATE request (FetchEdge.HomePreviews) — folded in the same way, or a baseline band that answered
        // before its lookup would hand out a stale, preview-less SectionInput forever.
        uint fold = 17;
        for (int i = 0; i < slots.Length; i++)
        {
            var section = new Section(slots[i]);
            fold = fold * 31 + section.CardVersion;
            fold = fold * 31 + section.PreviewVersion;
        }
        if (cache.HomeVersion == hv && cache.SectionEdgeVersion == sv && cache.CardFold == fold) return cache.Sections;

        // Always a fresh array: a published ScreenModel may still hold the previous one.
        var result = new SectionInput[slots.Length];
        for (int i = 0; i < slots.Length; i++) result[i] = Of(new Section(slots[i]));

        cache.Sections = result;
        cache.HomeVersion = hv;
        cache.SectionEdgeVersion = sv;
        cache.CardFold = fold;
        return result;
    }

    /// <summary>One band, read fresh off the tables (no memo — <see cref="Read"/> is the memoized entry point; this
    /// is also what test fixtures call to turn a committed <see cref="Section"/> into a <see cref="SectionInput"/>
    /// without staging a whole Home row).</summary>
    public static SectionInput Of(Section s)
    {
        string uri = s.Id is { IsEmpty: false } id ? id.Text : "";
        string? title = s.TitleId.IsEmpty ? null : Entities.Strings.Resolve(s.TitleId);
        string? subtitle = s.SubtitleId.IsEmpty ? null : Entities.Strings.Resolve(s.SubtitleId);
        return new SectionInput(s.Slot, uri, title, subtitle, s.Kind, CardsOf(s), s.Total, PreviewOf(s));
    }

    static IReadOnlyList<HomeCard> CardsOf(Section s)
    {
        EnsureScope();
        if (!s_sections.TryGetValue(s.Slot, out var cache)) s_sections[s.Slot] = cache = new SectionCache();
        uint cv = s.CardVersion;
        if (cache.CardVersion == cv) return cache.Cards;

        var targets = s.CardSlots;
        var kinds = s.CardKinds;
        int n = Math.Min(targets.Length, kinds.Length);
        var cards = new HomeCard[n];   // fresh: an earlier SectionInput may still hold the previous array
        for (int i = 0; i < n; i++) cards[i] = new HomeCard(new EntityRef(kinds[i].Kind, targets[i]), s.Slot);

        cache.Cards = cards;
        cache.CardVersion = cv;
        return cards;
    }

    /// <summary>D2's preview rows (<c>Edges.SectionPreviewTracks</c>): up to five thin Track cards, rank order. Not
    /// memoized on its own — <see cref="Read"/>'s fold already keys on <see cref="Section.PreviewVersion"/>, so a
    /// section object built between two lookup answers gets a fresh read here for free.</summary>
    static IReadOnlyList<HomeCard> PreviewOf(Section s)
    {
        var targets = s.PreviewSlots;
        if (targets.Length == 0) return [];
        var cards = new HomeCard[targets.Length];
        for (int i = 0; i < targets.Length; i++) cards[i] = new HomeCard(new EntityRef(EntityKind.Track, targets[i]), s.Slot);
        return cards;
    }

    /// <summary>This facet's chip strip, top-level words paired with their Following sub-chip when one hangs under
    /// them (06-facet-design.md §2.3: at most one sub-chip per word today, but the walk tolerates more and keeps the
    /// first). Not memoized — the chip strip changes only on a facet switch, which already rebuilds the whole
    /// screen.</summary>
    public static IReadOnlyList<ChipInput> Chips(Home h)
    {
        if (!h.IsValid) return s_noChips;
        var ids = h.ChipIds;
        var labels = h.ChipLabels;
        var parents = h.ChipParents;
        int n = Math.Min(ids.Length, Math.Min(labels.Length, parents.Length));
        if (n == 0) return s_noChips;

        var list = new List<ChipInput>(n);
        for (int i = 0; i < n; i++)
        {
            if (parents[i] != -1) continue;                       // a sub-chip: folded into its parent below
            string id = Entities.Strings.Resolve(ids[i]);
            string label = Entities.Strings.Resolve(labels[i]);
            string? subId = null, subLabel = null;
            for (int j = 0; j < n; j++)
            {
                if (parents[j] != i) continue;
                subId = Entities.Strings.Resolve(ids[j]);
                subLabel = Entities.Strings.Resolve(labels[j]);
                break;
            }
            list.Add(new ChipInput(id, label, subId, subLabel));
        }
        return list;
    }

    /// <summary>The server's transformed greeting, else a local-clock fallback ("Good morning/afternoon/evening") so
    /// the hero eyebrow is always renderable even before the first answer lands (parity with the old
    /// <c>HomeFeedReadiness</c> rule that a placeholder must never be a blank string).</summary>
    public static string Greeting(Home h)
    {
        if (h.IsValid && !h.GreetingId.IsEmpty) return Entities.Strings.Resolve(h.GreetingId);
        int hour = DateTime.Now.Hour;
        return hour switch
        {
            >= 4 and < 12 => "Good morning",
            >= 12 and < 17 => "Good afternoon",
            _ => "Good evening",
        };
    }
}
