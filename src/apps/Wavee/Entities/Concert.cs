// ── Entities/Concert.cs — CORE (owner A, wave 1; owner N stream N-C, wave 5; plan §2 · the file's full budget is 1,050) ──
//
// THE CONCERT COLUMNS, FLAGS, FIELD GROUPS AND HANDLE, plus the three small tables the surface cannot exist without:
// PLACES (where the user is looking), CONCEPTS (the genre tokens for that place) and the FEED SUBJECT (one synthetic
// row per filter tuple, which is what makes "reset pagination when a filter changes" structural instead of manual).
// Wave 5 (N-C) adds: the routes, the concert-family STAGING (`ConcertRun`, WP-5.N contract §6) and its commit, the
// feed append merge, the value factories the pages read, and the hub's data seam (`ConcertHost`). The 807 lines of
// ported pure rules live in the named partial `Concert.Rules.cs`.
//
// Ch 17 §7 opens by saying the plan gives `EntityKind.Concert`, a `ConcertTable` in `Scope`, a 200-line budget "and
// NOTHING ELSE": no fields, no columns, no edges, no place or geo storage, no feed or pagination concept at all. Its
// DATA GAPS table is therefore this file's whole specification, and every declaration below cites a row of it.
//
// TWO THINGS THIS FILE DOES DIFFERENTLY FROM EVERY OTHER KIND, both from ch 17:
//
//  1. THE DATE CARRIES ITS OWN UTC OFFSET. `Date` is unix ms and `OffsetMinutes` is REQUIRED beside it, because the
//     UI prints the PROVIDER'S LOCAL CLOCK — a show at 20:00 in Berlin says 20:00 in Sydney. Storing an instant alone
//     and formatting it in the viewer's zone is the one bug this pair exists to prevent (`ConcertTime.Local`).
//
//  2. THE ACCENT IS THE PROVIDER'S, NOT THE PALETTE'S. `Accent` comes down the concert / lineup branch of the wire
//     (0.2.9 `Models.cs:225-230`) and ch 17 is explicit: it must NOT be routed through `Entities/Palette.cs`. Grading
//     the poster would produce a second, different answer for a colour the provider already chose.
//
// THE ONE DATA PATH THAT DOES NOT GO THROUGH `Fetch` (reported): a feed subject, the count preview, a place's concepts,
// a city search, a reverse lookup and a save are not rows of a `Table` the planner can plan, so the hub page asks the
// `ConcertHost` seam (installed as `Spotify.Api`'s concert host) and the answer still lands the only legal way — a
// `Staging` committed on the UI thread. The concert detail and the artist schedule DO go through `Fetch`.

using System.Buffers;
using FluentGpu.Foundation;
using FluentGpu.Pal;

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
    /// commit writes through.</summary>
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
/// saved server-side and it survives a market switch, which is why it is a side table and not a ninth entity kind.
/// Keyed by <see cref="ConcertFeedKey.PlaceKey(string?,string?)"/>; every text field is OWNED by the row (retained at
/// commit, released by <see cref="Scope.ReleaseConcertText"/>).</summary>
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
    /// <summary><see cref="Place.Lat"/>/<see cref="Place.Lon"/> were answered (0,0 is a real coordinate).</summary>
    HasCoords = 1 << 1,
}

/// <summary>One genre token for a place, with the provider's WEIGHT — the order is the UI's top-3 rule, so the weight
/// is stored rather than the rank (ch 17 DATA GAPS, "Concepts").</summary>
public struct Concept
{
    public StringId Uri, Name;
    public float Weight;
}

/// <summary>ONE feed, identified by its filter tuple (ch 17 DATA GAPS, "Feed sections + pagination" and "The filter
/// tuple → subject identity", <see cref="ConcertFeedKey"/>). Changing the place, the radius, the date window or the
/// concept set is a DIFFERENT subject with its own edges and its own cursor.</summary>
public struct ConcertFeed
{
    /// <summary>The tuple's identity, interned: <see cref="ConcertFeedKey.For(string,int,ConcertDateRange?,IReadOnlyList{string}?)"/>.</summary>
    public StringId Key;
    /// <summary>The provider's opaque continuation, empty when there is no tail. The tail preloader mounts only while
    /// one exists.</summary>
    public StringId PaginationKey;
    /// <summary>The live count PREVIEW. Explicitly allowed to be newer than the edge list — it is never cached, by the
    /// provider's own contract, and the ticker is a preview by design (ch 17 §7).</summary>
    public int Count;
    /// <summary>The ask number of the count answer <see cref="Count"/> holds (0 = never answered). A count lands only when
    /// its ask is NEWER than this one — a late answer to an older ask never paints.</summary>
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
    /// process-wide interner (defect 1, doc §4.4). The ROWS' own text fields belong to whoever wrote them and are that
    /// writer's to give back (<see cref="Scope.ReleaseConcertText"/> does it for the three concert tables, then calls
    /// this). Wired into scope retirement by the one-line <c>Scope.ReleaseText</c> patch WP-5.N-C reported.</summary>
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
    /// <summary>The artist schedule's own location read (<c>ArtistConcertsPageLocation</c>, ch 17 §6: a SECOND,
    /// independent read from the hub's), 0 = none answered.</summary>
    public int ArtistPagePlace;
    /// <inheritdoc cref="Places"/>
    public readonly KeyedTable<Concept> Concepts = new();
    /// <inheritdoc cref="ConcertFeed"/>
    public readonly KeyedTable<ConcertFeed> ConcertFeeds = new();

    /// <summary>Give back every string the concert side tables and the concert edges OWN, then the three key maps —
    /// the concert half of <see cref="ReleaseText"/> (defect 1). Row text first: the key maps' own references must be
    /// the last to go, because <see cref="UriKeys"/> hashes the resolved text.</summary>
    public void ReleaseConcertText()
    {
        for (int i = 1; i < Places.Count; i++)
        {
            ref var p = ref Places.Row[i];
            Entities.ReleaseText(ref p.Id);
            Entities.ReleaseText(ref p.Name);
            Entities.ReleaseText(ref p.Region);
            Entities.ReleaseText(ref p.Country);
            Entities.ReleaseText(ref p.GeoHash);
        }
        for (int i = 1; i < Concepts.Count; i++)
        {
            ref var c = ref Concepts.Row[i];
            Entities.ReleaseText(ref c.Uri);
            Entities.ReleaseText(ref c.Name);
        }
        for (int i = 1; i < ConcertFeeds.Count; i++)
        {
            ref var f = ref ConcertFeeds.Row[i];
            Entities.ReleaseText(ref f.Key);
            Entities.ReleaseText(ref f.PaginationKey);
        }
        Edges.ReleaseConcertText();
        Places.ReleaseKeys();
        Concepts.ReleaseKeys();
        ConcertFeeds.ReleaseKeys();
    }
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
    /// always takes the TEXT form and keeps its interned uri (doc §6 "not solved").</para></summary>
    public EntityUri Uri => new(T.Id[Slot]);

    // ── identity + tile ──
    public StringId TitleId => T.Title[Slot];
    public StringId VenueId => T.Venue[Slot];
    public StringId CityId => T.City[Slot];
    /// <summary>Unix ms. Format it with <see cref="OffsetMinutes"/>, never in the viewer's zone (file header).</summary>
    public long Date => T.Date[Slot];
    public short OffsetMinutes => T.OffsetMinutes[Slot];
    /// <summary>The PROVIDER'S local clock for <see cref="Date"/> (ch 17 §0.14).</summary>
    public DateTimeOffset LocalDate => ConcertTime.Local(T.Date[Slot], T.OffsetMinutes[Slot]);
    public StringId ImageId => T.Image[Slot];
    public uint Accent => T.Accent[Slot];
    public bool IsFestival => (T.Flags[Slot] & (uint)ConcertFlags.Festival) != 0;
    public bool IsNearUser => (T.Flags[Slot] & (uint)ConcertFlags.NearUser) != 0;
    public bool HasArt => (T.Flags[Slot] & (uint)ConcertFlags.HasArt) != 0;

