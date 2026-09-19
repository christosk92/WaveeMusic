// ── Entities/Artist.cs — CORE (owner A, wave 1; owner N stream N-B, wave 5; plan §2 · the file's full budget is 550) ──
//
// THE ARTIST COLUMNS, FLAGS, FIELD GROUPS, HANDLE, THE TWO SPARSE SIDE TABLES, AND THE STAGED FORM OF THE OVERVIEW'S
// PAYLOAD EDGES. The ported pure rules and the commit-time derivations (the popular merge, the payload-edge commit,
// the tour banner) are the named partial `Artist.Rules.cs` (Wave 5, N-B) — this file passed 715 lines with them.
//
// Ch 08 §7 is the reader and its DATA GAPS 2-18 are the specification; ch 02 §7 adds the card's predicates. The plan's
// §4 declares no artist columns at all, so every column below cites the gap that asks for it.
//
// TWO SHAPES THIS FILE ESTABLISHES, both from ch 08:
//
//  1. DERIVED FACTS LIVE ON THE MODEL, COMPUTED AT COMMIT (P11). `BioLead` (strip the HTML, take the first sentence),
//     the three tour-banner strings and the `TourLive` / `Upcoming` bits are all things 0.2.9 recomputed per render.
//     They are columns here, written once by the commit, because the artist hero re-renders on every wash arrival.
//
//  2. RARE, WIDE FACTS GO IN A SPARSE SIDE TABLE, NOT IN TEN MOSTLY-EMPTY COLUMNS. The artist pick is eleven fields
//     that maybe one artist in twenty has; ten `Column<StringId>`s over 5,000 artists would be 200 KB of zeros. A
//     `Column<int> Pick` into `ArtistPickTable` costs 20 KB and one indirection on a surface that mounts once
//     (ch 08 GAP 5, GAP 6). Ch 02 GAP-4 proposes flat `Pin*` columns instead; ch 08 owns the artist page and its
//     answer is the one taken.
//
//  3. A SIDE SLAB'S TEXT IS STILL OWNED TEXT (defect 1, 2026-09-12; docs/plans/wavee/wavee-0.3-entity-identity-
//     memory.md §4.4). A pick holds EIGHT `StringId`s and an upcoming release four, and they are not
//     `Column<StringId>`s, so `Table.SetText` cannot reach them: they use the same pair one level down,
//     `Entities.RetainText` / `Entities.ReleaseText`. The artist row OWNS its pick, so `ArtistTable.ReleaseText`
//     releases the side row too — nothing else may, or two owners would race the same refcount to zero.

using FluentGpu.Foundation;

namespace Wavee;

// ── field groups ─────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Which of an artist's columns are filled. <see cref="Identity"/> is what a card, a credit line and a face
/// pile need; <see cref="Overview"/> is ONE pathfinder answer (<c>queryArtistOverview</c>) and the page renders its
/// stats, bio, pick, upcoming, latest and tour as a UNIT or not at all (ch 08 §7: "the three render as a unit").
/// <see cref="Chart"/> is a readiness bit with no column behind it — see its own note.</summary>
[Flags]
public enum ArtistFields : uint
{
    None = 0,

    Name = 1 << 0,
    Image = 1 << 1,
    Identity = Name | Image,

    /// <summary>The landscape hero backdrop, distinct from the square avatar (ch 08 GAP 4).</summary>
    Header = 1 << 2,
    /// <summary>Monthly listeners, followers, world rank and the verified bit (ch 08 GAP 2).</summary>
    Stats = 1 << 3,
    /// <summary>The HTML biography and its commit-computed first sentence (ch 08 GAP 3).</summary>
    Bio = 1 << 4,
    /// <summary>The artist pick (ch 08 GAP 5). Absent ⇒ the section is not built at all.</summary>
    Pick = 1 << 5,
    /// <summary>The upcoming release (ch 08 GAP 6).</summary>
    PreRelease = 1 << 6,
    /// <summary>The latest release's album slot (ch 08 GAP 7).</summary>
    Latest = 1 << 7,
    /// <summary>The tour banner's three strings and its live bit, DERIVED at commit from the concert list (ch 08
    /// GAP 15). The UI must not recompute this per render.</summary>
    Tour = 1 << 8,
    Overview = Identity | Header | Stats | Bio | Pick | PreRelease | Latest | Tour,

    /// <summary>"The chart transport has ANSWERED" (ch 08 GAP 18). It has no column of its own on purpose: presence
    /// cannot express it — a niche artist's real chart is six rows, so counting rows can never distinguish "we asked
    /// and this is all there is" from "we never asked", which is what made 0.2.9 re-fire the spclient GET forever and
    /// cost it a whole <c>ChartFetchedAt</c> field (<c>Models.cs:100-109</c>). Here it is one bit.</summary>
    Chart = 1 << 9,

    All = Overview | Chart,
}

/// <summary>Artist booleans as bits (P3).</summary>
[Flags]
public enum ArtistFlags : uint
{
    None = 0,
    /// <summary>The blue check (ch 08 §7 — part of the <see cref="ArtistFields.Stats"/> unit).</summary>
    Verified = 1 << 0,
    /// <summary>The tour banner's "on tour now" arm, derived at commit from the concert list (ch 08 GAP 15).</summary>
    TourLive = 1 << 1,
    /// <summary>The pre-release row is still in the future, derived at commit (ch 08 GAP 6) so the banner does not have
    /// to re-evaluate a wall clock on every render.</summary>
    Upcoming = 1 << 2,

    StatsMask = Verified,
    TourMask = TourLive,
    PreReleaseMask = Upcoming,
}

// ── the table ────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Every artist in one scope, as columns. <see cref="Name"/> and <see cref="Image"/> are the hot pair — every
/// credit line, card and face pile reads exactly those two and nothing else.</summary>
public sealed class ArtistTable : Table
{
    // ── identity ──
    public Column<StringId> Name, Image;

