using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Backend.Playlists;
using Wavee.Core;

namespace Wavee;

/// <summary>
/// The Core→engine bridge for the Mutations facet (docs/plans/wavee/architecture.md §6 "a LibraryBridge") — the saved / liked /
/// followed set as a reactive <see cref="Signal{T}"/>. Subscribes the source's change stream, marshalling each callback
/// onto the UI thread via the post delegate. Toggles write OPTIMISTICALLY (the heart flips this frame) and the source
/// reconciles + re-emits the confirmed set. The heart / follow affordances read <see cref="Saved"/> (so they re-skin on
/// any change). Provided once at the app root via <see cref="Slot"/>; the saved-state federation hook is the registry's
/// <c>OfCapability(Mutations)</c> (a future multi-source FederatedMutations attaches there — deferred like Federated*).
/// </summary>
public sealed class LibraryBridge : IUndoTarget
{
    public static readonly Context<LibraryBridge?> Slot = new(null);

    readonly IMutationSource _mut;
    readonly UserPlaylistSource _playlists;
    readonly IPlaylistMutationSource _playlistEdits;
    readonly ActivityLog _activity;
    readonly List<IDisposable> _subs = [];
    readonly Dictionary<string, Signal<bool>> _savedByUri = new(StringComparer.Ordinal);
    // The per-playlist pending-edit counters (the _savedByUri pattern) + the set of playlists this bridge has ever
    // issued an edit for. The SET is what keeps "pending changes" honest: the outbox also carries saves and follows,
    // which are library changes rather than playlist edits, and counting those under "playlist changes still syncing"
    // would be a lie in the one place the user goes to find out what is happening.
    readonly Dictionary<string, Signal<int>> _pendingByUri = new(StringComparer.Ordinal);
    readonly HashSet<string> _editedUris = new(StringComparer.Ordinal);
    Wavee.Backend.MutationEngine? _mutations;
    Wavee.Backend.IStore? _store;
    Action<Action> _post = static a => a();
    bool _active;

    /// <summary>The saved-set — read by the heart / follow affordances (which subscribe → re-skin on any change).</summary>
    public Signal<IReadOnlySet<string>> Saved { get; }

    /// <summary>How many playlist edits this session has issued that the server has not acked yet. Read by the
    /// notification center's one "still syncing" line; 0 whenever everything has landed (and always 0 on a build with
    /// no real mutation engine, which genuinely has nothing queued).</summary>
    public Signal<int> PendingEditsTotal { get; } = new(0);

    public LibraryBridge(IMutationSource mut, UserPlaylistSource playlists, IPlaylistMutationSource playlistEdits, ActivityLog activity)
    {
        _mut = mut;
        _playlists = playlists;
        _playlistEdits = playlistEdits;
        _activity = activity;
        Saved = new Signal<IReadOnlySet<string>>(mut.Saved);
    }

    /// <summary>Subscribe the source's change stream → the signal. Idempotent. Call once from a mount effect with <c>UsePost()</c>.</summary>
    public void Activate(Action<Action> post)
    {
        if (_active) return;
        _active = true;
        _post = post;
        _subs.Add(_mut.SavedChanged.Subscribe(s => post(() => PublishSaved(s))));
        SubscribePending();
    }

    // ── pending playlist edits (the outbox, as a signal) ───────────────────────────────────────────────────────────
    /// <summary>Attach the real durable-outbox engine. Called by the composition root right after it builds the engine
    /// (the bridge itself is constructed earlier, before the store/outbox exist). A build with no real backend simply
    /// never calls this and reports 0 pending — which is the truth, not a swallowed dependency.</summary>
    public void AttachMutations(Wavee.Backend.MutationEngine engine)
    {
        _mutations = engine;
        if (_active) SubscribePending();
    }

    // ── the rootlist marker stream (THE legality authority) ────────────────────────────────────────────────────────
    /// <summary>Attach the real store. Called by the composition root beside <see cref="AttachMutations"/> and for the
    /// same reason: the bridge is built in the Services ctor, before the store exists.</summary>
    public void AttachRootlist(Wavee.Backend.IStore store) => _store = store;

    /// <summary>The rootlist EXACTLY as the server holds it — the flat marker stream with its balanced
    /// start-group/end-group (kind 1/2) rows. The sidebar's projection tree is built from this very list
    /// (<c>RootlistTreeBuilder.Build</c>), so it is the one representation in which "where does this land" and "is it
    /// legal" have the same answer as the write itself.
    /// <para>Null on a build with no real store. Callers must refuse rather than guess — an ordering decided against a
    /// stream nobody has would land at an index that means nothing.</para></summary>
    public IReadOnlyList<Wavee.Backend.RootlistEntry>? RootlistMarkers => _store?.Rootlist();

