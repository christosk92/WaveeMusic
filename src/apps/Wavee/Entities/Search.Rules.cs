// ── Entities/Search.Rules.cs ───────────────────────────────────────────────────────────────────────────────────────
// the Wave 5 half of Search's CORE: the genre / related / hit-flag relations, their staged commit, and ch 13 §8's pure
// rule set (FacetsFrom / FacetCount, the fallback interleave, ColsFor, the playlist rail, the genre route, the shimmer
// shape, the per-facet empty sentence)
//
// Role: CORE
// Owner: P
// Wave: 5
// Budget: 500 lines (a named partial of Search.cs: Search.cs's 450 could not hold the Wave 1 columns AND ch 13 §8's
//   rules — split under the plan's >30 % rule by owner P at reconciliation; the sections keep their numbers)
// Spec: ch 13 §7 DATA GAPS 1-4, §8; WP-5.P contract §4

using System.Buffers;
using FluentGpu.Foundation;

namespace Wavee;

// ══ 5. THE WAVE 5 RELATIONS (owner P, stream P3) ════════════════════════════════════════════════════════════════════

/// <summary>How a row's hit list was assembled.</summary>
[Flags]
public enum SearchRowFlags : byte
{
    None = 0,
    /// <summary>The answer carried no ranked top-results list, so the hits were COLLECTED from the facet lists in wire
    /// order (tracks, albums, artists, playlists, …) — the page interleaves them (<see cref="Search.FallbackRows"/>)
    /// rather than promoting hit 0 to a Top Result.</summary>
    Collected = 1,
}

/// <summary>The per-hit search chrome a hit row paints beside its entity (ch 13 data gap 2, the part 0.3 keeps).</summary>
[Flags]
public enum SearchHitFlags : byte
{
    None = 0,
    /// <summary><c>matchedFields</c> named TITLE / NAME — the hero's "Matched title" chip.</summary>
    MatchedTitle = 1,
    /// <summary><c>matchedFields</c> named LYRICS — the "Lyrics match" chip and row eyebrow.</summary>
    MatchedLyrics = 2,
    /// <summary><c>trackMediaType: VIDEO</c> — the row reads "Music video" rather than "Song".</summary>
    VideoMedia = 4,
}

/// <summary>One related-search string (payload-only; the target is <see cref="Table.None"/>). The query text is OWNED:
/// retained at commit, released when the list is replaced and when the scope retires
/// (<see cref="Edges.ReleaseSearchText"/>).</summary>
public readonly record struct SearchRelatedEdge(StringId Query);

/// <summary>The chrome of one hit, keyed by the hit's own (kind, slot) — an independent relation rather than a payload
/// parallel to <c>Edges.SearchResult</c>, because that relation's commit squeezes out hits with no table and a
/// parallel list would drift off by one.</summary>
public readonly record struct SearchHitEdge(EntityKind Kind, SearchHitFlags Flags);

public sealed partial class Edges
{
    /// <summary>Parent = the query's All row; targets = <see cref="BrowseTable"/> rows (a genre IS a browse node).</summary>
    public readonly EdgeTable<NoEdge> SearchGenres = new();
    /// <summary>Parent = the All row; targets <see cref="Table.None"/>; the payload IS the query.</summary>
    public readonly EdgeTable<SearchRelatedEdge> SearchRelated = new();
    /// <summary>Parent = a search row; targets = the hits' slots in their own tables; payload = kind + chrome flags.</summary>
    public readonly EdgeTable<SearchHitEdge> SearchHits = new();

    /// <summary>Give back one row's related-query strings (before the list is replaced).</summary>
    internal void ReleaseSearchRelatedText(int parent)
    {
        var rows = SearchRelated.Payload(parent);
        for (int i = 0; i < rows.Length; i++) Entities.Strings.Release(rows[i].Query);
    }

    /// <summary>Give back every related-query string this scope owns — walked from <see cref="ReleaseText"/> when the
    /// scope retires (a shared-file patch, reported).</summary>
    public void ReleaseSearchText()
    {
        for (int p = 0; p < SearchRelated.ParentCount; p++) ReleaseSearchRelatedText(p);
    }
}

