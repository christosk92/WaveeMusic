// ── Entities/Concert.cs — CORE (owner A, wave 1; plan §2 · the file's full budget is 1,050) ──────────────────────────
//
// THE CONCERT COLUMNS, FLAGS, FIELD GROUPS AND HANDLE, plus the three small tables the surface cannot exist without:
// PLACES (where the user is looking), CONCEPTS (the genre tokens for that place) and the FEED SUBJECT (one synthetic
// row per filter tuple, which is what makes "reset pagination when a filter changes" structural instead of manual).
// The 807 lines of ported pure rules — `ConcertWhen`, `ConcertScheduleShaping`, the month boards, `LocationErrors` —
// are owner N's, in Wave 5.
//
// Ch 17 §7 opens by saying the plan gives `EntityKind.Concert`, a `ConcertTable` in `Scope`, a 200-line budget "and
// NOTHING ELSE": no fields, no columns, no edges, no place or geo storage, no feed or pagination concept at all. Its
// DATA GAPS table is therefore this file's whole specification, and every declaration below cites a row of it.
//
// TWO THINGS THIS FILE DOES DIFFERENTLY FROM EVERY OTHER KIND, both from ch 17:
//
//  1. THE DATE CARRIES ITS OWN UTC OFFSET. `Date` is unix ms and `OffsetMinutes` is REQUIRED beside it, because the
//     UI prints the PROVIDER'S LOCAL CLOCK — a show at 20:00 in Berlin says 20:00 in Sydney. Storing an instant alone
//     and formatting it in the viewer's zone is the one bug this pair exists to prevent.
//
//  2. THE ACCENT IS THE PROVIDER'S, NOT THE PALETTE'S. `Accent` comes down the concert / lineup branch of the wire
//     (0.2.9 `Models.cs:225-230`) and ch 17 is explicit: it must NOT be routed through `Entities/Palette.cs`. Grading
//     the poster would produce a second, different answer for a colour the provider already chose.

using FluentGpu.Foundation;

namespace Wavee;

// ── field groups ─────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Which of a concert's columns are filled (ch 17 DATA GAPS, "<c>ConcertFields</c> bitmask"). The surface's
/// readiness bar is a whole GROUP at a time: a shelf, a board or the grid appears only when its edge list is complete
/// AND every row it will paint knows <see cref="Tile"/>. A tile with a venue and no date is a regression.</summary>
[Flags]
public enum ConcertFields : uint
{
    None = 0,

    Title = 1 << 0,
    Venue = 1 << 1,
    City = 1 << 2,
    /// <summary>The date AND its offset — the pair is one fact (see the file header).</summary>
    When = 1 << 3,
    Identity = Title | Venue | City | When,

    /// <summary>The poster and the provider's extracted accent. No art is a DESIGNED state (the tinted fallback pane),
    /// not a missing one (ch 17 §7).</summary>
    Art = 1 << 4,
    Tile = Identity | Art,

    Doors = 1 << 5,
    Ages = 1 << 6,
    Status = 1 << 7,
    Region = 1 << 8,
    Country = 1 << 9,
    Coords = 1 << 10,
    Detail = Tile | Doors | Ages | Status | Region | Country | Coords,

    /// <summary>"The detail payload spoke about ticket offers / the lineup / related shows." The LIST is the edge and
    /// its <c>State</c> is the readiness the sections gate on (ch 17 §7); these three bits are the bitmask ch 17's
    /// DATA GAPS row names, and they answer the different question of whether the answer has arrived at all — an
    /// offers section is OMITTED when the answer said "none", which is not the same as "not asked".</summary>
    Offers = 1 << 11,
    Lineup = 1 << 12,
    Related = 1 << 13,

    All = Detail | Offers | Lineup | Related,
}

/// <summary>Concert booleans as bits (P3).</summary>
[Flags]
public enum ConcertFlags : uint
{
    None = 0,
    Festival = 1 << 0,
    /// <summary>Inside the saved place's radius (ch 17 §7's near-you set).</summary>
    NearUser = 1 << 1,
    /// <summary>There is a poster. Distinct from "the <see cref="ConcertFields.Art"/> group is known": known-with-no-art
    /// is the tinted fallback, unknown is the skeleton.</summary>
    HasArt = 1 << 2,

    IdentityMask = Festival,
    ArtMask = HasArt,
    NearMask = NearUser,
}

