using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Backend.Playlists;

/// <summary>Real Spotify playlist editing backed by <see cref="MutationEngine.Edit"/> and direct HTTP for cover/permission.</summary>
public sealed class PlaylistMutationSource : IPlaylistMutationSource
{
    /// <summary>Spotify's public mutation contract caps one item batch at 100. The internal playlist4 wire accepts the
    /// same ordered operation shape; retaining that cap keeps one drag/drop behavior across transports.</summary>
    public const int MaxItemBatch = 100;
    readonly MutationEngine _mut;
    readonly ITransport _transport;
    readonly IStore? _store;
    readonly PlaylistPermissionClient _permissions;
    IHttpExchange _http;
    readonly Func<SessionContext> _ctx;
    readonly Func<string> _spclientBaseUrl;
    readonly UserPlaylistSource _local;
    // I2 — the ONE rootlist write lane. Shared with the outbox's RootlistFollowStrategy at the composition root, so a
    // drained follow/unfollow can never interleave with a direct move/delete/visibility/create op and rebase a
    // positional MOV against marker indices that moved underneath it.
    readonly RootlistLane _rootlistLane;

    /// <summary>Set at go-live (§6): routes post-write drains through LibrarySync (same as <see cref="EngineMutationSource.ScheduleDrain"/>).</summary>
    public Func<CancellationToken, Task>? ScheduleDrain { get; set; }

    public PlaylistMutationSource(
        MutationEngine mut, ITransport transport, IHttpExchange http, Func<SessionContext> ctx,
        Func<string> spclientBaseUrl, UserPlaylistSource local, RootlistLane rootlistLane, IStore? store = null)
        => (_mut, _transport, _http, _ctx, _spclientBaseUrl, _local, _rootlistLane, _store, _permissions) =
            (mut, transport, http, ctx, spclientBaseUrl, local, rootlistLane, store, new PlaylistPermissionClient(transport));

    public void SetHttp(IHttpExchange http) => _http = http;

    /// <summary>The ONE create path (P3). Everything the UI needs is true the instant this returns: the header, an empty
    /// membership, the rootlist entry at <paramref name="placement"/> and the saved pill are all in the store, so the
    /// caller can navigate straight to a real (empty, owned, editable) playlist page.
    /// <para>The network is three ORDERED durable-outbox ops on one entity key — create → rootlist ADD → any seed-track
    /// edits the caller enqueues next — which is what closes the orphan-playlist hole (the rootlist row is durable, not
    /// a fire-and-forget follow-up) and what makes an offline create work with no extra code.</para></summary>
    public PlaylistCreated CreatePlaylist(string name, RootlistPlacement placement)
    {
        RequireStore();
        var store = _store!;
        var ctx = _ctx();
        string trimmed = string.IsNullOrWhiteSpace(name) ? "New Playlist" : name.Trim();
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // The id is OURS: the create posts /changes to it, so the uri is knowable before the first byte leaves.
        string id = SpotifyIds.NewPlaylistId();
        string uri = "spotify:playlist:" + id;

        using (store.BeginBulk())
        {
            store.UpsertPlaylist(new Playlist(
                id, uri, trimmed, null, ctx.Account, null, 0, System.Array.Empty<Track>(),
                Owner: new Owner(ctx.Account, ctx.Account, null),
                // We are the owner of a brand-new list: every edit affordance is live, and a fresh playlist is public
                // until the permission GET says otherwise (which is also what desktop assumes).
                Capabilities: new PlaylistCapabilities(CanView: true, CanEditItems: true, CanEditMetadata: true,
                                                       IsCollaborative: false, IsOwner: true, CanAdministratePermissions: true),
                IsPublic: true));
            store.SetMembership(uri, System.Array.Empty<PlaylistMember>(), null);
            int at = RootlistOps.PlacementIndex(store.Rootlist(), placement);
            if (at < 0) at = 0;                       // the folder vanished between the menu and the click → top level
            store.SetRootlist(RootlistOps.ApplyLocally(store.Rootlist(),
                new[] { new PlaylistOp(PlaylistOpKind.Add, FromIndex: at, Items: new[] { new PlaylistMember("", uri, null, nowMs) }) }));
            store.SetSaved("playlists", uri, true, SyncState.Pending, nowMs);
            store.Bump("rootlist", CollectionKind.Playlists);
        }

        var (_, completion) = _mut.Create(uri, trimmed);
        // The rootlist ADD is queued as its own durable op behind the create. Its optimistic arm is a no-op here (the
        // entry is already in the list above); its replay resolves the folder to an index against the CURRENT rootlist.
        _mut.Follow(uri, true, placement.ParentFolderId);
        _ = PumpAsync(uri);
        return new PlaylistCreated(uri, completion);
    }