public readonly partial struct Search
{
    /// <summary>How the hit list was assembled.</summary>
    public SearchRowFlags RowFlags => (SearchRowFlags)T.RowFlags[Slot];

    /// <summary>Has the planner asked for any of these groups this scope (G-040's seal)? A group that is asked and not
    /// known is PENDING; one that is neither was never asked or was un-asked by a failure.</summary>
    public bool Asked(SearchFields fields) => (T.Asked[Slot] & (uint)fields) != 0;

    /// <summary>The genre tiles, as browse-node slots.</summary>
    public ReadOnlySpan<int> GenreSlots => E.SearchGenres.Targets(Slot);
    /// <summary>The related-search strings, in server order.</summary>
    public ReadOnlySpan<SearchRelatedEdge> Related => E.SearchRelated.Payload(Slot);

    /// <summary>How many of this row's hits index <paramref name="kind"/>'s table — the All row's local count per facet.</summary>
    public int CountOf(EntityKind kind)
    {
        var kinds = ResultKinds;
        int n = 0;
        for (int i = 0; i < kinds.Length; i++) if (kinds[i].Kind == kind) n++;
        return n;
    }

    /// <summary>The chrome flags of one hit, or none. A linear probe over at most one page of hits, read at build time.</summary>
    public SearchHitFlags FlagsOf(EntityRef hit)
    {
        if (hit.IsNone) return SearchHitFlags.None;
        var targets = E.SearchHits.Targets(Slot);
        var payload = E.SearchHits.Payload(Slot);
        for (int i = 0; i < targets.Length && i < payload.Length; i++)
            if (targets[i] == hit.Slot && payload[i].Kind == hit.Kind) return payload[i].Flags;
        return SearchHitFlags.None;
    }
}

// ── 6. staging and the commit ────────────────────────────────────────────────────────────────────────────────────────

/// <summary>One decoded search subject: which groups the answer speaks for, its chip strip (a range into
/// <see cref="Staging.SearchChips"/>) and how its hit list was assembled.</summary>
public struct StagedSearch : IStagedRow
{
    public StagedId Id;
    public int ChipStart, ChipCount;
    public byte RowFlags;
    public uint Known;
    public Authority Authority;

    /// <inheritdoc cref="IStagedRow.Init"/>
    public void Init(in StagedId id, Authority authority, uint known) { Id = id; Authority = authority; Known = known; }
    /// <inheritdoc cref="IStagedRow.Identity"/>
    public readonly StagedId Identity => Id;
}

/// <summary>One chip of a staged strip, in server rank order.</summary>
public struct StagedSearchChip
{
    public byte Facet;
    public int Total;
}

/// <summary>Which search relation a <see cref="StagedSearchRun"/> rewrites.</summary>
public enum SearchRelation : byte { Genres, Related, Hits }

/// <summary>One child of a staged search run: a genre node, a related query's text, or a hit and its chrome.</summary>
public struct StagedSearchLink
{
    public StagedId Target;
    public TextRef Text;
    public byte Flags;
}

/// <summary>One parent's rewritten search relation: a slice of <see cref="Staging.SearchLinks"/>.</summary>
public struct StagedSearchRun
{
    public StagedId Parent;
    public SearchRelation Relation;
    public int Start, Length;
}

public sealed partial class Staging
{
    StagedList<StagedSearch>? _searches;
    StagedList<StagedSearchChip>? _searchChips;
    StagedList<StagedSearchLink>? _searchLinks;
    StagedList<StagedSearchRun>? _searchRuns;
    /// <summary>Lazy: a decode that touches no search subject allocates no search list.</summary>
    public StagedList<StagedSearch> Searches => _searches ??= Register(new StagedList<StagedSearch>());
    public StagedList<StagedSearchChip> SearchChips => _searchChips ??= Register(new StagedList<StagedSearchChip>());
    public StagedList<StagedSearchLink> SearchLinks => _searchLinks ??= Register(new StagedList<StagedSearchLink>());
    public StagedList<StagedSearchRun> SearchRuns => _searchRuns ??= Register(new StagedList<StagedSearchRun>());
    internal StagedList<StagedSearch>? StagedSearches => _searches;
    internal StagedList<StagedSearchChip>? StagedSearchChips => _searchChips;
    internal StagedList<StagedSearchLink>? StagedSearchLinks => _searchLinks;
    internal StagedList<StagedSearchRun>? StagedSearchRuns => _searchRuns;
}

