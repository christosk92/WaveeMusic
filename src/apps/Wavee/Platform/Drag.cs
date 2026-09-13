// ── Platform/Drag.cs ───────────────────────────────────────────────────────────────────────────────────────────────
// the payload record, the chip resolver, every drop rule, the insertion preview. Shipped in the FIRST week of Wave 4
// — owners I (tab spring-load) and J (the five sidebar drop cues) consume its rule tables
//
// Role: CORE
// Owner: L
// Wave: 4
// Budget: 700 lines
// Spec: ch 01 §9 / ch 29 §9.10
//
// ── WHAT THIS FILE IS ────────────────────────────────────────────────────────────────────────────────────────────────
//
// ONE drag vocabulary for the whole app. Rows, cards, the sidebar tree, the tab strip, the playlist page and the queue
// all lift the SAME payload and every destination reads the SAME predicates, so a capability can never be lost three
// layers away by a surface that spelled its kind differently. Ported from 0.2.9's `Features/DragDrop/*` (938 lines:
// `WaveeDragRules` 210 · `WaveeDragChipModel` 52 · `WaveeResourceDrag` 596 · `PlaylistInsertionPreview` 80), with the
// COMMIT half — the deposit, the rootlist move, the toasts and the Undo — deliberately left out: those call the
// library-mutation seam, and ch 01 §8 sends only the DECISIONS here. See §4 at the bottom for where each of them went.
//
// ── THE NAME COLLISION, STATED ONCE ──────────────────────────────────────────────────────────────────────────────────
//
// The engine also exports `FluentGpu.Controls.Drag` (the drag-SOURCE facade). Inside `namespace Wavee` the nearer name
// wins, so `Drag.Source(...)` anywhere in this assembly resolves to THIS class, not the engine's. That is deliberate
// and it is why <see cref="Drag.Source"/> exists: one app-side spelling that forwards to the engine with Wavee's kind
// already filled in. A call site that genuinely wants the raw engine facade writes `FluentGpu.Controls.Drag.Source(…)`.

using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Input;
using FluentGpu.Localization;

namespace Wavee;

// ── 1. the vocabulary ────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>What a <see cref="DragPayload"/> is carrying. A WIDER enum than the sidebar's own entry kinds: it also
/// carries <see cref="Track"/> and <see cref="Episode"/>, which are playables and never a pin — so
/// <see cref="Drag.Pinnable"/> refuses them explicitly rather than letting them fall through to a guessed route pin.
/// <para><see cref="Route"/> is the safe reading of "we do not know what this is": pinnable, inert everywhere else.</para></summary>
public enum DragKind : byte
{
    Route, Playlist, Album, Artist, Show, Folder, Track, Episode,
}

/// <summary>Why a playlist destination turned a drag away. A refusing drop target is TRANSPARENT by design (discovery
/// walks past it to an accepting ancestor), so without a named reason the user sees nothing at all happen — the
/// "cannot drop in this mode" report. Each value maps to one caption the chip shows beside its not-allowed glyph
/// (ch 29 W20).</summary>
public enum DropRefusal : byte
{
    /// <summary>Nothing to explain — the drop is allowed.</summary>
    None = 0,
    /// <summary>An editorial/daylist/someone-else's playlist: the user simply cannot write to it.</summary>
    NotEditable,
    /// <summary>The track list has not arrived yet, so there is no membership to insert into.</summary>
    Loading,
    /// <summary>The payload carries no tracks and can resolve none — an artist, a route, a podcast show.</summary>
    NoTracks,
    /// <summary>A same-list reorder under a non-natural SORT: display positions no longer name membership rows.</summary>
    Sorted,
    /// <summary>A same-list reorder under a search/filter: the display is a SUBSET, so a slot is ambiguous.</summary>
    Filtered,
    /// <summary>A same-list reorder whose rows (or whose landing anchor) have no membership <c>item_id</c> yet: our own
    /// add is still in flight, so the rows the server knows about are not the rows on screen. Transient by nature — the
    /// ids land with the ack — which is why it says "try again in a moment" rather than naming something to fix.</summary>
    Syncing,
}

/// <summary>ONE rootlist item a drag moves AS: a playlist by its uri, a folder by its group id. The sidebar's own
/// projection resolves both shapes to this, so the decision, the cue and the commit address the same items.</summary>
public readonly record struct RootRef(string Key, bool IsFolder);

/// <summary>ONE playlist MEMBERSHIP row a same-list move addresses — the stable item id off
/// <see cref="PlaylistTrackEdge.ItemId"/> plus the display index it was lifted from. A row with an empty
/// <see cref="ItemId"/> is not yet keyed (our own add is still in flight) and is what <see cref="Drag.RowsAreKeyed"/>
/// reports.</summary>
public readonly record struct RowRef(StringId ItemId, int Index);

