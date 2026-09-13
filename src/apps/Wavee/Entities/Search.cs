// ── Entities/Search.cs — CORE (owner B, wave 1; plan §2, ch 13 §7) ───────────────────────────────────────────────────
//
// A SEARCH IS A SUBJECT ROW, ONE PER (query, facet). 0.2.9 issued up to FOUR requests per search page from three
// different components — the facet results, the genres op, the suggest op and page 1+ of a facet grid — and held the
// answers in page-local `UseResource`s that a remount threw away. In 0.3 the query is a row like any other:
//
//        Searches["wavee:search:00:daft punk"] ── Edges.SearchResult ──▶ (kind, slot) rows  ← the All facet
//        Searches["wavee:search:01:daft punk"] ── Edges.SearchResult ──▶ (Track, slot) rows ← the Songs facet
//        …                                                                                   one row per facet
//
// ONE ROW PER FACET, and not one row with eleven lists, for one reason: the result relation is ONE
// <c>EdgeTable</c> keyed by parent slot, so eleven facets need eleven parents. It also happens to be the right model —
// each facet pages independently, carries its own <c>State</c>/<c>Total</c>, and a facet nobody opened has honestly
// never been asked for. The chip strip is shared, so it lives on the <see cref="SearchFacet.All"/> row and the sibling
// rows point back at it through <see cref="Search.All"/>.
//
// THE CHIP TOTALS ARE A STRIDED SLAB, not eleven columns and not a dictionary: eleven ints and eleven bytes per row,
// indexed <c>slot * FacetCount + facet</c>. The facet count is fixed by the wire and has been for the life of the
// endpoint (P7, P2).
//
// THE ONE PIECE OF PORTED SEMANTICS HERE IS `TotalFor`, and it is subtle enough to be worth stating: a per-facet total
// of -1 means THE SERVER SENT NO TOTAL, in which case the answer is the local list's own count. So `TotalFor` never
// distinguishes "not queried" from "queried and empty" — it just answers 0 — and `HasAny` is the separate question
// (0.2.9 `Library.cs:92-124`). Reproducing that exactly is what keeps a facet tab from showing a count it invented.
//
// A HIT CARRIES ITS TABLE (defect 5 of the identity investigation,
// docs/plans/wavee/wavee-0.3-entity-identity-memory.md §3.1 requirement 4). This relation was an
// `EdgeTable<NoEdge>` over "entity slots" with NOTHING recording which table each slot indexed — and the All facet
// mixes tracks, albums, artists, playlists, shows and profiles in ONE ranked order, where album slot 5 and track
// slot 5 are the same `int`. It is now an `EdgeTable<KindEdge>`: one byte per hit (not a whole 24-byte `EntityId`
// — the target already names the row), so a reader pairs the two into an <see cref="EntityRef"/> and knows exactly
// what it is looking at. A SINGLE-kind facet writes the same byte in every entry, which costs one byte a hit and
// means no reader has to know which facet it is reading.
//
// THE QUERY TEXT IS REF-COUNTED (defect 1, doc §4.4). A search row's uri is `wavee:search:<facet>:<query>` — never
// a gid — so it keeps its interned string, and so does <see cref="SearchTable.Query"/>. Typing into the omnibar
// mints a row per keystroke-batch; without the release half, an afternoon's searching would pin every prefix of
// every query the user ever typed for the life of the process. The identity string is released by
// <see cref="Table.FreeSlot"/>, the query column by <see cref="SearchTable.ReleaseText"/>.
//
// What is NOT here: `OmnibarSuggestQuery` + `SuggestState`, `SearchChipSkeletonPolicy`'s call sites, `GhostFor`,
// `FacetsFrom`, the fallback interleave and `ColsFor` — ch 13 §8's rule set, owner P's, Wave 5, in this same file.
// Wave 1 owns the columns they read.

using System.Buffers;
using FluentGpu.Foundation;

namespace Wavee;

// ── 1. facets and field groups ───────────────────────────────────────────────────────────────────────────────────────

/// <summary>The eleven search facets, ported member-for-member from 0.2.9's <c>SearchFacet</c> — the order IS the
/// static fallback order the chip strip falls back to when the server sends no <c>chipOrder</c>, so it is not free to
/// change (0.2.9 <c>SearchPage.cs:328-356</c>).</summary>
public enum SearchFacet : byte
{
    All = 0, Tracks, Albums, Playlists, Audiobooks, Podcasts, Artists, Episodes, Profiles, Genres, Authors,
}