// ── the table ────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Every concert in one scope, as columns (ch 17 DATA GAPS, "The concert row itself").</summary>
public sealed class ConcertTable : Table
{
    // ── identity + tile ──
    public Column<StringId> Title, Venue, City;
    /// <summary>Unix MILLISECONDS. The tile caption, the month boards and the chronological sort all read it.</summary>
    public Column<long> Date;
    /// <summary>The provider's UTC offset for <see cref="Date"/>, in minutes. REQUIRED — see the file header.</summary>
    public Column<short> OffsetMinutes;
    public Column<StringId> Image;
    /// <summary>The provider's own extracted accent, opaque ARGB (NOT the cover palette — see the file header).</summary>
    public Column<uint> Accent;
    /// <summary><see cref="ConcertFlags"/>.</summary>
    public Column<uint> Flags;

    // ── detail ──
    public Column<long> DoorsOpenAt;
    /// <inheritdoc cref="OffsetMinutes"/>
    public Column<short> DoorsOffsetMinutes;
    public Column<StringId> AgeRestriction;
    /// <summary>The parsed status plus the provider's raw word for it, because the surface prints the provider's
    /// phrasing and branches on the parse (ch 17 DATA GAPS).</summary>
    public Column<byte> Status;
    /// <inheritdoc cref="Status"/>
    public Column<StringId> StatusText;
    public Column<StringId> Region, Country;
    /// <summary>The venue's and the metro area's own ids — the drill trail's route arguments.</summary>
    public Column<StringId> VenuePlace, MetroArea;
    public Column<float> Lat, Lon;

    // ── authority, per column GROUP (D16) ──
    public Column<byte> IdentityAuthority, DetailAuthority;

    public override EntityKind Kind => EntityKind.Concert;

    protected override void GrowColumns(int capacity)
    {
        Title.EnsureCapacity(capacity);
        Venue.EnsureCapacity(capacity);
        City.EnsureCapacity(capacity);
        Date.EnsureCapacity(capacity);
        OffsetMinutes.EnsureCapacity(capacity);
        Image.EnsureCapacity(capacity);
        Accent.EnsureCapacity(capacity);
        Flags.EnsureCapacity(capacity);
        DoorsOpenAt.EnsureCapacity(capacity);
        DoorsOffsetMinutes.EnsureCapacity(capacity);
        AgeRestriction.EnsureCapacity(capacity);
        Status.EnsureCapacity(capacity);
        StatusText.EnsureCapacity(capacity);
        Region.EnsureCapacity(capacity);
        Country.EnsureCapacity(capacity);
        VenuePlace.EnsureCapacity(capacity);
        MetroArea.EnsureCapacity(capacity);
        Lat.EnsureCapacity(capacity);
        Lon.EnsureCapacity(capacity);
        IdentityAuthority.EnsureCapacity(capacity);
        DetailAuthority.EnsureCapacity(capacity);
    }

    /// <summary>Give back every string a concert row owns (defect 1; the REF-COUNTING block on <see cref="Table"/>).
    /// Ten columns — the widest text row in <c>Entities/</c> — and the pair to the <see cref="Table.SetText"/> the
    /// commit writes through. A feed page is the case that makes it matter: the hub replaces a whole filter tuple's
    /// worth of shows every time the user drags the radius, and before this override each pass left ten strings per
    /// dropped show in the interner for the life of the process (doc §4.4).</summary>
    protected override void ReleaseText(int slot)
    {
        ClearText(ref Title, slot);
        ClearText(ref Venue, slot);
        ClearText(ref City, slot);
        ClearText(ref Image, slot);
        ClearText(ref AgeRestriction, slot);
        ClearText(ref StatusText, slot);
        ClearText(ref Region, slot);
        ClearText(ref Country, slot);
        ClearText(ref VenuePlace, slot);
        ClearText(ref MetroArea, slot);
    }
}

// ── places, concepts and the feed subject (ch 17 DATA GAPS) ──────────────────────────────────────────────────────────

/// <summary>Where the user is looking (ch 17 DATA GAPS, "Places / geo"). ACCOUNT state, not catalogue state: it is
/// saved server-side and it survives a market switch, which is why it is a side table and not a ninth entity kind.</summary>
public struct Place
{
    public StringId Id, Name, Region, Country, GeoHash;
    public float Lat, Lon;
    /// <summary><see cref="PlaceFlags"/>.</summary>
    public uint Flags;
}

/// <summary>Place booleans as bits (P3).</summary>
[Flags]
public enum PlaceFlags : uint
{
    None = 0,
    /// <summary>The location was GUESSED, not chosen (ch 17 §7's "is my location inferred"). The Where pill says so.</summary>
    Inferred = 1 << 0,
}