    /// <summary>The title, else the venue, else the loc fallback — the route arg and the detail headline (ch 17 §6).</summary>
    public string TitleOrVenue
    {
        get
        {
            string title = Entities.Strings.Resolve(T.Title[Slot]);
            if (!string.IsNullOrWhiteSpace(title)) return title;
            string venue = Entities.Strings.Resolve(T.Venue[Slot]);
            return string.IsNullOrWhiteSpace(venue) ? ConcertCopy.Current.FallbackTitle() : venue;
        }
    }

    // ── detail ──
    public long DoorsOpenAt => T.DoorsOpenAt[Slot];
    public short DoorsOffsetMinutes => T.DoorsOffsetMinutes[Slot];
    /// <summary>Doors, in the provider's clock; null when the answer carried none.</summary>
    public DateTimeOffset? DoorsLocal => T.DoorsOpenAt[Slot] == 0 ? null : ConcertTime.Local(T.DoorsOpenAt[Slot], T.DoorsOffsetMinutes[Slot]);
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

    // ── routes (WP-5.N contract §5; parsing is `Shell.Parse`, which rebuilds `spotify:concert:<id>` from the bare id) ──

    /// <summary><c>concert:&lt;bare id&gt;</c>, Arg = title-or-venue. <see cref="Shell.Route.None"/> for a dead handle.</summary>
    public static Shell.Route DetailRoute(Concert c)
        => c.IsValid ? Shell.For(c.Uri, c.TitleOrVenue) : Shell.Route.None;

    /// <summary><c>artist-concerts:&lt;bare id&gt;</c>, Arg = the artist's name.</summary>
    public static Shell.Route ScheduleRoute(Artist a)
    {
        if (!a.IsValid) return Shell.Route.None;
        string name = a.Name;
        return new Shell.Route(Shell.RouteKind.ArtistConcerts, a.Uri,
            string.IsNullOrWhiteSpace(name) ? StringId.Empty : Entities.Strings.Intern(name.Trim()));
    }

    /// <summary><c>concerts</c>.</summary>
    public static readonly Shell.Route HubRoute = new(Shell.RouteKind.Concerts);

    public bool Equals(Concert other) => other.Slot == Slot;
    public override bool Equals(object? obj) => obj is Concert other && other.Slot == Slot;
    public override int GetHashCode() => Slot;
    public static bool operator ==(Concert a, Concert b) => a.Slot == b.Slot;
    public static bool operator !=(Concert a, Concert b) => a.Slot != b.Slot;
}

// ── the edges this kind adds (ch 17 DATA GAPS; ch 08 GAP 16 for the artist schedule) ───────────────────────
//
// AN OFFER, A BILLED ACT AND A FEED SECTION EACH CARRY TEXT, and the edge OWNS it (Edges.cs header rule): the commit
// below AddRefs the incoming payload FIRST, gives back the list it replaces, then lands; `Edges.ReleaseConcertText`
// gives the whole set back with the scope.

/// <summary>One ticket offer (ch 17 DATA GAPS, "Offers"). Prices are minor units and the currency is the provider's
/// code; the sale window is unix SECONDS plus the provider's offset for each end (ch 17 §0.14: "On sale" prints the
/// stored clock). <paramref name="Flags"/> is <see cref="HasMin"/> … <see cref="FirstParty"/>.</summary>
public readonly record struct OfferEdge(
    StringId Provider, StringId Url, byte Availability,
    int MinPriceCents, int MaxPriceCents, StringId Currency,
    int SaleStart, int SaleEnd, byte Flags,
    short SaleStartOffsetMinutes = 0, short SaleEndOffsetMinutes = 0)
{
    public const byte HasMin = 1 << 0, HasMax = 1 << 1, HasSaleStart = 1 << 2, HasSaleEnd = 1 << 3,
                      HasPromoCodes = 1 << 4, FirstParty = 1 << 5;
}

/// <summary>One billed act (ch 17 DATA GAPS). <see cref="BillingName"/> exists because the target artist slot may be
/// <see cref="Table.None"/> — a support act with no catalogue page still has a name on the poster.</summary>
public readonly record struct LineupEdge(StringId BillingName, StringId Image, StringId HeaderImage, uint Accent, byte Flags)
{
    /// <summary>The act carried a canonical <c>spotify:artist:</c> uri — the row navigates (ch 17 §6).</summary>
    public const byte Navigable = 1 << 0;
}

/// <summary>Which shelf a feed section is. The ORDER is 0.2.9's: a <c>ConcertCarousel</c> is Nearby, a
/// <c>LiveEventSection</c> is Recommended, the <c>AllEvents</c> wrapper's sections are the grid.</summary>
public enum ConcertFeedSectionKind : byte { Nearby, Recommended, AllEvents }

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

    /// <summary>Parent = a <see cref="ConcertFeed"/> subject; targets are concert slots, the sections CONTIGUOUS in their
    /// merged order (<see cref="ConcertFeedMerge"/>): the append-with-dedupe 0.2.9 hand-rolled, as one rewrite.</summary>
    public readonly EdgeTable<FeedSectionEdge> FeedSection = new();
    /// <summary>Parent = the same subject; targets are playlist slots (the feed's playlist promos), carrying their
    /// SECTION like <see cref="FeedSection"/> so a promo shelf renders under the section that shipped it (ch 17 §1.1).</summary>
    public readonly EdgeTable<FeedSectionEdge> FeedSectionPlaylists = new();

    /// <summary>Parent = artist slot (ch 08 GAP 16 asks for this edge; ch 17 §7's artist schedule is its reader).
    /// The near-you subset is <see cref="ConcertFlags.NearUser"/> per row rather than a second edge, so the boards and
    /// the "near you" count cannot disagree about the same show.</summary>
    public readonly EdgeTable<NoEdge> ArtistConcerts = new();

    internal void ReleaseOfferText(int parent)
    {
        var rows = ConcertOffers.Payload(parent);
        for (int i = 0; i < rows.Length; i++)
        {
            Entities.Strings.Release(rows[i].Provider);
            Entities.Strings.Release(rows[i].Url);
            Entities.Strings.Release(rows[i].Currency);
        }
    }

    internal void ReleaseLineupText(int parent)
    {
        var rows = ConcertLineup.Payload(parent);
        for (int i = 0; i < rows.Length; i++)
        {
            Entities.Strings.Release(rows[i].BillingName);
            Entities.Strings.Release(rows[i].Image);
            Entities.Strings.Release(rows[i].HeaderImage);
        }
    }

    internal static void ReleaseSectionKeys(EdgeTable<FeedSectionEdge> table, int parent)
    {
        var rows = table.Payload(parent);
        for (int i = 0; i < rows.Length; i++) Entities.Strings.Release(rows[i].Key);
    }

    /// <summary>The concert relations' owned payload text, for the whole scope (called by
    /// <see cref="Scope.ReleaseConcertText"/>).</summary>
    internal void ReleaseConcertText()
    {
        for (int p = 0; p < ConcertOffers.ParentCount; p++) ReleaseOfferText(p);
        for (int p = 0; p < ConcertLineup.ParentCount; p++) ReleaseLineupText(p);
        for (int p = 0; p < FeedSection.ParentCount; p++) ReleaseSectionKeys(FeedSection, p);
        for (int p = 0; p < FeedSectionPlaylists.ParentCount; p++) ReleaseSectionKeys(FeedSectionPlaylists, p);
    }
}

// ── staging: the concert row ─────────────────────────────────────────────────────────────────────────────────────────