    // ── overview (ch 08 GAP 2-4) ──
    /// <summary>The landscape header photograph (<c>visuals.headerImage</c>), NOT the avatar.</summary>
    public Column<StringId> Header;
    /// <summary>The provider's own extracted accent for the header photograph. Concert and lineup payloads ship one
    /// too, and ch 17 is explicit that it must NOT be routed through the cover-palette pipeline: the provider gives it,
    /// so re-grading the image would produce a second, different answer for the same picture.</summary>
    public Column<uint> HeaderAccent;
    /// <summary>The biography as HTML (<c>profile.biography.text</c>).</summary>
    public Column<StringId> Bio;
    /// <summary>The stripped first sentence, computed ONCE at commit (ch 08 GAP 3, P11): the hero states it on every
    /// render and 0.2.9 re-stripped the HTML each time.</summary>
    public Column<StringId> BioLead;
    public Column<uint> Monthly, Followers;
    public Column<ushort> WorldRank;
    /// <summary><see cref="ArtistFlags"/>.</summary>
    public Column<uint> Flags;

    /// <summary>The tour banner, derived at commit from the concert list (ch 08 GAP 15). Three strings and a bit, not a
    /// re-derivation per frame.</summary>
    public Column<StringId> TourEyebrow, TourHeadline, TourSubline;

    /// <summary>Row in <see cref="ArtistPickTable"/>, 0 = this artist has no pick (ch 08 GAP 5).</summary>
    public Column<int> Pick;
    /// <summary>Row in <see cref="ArtistPreReleaseTable"/>, 0 = nothing upcoming (ch 08 GAP 6).</summary>
    public Column<int> PreRelease;
    /// <summary>The latest release's ALBUM slot (ch 08 GAP 7). 0 = unknown.</summary>
    public Column<int> Latest;

    // ── authority, per column GROUP (D16) ──
    /// <summary>Identity is written by everything (a credit line, a card, a search hit); the overview is written by one
    /// request. Two columns, because a thin name-only stub must never look as authoritative as the overview — that is
    /// the mistake ch 07 G9 records, where a name-only stub satisfied Identity and left the pile on initials forever.</summary>
    public Column<byte> IdentityAuthority, OverviewAuthority;

    public override EntityKind Kind => EntityKind.Artist;

    protected override void GrowColumns(int capacity)
    {
        Name.EnsureCapacity(capacity);
        Image.EnsureCapacity(capacity);
        Header.EnsureCapacity(capacity);
        HeaderAccent.EnsureCapacity(capacity);
        Bio.EnsureCapacity(capacity);
        BioLead.EnsureCapacity(capacity);
        Monthly.EnsureCapacity(capacity);
        Followers.EnsureCapacity(capacity);
        WorldRank.EnsureCapacity(capacity);
        Flags.EnsureCapacity(capacity);
        TourEyebrow.EnsureCapacity(capacity);
        TourHeadline.EnsureCapacity(capacity);
        TourSubline.EnsureCapacity(capacity);
        Pick.EnsureCapacity(capacity);
        PreRelease.EnsureCapacity(capacity);
        Latest.EnsureCapacity(capacity);
        IdentityAuthority.EnsureCapacity(capacity);
        OverviewAuthority.EnsureCapacity(capacity);
    }

    /// <summary>Give back every string an artist row owns (defect 1; the REF-COUNTING block on <see cref="Table"/>):
    /// the eight text columns, AND the two sparse side rows this artist is the sole owner of — a pick is eight more
    /// strings and an upcoming release four, and nothing else in the graph points at them (file header note 3).
    ///
    /// <para>The side rows are reached through <see cref="Entities.Current"/> because they live on <see cref="Scope"/>
    /// and not on this table. That read is guarded by the pointer columns, which are 0 for every row of a table built
    /// outside a scope (a unit-test probe, a store fixture) — so a standalone <c>ArtistTable</c> never touches the
    /// current scope, and a table that IS the current scope's always finds its own side tables.</para></summary>
    protected override void ReleaseText(int slot)
    {
        ClearText(ref Name, slot);
        ClearText(ref Image, slot);
        ClearText(ref Header, slot);
        ClearText(ref Bio, slot);
        ClearText(ref BioLead, slot);
        ClearText(ref TourEyebrow, slot);
        ClearText(ref TourHeadline, slot);
        ClearText(ref TourSubline, slot);

        int pick = Pick[slot];
        if (pick > 0)
        {
            Entities.Current.ArtistPicks.ReleaseRow(pick);
            Pick[slot] = 0;                  // the pointer goes with the text: a recycled slot must not read a dead row
        }
        int upcoming = PreRelease[slot];
        if (upcoming > 0)
        {
            Entities.Current.ArtistPreReleases.ReleaseRow(upcoming);
            PreRelease[slot] = 0;
        }
    }
}

// ── the two sparse side tables (ch 08 GAP 5, GAP 6) ──────────────────────────────────────────────────────────────────

/// <summary>The artist's pinned item: eleven authored fields, one artist in twenty (ch 08 GAP 5). Only the
/// ARTIST-AUTHORED parts are here — the target's own title, subtitle and cover come from the target handle, which is
/// why <see cref="ItemUri"/> is a slot-bearing uri and not a copy of its metadata.</summary>
public struct ArtistPick
{
    public StringId Eyebrow, Title, Subtitle, Comment, Cover, Uri, ItemUri, Background;
    /// <summary>The pinned item's <see cref="EntityKind"/>, so the row routes without re-parsing its uri.</summary>
    public byte ItemKind;
    public int ReleaseAt;
}

/// <summary>The artist's upcoming release (ch 08 GAP 6): sparse for the same reason as the pick.</summary>
public struct ArtistPreRelease
{
    public StringId Uri, Name, Cover, Type;
    public int ReleaseAt;
}

/// <summary>A sparse side table: a bump-allocated slab of rows that entity rows point AT (the <see cref="MerchTable"/>
/// pattern, Edges.cs §4). Not a <see cref="Table"/> — a pick has no uri, no known bits, no authority and no fetch
/// ladder of its own; it arrives whole with its artist's overview and is replaced whole by the next one. Slot 0 is
/// "none", as everywhere else. <see cref="Publishable"/> so a page bound to picks re-renders on the same publication
/// as everything else in the drain (C3).</summary>
public class SparseTable<T> : Publishable where T : unmanaged
{
    public Column<T> Row;
    public int Count = 1;

    /// <summary>Slot 0 exists from construction, not from the first <see cref="Alloc"/>. The whole point of the
    /// "none" row is that a handle reads it WITHOUT a branch (<c>Row[T.Pick[Slot]]</c> with a 0 there), so a scope
    /// that has never seen a pick must still have the blank row to read.</summary>
    public SparseTable() => Row.EnsureCapacity(1);

