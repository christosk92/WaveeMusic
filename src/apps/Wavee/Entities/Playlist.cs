// ── Entities/Playlist.cs — CORE (owner B, wave 1; plan §2, §4.3, ch 06 §7) ───────────────────────────────────────────
//
// THE PLAYLIST ROW: columns, the handle, the field groups, the staged shape a decoder fills and the commit that lands
// it. Wave 1 owns the DATA (plan §2, "two notes on the entity CORE rows"). Wave 5 (owner O, WP-5.O stream A) adds §7-§10
// below: the tuning / recommendations relations, the collaborator fold, the membership revision, the facts refold, the
// notice + owner derivation at commit, and the ch 06 §8 rule sets NOT already ported elsewhere — edit-error kinds,
// deposit targets, the tune menu, the create intent, the local-files helpers. Reorder / membership diff are
// `Track.ReorderRules` / `MembershipDiff` (Track.Rules.cs), the drop table is `Drag.Evaluate` and the notice verdict is
// `Detail.NoticeRules` — this file calls them, it does not copy them.
//
// SIX THINGS THIS FILE DECIDES, and each of them is a correctness fix over 0.2.9, not a translation:
//
//  1. CAPABILITIES ARE A KNOWN BIT, NOT A BOOL: a rootlist-seeded thin header has no capabilities block, and 0.2.9's
//     all-false `default` read as "revoked". Here "did anyone tell us?" is `Knows(PlaylistFields.Capabilities)` (P3).
//
//  2. THE NOTICE IS A COLUMN. The verdict (`Detail.NoticeRules.Next`) is STATEFUL — CreateFailed is terminal, a pending
//     create suppresses Deleted — so a UI-local would forget it on remount. §6's commit writes it; the page reads it.
//
//  3. THE THREE COLUMN-EXISTENCE FACTS ARE DERIVED AT COMMIT. "Does this table get an Added-by column / a Date-added
//     column / a Video column" is `distinct AddedBy ≥ 2` · `any AddedAt` · `any VideoPresence` over the WHOLE
//     membership (0.2.9 `DetailPage.cs:501-531`). Recomputing that per frame is a walk over 5,000 rows on the paint
//     path; recomputing it per commit is a walk once per answer. See <see cref="PlaylistFacts"/>, which folds it in one
//     pass with no allocation and no second scan for the distinctness.
//
//  4. THE LOCAL-FILES SURFACE IS A PLAYLIST, NOT A KIND. Plan §9.5 gives the `local` route to `Playlist.Page.cs`'s
//     `DetailKind.Playlist` arm: imported files hang off ONE ordinary playlist row (<see cref="Playlist.LocalFilesUri"/>)
//     whose membership edge is the imported library (0.2.9 `LocalSource`: owned, items + metadata editable).
//
//  5. ONE ROW PER PLAYLIST, WHICHEVER SPELLING ARRIVES (defect 4, docs/plans/wavee/wavee-0.3-entity-identity-memory.md
//     §4.4). `spotify:user:<u>:playlist:<gid>` and `spotify:playlist:<gid>` fold IN THE PARSE (the id is the uri's
//     trailing gid, `EntityId.TryParseGid`) to one `EntityId` and one slot; a round trip formats the canonical spelling.
//     It is the GID that folds: a fixture id that is not 22 base62 characters takes the text form and stays two rows.
//
//  6. THE ROW OWNS ITS TEXT, AND GIVES IT BACK (defect 1, doc §4.4): a never-AddRef'd id is PERMANENT (the engine's
//     `StringTable.cs:26`). Every `Column<StringId>` here is written through <see cref="Table.SetText"/> and released in
//     <see cref="PlaylistTable.ReleaseText"/> (abstract on the base, so this file cannot forget one).
//
// Rules: single writer, UI thread (C1); no LINQ, no closures, no async, no boxing (P8/P9); every text column is a
// REF-COUNTED StringId (P6, and item 6 above). TIME UNITS (WP-5.O contract §2.5, binding): every edge instant
// (`PlaylistTrackEdge.AddedAt`) and the `DaylistExpiresAt` / `DaylistCreatedAt` / `ChartUpdatedAt` columns are UNIX
// seconds; `Entities.Now` is app seconds (`Store.ToUnix`).

using FluentGpu.Foundation;

namespace Wavee;

// ── 1. field groups, flags and the small enums the columns encode ────────────────────────────────────────────────────

/// <summary>Which column groups of a playlist row are filled (P3). A group is the unit a provider answer fills and the
/// unit <see cref="Table.Accepts"/> gates, so the split follows the WIRE shapes, not the visual ones: `ListAttributes`
/// + `/decorate` fill <see cref="Identity"/>, `currentUserCapabilities` fills <see cref="Capabilities"/>,
/// `/permission/base` fills <see cref="Visibility"/>, the popcount service fills <see cref="Saves"/> alone.</summary>
[Flags]
public enum PlaylistFields : uint
{
    /// <summary>Title, description, cover and owner — one wire shape fills all four. NOT the track count: both
    /// routes that fill Identity stamp it applied (<see cref="Spotify.Decode.ListMetadataV2"/>,
    /// <see cref="Spotify.Decode.PlaylistRevision"/>), but only the second ever carries a length. See
    /// <see cref="TrackCount"/> — a decoder that knows Identity does NOT thereby know the count (bug A1).</summary>
    Identity = 1 << 0,
    /// <summary>The <see cref="PlaylistCaps"/> bitmask. NOT set by a rootlist row: a thin header is "unknown", never
    /// "revoked" (ch 06 §7 gap 2).</summary>
    Capabilities = 1 << 1,
    /// <summary>Public/private plus the base-permission revision the invite flyout writes against.</summary>
    Visibility = 1 << 2,
    /// <summary>The recommender format, its header image and the generic title a daylist arrives with. Load-bearing:
    /// the format is what routes a Home card to Hero / MixBand / WeeklyPair / RadioDial (ch 10 §7).</summary>
    Format = 1 << 3,
    /// <summary>The save count (popcount). Its own group because its own service answers it, late and separately —
    /// 0.2.9's 250 ms grace exists only because the page could not paint without it (ch 06 §7).</summary>
    Saves = 1 << 4,
    /// <summary>The daylist rollover window.</summary>
    Daylist = 1 << 5,
    /// <summary>The chart header facts (new entries, last updated, rank type).</summary>
    Chart = 1 << 6,
    /// <summary>The server's own <c>extractedColors.colorDark</c>. Separate from the graded palette
    /// (<c>Palette.cs</c>): this is the COLD-START colour, the one a Home card already has before any image decodes
    /// (ch 10 §7 "payload accent").</summary>
    Accent = 1 << 7,
    /// <summary>The session-control (tune) options' revision + selection. The options themselves are an edge.</summary>
    Tuning = 1 << 8,
    /// <summary>Bug A1: the playlist's REAL track count has landed — set ONLY by the one decoder that actually saw
    /// the wire's length field (<see cref="Spotify.Decode.PlaylistRevision"/>, gated on the proto's own `optional`
    /// presence, not on the value: a genuinely empty playlist's real `length: 0` sets this bit exactly like a
    /// nonzero one), and by a SETTLED list restored from disk (Store.Lists.cs), whose count is the one that decoder
    /// answered and was written in the same transaction as the rows. NEVER inferred from <see cref="Identity"/> —
    /// <see cref="Spotify.Decode.ListMetadataV2"/> (ext kind 205) stamps Identity applied too while carrying no length
    /// field at all
    /// (<c>Protos/list_metadata_v2.proto</c>). Deliberately excluded from <see cref="Row"/>/<see cref="Header"/>/
    /// <see cref="All"/>: those aggregates gate `Knows(...)` calls all over the app (an ALL-of check,
    /// <c>Table.Knows</c>), and no route names this bit in its `Primary`/`Groups` — folding it in would make every
    /// existing `Knows(Row)`/`Knows(All)` caller wait on a per-subject REST call the cheap batchable routes never
    /// make. Read through <c>Playlist.Knows(PlaylistFields.TrackCount)</c> alone.</summary>
    TrackCount = 1 << 9,

    /// <summary>What a card, a sidebar row or a picker row paints.</summary>
    Row = Identity | Accent,
    /// <summary>What the detail header paints before it is honest about its edit affordances.</summary>
    Header = Identity | Capabilities | Visibility | Format | Accent,
    All = Identity | Capabilities | Visibility | Format | Saves | Daylist | Chart | Accent | Tuning,
}

/// <summary>What the current user may do (Spotify's <c>currentUserCapabilities</c>, 0.2.9's
/// <c>PlaylistCapabilities</c>). One byte in a column instead of six bools in a record; the seventh member of the
/// 0.2.9 record — <c>Known</c> — is <see cref="PlaylistFields.Capabilities"/> and is deliberately NOT a bit here.</summary>
[Flags]
public enum PlaylistCaps : byte
{
    None = 0,
    CanView = 1 << 0,
    CanEditItems = 1 << 1,
    CanEditMetadata = 1 << 2,
    IsCollaborative = 1 << 3,
    IsOwner = 1 << 4,
    CanAdministratePermissions = 1 << 5,
}

/// <summary>Row-level booleans that are not capabilities, packed into the one <c>Flags</c> column (P7).</summary>
[Flags]
public enum PlaylistFlags : uint
{
    None = 0,
    /// <summary>Owner visibility from <c>/permission/base</c>. Default TRUE until fetched, which is why the commit
    /// writes it under <see cref="PlaylistFields.Visibility"/> and the handle reads it as "public unless told".</summary>
    Public = 1 << 0,
    /// <summary>TOMBSTONE: the owner deleted this playlist remotely. LATCHING — once observed it can never be cleared
    /// by a later header write (0.2.9's <c>incoming || current</c> merge), which is why
    /// <see cref="PlaylistTable.LatchingFlags"/> excludes it from the commit's clear mask.</summary>
    DeletedByOwner = 1 << 1,
    /// <summary>An optimistic create that has not been confirmed. Suppresses the Deleted notice.</summary>
    CreatePending = 1 << 2,
    /// <summary>An optimistic create the server rejected. Terminal (the notice never leaves).</summary>
    CreateFailed = 1 << 3,
    /// <summary>A daylist whose name still equals its generic pre-title: identity is present but not YET the real one
    /// (ch 10 §7, the 0.2.9 <c>HomeDaylistHydrator</c> contract as a bit rather than a re-fetch loop).</summary>
    GenericTitle = 1 << 4,

