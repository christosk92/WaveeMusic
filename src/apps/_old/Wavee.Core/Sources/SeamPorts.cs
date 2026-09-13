namespace Wavee.Core;

// Seam-only facet ports (docs/plans/wavee/architecture.md §4.2, §9). A source implements the facets it supports and declares them
// via SourceCapabilities; the UI and the aggregate do not change shape.

/// <summary>The Session/account facet: auth + current user + the gating context (tier / market / locale) a real
/// source carries. Availability checks at queue time consult this.</summary>
public interface ISessionSource : ISource
{
    AuthStatus Status { get; }
    WaveeUser? CurrentUser { get; }
    IObservable<AuthStatus> StatusChanged { get; }
}

/// <summary>The Podcasts facet: shows + their episodes (docs/plans/wavee/architecture.md §2). A capability-segregated read port kept
/// OFF <see cref="ICatalogSource"/> so music-only sources don't carry empty podcast reads; the aggregate routes to it via
/// <c>OfCapability(Podcasts)</c>. (The export has no podcast data, so the in-process source synthesizes it.)</summary>
public interface IPodcastSource : ISource
{
    Task<IReadOnlyList<Show>> GetShowsAsync(CancellationToken ct = default);
    Task<Show?> GetShowAsync(string uri, HydrationLevel level = HydrationLevel.Open, CancellationToken ct = default);

    /// <summary>Page the NEXT block of episodes of an already-open show into residency, starting at membership index
    /// <paramref name="from"/> (design 2.3: the show ladder brings the first <c>HydrationLevels.ShowOpenPage</c> up on
    /// open and pages the rest; this is the explicit "the user scrolled to the end" ask for the next one).
    /// <para>Returns the NEW paging cursor — the membership offset that has now been asked for (<c>Show.PagedThrough</c>).
    /// It comes back unchanged (<c>== from</c>) when there was nothing left to ask for, which is what lets the episode
    /// list drop its load-more affordance. A cursor rather than a bool because a page can legitimately land ZERO rows
    /// (withdrawn / region-locked episodes): the caller must still advance, or the same unanswerable page is re-asked on
    /// every tap and the pill never goes away.</para>
    /// <para>A DEFAULT member returning <paramref name="from"/>: a synthetic source hands back its whole show in one
    /// read and has no second page to fetch, so it should not be forced to write a stub.</para></summary>
    Task<int> LoadMoreEpisodesAsync(string showUri, int from, CancellationToken ct = default) => Task.FromResult(from);
}

/// <summary>The Mutations facet: save / like / follow (saved-state) — optimistic local writes the UI gates on
/// <see cref="SourceCapabilities.Mutations"/> (and, for playlist item edits, the playlist's <see cref="PlaylistCapabilities"/>).
/// The set spans tracks (like), albums (save) and artists + playlists (follow); a real source reconciles via an outbox +
/// revision conflicts (docs/plans/wavee/architecture.md §3). Playlist item edits + folders are the next Mutations increment (§9 seam).</summary>
public interface IMutationSource : ISource
{
    /// <summary>Snapshot of the currently saved / liked / followed uris.</summary>
    IReadOnlySet<string> Saved { get; }
    bool IsSaved(string uri);
    /// <summary>Emits the full saved-set on every change, so a bridge can mirror it into an engine Signal (§6).</summary>
    IObservable<IReadOnlySet<string>> SavedChanged { get; }
    /// <summary>Set the saved/followed state of a uri (idempotent) — optimistic + persisted in the in-process source.</summary>
    Task SetSavedAsync(string uri, bool saved, CancellationToken ct = default);
}

/// <summary>First-party Spotify playlist editing: metadata, cover, item add/remove/move, permission level, contributor invites.
/// Local <c>wavee:playlist:*</c> playlists are handled by <see cref="UserPlaylistSource"/> instead.</summary>
public interface IPlaylistMutationSource
{
    /// <summary>Create a playlist SYNCHRONOUSLY: the optimistic row (header + empty membership + rootlist entry at
    /// <paramref name="placement"/> + the saved pill) is already in the store when this returns, so the caller can
    /// navigate to <see cref="PlaylistCreated.Uri"/> on the very next frame. The network rides the durable outbox as
    /// ORDERED ops (create → rootlist ADD → any seed tracks), so an offline create simply queues.</summary>
    PlaylistCreated CreatePlaylist(string name, RootlistPlacement placement);
    Task AddTracksAsync(string playlistUri, IReadOnlyList<Track> tracks, CancellationToken ct = default);
    /// <summary>Insert an ordered batch before <paramref name="toIndex"/>. Duplicates are membership rows, not entities,
    /// and therefore remain present and independently movable.</summary>
    Task InsertTracksAsync(string playlistUri, IReadOnlyList<Track> tracks, int toIndex, CancellationToken ct = default);
    Task RemoveRowsAsync(string playlistUri, IReadOnlyList<PlaylistRowRef> rows, CancellationToken ct = default);
    Task MoveRowsAsync(string playlistUri, IReadOnlyList<PlaylistRowRef> rows, int toIndex, CancellationToken ct = default);
    Task UpdateDetailsAsync(string playlistUri, string? name, string? description, bool? collaborative, CancellationToken ct = default);
    Task SetCoverJpegAsync(string playlistUri, byte[] jpeg, CancellationToken ct = default);
    Task SetPlaylistVisibilityAsync(string playlistUri, bool isPublic, CancellationToken ct = default);
    Task DeletePlaylistAsync(string playlistUri, CancellationToken ct = default);
    Task<string> CreateContributorInviteAsync(string playlistUri, CancellationToken ct = default);
    /// <summary>Move a whole ORDERED batch of rootlist rows/folders in ONE revisioned write. The moves are executed in
    /// the order given, each op built against the stream the preceding ops left behind — which is exactly how the
    /// server applies the ops of one <c>Delta</c> — so the batch is one POST, one optimistic apply and one rollback.
    /// <para>WHICH order a same-target batch is submitted in is a UI decision (drop <em>after</em> an anchor iterates
    /// the sources in reverse so each op lands adjacent to it); the seam only executes the list it is handed.</para>
    /// <para>Refusals are typed throws, never a quiet return: a batch is refused whole when ANY move is a cycle /
    /// missing / inexpressible, and a batch whose ops net to identity is a <see cref="PlaylistMutationFailure.NoOp"/>
    /// with nothing posted.</para></summary>
    Task MoveRootlistItemsAsync(IReadOnlyList<RootlistMove> moves, CancellationToken ct = default);
    /// <summary>The N=1 sugar over <see cref="MoveRootlistItemsAsync"/> — there is only one implementation.</summary>
    Task MoveRootlistItemAsync(RootlistItemRef source, RootlistItemRef target,
                               RootlistDropPlacement placement, CancellationToken ct = default);