/// <summary>One decoded concert.</summary>
public struct StagedConcert : IStagedRow
{
    /// <summary>THE row's identity as the wire gave it (<see cref="StagedId"/>).</summary>
    public StagedId Id;
    public TextRef Title, Venue, City, Image, AgeRestriction, StatusText, Region, Country, VenuePlace, MetroArea;
    public long Date, DoorsOpenAt;
    public uint Accent;
    public float Lat, Lon;
    public short OffsetMinutes, DoorsOffsetMinutes;
    public byte Status;
    /// <summary><see cref="ConcertFlags"/>.</summary>
    public uint Flags;
    /// <summary>Which <see cref="ConcertFlags"/> this row speaks for OUTSIDE its field groups. Only
    /// <see cref="ConcertFlags.NearMask"/> is read: the near-you bit is a fact about the ANSWER (the saved place it
    /// was asked against), so a schedule answer states it for every show it lists — clear for the list, set for the
    /// nearby branch — whatever authority wrote the row's identity.</summary>
    public uint FlagsMask;
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

// ── staging: the concert family's relations (WP-5.N contract §6) ────────────────────────────────────────────────────

/// <summary>Which concert relation a staged run rewrites. The link decides BOTH tables, as <see cref="Relation"/> does.</summary>
public enum ConcertLink : byte { ArtistConcerts, Offers, Lineup, Related, PlaceConcepts, FeedSection, FeedPlaylists }

/// <summary>One staged concert-family edge. A UNION read by its run's <see cref="ConcertLink"/>:
/// <list type="bullet">
/// <item><b>ArtistConcerts / Related / FeedSection</b>: <see cref="Target"/> = the concert. FeedSection also reads
/// <see cref="B0"/> = <see cref="ConcertFeedSectionKind"/> and <see cref="T0"/> = the section key.</item>
/// <item><b>FeedPlaylists</b>: <see cref="Target"/> = the playlist, <see cref="B0"/>/<see cref="T0"/> as FeedSection.</item>
/// <item><b>Offers</b>: no target; <see cref="T0"/> provider, <see cref="T1"/> url, <see cref="T2"/> currency,
/// <see cref="B0"/> availability, <see cref="B1"/> <see cref="OfferEdge"/> flags, <see cref="I0"/>/<see cref="I1"/> min/max
/// cents, <see cref="L0"/>/<see cref="L1"/> sale start/end in unix SECONDS, <see cref="I2"/>/<see cref="I3"/> their offsets.</item>
/// <item><b>Lineup</b>: <see cref="Target"/> = the artist (empty for a billing-only act), <see cref="T0"/> billing name,
/// <see cref="T1"/> avatar, <see cref="T2"/> header, <see cref="U0"/> accent, <see cref="B0"/> <see cref="LineupEdge"/> flags.</item>
/// <item><b>PlaceConcepts</b>: no target; <see cref="T0"/> concept uri, <see cref="T1"/> name, <see cref="U0"/> the weight's
/// float bits.</item>
/// </list></summary>
public struct StagedConcertEdge
{
    public StagedId Target;
    public TextRef T0, T1, T2, T3;
    public long L0, L1;
    public int I0, I1, I2, I3;
    public uint U0;
    public byte B0, B1;
}

/// <summary>One parent's rewritten concert relation. A keyed parent (a place, a feed subject) rides
/// <see cref="ParentKey"/>; an entity parent rides <see cref="Parent"/>.</summary>
public struct StagedConcertRun
{
    public ConcertLink Link;
    public StagedId Parent;
    public TextRef ParentKey;
    public int Start, Length;
    /// <summary>0 = whole rewrite; 1 = feed APPEND (merge into the held list, <see cref="ConcertFeedMerge"/>).</summary>
    public byte Mode;
}

/// <summary>The staged concert-family edges of one batch, with the runs that slice them. Pooled with its
/// <see cref="Staging"/>. The pending stack is <c>StagedEdgeList</c>'s idiom: a list whose members nest another run
/// (a schedule's shows each carry a lineup) PUSHES its members and closes them as one contiguous block.</summary>
public sealed class StagedConcertEdgeList : StagedList
{
    StagedConcertEdge[] _edges = new StagedConcertEdge[32];
    StagedConcertRun[] _runs = new StagedConcertRun[8];
    StagedConcertEdge[] _pending = new StagedConcertEdge[32];
    int _pendingCount;

    public int RunCount;

    public ref StagedConcertEdge Add()
    {
        if (Count == _edges.Length) Array.Resize(ref _edges, _edges.Length * 2);
        ref var e = ref _edges[Count++];
        e = default;
        return ref e;
    }

    public ReadOnlySpan<StagedConcertEdge> Span => _edges.AsSpan(0, Count);
    public ReadOnlySpan<StagedConcertRun> Runs => _runs.AsSpan(0, RunCount);

    /// <summary>Where a nested walk's members start on the pending stack.</summary>
    public int PendingMark => _pendingCount;

    /// <summary>How many members were pushed since <paramref name="mark"/>.</summary>
    public int Pending(int mark) => _pendingCount - mark;

    /// <summary>Push one member of the list being walked.</summary>
    public ref StagedConcertEdge Push()
    {
        if (_pendingCount == _pending.Length) Array.Resize(ref _pending, _pending.Length * 2);
        ref var e = ref _pending[_pendingCount++];
        e = default;
        return ref e;
    }

    /// <summary>Abandon everything pushed since <paramref name="mark"/>.</summary>
    public void Pop(int mark) { if (mark >= 0 && mark <= _pendingCount) _pendingCount = mark; }

    /// <summary>Land what was pushed since <paramref name="mark"/> as ONE whole rewrite of an entity parent.</summary>
    public void Close(ConcertLink link, in StagedId parent, int mark)
    {
        if (mark > _pendingCount) return;
        int start = Flush(mark);
        Run(link, in parent, default, start, Count - start, 0);
        Pop(mark);
    }

    /// <summary>Land what was pushed since <paramref name="mark"/> under a KEYED parent (a place, a feed subject).</summary>
    public void CloseKeyed(ConcertLink link, TextRef parentKey, int mark, bool append)
    {
        if (mark > _pendingCount) return;
        int start = Flush(mark);
        Run(link, default, parentKey, start, Count - start, append ? (byte)1 : (byte)0);
        Pop(mark);
    }

    /// <summary>Land what was pushed since <paramref name="mark"/> as a whole rewrite whose PARENT IS NOT KNOWN YET — the
    /// forward-only reader meets a concert's <c>artists</c> and <c>offers</c> before its <c>uri</c>. Answers the run's
    /// index for <see cref="BindParent"/>; a run never bound has no parent and the commit skips it.</summary>
    public int CloseDeferred(ConcertLink link, int mark)
    {
        if (mark > _pendingCount) return -1;
        int start = Flush(mark);
        if (RunCount == _runs.Length) Array.Resize(ref _runs, _runs.Length * 2);
        ref var run = ref _runs[RunCount];
        run = default;
        run.Link = link;
        run.Start = start;
        run.Length = Count - start;
        Pop(mark);
        return RunCount++;
    }

    /// <summary>Give a deferred run its parent (<see cref="CloseDeferred"/>).</summary>
    public void BindParent(int runIndex, in StagedId parent)
    {
        if ((uint)runIndex < (uint)RunCount) _runs[runIndex].Parent = parent;
    }

    int Flush(int mark)
    {
        int start = Count;
        for (int i = mark; i < _pendingCount; i++) Add() = _pending[i];
        return start;
    }

    internal void Run(ConcertLink link, in StagedId parent, TextRef parentKey, int start, int length, byte mode)
    {
        if ((parent.IsEmpty && parentKey.IsEmpty) || length < 0 || start < 0 || start + length > Count) return;
        if (RunCount == _runs.Length) Array.Resize(ref _runs, _runs.Length * 2);
        ref var run = ref _runs[RunCount++];
        run.Link = link;
        run.Parent = parent;
        run.ParentKey = parentKey;
        run.Start = start;
        run.Length = length;
        run.Mode = mode;
    }

