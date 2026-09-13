// ── Entities/Home.cs — CORE (owner B, wave 1; plan §2, ch 10 §7, ch 12) ──────────────────────────────────────────────
//
// HOME IS A SUBJECT, NOT A PAGE MODEL. 0.2.9 rebuilt a `HomeFeed` record — greeting, chips, sections, cards, all of it
// — on every load, handed it to the page, and let the page hold it; a keep-alive eviction threw the whole thing away
// and the next mount painted a skeleton over data the app had had thirty seconds earlier. In 0.3 the feed is TABLE
// ROWS with the same lifetime as every other row:
//
//        Homes[home(facet)] ── Edges.HomeSection ──▶ Sections[…] ── (section cards) ──▶ entity slots
//              │                                          │
//              greeting, chip strip                       title, subtitle, kind, totals, paging cursor
//
// ONE SECTION TABLE, TWO FAMILIES. Home's composer and Browse's page both produce "a titled band of cards with a
// server total and a paging cursor", and plan §2 gives BOTH drill pages to one class (`Home.SectionPage`, serving
// `home-section:` and `browse-section:` and switching on the PREFIX). So there is one <see cref="SectionTable"/> and
// one <see cref="SectionKind"/> whose members are namespaced by family. Two tables would be two paging
// implementations behind one page.
//
// THE READINESS RULES ARE THE POINT OF THIS FILE (ch 10 §7). Three separate questions, three separate answers, and
// 0.2.9 got each of them wrong in a different way:
//
//   • <see cref="Home.Classify"/> — the WHOLE-PAGE gate. `Store.Warm` paints yesterday's rows at boot, so
//     "sectionCount > 0" is not readiness; the live attempt must have CONCLUDED first. This is the "cached shelves
//     painted, then replaced 1.5 s later" shape the gate exists to prevent.
//   • <see cref="Home.ChromeConcluded"/> — the 1,500 ms hold. "Concluded" here means NOTHING IS COMING, not
//     "something arrived": an Idle notification bridge (offline, `--fake`) is concluded, and getting that wrong is the
//     difference between a 0 ms and a 1,500 ms hold on every offline launch.
//   • <see cref="Section.IsComplete"/> / <see cref="SectionPaging"/> — a SECTION reveals complete, and completeness is
//     the server's cursor, not its total. Measured on a captured Home: 7 of 31 sections disagreed with their own
//     `totalCount`, and a complete section can answer `nextOffset: 0`. Paging on the deduped card count walks the
//     cursor backwards; paging on the total never terminates.
//
// Everything here answers those questions from COLUMNS. No page ever probes, no page ever counts (the repo's
// derived-facts rule).
//
// THE SUBJECTS OWN THEIR TEXT, AND GIVE IT BACK (defect 1 of the identity investigation,
// docs/plans/wavee/wavee-0.3-entity-identity-memory.md §4.4). A Home subject and a section are both the TEXT form of
// the packed identity — `wavee:home[:<facet>]` and a section's own uri are not gids — so every one of them keeps an
// interned string, and the engine reclaims a string only when its last reference is released (a never-AddRef'd id is
// PERMANENT, the engine's `StringTable.cs:26`). A facet switch mints a subject, a feed refresh mints ~31 section rows
// and a chip strip is rewritten whole every time the server answers: without the release half, an afternoon of
// browsing pins every greeting, every band title and every abandoned chip set for the life of the process.
//   • row-indexed columns — `Greeting`, `Facet`, a section's `Title`/`Subtitle` — are written through
//     <see cref="Table.SetText"/> and released in each table's `ReleaseText`;
//   • the chip SLABS are addressed by a per-row RANGE, not by slot, so `SetText` cannot reach them: they use the pair
//     one level down, `Entities.RetainText` / `Entities.ReleaseText`, in <see cref="HomeTable.SetChips"/>.
//
// What is NOT here: `Home.Layout`, the two projections, the estimators, the hero geometry and the wash — ch 10 §9's
// 1,800-line rule set, owner P's, Wave 5, in this same file. Wave 1 owns the columns they read.

using FluentGpu.Foundation;

namespace Wavee;

// ── 1. field groups and section kinds ────────────────────────────────────────────────────────────────────────────────

/// <summary>Which column groups of a Home subject row are filled (P3).</summary>
[Flags]
public enum HomeFields : uint
{
    /// <summary>The server's transformed greeting label. Absent is fine — the local-clock fallback is always
    /// renderable — which is why it is its own bit and not part of a bundle (ch 10 §7).</summary>
    Greeting = 1 << 0,
    /// <summary>The facet chip strip. 0 chips is a real answer: no strip, and the greeting row still renders.</summary>
    Chips = 1 << 1,
    /// <summary>The section list itself has been answered for (whether or not it is empty). The EDGE carries the
    /// rows; this bit carries "somebody replied", which is what <see cref="Home.Classify"/> needs and what an edge
    /// state alone cannot say while the store is warming rows from disk.</summary>
    Sections = 1 << 2,

    All = Greeting | Chips | Sections,
}