    // ── derived at commit from the membership (see PlaylistFacts) ───────────────────────────────────────────────────
    /// <summary>≥ 2 distinct <c>AddedBy</c> values ⇒ the table shows an Added-by column.</summary>
    HasAddedBy = 1 << 8,
    /// <summary>Any row carries an <c>AddedAt</c> ⇒ the table shows a Date-added column.</summary>
    HasDateAdded = 1 << 9,
    /// <summary>Any row has a video counterpart ⇒ the table shows a Video column.</summary>
    HasVideo = 1 << 10,
    /// <summary>The membership contains episodes as well as tracks ⇒ the meta line takes its MIXED arm
    /// ("48 songs · 3 episodes · …", 0.2.9 <c>DetailPage.cs:509-518</c>).</summary>
    Mixed = 1 << 11,
}

/// <summary>The recommender format (0.2.9's free-text <c>Playlist.Format</c>, closed here because every consumer
/// switches on it). <see cref="Other"/> keeps a format we do not model from reading as "no format".</summary>
public enum PlaylistFormat : byte
{
    None = 0, Daylist, DailyMix, DiscoverWeekly, ReleaseRadar, TopicMix, InspiredByMix, Editorial, Chart, Radio, Other,
}

/// <summary>The detail page's notice verdict — a MODEL fact written at commit, never a UI probe. Five values, shared
/// with the album surface (ch 06 §8: "the enum is shared with the album chapter — one file, two owners"), which is why
/// it is declared here and <c>Album.cs</c> binds to it rather than declaring a second copy.</summary>
public enum DetailNotice : byte
{
    /// <summary>Nothing to say; the page is live and editable per its own capabilities.</summary>
    None = 0,
    /// <summary>Deleted (by us elsewhere, or by its owner) while we were looking at it.</summary>
    Deleted,
    /// <summary>It still exists but we may no longer view it — a permission flip landed under us.</summary>
    AccessRevoked,
    /// <summary>An optimistic create never became real. Terminal.</summary>
    CreateFailed,
    /// <summary>ALBUM path: the tracklist still holds gid-only rows whose TrackV4 repair has not landed.</summary>
    MinifiedAlbum,
}

/// <summary>What the detail track list shows in place of (or as) its rows — 0.2.9's <c>PlaylistRowsState</c> verbatim.
/// The distinction that matters is <see cref="Loading"/> vs <see cref="Empty"/>: a header whose membership has not been
/// adopted yet must shimmer, never say "Nothing here yet".</summary>
public enum PlaylistRowsState : byte { Loading, Empty, NoMatch, Rows }

// ── 2. the table ─────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The playlist rows. Columns grouped by who reads them together (P2): the header block first, the late/rare
/// facts after, the two authority columns last.</summary>
public sealed class PlaylistTable : Table
{
    // Identity — the header, the sidebar row, the picker row, the drag chip.
    public Column<StringId> Title, Description, Image;
    /// <summary>The canonical share link the wire states for this playlist (<c>sharingInfo.shareUrl</c>) — copy-link and
    /// the share sheet read it verbatim rather than rebuilding a url from the uri (the same column <c>AlbumTable</c>
    /// keeps for the same reason).</summary>
    public Column<StringId> ShareUrl;
    /// <summary>The owner's row in <see cref="Scope.Users"/> (slot 0 = unknown). NOT a name string: two playlists by
    /// the same owner share one user row, and the added-by cell resolves through the same slot (ch 06 §7).</summary>
    public Column<int> Owner, TrackCount;

    // Capabilities / visibility.
    public Column<byte> Caps;
    public Column<StringId> PermissionRevision;

    // Format band (the Home card's routing facts).
    public Column<byte> Format;
    public Column<StringId> HeaderImage, GenericTitle;

    // Late and rare.
    public Column<int> Saves, DaylistExpiresAt, DaylistCreatedAt, ChartUpdatedAt;
    public Column<ushort> ChartNewEntries;
    public Column<StringId> ChartRankType, TuningSelected;
    /// <summary>The options are only valid while this hash still matches the membership revision they came with.</summary>
    public Column<uint> TuningRevision;
    /// <summary>The wire's own cold-start accent (opaque ARGB, 0 = none) — see <see cref="PlaylistFields.Accent"/>.</summary>
    public Column<uint> Accent;

    /// <summary><see cref="PlaylistFlags"/>.</summary>
    public Column<uint> Flags;
    /// <summary><see cref="DetailNotice"/>, written by the ported rule at commit (ch 06 §7).</summary>
    public Column<byte> Notice;
    /// <summary>How many of the membership rows are episodes — the MIXED meta line's second half. Derived at commit.</summary>
    public Column<int> EpisodeCount;
    /// <summary>Σ of the membership's durations, in milliseconds. Derived at commit so the meta line is a format, not a
    /// walk (P11: computed once per answer, never per frame).</summary>
    public Column<long> DurationMs;

    public Column<byte> IdentityAuthority, ExtrasAuthority;

    /// <summary>The membership revision the rows were answered at, in the wire spelling <c>{counter},{hex}</c>
    /// (<c>Spotify.Api.FormatRevision</c>) — the base a <c>/diff</c> read sends (B1b gap 8) and the tuning hash's input.
    /// Not a row fact on disk: it is restored only WITH the rows it describes (<c>list_head</c>, Store.Lists.cs).</summary>
    public Column<StringId> Revision;

    /// <summary>Flags a provider answer may SET but never CLEAR. Only the tombstone: 0.2.9 merges it as
    /// <c>incoming || current</c> precisely so a stale header cannot un-delete a playlist.</summary>
    public const uint LatchingFlags = (uint)PlaylistFlags.DeletedByOwner;

    /// <summary>Flags nobody but this app writes — the optimistic create pair and the four derived facts. A wire
    /// answer's clear mask must not touch them or a header refresh would erase the table's own column decisions.</summary>
    public const uint LocalFlags = (uint)(PlaylistFlags.CreatePending | PlaylistFlags.CreateFailed
        | PlaylistFlags.HasAddedBy | PlaylistFlags.HasDateAdded | PlaylistFlags.HasVideo | PlaylistFlags.Mixed);

    /// <summary>THE flag merge (pure): an answer clears only the bits its <paramref name="mask"/> claims, never a
    /// <see cref="LatchingFlags"/> bit (the tombstone), and neither sets nor clears a <see cref="LocalFlags"/> bit.</summary>
    public static uint MergeFlags(uint current, uint incoming, uint mask)
    {
        uint clear = mask & ~(LatchingFlags | LocalFlags);
        return (current & ~clear) | (incoming & ~LocalFlags);
    }

    public override EntityKind Kind => EntityKind.Playlist;

    protected override void GrowColumns(int capacity)
    {
        Title.EnsureCapacity(capacity);
        Description.EnsureCapacity(capacity);
        Image.EnsureCapacity(capacity);
        Owner.EnsureCapacity(capacity);
        TrackCount.EnsureCapacity(capacity);
        Caps.EnsureCapacity(capacity);
        PermissionRevision.EnsureCapacity(capacity);
        Format.EnsureCapacity(capacity);
        ShareUrl.EnsureCapacity(capacity);
        HeaderImage.EnsureCapacity(capacity);
        GenericTitle.EnsureCapacity(capacity);
        Saves.EnsureCapacity(capacity);
        DaylistExpiresAt.EnsureCapacity(capacity);
        DaylistCreatedAt.EnsureCapacity(capacity);
        ChartUpdatedAt.EnsureCapacity(capacity);
        ChartNewEntries.EnsureCapacity(capacity);
        ChartRankType.EnsureCapacity(capacity);
        TuningSelected.EnsureCapacity(capacity);
        TuningRevision.EnsureCapacity(capacity);
        Accent.EnsureCapacity(capacity);
        Flags.EnsureCapacity(capacity);
        Notice.EnsureCapacity(capacity);
        EpisodeCount.EnsureCapacity(capacity);
        DurationMs.EnsureCapacity(capacity);
        IdentityAuthority.EnsureCapacity(capacity);
        ExtrasAuthority.EnsureCapacity(capacity);
        Revision.EnsureCapacity(capacity);
    }

    /// <summary>Give back every string a playlist row owns (file header item 6): one line per <c>Column&lt;StringId&gt;</c>,
    /// each written only through <see cref="Table.SetText"/>. Called by <see cref="Table.FreeSlot"/> (R2) and
    /// <see cref="Table.ReleaseAllText"/> (D9).</summary>
    protected override void ReleaseText(int slot)
    {
        ClearText(ref Title, slot);
        ClearText(ref Description, slot);
        ClearText(ref Image, slot);
        ClearText(ref PermissionRevision, slot);
        ClearText(ref ShareUrl, slot);
        ClearText(ref HeaderImage, slot);
        ClearText(ref GenericTitle, slot);
        ClearText(ref ChartRankType, slot);
        ClearText(ref TuningSelected, slot);
        ClearText(ref Revision, slot);
    }
}

// ── 3. the membership fold (the three column-existence facts, ch 06 §7) ──────────────────────────────────────────────

/// <summary>One pass over a playlist's membership, folded into the header/table facts with no allocation: "≥ 2 distinct
/// added-by" remembers the FIRST non-zero adder and latches on a different one (O(n), no set — P8/P9). The caller passes
/// the per-row facts it can see, so a live decode, the seed and a test fold identically.</summary>
public struct PlaylistFacts
{
    public int Rows;
    public int Episodes;
    public long DurationMs;
    /// <summary>The first non-zero adder seen; 0 while none has been.</summary>
    public int FirstAddedBy;
    public bool ManyAdders;
    public bool AnyDate;
    public bool AnyVideo;