    /// <summary>Would this rootlist move be accepted, and if not why? THE one legality question in the app: the sidebar
    /// drop cue, the rail folder tile and the "Move to folder…" picker all ask it here, and
    /// <c>PlaylistMutationSource.MoveRootlistItemAsync</c> re-asks the same builder when it writes — so a cue that says
    /// yes cannot be followed by a write that quietly does nothing (F1/F3).
    /// <para><see cref="RootlistMoveCheck.Missing"/> when there is no marker stream at all.</para></summary>
    public RootlistMoveCheck CheckRootlistMove(RootlistItemRef source, RootlistItemRef target,
                                               RootlistDropPlacement placement)
        => RootlistDropDecision.Check(RootlistMarkers, source, target, placement);

    void SubscribePending()
    {
        if (_mutations is not { } engine) return;
        var post = _post;
        _subs.Add(engine.PendingChanged.Subscribe(Wavee.Backend.Observers.From<string>(uri => post(() => PublishPending(uri)))));
    }

    /// <summary>The live pending-edit count for ONE playlist — the header chip's source. Reading it subscribes the
    /// caller to that uri only, so a drain elsewhere in the library never re-renders this page's header.</summary>
    public IReadSignal<int> PendingEdits(string playlistUri)
    {
        var state = PendingSignal(playlistUri);
        _editedUris.Add(playlistUri);       // an OPEN playlist counts toward the total even if we did not edit it here
        return state;
    }

    Signal<int> PendingSignal(string playlistUri)
    {
        if (!_pendingByUri.TryGetValue(playlistUri, out var state))
        {
            state = new Signal<int>(_mutations?.PendingFor(playlistUri) ?? 0);
            _pendingByUri.Add(playlistUri, state);
        }
        return state;
    }

    void PublishPending(string entityKey)
    {
        if (_mutations is not { } engine) return;
        if (_pendingByUri.TryGetValue(entityKey, out var state)) state.Value = engine.PendingFor(entityKey);
        int total = 0;
        foreach (var uri in _editedUris) total += engine.PendingFor(uri);
        PendingEditsTotal.Value = total;
    }

    /// <summary>Record a playlist as one this session edits, so its pending count is part of <see cref="PendingEditsTotal"/>.</summary>
    void TrackEdited(string playlistUri)
    {
        if (playlistUri.Length == 0) return;
        _editedUris.Add(playlistUri);
        PublishPending(playlistUri);
    }

    // ── playlist edits (create + add) ──────────────────────────────────────────────────────────────────────
    /// <summary>Create a Spotify playlist through the P3 seam. SYNCHRONOUS by contract: the optimistic header, the empty
    /// membership and the rootlist row are in the store when this returns, so the ONE create path
    /// (<c>PlaylistCreateFlow</c>) can navigate on the very next frame — a real, 0-track owner page instead of a
    /// skeleton that waits for an ack. <see cref="PlaylistCreated.Completion"/> is what says whether it became real;
    /// the flow observes it and calls <see cref="SettleCreate"/>, which is what <see cref="IsCreatePending"/> /
    /// <see cref="IsCreateFailed"/> (the playlist page's notice rule) read.</summary>
    public PlaylistCreated CreatePlaylist(string name, RootlistPlacement placement)
    {
        var created = _playlistEdits.CreatePlaylist(name, placement);
        _createFailed.Remove(created.Uri);
        _createPending.Add(created.Uri);
        if (!_activity.IsSuppressed) _activity.Record(ActivityKind.PlaylistCreate, created.Uri, name);   // log-only (no Undo)
        return created;
    }

    // The create lifecycle, as two URI sets rather than a signal: the ONE reader is the playlist page's notice rule,
    // which re-decides on every reload anyway, and a per-uri signal here would outlive the handful of frames a create
    // is actually in flight. Written only from the flow's UI-thread post, read only on the UI thread.
    readonly HashSet<string> _createPending = new(StringComparer.Ordinal);
    readonly HashSet<string> _createFailed = new(StringComparer.Ordinal);

    /// <summary>The create for <paramref name="uri"/> reached its verdict. Called by <c>PlaylistCreateFlow</c> — the one
    /// create path — so the toast, the notice strip and the announcement can never tell different stories.</summary>
    internal void SettleCreate(string uri, bool ok)
    {
        if (uri.Length == 0) return;
        _createPending.Remove(uri);
        if (ok) _createFailed.Remove(uri); else _createFailed.Add(uri);
    }