    /// <summary>One row, zeroed. Returns its slot — the value an entity column points at.</summary>
    public int Alloc()
    {
        int slot = Count++;
        Row.EnsureCapacity(Count);
        Row[slot] = default;
        MarkDirty();
        return slot;
    }
}

/// <inheritdoc cref="ArtistPick"/>
public sealed class ArtistPickTable : SparseTable<ArtistPick>
{
    /// <summary>Hand back the eight strings one pick row owns and zero it (defect 1, file header note 3). Called ONLY
    /// by <c>ArtistTable.ReleaseText</c>, for the artist whose <c>Pick</c> column points here — a pick has no
    /// second owner, and a second release would take a live id's refcount to zero under whoever still holds it.
    /// <para>The slot itself is not recycled: <see cref="SparseTable{T}"/> is a bump allocator by design (a pick is
    /// replaced whole by the next answer, not freed), so this reclaims the TEXT, which is all of the bytes.</para></summary>
    public void ReleaseRow(int slot)
    {
        if (slot <= 0 || slot >= Count) return;
        ref var p = ref Row[slot];
        Entities.ReleaseText(ref p.Eyebrow);
        Entities.ReleaseText(ref p.Title);
        Entities.ReleaseText(ref p.Subtitle);
        Entities.ReleaseText(ref p.Comment);
        Entities.ReleaseText(ref p.Cover);
        Entities.ReleaseText(ref p.Uri);
        Entities.ReleaseText(ref p.ItemUri);
        Entities.ReleaseText(ref p.Background);
        p = default;
    }
}

/// <inheritdoc cref="ArtistPreRelease"/>
public sealed class ArtistPreReleaseTable : SparseTable<ArtistPreRelease>
{
    /// <inheritdoc cref="ArtistPickTable.ReleaseRow"/>
    public void ReleaseRow(int slot)
    {
        if (slot <= 0 || slot >= Count) return;
        ref var u = ref Row[slot];
        Entities.ReleaseText(ref u.Uri);
        Entities.ReleaseText(ref u.Name);
        Entities.ReleaseText(ref u.Cover);
        Entities.ReleaseText(ref u.Type);
        u = default;
    }
}

public sealed partial class Scope
{
    /// <summary>Ch 08 GAP 5. Scope-owned, because a pick is catalogue state and a market switch can change it.</summary>
    public readonly ArtistPickTable ArtistPicks = new();
    /// <inheritdoc cref="ArtistPicks"/>
    public readonly ArtistPreReleaseTable ArtistPreReleases = new();
}

// ── the handle ───────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>An artist: one <c>int</c>. Partial because <c>Artist.UI.cs</c>, <c>Artist.Page.cs</c> and
/// <c>Artist.Discography.cs</c> (Wave 5) add the page's readers.</summary>
public readonly partial struct Artist(int slot) : IEquatable<Artist>
{
    static ArtistTable T => Entities.Current.Artists;

    public int Slot { get; } = slot;
    public bool IsValid => Slot > Table.None && Slot < T.Count;
    public uint Version => T.Version[Slot];
    public bool Knows(ArtistFields fields) => (T.Known[Slot] & (uint)fields) == (uint)fields;

    /// <inheritdoc cref="Track.Id"/>
    public EntityId Id => T.Id[Slot];
    /// <inheritdoc cref="Track.Uri"/>
    public EntityUri Uri => new(T.Id[Slot]);

    // ── identity ──
    public StringId NameId => T.Name[Slot];
    public string Name => Entities.Strings.Resolve(T.Name[Slot]);
    public StringId ImageId => T.Image[Slot];

    // ── overview ──
    public StringId HeaderId => T.Header[Slot];
    /// <summary>The hero photograph, falling back to the avatar (ch 08 §7's first row).</summary>
    public StringId HeroImageId => T.Header[Slot].IsEmpty ? T.Image[Slot] : T.Header[Slot];
    /// <summary>The ONE image every artwork-derived colour on the artist page keys on — <c>Header ?? Image</c>
    /// (ch 08 §4, 0.2.9 <c>ArtistPage.PaletteImageUrl</c>). The same answer as <see cref="HeroImageId"/>, named for the
    /// palette so the page reads the rule, not a coincidence.</summary>
    public StringId PaletteImageId => HeroImageId;
    public uint HeaderAccent => T.HeaderAccent[Slot];
    public StringId BioId => T.Bio[Slot];
    public StringId BioLeadId => T.BioLead[Slot];
    public uint MonthlyListeners => T.Monthly[Slot];
    public uint Followers => T.Followers[Slot];
    public ushort WorldRank => T.WorldRank[Slot];
    public bool IsVerified => (T.Flags[Slot] & (uint)ArtistFlags.Verified) != 0;
    public bool IsTourLive => (T.Flags[Slot] & (uint)ArtistFlags.TourLive) != 0;
    public bool HasUpcoming => (T.Flags[Slot] & (uint)ArtistFlags.Upcoming) != 0;
    public StringId TourEyebrowId => T.TourEyebrow[Slot];
    public StringId TourHeadlineId => T.TourHeadline[Slot];
    public StringId TourSublineId => T.TourSubline[Slot];
    public Album Latest => new(T.Latest[Slot]);

    /// <summary>The pick's row, or a zeroed one when there is none (slot 0 is permanently blank, so the caller may read
    /// it without a branch and gate on <see cref="HasPick"/> for the SECTION).</summary>
    public ref ArtistPick Pick => ref Entities.Current.ArtistPicks.Row[T.Pick[Slot]];
    public bool HasPick => T.Pick[Slot] > 0;
    /// <inheritdoc cref="Pick"/>
    public ref ArtistPreRelease PreRelease => ref Entities.Current.ArtistPreReleases.Row[T.PreRelease[Slot]];
    public bool HasPreRelease => T.PreRelease[Slot] > 0;

    // ── edges ──
    /// <summary>The chart (ch 08 GAP 18). Readiness is <c>Knows(Chart)</c> AND the edge complete AND every target at
    /// <c>TrackFields.Row</c> — the owner's "no rows without play counts" gate.</summary>
    public ReadOnlySpan<int> PopularSlots => Entities.Current.Edges.ArtistPopular.Targets(Slot);
    /// <summary>The three discography facets, each independently paged with its own <c>Total</c> (ch 08 GAP 8).</summary>
    public ReadOnlySpan<int> AlbumSlots => Entities.Current.Edges.ArtistAlbums.Targets(Slot);
    /// <inheritdoc cref="AlbumSlots"/>
    public ReadOnlySpan<int> SingleSlots => Entities.Current.Edges.ArtistSingles.Targets(Slot);
    /// <inheritdoc cref="AlbumSlots"/>
    public ReadOnlySpan<int> CompilationSlots => Entities.Current.Edges.ArtistCompilations.Targets(Slot);
    public ReadOnlySpan<int> AppearsOnSlots => Entities.Current.Edges.ArtistAppearsOn.Targets(Slot);
    public ReadOnlySpan<int> RelatedSlots => Entities.Current.Edges.ArtistRelated.Targets(Slot);
    /// <summary>Payload IS the image id; targets unused (ch 08 GAP 9, the <c>TrackTags</c> precedent).</summary>
    public ReadOnlySpan<StringId> GallerySlots => Entities.Current.Edges.ArtistGallery.Payload(Slot);
    /// <summary>Targets are playlist slots; the payload is each one's subtitle (ch 08 GAP 10).</summary>
    public ReadOnlySpan<int> PlaylistSlots => Entities.Current.Edges.ArtistPlaylists.Targets(Slot);
    /// <summary>Targets are TRACK slots (the video counterpart); the payload carries the 16:9 thumb and duration
    /// (ch 08 GAP 11).</summary>
    public ReadOnlySpan<int> VideoSlots => Entities.Current.Edges.ArtistVideos.Targets(Slot);
    /// <summary>Targets are <see cref="MerchTable"/> rows (ch 08 GAP 12) — read one with <see cref="Album.MerchAt"/>.</summary>
    public ReadOnlySpan<int> MerchSlots => Entities.Current.Edges.ArtistMerch.Targets(Slot);
    /// <summary>Targets unused; the payload IS the city (ch 08 GAP 13).</summary>
    public ReadOnlySpan<CityEdge> TopCities => Entities.Current.Edges.ArtistCities.Payload(Slot);
    /// <summary>Targets unused; the payload IS the link (ch 08 GAP 14).</summary>
    public ReadOnlySpan<LinkEdge> Links => Entities.Current.Edges.ArtistLinks.Payload(Slot);
    /// <summary>The artist's upcoming shows, concert slots (ch 08 GAP 16; the edge is Concert.cs's).</summary>
    public ReadOnlySpan<int> ConcertSlots => Entities.Current.Edges.ArtistConcerts.Targets(Slot);
    /// <summary>The music videos' thumb + duration, parallel to <see cref="VideoSlots"/>.</summary>
    public ReadOnlySpan<VideoEdge> VideoPayload => Entities.Current.Edges.ArtistVideos.Payload(Slot);
    /// <summary>Each playlist's subtitle, parallel to <see cref="PlaylistSlots"/>.</summary>
    public ReadOnlySpan<StringId> PlaylistSubtitleIds => Entities.Current.Edges.ArtistPlaylists.Payload(Slot);

    public bool Equals(Artist other) => other.Slot == Slot;
    public override bool Equals(object? obj) => obj is Artist other && other.Slot == Slot;
    public override int GetHashCode() => Slot;
    public static bool operator ==(Artist a, Artist b) => a.Slot == b.Slot;
    public static bool operator !=(Artist a, Artist b) => a.Slot != b.Slot;
}