public readonly partial struct Search
{
    // UI thread only (C1): one static scratch set, grown ×2, so a steady stream of answers allocates nothing (P8).
    static int[] s_targets = new int[32];
    static SearchRelatedEdge[] s_related = new SearchRelatedEdge[16];
    static SearchHitEdge[] s_hits = new SearchHitEdge[32];

    /// <summary>Land a staged batch of search subjects and their relations. Called from <c>Entities.CommitBrowse</c>
    /// (Browse.cs), after the browse nodes a genre run points at and before <c>CommitEdges</c> lands the hit list.</summary>
    public static void Commit(Staging s)
    {
        var t = Entities.Current.Searches;
        if (s.StagedSearches is { Count: > 0 } staged)
        {
            var chips = s.StagedSearchChips is { } c ? c.Span : default;
            Span<SearchFacet> facets = stackalloc SearchFacet[SearchTable.FacetCount];
            Span<int> totals = stackalloc int[SearchTable.FacetCount];
            foreach (ref var row in staged.Span)
            {
                int slot = s.Slot(t, in row.Id);
                if (slot == Table.None) continue;
                var authority = row.Authority == Authority.None ? s.Authority : row.Authority;
                if (!t.Accepts(slot, row.Known, authority, in t.IdentityAuthority)) continue;
                if ((row.Known & (uint)SearchFields.Chips) != 0)
                {
                    int n = 0;
                    for (int i = 0; i < row.ChipCount && n < facets.Length; i++)
                    {
                        int at = row.ChipStart + i;
                        if ((uint)at >= (uint)chips.Length) break;
                        facets[n] = (SearchFacet)chips[at].Facet;
                        totals[n++] = chips[at].Total;
                    }
                    new Search(slot).SetChips(facets[..n], totals[..n]);
                }
                if ((row.Known & (uint)SearchFields.Results) != 0) t.RowFlags[slot] = row.RowFlags;
                t.Applied(slot, row.Known, authority, ref t.IdentityAuthority);
            }
        }

        if (s.StagedSearchRuns is not { Count: > 0 } runs) return;
        var links = s.StagedSearchLinks is { } l ? l.Span : default;
        var edges = Entities.Current.Edges;
        foreach (ref readonly var run in runs.Span)
        {
            if (run.Start < 0 || run.Length < 0 || run.Start + run.Length > links.Length) continue;
            int parent = s.Slot(t, in run.Parent);
            if (parent == Table.None) continue;
            Grow(run.Length);
            var page = links.Slice(run.Start, run.Length);
            int n = 0;
            switch (run.Relation)
            {
                case SearchRelation.Genres:
                    for (int i = 0; i < page.Length; i++)
                    {
                        int node = s.Slot(Entities.Current.Browses, in page[i].Target);
                        if (node != Table.None) s_targets[n++] = node;
                    }
                    edges.SearchGenres.Replace(parent, s_targets.AsSpan(0, n), default, EdgeState.Complete, n);
                    break;
                case SearchRelation.Related:
                    // AddRef the incoming FIRST, then release the list being replaced: a query that survives keeps its id.
                    for (int i = 0; i < page.Length; i++)
                    {
                        if (page[i].Text.IsEmpty) continue;
                        StringId text = default;
                        Entities.RetainText(ref text, s.Intern(page[i].Text));
                        s_targets[n] = Table.None;
                        s_related[n++] = new SearchRelatedEdge(text);
                    }
                    edges.ReleaseSearchRelatedText(parent);
                    edges.SearchRelated.Replace(parent, s_targets.AsSpan(0, n), s_related.AsSpan(0, n), EdgeState.Complete, n);
                    break;
                default:
                    for (int i = 0; i < page.Length; i++)
                    {
                        var kind = page[i].Target.Kind(s);
                        int target = s.Slot(null, in page[i].Target);
                        if (target == Table.None) continue;
                        s_targets[n] = target;
                        s_hits[n++] = new SearchHitEdge(kind, (SearchHitFlags)page[i].Flags);
                    }
                    edges.SearchHits.Replace(parent, s_targets.AsSpan(0, n), s_hits.AsSpan(0, n), EdgeState.Complete, n);
                    break;
            }
        }
    }

    static void Grow(int n)
    {
        if (n <= s_targets.Length) return;
        int size = s_targets.Length;
        while (size < n) size *= 2;
        s_targets = new int[size];
        s_related = new SearchRelatedEdge[size];
        s_hits = new SearchHitEdge[size];
    }
}

