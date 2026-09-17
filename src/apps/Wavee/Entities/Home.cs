// ── Entities/Home.cs — CORE (owner B, wave 1; owner P, wave 5; plan §2, ch 10 §7-§8, ch 11 §7-§8, ch 12 §7-§8) ─────────
//
// Role: CORE
// Owner: P (Wave 5; the Wave-1 tables below are owner B's and are kept verbatim)
// Wave: 5
// Budget: 1,800 lines for Home.cs + its pre-declared overflow `Home.Rules.cs` (plan §2 A15; ch 10/11/12 §9 line budgets)
// Spec: ch 10 §7-§8, ch 11 §7-§8, ch 12 §7-§8, WP-5.P contract §2.1 / §2.6
//
// WAVE 5 ADDS, in this file: the section FLAGS and the per-section CARD FACTS (§2b — what a home card paints that no
// entity column holds: the payload accent of a non-playlist card, the section's own subtitle line, the raw format token,
// the audiobook facts, the seeds), the account's TOP relations, `SectionKind.HomeShorts`, the readiness gate
// (`HomeFeedReadiness` + `HomeRevealGate`, §5b) and the page's demand (`Home.EnsureFeed` / `EnsureSection` /
// `RequestNextPage`, §7). The composed feed model, the composer and every ported rule set live in `Home.Rules.cs`.
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
using FluentGpu.Localization;
using FluentGpu.Signals;

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
    /// <summary>`HomeShortsSectionData` — 0.2.9's composer has no arm for it, so it routes through the card-driven
    /// default arm exactly like a generic shelf (<c>SpotifyHomeComposer.cs:111-118</c>). Its own member so the decode
    /// never lies about the typename it read (B1's fold filed it as a baseline shelf, which stamped eyebrows).</summary>
    HomeShorts = 5,
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
    /// <summary><see cref="SectionFlags"/> — decoded, never sniffed from the uri at render time (ch 12 §9 trap 10).</summary>
    public Column<byte> Flags;

    public Column<byte> IdentityAuthority;

    // ── the per-section CARD FACTS (Wave 5, §2b) ──────────────────────────────────────────────────────────────────────
    //
    // What a home card paints that no ENTITY column holds: the payload accent of an album / artist / show card, the
    // section's own subtitle line (`description ?? owner`, "Song - A, B", an audiobook's author), the RAW format token
    // (the enum `PlaylistFormat` folds `artist-mix-reader`/`descripto`/`artistsets`/`format-shows-shuffle` into Other,
    // and the composer routes on exactly those), the audiobook rating / signifier, an episode's video flag and resume, and
    // the seeds. One RANGE per section row into shared slabs, like the chip strip on the Home row — addressed by the
    // section, so a section's rewrite gives its own range back and a scope retire releases every string through
    // `ReleaseText` (defect 1). The range is REUSED in place when the new answer fits, so a 60 s poll does not walk the
    // slabs forward forever.

    /// <summary>Per row: the fact range and the capacity reserved for it.</summary>
    public Column<int> FactStart, FactCount, FactCap;
    /// <summary>Per row: the seed range (the seeds of every card of the section, contiguous) and its capacity.</summary>
    public Column<int> SeedStart, SeedCap;

    /// <summary>The fact slabs, one entry per card. <c>FactKind</c> is the target's <see cref="EntityKind"/>.</summary>
    public Column<int> FactTarget, FactDurationMs, FactResumeMs, FactSeedAt;
    public Column<uint> FactAccent;
    public Column<ushort> FactRating;
    public Column<byte> FactKind, FactFlags, FactSeedCount;
    public Column<StringId> FactSubtitle, FactFormat, FactAuthor, FactSignifier;
    /// <summary>The seed slab.</summary>
    public Column<StringId> Seeds;
    public int FactTail, SeedTail;
    // `HomeCard.Seeds` hands out an IReadOnlyList; building it once per fact entry (and never per render) is what keeps
    // the hero / mix band render allocation-free after the first frame. Invalidated with the range it describes.
    string[]?[] _seedViews = [];

    public override EntityKind Kind => EntityKind.Unknown;

    /// <summary>Give back every string a section row owns (defect 1): the title, the subtitle and its card facts. ~31
    /// rows are minted per feed answer and a facet switch replaces all of them, so this is the difference between a
    /// bounded floor and a rising one.</summary>
    protected override void ReleaseText(int slot)
    {
        ClearText(ref Title, slot);
        ClearText(ref Subtitle, slot);
        ReleaseFacts(slot);
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
        Flags.EnsureCapacity(capacity);
        IdentityAuthority.EnsureCapacity(capacity);
        FactStart.EnsureCapacity(capacity);
        FactCount.EnsureCapacity(capacity);
        FactCap.EnsureCapacity(capacity);
        SeedStart.EnsureCapacity(capacity);
        SeedCap.EnsureCapacity(capacity);
    }

    /// <summary>The table a card target of <paramref name="kind"/> resolves in. Liked Songs is a COLLECTION uri whose
    /// row lives in the playlist table (<c>Decode.Stage</c>) — the one kind <c>Entities.TableFor</c> answers null for.</summary>
    public static Table? CardTable(EntityKind kind)
        => kind == EntityKind.Collection ? Entities.Current.Playlists : Entities.TableFor(kind);

    /// <summary>The fact entry for one card of this section, or -1. A linear scan over the section's own range (a band
    /// holds ≤ ~20 cards); allocation-free, and O(1) for a section that carries no facts.</summary>
    public int FactIndexOf(int slot, EntityKind kind, int target)
    {
        if (slot <= None || slot >= Count || target <= None) return -1;
        int start = FactStart[slot], n = FactCount[slot];
        bool playlistLike = kind is EntityKind.Playlist or EntityKind.Collection;
        for (int i = start; i < start + n; i++)
        {
            if (FactTarget[i] != target) continue;
            byte k = FactKind[i];
            if (k == (byte)kind || (playlistLike && k is (byte)EntityKind.Playlist or (byte)EntityKind.Collection)) return i;
        }
        return -1;
    }

    /// <summary>The seeds of one fact entry as a list, built once per entry and cached until the range is rewritten.</summary>
    public IReadOnlyList<string>? SeedsOf(int fact)
    {
        if (fact < 0 || fact >= FactTail || FactSeedCount[fact] == 0) return null;
        if (fact < _seedViews.Length && _seedViews[fact] is { } cached) return cached;
        int n = FactSeedCount[fact], at = FactSeedAt[fact];
        var list = new string[n];
        for (int i = 0; i < n; i++) list[i] = Entities.Strings.Resolve(Seeds[at + i]);
        if (fact >= _seedViews.Length) Array.Resize(ref _seedViews, Math.Max(fact + 1, _seedViews.Length * 2));
        _seedViews[fact] = list;
        return list;
    }

    /// <summary>Write one section's card facts from a staged batch: give the old range's strings back, reuse the range
    /// when the new answer fits (else take a fresh one at the tail), resolve each target the way the card edge resolves
    /// it, and own every string through <see cref="Entities.RetainText"/>.</summary>
    public void SetFacts(int slot, Staging s, ReadOnlySpan<StagedCardFact> facts, ReadOnlySpan<TextRef> seeds)
    {
        ReleaseFacts(slot);
        int n = facts.Length, seedTotal = 0;
        for (int i = 0; i < n; i++)
        {
            ref readonly var f = ref facts[i];
            if (f.SeedStart >= 0 && f.SeedCount > 0 && f.SeedStart + f.SeedCount <= seeds.Length)
                seedTotal += Math.Min(f.SeedCount, byte.MaxValue);
        }

        if (n > FactCap[slot])
        {
            FactStart[slot] = FactTail;
            FactCap[slot] = n;
            FactTail += n;
            GrowFacts(FactTail);
        }
        if (seedTotal > SeedCap[slot])
        {
            SeedStart[slot] = SeedTail;
            SeedCap[slot] = seedTotal;
            SeedTail += seedTotal;
            Seeds.EnsureCapacity(SeedTail);
        }

        int start = FactStart[slot], seedAt = SeedStart[slot];
        for (int i = 0; i < n; i++)
        {
            ref readonly var f = ref facts[i];
            int at = start + i;
            var kind = f.Target.Kind(s);
            FactKind[at] = (byte)kind;
            FactTarget[at] = CardTable(kind) is { } table ? s.Slot(table, in f.Target) : None;
            FactAccent[at] = f.Accent;
            FactFlags[at] = f.Flags;
            FactRating[at] = f.Rating;
            FactDurationMs[at] = f.DurationMs;
            FactResumeMs[at] = f.ResumeMs;
            Entities.RetainText(ref FactSubtitle[at], s.Intern(f.Subtitle));
            Entities.RetainText(ref FactFormat[at], s.Intern(f.Format));
            Entities.RetainText(ref FactAuthor[at], s.Intern(f.Author));
            Entities.RetainText(ref FactSignifier[at], s.Intern(f.Signifier));
            int count = f.SeedStart >= 0 && f.SeedCount > 0 && f.SeedStart + f.SeedCount <= seeds.Length
                ? Math.Min(f.SeedCount, byte.MaxValue) : 0;
            FactSeedAt[at] = seedAt;
            FactSeedCount[at] = (byte)count;
            for (int k = 0; k < count; k++) Entities.RetainText(ref Seeds[seedAt + k], s.Intern(seeds[f.SeedStart + k]));
            seedAt += count;
            if (at < _seedViews.Length) _seedViews[at] = null;
        }
        FactCount[slot] = n;
    }

    /// <summary>Give one section's fact strings back and empty its range (the capacity is kept). Idempotent.</summary>
    void ReleaseFacts(int slot)
    {
        int start = FactStart[slot], n = FactCount[slot];
        for (int i = start; i < start + n; i++)
        {
            Entities.ReleaseText(ref FactSubtitle[i]);
            Entities.ReleaseText(ref FactFormat[i]);
            Entities.ReleaseText(ref FactAuthor[i]);
            Entities.ReleaseText(ref FactSignifier[i]);
            for (int k = 0; k < FactSeedCount[i]; k++) Entities.ReleaseText(ref Seeds[FactSeedAt[i] + k]);
            FactSeedCount[i] = 0;
            FactTarget[i] = None;
            if (i < _seedViews.Length) _seedViews[i] = null;
        }
        FactCount[slot] = 0;
    }

    void GrowFacts(int capacity)
    {
        FactTarget.EnsureCapacity(capacity);
        FactDurationMs.EnsureCapacity(capacity);
        FactResumeMs.EnsureCapacity(capacity);
        FactSeedAt.EnsureCapacity(capacity);
        FactAccent.EnsureCapacity(capacity);
        FactRating.EnsureCapacity(capacity);
        FactKind.EnsureCapacity(capacity);
        FactFlags.EnsureCapacity(capacity);
        FactSeedCount.EnsureCapacity(capacity);
        FactSubtitle.EnsureCapacity(capacity);
        FactFormat.EnsureCapacity(capacity);
        FactAuthor.EnsureCapacity(capacity);
        FactSignifier.EnsureCapacity(capacity);
    }
}

