// ── Entities/Album.cs — CORE (owner A, wave 1; plan §2 · the file's full budget is 400) ──────────────────────────────
//
// THE ALBUM COLUMNS, FLAGS, FIELD GROUPS AND HANDLE. The five ported pure rules that share this file —
// `AlbumReleaseFacts`, `PreReleaseDerivation`, `AlbumDrawerVerdict`, the notice rule and `AlbumTrailing`'s predicates —
// are owner M's, in Wave 5 (plan §2, "Two notes on the entity CORE rows").
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

using FluentGpu.Foundation;

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
    Identity = Title | Image | Year | TrackCount | Kind,

    /// <summary>The ISO release date, its precision and the parsed instant (ch 05 D2, ch 08 GAP 17). Its own bit
    /// because the card subtitle and the era bands need the DATE while the panel needs the whole publishing record.</summary>
    Release = 1 << 5,
    /// <summary>What a grid or shelf card paints (ch 08 GAP 17): name, cover, year, release date + precision, track
    /// count, kind. The discography grid gates on this GROUP, never per cell.</summary>
    Card = Identity | Release,

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
                t.SetText(ref t.Title, slot, s.Intern(row.Title));
                t.SetText(ref t.Image, slot, s.Intern(row.Image));
                t.Year[slot] = row.Year;
                t.TrackCount[slot] = row.TrackCount;
                t.ReleaseKind[slot] = row.Kind;
                t.Applied(slot, (uint)AlbumFields.Identity, auth, ref t.IdentityAuthority);
            }

            if ((known & (uint)AlbumFields.Release) != 0
                && t.Accepts(slot, (uint)AlbumFields.Release, auth, in t.ReleaseAuthority))
            {
                t.SetText(ref t.ReleaseDateIso, slot, s.Intern(row.ReleaseDateIso));
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
                t.SetText(ref t.Label, slot, s.Intern(row.Label));
                t.SetText(ref t.Copyright, slot, s.Intern(row.Copyright));
                t.SetText(ref t.Courtesy, slot, s.Intern(row.Courtesy));
                t.SetText(ref t.ShareUrl, slot, s.Intern(row.ShareUrl));
                t.DiscCount[slot] = row.DiscCount;
                t.Applied(slot, (uint)AlbumFields.Publishing, auth, ref t.PublishingAuthority);
            }

            if ((known & (uint)AlbumFields.PreReleaseLink) != 0
                && t.Accepts(slot, (uint)AlbumFields.PreReleaseLink, auth, in t.ReleaseAuthority))
            {
                t.SetText(ref t.PreReleaseUri, slot, s.Intern(row.PreReleaseUri));
                t.Applied(slot, (uint)AlbumFields.PreReleaseLink, auth, ref t.ReleaseAuthority);
            }

            // A uri cannot stop being a prerelease uri, so this only ever SETS — no mask, no authority rung, no path
            // that clears it. No `Bump`: the groups above already bumped, and a row whose only news is a bit its own
            // identity always implied is not news.
            if (uriPreRelease) t.Flags[slot] |= (uint)AlbumFlags.PreRelease;
        }
    }
}