/// <summary>The four data pieces the framework-owned drag chip renders (<see cref="DragChipSpec"/>), resolved PURELY.
/// The chip ELEMENT is the framework's; this is the only part with decisions in it — which line wins for a track drag
/// versus an entity drag, where the art comes from, what the badge counts.
///
/// <para>A TRACK snapshot names its FIRST track (title + first artist) and counts the WHOLE selection: the corner count
/// badge is what communicates "and N−1 more", so a multi-select chip stays a real track card instead of degrading into
/// a bare "3 songs" label. Every other resource names itself and has no second line.</para>
///
/// <para>A ROOTLIST multi-select (several sidebar playlists/folders dragged as one) carries no tracks at all, so it
/// counts through <paramref name="rootlistCount"/> — the SAME badge and stacked backdrop the framework already draws
/// for a song selection, because "I am carrying five things" is one idea and deserves one visual.</para></summary>
public readonly record struct DragChipModel(string? Title, string? Subtitle, string? ArtUrl, int Count)
{
    /// <inheritdoc cref="DragChipModel"/>
    public static DragChipModel For(string? name, string? artUrl, ReadOnlySpan<Track> tracks, int rootlistCount = 1)
    {
        if (tracks.Length == 0)
            return new(Nz(name), null, Nz(artUrl), rootlistCount > 1 ? rootlistCount : 1);
        var first = tracks[0];
        var artists = first.ArtistSlots;
        string? subtitle = artists.Length > 0
            ? Nz(new Artist(artists[0]).Name)
            : Nz(Entities.Strings.Resolve(first.ArtistLineId));
        return new(
            Nz(first.Title) ?? Nz(name),
            subtitle,
            Nz(artUrl) ?? Nz(Controls.ArtUrl(first.ImageId)),
            tracks.Length);
    }

    static string? Nz(string? s) => string.IsNullOrEmpty(s) ? null : s;
}

// ── 2. the payload ───────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The ONE transport envelope shared by tabs, sidebar rows, cards, track lists and the queue. Display and
/// navigation identity is retained separately from an optional ordered track snapshot; source membership rows make a
/// same-playlist drop a MOVE while every other playlist target is a COPY. The resolver is cold and runs only after a
/// compatible drop.
///
/// <para><see cref="ArtUrl"/> is the drag CHIP's artwork and nothing else — a source fills it only where the cover is
/// already in hand. A track snapshot needs no help: the chip reads the first track's own image.</para>
///
/// <para><see cref="SourceQueueItemId"/> marks a row lifted out of the QUEUE panel: the session-stable
/// <see cref="QueueEdge.ItemId"/> of that row, or 0 for a degenerate snapshot row that has none yet. Null for every
/// other source. It is the payload's self-identification as a REORDER gesture — see <see cref="FromQueue"/> and
/// <see cref="Drag.Depositable"/>.</para>
///
/// <para>A record, not a struct: a payload is built ONCE per gesture at drag promotion (the source's factory is cold by
/// contract) and then read from event handlers, never per pointer move (P1).</para></summary>
public sealed record DragPayload(
    DragKind Kind,
    string Id,
    string Uri,
    string Name,
    EntityRef Entity = default,
    Track[]? Tracks = null,
    string? SourcePlaylistUri = null,
    RowRef[]? SourceRows = null,
    Func<CancellationToken, Task<Track[]>>? TrackResolver = null,
    bool RootlistItem = false,
    string? ArtUrl = null,
    RootRef[]? RootlistItems = null,
    ulong? SourceQueueItemId = null)
{
    /// <summary>This drag lifted a row out of the queue panel. The gesture is a reorder of that list; no playlist, tab,
    /// sidebar row or player-bar surface may read it as a track to deposit (<see cref="CanCopyTracks"/> is false).</summary>
    public bool FromQueue => SourceQueueItemId is not null;

    /// <summary>How many ROOTLIST items this drag is carrying: the whole normalised selection for a multi-select, 1 for
    /// an ordinary single rootlist drag, 0 for a payload that is not a rootlist item at all. The chip's count badge and
    /// the "Moved {n} items to {name}" toast both read this — it is the ONE count.</summary>
    public int RootlistCount => RootlistItems?.Length ?? (RootlistItem ? 1 : 0);

    /// <summary>This payload's chip data (the engine-free resolution rules).</summary>
    public DragChipModel ChipModel()
        => DragChipModel.For(Name, ArtUrl, Tracks is { Length: > 0 } t ? t : default, RootlistCount);

    /// <summary>This payload OFFERS its tracks to a destination (a playlist deposit, a queue insert). Not merely "has
    /// tracks": a queue row travels with its track (the chip reads it) and offers it to nobody — the rule is
    /// <see cref="Drag.Depositable"/>, and every destination reads THIS property, so the queue refusal reaches them all
    /// without a single target knowing the queue exists.</summary>
    public bool CanCopyTracks
        => Drag.Depositable(Tracks is { Length: > 0 } || TrackResolver is not null, FromQueue);

    /// <summary>Cheap eligibility for UI gating (drop-zone reveal, spring-load) — routes through the SAME boundary the
    /// pin commit uses rather than duplicating its kind list, so the two can never drift apart.</summary>
    public bool CanPin => Drag.Pinnable(Kind);

    /// <summary>Every row this payload can actually deposit, resolved ONCE, after the drop. Empty when the payload has
    /// nothing (an artist, a show, a route) — which is exactly the state <see cref="DropRefusal.NoTracks"/> names.</summary>
    public Task<Track[]> ResolveTracksAsync(CancellationToken ct = default)
        => Tracks is { } tracks ? Task.FromResult(tracks)
            : TrackResolver is { } resolve ? resolve(ct)
            : Task.FromResult(Array.Empty<Track>());

    /// <summary>THE rootlist references this payload moves AS, in tree order. A single-item drag answers a list of ONE,
    /// which is why the decision, the cue and the commit have no separate single-item path at all.
    /// <para>A folder payload's <see cref="Id"/> may be its wire uri (<c>spotify:folder:&lt;hex&gt;</c>) or the bare
    /// group id; both reduce to the group id here, so the sidebar's two spellings cannot address two different
    /// folders.</para></summary>
    public RootRef[] RootRefs()
        => RootlistItems is { Length: > 0 } many ? many
            : [Kind == DragKind.Folder
                ? new RootRef(Drag.FolderKey(Id), IsFolder: true)
                : new RootRef(Uri, IsFolder: false)];
}