    // Kick the drain without making the seam async. Failures are not swallowed: an op-level failure is already reported
    // through the create completion / the pending state, and a drain-level fault is logged here.
    async Task PumpAsync(string uri)
    {
        try
        {
            if (ScheduleDrain is { } viaLoop) await viaLoop(CancellationToken.None).ConfigureAwait(false);
            else await _mut.Drain(_transport, _ctx(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* shutdown — the op stays durable and replays next login */ }
        catch (Exception ex) { PlaylistMutationDiagnostics.CreateFailed(uri, "drain-faulted:" + ex.GetType().Name); }
    }

    public Task AddTracksAsync(string playlistUri, IReadOnlyList<Track> tracks, CancellationToken ct = default)
    {
        if (IsLocal(playlistUri)) { foreach (var t in tracks) _local.AddTrack(playlistUri, t); return Task.CompletedTask; }
        return InsertTracksCoreAsync(playlistUri, tracks, toIndex: null, ct);
    }

    public Task InsertTracksAsync(string playlistUri, IReadOnlyList<Track> tracks, int toIndex, CancellationToken ct = default)
    {
        if (IsLocal(playlistUri)) { _local.InsertTracks(playlistUri, tracks, toIndex); return Task.CompletedTask; }
        if (toIndex < 0) throw new ArgumentOutOfRangeException(nameof(toIndex));
        return InsertTracksCoreAsync(playlistUri, tracks, toIndex, ct);
    }

    Task InsertTracksCoreAsync(string playlistUri, IReadOnlyList<Track> tracks, int? toIndex, CancellationToken ct)
    {
        if (tracks.Count == 0) return Task.CompletedTask;
        RequireStore();
        // Membership is intentionally thin (URI + row facts). Recommended/search results are not guaranteed to have
        // passed through metadata hydration, so persist the supplied entities before the optimistic membership edit;
        // otherwise JoinMembership drops the new row and the add appears to work only for previously-cached tracks.
        for (int i = 0; i < tracks.Count; i++) _store!.UpsertTrack(tracks[i]);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string account = _ctx().Account;
        var membership = _store!.Membership(playlistUri);
        var ops = new List<PlaylistOp>((tracks.Count + MaxItemBatch - 1) / MaxItemBatch);
        string? previousBatchLastId = null;
        for (int offset = 0; offset < tracks.Count; offset += MaxItemBatch)
        {
            int count = Math.Min(MaxItemBatch, tracks.Count - offset);
            var members = new PlaylistMember[count];
            // MINT the row ids here (A 046 shape). The server keeps them verbatim, so the optimistic row is keyed the
            // moment it appears: a later remove/reorder can name it without a round-trip, and our own ADD echo is
            // idempotent by id (I6).
            for (int i = 0; i < count; i++)
                members[i] = new PlaylistMember(SpotifyIds.NewItemId(), tracks[offset + i].Uri, account, now);
            int at = toIndex is { } t ? t + offset : 0;
            ops.Add(new PlaylistOp(PlaylistOpKind.Add,
                FromIndex: at,
                AddLast: toIndex is null,
                Items: members,
                // I5 — an index ADD is base-bound. Record the row it was built to sit after so a replay against a
                // moved base recomputes from_index instead of inserting at a stale position.
                Anchor: toIndex is null ? null : InsertAnchor(membership, at, previousBatchLastId)));
            previousBatchLastId = members[count - 1].ItemId;
        }
        long edit = EnqueueEdit(playlistUri, ops);
        return DrainAsync(edit, ct);
    }

    /// <summary>I5 — the predecessor an index ADD was built against: the previous batch's last minted row when this is
    /// a continuation, else the membership row before the insertion point. <see cref="PlaylistMoveAnchorKind.First"/>
    /// means "at the head" (no predecessor); <c>null</c> means the predecessor carries no id and cannot be re-found.</summary>
    static PlaylistMoveAnchor? InsertAnchor(IReadOnlyList<PlaylistMember> membership, int at, string? previousBatchLastId)
    {
        if (previousBatchLastId is { Length: > 0 }) return new PlaylistMoveAnchor(PlaylistMoveAnchorKind.AfterItem, previousBatchLastId);
        if (at <= 0) return new PlaylistMoveAnchor(PlaylistMoveAnchorKind.First);
        if (at - 1 >= membership.Count) return null;
        var predecessor = membership[at - 1];
        return string.IsNullOrEmpty(predecessor.ItemId) ? null
            : new PlaylistMoveAnchor(PlaylistMoveAnchorKind.AfterItem, predecessor.ItemId);
    }

    public Task RemoveRowsAsync(string playlistUri, IReadOnlyList<PlaylistRowRef> rows, CancellationToken ct = default)
    {
        if (IsLocal(playlistUri)) throw new PlaylistMutationException(PlaylistMutationFailure.NotSupported, $"Local playlist row removal is not implemented (uri={playlistUri}).");
        if (rows.Count == 0) return Task.CompletedTask;
        long edit = EnqueueEdit(playlistUri, BuildRemoveOps(rows));
        return DrainAsync(edit, ct);
    }

    /// <summary>One Delta, one keyed REM per row (the A 143 shape): every row is named by <c>(uri, item_id)</c> and
    /// carries no index at all, so the server resolves each against the base revision and concurrent position drift
    /// cannot delete the wrong track — duplicates of one uri included.
    /// <para>The descending-index fallback exists for exactly one case: a row whose id we never learned (a membership
    /// cached before ids were minted). It is all-or-nothing per batch, because mixing keyed and index REMs in ONE Delta
    /// would let the keyed removals shift the indices the positional ones were computed against.</para></summary>
    public static IReadOnlyList<PlaylistOp> BuildRemoveOps(IReadOnlyList<PlaylistRowRef> rows)
    {
        bool keyed = true;
        for (int i = 0; i < rows.Count; i++) if (string.IsNullOrEmpty(rows[i].ItemId)) { keyed = false; break; }

        var ops = new List<PlaylistOp>(rows.Count);
        if (keyed)
        {
            for (int i = 0; i < rows.Count; i++)
                ops.Add(new PlaylistOp(PlaylistOpKind.Remove, ItemsAsKey: true,
                    Items: new[] { new PlaylistMember(rows[i].ItemId, rows[i].Uri, null, 0) }));
            return ops;
        }

        var descending = new List<PlaylistRowRef>(rows);
        descending.Sort(static (a, b) => b.Index.CompareTo(a.Index));   // highest index first — earlier removals never shift later ones
        for (int i = 0; i < descending.Count; i++)
            ops.Add(new PlaylistOp(PlaylistOpKind.Remove, FromIndex: descending[i].Index, Length: 1,
                Items: new[] { new PlaylistMember(descending[i].ItemId, descending[i].Uri, null, 0) }));
        return ops;
    }

    public Task MoveRowsAsync(string playlistUri, IReadOnlyList<PlaylistRowRef> rows, int toIndex, CancellationToken ct = default)
    {
        if (IsLocal(playlistUri)) throw new PlaylistMutationException(PlaylistMutationFailure.NotSupported, $"Local playlist row reordering is not implemented (uri={playlistUri}).");
        if (rows.Count == 0) return Task.CompletedTask;
        RequireStore();
        var op = BuildKeyedMove(_store!.Membership(playlistUri), rows, toIndex);
        if (op is null) return Task.CompletedTask;               // the rows already sit where the drop asked for them
        long edit = EnqueueEdit(playlistUri, op);
        return DrainAsync(edit, ct);
    }

    /// <summary>The ONE reorder shape: a single item-keyed MOV (the A 148 shape) carrying every selected row plus ONE
    /// anchor — gapped multi-select included, because the anchor is a row identity, not a range.
    /// <para><paramref name="toIndex"/> is the PRE-MOVE insertion index into <paramref name="membership"/> ("insert
    /// before the row currently at this index"). It becomes an anchor here: 0 or less → <c>add_first</c>; at/past the
    /// end → <c>add_last</c>; otherwise the row at <c>toIndex-1</c>, walking backwards over rows that are themselves
    /// being moved (that is what makes a gapped selection land as one contiguous run) — nothing left → <c>add_first</c>.</para>
    /// <para>Returns null when the move is a no-op. Throws <see cref="PlaylistMutationFailure.Pending"/> when any moved
    /// row or the anchor has no <c>item_id</c> yet: there is no positional fallback, because sending indices while our
    /// own add is still in flight is exactly how rows land in the wrong place.</para></summary>
    public static PlaylistOp? BuildKeyedMove(IReadOnlyList<PlaylistMember> membership, IReadOnlyList<PlaylistRowRef> rows, int toIndex)
    {
        var ordered = new List<PlaylistRowRef>(rows);
        ordered.Sort(static (a, b) => a.Index.CompareTo(b.Index));   // list order IS the order they land in
        var movedIds = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < ordered.Count; i++)
        {
            if (string.IsNullOrEmpty(ordered[i].ItemId)) throw StillSyncing();
            movedIds.Add(ordered[i].ItemId);
        }

        PlaylistMoveAnchor anchor;
        string? anchorUri = null;
        if (toIndex <= 0) anchor = new PlaylistMoveAnchor(PlaylistMoveAnchorKind.First);
        else if (toIndex >= membership.Count) anchor = new PlaylistMoveAnchor(PlaylistMoveAnchorKind.Last);
        else
        {
            int j = toIndex - 1;
            while (j >= 0 && movedIds.Contains(membership[j].ItemId)) j--;
            if (j < 0) anchor = new PlaylistMoveAnchor(PlaylistMoveAnchorKind.First);
            else
            {
                if (string.IsNullOrEmpty(membership[j].ItemId)) throw StillSyncing();
                anchor = new PlaylistMoveAnchor(PlaylistMoveAnchorKind.AfterItem, membership[j].ItemId);
                anchorUri = membership[j].ItemUri;
            }
        }

        var items = new PlaylistMember[ordered.Count];
        for (int i = 0; i < ordered.Count; i++) items[i] = new PlaylistMember(ordered[i].ItemId, ordered[i].Uri, null, 0);
        var op = new PlaylistOp(PlaylistOpKind.Move, ItemsAsKey: true, Items: items, Anchor: anchor, AnchorUri: anchorUri);
        return ChangesNothing(membership, op) ? null : op;
    }

    static PlaylistMutationException StillSyncing() => new(PlaylistMutationFailure.Pending,
        "Those rows are still syncing — try again in a moment.");

    // Dropping a selection back where it already sits must not spend a write. Simulated against the same applier the
    // optimistic path uses, so "nothing moved" means exactly what the server would compute.
    static bool ChangesNothing(IReadOnlyList<PlaylistMember> membership, PlaylistOp op)
    {
        if (membership.Count == 0) return false;
        var sim = new List<PlaylistMember>(membership);
        try { PlaylistDiffApplier.Apply(sim, new[] { op }); }
        catch (ArgumentOutOfRangeException) { return false; }   // the rows are not where we think — let the server decide
        if (sim.Count != membership.Count) return false;
        for (int i = 0; i < sim.Count; i++)
            if (!string.Equals(sim[i].ItemId, membership[i].ItemId, StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>Move a batch of rootlist rows (or whole folder subtrees) — LOCAL-FIRST, then the wire, as ONE Delta.
    ///
    /// <para>The rootlist <c>/changes</c> reply carries revision bookkeeping and NO contents, and the dealer echo that
    /// follows is a head-only push at the revision we just stored — so it is correctly echo-dropped and never triggers
    /// a GET. That is the whole bug this shape fixes: a move that only POSTed produced a perfectly successful await, a
    /// truthful-looking "Moved to …" toast, and a sidebar that did not move until some unrelated rootlist change
    /// happened to force a refetch. The new tree can only come from applying the ops ourselves.</para>
    ///
    /// <para>So: apply optimistically BEFORE the POST (1-arg <c>SetRootlist</c> — rows now, the revision we still trust
    /// preserved), and on the ack re-stamp those rows with the revision the reply advanced to. Anything that means the
    /// write did not land — a transport fault, a rejection, a refusal discovered on a rebuilt attempt — puts the
    /// pre-move rows back. That is <see cref="RunRootlistOpAsync"/>, the same three moves folder CRUD makes; the only
    /// thing this adds is WHAT to build.</para>
    ///
    /// <para>N items are ONE delta, ONE optimistic apply, ONE POST and ONE rollback: the sequential build lives INSIDE
    /// the builder delegate (<see cref="RootlistOps.TryBuildMoves"/>), so the 409 rebase attempt re-derives every op
    /// against the refreshed marker stream rather than replaying indices the server has already invalidated. A refused
    /// build is a THROW, never a quiet return (F3): the caller must not toast "Moved to …" for a batch never sent.</para></summary>
    public async Task MoveRootlistItemsAsync(IReadOnlyList<RootlistMove> moves, CancellationToken ct = default)
    {
        RequireStore();
        // One log key for the whole batch: the first source, and how many rode with it.
        string logKey = moves.Count == 0 ? "rootlist"
                      : moves.Count == 1 ? moves[0].Source.Key
                      : $"{moves[0].Source.Key} +{moves.Count - 1}";
        long startedAt = Environment.TickCount64;
        await RunRootlistOpAsync(logKey,
            entries => RootlistOps.TryBuildMoves(entries, moves, out var ops, out var reason)
                ? ops
                : throw MoveRefused(reason), ct).ConfigureAwait(false);
        PlaylistMutationDiagnostics.RootlistMoveApplied(logKey, moves.Count == 0 ? "" : moves[0].Placement.ToString(),
                                                        Environment.TickCount64 - startedAt);
    }

    /// <summary>The N=1 sugar. There is no second write path: one move is a batch of one.</summary>
    public Task MoveRootlistItemAsync(RootlistItemRef source, RootlistItemRef target,
                                      RootlistDropPlacement placement, CancellationToken ct = default)
        => MoveRootlistItemsAsync([new RootlistMove(source, target, placement)], ct);

    /// <summary>Put <paramref name="before"/> back — but ONLY if the store still holds the exact rows we invented. A 409
    /// bootstrap (or a dealer push) may already have replaced them with server truth, and restoring a pre-move snapshot
    /// over that would resurrect rows the server no longer has. Returns the still-outstanding optimistic rows (null once
    /// there is nothing left to undo) so a caller can keep tracking it with one assignment.</summary>
    static IReadOnlyList<RootlistEntry>? RollbackRootlist(IStore store, IReadOnlyList<RootlistEntry> before,
                                                          IReadOnlyList<RootlistEntry>? optimistic, string reason)
    {
        if (optimistic is null) return null;
        if (!ReferenceEquals(store.Rootlist(), optimistic)) return null;   // server truth already landed on top of ours
        store.SetRootlist(before);
        store.Bump("rootlist", CollectionKind.Playlists);
        PlaylistMutationDiagnostics.RootlistMoveRolledBack(reason);
        return null;
    }

    static string ReasonOf(Exception ex) => ex switch
    {
        PlaylistMutationException pme => pme.Kind.ToString(),
        OperationCanceledException => "canceled",
        _ => ex.GetType().Name,
    };

    /// <summary>The typed refusal for a rootlist move whose op could not be built. One kind per reason, because each is
    /// a different sentence upstream (<c>PlaylistEditErrorKinds</c>): "Already there" for a destination the item
    /// already occupies, "Can't move here" for a placement this stream cannot express, and the ordinary conflict copy
    /// for a source/target that is no longer in the rootlist at all (the tree moved under the gesture).</summary>
    static PlaylistMutationException MoveRefused(RootlistMoveCheck reason) => reason switch
    {
        RootlistMoveCheck.NoOp or RootlistMoveCheck.SameItem =>
            new PlaylistMutationException(PlaylistMutationFailure.NoOp, "That is already where it is."),
        RootlistMoveCheck.Cycle or RootlistMoveCheck.Invalid =>
            new PlaylistMutationException(PlaylistMutationFailure.Invalid, "That move cannot be expressed in the rootlist."),
        _ => new PlaylistMutationException(PlaylistMutationFailure.Conflict,
                                           "Your library changed while that was saving — try again."),
    };

    public Task UpdateDetailsAsync(string playlistUri, string? name, string? description, bool? collaborative, CancellationToken ct = default)
    {
        if (IsLocal(playlistUri)) throw new PlaylistMutationException(PlaylistMutationFailure.NotSupported, $"Local playlist metadata editing is not implemented (uri={playlistUri}).");
        var patch = new PlaylistListAttributePatch(Name: name, Description: description, Collaborative: collaborative);
        long edit = EnqueueEdit(playlistUri, new PlaylistOp(PlaylistOpKind.UpdateList, ListPatch: patch));
        return DrainAsync(edit, ct);
    }

    public async Task SetCoverJpegAsync(string playlistUri, byte[] jpeg, CancellationToken ct = default)
    {
        if (IsLocal(playlistUri)) throw new PlaylistMutationException(PlaylistMutationFailure.NotSupported, $"Local playlist covers are not implemented (uri={playlistUri}).");
        var id = EntityUri.IdOf(playlistUri);
        var uploadUrl = "https://image-upload.spotify.com/v4/playlist";
        var uploadHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "image/jpeg",
            ["Accept"] = "application/json",
        };
        using var uploadResp = await _http.SendAsync(new HttpReq("POST", uploadUrl, uploadHeaders, jpeg), ct).ConfigureAwait(false);
        if (uploadResp.Status is < 200 or >= 300) throw new InvalidOperationException($"cover upload failed ({uploadResp.Status})");
        using var uploadMs = new System.IO.MemoryStream();
        await uploadResp.Body.CopyToAsync(uploadMs, ct).ConfigureAwait(false);
        var uploadJson = JsonDocument.Parse(uploadMs.ToArray());
        var uploadToken = uploadJson.RootElement.GetProperty("uploadToken").GetString()
            ?? throw new InvalidOperationException("cover upload missing uploadToken");

        var registerBody = Encoding.UTF8.GetBytes($"{{\"uploadToken\":\"{uploadToken}\"}}");
        var registerHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/json",
            ["Accept"] = "application/json",
        };
        var reg = await _transport.Request(Channel.SpclientWg, $"/playlist/v2/playlist/{id}/register-image", registerBody, ct, "POST", registerHeaders).ConfigureAwait(false);
        if (!reg.Ok) throw new InvalidOperationException($"register-image failed ({reg.Status})");
        var regJson = JsonDocument.Parse(reg.Body);
        var pictureB64 = regJson.RootElement.GetProperty("picture").GetString() ?? "";
        var pictureBytes = Convert.FromBase64String(pictureB64);
        long edit = EnqueueEdit(playlistUri, new PlaylistOp(PlaylistOpKind.UpdateList, ListPatch: new PlaylistListAttributePatch(PictureBytes: pictureBytes)));
        await DrainAsync(edit, ct).ConfigureAwait(false);
    }

    public async Task SetPlaylistVisibilityAsync(string playlistUri, bool isPublic, CancellationToken ct = default)
    {
        if (IsLocal(playlistUri)) throw new PlaylistMutationException(PlaylistMutationFailure.NotSupported, $"Local playlists have no visibility (uri={playlistUri}).");
        RequireStore();
        var level = isPublic ? PlaylistPermissionLevel.Viewer : PlaylistPermissionLevel.Blocked;
        var resulting = await _permissions.SetBasePermissionAsync(playlistUri, level, ct).ConfigureAwait(false);
        PatchPlaylistVisibility(playlistUri, isPublic, resulting.Revision);
        try { await TryRootlistPublicFlagAsync(playlistUri, isPublic, ct).ConfigureAwait(false); }
        catch { /* best-effort — permission already committed */ }
    }

    public async Task DeletePlaylistAsync(string playlistUri, CancellationToken ct = default)
    {
        if (IsLocal(playlistUri)) throw new PlaylistMutationException(PlaylistMutationFailure.NotSupported, $"Local playlist delete is not implemented (uri={playlistUri}).");
        RequireStore();
        var store = _store!;
        var ctx = _ctx();
        // I2 — the index this REM carries is only valid against the marker stream nobody else is rewriting.
        await _rootlistLane.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int index = RootlistOps.FindPlaylistIndex(store.Rootlist(), playlistUri);
            if (index < 0)
            {
                await RootlistOps.BootstrapRootlistAsync(store, _transport, ctx, ct).ConfigureAwait(false);
                index = RootlistOps.FindPlaylistIndex(store.Rootlist(), playlistUri);
            }
            if (index < 0) throw new InvalidOperationException($"Playlist '{playlistUri}' is not in the user's rootlist.");
            var rem = new PlaylistOp(PlaylistOpKind.Remove, FromIndex: index, Length: 1);
            // Local-first, like every other rootlist write: the reply has no contents, so the row can only leave the
            // tree because we removed it. A failed POST puts it straight back.
            var before = store.Rootlist();
            IReadOnlyList<RootlistEntry>? optimistic = RootlistOps.ApplyLocally(before, new[] { rem });
            store.SetRootlist(optimistic);
            store.Bump("rootlist", CollectionKind.Playlists);
            try
            {
                await RootlistOps.PostRootlistOpsAsync(store, _transport, _spclientBaseUrl, ctx, new[] { rem }, ct, playlistUri)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RollbackRootlist(store, before, optimistic, ReasonOf(ex));
                PlaylistMutationDiagnostics.DeleteFailed(playlistUri, ex);
                throw;
            }
            store.SetRootlist(optimistic, store.RootlistRevision());   // the reply advanced the revision; the ROWS are ours
        }
        finally { _rootlistLane.Release(); }
        store.SetSaved("playlists", playlistUri, false, SyncState.Confirmed);
        store.Bump("rootlist", CollectionKind.Playlists);
    }

    public async Task<string> CreateContributorInviteAsync(string playlistUri, CancellationToken ct = default)
    {
        if (IsLocal(playlistUri)) throw new PlaylistMutationException(PlaylistMutationFailure.NotSupported, $"Local playlists have no invites (uri={playlistUri}).");
        await EnsureCollaborativeForInviteAsync(playlistUri, ct).ConfigureAwait(false);
        var id = EntityUri.IdOf(playlistUri);
        // permission-grant wants permissionLevel NESTED under "permission" (not flat) — a flat body is rejected 400 (empty).
        var json = Encoding.UTF8.GetBytes("{\"permission\":{\"permissionLevel\":\"CONTRIBUTOR\"},\"ttlMs\":604800000}");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/json",
            ["Accept"] = "application/json",
        };
        // The grant endpoint answers on the WG host (Channel.SpclientWg), not the plain spclient route the base
        // permission uses — the same split desktop makes.
        var r = await _transport.Request(Channel.SpclientWg, $"/playlist-permission/v1/playlist/{id}/permission-grant", json, ct, "POST", headers).ConfigureAwait(false);
        if (!r.Ok)
        {
            PlaylistMutationDiagnostics.PermissionGrantFailed(playlistUri, r.Status);
            throw new InvalidOperationException($"permission grant failed ({r.Status})");
        }
        var doc = JsonDocument.Parse(r.Body);
        return doc.RootElement.GetProperty("token").GetString() ?? "";
    }

