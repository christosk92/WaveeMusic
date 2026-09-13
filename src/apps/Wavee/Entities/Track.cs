// ── Entities/Track.cs — CORE (owner A, wave 1; plan §2, §4.2 · the file's full budget is 1,200) ───────────────────────
//
// THE COLUMNS, THE FLAGS, THE FIELD GROUPS AND THE HANDLE for a track. This is the Wave-1 half of `Track.cs`: the data
// model only. The pure rules that share the file — the lane table, the tier/relief ladders, the density and art
// ladders, the sort cycle, the filter model, `TrackExpandedFacts`, the shared number formats — are owner M's, in
// Wave 4.5 (plan §2, "Two notes on the entity CORE rows": the columns must exist before Wave 2 can decode into them).
//
// Where the shape comes from. Plan §4.2 gives the derivation from the 30-field 0.2.9 `Track` record
// (`Wavee.Core/Domain/Models.cs:285`) and the hot/cold split; ch 01 §7, ch 04 §7 and ch 05 §7 are the READERS, and
// every column below is justified by a row in one of those three tables. The four columns plan §4.2 does not list are
// each a named chapter gap, cited at the column.
//
// The two rules this file is written under, beyond the file-header set in Entities.cs:
//   P2  columns are grouped by WHO READS THEM TOGETHER. The `Identity` group is one wire shape (a TrackV4, a search
//       hit and a playlist item all fill exactly it) and it is what a list row paints; everything else is cold, arrives
//       later from a different transport, and must never make a row shimmer again once it has painted.
//   P3  no bool columns. Explicit, unavailable, local, podcast, has-video and video-override are BITS in one `uint`
//       `Flags` column, so a row's whole boolean state is one load and one mask.
//
// MEMBERSHIP METADATA IS NOT HERE (D10). `AddedAt`, `AddedBy`, `ContextUid` and `Chart` were fields on the 0.2.9
// record; they are properties of a track's membership IN A LIST, not of the track, and they live on
// `PlaylistTrackEdge` (Edges.cs). One track in two playlists has two added-at stamps and one row.

using FluentGpu.Foundation;

namespace Wavee;

// ── field groups (P3) ────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Which of a track's columns are filled (the row's <c>Known</c> bits) and, in an <c>Ensure</c> call, which a
/// page wants. The named GROUPS are the vocabulary every page and the planner speak: <c>wanted &amp; ~known</c> is the
/// whole fetch question (P3, P11).
///
/// <para><b>Identity is the hot group</b> and it is deliberately one wire shape: a TrackV4, a search hit and a playlist
/// item each fill exactly these six. <b>Row</b> is what a list row paints (ch 01 §7, ch 04 §7: a row shimmers until the
/// GROUP lands, never per cell) — Identity plus the two lanes a list surface always shows. The two late enrichments,
/// <see cref="Audio"/> (extension kind 222) and <see cref="PlayCount"/> (kind 185), ride every list surface's demand
/// bundle but render EMPTY and DASH respectively rather than shimmering, because a shimmer that resolves four seconds
/// later on 200 rows reads worse than a settled lane (ch 01 §7's readiness discipline).</para></summary>
[Flags]
public enum TrackFields : uint
{
    None = 0,

    // ── the hot group: one wire shape fills all six ──
    Title = 1 << 0,
    /// <summary>The <c>TrackArtists</c> edge AND the commit-computed <see cref="TrackTable.ArtistLine"/>.</summary>
    Artists = 1 << 1,
    Album = 1 << 2,
    Duration = 1 << 3,
    Explicit = 1 << 4,
    Image = 1 << 5,
    Identity = Title | Artists | Album | Duration | Explicit | Image,