/// <summary>What a section row says about itself beyond its kind (WP-5.P contract §2.6).</summary>
[Flags]
public enum SectionFlags : byte
{
    None = 0,
    /// <summary>One of the five hardcoded chart sections (<c>ChartSections.All</c>), set at DECODE. The drill grid's
    /// two-line titles, blank subtitles and filter box read THIS, never a uri lookup (ch 12 §9 trap 10).</summary>
    Chart = 1 << 0,
}

/// <summary>The per-card fact bits (<see cref="SectionTable.FactFlags"/>).</summary>
[Flags]
public enum HomeCardFlags : byte
{
    None = 0,
    /// <summary>An episode that ships a video track (<c>mediaTypes</c> carries <c>VIDEO</c>).</summary>
    HasVideo = 1 << 0,
    /// <summary>A <c>spotify:show:</c> card the wire typed <c>Audiobook</c>: it routes like a show and renders a rating
    /// cluster (0.2.9 <c>HomeCardKind.Audiobook</c>).</summary>
    Audiobook = 1 << 1,
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

    /// <summary>THE ACCOUNT'S TOP ARTISTS (<c>userTopContent</c>, affinity over four weeks): parent = the account's own
    /// row (<c>Scope.MeSlot</c>), and the ORDER IS THE RANK — the podium's 76/60/46 ramp reads the index. ch 11 §7 DATA
    /// GAPS; written by <c>Home.Feeds.EnsureTopContent</c>'s decode and by the seed.</summary>
    public readonly EdgeTable<NoEdge> UserTopArtists = new();

    /// <summary>The top TRACKS from the same document ("In your top N" in the podium's disclosure).</summary>
    public readonly EdgeTable<NoEdge> UserTopTracks = new();
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
    /// <summary>The decoded <see cref="SectionFlags"/>.</summary>
    public SectionFlags Flags => (SectionFlags)T.Flags[Slot];
    /// <summary>Is this one of the chart sections? Decoded, threaded into the grid — never a uri lookup (ch 12 trap 10).</summary>
    public bool IsChart => (T.Flags[Slot] & (byte)SectionFlags.Chart) != 0;
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
    /// <summary><see cref="SectionFlags"/>, written with the Identity group.</summary>
    public byte Flags;
    /// <summary>This band's card facts: a range into <see cref="Staging.CardFacts"/>. Only a fold that speaks for the
    /// facts sets <see cref="HasFacts"/>; a band committed without it keeps whatever facts it had.</summary>
    public int FactStart, FactCount;
    public bool HasFacts;
    public uint Known;
    public Authority Authority;

    /// <inheritdoc cref="IStagedRow.Init"/>
    public void Init(in StagedId id, Authority authority, uint known) { Id = id; Authority = authority; Known = known; }
    /// <inheritdoc cref="IStagedRow.Identity"/>
    public readonly StagedId Identity => Id;
}

/// <summary>One home card's facts, staged (<see cref="SectionTable"/> §2b). Text is a <see cref="TextRef"/>.</summary>
public struct StagedCardFact
{
    public StagedId Target;
    public TextRef Subtitle, Format, Author, Signifier;
    public uint Accent;
    public int DurationMs, ResumeMs;
    /// <summary>The average rating × 100 (0 = withheld).</summary>
    public ushort Rating;
    /// <summary><see cref="HomeCardFlags"/>.</summary>
    public byte Flags;
    /// <summary>A range into <see cref="Staging.CardSeeds"/>.</summary>
    public int SeedStart, SeedCount;
}

/// <summary>One staged top-content run (<see cref="Edges.UserTopArtists"/> / <see cref="Edges.UserTopTracks"/>): the
/// account it hangs off and its targets, a range into <see cref="Staging.TopTargets"/> in rank order.</summary>
public struct StagedTopRun
{
    public StagedId Parent;
    public bool Tracks;
    public int Start, Count;
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

