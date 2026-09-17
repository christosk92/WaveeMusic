// ── Entities/Album.cs ──────────────────────────────────────────────────────────────────────────────────────────────
// the album columns, flags, field groups, handle and commit (Wave 1) + the album surface's pure rules (Wave 5)
//
// Role: CORE
// Owner: A (Wave 1: the data model) · M (Wave 5: the rules section at the bottom, stream C of WP-5.M)
// Wave: 1 · 5
// Budget: 400 lines (plan §2). The columns alone were 443 before the rules; the Wave 5 rules push the file well past
//   +30 %, and the partial they belong in is `Entities/Album.Rules.cs` (release facts, upcoming, drawer verdict, page
//   predicates) — named here and in the WP-5.M report, not created (the WP-5 file rule).
// Spec: plan §2, §4 · ch 05 §6 (the loc drift), §7, §8, §9 · ch 01 GAP 4 · ch 08 GAP 17
//
// THE ALBUM COLUMNS, FLAGS, FIELD GROUPS AND HANDLE. The ported pure rules that share this file —
// `AlbumReleaseFacts`, `PreReleaseDerivation`, `AlbumDrawerVerdict` and `AlbumTrailing`'s predicates — are owner M's,
// in Wave 5 (plan §2, "Two notes on the entity CORE rows"), at the bottom. The notice rule landed in
// `Detail.NoticeRules.ForAlbum` (WP-4.5) and is not restated here.
//
// Plan §4 declares no album columns at all; ch 05 §7 DATA GAPS D1-D3 is the specification, ch 08 GAP 17 adds the CARD
// group the discography grid reads, and ch 01 GAP 4 adds the top-track slot. Every column below cites one of them.
//
// PRERELEASE IS A FLAG AND A DATE, NOT A KIND (§9.6 Q4 and the prerelease row of §9.5; ch 05 D3/D9, ch 31 §7.3).
// `spotify:prerelease:<id>` parses as an ALBUM (Entities.cs's `EntityUri.KindOf`), the row carries
// `AlbumFlags.PreRelease` plus `PreReleaseEnd`, and the resolved kind-138 pairing lives in its own field group because
// it is its own request. The alternative — a second kind — would migrate a row between tables on release day, which is
// the one thing a slot-based model must never do: every handle to it would silently point at the wrong table.
//
// SINCE THE PACKED IDENTITY (2026-09-12, docs/plans/wavee/wavee-0.3-entity-identity-memory.md §6) the shape carries
// itself: `EntityId` spends one flag bit on `Prerelease`, so a `spotify:prerelease:<22>` uri and the album's own
// `spotify:album:<22>` are two ids one bit apart — two rows, exactly as the two uri STRINGS were two rows before, and
// `Format` reproduces `prerelease:` rather than naming an album that does not exist. Two consequences this file owns:
// the VERDICT bit is now set from the uri at commit (ch 05 D3's "from the wire's own flag OR from a
// `spotify:prerelease:` uri", which nothing implemented before), and `PreReleaseUri` STAYS a `StringId` column — it
// names the OTHER row, it is a fact about one album in twenty, and 20 more bytes on every album row to carry a packed
// id for it would cost far more than the handful of strings it saves (doc §5.1 concludes the same).

using System.Globalization;
using System.Runtime.InteropServices;
using FluentGpu.Foundation;
using FluentGpu.Localization;

namespace Wavee;

// ── field groups ─────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Which of an album's columns are filled. Three groups arrive from three different requests and the split is
/// exactly that split (ch 05 §7's "who asks for what"): <see cref="Identity"/> and <see cref="Release"/> ride the
/// interactive open (<c>HydrationLevel.Rich</c>), <see cref="Publishing"/> rides the TRAILING pane's background
/// getAlbum, and <see cref="PreReleaseLink"/> is asked only for a row that is or might be upcoming.</summary>
[Flags]
public enum AlbumFields : uint
{
    None = 0,

    Title = 1 << 0,
    Image = 1 << 1,
    Year = 1 << 2,
    TrackCount = 1 << 3,
    /// <summary>Album / Single / EP / Compilation — the one place release kind becomes layout
    /// (<c>DetailPage.ResolveConfig</c>, ch 05 §8).</summary>
    Kind = 1 << 4,
    /// <summary>The billed artists: the <c>AlbumArtists</c> edge run (<c>Edges.AlbumArtists</c>), set ONLY by a
    /// decoder that actually closed that run — mirrors <see cref="TrackFields.Artists"/> (Track.cs:57) exactly, and
    /// exists for the identical reason (bug C's album variant, 2026-09-15): <c>AlbumV4</c> (ext kind 9) is the sole
    /// route registered for <see cref="Identity"/>, and it also emits the <c>Relation.AlbumArtists</c> run as a side
    /// effect — a relation is not a <c>FetchEdge</c> and is asked only by asking the row's own group
    /// (<c>Fetch.Routes.cs</c>). Before this bit existed, <see cref="AlbumShape.PersistedFields"/> persisted all of
    /// <see cref="Identity"/> and a disk-restored album's billed-artist line went blank forever: <c>Fetch.NeedOf</c>
    /// never re-asked a group it believed it already had. There is no flat <c>artist_line</c> column for an album —
    /// a reader wants <c>Edges.AlbumArtists.Targets(slot)</c> directly (<c>Album.ArtistSlots</c>).</summary>
    Artists = 1 << 6,
    /// <summary>The hot group. Unlike <see cref="TrackFields.Row"/>, note that <see cref="Card"/> below inherits
    /// <see cref="Artists"/> through this composite even though a grid/shelf card never PAINTS the billed-artist
    /// line (ch 08 GAP 17 names only name/cover/year/date/count/kind) — see the bug-handoff doc's surface analysis
    /// for which callers that puts an extra <c>AlbumV4</c> round trip behind. The one that mattered enough to fix —
    /// <c>Artist.Discography.cs</c>'s grid, whose rows are near-universally thin (<c>ArtistStageRelease</c> never
    /// parses billed artists at all) — now asks <see cref="DiscoCard"/> instead, see its doc.</summary>
    Identity = Title | Artists | Image | Year | TrackCount | Kind,

    /// <summary>The ISO release date, its precision and the parsed instant (ch 05 D2, ch 08 GAP 17). Its own bit
    /// because the card subtitle and the era bands need the DATE while the panel needs the whole publishing record.</summary>
    Release = 1 << 5,
    /// <summary>What a grid or shelf card paints (ch 08 GAP 17): name, cover, year, release date + precision, track
    /// count, kind. Composed from <see cref="Identity"/>, so it also carries <see cref="Artists"/> — honest for a
    /// SURFACE that renders the billed-artist line off the same row, dishonest for one that never reads it. See
    /// <see cref="DiscoCard"/> for the one that doesn't.</summary>
    Card = Identity | Release,
    /// <summary><see cref="Card"/> minus <see cref="Artists"/> — the artist page's OWN discography grid
    /// (<c>Artist.Discography.cs</c>'s <c>DiscoGridHost</c>, shared by the inline facets and the standalone
    /// <c>DiscographyPage</c>). A card there paints title/cover/year/date/track-count/kind
    /// (<c>DiscoCardText.AlbumMeta</c>) and NEVER the billed artists — every card is already on the page of the
    /// artist who made it, so naming them again would be noise, not a fact the row is missing. Gating the grid's
    /// `Ensure`/readiness on the full <see cref="Card"/> instead would demand <see cref="AlbumFields.Artists"/> for
    /// every release, and <c>ArtistStageRelease</c> (`Spotify.Decode.Artist.cs`) never parses billed artists off the
    /// discography payload at all — a THIN row there is the common case, not the exception, so that would add a
    /// third <c>AlbumV4</c> round trip behind the two bug D already has for every card in the grid. Use this group,
    /// not <see cref="Card"/>, for anything that only ever renders what <c>DiscoCardText.AlbumMeta</c> reads.</summary>
    DiscoCard = Card & ~Artists,

    /// <summary>Label, copyright, the courtesy line, the share url and the disc count — the "About this release" panel,
    /// whole or not at all (ch 05 §7: the panel must not grow a Label line later).</summary>
    Publishing = 1 << 8,
    /// <summary>The prerelease VERDICT: <see cref="AlbumFlags.PreRelease"/> plus <see cref="AlbumTable.PreReleaseEnd"/>
    /// (ch 02 §7's countdown row).</summary>
    Availability = 1 << 9,
    /// <summary>The resolved <c>spotify:prerelease:</c> pairing (extension kind 138). Its own group because it is its
    /// own request, gated exactly as 0.2.9 gates it, and because a STALE link must not turn the heart into a pre-save
    /// for a record that shipped last week (ch 05 §7).</summary>
    PreReleaseLink = 1 << 10,
    /// <summary>The star's row (ch 01 GAP 4, ch 04 §7) — derived on the model at commit, never an O(n) scan per render.</summary>
    TopTrack = 1 << 11,