    async Task EnsureCollaborativeForInviteAsync(string playlistUri, CancellationToken ct)
    {
        bool needsCollab = _store?.GetPlaylist(playlistUri) is not { Capabilities.IsCollaborative: true };
        if (!needsCollab) return;
        await UpdateDetailsAsync(playlistUri, null, null, true, ct).ConfigureAwait(false);
    }

    void PatchPlaylistVisibility(string playlistUri, bool isPublic, string revision)
    {
        if (_store?.GetPlaylist(playlistUri) is not { } header) return;
        _store.UpsertPlaylist(header with { IsPublic = isPublic, BasePermissionRevision = revision });
    }

    async Task TryRootlistPublicFlagAsync(string playlistUri, bool isPublic, CancellationToken ct)
    {
        var store = _store!;
        var ctx = _ctx();
        await _rootlistLane.WaitAsync(ct).ConfigureAwait(false);   // I2 — same lane as every other rootlist write
        try
        {
            int index = RootlistOps.FindPlaylistIndex(store.Rootlist(), playlistUri);
            if (index < 0)
            {
                await RootlistOps.BootstrapRootlistAsync(store, _transport, ctx, ct).ConfigureAwait(false);
                index = RootlistOps.FindPlaylistIndex(store.Rootlist(), playlistUri);
            }
            if (index < 0) return;
            var op = new PlaylistOp(PlaylistOpKind.UpdateItem, FromIndex: index, ItemPublic: isPublic);
            await RootlistOps.PostRootlistOpsAsync(store, _transport, _spclientBaseUrl, ctx, new[] { op }, ct, playlistUri).ConfigureAwait(false);
        }
        finally { _rootlistLane.Release(); }
    }