// ── the edges this kind adds (ch 08 GAP 8-14) ────────────────────────────────────────────────────────
//
// FIVE OF THESE PAYLOADS CARRY TEXT (the gallery's image id, a playlist's subtitle, a video's thumb, a city, a link).
// It is declared here and WRITTEN by the edge's replace path, so the retain/release pair belongs to `EdgeTable`
// (Edges.cs), not to this file — an artist row cannot release a string an edge slab still points at. Flagged to that
// owner: a payload StringId nobody AddRefs is as permanent as a column one was (defect 1, doc §4.4).────────────────

/// <summary>A music video off the artist page's 16:9 shelf (ch 08 GAP 11). The TARGET is the counterpart track, so the
/// card's title comes from the track row and only the thumb and duration are the video's own.</summary>
public readonly record struct VideoEdge(StringId Thumb, int DurationMs);

/// <summary>One row of the "where people listen" list (ch 08 GAP 13). Targets are unused — a city is not an entity.</summary>
public readonly record struct CityEdge(StringId City, StringId Country, uint Listeners);

/// <summary>One external profile link (ch 08 GAP 14). <c>Kind</c> selects the glyph.</summary>
public readonly record struct LinkEdge(StringId Name, StringId Url, byte Kind);

public sealed partial class Edges
{
    /// <summary>The three discography facets (ch 08 GAP 8). Three edges and not one <c>ArtistReleases</c> +
    /// <c>DiscographyEdge(Kind)</c>, because the wire pages them INDEPENDENTLY and reports three separate totals: one
    /// edge cannot carry three <c>State</c>s and three <c>Total</c>s, and the "See all N" gate reads the true total.
    /// The plan's <see cref="ArtistReleases"/> stays for the album page's "more by" fallback.</summary>
    public readonly EdgeTable<NoEdge> ArtistAlbums = new(), ArtistSingles = new(), ArtistCompilations = new();
    /// <summary>Payload IS the image id, targets unused (ch 08 GAP 9).</summary>
    public readonly EdgeTable<StringId> ArtistGallery = new();
    /// <summary>Targets are playlist slots, payload is the subtitle (ch 08 GAP 10).</summary>
    public readonly EdgeTable<StringId> ArtistPlaylists = new();
    /// <summary>Targets are track slots (ch 08 GAP 11).</summary>
    public readonly EdgeTable<VideoEdge> ArtistVideos = new();
    /// <summary>Targets are <see cref="MerchTable"/> rows (ch 08 GAP 12).</summary>
    public readonly EdgeTable<NoEdge> ArtistMerch = new();
    /// <summary>Payload-only edges: targets unused (ch 08 GAP 13, GAP 14).</summary>
    public readonly EdgeTable<CityEdge> ArtistCities = new();
    /// <inheritdoc cref="ArtistCities"/>
    public readonly EdgeTable<LinkEdge> ArtistLinks = new();
}

// ── staging and the commit ───────────────────────────────────────────────────────────────────────────────────────────