/// <summary>One genre token for a place, with the provider's WEIGHT — the order is the UI's top-3 rule, so the weight
/// is stored rather than the rank (ch 17 DATA GAPS, "Concepts").</summary>
public struct Concept
{
    public StringId Uri, Name;
    public float Weight;
}

/// <summary>ONE feed, identified by its filter tuple (ch 17 DATA GAPS, "Feed sections + pagination" and "The filter
/// tuple → subject identity"). Changing the place, the radius, the date window or the concept set is a DIFFERENT
/// subject with its own edges and its own cursor — which is what makes "reset pagination on a filter change"
/// structural rather than a thing the page has to remember to do.</summary>
public struct ConcertFeed
{
    /// <summary>The tuple's identity, interned: <c>place|radius|from|to|concepts…</c>.</summary>
    public StringId Key;
    /// <summary>The provider's opaque continuation, empty when there is no tail. The tail preloader mounts only while
    /// one exists.</summary>
    public StringId PaginationKey;
    /// <summary>The live count PREVIEW. Explicitly allowed to be newer than the edge list — it is never cached, by the
    /// provider's own contract, and the ticker is a preview by design (ch 17 §7).</summary>
    public int Count;
    /// <summary>Bumps whenever <see cref="Count"/> is answered, so a debounced ticker can tell a repeat from a change.</summary>
    public uint CountVersion;
}

/// <summary>A sparse table keyed by an interned id, for the three non-entity rows above. Get-or-allocate by id is the
/// whole API: places, concepts and feed subjects are all "the same tuple must be the same slot".</summary>
public sealed class KeyedTable<T> : SparseTable<T> where T : unmanaged
{
    readonly Dictionary<StringId, int> _byId = new(UriKeys.Instance);

    /// <summary>The slot for <paramref name="id"/>, allocating a zeroed row when it is new. The caller fills the row's
    /// own id field — this table only owns the mapping.
    ///
    /// <para><b>The MAP owns a reference to its key</b> (defect 1), for the same reason <c>Table.BindId</c> does:
    /// <see cref="UriKeys"/> hashes the RESOLVED text, so a key whose last other owner released it would resolve to
    /// nothing and silently corrupt every bucket after it — not just its own. Hand them back with
    /// <see cref="ReleaseKeys"/> when the scope that owns this table is retired.</para></summary>
    public int Slot(StringId id)
    {
        if (_byId.TryGetValue(id, out int slot)) return slot;
        slot = Alloc();
        Entities.Strings.AddRef(id);
        _byId[id] = slot;
        return slot;
    }

    public bool TryGetSlot(StringId id, out int slot) => _byId.TryGetValue(id, out slot);

    /// <summary>Release every key this table interned against and empty the map — what a retired scope owes the
    /// process-wide interner (defect 1, doc §4.4). The ROWS' own text fields (<see cref="Place.Name"/>,
    /// <see cref="Concept.Name"/>, <see cref="ConcertFeed.PaginationKey"/>…) belong to whoever wrote them, through
    /// <see cref="Entities.RetainText"/>, and are that writer's to give back — this table cannot see them through
    /// <typeparamref name="T"/>.
    /// <para>NOT WIRED YET: <c>Scope.ReleaseText()</c> (Entities.cs) walks the eight entity tables only, so nothing
    /// calls this on a market switch. Reported to that file's owner; until then the keys behave exactly as they did
    /// before — permanent — and never worse.</para></summary>
    public void ReleaseKeys()
    {
        foreach (var key in _byId.Keys) Entities.Strings.Release(key);
        _byId.Clear();
    }
}

public sealed partial class Scope
{
    /// <summary>Ch 17 DATA GAPS. <see cref="SavedPlace"/> is the one the Where pill states.</summary>
    public readonly KeyedTable<Place> Places = new();
    /// <summary>The saved (or inferred) place's slot, 0 = the user has not set one and none was guessed.</summary>
    public int SavedPlace;
    /// <inheritdoc cref="Places"/>
    public readonly KeyedTable<Concept> Concepts = new();
    /// <inheritdoc cref="ConcertFeed"/>
    public readonly KeyedTable<ConcertFeed> ConcertFeeds = new();
}