    /// <summary>Fold one membership row in. <paramref name="addedBy"/> is a user SLOT (0 = unknown),
    /// <paramref name="addedAt"/> UNIX seconds (0 = none).</summary>
    public void Add(int addedBy, int addedAt, int durationMs, bool isEpisode, bool hasVideo)
    {
        Rows++;
        if (isEpisode) Episodes++;
        if (durationMs > 0) DurationMs += durationMs;
        if (addedAt > 0) AnyDate = true;
        if (hasVideo) AnyVideo = true;
        if (addedBy == 0) return;
        if (FirstAddedBy == 0) FirstAddedBy = addedBy;
        else if (FirstAddedBy != addedBy) ManyAdders = true;
    }

    /// <summary>The four derived <see cref="PlaylistFlags"/> this fold decided. Never the other bits.</summary>
    public readonly uint Flags
    {
        get
        {
            uint flags = 0;
            if (ManyAdders) flags |= (uint)PlaylistFlags.HasAddedBy;
            if (AnyDate) flags |= (uint)PlaylistFlags.HasDateAdded;
            if (AnyVideo) flags |= (uint)PlaylistFlags.HasVideo;
            if (Episodes > 0 && Episodes < Rows) flags |= (uint)PlaylistFlags.Mixed;
            return flags;
        }
    }
}

// ── 4. the handle ────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>A playlist: one <c>int</c> over the columns (D13). Partial so Wave 5's owner O adds ch 06 §8's rule sets
/// to the same type without touching this file.</summary>
public readonly partial struct Playlist(int slot) : IEquatable<Playlist>
{
    /// <summary>The imported-files surface (plan §9.5: the `local` route is a <c>DetailKind.Playlist</c> arm). Spelled
    /// so <see cref="EntityUri.ProviderOf(ReadOnlySpan{char},out EntityKind)"/> answers (Playlist, Local) without a
    /// special case: <c>wavee:local:</c> routes to the local provider and <c>playlist:</c> is its catalog kind.</summary>
    public const string LocalFilesUri = "wavee:local:playlist:all";

    static PlaylistTable T => Entities.Current.Playlists;
    static Edges E => Entities.Current.Edges;

    public int Slot { get; } = slot;

    /// <summary>The one local-files playlist row, allocated on first ask like any other (D10).</summary>
    public static Playlist LocalFiles => new(Entities.Current.Playlists.Slot(LocalFilesUri.AsSpan()));

    public bool IsValid => Slot > Table.None && Slot < T.Count;
    /// <summary>Bumped by every write to this row; a bound header compares it across frames (P5 cost 1).</summary>
    public uint Version => T.Version[Slot];
    public bool Knows(PlaylistFields fields) => T.Knows(Slot, (uint)fields);

    /// <summary>THE row's identity, packed (<c>Entities.cs</c> §2). A field load — kind, provider and the 128-bit
    /// gid come straight out of it, where <c>EntityUri.Of</c> used to resolve the uri string and re-walk its prefix on
    /// every read (45-100 ns, doc §1.3 item 3). Both playlist spellings land on ONE id (file header item 5).</summary>
    public EntityId Id => T.Id[Slot];

    /// <summary>The identity as the text-facing VIEW — what a deep link, a copy-link, a <c>PutState</c> body or a
    /// test holds. Free: an <see cref="EntityUri"/> IS an <see cref="EntityId"/>, and only <c>Uri.Text</c>
    /// materialises a string (81 ns, at a cold call site).</summary>
    public EntityUri Uri => new(T.Id[Slot]);

    // ── identity ────────────────────────────────────────────────────────────────────────────────────────────────────

    public StringId TitleId => T.Title[Slot];
    public StringId DescriptionId => T.Description[Slot];
    /// <summary>The cover's image id. Empty is a real answer for a cover-less playlist — the mosaic renders instead.</summary>
    public StringId ImageId => T.Image[Slot];
    public StringId ShareUrlId => T.ShareUrl[Slot];
    public StringId HeaderImageId => T.HeaderImage[Slot];
    /// <summary>The generic pre-title a daylist arrives with; see <see cref="PlaylistFlags.GenericTitle"/>.</summary>
    public StringId GenericTitleId => T.GenericTitle[Slot];
    public User Owner => new(T.Owner[Slot]);
    /// <summary>The SERVER's count, which is the honest one while the membership is still paging. The resident count is
    /// <see cref="TrackSlots"/>.Length.</summary>
    public int TrackCount => T.TrackCount[Slot];
    public int Saves => T.Saves[Slot];
    public uint Accent => T.Accent[Slot];
    public PlaylistFormat Format => (PlaylistFormat)T.Format[Slot];

    // ── capabilities and visibility ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The raw capability bits. Read them through the named questions below, which know what an UNKNOWN
    /// block means — and it is not the same answer for every capability.</summary>
    public PlaylistCaps Caps => (PlaylistCaps)T.Caps[Slot];
    public bool CanView => CanViewOf(Knows(PlaylistFields.Capabilities), Caps);
    public bool IsCollaborative => (Caps & PlaylistCaps.IsCollaborative) != 0;
    public bool IsOwner => (Caps & PlaylistCaps.IsOwner) != 0;
    public bool CanAdministratePermissions => (Caps & PlaylistCaps.CanAdministratePermissions) != 0;
    /// <summary>THE EDIT-GATE TRIO (ch 06 §0.2, 0.2.9 <c>PlaylistInlineEdit.Editable/EditableMetadata/Live</c>): every
    /// affordance routes through these, so "a notice mounts ⇒ every edit affordance disappears in the same frame" is ONE
    /// fact. Rows may be added / removed / reordered: no notice AND the capability.</summary>
    public bool Editable => Notice == DetailNotice.None && EditableOf(Knows(PlaylistFields.Capabilities), Caps);
    /// <summary>Title / description / cover: no notice AND the capability.</summary>
    public bool EditableMetadata => Notice == DetailNotice.None && EditableMetadataOf(Knows(PlaylistFields.Capabilities), Caps);
    /// <summary>The shared half the two OWNER affordances (invite, the ⋯ menu) gate on beside <see cref="IsOwner"/>.</summary>
    public bool Live => Notice == DetailNotice.None;

    /// <summary>0.2.9 <c>SpotifyEditsLive</c>: a Spotify account scope with a session that can send. The page ANDs it into
    /// the invite pill and the ⋯ menu (item 58: under <c>--fake</c> both are absent on an owned playlist). Pure.</summary>
    public static bool SpotifyEditsLiveOf(bool accountScope, bool online) => accountScope && online;

    /// <summary>Unknown capabilities read as "may view": the page renders, read-only. NEVER as "revoked" — a
    /// rootlist-seeded thin header carries no capability block, and 0.2.9's all-false <c>default</c> is why one could
    /// briefly claim the user had lost access to their own playlist (ch 06 §7 gap 2).</summary>
    public static bool CanViewOf(bool knowsCaps, PlaylistCaps caps) => !knowsCaps || (caps & PlaylistCaps.CanView) != 0;

    /// <summary>Rows may be added / removed / reordered. Unknown ⇒ FALSE, the opposite default from
    /// <see cref="CanViewOf"/> and deliberately so: gaining an affordance when the block lands is fine, losing one the
    /// user has already clicked is not.</summary>
    public static bool EditableOf(bool knowsCaps, PlaylistCaps caps)
        => knowsCaps && (caps & PlaylistCaps.CanEditItems) != 0;

    /// <summary>Title / description / cover may be edited. Same unknown-is-false rule.</summary>
    public static bool EditableMetadataOf(bool knowsCaps, PlaylistCaps caps)
        => knowsCaps && (caps & PlaylistCaps.CanEditMetadata) != 0;

    uint Bits => T.Flags[Slot];
    public bool IsPublic => IsPublicOf(Knows(PlaylistFields.Visibility), Bits);

    /// <summary>Public unless <c>/permission/base</c> said otherwise — 0.2.9's <c>IsPublic = true</c> default, which
    /// matters because the invite flyout renders differently for a private list and must not guess it into one.</summary>
    public static bool IsPublicOf(bool knowsVisibility, uint flags)
        => !knowsVisibility || (flags & (uint)PlaylistFlags.Public) != 0;
    public bool DeletedByOwner => (Bits & (uint)PlaylistFlags.DeletedByOwner) != 0;
    public bool CreatePending => (Bits & (uint)PlaylistFlags.CreatePending) != 0;
    public bool CreateFailed => (Bits & (uint)PlaylistFlags.CreateFailed) != 0;
    public bool HasGenericTitle => (Bits & (uint)PlaylistFlags.GenericTitle) != 0;

    // ── the table's column-existence facts (derived at commit, never probed) ────────────────────────────────────────

    public bool HasAddedByColumn => (Bits & (uint)PlaylistFlags.HasAddedBy) != 0;
    public bool HasDateAddedColumn => (Bits & (uint)PlaylistFlags.HasDateAdded) != 0;
    public bool HasVideoColumn => (Bits & (uint)PlaylistFlags.HasVideo) != 0;
    public bool IsMixed => (Bits & (uint)PlaylistFlags.Mixed) != 0;
    public int EpisodeCount => T.EpisodeCount[Slot];
    /// <summary>Σ of the resident membership's durations. Zero until the rows carry durations.</summary>
    public long DurationMs => T.DurationMs[Slot];

    // ── the late facts ──────────────────────────────────────────────────────────────────────────────────────────────

    public DetailNotice Notice => (DetailNotice)T.Notice[Slot];
    /// <summary>UNIX seconds, 0 = not a daylist (as are <see cref="DaylistCreatedAt"/> and <see cref="ChartUpdatedAt"/>).</summary>
    public int DaylistExpiresAt => T.DaylistExpiresAt[Slot];
    public int DaylistCreatedAt => T.DaylistCreatedAt[Slot];
    public int ChartNewEntries => T.ChartNewEntries[Slot];
    public int ChartUpdatedAt => T.ChartUpdatedAt[Slot];
    public StringId ChartRankTypeId => T.ChartRankType[Slot];
    public StringId TuningSelectedId => T.TuningSelected[Slot];

    // ── membership ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The resident rows, in order. Read the span rule in <c>Edges.cs</c>'s header before you keep it.</summary>
    public ReadOnlySpan<int> TrackSlots => E.PlaylistTracks.Targets(Slot);
    /// <summary>Per-row membership facts (item id, added-at, added-by, the chart triple), parallel to
    /// <see cref="TrackSlots"/>. None of this is on the track row: two playlists holding the same recording disagree
    /// about all of it (D10).</summary>
    public ReadOnlySpan<PlaylistTrackEdge> TrackEdges => E.PlaylistTracks.Payload(Slot);
    /// <summary>The optimistic state of each row (C6) — parallel again; a pending remove greys the row in place.</summary>
    public ReadOnlySpan<byte> TrackPending => E.PlaylistTracks.Pending(Slot);
    public EdgeState MembershipState => E.PlaylistTracks.State(Slot);
    /// <summary>How many rows there will be: the server's paging total while partial, the resident count once complete.</summary>
    public int MembershipTotal => E.PlaylistTracks.Total(Slot);
    /// <summary>THE three-way truth ch 06 §7 insists on. 0.2.9 spelled it <c>MembershipLoaded</c> and gave it a "no
    /// store" escape hatch, which is why <c>--fake</c>, Local Files and <c>wavee:playlist:*</c> pinned on the shimmer:
    /// here an unknown edge is unknown for everyone, and the seed marks its lists complete like any other writer.</summary>
    public bool MembershipKnown => E.PlaylistTracks.State(Slot) != EdgeState.Unknown;
    /// <summary>Bumps on every structural change to the membership — the number a bound list compares.</summary>
    public uint MembershipVersion => E.PlaylistTracks.Version(Slot);

    /// <summary>The list's own state, 0.2.9's <c>PlaylistListState.For</c> over the edge instead of a bool + a count.
    /// <paramref name="visible"/> is the filtered view's length (the live search / chip filters).</summary>
    public PlaylistRowsState RowsState(int visible)
        => Playlist.RowsStateOf(MembershipKnown, E.PlaylistTracks.Count(Slot), visible);

    /// <summary>Shimmer instead of a verdict: nothing resident AND nobody has said the list is empty. A list that
    /// already carries rows is never loading, whatever the state says — rows are proof.</summary>
    public static bool IsLoading(bool membershipKnown, int total) => !membershipKnown && total == 0;

    /// <inheritdoc cref="RowsState(int)"/>
    public static PlaylistRowsState RowsStateOf(bool membershipKnown, int total, int visible)
        => IsLoading(membershipKnown, total) ? PlaylistRowsState.Loading
         : total == 0 ? PlaylistRowsState.Empty
         : visible == 0 ? PlaylistRowsState.NoMatch
         : PlaylistRowsState.Rows;

    /// <summary>The diagnostics spelling (<c>playlist.open.state state=</c>) — constants, never enum reflection (P9).</summary>
    public static string NameOf(PlaylistRowsState state) => state switch
    {
        PlaylistRowsState.Loading => "Loading",
        PlaylistRowsState.Empty => "Empty",
        PlaylistRowsState.NoMatch => "NoMatch",
        _ => "Rows",
    };

    public bool Equals(Playlist other) => other.Slot == Slot;
    public override bool Equals(object? o) => o is Playlist p && p.Slot == Slot;
    public override int GetHashCode() => Slot;
    public static bool operator ==(Playlist a, Playlist b) => a.Slot == b.Slot;
    public static bool operator !=(Playlist a, Playlist b) => a.Slot != b.Slot;
}