// ── 3. the rules, the chip and the insertion preview ─────────────────────────────────────────────────────────────────

public static partial class Drag
{
    // ── 3.1 the discriminator and the source facade ─────────────────────────────────────────────────────────────────

    /// <summary>The ONE in-app drag discriminator for navigable resources and playable items. Targets decide capability
    /// from <see cref="DragPayload.Kind"/> instead of inventing surface-specific discriminators.</summary>
    public const string Resource = "wavee.resource";

    /// <summary>How long a drag has to rest on a container before it opens itself (a collapsed sidebar folder expands,
    /// a tab activates). 500 ms is the platform convention shared by macOS spring-loaded folders and WinUI's
    /// hold-to-open surfaces — long enough that merely travelling ACROSS a folder never opens it (ch 29 W19). Two
    /// surfaces use it and neither owns it: the sidebar tree (ch 25 W16) and the tab strip (ch 18 §6).</summary>
    public const float SpringLoadMs = 500f;

    /// <summary>Make a node draggable with Wavee's own kind already filled in. Forwards to the engine facade — see the
    /// name-collision note in the file header for why this wrapper exists.
    /// <para><paramref name="clickPrimary"/> widens the MOUSE drag box on a source where SELECTING is the common intent
    /// and dragging the exception (a tab, a small nav row), so a click landed while the mouse is still travelling is not
    /// eaten by a drag promotion.</para></summary>
    public static DragSource Source(Func<object?> payload, bool clickPrimary = false)
        => FluentGpu.Controls.Drag.Source(Resource, payload,
            thresholdMultiplier: clickPrimary ? FluentGpu.Controls.Drag.ClickPrimaryThresholdMultiplier : 1f);

    /// <summary>A source that HIDES its row entirely for the gesture — the sidebar/reorder case where the vacated slot
    /// itself IS the insertion gap, so a dimmed ghost row would read as a duplicate.</summary>
    public static DragSource SourceHidden(Func<object?> payload)
        => FluentGpu.Controls.Drag.SourceHidden(Resource, payload);

    /// <summary>Unwrap either a plain Wavee source or an item owned by the engine's <see cref="Reorderable"/>.</summary>
    public static DragPayload? Unwrap(object? payload) => payload switch
    {
        DragPayload direct => direct,
        ReorderPayload { Item: DragPayload wrapped } => wrapped,
        _ => null,
    };

    // ── 3.2 the kind map ─────────────────────────────────────────────────────────────────────────────────────────────
    //
    // 0.2.9 needed FOUR maps here, one per surface enum (`HomeCardKind`, `SearchHitKind`, `SidebarEntryKind`, plus a uri
    // probe), because every card surface named its entity with a different enum and a payload that mislabelled its kind
    // silently lost a capability three layers away — an album drag that says "route" cannot resolve tracks, a playlist
    // that says "album" cannot be filed into a folder. 0.3 has ONE card identity, `EntityRef(EntityKind, Slot)`, so
    // three of the four maps collapse into `KindOf(EntityKind)` and cannot disagree by construction. The uri arm
    // survives for the surfaces that hold nothing BUT a uri (a tab, a deep link, a rich-text mention).

    /// <summary>Entity kind → drag kind. <see cref="EntityKind.Collection"/> is the Liked Songs pseudo-playlist: it
    /// navigates and pins like a playlist and its tracks resolve through the playlist reader, so it maps there.
    /// <see cref="EntityKind.User"/> is a person with no Wavee resource behind them, so it falls to
    /// <see cref="DragKind.Route"/> — pinnable, never depositable.</summary>
    public static DragKind KindOf(EntityKind kind) => kind switch
    {
        EntityKind.Track => DragKind.Track,
        EntityKind.Episode => DragKind.Episode,
        EntityKind.Album => DragKind.Album,
        EntityKind.Artist => DragKind.Artist,
        EntityKind.Playlist or EntityKind.Collection => DragKind.Playlist,
        EntityKind.Show => DragKind.Show,
        _ => DragKind.Route,
    };