/// <summary>Which column groups of a section row are filled (P3).</summary>
[Flags]
public enum SectionFields : uint
{
    /// <summary>Title, subtitle, kind and the server's totals — one answer fills all of them.</summary>
    Identity = 1 << 0,
    /// <summary>The server's own accent for the band (browse pages carry one; Home sections usually do not).</summary>
    Accent = 1 << 1,

    All = Identity | Accent,
}

/// <summary>What KIND of band a section row is, namespaced by the composer that produced it.
///
/// <para>The Home members are the composer's <c>__typename</c> verdict, read at decode
/// (0.2.9 <c>SpotifyHomeComposer.cs:60-110</c>) — and it must be a column, because both projections depend on it and
/// the card kinds alone cannot reproduce it (ch 10 §7 gap 3). The Browse members are 0.2.9's
/// <c>BrowseSectionKind</c> verbatim.</para></summary>
public enum SectionKind : byte
{
    Unknown = 0,
    /// <summary>A generic Home shelf.</summary>
    HomeGeneric = 1,
    /// <summary>`HomeSpotlightSectionData` — the hero band.</summary>
    HomeSpotlight = 2,
    /// <summary>`HomeFeedBaselineSectionData` — the eyebrow-stamped baseline shelf.</summary>
    HomeBaseline = 3,
    /// <summary>`HomeRecentlyPlayedSectionData`.</summary>
    HomeRecentlyPlayed = 4,
    /// <summary>A browse shelf of entity cards.</summary>
    BrowseShelf = 16,
    /// <summary>A grid of further browse categories — Browse is a TREE and this is the branch node.</summary>
    BrowseCategoryGrid = 17,
    /// <summary>The trailing "related categories" block: same content as a grid, different placement.</summary>
    BrowseRelated = 18,
}

/// <summary>The whole-page readiness verdict — 0.2.9's <c>HomeFeedReadiness.Classify</c>.</summary>
public enum HomeState : byte
{
    /// <summary>Keep the skeleton: the warm rows are provisional and the live attempt has not concluded.</summary>
    Placeholder = 0,
    Ready = 1,
    /// <summary>Concluded, and there is genuinely nothing. A real, renderable empty — not a skeleton forever.</summary>
    Empty = 2,
}

// ── 2. the tables ────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The Home subjects: ONE ROW PER (scope, facet). A facet switch is a different row, not a mutation of this
/// one, so switching back is instant and the two never race (ch 10 §7).</summary>
public sealed class HomeTable : Table
{
    /// <summary>The server's transformed greeting label.</summary>
    public Column<StringId> Greeting;
    /// <summary>The facet this row is the feed FOR; empty = unfiltered.</summary>
    public Column<StringId> Facet;

    /// <summary>This row's chips: a range into the three shared slabs below. Chips are NOT entities — they have no uri
    /// worth a row and nothing links to them — so they are a slab, exactly like the user row's content filters
    /// (ch 10 §7: "not entities; do not force them into a slab" refers to a TABLE, which this is not).</summary>
    public Column<int> ChipStart, ChipCount;

    public Column<StringId> ChipIds, ChipLabels;
    /// <summary>Index into the same slabs of the chip this one hangs under, or -1 for a top-level chip. That is how
    /// <c>HomeChip.SubChips</c> survives being flattened.</summary>
    public Column<int> ChipParents;
    /// <summary>Bump allocator over the three chip slabs; a rewrite abandons the old range (P5).</summary>
    public int ChipTail;

    public Column<byte> IdentityAuthority;

    public override EntityKind Kind => EntityKind.Unknown;

    /// <summary>Write a row's whole chip strip and point the row at it. The previous range's CELLS are abandoned to
    /// the bump allocator (P5) — but its STRINGS are given back first, or every re-answer of the strip leaks a whole
    /// strip (defect 1, file header). <c>ChipParents</c> is an <c>int</c> slab and owns nothing.</summary>
    public void SetChips(int slot, ReadOnlySpan<StringId> ids, ReadOnlySpan<StringId> labels, ReadOnlySpan<int> parents)
    {
        ReleaseChips(slot);                               // FIRST: the range we are about to abandon owns its strings
        int n = ids.Length;
        if (labels.Length < n) n = labels.Length;
        if (parents.Length < n) n = parents.Length;
        ChipIds.EnsureCapacity(ChipTail + n);
        ChipLabels.EnsureCapacity(ChipTail + n);
        ChipParents.EnsureCapacity(ChipTail + n);
        for (int i = 0; i < n; i++)
        {
            Entities.RetainText(ref ChipIds[ChipTail + i], ids[i]);
            Entities.RetainText(ref ChipLabels[ChipTail + i], labels[i]);
        }
        parents[..n].CopyTo(ChipParents.Span.Slice(ChipTail, n));
        ChipStart[slot] = ChipTail;
        ChipCount[slot] = n;
        ChipTail += n;
    }

    /// <summary>Give one row's chip strings back and forget the range. Idempotent: a second call sees a zero count.</summary>
    void ReleaseChips(int slot)
    {
        int start = ChipStart[slot], count = ChipCount[slot];
        for (int i = 0; i < count; i++)
        {
            Entities.ReleaseText(ref ChipIds[start + i]);
            Entities.ReleaseText(ref ChipLabels[start + i]);
        }
        ChipCount[slot] = 0;
    }