// ── the handle ───────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>A concert: one <c>int</c>. Partial because <c>Concert.UI.cs</c> and <c>Concert.Page.cs</c> (Wave 5) add
/// the hub, the boards and the detail surface.</summary>
public readonly partial struct Concert(int slot) : IEquatable<Concert>
{
    static ConcertTable T => Entities.Current.Concerts;

    public int Slot { get; } = slot;
    public bool IsValid => Slot > Table.None && Slot < T.Count;
    public uint Version => T.Version[Slot];
    public bool Knows(ConcertFields fields) => (T.Known[Slot] & (uint)fields) == (uint)fields;

    /// <inheritdoc cref="Track.Id"/>
    public EntityId Id => T.Id[Slot];
    /// <summary><inheritdoc cref="Track.Uri" path="/summary"/>
    /// <para>A concert uri is <c>spotify:concert:&lt;hex&gt;</c> — never 22 base62 characters — so a concert row
    /// always takes the TEXT form and keeps its interned uri (doc §6 "not solved"). The win here is the parse that no
    /// longer happens per read, not the bytes.</para></summary>
    public EntityUri Uri => new(T.Id[Slot]);

    // ── identity + tile ──
    public StringId TitleId => T.Title[Slot];
    public StringId VenueId => T.Venue[Slot];
    public StringId CityId => T.City[Slot];
    /// <summary>Unix ms. Format it with <see cref="OffsetMinutes"/>, never in the viewer's zone (file header).</summary>
    public long Date => T.Date[Slot];
    public short OffsetMinutes => T.OffsetMinutes[Slot];
    public StringId ImageId => T.Image[Slot];
    public uint Accent => T.Accent[Slot];
    public bool IsFestival => (T.Flags[Slot] & (uint)ConcertFlags.Festival) != 0;
    public bool IsNearUser => (T.Flags[Slot] & (uint)ConcertFlags.NearUser) != 0;
    public bool HasArt => (T.Flags[Slot] & (uint)ConcertFlags.HasArt) != 0;

    // ── detail ──
    public long DoorsOpenAt => T.DoorsOpenAt[Slot];
    public short DoorsOffsetMinutes => T.DoorsOffsetMinutes[Slot];
    public StringId AgeRestrictionId => T.AgeRestriction[Slot];
    public byte Status => T.Status[Slot];
    public StringId StatusTextId => T.StatusText[Slot];
    public StringId RegionId => T.Region[Slot];
    public StringId CountryId => T.Country[Slot];
    public StringId VenuePlaceId => T.VenuePlace[Slot];
    public StringId MetroAreaId => T.MetroArea[Slot];
    public float Lat => T.Lat[Slot];
    public float Lon => T.Lon[Slot];

    // ── edges ──
    /// <summary>Ticket offers. The section is OMITTED when the answer was empty (ch 17 §7).</summary>
    public ReadOnlySpan<OfferEdge> Offers => Entities.Current.Edges.ConcertOffers.Payload(Slot);
    /// <summary>Artist slots — but a billed act may have NO catalogue artist, which is why the payload carries the
    /// billing text (ch 17 DATA GAPS, "Lineup with no catalog artist").</summary>
    public ReadOnlySpan<int> LineupSlots => Entities.Current.Edges.ConcertLineup.Targets(Slot);
    /// <inheritdoc cref="LineupSlots"/>
    public ReadOnlySpan<LineupEdge> Lineup => Entities.Current.Edges.ConcertLineup.Payload(Slot);
    public ReadOnlySpan<int> RelatedSlots => Entities.Current.Edges.ConcertRelated.Targets(Slot);

    public bool Equals(Concert other) => other.Slot == Slot;
    public override bool Equals(object? obj) => obj is Concert other && other.Slot == Slot;
    public override int GetHashCode() => Slot;
    public static bool operator ==(Concert a, Concert b) => a.Slot == b.Slot;
    public static bool operator !=(Concert a, Concert b) => a.Slot != b.Slot;
}

// ── the edges this kind adds (ch 17 DATA GAPS; ch 08 GAP 16 for the artist schedule) ───────────────────────
//
// AN OFFER, A BILLED ACT AND A FEED SECTION EACH CARRY TEXT. It is declared here and WRITTEN by the edge's replace
// path, so the retain/release pair is `EdgeTable`'s (Edges.cs) and not this file's — a concert row cannot release a
// string an edge slab still points at. Flagged to that owner: a payload StringId nobody AddRefs is as permanent as a
// column one was (defect 1, doc §4.4).──────────

/// <summary>One ticket offer (ch 17 DATA GAPS, "Offers"). Prices are minor units and the currency is the provider's
/// code, so the row formats and nothing here has to parse money.</summary>
public readonly record struct OfferEdge(
    StringId Provider, StringId Url, byte Availability,
    int MinPriceCents, int MaxPriceCents, StringId Currency,
    int SaleStart, int SaleEnd, byte Flags);