    // ── cold groups: separate transports, separate arrival times ──
    /// <summary>Extension kind 185. Rides every list surface's bundle (ch 04 §7: the Plays toggle must not change the
    /// demand, or it shows a column of dashes).</summary>
    PlayCount = 1 << 8,
    Year = 1 << 9,
    /// <summary>The availability VERDICT. <c>Knows(Availability)</c> is the "somebody ruled on this track" bit that
    /// 0.2.9 spelled as a nullable (ch 04 DATA GAPS): an unruled row is never dimmed and never hidden by
    /// <c>PlayableOnly</c>.</summary>
    Availability = 1 << 10,
    Isrc = 1 << 11,
    Canonical = 1 << 12,
    /// <summary>Extension kind 222: tempo, key, camelot code and its swatch colour.</summary>
    Audio = 1 << 13,
    /// <summary>Extension kind 6 — the descriptor chips (<c>Edges.TrackTags</c>).</summary>
    Tags = 1 << 14,
    /// <summary>Extension kind 99 plus the user's own override: the film lane, the counterpart slot and its thumbnail.</summary>
    Video = 1 << 15,
    Publishing = 1 << 16,
    /// <summary>Extension kind 5 (<c>AUDIO_FILES</c>) on the DERIVED <c>spotify:audio:</c> entity: the format ladder
    /// (<c>Edges.TrackFormats</c>) and the two lossless bits (FLAC plan §5.1/§5.2). It is the only route that carries
    /// FLAC rows — <c>TRACK_V4</c>'s own <c>file[]</c> lists Ogg and AAC and never a lossless one — and the answer is
    /// per ACCOUNT and per market, which is why "is this track available lossless" is a catalogue fact here and not a
    /// flag on the session (there is none: librespot's <c>connect.proto</c> only RESERVES one).
    /// <para>It needs <see cref="TrackTable.OriginalAudio"/> first — the planner asks for Files only on rows whose
    /// audio key is non-zero, and a row whose TrackV4 named none is answered EMPTY at commit and never asked.</para>
    /// <para>Deliberately NOT in <see cref="Row"/>: the drawer fetches it on expand and a play asks
    /// <c>Spotify.Audio.Open</c>, which fetches it itself. A 300-row playlist would otherwise be 300 audio-uri POSTs
    /// for a bit nobody paints in a row (plan §5.2).</para></summary>
    Files = 1 << 17,

    /// <summary>What a list row paints (ch 01 §7, ch 04 §7). A page demands this for its WHOLE model on mount.</summary>
    Row = Identity | PlayCount | Availability,
    All = Identity | PlayCount | Year | Availability | Isrc | Canonical | Audio | Tags | Video | Publishing | Files,
}

/// <summary>The boolean state of a track, as bits in one <c>uint</c> column (P3). Each bit belongs to exactly one field
/// group, and a commit writes only its own group's bits — see the three masks at the bottom, which is what lets a
/// kind-99 answer set <see cref="HasVideo"/> without touching the availability verdict.</summary>
[Flags]
public enum TrackFlags : uint
{
    None = 0,

    // Identity group
    Explicit = 1 << 0,
    /// <summary>An imported local file (<c>wavee:local:file:…</c>) — the row has no catalogue behind it.</summary>
    Local = 1 << 1,
    /// <summary>The row is an episode rendered as a track (<c>EpisodeAsTrack</c>): no artists, the SHOW in the album
    /// slot, no drawer chevron (ch 04 DATA GAPS, "Episodes inside a playlist").</summary>
    Podcast = 1 << 2,

    // Availability group
    /// <summary>A CONFIRMED unavailable. Only meaningful once <see cref="TrackFields.Availability"/> is known — an
    /// unruled row must read as playable, not as blocked (ch 04 DATA GAPS).</summary>
    Unavailable = 1 << 8,

    // Video group
    /// <summary>The catalogue says there is a music video (extension kind 99).</summary>
    HasVideo = 1 << 16,
    /// <summary>The USER attached a local mp4 (ch 01 GAP 7, ch 04's "user-attached local video override", ch 05 D11).
    /// Its own bit, so <c>Track.HasVideo</c> stays ONE ordinal probe over the OR of both planes.</summary>
    VideoOverride = 1 << 17,

    // Files group (FLAC plan §5.2). The ladder itself is `Edges.TrackFormats`; these two are the cheap read every
    // surface that only wants a BADGE takes — one load and one mask, no edge walk, for a versions row or a list lane.
    /// <summary>The catalogue offered a 16-bit FLAC for this account (<c>FLAC_FLAC = 16</c>).</summary>
    Lossless = 1 << 24,
    /// <summary>…and a 24-bit one (<c>FLAC_FLAC_24BIT = 22</c>).</summary>
    Lossless24 = 1 << 25,

    IdentityMask = Explicit | Local | Podcast,
    AvailabilityMask = Unavailable,
    VideoMask = HasVideo | VideoOverride,
    FilesMask = Lossless | Lossless24,
}

// ── the table ────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Every track in one scope, as columns. Hot columns first: a list row's paint touches <see cref="Title"/>,
/// <see cref="ArtistLine"/>, <see cref="Image"/>, <see cref="DurationMs"/> and <see cref="Flags"/> and nothing else, so
/// those five slabs are what stays in cache while a 200-row page scrolls (P2).</summary>
public sealed class TrackTable : Table
{
    // ── hot (TrackFields.Identity) ──
    public Column<StringId> Title, Image;
    /// <summary>The credit line as ONE interned string, computed at COMMIT and never per frame (ch 01 GAP 2, ch 02 §7's
    /// row subtitle, P11). It is for MEASURE AND PAINT only: a single string cannot carry N click targets, so the row
    /// builds its per-artist spans from <c>Edges.TrackArtists</c> — ch 04 DATA GAPS corrects plan §4.12 on exactly
    /// this point.</summary>
    public Column<StringId> ArtistLine;
    /// <summary>The album's SLOT (0 = none). For a <see cref="TrackFlags.Podcast"/> row this is the SHOW's slot — the
    /// "go to the container" lane resolves the right table off the uri's kind (ch 04 DATA GAPS).</summary>
    public Column<int> Album;
    public Column<int> DurationMs;
    /// <summary><see cref="TrackFlags"/>.</summary>
    public Column<uint> Flags;