    /// <summary>Give back every string a Home subject owns (defect 1): the greeting, the facet id and the chip range.
    /// Called by <see cref="Table.FreeSlot"/> and by <see cref="Table.ReleaseAllText"/> when the scope retires (D9) —
    /// which for this table is the common case, because a locale or account switch is exactly what invalidates a feed.</summary>
    protected override void ReleaseText(int slot)
    {
        ClearText(ref Greeting, slot);
        ClearText(ref Facet, slot);
        ReleaseChips(slot);
    }

    protected override void GrowColumns(int capacity)
    {
        Greeting.EnsureCapacity(capacity);
        Facet.EnsureCapacity(capacity);
        ChipStart.EnsureCapacity(capacity);
        ChipCount.EnsureCapacity(capacity);
        IdentityAuthority.EnsureCapacity(capacity);
    }
}

/// <summary>The section rows, shared by Home and Browse (see the file header). Keyed by the section's own uri, which
/// is what makes the `home-section:` / `browse-section:` drill page a route over a row rather than a page that has to
/// be handed its model.</summary>
public sealed class SectionTable : Table
{
    public Column<StringId> Title, Subtitle;
    /// <summary>The section's <see cref="SectionKind"/>. Named Form, not Kind: <c>Table.Kind</c> is the abstract
    /// EntityKind every table answers with, and a section is not an entity kind.</summary>
    public Column<byte> Form;
    /// <summary>The server's item count. AN ARMING HINT, NEVER A TERMINATOR (see <see cref="SectionPaging"/>).</summary>
    public Column<int> Total;
    /// <summary>How many items the endpoint has actually handed us, duplicates and unsupported entries INCLUDED. This
    /// — not the deduped card count — is the paging cursor: paging on the deduped count walks the cursor backwards by
    /// exactly the number of items we dropped and re-fetches them forever (0.2.9 <c>HomeSectionPaging</c>).</summary>
    public Column<int> Raw;
    /// <summary>The server's own <c>pagingInfo.nextOffset</c> for this section, in three states —
    /// <see cref="SectionPaging.NoCursor"/>, <see cref="SectionPaging.Complete"/>, or a real offset (ch 13 §7 gap 8).</summary>
    public Column<int> NextOffset;
    /// <summary>How many cards the band actually HOLDS after dedupe — the fourth term of the ledger
    /// <c>Raw == Cards + Unsupported + Duplicates</c>, written at commit beside the card edge.
    /// <para>It is a COLUMN and not a count over the card edge on purpose: "is this band whole" is asked once per band
    /// per frame by the reveal ramp, and the answer must be a column read rather than a page counting an edge (the
    /// repo's derived-facts rule). The two are written together and cannot disagree.</para></summary>
    public Column<int> Cards;
    /// <summary>Cards the composer dropped as unsupported, and as duplicates. Kept because the ledger above is what
    /// the paging arithmetic is checked against, and because the Fold tile reports them.</summary>
    public Column<ushort> Unsupported, Duplicates;
    public Column<uint> Accent;

    public Column<byte> IdentityAuthority;

    public override EntityKind Kind => EntityKind.Unknown;

    /// <summary>Give back the two strings a section row owns (defect 1). ~31 rows are minted per feed answer and a
    /// facet switch replaces all of them, so this is the difference between a bounded floor and a rising one.</summary>
    protected override void ReleaseText(int slot)
    {
        ClearText(ref Title, slot);
        ClearText(ref Subtitle, slot);
    }

    protected override void GrowColumns(int capacity)
    {
        Title.EnsureCapacity(capacity);
        Subtitle.EnsureCapacity(capacity);
        Form.EnsureCapacity(capacity);
        Total.EnsureCapacity(capacity);
        Raw.EnsureCapacity(capacity);
        Cards.EnsureCapacity(capacity);
        NextOffset.EnsureCapacity(capacity);
        Unsupported.EnsureCapacity(capacity);
        Duplicates.EnsureCapacity(capacity);
        Accent.EnsureCapacity(capacity);
        IdentityAuthority.EnsureCapacity(capacity);
    }
}

/// <summary>The synthetic-subject half of the scope (the partial <c>Entities.cs</c> left open for exactly this). They
/// die with their <see cref="Scope"/> like every other table, which is what makes a locale or account switch drop a
/// stale feed instead of re-rendering it.
/// <para>Deliberately NOT in <c>Scope.Tables</c>: that array is the store's warm/trim walk, and a Home feed is not
/// something to warm from disk — a stale greeting and yesterday's shelves painted at boot is the exact failure
/// <see cref="Home.Classify"/> exists to prevent.</para>
///
/// <para><b>KNOWN GAP, REPORTED (defect 1).</b> <c>Scope.ReleaseText</c> walks <c>Scope.Tables</c>, and these four
/// synthetic tables — <see cref="Homes"/>, <see cref="Sections"/>, <c>Searches</c> and <c>Browses</c> — are not in it,
/// so a scope switch drops them WITHOUT handing their interned text back: every greeting, band title, browse tile and
/// query the retired scope owned stays in the engine's <c>StringTable</c> for the life of the process, which is
/// precisely the leak the ref-counting discipline exists to close (doc §4.4). Each table's own <c>ReleaseText</c> is
/// implemented and correct; what is missing is the CALL. The fix belongs to <c>Entities.cs</c>'s owner and is four
/// lines — walk these tables in <c>Scope.ReleaseText</c> beside <c>Tables</c> (a second array, or a partial hook this
/// file implements) — so it is written down here rather than worked around from the wrong side of the seam.</para></summary>
public sealed partial class Scope
{
    public readonly HomeTable Homes = new();
    public readonly SectionTable Sections = new();
}