    void RequireStore()
    {
        if (_store is null) throw new InvalidOperationException("Playlist visibility/delete requires the persistent store.");
    }

    // Every edit is enqueued against the revision it was BUILT for (I5). OpRebaseStrategy compares that base against
    // the freshest stored one at replay time and re-expresses any index op whose base moved underneath it.
    long EnqueueEdit(string playlistUri, params PlaylistOp[] ops) => EnqueueEdit(playlistUri, (IReadOnlyList<PlaylistOp>)ops);
    long EnqueueEdit(string playlistUri, IReadOnlyList<PlaylistOp> ops)
        => RequireEdit(_mut.Edit(playlistUri, ops, _store?.PlaylistRevision(playlistUri)));

    // The ONE place a queued playlist edit turns into a caller-visible outcome. Everything that leaves here is a
    // PlaylistMutationException carrying a KIND — the UI maps kinds to copy and never reads a message (P1 shared contract).
    async Task DrainAsync(long edit, CancellationToken ct)
    {
        try
        {
            if (ScheduleDrain is { } viaLoop) await viaLoop(ct).ConfigureAwait(false);
            else await _mut.Drain(_transport, _ctx(), ct).ConfigureAwait(false);
        }
        catch (PlaylistMutationException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new PlaylistMutationException(PlaylistMutationFailure.Unknown, "That change could not be saved.", ex);
        }

        // The op dead-lettered during the drain (deleted / forbidden / torn rebase) — report the exact reason.
        if (_mut.TryTakeTerminal(edit, out var kind)) throw new PlaylistMutationException(kind, TerminalMessage(kind));

        // Still queued: offline (it will replay on reconnect — an informational outcome, not an error) vs merely
        // not-yet-confirmed (the server is reachable but the write has not landed).
        if (_mut.IsEditPending(edit))
            throw IsOffline()
                ? new PlaylistMutationException(PlaylistMutationFailure.Offline,
                    "You're offline — that change is queued and will sync when you reconnect.")
                : new PlaylistMutationException(PlaylistMutationFailure.Pending,
                    "That change is still syncing.");
    }