    // ── cold ──
    public Column<uint> PlayCount;
    /// <summary>The camelot swatch's wire colour (ch 01 §7's BPM · key · swatch cell). Opaque ARGB; the light-mode ink
    /// derivation is <c>Design.Palette.DataDotInk</c>, pure, in <c>Platform/Design.cs</c> (ch 01 GAP 9).</summary>
    public Column<uint> CamelotColor;
    public Column<ushort> Year;
    /// <summary>Tempo ×10, so 128.4 BPM is 1284 in two bytes (plan §4.2).</summary>
    public Column<ushort> Tempo;
    /// <summary>The 12-slot musical-key ring and the camelot wheel position. Both rings are closed, so
    /// <c>KeyLabel(byte)</c> / <c>CamelotLabel(byte)</c> are exact static tables in owner M's rule section rather than
    /// strings on the wire (ch 04 DATA GAPS, "Camelot code + key NAME").</summary>
    public Column<byte> Key, Camelot;
    /// <summary>When an unreleased row becomes playable (ch 01 §7's not-yet-out dim + date). Wall-clock gated by the
    /// reader, so it expires itself with no refetch (ch 05 §7).</summary>
    public Column<int> AvailableAt;
    /// <summary>The canonical track this row duplicates (0 = it is canonical).</summary>
    public Column<int> Canonical;
    /// <summary>The music-video counterpart's own track slot (0 = none).</summary>
    public Column<int> VideoCounterpart;
    /// <summary>The counterpart's 16:9 thumbnail — a SECOND image per track, not a reuse of <see cref="Image"/>
    /// (ch 02 GAP-6).</summary>
    public Column<StringId> VideoImage;
    /// <summary>The user's own attached mp4 path (ch 01 GAP 7, ch 04 DATA GAPS). Written at
    /// <see cref="Authority.Local"/>, so no catalogue answer can clear it.</summary>
    public Column<StringId> LocalVideo;
    public Column<StringId> Isrc;
    /// <summary><c>Track.original_audio.uuid</c> (metadata.proto field 24), packed; 0 = the wire gave none (a local, a
    /// podcast, a very old row). THE key to the <c>spotify:audio:&lt;base62(uuid)&gt;</c> entity and therefore the only
    /// route to <see cref="TrackFields.Files"/> — the format ladder and the lossless bits (FLAC plan §5.2).
    /// <para>16 B/row, cold, and the plan pays for it deliberately: the uuid is on no other payload, it is not
    /// derivable from the track's own gid, and without a column the app would have to re-fetch TrackV4 per track to
    /// ask one question about it. It is the one column that moved the footprint gate (FootprintGateTests).</para></summary>
    public Column<UInt128> OriginalAudio;

    // ── authority, per column GROUP (D16) ──
    public Column<byte> IdentityAuthority, ExtrasAuthority;

    public override EntityKind Kind => EntityKind.Track;

    protected override void GrowColumns(int capacity)
    {
        Title.EnsureCapacity(capacity);
        Image.EnsureCapacity(capacity);
        ArtistLine.EnsureCapacity(capacity);
        Album.EnsureCapacity(capacity);
        DurationMs.EnsureCapacity(capacity);
        Flags.EnsureCapacity(capacity);
        PlayCount.EnsureCapacity(capacity);
        CamelotColor.EnsureCapacity(capacity);
        Year.EnsureCapacity(capacity);
        Tempo.EnsureCapacity(capacity);
        Key.EnsureCapacity(capacity);
        Camelot.EnsureCapacity(capacity);
        AvailableAt.EnsureCapacity(capacity);
        Canonical.EnsureCapacity(capacity);
        VideoCounterpart.EnsureCapacity(capacity);
        VideoImage.EnsureCapacity(capacity);
        LocalVideo.EnsureCapacity(capacity);
        Isrc.EnsureCapacity(capacity);
        OriginalAudio.EnsureCapacity(capacity);
        IdentityAuthority.EnsureCapacity(capacity);
        ExtrasAuthority.EnsureCapacity(capacity);
    }