public sealed partial class Edges
{
    /// <summary>A BAND'S CARDS: parent = a <see cref="SectionTable"/> row, targets = the entity slots the band shows,
    /// in server order (ch 10 §7's "<c>Edges.SectionCards</c> parents a section → entity slots"; ch 11's card table).
    ///
    /// <para>CROSS-KIND, so the payload carries the table each target belongs to (<see cref="KindEdge"/>): one band
    /// mixes playlists, albums, artists, shows and episodes in one ranked order, and a bare slot cannot say which is
    /// which — the same defect <c>Edges.SearchResult</c> was fixed for. Without this edge a section knew how MANY cards
    /// it had (<see cref="SectionTable.Cards"/>) and not WHICH, so every band painted its count and no content.</para></summary>
    public readonly EdgeTable<KindEdge> SectionCards = new();
}

// ── 3. the paging arithmetic (ported, ch 10 §7 / ch 13 §7 gap 8) ─────────────────────────────────────────────────────

/// <summary>The cursor arithmetic behind "Show all", ported from 0.2.9's <c>HomeSectionPaging</c> and kept pure: the
/// whole defect class here is arithmetic, and both defects it guards against are measured, not hypothetical.
///
/// <para><b>RAW vs DEDUPED.</b> The card list is deduplicated; the server's cursor counts everything it sent. Paging by
/// the card count walks the cursor BACKWARDS by exactly the number of items we dropped, and a page that is entirely
/// already-seen uris does not move it at all — so the same request repeats forever while the total keeps the button
/// armed. Every quantity here is the RAW one.</para>
///
/// <para><b>TERMINATION.</b> A complete section can answer <c>nextOffset: 0</c>, and a section's own total can
/// overshoot what it actually holds (7 of 31 sections in a captured Home). So a cursor is only a cursor when it points
/// PAST the offset that produced it, and the total is an arming hint that is only consulted when there is no cursor at
/// all.</para></summary>
public static class SectionPaging
{
    /// <summary>The server sent no <c>pagingInfo</c> at all — a section seeded inline by the page it came in with.
    /// Distinct from <see cref="Complete"/>: absent means "ask the synthesized cursor", not "you are done".</summary>
    public const int NoCursor = int.MinValue;

    /// <summary>`pagingInfo` was present and `nextOffset` was EXPLICITLY null: done, full stop, even if the total
    /// still claims more. Never a legal offset — offsets are non-negative.</summary>
    public const int Complete = -1;

    /// <summary>The offset to request next: the raw count, floored at the cards we are actually showing so a section
    /// that under-reported its raw count still asks for the page AFTER what is on screen.</summary>
    public static int NextRequest(int raw, int cards) => raw > cards ? raw : cards;

    /// <summary>Does the server still have items past our cursor — i.e. does "Show all" stay armed? The server's own
    /// cursor WINS when we have one; the total is consulted only when we do not.</summary>
    public static bool HasMore(int raw, int cards, int total, int serverNextOffset)
        => serverNextOffset == NoCursor ? total > NextRequest(raw, cards)
         : serverNextOffset != Complete && serverNextOffset >= NextRequest(raw, cards);

    /// <summary>Can a fetched page's cursor carry us forward from the offset that produced it? <see cref="Complete"/>
    /// and <see cref="NoCursor"/> both say no; so does a value at or behind the request, which is how a complete
    /// section answering <c>nextOffset: 0</c> stops instead of re-reading page one forever. THIS, and not
    /// "loaded &lt; total", is the terminator.</summary>
    public static bool CanAdvance(int requestedOffset, int serverNextOffset)
        => serverNextOffset != NoCursor && serverNextOffset != Complete && serverNextOffset > requestedOffset;

    /// <summary>The raw cursor after folding in a page: the FULL page, duplicates included. That is the no-progress
    /// guard — a page contributing zero new cards still moves the cursor, so the next click asks for the following
    /// page instead of re-issuing the same request.</summary>
    public static int Advance(int raw, int pageItems) => raw + pageItems;
}