    /// <summary>An optimistic create for this uri is still riding the outbox — "the server has never heard of it" is the
    /// EXPECTED state, not a deletion.</summary>
    public bool IsCreatePending(string uri) => _createPending.Contains(uri);

    /// <summary>The create for this uri was rejected: the page is showing a playlist that will never exist.</summary>
    public bool IsCreateFailed(string uri) => _createFailed.Contains(uri);

    // ── rootlist folder CRUD (P3) ──────────────────────────────────────────────────────────────────────────
    /// <summary>Create a folder; returns the client-minted groupId (expansion state and pins key off it, so they survive
    /// every later rename). Online-only by seam contract — an offline call fails fast with <c>Offline</c>.</summary>
    public Task<string> CreateFolderAsync(string name, RootlistPlacement placement, CancellationToken ct = default)
        => _playlistEdits.CreateFolderAsync(name, placement, ct);

    public Task RenameFolderAsync(string groupId, string name, CancellationToken ct = default)
        => _playlistEdits.RenameFolderAsync(groupId, name, ct);

    /// <summary>Delete a folder's marker pair. Its children are NOT deleted — they move up one level.</summary>
    public Task DeleteFolderAsync(string groupId, CancellationToken ct = default)
        => _playlistEdits.DeleteFolderAsync(groupId, ct);

    public string CreatePlaylist(string name)
    {
        var uri = _playlists.CreatePlaylist(name);
        if (!_activity.IsSuppressed) _activity.Record(ActivityKind.PlaylistCreate, uri, name);   // log-only (no Undo)
        return uri;
    }

    public void AddToPlaylist(string playlistUri, Track track)
    {
        _playlists.AddTrack(playlistUri, track);
        if (!_activity.IsSuppressed)
            _activity.Record(ActivityKind.PlaylistAddTracks, playlistUri, null, new ActivityPayload(Tracks: new[] { TrackRef(track) }));
    }

    /// <summary>Add tracks to ANY editable playlist by uri — the "Copy to playlist" picker's target. The mutation seam
    /// routes <c>wavee:playlist:*</c> to the local source and <c>spotify:playlist:*</c> to the real Spotify path; it
    /// fails loud (never silently no-ops) if a real backend isn't wired, which is intended.</summary>
    public Task AddTracksAsync(string playlistUri, IReadOnlyList<Track> tracks, CancellationToken ct = default)
        => AddTracksTrackedAsync(playlistUri, tracks, ct);

    /// <summary>The same add, returning the ACTIVITY ID of the entry it recorded (-1 when recording is suppressed) so the
    /// caller can offer Undo on its OWN confirmation toast — see <c>NotificationCenterBridge.UndoByIdAsync</c>. Undo for a
    /// playlist add has always existed; it was only reachable from the notification panel, which is not where anyone looks
    /// the moment they realise they filed a song into the wrong list.</summary>
    public async Task<long> AddTracksTrackedAsync(string playlistUri, IReadOnlyList<Track> tracks, CancellationToken ct = default)
    {
        long id = _activity.IsSuppressed ? -1 : _activity.Record(ActivityKind.PlaylistAddTracks, playlistUri, null, PayloadFor(tracks));
        TrackEdited(playlistUri);
        await WithFailure(_playlistEdits.AddTracksAsync(playlistUri, tracks, ct), id).ConfigureAwait(false);
        return id;
    }

    /// <summary>Insert an ordered track batch at a visible playlist slot. The same activity/undo path as append is used;
    /// the mutation source retains duplicates and chunks the transport without changing order.</summary>
    public Task InsertTracksAsync(string playlistUri, IReadOnlyList<Track> tracks, int toIndex, CancellationToken ct = default)
    {
        long id = _activity.IsSuppressed ? -1 : _activity.Record(ActivityKind.PlaylistAddTracks, playlistUri, null, PayloadFor(tracks));
        TrackEdited(playlistUri);
        return WithFailure(_playlistEdits.InsertTracksAsync(playlistUri, tracks, toIndex, ct), id);
    }