/// <summary>One billed act (ch 17 DATA GAPS). <see cref="BillingName"/> exists because the target artist slot may be
/// <see cref="Table.None"/> — a support act with no catalogue page still has a name on the poster.</summary>
public readonly record struct LineupEdge(StringId BillingName, StringId Image, StringId HeaderImage, uint Accent, byte Flags);

/// <summary>One section of a feed answer (ch 17 DATA GAPS, "Feed sections + pagination"): Nearby, Recommended,
/// AllEvents… The section's own key rides along so an append can match sections across pages.</summary>
public readonly record struct FeedSectionEdge(byte Kind, StringId Key);

public sealed partial class Edges
{
    /// <summary>Parent = concert slot.</summary>
    public readonly EdgeTable<OfferEdge> ConcertOffers = new();
    /// <inheritdoc cref="ConcertOffers"/>
    public readonly EdgeTable<LineupEdge> ConcertLineup = new();
    /// <inheritdoc cref="ConcertOffers"/>
    public readonly EdgeTable<NoEdge> ConcertRelated = new();

    /// <summary>Parent = a <see cref="Place"/> slot; targets are <see cref="Concept"/> slots, in the provider's weight
    /// order — the ORDER is the UI's top-3 rule (ch 17 §7).</summary>
    public readonly EdgeTable<NoEdge> PlaceConcepts = new();

    /// <summary>Parent = a <see cref="ConcertFeed"/> subject; targets are concert slots, one run per section. Paging is
    /// <c>ReplacePage</c>, which is exactly the append-with-dedupe 0.2.9 hand-rolled (ch 17 DATA GAPS).</summary>
    public readonly EdgeTable<FeedSectionEdge> FeedSection = new();
    /// <summary>Parent = the same subject; targets are playlist slots (the feed's playlist promos).</summary>
    public readonly EdgeTable<NoEdge> FeedSectionPlaylists = new();

    /// <summary>Parent = artist slot (ch 08 GAP 16 asks for this edge; ch 17 §7's artist schedule is its reader).
    /// The near-you subset is <see cref="ConcertFlags.NearUser"/> per row rather than a second edge, so the boards and
    /// the "near you" count cannot disagree about the same show.</summary>
    public readonly EdgeTable<NoEdge> ArtistConcerts = new();
}

// ── staging and the commit ───────────────────────────────────────────────────────────────────────────────────────────

/// <summary>One decoded concert. Places, concepts and feed subjects are written by the hub page and its host, not by a
/// catalogue decode, so they have no staged form here.</summary>
public struct StagedConcert : IStagedRow
{
    /// <summary>THE row's identity as the wire gave it (<see cref="StagedId"/>): the packed <see cref="EntityId"/> when
    /// it arrived as 16 gid bytes, the uri's UTF-8 in the arena when it arrived as text. One field, one resolve —
    /// <c>s.Slot(table, in row.Id)</c> — and no format-to-arena-then-parse-back round trip.</summary>
    public StagedId Id;
    public TextRef Title, Venue, City, Image, AgeRestriction, StatusText, Region, Country, VenuePlace, MetroArea;
    public long Date, DoorsOpenAt;
    public uint Accent;
    public float Lat, Lon;
    public short OffsetMinutes, DoorsOffsetMinutes;
    public byte Status;
    /// <summary><see cref="ConcertFlags"/>.</summary>
    public uint Flags;
    /// <summary><see cref="ConcertFields"/>: which groups this row speaks for.</summary>
    public uint Known;
    public Authority Authority;

    /// <inheritdoc cref="IStagedRow.Init"/>
    public void Init(in StagedId id, Authority authority, uint known) { Id = id; Authority = authority; Known = known; }
    /// <inheritdoc cref="IStagedRow.Identity"/>
    public readonly StagedId Identity => Id;
}

public sealed partial class Staging
{
    StagedList<StagedConcert>? _concerts;
    public StagedList<StagedConcert> Concerts => _concerts ??= Register(new StagedList<StagedConcert>());
    internal StagedList<StagedConcert>? ConcertsOrNull => _concerts;
}

public static partial class Entities
{
    /// <inheritdoc cref="Ensure(ReadOnlySpan{Track},TrackFields,FetchPriority)"/>
    public static void Ensure(ReadOnlySpan<Concert> rows, ConcertFields wanted, FetchPriority priority = FetchPriority.Visible)
        => Ensure(Current.Concerts, Slots(rows), (uint)wanted, priority);