    /// <summary>Spotify URI → drag kind, for the surfaces whose card carries nothing BUT a uri. Kind comes from the ONE
    /// parser (<see cref="EntityUri.KindOf(ReadOnlySpan{char})"/> — allocation-free) instead of seven substring probes,
    /// so the "more specific scheme wins" ordering is structural: a prerelease IS its own kind and still drags as an
    /// Album. A rootlist FOLDER is not a catalog entity and has no kind, so its uri shape is recognised here. An
    /// unrecognised uri is a <see cref="DragKind.Route"/>.</summary>
    public static DragKind KindOfUri(ReadOnlySpan<char> uri)
    {
        if (uri.IsEmpty) return DragKind.Route;
        if (uri.SequenceEqual(EntityUri.LikedCollection)) return DragKind.Playlist;   // Liked Songs reads as a playlist
        if (uri.StartsWith(EntityUri.FolderPrefix, StringComparison.Ordinal)) return DragKind.Folder;
        return KindOf(EntityUri.KindOf(uri));
    }

    /// <summary>The drag kind of one PLAYABLE row. An episode rides the same row model as a song but it is not one, and
    /// the chip's glyph and the drop captions read the KIND — so the row states which it is. Anything else (a song, a
    /// local import whose uri is its encoded file path, an unclassifiable uri) stays <see cref="DragKind.Track"/>: it is
    /// a playable with a track snapshot behind it, which is exactly what every destination acts on.</summary>
    public static DragKind PlayableKind(ReadOnlySpan<char> uri)
        => EntityUri.KindOf(uri) == EntityKind.Episode ? DragKind.Episode : DragKind.Track;

    /// <summary>May a payload of this kind become a sidebar PIN? Tracks and episodes are playables, never pins — they
    /// are refused explicitly rather than collapsed to a route pin. Owner J's <c>PinRowRule</c> reads this; it is not
    /// re-derived there (§9.4 A7 sends <c>PinRowRule</c> to `Sidebar.cs` and keeps this predicate here).</summary>
    public static bool Pinnable(DragKind kind)
        => kind is DragKind.Playlist or DragKind.Album or DragKind.Artist or DragKind.Show
                or DragKind.Folder or DragKind.Route;

    // ── 3.3 the playlist refusal table (ch 29 W20) ───────────────────────────────────────────────────────────────────

    /// <summary>THE single decision table behind a track list's insertion <c>CanAccept</c> and its refusal caption: one
    /// function answers BOTH, so a refusal can never be cued with a reason the accept test did not actually use (the two
    /// drifting apart is how a "cannot drop" ends up unexplained).
    ///
    /// <para>Order matters and is deliberate: page-level write capability first (nothing else can rescue a read-only
    /// playlist), then whether the destination is even loaded, then the payload's own ability to produce tracks, and
    /// only then the same-list-move ambiguities — a foreign COPY is legal under any sort or filter, because it
    /// appends/inserts by display position without having to name existing membership rows.</para></summary>
    /// <param name="editable">The destination is a playlist this user can write to.</param>
    /// <param name="loading">The destination's track list is still loading (a shimmer, not a list).</param>
    /// <param name="payloadHasTracks">The payload carries a track snapshot or can resolve one.</param>
    /// <param name="sameList">The payload's rows came from THIS playlist — a MOVE, not a copy.</param>
    /// <param name="naturalOrder">The display order IS the membership order (sort = Index, ascending).</param>
    /// <param name="filtered">A search query or a filter chip is narrowing the display.</param>
    /// <param name="rowsKeyed">Every dragged row carries its membership item id (<see cref="RowsAreKeyed"/>). Reported
    /// LAST of the same-list arms on purpose: sorting and filtering are states the user can act on and fix, while
    /// "still syncing" is a wait — naming the wait first would hide the two refusals that have a remedy.</param>
    public static DropRefusal Evaluate(bool editable, bool loading, bool payloadHasTracks,
                                       bool sameList, bool naturalOrder, bool filtered, bool rowsKeyed)
    {
        if (!editable) return DropRefusal.NotEditable;
        if (loading) return DropRefusal.Loading;
        if (!payloadHasTracks) return DropRefusal.NoTracks;
        if (!sameList) return DropRefusal.None;
        if (!naturalOrder) return DropRefusal.Sorted;
        if (filtered) return DropRefusal.Filtered;
        if (!rowsKeyed) return DropRefusal.Syncing;
        return DropRefusal.None;
    }

    /// <summary>The accept test, expressed against the same table so the two can never disagree.</summary>
    public static bool Accepts(bool editable, bool loading, bool payloadHasTracks,
                               bool sameList, bool naturalOrder, bool filtered, bool rowsKeyed)
        => Evaluate(editable, loading, payloadHasTracks, sameList, naturalOrder, filtered, rowsKeyed) == DropRefusal.None;

    /// <summary>Does every dragged row carry its membership item id? A row whose id is empty is one our own add has not
    /// been acked for yet, so the rows the server knows about are not the rows on screen — the
    /// <see cref="DropRefusal.Syncing"/> arm. An EMPTY set is keyed (there is nothing unkeyed in it), which keeps a
    /// foreign copy legal.</summary>
    public static bool RowsAreKeyed(ReadOnlySpan<RowRef> rows)
    {
        for (int i = 0; i < rows.Length; i++) if (rows[i].ItemId.IsEmpty) return false;
        return true;
    }