    /// <summary>Everything the detail page reads (ch 04 §7's top-track predicate spells it <c>AlbumFields.Detail</c>).</summary>
    Detail = Card | Publishing | Availability | TopTrack,
    All = Detail | PreReleaseLink,
}

/// <summary>How the surface treats the release (ch 05 §8, <c>DetailPage.ResolveConfig</c>): <c>EP</c> deliberately
/// falls through to the plain Album layout. Values match the 0.2.9 <c>AlbumKind</c> ordinals so a persisted byte
/// survives the rewrite.</summary>
public enum AlbumKind : byte { Single = 0, EP = 1, Album = 2, Compilation = 3 }

/// <summary>Album booleans as bits (P3).</summary>
[Flags]
public enum AlbumFlags : uint
{
    None = 0,
    /// <summary>Unreleased: the countdown card, the pre-save heart swap and the greyed pending rows (ch 05 D3). Set
    /// from the wire's own flag OR from a <c>spotify:prerelease:</c> uri — never from a second entity kind.</summary>
    PreRelease = 1 << 0,
    AvailabilityMask = PreRelease,
}

// ── the table ────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Every album in one scope, as columns (ch 05 D1-D3). Identity first — a grid card touches
/// <see cref="Title"/>, <see cref="Image"/>, <see cref="Year"/>, <see cref="TrackCount"/> and <see cref="Kind"/>, and a
/// virtualized discography facet touches nothing else for hundreds of rows.</summary>
public sealed class AlbumTable : Table
{
    // ── identity (ch 05 D1) ──
    public Column<StringId> Title, Image;
    public Column<ushort> Year;
    public Column<int> TrackCount;
    /// <summary><see cref="AlbumKind"/>. Named <c>ReleaseKind</c> and not <c>Kind</c> (which is what ch 05 D1 calls
    /// it) because <see cref="Table.Kind"/> is the abstract ENTITY-kind property every table overrides — a column of
    /// that name could only hide it, never implement it. It is also 0.2.9's own name for the value
    /// (<c>AlbumTrailing</c>'s <c>ReleaseKind == Single</c> short-release predicate, ch 05 §8).</summary>
    public Column<byte> ReleaseKind;
    /// <summary><see cref="AlbumFlags"/>.</summary>
    public Column<uint> Flags;
    /// <summary>ARGB from the cover's <c>extractedColors.colorRaw.hex</c>; 0 = none. Rides with Identity and is
    /// written only when the answer carried one.</summary>
    public Column<uint> Accent;

    // ── release (ch 05 D2, ch 08 GAP 17) ──
    /// <summary>The wire's ISO string, kept verbatim: the panel states the date at the provider's PRECISION, and a
    /// re-formatted date would silently promote "2019" to "1 Jan 2019".</summary>
    public Column<StringId> ReleaseDateIso;
    /// <summary>0 year · 1 month · 2 day — which of the ISO string's fields the provider actually knows.</summary>
    public Column<byte> DatePrecision;
    /// <summary>The parsed instant, seconds (ch 05 §8's <c>PreReleaseDerivation.ReleaseInstant</c>; ch 02 §7 reads it
    /// as <c>a.ReleaseAt</c>). 0 = unparsed — a bare <c>"2019"</c> deliberately does NOT parse.</summary>
    public Column<int> ReleaseAt;

    // ── publishing (ch 05 D2) ──
    public Column<StringId> Label, Copyright, Courtesy, ShareUrl;
    public Column<byte> DiscCount;

    // ── prerelease (ch 05 D3) ──
    /// <summary>When the countdown ends, seconds. Wall-clock gated by every rung of the derivation, so a stale flag
    /// can never resurrect a countdown (ch 05 §8).</summary>
    public Column<int> PreReleaseEnd;
    /// <summary>The resolved <c>spotify:prerelease:</c> uri the pre-save heart targets (extension kind 138).</summary>
    public Column<StringId> PreReleaseUri;

    /// <summary>The highest-play-count track's SLOT (ch 01 GAP 4, ch 04 §7's "derived on the model"). 0 = no row has
    /// plays, which is also the "no star" answer. It is the similar-albums seed too (<c>AlbumTrailing.SeedTrack</c>).</summary>
    public Column<int> TopTrackSlot;

    // ── authority, per column GROUP (D16) ──
    public Column<byte> IdentityAuthority, ReleaseAuthority, PublishingAuthority;

    public override EntityKind Kind => EntityKind.Album;

    protected override void GrowColumns(int capacity)
    {
        Title.EnsureCapacity(capacity);
        Image.EnsureCapacity(capacity);
        Year.EnsureCapacity(capacity);
        TrackCount.EnsureCapacity(capacity);
        ReleaseKind.EnsureCapacity(capacity);
        Flags.EnsureCapacity(capacity);
        Accent.EnsureCapacity(capacity);
        ReleaseDateIso.EnsureCapacity(capacity);
        DatePrecision.EnsureCapacity(capacity);
        ReleaseAt.EnsureCapacity(capacity);
        Label.EnsureCapacity(capacity);
        Copyright.EnsureCapacity(capacity);
        Courtesy.EnsureCapacity(capacity);
        ShareUrl.EnsureCapacity(capacity);
        DiscCount.EnsureCapacity(capacity);
        PreReleaseEnd.EnsureCapacity(capacity);
        PreReleaseUri.EnsureCapacity(capacity);
        TopTrackSlot.EnsureCapacity(capacity);
        IdentityAuthority.EnsureCapacity(capacity);
        ReleaseAuthority.EnsureCapacity(capacity);
        PublishingAuthority.EnsureCapacity(capacity);
    }

    /// <summary>Give back every string an album row owns (defect 1; the REF-COUNTING block on <see cref="Table"/>). One
    /// line per <c>Column&lt;StringId&gt;</c> above, and the pair to the <see cref="Table.SetText"/> the commit writes
    /// through — miss one and the store's trim frees the row's columns while its text stays in the interner for the
    /// life of the process (doc §4.4).</summary>
    protected override void ReleaseText(int slot)
    {
        ClearText(ref Title, slot);
        ClearText(ref Image, slot);
        ClearText(ref ReleaseDateIso, slot);
        ClearText(ref Label, slot);
        ClearText(ref Copyright, slot);
        ClearText(ref Courtesy, slot);
        ClearText(ref ShareUrl, slot);
        ClearText(ref PreReleaseUri, slot);
    }
}