    /// <inheritdoc cref="Ensure(ReadOnlySpan{Track},TrackFields,FetchPriority)"/>
    public static void Ensure(Concert row, ConcertFields wanted, FetchPriority priority = FetchPriority.Visible)
    {
        Span<int> one = stackalloc int[1];
        one[0] = row.Slot;
        Ensure(Current.Concerts, one, (uint)wanted, priority);
    }

    static partial void CommitConcerts(Staging s)
    {
        var staged = s.ConcertsOrNull;
        if (staged is null || staged.Count == 0) return;

        var t = Current.Concerts;
        var rows = staged.Span;
        t.EnsureCapacity(t.Count + rows.Length);

        for (int i = 0; i < rows.Length; i++)
        {
            ref var row = ref rows[i];
            int slot = s.Slot(t, in row.Id);
            if (slot == Table.None) continue;   // a row with no identity is not a row
            var auth = row.Authority;
            uint known = row.Known;

            if ((known & (uint)ConcertFields.Identity) != 0
                && t.Accepts(slot, (uint)ConcertFields.Identity, auth, in t.IdentityAuthority))
            {
                // `SetText`, never `Title[slot] = …`: AddRef in, release what it overwrites (defect 1). A feed page
                // re-answers the same show on every filter change, so the overwrite path is the busy one here.
                t.SetText(ref t.Title, slot, s.Intern(row.Title));
                t.SetText(ref t.Venue, slot, s.Intern(row.Venue));
                t.SetText(ref t.City, slot, s.Intern(row.City));
                t.Date[slot] = row.Date;
                t.OffsetMinutes[slot] = row.OffsetMinutes;
                t.Flags[slot] = (t.Flags[slot] & ~(uint)ConcertFlags.IdentityMask)
                              | (row.Flags & (uint)ConcertFlags.IdentityMask);
                t.Applied(slot, (uint)ConcertFields.Identity, auth, ref t.IdentityAuthority);
            }
            if ((known & (uint)ConcertFields.Art) != 0
                && t.Accepts(slot, (uint)ConcertFields.Art, auth, in t.IdentityAuthority))
            {
                t.SetText(ref t.Image, slot, s.Intern(row.Image));
                t.Accent[slot] = row.Accent;
                t.Flags[slot] = (t.Flags[slot] & ~(uint)ConcertFlags.ArtMask) | (row.Flags & (uint)ConcertFlags.ArtMask);
                t.Applied(slot, (uint)ConcertFields.Art, auth, ref t.IdentityAuthority);
            }

            uint detail = known & ~(uint)ConcertFields.Tile;
            if (detail == 0) continue;
            if (!t.Accepts(slot, detail, auth, in t.DetailAuthority)) continue;

            if ((detail & (uint)ConcertFields.Doors) != 0)
            {
                t.DoorsOpenAt[slot] = row.DoorsOpenAt;
                t.DoorsOffsetMinutes[slot] = row.DoorsOffsetMinutes;
            }
            if ((detail & (uint)ConcertFields.Ages) != 0)
                t.SetText(ref t.AgeRestriction, slot, s.Intern(row.AgeRestriction));
            if ((detail & (uint)ConcertFields.Status) != 0)
            {
                t.Status[slot] = row.Status;
                t.SetText(ref t.StatusText, slot, s.Intern(row.StatusText));
            }
            if ((detail & (uint)ConcertFields.Region) != 0)
            {
                t.SetText(ref t.Region, slot, s.Intern(row.Region));
                t.SetText(ref t.VenuePlace, slot, s.Intern(row.VenuePlace));
                t.SetText(ref t.MetroArea, slot, s.Intern(row.MetroArea));
            }
            if ((detail & (uint)ConcertFields.Country) != 0)
                t.SetText(ref t.Country, slot, s.Intern(row.Country));
            if ((detail & (uint)ConcertFields.Coords) != 0)
            {
                t.Lat[slot] = row.Lat;
                t.Lon[slot] = row.Lon;
            }
            // The near-you bit rides the detail answer because it is computed against the SAVED PLACE, which the
            // detail request already carries (ch 17 §7's near-you set).
            t.Flags[slot] = (t.Flags[slot] & ~(uint)ConcertFlags.NearMask) | (row.Flags & (uint)ConcertFlags.NearMask);
            t.Applied(slot, detail, auth, ref t.DetailAuthority);
        }
    }
}