    /// <summary>Add tracks to the user's default playlist (creating one if none) — the no-picker "Add to playlist".
    /// Returns the target (uri, name) for a confirmation toast.</summary>
    public (string Uri, string Name) AddToDefaultPlaylist(IEnumerable<Track> tracks)
    {
        var target = _playlists.DefaultPlaylist();
        var added = new List<Track>();
        foreach (var t in tracks) { _playlists.AddTrack(target.Uri, t); added.Add(t); }
        if (!_activity.IsSuppressed && added.Count > 0)
            _activity.Record(ActivityKind.PlaylistAddTracks, target.Uri, target.Name, PayloadFor(added));
        return target;
    }

    /// <summary>Is this uri saved / liked / followed? Reads the signal → subscribes the caller (live heart state).</summary>
    public bool IsSaved(string uri)
    {
        // Saved-state affordances subscribe to this URI only. A mutation elsewhere in the library must not re-render
        // every visible heart/card and turn a paint-sized state change into app-wide layout invalidation.
        if (!_savedByUri.TryGetValue(uri, out var state))
        {
            state = new Signal<bool>(Saved.Peek().Contains(uri));
            _savedByUri.Add(uri, state);
        }
        return state.Value;
    }

    /// <summary>Toggle saved-state with an OPTIMISTIC local flip (the heart updates this frame), then reconcile through
    /// the source (which re-emits the confirmed set). Called from a click handler, so the reads here don't subscribe.
    /// <paramref name="name"/> is display-only: it names the item in the notification-center activity entry.</summary>
    public void ToggleSaved(string uri, string? name = null) => SetSaved(uri, !Saved.Peek().Contains(uri), name);

    public void SetSaved(string uri, bool saved, string? name = null)
    {
        var cur = Saved.Peek();
        if (cur.Contains(uri) == saved) return;
        var next = new HashSet<string>(cur);
        if (saved) next.Add(uri); else next.Remove(uri);
        PublishSaved(next);                      // optimistic, URI-selective subscribers update this frame
        AnnounceSaved(saved, name);
        // A pre-save is the trigger for its scheduled "out now" toast, and this is the one chokepoint every heart / menu /
        // drop target goes through. No-op for ordinary uris and when release drops are off.
        ReleaseNotifier.OnSavedChanged(uri, saved);
        // Record BEFORE the async reconcile so the entry exists to flip Failed if the write faults immediately.
        long id = _activity.IsSuppressed ? -1 : _activity.Record(saved ? ActivityKind.Save : ActivityKind.Unsave, uri, name);
        var task = _mut.SetSavedAsync(uri, saved);   // reconcile (re-emits the confirmed set via the bridge subscription)
        if (id >= 0) _ = task.ContinueWith(t => { if (t.IsFaulted) _activity.MarkFailed(id); }, TaskScheduler.Default);
    }

    // Save/unsave is a silent visual flip (a heart fills), so a screen-reader user gets no confirmation that the thing they
    // pressed actually happened. Announced HERE rather than per affordance: every heart, menu item, row action and drop
    // target routes through SetSaved, so one call covers them all and none can drift. Composed on the event, never per
    // frame; throttled so holding a key or a fast unsave-resave burst does not flood the reader.
    static void AnnounceSaved(bool saved, string? name)
    {
        if (!Announcer.IsAvailable) return;
        string what = saved ? "Saved" : "Removed from library";
        Announcer.SayThrottled(string.IsNullOrEmpty(name) ? what : what + ": " + name);
    }

    void PublishSaved(IReadOnlySet<string> next)
    {
        // Keep the aggregate snapshot for imperative reads/backwards compatibility. Equality suppression on each bool
        // means a full-set confirmation only wakes the affordance whose URI genuinely changed.
        Saved.Value = next;
        foreach (var (uri, state) in _savedByUri)
        {
            bool saved = next.Contains(uri);
            state.Value = saved;
        }
    }

    void IUndoTarget.SetSaved(string uri, bool saved) => SetSaved(uri, saved);

    // ── Spotify playlist editing ─────────────────────────────────────────────────────────────────────────
    /// <summary><paramref name="previousName"/> lets the rename be recorded (and undone) when the name actually changes;
    /// callers editing description/collaborative pass name=null so nothing is logged.</summary>
    public Task UpdatePlaylistDetailsAsync(string playlistUri, string? name, string? description, bool? collaborative,
        string? previousName = null, CancellationToken ct = default)
    {
        long id = -1;
        if (!_activity.IsSuppressed && name is { } newName && previousName is { } prev && !string.Equals(prev, newName, StringComparison.Ordinal))
            id = _activity.Record(ActivityKind.PlaylistRename, playlistUri, newName, new ActivityPayload(OldName: prev, NewName: newName));
        TrackEdited(playlistUri);
        return AnnounceEditAsync(_playlistEdits.UpdateDetailsAsync(playlistUri, name, description, collaborative, ct), id,
            PlaylistEditVerb.Rename, count: 1);
    }