// ── the handle ───────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>An album: one <c>int</c>. Partial because <c>Album.UI.cs</c> and <c>Album.Page.cs</c> (Wave 5) add the
/// surface's readers over exactly these columns.</summary>
public readonly partial struct Album(int slot) : IEquatable<Album>
{
    static AlbumTable T => Entities.Current.Albums;

    public int Slot { get; } = slot;
    public bool IsValid => Slot > Table.None && Slot < T.Count;
    public uint Version => T.Version[Slot];
    public bool Knows(AlbumFields fields) => (T.Known[Slot] & (uint)fields) == (uint)fields;

    /// <inheritdoc cref="Track.Id"/>
    public EntityId Id => T.Id[Slot];
    /// <inheritdoc cref="Track.Uri"/>
    public EntityUri Uri => new(T.Id[Slot]);

    // ── identity ──
    public StringId TitleId => T.Title[Slot];
    public string Title => Entities.Strings.Resolve(T.Title[Slot]);
    public StringId ImageId => T.Image[Slot];
    /// <summary>0 = unknown. The eyebrow emits nothing below 1 — 0.2.9 rendered "ALBUM · 0" because its mapper wrote
    /// <c>Year.ToString()</c> unconditionally over an <c>int</c> (ch 05 §7, a deliberate fix).</summary>
    public ushort Year => T.Year[Slot];
    public int TrackCount => T.TrackCount[Slot];
    public AlbumKind Kind => (AlbumKind)T.ReleaseKind[Slot];
    /// <summary>ARGB from <c>coverArt.extractedColors.colorRaw.hex</c>; 0 = none.</summary>
    public uint Accent => T.Accent[Slot];

    // ── release + publishing ──
    public StringId ReleaseDateIsoId => T.ReleaseDateIso[Slot];
    public byte DatePrecision => T.DatePrecision[Slot];
    public int ReleaseAt => T.ReleaseAt[Slot];
    public StringId LabelId => T.Label[Slot];
    public StringId CopyrightId => T.Copyright[Slot];
    public StringId CourtesyId => T.Courtesy[Slot];
    public StringId ShareUrlId => T.ShareUrl[Slot];
    public byte DiscCount => T.DiscCount[Slot];

    // ── prerelease ──
    /// <summary>The countdown verdict (ch 05 D3). TWO sources, both monotone and neither able to contradict the other:
    /// the <see cref="AlbumFlags.PreRelease"/> bit, which is what a COLUMN SCAN over <c>Flags</c> reads and which the
    /// commit sets from the wire's flag or from the uri; and the row's own id, because a row allocated as some other
    /// answer's <c>spotify:prerelease:</c> TARGET — a track's album, a kind-138 pairing — has never been through this
    /// kind's commit and its <c>Flags</c> cell is still zero. Reading the id is one field load and no parse.</summary>
    public bool IsPreRelease => (T.Flags[Slot] & (uint)AlbumFlags.PreRelease) != 0 || T.Id[Slot].IsPrerelease;
    public int PreReleaseEnd => T.PreReleaseEnd[Slot];
    /// <summary>The pre-save target, or empty until kind 138 resolves — the heart falls back to the album uri and never
    /// blocks the CTA (ch 05 §7). Deliberately still a <see cref="StringId"/>: see the file header.</summary>
    public StringId PreReleaseUriId => T.PreReleaseUri[Slot];

    /// <summary>The same target as a PACKED id — what a pre-save call or a route actually wants, since
    /// <c>spotify:prerelease:&lt;22&gt;</c> is an ALBUM id carrying <see cref="EntityIdFlags.Prerelease"/> and names its
    /// own row (file header). COLD: it resolves the string and re-walks it (45-100 ns), so it belongs at the CTA's click
    /// and never in a card's paint — which is exactly why the column behind it is text and not one of these. Allocates
    /// nothing: <see cref="EntityId.Of(StringId)"/> reuses the id it was handed whenever the uri takes the text
    /// form.</summary>
    public EntityId PreReleaseId => EntityId.Of(T.PreReleaseUri[Slot]);

    /// <summary>The star's row and the similar-albums seed (ch 01 GAP 4, ch 05 §8). 0 = no row has plays.</summary>
    public Track TopTrack => new(T.TopTrackSlot[Slot]);

    // ── edges ──
    public ReadOnlySpan<int> TrackSlots => Entities.Current.Edges.AlbumTracks.Targets(Slot);
    /// <summary>Disc + track number per member (ch 05 D13). Parallel to <see cref="TrackSlots"/>.</summary>
    public ReadOnlySpan<AlbumTrackEdge> TrackNumbers => Entities.Current.Edges.AlbumTracks.Payload(Slot);
    public ReadOnlySpan<int> ArtistSlots => Entities.Current.Edges.AlbumArtists.Targets(Slot);
    /// <summary>Deluxe / remaster / anniversary editions of THIS album (ch 05 D4).</summary>
    public ReadOnlySpan<int> VersionSlots => Entities.Current.Edges.AlbumVersions.Targets(Slot);
    /// <summary>"More by &lt;artist&gt;" as the ALBUM payload carries it — its own edge, because that payload lands
    /// long before the artist is ever fetched (ch 05 D5).</summary>
    public ReadOnlySpan<int> MoreBySlots => Entities.Current.Edges.AlbumMoreBy.Targets(Slot);
    public ReadOnlySpan<int> FeaturedOnSlots => Entities.Current.Edges.AlbumFeaturedOn.Targets(Slot);
    public ReadOnlySpan<int> SimilarSlots => Entities.Current.Edges.AlbumSimilar.Targets(Slot);
    /// <summary>Merch listings: targets are <see cref="MerchTable"/> rows, not entities (§9.6 Q4, ch 05 D8). Read one
    /// with <see cref="MerchAt"/>.</summary>
    public ReadOnlySpan<int> MerchSlots => Entities.Current.Edges.AlbumMerch.Targets(Slot);

    /// <summary>One merch listing by the slot an <see cref="MerchSlots"/> entry names.</summary>
    public static ref Merch MerchAt(int merchSlot) => ref Entities.Current.Edges.Merch.Row[merchSlot];

    public bool Equals(Album other) => other.Slot == Slot;
    public override bool Equals(object? obj) => obj is Album other && other.Slot == Slot;
    public override int GetHashCode() => Slot;
    public static bool operator ==(Album a, Album b) => a.Slot == b.Slot;
    public static bool operator !=(Album a, Album b) => a.Slot != b.Slot;

    // ── the one derived fact this file owns ────────────────────────────────────────────────────────────────────────

    /// <summary>Recompute the top-play-count row and cache it on the album (ch 04 §7: "derived on the model … at
    /// commit"; ch 05 §8's <c>TrackList.TopTrack</c> and <c>AlbumTrailing.SeedTrack</c> are the same pick).
    ///
    /// <para>Called from the commit that could have changed the answer — the tracklist edge landing, or a kind-185 play
    /// count batch — never from a render. 0.2.9 scanned the whole list per snapshot; this scans once per answer and
    /// the star costs one column read per row afterwards. Sets <see cref="AlbumFields.TopTrack"/> even when the answer
    /// is "nobody has plays", because that is a known answer, not a missing one.</para></summary>
    public static void DeriveTopTrack(int albumSlot)
    {
        if (albumSlot <= Table.None) return;
        var albums = Entities.Current.Albums;
        var tracks = Entities.Current.Tracks;
        var members = Entities.Current.Edges.AlbumTracks.Targets(albumSlot);

        int best = Table.None;
        uint bestPlays = 0;
        for (int i = 0; i < members.Length; i++)
        {
            uint plays = tracks.PlayCount[members[i]];
            if (plays <= bestPlays) continue;              // strict: ties keep the earlier row, as 0.2.9's scan did
            bestPlays = plays;
            best = members[i];
        }

        if (albums.TopTrackSlot[albumSlot] == best && albums.Knows(albumSlot, (uint)AlbumFields.TopTrack)) return;
        albums.TopTrackSlot[albumSlot] = best;
        albums.Bump(albumSlot, (uint)AlbumFields.TopTrack);
    }
}

// ── the edges this kind adds (ch 05 D4, D5) ──────────────────────────────────────────────────────────────────────────

public sealed partial class Edges
{
    /// <summary>"Other versions" — deluxe / remaster / anniversary editions of the same record (ch 05 D4). Named by
    /// plan §4.13's own tree and missing from §4.3.</summary>
    public readonly EdgeTable<NoEdge> AlbumVersions = new();
    /// <summary>"More by &lt;artist&gt;" as carried by the ALBUM payload (ch 05 D5). Deliberately not
    /// <see cref="ArtistReleases"/>: this answer lands with the album, before the artist row exists at all.</summary>
    public readonly EdgeTable<NoEdge> AlbumMoreBy = new();
}

// ── staging and the commit ───────────────────────────────────────────────────────────────────────────────────────────

/// <summary>One decoded album. Text is <see cref="TextRef"/> until the drain interns it (C1).</summary>
public struct StagedAlbum : IStagedRow
{
    /// <summary>THE row's identity as the wire gave it (<see cref="StagedId"/>): the packed <see cref="EntityId"/> when
    /// it arrived as 16 gid bytes, the uri's UTF-8 in the arena when it arrived as text. One field, one resolve —
    /// <c>s.Slot(table, in row.Id)</c> — and no format-to-arena-then-parse-back round trip.</summary>
    public StagedId Id;
    public TextRef Title, Image, ReleaseDateIso, Label, Copyright, Courtesy, ShareUrl, PreReleaseUri;
    public int TrackCount, ReleaseAt, PreReleaseEnd;
    public ushort Year;
    public byte Kind, DatePrecision, DiscCount;
    /// <summary><see cref="AlbumFlags"/>.</summary>
    public uint Flags;
    /// <summary>ARGB cover accent; 0 = the answer carried none.</summary>
    public uint Accent;
    /// <summary><see cref="AlbumFields"/>: which groups this row speaks for.</summary>
    public uint Known;
    public Authority Authority;

    /// <inheritdoc cref="IStagedRow.Init"/>
    public void Init(in StagedId id, Authority authority, uint known) { Id = id; Authority = authority; Known = known; }
    /// <inheritdoc cref="IStagedRow.Identity"/>
    public readonly StagedId Identity => Id;
}

public sealed partial class Staging
{
    StagedList<StagedAlbum>? _albums;
    public StagedList<StagedAlbum> Albums => _albums ??= Register(new StagedList<StagedAlbum>());
    internal StagedList<StagedAlbum>? AlbumsOrNull => _albums;
}

public static partial class Entities
{
    /// <inheritdoc cref="Ensure(ReadOnlySpan{Track},TrackFields,FetchPriority)"/>
    public static void Ensure(ReadOnlySpan<Album> rows, AlbumFields wanted, FetchPriority priority = FetchPriority.Visible)
        => Ensure(Current.Albums, Slots(rows), (uint)wanted, priority);

    /// <inheritdoc cref="Ensure(ReadOnlySpan{Track},TrackFields,FetchPriority)"/>
    public static void Ensure(Album row, AlbumFields wanted, FetchPriority priority = FetchPriority.Visible)
    {
        Span<int> one = stackalloc int[1];
        one[0] = row.Slot;
        Ensure(Current.Albums, one, (uint)wanted, priority);
    }