    static string TerminalMessage(PlaylistMutationFailure kind) => kind switch
    {
        PlaylistMutationFailure.Deleted => "That playlist no longer exists.",
        PlaylistMutationFailure.Forbidden => "You no longer have permission to edit that playlist.",
        PlaylistMutationFailure.Conflict => "That playlist changed while your edit was saving.",
        _ => "That change could not be saved.",
    };

    /// <summary>Offline == there is no live wire behind the switchable mutation transport (pre-go-live / after logout it
    /// is the named <see cref="StubTransport"/>), or the session carries no account. Both are the "queued, will replay"
    /// state, not a failure.</summary>
    bool IsOffline()
    {
        var t = _transport;
        while (t is SwitchableTransport sw) t = sw.Inner;
        return t is StubTransport || string.IsNullOrEmpty(_ctx().Account);
    }

    static long RequireEdit(long id) => id > 0 ? id
        : throw new PlaylistMutationException(PlaylistMutationFailure.NotSupported, "Playlist editing is not available.");

    // Every wavee:* URI is virtual/local and must stop before a Spotify playlist endpoint. In particular,
    // wavee:local:all used to slip past the narrower wavee:playlist:* check and produce permission/extender 400s.
    static bool IsLocal(string uri) => uri.StartsWith("wavee:", StringComparison.Ordinal);