    /// <summary>Give back every string a track row owns (defect 1; the REF-COUNTING block on <see cref="Table"/>). One
    /// line per <c>Column&lt;StringId&gt;</c> declared above, and the pair to the <see cref="Table.SetText"/> the commit
    /// writes through: an id nobody releases is PERMANENT in the engine's interner
    /// (<c>StringTable.cs:26</c>), so before this override the store's trim freed a track's 89 B of columns and left
    /// its title, cover, credit line, isrc and both video strings behind for the life of the process (doc §4.4).
    /// <see cref="Table.FreeSlot"/> and <see cref="Table.ReleaseAllText"/> call it.</summary>
    protected override void ReleaseText(int slot)
    {
        ClearText(ref Title, slot);
        ClearText(ref Image, slot);
        ClearText(ref ArtistLine, slot);
        ClearText(ref VideoImage, slot);
        ClearText(ref LocalVideo, slot);
        ClearText(ref Isrc, slot);
        // NOT a string, and here on purpose: this hook is the row's RETIREMENT (`FreeSlot`, `ReleaseAllText`), and the
        // audio key is the one column the planner reads WITHOUT a `Known` gate — it is what it uses to decide whether a
        // row can be asked for its format ladder at all (Fetch.cs). A recycled slot that kept the previous tenant's key
        // would send the PREVIOUS track's `spotify:audio:` uri and commit that track's ladder onto this row.
        OriginalAudio[slot] = UInt128.Zero;
    }
}