    public override void Clear() { Count = 0; RunCount = 0; _pendingCount = 0; }
}

/// <summary>ONE concert relation being appended, as a cursor (the <see cref="EdgeRun"/> shape). Do not interleave two
/// cursors' <c>Add</c>s — a nested list uses <see cref="StagedConcertEdgeList.Push"/> instead.</summary>
public ref struct ConcertRun
{
    readonly StagedConcertEdgeList _list;
    readonly ConcertLink _link;
    int _start;
    int _count;

    public ConcertRun(Staging s, ConcertLink link)
    {
        _list = s.ConcertEdges;
        _link = link;
        _start = -1;
        _count = 0;
    }

    public readonly int Count => _count;

    /// <summary>Append one payload-only member (an offer, a concept).</summary>
    public ref StagedConcertEdge Add()
    {
        if (_start < 0) _start = _list.Count;
        _count++;
        return ref _list.Add();
    }

    /// <summary>Append one member by identity. The concert ROW itself goes to <c>s.Concerts.RowFor(...)</c>.</summary>
    public ref StagedConcertEdge Add(in StagedId target)
    {
        ref var e = ref Add();
        e.Target = target;
        return ref e;
    }

    /// <summary>Close as a whole-list rewrite of an entity parent — Complete, EVEN IF EMPTY ("this artist has no dates"
    /// is the answer that stops the page asking).</summary>
    public void End(in StagedId parent)
    {
        if (parent.IsEmpty) { Discard(); return; }
        _list.Run(_link, in parent, default, _start < 0 ? _list.Count : _start, _count, 0);
        Reset();
    }

    /// <summary>Close as a whole-list rewrite of a KEYED parent (a place's concepts, a feed's first page).</summary>
    public void EndKeyed(TextRef parentKey)
    {
        if (parentKey.IsEmpty) { Discard(); return; }
        _list.Run(_link, default, parentKey, _start < 0 ? _list.Count : _start, _count, 0);
        Reset();
    }

    /// <summary>Close as a feed APPEND: merged into the held list by <see cref="ConcertFeedMerge"/>.</summary>
    public void AppendKeyed(TextRef parentKey)
    {
        if (parentKey.IsEmpty) { Discard(); return; }
        _list.Run(_link, default, parentKey, _start < 0 ? _list.Count : _start, _count, 1);
        Reset();
    }

    /// <summary>Throw the run's members away.</summary>
    public void Discard()
    {
        if (_start >= 0) _list.Rewind(_start);
        Reset();
    }

    void Reset() { _start = -1; _count = 0; }
}

/// <summary>Why a staged place was answered.</summary>
public enum PlaceRole : byte
{
    /// <summary>A search / reverse-lookup / location-details match. Writes the row, touches no pointer.</summary>
    Match,
    /// <summary>The account's saved (or inferred) place: becomes <see cref="Scope.SavedPlace"/>, writes its flags.</summary>
    Saved,
    /// <summary>The artist schedule's own location read: becomes <see cref="Scope.ArtistPagePlace"/>.</summary>
    ArtistPage,
}

/// <summary>One decoded place. <see cref="Key"/> is <see cref="ConcertFeedKey.PlaceKey(string?,string?)"/> as UTF-8.</summary>
public struct StagedPlace
{
    public TextRef Key, Id, Name, Region, Country, GeoHash;
    public float Lat, Lon;
    /// <summary><see cref="PlaceFlags"/> — written only for <see cref="PlaceRole.Saved"/>.</summary>
    public uint Flags;
    public PlaceRole Role;
}

/// <summary>One decoded feed-subject fact: a page's continuation, a count preview, or both.</summary>
public struct StagedConcertFeed
{
    public const byte PagePart = 1, CountPart = 2;
    public TextRef Key, PaginationKey;
    public int Count;
    /// <summary>The count ask this answers (<see cref="ConcertFeed.CountVersion"/>).</summary>
    public uint CountVersion;
    /// <summary><see cref="PagePart"/> | <see cref="CountPart"/>.</summary>
    public byte Parts;
}

public sealed partial class Staging
{
    StagedConcertEdgeList? _concertEdges;
    StagedList<StagedPlace>? _places;
    StagedList<StagedConcertFeed>? _concertFeeds;

    /// <inheritdoc cref="StagedConcertEdgeList"/>
    public StagedConcertEdgeList ConcertEdges => _concertEdges ??= Register(new StagedConcertEdgeList());
    /// <summary>Begin a concert relation (<see cref="Wavee.ConcertRun"/>).</summary>
    public ConcertRun ConcertRun(ConcertLink link) => new(this, link);
    /// <inheritdoc cref="StagedPlace"/>
    public StagedList<StagedPlace> Places => _places ??= Register(new StagedList<StagedPlace>());
    /// <inheritdoc cref="StagedConcertFeed"/>
    public StagedList<StagedConcertFeed> ConcertFeedRows => _concertFeeds ??= Register(new StagedList<StagedConcertFeed>());

    internal StagedConcertEdgeList? ConcertEdgesOrNull => _concertEdges;
    internal StagedList<StagedPlace>? PlacesOrNull => _places;
    internal StagedList<StagedConcertFeed>? ConcertFeedRowsOrNull => _concertFeeds;
}

// ── the feed append merge (0.2.9 `ConcertFeedPage.Append`, ConcertModels.cs:95-149, as edge page + insert dedupe) ────

/// <summary>THE merge rule a feed page lands through. Sections are (Kind, Key) groups kept CONTIGUOUS; the held
/// sections keep their order, a page's items for a held section are appended to it, a section the held list has never
/// seen is appended at the end only when it kept at least one item, and every concert is deduped by identity — the
/// held list first, then the page in its own order (a page item duplicating a held one is dropped wherever it sits).
/// A first page is the same call with nothing held, which is what dedupes a page against itself. PURE; the scratch is
/// pooled, so a steady scroll allocates nothing after warm-up.</summary>
public static class ConcertFeedMerge
{
    [ThreadStatic] static HashSet<int>? t_seen;

    /// <summary>Merge into <paramref name="outTargets"/>/<paramref name="outPayload"/> (each at least
    /// <c>held + incoming</c> long); answers the merged count.</summary>
    public static int Merge(ReadOnlySpan<int> heldTargets, ReadOnlySpan<FeedSectionEdge> heldPayload,
        ReadOnlySpan<int> incomingTargets, ReadOnlySpan<FeedSectionEdge> incomingPayload,
        Span<int> outTargets, Span<FeedSectionEdge> outPayload)
    {
        int held = Math.Min(heldTargets.Length, heldPayload.Length);
        int incoming = Math.Min(incomingTargets.Length, incomingPayload.Length);
        int total = held + incoming;
        if (total == 0) return 0;

        var seen = t_seen ??= new HashSet<int>();
        seen.Clear();
        bool[] keep = ArrayPool<bool>.Shared.Rent(total);
        long[] groups = ArrayPool<long>.Shared.Rent(total);
        int groupCount = 0, n = 0;
        try
        {
            for (int i = 0; i < held; i++)
            {
                keep[i] = heldTargets[i] > Table.None && seen.Add(heldTargets[i]);
                long g = GroupOf(heldPayload[i]);
                if (Array.IndexOf(groups, g, 0, groupCount) < 0) groups[groupCount++] = g;
            }
            for (int i = 0; i < incoming; i++)
            {
                keep[held + i] = incomingTargets[i] > Table.None && seen.Add(incomingTargets[i]);
                long g = GroupOf(incomingPayload[i]);
                if (Array.IndexOf(groups, g, 0, groupCount) < 0) groups[groupCount++] = g;
            }
            for (int k = 0; k < groupCount && n < outTargets.Length; k++)
            {
                long g = groups[k];
                for (int i = 0; i < held && n < outTargets.Length; i++)
                {
                    if (!keep[i] || GroupOf(heldPayload[i]) != g) continue;
                    outTargets[n] = heldTargets[i];
                    outPayload[n++] = heldPayload[i];
                }
                for (int i = 0; i < incoming && n < outTargets.Length; i++)
                {
                    if (!keep[held + i] || GroupOf(incomingPayload[i]) != g) continue;
                    outTargets[n] = incomingTargets[i];
                    outPayload[n++] = incomingPayload[i];
                }
            }
        }
        finally
        {
            ArrayPool<bool>.Shared.Return(keep);
            ArrayPool<long>.Shared.Return(groups);
        }
        return n;
    }

    /// <summary>A section's identity: (Kind, Key).</summary>
    public static long GroupOf(in FeedSectionEdge e) => ((long)e.Kind << 32) | (uint)e.Key.Value;