    /// <summary>The refusal's CAPTION — the chip is the only caption surface, so a refusal with no sentence here is a
    /// drag that turns away silently (ch 29 W20). One loc key per arm, no interpolation, so it stays safe inside the
    /// zero-alloc frame region a live drag renders in.</summary>
    public static string? RefusalCaption(DropRefusal refusal) => refusal switch
    {
        DropRefusal.NotEditable => Loc.Get(Strings.Drag.CantEditPlaylist),
        DropRefusal.Loading => Loc.Get(Strings.Drag.StillLoading),
        DropRefusal.NoTracks => Loc.Get(Strings.Drag.NothingToAdd),
        DropRefusal.Sorted => Loc.Get(Strings.Drag.ClearSortingToReorder),
        DropRefusal.Filtered => Loc.Get(Strings.Drag.ClearFiltersToReorder),
        DropRefusal.Syncing => Loc.Get(Strings.Drag.StillSyncing),
        _ => null,
    };

    // ── 3.4 the three surface rules (tab strip · queue · collapsed rail) ─────────────────────────────────────────────

    /// <summary>Only a REAL, writable Spotify playlist is a deposit destination: pseudo-playlists (Liked Songs, an
    /// editorial daylist) navigate like playlists but are not membership lists this app writes to.
    /// <para>THE ONE predicate — owner O's playlist picker and owner I's "Add to playlist" menu delegate here rather
    /// than hand-writing a third and fourth copy (0.2.9 had exactly that, with a comment on each warning that changing
    /// one meant changing all three).</para></summary>
    public static bool IsDepositablePlaylistUri(ReadOnlySpan<char> uri)
    {
        if (uri.IsEmpty || uri.SequenceEqual(EntityUri.LikedCollection)) return false;
        return EntityUri.KindOf(uri) == EntityKind.Playlist;
    }

    /// <summary>May this payload be deposited on the TAB standing for <paramref name="targetUri"/>?
    /// <para>The SAME-playlist exclusions are the point of this rule rather than a nicety. A tab drop can only ever
    /// APPEND (there is no slot in a tab), so the same-list MOVE arm — which needs an insertion index — cannot engage:
    /// a row dragged out of playlist P onto P's own tab would fall through to the copy arm and duplicate the user's rows
    /// into their own playlist. Refusing here instead means the tab never lights up for a gesture that has nothing to
    /// do, which is the honest cue.</para></summary>
    public static bool TabAcceptsDeposit(string targetUri, bool targetEditable, bool payloadHasTracks,
                                         string? payloadSourcePlaylistUri, string? payloadUri)
    {
        if (!targetEditable || !payloadHasTracks || !IsDepositablePlaylistUri(targetUri)) return false;
        if (string.Equals(payloadSourcePlaylistUri, targetUri, StringComparison.Ordinal)) return false;
        return !string.Equals(payloadUri, targetUri, StringComparison.Ordinal);
    }

    /// <summary>A QUEUE row's drag is a REORDER, never a deposit. The one decision every playlist destination reads
    /// through <see cref="DragPayload.CanCopyTracks"/>, so no target has to know the queue exists.
    /// <para>The finding this answers: the queue's "Next in queue" row travelled as an ordinary track payload (it
    /// carries its track — the chip needs it for the title and the art), so every playlist surface accepted it as a copy
    /// and a drag aimed at the queue's own rows ended as "Added to {playlist}". Inside the queue the gesture is the
    /// list's OWN (<see cref="ReorderPayload"/>), which the list accepts without asking this question — so refusing
    /// everywhere else costs the reorder nothing.</para></summary>
    public static bool Depositable(bool hasTracks, bool fromQueue) => hasTracks && !fromQueue;

    /// <summary>D16 — the COLLAPSED RAIL's transparency rule. Should a rail PLAYLIST tile sit this gesture out entirely?
    ///
    /// <para>TRUE for a rootlist payload that carries no tracks — a FOLDER being re-filed. It has nothing it could add
    /// to a playlist and nothing it could file into one, and it is on its way to a folder tile or to the peeked pane, so
    /// the tile is merely on the route. The landed behaviour was a refusal reading "Nothing to add", which is an
    /// accusation aimed at a drag that was only passing through.</para>
    ///
    /// <para>FALSE for everything else, which keeps both other answers intact: a track-bearing payload over an editable
    /// playlist tile is a real deposit, and a track-bearing payload the tile cannot take still owes the user a
    /// reason.</para></summary>
    public static bool RailTileTransparent(bool payloadIsRootlistItem, bool payloadCanCopyTracks)
        => payloadIsRootlistItem && !payloadCanCopyTracks;