// ── 4. the handles ───────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>One band of a feed. Partial so Wave 5 adds ch 12's drill-page rules to the same type.</summary>
public readonly partial struct Section(int slot) : IEquatable<Section>
{
    static SectionTable T => Entities.Current.Sections;

    public int Slot { get; } = slot;

    public bool IsValid => Slot > Table.None && Slot < T.Count;
    public uint Version => T.Version[Slot];
    public bool Knows(SectionFields fields) => T.Knows(Slot, (uint)fields);

    /// <summary>THE band's identity, packed (<c>Entities.cs</c> §2) — always the TEXT form: a section uri is a
    /// composer subject, not a catalog gid. It is what the <c>home-section:</c> / <c>browse-section:</c> drill route
    /// re-finds the row by.</summary>
    public EntityId Id => T.Id[Slot];

    public StringId TitleId => T.Title[Slot];
    public StringId SubtitleId => T.Subtitle[Slot];
    public SectionKind Kind => (SectionKind)T.Form[Slot];
    public uint Accent => T.Accent[Slot];
    /// <summary>The server's item count — an arming hint, never a terminator (<see cref="SectionPaging"/>).</summary>
    public int Total => T.Total[Slot];
    /// <summary>Raw items received, duplicates and unsupported included: THE paging cursor.</summary>
    public int Raw => T.Raw[Slot];
    public int NextOffset => T.NextOffset[Slot];
    public int Unsupported => T.Unsupported[Slot];
    public int Duplicates => T.Duplicates[Slot];

    /// <summary>How many cards the band holds after dedupe (see <see cref="SectionTable.Cards"/>).</summary>
    public int Cards => T.Cards[Slot];

    /// <summary>The band's cards, in server order — the CSR slice of <see cref="Edges.SectionCards"/>. Never held
    /// across a UI drain (Edges.cs's span rule).</summary>
    public ReadOnlySpan<int> CardSlots => Entities.Current.Edges.SectionCards.Targets(Slot);
    /// <summary>Which TABLE each card's slot indexes, parallel to <see cref="CardSlots"/>: a band mixes kinds, so a
    /// row binds <c>new EntityRef(CardKinds[i].Kind, CardSlots[i])</c> and never a bare slot.</summary>
    public ReadOnlySpan<KindEdge> CardKinds => Entities.Current.Edges.SectionCards.Payload(Slot);
    public EdgeState CardState => Entities.Current.Edges.SectionCards.State(Slot);
    public uint CardVersion => Entities.Current.Edges.SectionCards.Version(Slot);

    /// <summary>IS THIS SECTION WHOLE? Ch 10 §7's reveal gate — "a section reveals COMPLETE" — answered from three
    /// columns, so the page never counts and never probes: somebody has answered for the band, and the server has
    /// nothing left past our cursor. A section that revealed on "some cards arrived" is the pop-in the chapter
    /// forbids, and one that waited for <see cref="Total"/> to be reached would never reveal at all (7 of 31 measured
    /// sections overshoot their own total).</summary>
    public bool IsComplete => Knows(SectionFields.Identity) && !HasMore;

    /// <summary>Does "Show all" stay armed?</summary>
    public bool HasMore => SectionPaging.HasMore(Raw, Cards, Total, NextOffset);

    /// <summary>The offset the next "Show all" page should request.</summary>
    public int NextRequest => SectionPaging.NextRequest(Raw, Cards);

    public bool Equals(Section other) => other.Slot == Slot;
    public override bool Equals(object? o) => o is Section s && s.Slot == Slot;
    public override int GetHashCode() => Slot;
    public static bool operator ==(Section a, Section b) => a.Slot == b.Slot;
    public static bool operator !=(Section a, Section b) => a.Slot != b.Slot;
}

/// <summary>A Home feed for one facet. Partial so Wave 5 adds ch 10 §9's projections to the same type.</summary>
public readonly partial struct Home(int slot) : IEquatable<Home>
{
    /// <summary>The unfiltered feed's uri. A synthetic subject needs a uri like any other row — it is how the row is
    /// found again after a scope switch, and how the diagnostics row inspector names it.</summary>
    public const string FeedUri = "wavee:home";
    /// <summary>A facet feed's uri prefix (<c>wavee:home:&lt;facetId&gt;</c>).</summary>
    public const string FacetPrefix = "wavee:home:";

    static HomeTable T => Entities.Current.Homes;
    static Edges E => Entities.Current.Edges;

    public int Slot { get; } = slot;

    public bool IsValid => Slot > Table.None && Slot < T.Count;
    public uint Version => T.Version[Slot];
    public bool Knows(HomeFields fields) => T.Knows(Slot, (uint)fields);

    /// <summary>THE feed subject's identity, packed (<c>Entities.cs</c> §2). Always the TEXT form — <c>wavee:home</c>
    /// is not a gid — and it is how the row is found again after a facet switch.</summary>
    public EntityId Id => T.Id[Slot];
    public StringId GreetingId => T.Greeting[Slot];
    public StringId FacetId => T.Facet[Slot];

    /// <summary>The chip strip's ids / labels / parent indices, parallel and in server order.</summary>
    public ReadOnlySpan<StringId> ChipIds => T.ChipIds.Span.Slice(T.ChipStart[Slot], T.ChipCount[Slot]);
    /// <inheritdoc cref="ChipIds"/>
    public ReadOnlySpan<StringId> ChipLabels => T.ChipLabels.Span.Slice(T.ChipStart[Slot], T.ChipCount[Slot]);
    /// <inheritdoc cref="ChipIds"/>
    public ReadOnlySpan<int> ChipParents => T.ChipParents.Span.Slice(T.ChipStart[Slot], T.ChipCount[Slot]);

    /// <summary>The feed's sections, in order.</summary>
    public ReadOnlySpan<int> SectionSlots => E.HomeSection.Targets(Slot);
    public EdgeState SectionState => E.HomeSection.State(Slot);
    public int SectionCount => E.HomeSection.Count(Slot);
    public uint SectionVersion => E.HomeSection.Version(Slot);

    /// <summary>The whole-page verdict. <paramref name="concluded"/> is "the fetch for this (scope, facet) is no
    /// longer in flight AND the session is not still connecting" — see <see cref="Home.Concluded"/>.</summary>
    public HomeState State(bool concluded) => Classify(SectionCount, concluded);

    public bool Equals(Home other) => other.Slot == Slot;
    public override bool Equals(object? o) => o is Home h && h.Slot == Slot;
    public override int GetHashCode() => Slot;
    public static bool operator ==(Home a, Home b) => a.Slot == b.Slot;
    public static bool operator !=(Home a, Home b) => a.Slot != b.Slot;
}