    // ── rootlist folder CRUD (I5: rootlist structural ops are INDEX ops → online-only, lane-serialized) ───────────────
    // All three share one shape: take the rootlist lane, build the ops against the CURRENT marker stream, POST, and on a
    // 409 let TryPostRootlistOpsAsync bootstrap the fresh rootlist and rebuild the ops against it (twice, then Conflict).
    // The reply carries no contents (golden a164-folder-create-response), so the new tree is the LOCALLY applied one.

    public async Task<string> CreateFolderAsync(string name, RootlistPlacement placement, CancellationToken ct = default)
    {
        RequireStore();
        RequireOnline();
        string groupId = SpotifyIds.NewGroupId();
        string trimmed = string.IsNullOrWhiteSpace(name) ? "New Folder" : name.Trim();
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await RunRootlistOpAsync(groupId, entries =>
        {
            int at = RootlistOps.PlacementIndex(entries, placement);
            if (at < 0) throw new PlaylistMutationException(PlaylistMutationFailure.Conflict,
                "That folder no longer exists — try again.");
            return RootlistOps.BuildCreateFolder(entries, groupId, trimmed, at, nowMs);
        }, ct).ConfigureAwait(false);
        PlaylistMutationDiagnostics.FolderCreated(groupId, trimmed);
        return groupId;
    }