/// <summary>Which column groups of a search row are filled (P3).</summary>
[Flags]
public enum SearchFields : uint
{
    /// <summary>The chip strip: which facets exist, in what server rank, with what totals. Only the
    /// <see cref="SearchFacet.All"/> row carries it.</summary>
    Chips = 1 << 0,
    /// <summary>This facet's own result list has been answered for. The rows are the EDGE; this bit says somebody
    /// replied, which an edge state cannot say while a warm read is in flight.</summary>
    Results = 1 << 1,
    /// <summary>The genre tiles (0.2.9's SECOND request, folded into the one demand).</summary>
    Genres = 1 << 2,
    /// <summary>The related-search strings (0.2.9's THIRD request, likewise).</summary>
    Related = 1 << 3,

    All = Chips | Results | Genres | Related,
}

// ── 2. the table ─────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The search subjects. Never persisted by the store — a query is a session fact, and warming yesterday's
/// results would be worse than a skeleton.</summary>
public sealed class SearchTable : Table
{
    /// <summary>How many facets each strided chip row holds. Fixed by the wire.</summary>
    public const int FacetCount = 11;

    /// <summary>The query text, interned once. The page echoes it from frame one and never skeletons it (ch 13 §7).</summary>
    public Column<StringId> Query;
    /// <summary>Which facet THIS row is (a <see cref="SearchFacet"/>).</summary>
    public Column<byte> Facet;

    /// <summary>Which facets the server's chip order mentioned, one bit per <see cref="SearchFacet"/>. Presence is a
    /// separate question from a total: a facet can be advertised with a total of 0.</summary>
    public Column<ushort> ChipMask;
    /// <summary>Per-facet total, strided <c>slot * <see cref="FacetCount"/> + facet</c>. -1 = THE SERVER SENT NO TOTAL
    /// (see <see cref="Search.TotalFor"/>); 0 is a real zero.</summary>
    public Column<int> ChipTotals;
    /// <summary>Per-facet server rank, same stride. 255 = unranked, which sorts last.</summary>
    public Column<byte> ChipRanks;

    public Column<byte> IdentityAuthority;

    /// <summary>A search subject is not a catalog entity: nothing addresses it, the store never warms it, and its
    /// <see cref="Table.Id"/> is the TEXT form of <c>wavee:search:&lt;facet&gt;:&lt;query&gt;</c>.</summary>
    public override EntityKind Kind => EntityKind.Unknown;

    /// <summary>Give back every string a search row owns (defect 1): the query text. The chip slab holds no strings —
    /// totals and ranks are numbers — so this is one line, and it is one line that matters: a session's omnibar
    /// mints a row per query.</summary>
    protected override void ReleaseText(int slot) => ClearText(ref Query, slot);

    /// <summary>Clear a row's chip strip to "the server said nothing about any facet" — the state every fresh row must
    /// start in, because a zeroed total would read as a real zero (see <see cref="Search.NoTotal"/>).</summary>
    public void ResetChips(int slot)
    {
        int at = slot * FacetCount;
        var totals = ChipTotals.Span.Slice(at, FacetCount);
        var ranks = ChipRanks.Span.Slice(at, FacetCount);
        for (int i = 0; i < FacetCount; i++) { totals[i] = Search.NoTotal; ranks[i] = Search.Unranked; }
        ChipMask[slot] = 0;
    }

    protected override void GrowColumns(int capacity)
    {
        Query.EnsureCapacity(capacity);
        Facet.EnsureCapacity(capacity);
        ChipMask.EnsureCapacity(capacity);
        ChipTotals.EnsureCapacity(capacity * FacetCount);
        ChipRanks.EnsureCapacity(capacity * FacetCount);
        IdentityAuthority.EnsureCapacity(capacity);
    }
}

/// <summary>The search half of the scope's synthetic subjects (see <c>Home.cs</c> for the rest, and for why they are
/// deliberately absent from <c>Scope.Tables</c>).</summary>
public sealed partial class Scope
{
    public readonly SearchTable Searches = new();
}