// ── 7. the pure rules (ch 13 §8 — ported verbatim, extracted from 0.2.9 SearchPage.cs and tested) ───────────────────

public readonly partial struct Search
{
    /// <summary>The wire page size a facet asks (0.2.9 <c>SearchPageSize</c>).</summary>
    public const int PageSize = 50;

    /// <summary>The eleven placeholder pill widths — the known facet superset, which wraps to two rows at typical widths.</summary>
    public static readonly float[] ChipSkeletonWidths = [40f, 68f, 76f, 74f, 88f, 68f, 96f, 116f, 76f, 76f, 76f];

    /// <summary>The static fallback facet order (<c>FacetsFrom</c>'s second pass).</summary>
    public static readonly SearchFacet[] FallbackFacets =
    [
        SearchFacet.Tracks, SearchFacet.Albums, SearchFacet.Playlists, SearchFacet.Audiobooks, SearchFacet.Podcasts,
        SearchFacet.Artists, SearchFacet.Episodes, SearchFacet.Profiles, SearchFacet.Genres, SearchFacet.Authors,
    ];

    /// <summary>THE facet list (0.2.9 <c>SearchPage.FacetsFrom</c>): All first; then the server's chip order, deduped
    /// (a chip is real even when its nested list is empty — clicking it runs the dedicated op); then the fallback
    /// superset filtered by "the local list has rows OR the total claims some". <paramref name="totals"/> and
    /// <paramref name="localCounts"/> are indexed by facet (<see cref="NoTotal"/> = the server sent none). Writes into
    /// <paramref name="into"/> (≥ 11) and returns the count.</summary>
    public static int FacetsFrom(ReadOnlySpan<SearchFacet> chipOrder, ReadOnlySpan<int> totals, ReadOnlySpan<int> localCounts,
                                 Span<SearchFacet> into)
    {
        if (into.IsEmpty) return 0;
        uint seen = 1u << (int)SearchFacet.All;
        int n = 0;
        into[n++] = SearchFacet.All;
        for (int i = 0; i < chipOrder.Length && n < into.Length; i++)
        {
            uint bit = 1u << (int)chipOrder[i];
            if ((seen & bit) != 0) continue;
            seen |= bit;
            into[n++] = chipOrder[i];
        }
        for (int i = 0; i < FallbackFacets.Length && n < into.Length; i++)
        {
            var f = FallbackFacets[i];
            uint bit = 1u << (int)f;
            if ((seen & bit) != 0) continue;
            seen |= bit;
            int local = (uint)f < (uint)localCounts.Length ? localCounts[(int)f] : 0;
            int total = (uint)f < (uint)totals.Length ? totals[(int)f] : NoTotal;
            if (local > 0 || TotalFor(total, local) > 0) into[n++] = f;
        }
        return n;
    }

    /// <summary>The All row's own facet list: its chip strip in server RANK order, its local count per facet (the hits of
    /// that facet's kind; the genre tiles for Genres).</summary>
    public int FacetsFrom(Span<SearchFacet> into)
    {
        Span<SearchFacet> order = stackalloc SearchFacet[SearchTable.FacetCount];
        Span<int> totals = stackalloc int[SearchTable.FacetCount];
        Span<int> local = stackalloc int[SearchTable.FacetCount];
        int chips = 0;
        for (int rank = 0; rank < SearchTable.FacetCount; rank++)
            for (int f = 0; f < SearchTable.FacetCount; f++)
                if (Advertises((SearchFacet)f) && RankOf((SearchFacet)f) == rank) order[chips++] = (SearchFacet)f;
        for (int f = 0; f < SearchTable.FacetCount; f++)
        {
            totals[f] = RawTotalOf((SearchFacet)f);
            var kind = KindOf((SearchFacet)f);
            local[f] = (SearchFacet)f == SearchFacet.Genres ? GenreSlots.Length : kind == EntityKind.Unknown ? 0 : CountOf(kind);
        }
        return FacetsFrom(order[..chips], totals, local, into);
    }

    /// <summary>A tab's count (0.2.9 <c>FacetCount</c>): the resolved total when positive, else nothing. The tab omits the
    /// number for All and for 0.</summary>
    public static int FacetCount(int storedTotal, int localCount) => TotalFor(storedTotal, localCount) is > 0 and var n ? n : 0;

    /// <summary>One row of the no-top-hits fallback list.</summary>
    public readonly record struct FallbackRow(SearchFacet Facet, int Index, bool Large);

    /// <summary>The fallback list's ceiling (1 large + 8 tracks + artists to 14 + 4 albums + 4 playlists).</summary>
    public const int FallbackMaxRows = 22;

    /// <summary>THE fallback interleave (0.2.9 <c>SearchAllList.FallbackRows</c>) for an answer with no ranked top hits:
    /// the top artist | album | playlist as the large row, then up to 8 tracks with artists spliced after the third and
    /// the sixth, artists topped up to a 14-row cap, then up to 4 albums and 4 playlists (each skipping the one promoted
    /// to the large row).</summary>
    public static int FallbackRows(int tracks, int artists, int albums, int playlists, Span<FallbackRow> into)
    {
        int n = 0;
        bool topIsArtist = artists > 0;
        bool topIsAlbum = !topIsArtist && albums > 0;
        bool topIsPlaylist = !topIsArtist && !topIsAlbum && playlists > 0;

        if (topIsArtist) Add(into, ref n, new(SearchFacet.Artists, 0, true));
        else if (topIsAlbum) Add(into, ref n, new(SearchFacet.Albums, 0, true));
        else if (topIsPlaylist) Add(into, ref n, new(SearchFacet.Playlists, 0, true));

        int artistIndex = topIsArtist ? 1 : 0;
        int trackCount = Math.Min(tracks, 8);
        for (int i = 0; i < trackCount; i++)
        {
            Add(into, ref n, new(SearchFacet.Tracks, i, false));
            if ((i == 2 || i == 5) && artistIndex < artists) Add(into, ref n, new(SearchFacet.Artists, artistIndex++, false));
        }
        while (artistIndex < artists && n < 14) Add(into, ref n, new(SearchFacet.Artists, artistIndex++, false));

        int albumStart = topIsAlbum ? 1 : 0;
        for (int i = albumStart; i < albums && i < albumStart + 4; i++) Add(into, ref n, new(SearchFacet.Albums, i, false));
        int playlistStart = topIsPlaylist ? 1 : 0;
        for (int i = playlistStart; i < playlists && i < playlistStart + 4; i++)
            Add(into, ref n, new(SearchFacet.Playlists, i, false));
        return n;

        static void Add(Span<FallbackRow> into, ref int n, FallbackRow row) { if (n < into.Length) into[n++] = row; }
    }

    /// <summary>Best matches' cell floor, gap, column cap and the one-column row count (0.2.9 <c>SearchHitsGrid</c>).</summary>
    public const float BestMatchCellMin = 280f, BestMatchGap = 12f, BestMatchRowH = 64f;
    public const int BestMatchMaxCols = 3, SingleColRows = 3;

    /// <summary>THE column count (0.2.9 <c>SearchHitsGrid.ColsFor</c>): <c>clamp(floor((w+12)/292), 1, 3)</c>, growing at
    /// once and shrinking only once the width is 24 DIP past the current count's own need. <paramref name="initialized"/>
    /// false (never measured) takes the nominal count outright.</summary>
    public static int ColsFor(float w, int prev, bool initialized)
    {
        int nominal = w <= 0f ? 1 : Math.Clamp((int)MathF.Floor((w + BestMatchGap) / (BestMatchCellMin + BestMatchGap)), 1, BestMatchMaxCols);
        if (!initialized || nominal >= prev) return nominal;
        float need = prev * BestMatchCellMin + (prev - 1) * BestMatchGap;
        return w < need - Detail.Breakpoints.TierHysteresisDip ? nominal : prev;
    }

    /// <summary>Rows per page: square, except one column keeps three (never a one-cell pager).</summary>
    public static int RowsFor(int cols) => cols <= 1 ? SingleColRows : cols;

    /// <summary>A short list narrows the page: <c>min(cols, ceil(n / rows))</c>, at least one.</summary>
    public static int MaxColumns(int cols, int rows, int count) => Math.Min(cols, Math.Max(1, (count + rows - 1) / Math.Max(1, rows)));

    /// <summary>The All tab's playlist rail (0.2.9 <c>PlaylistShelfItems</c>): the top hits' playlists first, in hit
    /// order, then the Playlists facet's own slots — deduped, and never the hero's own item.</summary>
    public static int PlaylistShelfItems(ReadOnlySpan<EntityRef> hits, ReadOnlySpan<int> playlists, EntityRef skip, Span<int> into)
    {
        int n = 0;
        for (int i = 0; i < hits.Length && n < into.Length; i++)
            if (hits[i].Kind == EntityKind.Playlist) Add(hits[i].Slot, skip, into, ref n);
        for (int i = 0; i < playlists.Length && n < into.Length; i++) Add(playlists[i], skip, into, ref n);
        return n;

        static void Add(int slot, EntityRef skip, Span<int> into, ref int n)
        {
            if (slot <= Table.None || (skip.Kind == EntityKind.Playlist && skip.Slot == slot)) return;
            if (into[..n].IndexOf(slot) >= 0) return;
            into[n++] = slot;
        }
    }

    /// <summary>The owner a top-hit playlist's subtitle names: the text after the first <c>" • "</c> (0.2.9
    /// <c>PlaylistOwnerOf</c>).</summary>
    public static string PlaylistOwnerOf(string subtitle)
    {
        const string sep = " • ";
        int i = subtitle.IndexOf(sep, StringComparison.Ordinal);
        return i < 0 ? subtitle : subtitle[(i + sep.Length)..];
    }

    /// <summary>Where a genre result goes (0.2.9 <c>SearchRoutes.OpenGenre</c>): a <c>spotify:genre:</c> or
    /// <c>spotify:page:</c> uri opens its browse page; anything else re-commits the NAME as a search (a blank name is the
    /// Browse directory). UI thread (the route interns).</summary>
    public static Shell.Route GenreRoute(string uri, string name)
    {
        if ((uri.StartsWith(Browse.GenrePrefix, StringComparison.Ordinal) && uri.Length > Browse.GenrePrefix.Length)
            || (uri.StartsWith(Browse.PagePrefix, StringComparison.Ordinal) && uri.Length > Browse.PagePrefix.Length))
            return BrowseTiles.PageRoute(uri, name);
        return Shell.Parse("search".AsSpan(), name.AsSpan());
    }

    /// <summary>A related link is dropped when it repeats the typed query (OrdinalIgnoreCase).</summary>
    public static bool KeepsRelated(ReadOnlySpan<char> related, ReadOnlySpan<char> typed)
        => !related.IsEmpty && !related.Trim().Equals(typed.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The hero's Play gate (0.2.9: not for a profile, genre or author — none of which 0.3 hits carry but User).</summary>
    public static bool CanPlay(EntityKind kind) => kind is EntityKind.Track or EntityKind.Album or EntityKind.Artist
        or EntityKind.Playlist or EntityKind.Show or EntityKind.Episode;

    /// <summary>The hero's "Open page" gate: a destination with a page (a track's open IS its play).</summary>
    public static bool CanOpen(EntityKind kind) => kind is EntityKind.Album or EntityKind.Artist or EntityKind.Playlist
        or EntityKind.Show or EntityKind.Episode;

    /// <summary>Does a hit of this kind resolve a menu (and therefore a "…" button and a right-click)?</summary>
    public static bool HasMenu(EntityKind kind) => kind is EntityKind.Track or EntityKind.Album or EntityKind.Artist
        or EntityKind.Playlist or EntityKind.Show;

    /// <summary>People are circles.</summary>
    public static bool RoundArt(EntityKind kind) => kind is EntityKind.Artist or EntityKind.User;

    /// <summary>The three loading geometries (ch 13 §0.6) — never one.</summary>
    public enum ShimmerShape : byte { Hero, CardGrid, Rows }

    public static ShimmerShape ShimmerFor(SearchFacet facet) => facet switch
    {
        SearchFacet.All => ShimmerShape.Hero,
        SearchFacet.Albums or SearchFacet.Playlists or SearchFacet.Genres => ShimmerShape.CardGrid,
        _ => ShimmerShape.Rows,
    };

    /// <summary>The facet body slides only on a REAL switch — never on the first render.</summary>
    public static bool Slides(bool armed, int previousChip, int chip) => armed && previousChip != chip;

    /// <summary>A tab's label key.</summary>
    public static string FacetNameKey(SearchFacet facet) => facet switch
    {
        SearchFacet.Tracks => "search.songs",
        SearchFacet.Albums => "search.albums",
        SearchFacet.Playlists => "search.playlists",
        SearchFacet.Audiobooks => "search.audiobooks",
        SearchFacet.Podcasts => "search.podcastsShows",
        SearchFacet.Artists => "search.artists",
        SearchFacet.Episodes => "search.episodes",
        SearchFacet.Profiles => "search.profiles",
        SearchFacet.Genres => "search.genres",
        SearchFacet.Authors => "search.authors",
        _ => "search.all",
    };

    /// <summary>A dedicated facet's own empty sentence, or null for the ONE generic captioned empty (ch 13 §9 gap 1: the
    /// Songs / Albums / Playlists / Artists / All arms all take the captioned one). Genres is an empty box — no sentence.</summary>
    public static string? EmptyKey(SearchFacet facet) => facet switch
    {
        SearchFacet.Audiobooks => "search.noAudiobookResults",
        SearchFacet.Podcasts => "search.noPodcastResults",
        SearchFacet.Episodes => "search.noEpisodeResults",
        SearchFacet.Profiles => "search.noProfileResults",
        SearchFacet.Authors => "search.noAuthorResults",
        _ => null,
    };

    /// <summary>The Albums / Playlists grid (0.2.9 <c>SearchFacetGrid</c>): min column 180, gap 16, card chrome 50,
    /// row gap 20.</summary>
    public const float FacetGridMinCol = 180f, FacetGridGap = 16f, FacetGridChrome = 50f, FacetGridRowGap = 20f;

    /// <summary>How far a dedicated facet's list is paged by the page's own demand (the extent the grid reserves): the
    /// wire refuses offsets past ~1,000, and 0.2.9's scroll-driven grid never went near it in practice.</summary>
    public const int FacetHitCeiling = 500;

    /// <summary>Does a facet's list keep paging? The edge says there is more and the ceiling is not reached.</summary>
    public static bool KeepsPaging(EdgeState state, int count) => state == EdgeState.Partial && count < FacetHitCeiling;

    public static int FacetGridColumns(float w) =>Math.Max(1, (int)MathF.Floor((w + FacetGridGap) / (FacetGridMinCol + FacetGridGap)));

    public static float FacetGridCellWidth(float w, int cols) => MathF.Max(90f, (w - (cols - 1) * FacetGridGap) / Math.Max(1, cols));
}