// ── the handle ───────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>A track: ONE <c>int</c> (D5/D13). Every property is a column index off the current scope's table, so a
/// handle is free to copy, free to put in a span, and stale by construction the moment the scope switches — which is
/// why a page compares <see cref="Version"/> rather than caching values.
///
/// <para><b>It must stay exactly four bytes wide.</b> <c>Entities.Slots</c> reinterprets a
/// <c>ReadOnlySpan&lt;Track&gt;</c> as a <c>ReadOnlySpan&lt;int&gt;</c> with no copy; a second field would silently
/// break every typed <c>Ensure</c>. Partial because <c>Track.UI.cs</c> (Wave 4.5) adds the row's pure readers.</para></summary>
public readonly partial struct Track(int slot) : IEquatable<Track>
{
    static TrackTable T => Entities.Current.Tracks;

    public int Slot { get; } = slot;

    /// <summary>A handle that addresses a live row. Slot 0 is the permanent "none" row in every table.</summary>
    public bool IsValid => Slot > Table.None && Slot < T.Count;

    /// <summary>Bumps on every write to this row (D8). Comparing it across frames costs 1 (P5).</summary>
    public uint Version => T.Version[Slot];

    /// <summary>Does the row carry EVERY bit of <paramref name="fields"/>? The question a page asks before it paints
    /// (P3) — never a null check, because there are no nulls in a column.</summary>
    public bool Knows(TrackFields fields) => (T.Known[Slot] & (uint)fields) == (uint)fields;

    /// <summary>THE row's identity, packed: kind, provider, form and the 128-bit payload in 24 bytes (§2 of
    /// Entities.cs). This is what a queue row, a route subject, a pin or a cross-kind list carries — never the slot,
    /// which is only meaningful beside its own table, and never the uri text, which cost 158 B/row to keep
    /// (doc §1.2).</summary>
    public EntityId Id => T.Id[Slot];

    /// <summary>The identity as a URI view — a field load and a struct wrap, no parse. Reading <c>.Uri.Kind</c> or
    /// <c>.Uri.Provider</c> re-resolved the interned string and re-walked its prefix on EVERY read before 2026-09-12
    /// (<c>EntityUri.Of</c>, 45-100 ns, doc §1.3 item 3 — defect 3); both are now bytes of
    /// <see cref="Id"/>.</summary>
    public EntityUri Uri => new(T.Id[Slot]);

    // ── identity ──
    public StringId TitleId => T.Title[Slot];
    /// <summary>Resolving is two array indexes, but it IS a string: never per row per frame — bind the id.</summary>
    public string Title => Entities.Strings.Resolve(T.Title[Slot]);
    /// <summary>The precomputed credit line (measure/paint only — see <see cref="TrackTable.ArtistLine"/>).</summary>
    public StringId ArtistLineId => T.ArtistLine[Slot];
    public StringId ImageId => T.Image[Slot];
    /// <summary>The album, or the SHOW for a <see cref="TrackFlags.Podcast"/> row (ch 04).</summary>
    public Album Album => new(T.Album[Slot]);
    public int AlbumSlot => T.Album[Slot];
    public int DurationMs => T.DurationMs[Slot];

    // ── flags (P3: one load, one mask) ──
    public uint FlagBits => T.Flags[Slot];
    public bool IsExplicit => (T.Flags[Slot] & (uint)TrackFlags.Explicit) != 0;
    public bool IsLocal => (T.Flags[Slot] & (uint)TrackFlags.Local) != 0;
    public bool IsPodcast => (T.Flags[Slot] & (uint)TrackFlags.Podcast) != 0;
    /// <summary>Playable unless somebody RULED it unavailable. An unruled row is playable (ch 04 DATA GAPS).</summary>
    public bool IsPlayable => (T.Flags[Slot] & (uint)TrackFlags.Unavailable) == 0;
    /// <summary>The catalogue's association OR the user's own mp4, in one probe. The THIRD plane — a module playable's
    /// form probe — is owner L's <c>Platform/Modules.cs</c> dictionary and folds in at the same call site when
    /// <c>VideoOverrideUx.HasVideo</c> is ported (ch 01 GAP 8).</summary>
    public bool HasVideo => (T.Flags[Slot] & (uint)TrackFlags.VideoMask) != 0;
    /// <summary>The catalogue offered this account a FLAC of this track (16- or 24-bit). Only meaningful once
    /// <see cref="TrackFields.Files"/> is known — an unasked row reads as false, which is the honest default: a badge
    /// nobody has earned is not painted (FLAC plan §5.2, "one load and one mask for any surface that wants a badge,
    /// no edge walk").</summary>
    public bool IsLossless => (T.Flags[Slot] & (uint)TrackFlags.FilesMask) != 0;
    /// <summary>…and a 24-bit one specifically — the "FLAC 24/44.1" rung the versions row prints (plan §5.3).</summary>
    public bool IsLossless24 => (T.Flags[Slot] & (uint)TrackFlags.Lossless24) != 0;

    // ── cold ──
    public uint PlayCount => T.PlayCount[Slot];
    public ushort Year => T.Year[Slot];
    public int AvailableAt => T.AvailableAt[Slot];
    public StringId IsrcId => T.Isrc[Slot];
    public Track Canonical => new(T.Canonical[Slot]);
    public Track VideoCounterpart => new(T.VideoCounterpart[Slot]);
    public StringId VideoImageId => T.VideoImage[Slot];
    public StringId LocalVideoId => T.LocalVideo[Slot];
    /// <summary>Tempo ×10 (1284 = 128.4 BPM).</summary>
    public ushort Tempo => T.Tempo[Slot];
    public byte Key => T.Key[Slot];
    public byte Camelot => T.Camelot[Slot];
    public uint CamelotColor => T.CamelotColor[Slot];
    /// <inheritdoc cref="TrackTable.OriginalAudio"/>
    public UInt128 OriginalAudio => T.OriginalAudio[Slot];

    // ── edges (CSR spans: zero allocation, and never held across a UI drain) ──
    public ReadOnlySpan<int> ArtistSlots => Entities.Current.Edges.TrackArtists.Targets(Slot);
    /// <summary>The descriptor chips (extension kind 6). The payload IS the tag id; targets are unused.</summary>
    public ReadOnlySpan<StringId> Tags => Entities.Current.Edges.TrackTags.Payload(Slot);
    /// <summary>"Fans also like" for a SHORT release, which seeds off the track rather than the artist (ch 05 D7).</summary>
    public ReadOnlySpan<int> RelatedArtistSlots => Entities.Current.Edges.TrackRelatedArtists.Targets(Slot);
    /// <summary>The format ladder this account was offered for this track, in WIRE order (extension kind 5). The
    /// payload IS the rung; targets are unused. Empty until <see cref="Knows"/> says <see cref="TrackFields.Files"/> —
    /// and legitimately empty afterwards, because "the catalogue offered nothing" is an answer (plan §5.3's drawer
    /// sorts it by <c>Kbps</c> descending; it never holds this span across a drain, the SPAN RULE in Edges.cs).</summary>
    public ReadOnlySpan<FormatEdge> Formats => Entities.Current.Edges.TrackFormats.Payload(Slot);

    public bool Equals(Track other) => other.Slot == Slot;
    public override bool Equals(object? obj) => obj is Track other && other.Slot == Slot;
    public override int GetHashCode() => Slot;
    public static bool operator ==(Track a, Track b) => a.Slot == b.Slot;
    public static bool operator !=(Track a, Track b) => a.Slot != b.Slot;
}

// ── the edges this kind adds (ch 05 D7) ──────────────────────────────────────────────────────────────────────────────

public sealed partial class Edges
{
    /// <summary>"Fans also like" seeded by a TRACK — the arm a short release takes instead of the artist's own related
    /// artists (ch 05 D7). Parent = track slot, targets = artist slots.</summary>
    public readonly EdgeTable<NoEdge> TrackRelatedArtists = new();
}

// ── staging: what a decode hands the drain ───────────────────────────────────────────────────────────────────────────