/// <summary>One decoded artist. The pick and the pre-release ride along as flat text (a decode cannot allocate a side
/// row), and the commit files them into their sparse tables.</summary>
public struct StagedArtist : IStagedRow
{
    /// <summary>THE row's identity as the wire gave it (<see cref="StagedId"/>): the packed <see cref="EntityId"/> when
    /// it arrived as 16 gid bytes, the uri's UTF-8 in the arena when it arrived as text. One field, one resolve —
    /// <c>s.Slot(table, in row.Id)</c> — and no format-to-arena-then-parse-back round trip.</summary>
    public StagedId Id;
    public TextRef Name, Image, Header, Bio, BioLead;
    public TextRef TourEyebrow, TourHeadline, TourSubline;
    public StagedId LatestUri;
    public TextRef PickEyebrow, PickTitle, PickSubtitle, PickComment, PickCover, PickUri, PickItemUri, PickBackground;
    public TextRef UpcomingUri, UpcomingName, UpcomingCover, UpcomingType;
    public uint Monthly, Followers, HeaderAccent;
    public ushort WorldRank;
    public int PickReleaseAt, UpcomingReleaseAt;
    public byte PickItemKind;
    /// <summary><see cref="ArtistFlags"/>.</summary>
    public uint Flags;
    /// <summary><see cref="ArtistFields"/>: which groups this row speaks for.</summary>
    public uint Known;
    public Authority Authority;

    /// <inheritdoc cref="IStagedRow.Init"/>
    public void Init(in StagedId id, Authority authority, uint known) { Id = id; Authority = authority; Known = known; }
    /// <inheritdoc cref="IStagedRow.Identity"/>
    public readonly StagedId Identity => Id;
}

public sealed partial class Staging
{
    StagedList<StagedArtist>? _artists;
    public StagedList<StagedArtist> Artists => _artists ??= Register(new StagedList<StagedArtist>());
    internal StagedList<StagedArtist>? ArtistsOrNull => _artists;
}

public static partial class Entities
{
    /// <inheritdoc cref="Ensure(ReadOnlySpan{Track},TrackFields,FetchPriority)"/>
    public static void Ensure(ReadOnlySpan<Artist> rows, ArtistFields wanted, FetchPriority priority = FetchPriority.Visible)
        => Ensure(Current.Artists, Slots(rows), (uint)wanted, priority);

    /// <inheritdoc cref="Ensure(ReadOnlySpan{Track},TrackFields,FetchPriority)"/>
    public static void Ensure(Artist row, ArtistFields wanted, FetchPriority priority = FetchPriority.Visible)
    {
        Span<int> one = stackalloc int[1];
        one[0] = row.Slot;
        Ensure(Current.Artists, one, (uint)wanted, priority);
    }

    static partial void CommitArtists(Staging s)
    {
        // The chart merge reads `Knows(Chart)` as it stood BEFORE this batch, so it runs ahead of the row loop that may
        // apply the bit (Artist.Rules.cs). The payload edges ride after the rows, as every edge does.
        CommitPopular(s);
        CommitArtistRows(s);
        CommitArtistExtras(s);
    }