    StagedList<StagedCardFact>? _cardFacts;
    StagedList<TextRef>? _cardSeeds;
    StagedList<StagedTopRun>? _topRuns;
    StagedList<StagedId>? _topTargets;
    /// <inheritdoc cref="StagedCardFact"/>
    public StagedList<StagedCardFact> CardFacts => _cardFacts ??= Register(new StagedList<StagedCardFact>());
    /// <summary>The seed text every staged card fact indexes.</summary>
    public StagedList<TextRef> CardSeeds => _cardSeeds ??= Register(new StagedList<TextRef>());
    /// <inheritdoc cref="StagedTopRun"/>
    public StagedList<StagedTopRun> TopRuns => _topRuns ??= Register(new StagedList<StagedTopRun>());
    /// <inheritdoc cref="StagedTopRun"/>
    public StagedList<StagedId> TopTargets => _topTargets ??= Register(new StagedList<StagedId>());
    internal StagedList<StagedCardFact>? StagedCardFacts => _cardFacts;
    internal StagedList<TextRef>? StagedCardSeeds => _cardSeeds;
    internal StagedList<StagedTopRun>? StagedTopRuns => _topRuns;
    internal StagedList<StagedId>? StagedTopTargets => _topTargets;
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
        CommitTopContent(s);
    }

    static int[] s_topTargets = new int[16];

    /// <summary>The account's top relations (Wave 5): a whole Replace per run, rank order, duplicates and unresolvable
    /// targets dropped. Complete even when empty — a new account's empty podium is an answer, not a skeleton.</summary>
    static void CommitTopContent(Staging s)
    {
        var runs = s.StagedTopRuns;
        if (runs is null || runs.Count == 0) return;
        var targets = s.StagedTopTargets is { } t ? t.Span : default;
        var edges = Current.Edges;
        foreach (ref readonly var run in runs.Span)
        {
            int parent = s.Slot(Current.Users, in run.Parent);
            if (parent == Table.None || run.Start < 0 || run.Count < 0 || run.Start + run.Count > targets.Length) continue;
            if (run.Count > s_topTargets.Length) s_topTargets = new int[Math.Max(run.Count, s_topTargets.Length * 2)];
            var table = run.Tracks ? (Table)Current.Tracks : Current.Artists;
            int n = 0;
            for (int i = 0; i < run.Count; i++)
            {
                int slot = s.Slot(table, in targets[run.Start + i]);
                if (slot == Table.None || s_topTargets.AsSpan(0, n).IndexOf(slot) >= 0) continue;
                s_topTargets[n++] = slot;
            }
            (run.Tracks ? edges.UserTopTracks : edges.UserTopArtists).ReplaceRun(parent, s_topTargets.AsSpan(0, n), default);
        }
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
        var facts = s.StagedCardFacts is { Count: > 0 } f ? f.Span : default;
        var seeds = s.StagedCardSeeds is { Count: > 0 } sd ? sd.Span : default;
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
                t.Flags[slot] = row.Flags;
                t.Total[slot] = row.Total;
                t.Raw[slot] = row.Raw;
                t.Cards[slot] = row.Cards;
                t.NextOffset[slot] = row.NextOffset;
                t.Unsupported[slot] = row.Unsupported;
                t.Duplicates[slot] = row.Duplicates;
                if (row.HasFacts && row.FactStart >= 0 && row.FactCount >= 0 && row.FactStart + row.FactCount <= facts.Length)
                    t.SetFacts(slot, s, facts.Slice(row.FactStart, row.FactCount), seeds);
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

// ── 7. the reveal gate (Wave 5, ported verbatim: 0.2.9 `HomeFeedReadiness.cs:31-164`, ch 10 §8) ──────────────────────

/// <summary>Where the home LANDING feed stands, and the reveal gate's pure halves. Home reveals ONCE, from the feed the
/// session settles on: before the live-catalog attempt has concluded every read is provisional — including one that
/// already carries resident shelves — and publishing those is the "cached grid, then everything jumps" recording (#53).
/// <see cref="Classify"/> delegates to <see cref="Home.Classify"/>, the one copy of the three-way decision.</summary>
public static class HomeFeedReadiness
{
    /// <param name="groupCount">The unfaceted feed's group (section) count.</param>
    /// <param name="liveCatalogConcluded">The live attempt has CONCLUDED — succeeded, failed, gone offline, or was never
    /// going to run. The one state that is not concluded is a connect in flight (<see cref="Home.LiveAttemptConcluded"/>).</param>
    public static HomeState Classify(int groupCount, bool liveCatalogConcluded) => Home.Classify(groupCount, liveCatalogConcluded);

    /// <summary>The hard fallback: past this since mount the page force-publishes the best answer it has.</summary>
    public const double ForceReleaseMs = 8000d;

    /// <summary>Pure elapsed-time gate for the hard fallback — see <see cref="ForceReleaseMs"/>.</summary>
    public static bool ShouldForceRelease(double elapsedMs) => elapsedMs >= ForceReleaseMs;

    /// <summary>How long the FIRST reveal waits, after the feed settled, for the chrome rows (Charts, the timeline) to
    /// conclude. A cap, not a sleep; later publishes never wait.</summary>
    public const double ChromeSettleMs = 1500d;

    /// <summary>The first paint may happen once the feed settled AND the chrome concluded (or the cap elapsed, or the hard
    /// fallback forced it).</summary>
    public static bool MayReveal(bool feedSettled, bool chromeConcluded, double msSinceSettled, bool force = false)
        => feedSettled && (chromeConcluded || force || msSinceSettled >= ChromeSettleMs);

    /// <summary>A MOUNTING page's warm-start predicate — "should this region seed already revealed instead of a
    /// skeleton?" Deliberately NOT <see cref="Home.Classify"/>: a disk-warmed row can carry Known sections BEFORE
    /// its first live check concludes (<c>Store.Warm</c> — "the store paints yesterday's rows at boot"), and
    /// painting those on a genuine COLD BOOT is the exact "cached grid, then everything jumps" regression Classify
    /// exists to prevent (see the file banner). <paramref name="revealedThisSession"/> is the signal that rules
    /// that case out WITHOUT relaxing Classify's gate: it is true only after this exact content has already cleared
    /// that gate once, this process, this scope (<see cref="Home.Feeds.HasRevealed"/> /
    /// <see cref="Home.Feeds.HasChartsRevealed"/> — session-scoped, so a KEEP-ALIVE EVICTION of the page's
    /// Loadable/gate never clears it). A background refresh setting <c>Inflight</c> again afterward does not
    /// un-reveal it — that is the point: a returning page must paint the graph's still-known content and let the
    /// refresh update it in place, not re-shimmer merely because its page-local state was discarded.</summary>
    public static bool ShouldPaintOnMount(int groupCount, bool revealedThisSession)
        => groupCount > 0 && revealedThisSession;

    /// <summary>The epoch/placeholder transition every UNFACETED read runs: a withheld read returns the applied epoch
    /// UNCHANGED, so a later read at the SAME epoch still publishes; <paramref name="force"/> publishes a placeholder but
    /// still obeys the epoch gate.</summary>
    public static (bool Publish, int AppliedEpoch) ApplyEpoch(
        int appliedEpoch, int epoch, int groupCount, bool liveCatalogConcluded, bool force = false)
    {
        if (epoch < appliedEpoch) return (false, appliedEpoch);
        if (!force && Classify(groupCount, liveCatalogConcluded) == HomeState.Placeholder) return (false, appliedEpoch);
        return (true, epoch);
    }
}

/// <summary>What <see cref="HomeRevealGate{TFeed}.Offer"/> decided about a read.</summary>
public enum HomeRevealVerdict : byte
{
    /// <summary>Withheld — stale epoch, or a provisional read the page keeps skeletonized.</summary>
    Withheld,
    /// <summary>Settled but the chrome has not concluded: held for the first reveal (published from <c>Tick</c>).</summary>
    Held,
    /// <summary>The FIRST reveal: publish now, once.</summary>
    Reveal,
    /// <summary>Already revealed: an in-place Ready→Ready swap — never a second reveal, never a skeleton.</summary>
    Swap,
}

/// <summary>The Home page's reveal state machine, engine-free so the whole launch sequence is testable. One instance per
/// mounted page (a page-local field, never a static — two tabs each track what THEY consumed).</summary>
public sealed class HomeRevealGate<TFeed> where TFeed : class
{
    /// <summary>The feed epoch this page's rendered (or held) feed was read at; -1 until the first read lands.</summary>
    public int AppliedEpoch { get; private set; } = -1;

    /// <summary>The page has painted a real branch (feed, empty or error) — from here every publish is a swap.</summary>
    public bool Revealed { get; private set; }

    // The most recent UNFACETED read seen, applied or not: what the 8 s hard fallback force-publishes.
    int _lastSeenEpoch = -1;
    TFeed? _lastSeenFeed;

    // The settled feed waiting on the chrome for the first reveal, and when it settled (the cap's origin).
    TFeed? _held;
    double _heldAtMs;

    /// <summary>A settled feed is waiting for the chrome rows.</summary>
    public bool IsHolding => _held is not null;

    /// <summary>A read landed. <paramref name="alreadyResolved"/>: the region left Pending by another path (an initial
    /// failure, a facet tap) — a page that painted anything real is revealed. A faceted read always passes
    /// (<see cref="HomeFeedReadiness.Classify"/> never applies to the server's own facet document).</summary>
    public HomeRevealVerdict Offer(int epoch, TFeed feed, int groupCount, bool faceted, bool liveCatalogConcluded,
        bool force, bool alreadyResolved, bool chromeConcluded, double nowMs)
    {
        if (alreadyResolved) Revealed = true;
        if (epoch < AppliedEpoch) return HomeRevealVerdict.Withheld;
        if (!faceted && epoch >= _lastSeenEpoch) { _lastSeenEpoch = epoch; _lastSeenFeed = feed; }
        var (publish, applied) = HomeFeedReadiness.ApplyEpoch(AppliedEpoch, epoch, groupCount, liveCatalogConcluded, force || faceted);
        AppliedEpoch = applied;
        if (!publish) return HomeRevealVerdict.Withheld;
        if (Revealed) { _held = null; return HomeRevealVerdict.Swap; }
        _held = feed;
        _heldAtMs = nowMs;
        return Tick(chromeConcluded, nowMs, force) is null ? HomeRevealVerdict.Held : HomeRevealVerdict.Reveal;
    }

    /// <summary>The chrome moved, or the cap timer fired: the held feed to publish as the first reveal, or null.
    /// Idempotent — a second tick after the reveal returns null.</summary>
    public TFeed? Tick(bool chromeConcluded, double nowMs, bool force = false)
    {
        if (Revealed || _held is null) return null;
        if (!HomeFeedReadiness.MayReveal(feedSettled: true, chromeConcluded, nowMs - _heldAtMs, force)) return null;
        var feed = _held;
        _held = null;
        Revealed = true;
        return feed;
    }

    /// <summary>The 8 s hard fallback's input: a held feed first, then the last read seen even if withheld. A null feed
    /// means nothing has landed at all yet (the page offers <c>HomeFeedView.Empty</c>).</summary>
    public (int Epoch, TFeed? Feed) ForceRelease()
        => (Math.Max(_lastSeenEpoch, AppliedEpoch), _held ?? _lastSeenFeed);
}

// ── 8. the page's demand (Wave 5, WP-5.P contract §2.1) ──────────────────────────────────────────────────────────────

public readonly partial struct Home
{
    /// <summary>The selected facet id ("" = unfiltered). App-global, like 0.2.9's <c>Services.HomeFacet</c>: the strip
    /// WRITES it; the page and the feed host READ it — the host as a request parameter (ch 10 §9 trap 7).</summary>
    public static readonly Signal<string> SelectedFacet = new("");

    /// <summary>"The live attempt for this row concluded" (ch 10 §7): no request is out for the row AND the session is
    /// not in <c>Resolving..Minting</c> AND the row has been answered, asked, or can never be asked (the session is not
    /// Online). The third leg is what keeps a first render BEFORE the mount effect's <see cref="EnsureFeed"/> from reading
    /// an unasked row as a concluded empty feed. Reads <c>Spotify.Status.Value</c>, so a page render subscribes to the
    /// phase. The feed epoch the reveal gate wants is <see cref="Version"/>.</summary>
    public bool LiveAttemptConcluded
    {
        get
        {
            var t = T;
            if (!IsValid || t.Inflight[Slot] != 0) return false;
            var phase = Spotify.Status.Value;
            if (phase is >= Spotify.SessionPhase.Resolving and <= Spotify.SessionPhase.Minting) return false;
            return t.Knows(Slot, (uint)HomeFields.Sections)
                || (t.Asked[Slot] & (uint)HomeFields.Sections) != 0
                || phase != Spotify.SessionPhase.Online;
        }
    }

    /// <summary>The landing / facet page's ONE demand (ch 10 §7 "demand the whole model on mount"): the document, then
    /// ONE batched ensure per card kind for every card it holds. Idempotent (the planner dedupes on Known/Asked), so it is
    /// cheap from a mount effect and on a version change. UI thread (C1).</summary>
    public static void EnsureFeed(Home h)
    {
        if (!h.IsValid) return;
        Entities.Ensure(h, HomeFields.All);
        var sections = E.HomeSection.Targets(h.Slot);
        s_demand.Clear();
        for (int i = 0; i < sections.Length; i++) s_demand.AddCards(new Section(sections[i]));
        s_demand.Flush();
    }

    /// <summary>A drill page's demand (ch 12 §7a): the band's identity, then its cards' rows. <paramref name="browse"/>
    /// stamps an unformed row a browse shelf first, so the planner routes it to <c>browseSection</c>
    /// (<c>Entities.BrowseSection</c>'s rule, for a row minted through <c>Entities.Section</c>).</summary>
    public static void EnsureSection(Section s, bool browse)
    {
        if (!s.IsValid) return;
        var t = Entities.Current.Sections;
        if (browse && t.Form[s.Slot] == (byte)SectionKind.Unknown) t.Form[s.Slot] = (byte)SectionKind.BrowseShelf;
        int slot = s.Slot;
        Entities.Ensure(t, new ReadOnlySpan<int>(in slot), (uint)SectionFields.Identity);
        s_demand.Clear();
        s_demand.AddCards(s);
        s_demand.Flush();
    }

    /// <summary>"Show all", the append preloader and the Charts walk: the next page of the band's cards at
    /// <c>HomeSectionPaging.NextOffset(view)</c>. False when there is nothing to ask for — the server's EXPLICIT terminator
    /// (<see cref="SectionPaging.Complete"/>), a band with no identity, or that page already asked this scope. Totals are
    /// NOT consulted: the walk must ask past an under-reported total (<c>BrowseSectionWalk.Begin</c>).</summary>
    public static bool RequestNextPage(Section s, bool browse)
    {
        if (!s.IsValid || s.NextOffset == SectionPaging.Complete) return false;
        int offset = HomeSectionPaging.NextOffset(HomeSectionView.Of(s));
        var edge = browse ? FetchEdge.BrowseSectionCards : FetchEdge.HomeSectionCards;
        if (E.SectionCards.WasAsked(s.Slot, offset)) return false;
        Entities.EnsureEdge(edge, s.Slot, offset);
        return true;
    }

    static readonly CardDemand s_demand = new();

    /// <summary>The batched card demand's scratch: one slot list per card table, reused (P8), UI thread only.</summary>
    sealed class CardDemand
    {
        readonly int[][] _slots = [new int[32], new int[32], new int[32], new int[32], new int[32], new int[32]];
        readonly int[] _counts = new int[6];

        public void Clear() => Array.Clear(_counts);

        public void AddCards(Section s)
        {
            var targets = s.CardSlots;
            var kinds = s.CardKinds;
            for (int i = 0; i < targets.Length && i < kinds.Length; i++)
            {
                int lane = LaneOf(kinds[i].Kind);
                if (lane < 0 || targets[i] <= Table.None) continue;
                ref int[] slots = ref _slots[lane];
                if (_counts[lane] == slots.Length) Array.Resize(ref slots, slots.Length * 2);
                slots[_counts[lane]++] = targets[i];
            }
        }

        public void Flush()
        {
            var scope = Entities.Current;
            Ensure(scope.Playlists, 0, (uint)PlaylistFields.Row);
            Ensure(scope.Albums, 1, (uint)AlbumFields.Identity);
            Ensure(scope.Artists, 2, (uint)ArtistFields.Identity);
            Ensure(scope.Shows, 3, (uint)ShowFields.Identity);
            Ensure(scope.Episodes, 4, (uint)EpisodeFields.Row);
            Ensure(scope.Tracks, 5, (uint)TrackFields.Identity);
        }

        void Ensure(Table table, int lane, uint fields)
        {
            if (_counts[lane] > 0) Entities.Ensure(table, _slots[lane].AsSpan(0, _counts[lane]), fields);
            _counts[lane] = 0;
        }

        // Liked Songs (a collection) is never asked: its identity is the home answer's and its art the user's treatment.
        static int LaneOf(EntityKind kind) => kind switch
        {
            EntityKind.Playlist => 0,
            EntityKind.Album => 1,
            EntityKind.Artist => 2,
            EntityKind.Show => 3,
            EntityKind.Episode => 4,
            EntityKind.Track => 5,
            _ => -1,
        };
    }
}

// ── 9. the composed feed model (Wave 5, WP-5.P contract §2; 0.2.9 `Wavee.Core/Library/HomeFeed.cs`) ───────────────────
//
// The 0.2.9 record graph (`HomeFeed` / `HomeGroup` / `HomeSection` / `HomeCard` + `HomeCardMeta`) is REPLACED by a
// presentation view over the tables: the groups and the section ledger are records (so the ported rules and their tests
// keep their shape), but a card is a HANDLE — an entity ref plus the section it was read from — whose every property is a
// live column read. A card whose playlist row hydrates re-describes itself on the next render with no recompose.

/// <summary>How a home group is laid out — 0.2.9's order verbatim. <c>HomeLayoutModules.KindName</c> persists the
/// NAMES, never the ordinals.</summary>
public enum HomeGroupKind : byte
{
    Hero, QuickGrid, Shelf, Featured,
    MixBand, WeeklyPair, ChipCards, RadioDial, RatedShelf, QueueList, DiscoverFeed,
    Recents,
    Topic, SectionEntry,
    PodcastShelf,
}

/// <summary>What a home card points at — drives the route, the shape and the play verb. Audiobook and Podcast both carry
/// a <c>spotify:show:</c> uri and render differently.</summary>
public enum HomeCardKind : byte { Playlist, Album, Artist, Track, Liked, Episode, Audiobook, Podcast }

/// <summary>A card HANDLE: the section-edge target plus the section it was read from. Every property reads columns LIVE
/// (<c>StringTable.Resolve</c> is allocation-free), with the section's card facts (§2b) first where the entity has no
/// column. <c>default(HomeCard)</c> is the BLANK card; <see cref="Blank"/> mints a blank of a kind and format (the
/// skeleton seed's silhouette needs both — a weekly pair of blanks must still BE a pair).</summary>
public readonly record struct HomeCard(EntityRef Target, int SectionSlot = Table.None)
{
    static readonly string?[] s_blankFormats =
        [null, "daylist", "discover-weekly", "release-radar", "daily-mix", "topic-mix", "inspiredby-mix", "editorial"];

    /// <summary>A blank card of <paramref name="kind"/> (and <paramref name="format"/>). <paramref name="index"/> keeps two
    /// blanks of one module distinct, exactly as 0.2.9's <c>wavee:skeleton:&lt;group&gt;:&lt;i&gt;</c> uris did — a
    /// projection dedupes cards, and a skeleton grid of eight identical blanks would collapse to one.</summary>
    public static HomeCard Blank(HomeCardKind kind = HomeCardKind.Playlist, int index = 0, string? format = null)
    {
        int f = format is null ? 0 : Array.IndexOf(s_blankFormats, format);
        return new HomeCard(default, -(1 + ((Math.Max(0, index) & 0xFFFFF) << 8) + ((int)kind << 4) + Math.Max(0, f)));
    }

    public bool IsBlank => Target.IsNone;

    /// <summary>A key two occurrences of one card share: the entity (a playlist and its collection spelling fold), or the
    /// blank's own code.</summary>
    public long DedupeKey => IsBlank
        ? SectionSlot
        : ((long)(Target.Kind == EntityKind.Collection ? EntityKind.Playlist : Target.Kind) << 32) | (uint)Target.Slot;

    public HomeCardKind Kind
    {
        get
        {
            if (IsBlank) return SectionSlot < 0 ? (HomeCardKind)(((-SectionSlot - 1) >> 4) & 0xF) : HomeCardKind.Playlist;
            return Target.Kind switch
            {
                EntityKind.Collection => HomeCardKind.Liked,
                EntityKind.Album => HomeCardKind.Album,
                EntityKind.Artist => HomeCardKind.Artist,
                EntityKind.Track => HomeCardKind.Track,
                EntityKind.Episode => HomeCardKind.Episode,
                EntityKind.Show => (FlagsOf() & HomeCardFlags.Audiobook) != 0 ? HomeCardKind.Audiobook : HomeCardKind.Podcast,
                _ => HomeCardKind.Playlist,
            };
        }
    }

    /// <summary>The card's identity text — "" for a blank. ALLOCATES for a gid-form id (a cold call site: a key, a log
    /// line, a test); a hot path compares <see cref="Target"/> or <see cref="DedupeKey"/> instead.</summary>
    public string Uri => Id is { IsEmpty: false } id ? id.Text : "";

    /// <summary>The target row's identity, <c>default</c> for a blank or a stale handle. Read through
    /// <see cref="SectionTable.CardTable"/>, NOT <see cref="EntityRef.Id"/>: <c>Entities.TableFor(Collection)</c> answers
    /// null (Liked lives in the playlist table), so the ref's own getter reads a Liked card as no identity at all.</summary>
    public EntityId Id => !IsBlank && SectionTable.CardTable(Target.Kind) is { } table && (uint)Target.Slot < (uint)table.Count
        ? table.Id[Target.Slot] : default;

    /// <summary>The target row knows its Identity group.</summary>
    public bool KnowsIdentity => !IsBlank && Live && Target.Kind switch
    {
        EntityKind.Playlist or EntityKind.Collection => S.Playlists.Knows(Target.Slot, (uint)PlaylistFields.Identity),
        EntityKind.Album => S.Albums.Knows(Target.Slot, (uint)AlbumFields.Title),
        EntityKind.Artist => S.Artists.Knows(Target.Slot, (uint)ArtistFields.Name),
        EntityKind.Track => S.Tracks.Knows(Target.Slot, (uint)TrackFields.Title),
        EntityKind.Episode => S.Episodes.Knows(Target.Slot, (uint)EpisodeFields.Title),
        EntityKind.Show => S.Shows.Knows(Target.Slot, (uint)ShowFields.Title),
        _ => false,
    };

    public string Title => Resolve(TitleId);

    public StringId TitleId => IsBlank || !Live ? StringId.Empty : Target.Kind switch
    {
        EntityKind.Playlist or EntityKind.Collection => S.Playlists.Title[Target.Slot],
        EntityKind.Album => S.Albums.Title[Target.Slot],
        EntityKind.Artist => S.Artists.Name[Target.Slot],
        EntityKind.Track => S.Tracks.Title[Target.Slot],
        EntityKind.Episode => S.Episodes.Title[Target.Slot],
        EntityKind.Show => S.Shows.Title[Target.Slot],
        _ => StringId.Empty,
    };

    /// <summary>0.2.9's one subtitle slot, verbatim: the section's own line when the answer stated one
    /// (<c>description ?? owner</c>, the first artist, "Artist", the show, the author, the publisher, a recents card's
    /// "Song - A, B"); otherwise the same derivation from the columns. Null when there is nothing to say.</summary>
    public string? Subtitle
    {
        get
        {
            if (IsBlank || !Live) return null;
            int fact = Fact;
            if (fact >= 0 && !S.Sections.FactSubtitle[fact].IsEmpty) return Resolve(S.Sections.FactSubtitle[fact]);
            int slot = Target.Slot;
            StringId id = Target.Kind switch
            {
                EntityKind.Playlist or EntityKind.Collection => !S.Playlists.Description[slot].IsEmpty
                    ? S.Playlists.Description[slot] : OwnerNameId,
                EntityKind.Album => FirstAlbumArtistId(slot),
                EntityKind.Track => S.Tracks.ArtistLine[slot],
                EntityKind.Episode => S.Episodes.Show[slot] > Table.None ? S.Shows.Title[S.Episodes.Show[slot]] : StringId.Empty,
                EntityKind.Show => S.Shows.Publisher[slot],
                _ => StringId.Empty,
            };
            if (Target.Kind == EntityKind.Artist) return ArtistSubtitle;
            return id.IsEmpty ? null : Resolve(id);
        }
    }

    /// <summary>The literal 0.2.9's mapper wrote under an artist card (<c>SpotifyExportMapper.cs:1204</c>).</summary>
    public const string ArtistSubtitle = "Artist";

    public string? ImageUrl
    {
        get
        {
            if (IsBlank || !Live) return null;
            StringId id = Target.Kind switch
            {
                EntityKind.Playlist => S.Playlists.Image[Target.Slot],
                EntityKind.Album => S.Albums.Image[Target.Slot],
                EntityKind.Artist => S.Artists.Image[Target.Slot],
                // A pathfinder-thin track card without its own cover falls back to its album's (S3) — the same
                // fallback `Artist.UI.Chart.cs`'s chart row applies, for a card staged before the pathfinder fix ran.
                EntityKind.Track => !S.Tracks.Image[Target.Slot].IsEmpty ? S.Tracks.Image[Target.Slot]
                    : S.Tracks.Album[Target.Slot] > Table.None ? S.Albums.Image[S.Tracks.Album[Target.Slot]] : StringId.Empty,
                EntityKind.Episode => S.Episodes.Image[Target.Slot],
                EntityKind.Show => S.Shows.Image[Target.Slot],
                _ => StringId.Empty,   // Liked: the user's own treatment replaces the stock heart (0.2.9)
            };
            return id.IsEmpty ? null : Resolve(id);
        }
    }

    /// <summary>The SECTION's title when that section is a baseline recommendation ("For fans of IU"), else null — 0.2.9
    /// copied it onto each card only because cards were records.</summary>
    public string? Eyebrow
    {
        get
        {
            if (SectionSlot <= Table.None || SectionSlot >= S.Sections.Count) return null;
            if ((SectionKind)S.Sections.Form[SectionSlot] != SectionKind.HomeBaseline) return null;
            var title = S.Sections.Title[SectionSlot];
            return title.IsEmpty ? null : Resolve(title);
        }
    }

    /// <summary>Spotify's playlist format TOKEN (<c>daylist</c>, <c>daily-mix</c>, <c>artist-mix-reader</c>, …): the raw
    /// token the answer carried, else the canonical token of the playlist's format column; null otherwise.</summary>
    public string? Format
    {
        get
        {
            if (IsBlank) return SectionSlot < 0 ? s_blankFormats[(-SectionSlot - 1) & 0xF] : null;
            if (!Live || Target.Kind is not (EntityKind.Playlist or EntityKind.Collection)) return null;
            int fact = Fact;
            if (fact >= 0 && !S.Sections.FactFormat[fact].IsEmpty) return Resolve(S.Sections.FactFormat[fact]);
            return (PlaylistFormat)S.Playlists.Format[Target.Slot] switch
            {
                PlaylistFormat.Daylist => "daylist",
                PlaylistFormat.DailyMix => "daily-mix",
                PlaylistFormat.DiscoverWeekly => "discover-weekly",
                PlaylistFormat.ReleaseRadar => "release-radar",
                PlaylistFormat.TopicMix => "topic-mix",
                PlaylistFormat.InspiredByMix => "inspiredby-mix",
                PlaylistFormat.Editorial => "editorial",
                PlaylistFormat.Chart => "chart",
                PlaylistFormat.Radio => "radio",
                _ => null,
            };
        }
    }

    /// <summary>The payload's <c>extractedColors.colorDark</c> as ARGB; 0 = unknown.</summary>
    public uint Accent
    {
        get
        {
            if (IsBlank || !Live) return 0;
            int fact = Fact;
            if (fact >= 0 && S.Sections.FactAccent[fact] != 0) return S.Sections.FactAccent[fact];
            return Target.Kind is EntityKind.Playlist or EntityKind.Collection
                && S.Playlists.Knows(Target.Slot, (uint)PlaylistFields.Accent) ? S.Playlists.Accent[Target.Slot] : 0;
        }
    }

    public int TrackCount => IsBlank || !Live ? 0 : Target.Kind switch
    {
        EntityKind.Playlist or EntityKind.Collection => S.Playlists.TrackCount[Target.Slot],
        EntityKind.Album => S.Albums.TrackCount[Target.Slot],
        _ => 0,
    };

    /// <summary>The seed chips (a mix's artists, a daylist's terms); null when none. Built once per fact and cached.</summary>
    public IReadOnlyList<string>? Seeds => Fact is var f && f >= 0 ? S.Sections.SeedsOf(f) : null;

    /// <summary>How many seeds — the allocation-free twin of <see cref="Seeds"/>.</summary>
    public int SeedCount => Fact is var f && f >= 0 ? S.Sections.FactSeedCount[f] : 0;

    /// <summary>Seed <paramref name="i"/> — the allocation-free twin of <see cref="Seeds"/>.</summary>
    public string SeedAt(int i) => Fact is var f && f >= 0 && (uint)i < S.Sections.FactSeedCount[f]
        ? Resolve(S.Sections.Seeds[S.Sections.FactSeedAt[f] + i]) : "";

    public string? OwnerName => OwnerNameId is { IsEmpty: false } id ? Resolve(id) : null;

    public long DurationMs
    {
        get
        {
            if (IsBlank || !Live) return 0;
            int fact = Fact;
            if (fact >= 0 && S.Sections.FactDurationMs[fact] > 0) return S.Sections.FactDurationMs[fact];
            return Target.Kind switch
            {
                EntityKind.Episode => S.Episodes.DurationMs[Target.Slot],
                EntityKind.Track => S.Tracks.DurationMs[Target.Slot],
                _ => 0,
            };
        }
    }

    public long ResumeMs
    {
        get
        {
            if (IsBlank || !Live) return 0;
            int fact = Fact;
            if (fact >= 0 && S.Sections.FactResumeMs[fact] > 0) return S.Sections.FactResumeMs[fact];
            return Target.Kind == EntityKind.Episode && S.Episodes.Knows(Target.Slot, (uint)EpisodeFields.Progress)
                ? S.Episodes.ProgressMs[Target.Slot] : 0;
        }
    }

    public bool HasVideo => (FlagsOf() & HomeCardFlags.HasVideo) != 0
        || (!IsBlank && Live && Target.Kind == EntityKind.Track
            && (S.Tracks.Flags[Target.Slot] & (uint)TrackFlags.VideoMask) != 0);

    /// <summary>The audiobook's average rating (0 when withheld, or for any other card).</summary>
    public double Rating => Fact is var f && f >= 0 ? S.Sections.FactRating[f] / 100d : 0d;

    public string? Author => FactText(static (t, f) => t.FactAuthor[f]);

    public string? Signifier => FactText(static (t, f) => t.FactSignifier[f]);

    public string? GenericTitle => PlaylistText(static (t, slot) => t.GenericTitle[slot]);

    /// <summary>A daylist whose name is empty or still its generic pre-title: a shallow identity that must not be
    /// displayed as a personalized title (0.2.9 <c>HomeCardMeta.NeedsHydration</c>). LIVE: it flips the render after the
    /// real header lands, which is what 0.2.9's hydrator loop became (ch 10 §7 DATA GAPS).</summary>
    public bool NeedsHydration
    {
        get
        {
            if (IsBlank || !Live || Format != "daylist") return false;
            var title = TitleId;
            if (title.IsEmpty) return true;
            var generic = S.Playlists.GenericTitle[Target.Slot];
            return !generic.IsEmpty && (generic.Value == title.Value || Resolve(generic) == Resolve(title));
        }
    }

    /// <summary>Unix ms the daylist's window rolls over; 0 = unknown.</summary>
    public long ExpiresAtMs => IsPlaylist ? S.Playlists.DaylistExpiresAt[Target.Slot] * 1000L : 0;

    /// <summary>Unix ms the daylist's window began; 0 = unknown.</summary>
    public long CreatedAtMs => IsPlaylist ? S.Playlists.DaylistCreatedAt[Target.Slot] * 1000L : 0;

    public string? HeaderImageUrl => PlaylistText(static (t, slot) => t.HeaderImage[slot]);

    /// <summary>No mosaic in 0.3 (the cover is one url); kept so the 0.2.9 call sites port.</summary>
    public IReadOnlyList<string>? MosaicTiles => null;

    // ── plumbing ──

    static Scope S => Entities.Current;
    static string Resolve(StringId id) => Entities.Strings.Resolve(id);

    /// <summary>The target still indexes a row of the CURRENT scope (a handle held across a switch does not).</summary>
    bool Live => Target.Slot < (SectionTable.CardTable(Target.Kind)?.Count ?? 0);

    bool IsPlaylist => !IsBlank && Live && Target.Kind is EntityKind.Playlist or EntityKind.Collection;

    int Fact => IsBlank || SectionSlot <= Table.None ? -1 : S.Sections.FactIndexOf(SectionSlot, Target.Kind, Target.Slot);

    HomeCardFlags FlagsOf() => Fact is var f && f >= 0 ? (HomeCardFlags)S.Sections.FactFlags[f] : HomeCardFlags.None;

    string? FactText(Func<SectionTable, int, StringId> column)
    {
        int f = Fact;
        if (f < 0) return null;
        var id = column(S.Sections, f);
        return id.IsEmpty ? null : Resolve(id);
    }

    string? PlaylistText(Func<PlaylistTable, int, StringId> column)
    {
        if (!IsPlaylist) return null;
        var id = column(S.Playlists, Target.Slot);
        return id.IsEmpty ? null : Resolve(id);
    }

    StringId OwnerNameId
    {
        get
        {
            if (!IsPlaylist) return StringId.Empty;
            int owner = S.Playlists.Owner[Target.Slot];
            return owner > Table.None && owner < S.Users.Count ? S.Users.Name[owner] : StringId.Empty;
        }
    }

    static StringId FirstAlbumArtistId(int album)
    {
        var artists = S.Edges.AlbumArtists.Targets(album);
        return artists.Length > 0 && artists[0] > Table.None && artists[0] < S.Artists.Count
            ? S.Artists.Name[artists[0]] : StringId.Empty;
    }
}

/// <summary>A titled group of home cards laid out per <see cref="HomeGroupKind"/>. <see cref="Title"/> is the SERVER's
/// label verbatim, or null (a split section's continuation, the quick grid).</summary>
public sealed record HomeGroup(HomeGroupKind Kind, string? Title, IReadOnlyList<HomeCard> Cards,
    string? Subtitle = null, string? Uri = null, int TotalCount = 0);

/// <summary>One section of the lossless ledger (0.2.9 <c>HomeSection</c> + <c>HomeSectionPageResult</c>): its cards in
/// response order, deduplicated only inside the section, and the raw accounting
/// <c>RawItemCount == Cards.Count + UnsupportedCount + DuplicateCount</c>.</summary>
public sealed record HomeSectionView(int Slot, string? Uri, string? Title, string? Subtitle,
    IReadOnlyList<HomeCard> Cards, int TotalCount, int RawItemCount, int UnsupportedCount = 0, int DuplicateCount = 0,
    int NextOffset = SectionPaging.NoCursor, SectionKind Kind = SectionKind.Unknown, bool IsChart = false)
{
    /// <summary>No section.</summary>
    public static readonly HomeSectionView Empty = new(Table.None, null, null, null, Array.Empty<HomeCard>(), 0, 0);

    static Scope? s_scope;
    static HomeSectionView?[] s_views = [];
    static uint[] s_versions = [], s_cardVersions = [];

    /// <summary>The row as it stands: cards deduped by entity from <see cref="Edges.SectionCards"/>, the ledger from the
    /// columns. MEMOIZED per (slot, <see cref="Section.Version"/>, <see cref="Section.CardVersion"/>) — the same instance
    /// comes back until either moves, which is what lets a render compare by reference. UI thread only (C1).</summary>
    public static HomeSectionView Of(Section s)
    {
        if (!s.IsValid) return Empty;
        var scope = Entities.Current;
        if (!ReferenceEquals(scope, s_scope)) { s_scope = scope; Array.Clear(s_views); }
        int slot = s.Slot;
        if (slot >= s_views.Length)
        {
            int size = Math.Max(slot + 1, Math.Max(32, s_views.Length * 2));
            Array.Resize(ref s_views, size);
            Array.Resize(ref s_versions, size);
            Array.Resize(ref s_cardVersions, size);
        }
        uint version = s.Version, cardVersion = s.CardVersion;
        if (s_views[slot] is { } cached && s_versions[slot] == version && s_cardVersions[slot] == cardVersion) return cached;

        var targets = s.CardSlots;
        var kinds = s.CardKinds;
        var cards = new List<HomeCard>(targets.Length);
        int duplicates = 0;
        for (int i = 0; i < targets.Length && i < kinds.Length; i++)
        {
            if (targets[i] <= Table.None) continue;             // a page hole (ReplacePage past the end)
            var card = new HomeCard(new EntityRef(kinds[i].Kind, targets[i]), slot);
            if (Holds(cards, card)) { duplicates++; continue; }
            cards.Add(card);
        }

        var t = scope.Sections;
        string? uri = s.Id.Form == EntityForm.None ? null : s.Id.Text;
        var view = new HomeSectionView(slot, uri, TextOrNull(t.Title[slot]), TextOrNull(t.Subtitle[slot]), cards,
            Math.Max(s.Total, cards.Count), s.Raw, s.Unsupported, s.Duplicates + duplicates, s.NextOffset, s.Kind, s.IsChart);
        s_views[slot] = view;
        s_versions[slot] = version;
        s_cardVersions[slot] = cardVersion;
        return view;
    }

    static bool Holds(List<HomeCard> cards, in HomeCard card)
    {
        long key = card.DedupeKey;
        for (int i = 0; i < cards.Count; i++) if (cards[i].DedupeKey == key) return true;
        return false;
    }

    static string? TextOrNull(StringId id) => id.IsEmpty ? null : Entities.Strings.Resolve(id);
}

/// <summary>A home facet chip (<c>home.homeChips[]</c>). <see cref="Id"/> is the opaque server token that goes back into
/// the <c>facet</c> request variable; <see cref="SubChips"/> is the second level ("Following").</summary>
public sealed record HomeChip(string Id, string Label, IReadOnlyList<HomeChip> SubChips);

/// <summary>The composed Home document for one facet: greeting, typed groups, chips and the section ledger.</summary>
public sealed record HomeFeedView(string Greeting, IReadOnlyList<HomeGroup> Groups,
    IReadOnlyList<HomeChip>? Chips = null, IReadOnlyList<HomeSectionView>? Sections = null, string Facet = "")
{
    public static readonly HomeFeedView Empty = new("", Array.Empty<HomeGroup>());

    /// <summary>0.2.9 <c>FakeData.HomeSeed</c> — the BLANK-shaped document the landing skeleton is derived from (ch 31
    /// W2): fourteen groups at the counts each module SHOWS, in the composer's order, plus the 3-entry section ledger, and
    /// no chips (ch 10 §11 audit row 2). The shimmer IS this tree, so it tracks the loaded layout.</summary>
    public static HomeFeedView Seed { get; } = BuildSeed();

    static HomeFeedView BuildSeed()
    {
        int next = 0;
        HomeCard[] Blanks(int n, HomeCardKind kind)
        {
            var cards = new HomeCard[n];
            for (int i = 0; i < n; i++) cards[i] = HomeCard.Blank(kind, next++);
            return cards;
        }
        return new HomeFeedView("",
        [
            new(HomeGroupKind.Hero, " ", [HomeCard.Blank(HomeCardKind.Playlist, next++, "daylist")],
                Uri: "wavee:skeleton:section:hero", TotalCount: 1),
            new(HomeGroupKind.WeeklyPair, null,
                [HomeCard.Blank(HomeCardKind.Playlist, next++, "discover-weekly"),
                 HomeCard.Blank(HomeCardKind.Playlist, next++, "release-radar")]),
            new(HomeGroupKind.QuickGrid, " ", Blanks(8, HomeCardKind.Playlist)),
            new(HomeGroupKind.Recents, " ", Blanks(8, HomeCardKind.Album)),
            new(HomeGroupKind.MixBand, " ", Blanks(6, HomeCardKind.Playlist)),
            new(HomeGroupKind.ChipCards, " ", Blanks(6, HomeCardKind.Playlist)),
            new(HomeGroupKind.RadioDial, " ", Blanks(12, HomeCardKind.Playlist)),
            new(HomeGroupKind.QueueList, " ", Blanks(6, HomeCardKind.Episode)),
            new(HomeGroupKind.RatedShelf, " ", Blanks(6, HomeCardKind.Audiobook)),
            new(HomeGroupKind.Featured, " ", Blanks(4, HomeCardKind.Playlist)),
            new(HomeGroupKind.PodcastShelf, " ", Blanks(6, HomeCardKind.Podcast),
                Uri: "wavee:skeleton:section:podcasts", TotalCount: 6),
            new(HomeGroupKind.Topic, " ", Blanks(7, HomeCardKind.Playlist), Uri: "wavee:skeleton:section:topic", TotalCount: 20),
            new(HomeGroupKind.SectionEntry, " ", Blanks(7, HomeCardKind.Playlist), Uri: "wavee:skeleton:section:mixed", TotalCount: 20),
            new(HomeGroupKind.DiscoverFeed, " ", Blanks(12, HomeCardKind.Playlist)),
        ], Sections:
        [
            new(Table.None, "wavee:skeleton:section:topic", " ", " ", Blanks(7, HomeCardKind.Playlist), 20, 7),
            new(Table.None, "wavee:skeleton:section:mixed", " ", null, Blanks(7, HomeCardKind.Playlist), 20, 7),
            new(Table.None, "wavee:skeleton:section:podcasts", " ", null, Blanks(6, HomeCardKind.Podcast), 6, 6),
        ]);
    }
}

/// <summary>App-authored labels for modules that combine or supplement source sections (0.2.9 verbatim).</summary>
public sealed record HomeModuleTitles(
    string JumpBackIn = "Jump back in",
    string Recents = "Recents",
    string MadeForYou = "Made for you",
    string TopMixes = "Your top mixes",
    string Radio = "Radio",
    string UpNext = "Up next",
    string Audiobooks = "Audiobooks for you",
    string EditorsPicks = "Editors' picks",
    string BecauseYouListened = "Because you listened",
    string Podcasts = "Podcasts")
{
    public static readonly HomeModuleTitles Default = new();
}

/// <summary>The one place Home's module names cross from the loc system into the composer. Rebuilt per read — Loc is
/// live, and a snapshot would pin the startup language for the process.</summary>
public static class HomeModuleCopy
{
    public static HomeModuleTitles Titles => new(
        JumpBackIn: Loc.Get(Strings.Home.JumpBackIn),
        Recents: Loc.Get(Strings.Home.Recents),
        MadeForYou: Loc.Get(Strings.Home.MadeForYou),
        TopMixes: Loc.Get(Strings.Home.TopMixes),
        Radio: Loc.Get(Strings.Home.Radio),
        UpNext: Loc.Get(Strings.Home.UpNext),
        Audiobooks: Loc.Get(Strings.Home.AudiobooksForYou),
        EditorsPicks: Loc.Get(Strings.Home.EditorsPicks),
        BecauseYouListened: Loc.Get(Strings.Home.BecauseYouListened),
        Podcasts: Loc.Get(Strings.Home.Podcasts));
}

// ── 10. the composer (0.2.9 `SpotifyHomeComposer.Compose`, ported over the tables) ───────────────────────────────────

/// <summary>Projects one Home subject row into authored module previews plus the lossless section ledger. The typename
/// verdict is the section's <see cref="SectionKind"/> column (read at decode); classification is per card, grouping and
/// dedupe per section. NO synthetic library quick grid (ch 31 §0.10g: there is one "Jump back in", the server's).</summary>
public static class HomeComposer
{
    public static HomeFeedView Compose(Home h, HomeModuleTitles titles)
    {
        if (!h.IsValid) return HomeFeedView.Empty;
        var t = titles;
        var groups = new List<HomeGroup>();
        var ledger = new List<HomeSectionView>();
        HomeGroup? spotlight = null;

        var slots = h.SectionSlots;
        for (int i = 0; i < slots.Length; i++)
        {
            var section = new Section(slots[i]);
            if (!section.IsValid) continue;
            var view = HomeSectionView.Of(section);
            switch (section.Kind)
            {
                case SectionKind.HomeSpotlight:
                    {
                        ledger.Add(view);
                        if (view.Cards.Count == 0)
                        {
                            if (HasIdentity(view)) groups.Add(Group(HomeGroupKind.SectionEntry, view, view.Cards, true));
                            break;
                        }
                        var hero = Group(HomeGroupKind.Hero, view, view.Cards, true);
                        if (spotlight is null) spotlight = hero;
                        else groups.Add(hero);
                        break;
                    }
                case SectionKind.HomeBaseline:
                    // The eyebrow (the section's own title) is a LIVE read on each card (HomeCard.Eyebrow).
                    ledger.Add(view);
                    groups.Add(Group(view.Cards.Count > 0 ? HomeGroupKind.DiscoverFeed : HomeGroupKind.SectionEntry,
                        view, view.Cards, true));
                    break;
                case SectionKind.HomeRecentlyPlayed:
                    {
                        // 0.2.9 `title ?? t.Recents`, on the ledger AND the group.
                        var titled = view.Title is null ? view with { Title = t.Recents } : view;
                        ledger.Add(titled);
                        groups.Add(Group(titled.Cards.Count > 0 ? HomeGroupKind.Recents : HomeGroupKind.SectionEntry,
                            titled, titled.Cards, true));
                        break;
                    }
                default:
                    // Generic, shorts and unknown future types enter the ledger and degrade to the card-driven classifier.
                    ledger.Add(view);
                    EmitSectionGroups(view, groups);
                    break;
            }
        }

        // Spotlight is the preferred Hero preview regardless of response position.
        if (spotlight is not null) groups.Insert(0, spotlight);

        return new HomeFeedView(Text(h.GreetingId), groups, Chips(h), ledger, Text(h.FacetId));
    }

    static readonly Dictionary<int, Memo> s_memo = new();
    static Scope? s_memoScope;

    sealed record Memo(uint Version, uint SectionVersion, ulong SectionsKey, HomeModuleTitles Titles, HomeFeedView View);

    /// <summary>The composed document, memoized per Home row on (version, section-list version, every section's
    /// Version + CardVersion, titles BY VALUE — Loc is live). <see cref="HomeFeedView.Empty"/> for a row that knows
    /// nothing and holds no section. UI thread only (C1).</summary>
    public static HomeFeedView For(Home h, HomeModuleTitles titles)
    {
        if (!h.IsValid) return HomeFeedView.Empty;
        var scope = Entities.Current;
        if (!ReferenceEquals(scope, s_memoScope)) { s_memoScope = scope; s_memo.Clear(); }
        if ((scope.Homes.Known[h.Slot] & (uint)HomeFields.All) == 0 && h.SectionCount == 0) return HomeFeedView.Empty;

        ulong key = 14695981039346656037UL;
        foreach (int slot in h.SectionSlots)
        {
            var s = new Section(slot);
            key = (key ^ (uint)slot) * 1099511628211UL;
            if (!s.IsValid) continue;
            key = (key ^ s.Version) * 1099511628211UL;
            key = (key ^ s.CardVersion) * 1099511628211UL;
        }
        if (s_memo.TryGetValue(h.Slot, out var memo) && memo.Version == h.Version && memo.SectionVersion == h.SectionVersion
            && memo.SectionsKey == key && memo.Titles == titles)
            return memo.View;

        var view = Compose(h, titles);
        s_memo[h.Slot] = new Memo(h.Version, h.SectionVersion, key, titles, view);
        return view;
    }

    static void EmitSectionGroups(HomeSectionView section, List<HomeGroup> groups)
    {
        var cards = section.Cards;
        if (cards.Count == 0)
        {
            if (HasIdentity(section)) groups.Add(Group(HomeGroupKind.SectionEntry, section, cards, true));
            return;
        }

        int editorial = 0;
        for (int i = 0; i < cards.Count; i++) if (IsEditorialFormat(cards[i].Format)) editorial++;
        if (editorial * 2 > cards.Count)
        {
            groups.Add(Group(HomeGroupKind.Topic, section, cards, true));
            return;
        }

        var byKind = new Dictionary<HomeGroupKind, List<HomeCard>>();
        for (int i = 0; i < cards.Count; i++)
        {
            var kind = ModuleFor(cards[i]);
            if (!byKind.TryGetValue(kind, out var list)) byKind.Add(kind, list = []);
            list.Add(cards[i]);
        }

        HomeGroupKind? dominant = null;
        foreach (var pair in byKind)
            if (pair.Value.Count * 2 > cards.Count) { dominant = pair.Key; break; }

        bool moduleOwnsTitle = dominant is not null && section.Title is { Length: > 0 };
        if (dominant is null || !moduleOwnsTitle)
            groups.Add(Group(HomeGroupKind.SectionEntry, section, cards, true));

        foreach (var pair in byKind)
            groups.Add(Group(pair.Key, section, pair.Value, moduleOwnsTitle && dominant == pair.Key));
    }

    /// <summary>The editorial formats — a section more than half of which is these is a Topic.</summary>
    public static bool IsEditorialFormat(string? format) =>
        format is "editorial" or "format-shows-shuffle" or "artistsets" or "descripto";

    static bool HasIdentity(HomeSectionView section) => section.Uri is { Length: > 0 } || section.Title is { Length: > 0 };

    static HomeGroup Group(HomeGroupKind kind, HomeSectionView section, IReadOnlyList<HomeCard> cards, bool ownsTitle) =>
        new(kind, ownsTitle ? section.Title : null, cards, ownsTitle ? section.Subtitle : null, section.Uri, section.TotalCount);

    /// <summary>The module a card's CONTENT names (never its copy).</summary>
    public static HomeGroupKind ModuleFor(in HomeCard card) => card.Kind switch
    {
        HomeCardKind.Episode => HomeGroupKind.QueueList,
        HomeCardKind.Audiobook => HomeGroupKind.RatedShelf,
        HomeCardKind.Podcast => HomeGroupKind.PodcastShelf,
        HomeCardKind.Playlist => ModuleForFormat(card.Format),
        _ => HomeGroupKind.QuickGrid,
    };

    /// <summary>The module a playlist format token routes to.</summary>
    public static HomeGroupKind ModuleForFormat(string? format) => format switch
    {
        "daylist" => HomeGroupKind.Hero,
        "daily-mix" => HomeGroupKind.MixBand,
        "discover-weekly" or "release-radar" => HomeGroupKind.WeeklyPair,
        "topic-mix" or "artist-mix-reader" => HomeGroupKind.ChipCards,
        "inspiredby-mix" => HomeGroupKind.RadioDial,
        "editorial" or "format-shows-shuffle" or "artistsets" or "descripto" => HomeGroupKind.Featured,
        _ => HomeGroupKind.QuickGrid,
    };

    /// <summary>The chip strip as a tree: top-level chips in server order, each with its sub-chips. Null for no chips
    /// (0.2.9 <c>MapChips</c>); a chip with a blank id or label is dropped.</summary>
    static IReadOnlyList<HomeChip>? Chips(Home h)
    {
        var ids = h.ChipIds;
        if (ids.Length == 0) return null;
        var labels = h.ChipLabels;
        var parents = h.ChipParents;
        var chips = new List<HomeChip>(ids.Length);
        for (int i = 0; i < ids.Length; i++)
        {
            if (parents[i] >= 0) continue;
            string id = Text(ids[i]), label = Text(labels[i]);
            if (id.Length == 0 || label.Length == 0) continue;
            List<HomeChip>? subs = null;
            for (int j = 0; j < ids.Length; j++)
            {
                if (parents[j] != i) continue;
                string subId = Text(ids[j]), subLabel = Text(labels[j]);
                if (subId.Length == 0 || subLabel.Length == 0) continue;
                (subs ??= new List<HomeChip>(2)).Add(new HomeChip(subId, subLabel, Array.Empty<HomeChip>()));
            }
            chips.Add(new HomeChip(id, label, (IReadOnlyList<HomeChip>?)subs ?? Array.Empty<HomeChip>()));
        }
        return chips.Count > 0 ? chips : null;
    }

    static string Text(StringId id) => Entities.Strings.Resolve(id);
}
