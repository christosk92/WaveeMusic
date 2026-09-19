// ── Entities/Track.cs ──────────────────────────────────────────────────────────────────────────────────────────────
// the columns, the flags, the field groups, the handle, the staging row and the commit for a track
//
// Role: CORE
// Owner: A (Wave 1: the data model) · M (Wave 4.5: the rules, in the named partial `Track.Rules.cs`)
// Wave: 1 · 4.5
// Budget: 1,200 lines — the columns alone fill half of it, so the rules live in `Track.Rules.cs` (a >30 % partial)
// Spec: plan §2, §4.2 · ch 01 §7-§9, ch 04 §7-§8, ch 05 §7
//
// THE COLUMNS, THE FLAGS, THE FIELD GROUPS AND THE HANDLE for a track. This is the Wave-1 half of the track CORE: the
// data model only. The pure rules of the surface — the lane table, the tier/relief ladders, the density and art ladders,
// the sort cycle, the command-bar fit, the filter model, the expanded-row facts, the shared number formats (incl. the
// exact `KeyLabel(byte)` / `CamelotLabel(byte)` tables), the membership diff, the reorder rules and the right-click
// target resolver — are owner M's, Wave 4.5, in `Entities/Track.Rules.cs` (plan §2, "Two notes on the entity CORE rows":
// the columns must exist before Wave 2 can decode into them).
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
    /// <summary>What a list row must know before it paints as REAL (title, album, duration, explicit, image): Identity
    /// minus <see cref="Artists"/>, because the credit line rides the <c>TrackArtists</c> edge and a disk-restored row
    /// withholds that bit until the edge re-lands (<c>TrackShape.PersistedIdentity</c>) — a warm open must reveal at
    /// once and let the credits fill in place. The table's reveal gate and the row's own shimmer/real decision read this
    /// group and nothing narrower.</summary>
    Face = Title | Album | Duration | Explicit | Image,

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
    /// <summary>SUPERSEDED (plan §3.1, ledger row 12): the "episode rendered as a Track row, SHOW in the album slot"
    /// design (ch 04 DATA GAPS, "Episodes inside a playlist") was never wired to the real playlist decode, and as of
    /// A3's follow-up the fake-data seed (<c>Entities.Fake.Library.cs</c>'s X5) no longer writes it either — nothing
    /// commits this bit AT ALL any more. An episode playlist member now resolves into <c>Current.Episodes</c>
    /// directly, via the edge's own <see cref="PlaylistItemKind"/> (<c>Edges.Staging.cs</c>'s
    /// <c>Relation.PlaylistTracks</c> per-row dispatch), and <see cref="Playlist.Refold"/> reads THAT, not this flag.
    /// Left defined — and <see cref="TrackTable.Album"/>'s SHOW-in-the-album-slot reading with it — only because it
    /// is still READ outside this owner's files (<c>Track.UI.cs</c>, <c>Track.Menu.cs</c>, <c>Lyrics.UI.cs</c>,
    /// the playback host's <c>Playback.Host.Wire.cs</c>); a future pass that retires those readers should retire
    /// this bit with them.</summary>
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
    /// <summary>The video source is a LIVE broadcast (ch 24 DATA GAP 1, G-058): the LIVE chip, Go live and the DVR rail,
    /// and a re-watch that opens live-shaped before the manifest lands. Written by the VIDEO HOST when the resolving
    /// source says so — never by a catalogue answer, which is why it is outside <see cref="VideoMask"/> and survives a
    /// kind-99 commit. The default is "finite": a wrong guess the other way rendered a six-hour broadcast as 0:03.</summary>
    LiveVideo = 1 << 18,

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
    /// <summary>The album's SLOT (0 = none). The <see cref="TrackFlags.Podcast"/> repurposing this once documented
    /// (SHOW's slot here) is superseded for playlist membership — see that flag's own summary — and unused by any
    /// live decode; kept only for the readers outside this owner's files that still branch on the flag.</summary>
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
    /// <summary>THE MANIFEST ID (G-056): <c>Track.original_video[0].gid</c> (metadata.proto field 38) as 32 lowercase hex
    /// characters — what <c>/manifests/v9/json/sources/{id}</c> is addressed by. Set for a SELF-CONTAINED music-video
    /// track; empty for a linked-uri one, whose manifest id is its video counterpart's own (the resolver's second tier:
    /// own gid → <see cref="VideoCounterpart"/>'s gid → a TrackV4 read of the counterpart). Like
    /// <see cref="OriginalAudio"/> it is a KEY, not a rendered value: written by whoever carries it, never cleared by
    /// an answer that does not, and needing no authority gate. 4 B/row.</summary>
    public Column<StringId> VideoGid;
    /// <summary>The music video's natural size in pixels (G-058, ch 24 DATA GAP 1): the largest rendition kind 99 names.
    /// 0 = unknown, and the cap then seeds 16:9 exactly as before — read FIRST by the docked cap and the PiP fit so a
    /// 4:3 or vertical video does not open at the wrong shape and refit. Two bytes each, under
    /// <see cref="TrackFields.Video"/>.</summary>
    public Column<ushort> VideoW, VideoH;

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
        VideoGid.EnsureCapacity(capacity);
        VideoW.EnsureCapacity(capacity);
        VideoH.EnsureCapacity(capacity);
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
        ClearText(ref VideoGid, slot);
        VideoW[slot] = 0;
        VideoH[slot] = 0;
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
    /// <remarks>Qualified: the static <c>Track.Table(...)</c> factory (Track.Table.cs) hides the base type by name inside
    /// this struct.</remarks>
    public bool IsValid => Slot > global::Wavee.Table.None && Slot < T.Count;

    /// <summary>Bumps on every write to this row (D8). Comparing it across frames costs 1 (P5).</summary>
    public uint Version => T.Version[Slot];

    /// <summary>Does the row carry EVERY bit of <paramref name="fields"/>? The question a page asks before it paints
    /// (P3) — never a null check, because there are no nulls in a column.</summary>
    public bool Knows(TrackFields fields) => (T.Known[Slot] & (uint)fields) == (uint)fields;

    /// <summary>A request naming this row is out right now (the planner's <c>Inflight</c> mark; cleared when a group
    /// lands or the batch settles).</summary>
    public bool InFlight => T.Inflight[Slot] != 0;

    /// <summary>Somebody has answered about this row before — a commit, or the disk saying it has nothing
    /// (<c>FetchedAt != 0</c>). False only for a row nobody has ever asked or answered.</summary>
    public bool Answered => T.FetchedAt[Slot] != 0;

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
    /// <summary>The album, or the SHOW for a legacy <see cref="TrackFlags.Podcast"/> row (superseded — that flag's
    /// own summary).</summary>
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
    /// <inheritdoc cref="TrackTable.VideoGid"/>
    public StringId VideoGidId => T.VideoGid[Slot];
    /// <inheritdoc cref="TrackTable.VideoW"/>
    public ushort VideoWidth => T.VideoW[Slot];
    /// <inheritdoc cref="TrackTable.VideoW"/>
    public ushort VideoHeight => T.VideoH[Slot];
    /// <inheritdoc cref="TrackFlags.LiveVideo"/>
    public bool IsLiveVideo => (T.Flags[Slot] & (uint)TrackFlags.LiveVideo) != 0;
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
    /// <inheritdoc cref="TrackTable.VideoGid"/>
    public TextRef VideoGid;
    /// <inheritdoc cref="TrackTable.VideoW"/>
    public ushort VideoW, VideoH;
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
            // The manifest id is the same kind of fact (G-056): a key carried by TrackV4 alone, never cleared by an
            // answer that does not carry it, and not a group anyone renders.
            if (!row.VideoGid.IsEmpty) t.SetText(ref t.VideoGid, slot, s.Intern(row.VideoGid));
            // THE USER'S OWN ATTACHED MP4 (G-220), folded in OUTSIDE the Video arm below and deliberately so. The
            // roster (`Video.Overrides`) is the only thing that ever ORIGINATES `VideoOverride`, it speaks at
            // `Authority.Local` and no provider answer speaks for it — while the arm below runs only for a row that
            // STAGED the Video group and whose authority `Accepts` let through. A row that lands AFTER the attach (a
            // page mounted later, or a restart, where the device-wide roster loads long before any track slot exists)
            // carries no Video group at all, and an arm that never runs can never light the bit. Set-only: clearing
            // is the mirror's own job on a detach (`Shell/Video.Overrides.Mirror.cs`), and the arm below preserves
            // this bit for exactly the same reason. ONE probe — of an index that is EMPTY, and returns before it
            // hashes anything, in every session where the user has curated nothing — and it answers for both columns
            // the gap names: the bit every surface reads, and the path the row is supposed to carry.
            if (Video.OverrideMirror.TryPath(t.Id[slot], out string attachedVideo))
            {
                t.Flags[slot] |= (uint)TrackFlags.VideoOverride;
                t.SetText(ref t.LocalVideo, slot, Entities.Strings.Intern(attachedVideo));
            }

            if ((known & (uint)TrackFields.Identity) != 0
                && t.Accepts(slot, (uint)TrackFields.Identity, auth, in t.IdentityAuthority))
            {
                // Every text write goes through `SetText`, which AddRefs the incoming id and releases the one it
                // overwrites (defect 1). A row re-answered by a search hit, then by TrackV4, then by a playlist item
                // would otherwise leak two titles, two credit lines and two covers into the interner — and the
                // OVERWRITE half is the one that leaks SILENTLY, because nothing about the row looks wrong afterwards.
                // A thin answer that carries no title (S5: a disc track with only a gid) must not blank the one a
                // fuller answer already set — same rule as the image guard right below (Fix 4).
                if (!row.Title.IsEmpty) t.SetText(ref t.Title, slot, s.Intern(row.Title));
                // Same guard as Title/Image right above and below: a thin answer with no credit line (a disk load,
                // whose row never carries `ArtistLine` — see `TrackShape.PersistedIdentity`) must not blank a line a
                // fuller live answer already interned (bug C's secondary defect, Track.cs:512 in the handoff).
                if (!row.ArtistLine.IsEmpty) t.SetText(ref t.ArtistLine, slot, s.Intern(row.ArtistLine));
                // An answer that carries no cover never blanks the one a playlist item already set — nothing
                // legitimately removes an image (Fix 4).
                if (!row.Image.IsEmpty) t.SetText(ref t.Image, slot, s.Intern(row.Image));
                t.DurationMs[slot] = row.DurationMs;
                if (!row.AlbumUri.IsEmpty) t.Album[slot] = s.Slot(Current.Albums, in row.AlbumUri);
                t.Flags[slot] = (t.Flags[slot] & ~(uint)TrackFlags.IdentityMask)
                              | (row.Flags & (uint)TrackFlags.IdentityMask);
                // `known & Identity`, NOT the bare `TrackFields.Identity` constant: every LIVE producer fills the
                // whole six-bit group as one wire shape, so this was always equivalent for them — but a disk-loaded
                // batch (`TrackShape.Load`) now deliberately withholds the `Artists` bit (bug C), and marking the
                // group Known unconditionally here would silently re-grant it regardless of what the row actually
                // staged, defeating that mask one line downstream of it.
                uint identity = known & (uint)TrackFields.Identity;
                // AND THE `Image` BIT IS NOT A THIN ANSWER'S TO GIVE (2026-09-18 — the row-thumbnail variant of the
                // artist half of bug C, `ArtistImageBitTests`). `Identity` is one six-bit constant, so a playlist
                // item, a search hit or a pathfinder row that staged the group while carrying NO cover — a cover is
                // optional in all three shapes (`Spotify.Decode.Playlist.cs`, `Spotify.Decode.Pathfinder.cs`) — used
                // to seal `Image` known over an empty column. `Fetch.NeedOf = wanted & ~Known & ~Asked` then sees no
                // hole, so every demand whose wanted set is Image-shaped and nothing wider — `User.Cover.cs`'s
                // `Ensure(Image | Album)`, `Sidebar.MosaicTrackFields` — asks for nothing and the art stays the flat
                // placeholder forever, unaskable. Withhold the bit instead, exactly as `Spotify.Decode.cs` withholds
                // a 0 `TrackCount` and an absent artist portrait: an empty column nobody answered for is a hole.
                //
                // AUTHORITY IS THE GATE, and `Thin` is the only rung that loses the bit — the same "who speaks for
                // the group" rule `ArtistUnion`'s `overview ||` follows. A `Full` answer (TrackV4, and the terminal
                // `UnavailableTrack` verdict for a track that does not exist for this account), a `Local` row (a
                // file's own tags: a cover-less mp3 is a fact) and a `Seed` row all speak for the WHOLE entity —
                // "no cover" is their answer and there is nothing further to ask. Demoting their bit would re-ask
                // nothing (`Fetch.Answer` seals `Asked` on success); it would only make `TrackFields.Face` — which
                // is `Identity` minus `Artists`, `Image` INCLUDED — unknowable, and `TableRules.RowHasData` reads
                // exactly that group: the row would shimmer as LOADING forever instead of painting the blank row
                // the verdict describes (the fake local-files rows, `Entities.Fake.Library.cs`, are the same shape).
                // A thin mention has no verdict behind it, and that is the whole distinction.
                if (auth == Authority.Thin && row.Image.IsEmpty && t.Image[slot].IsEmpty)
                {
                    identity &= ~(uint)TrackFields.Image;
                    // A WITHHELD BIT IS A HOLE, NOT A STALE OR FAILED ONE. `Applied` clears `Stale` and `Failed` for
                    // the bits it is handed, so the one bit it will not see has to be cleared here or it keeps marks
                    // that outlive their meaning: an invalidated row re-answered by a thin, cover-less answer would
                    // read `IsStale(Identity)` true forever (that is `(Stale & group) != 0`, any bit) even though the
                    // answer arrived, and `Failed` would still claim a terminal refusal the answer just retired.
                    // `NeedOf` already asks for a bit that is not Known; the marks would only mislead every OTHER
                    // reader — the pane that turns a shimmer into a Retry strip, the planner's `Settled` set.
                    t.Stale[slot] &= ~(uint)TrackFields.Image;
                    t.Failed[slot] &= ~(uint)TrackFields.Image;
                }
                t.Applied(slot, identity, auth, ref t.IdentityAuthority);
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
                t.VideoW[slot] = row.VideoW;
                t.VideoH[slot] = row.VideoH;
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

// ── persistence (Store.cs's per-kind seam) ───────────────────────────────────────────────────────────────────────────

/// <summary>How a track survives a restart. Persists <see cref="TrackFields.Identity"/> — minus the
/// <see cref="TrackFields.Artists"/> bit, see below — and the six cold groups whose facts are answered values
/// (<see cref="TrackFields.PlayCount"/>, <see cref="TrackFields.Year"/>,
/// <see cref="TrackFields.Availability"/>, <see cref="TrackFields.Isrc"/>, <see cref="TrackFields.Canonical"/>,
/// <see cref="TrackFields.Video"/> — the counterpart's URI only, not its still or the user's own mp4 — and
/// <see cref="TrackFields.Audio"/>).
///
/// <para><b>Deliberately NOT persisted:</b> <see cref="TrackTable.ArtistLine"/> — it reads as the credit line
/// (ch 01 GAP 2), but the click targets behind it are <c>Edges.TrackArtists</c>, which this shape does not persist
/// (only the five <c>LibraryEdge</c> relations are, per the store's own scope). Because of that, the
/// <see cref="TrackFields.Artists"/> bit inside <see cref="TrackFields.Identity"/> is masked OUT of
/// <c>PersistedFields</c> below — on both <see cref="Save"/> and <see cref="Load"/>, since they share the one
/// constant. <b>Do not restore it.</b> A row that came back from disk claiming Identity known WITH Artists would
/// tell <c>Fetch.NeedOf = wanted &amp; ~Known &amp; ~Asked</c> "never ask again" for a fact this cache genuinely
/// cannot answer, and there is no route that answers Artists on its own: <c>TrackV4</c> is the only producer of
/// both the credit line and the <c>TrackArtists</c> edge run, and a relation that arrives only as a side effect of
/// a row answer is asked by asking for the row's own group (<c>Fetch.Routes.cs</c>'s <see cref="FetchEdge"/> doc).
/// An earlier version of this comment argued the credit line "is left to re-arrive with the network's next
/// Identity answer" — that premise is false: marking Identity known is exactly what guarantees there is no next
/// Identity answer (bug C, 2026-09-15 — a cached track showed LESS than an uncached one, forever). Masking the
/// bit costs one extra Identity round trip per cached track after a restart, which is what every track paid
/// before persistence existed at all. <see cref="TrackFields.Tags"/>, <see cref="TrackFields.Publishing"/> and
/// <see cref="TrackFields.Files"/> are markers with no column of their own (their data is an edge or lives on
/// <see cref="AlbumTable"/>) and are masked out of the persisted <c>known</c> bits for the same reason — a bit
/// with no data behind it is a promise the disk cannot keep.</para>
///
/// <para><b>Two Flags sub-ranges, two columns.</b> <see cref="TrackTable.Flags"/> is ONE shared column in memory but
/// its bits belong to independently-gated groups (<see cref="TrackFlags.IdentityMask"/> to Identity,
/// <see cref="TrackFlags.AvailabilityMask"/> to Availability); a single persisted <c>flags</c> column bound whenever
/// EITHER group was known would let a later Availability-only batch's coalesce blank Identity's bits (and vice
/// versa) with a value that was never staged for them. <c>identity_flags</c>/<c>avail_flags</c> keep the same
/// independence on disk that <see cref="Table.Accepts"/> enforces in memory.</para>
///
/// <para>STORE THREAD (both halves) — see <see cref="ShowShape"/>'s note. The three cross-reference columns go
/// through <see cref="RowWriter.Id"/>, because a track's album/canonical/video-counterpart target can be either
/// packed-gid or arena text, exactly like the row's own identity.</para></summary>
public sealed class TrackShape : KindShape
{
    static readonly StoreColumn[] Cols =
    [
        new("title", StoreType.Text, StoreColumnFlags.Title),
        new("image", StoreType.Text),
        new("album_uri", StoreType.Text),
        new("duration_ms", StoreType.Int),
        new("identity_flags", StoreType.Int),
        new("play_count", StoreType.Int),
        new("year", StoreType.Int),
        new("available_at", StoreType.Int),
        new("avail_flags", StoreType.Int),
        new("isrc", StoreType.Text),
        new("canonical_uri", StoreType.Text),
        new("video_uri", StoreType.Text),
        new("tempo", StoreType.Int),
        new("musical_key", StoreType.Int),
        new("camelot", StoreType.Int),
        new("camelot_color", StoreType.Int),
        new("identity_auth", StoreType.Int, StoreColumnFlags.Authority),
        new("extras_auth", StoreType.Int, StoreColumnFlags.Authority),
    ];

    const uint ExtrasFields = (uint)(TrackFields.PlayCount | TrackFields.Year | TrackFields.Availability
                                    | TrackFields.Isrc | TrackFields.Canonical | TrackFields.Video | TrackFields.Audio);
    /// <summary>Every bit of <see cref="TrackFields.Identity"/> this shape can actually answer from disk.
    /// <see cref="TrackFields.Artists"/> is masked OUT — see the class doc above — because the credit line and
    /// its click targets live only in <c>Edges.TrackArtists</c>, which nothing below persists. Title, Album,
    /// Duration, Explicit and Image ARE real columns (<see cref="Save"/>), so restoring their bits is honest.</summary>
    const uint PersistedIdentity = (uint)TrackFields.Identity & ~(uint)TrackFields.Artists;
    /// <summary>What <see cref="Save"/> may write and <see cref="Load"/> may restore into <c>Known</c> — ONE
    /// constant shared by both, so they can never drift apart. Bug C's root cause was exactly a <c>Known</c> bit
    /// (Artists) surviving a disk restore without the data its live answer implies; keeping Save and Load on the
    /// same mask is what makes that class of bug structurally harder to reintroduce.</summary>
    const uint PersistedFields = PersistedIdentity | ExtrasFields;

    public override EntityKind Kind => EntityKind.Track;
    public override string Table => "track";
    public override ReadOnlySpan<StoreColumn> Columns => Cols;

    public override void Save(Staging s, RowWriter w)
    {
        var rows = s.TracksOrNull;
        if (rows is null) return;
        var span = rows.Span;
        for (int i = 0; i < span.Length; i++)
        {
            ref readonly var row = ref span[i];
            uint known = row.Known & PersistedFields;
            bool identity = (known & PersistedIdentity) != 0;
            bool extras = (known & ExtrasFields) != 0;

            if (identity)
            {
                w.Text(0, row.Title);
                w.Text(1, row.Image);
                w.Id(2, s, row.AlbumUri);
                w.Int(3, row.DurationMs);
                w.Int(4, (long)(row.Flags & (uint)TrackFlags.IdentityMask));
                w.Int(16, (int)row.Authority);
            }
            else { w.Null(0); w.Null(1); w.Null(2); w.Null(3); w.Null(4); w.Null(16); }

            if ((known & (uint)TrackFields.PlayCount) != 0) w.Int(5, row.PlayCount); else w.Null(5);
            if ((known & (uint)TrackFields.Year) != 0) w.Int(6, row.Year); else w.Null(6);
            if ((known & (uint)TrackFields.Availability) != 0)
            {
                w.Int(7, row.AvailableAt);
                w.Int(8, (long)(row.Flags & (uint)TrackFlags.AvailabilityMask));
            }
            else { w.Null(7); w.Null(8); }
            if ((known & (uint)TrackFields.Isrc) != 0) w.Text(9, row.Isrc); else w.Null(9);
            if ((known & (uint)TrackFields.Canonical) != 0) w.Id(10, s, row.CanonicalUri); else w.Null(10);
            if ((known & (uint)TrackFields.Video) != 0) w.Id(11, s, row.VideoUri); else w.Null(11);
            if ((known & (uint)TrackFields.Audio) != 0)
            {
                w.Int(12, row.Tempo);
                w.Int(13, row.Key);
                w.Int(14, row.Camelot);
                w.Int(15, row.CamelotColor);
            }
            else { w.Null(12); w.Null(13); w.Null(14); w.Null(15); }
            if (extras) w.Int(17, (int)row.Authority); else w.Null(17);

            w.Emit(row.Id, known, Entities.Now, Entities.Now);
        }
    }

    public override void Load(RowReader r, Staging into)
    {
        ref var row = ref into.Tracks.Add();
        row.Id = r.Uri;
        row.Title = r.Text(0);
        row.Image = r.Text(1);
        row.AlbumUri = r.Text(2);
        row.DurationMs = (int)r.Int(3);
        uint identityFlags = (uint)r.Int(4);
        row.PlayCount = (uint)r.Int(5);
        row.Year = (ushort)r.Int(6);
        row.AvailableAt = (int)r.Int(7);
        uint availFlags = (uint)r.Int(8);
        row.Flags = identityFlags | availFlags;
        row.Isrc = r.Text(9);
        row.CanonicalUri = r.Text(10);
        row.VideoUri = r.Text(11);
        row.Tempo = (ushort)r.Int(12);
        row.Key = (byte)r.Int(13);
        row.Camelot = (byte)r.Int(14);
        row.CamelotColor = (uint)r.Int(15);
        row.Known = r.Known & PersistedFields;
        row.Authority = (Authority)Math.Max(r.Int(16), r.Int(17));
    }
}