    static void CommitArtistRows(Staging s)
    {
        var staged = s.ArtistsOrNull;
        if (staged is null || staged.Count == 0) return;

        var t = Current.Artists;
        var rows = staged.Span;
        t.EnsureCapacity(t.Count + rows.Length);

        for (int i = 0; i < rows.Length; i++)
        {
            ref var row = ref rows[i];
            int slot = s.Slot(t, in row.Id);
            if (slot == Table.None) continue;   // a row with no identity is not a row
            var auth = row.Authority;
            uint known = row.Known;

            if ((known & (uint)ArtistFields.Identity) != 0
                && t.Accepts(slot, (uint)ArtistFields.Identity, auth, in t.IdentityAuthority))
            {
                // `SetText` and never `Name[slot] = …`: the write AddRefs the incoming id and releases the one it
                // overwrites, which is what stops a credit line, a card and a search hit re-answering the same artist
                // from leaving three names in the interner (defect 1).
                t.SetText(ref t.Name, slot, s.Intern(row.Name));
                // A name-only stub must not blank a portrait the overview already gave us (ch 07 G9).
                if (!row.Image.IsEmpty) t.SetText(ref t.Image, slot, s.Intern(row.Image));
                // `known & Identity`, NOT the bare `ArtistFields.Identity` constant (the "Top artists" chip bug,
                // 2026-09-15): a billed-artist mention (`Spotify.Decode.cs`'s `ThinArtist`, a track/album's credit
                // run) stages Name ALONE — it never reads a portrait off the wire at all — so sealing the whole
                // group here would mark `Image` known with nothing behind it. `Fetch.NeedOf = wanted & ~Known &
                // ~Asked` (Fetch.cs) then sees no hole to fill and the photo is never asked for: the chip rail
                // falls back to initials forever, permanently, for every artist whose only mention so far was a
                // credit line. Same fix as `CommitTracks`/`CommitAlbums` (Track.cs, Album.cs) for the identical
                // class of bug.
                t.Applied(slot, known & (uint)ArtistFields.Identity, auth, ref t.IdentityAuthority);
            }

            uint overview = known & ~(uint)ArtistFields.Identity;
            if (overview == 0) continue;

            if ((overview & (uint)ArtistFields.Header) != 0
                && t.Accepts(slot, (uint)ArtistFields.Header, auth, in t.OverviewAuthority))
            {
                t.SetText(ref t.Header, slot, s.Intern(row.Header));
                t.HeaderAccent[slot] = row.HeaderAccent;
                t.Applied(slot, (uint)ArtistFields.Header, auth, ref t.OverviewAuthority);
                // The grading is requested at commit, not on the hero's first render (ch 07 G1's explicit-Ensure path).
                StringId header = t.Header[slot];
                if (!header.IsEmpty) Palette.Ensure(new ReadOnlySpan<StringId>(in header));
            }
            if ((overview & (uint)ArtistFields.Stats) != 0
                && t.Accepts(slot, (uint)ArtistFields.Stats, auth, in t.OverviewAuthority))
            {
                t.Monthly[slot] = row.Monthly;
                t.Followers[slot] = row.Followers;
                t.WorldRank[slot] = row.WorldRank;
                t.Flags[slot] = (t.Flags[slot] & ~(uint)ArtistFlags.StatsMask) | (row.Flags & (uint)ArtistFlags.StatsMask);
                t.Applied(slot, (uint)ArtistFields.Stats, auth, ref t.OverviewAuthority);
            }
            if ((overview & (uint)ArtistFields.Bio) != 0
                && t.Accepts(slot, (uint)ArtistFields.Bio, auth, in t.OverviewAuthority))
            {
                t.SetText(ref t.Bio, slot, s.Intern(row.Bio));
                // The decoder strips the HTML and cuts the first sentence; the column exists so the hero never does
                // either on a render (ch 08 GAP 3, P11).
                t.SetText(ref t.BioLead, slot, s.Intern(row.BioLead));
                t.Applied(slot, (uint)ArtistFields.Bio, auth, ref t.OverviewAuthority);
            }
            if ((overview & (uint)ArtistFields.Tour) != 0
                && t.Accepts(slot, (uint)ArtistFields.Tour, auth, in t.OverviewAuthority))
            {
                // The banner is DERIVED from the concert list (`Artist.DeriveTour`, ch 08 GAP 15). An overview answer
                // speaks for the group — it says "the tour is whatever the concerts say" — but carries no banner text,
                // and must not blank one the concert commit already derived. Only a row that states text writes it.
                if (!row.TourEyebrow.IsEmpty || !row.TourHeadline.IsEmpty || !row.TourSubline.IsEmpty)
                {
                    t.SetText(ref t.TourEyebrow, slot, s.Intern(row.TourEyebrow));
                    t.SetText(ref t.TourHeadline, slot, s.Intern(row.TourHeadline));
                    t.SetText(ref t.TourSubline, slot, s.Intern(row.TourSubline));
                    t.Flags[slot] = (t.Flags[slot] & ~(uint)ArtistFlags.TourMask) | (row.Flags & (uint)ArtistFlags.TourMask);
                }
                t.Applied(slot, (uint)ArtistFields.Tour, auth, ref t.OverviewAuthority);
            }
            if ((overview & (uint)ArtistFields.Latest) != 0
                && t.Accepts(slot, (uint)ArtistFields.Latest, auth, in t.OverviewAuthority))
            {
                // The answer SPOKE for the latest release: no uri is "there is none", not "unchanged".
                t.Latest[slot] = row.LatestUri.IsEmpty ? Table.None : s.Slot(Current.Albums, in row.LatestUri);
                t.Applied(slot, (uint)ArtistFields.Latest, auth, ref t.OverviewAuthority);
            }
            if ((overview & (uint)ArtistFields.Pick) != 0
                && t.Accepts(slot, (uint)ArtistFields.Pick, auth, in t.OverviewAuthority))
            {
                // A pick arrives whole and replaces the previous one whole; the artist keeps its row, so a re-answer
                // costs no slab growth at all. An answer that says "NONE" (no pinned item) releases the row and zeroes
                // the pointer — it used to allocate an empty side row anyway, and `HasPick` then lied.
                var picks = Current.ArtistPicks;
                int pick = t.Pick[slot];
                if (!HasPickContent(in row))
                {
                    if (pick > 0) { picks.ReleaseRow(pick); t.Pick[slot] = 0; picks.MarkDirty(); }
                }
                else
                {
                    if (pick <= 0) { pick = picks.Alloc(); t.Pick[slot] = pick; }
                    ref var p = ref picks.Row[pick];
                    // A side slab is not a `Column<StringId>`, so `Table.SetText` cannot reach it — the same pair one
                    // level down (file header note 3). REPLACING a pick is the case that matters: the second answer
                    // overwrites all eight fields, and without the release half those eight strings would stay in the
                    // interner for the life of the process (defect 1).
                    Entities.RetainText(ref p.Eyebrow, s.Intern(row.PickEyebrow));
                    Entities.RetainText(ref p.Title, s.Intern(row.PickTitle));
                    Entities.RetainText(ref p.Subtitle, s.Intern(row.PickSubtitle));
                    Entities.RetainText(ref p.Comment, s.Intern(row.PickComment));
                    Entities.RetainText(ref p.Cover, s.Intern(row.PickCover));
                    Entities.RetainText(ref p.Uri, s.Intern(row.PickUri));
                    Entities.RetainText(ref p.ItemUri, s.Intern(row.PickItemUri));
                    Entities.RetainText(ref p.Background, s.Intern(row.PickBackground));
                    p.ItemKind = row.PickItemKind;
                    p.ReleaseAt = row.PickReleaseAt;
                    picks.MarkDirty();
                }
                t.Applied(slot, (uint)ArtistFields.Pick, auth, ref t.OverviewAuthority);
            }
            if ((overview & (uint)ArtistFields.PreRelease) != 0
                && t.Accepts(slot, (uint)ArtistFields.PreRelease, auth, in t.OverviewAuthority))
            {
                var upcoming = Current.ArtistPreReleases;
                int pre = t.PreRelease[slot];
                if (!HasPreReleaseContent(in row))
                {
                    // Same rule as the pick: "nothing upcoming" is an answer, and it owns no side row.
                    if (pre > 0) { upcoming.ReleaseRow(pre); t.PreRelease[slot] = 0; upcoming.MarkDirty(); }
                    t.Flags[slot] &= ~(uint)ArtistFlags.PreReleaseMask;
                }
                else
                {
                    if (pre <= 0) { pre = upcoming.Alloc(); t.PreRelease[slot] = pre; }
                    ref var u = ref upcoming.Row[pre];
                    Entities.RetainText(ref u.Uri, s.Intern(row.UpcomingUri));
                    Entities.RetainText(ref u.Name, s.Intern(row.UpcomingName));
                    Entities.RetainText(ref u.Cover, s.Intern(row.UpcomingCover));
                    Entities.RetainText(ref u.Type, s.Intern(row.UpcomingType));
                    u.ReleaseAt = row.UpcomingReleaseAt;
                    upcoming.MarkDirty();
                    t.Flags[slot] = (t.Flags[slot] & ~(uint)ArtistFlags.PreReleaseMask)
                                  | (row.Flags & (uint)ArtistFlags.PreReleaseMask);
                }
                t.Applied(slot, (uint)ArtistFields.PreRelease, auth, ref t.OverviewAuthority);
            }
            if ((overview & (uint)ArtistFields.Chart) != 0
                && t.Accepts(slot, (uint)ArtistFields.Chart, auth, in t.OverviewAuthority))
            {
                // No column: the BIT is the fact (ch 08 GAP 18). The rows themselves are `Edges.ArtistPopular`.
                t.Applied(slot, (uint)ArtistFields.Chart, auth, ref t.OverviewAuthority);
            }
        }
    }