/// <summary>One decoded track, as the wire gave it: TEXT IS <see cref="TextRef"/>, not <see cref="StringId"/>, because
/// the decoder runs on the socket's thread and interning is the UI thread's alone (C1, §5.6). <see cref="Known"/> says
/// which GROUPS this row actually speaks for — a search hit sets <c>Identity</c> and nothing else — and
/// <see cref="Authority"/> lets one batch carry rows of different rungs (a full track beside its thin duplicates).</summary>
public struct StagedTrack : IStagedRow
{
    /// <summary>THE row's identity as the wire gave it (<see cref="StagedId"/>): the packed <see cref="EntityId"/> when
    /// it arrived as 16 gid bytes, the uri's UTF-8 in the arena when it arrived as text. One field, one resolve —
    /// <c>s.Slot(table, in row.Id)</c> — and no format-to-arena-then-parse-back round trip.</summary>
    public StagedId Id;
    public TextRef Title, Image, ArtistLine, Isrc;
    /// <summary>Uris of the rows this one POINTS at. The commit resolves each to a slot, allocating an empty row when
    /// it has never been seen — which is exactly how a track's album becomes bindable before anyone fetched it.</summary>
    public StagedId AlbumUri, CanonicalUri, VideoUri;
    /// <summary>The counterpart's still and the user's own mp4 — TEXT, not identities: neither is a row.</summary>
    public TextRef VideoImage, LocalVideo;
    public int DurationMs, AvailableAt;
    /// <inheritdoc cref="TrackTable.OriginalAudio"/>
    public UInt128 OriginalAudio;
    public uint PlayCount, CamelotColor;
    /// <summary>The row's <see cref="TrackFlags"/>. Only the bits of the groups named in <see cref="Known"/> are read.</summary>
    public uint Flags;
    public ushort Year, Tempo;
    public byte Key, Camelot;
    /// <summary><see cref="TrackFields"/>: which groups this row speaks for.</summary>
    public uint Known;
    public Authority Authority;

    /// <inheritdoc cref="IStagedRow.Init"/>
    public void Init(in StagedId id, Authority authority, uint known) { Id = id; Authority = authority; Known = known; }
    /// <inheritdoc cref="IStagedRow.Identity"/>
    public readonly StagedId Identity => Id;
}

public sealed partial class Staging
{
    StagedList<StagedTrack>? _tracks;

    /// <summary>The decoded track rows. Lazy, so a decode that touches only albums allocates no track list at all.</summary>
    public StagedList<StagedTrack> Tracks => _tracks ??= Register(new StagedList<StagedTrack>());

    /// <summary>The list WITHOUT creating it — what the commit asks, so a batch with no tracks costs one null test.</summary>
    internal StagedList<StagedTrack>? TracksOrNull => _tracks;
}

// ── the batch API and the commit ─────────────────────────────────────────────────────────────────────────────────────

public static partial class Entities
{
    /// <summary>"These rows, these groups, this urgency" (P4). The typed sugar over
    /// <see cref="Ensure(Table,ReadOnlySpan{int},uint,FetchPriority)"/>: the handle span is reinterpreted as slots with
    /// no copy, because a handle is exactly one <c>int</c> wide.
    /// <para>A page calls this ONCE on mount for its WHOLE model — never for a visible range. The query layer pages it
    /// at 300 uris per POST (ch 01 §7, ch 04 §7: "no page-side fetch windows").</para></summary>
    public static void Ensure(ReadOnlySpan<Track> rows, TrackFields wanted, FetchPriority priority = FetchPriority.Visible)
        => Ensure(Current.Tracks, Slots(rows), (uint)wanted, priority);

    /// <inheritdoc cref="Ensure(ReadOnlySpan{Track},TrackFields,FetchPriority)"/>
    public static void Ensure(Track row, TrackFields wanted, FetchPriority priority = FetchPriority.Visible)
    {
        Span<int> one = stackalloc int[1];
        one[0] = row.Slot;
        Ensure(Current.Tracks, one, (uint)wanted, priority);
    }

    /// <summary>Copy a batch of decoded tracks into the live columns. UI thread, inside the drain (C1).
    ///
    /// <para>The shape is the same for every kind and every group: resolve the uri to a slot, ask
    /// <see cref="Table.Accepts(int,uint,Authority,in Column{byte})"/> whether this authority may write the group,
    /// write the group's columns, then <see cref="Table.Applied"/>. A refused group leaves the row untouched and costs
    /// one compare — that is D16 in one line: a Thin answer never overwrites a Full one, but it does fill a hole.</para></summary>
    /// <summary>Albums whose star may have moved in this batch. Static and reused, so a steady stream of kind-185
    /// answers allocates nothing after warm-up (P8); cleared at the top of every commit, never held across one.</summary>
    static readonly HashSet<int> s_starDirty = [];