// ── 5. the readiness rules (ch 10 §7) ────────────────────────────────────────────────────────────────────────────────

public readonly partial struct Home
{
    /// <summary>THE whole-page gate — 0.2.9's <c>HomeFeedReadiness.Classify</c>, unchanged in behaviour and moved onto
    /// the model. Until the live attempt has CONCLUDED the answer is <see cref="HomeState.Placeholder"/> whatever the
    /// store warmed from disk, because warm rows are provisional and painting them and then replacing them 1.5 s later
    /// is the exact regression the gate exists to prevent.</summary>
    public static HomeState Classify(int sectionCount, bool concluded)
        => !concluded ? HomeState.Placeholder
         : sectionCount > 0 ? HomeState.Ready
         : HomeState.Empty;

    /// <summary>"The live attempt is over" — nothing is in flight for this feed and the session is not still coming
    /// up. Split out so the page never has to remember that BOTH halves matter: a concluded fetch during a
    /// reconnecting session is not a conclusion.</summary>
    public static bool Concluded(bool fetchInflight, bool sessionConnecting) => !fetchInflight && !sessionConnecting;

    /// <summary>THE 1,500 ms hold's predicate — 0.2.9's <c>ChromeConcluded</c>. "Concluded" means NOTHING IS COMING,
    /// which is why an absent notification bridge and an Idle one both answer true: Idle is "never fetched" (offline,
    /// <c>--fake</c>), and treating it as pending is the difference between a 0 ms hold and a 1,500 ms one on every
    /// offline launch. <see cref="HomeLoad.Pending"/> is the ONE state worth a bounded wait.</summary>
    public static bool ChromeConcluded(HomeLoad charts, bool hasNotifications, HomeLoad whatsNew, HomeLoad social)
        => charts != HomeLoad.Pending
        && (!hasNotifications || (whatsNew != HomeLoad.Pending && social != HomeLoad.Pending));
}

/// <summary>The three-state load verdict the chrome predicate reads. Deliberately not a bool pair: "never asked" and
/// "asked and waiting" are different answers and 0.2.9 conflating them is what armed the hold offline.</summary>
public enum HomeLoad : byte
{
    /// <summary>Never fetched — offline, <c>--fake</c>, or a bridge that does not exist. CONCLUDED.</summary>
    Idle = 0,
    /// <summary>In flight. The only state worth waiting for.</summary>
    Pending = 1,
    Ready = 2,
    Failed = 3,
}

// ── 6. factories, batch sugar and the commit ─────────────────────────────────────────────────────────────────────────

public static partial class Entities
{
    /// <summary>The unfiltered Home feed (D10: allocates the row if unseen, so the page binds before anything has been
    /// fetched). Named <c>HomeFeed</c> rather than <c>Home</c> so the method never shadows the <see cref="Home"/>
    /// TYPE inside this class.</summary>
    public static Home HomeFeed() => new(Current.Homes.Slot(Home.FeedUri.AsSpan()));

    /// <summary>The feed for one facet. An empty facet is the unfiltered feed — the same row, not a second one.</summary>
    public static Home HomeFeed(ReadOnlySpan<char> facetId)
    {
        if (facetId.IsEmpty) return HomeFeed();
        int n = Home.FacetPrefix.Length + facetId.Length;
        if (n > EntityUri.StackChars) return HomeFeed();            // an absurd facet id: the unfiltered feed, never a throw
        Span<char> uri = stackalloc char[EntityUri.StackChars];
        Home.FacetPrefix.AsSpan().CopyTo(uri);
        facetId.CopyTo(uri[Home.FacetPrefix.Length..]);
        var homes = Current.Homes;
        int slot = homes.Slot(uri[..n]);
        // SetText, never `homes.Facet[slot] = …`: the row owns this string and gives it back in
        // `HomeTable.ReleaseText` (defect 1, file header). Re-asking for the same facet re-writes the same id, which
        // `RetainText` short-circuits to nothing.
        homes.SetText(ref homes.Facet, slot, Strings.Intern(facetId));
        return new Home(slot);
    }