// ── 3. the handle ────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>One (query, facet) subject. Partial so Wave 5 adds ch 13 §8's rule set to the same type.</summary>
public readonly partial struct Search(int slot) : IEquatable<Search>
{
    /// <summary>The subject uri prefix. The facet index is in the uri because a facet is a different ROW, and a row is
    /// found by uri (<c>wavee:search:&lt;facet&gt;:&lt;query&gt;</c>).</summary>
    public const string Prefix = "wavee:search:";

    /// <summary>"The server sent no total for this facet." Distinct from 0, and the whole reason
    /// <see cref="TotalFor"/> exists (0.2.9 <c>Library.cs:92-124</c>).</summary>
    public const int NoTotal = -1;
    /// <summary>"The server's chip order did not rank this facet." Sorts last.</summary>
    public const byte Unranked = 255;

    static SearchTable T => Entities.Current.Searches;
    static Edges E => Entities.Current.Edges;

    public int Slot { get; } = slot;

    public bool IsValid => Slot > Table.None && Slot < T.Count;
    public uint Version => T.Version[Slot];
    public bool Knows(SearchFields fields) => T.Knows(Slot, (uint)fields);

    /// <summary>THE subject's identity, packed (<c>Entities.cs</c> §2). Always the TEXT form: <c>wavee:</c> is not a
    /// provider the gid parse claims, and a query is not a gid.</summary>
    public EntityId Id => T.Id[Slot];

    /// <summary>The query, verbatim. Ready from frame one — the page echoes it and never skeletons it.</summary>
    public StringId QueryId => T.Query[Slot];
    public SearchFacet Facet => (SearchFacet)T.Facet[Slot];

    /// <summary>The row that carries the chip strip for this query: the <see cref="SearchFacet.All"/> sibling, or this
    /// row when it already is one.</summary>
    public Search All => Facet == SearchFacet.All ? this : Entities.Search(Entities.Strings.Resolve(QueryId).AsSpan());

    // ── the chip strip (ch 13 §7 gap 1) ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Did the server's chip order mention this facet at all?</summary>
    public bool Advertises(SearchFacet facet) => (T.ChipMask[Slot] & (1 << (int)facet)) != 0;

    /// <summary>The server's own rank for a facet, or <see cref="Unranked"/>. The chip strip renders in THIS order,
    /// not in the enum's, whenever the server gave one.</summary>
    public byte RankOf(SearchFacet facet) => T.ChipRanks[Slot * SearchTable.FacetCount + (int)facet];

    /// <summary>The raw stored total: <see cref="NoTotal"/> when the server sent none. Prefer
    /// <see cref="TotalFor"/>, which resolves that.</summary>
    public int RawTotalOf(SearchFacet facet) => T.ChipTotals[Slot * SearchTable.FacetCount + (int)facet];

    /// <summary>THE ported total rule. A stored <see cref="NoTotal"/> falls back to the local list's own count, which
    /// is why this never distinguishes "not queried" from "queried and empty" — it just answers 0, exactly as 0.2.9
    /// does. The facet tab shows the number only when it is <c>&gt; 0</c> and the facet is not All.</summary>
    public int TotalFor(SearchFacet facet, int localCount) => TotalFor(RawTotalOf(facet), localCount);

    /// <inheritdoc cref="TotalFor(SearchFacet,int)"/>
    public static int TotalFor(int storedTotal, int localCount) => storedTotal == NoTotal ? localCount : storedTotal;

    /// <summary>Which entity kind a SINGLE-kind facet's hits are, for the writer that has to stamp
    /// <see cref="KindEdge"/> on them (defect 5). <see cref="SearchFacet.All"/> answers
    /// <see cref="EntityKind.Unknown"/> because that facet is the mixed one and its writer must stamp per hit; so do
    /// the three facets 0.3 has no table for — <see cref="SearchFacet.Audiobooks"/> (the wire spells them as shows in
    /// some markets and as their own kind in others: UNVERIFIED, so not guessed here),
    /// <see cref="SearchFacet.Genres"/> (a browse node, not an entity) and <see cref="SearchFacet.Authors"/>.</summary>
    public static EntityKind KindOf(SearchFacet facet) => facet switch
    {
        SearchFacet.Tracks => EntityKind.Track,
        SearchFacet.Albums => EntityKind.Album,
        SearchFacet.Playlists => EntityKind.Playlist,
        SearchFacet.Podcasts => EntityKind.Show,
        SearchFacet.Artists => EntityKind.Artist,
        SearchFacet.Episodes => EntityKind.Episode,
        SearchFacet.Profiles => EntityKind.User,
        _ => EntityKind.Unknown,
    };

    /// <summary>Write the whole chip strip from a server answer, in rank order. Facets the answer does not mention
    /// keep <see cref="NoTotal"/> — an omission is not a zero.</summary>
    public void SetChips(ReadOnlySpan<SearchFacet> facets, ReadOnlySpan<int> totals)
    {
        T.ResetChips(Slot);
        int at = Slot * SearchTable.FacetCount;
        ushort mask = 0;
        int n = facets.Length < totals.Length ? facets.Length : totals.Length;
        for (int i = 0; i < n; i++)
        {
            int f = (int)facets[i];
            if ((uint)f >= SearchTable.FacetCount) continue;
            T.ChipTotals[at + f] = totals[i];
            T.ChipRanks[at + f] = (byte)(i < Unranked ? i : Unranked - 1);
            mask |= (ushort)(1 << f);
        }
        T.ChipMask[Slot] = mask;
        T.Bump(Slot, (uint)SearchFields.Chips);
    }

    /// <summary>Does the facet row get a chip at all? Ported: chip order first, then the static fallback filtered by
    /// "the local list has rows OR the server claims some" (0.2.9 <c>SearchPage.FacetsFrom</c>).</summary>
    public bool ShowsChip(SearchFacet facet, int localCount)
        => Advertises(facet) || localCount > 0 || TotalFor(facet, localCount) > 0;

    /// <summary>THE chip-skeleton gate, 0.2.9's <c>SearchChipSkeletonPolicy</c> verbatim: the eleven-pill skeleton
    /// shows only while nothing has EVER supplied a chip source and a fetch is pending. A later facet-switch fetch
    /// must not re-trigger it, which is what the first half prevents.</summary>
    public static bool ShowChipSkeleton(bool hasChipSource, bool pending) => !hasChipSource && pending;

    // ── results ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>This facet's hits as row SLOTS, in server order — the first is the Top Result. A slot alone is not an
    /// identity on the All facet: pair it with <see cref="ResultKinds"/>, or read
    /// <see cref="ResultRef(int)"/>. Read the span rule in <c>Edges.cs</c>'s header.</summary>
    public ReadOnlySpan<int> ResultSlots => E.SearchResult.Targets(Slot);
    /// <summary>WHICH TABLE each hit indexes, parallel to <see cref="ResultSlots"/> (defect 5, file header).</summary>
    public ReadOnlySpan<KindEdge> ResultKinds => E.SearchResult.Payload(Slot);
    public EdgeState ResultState => E.SearchResult.State(Slot);
    public int ResultCount => E.SearchResult.Count(Slot);
    /// <summary>How many hits there will be — the facet grid's real extent while it is still paging.</summary>
    public int ResultTotal => E.SearchResult.Total(Slot);
    public uint ResultVersion => E.SearchResult.Version(Slot);

    /// <summary>Hit <paramref name="index"/> as a cross-kind row pointer — what a mixed row binds to.
    /// <see cref="EntityRef.IsNone"/> past the end, and for a hole a page has not landed in yet (a zeroed target reads
    /// as slot 0 = "none", P3).</summary>
    public EntityRef ResultRef(int index)
    {
        var slots = ResultSlots;
        if ((uint)index >= (uint)slots.Length) return default;
        var kinds = ResultKinds;
        return index < kinds.Length ? kinds[index].Ref(slots[index]) : default;
    }

    /// <summary>Land a page of this facet's hits, each stamped with the table it indexes. The grid asks for its WHOLE
    /// model and the planner decides what to send; there is no <c>EnsureRange</c> and no page-side window (CLAUDE.md,
    /// ch 13 §7).</summary>
    public void ApplyResults(int offset, ReadOnlySpan<int> hits, ReadOnlySpan<KindEdge> kinds, int total)
        => E.SearchResult.ReplacePage(Slot, offset, hits, kinds, total);

    /// <summary>Land a page of the MIXED facet from cross-kind row pointers — the shape a decoder holds after
    /// <c>Entities.Ref(id)</c> per hit. Splits into the two parallel spans the CSR wants through POOLED buffers, so a
    /// 100-hit page allocates nothing after warm-up (P8).</summary>
    public void ApplyResults(int offset, ReadOnlySpan<EntityRef> hits, int total)
    {
        int n = hits.Length;
        // An explicitly-typed empty page, not `ApplyResults(offset, default, default, total)`: the two four-argument
        // overloads below both accept `default` for their third parameter, so that call is ambiguous.
        if (n == 0) { E.SearchResult.ReplacePage(Slot, offset, default, default, total); return; }
        int[] slots = ArrayPool<int>.Shared.Rent(n);
        KindEdge[] kinds = ArrayPool<KindEdge>.Shared.Rent(n);
        try
        {
            for (int i = 0; i < n; i++) { slots[i] = hits[i].Slot; kinds[i] = new KindEdge(hits[i].Kind); }
            ApplyResults(offset, slots.AsSpan(0, n), kinds.AsSpan(0, n), total);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(slots);
            ArrayPool<KindEdge>.Shared.Return(kinds);
        }
    }

    /// <summary>Land a page of a SINGLE-kind facet: every hit gets the same one-byte payload
    /// (<see cref="KindOf(SearchFacet)"/> is what the caller passes). The kind is explicit rather than read off
    /// <see cref="Facet"/> on purpose — the All facet would silently stamp <see cref="EntityKind.Unknown"/> on every
    /// hit and the mixed rows would be exactly as ambiguous as before.</summary>
    public void ApplyResults(int offset, ReadOnlySpan<int> hits, EntityKind kind, int total)
    {
        int n = hits.Length;
        if (n == 0) { E.SearchResult.ReplacePage(Slot, offset, default, default, total); return; }
        KindEdge[] kinds = ArrayPool<KindEdge>.Shared.Rent(n);
        try
        {
            var edge = new KindEdge(kind);
            kinds.AsSpan(0, n).Fill(edge);
            ApplyResults(offset, hits, kinds.AsSpan(0, n), total);
        }
        finally { ArrayPool<KindEdge>.Shared.Return(kinds); }
    }

    public bool Equals(Search other) => other.Slot == Slot;
    public override bool Equals(object? o) => o is Search s && s.Slot == Slot;
    public override int GetHashCode() => Slot;
    public static bool operator ==(Search a, Search b) => a.Slot == b.Slot;
    public static bool operator !=(Search a, Search b) => a.Slot != b.Slot;
}