// ── 5. writes: membership pages and the local-only flags ─────────────────────────────────────────────────────────────

public readonly partial struct Playlist
{
    /// <summary>Land one page of the membership and re-derive the facts that depend on it. <paramref name="facts"/> is
    /// folded by the CALLER over the whole resident membership; <c>default</c> lands the page without touching them
    /// (<see cref="Refold"/> re-derives them from the rows).</summary>
    public void ApplyPage(int offset, ReadOnlySpan<int> trackSlots, ReadOnlySpan<PlaylistTrackEdge> edges, int total,
        in PlaylistFacts facts = default)
    {
        E.PlaylistTracks.ReplacePage(Slot, offset, trackSlots, edges, total);
        if (facts.Rows > 0) Apply(in facts);
        T.Bump(Slot);
    }

    /// <summary>Land a COMPLETE membership in one copy (ch 31 GAP 3's shape).</summary>
    public void ApplyMembership(ReadOnlySpan<int> trackSlots, ReadOnlySpan<PlaylistTrackEdge> edges,
        in PlaylistFacts facts = default)
    {
        E.PlaylistTracks.ReplaceRun(Slot, trackSlots, edges);
        if (facts.Rows > 0) Apply(in facts);
        T.Bump(Slot);
    }

    /// <summary>Write the four derived flags, the episode count and the duration sum. Authority-free by construction:
    /// these are OUR arithmetic over rows a provider already wrote, so nothing may overrule them.</summary>
    public void Apply(in PlaylistFacts facts)
    {
        uint bits = T.Flags[Slot];
        bits &= ~(uint)(PlaylistFlags.HasAddedBy | PlaylistFlags.HasDateAdded | PlaylistFlags.HasVideo | PlaylistFlags.Mixed);
        T.Flags[Slot] = bits | facts.Flags;
        T.EpisodeCount[Slot] = facts.Episodes;
        T.DurationMs[Slot] = facts.DurationMs;
        T.Bump(Slot);
    }

    /// <summary>The notice verdict, written by the ported rule at commit time (ch 06 §7). A separate entry point from
    /// the commit because the rule is stateful and Wave 5 owns it; the COLUMN is what both waves agree on.</summary>
    public void SetNotice(DetailNotice notice)
    {
        if ((DetailNotice)T.Notice[Slot] == notice) return;
        T.Notice[Slot] = (byte)notice;
        T.Bump(Slot);
    }

    /// <summary>Mark an optimistic create in flight (C6). Nothing else in the model changes: the row already exists,
    /// because a factory allocated it the moment the user named the playlist.</summary>
    public void MarkCreatePending()
    {
        T.Flags[Slot] = (T.Flags[Slot] | (uint)PlaylistFlags.CreatePending) & ~(uint)PlaylistFlags.CreateFailed;
        T.Bump(Slot);
    }

    /// <summary>The create settled. A rejection is TERMINAL — the failed bit stays and the notice never leaves, because
    /// the page is showing a playlist the server does not have (0.2.9's <c>_createFailed</c> set).</summary>
    public void SettleCreate(bool ok)
    {
        uint bits = T.Flags[Slot] & ~(uint)PlaylistFlags.CreatePending;
        if (!ok) bits |= (uint)PlaylistFlags.CreateFailed;
        T.Flags[Slot] = bits;
        if (!ok) T.Notice[Slot] = (byte)DetailNotice.CreateFailed;
        T.Bump(Slot);
    }
}

// ── 6. staging + commit (C10, C1) ────────────────────────────────────────────────────────────────────────────────────

/// <summary>One decoded playlist header, as a worker can produce it: TEXT IS <see cref="TextRef"/>, never
/// <see cref="StringId"/> — interning is a write to the single-writer <c>StringTable</c> and the decode does not run on
/// the UI thread (§5.6, C1/C10).</summary>
public struct StagedPlaylist : IStagedRow
{
    /// <summary>THE row's identity as the wire gave it (<see cref="StagedId"/>): the packed <see cref="EntityId"/> when
    /// it arrived as 16 gid bytes, the uri's UTF-8 in the arena when it arrived as text. One field, one resolve —
    /// <c>s.Slot(table, in row.Id)</c> — and no format-to-arena-then-parse-back round trip.</summary>
    public StagedId Id;
    public TextRef Title, Description, Image, HeaderImage, GenericTitle, PermissionRevision, ShareUrl;
    /// <summary>The owning account — resolved to a user SLOT at commit, so a byline binds before the profile lands.</summary>
    public StagedId OwnerUri;
    public TextRef ChartRankType, TuningSelected;
    /// <summary>The membership revision in its wire spelling; written whenever present (no group gate).</summary>
    public TextRef Revision;
    public int TrackCount, Saves, DaylistExpiresAt, DaylistCreatedAt, ChartUpdatedAt;
    public uint Accent, TuningRevision;
    public ushort ChartNewEntries;
    public byte Caps, Format;
    /// <summary>The <see cref="PlaylistFlags"/> this answer asserts…</summary>
    public uint Flags;
    /// <summary>…and which of them it is entitled to speak about. A rootlist row that knows nothing about visibility
    /// must not clear <see cref="PlaylistFlags.Public"/> by omission.</summary>
    public uint FlagsMask;
    /// <summary>The <see cref="PlaylistFields"/> groups this row fills.</summary>
    public uint Known;
    public Authority Authority;

    /// <inheritdoc cref="IStagedRow.Init"/>
    public void Init(in StagedId id, Authority authority, uint known) { Id = id; Authority = authority; Known = known; }
    /// <inheritdoc cref="IStagedRow.Identity"/>
    public readonly StagedId Identity => Id;
}

public sealed partial class Staging
{
    StagedList<StagedPlaylist>? _playlists;
    /// <summary>Lazy: a decode that touches no playlist allocates no playlist list.</summary>
    public StagedList<StagedPlaylist> Playlists => _playlists ??= Register(new StagedList<StagedPlaylist>());
    internal StagedList<StagedPlaylist>? StagedPlaylists => _playlists;
}

public static partial class Entities
{
    /// <summary>Typed batch sugar over <see cref="Ensure(Table,ReadOnlySpan{int},uint,FetchPriority)"/> (P4). The span
    /// of handles is reinterpreted as slots — a cast, not a copy.</summary>
    public static void Ensure(ReadOnlySpan<Playlist> rows, PlaylistFields wanted, FetchPriority priority = FetchPriority.Visible)
        => Ensure(Current.Playlists, Slots(rows), (uint)wanted, priority);

    /// <inheritdoc cref="Ensure(ReadOnlySpan{Playlist},PlaylistFields,FetchPriority)"/>
    public static void Ensure(Playlist row, PlaylistFields wanted, FetchPriority priority = FetchPriority.Visible)
    {
        int slot = row.Slot;                                   // the one-row call IS the one-element span (P4)
        Ensure(Current.Playlists, new ReadOnlySpan<int>(in slot), (uint)wanted, priority);
    }