    /// <summary>A section row by its own uri (the `home-section:` / `browse-section:` drill route's entry point).</summary>
    public static Section Section(ReadOnlySpan<char> uri) => new(Current.Sections.Slot(uri));

    /// <inheritdoc cref="Ensure(Table,ReadOnlySpan{int},uint,FetchPriority)"/>
    public static void Ensure(Home row, HomeFields wanted, FetchPriority priority = FetchPriority.Visible)
    {
        int slot = row.Slot;
        Ensure(Current.Homes, new ReadOnlySpan<int>(in slot), (uint)wanted, priority);
    }

    /// <inheritdoc cref="Ensure(Table,ReadOnlySpan{int},uint,FetchPriority)"/>
    public static void Ensure(ReadOnlySpan<Section> rows, SectionFields wanted, FetchPriority priority = FetchPriority.Visible)
        => Ensure(Current.Sections, Slots(rows), (uint)wanted, priority);
}

/// <summary>One decoded section band. Text is a <see cref="TextRef"/>: the decoder cannot intern (C1/C10).</summary>
public struct StagedSection : IStagedRow
{
    /// <summary>THE row's identity as the wire gave it (<see cref="StagedId"/>): the packed <see cref="EntityId"/> when
    /// it arrived as 16 gid bytes, the uri's UTF-8 in the arena when it arrived as text. One field, one resolve —
    /// <c>s.Slot(table, in row.Id)</c> — and no format-to-arena-then-parse-back round trip.</summary>
    public StagedId Id;
    public TextRef Title, Subtitle;
    /// <summary>The feed or browse-page row this band belongs to, so the commit can rebuild the parent's edge without
    /// a second pass. Empty for a band fetched on its own (the drill page).</summary>
    public TextRef ParentUri;
    public int Total, Raw, Cards, NextOffset;
    public uint Accent;
    public ushort Unsupported, Duplicates;
    public byte Kind;
    public uint Known;
    public Authority Authority;

    /// <inheritdoc cref="IStagedRow.Init"/>
    public void Init(in StagedId id, Authority authority, uint known) { Id = id; Authority = authority; Known = known; }
    /// <inheritdoc cref="IStagedRow.Identity"/>
    public readonly StagedId Identity => Id;
}

/// <summary>One decoded FEED SUBJECT: the server's greeting and its facet chip strip. The band list itself is the
/// <see cref="Edges.HomeSection"/> run — this row carries what is ON the subject and the
/// <see cref="HomeFields.Sections"/> bit, which is "somebody replied", the one thing an edge state cannot say while the
/// store is warming yesterday's rows from disk (<see cref="Home.Classify"/>).</summary>
public struct StagedHome : IStagedRow
{
    public StagedId Id;
    public TextRef Greeting, Facet;
    /// <summary>This row's chips: a range into <see cref="Staging.Chips"/>. Chips are NOT entities (ch 10 §7), so they
    /// stage as a flat run exactly as they live as a flat slab.</summary>
    public int ChipStart, ChipCount;
    /// <summary><see cref="HomeFields"/>: which groups this row speaks for.</summary>
    public uint Known;
    public Authority Authority;

    /// <inheritdoc cref="IStagedRow.Init"/>
    public void Init(in StagedId id, Authority authority, uint known) { Id = id; Authority = authority; Known = known; }
    /// <inheritdoc cref="IStagedRow.Identity"/>
    public readonly StagedId Identity => Id;
}

/// <summary>One facet chip, staged. <see cref="Parent"/> is the index — WITHIN THE SAME STAGED RUN — of the chip this
/// one hangs under, or -1 for a top-level chip; the commit rebases it onto the row's own slab range, which is how
/// <c>HomeChip.SubChips</c> survives being flattened.</summary>
public struct StagedChip
{
    public TextRef Id, Label;
    public int Parent;
}

public sealed partial class Staging
{
    StagedList<StagedSection>? _sections;
    StagedList<StagedHome>? _homes;
    StagedList<StagedChip>? _chips;
    /// <summary>Lazy: a decode that touches no section allocates no section list.</summary>
    public StagedList<StagedSection> Sections => _sections ??= Register(new StagedList<StagedSection>());
    /// <inheritdoc cref="StagedHome"/>
    public StagedList<StagedHome> Homes => _homes ??= Register(new StagedList<StagedHome>());
    /// <inheritdoc cref="StagedChip"/>
    public StagedList<StagedChip> Chips => _chips ??= Register(new StagedList<StagedChip>());
    internal StagedList<StagedSection>? StagedSections => _sections;
    internal StagedList<StagedHome>? StagedHomes => _homes;
    internal StagedList<StagedChip>? StagedChips => _chips;
}

public static partial class Entities
{
    // The chip commit's scratch: UI thread only (C1), so one static set is safe and a steady stream of feed answers
    // allocates nothing after the widest strip it has seen (P8).
    static StringId[] s_chipIds = new StringId[16], s_chipLabels = new StringId[16];
    static int[] s_chipParents = new int[16];