    /// <summary>Where the contiguous section starting at <paramref name="start"/> ends (exclusive).</summary>
    public static int SectionEnd(ReadOnlySpan<FeedSectionEdge> payload, int start)
    {
        if ((uint)start >= (uint)payload.Length) return payload.Length;
        long g = GroupOf(payload[start]);
        int end = start + 1;
        while (end < payload.Length && GroupOf(payload[end]) == g) end++;
        return end;
    }
}

// ── the values the pages read (UI thread: they resolve interned text) ────────────────────────────────────────────────

/// <summary>Handles → the rule values of <c>Concert.Rules.cs</c>.</summary>
public static class ConcertShows
{
    /// <summary>A concert row as a <see cref="ConcertShow"/>: its provider-clock date and its lineup names (the billing
    /// text first, the catalogue name when the billing text is blank).</summary>
    public static ConcertShow From(Concert c)
    {
        var strings = Entities.Strings;
        string[]? names = null;
        var lineup = c.Lineup;
        if (lineup.Length > 0)
        {
            var targets = c.LineupSlots;
            names = new string[lineup.Length];
            for (int i = 0; i < lineup.Length; i++)
            {
                string name = strings.Resolve(lineup[i].BillingName);
                if (name.Length == 0 && i < targets.Length && targets[i] > Table.None) name = new Artist(targets[i]).Name;
                names[i] = name;
            }
        }
        string title = strings.Resolve(c.TitleId);
        string region = strings.Resolve(c.RegionId), country = strings.Resolve(c.CountryId);
        return new ConcertShow(
            c.Uri.Text,
            string.IsNullOrWhiteSpace(title) ? null : title,
            strings.Resolve(c.VenueId),
            strings.Resolve(c.CityId),
            c.LocalDate,
            c.IsFestival,
            c.IsNearUser,
            region.Length == 0 ? null : region,
            country.Length == 0 ? null : country,
            names);
    }

    /// <summary>Every target of a concert list as values, in list order (the schedule page's bounded model).</summary>
    public static ConcertShow[] From(ReadOnlySpan<int> concertSlots)
    {
        var shows = new ConcertShow[concertSlots.Length];
        for (int i = 0; i < shows.Length; i++) shows[i] = From(new Concert(concertSlots[i]));
        return shows;
    }
}

/// <summary>The place side table, read as values, plus the saved-place pointer's writes.</summary>
public static class ConcertPlaces
{
    static string? s_savedGeoHash;

    /// <summary>The saved place's geohash as plain text, for a request built on an api thread (C1: a
    /// <c>StringId</c> may not be resolved there). Written on the UI thread whenever the saved place moves.</summary>
    public static string? SavedGeoHash => Volatile.Read(ref s_savedGeoHash);

    /// <summary>A place row as a value; null for slot 0 or a slot this scope never allocated.</summary>
    public static ConcertPlace? From(int placeSlot)
    {
        var places = Entities.Current.Places;
        if (placeSlot <= 0 || placeSlot >= places.Count) return null;
        ref readonly var p = ref places.Row[placeSlot];
        var s = Entities.Strings;
        bool coords = (p.Flags & (uint)PlaceFlags.HasCoords) != 0;
        string region = s.Resolve(p.Region), country = s.Resolve(p.Country), geo = s.Resolve(p.GeoHash);
        return new ConcertPlace(s.Resolve(p.Id), s.Resolve(p.Name),
            region.Length == 0 ? null : region, country.Length == 0 ? null : country, geo.Length == 0 ? null : geo,
            coords ? p.Lat : null, coords ? p.Lon : null);
    }

    /// <summary>Was this place guessed rather than chosen? Null when the slot is none.</summary>
    public static bool? IsInferred(int placeSlot)
    {
        var places = Entities.Current.Places;
        if (placeSlot <= 0 || placeSlot >= places.Count) return null;
        return (places.Row[placeSlot].Flags & (uint)PlaceFlags.Inferred) != 0;
    }

    /// <summary>The place's concepts in the provider's weight order, as values (empty until answered).</summary>
    public static IReadOnlyList<ConcertConcept> ConceptsOf(int placeSlot)
    {
        var scope = Entities.Current;
        var targets = scope.Edges.PlaceConcepts.Targets(placeSlot);
        if (targets.IsEmpty) return Array.Empty<ConcertConcept>();
        var list = new ConcertConcept[targets.Length];
        for (int i = 0; i < targets.Length; i++)
        {
            ref readonly var c = ref scope.Concepts.Row[targets[i]];
            list[i] = new ConcertConcept(Entities.Strings.Resolve(c.Uri), Entities.Strings.Resolve(c.Name), c.Weight);
        }
        return list;
    }

    /// <summary>A save landed: <paramref name="placeSlot"/> is the account's place now, CHOSEN (not inferred). UI thread.</summary>
    public static void MarkSaved(int placeSlot)
    {
        var scope = Entities.Current;
        if (placeSlot <= 0 || placeSlot >= scope.Places.Count) return;
        ref var p = ref scope.Places.Row[placeSlot];
        p.Flags &= ~(uint)PlaceFlags.Inferred;
        scope.SavedPlace = placeSlot;
        NoteSaved(in p);
        scope.Places.MarkDirty();
    }

    internal static void NoteSaved(in Place p)
    {
        string geo = Entities.Strings.Resolve(p.GeoHash);
        Volatile.Write(ref s_savedGeoHash, geo.Length == 0 ? null : geo);
    }

    /// <summary>The feed subject slot for a filter tuple, allocating it when new. UI thread.</summary>
    public static int FeedSlot(string feedKey)
    {
        if (feedKey.Length == 0) return Table.None;
        var feeds = Entities.Current.ConcertFeeds;
        var id = Entities.Strings.Intern(feedKey);
        int slot = feeds.Slot(id);
        Entities.RetainText(ref feeds.Row[slot].Key, id);
        return slot;
    }
}

// ── the hub's data seam (WP-5.N contract §7) ─────────────────────────────────────────────────────────────────────────

/// <summary>What a place search, a reverse lookup or a location-details read came back with: <see cref="Ok"/> false is a
/// failed request (<c>concerts.location.searchFailed</c> / <c>lookupFailed</c>), an Ok answer with no places is "No
/// locations found" / <c>noMatches</c>.</summary>
public readonly record struct ConcertPlaceAnswer(bool Ok, int[] Places)
{
    public static ConcertPlaceAnswer Failed => new(false, []);
    public static ConcertPlaceAnswer Empty => new(true, []);
}

/// <summary>THE HUB'S DATA SEAM: everything a concert page needs that is not a row a <c>Fetch</c> batch can plan (a feed
/// subject, a count preview, a place's concepts, a place search / reverse lookup / save, the two location reads).
/// Every method is called on the UI thread and answers by COMMITTING a staging (the tables publish; the page re-reads)
/// and then invoking its callback on the UI thread. An implementation never throws and never leaves a callback
/// uncalled; outside a Spotify scope it answers EMPTY (the <see cref="Offline"/> answers below).
/// <para><c>Concert.InstallPages</c> assigns <see cref="Current"/> to <c>Spotify.Api</c>'s concert host.</para></summary>
public abstract class ConcertHost
{
    /// <summary>The empty host: nothing to ask, every list answered empty and Complete, every save refused.</summary>
    public static ConcertHost Offline { get; } = new OfflineConcertHost();

    static ConcertHost s_current = Offline;