    /// <summary>Does the staged pick say anything? A pinned item is addressed by its uri and labelled by its title;
    /// either alone is a pick, neither is "none".</summary>
    internal static bool HasPickContent(in StagedArtist row)
        => !row.PickUri.IsEmpty || !row.PickItemUri.IsEmpty || !row.PickTitle.IsEmpty || !row.PickComment.IsEmpty;

    /// <summary>Does the staged upcoming release say anything? (0.2.9 <c>MapPreRelease</c> required a uri and a name;
    /// the decoder applies that gate, and the commit accepts either so a seeded row cannot be half-dropped.)</summary>
    internal static bool HasPreReleaseContent(in StagedArtist row) => !row.UpcomingUri.IsEmpty || !row.UpcomingName.IsEmpty;
}

// ── persistence (Store.cs's per-kind seam) ───────────────────────────────────────────────────────────────────────────

/// <summary>How an artist survives a restart. Persists <see cref="ArtistFields.Identity"/> plus three of the
/// overview's sub-groups that are plain answered scalars: <see cref="ArtistFields.Header"/>,
/// <see cref="ArtistFields.Stats"/> and <see cref="ArtistFields.Bio"/> (the raw HTML only, not
/// <see cref="ArtistTable.BioLead"/> — the decoder's own stripped first sentence, which this shape leaves to
/// re-derive with the next overview answer, same reasoning as <c>TrackTable.ArtistLine</c>).
///
/// <para><b>Deliberately NOT persisted:</b> <see cref="ArtistFields.Pick"/> and <see cref="ArtistFields.PreRelease"/>
/// — both are sparse SIDE-TABLE rows (<see cref="ArtistPickTable"/>, <see cref="ArtistPreReleaseTable"/>) with their
/// own bump-allocated identity that this schema has no column for; <see cref="ArtistFields.Latest"/> — an album
/// cross-reference that is cheap to re-ask and, unlike the six columns above, would need its own persisted side
/// state to survive meaningfully; <see cref="ArtistFields.Tour"/> — derived at commit from the concert list, which
/// this shape does not persist either; and <see cref="ArtistFields.Chart"/> — a marker bit with no column at all
/// (the rows are <c>Edges.ArtistPopular</c>). A cold-started artist page re-asks the overview for all of these, same
/// as any row this store has never seen.</para>
///
/// <para>STORE THREAD (both halves) — see <see cref="ShowShape"/>'s note.</para></summary>
public sealed class ArtistShape : KindShape
{
    static readonly StoreColumn[] Cols =
    [
        new("name", StoreType.Text, StoreColumnFlags.Title),
        new("image", StoreType.Text),
        new("header", StoreType.Text),
        new("header_accent", StoreType.Int),
        new("bio", StoreType.Text),
        new("monthly", StoreType.Int),
        new("followers", StoreType.Int),
        new("world_rank", StoreType.Int),
        new("identity_auth", StoreType.Int, StoreColumnFlags.Authority),
        new("overview_auth", StoreType.Int, StoreColumnFlags.Authority),
    ];

    const uint OverviewFields = (uint)(ArtistFields.Header | ArtistFields.Stats | ArtistFields.Bio);
    const uint PersistedFields = (uint)ArtistFields.Identity | OverviewFields;

    public override EntityKind Kind => EntityKind.Artist;
    public override string Table => "artist";
    public override ReadOnlySpan<StoreColumn> Columns => Cols;

    public override void Save(Staging s, RowWriter w)
    {
        var rows = s.ArtistsOrNull;
        if (rows is null) return;
        var span = rows.Span;
        for (int i = 0; i < span.Length; i++)
        {
            ref readonly var row = ref span[i];
            uint known = row.Known & PersistedFields;
            bool identity = (known & (uint)ArtistFields.Identity) != 0;
            bool overview = (known & OverviewFields) != 0;

            if (identity)
            {
                w.Text(0, row.Name);
                w.Text(1, row.Image);
                w.Int(8, (int)row.Authority);
            }
            else { w.Null(0); w.Null(1); w.Null(8); }

            if ((known & (uint)ArtistFields.Header) != 0)
            {
                w.Text(2, row.Header);
                w.Int(3, row.HeaderAccent);
            }
            else { w.Null(2); w.Null(3); }

            if ((known & (uint)ArtistFields.Bio) != 0) w.Text(4, row.Bio); else w.Null(4);

            if ((known & (uint)ArtistFields.Stats) != 0)
            {
                w.Int(5, row.Monthly);
                w.Int(6, row.Followers);
                w.Int(7, row.WorldRank);
            }
            else { w.Null(5); w.Null(6); w.Null(7); }

            if (overview) w.Int(9, (int)row.Authority); else w.Null(9);

            w.Emit(row.Id, known, Entities.Now, Entities.Now);
        }
    }

    public override void Load(RowReader r, Staging into)
    {
        ref var row = ref into.Artists.Add();
        row.Id = r.Uri;
        row.Name = r.Text(0);
        row.Image = r.Text(1);
        row.Header = r.Text(2);
        row.HeaderAccent = (uint)r.Int(3);
        row.Bio = r.Text(4);
        row.Monthly = (uint)r.Int(5);
        row.Followers = (uint)r.Int(6);
        row.WorldRank = (ushort)r.Int(7);
        row.Known = r.Known & PersistedFields;
        row.Authority = (Authority)Math.Max(r.Int(8), r.Int(9));
    }
}

// ── the overview's payload edges, staged (ch 08 GAP 9-14) ────────────────────────────────────────────────────────────
//
// SIX PAYLOAD RELATIONS HAVE NO `Relation` MEMBER: their payloads carry text the generic staged edge has one slot for,
// and their commit owns that text (Edges.cs header: "the rule is on the WRITER"). So they cross the thread boundary in
// their OWN lists, exactly like the rootlist and the traits, and `Entities.CommitArtistExtras` (Artist.Rules.cs) lands
// them inside `CommitArtists`. Every run is a WHOLE-LIST rewrite, Complete — an empty answer lands Complete-and-empty,
// which is what renders "no gallery" instead of a skeleton forever.