    static partial void CommitAlbums(Staging s)
    {
        var staged = s.AlbumsOrNull;
        if (staged is null || staged.Count == 0) return;

        var t = Current.Albums;
        var rows = staged.Span;
        t.EnsureCapacity(t.Count + rows.Length);

        for (int i = 0; i < rows.Length; i++)
        {
            ref var row = ref rows[i];
            int slot = s.Slot(t, in row.Id);
            if (slot == Table.None) continue;   // a row with no identity is not a row
            var auth = row.Authority;
            uint known = row.Known;

            // Ch 05 D3: the verdict is the wire's own flag OR a `spotify:prerelease:` uri. Only the first half existed
            // before the packed id. Read it here, once, off bytes already in hand: the id's flag bit answers for a real
            // 22-base62 uri (free), and the byte prefix answers for the TEXT form — a fixture or a truncated wire value,
            // whose id never went through `TryParseGid` and therefore carries no flag. Applied at the BOTTOM of the loop
            // so the Availability group's masked write cannot clear a bit the uri asserts.
            bool uriPreRelease = t.Id[slot].IsPrerelease
                              || (!row.Id.Text.IsEmpty && EntityUri.IsPrerelease(s.Utf8(row.Id.Text)));

            if ((known & (uint)AlbumFields.Identity) != 0
                && t.Accepts(slot, (uint)AlbumFields.Identity, auth, in t.IdentityAuthority))
            {
                // `SetText`, never `Title[slot] = …`: it AddRefs the incoming id and releases the one it overwrites,
                // which is the only reason a re-answered row does not leak its previous title and cover (defect 1).
                // Both guarded (bug C's secondary defect, mirrored from Track.cs:511/515/518): a thin claim that
                // touches only ONE Identity bit still enters this block — the prerelease-link decoder
                // (`Spotify.Decode.cs`'s kind-138 handler) stages `AlbumFields.Title` alone when it read a name, with
                // no `row.Image` — and an unguarded write here would blank a cover a fuller live answer already set.
                if (!row.Title.IsEmpty) t.SetText(ref t.Title, slot, s.Intern(row.Title));
                if (!row.Image.IsEmpty) t.SetText(ref t.Image, slot, s.Intern(row.Image));
                t.Year[slot] = row.Year;
                // Guarded on the row's OWN `TrackCount` bit, the way Title/Image are guarded on emptiness: a count of
                // 0 is "not known yet", and every producer that has none withholds the bit (`AlbumV4` without discs,
                // the pathfinder's `Stage` when the node carried no count — `GetAlbum`'s header never does, its
                // `tracksV2.totalCount` closes the edge instead). `Applied` merges `Known` additively (`|=`), so an
                // unguarded write here zeroed the column while the bit an earlier `AlbumV4` had set survived: the
                // Known + 0 that painted "0 songs" beside a one-row tracklist (album page, 2026-09-16).
                if ((known & (uint)AlbumFields.TrackCount) != 0) t.TrackCount[slot] = row.TrackCount;
                t.ReleaseKind[slot] = row.Kind;
                // 0 = the answer carried no colour; never blank one an earlier answer set.
                if (row.Accent != 0) t.Accent[slot] = row.Accent;
                // `known & Identity`, NOT the bare `AlbumFields.Identity` constant — same fix as `CommitTracks`
                // (Track.cs), for the same reason. A THIN producer (`ArtistStageRelease`'s discography card, the
                // pathfinder's generic node `Stage` when its JSON carried no `artists`) stages some but not all of
                // Identity's bits, and marking the whole group Known unconditionally here would silently re-grant
                // whatever it withheld — most importantly `AlbumFields.Artists`, which is what stops the planner
                // from ever asking `AlbumV4` again for a row that never actually closed `Relation.AlbumArtists`.
                t.Applied(slot, known & (uint)AlbumFields.Identity, auth, ref t.IdentityAuthority);
            }

            if ((known & (uint)AlbumFields.Release) != 0
                && t.Accepts(slot, (uint)AlbumFields.Release, auth, in t.ReleaseAuthority))
            {
                // Guarded for the same reason as Title/Image above: extension kind 183 (`Spotify.Decode.cs`'s
                // `Publishing`) claims this group off `Year` alone and never sets `row.ReleaseDateIso` — an
                // unguarded write would blank an ISO date `AlbumV4`/`GetAlbum` already interned.
                if (!row.ReleaseDateIso.IsEmpty) t.SetText(ref t.ReleaseDateIso, slot, s.Intern(row.ReleaseDateIso));
                t.DatePrecision[slot] = row.DatePrecision;
                t.ReleaseAt[slot] = row.ReleaseAt;
                // THE YEAR RIDES WITH THE DATE. `Year` is an Identity bit and the Identity arm above owns it — but
                // extension kind 183 knows the release date and NOTHING else of the identity group (no title, no
                // cover), so for an album nobody has answered for this arm is the only source the `Year` column has.
                // Writing it here rather than widening 183's `Known` to an Identity bit is what stops a date-only
                // answer from blanking a title through the Identity arm's `SetText` (ch 05 §7's "additive only").
                uint group = (uint)AlbumFields.Release;
                if (row.Year != 0) { t.Year[slot] = row.Year; group |= (uint)AlbumFields.Year; }
                t.Applied(slot, group, auth, ref t.ReleaseAuthority);
            }

            if ((known & (uint)AlbumFields.Availability) != 0
                && t.Accepts(slot, (uint)AlbumFields.Availability, auth, in t.ReleaseAuthority))
            {
                t.PreReleaseEnd[slot] = row.PreReleaseEnd;
                t.Flags[slot] = (t.Flags[slot] & ~(uint)AlbumFlags.AvailabilityMask)
                              | (row.Flags & (uint)AlbumFlags.AvailabilityMask);
                t.Applied(slot, (uint)AlbumFields.Availability, auth, ref t.ReleaseAuthority);
            }

            if ((known & (uint)AlbumFields.Publishing) != 0
                && t.Accepts(slot, (uint)AlbumFields.Publishing, auth, in t.PublishingAuthority))
            {
                // Guarded for the same reason as Title/Image/ReleaseDateIso above: kind 183 also claims this group
                // off Copyright/Courtesy alone and never sets `row.Label`/`row.ShareUrl` — the "whole or not at
                // all" panel (ch 05 §7) must not have its Label line blanked by the narrower answer.
                if (!row.Label.IsEmpty) t.SetText(ref t.Label, slot, s.Intern(row.Label));
                if (!row.Copyright.IsEmpty) t.SetText(ref t.Copyright, slot, s.Intern(row.Copyright));
                if (!row.Courtesy.IsEmpty) t.SetText(ref t.Courtesy, slot, s.Intern(row.Courtesy));
                if (!row.ShareUrl.IsEmpty) t.SetText(ref t.ShareUrl, slot, s.Intern(row.ShareUrl));
                t.DiscCount[slot] = row.DiscCount;
                t.Applied(slot, (uint)AlbumFields.Publishing, auth, ref t.PublishingAuthority);
            }

            if ((known & (uint)AlbumFields.PreReleaseLink) != 0
                && t.Accepts(slot, (uint)AlbumFields.PreReleaseLink, auth, in t.ReleaseAuthority))
            {
                // The decoder only ever sets this bit alongside a real uri (Spotify.Decode.cs's kind-138 handler),
                // so this guard is defensive rather than load-bearing — kept for the same consistency Track.cs's
                // three text writes keep (guard every optional-column SetText, not just the ones a known bug hit).
                if (!row.PreReleaseUri.IsEmpty) t.SetText(ref t.PreReleaseUri, slot, s.Intern(row.PreReleaseUri));
                t.Applied(slot, (uint)AlbumFields.PreReleaseLink, auth, ref t.ReleaseAuthority);
            }

            // A uri cannot stop being a prerelease uri, so this only ever SETS — no mask, no authority rung, no path
            // that clears it. No `Bump`: the groups above already bumped, and a row whose only news is a bit its own
            // identity always implied is not news.
            if (uriPreRelease) t.Flags[slot] |= (uint)AlbumFlags.PreRelease;
        }
    }
}

// ── persistence (Store.cs's per-kind seam) ───────────────────────────────────────────────────────────────────────────

