using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using Wavee.Core;

namespace Wavee;

/// <summary>The one in-app drag discriminator for navigable resources and playable items. Targets decide capability
/// from <see cref="WaveeResourceDragPayload.Kind"/> instead of inventing surface-specific discriminators.</summary>
static class WaveeDragKinds
{
    public const string Resource = "wavee.resource";
}

/// <summary>A transport envelope shared by tabs, sidebar rows and track lists. Display/navigation identity is retained
/// separately from an optional ordered track snapshot; source playlist row refs make a same-playlist drop a move while
/// every other playlist target is a copy. The resolver is cold and runs only after a compatible drop.
/// <para><see cref="ArtUrl"/> is the drag CHIP's artwork and nothing else — a source fills it only where the cover is
/// already in hand (a sidebar row's entry). A track snapshot needs no help: the chip reads the first track's own image.
/// </para>
/// <para><see cref="SourceQueueItemId"/> marks a row lifted out of the QUEUE panel (<see cref="ForQueueRow"/>): the
/// session-stable id of that row, or <see cref="QueueItemId.None"/> for a degenerate snapshot row that has none yet.
/// Null for every other source. It is the payload's self-identification as a REORDER gesture — see
/// <see cref="FromQueue"/> and <see cref="QueueDragRules"/>.</para></summary>
sealed record WaveeResourceDragPayload(
    WaveeResourceKind Kind,
    string Id,
    string Uri,
    string Name,
    IReadOnlyList<Track>? Tracks = null,
    string? SourcePlaylistUri = null,
    IReadOnlyList<PlaylistRowRef>? SourceRows = null,
    Func<CancellationToken, Task<IReadOnlyList<Track>>>? TrackResolver = null,
    bool RootlistItem = false,
    string? ArtUrl = null,
    IReadOnlyList<RootlistItemRef>? RootlistItems = null,
    QueueItemId? SourceQueueItemId = null)
{
    /// <summary>This drag lifted a row out of the queue panel. The gesture is a reorder of that list; no playlist, tab,
    /// sidebar row or player-bar surface may read it as a track to deposit (<see cref="CanCopyTracks"/> is false).</summary>
    public bool FromQueue => SourceQueueItemId is not null;

    /// <summary>How many ROOTLIST items this drag is carrying: the whole normalised selection for a multi-select
    /// (<see cref="FromEntries"/>), 1 for an ordinary single rootlist drag, 0 for a payload that is not a rootlist item
    /// at all. The chip's count badge and the "Moved {n} items to {name}" toast both read this — it is the ONE count.</summary>
    public int RootlistCount => RootlistItems?.Count ?? (RootlistItem ? 1 : 0);

    /// <summary>This payload's chip data (the engine-free resolution rules).</summary>
    public WaveeDragChipModel ChipModel() => WaveeDragChipModel.For(Name, ArtUrl, Tracks, RootlistCount);

    /// <summary>Cheap eligibility check for UI gating (drop-zone reveal, spring-load) — routes through the SAME
    /// boundary <see cref="TryPin"/> uses rather than duplicating its kind list, so the two can never drift apart.</summary>
    public bool CanPin => TryPin(out _);

    /// <summary>This payload OFFERS its tracks to a destination (a playlist deposit, a queue insert). Not merely "has
    /// tracks": a queue row travels with its <see cref="Track"/> (the chip reads it) and offers it to nobody — the rule
    /// is <see cref="QueueDragRules.Depositable"/>, and every destination reads THIS property, so the queue refusal
    /// reaches them all without a single target knowing the queue exists.</summary>
    public bool CanCopyTracks
        => QueueDragRules.Depositable(Tracks is { Count: > 0 } || TrackResolver is not null, FromQueue);

    public Task<IReadOnlyList<Track>> ResolveTracksAsync(CancellationToken ct = default)
        => Tracks is { } tracks ? Task.FromResult(tracks)
            : TrackResolver is { } resolve ? resolve(ct)
            : Task.FromResult<IReadOnlyList<Track>>(Array.Empty<Track>());

    /// <summary>Resolve the pin kind this payload would create, or refuse — routed through
    /// <see cref="SidebarPinId.IsPinnable"/> at the boundary rather than letting an unpinnable resource (a track, an
    /// episode) fall through to a guessed kind. <c>Kind</c> is <see cref="WaveeResourceKind"/>, a wider enum than
    /// <see cref="SidebarEntryKind"/> (it also carries Track/Episode, which are never a pin), so those two arms map to
    /// the explicit <see cref="SidebarEntryKind.Track"/> sentinel and are then refused by <c>IsPinnable</c> — never
    /// silently collapsed to a route pin.</summary>
    public bool TryPin(out SidebarEntryKind kind)
    {
        kind = Kind switch
        {
            WaveeResourceKind.Playlist => SidebarEntryKind.Playlist,
            WaveeResourceKind.Album => SidebarEntryKind.Album,
            WaveeResourceKind.Artist => SidebarEntryKind.Artist,
            WaveeResourceKind.Show => SidebarEntryKind.Show,
            WaveeResourceKind.Folder => SidebarEntryKind.Folder,
            WaveeResourceKind.Route => SidebarEntryKind.AppRoute,
            _ => SidebarEntryKind.Track,   // Track / Episode — never pinnable
        };
        return SidebarPinId.IsPinnable(kind);
    }

    public static WaveeResourceDragPayload FromEntry(SidebarLibraryEntry entry, Services? svc, bool rootlistItem = false)
    {
        var kind = WaveeDragKindMap.Of(entry.Kind);
        return new(kind, entry.Id, entry.Uri, entry.Name,
            TrackResolver: ResolverFor(kind, entry.Uri, svc), RootlistItem: rootlistItem,
            ArtUrl: WaveeDragChipModel.ArtOf(entry.Cover));
    }

    /// <summary>A MULTI-SELECT of sidebar tree rows, lifted as ONE payload.
    ///
    /// <para><paramref name="orderedTreeEntries"/> is the selection already normalised — tree order, descendants of a
    /// selected folder dropped (<c>RootlistSelection.Normalize</c>). The payload's display identity is the FIRST
    /// entry's (kind, id, uri, art) so every surface that reads one item still reads a real one; the whole selection
    /// travels as <see cref="RootlistItems"/>, which is what the drop decision checks and what the commit moves.</para>
    ///
    /// <para>A selection of TWO OR MORE carries NO track resolver: a rootlist multi-select is an ORGANISATION gesture,
    /// and offering "deposit the first playlist's songs" for a drag the user aimed at five rows would deposit a lie.
    /// N=1 is byte-for-byte <see cref="FromEntry"/>, which is why there is no second single-item path.</para></summary>
    public static WaveeResourceDragPayload FromEntries(IReadOnlyList<SidebarLibraryEntry> orderedTreeEntries,
                                                       Services? svc)
    {
        if (orderedTreeEntries is not { Count: > 0 })
        {
            // Nothing to lift. Logged rather than thrown: a drag source is a pointer path, and an empty selection is a
            // caller bug — but an inert payload (empty key ⇒ every target refuses it) is a better failure than a crash.
            svc?.Log.Warn("drag", "rootlist drag payload requested for an EMPTY selection");
            return new(WaveeResourceKind.Playlist, "", "", "", RootlistItem: true,
                       RootlistItems: Array.Empty<RootlistItemRef>());
        }

        var first = orderedTreeEntries[0];
        int n = orderedTreeEntries.Count;
        if (n == 1) return FromEntry(first, svc, rootlistItem: true);

        var refs = new RootlistItemRef[n];
        for (int i = 0; i < n; i++) refs[i] = RootlistTreeNav.RefOf(orderedTreeEntries[i]);
        var kind = WaveeDragKindMap.Of(first.Kind);
        return new(kind, first.Id, first.Uri, Strings.Sidebar.ItemCount(n), RootlistItem: true,
                   ArtUrl: WaveeDragChipModel.ArtOf(first.Cover), RootlistItems: refs);
    }

    public static WaveeResourceDragPayload FromDestination(SidebarDestination destination, ActionServices? acts)
    {
        var kind = destination.Kind switch
        {
            SidebarEntryKind.Playlist => WaveeResourceKind.Playlist,
            SidebarEntryKind.Album => WaveeResourceKind.Album,
            SidebarEntryKind.Artist => WaveeResourceKind.Artist,
            SidebarEntryKind.Show => WaveeResourceKind.Show,
            SidebarEntryKind.Folder => WaveeResourceKind.Folder,
            SidebarEntryKind.AppRoute => WaveeResourceKind.Route,
            _ => WaveeResourceKind.Route,   // Track — a SidebarDestination is never built from one (FromRoute only)
        };
        // A destination is a ROUTE record — it carries no cover (the tab strip never had one to show), so a tab drag's
        // chip falls back to the kind glyph tile. It carries no OWNERSHIP either, so rootlist membership is looked up
        // rather than assumed: that is what lets a tab dragged onto a sidebar folder file itself (see WaveeRootlist).
        return new(kind, destination.PinId, destination.Uri, destination.Name,
            TrackResolver: ResolverFor(kind, destination.Uri, acts?.Svc),
            RootlistItem: WaveeRootlist.IsMember(acts, kind, destination.Uri));
    }

    /// <summary>ONE track in hand — the payload carries the track itself, so every playlist destination can actually
    /// deposit it. A track payload without this list resolves nothing (<c>CanCopyTracks</c> false) and is inert on the
    /// exact surfaces a song drag exists for.</summary>
    public static WaveeResourceDragPayload ForTrack(Track track)
        => new(PlayableKind(track.Uri), track.Id is { Length: > 0 } id ? id : track.Uri, track.Uri, track.Title,
               new[] { track });

    /// <summary>ONE row of the queue panel in hand. Byte-for-byte <see cref="ForTrack"/> (the chip still shows the song)
    /// plus the queue marker that makes it a REORDER gesture: <see cref="CanCopyTracks"/> is false, so the playlist
    /// surfaces that used to accept it as a copy — and turned a reorder into "Added to {playlist}" — go dark, and the
    /// chip's resting verb says what the drag is for (<see cref="WaveeResourceDrag.Chip"/>).</summary>
    public static WaveeResourceDragPayload ForQueueRow(QueueEntry entry)
        => ForTrack(entry.Track) with { SourceQueueItemId = entry.ItemId };

    /// <summary>The drag kind of one PLAYABLE row. An episode rides the same <c>Track</c> read-model as a song
    /// (<c>EpisodeAsTrack</c>, design §1.5) but it is not one, and the chip's glyph and the drop captions read the KIND
    /// — so the row states which it is. Anything else (a song, a local import whose uri is its encoded file path, an
    /// unclassifiable uri) stays <see cref="WaveeResourceKind.Track"/>: it is a playable with a track snapshot behind
    /// it, which is exactly what every destination acts on.</summary>
    public static WaveeResourceKind PlayableKind(string uri)
        => EntityUri.KindOf(uri) == EntityKind.Episode ? WaveeResourceKind.Episode : WaveeResourceKind.Track;

    /// <summary>A track SELECTION from a list that is not an editable playlist (search results, an album drawer, the
    /// top-tracks chart, a recommendation strip). No <c>SourcePlaylistUri</c>/<c>SourceRows</c>: with no membership
    /// rows behind it every destination correctly treats this as a COPY rather than a move.</summary>
    public static WaveeResourceDragPayload? ForTracks(IReadOnlyList<Track> tracks)
        => tracks.Count switch
        {
            0 => null,
            1 => ForTrack(tracks[0]),
            _ => new(WaveeResourceKind.Track, tracks[0].Uri, tracks[0].Uri,
                     Strings.Sidebar.SongCount(tracks.Count), tracks),
        };

    /// <summary>The navigable ENTITY a card or a hero cover stands for. The uri doubles as the identity (a card has no
    /// sidebar pin id), the resolver comes from the kind, and rootlist membership is LOOKED UP rather than guessed —
    /// a card never knows it on its own, and claiming it falsely would offer a folder a move it cannot perform.</summary>
    public static WaveeResourceDragPayload ForEntity(WaveeResourceKind kind, string uri, string name,
                                                     Image? cover, ActionServices? acts, string? artUrl = null)
        => new(kind, SidebarPinId.Canonical(uri) ?? uri, uri, name,
               TrackResolver: ResolverFor(kind, uri, acts?.Svc),
               RootlistItem: WaveeRootlist.IsMember(acts, kind, uri),
               ArtUrl: artUrl ?? WaveeDragChipModel.ArtOf(cover));

    internal static Func<CancellationToken, Task<IReadOnlyList<Track>>>? ResolverFor(
        WaveeResourceKind kind, string uri, Services? svc)
    {
        if (svc is null || uri.Length == 0) return null;
        if (kind == WaveeResourceKind.Playlist)
            return async ct => (await svc.Library.GetPlaylistAsync(uri, ct: ct).ConfigureAwait(false))?.Tracks
                ?? Array.Empty<Track>();
        if (kind == WaveeResourceKind.Album)
            return async ct => (await svc.Library.GetAlbumAsync(uri, ct: ct).ConfigureAwait(false))?.Tracks
                ?? Array.Empty<Track>();
        // Deliberately NOT resolved:
        //  • ARTIST — locked product decision: an artist has no single obvious track set, so an artist dropped on a
        //    playlist is refused with a cue instead of silently depositing a guess. Future work: let the user CHOOSE
        //    what to deposit (top tracks / a discography picker) rather than the app inventing an answer.
        //  • SHOW / EPISODE — Wavee.Core models an episode as its own record, not a Track (no artists, no album ref),
        //    and a synthetic Track would be a fabrication that leaks into real playlist mutations. Adding episodes to
        //    playlists needs an Episode-aware deposit seam, not an adapter here.
        return null;
    }
}