    /// <summary>Copy a staged batch of playlist headers into the columns. The commit shape every kind file repeats:
    /// resolve the uri to a slot, ask whether this authority may write the group, write it, call
    /// <see cref="Table.Applied"/> (D16).</summary>
    static partial void CommitPlaylists(Staging s)
    {
        var staged = s.StagedPlaylists;
        if (staged is null || staged.Count == 0) return;

        var t = Current.Playlists;
        var users = Current.Users;
        var rows = staged.Span;
        for (int i = 0; i < rows.Length; i++)
        {
            ref var row = ref rows[i];
            int slot = s.Slot(t, in row.Id);
            if (slot == Table.None) continue;   // a row with no identity is not a row
            var authority = row.Authority == Wavee.Authority.None ? s.Authority : row.Authority;
            // The edition is the authority: a later daylist window outranks whoever wrote the earlier one, so its
            // Identity/Format/Daylist land even from a Thin feed row. A held window of 0 (relaunch: Daylist is not
            // persisted) yields to any window. Applied records the edition gate too: a stale Thin card cannot retitle a new edition, a later edition still can.
            bool newEdition = DaylistEdition.Outranks((row.Known & (uint)PlaylistFields.Daylist) != 0,
                                                      row.DaylistExpiresAt, t.DaylistExpiresAt[slot]);
            var editionGate = newEdition ? Wavee.Authority.Full : authority;

            if ((row.Known & (uint)PlaylistFields.Identity) != 0
                && t.Accepts(slot, (uint)PlaylistFields.Identity, editionGate, in t.IdentityAuthority))
            {
                // SetText, never `t.Title[slot] = …`: the write AddRefs the incoming id and releases the one it
                // overwrites, so a header re-answered a hundred times owns exactly one title's worth of interner at
                // the end of it (defect 1, file header item 6).
                t.SetText(ref t.Title, slot, s.Intern(row.Title));
                t.SetText(ref t.Description, slot, s.Intern(row.Description));
                // An answer that carries no cover never blanks the one a fuller read already set — nothing
                // legitimately removes a cover (S2, mirrors the Track.cs Fix-4 guard).
                if (!row.Image.IsEmpty) t.SetText(ref t.Image, slot, s.Intern(row.Image));
                t.SetText(ref t.ShareUrl, slot, s.Intern(row.ShareUrl));
                // A thin answer with no server count (0) must not zero out a count a fuller read already established
                // (S2) — 0 is what an unset `StagedPlaylist.TrackCount` reads as, UNLESS this decoder actually saw
                // the wire's length field (the `PlaylistFields.TrackCount` bit below), in which case a real zero is
                // a real answer — a genuinely empty playlist — and must land, not stay stuck on a stale count.
                if (row.TrackCount > 0 || (row.Known & (uint)PlaylistFields.TrackCount) != 0)
                    t.TrackCount[slot] = row.TrackCount;
                if (!row.OwnerUri.IsEmpty) t.Owner[slot] = s.Slot(users, in row.OwnerUri);
                t.Applied(slot, (uint)PlaylistFields.Identity, editionGate, ref t.IdentityAuthority);   // a new edition is recorded as Full: a stale Thin card cannot retitle it, a later edition still can
            }

            // Bug A1: the COUNT-KNOWN bit, applied separately from Identity so a route that fills Identity without a
            // length (ListMetadataV2) never sets it. Shares `IdentityAuthority` — same wire family, same persisted
            // column (`PlaylistShape.IdentityFields`) — but its own group bit, so `Knows(Identity)` staying true
            // can never be read as "the count is known too".
            // The group writes its OWN value (wave D2): a row that knows its count and nothing else — a list restored
            // from disk (Store.Lists.cs), a row whose `track_count` was rewritten with its settled list — lands the
            // count without pretending to know Identity. A row that also carries Identity wrote the same value above;
            // one whose Identity lost the authority gate still fills the count HOLE, which is D16's own rule.
            if ((row.Known & (uint)PlaylistFields.TrackCount) != 0
                && t.Accepts(slot, (uint)PlaylistFields.TrackCount, authority, in t.IdentityAuthority))
            {
                t.TrackCount[slot] = row.TrackCount;
                t.Applied(slot, (uint)PlaylistFields.TrackCount, authority, ref t.IdentityAuthority);
            }

            if ((row.Known & (uint)PlaylistFields.Capabilities) != 0
                && t.Accepts(slot, (uint)PlaylistFields.Capabilities, authority, in t.IdentityAuthority))
            {
                t.Caps[slot] = row.Caps;
                t.Applied(slot, (uint)PlaylistFields.Capabilities, authority, ref t.IdentityAuthority);
            }

            if ((row.Known & (uint)PlaylistFields.Visibility) != 0
                && t.Accepts(slot, (uint)PlaylistFields.Visibility, authority, in t.IdentityAuthority))
            {
                t.SetText(ref t.PermissionRevision, slot, s.Intern(row.PermissionRevision));
                t.Applied(slot, (uint)PlaylistFields.Visibility, authority, ref t.IdentityAuthority);
            }

            if ((row.Known & (uint)PlaylistFields.Format) != 0
                && t.Accepts(slot, (uint)PlaylistFields.Format, editionGate, in t.IdentityAuthority))
            {
                t.Format[slot] = row.Format;
                // A Format answer with no masthead (PlaylistRead stages neither) never blanks the one the feed set —
                // except for a new edition, whose empty value replaces both: the old masthead belongs to the wrong
                // edition. Same shape as the Image guard above.
                if (!row.HeaderImage.IsEmpty || newEdition) t.SetText(ref t.HeaderImage, slot, s.Intern(row.HeaderImage));
                if (!row.GenericTitle.IsEmpty || newEdition) t.SetText(ref t.GenericTitle, slot, s.Intern(row.GenericTitle));
                t.Applied(slot, (uint)PlaylistFields.Format, editionGate, ref t.IdentityAuthority);
            }

            if ((row.Known & (uint)PlaylistFields.Saves) != 0
                && t.Accepts(slot, (uint)PlaylistFields.Saves, authority, in t.ExtrasAuthority))
            {
                t.Saves[slot] = row.Saves;
                t.Applied(slot, (uint)PlaylistFields.Saves, authority, ref t.ExtrasAuthority);
            }

            if ((row.Known & (uint)PlaylistFields.Daylist) != 0
                && t.Accepts(slot, (uint)PlaylistFields.Daylist, editionGate, in t.ExtrasAuthority))
            {
                t.DaylistExpiresAt[slot] = row.DaylistExpiresAt;
                t.DaylistCreatedAt[slot] = row.DaylistCreatedAt;
                t.Applied(slot, (uint)PlaylistFields.Daylist, editionGate, ref t.ExtrasAuthority);
            }

            if ((row.Known & (uint)PlaylistFields.Chart) != 0
                && t.Accepts(slot, (uint)PlaylistFields.Chart, authority, in t.ExtrasAuthority))
            {
                t.ChartNewEntries[slot] = row.ChartNewEntries;
                t.ChartUpdatedAt[slot] = row.ChartUpdatedAt;
                t.SetText(ref t.ChartRankType, slot, s.Intern(row.ChartRankType));
                t.Applied(slot, (uint)PlaylistFields.Chart, authority, ref t.ExtrasAuthority);
            }

            if ((row.Known & (uint)PlaylistFields.Accent) != 0
                && t.Accepts(slot, (uint)PlaylistFields.Accent, authority, in t.ExtrasAuthority))
            {
                t.Accent[slot] = row.Accent;
                t.Applied(slot, (uint)PlaylistFields.Accent, authority, ref t.ExtrasAuthority);
            }

            if ((row.Known & (uint)PlaylistFields.Tuning) != 0
                && t.Accepts(slot, (uint)PlaylistFields.Tuning, authority, in t.ExtrasAuthority))
            {
                t.SetText(ref t.TuningSelected, slot, s.Intern(row.TuningSelected));
                t.TuningRevision[slot] = row.TuningRevision;
                t.Applied(slot, (uint)PlaylistFields.Tuning, authority, ref t.ExtrasAuthority);
            }

            if (!row.Revision.IsEmpty) t.SetText(ref t.Revision, slot, s.Intern(row.Revision));   // the newest head, ungated
            // Flags last and outside the group gates: a mask says exactly which bits this answer speaks about, the
            // latching tombstone is never cleared, and the app's own bits (create lifecycle, the derived column facts)
            // are nobody's business but ours.
            if (row.FlagsMask != 0 || row.Flags != 0)
            {
                uint merged = PlaylistTable.MergeFlags(t.Flags[slot], row.Flags, row.FlagsMask);
                if (merged != t.Flags[slot]) { t.Flags[slot] = merged; t.Bump(slot); }
            }

            // Wave 5 (owner O): the two facts derived from what just landed.
            if ((row.Known & (uint)(PlaylistFields.Identity | PlaylistFields.Capabilities)) != 0)
                DeriveOwner(t, slot, Current.MeSlot);
            DeriveNotice(t, slot);
        }

        CommitTuningOptions(s);
    }

    /// <summary>0.2.9 <c>PlaylistFetcher.CapabilitiesOf</c>: owner = the owner row is the account OR the server granted
    /// permission administration, and an owner administers. The decoder cannot see "me"; the commit can.</summary>
    static void DeriveOwner(PlaylistTable t, int slot, int meSlot)
    {
        if (!t.Knows(slot, (uint)PlaylistFields.Capabilities)) return;
        byte caps = t.Caps[slot];
        bool owner = (meSlot > Table.None && t.Owner[slot] == meSlot)
                     || (caps & (byte)(PlaylistCaps.CanAdministratePermissions | PlaylistCaps.IsOwner)) != 0;
        byte next = owner ? (byte)(caps | (byte)(PlaylistCaps.IsOwner | PlaylistCaps.CanAdministratePermissions)) : caps;
        if (next != caps) { t.Caps[slot] = next; t.Bump(slot); }
    }