    /// <summary>Is this tree row one of the items the drag is CARRYING? The resolver's "source is self" fact, and the
    /// only payload legality question left in the sidebar's geometry layer — "into MYSELF" and "before myself" need two
    /// different sentences where the marker stream reports one same-item.
    /// <para>Asks the row BOTH ways because the two identities the sidebar addresses a row by are different strings: the
    /// projection's entry id and the bare uri the seam moves a playlist as.</para></summary>
    public static bool IsSource(DragPayload? payload, string entryId, string uri)
    {
        if (payload is null) return false;
        if (payload.RootlistItems is { Length: > 0 } refs)
        {
            string folderId = entryId.Length > 0 ? FolderKey(entryId) : "";
            for (int i = 0; i < refs.Length; i++)
            {
                var r = refs[i];
                if (r.Key.Length == 0) continue;
                bool hit = r.IsFolder
                    ? folderId.Length > 0 && string.Equals(FolderKey(r.Key), folderId, StringComparison.Ordinal)
                    : uri.Length > 0 && string.Equals(r.Key, uri, StringComparison.Ordinal);
                if (hit) return true;
            }
            return false;
        }
        return (entryId.Length > 0 && string.Equals(payload.Id, entryId, StringComparison.Ordinal))
            || (uri.Length > 0 && string.Equals(payload.Uri, uri, StringComparison.Ordinal));
    }

    /// <summary>A folder's GROUP id from either spelling the app addresses it by: the wire uri
    /// (<c>spotify:folder:&lt;hex&gt;</c>) reduces to its hex tail, and anything else is taken to BE the group id. One
    /// reduction, so a projection entry and a payload that spelled the same folder differently still compare equal.</summary>
    public static string FolderKey(string idOrUri)
    {
        var id = EntityUri.FolderIdOf(idOrUri);
        return id.IsEmpty ? idOrUri : new string(id);
    }

    // ── 3.5 the live-session probes ─────────────────────────────────────────────────────────────────────────────────
    //
    // Both read the live DragDropContext through the host's drag-state seam, so they are plain synchronous reads with no
    // subscription: the caller is an event/refresh path, never a render.

    /// <summary>Is a SAME-LIST reorder of <paramref name="playlistUri"/> live right now?
    /// <para>The one question a page has to answer before it publishes a re-projection of its own rows: while the user
    /// is aiming a drag at those very rows, a fresh membership snapshot re-keys the list under the pointer and the drop
    /// lands somewhere they did not aim. A FOREIGN session — a drag from another list, an OS file drag — has no stake in
    /// this list's order and is deliberately not reported.</para></summary>
    public static bool LiveSameListReorder(string? playlistUri)
    {
        if (string.IsNullOrEmpty(playlistUri)) return false;
        var state = FluentGpu.Hooks.InputHooks.Current.Default.GetDragState?.Invoke() ?? default;
        if (!state.Active) return false;
        return Unwrap(state.Payload) is { SourceRows.Length: > 0 } resource
               && string.Equals(resource.SourcePlaylistUri, playlistUri, StringComparison.Ordinal);
    }

    /// <summary>Is a ROOTLIST organisation drag (a playlist or folder being re-filed in the sidebar tree) live right
    /// now? The sibling of <see cref="LiveSameListReorder"/>, and it exists for the same reason.</summary>
    public static bool LiveRootlistDrag()
    {
        var state = FluentGpu.Hooks.InputHooks.Current.Default.GetDragState?.Invoke() ?? default;
        if (!state.Active) return false;
        return Unwrap(state.Payload) is { RootlistItem: true } resource
               && resource.Kind is DragKind.Playlist or DragKind.Folder;
    }

    // ── 3.6 the chip (ch 29 W17) ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The app's chip DATA for a live drag — Wavee's whole contribution to the drag visual. The framework
    /// renders it (opaque compact card, art + title + subtitle, corner count badge and stacked backdrop for a
    /// multi-select, tilt, caption, not-allowed cue, cursor offset, window clamp); this decides only what it says.
    ///
    /// <para><see cref="ExtraChip"/> is the ONE extension point, and it exists because `DragPreviewLayer.Of` takes
    /// exactly one resolver: a surface whose kind is unknown here draws no moving visual at all AND — because the chip
    /// is also the only caption surface — publishes neither its drop caption nor its refusal reason. Owner J's
    /// sidebar-customizer palette chips install themselves through it rather than mounting a second preview layer.</para></summary>
    public static DragChipSpec? Chip(DragState state)
    {
        if (!string.Equals(state.Kind, Resource, StringComparison.Ordinal))
            return ExtraChip?.Invoke(state);

        if (Unwrap(state.Payload) is not { } payload) return null;
        var model = payload.ChipModel();
        return new DragChipSpec(
            // A liked-collection drag travels wearing its own cover, not the generic playlist glyph. The element is
            // PRE-BUILT once at install time, never per frame: `Chip` runs inside the zero-alloc frame region while a
            // drag is live, and the chip's art box is a fixed 40 DIP, so there is exactly one element to build and it
            // can be built ahead of the gesture.
            Art: string.Equals(payload.Uri, EntityUri.LikedCollection, StringComparison.Ordinal) ? LikedChipArt : null,
            ArtSource: model.ArtUrl, Title: model.Title, Subtitle: model.Subtitle,
            Count: model.Count, Glyph: GlyphFor(payload.Kind),
            // The RESTING verb — what the chip says while travelling, before anything accepts. Reported as part of
            // "drag & drop is unclear": the card showed a song title and an artist and never a verb, so the gesture gave
            // no evidence it was even armed for a playlist. A live target's caption supersedes it the moment one
            // accepts, and a refusal reason supersedes it when one refuses. Loc.Get is a table lookup of an interned
            // string — no interpolation, so this stays safe inside the zero-alloc frame region.
            //
            // A ROOTLIST payload gets its OWN verb (D14): "Drag onto a playlist to add" is the wrong sentence for a
            // gesture that is organising the sidebar — the user is not adding anything, and a folder drag (which can add
            // nothing at all) used to travel with no caption whatsoever. A QUEUE row gets its own for the same reason in
            // the other direction: it used to travel wearing the exact promise the drop then kept, which is how a
            // reorder attempt ended as a playlist add.
            RestingCaption: payload.FromQueue
                ? Loc.Get(Strings.Drag.ReorderHint)
                : payload.RootlistItem && payload.Kind is DragKind.Playlist or DragKind.Folder
                    ? Loc.Get(Strings.Drag.OrganizeHint)
                    : payload.CanCopyTracks ? Loc.Get(Strings.Drag.DragOntoPlaylist) : null);
    }