/// <summary>How an album survives a restart. Persists <see cref="AlbumFields.Identity"/> — minus the
/// <see cref="AlbumFields.Artists"/> bit, see below — <see cref="AlbumFields.Release"/> and
/// <see cref="AlbumFields.Publishing"/>: the three groups whose facts are answered values with their own
/// authority column.
///
/// <para><b>Deliberately NOT persisted:</b> <see cref="AlbumFields.TopTrack"/> (derived at commit from
/// <c>Edges.AlbumTracks</c>, which this shape does not persist — it re-derives the moment the tracklist edge lands
/// again); <see cref="AlbumFields.Availability"/>/<see cref="AlbumFields.PreReleaseLink"/> (the prerelease countdown
/// and its kind-138 pairing — both wall-clock/edge gated and cheap to re-ask, and the verdict bit
/// (<see cref="Album.IsPreRelease"/>) is re-derived from the id's own uri every time regardless of any column).</para>
///
/// <para><b>Also deliberately NOT persisted: <see cref="AlbumFields.Artists"/></b> — the album variant of bug C
/// (2026-09-15, the same day as the track one). <c>Album.ArtistSlots</c> reads
/// <c>Edges.AlbumArtists.Targets(slot)</c>, and this shape does not persist ANY edge relation (only the five
/// <c>LibraryEdge</c> kinds are, per the store's own scope — <c>Store.cs:450</c>'s <c>EdgeRelation.AlbumArtists</c>
/// exists but nothing here reads or writes it). Restoring the bit WITH <see cref="AlbumFields.Identity"/> would
/// tell <c>Fetch.NeedOf = wanted &amp; ~Known &amp; ~Asked</c> "never ask again" for a fact this cache genuinely
/// cannot answer: <c>AlbumV4</c> (ext kind 9) is the sole route registered for <see cref="AlbumFields.Identity"/>,
/// so a cached album's billed-artist line would go blank forever, exactly as the track credit line did before its
/// own fix. Masking the bit costs one extra Identity round trip per cached album after a restart, same as Track's
/// — and note it also means <see cref="AlbumFields.Card"/> and <see cref="AlbumFields.Detail"/> (both composed
/// from <see cref="AlbumFields.Identity"/>) cannot read as fully Known off a disk load either; that is intended,
/// not a widening of the persisted set — see <see cref="PersistedIdentity"/>.</para>
///
/// <para><b>The year rides with the date (ch 05 §7).</b> <c>Year</c> is an <see cref="AlbumFields.Identity"/> bit, but
/// a date-only answer (extension kind 183) can carry it while knowing NOTHING else of Identity — the live commit
/// writes it through the Release arm for exactly that row (<c>CommitAlbums</c> above). The persisted <c>year</c>
/// column follows the same rule: written whenever EITHER group is known, so a date-only batch's year is not silently
/// dropped by gating it on Identity alone.</para>
///
/// <para>STORE THREAD (both halves) — see <see cref="ShowShape"/>'s note.</para></summary>
public sealed class AlbumShape : KindShape
{
    static readonly StoreColumn[] Cols =
    [
        new("title", StoreType.Text, StoreColumnFlags.Title),
        new("image", StoreType.Text),
        new("year", StoreType.Int),
        new("track_count", StoreType.Int),
        new("release_kind", StoreType.Int),
        new("release_date_iso", StoreType.Text),
        new("date_precision", StoreType.Int),
        new("release_at", StoreType.Int),
        new("label", StoreType.Text),
        new("copyright", StoreType.Text),
        new("courtesy", StoreType.Text),
        new("share_url", StoreType.Text),
        new("disc_count", StoreType.Int),
        new("identity_auth", StoreType.Int, StoreColumnFlags.Authority),
        new("release_auth", StoreType.Int, StoreColumnFlags.Authority),
        new("publishing_auth", StoreType.Int, StoreColumnFlags.Authority),
        new("accent", StoreType.Int),
    ];

    /// <summary>Every bit of <see cref="AlbumFields.Identity"/> this shape can actually answer from disk.
    /// <see cref="AlbumFields.Artists"/> is masked OUT — see the class doc above — because the billed-artist edge
    /// lives only in <c>Edges.AlbumArtists</c>, which nothing below persists. Title, Image, Year, TrackCount and
    /// Kind ARE real columns (<see cref="Save"/>), so restoring their bits is honest.</summary>
    const uint PersistedIdentity = (uint)AlbumFields.Identity & ~(uint)AlbumFields.Artists;
    /// <summary>What <see cref="Save"/> may write and <see cref="Load"/> may restore into <c>Known</c> — ONE
    /// constant shared by both, so they can never drift apart. Bug C's root cause (both variants) was exactly a
    /// <c>Known</c> bit (Artists) surviving a disk restore without the data its live answer implies; keeping Save
    /// and Load on the same mask is what makes that class of bug structurally harder to reintroduce.</summary>
    const uint PersistedFields = PersistedIdentity | (uint)AlbumFields.Release | (uint)AlbumFields.Publishing;

    public override EntityKind Kind => EntityKind.Album;
    public override string Table => "album";
    public override ReadOnlySpan<StoreColumn> Columns => Cols;

    public override void Save(Staging s, RowWriter w)
    {
        var rows = s.AlbumsOrNull;
        if (rows is null) return;
        var span = rows.Span;
        for (int i = 0; i < span.Length; i++)
        {
            ref readonly var row = ref span[i];
            uint known = row.Known & PersistedFields;
            bool identity = (known & (uint)AlbumFields.Identity) != 0;
            bool release = (known & (uint)AlbumFields.Release) != 0;
            bool publishing = (known & (uint)AlbumFields.Publishing) != 0;

            if (identity)
            {
                w.Text(0, row.Title);
                w.Text(1, row.Image);
                w.Int(3, row.TrackCount);
                w.Int(4, row.Kind);
                w.Int(13, (int)row.Authority);
                w.Int(16, row.Accent);
            }
            else { w.Null(0); w.Null(1); w.Null(3); w.Null(4); w.Null(13); w.Null(16); }

            if (identity || release) w.Int(2, row.Year); else w.Null(2);   // the year rides with the date

            if (release)
            {
                w.Text(5, row.ReleaseDateIso);
                w.Int(6, row.DatePrecision);
                w.Int(7, row.ReleaseAt);
                w.Int(14, (int)row.Authority);
            }
            else { w.Null(5); w.Null(6); w.Null(7); w.Null(14); }

            if (publishing)
            {
                w.Text(8, row.Label);
                w.Text(9, row.Copyright);
                w.Text(10, row.Courtesy);
                w.Text(11, row.ShareUrl);
                w.Int(12, row.DiscCount);
                w.Int(15, (int)row.Authority);
            }
            else { w.Null(8); w.Null(9); w.Null(10); w.Null(11); w.Null(12); w.Null(15); }

            w.Emit(row.Id, known, Entities.Now, Entities.Now);
        }
    }

    public override void Load(RowReader r, Staging into)
    {
        ref var row = ref into.Albums.Add();
        row.Id = r.Uri;
        row.Title = r.Text(0);
        row.Image = r.Text(1);
        row.Year = (ushort)r.Int(2);
        row.TrackCount = (int)r.Int(3);
        row.Kind = (byte)r.Int(4);
        row.ReleaseDateIso = r.Text(5);
        row.DatePrecision = (byte)r.Int(6);
        row.ReleaseAt = (int)r.Int(7);
        row.Label = r.Text(8);
        row.Copyright = r.Text(9);
        row.Courtesy = r.Text(10);
        row.ShareUrl = r.Text(11);
        row.DiscCount = (byte)r.Int(12);
        row.Accent = (uint)r.Int(16);
        row.Known = r.Known & PersistedFields;
        row.Authority = (Authority)Math.Max(r.Int(13), Math.Max(r.Int(14), r.Int(15)));
    }
}

// ══ WAVE 5 (owner M, stream C): THE ALBUM SURFACE'S PURE RULES ═══════════════════════════════════════════════════════
//
// Ported from 0.2.9 `Features/Detail/{AlbumReleaseFactsRules, PreReleaseDerivation, AlbumDrawerVerdict,
// DetailTrailing (predicates), ArtistFacePile (overflow), DetailTracks (TopTrack)}.cs` and `PreReleaseCountdown.Breakdown`.
// The decisions are 0.2.9's; the input shapes are handles, unix SECONDS in `int`s (0 = none) and an injected `now`.
// Three deliberate changes, each named by its chapter line:
//   · the loc drift (ch 05 §6, §8 "TotalTimeLiteral"): the facts record carries PARTS and the *Text helpers format
//     through the loc runtime ONCE, so the Length tile and the meta line spell a duration the same way;
//   · the face-pile `+N` (ch 05 §0.5): the overflow counts the faces actually DRAWN;
//   · "ALBUM · 0" (ch 05 §7) is the eyebrow's input fix and lives in `Detail.Text` — `Year` 0 emits no year.