    /// <summary>The installed host. Never null.</summary>
    public static ConcertHost Current
    {
        get => s_current;
        set => s_current = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>The account's saved place and its inferred bit → <see cref="Scope.SavedPlace"/>.</summary>
    public abstract void ResolveLocation(Action<bool>? done);
    /// <summary>The artist schedule's own location read → <see cref="Scope.ArtistPagePlace"/>.</summary>
    public abstract void ResolveArtistPageLocation(Action<bool>? done);
    /// <summary>A place's genre tokens → <see cref="Edges.PlaceConcepts"/> (asked once per place per scope).</summary>
    public abstract void Concepts(int placeSlot, string? biasConceptUri, Action<bool>? done);
    /// <summary>A feed subject's first page (<paramref name="append"/> false; asked once per subject per scope) or its
    /// next page (<paramref name="append"/> true, <see cref="ConcertFeedQuery.PaginationKey"/> set).</summary>
    public abstract void Feed(int feedSlot, ConcertFeedQuery query, bool append, Action<bool>? done);
    /// <summary>The count preview for a subject; lands only when <paramref name="askVersion"/> is newer than the held one.</summary>
    public abstract void Count(int feedSlot, ConcertFeedQuery query, uint askVersion, Action<bool>? done);
    /// <summary>City search → place rows (<see cref="PlaceRole.Match"/>).</summary>
    public abstract void SearchPlaces(string query, Action<ConcertPlaceAnswer> done);
    /// <summary>Coordinates → place rows (<see cref="PlaceRole.Match"/>).</summary>
    public abstract void ReversePlaces(double latitude, double longitude, Action<ConcertPlaceAnswer> done);
    /// <summary>Store the account's place; true only when the provider said it stored it.</summary>
    public abstract void SavePlace(int placeSlot, Action<bool> done);
    /// <summary>The OS geolocation provider, or null when this build has none (the picker then says
    /// <c>concerts.location.unavailable</c>).</summary>
    public virtual IGeolocationProvider? Geolocation => null;

    // ── the shared empty answers (UI thread) ──

    /// <summary>An empty, Complete feed page for a subject nobody has answered (a vacancy, never a skeleton).</summary>
    public static void AnswerFeedEmpty(int feedSlot)
    {
        var e = Entities.Current.Edges;
        if (feedSlot <= Table.None) return;
        if (e.FeedSection.State(feedSlot) == EdgeState.Unknown)
            e.FeedSection.Replace(feedSlot, ReadOnlySpan<int>.Empty, ReadOnlySpan<FeedSectionEdge>.Empty, EdgeState.Complete, 0);
        if (e.FeedSectionPlaylists.State(feedSlot) == EdgeState.Unknown)
            e.FeedSectionPlaylists.Replace(feedSlot, ReadOnlySpan<int>.Empty, ReadOnlySpan<FeedSectionEdge>.Empty, EdgeState.Complete, 0);
    }

    /// <summary>A zero count for an ask (the ticker eases to 0 rather than holding a stale figure).</summary>
    public static void AnswerCountEmpty(int feedSlot, uint askVersion)
    {
        var feeds = Entities.Current.ConcertFeeds;
        if (feedSlot <= Table.None || feedSlot >= feeds.Count) return;
        ref var f = ref feeds.Row[feedSlot];
        if (askVersion <= f.CountVersion) return;
        f.Count = 0;
        f.CountVersion = askVersion;
        feeds.MarkDirty();
    }

    /// <summary>An empty, Complete concept list for a place (the strip is the lone "All").</summary>
    public static void AnswerConceptsEmpty(int placeSlot)
    {
        var e = Entities.Current.Edges;
        if (placeSlot > Table.None && e.PlaceConcepts.State(placeSlot) == EdgeState.Unknown)
            e.PlaceConcepts.Replace(placeSlot, ReadOnlySpan<int>.Empty, ReadOnlySpan<NoEdge>.Empty, EdgeState.Complete, 0);
    }

    sealed class OfflineConcertHost : ConcertHost
    {
        public override void ResolveLocation(Action<bool>? done) => done?.Invoke(false);
        public override void ResolveArtistPageLocation(Action<bool>? done) => done?.Invoke(false);
        public override void Concepts(int placeSlot, string? biasConceptUri, Action<bool>? done)
        {
            AnswerConceptsEmpty(placeSlot);
            done?.Invoke(true);
        }
        public override void Feed(int feedSlot, ConcertFeedQuery query, bool append, Action<bool>? done)
        {
            if (!append) AnswerFeedEmpty(feedSlot);
            else ClearTail(feedSlot);
            Entities.Publish();
            done?.Invoke(true);
        }
        public override void Count(int feedSlot, ConcertFeedQuery query, uint askVersion, Action<bool>? done)
        {
            AnswerCountEmpty(feedSlot, askVersion);
            Entities.Publish();
            done?.Invoke(true);
        }
        public override void SearchPlaces(string query, Action<ConcertPlaceAnswer> done) => done(ConcertPlaceAnswer.Empty);
        public override void ReversePlaces(double latitude, double longitude, Action<ConcertPlaceAnswer> done) => done(ConcertPlaceAnswer.Empty);
        public override void SavePlace(int placeSlot, Action<bool> done) => done(false);
    }

    /// <summary>An append that answered nothing ends the tail (the preloader unmounts instead of spinning).</summary>
    public static void ClearTail(int feedSlot)
    {
        var feeds = Entities.Current.ConcertFeeds;
        if (feedSlot <= Table.None || feedSlot >= feeds.Count) return;
        ref var f = ref feeds.Row[feedSlot];
        if (f.PaginationKey.IsEmpty) return;
        Entities.ReleaseText(ref f.PaginationKey);
        feeds.MarkDirty();
    }
}

// ── the commit ───────────────────────────────────────────────────────────────────────────────────────────────────────

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
        CommitConcertRows(s);
        // After the rows (a run's targets must exist) and after CommitArtists/CommitPlaylists, which ran first: a lineup
        // artist and a promo playlist resolve to their filled rows.
        CommitPlaces(s);
        CommitFeedRows(s);
        CommitConcertLinks(s);
    }

    static void CommitConcertRows(Staging s)
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

            // The near-you bit is the ANSWER's fact, not a group's (StagedConcert.FlagsMask): written whatever authority
            // holds the identity, and only when this row speaks for it.
            if ((row.FlagsMask & (uint)ConcertFlags.NearMask) != 0)
            {
                uint near = row.Flags & (uint)ConcertFlags.NearMask;
                if ((t.Flags[slot] & (uint)ConcertFlags.NearMask) != near)
                {
                    t.Flags[slot] = (t.Flags[slot] & ~(uint)ConcertFlags.NearMask) | near;
                    t.Bump(slot);
                }
            }