    /// <summary>THE NOTICE COLUMN (file header item 2): <c>Detail.NoticeRules.Next</c> over the row as it now stands, the
    /// column's own verdict as <c>prev</c> — sticky exactly as the rule is.</summary>
    static void DeriveNotice(PlaylistTable t, int slot)
    {
        var prev = (DetailNotice)t.Notice[slot];
        uint flags = t.Flags[slot];
        bool knowsCaps = t.Knows(slot, (uint)PlaylistFields.Capabilities);
        var caps = (PlaylistCaps)t.Caps[slot];
        var next = Detail.NoticeRules.Next(prev == DetailNotice.MinifiedAlbum ? DetailNotice.None : prev,
            freshIsNull: false,
            headerDeleted: (flags & (uint)PlaylistFlags.DeletedByOwner) != 0,
            capabilitiesKnown: knowsCaps,
            canView: global::Wavee.Playlist.CanViewOf(knowsCaps, caps),
            isOwner: (caps & PlaylistCaps.IsOwner) != 0,
            isCreatePending: (flags & (uint)PlaylistFlags.CreatePending) != 0);
        if ((flags & (uint)PlaylistFlags.CreateFailed) != 0) next = DetailNotice.CreateFailed;
        if (next == prev) return;
        t.Notice[slot] = (byte)next;
        t.Bump(slot);
    }

    static TuningEdge[] s_tuning = new TuningEdge[16];

    /// <summary>Each contiguous staged run naming one playlist is its WHOLE Tune list; one blank row = "no options".</summary>
    static void CommitTuningOptions(Staging s)
    {
        var staged = s.StagedTuningOptions;
        if (staged is null || staged.Count == 0) return;
        var rows = staged.Span;
        var playlists = Current.Playlists;
        for (int start = 0; start < rows.Length;)
        {
            int end = start + 1;
            while (end < rows.Length && rows[end].Playlist.Packed == rows[start].Playlist.Packed
                   && rows[end].Playlist.Text == rows[start].Playlist.Text) end++;
            int slot = s.Slot(playlists, in rows[start].Playlist);
            if (slot != Table.None)
            {
                if (s_tuning.Length < end - start) s_tuning = new TuningEdge[Math.Max(end - start, s_tuning.Length * 2)];
                int n = 0;
                for (int i = start; i < end; i++)
                {
                    if (rows[i].Identifier.IsEmpty) continue;
                    s_tuning[n++] = new TuningEdge(s.Intern(rows[i].Identifier), s.Intern(rows[i].DisplayName), rows[i].Kind);
                }
                new global::Wavee.Playlist(slot).ReplaceTuningOptions(s_tuning.AsSpan(0, n));
            }
            start = end;
        }
    }
}

// ── persistence (Store.cs's per-kind seam) ───────────────────────────────────────────────────────────────────────────

/// <summary>How a playlist header survives a restart. Persists <see cref="PlaylistFields.Identity"/>,
/// <see cref="PlaylistFields.Capabilities"/>, <see cref="PlaylistFields.Visibility"/> and
/// <see cref="PlaylistFields.TrackCount"/> (bug A1's count-known bit round-trips through the SAME identity_auth
/// column and the existing <c>track_count</c> column — no new sqlite column — so a restart never re-blesses a
/// row's count as known off a thin ListMetadataV2 answer, the way the Track table's Artists bit once did, ch 03
/// bug C) (they share
/// <see cref="PlaylistTable.IdentityAuthority"/> in memory, so this shape shares one <c>identity_auth</c> column for
/// all four, bound whenever any is known — the same pattern <see cref="TrackShape"/> uses for its cold groups), plus
/// <see cref="PlaylistFields.Accent"/> and <see cref="PlaylistFields.Saves"/> (sharing <c>extras_auth</c>).
///
/// <para><b>THE PERSISTED COUNT IS AUTHORITATIVE.</b> <c>track_count</c> is rewritten — with the count-known bit — in
/// the SAME transaction as a settled membership list (<c>Store.Lists.cs</c>, <see cref="PlaylistShape.CountColumn"/>), so a stored
/// 0 known is a real, empty playlist and loads as one: a restored empty list is an answer. The load-time
/// "a persisted 0 is unknown" mask that healed bug A1's corrupted rows is gone — those rows live in a file this schema
/// never opens (the file's NAME carries the schema, <see cref="Store.FileName"/>), and a mask would turn every genuinely
/// empty playlist into a re-ask on every launch.</para>
///
/// <para><b>Deliberately NOT persisted:</b> <see cref="PlaylistFields.Format"/>/<see cref="PlaylistFields.Daylist"/>/
/// <see cref="PlaylistFields.Chart"/>/<see cref="PlaylistFields.Tuning"/> (each cheap to re-ask and, for Tuning, an
/// edge this shape does not persist). Daylist and Format stay unpersisted on purpose: a held
/// <see cref="PlaylistTable.DaylistExpiresAt"/> of 0 is the edition-unknown state a relaunch relies on — any window
/// outranks it (<see cref="DaylistEdition.Outranks"/>), so the feed's Thin write of the new edition is accepted over
/// the persisted Full Identity instead of being rejected as a downgrade; <see cref="PlaylistTable.Flags"/> — it LATCHES a tombstone bit
/// (<see cref="PlaylistFlags.DeletedByOwner"/>) through a merge (<see cref="PlaylistTable.MergeFlags"/>) that is not
/// a plain per-group authority write, and persisting it half-right (accepting the merge's real semantics) is a
/// follow-up, not a silent approximation; <see cref="PlaylistTable.Notice"/> — commit-DERIVED
/// (<see cref="Entities.DeriveNotice"/>, called every commit) and re-derives itself the moment the row commits
/// again; and <see cref="PlaylistTable.Revision"/> — which is not a ROW fact at all: a revision describes the
/// MEMBERSHIP, so it lives in <c>list_head</c> beside the rows it is true of, written in the same transaction
/// (Store.Lists.cs). There is no revision column here, so a row write can never advance it, and a restart restores it
/// only together with a list it describes.</para>
///
/// <para>STORE THREAD (both halves) — see <see cref="ShowShape"/>'s note.</para></summary>
public sealed class PlaylistShape : KindShape
{
    /// <summary>The count column, named once: <c>Store.Lists.cs</c> rewrites it inside a settled list's transaction
    /// (the list's total IS the count), and this shape binds it at ordinal 4 on both halves.</summary>
    public const string CountColumn = "track_count";

    static readonly StoreColumn[] Cols =
    [
        new("title", StoreType.Text, StoreColumnFlags.Title),
        new("description", StoreType.Text),
        new("image", StoreType.Text),
        new("share_url", StoreType.Text),
        new(CountColumn, StoreType.Int),
        new("owner_uri", StoreType.Text),
        new("caps", StoreType.Int),
        new("permission_revision", StoreType.Text),
        new("accent", StoreType.Int),
        new("saves", StoreType.Int),
        new("identity_auth", StoreType.Int, StoreColumnFlags.Authority),
        new("extras_auth", StoreType.Int, StoreColumnFlags.Authority),
    ];

    // Bug A1: TrackCount rides in this same group. Identity is always co-present whenever TrackCount is known (both
    // playlist decoders stamp Identity applied on the SAME row they may also stamp TrackCount on), so folding it in
    // here costs nothing extra and means a restart restores "the count is real" exactly when it was real, instead of
    // forcing a re-ask of every visible row on every launch.
    const uint IdentityFields = (uint)(PlaylistFields.Identity | PlaylistFields.Capabilities | PlaylistFields.Visibility
                                      | PlaylistFields.TrackCount);
    const uint ExtrasFields = (uint)(PlaylistFields.Accent | PlaylistFields.Saves);
    const uint PersistedFields = IdentityFields | ExtrasFields;

    public override EntityKind Kind => EntityKind.Playlist;
    public override string Table => "playlist";
    public override ReadOnlySpan<StoreColumn> Columns => Cols;

    public override void Save(Staging s, RowWriter w)
    {
        var rows = s.StagedPlaylists;
        if (rows is null) return;
        var span = rows.Span;
        for (int i = 0; i < span.Length; i++)
        {
            ref readonly var row = ref span[i];
            uint known = row.Known & PersistedFields;
            bool identityGroup = (known & IdentityFields) != 0;

            if ((known & (uint)PlaylistFields.Identity) != 0)
            {
                w.Text(0, row.Title);
                w.Text(1, row.Description);
                w.Text(2, row.Image);
                w.Text(3, row.ShareUrl);
                w.Int(4, row.TrackCount);
                w.Id(5, s, row.OwnerUri);
            }
            else { w.Null(0); w.Null(1); w.Null(2); w.Null(3); w.Null(4); w.Null(5); }

            if ((known & (uint)PlaylistFields.Capabilities) != 0) w.Int(6, row.Caps); else w.Null(6);
            if ((known & (uint)PlaylistFields.Visibility) != 0) w.Text(7, row.PermissionRevision); else w.Null(7);
            if (identityGroup) w.Int(10, (int)row.Authority); else w.Null(10);

            if ((known & (uint)PlaylistFields.Accent) != 0) w.Int(8, row.Accent); else w.Null(8);
            if ((known & (uint)PlaylistFields.Saves) != 0) w.Int(9, row.Saves); else w.Null(9);
            if ((known & ExtrasFields) != 0) w.Int(11, (int)row.Authority); else w.Null(11);

            w.Emit(row.Id, known, Entities.Now, Entities.Now);
        }
    }

    public override void Load(RowReader r, Staging into)
    {
        ref var row = ref into.Playlists.Add();
        row.Id = r.Uri;
        row.Title = r.Text(0);
        row.Description = r.Text(1);
        row.Image = r.Text(2);
        row.ShareUrl = r.Text(3);
        row.TrackCount = (int)r.Int(4);
        row.OwnerUri = r.Text(5);
        row.Caps = (byte)r.Int(6);
        row.PermissionRevision = r.Text(7);
        row.Accent = (uint)r.Int(8);
        row.Saves = (int)r.Int(9);
        row.Known = r.Known & PersistedFields;
        row.Authority = (Authority)Math.Max(r.Int(10), r.Int(11));
    }
}

/// <summary>One staged Tune option (ch 06 W24); a blank <see cref="Identifier"/> alone in its run = "no options".</summary>
public struct StagedTuningOption
{
    public StagedId Playlist;
    public TextRef Identifier, DisplayName;
    public byte Kind;
}

public sealed partial class Staging
{
    StagedList<StagedTuningOption>? _tuningOptions;
    public StagedList<StagedTuningOption> PlaylistTuningOptions => _tuningOptions ??= Register(new StagedList<StagedTuningOption>());
    internal StagedList<StagedTuningOption>? StagedTuningOptions => _tuningOptions;
}