    /// <summary>A SECOND drag kind's chip, resolved inside the ONE resolver the shell mounts. Assigned once, at
    /// composition time (owner J's sidebar customizer); null for every other kind, which leaves that gesture its
    /// deliberate ghost lift.</summary>
    public static Func<DragState, DragChipSpec?>? ExtraChip { get; set; }

    /// <summary>The liked collection's chip artwork, BUILT ONCE and installed at composition time by the surface that
    /// owns the liked cover treatments (owner O's `User.Cover.cs`). Null leaves the generic playlist glyph, which is the
    /// honest degradation — never a per-frame build inside <see cref="Chip"/>.
    /// <para>40 DIP is below the liked treatment floor, so what belongs here is the flat 2×2 mosaic of the newest likes
    /// (or the stock cover) — still the collection's own art, where the alternative is a generic music-note tile.</para></summary>
    public static Element? LikedChipArt { get; set; }

    /// <summary>The ONE preview mounted at the shell root (`DragPreviewLayer.Of`).</summary>
    public static readonly Func<DragState, Element?> Preview = DragChip.Resolve(Chip);

    /// <summary>The art-less fallback tile: the same kind glyphs the sidebar uses, so a cover-less drag still reads as
    /// "a playlist" / "an album" rather than as a generic note.</summary>
    static string GlyphFor(DragKind kind) => kind switch
    {
        DragKind.Playlist => Icons.MusicNote,
        DragKind.Album => Icons.Album,
        DragKind.Artist => Icons.Contact,
        DragKind.Show or DragKind.Episode => Icons.RadioTower,
        DragKind.Folder => Icons.Folder,
        DragKind.Route => Icons.Home,
        _ => Icons.MusicNote,
    };

    // ── 3.7 the insertion preview (ch 29 W18) ───────────────────────────────────────────────────────────────────────

    /// <summary>Preview cards drawn in the gap (and, for a cross-list copy, the gap's row cap — an exact-N gap for a
    /// 500-track copy would blow the viewport). Deliberately the FRAMEWORK's cap, not a second 3: the view sizes the gap
    /// from <see cref="SortableMath.DefaultPreviewCap"/>, so a local literal would drift the cards off the gap.</summary>
    public const int PreviewCap = SortableMath.DefaultPreviewCap;

    /// <summary>The CONTENT of a playlist insertion gap — the cards the user sees land where they are aiming. Position,
    /// size and lifecycle belong to the framework (<c>InsertionOptions.GapPreview</c> on <c>ItemsView</c>); this only
    /// draws the ≤<see cref="PreviewCap"/> track cards, the last one carrying the "+N" pill for a larger block.
    /// <para><paramref name="inset"/>/<paramref name="padX"/> come from the destination list's own row geometry (owner
    /// M's lane table in `Entities/Track.cs`), so the preview lines up with the rows above and below it rather than
    /// carrying a second copy of the track row's margins. <paramref name="showArtwork"/> is the Appearance setting:
    /// hiding track artwork drops the art column from the preview too.</para></summary>
    public static Element InsertionCards(DragPayload payload, float rowH, float artEdge,
                                         float inset, float padX, bool showArtwork = true)
    {
        var tracks = payload.Tracks;
        int total = tracks is { Length: > 0 } ? tracks.Length : 1;
        int shown = Math.Min(PreviewCap, total);
        var rows = new Element[shown];
        for (int i = 0; i < shown; i++)
        {
            Track? track = tracks is { Length: > 0 } && i < tracks.Length ? tracks[i] : null;
            int hidden = i == shown - 1 ? total - shown : 0;
            rows[i] = PreviewRow(track, payload.Name, rowH, artEdge, inset, padX, hidden, showArtwork);
        }
        return new BoxEl { Direction = 1, Shrink = 0f, HitTestVisible = false, Children = rows };
    }