/// <summary>Is this resource one of the user's OWN rootlist items (a saved playlist, or a folder in their sidebar
/// tree)? Only a rootlist member can be FILED into a sidebar folder, and only the library store actually knows —
/// a home card, a search hit and a tab all carry a bare uri.
/// <para>The lookup is a linear scan of the already-loaded playlist summaries, run ONCE per gesture at drag promotion
/// (the payload factory is cold by contract), never per pointer move. It answers false when the store has not loaded,
/// which is the honest reading: "not known to be in the rootlist" must not present as "is".</para></summary>
static class WaveeRootlist
{
    public static bool IsMember(ActionServices? acts, WaveeResourceKind kind, string uri)
    {
        // A FOLDER only ever originates inside the sidebar tree, where the projection sets the flag directly; there is
        // no folder uri to look up here.
        if (kind != WaveeResourceKind.Playlist || uri.Length == 0) return false;
        if (acts?.Store is not { } store) return false;
        var playlists = store.Playlists.Value.Peek();
        for (int i = 0; i < playlists.Count; i++)
            if (string.Equals(playlists[i].Uri, uri, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Is this playlist one the user can WRITE to? The same linear scan of the loaded rootlist summaries as
    /// <see cref="IsMember"/>, reading the <c>CanEdit</c> the sidebar/menus already gate on
    /// (<c>Menus.SidebarPlaylistRows</c>, <c>PlaylistPicker</c>). Callers that hold a full <c>DetailModel</c> should use
    /// its <c>Capabilities.CanEditItems</c> instead — this exists for the surfaces that hold nothing but a uri (a TAB).
    /// <para>Not loaded ⇒ false, the same honest reading <see cref="IsMember"/> takes: "not known to be editable" must
    /// not present as "is". The cost of that is a tab that refuses a deposit until the rootlist has arrived, which is
    /// strictly better than one that accepts a drop the server will reject.</para></summary>
    public static bool CanEditPlaylist(ActionServices? acts, string? uri)
    {
        if (!TabDropRules.IsDepositablePlaylistUri(uri) || acts?.Store is not { } store) return false;
        var playlists = store.Playlists.Value.Peek();
        for (int i = 0; i < playlists.Count; i++)
            if (string.Equals(playlists[i].Uri, uri, StringComparison.Ordinal)) return playlists[i].CanEdit;
        return false;
    }
}

static class WaveeResourceDrag
{
    /// <summary>How long a drag has to rest on a container before it opens itself (a collapsed sidebar folder expands,
    /// a tab activates). 500ms is the platform convention shared by macOS spring-loaded folders and WinUI's
    /// hold-to-open surfaces — long enough that merely travelling ACROSS a folder never opens it.</summary>
    public const float SpringLoadMs = 500f;

    /// <summary>Unwrap either a plain source or an item owned by <see cref="Reorderable"/>.</summary>
    public static WaveeResourceDragPayload? Unwrap(object? payload) => payload switch
    {
        WaveeResourceDragPayload direct => direct,
        ReorderPayload { Item: WaveeResourceDragPayload wrapped } => wrapped,
        _ => null,
    };

    /// <summary>Is a SAME-LIST reorder of <paramref name="playlistUri"/> live right now?
    /// <para>The one question a page has to answer before it publishes a re-projection of its own rows: while the user
    /// is aiming a drag at those very rows, a fresh membership snapshot re-keys the list under the pointer and the drop
    /// lands somewhere they did not aim (<c>PlaylistReorderDefer</c>). A FOREIGN session — a drag from another list, an
    /// OS file drag — has no stake in this list's order and is deliberately not reported.</para>
    /// <para>Reads the live <c>DragDropContext</c> session through the host's drag-state seam, so it is a plain
    /// synchronous read with no subscription: the caller is an event/refresh path, not a render.</para></summary>
    public static bool LiveSameListReorder(string? playlistUri)
    {
        if (string.IsNullOrEmpty(playlistUri)) return false;
        var state = InputHooks.Current.Default.GetDragState?.Invoke() ?? default;
        if (!state.Active) return false;
        return Unwrap(state.Payload) is { SourceRows.Count: > 0 } resource
               && string.Equals(resource.SourcePlaylistUri, playlistUri, StringComparison.Ordinal);
    }

    /// <summary>Is a ROOTLIST organisation drag (a playlist or folder being re-filed in the sidebar tree) live right
    /// now? The sibling of <see cref="LiveSameListReorder"/>, and it exists for the same reason: while the user is
    /// aiming at the tree's rows, a fresh projection re-keys those rows under the pointer and the drop lands somewhere
    /// they did not aim. Same seam, same plain synchronous read, no subscription.</summary>
    public static bool LiveRootlistDrag()
    {
        var state = InputHooks.Current.Default.GetDragState?.Invoke() ?? default;
        if (!state.Active) return false;
        return Unwrap(state.Payload) is { RootlistItem: true } resource
               && resource.Kind is WaveeResourceKind.Playlist or WaveeResourceKind.Folder;
    }

    /// <summary>The app's chip DATA for a live drag — Wavee's whole contribution to the drag visual. The framework
    /// renders it (opaque compact card, art + title + subtitle, corner count badge and stacked backdrop for a
    /// multi-select, tilt, caption, not-allowed cue, cursor offset, window clamp); this decides only what it says.
    /// The resolution rules themselves live in the engine-free <see cref="WaveeDragChipModel"/>.</summary>
    public static DragChipSpec? Chip(DragState state)
    {
        // PHASE 3 — the sidebar customizer's palette chips. A SECOND kind, deliberately handled in the ONE resolver the
        // shell mounts: `DragPreviewLayer.Of` takes exactly one, so a surface whose kind is unknown here draws no moving
        // visual at all (the dnd skill's "two visuals for one gesture" pitfall, in its other direction) and — because the
        // chip is also the only caption surface — publishes neither its drop caption nor its refusal reason.
        //
        // The section-card REORDER band shares this kind but carries a `ReorderPayload` whose Item is null, so it falls
        // through to null here and keeps its deliberate ghost lift (SidebarPane.SectionReorder's remarks).
        if (string.Equals(state.Kind, SidebarEditPlan.SectionDragKind, StringComparison.Ordinal))
            return state.Payload is SidebarSectionDropPayload section
                ? new DragChipSpec(Title: section.Label, Glyph: CzGlyphs.ForKind(section.Kind), Count: 1,
                                   RestingCaption: Loc.Get(SidebarPaneLoc.EditDropHere))
                : null;

        if (!string.Equals(state.Kind, WaveeDragKinds.Resource, StringComparison.Ordinal)
            || Unwrap(state.Payload) is not { } payload) return null;
        var model = payload.ChipModel();
        return new DragChipSpec(
            // Liked Songs travels wearing its own cover, not the generic playlist glyph. See LikedChipArt for why it
            // is a pre-built element rather than a call.
            Art: LikedSongsArtwork.IsLikedUri(payload.Uri) ? LikedChipArt : null,
            ArtSource: model.ArtUrl, Title: model.Title, Subtitle: model.Subtitle,
            Count: model.Count, Glyph: GlyphFor(payload.Kind),
            // The RESTING verb — what the chip says while travelling, before anything accepts. Reported as part of
            // "drag & drop is unclear" (2026-08-10): the card showed a song title and an artist and never a verb, so the
            // gesture gave no evidence it was even armed for a playlist. A live target's caption supersedes it the moment
            // one accepts, and a refusal reason supersedes it when one refuses. Loc.Get is a table lookup of an interned
            // string — no interpolation, so this stays safe inside the 0-alloc frame region.
            //
            // A ROOTLIST payload gets its OWN verb (D14): "Drag onto a playlist to add" is the wrong sentence for a
            // gesture that is organising the sidebar — the user is not adding anything, and a folder drag (which can add
            // nothing at all) used to travel with no caption whatsoever.
            //
            // A QUEUE row gets its own verb for the same reason in the other direction: it used to travel wearing
            // "Drag onto a playlist to add" — the exact promise the drop then kept, which is how a reorder attempt
            // ended as a playlist add. It is a reorder, and that is all the chip may claim it is.
            RestingCaption: payload.FromQueue
                ? Loc.Get(Strings.Drag.ReorderHint)
                : payload.RootlistItem && payload.Kind is WaveeResourceKind.Playlist or WaveeResourceKind.Folder
                    ? Loc.Get(Strings.Drag.OrganizeHint)
                    : payload.CanCopyTracks ? Loc.Get(Strings.Drag.DragOntoPlaylist) : null);
    }

    /// <summary>The one preview mounted at the shell root (<c>DragPreviewLayer.Of</c>).</summary>
    public static readonly Func<DragState, Element?> Preview = DragChip.Resolve(Chip);

    /// <summary>The liked collection's chip artwork, BUILT ONCE at type init.
    ///
    /// <para><see cref="Chip"/> runs inside the 0-alloc frame region while a drag is live (the same constraint that
    /// makes <c>RestingCaption</c> a table lookup and never an interpolation), and
    /// <c>LikedSongsArtwork.Dynamic</c> allocates a factory closure and a key string per call. The chip's art box is a
    /// fixed 40 DIP, so there is exactly one element to build and it can be built ahead of the gesture; an immutable
    /// Element re-handed to the reconciler unchanged is a no-op, and only one drag chip exists at a time.</para>
    ///
    /// <para>40 is below the treatment floor, so this paints the flat 2x2 mosaic of the newest likes (or the stock PNG)
    /// — which is still the collection's own art, where the alternative was a generic music-note tile.</para></summary>
    static readonly Element LikedChipArt = LikedSongsArtwork.Dynamic(DragChip.ArtSize, Radii.Control);

    /// <summary>The art-less fallback tile: the same kind glyphs the sidebar uses, so a cover-less drag still reads as
    /// "a playlist" / "an album" rather than as a generic note.</summary>
    static string GlyphFor(WaveeResourceKind kind) => kind switch
    {
        WaveeResourceKind.Playlist => Icons.MusicNote,
        WaveeResourceKind.Album => Icons.Album,
        WaveeResourceKind.Artist => Icons.Contact,
        WaveeResourceKind.Show or WaveeResourceKind.Episode => Icons.RadioTower,
        WaveeResourceKind.Folder => Icons.Folder,
        WaveeResourceKind.Route => Icons.Home,
        _ => Icons.MusicNote,
    };
}

/// <summary>The detail page's HERO cover as a drag source: dragging the artwork drags the whole entity the page is
/// about (drop it on a sidebar playlist to add its tracks, on a folder to file it, on the pin band to pin it).
/// <para>It coexists with the editable-cover FILE drop target underneath: those are opposite directions of two
/// different gestures — a press-and-drag on the cover LIFTS this payload, while an OS file drag hovering the cover
/// still targets the <c>DropKinds.Files</c> spec, which never accepts <see cref="WaveeDragKinds.Resource"/>. They also
/// live on different nodes (this on the framing box, the file target on the editable cover inside it).</para></summary>
static class WaveeDetailDrag
{
    public static DragSource? Hero(DetailModel m, ActionServices? acts)
    {
        if (m.ContextUri is not { Length: > 0 } uri) return null;
        var kind = WaveeDragKindMap.OfUri(uri);
        if (kind == WaveeResourceKind.Route) return null;   // nothing this payload could be dropped on
        return Drag.Source(WaveeDragKinds.Resource,
            () => WaveeResourceDragPayload.ForEntity(kind, uri, m.Title, m.Cover, acts));
    }
}

static class WaveeResourceDrop
{
    /// <summary>May a drop of <paramref name="payload"/> deposit tracks here? False for a payload with nothing to
    /// resolve AND for a live QUEUE drag (<see cref="WaveeResourceDragPayload.FromQueue"/> — a reorder, never a copy;
    /// the rule is inside <c>CanCopyTracks</c> so the targets that read that property directly agree with this one).</summary>
    public static bool CanDepositTracks(object? payload)
        => WaveeResourceDrag.Unwrap(payload) is { CanCopyTracks: true };

    /// <summary>File one rootlist item at a RESOLVED destination.
    ///
    /// <para>Fire-and-forget with an error-only toast is what this used to be (D13): a successful move — the whole point
    /// of the gesture — produced no confirmation, no announcement and no way back. It now says where the item landed,
    /// announces it for a screen reader (the same discipline as <c>FolderActions</c> and the bridge's announced-edit
    /// chokepoint), and offers the INVERSE move as Undo. The inverse rides the very same seam, so there is no second
    /// mutation path to keep in sync — <paramref name="undoAnchor"/> is simply where the item was before.</para></summary>
    /// <para>ONE drop issues ONE <c>MoveRootlistItemsAsync</c> — a multi-select is a BATCH, never N drops: the seam
    /// applies its ops sequentially inside one Delta, so the relative order of the moved items survives (the ordering
    /// rule itself is <c>RootlistBatchOrder.For</c>, and it is the same list the cue's legality check asked about).</para>
    /// <param name="destinationName">The folder the items landed in; empty = the top level ("Your Library").</param>
    /// <param name="undoMoves">The pre-move anchors (<c>RootlistUndoAnchors.TryResolveMany</c>), replayed as ONE batch.
    /// Null/empty ⇒ the toast appears WITHOUT Undo rather than offering one that would land somewhere else.</param>
    public static void MoveRootlist(ActionServices acts, object? payload, RootlistItemRef target,
                                    RootlistDropPlacement placement, string? destinationName,
                                    IReadOnlyList<RootlistMove>? undoMoves = null)
    {
        if (acts.Library is not { } lib || WaveeResourceDrag.Unwrap(payload) is not { RootlistItem: true } source)
        {
            // Not a refusal the user aimed at — a payload that never belonged to the rootlist, or a host with no
            // library bridge. It is still LOGGED: a drop that reaches this line and vanishes is the exact shape of
            // failure this whole change exists to remove.
            acts.Svc?.Log.Warn("drag", "rootlist move ignored: no library bridge, or the payload is not a rootlist item");
            return;
        }
        var moves = RootlistBatchOrder.For(RootRefs(source), target, placement);
        if (moves.Count == 0 || target.Key.Length == 0)
        {
            acts.Svc?.Log.Warn("drag", $"rootlist move ignored: sources={source.RootlistCount} target='{target.Key}'");
            Toast.Show(Loc.Get(Strings.Drag.CantMoveHere), new ToastOptions { Severity = InfoBarSeverity.Informational });
            return;
        }
        _ = Run();

        async Task Run()
        {
            try { await lib.MoveRootlistItemsAsync(moves).ConfigureAwait(false); }
            // Mapped by VERB: this is an ORDERING that did not stick, not an add and not a remove — never the raw
            // exception text.
            catch (Exception ex)
            {
                acts.Post?.Invoke(() => PlaylistEditErrors.Toast(ex, PlaylistEditVerb.Reorder));
                return;
            }
            acts.Post?.Invoke(() => Confirm(acts, lib, moves.Count, destinationName, undoMoves));
        }
    }

    static void Confirm(ActionServices acts, LibraryBridge lib, int count, string? destinationName,
                        IReadOnlyList<RootlistMove>? undoMoves)
    {
        // ONE sentence per outcome, and the plural one NAMES the destination too: "Moved to Your Library" cannot carry
        // a count, and a 5-item filing that reports the singular reads as a move that only took one of them.
        string where = count > 1
            ? Strings.Drag.MovedManyTo(count, destinationName is { Length: > 0 } many
                                              ? many : Loc.Get(Strings.Sidebar.YourLibrary))
            : destinationName is { Length: > 0 } name ? Strings.Drag.MovedTo(name)
                                                     : Loc.Get(Strings.Drag.MovedToLibrary);
        // A rootlist move is a SILENT structural change to a list the user may not be looking at — announced for the
        // same reason a folder create is (FolderActions.Announce): one call at the one chokepoint.
        if (Announcer.IsAvailable) Announcer.SayThrottled(where);
        var options = new ToastOptions { Severity = InfoBarSeverity.Success };
        if (undoMoves is { Count: > 0 } undo)
            options = options with
            {
                ActionLabel = Loc.Get(Strings.Sidebar.Pin.Undo),
                OnAction = () => _ = UndoAsync(acts, lib, undo),
            };
        Toast.Show(where, options);
    }

    static async Task UndoAsync(ActionServices acts, LibraryBridge lib, IReadOnlyList<RootlistMove> undo)
    {
        // The inverse rides the very same seam in ONE batch, so an undone multi-select restores in one Delta rather
        // than as N racing writes.
        try { await lib.MoveRootlistItemsAsync(undo).ConfigureAwait(false); }
        catch (Exception ex) { acts.Post?.Invoke(() => PlaylistEditErrors.Toast(ex, PlaylistEditVerb.Reorder)); }
    }

    /// <summary>THE rootlist references a drag PAYLOAD moves AS, in tree order — a folder by its group id, a playlist by
    /// its uri. A single-item drag answers a list of ONE, which is why the decision, the cue and the commit have no
    /// separate single-item path at all.
    /// <para>Shared with the sidebar's drop decision so the cue's legality question and the mutation address the same
    /// items (<c>RootlistTreeNav.RefOf</c> is the same rule over a projection ENTRY — a different input shape).</para></summary>
    internal static IReadOnlyList<RootlistItemRef> RootRefs(WaveeResourceDragPayload payload)
        => payload.RootlistItems is { Count: > 0 } many ? many : [SingleRef(payload)];

    static RootlistItemRef SingleRef(WaveeResourceDragPayload payload)
        => payload.Kind == WaveeResourceKind.Folder
            ? new RootlistItemRef(SidebarPinId.FolderIdOf(payload.Id), IsFolder: true)
            : new RootlistItemRef(payload.Uri, IsFolder: false);

    /// <summary>Is this tree row one of the items the drag is CARRYING? The resolver's <c>SourceIsSelf</c> fact, and the
    /// only payload legality question left in the geometry layer — "into MYSELF" and "before myself" need two different
    /// sentences where the marker stream reports one <c>SameItem</c>.
    /// <para>Asks the row BOTH ways because the two identities the sidebar addresses a row by are different strings: the
    /// projection's entry id (<c>pl:&lt;uri&gt;</c> / <c>folder:&lt;groupId&gt;</c>) and the bare uri the seam moves a
    /// playlist as.</para></summary>
    public static bool IsSource(WaveeResourceDragPayload? payload, string entryId, string uri)
    {
        if (payload is null) return false;
        if (payload.RootlistItems is { Count: > 0 } refs)
        {
            string folderId = SidebarPinId.FolderIdOf(entryId);
            for (int i = 0; i < refs.Count; i++)
            {
                var r = refs[i];
                if (r.Key.Length == 0) continue;
                if (r.IsFolder
                    ? folderId.Length > 0 && string.Equals(r.Key, folderId, StringComparison.Ordinal)
                    : uri.Length > 0 && string.Equals(r.Key, uri, StringComparison.Ordinal)) return true;
            }
            return false;
        }
        return (entryId.Length > 0 && string.Equals(payload.Id, entryId, StringComparison.Ordinal))
            || (uri.Length > 0 && string.Equals(payload.Uri, uri, StringComparison.Ordinal));
    }

    /// <summary>Commit one playlist deposit. Same-playlist row drags move stable membership rows; every other resource
    /// resolves to an ordered track snapshot and copies it. A null insertion index means append (sidebar target).</summary>
    public static void DepositTracks(ActionServices acts, string targetUri, string targetName,
                                     object? payload, int? insertionIndex)
        => _ = DepositTracksAsync(acts, targetUri, targetName, payload, insertionIndex);

    /// <summary>Deposit without the built-in confirmation toast, for a caller that owns a BETTER one. A drop that CREATED
    /// the playlist it just filled wants "Open" (the new playlist needs a name), not the generic "Added to {name}" — and
    /// two toasts for one gesture reads as a bug.</summary>
    public static Task<bool> DepositTracksSilentAsync(ActionServices acts, string targetUri, object? payload,
                                                      int? insertionIndex)
        => DepositTracksAsync(acts, targetUri, "", payload, insertionIndex, toast: false);

    /// <summary>The awaitable deposit seam used by live insertion previews. The library mutation source publishes its
    /// optimistic snapshot synchronously before the returned network task completes, so callers can hand visual ownership
    /// to the real list immediately while still retaining an error-completion edge.
    /// <para>The result is "a mutation was issued and completed" — false for every no-op refusal below and for a failed
    /// commit. A live insertion preview keys its teardown on it: only a true can promise the membership snapshot that
    /// closes the gap, so the refusals must be answerable SYNCHRONOUSLY wherever possible (they are, except the empty
    /// track resolve) or the preview waits on a handoff that never comes.</para></summary>
    public static Task<bool> DepositTracksAsync(ActionServices acts, string targetUri, string targetName,
                                                object? payload, int? insertionIndex, bool toast = true)
    {
        if (acts.Library is not { } lib || WaveeResourceDrag.Unwrap(payload) is not { } resource)
            return Task.FromResult(false);
        if (!resource.CanCopyTracks) return Task.FromResult(false);
        bool sameList = insertionIndex is not null
            && string.Equals(resource.SourcePlaylistUri, targetUri, StringComparison.Ordinal)
            && resource.SourceRows is { Count: > 0 };
        // Dropping a playlist onto itself without membership refs would append a duplicate of the entire playlist.
        // Treat that ambiguous container-on-itself gesture as a no-op; track-row drags still move.
        if (!sameList && resource.Kind == WaveeResourceKind.Playlist
            && string.Equals(resource.Uri, targetUri, StringComparison.Ordinal))
            return Task.FromResult(false);
        return Run();

        async Task<bool> Run()
        {
            try
            {
                bool moved = false;
                if (sameList && insertionIndex is { } at && resource.SourceRows is { } rows)
                {
                    // `at` is the PRE-move insertion index ("insert before the row currently at this index") — the
                    // convention PlaylistMutationSource.BuildKeyedMove / UserPlaylistSource.MoveRows both implement by
                    // discounting the rows removed above it. Pinned by MoveRowsConventionTests; do NOT pre-correct here.
                    await lib.MovePlaylistRowsTrackedAsync(targetUri, rows, at).ConfigureAwait(false);
                    moved = true;
                }
                else
                {
                    var tracks = await resource.ResolveTracksAsync().ConfigureAwait(false);
                    if (tracks.Count == 0) return false;
                    if (insertionIndex is { } insert)
                        await lib.InsertTracksAsync(targetUri, tracks, insert).ConfigureAwait(false);
                    else
                        await lib.AddTracksAsync(targetUri, tracks).ConfigureAwait(false);
                }

                if (!moved && toast)
                    acts.Post?.Invoke(() => Toast.Show(Strings.Detail.AddedToPlaylist(targetName),
                        new ToastOptions { Severity = InfoBarSeverity.Success }));
                return true;
            }
            // Mapped by VERB: a refused reorder and a refused copy are different sentences, and a same-list drop is
            // always the former. The rows have already snapped back (the store rolled the optimistic move back) — this
            // is the only thing that says why.
            catch (Exception ex)
            {
                acts.Post?.Invoke(() => PlaylistEditErrors.Toast(ex, sameList ? PlaylistEditVerb.Reorder : PlaylistEditVerb.Add));
                return false;
            }
        }
    }
}
