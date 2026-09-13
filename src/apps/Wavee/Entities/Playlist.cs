// ── Entities/Playlist.cs — CORE (owner B, wave 1; plan §2, §4.3, ch 06 §7) ───────────────────────────────────────────
//
// THE PLAYLIST ROW: columns, the handle, the field groups, the staged shape a decoder fills and the commit that lands
// it. Wave 1 owns the DATA (plan §2, "two notes on the entity CORE rows"); the seven ported rule sets ch 06 §8 names —
// reorder, deposit targets, edit-error kinds, the tune menu, the drop table, membership diff, the notice verdict — are
// owner O's, in Wave 5, in this same file. What is here is what those rules and the page will read.
//
// SIX THINGS THIS FILE DECIDES, and each of them is a correctness fix over 0.2.9, not a translation:
//
//  1. CAPABILITIES ARE A KNOWN BIT, NOT A BOOL. 0.2.9's `PlaylistCapabilities` carried its own `Known` flag inside the
//     record because a rootlist-seeded thin header has no capabilities block, and `default` (all false) reads as
//     "revoked". Here the bits live in `Column<byte> Caps` and "did anyone tell us?" is `Knows(PlaylistFields.
//     Capabilities)` — the same question every other group answers, with no per-kind convention (P3, ch 06 §7 gap 2).
//
//  2. THE NOTICE IS A COLUMN. `PlaylistPageNoticeRules.Next` is STATEFUL (the verdict is sticky: CreateFailed is
//     terminal, a create-pending suppresses the deleted verdict), so it cannot be a UI-local — a remount would forget
//     it and a re-render would recompute it from a `prev` nobody kept. It is written at commit and read as a column
//     (ch 06 §7, and the repo rule "derived facts live on the model").
//
//  3. THE THREE COLUMN-EXISTENCE FACTS ARE DERIVED AT COMMIT. "Does this table get an Added-by column / a Date-added
//     column / a Video column" is `distinct AddedBy ≥ 2` · `any AddedAt` · `any VideoPresence` over the WHOLE
//     membership (0.2.9 `DetailPage.cs:501-531`). Recomputing that per frame is a walk over 5,000 rows on the paint
//     path; recomputing it per commit is a walk once per answer. See <see cref="PlaylistFacts"/>, which folds it in one
//     pass with no allocation and no second scan for the distinctness.
//
//  4. THE LOCAL-FILES SURFACE IS A PLAYLIST, NOT A KIND. Plan §9.5 gives the `local` route to `Playlist.Page.cs`'s
//     `DetailKind.Playlist` arm, so imported files hang off one ordinary playlist row —
//     <see cref="Playlist.LocalFilesUri"/> — whose membership edge is the imported library and whose capabilities say
//     "not editable, not owned". Nothing else in the model has to learn about local files.
//
//  5. ONE ROW PER PLAYLIST, WHICHEVER SPELLING ARRIVES (defect 4 of the identity investigation,
//     docs/plans/wavee/wavee-0.3-entity-identity-memory.md §4.4). `spotify:user:<u>:playlist:<gid>` and
//     `spotify:playlist:<gid>` interned to two different `StringId`s and therefore allocated TWO `PlaylistTable` rows
//     for ONE playlist — two headers, two membership edges, two fetches, and whichever one the sidebar happened to
//     hold was the one that did not get the answer. The packed identity folds them IN THE PARSE: an id is the uri's
//     TRAILING segment, so both spellings decode to the same 128-bit gid, the same `EntityId` and the same slot
//     (`EntityId.TryParseGid`). A round trip formats the canonical `spotify:playlist:<gid>`, which Connect and deep
//     links accept — 0.2.9 already folds the Liked collection's spellings the same way.
//     UNVERIFIED whether the 0.3 wire path still emits the user-namespaced spelling at all (0.2.9's Home and recents
//     did). The fold costs nothing if it does not, and it is a decision written down rather than an accident, so it
//     stays either way. NOTE it is the GID that folds: a fixture id that is not 22 base62 characters
//     (`spotify:playlist:1a2b`) takes the text form, and there the two spellings are still two rows.
//
//  6. THE ROW OWNS ITS TEXT, AND GIVES IT BACK (defect 1, doc §4.4). The engine's interner reclaims an id only when
//     its last reference is released, and a string that was never AddRef'd is PERMANENT (the engine's
//     `StringTable.cs:26`), so before 2026-09-12 a trim freed a playlist row's 89 B of columns and leaked its title,
//     description, cover, permission revision, header image, generic title, chart rank type and tuning selection for
//     the life of the process — a scope's memory floor could only rise. Every `Column<StringId>` here is written
//     through <see cref="Table.SetText"/> and released in <see cref="PlaylistTable.ReleaseText"/>; there is no third
//     way, and the base declares `ReleaseText` abstract so this file cannot forget one.
//
// Rules: single writer, UI thread (C1); no LINQ, no closures, no async, no boxing (P8/P9); every text column is a
// REF-COUNTED StringId (P6, and item 6 above) and every timestamp is app-epoch seconds (P7).

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
    /// <summary>Title, description, cover, owner and the server's own track count — one wire shape fills all five.</summary>
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

    /// <summary>Flags a provider answer may SET but never CLEAR. Only the tombstone: 0.2.9 merges it as
    /// <c>incoming || current</c> precisely so a stale header cannot un-delete a playlist.</summary>
    public const uint LatchingFlags = (uint)PlaylistFlags.DeletedByOwner;

    /// <summary>Flags nobody but this app writes — the optimistic create pair and the four derived facts. A wire
    /// answer's clear mask must not touch them or a header refresh would erase the table's own column decisions.</summary>
    public const uint LocalFlags = (uint)(PlaylistFlags.CreatePending | PlaylistFlags.CreateFailed
        | PlaylistFlags.HasAddedBy | PlaylistFlags.HasDateAdded | PlaylistFlags.HasVideo | PlaylistFlags.Mixed);

    /// <summary>THE flag merge, pure so it can be tested without a scope. Three rules in one expression:
    /// an answer may only clear the bits its <paramref name="mask"/> claims; it may never clear a
    /// <see cref="LatchingFlags"/> bit (the tombstone: 0.2.9 merges it as <c>incoming || current</c> so a stale header
    /// cannot un-delete a playlist); and it may neither set nor clear a <see cref="LocalFlags"/> bit, because the
    /// create lifecycle and the four derived column facts are this app's arithmetic and not the server's.</summary>
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
    }

    /// <summary>Give back every string a playlist row owns (defect 1; file header item 6). One line per
    /// <c>Column&lt;StringId&gt;</c> declared above — miss one and its text is permanent; list one that some call
    /// site wrote DIRECTLY rather than through <see cref="Table.SetText"/> and this drops a reference the row never
    /// took, which is worse. The two halves land together or not at all.
    /// <para>Called by <see cref="Table.FreeSlot"/> (the store's trim, R2) and by <see cref="Table.ReleaseAllText"/>
    /// (a retired scope, D9).</para></summary>
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
    }
}