public readonly partial struct Album
{
    /// <summary>The members as handles — a reinterpretation of the edge's slot span, no copy (a handle is one int).</summary>
    static ReadOnlySpan<Track> MembersOf(Album a) => MemoryMarshal.Cast<int, Track>(a.TrackSlots);

    /// <summary>The release instant the upcoming ladder and the Released/Releases tense read: the ISO column parsed by
    /// <see cref="Upcoming.ReleaseInstant"/> (0.2.9's input was that string) when the wire gave one, else the parsed
    /// <see cref="ReleaseAt"/> column a protobuf answer wrote without text.</summary>
    static int ReleaseAtOf(Album a)
        => a.ReleaseDateIsoId.IsEmpty ? a.ReleaseAt : Upcoming.ReleaseInstant(Entities.Strings.Resolve(a.ReleaseDateIsoId));

    static string? TextOf(StringId id) => id.IsEmpty ? null : Entities.Strings.Resolve(id);

    /// <summary>The similar-albums SEED as uri text (<c>AlbumTrailing.SeedTrack</c>): the highest play-count member, else
    /// the first; null while the tracklist has not landed. UI thread — the planner resolves it into the edge batch
    /// (<c>FetchBatch.Revisions</c>) because the request is seeded by a TRACK while the relation hangs off the ALBUM and
    /// the provider cannot read the tables. COLD: one string per ask.</summary>
    public static string? SimilarSeedUri(int albumSlot)
    {
        if (albumSlot <= global::Wavee.Table.None || albumSlot >= Entities.Current.Albums.Count) return null;
        var members = MembersOf(new Album(albumSlot));
        int seed = PageRules.SeedTrackIndex(members);
        return seed < 0 ? null : members[seed].Uri.Text;
    }

    // ── release facts (ch 05 W11, §8 row 1) ─────────────────────────────────────────────────────────────────────────

    /// <summary>A release date as PARTS. <paramref name="Precision"/> is <c>AlbumTable.DatePrecision</c>'s byte:
    /// 0 year · 1 month · anything else (2 day, or an unstated 255) reads as a full day — 0.2.9's null default.</summary>
    public readonly record struct ReleaseDate(ushort Year, byte Month, byte Day, byte Precision);

    /// <summary>"About this release" as DATA (0.2.9 <c>AlbumReleaseFacts</c>), in parts: the rule counts and measures, the
    /// view formats once (<see cref="ReleaseFactsRules.SongsText"/> …). The shape is FIXED — Songs/Length/Released are
    /// tiles, Label is a note, the notes are courtesy then copyright.</summary>
    public sealed record ReleaseFacts(int SongsOut, int SongsTotal, long LengthMs, ReleaseDate? Released,
                                      bool ReleasesInFuture, string? Label, IReadOnlyList<string> Notes)
    {
        public static readonly ReleaseFacts Empty = new(0, 0, 0, null, false, null, Array.Empty<string>());
        public bool HasSongs => SongsTotal > 0;
        public bool HasLength => LengthMs > 0;
        public bool HasTiles => HasSongs || HasLength || Released is not null;
        public bool IsEmpty => !HasTiles && Label is null && Notes.Count == 0;
    }

    /// <summary>The pure arithmetic behind <see cref="ReleaseFacts"/> (0.2.9 <c>AlbumReleaseFactsRules</c>). <c>now</c> is
    /// injected, never read in here.</summary>
    public static class ReleaseFactsRules
    {
        public static ReleaseFacts For(ReadOnlySpan<Track> tracks, long nowUnixSeconds, string? releaseDateIso, byte precision,
                                       ushort year, int releaseAtUnixSeconds, string? label, string? courtesy, string? copyright)
        {
            // On a PARTLY released album the plain count and the summed length both lie: report what is OUT, and
            // measure only that (the ONE not-yet-out predicate, `Track.NotYetOutOf`).
            int outNow = 0;
            long ms = 0;
            for (int i = 0; i < tracks.Length; i++)
            {
                if (tracks[i].NotYetOut(nowUnixSeconds)) continue;
                outNow++;
                ms += tracks[i].DurationMs;
            }

            ReleaseDate? released = ParseReleaseDate(releaseDateIso.AsSpan(), precision)
                                    ?? (year > 0 ? new ReleaseDate(year, 0, 0, 0) : (ReleaseDate?)null);
            bool future = releaseAtUnixSeconds != 0 && releaseAtUnixSeconds > nowUnixSeconds;
            string? lbl = label is { Length: > 0 } ? label : null;

            // [courtesy, copyright], present-only. A multi-line copyright stays ONE entry: the view wraps it.
            int count = (courtesy is { Length: > 0 } ? 1 : 0) + (copyright is { Length: > 0 } ? 1 : 0);
            IReadOnlyList<string> notes = Array.Empty<string>();
            if (count > 0)
            {
                var list = new string[count];
                int n = 0;
                if (courtesy is { Length: > 0 }) list[n++] = courtesy;
                if (copyright is { Length: > 0 }) list[n] = copyright;
                notes = list;
            }
            return new ReleaseFacts(outNow, tracks.Length, ms, released, future, lbl, notes);
        }

        /// <summary>The album's facts off its columns and its tracklist. The caller gates on
        /// <c>Knows(AlbumFields.Publishing | AlbumFields.Release)</c> — the whole record or nothing (ch 05 §7).</summary>
        public static ReleaseFacts Of(Album a, long nowUnixSeconds)
            => For(MembersOf(a), nowUnixSeconds, TextOf(a.ReleaseDateIsoId), a.DatePrecision, a.Year, ReleaseAtOf(a),
                   TextOf(a.LabelId), TextOf(a.CourtesyId), TextOf(a.CopyrightId));

        /// <summary>An ISO date → parts at the stated precision; null when absent or unparseable (the mapper's raw-echo
        /// fallback is deliberately dropped). Invariant culture, assumed UTC: a wire value.</summary>
        public static ReleaseDate? ParseReleaseDate(ReadOnlySpan<char> iso, byte precision)
        {
            if (iso.IsWhiteSpace()) return null;
            if (!DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d))
                return null;
            return new ReleaseDate((ushort)d.Year, (byte)d.Month, (byte)d.Day, precision);
        }

        /// <summary>The Length phrase's parts: hours and minutes, a sub-minute total floors UP to one minute.</summary>
        public static (int Hours, int Minutes) LengthParts(long ms)
        {
            var t = TimeSpan.FromMilliseconds(ms);
            int h = (int)t.TotalHours, m = t.Minutes;
            return h >= 1 ? (h, m) : (0, Math.Max(1, m));
        }

        /// <summary>"13" when every row is out, "8 of 13" (<c>detail.factOfCount</c>) otherwise, null with no rows.</summary>
        public static string? SongsText(ReleaseFacts f)
            => !f.HasSongs ? null
             : f.SongsOut == f.SongsTotal ? f.SongsTotal.ToString(CultureInfo.InvariantCulture)
             : Strings.Detail.FactOfCount(f.SongsOut, f.SongsTotal);

        /// <summary>The Length tile through the SAME formatter the meta line uses (ch 05 §6's fix).</summary>
        public static string? LengthText(ReleaseFacts f) => f.HasLength ? Track.Format.TotalTime(f.LengthMs) : null;

        /// <summary>YEAR "2014" · MONTH "November 2014" · DAY "November 4, 2014", in the CURRENT culture; null when unknown.</summary>
        public static string? ReleasedText(ReleaseFacts f)
        {
            if (f.Released is not { } d || d.Year == 0) return null;
            int month = d.Month is >= 1 and <= 12 ? d.Month : 1;
            int day = Math.Clamp((int)d.Day, 1, DateTime.DaysInMonth(d.Year, month));
            var date = new DateTime(d.Year, month, day);
            return d.Precision switch
            {
                0 => date.ToString("yyyy", CultureInfo.CurrentCulture),
                1 => date.ToString("MMMM yyyy", CultureInfo.CurrentCulture),
                _ => date.ToString("MMMM d, yyyy", CultureInfo.CurrentCulture),
            };
        }

        /// <summary>The Released tile's caption: "Releases" while the instant is ahead, else "Released".</summary>
        public static string ReleasedCaption(ReleaseFacts f)
            => Loc.Get(f.ReleasesInFuture ? Strings.Detail.FactReleases : Strings.Detail.FactReleased);
    }

    // ── upcoming (ch 05 W13, §7, §8 row 2) ──────────────────────────────────────────────────────────────────────────

    /// <summary>How an album's scattered "not out yet" signals collapse into ONE countdown instant (0.2.9
    /// <c>PreReleaseDerivation</c>), plus the kind-138 gates the page needs around it.</summary>
    public static class Upcoming
    {
        /// <summary><c>PreReleaseEnd</c> ▸ the earliest FUTURE row <c>AvailableAt</c> ▸ a FUTURE release instant ▸ 0.
        /// Every rung is wall-clock gated, so a stale flag can never resurrect a countdown.</summary>
        public static int At(int preReleaseEnd, ReadOnlySpan<Track> tracks, int releaseAtUnixSeconds, long nowUnixSeconds)
        {
            if (preReleaseEnd > nowUnixSeconds) return preReleaseEnd;

            // A waterfall album carries no album-level flag at all: the next pending row is the moment worth announcing.
            int soonest = 0;
            for (int i = 0; i < tracks.Length; i++)
            {
                int at = tracks[i].AvailableAt;
                if (at > nowUnixSeconds && (soonest == 0 || at < soonest)) soonest = at;
            }
            if (soonest != 0) return soonest;

            return releaseAtUnixSeconds > nowUnixSeconds ? releaseAtUnixSeconds : 0;
        }

        public static int Of(Album a, long nowUnixSeconds) => At(a.PreReleaseEnd, MembersOf(a), ReleaseAtOf(a), nowUnixSeconds);

        /// <summary>An ISO date → unix seconds; 0 when absent, unparseable or not after the epoch. "2026-09" parses (the
        /// 1st); a bare "2026" does NOT (<c>DateTimeOffset.TryParse</c> rejects it — pinned).</summary>
        public static int ReleaseInstant(ReadOnlySpan<char> iso)
        {
            if (iso.IsWhiteSpace()) return 0;
            if (!DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)) return 0;
            long seconds = d.ToUnixTimeSeconds();
            return seconds is > 0 and <= int.MaxValue ? (int)seconds : 0;
        }

        /// <summary>The four per-unit remainders, clamped at 0 — identical to <c>Controls.Breakdown</c>.</summary>
        public static (int Days, int Hours, int Minutes, int Seconds) Breakdown(long remainingSeconds)
        {
            if (remainingSeconds <= 0) return (0, 0, 0, 0);
            long rest = remainingSeconds % 86_400;
            return ((int)Math.Min(remainingSeconds / 86_400, int.MaxValue), (int)(rest / 3600), (int)(rest % 3600 / 60),
                    (int)(rest % 60));
        }

        /// <summary>Should the page ask kind 138? 0.2.9's gate: a flagged prerelease, or anything upcoming.</summary>
        public static bool NeedsLink(Album a, long nowUnixSeconds) => a.IsPreRelease || Of(a, nowUnixSeconds) > 0;

        /// <summary>The kind-138 link's own polarity (0.2.9 <c>PreReleaseLink.IsUpcoming</c>): a null date is "announced,
        /// date unknown" (upcoming); a stated one must still be ahead — a link is cached up to 30 days.</summary>
        public static bool LinkUpcoming(int preReleaseEnd, long nowUnixSeconds) => preReleaseEnd == 0 || preReleaseEnd > nowUnixSeconds;

        /// <summary>The heart's target: the resolved <c>spotify:prerelease:</c> uri ONLY while the link is upcoming,
        /// else the album itself (ch 05 §0.9; the page keys the heart on it).</summary>
        public static EntityUri SaveTarget(Album a, long nowUnixSeconds)
            => !a.PreReleaseUriId.IsEmpty && LinkUpcoming(a.PreReleaseEnd, nowUnixSeconds) ? new EntityUri(a.PreReleaseId) : a.Uri;

        /// <summary>A <c>prerelease:</c> route's subject → the album row whose link names it (a cold scan), else the
        /// prerelease row itself when it knows its title, else <c>default</c> (the page's "Nothing here yet").</summary>
        public static Album ResolvePreRelease(EntityUri prereleaseUri)
        {
            if (!prereleaseUri.IsValid) return default;
            var t = Entities.Current.Albums;
            string want = prereleaseUri.Text;
            for (int slot = 1; slot < t.Count; slot++)
            {
                var link = t.PreReleaseUri[slot];
                if (link.IsEmpty || t.Id[slot] == prereleaseUri.Id) continue;
                if (string.Equals(Entities.Strings.Resolve(link), want, StringComparison.Ordinal)) return new Album(slot);
            }
            return t.TryGetSlot(prereleaseUri.Id, out int self) && t.Knows(self, (uint)AlbumFields.Title) ? new Album(self) : default;
        }

        /// <summary><c>Controls.PreSave</c>'s answer, after the kind-138 commit: the <c>spotify:prerelease:</c> uri a save
        /// must address, or <c>default</c> when the release is out or nothing resolves. Takes either scheme.</summary>
        public static EntityUri PreSaveTarget(EntityUri subject, long nowUnixSeconds)
        {
            if (subject.Kind != EntityKind.Album) return default;
            var albums = Entities.Current.Albums;
            Album a = EntityUri.IsPrerelease(subject.Text.AsSpan()) ? ResolvePreRelease(subject)
                    : albums.TryGetSlot(subject.Id, out int slot) ? new Album(slot) : default;
            if (!a.IsValid) return default;
            // The album that links it already applied the link's polarity in SaveTarget; the prerelease row itself
            // answers with its own uri, so the same polarity is applied to its own window here.
            var target = SaveTarget(a, nowUnixSeconds);
            bool prerelease = target.Id.IsPrerelease || EntityUri.IsPrerelease(target.Text.AsSpan());
            return prerelease && LinkUpcoming(a.PreReleaseEnd, nowUnixSeconds) ? target : default;
        }
    }

    // ── the artist-page album drawer (ch 05 W19, §8 row 3) ──────────────────────────────────────────────────────────

    /// <summary>Everything the drawer needs, decided in ONE place (0.2.9 <c>AlbumDrawerVerdict</c>): rows, columns, the
    /// show-all cell and BOTH heights, so the panel, the reserved slot and the bring-into-view can never disagree.</summary>
    public readonly record struct DrawerVerdict(string Uri, int Rows, int Columns, int Shown, int Total, bool Loading,
                                                bool ReadyEmpty, bool ShowAllRow, float PanelHeight, float SlotHeight)
    {
        /// <summary>6 pad + 28 header row + 6 pad.</summary>
        public const float HeaderH = 40f, RowPitch = 32f, TopGap = 8f, BottomGap = 8f;
        public const int CapPerColumn = 12, TwoColumnMinGridCols = 5, FallbackShimmerRows = 3;

        public static int ColumnsFor(int gridCols) => gridCols >= TwoColumnMinGridCols ? 2 : 1;

        /// <param name="selectedUri">the card the user clicked ("" = closed)</param>
        /// <param name="loadedUri">the album whose list is in hand (null = nothing)</param>
        /// <param name="loadedTracks">that album's rows</param>
        /// <param name="thinTracks">rows already on the card itself</param>
        /// <param name="thinTrackCount">the card's advertised count (sizes the placeholder)</param>
        public static DrawerVerdict For(string selectedUri, string? loadedUri, int loadedTracks, int thinTracks, int thinTrackCount,
                                        bool pending, int gridCols)
        {
            if (selectedUri.Length == 0) return default;
            bool match = loadedUri == selectedUri;                        // identity, not "whatever is in hand"
            int have = match ? loadedTracks : thinTracks;
            bool loading = !match && pending;
            bool readyEmpty = match && !pending && have == 0;
            int columns = ColumnsFor(gridCols);
            int cap = CapPerColumn * columns;
            int total = have > 0 ? have : Math.Max(thinTrackCount, 0);
            int shown = loading ? Math.Min(total > 0 ? total : FallbackShimmerRows, cap) : Math.Min(have, cap);
            bool showAll = !loading && total > cap;
            int rows = readyEmpty ? 2 : (int)Math.Ceiling((shown + (showAll ? 1 : 0)) / (float)columns);
            float panel = HeaderH + rows * RowPitch;
            return new(selectedUri, rows, columns, shown, total, loading, readyEmpty, showAll, panel, panel + TopGap + BottomGap);
        }

        /// <summary>The verdict off the album's own <c>AlbumTracks</c> edge: pending while the list is Unknown and not
        /// Failed; a failed ask reads as ready-empty (the Retry arm). COLD-ish: formats the uri once per call.</summary>
        public static DrawerVerdict Of(Album a, int gridCols)
        {
            if (!a.IsValid) return default;
            var edges = Entities.Current.Edges.AlbumTracks;
            var state = edges.Readiness(a.Slot);
            string uri = a.Uri.Text;
            bool answered = state != EdgeState.Unknown;
            return For(uri, answered ? uri : null, edges.Count(a.Slot), 0, a.TrackCount, !answered, gridCols);
        }
    }

    // ── page predicates (ch 05 §8: DetailTrailing / DetailTracks / ArtistFacePile) ─────────────────────────────────

    public static class PageRules
    {
        /// <summary>A trailing section's row cap; "Fans also like"'s chip cap; the similar-albums ask.</summary>
        public const int StackCap = 5, FansCap = 8, SimilarLimit = 24;

        /// <summary><c>ReleaseKind == Single || tracks is &gt; 0 and &lt;= 2</c> — gates the watch-video card and switches
        /// "Fans also like" to the seed track's related artists.</summary>
        public static bool IsShortRelease(AlbumKind kind, int trackCount) => kind == AlbumKind.Single || trackCount is > 0 and <= 2;

        /// <summary>The song count a meta line prints. A count of 0 is "not known yet", never "no songs" (the row-wide
        /// convention: `AlbumV4` without discs and the pathfinder's `Stage` withhold the bit rather than claim 0), so
        /// the realised members are the fallback — the same rows the facts tile and the tracklist count. A known
        /// count wins over the members when it is real, because the members may be a partial page.</summary>
        public static int SongCount(int knownTrackCount, int memberCount) => knownTrackCount > 0 ? knownTrackCount : memberCount;

        public static bool HasReleasePanel(ReleaseFacts facts, int otherVersions) => !facts.IsEmpty || otherVersions > 0;

        /// <summary>A trailing relation still out: asked this scope and neither answered nor failed.</summary>
        public static bool InFlight(bool asked, EdgeState readiness) => asked && readiness == EdgeState.Unknown;

        /// <summary>How long the trailing band waits for its two above-the-fold sections (About the artist, Featured on)
        /// after the page has demanded before it reveals whatever it has. Both are asked at Visible priority alongside
        /// the tracklist, so they normally land inside this window; the deadline is the fail-soft cap, not the path.</summary>
        public const float TrailingDeadlineMs = 400f;

        /// <summary>The trailing band's reserve (ch 05 §0.10, W7): it holds its ONE skeleton while the page has not demanded
        /// or the tracklist has not answered — those two are unconditional — and then, for at most
        /// <see cref="TrailingDeadlineMs"/>, while any of the sections that sit directly under the rows is still
        /// unresolved: About the artist (<paramref name="aboutReady"/> — the lead artist's name is known, or there is no
        /// billed artist), Featured on (<paramref name="featuredReady"/> — the recommendations edge has answered or
        /// failed) and the watch-video card (<paramref name="videoReady"/>, <see cref="VideoDecided"/> — a short
        /// release's members all know whether they have a video; a full album never waits). The deadline releases the
        /// reserve whatever is still out (a stuck ask must never hold the visible skeleton), and the below-the-fold
        /// relations (merch, more-by, similar) stay Prefetch and are NOT part of this gate: they resolve later and
        /// update in place. Fans also like (<paramref name="fansReady"/>: the related edge answered and every drawn fan
        /// knows its name) IS part of it — it sits directly under About. A failed relation is not "out" either way: its section simply does not
        /// mount (fail-soft, W12).</summary>
        public static bool TrailingReserved(bool demanded, EdgeState tracklist, bool aboutReady, bool featuredReady, bool fansReady,
                                            bool videoReady, bool deadlinePassed)
        {
            if (!demanded || tracklist == EdgeState.Unknown) return true;
            if (deadlinePassed) return false;
            return !(aboutReady && featuredReady && fansReady && videoReady);
        }

        /// <summary>Whether the watch-video card's verdict is in: a full album never shows one, so it is decided at once;
        /// a short release waits until every member knows its <see cref="TrackFields.Video"/> group.</summary>
        public static bool VideoDecided(bool shortRelease, ReadOnlySpan<Track> members)
        {
            if (!shortRelease) return true;
            for (int i = 0; i < members.Length; i++)
                if (!members[i].Knows(TrackFields.Video)) return false;
            return true;
        }

        // ── the trailing skeleton's shape (the recording's defect 4: a fixed three-section reserve collapsed on reveal) ──

        /// <summary>Which section skeletons the band reserves: the About card, the fans chip row, and
        /// <paramref name="RowBlocks"/> list-section blocks (a short release reserves none — its band is a video card
        /// and chips, not rows).</summary>
        public readonly record struct TrailingShape(bool About, bool Fans, int RowBlocks)
        {
            public bool IsEmpty => !About && !Fans && RowBlocks == 0;
        }

        /// <summary>The shape off what the row knows at mount. An unknown kind reads as a full album (the sections do
        /// the same); About and Fans are always reserved (a release without a billed artist is the exception, and a
        /// wrong reserve eases while a re-keyed region goes blank).</summary>
        public static TrailingShape SkeletonShape(bool knowsKind, AlbumKind kind, int knownTrackCount)
        {
            bool shortRelease = IsShortRelease(knowsKind ? kind : AlbumKind.Album, knownTrackCount);
            return new TrailingShape(About: true, Fans: true, RowBlocks: shortRelease ? 0 : 1);
        }

        // The skeleton's numbers, shared with the elements Album.Page.cs draws so the reserve equals the paint.
        public const float SkelHeaderH = 18f, SkelAboutH = 96f, SkelChipH = 40f, SkelRowH = 64f, SkelRowGap = 4f;
        /// <summary>Header-to-body gap and the section padding's vertical sum (Album.Page.cs <c>SectionPad</c>: XL top, L bottom).</summary>
        public const float SkelSectionGap = 12f, SkelSectionPadV = 36f;
        public const int SkelRows = 3;

        /// <summary>The DIP the skeleton for <paramref name="s"/> occupies — the same numbers the elements use.</summary>
        public static float SkeletonHeight(in TrailingShape s)
        {
            float section = SkelSectionPadV + SkelHeaderH + SkelSectionGap;
            float h = 0f;
            if (s.About) h += section + SkelAboutH;
            if (s.Fans) h += section + SkelChipH;
            if (s.RowBlocks > 0) h += s.RowBlocks * (section + SkelRows * SkelRowH + (SkelRows - 1) * SkelRowGap);
            return h;
        }

        /// <summary>W18's unresolvable <c>prerelease:</c> route: nothing named it, its own tracklist ask FAILED, and the row
        /// never learned a title — the shell with "Nothing here yet", never an error page.</summary>
        public static bool IsUnresolvedPreRelease(bool resolved, EdgeState tracklist, bool knowsTitle)
            => !resolved && tracklist == EdgeState.Failed && !knowsTitle;

        /// <summary>Does the trailing band exist at all? <paramref name="moreBy"/> is the album payload's more-by run or
        /// the artist's top albums — the two 0.2.9 arms — and needs a billed artist to title it.</summary>
        public static bool HasTrailingSections(bool shortRelease, bool hasVideo, bool about, int fans, int featured, int merch,
                                               int similar, int moreBy, int billedArtists)
        {
            if (shortRelease && hasVideo) return true;
            if (about || fans > 0 || featured > 0 || merch > 0 || similar > 0) return true;
            return moreBy > 0 && billedArtists > 0;
        }

        /// <summary>The similar-albums seed: the highest play count (ties keep the earlier row), else row 0; -1 empty.</summary>
        public static int SeedTrackIndex(ReadOnlySpan<Track> tracks)
        {
            if (tracks.Length == 0) return -1;
            int best = 0;
            for (int i = 1; i < tracks.Length; i++)
                if (tracks[i].PlayCount > tracks[best].PlayCount) best = i;
            return best;
        }

        /// <summary>The star's row: argmax play count when some row has plays, else -1 (agrees with
        /// <see cref="DeriveTopTrack"/>).</summary>
        public static int TopTrackIndex(ReadOnlySpan<Track> tracks)
        {
            int best = -1;
            for (int i = 0; i < tracks.Length; i++)
                if (tracks[i].PlayCount > 0 && (best < 0 || tracks[i].PlayCount > tracks[best].PlayCount)) best = i;
            return best;
        }

        /// <summary>A related album's subtitle: its artist ▸ its year ▸ its kind badge.</summary>
        public static string Subtitle(string? leadArtist, ushort year, AlbumKind kind)
            => leadArtist is { Length: > 0 } ? leadArtist
             : year > 0 ? year.ToString(CultureInfo.InvariantCulture)
             : KindBadge(kind);

        /// <summary>An other-version's menu label: "Name · Year · KIND", the year omitted when unknown.</summary>
        public static string VersionLabel(string name, ushort year, AlbumKind kind)
            => year > 0 ? name + " · " + year.ToString(CultureInfo.InvariantCulture) + " · " + KindBadge(kind)
                        : name + " · " + KindBadge(kind);

        /// <summary><c>detail.badge.album|ep|single|compilation</c>.</summary>
        public static string KindBadge(AlbumKind kind) => Detail.Text.KindLabel(kind);

        /// <summary>Does any row of a short release play a USER-ATTACHED video? A linear scan — a short release is 1-2 rows.</summary>
        public static bool HasCustomVideo(ReadOnlySpan<Track> tracks, Func<Track, bool> hasOverride)
        {
            for (int i = 0; i < tracks.Length; i++)
                if (hasOverride(tracks[i])) return true;
            return false;
        }

        /// <summary>The face pile's <c>+N</c>. FIXED (ch 05 §0.5): with billed artists it is everything not DRAWN — 0.2.9
        /// drew <c>min(4, billed)</c> faces and subtracted all of <paramref name="billed"/>, so a 6-artist billing hid two
        /// faces and counted neither. With none billed the pile draws the first four of all.</summary>
        public static int FaceOverflow(int billed, int allDistinct, int drawn)
            => billed > 0 ? Math.Max(0, allDistinct - drawn) : Math.Max(0, allDistinct - Math.Min(allDistinct, 4));

        /// <summary>Every distinct artist on the album — the billed ones first, then track-only contributors in row
        /// order — written into <paramref name="into"/>. Returns the count (capped by the span).</summary>
        public static int DistinctArtists(Album a, Span<int> into)
        {
            int n = 0;
            var billed = a.ArtistSlots;
            for (int i = 0; i < billed.Length && n < into.Length; i++) Add(into, ref n, billed[i]);
            var tracks = a.TrackSlots;
            var credits = Entities.Current.Edges.TrackArtists;
            for (int t = 0; t < tracks.Length && n < into.Length; t++)
            {
                var artists = credits.Targets(tracks[t]);
                for (int i = 0; i < artists.Length && n < into.Length; i++) Add(into, ref n, artists[i]);
            }
            return n;

            static void Add(Span<int> into, ref int n, int slot)
            {
                if (slot <= global::Wavee.Table.None || into[..n].IndexOf(slot) >= 0) return;
                into[n++] = slot;
            }
        }
    }
}