/// <summary>Which artist payload relation a <see cref="StagedArtistExtraRun"/> rewrites.</summary>
public enum ArtistExtraKind : byte
{
    /// <summary><c>Edges.ArtistGallery</c>: T0 = the image url. No target.</summary>
    Gallery,
    /// <summary><c>Edges.ArtistPlaylists</c>: Target = the playlist, T0 = the subtitle.</summary>
    Playlists,
    /// <summary><c>Edges.ArtistVideos</c>: Target = the track, T0 = the 16:9 thumb, I0 = duration ms.</summary>
    Videos,
    /// <summary><c>Edges.ArtistMerch</c> → <c>Edges.Merch</c> rows: T0 name, T1 price, T2 image, T3 shop url. No target.</summary>
    Merch,
    /// <summary><c>Edges.ArtistCities</c>: T0 city, T1 country, U0 listeners. No target.</summary>
    Cities,
    /// <summary><c>Edges.ArtistLinks</c>: T0 name, T1 url, B0 <see cref="ArtistCatalog.LinkKind"/>. No target.</summary>
    Links,
}

/// <summary>One staged payload edge — a union read by its run's <see cref="ArtistExtraKind"/> (the field map is on each
/// member). Text is a <see cref="TextRef"/>: the decoder cannot intern (C1).</summary>
public struct StagedArtistExtra
{
    public StagedId Target;
    public TextRef T0, T1, T2, T3;
    public uint U0;
    public int I0;
    public byte B0;
}

/// <summary>One artist's rewritten payload relation: a slice of <see cref="Staging.ArtistExtraRows"/>.</summary>
public struct StagedArtistExtraRun
{
    public StagedId Parent;
    public ArtistExtraKind Kind;
    public int Start, Length;
}

/// <summary>One artist's staged chart list — the overview's SEED run or the extended list's EXTENSION run — a slice of
/// <see cref="Staging.PopularTracks"/>. Neither is an edge run: the commit MERGES them (Artist.Rules.cs,
/// <see cref="ArtistPopularTracks.Merge"/>) against each other and against the committed chart, so the order contract
/// holds whichever answer lands first and whether they share a batch or not.</summary>
public struct StagedPopularRun
{
    public StagedId Parent;
    public int Start, Length;
    public bool Extension;
}

public sealed partial class Staging
{
    StagedList<StagedArtistExtra>? _artistExtraRows;
    StagedList<StagedArtistExtraRun>? _artistExtraRuns;
    StagedList<StagedId>? _popularTracks;
    StagedList<StagedPopularRun>? _popularRuns;

    /// <inheritdoc cref="StagedArtistExtra"/>
    public StagedList<StagedArtistExtra> ArtistExtraRows => _artistExtraRows ??= Register(new StagedList<StagedArtistExtra>());
    /// <inheritdoc cref="StagedArtistExtraRun"/>
    public StagedList<StagedArtistExtraRun> ArtistExtraRuns => _artistExtraRuns ??= Register(new StagedList<StagedArtistExtraRun>());
    /// <inheritdoc cref="StagedPopularRun"/>
    public StagedList<StagedId> PopularTracks => _popularTracks ??= Register(new StagedList<StagedId>());
    /// <inheritdoc cref="StagedPopularRun"/>
    public StagedList<StagedPopularRun> PopularRuns => _popularRuns ??= Register(new StagedList<StagedPopularRun>());

    internal StagedList<StagedArtistExtra>? ArtistExtraRowsOrNull => _artistExtraRows;
    internal StagedList<StagedArtistExtraRun>? ArtistExtraRunsOrNull => _artistExtraRuns;
    internal StagedList<StagedId>? PopularTracksOrNull => _popularTracks;
    internal StagedList<StagedPopularRun>? PopularRunsOrNull => _popularRuns;

    /// <summary>Begin one artist payload relation (see <see cref="ArtistExtraRun"/>).</summary>
    public ArtistExtraRun RunArtistExtra(ArtistExtraKind kind) => new(this, kind);

    /// <summary>Where the next chart list starts — take it before the walk, hand it to <see cref="EndPopular"/>.</summary>
    public int PopularMark => PopularTracks.Count;

    /// <summary>Close the chart list appended since <paramref name="mark"/> as the artist's SEED
    /// (<paramref name="extension"/> false, the overview's top tracks) or its EXTENSION (the extended list). An empty list
    /// is recorded too: "the chart is empty" is an answer (finding 27).</summary>
    public void EndPopular(in StagedId parent, int mark, bool extension)
    {
        if (parent.IsEmpty || mark < 0 || mark > PopularTracks.Count) return;
        ref var run = ref PopularRuns.Add();
        run.Parent = parent;
        run.Start = mark;
        run.Length = PopularTracks.Count - mark;
        run.Extension = extension;
    }
}

/// <summary>ONE artist payload relation being appended, as a cursor (the <see cref="EdgeRun"/> shape): members go to
/// <see cref="Staging.ArtistExtraRows"/>, <see cref="End"/> records the run. A ref struct over the pooled lists — no
/// allocation, and it cannot outlive the decode.</summary>
public ref struct ArtistExtraRun
{
    readonly Staging _s;
    readonly ArtistExtraKind _kind;
    readonly int _start;

    public ArtistExtraRun(Staging s, ArtistExtraKind kind)
    {
        _s = s;
        _kind = kind;
        _start = s.ArtistExtraRows.Count;
    }

    /// <summary>How many members this run has taken.</summary>
    public readonly int Count => _s.ArtistExtraRows.Count - _start;

    /// <summary>Append one member by reference, zeroed.</summary>
    public ref StagedArtistExtra Add() => ref _s.ArtistExtraRows.Add();

    /// <summary>Undo the last <see cref="Add"/> (the wire named a member and then failed to identify it).</summary>
    public void DropLast() { if (Count > 0) _s.ArtistExtraRows.Drop(); }

    /// <summary>Close as a whole-list rewrite — Complete, EVEN IF EMPTY. A parentless run is thrown away.</summary>
    public void End(in StagedId parent)
    {
        if (parent.IsEmpty) { _s.ArtistExtraRows.Rewind(_start); return; }
        ref var run = ref _s.ArtistExtraRuns.Add();
        run.Parent = parent;
        run.Kind = _kind;
        run.Start = _start;
        run.Length = Count;
    }
}