    public async Task RenameFolderAsync(string groupId, string name, CancellationToken ct = default)
    {
        RequireStore();
        RequireOnline();
        string trimmed = string.IsNullOrWhiteSpace(name) ? "New Folder" : name.Trim();
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // The rename has to resend the marker's ORIGINAL create timestamp. If the resident row has none (adopted before
        // the rootlist carried timestamps), the GET is the only place to get it — bootstrap BEFORE building the op.
        if (RootlistOps.FindFolderStart(_store!.Rootlist(), groupId) is var found && (found < 0 || _store.Rootlist()[found].AddedAtMs <= 0))
        {
            await _rootlistLane.WaitAsync(ct).ConfigureAwait(false);
            try { await RootlistOps.BootstrapRootlistAsync(_store, _transport, _ctx(), ct).ConfigureAwait(false); }
            finally { _rootlistLane.Release(); }
        }
        await RunRootlistOpAsync(groupId, entries => RootlistOps.BuildRenameFolder(entries, groupId, trimmed, nowMs)
            ?? throw FolderGone(), ct).ConfigureAwait(false);
        PlaylistMutationDiagnostics.FolderRenamed(groupId, trimmed);
    }

    public async Task DeleteFolderAsync(string groupId, CancellationToken ct = default)
    {
        RequireStore();
        RequireOnline();
        await RunRootlistOpAsync(groupId, entries => RootlistOps.BuildDeleteFolder(entries, groupId)
            ?? throw FolderGone(), ct).ConfigureAwait(false);
        PlaylistMutationDiagnostics.FolderDeleted(groupId);
    }