// ── 3. the membership fold (the three column-existence facts, ch 06 §7) ──────────────────────────────────────────────

/// <summary>One pass over a playlist's membership, folded into the facts the header and the table need. A struct with
/// no allocation and no second scan: the "≥ 2 distinct added-by" question is answered by remembering the FIRST
/// non-zero adder and latching as soon as a different one appears, which is what makes the distinctness O(n) with no
/// set (P8/P9).
///
/// <para>Deliberately free of every other kind's types: the caller passes the two per-row facts it can see
/// (<c>durationMs</c>, and whether the row is an episode / has a video counterpart), so this folds identically for a
/// live decode, the seed and a unit test, and does not bind Wave 1's playlist file to Wave 1's track file.</para></summary>
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
    /// <paramref name="addedAt"/> app-epoch seconds (0 = none).</summary>
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
    public bool Editable => EditableOf(Knows(PlaylistFields.Capabilities), Caps);
    public bool EditableMetadata => EditableMetadataOf(Knows(PlaylistFields.Capabilities), Caps);

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
    /// <summary>Land one page of the membership and re-derive the header facts that depend on it. This is the seam
    /// Wave 2's decoder and the seed both call: the edge write and the fold belong together, because a page that lands
    /// without re-folding leaves the table's Added-by column deciding on stale evidence.
    ///
    /// <para><paramref name="facts"/> is folded by the CALLER over the whole resident membership (it is the only party
    /// that can see the rows' durations and kinds); pass <c>default</c> to land the page without touching the derived
    /// facts, which is what a page-at-a-time decode does until its last page.</para></summary>
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

            if ((row.Known & (uint)PlaylistFields.Identity) != 0
                && t.Accepts(slot, (uint)PlaylistFields.Identity, authority, in t.IdentityAuthority))
            {
                // SetText, never `t.Title[slot] = …`: the write AddRefs the incoming id and releases the one it
                // overwrites, so a header re-answered a hundred times owns exactly one title's worth of interner at
                // the end of it (defect 1, file header item 6).
                t.SetText(ref t.Title, slot, s.Intern(row.Title));
                t.SetText(ref t.Description, slot, s.Intern(row.Description));
                t.SetText(ref t.Image, slot, s.Intern(row.Image));
                t.SetText(ref t.ShareUrl, slot, s.Intern(row.ShareUrl));
                t.TrackCount[slot] = row.TrackCount;
                if (!row.OwnerUri.IsEmpty) t.Owner[slot] = s.Slot(users, in row.OwnerUri);
                t.Applied(slot, (uint)PlaylistFields.Identity, authority, ref t.IdentityAuthority);
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
                && t.Accepts(slot, (uint)PlaylistFields.Format, authority, in t.IdentityAuthority))
            {
                t.Format[slot] = row.Format;
                t.SetText(ref t.HeaderImage, slot, s.Intern(row.HeaderImage));
                t.SetText(ref t.GenericTitle, slot, s.Intern(row.GenericTitle));
                t.Applied(slot, (uint)PlaylistFields.Format, authority, ref t.IdentityAuthority);
            }

            if ((row.Known & (uint)PlaylistFields.Saves) != 0
                && t.Accepts(slot, (uint)PlaylistFields.Saves, authority, in t.ExtrasAuthority))
            {
                t.Saves[slot] = row.Saves;
                t.Applied(slot, (uint)PlaylistFields.Saves, authority, ref t.ExtrasAuthority);
            }

            if ((row.Known & (uint)PlaylistFields.Daylist) != 0
                && t.Accepts(slot, (uint)PlaylistFields.Daylist, authority, in t.ExtrasAuthority))
            {
                t.DaylistExpiresAt[slot] = row.DaylistExpiresAt;
                t.DaylistCreatedAt[slot] = row.DaylistCreatedAt;
                t.Applied(slot, (uint)PlaylistFields.Daylist, authority, ref t.ExtrasAuthority);
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

            // Flags last and outside the group gates: a mask says exactly which bits this answer speaks about, the
            // latching tombstone is never cleared, and the app's own bits (create lifecycle, the derived column facts)
            // are nobody's business but ours.
            if (row.FlagsMask != 0 || row.Flags != 0)
            {
                uint merged = PlaylistTable.MergeFlags(t.Flags[slot], row.Flags, row.FlagsMask);
                if (merged != t.Flags[slot]) { t.Flags[slot] = merged; t.Bump(slot); }
            }
        }
    }
}