    /// <summary>The Home subjects and their bands, wired into <c>Entities.Commit</c>'s chain (Entities.cs) ahead of
    /// <c>CommitEdges</c>, so the section rows a <see cref="Relation.HomeSection"/> run points at already exist when the
    /// run lands. Before this hook <c>Home.Commit</c> was a public static with NO CALLER anywhere in the tree: a
    /// decoded feed staged ~31 bands and committed none of them.</summary>
    static partial void CommitHome(Staging s)
    {
        Home.Commit(s);
        CommitHomeSubjects(s);
    }

    static void CommitHomeSubjects(Staging s)
    {
        var staged = s.StagedHomes;
        if (staged is null || staged.Count == 0) return;

        var t = Current.Homes;
        var chips = s.StagedChips is { Count: > 0 } c ? c.Span : default;
        var rows = staged.Span;
        for (int i = 0; i < rows.Length; i++)
        {
            ref var row = ref rows[i];
            int slot = s.Slot(t, in row.Id);
            if (slot == Table.None) continue;
            var authority = row.Authority == Authority.None ? s.Authority : row.Authority;
            if (!t.Accepts(slot, row.Known, authority, in t.IdentityAuthority)) continue;

            if ((row.Known & (uint)HomeFields.Greeting) != 0)
                t.SetText(ref t.Greeting, slot, s.Intern(row.Greeting));
            if (!row.Facet.IsEmpty) t.SetText(ref t.Facet, slot, s.Intern(row.Facet));

            if ((row.Known & (uint)HomeFields.Chips) != 0)
            {
                int n = row.ChipStart < 0 || row.ChipCount < 0 || row.ChipStart + row.ChipCount > chips.Length
                    ? 0 : row.ChipCount;
                GrowChips(n);
                for (int j = 0; j < n; j++)
                {
                    ref readonly var chip = ref chips[row.ChipStart + j];
                    s_chipIds[j] = s.Intern(chip.Id);
                    s_chipLabels[j] = s.Intern(chip.Label);
                    // The staged parent indexes the RUN; the slab wants an index into the row's own range.
                    s_chipParents[j] = chip.Parent >= 0 && chip.Parent < n ? chip.Parent : -1;
                }
                // SetChips releases the range it abandons before it writes the new one (defect 1, file header): a chip
                // strip is rewritten whole on every feed answer, and 0 chips is a real answer.
                t.SetChips(slot, s_chipIds.AsSpan(0, n), s_chipLabels.AsSpan(0, n), s_chipParents.AsSpan(0, n));
            }

            t.Applied(slot, row.Known, authority, ref t.IdentityAuthority);
        }
    }

    static void GrowChips(int n)
    {
        if (n <= s_chipIds.Length) return;
        int size = s_chipIds.Length;
        while (size < n) size *= 2;
        s_chipIds = new StringId[size];
        s_chipLabels = new StringId[size];
        s_chipParents = new int[size];
    }
}

public readonly partial struct Home
{
    /// <summary>Copy a staged batch of section rows into the columns. Called by <c>Entities.CommitHome</c>, which is
    /// the <c>Entities.Commit</c> chain's hook for the synthetic subjects (a section is not an entity kind, so it does
    /// not get a <c>Commit&lt;Kind&gt;</c> of its own). Same shape as every kind commit: resolve, ask, write,
    /// <c>Applied</c> (D16).</summary>
    public static void Commit(Staging s)
    {
        var staged = s.StagedSections;
        if (staged is null || staged.Count == 0) return;

        var t = Entities.Current.Sections;
        var rows = staged.Span;
        for (int i = 0; i < rows.Length; i++)
        {
            ref var row = ref rows[i];
            int slot = s.Slot(t, in row.Id);
            if (slot == Table.None) continue;   // a row with no identity is not a row
            var authority = row.Authority == Authority.None ? s.Authority : row.Authority;

            if ((row.Known & (uint)SectionFields.Identity) != 0
                && t.Accepts(slot, (uint)SectionFields.Identity, authority, in t.IdentityAuthority))
            {
                // SetText, never `t.Title[slot] = …`: the write AddRefs the incoming id and releases the one it
                // overwrites, so a band re-answered on every feed refresh owns one title, not one per refresh
                // (defect 1, file header).
                t.SetText(ref t.Title, slot, s.Intern(row.Title));
                t.SetText(ref t.Subtitle, slot, s.Intern(row.Subtitle));
                t.Form[slot] = row.Kind;
                t.Total[slot] = row.Total;
                t.Raw[slot] = row.Raw;
                t.Cards[slot] = row.Cards;
                t.NextOffset[slot] = row.NextOffset;
                t.Unsupported[slot] = row.Unsupported;
                t.Duplicates[slot] = row.Duplicates;
                t.Applied(slot, (uint)SectionFields.Identity, authority, ref t.IdentityAuthority);
            }

            if ((row.Known & (uint)SectionFields.Accent) != 0
                && t.Accepts(slot, (uint)SectionFields.Accent, authority, in t.IdentityAuthority))
            {
                t.Accent[slot] = row.Accent;
                t.Applied(slot, (uint)SectionFields.Accent, authority, ref t.IdentityAuthority);
            }
        }
    }
}