    /// <summary>Build-apply-post-adopt under the rootlist lane, with ONE rebase: a 409 has already refreshed the
    /// rootlist inside <see cref="RootlistOps.TryPostRootlistOpsAsync"/>, so the second attempt rebuilds its indices
    /// against the marker stream that actually exists. The tree is computed locally because a rootlist /changes reply
    /// carries revision bookkeeping only.
    /// <para>It is applied BEFORE the POST (the same order <c>MoveRootlistItemsAsync</c> uses): a new folder appears
    /// under the cursor instead of after a round trip, and a write that does not land puts <c>before</c> back rather
    /// than leaving a folder the server never got.</para></summary>
    async Task RunRootlistOpAsync(string logKey, Func<IReadOnlyList<RootlistEntry>, IReadOnlyList<PlaylistOp>> build, CancellationToken ct)
    {
        var store = _store!;
        await _rootlistLane.WaitAsync(ct).ConfigureAwait(false);
        var before = store.Rootlist();
        IReadOnlyList<RootlistEntry>? optimistic = null;
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var entries = store.Rootlist();
                var ops = build(entries);
                optimistic = RootlistOps.ApplyLocally(entries, ops);
                store.SetRootlist(optimistic);                          // rows now; the revision we still trust stands
                store.Bump("rootlist", CollectionKind.Playlists);
                var outcome = await RootlistOps.TryPostRootlistOpsAsync(store, _transport, _spclientBaseUrl, _ctx(), ops, logKey, ct)
                    .ConfigureAwait(false);
                if (outcome == RootlistPostOutcome.Rebased)
                {   // 409: the bootstrap already refreshed the rootlist — undo ours only if that GET carried nothing.
                    optimistic = RollbackRootlist(store, before, optimistic, "rebased-without-contents");
                    continue;
                }
                if (outcome == RootlistPostOutcome.Retry)
                    throw new PlaylistMutationException(PlaylistMutationFailure.Unknown,
                        "Spotify could not be reached — that change was not saved.");
                store.SetRootlist(optimistic, store.RootlistRevision());   // the reply advanced the revision; the ROWS are ours
                store.Bump("rootlist", CollectionKind.Playlists);
                return;
            }
            throw new PlaylistMutationException(PlaylistMutationFailure.Conflict,
                "Your library changed while that was saving — try again.");
        }
        catch (Exception ex)
        {
            RollbackRootlist(store, before, optimistic, ReasonOf(ex));
            throw;
        }
        finally { _rootlistLane.Release(); }
    }

    static PlaylistMutationException FolderGone() =>
        new(PlaylistMutationFailure.Deleted, "That folder no longer exists.");

    /// <summary>Rootlist structural ops are index ops (I5) and are therefore never queued: they would replay against a
    /// marker stream that has moved. Offline is a fast, typed refusal instead.</summary>
    void RequireOnline()
    {
        if (IsOffline()) throw new PlaylistMutationException(PlaylistMutationFailure.Offline,
            "You're offline — folders can only be changed while connected.");
    }
}