    public Task SetPlaylistCoverJpegAsync(string playlistUri, byte[] jpeg, CancellationToken ct = default)
    {
        long id = _activity.IsSuppressed ? -1 : _activity.Record(ActivityKind.PlaylistCoverSet, playlistUri);   // log-only
        return WithFailure(_playlistEdits.SetCoverJpegAsync(playlistUri, jpeg, ct), id);
    }

    /// <summary><paramref name="removedTracks"/> (optional) captures the removed rows' uri/name so the remove can be undone
    /// by re-adding them; a null list still records the remove (log-only, undo fails cleanly).</summary>
    public Task RemovePlaylistRowsAsync(string playlistUri, IReadOnlyList<PlaylistRowRef> rows,
        IReadOnlyList<ActivityTrackRef>? removedTracks = null, CancellationToken ct = default)
        => RemovePlaylistRowsTrackedAsync(playlistUri, rows, removedTracks, ct);

    /// <summary>Remove membership rows, returning the ACTIVITY ID of the entry it recorded (-1 when recording is
    /// suppressed) so the caller can offer Undo on its OWN confirmation toast — the <see cref="AddTracksTrackedAsync"/>
    /// shape. Remove is the edit most often made by accident (a mis-aimed context menu on a 10 000-row playlist), and
    /// until now it was fire-and-forget: a failure reported nothing and a success offered no way back.</summary>
    public async Task<long> RemovePlaylistRowsTrackedAsync(string playlistUri, IReadOnlyList<PlaylistRowRef> rows,
        IReadOnlyList<ActivityTrackRef>? removedTracks = null, CancellationToken ct = default)
    {
        long id = _activity.IsSuppressed ? -1 : _activity.Record(ActivityKind.PlaylistRemoveTracks, playlistUri, null, new ActivityPayload(Tracks: removedTracks));
        TrackEdited(playlistUri);
        await AnnounceEditAsync(_playlistEdits.RemoveRowsAsync(playlistUri, rows, ct), id,
            PlaylistEditVerb.Remove, rows.Count).ConfigureAwait(false);
        return id;
    }

    public Task MovePlaylistRowsAsync(string playlistUri, IReadOnlyList<PlaylistRowRef> rows, int toIndex, CancellationToken ct = default)
        => MovePlaylistRowsTrackedAsync(playlistUri, rows, toIndex, ct);

    /// <summary>Reorder membership rows, returning the activity id (the remove/add shape). Every reorder call site
    /// AWAITS this now: a rejected reorder rolls the store back, and a caller that never looked at the task showed the
    /// rows snapping home with no explanation at all.</summary>
    public async Task<long> MovePlaylistRowsTrackedAsync(string playlistUri, IReadOnlyList<PlaylistRowRef> rows, int toIndex,
                                                        CancellationToken ct = default)
    {
        long id = -1;
        if (!_activity.IsSuppressed && rows.Count > 0)
        {
            var refs = new ActivityTrackRef[rows.Count];
            for (int i = 0; i < rows.Count; i++) refs[i] = new ActivityTrackRef(rows[i].Uri, null, rows[i].ItemId);
            id = _activity.Record(ActivityKind.PlaylistMoveTracks, playlistUri, null,
                new ActivityPayload(Tracks: refs, FromIndex: rows[0].Index, ToIndex: toIndex));
        }
        TrackEdited(playlistUri);
        await AnnounceEditAsync(_playlistEdits.MoveRowsAsync(playlistUri, rows, toIndex, ct), id,
            PlaylistEditVerb.Reorder, rows.Count).ConfigureAwait(false);
        return id;
    }

    /// <summary>Move one playlist or balanced folder subtree in Spotify's rootlist. The source serializes this revisioned
    /// write so rapid drag/drop operations cannot race the same base revision.</summary>
    public Task MoveRootlistItemAsync(RootlistItemRef source, RootlistItemRef target,
                                      RootlistDropPlacement placement, CancellationToken ct = default)
        => _playlistEdits.MoveRootlistItemAsync(source, target, placement, ct);