    static partial void CommitTracks(Staging s)
    {
        var staged = s.TracksOrNull;
        if (staged is null || staged.Count == 0) return;

        var t = Current.Tracks;
        var rows = staged.Span;
        t.EnsureCapacity(t.Count + rows.Length);           // one capacity probe for the whole batch (P4)
        s_starDirty.Clear();

        for (int i = 0; i < rows.Length; i++)
        {
            ref var row = ref rows[i];
            int slot = s.Slot(t, in row.Id);
            if (slot == Table.None) continue;   // a row with no identity is not a row
            var auth = row.Authority;
            uint known = row.Known;

            // THE KEY TO THE AUDIO ENTITY (§5.2), written by whoever CARRIES it and never cleared by anyone who does
            // not. It is not a rendered value and no answer disagrees with another about it — `Track.original_audio`
            // rides the TrackV4 payload alone — so it needs no authority gate; what it must never do is let a search
            // hit or a playlist item blank the key a full answer already learned. Filling the column is NOT the same
            // as knowing `Files`: this is the key to the ladder, the ladder is kind 5's answer.
            if (row.OriginalAudio != UInt128.Zero) t.OriginalAudio[slot] = row.OriginalAudio;

            if ((known & (uint)TrackFields.Identity) != 0
                && t.Accepts(slot, (uint)TrackFields.Identity, auth, in t.IdentityAuthority))
            {
                // Every text write goes through `SetText`, which AddRefs the incoming id and releases the one it
                // overwrites (defect 1). A row re-answered by a search hit, then by TrackV4, then by a playlist item
                // would otherwise leak two titles, two credit lines and two covers into the interner — and the
                // OVERWRITE half is the one that leaks SILENTLY, because nothing about the row looks wrong afterwards.
                t.SetText(ref t.Title, slot, s.Intern(row.Title));
                t.SetText(ref t.ArtistLine, slot, s.Intern(row.ArtistLine));
                t.SetText(ref t.Image, slot, s.Intern(row.Image));
                t.DurationMs[slot] = row.DurationMs;
                if (!row.AlbumUri.IsEmpty) t.Album[slot] = s.Slot(Current.Albums, in row.AlbumUri);
                t.Flags[slot] = (t.Flags[slot] & ~(uint)TrackFlags.IdentityMask)
                              | (row.Flags & (uint)TrackFlags.IdentityMask);
                t.Applied(slot, (uint)TrackFields.Identity, auth, ref t.IdentityAuthority);
            }

            // The cold groups share ONE authority column (plan §4.2's `ExtrasAuthority`): they arrive from different
            // transports but never from two rungs of the same one, and `Accepts` lets any of them fill a hole.
            uint extras = known & ~(uint)TrackFields.Identity;
            if (extras == 0) continue;

            if ((extras & (uint)TrackFields.PlayCount) != 0
                && t.Accepts(slot, (uint)TrackFields.PlayCount, auth, in t.ExtrasAuthority))
            {
                t.PlayCount[slot] = row.PlayCount;
                t.Applied(slot, (uint)TrackFields.PlayCount, auth, ref t.ExtrasAuthority);
                // The album's top-track star is a DERIVED fact on the model (ch 04 §7), and a play count is the only
                // thing that can move it. Deferred to one pass per ALBUM after the loop: a 300-row album batch would
                // otherwise re-scan the same tracklist 300 times.
                if (t.Album[slot] > Table.None) s_starDirty.Add(t.Album[slot]);
            }
            if ((extras & (uint)TrackFields.Year) != 0
                && t.Accepts(slot, (uint)TrackFields.Year, auth, in t.ExtrasAuthority))
            {
                t.Year[slot] = row.Year;
                t.Applied(slot, (uint)TrackFields.Year, auth, ref t.ExtrasAuthority);
            }
            if ((extras & (uint)TrackFields.Availability) != 0
                && t.Accepts(slot, (uint)TrackFields.Availability, auth, in t.ExtrasAuthority))
            {
                t.AvailableAt[slot] = row.AvailableAt;
                t.Flags[slot] = (t.Flags[slot] & ~(uint)TrackFlags.AvailabilityMask)
                              | (row.Flags & (uint)TrackFlags.AvailabilityMask);
                t.Applied(slot, (uint)TrackFields.Availability, auth, ref t.ExtrasAuthority);
            }
            if ((extras & (uint)TrackFields.Isrc) != 0
                && t.Accepts(slot, (uint)TrackFields.Isrc, auth, in t.ExtrasAuthority))
            {
                t.SetText(ref t.Isrc, slot, s.Intern(row.Isrc));
                t.Applied(slot, (uint)TrackFields.Isrc, auth, ref t.ExtrasAuthority);
            }
            if ((extras & (uint)TrackFields.Canonical) != 0
                && t.Accepts(slot, (uint)TrackFields.Canonical, auth, in t.ExtrasAuthority))
            {
                if (!row.CanonicalUri.IsEmpty) t.Canonical[slot] = s.Slot(t, in row.CanonicalUri);
                t.Applied(slot, (uint)TrackFields.Canonical, auth, ref t.ExtrasAuthority);
            }
            if ((extras & (uint)TrackFields.Audio) != 0
                && t.Accepts(slot, (uint)TrackFields.Audio, auth, in t.ExtrasAuthority))
            {
                t.Tempo[slot] = row.Tempo;
                t.Key[slot] = row.Key;
                t.Camelot[slot] = row.Camelot;
                t.CamelotColor[slot] = row.CamelotColor;
                t.Applied(slot, (uint)TrackFields.Audio, auth, ref t.ExtrasAuthority);
            }
            if ((extras & (uint)TrackFields.Video) != 0
                && t.Accepts(slot, (uint)TrackFields.Video, auth, in t.ExtrasAuthority))
            {
                if (!row.VideoUri.IsEmpty) t.VideoCounterpart[slot] = s.Slot(t, in row.VideoUri);
                t.SetText(ref t.VideoImage, slot, s.Intern(row.VideoImage));
                // The user's own mp4 is written at Local authority by the curation store, so a catalogue answer that
                // carries no override must not clear one (ch 01 GAP 7): keep the override bit unless this row states it.
                uint videoBits = row.Flags & (uint)TrackFlags.VideoMask;
                if (row.LocalVideo.IsEmpty) videoBits |= t.Flags[slot] & (uint)TrackFlags.VideoOverride;
                else t.SetText(ref t.LocalVideo, slot, s.Intern(row.LocalVideo));
                t.Flags[slot] = (t.Flags[slot] & ~(uint)TrackFlags.VideoMask) | videoBits;
                t.Applied(slot, (uint)TrackFields.Video, auth, ref t.ExtrasAuthority);
            }
            if ((extras & (uint)TrackFields.Tags) != 0
                && t.Accepts(slot, (uint)TrackFields.Tags, auth, in t.ExtrasAuthority))
            {
                // The chips themselves are the `TrackTags` RUN's (the payload IS the tag id, Edges.cs) and they land in
                // `CommitEdges`; this arm is the group's own KNOWN bit, and it is what stops the planner asking kind 6
                // again. An answer with NO descriptors is a real answer — "this track has none" — so the bit is set
                // whether the run was empty or not (finding 27). Without it a descriptor-less track was re-requested on
                // every page mount for the life of the session.
                t.Applied(slot, (uint)TrackFields.Tags, auth, ref t.ExtrasAuthority);
            }
            if ((extras & (uint)TrackFields.Publishing) != 0
                && t.Accepts(slot, (uint)TrackFields.Publishing, auth, in t.ExtrasAuthority))
            {
                // Publishing text (℗ / ©) is the ALBUM's, not the track's (ch 05 D2). The bit records that the track
                // payload spoke for it — so the planner stops asking — and the columns live on `AlbumTable`.
                t.Applied(slot, (uint)TrackFields.Publishing, auth, ref t.ExtrasAuthority);
            }
            if ((extras & (uint)TrackFields.Files) != 0
                && t.Accepts(slot, (uint)TrackFields.Files, auth, in t.ExtrasAuthority))
            {
                // The ladder's RUNGS land in `CommitEdges` as the `TrackFormats` run (the payload IS the rung,
                // Edges.cs); this arm is the two badge bits and the group's KNOWN bit, which is what stops the planner
                // asking kind 5 again. Two answers set it and both are real:
                //   · kind 5 itself — with FLAC rows, or with none, because an account without lossless in this market
                //     gets the same Ogg/AAC ladder and that IS the answer (plan §5.1, finding 27's rule for kind 6);
                //   · a TrackV4 that named no `original_audio` — there is no audio entity, so there is no ladder to
                //     fetch, ever. Without that seal the planner would re-derive the same impossibility on every page
                //     mount for the life of the session (P3).
                t.Flags[slot] = (t.Flags[slot] & ~(uint)TrackFlags.FilesMask)
                              | (row.Flags & (uint)TrackFlags.FilesMask);
                t.Applied(slot, (uint)TrackFields.Files, auth, ref t.ExtrasAuthority);
            }
        }

        if (s_starDirty.Count == 0) return;
        foreach (int album in s_starDirty) global::Wavee.Album.DeriveTopTrack(album);   // qualified: inside `Entities`, `Album` binds to the factory METHOD, not the handle type
        s_starDirty.Clear();
    }
}