    // ── rootlist folder CRUD (online-only, invariant I5: rootlist structural ops are index ops by nature) ─────────────
    /// <summary>Create a folder at <paramref name="placement"/>; returns the CLIENT-MINTED groupId (expansion state and
    /// pins are keyed by it, so they survive every later rename).</summary>
    Task<string> CreateFolderAsync(string name, RootlistPlacement placement, CancellationToken ct = default);
    /// <summary>Rename a folder in place. The wire shape re-sends the folder's ORIGINAL create timestamp, so the
    /// rootlist keeps its ordering facts.</summary>
    Task RenameFolderAsync(string groupId, string name, CancellationToken ct = default);
    /// <summary>Delete a folder's marker pair. Its children are NOT deleted — they move up one level.</summary>
    Task DeleteFolderAsync(string groupId, CancellationToken ct = default);
}

public readonly record struct RootlistItemRef(string Key, bool IsFolder);
public enum RootlistDropPlacement : byte { Before, After, Inside }

/// <summary>One element of a rootlist move batch: move <paramref name="Source"/> to <paramref name="Placement"/> of
/// <paramref name="Target"/>. A batch is applied in list order (see <see cref="IPlaylistMutationSource.MoveRootlistItemsAsync"/>).</summary>
public readonly record struct RootlistMove(RootlistItemRef Source, RootlistItemRef Target, RootlistDropPlacement Placement);

/// <summary>Applies one server-advertised automatic-playlist tuning option.</summary>
public interface IPlaylistTuningSource
{
    Task ApplyAsync(string playlistUri, string optionIdentifier, CancellationToken ct = default);
}

// ── Playlist mutation failure vocabulary (P1) ─────────────────────────────────────────────────────────────────────────
// EVERY playlist mutation failure that reaches the UI is a PlaylistMutationException carrying one of these kinds. The UI
// maps the KIND to copy — it never sniffs an exception message and never shows ex.Message. Backend code that used to
// throw InvalidOperationException("rootlist revision conflict …") / NotSupportedException now throws the typed form.

/// <summary>Why a playlist mutation could not be completed. <see cref="Unknown"/> is the catch-all the UI renders as the
/// generic "couldn't save your change" copy — it is never a message passthrough.</summary>
/// <remarks><see cref="NoOp"/> and <see cref="Invalid"/> exist because a REFUSED write used to return quietly: a
/// rootlist move whose op could not be built (the destination is where the item already is, or the placement is one the
/// marker stream cannot express) left <c>MoveRootlistItemAsync</c> with nothing to post and it simply returned — so the
/// caller posted its "Moved to …" confirmation for a move that never happened. Both are now thrown, and both have their
/// own sentence.</remarks>
public enum PlaylistMutationFailure : byte { Unknown = 0, Conflict, Forbidden, Deleted, Offline, Pending, NotSupported, NoOp, Invalid }

/// <summary>The ONLY failure type a playlist mutation surfaces to the UI (docs plan P1 "Shared contracts").</summary>
public sealed class PlaylistMutationException : Exception
{
    public PlaylistMutationException(PlaylistMutationFailure kind, string message, Exception? inner = null)
        : base(message, inner) => Kind = kind;

    public PlaylistMutationFailure Kind { get; }
}

/// <summary>Where a keyed move lands: at the head, at the tail, or immediately after one existing row.</summary>
public enum PlaylistMoveAnchorKind : byte { First, Last, AfterItem }

/// <summary>The destination of a keyed playlist move. <see cref="AfterItemId"/> is the membership row's stable
/// <c>item_id</c> and is required exactly when <see cref="Kind"/> is <see cref="PlaylistMoveAnchorKind.AfterItem"/>.</summary>
public readonly record struct PlaylistMoveAnchor(PlaylistMoveAnchorKind Kind, string? AfterItemId = null);

/// <summary>Where a new playlist/folder is inserted in the rootlist. <c>null</c> = top level (index 0).</summary>
public readonly record struct RootlistPlacement(string? ParentFolderId);

/// <summary>The result of a synchronous create: the optimistic row is ALREADY in the store when this returns, and
/// <see cref="Completion"/> observes the durable outbox drain that makes it real on the server.</summary>
public readonly record struct PlaylistCreated(string Uri, Task Completion);