    /// <summary>Move a whole ordered batch (a multi-selection drop) as ONE revisioned write — one Delta, one optimistic
    /// apply, one rollback. The submission ORDER is the caller's (see <c>RootlistBatchOrder</c>); the seam executes it.</summary>
    public Task MoveRootlistItemsAsync(IReadOnlyList<RootlistMove> moves, CancellationToken ct = default)
        => _playlistEdits.MoveRootlistItemsAsync(moves, ct);

    public Task<string> CreateContributorInviteAsync(string playlistUri, CancellationToken ct = default)
    {
        long id = _activity.IsSuppressed ? -1 : _activity.Record(ActivityKind.ContributorInvite, playlistUri);   // log-only
        return WithFailure(_playlistEdits.CreateContributorInviteAsync(playlistUri, ct), id);
    }

    // NO permission GET here. The base permission is read exactly once per playlist, by the SYNC loop when the page
    // opens (and re-read by nothing: a dealer permission push flips the same header in place), so the store header is
    // the one permission state every surface reads. The bridge passthrough this replaced existed only for the detail
    // page's own owner-only GET, which P1 deleted — keeping it would be a second, racing way to learn the same fact.

    public Task SetPlaylistVisibilityAsync(string playlistUri, bool isPublic, CancellationToken ct = default)
    {
        long id = _activity.IsSuppressed ? -1 : _activity.Record(ActivityKind.PlaylistVisibility, playlistUri, null, new ActivityPayload(NewIsPublic: isPublic));
        return WithFailure(_playlistEdits.SetPlaylistVisibilityAsync(playlistUri, isPublic, ct), id);
    }

    public Task DeletePlaylistAsync(string playlistUri, CancellationToken ct = default)
    {
        long id = _activity.IsSuppressed ? -1 : _activity.Record(ActivityKind.PlaylistDelete, playlistUri);   // log-only, destructive
        return WithFailure(_playlistEdits.DeletePlaylistAsync(playlistUri, ct), id);
    }

    // ── the announced-edit chokepoint ──────────────────────────────────────────────────────────────────────
    // Reorder / remove / rename are SILENT visual changes: rows slide, a title swaps. A screen-reader user gets no
    // confirmation that the thing they pressed happened, and — worse — no notice when it was refused and rolled back.
    // Announced HERE for the same reason AnnounceSaved is: every menu, drag, keyboard command and picker routes through
    // these methods, so one call covers them all and none can drift. The FAILURE announcement is assertive (it
    // interrupts: the edit did not survive and the list the user is reading just changed back) and reuses the very copy
    // the toast shows, so the two channels can never tell different stories.
    async Task AnnounceEditAsync(Task work, long id, PlaylistEditVerb verb, int count)
    {
        try { await WithFailure(work, id).ConfigureAwait(false); }
        catch (Exception ex)
        {
            var kind = PlaylistEditErrorKinds.KindOf(ex);
            _post(() => Announcer.Say(Loc.Get(PlaylistEditErrorKinds.KeyFor(kind, verb)), assertive: true));
            throw;
        }
        if (count > 0) _post(() => Announcer.Say(EditDone(verb, count)));
    }

    static string EditDone(PlaylistEditVerb verb, int count) => verb switch
    {
        PlaylistEditVerb.Remove => Strings.Detail.Edit.RemovedFromPlaylist(count),
        PlaylistEditVerb.Reorder => Strings.Detail.Edit.MovedRows(count),
        _ => Loc.Get(Strings.Detail.Edit.Saved),
    };

    // ── activity plumbing ──────────────────────────────────────────────────────────────────────────────────
    // Optimistic Done; an IMMEDIATE Task fault flips the entry to Failed. Eventual outbox dead-letter failures are NOT
    // reflected (documented cut). The continuation runs off the UI thread — ActivityLog is thread-safe.
    Task WithFailure(Task task, long id)
    {
        if (id >= 0) _ = task.ContinueWith(t => { if (t.IsFaulted) _activity.MarkFailed(id); }, TaskScheduler.Default);
        return task;
    }

    Task<T> WithFailure<T>(Task<T> task, long id)
    {
        if (id >= 0) _ = task.ContinueWith(t => { if (t.IsFaulted) _activity.MarkFailed(id); }, TaskScheduler.Default);
        return task;
    }

    static ActivityTrackRef TrackRef(Track t) => new(t.Uri, t.Title, t.ContextUid);

    static ActivityPayload PayloadFor(IReadOnlyList<Track> tracks)
    {
        var refs = new ActivityTrackRef[tracks.Count];
        for (int i = 0; i < tracks.Count; i++) refs[i] = TrackRef(tracks[i]);
        return new ActivityPayload(Tracks: refs);
    }
}