// ── 7. Wave 5 data: tuning, recommendations, collaborators, revision, the refold (WP-5.O §2.5) ─────────────────────────

/// <summary>What one session-control option does.</summary>
public enum TuningOptionKind : byte { Choice = 0, Reset = 1 }
/// <summary>One Tune option; both strings OWNED by the edge (<see cref="Edges.ReleaseTuningText"/>).</summary>
public readonly record struct TuningEdge(StringId Identifier, StringId DisplayName, byte Kind);

public sealed partial class Edges
{
    /// <summary>Parent = playlist slot; PAYLOAD-ONLY (targets unused): the Tune options in server order (ch 06 W24).</summary>
    public readonly EdgeTable<TuningEdge> PlaylistTuning = new();
    /// <summary>Parent = playlist slot, targets = track slots: the extender's "Recommended songs" (ch 06 W10).</summary>
    public readonly EdgeTable<NoEdge> PlaylistRecs = new();

    /// <summary>Give back the strings one playlist's Tune options own (before a replace; per parent at scope retire).</summary>
    internal void ReleaseTuningText(int parent)
    {
        var rows = PlaylistTuning.Payload(parent);
        for (int i = 0; i < rows.Length; i++) { Entities.Strings.Release(rows[i].Identifier); Entities.Strings.Release(rows[i].DisplayName); }
    }
}

public readonly partial struct Playlist
{
    public ReadOnlySpan<TuningEdge> TuningOptions => E.PlaylistTuning.Payload(Slot);
    public ReadOnlySpan<int> RecommendationSlots => E.PlaylistRecs.Targets(Slot);
    public EdgeState RecommendationsState => E.PlaylistRecs.State(Slot);
    public uint TuningRevision => T.TuningRevision[Slot];
    /// <summary>The membership revision (<c>{counter},{hex}</c>), or empty.</summary>
    public StringId RevisionId => T.Revision[Slot];

    /// <summary>0.2.9: the options are valid only while their revision hash matches the membership's (0 / unknown = valid).</summary>
    public bool TuningCurrent
        => TuningRevision == 0 || RevisionId.IsEmpty || RevisionHash(Entities.Strings.Resolve(RevisionId)) == TuningRevision;

    /// <summary>FNV-1a over the revision's wire spelling (0 for none). Pure.</summary>
    public static uint RevisionHash(ReadOnlySpan<char> revision)
    {
        uint h = 2166136261;
        for (int i = 0; i < revision.Length; i++) { h ^= revision[i]; h *= 16777619; }
        return revision.IsEmpty ? 0 : h;
    }

    /// <summary>Whole tuning write (UI thread): options (AddRef'd first, the previous released after), the selected id
    /// (Empty = untuned), the revision hash; marks <see cref="PlaylistFields.Tuning"/> known.</summary>
    public void ApplyTuning(ReadOnlySpan<TuningEdge> options, StringId selected, uint revision)
    {
        ReplaceTuningOptions(options);
        T.SetText(ref T.TuningSelected, Slot, selected);
        T.TuningRevision[Slot] = revision;
        T.Bump(Slot, (uint)PlaylistFields.Tuning);
    }

    internal void ReplaceTuningOptions(ReadOnlySpan<TuningEdge> options)
    {
        for (int i = 0; i < options.Length; i++) { Entities.Strings.AddRef(options[i].Identifier); Entities.Strings.AddRef(options[i].DisplayName); }
        E.ReleaseTuningText(Slot);
        Span<int> targets = options.Length <= 64 ? stackalloc int[options.Length] : new int[options.Length];
        targets.Clear();
        E.PlaylistTuning.ReplaceRun(Slot, targets, options);
    }

    /// <summary>Whole recommendations write (UI thread).</summary>
    public void ApplyRecommendations(ReadOnlySpan<int> trackSlots)
    {
        E.PlaylistRecs.ReplaceRun(Slot, trackSlots, ReadOnlySpan<NoEdge>.Empty);
        T.Bump(Slot);
    }

    /// <summary>Collaborators are DERIVED (0.2.9): the owner first, then distinct <c>AddedBy</c> slots in first-seen order.</summary>
    public int CollaboratorSlots(Span<int> into)
    {
        int n = 0;
        int owner = T.Owner[Slot];
        if (owner > Table.None && into.Length > 0) into[n++] = owner;
        var edges = E.PlaylistTracks.Payload(Slot);
        for (int i = 0; i < edges.Length && n < into.Length; i++)
        {
            int by = edges[i].AddedBy;
            if (by > Table.None && into[..n].IndexOf(by) < 0) into[n++] = by;
        }
        return n;
    }

    /// <summary>Re-fold <see cref="PlaylistFacts"/> over the resident membership; writes (and bumps) ONLY when an answer
    /// moved, so a page effect and a commit hook can both call it without a publish loop. UI thread.
    /// <para>Whether a row IS an episode comes from the edge's own <see cref="PlaylistTrackEdge.Kind"/>
    /// (<see cref="PlaylistItemKind"/>, plan §3.1) — never from a flag on the target row — because the target slot
    /// itself indexes a DIFFERENT table for each: <see cref="Entities.Current"/>.<c>Tracks</c> for
    /// <see cref="PlaylistItemKind.Track"/>, <c>Episodes</c> for <see cref="PlaylistItemKind.Episode"/>.</para></summary>
    public bool Refold()
    {
        if (!IsValid) return false;
        var slots = E.PlaylistTracks.Targets(Slot);
        var edges = E.PlaylistTracks.Payload(Slot);
        var tracks = Entities.Current.Tracks;
        var episodes = Entities.Current.Episodes;
        var facts = default(PlaylistFacts);
        for (int i = 0; i < slots.Length; i++)
        {
            int ts = slots[i];
            bool isEpisode = i < edges.Length && edges[i].Kind == PlaylistItemKind.Episode;
            int durationMs;
            bool hasVideo;
            if (isEpisode)
            {
                bool row = ts > Table.None && ts < episodes.Count;
                durationMs = row ? episodes.DurationMs[ts] : 0;
                hasVideo = row && (episodes.Flags[ts] & (uint)EpisodeFlags.Video) != 0;
            }
            else
            {
                bool row = ts > Table.None && ts < tracks.Count;
                uint flags = row ? tracks.Flags[ts] : 0;
                durationMs = row ? tracks.DurationMs[ts] : 0;
                hasVideo = (flags & (uint)TrackFlags.VideoMask) != 0;
            }
            facts.Add(i < edges.Length ? edges[i].AddedBy : 0, i < edges.Length ? edges[i].AddedAt : 0,
                      durationMs, isEpisode, hasVideo);
        }
        const uint derived = (uint)(PlaylistFlags.HasAddedBy | PlaylistFlags.HasDateAdded | PlaylistFlags.HasVideo | PlaylistFlags.Mixed);
        if ((T.Flags[Slot] & derived) == facts.Flags && T.EpisodeCount[Slot] == facts.Episodes
            && T.DurationMs[Slot] == facts.DurationMs) return false;
        Apply(in facts);
        return true;
    }
}

// ── 8. the ported rule sets (ch 06 §8, verbatim; inputs are values, never engine types) ──────────────────────────────

/// <summary>Which EDIT failed. The kind says what went wrong; the verb says what the user was doing.</summary>
public enum PlaylistEditVerb : byte { Generic = 0, Add, Remove, Reorder, Rename }
/// <summary>The one failure vocabulary a playlist write surfaces (0.2.9 <c>SeamPorts.PlaylistMutationFailure</c>).</summary>
public enum PlaylistMutationFailure : byte { Unknown = 0, Conflict, Forbidden, Deleted, Offline, Pending, NotSupported, NoOp, Invalid }
/// <summary>The ONLY failure type a playlist mutation surfaces to the UI.</summary>
public sealed class PlaylistMutationException(PlaylistMutationFailure kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public PlaylistMutationFailure Kind { get; } = kind;
}

/// <summary>0.2.9 <c>PlaylistEditErrorKinds</c>: exception → kind → loc KEY per (kind × verb), and the severity split.</summary>
public static class PlaylistEditErrorKinds
{
    /// <summary>The typed failure wins anywhere in the inner / aggregate chain; <see cref="NotSupportedException"/> is NotSupported.</summary>
    public static PlaylistMutationFailure KindOf(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is PlaylistMutationException typed) return typed.Kind;
            if (e is AggregateException agg && agg.InnerExceptions.Count > 0)
                for (int i = 0; i < agg.InnerExceptions.Count; i++)
                {
                    var k = KindOf(agg.InnerExceptions[i]);
                    if (k != PlaylistMutationFailure.Unknown) return k;
                }
            if (e is NotSupportedException) return PlaylistMutationFailure.NotSupported;
        }
        return PlaylistMutationFailure.Unknown;
    }

    /// <summary>An HTTP status off a write → the kind. 0 is a LOST edit in 0.3 (no offline outbox): Unknown, not Offline.</summary>
    public static PlaylistMutationFailure KindOfStatus(int status) => status switch
    {
        409 => PlaylistMutationFailure.Conflict,
        401 or 403 => PlaylistMutationFailure.Forbidden,
        404 or 410 => PlaylistMutationFailure.Deleted,
        _ => PlaylistMutationFailure.Unknown,
    };

    public static string KeyFor(PlaylistMutationFailure kind, PlaylistEditVerb verb = PlaylistEditVerb.Generic) => kind switch
    {
        PlaylistMutationFailure.Conflict => verb == PlaylistEditVerb.Reorder ? Strings.Detail.Edit.ReorderConflict : Strings.Detail.Edit.Conflict,
        PlaylistMutationFailure.Forbidden => Strings.Detail.Edit.Forbidden,
        PlaylistMutationFailure.Deleted => Strings.Detail.Edit.DeletedElsewhere,
        PlaylistMutationFailure.Offline => Strings.Detail.Edit.QueuedOffline,
        PlaylistMutationFailure.Pending => verb == PlaylistEditVerb.Reorder ? Strings.Drag.StillSyncing : Strings.Detail.Edit.PendingSync,
        PlaylistMutationFailure.NotSupported => Strings.Detail.Edit.OfflineSpotifyEdits,
        PlaylistMutationFailure.NoOp => verb == PlaylistEditVerb.Reorder ? Strings.Drag.AlreadyThere : Strings.Detail.Edit.Failed,
        PlaylistMutationFailure.Invalid => verb == PlaylistEditVerb.Reorder ? Strings.Drag.CantMoveHere : Strings.Detail.Edit.Failed,
        _ => Strings.Detail.Edit.Failed,
    };

    /// <summary>Kept / not-a-failure outcomes are Informational; everything else is an Error.</summary>
    public static bool IsInformational(PlaylistMutationFailure kind)
        => kind is PlaylistMutationFailure.Offline or PlaylistMutationFailure.Pending
                or PlaylistMutationFailure.NoOp or PlaylistMutationFailure.Invalid;
}