    static Element PreviewRow(Track? track, string fallback, float height, float artEdge, float inset, float padX,
                              int hidden, bool showArtwork)
    {
        string title = track is { } t0 && t0.Title is { Length: > 0 } name ? name : fallback;
        string subtitle = track is { } t1 ? Entities.Strings.Resolve(t1.ArtistLineId) : "";
        Element art = track is { } t
            ? Controls.Artwork(Controls.ArtUrl(t.ImageId), artEdge, artEdge, Radii.Control)
            : new BoxEl
            {
                Width = artEdge, Height = artEdge,
                Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary,
                Children = [Ui.Icon(Icons.MusicNote, 16f, Tok.TextSecondary)],
            };
        var children = new Element[(showArtwork ? 1 : 0) + 1 + (hidden > 0 ? 1 : 0)];
        int child = 0;
        if (showArtwork) children[child++] = art;
        children[child++] = new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, Gap = Spacing.XXS,
            Children =
            [
                new TextEl(title) { Size = 14f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                new TextEl(subtitle) { Size = 12f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            ],
        };
        if (hidden > 0)
            children[child] = new BoxEl
            {
                Shrink = 0f, Padding = new Edges4(Spacing.S, Spacing.XXS, Spacing.S, Spacing.XXS),
                Corners = Radii.PillAll, Fill = Tok.AccentSubtle,
                Children = [new TextEl("+" + hidden) { Size = 12f, Weight = 600, Color = Tok.AccentTextPrimary }],
            };
        return new BoxEl
        {
            Direction = 0, Height = height, AlignItems = FlexAlign.Center, Gap = Spacing.M,
            Margin = new Edges4(inset, 0f, inset, 0f),
            Padding = new Edges4(padX - inset, 0f, padX - inset, 0f),
            Corners = Radii.ControlAll,
            Fill = Tok.FillSolidSecondary,
            BorderWidth = 1f, BorderColor = Tok.AccentDefault,
            Shadow = Elevation.Card, HitTestVisible = false,
            Children = children,
        };
    }

    // ── 3.8 the captions the ACCEPTING side publishes ───────────────────────────────────────────────────────────────
    //
    // §3.3 owns the refusals. These are the affirmative half — what a target says it WILL do — in one place, so the
    // sidebar row, the tab and the playlist body cannot phrase the same outcome three ways.

    /// <summary>"Add to {name}" — a playlist/tab that will take the payload's tracks.</summary>
    public static string AddTo(string name) => Strings.Drag.AddTo(name);
    /// <summary>"Move into {name}" — a folder that will take a rootlist item.</summary>
    public static string MoveInto(string name) => Strings.Drag.MoveInto(name);
    /// <summary>"Pin {name}" — the pin band.</summary>
    public static string Pin(string name) => Strings.Drag.Pin(name);
    /// <summary>"Move # song(s)" — a same-list reorder whose destination is the list the pointer is already inside.</summary>
    public static string MoveTracks(int count) => Strings.Drag.MoveTracks(count);
    /// <summary>"Add # song(s)" — the copy arm of the same.</summary>
    public static string AddTracks(int count) => Strings.Drag.AddTracks(count);
}

// ── 4. what this file does NOT carry, and where each piece went ──────────────────────────────────────────────────────
//
// · The COMMIT half — 0.2.9's `WaveeResourceDrop.DepositTracksAsync` / `MoveRootlist` / `Confirm` / `UndoAsync`
//   (~200 lines). They call the library-mutation seam, raise toasts and announce for Narrator, none of which is a
//   decision. `DepositTracks*` belongs with the playlist mutations (owner O, `Entities/Playlist.cs` +
//   `Playlist.Page.cs`); `MoveRootlist` belongs with the rootlist seam (owner J, `Shell/Sidebar.Host.cs`). Both read
//   THIS file's predicates (`CanCopyTracks`, `RootRefs()`, `Accepts`) and must add no new ones. The two conventions
//   they must keep are recorded here so they are not lost: the insertion index handed to a same-list move is the
//   PRE-move index ("insert before the row currently at this index") and must not be pre-corrected, and one drop issues
//   ONE batched move so a multi-select's relative order survives.
// · The PAYLOAD FACTORIES (0.2.9's `FromEntry` / `FromEntries` / `FromDestination` / `ForTrack` / `ForQueueRow` /
//   `ForTracks` / `ForEntity`). They lived here because every source had a different record type to marshal; in 0.3
//   every source holds a handle, so a factory is `new DragPayload(Drag.KindOf(kind), …)` at the call site and a shared
//   factory would only re-introduce the marshalling this rebuild removed. The one genuinely shared piece — the kind
//   map — is §3.2.
// · `WaveeRootlist.IsMember` / `.CanEditPlaylist`. Both are LOOKUPS into the user's rootlist edges, which is
//   `Entities/User.cs` (owner O) in 0.3. Their CONTRACT survives verbatim and is restated here so it is not lost:
//   **"not known to be in the rootlist / editable" must never present as "is"** — both answer FALSE on a cold store,
//   which costs a tab that refuses a deposit until the rootlist has arrived and is strictly better than one that accepts
//   a drop the server will reject.