            if ((known & (uint)ConcertFields.Identity) != 0
                && t.Accepts(slot, (uint)ConcertFields.Identity, auth, in t.IdentityAuthority))
            {
                // `SetText`, never `Title[slot] = …`: AddRef in, release what it overwrites (defect 1).
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
            // The detail answer carries the near-you bit against the saved place it was asked with (ch 17 §7).
            t.Flags[slot] = (t.Flags[slot] & ~(uint)ConcertFlags.NearMask) | (row.Flags & (uint)ConcertFlags.NearMask);
            t.Applied(slot, detail, auth, ref t.DetailAuthority);
        }
    }

    static void CommitPlaces(Staging s)
    {
        var staged = s.PlacesOrNull;
        if (staged is null || staged.Count == 0) return;
        var scope = Current;
        var places = scope.Places;
        foreach (ref readonly var row in staged.Span)
        {
            if (row.Key.IsEmpty) continue;
            int slot = places.Slot(s.Intern(row.Key));
            ref var p = ref places.Row[slot];
            RetainText(ref p.Id, s.Intern(row.Id));
            if (!row.Name.IsEmpty) RetainText(ref p.Name, s.Intern(row.Name));
            if (!row.Region.IsEmpty) RetainText(ref p.Region, s.Intern(row.Region));
            if (!row.Country.IsEmpty) RetainText(ref p.Country, s.Intern(row.Country));
            if (!row.GeoHash.IsEmpty) RetainText(ref p.GeoHash, s.Intern(row.GeoHash));
            if ((row.Flags & (uint)PlaceFlags.HasCoords) != 0)
            {
                p.Lat = row.Lat;
                p.Lon = row.Lon;
                p.Flags |= (uint)PlaceFlags.HasCoords;
            }
            switch (row.Role)
            {
                case PlaceRole.Saved:
                    p.Flags = (p.Flags & (uint)PlaceFlags.HasCoords) | (row.Flags & ~(uint)PlaceFlags.HasCoords);
                    scope.SavedPlace = slot;
                    ConcertPlaces.NoteSaved(in p);
                    break;
                case PlaceRole.ArtistPage:
                    scope.ArtistPagePlace = slot;
                    break;
            }
            places.MarkDirty();
        }
    }

    static void CommitFeedRows(Staging s)
    {
        var staged = s.ConcertFeedRowsOrNull;
        if (staged is null || staged.Count == 0) return;
        var feeds = Current.ConcertFeeds;
        foreach (ref readonly var row in staged.Span)
        {
            if (row.Key.IsEmpty) continue;
            var key = s.Intern(row.Key);
            int slot = feeds.Slot(key);
            ref var f = ref feeds.Row[slot];
            RetainText(ref f.Key, key);
            if ((row.Parts & StagedConcertFeed.PagePart) != 0) RetainText(ref f.PaginationKey, s.Intern(row.PaginationKey));
            if ((row.Parts & StagedConcertFeed.CountPart) != 0 && row.CountVersion > f.CountVersion)
            {
                f.Count = row.Count;
                f.CountVersion = row.CountVersion;
            }
            feeds.MarkDirty();
        }
    }

    // UI thread only (C1); grown to the widest run seen, never shrunk (P8).
    static int[] s_linkTargets = new int[64], s_linkHeldTargets = new int[64];
    static OfferEdge[] s_linkOffers = new OfferEdge[16];
    static LineupEdge[] s_linkLineup = new LineupEdge[16];
    static FeedSectionEdge[] s_linkSections = new FeedSectionEdge[64], s_linkHeldSections = new FeedSectionEdge[64],
                             s_linkMerged = new FeedSectionEdge[64];
    static int[] s_linkMergedTargets = new int[64];

    static void GrowLinks(int n)
    {
        if (n <= s_linkTargets.Length) return;
        int size = s_linkTargets.Length;
        while (size < n) size *= 2;
        s_linkTargets = new int[size];
        s_linkHeldTargets = new int[size];
        s_linkMergedTargets = new int[size];
        s_linkOffers = new OfferEdge[size];
        s_linkLineup = new LineupEdge[size];
        s_linkSections = new FeedSectionEdge[size];
        s_linkHeldSections = new FeedSectionEdge[size];
        s_linkMerged = new FeedSectionEdge[size];
    }

    static void CommitConcertLinks(Staging s)
    {
        var list = s.ConcertEdgesOrNull;
        if (list is null || list.RunCount == 0) return;
        var scope = Current;
        var e = scope.Edges;
        var edges = list.Span;
        var runs = list.Runs;

        for (int r = 0; r < runs.Length; r++)
        {
            ref readonly var run = ref runs[r];
            if (run.Start < 0 || run.Length < 0 || run.Start + run.Length > edges.Length) continue;
            var page = edges.Slice(run.Start, run.Length);
            GrowLinks(page.Length);

            switch (run.Link)
            {
                case ConcertLink.ArtistConcerts:
                    {
                        int parent = s.Slot(scope.Artists, in run.Parent);
                        if (parent == Table.None) break;
                        int n = ResolveDistinct(s, page, scope.Concerts);
                        e.ArtistConcerts.Replace(parent, s_linkTargets.AsSpan(0, n), ReadOnlySpan<NoEdge>.Empty, EdgeState.Complete, n);
                        // The tour banner is derived HERE, from the list that just landed (ch 08 GAP 15, contract §6).
                        // `global::` because inside `Entities` the simple name `Artist` is the factory method group.
                        global::Wavee.Artist.DeriveTour(parent, Store.ToUnix(Now) * 1000L);
                        break;
                    }
                case ConcertLink.Related:
                    {
                        int parent = s.Slot(scope.Concerts, in run.Parent);
                        if (parent == Table.None) break;
                        int n = ResolveDistinct(s, page, scope.Concerts);
                        e.ConcertRelated.Replace(parent, s_linkTargets.AsSpan(0, n), ReadOnlySpan<NoEdge>.Empty, EdgeState.Complete, n);
                        break;
                    }
                case ConcertLink.Offers:
                    {
                        int parent = s.Slot(scope.Concerts, in run.Parent);
                        if (parent == Table.None) break;
                        for (int i = 0; i < page.Length; i++)
                        {
                            ref readonly var o = ref page[i];
                            s_linkTargets[i] = Table.None;
                            s_linkOffers[i] = new OfferEdge(Retained(s.Intern(o.T0)), Retained(s.Intern(o.T1)), o.B0,
                                o.I0, o.I1, Retained(s.Intern(o.T2)), (int)o.L0, (int)o.L1, o.B1, (short)o.I2, (short)o.I3);
                        }
                        e.ReleaseOfferText(parent);                      // AFTER the AddRefs: an unchanged provider keeps its id
                        e.ConcertOffers.Replace(parent, s_linkTargets.AsSpan(0, page.Length), s_linkOffers.AsSpan(0, page.Length),
                            EdgeState.Complete, page.Length);
                        break;
                    }
                case ConcertLink.Lineup:
                    {
                        int parent = s.Slot(scope.Concerts, in run.Parent);
                        if (parent == Table.None) break;
                        for (int i = 0; i < page.Length; i++)
                        {
                            ref readonly var a = ref page[i];
                            s_linkTargets[i] = s.Slot(scope.Artists, in a.Target);   // None for a billing-only act
                            s_linkLineup[i] = new LineupEdge(Retained(s.Intern(a.T0)), Retained(s.Intern(a.T1)),
                                Retained(s.Intern(a.T2)), a.U0, a.B0);
                        }
                        e.ReleaseLineupText(parent);
                        e.ConcertLineup.Replace(parent, s_linkTargets.AsSpan(0, page.Length), s_linkLineup.AsSpan(0, page.Length),
                            EdgeState.Complete, page.Length);
                        break;
                    }
                case ConcertLink.PlaceConcepts:
                    {
                        if (run.ParentKey.IsEmpty) break;
                        int parent = scope.Places.Slot(s.Intern(run.ParentKey));
                        var concepts = scope.Concepts;
                        int n = 0;
                        for (int i = 0; i < page.Length; i++)
                        {
                            ref readonly var c = ref page[i];
                            if (c.T0.IsEmpty) continue;
                            var uri = s.Intern(c.T0);
                            int slot = concepts.Slot(uri);
                            if (s_linkTargets.AsSpan(0, n).IndexOf(slot) >= 0) continue;
                            ref var row = ref concepts.Row[slot];
                            RetainText(ref row.Uri, uri);
                            RetainText(ref row.Name, s.Intern(c.T1));
                            row.Weight = BitConverter.UInt32BitsToSingle(c.U0);
                            s_linkTargets[n++] = slot;
                        }
                        concepts.MarkDirty();
                        e.PlaceConcepts.Replace(parent, s_linkTargets.AsSpan(0, n), ReadOnlySpan<NoEdge>.Empty, EdgeState.Complete, n);
                        break;
                    }
                case ConcertLink.FeedSection:
                case ConcertLink.FeedPlaylists:
                    {
                        if (run.ParentKey.IsEmpty) break;
                        int parent = scope.ConcertFeeds.Slot(s.Intern(run.ParentKey));
                        bool playlists = run.Link == ConcertLink.FeedPlaylists;
                        var table = playlists ? e.FeedSectionPlaylists : e.FeedSection;
                        Table targetTable = playlists ? scope.Playlists : scope.Concerts;
                        CommitFeedSection(s, page, table, targetTable, parent, append: run.Mode == 1);
                        break;
                    }
            }
        }
    }

    /// <summary>Resolve a run's targets to slots, dropping empty identities and duplicates, into <c>s_linkTargets</c>.</summary>
    static int ResolveDistinct(Staging s, ReadOnlySpan<StagedConcertEdge> page, Table table)
    {
        int n = 0;
        for (int i = 0; i < page.Length; i++)
        {
            int slot = s.Slot(table, in page[i].Target);
            if (slot == Table.None || s_linkTargets.AsSpan(0, n).IndexOf(slot) >= 0) continue;
            s_linkTargets[n++] = slot;
        }
        return n;
    }

    /// <summary>A feed page: resolve, merge with the held list (<see cref="ConcertFeedMerge"/>; nothing held for a first
    /// page), AddRef every section key of the merged list, give back the held list's, and land it Complete.</summary>
    static void CommitFeedSection(Staging s, ReadOnlySpan<StagedConcertEdge> page, EdgeTable<FeedSectionEdge> table,
        Table targetTable, int parent, bool append)
    {
        int held = append ? table.Count(parent) : 0;
        GrowLinks(held + page.Length);                           // BEFORE filling: a grow swaps the scratch arrays
        int incoming = 0;
        for (int i = 0; i < page.Length; i++)
        {
            int slot = s.Slot(targetTable, in page[i].Target);
            if (slot == Table.None) continue;
            s_linkTargets[incoming] = slot;
            s_linkSections[incoming++] = new FeedSectionEdge(page[i].B0, s.Intern(page[i].T0));
        }

        if (held > 0)
        {
            table.Targets(parent).CopyTo(s_linkHeldTargets);
            table.Payload(parent).CopyTo(s_linkHeldSections);
        }
        int n = ConcertFeedMerge.Merge(s_linkHeldTargets.AsSpan(0, held), s_linkHeldSections.AsSpan(0, held),
            s_linkTargets.AsSpan(0, incoming), s_linkSections.AsSpan(0, incoming),
            s_linkMergedTargets, s_linkMerged);

        for (int i = 0; i < n; i++) Strings.AddRef(s_linkMerged[i].Key);
        Edges.ReleaseSectionKeys(table, parent);
        table.Replace(parent, s_linkMergedTargets.AsSpan(0, n), s_linkMerged.AsSpan(0, n), EdgeState.Complete, n);
    }
}