/// <summary>One playlist a deposit could land in — 0.2.9's <c>PlaylistSummary</c> reduced to what the rule reads.</summary>
public readonly record struct DepositCandidate(string Uri, string Name, bool CanEdit, int Slot = 0);

/// <summary>0.2.9 <c>PlaylistDepositTargets</c>: the ONE eligibility predicate and the ONE order (MRU first, then
/// rootlist order) shared by "Add to playlist ▸", "Move to playlist ▸" and the picker (ch 06 §0.14).</summary>
public static class PlaylistDepositTargets
{
    public const int MaxInline = 10;
    public const int MaxRecent = 8;

    /// <summary>A real Spotify PLAYLIST uri: not Liked (Collection), not a route key, not <c>wavee:playlist:</c>.</summary>
    public static bool IsDepositable(string? uri)
        => uri is { Length: > 0 } && EntityUri.ProviderOf(uri.AsSpan(), out var kind) == EntityProvider.Spotify
           && kind == EntityKind.Playlist;

    public static bool IsEligible(in DepositCandidate p, string? excludeUri = null)
        => IsDepositable(p.Uri) && p.CanEdit
           && !(excludeUri is { Length: > 0 } && string.Equals(p.Uri, excludeUri, StringComparison.Ordinal));

    /// <summary>The eligible playlists, most-recently-deposited first, then rootlist order; a stale recent is skipped;
    /// <paramref name="query"/> is an ordinal-case-insensitive name filter. Stable.</summary>
    public static List<DepositCandidate> Order(IReadOnlyList<DepositCandidate>? playlists,
        IReadOnlyList<string>? recentUris = null, string? excludeUri = null, string? query = null)
    {
        var ordered = new List<DepositCandidate>(playlists?.Count ?? 0);
        if (playlists is not { Count: > 0 }) return ordered;
        if (recentUris is { Count: > 0 })
            for (int r = 0; r < recentUris.Count; r++)
            {
                string uri = recentUris[r];
                if (!IsDepositable(uri)) continue;
                for (int i = 0; i < playlists.Count; i++)
                {
                    var p = playlists[i];
                    if (!string.Equals(p.Uri, uri, StringComparison.Ordinal)) continue;
                    if (IsEligible(in p, excludeUri) && Matches(p.Name, query) && !AlreadyOrdered(ordered, p.Uri)) ordered.Add(p);
                    break;
                }
            }
        for (int i = 0; i < playlists.Count; i++)
        {
            var p = playlists[i];
            if (IsEligible(in p, excludeUri) && Matches(p.Name, query) && !AlreadyOrdered(ordered, p.Uri)) ordered.Add(p);
        }
        return ordered;
    }

    /// <summary>The MRU with <paramref name="uri"/> promoted to the front, deduped, capped at <see cref="MaxRecent"/>.</summary>
    public static List<string> Remember(IReadOnlyList<string>? recentUris, string? uri)
    {
        var next = new List<string>(MaxRecent);
        if (IsDepositable(uri)) next.Add(uri!);
        if (recentUris is { Count: > 0 })
            for (int i = 0; i < recentUris.Count && next.Count < MaxRecent; i++)
            {
                string u = recentUris[i];
                if (IsDepositable(u) && !next.Contains(u)) next.Add(u);
            }
        return next;
    }

    /// <summary>The MRU codec: newline-joined; empty segments dropped on read, non-depositable ones never written.</summary>
    public static List<string> Parse(string? stored)
    {
        var uris = new List<string>();
        if (string.IsNullOrEmpty(stored)) return uris;
        foreach (var part in stored.Split('\n', StringSplitOptions.RemoveEmptyEntries)) uris.Add(part);
        return uris;
    }

    public static string Serialize(IReadOnlyList<string>? uris)
    {
        if (uris is not { Count: > 0 }) return "";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < uris.Count; i++)
            if (IsDepositable(uris[i])) (sb.Length > 0 ? sb.Append('\n') : sb).Append(uris[i]);
        return sb.ToString();
    }

    /// <summary>The next unused "<c>{base} #N</c>" — forwards to the ONE implementation, <c>Spotify.Encode.NextPlaylistName</c>.</summary>
    public static string NextDefaultName(IReadOnlyList<DepositCandidate>? playlists, string baseName)
    {
        var taken = new string[playlists?.Count ?? 0];
        for (int i = 0; i < taken.Length; i++) taken[i] = playlists![i].Name ?? "";
        return Spotify.Encode.NextPlaylistName(taken, baseName);
    }

    static bool Matches(string? name, string? query)
        => string.IsNullOrEmpty(query) || (name is not null && name.Contains(query, StringComparison.OrdinalIgnoreCase));

    static bool AlreadyOrdered(List<DepositCandidate> ordered, string uri)
    {
        for (int i = 0; i < ordered.Count; i++)
            if (string.Equals(ordered[i].Uri, uri, StringComparison.Ordinal)) return true;
        return false;
    }
}

/// <summary>One Tune option as the pure model reads it.</summary>
public readonly record struct TuneOption(string Identifier, string? DisplayName, TuningOptionKind Kind);

/// <summary>0.2.9 <c>PlaylistTuneMenuModel</c>: eligibility, the visible choices, the Reset gate (null while untuned, item 56).</summary>
public static class PlaylistTuneMenuModel
{
    public static bool IsEligible(IReadOnlyList<TuneOption>? options, bool sourceAvailable)
    {
        if (!sourceAvailable || options is null) return false;
        for (int i = 0; i < options.Count; i++)
            if (options[i].Kind == TuningOptionKind.Choice && !string.IsNullOrWhiteSpace(options[i].DisplayName)) return true;
        return false;
    }

    public static List<TuneOption> VisibleChoices(IReadOnlyList<TuneOption> options)
    {
        var visible = new List<TuneOption>(options.Count);
        foreach (var o in options) if (o.Kind == TuningOptionKind.Choice && !string.IsNullOrWhiteSpace(o.DisplayName)) visible.Add(o);
        return visible;
    }

    public static TuneOption? ResetOption(IReadOnlyList<TuneOption> options, string? selectedIdentifier)
    {
        if (string.IsNullOrEmpty(selectedIdentifier)) return null;
        for (int i = 0; i < options.Count; i++)
            if (options[i].Kind == TuningOptionKind.Reset) return options[i];
        return null;
    }
}

// ── 9. local files (0.2.9 LocalPlayables + PlayableUri). The uri↔path codec is FOR WP-6.T TO ADOPT OR REPLACE. ──────
public readonly partial struct Playlist
{
    public const string LocalFilePrefix = "wavee:local:file:";
    /// <summary><c>wavee:local:file:</c> + base64url(UTF-8 path), unpadded — byte-identical to 0.2.9's.</summary>
    public static string LocalFileUri(string absolutePath)
        => LocalFilePrefix + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(absolutePath ?? ""))
               .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The path behind a local-file identity; null for anything else or a malformed payload.</summary>
    public static string? LocalPathOf(EntityId id)
    {
        if (id.Provider != EntityProvider.Local) return null;
        string text = id.Text;
        if (!text.StartsWith(LocalFilePrefix, StringComparison.Ordinal) || text.Length == LocalFilePrefix.Length) return null;
        string b64 = text[LocalFilePrefix.Length..].Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight((b64.Length + 3) & ~3, '=');
        var bytes = new byte[b64.Length];
        return Convert.TryFromBase64String(b64, bytes, out int n) ? System.Text.Encoding.UTF8.GetString(bytes, 0, n) : null;
    }

    /// <summary>0.2.9 <c>LocalPlayables.TitleOf</c> verbatim: the file name without its extension; a URL's last segment.</summary>
    public static string LocalTitleOf(string pathOrUrl)
    {
        if (string.IsNullOrWhiteSpace(pathOrUrl)) return "";
        if (pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            int q = pathOrUrl.IndexOfAny(['?', '#']);
            string trimmed = q >= 0 ? pathOrUrl[..q] : pathOrUrl;
            int slash = trimmed.LastIndexOf('/');
            string tail = slash >= 0 && slash + 1 < trimmed.Length ? trimmed[(slash + 1)..] : trimmed;
            return tail.Length > 0 ? tail : pathOrUrl;
        }
        string name = System.IO.Path.GetFileNameWithoutExtension(pathOrUrl);   // .NET Core: never throws on a bad char
        return name.Length > 0 ? name : pathOrUrl;
    }

    public enum LocalDropAction : byte { None, PlayAudio, PlayVideo }

    public static bool IsLocalAudioFile(string? path)
        => path is { Length: > 0 } && (path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".flac", StringComparison.OrdinalIgnoreCase));
    public static bool IsLocalVideoFile(string? path) => path is { Length: > 0 } && path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);

    /// <summary>0.2.9 <c>LocalPlayables.ClassifyDrop</c>: audio wins over video; the first playable path is picked.</summary>
    public static LocalDropAction ClassifyDrop(IReadOnlyList<string>? paths, out string picked)
    {
        picked = "";
        if (paths is null) return LocalDropAction.None;
        for (int i = 0; i < paths.Count; i++)
            if (IsLocalAudioFile(paths[i])) { picked = paths[i]; return LocalDropAction.PlayAudio; }
        for (int i = 0; i < paths.Count; i++)
            if (IsLocalVideoFile(paths[i])) { picked = paths[i]; return LocalDropAction.PlayVideo; }
        return LocalDropAction.None;
    }
}