// ── 4. factories and batch sugar ─────────────────────────────────────────────────────────────────────────────────────

public static partial class Entities
{
    /// <summary>The <see cref="SearchFacet.All"/> subject for a query (D10: allocates if unseen).</summary>
    public static Search Search(ReadOnlySpan<char> query) => Search(query, SearchFacet.All);

    /// <summary>The subject for one (query, facet). The uri is <c>wavee:search:&lt;facet&gt;:&lt;query&gt;</c>; a query
    /// longer than the stack buffer is truncated rather than refused, because a 500-character search box is a user
    /// mistake and not a reason to throw on the UI thread.</summary>
    public static Search Search(ReadOnlySpan<char> query, SearchFacet facet)
    {
        Span<char> uri = stackalloc char[EntityUri.StackChars];
        int n = 0;
        Wavee.Search.Prefix.AsSpan().CopyTo(uri);
        n += Wavee.Search.Prefix.Length;
        uri[n++] = (char)('0' + (int)facet / 10);
        uri[n++] = (char)('0' + (int)facet % 10);
        uri[n++] = ':';
        int take = query.Length < uri.Length - n ? query.Length : uri.Length - n;
        query[..take].CopyTo(uri[n..]);
        n += take;

        var table = Current.Searches;
        bool fresh = !table.TryGetSlot(uri[..n], out _);
        int slot = table.Slot(uri[..n]);
        if (fresh)
        {
            // SetText, never `table.Query[slot] = …`: the row OWNS this string and gives it back in
            // `SearchTable.ReleaseText` (defect 1, file header) — a session's omnibar mints a row per query.
            table.SetText(ref table.Query, slot, Strings.Intern(query[..take]));
            table.Facet[slot] = (byte)facet;
            table.ResetChips(slot);                                 // omission is NOT a zero: start at NoTotal
        }
        return new Search(slot);
    }

    /// <inheritdoc cref="Ensure(Table,ReadOnlySpan{int},uint,FetchPriority)"/>
    public static void Ensure(Search row, SearchFields wanted, FetchPriority priority = FetchPriority.Visible)
    {
        int slot = row.Slot;
        Ensure(Current.Searches, new ReadOnlySpan<int>(in slot), (uint)wanted, priority);
    }
}