// ── persistence (Store.cs's per-kind seam) ───────────────────────────────────────────────────────────────────────────

/// <summary>How a concert survives a restart. Persists <see cref="ConcertFields.Identity"/> and every DETAIL
/// sub-group (<see cref="ConcertFields.Art"/>, <see cref="ConcertFields.Doors"/>, <see cref="ConcertFields.Ages"/>,
/// <see cref="ConcertFields.Status"/>, <see cref="ConcertFields.Region"/>, <see cref="ConcertFields.Country"/>,
/// <see cref="ConcertFields.Coords"/>) — unlike Track/Album/Artist's several cold groups, every one of these shares
/// ONE authority column in memory (<see cref="ConcertTable.DetailAuthority"/>) already, so there is no shared-column
/// hazard in binding them independently here.
///
/// <para><b>Deliberately NOT persisted:</b> <see cref="ConcertFields.Offers"/>/<see cref="ConcertFields.Lineup"/>/
/// <see cref="ConcertFields.Related"/> — marker bits with no column of their own (the rows are
/// <c>Edges.ConcertOffers</c>/<c>ConcertLineup</c>/<c>ConcertRelated</c>, which this shape does not persist); the
/// <see cref="ConcertFlags.NearUser"/> bit — it is the ANSWER's fact against a saved place, not the row's, and stays
/// with the network. <c>Lat</c>/<c>Lon</c> are <c>float</c>, which <see cref="StoreType"/> has no storage class for,
/// so they are persisted as their raw bit pattern in an <c>INT</c> column (<see cref="BitConverter.SingleToInt32Bits"/>).</para>
///
/// <para>STORE THREAD (both halves) — see <see cref="ShowShape"/>'s note.</para></summary>
public sealed class ConcertShape : KindShape
{
    static readonly StoreColumn[] Cols =
    [
        new("title", StoreType.Text, StoreColumnFlags.Title),
        new("venue", StoreType.Text),
        new("city", StoreType.Text),
        new("date", StoreType.Int),
        new("offset_minutes", StoreType.Int),
        new("image", StoreType.Text),
        new("accent", StoreType.Int),
        new("doors_open_at", StoreType.Int),
        new("doors_offset_minutes", StoreType.Int),
        new("age_restriction", StoreType.Text),
        new("status", StoreType.Int),
        new("status_text", StoreType.Text),
        new("region", StoreType.Text),
        new("country", StoreType.Text),
        new("venue_place", StoreType.Text),
        new("metro_area", StoreType.Text),
        new("lat_bits", StoreType.Int),
        new("lon_bits", StoreType.Int),
        new("identity_auth", StoreType.Int, StoreColumnFlags.Authority),
        new("detail_auth", StoreType.Int, StoreColumnFlags.Authority),
    ];

    const uint DetailFields = (uint)(ConcertFields.Detail & ~ConcertFields.Tile);
    const uint PersistedFields = (uint)ConcertFields.Tile | DetailFields;

    public override EntityKind Kind => EntityKind.Concert;
    public override string Table => "concert";
    public override ReadOnlySpan<StoreColumn> Columns => Cols;

    public override void Save(Staging s, RowWriter w)
    {
        var rows = s.ConcertsOrNull;
        if (rows is null) return;
        var span = rows.Span;
        for (int i = 0; i < span.Length; i++)
        {
            ref readonly var row = ref span[i];
            uint known = row.Known & PersistedFields;
            bool identity = (known & (uint)ConcertFields.Identity) != 0;
            bool art = (known & (uint)ConcertFields.Art) != 0;
            bool detail = (known & DetailFields) != 0;

            if (identity)
            {
                w.Text(0, row.Title);
                w.Text(1, row.Venue);
                w.Text(2, row.City);
                w.Int(3, row.Date);
                w.Int(4, row.OffsetMinutes);
                w.Int(18, (int)row.Authority);
            }
            else { w.Null(0); w.Null(1); w.Null(2); w.Null(3); w.Null(4); w.Null(18); }

            if (art)
            {
                w.Text(5, row.Image);
                w.Int(6, row.Accent);
            }
            else { w.Null(5); w.Null(6); }

            if ((known & (uint)ConcertFields.Doors) != 0)
            {
                w.Int(7, row.DoorsOpenAt);
                w.Int(8, row.DoorsOffsetMinutes);
            }
            else { w.Null(7); w.Null(8); }

            if ((known & (uint)ConcertFields.Ages) != 0) w.Text(9, row.AgeRestriction); else w.Null(9);

            if ((known & (uint)ConcertFields.Status) != 0)
            {
                w.Int(10, row.Status);
                w.Text(11, row.StatusText);
            }
            else { w.Null(10); w.Null(11); }

            if ((known & (uint)ConcertFields.Region) != 0)
            {
                w.Text(12, row.Region);
                w.Text(14, row.VenuePlace);
                w.Text(15, row.MetroArea);
            }
            else { w.Null(12); w.Null(14); w.Null(15); }

            if ((known & (uint)ConcertFields.Country) != 0) w.Text(13, row.Country); else w.Null(13);

            if ((known & (uint)ConcertFields.Coords) != 0)
            {
                w.Int(16, BitConverter.SingleToInt32Bits(row.Lat));
                w.Int(17, BitConverter.SingleToInt32Bits(row.Lon));
            }
            else { w.Null(16); w.Null(17); }

            if (detail) w.Int(19, (int)row.Authority); else w.Null(19);

            w.Emit(row.Id, known, Entities.Now, Entities.Now);
        }
    }

    public override void Load(RowReader r, Staging into)
    {
        ref var row = ref into.Concerts.Add();
        row.Id = r.Uri;
        row.Title = r.Text(0);
        row.Venue = r.Text(1);
        row.City = r.Text(2);
        row.Date = r.Int(3);
        row.OffsetMinutes = (short)r.Int(4);
        row.Image = r.Text(5);
        row.Accent = (uint)r.Int(6);
        row.DoorsOpenAt = r.Int(7);
        row.DoorsOffsetMinutes = (short)r.Int(8);
        row.AgeRestriction = r.Text(9);
        row.Status = (byte)r.Int(10);
        row.StatusText = r.Text(11);
        row.Region = r.Text(12);
        row.Country = r.Text(13);
        row.VenuePlace = r.Text(14);
        row.MetroArea = r.Text(15);
        row.Lat = BitConverter.Int32BitsToSingle((int)r.Int(16));
        row.Lon = BitConverter.Int32BitsToSingle((int)r.Int(17));
        row.Known = r.Known & PersistedFields;
        row.Authority = (Authority)Math.Max(r.Int(18), r.Int(19));
    }
}
