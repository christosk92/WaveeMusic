using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Collections;
using Wavee.Backend.Playlists;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Backend;

// ── ENGINE ② — Mutation (durable intents + per-type sync strategy) ───────────────────────────────────────────────────
// Every write is a durable intent: optimistic apply → outbox → reconnect drain → reconcile, with terminal failure →
// rollback + dead-letter. Each resource TYPE supplies a strategy; SetReplay (library saves/follows) is the representative
// one wired here (the plan also has OpRebase for playlists and OnlineOnly for pins/cover-upload). The outbox is in-memory
// here (the durable table is store-backed in §4.1); the coalescing + reconcile shape is the real one.

// Carries either a boolean end-state (SetReplay) OR an ordered op list + base revision (OpRebase). Ops/BaseRev are
// additive/nullable so the boolean save path is unchanged.
// ParentFolderId is the rootlist FOLDER a queued ADD belongs in (create placement / follow-into-folder). Deliberately
// the folder ID and not an index: the index moves while the op waits in the outbox, so it is resolved at replay time.
public sealed record OutboxOp(long Id, string Type, string EntityKey, string SetId, bool TargetSaved, long LogicalTs, int Attempts,
    IReadOnlyList<PlaylistOp>? Ops = null, byte[]? BaseRev = null, string? ParentFolderId = null);

public interface IMutationStrategy
{
    string Type { get; }
    bool OfflineQueueable { get; }
    void ApplyOptimistic(OutboxOp op, IStore store);
    Task<bool> Replay(OutboxOp op, ITransport t, SessionContext ctx, CancellationToken ct);
    void Rollback(OutboxOp op, IStore store);
}

/// <summary>Durable backing for the outbox: pending intents persist here so a restart can replay them (offline-first).
/// SQLite implements it; a null engine outbox keeps the in-memory-only behaviour.</summary>
public interface IMutationOutbox
{
    IReadOnlyList<OutboxOp> Load();
    void Save(OutboxOp op);                 // insert-or-replace by Id (also used to persist an attempts bump)
    void Remove(long id);
    void DeadLetter(OutboxOp op, string reason);
}

/// <summary>SetReplay: idempotent end-state writes (saved tracks/albums/artists, follows). Local-intent-wins: replay the
/// desired state; a server no-op when already in that state. Rollback reverts on terminal failure.</summary>
public sealed class SetReplayStrategy : IMutationStrategy
{
    const string VendorType = "application/vnd.collection-v2.spotify.proto";

    // The echo ring (§7.1) records the client_update_id of each ACCEPTED write so LibrarySync can drop our own PubSubUpdate
    // echo. Nullable: tests/scaffold that don't exercise echo suppression pass none; production always wires it.
    readonly CollectionEchoRing? _echoRing;

    public SetReplayStrategy(CollectionEchoRing? echoRing = null) => _echoRing = echoRing;

    public string Type => "set";
    public bool OfflineQueueable => true;

    public void ApplyOptimistic(OutboxOp op, IStore store)
        // added_at = now for a save (the local like time — the server echo refines it); an unsave removes the row.
        => store.SetSaved(op.SetId, op.EntityKey, op.TargetSaved, SyncState.Pending,
                          op.TargetSaved ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : 0);

    // §2.4 (fixes RC4): the real collection write — POST /collection/v2/write with the vendor media type (the gateway 400s
    // on the generic type at the media-type layer) and an EXPLICIT method (never the RC4 bodyless-GET inference). The body is
    // a single-item WriteRequest carrying the desired end-state (added_at in UNIX SECONDS); on accept the cuid is recorded.
    public async Task<bool> Replay(OutboxOp op, ITransport t, SessionContext ctx, CancellationToken ct)
    {
        var cuid = Guid.NewGuid().ToString("N");
        var body = CollectionWriteMapper.BuildWrite(ctx.Account, op.SetId, op.EntityKey, op.TargetSaved,
                                                    DateTimeOffset.UtcNow.ToUnixTimeSeconds(), cuid);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = VendorType,
            ["Accept"] = VendorType,
        };
        var r = await t.Request(Channel.Spclient, "/collection/v2/write", body, ct, method: "POST", headers: headers).ConfigureAwait(false);
        if (r.Ok) _echoRing?.Record(cuid);   // register the echo id (§7.1)
        return r.Ok;
    }

    public void Rollback(OutboxOp op, IStore store)
        => store.SetSaved(op.SetId, op.EntityKey, !op.TargetSaved, SyncState.Confirmed);
}

/// <summary>OpRebase: ordered playlist edits (add/remove/reorder). Optimistic-applies the ops to the local membership;
/// replays as a POST of the ListChanges body to /playlist/v2/{path}/changes against the captured base revision. The
/// pre-edit membership snapshot (for rollback on terminal failure) is engine-managed.</summary>
public sealed class OpRebaseStrategy : IMutationStrategy
{
    readonly IStore _store;
    readonly Func<string> _spclientBaseUrl;
    // I4 — where a /changes response that could NOT be folded in place drops its uri. Required, never optional: the
    // strategy runs inside the drain and has no business fetching, so this is the only way the loop learns to converge.
    readonly PlaylistResyncQueue _resync;

    public OpRebaseStrategy(IStore store, Func<string> spclientBaseUrl, PlaylistResyncQueue resync)
        => (_store, _spclientBaseUrl, _resync) = (store, spclientBaseUrl, resync);

    public string Type => "oprebase";
    public bool OfflineQueueable => true;

    public void ApplyOptimistic(OutboxOp op, IStore store) => TryApply(op, store);

    /// <summary>Apply one queued edit's optimistic effect to the CURRENT membership. False = torn (the list this op was
    /// built against no longer exists in a shape the op fits). <see cref="MutationEngine.ReapplyPending"/> needs that
    /// answer — a torn re-apply is a Conflict dead-letter, not a silent skip.</summary>
    public static bool TryApply(OutboxOp op, IStore store)
    {
        var ops = op.Ops ?? Array.Empty<PlaylistOp>();
        var list = new List<PlaylistMember>(store.Membership(op.EntityKey));
        try { PlaylistDiffApplier.Apply(list, ops); }
        catch (ArgumentOutOfRangeException) { return false; }
        store.SetMembership(op.EntityKey, list, store.PlaylistRevision(op.EntityKey));
        ApplyHeaderPatch(store, op.EntityKey, ops);
        return true;
    }

    internal static void ApplyHeaderPatch(IStore store, string uri, IReadOnlyList<PlaylistOp> ops)
    {
        PlaylistListAttributePatch? patch = null;
        for (int i = 0; i < ops.Count; i++)
            if (ops[i].Kind == PlaylistOpKind.UpdateList && ops[i].ListPatch is { } p) { patch = p; break; }
        if (patch is null) return;
        var header = store.GetPlaylist(uri);
        if (header is null) return;
        string? name = patch.ClearName ? "" : patch.Name ?? header.Name;
        string? desc = patch.ClearDescription ? null : patch.Description ?? header.Description;
        Image? cover = patch.ClearPicture ? null
            : patch.PictureBytes is { Length: > 0 } pic
                ? new Image("https://i.scdn.co/image/" + Convert.ToHexStringLower(pic))
                : header.Cover;
        bool collab = patch.Collaborative ?? header.Capabilities.IsCollaborative;
        var caps = header.Capabilities with { IsCollaborative = collab };
        store.UpsertPlaylist(header with { Name = name ?? header.Name, Description = desc, Cover = cover, Capabilities = caps });
    }

    // §2.7 — the /changes POST now (1) carries the first-party header set + an EXPLICIT POST method (a bare POST 200-OKs
    // against a passive read handler → a silent no-op; that latent RC-class bug in playlist edits is fixed here), and (2)
    // REBASES per attempt against the freshest stored revision (mirroring the reference), then CAPTURES the 200 response as
    // the fresh membership + revision (the response IS the fresh list) so echo suppression (§7.3) sees a matching revision.
    public async Task<bool> Replay(OutboxOp op, ITransport t, SessionContext ctx, CancellationToken ct)
    {
        // TERMINAL before the wire: the playlist was deleted by its owner. Retrying this ten times would only spend ten
        // 404s and hold the user's edit "pending" for a minute; dead-letter it now.
        if (_store.GetPlaylist(op.EntityKey) is { DeletedByOwner: true })
            throw new PlaylistMutationException(PlaylistMutationFailure.Deleted,
                "That playlist was deleted and can no longer be edited.");

        var path = op.EntityKey.StartsWith("spotify:", StringComparison.Ordinal) ? op.EntityKey.Substring(8).Replace(':', '/') : op.EntityKey;
        var storedRev = _store.PlaylistRevision(op.EntityKey);
        var baseRev = storedRev ?? op.BaseRev;   // rebase per attempt: freshest cached rev wins
        var ops = RebaseOps(op, storedRev);
        var body = PlaylistWireMapper.BuildChanges(baseRev, ops, ctx.Account, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var headers = SpotifyHeaders.PlaylistV2Mutation(ctx.Locale, _spclientBaseUrl());
        var r = await t.Request(Channel.Spclient, $"/playlist/v2/{path}/changes", body, ct, method: "POST", headers: headers).ConfigureAwait(false);
        if (r.Ok) { CaptureChangesResponse(_store, _resync, op.EntityKey, r.Body); return true; }

        // TERMINAL failures throw typed and are dead-lettered immediately (no attempt burn, no 10× retry storm):
        // the playlist is gone (404/410) or we are not allowed to edit it (403). Everything else — 409 revision
        // conflict, 5xx, a network fault — is retryable and just returns false so the next drain rebases.
        if (r.Status is 404 or 410)
            throw new PlaylistMutationException(PlaylistMutationFailure.Deleted,
                "That playlist no longer exists.");
        if (r.Status == 403)
            throw new PlaylistMutationException(PlaylistMutationFailure.Forbidden,
                "You no longer have permission to edit that playlist.");
        return false;   // a 409 (revision conflict) surfaces as !Ok → retry rebased against the fresher cached revision next drain
    }

    // ── I5: an index op is BASE-BOUND ────────────────────────────────────────────────────────────────────────────────
    // An ADD{from_index} / REM{from_index,length} describes a position in ONE specific revision of the list. If the
    // stored head moved between building the op and replaying it (a foreign edit landed, a /diff applied, a snapshot was
    // adopted), those indices name different rows now and sending them verbatim silently corrupts the list. Keyed ops
    // (every playlist REM/MOV we build in P2) are position-independent and pass through untouched; the two index shapes
    // are re-expressed against the resident membership:
    //   ADD  → from_index recomputed from the recorded anchor row (anchor gone / never recorded → append).
    //   REM  → re-expressed as the keyed form when every row carries an item_id; otherwise the op cannot be honestly
    //          re-expressed at all and is TERMINAL (Conflict → rollback + dead-letter), never a guess.
    IReadOnlyList<PlaylistOp> RebaseOps(OutboxOp op, byte[]? storedRev)
    {
        var ops = op.Ops ?? Array.Empty<PlaylistOp>();
        if (ops.Count == 0 || op.BaseRev is null || storedRev is null || PlaylistRevisions.Equal(storedRev, op.BaseRev))
            return ops;
        if (!NeedsRebase(ops)) return ops;

        var membership = _store.Membership(op.EntityKey);
        var rebased = new List<PlaylistOp>(ops.Count);
        for (int i = 0; i < ops.Count; i++) rebased.Add(RebaseOne(ops[i], membership));
        PlaylistMutationDiagnostics.OpsRebased(op.EntityKey, rebased.Count);
        return rebased;
    }

    static bool NeedsRebase(IReadOnlyList<PlaylistOp> ops)
    {
        for (int i = 0; i < ops.Count; i++)
        {
            var o = ops[i];
            if (o.Kind == PlaylistOpKind.Add && !o.AddFirst && !o.AddLast) return true;
            if (o.Kind == PlaylistOpKind.Remove && !o.ItemsAsKey) return true;
        }
        return false;
    }

    static PlaylistOp RebaseOne(PlaylistOp op, IReadOnlyList<PlaylistMember> membership)
    {
        if (op.Kind == PlaylistOpKind.Add && !op.AddFirst && !op.AddLast)
        {
            if (op.Anchor is not { } anchor) return op with { FromIndex = 0, AddLast = true };
            if (anchor.Kind == PlaylistMoveAnchorKind.First) return op with { FromIndex = 0 };
            int at = IndexOfId(membership, anchor.AfterItemId);
            return at < 0 ? op with { FromIndex = 0, AddLast = true } : op with { FromIndex = at + 1 };
        }
        if (op.Kind == PlaylistOpKind.Remove && !op.ItemsAsKey)
        {
            if (op.Items is not { Count: > 0 } items || items.Count != op.Length)
                throw new PlaylistMutationException(PlaylistMutationFailure.Conflict,
                    "That playlist changed while your edit was saving.");
            for (int i = 0; i < items.Count; i++)
                if (string.IsNullOrEmpty(items[i].ItemId))
                    throw new PlaylistMutationException(PlaylistMutationFailure.Conflict,
                        "That playlist changed while your edit was saving.");
            return op with { ItemsAsKey = true, FromIndex = 0, Length = 0 };
        }
        return op;
    }

    static int IndexOfId(IReadOnlyList<PlaylistMember> membership, string? itemId)
    {
        if (string.IsNullOrEmpty(itemId)) return -1;
        for (int i = 0; i < membership.Count; i++)
            if (string.Equals(membership[i].ItemId, itemId, StringComparison.Ordinal)) return i;
        return -1;
    }

    // ── I4: never advance a revision past ops you did not apply ───────────────────────────────────────────────────────
    // Fold the /changes SelectedListContent response back into the store. The ORDER is the invariant:
    //   (a) multiple_heads || changes_require_resync → the server could not express the result against our base. Do NOT
    //       advance the revision; queue a revalidation (a full refetch inside the /diff fallback) for after the drain.
    //   (b) sync_result carries ops → apply them to the local membership FIRST, then adopt resulting_revisions[^1].
    //       A torn apply means our baseline drifted → queue a revalidation, revision unchanged.
    //   (c) full contents → replace membership, adopt the head.
    //   (d) rev-only (empty sync_result, no contents) → advance just the revision, keeping current rows.
    // Zstd-guarded (§2.7). I1: a resulting revision that is not the 24-byte head is never adopted.
    // Static + explicitly parameterised because CreatePlaylistStrategy folds its own /changes reply through the SAME
    // path: a create response is the rev-only case (no contents, empty sync_result), which case (d) below already owns.
    internal static void CaptureChangesResponse(IStore store, PlaylistResyncQueue resync, string uri, byte[] body)
    {
        var bytes = SpotifyZstd.MaybeDecompressZstd(body);
        if (bytes.Length == 0) return;
        Pl.SelectedListContent slc;
        try { slc = Pl.SelectedListContent.Parser.ParseFrom(bytes); }
        catch
        {
            PlaylistMutationDiagnostics.DealerDrop("playlist/changes", "unparseable", bytes.Length);
            return;
        }

        if (slc.MultipleHeads || slc.ChangesRequireResync)
        {
            resync.Mark(uri);
            PlaylistMutationDiagnostics.SyncResultTorn(uri, slc.MultipleHeads ? "multiple-heads" : "requires-resync");
            return;
        }

        var rev = PlaylistWireMapper.LastResultingRevision(slc);
        bool storable = PlaylistRevisions.IsWellFormed(rev);
        if (!storable && rev is not null) PlaylistMutationDiagnostics.RootlistBadRevision(rev.Length, "changes-response");

        IReadOnlyList<PlaylistOp> syncOps;
        try { syncOps = slc.SyncResult is { } sync ? PlaylistWireMapper.MapOps(sync.Ops) : Array.Empty<PlaylistOp>(); }
        catch (ArgumentOutOfRangeException)   // an op shape this client cannot express — converge by refetching
        {
            resync.Mark(uri);
            PlaylistMutationDiagnostics.SyncResultTorn(uri, "unsupported-op");
            return;
        }
        if (syncOps.Count > 0)
        {
            var list = new List<PlaylistMember>(store.Membership(uri));
            try { PlaylistDiffApplier.Apply(list, syncOps); }
            catch (ArgumentOutOfRangeException)
            {
                resync.Mark(uri);
                PlaylistMutationDiagnostics.SyncResultTorn(uri, "torn-apply");
                return;   // revision NOT advanced — we did not apply these ops
            }
            store.SetMembership(uri, list, storable ? rev : store.PlaylistRevision(uri));
            PlaylistMutationDiagnostics.SyncResultApplied(uri, syncOps.Count);
            return;
        }

        if (slc.Contents is { } contents && contents.Items.Count > 0)
        {
            var (members, _) = PlaylistWireMapper.ParseContents(slc);
            store.SetMembership(uri, members, storable ? rev : store.PlaylistRevision(uri));
        }
        else if (storable)
            store.SetMembership(uri, store.Membership(uri), rev);   // rev-only: keep current rows, advance the revision
    }

    public void Rollback(OutboxOp op, IStore store) { /* membership restore is engine-managed via the pre-edit snapshot */ }
}

/// <summary>Create (P3): a brand-new playlist is a <c>/changes</c> POST to the CLIENT-MINTED id, against the fixed
/// 8-byte create base — never a rebase, because there is no prior revision to rebase onto. The optimistic store row
/// (header + empty membership + rootlist entry + saved pill) is seeded synchronously by
/// <c>PlaylistMutationSource.CreatePlaylist</c> before the op is ever enqueued, which is what lets the UI navigate to
/// the new page on the next frame; this strategy owns the WIRE and the ROLLBACK of that seed.
/// <para>The queued op carries exactly one domain op — UPDATE_LIST{name} — so the durable blob round-trips through the
/// same <c>outbox.op</c> column as a playlist edit.</para></summary>
public sealed class CreatePlaylistStrategy : IMutationStrategy
{
    readonly IStore _store;
    readonly Func<string> _spclientBaseUrl;
    readonly PlaylistResyncQueue _resync;

    public CreatePlaylistStrategy(IStore store, Func<string> spclientBaseUrl, PlaylistResyncQueue resync)
        => (_store, _spclientBaseUrl, _resync) = (store, spclientBaseUrl, resync);

    public string Type => "create";
    public bool OfflineQueueable => true;                 // an offline create simply queues — that is the whole point

    // The seed is inline (the seam returns the uri synchronously, so it cannot wait for an enqueue callback).
    public void ApplyOptimistic(OutboxOp op, IStore store) { }

    public async Task<bool> Replay(OutboxOp op, ITransport t, SessionContext ctx, CancellationToken ct)
    {
        string id = EntityUri.IdOf(op.EntityKey);
        string name = NameOf(op.Ops);
        var body = PlaylistWireMapper.BuildCreateChanges(name, ctx.Account, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var headers = SpotifyHeaders.PlaylistV2Create(ctx.Locale, _spclientBaseUrl());
        var r = await t.Request(Channel.Spclient, $"/playlist/v2/playlist/{id}/changes", body, ct, method: "POST", headers: headers).ConfigureAwait(false);
        if (r.Ok)
        {
            // The reply carries revision bookkeeping only — no name, no contents — so this is the rev-only arm.
            OpRebaseStrategy.CaptureChangesResponse(_store, _resync, op.EntityKey, r.Body);
            PlaylistMutationDiagnostics.CreateAcked(op.EntityKey);
            return true;
        }
        // A 4xx on a create is TERMINAL: the id is minted, the base is fixed and the body is complete, so no retry can
        // change the answer (a 409 here means the id is taken — the retry is a NEW id, which is a new user action).
        if (r.Status is >= 400 and < 500)
        {
            PlaylistMutationDiagnostics.CreateFailed(op.EntityKey, "http-" + r.Status);
            throw new PlaylistMutationException(PlaylistMutationFailure.Unknown,
                "That playlist could not be created.");
        }
        return false;   // 5xx / network → retry on the next drain
    }

    /// <summary>Undo the whole optimistic seed: the rootlist entry, the saved pill and the empty membership all go. The
    /// cached header row is left behind deliberately — it is unreachable (not in the rootlist, not saved) and evicting
    /// an entity is the cache tier's job, not a mutation's.</summary>
    public void Rollback(OutboxOp op, IStore store)
    {
        var uri = op.EntityKey;
        if (RootlistOps.RemovePlaylistEntry(store.Rootlist(), uri) is { } trimmed) store.SetRootlist(trimmed);
        store.SetSaved("playlists", uri, false, SyncState.Confirmed);
        store.SetMembership(uri, Array.Empty<PlaylistMember>(), null);
        store.Bump("rootlist", CollectionKind.Playlists);
    }

    internal static string NameOf(IReadOnlyList<PlaylistOp>? ops)
    {
        if (ops is not null)
            for (int i = 0; i < ops.Count; i++)
                if (ops[i].Kind == PlaylistOpKind.UpdateList && ops[i].ListPatch is { Name: { } n }) return n;
        return "";
    }

}
/// <summary>RootlistFollow (§2.5, fixes RC3): following/unfollowing a playlist is a rootlist ADD/REM, not a collection
/// write. Optimistic-flips the "playlists" saved pill + edits the rootlist entry list inline; replays as a POST of the
/// rootlist ListChanges body to /playlist/v2/user/{username}/rootlist/changes against the stored rootlist revision
/// (bootstrapped once via a GET if absent). The 200 response IS the fresh rootlist → captured; a 409 refetches the base
/// so the next drain rebases.</summary>
public sealed class RootlistFollowStrategy : IMutationStrategy
{
    readonly IStore _store;
    readonly RootlistLane _lane;

    /// <param name="lane">I2 — the ONE rootlist write lane, shared with <c>PlaylistMutationSource</c>'s direct ops.
    /// Required, never optional: an unserialized replay can interleave with an in-flight positional MOV and rebase it
    /// against marker indices that moved underneath it.</param>
    public RootlistFollowStrategy(IStore store, RootlistLane lane) => (_store, _lane) = (store, lane);

    public string Type => "rootlist";
    public bool OfflineQueueable => true;

    // (1) flip the pill (Pending — the Saved union folds it this frame, §2.8) and (2) edit the rootlist entry inline so the
    // sidebar reflects it immediately (follow → insert at position 0; unfollow → drop the matching row). Rev-preserving.
    public void ApplyOptimistic(OutboxOp op, IStore store)
    {
        var uri = op.EntityKey;
        bool follow = op.TargetSaved;
        store.SetSaved("playlists", uri, follow, SyncState.Pending,
                       follow ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : 0);
        var next = follow ? InsertFollow(store.Rootlist(), uri) : RemoveFollow(store.Rootlist(), uri);
        if (next is not null) store.SetRootlist(next);   // 1-arg overload preserves the stored revision (§2.6)
    }

    public async Task<bool> Replay(OutboxOp op, ITransport t, SessionContext ctx, CancellationToken ct)
    {
        var uri = op.EntityKey;
        bool follow = op.TargetSaved;
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // I2 — the whole bootstrap+POST runs on the one rootlist lane, so a drain can never interleave with a direct
        // rootlist op (move / delete / visibility / create-follow). Nothing under the lane awaits a drain → no cycle.
        await _lane.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // (1) base revision — one-time bootstrap of the rootlist if we don't have it.
            var rev = _store.RootlistRevision();
            if (rev is null) rev = await RootlistOps.BootstrapRootlistAsync(_store, t, ctx, ct).ConfigureAwait(false);

            // (2) the op: follow → ADD at index 0 with item attributes (timestamp ms + public); unfollow → keyed REM
            // (items_as_key: remove by uri, order-independent). NEVER index-based and never skipped-on-local-absence — the
            // optimistic edit already removed the local row before this replay runs, so local absence proves nothing about
            // the server; the server treats removing an absent uri as a no-op success.
            // The ADD index is resolved HERE, not at enqueue time: a queued op can sit through any number of foreign
            // rootlist edits, so a stored index would name a different row by the time it replays. The op remembers the
            // FOLDER; the index is recomputed from the folder's current start marker (folder gone → top of the list).
            int at = 0;
            if (op.ParentFolderId is { Length: > 0 })
            {
                at = RootlistOps.PlacementIndex(_store.Rootlist(), new RootlistPlacement(op.ParentFolderId));
                if (at < 0) { PlaylistMutationDiagnostics.RootlistPlacementLost(uri, op.ParentFolderId); at = 0; }
            }
            var plop = follow
                ? new PlaylistOp(PlaylistOpKind.Add, FromIndex: at, Items: new[] { new PlaylistMember("", uri, null, nowMs) })
                : new PlaylistOp(PlaylistOpKind.Remove, Items: new[] { new PlaylistMember("", uri, null, 0) }, ItemsAsKey: true);

            // (3–5) POST rootlist changes — shared with visibility/delete (409 → rebase, return false for retry).
            // Rebased (409) and Retry (5xx/network) both mean "not landed": return false and let the outbox replay it
            // with backoff. Only a terminal 4xx throws, and only that dead-letters.
            return await RootlistOps.TryPostRootlistOpsAsync(_store, t, ctx, new[] { plop }, uri, ct).ConfigureAwait(false)
                == RootlistPostOutcome.Applied;
        }
        finally { _lane.Release(); }
    }

    // undo the optimistic entry edit + flip the pill back (a subsequent authoritative refetch reconciles ordering).
    public void Rollback(OutboxOp op, IStore store)
    {
        var uri = op.EntityKey;
        bool follow = op.TargetSaved;
        store.SetSaved("playlists", uri, !follow, SyncState.Confirmed);
        var next = follow ? RemoveFollow(store.Rootlist(), uri) : InsertFollow(store.Rootlist(), uri);
        if (next is not null) store.SetRootlist(next);
    }

    // insert a followed playlist at position 0 (skip if already present); returns null on no-op.
    static IReadOnlyList<RootlistEntry>? InsertFollow(IReadOnlyList<RootlistEntry> cur, string uri)
    {
        for (int i = 0; i < cur.Count; i++) if (cur[i].Kind == 0 && cur[i].Uri == uri) return null;   // already followed
        var list = new List<RootlistEntry>(cur.Count + 1) { new RootlistEntry(0, 0, uri, null, 0) };
        for (int i = 0; i < cur.Count; i++) list.Add(cur[i]);
        return Renumber(list);
    }

    // remove the first matching kind-0 row; returns null when absent (no-op).
    static IReadOnlyList<RootlistEntry>? RemoveFollow(IReadOnlyList<RootlistEntry> cur, string uri)
    {
        int found = -1;
        for (int i = 0; i < cur.Count; i++) if (cur[i].Kind == 0 && cur[i].Uri == uri) { found = i; break; }
        if (found < 0) return null;
        var list = new List<RootlistEntry>(cur.Count - 1);
        for (int i = 0; i < cur.Count; i++) if (i != found) list.Add(cur[i]);
        return Renumber(list);
    }

    static IReadOnlyList<RootlistEntry> Renumber(List<RootlistEntry> list)
    {
        for (int i = 0; i < list.Count; i++) list[i] = list[i] with { Position = i };
        return list;
    }
}

public sealed record EditSnapshot(IReadOnlyList<PlaylistMember> Membership, Playlist? Header);

public sealed class MutationEngine
{
    const int MaxAttempts = 10;

    readonly IStore _store;
    readonly Dictionary<string, IMutationStrategy> _strategies;
    readonly IMutationOutbox? _durable;
    readonly object _gate = new();
    // "set" rows coalesce (one per (set, entity), latest end-state wins); "oprebase" rows append (keyed by unique id —
    // the server permits duplicate playlist items, so edits must NOT dedupe).
    readonly Dictionary<string, OutboxOp> _outbox = new();
    readonly Dictionary<long, EditSnapshot> _editSnapshots = new();   // pre-edit membership + header for OpRebase rollback
    // §8.3 — per-op replay backoff (in-memory only: after a restart, attempts reload from SQLite and the clock resets —
    // a restart is a natural retry moment). Drain skips ops whose next-attempt time hasn't come; cleared on success/dead-letter.
    readonly Dictionary<long, DateTime> _nextAttemptAt = new();
    // Terminal outcomes keyed by the edit id that produced them, so the caller awaiting a drain can rethrow the exact
    // kind (Deleted / Forbidden / Conflict) instead of inferring "still queued". Taken once, then forgotten.
    readonly Dictionary<long, PlaylistMutationFailure> _terminalByEditId = new();
    // Per-op completion promises. Only the create path registers one today: its caller gets the uri back synchronously
    // and needs a separate handle for "the server has it now" / "it failed for good". Resolved exactly once.
    readonly Dictionary<long, TaskCompletionSource> _completions = new();
    readonly SimpleEvent<string> _pendingChanged = new();
    readonly Func<DateTime> _now;
    long _seq;

    // "set" (collection saves) and "rootlist" (playlist follows) COALESCE per (set, entity) — latest end-state wins, so a
    // follow/unfollow toggle never stacks; "oprebase" (playlist edits) append (keyed by unique id — duplicate items are
    // legal). The "rootlist" shape is exactly what HasPending checks (rootlist|{setId}|{entityKey}).
    static string KeyOf(OutboxOp op) => op.Type == "set" ? $"set|{op.SetId}|{op.EntityKey}"
        : op.Type == "rootlist" ? $"rootlist|{op.SetId}|{op.EntityKey}"
        : $"{op.Type}|{op.Id}";

    public List<OutboxOp> DeadLetter { get; } = new();

    public MutationEngine(IStore store, IEnumerable<IMutationStrategy> strategies, IMutationOutbox? durable = null, Func<DateTime>? now = null)
    {
        _store = store;
        _strategies = strategies.ToDictionary(s => s.Type);
        _durable = durable;
        _now = now ?? (() => DateTime.UtcNow);
        if (_durable is not null)
            foreach (var op in _durable.Load())   // restore pending intents from disk (the optimistic store state already persisted)
                if (_strategies.ContainsKey(op.Type)) { _outbox[KeyOf(op)] = op; if (op.Id > _seq) _seq = op.Id; }
    }

    public int Pending { get { lock (_gate) return _outbox.Count; } }

    /// <summary>Fires with an ENTITY KEY (a playlist uri, a saved item's uri) every time that entity's pending count can
    /// have changed: an enqueue, an ack, a dead-letter. Non-replaying — a subscriber reads the current count itself via
    /// <see cref="PendingFor"/>. This is what drives the per-playlist "syncing…" chip.</summary>
    public IObservable<string> PendingChanged => _pendingChanged;

    /// <summary>How many intents are still queued for one entity (playlist uri / saved uri). The I3(a) gate: while this
    /// is &gt; 0 an inbound push for that uri is never applied in place.</summary>
    public int PendingFor(string entityKey)
    {
        if (string.IsNullOrEmpty(entityKey)) return 0;
        int n = 0;
        lock (_gate)
            foreach (var op in _outbox.Values)
                if (string.Equals(op.EntityKey, entityKey, StringComparison.Ordinal)) n++;
        return n;
    }

    void NotifyPending(string entityKey)
    {
        if (entityKey.Length == 0) return;
        _pendingChanged.OnNext(entityKey);
    }

    /// <summary>The pending-op shield (§7.2): true when a local intent is in flight for this (setId, entityKey). Checks
    /// BOTH the "set" key shape (collection saves) and the "rootlist" key shape (playlist follows — `rootlist|playlists|
    /// {uri}`). The rootlist strategy lands in a later phase; the key check is built now so inbound Confirmed writes can
    /// be skipped for a shielded key while its own drain reconciles it. Unused by production code yet.</summary>
    public bool HasPending(string setId, string entityKey)
    {
        lock (_gate) return _outbox.ContainsKey($"set|{setId}|{entityKey}") || _outbox.ContainsKey($"rootlist|{setId}|{entityKey}");
    }

    /// <summary>Save / unsave (idempotent). Optimistic: the store reflects it as Pending immediately; the outbox replays on drain.</summary>
    public void Save(string setId, string uri, bool saved)
    {
        if (!_strategies.TryGetValue("set", out var s)) return;
        var id = Interlocked.Increment(ref _seq);
        var op = new OutboxOp(id, "set", uri, setId, saved, id, 0);
        OutboxOp? replaced = null;
        lock (_gate) { if (_outbox.TryGetValue(KeyOf(op), out var ex)) replaced = ex; _outbox[KeyOf(op)] = op; }   // coalesce
        if (_durable is not null) { if (replaced is not null) _durable.Remove(replaced.Id); _durable.Save(op); }
        s.ApplyOptimistic(op, _store);
        NotifyPending(uri);
    }

    /// <summary>Follow / unfollow a playlist (§2.5) — a rootlist ADD/REM, not a collection write. Sibling of <see cref="Save"/>:
    /// optimistic (the "playlists" pill flips + the rootlist entry edits immediately), coalesced per uri (latest end-state
    /// wins — follow/unfollow toggles must not stack), and durably persisted so it replays on the next login/reconnect.</summary>
    public void Follow(string playlistUri, bool follow, string? parentFolderId = null)
    {
        if (!_strategies.TryGetValue("rootlist", out var s)) return;
        var id = Interlocked.Increment(ref _seq);
        var op = new OutboxOp(id, "rootlist", playlistUri, "playlists", follow, id, 0, ParentFolderId: parentFolderId);
        OutboxOp? replaced = null;
        lock (_gate) { if (_outbox.TryGetValue(KeyOf(op), out var ex) && ex.Id != id) replaced = ex; _outbox[KeyOf(op)] = op; }   // coalesce per uri
        if (_durable is not null) { if (replaced is not null) _durable.Remove(replaced.Id); _durable.Save(op); }
        s.ApplyOptimistic(op, _store);
        NotifyPending(playlistUri);
    }

    /// <summary>Queue the CREATE of a client-minted playlist (P3). The caller has already seeded the optimistic store
    /// row, so this owns only the durable intent; the returned task completes when the server has acknowledged the
    /// create and FAULTS with the typed <see cref="PlaylistMutationException"/> if it dead-letters.
    /// <para>Ordering matters and is guaranteed by the monotonic id plus the drain's per-entity gate: the rootlist ADD
    /// and any seed-track edits enqueued after this one never reach the wire before the playlist exists.</para></summary>
    public (long Id, Task Completion) Create(string playlistUri, string name)
    {
        if (!_strategies.TryGetValue("create", out var s))
            return (0, Task.FromException(new PlaylistMutationException(PlaylistMutationFailure.NotSupported,
                "Playlist creation is not available.")));
        var id = Interlocked.Increment(ref _seq);
        var ops = new[] { new PlaylistOp(PlaylistOpKind.UpdateList, ListPatch: new PlaylistListAttributePatch(Name: name)) };
        var op = new OutboxOp(id, "create", playlistUri, playlistUri, false, id, 0, ops, PlaylistRevisions.NewCreateBase());
        var promise = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) { _outbox[KeyOf(op)] = op; _completions[id] = promise; }
        _durable?.Save(op);
        s.ApplyOptimistic(op, _store);
        PlaylistMutationDiagnostics.CreateQueued(playlistUri, name);
        NotifyPending(playlistUri);
        return (id, promise.Task);
    }

    /// <summary>Edit a playlist's ordered membership (add/remove/reorder). Each edit is a DISTINCT outbox row — appended,
    /// never coalesced. Optimistic: the membership reflects it immediately; a pre-edit snapshot is captured so a terminal
    /// replay failure rolls the membership back.</summary>
    public long Edit(string playlistUri, IReadOnlyList<PlaylistOp> ops, byte[]? baseRev = null)
    {
        if (!_strategies.TryGetValue("oprebase", out var s)) return 0;
        var id = Interlocked.Increment(ref _seq);
        var op = new OutboxOp(id, "oprebase", playlistUri, playlistUri, false, id, 0, ops, baseRev);
        lock (_gate) { _outbox[KeyOf(op)] = op; _editSnapshots[id] = new EditSnapshot(_store.Membership(playlistUri), _store.GetPlaylist(playlistUri)); }
        _durable?.Save(op);
        s.ApplyOptimistic(op, _store);
        NotifyPending(playlistUri);
        return id;
    }

    /// <summary>Whether one specific playlist edit is still queued after a drain attempt.</summary>
    public bool IsEditPending(long id)
    {
        if (id <= 0) return false;
        lock (_gate) return _outbox.ContainsKey($"oprebase|{id}");
    }

    /// <summary>Take the terminal outcome of one edit, if it dead-lettered. Consumed once — the caller awaiting the drain
    /// rethrows it as a <see cref="PlaylistMutationException"/> and nobody else can report the same failure twice.</summary>
    public bool TryTakeTerminal(long editId, out PlaylistMutationFailure kind)
    {
        kind = default;
        if (editId <= 0) return false;
        lock (_gate) return _terminalByEditId.Remove(editId, out kind);
    }

    /// <summary>I3(b) — the SINGLE membership-replace chokepoint. A fresh network snapshot lands here, its revision
    /// I1-gated (a non-24-byte head keeps the baseline we already trust), and every still-pending local op for that
    /// playlist is re-applied on top so an unacked edit never visibly reverts while the server catches up.</summary>
    public void AdoptSnapshot(string playlistUri, IReadOnlyList<PlaylistMember> members, byte[]? revision)
    {
        if (string.IsNullOrEmpty(playlistUri)) return;
        byte[]? adopted = revision;
        if (!PlaylistRevisions.IsWellFormed(revision))
        {
            if (revision is not null) PlaylistMutationDiagnostics.RootlistBadRevision(revision.Length, "adopt-snapshot");
            adopted = _store.PlaylistRevision(playlistUri);
        }
        _store.SetMembership(playlistUri, members, adopted);
        ReapplyPending(playlistUri);
    }

    /// <summary>Re-apply every still-pending playlist edit for one uri on top of whatever membership is resident now, in
    /// id order (the drain's own tiebreak). Returns how many were re-applied. An op that no longer fits the list — its
    /// anchor row is gone, its index range no longer exists — is TERMINAL: it dead-letters with
    /// <see cref="PlaylistMutationFailure.Conflict"/> rather than being replayed against a list it cannot describe.
    /// <para>Deliberately NOT a rollback: we are sitting on a fresh authoritative snapshot, so "undo the optimistic
    /// effect" is exactly "don't re-apply it".</para></summary>
    public int ReapplyPending(string playlistUri)
    {
        if (string.IsNullOrEmpty(playlistUri)) return 0;
        List<OutboxOp> ops;
        lock (_gate)
            ops = _outbox.Values
                .Where(o => o.Type == "oprebase" && string.Equals(o.EntityKey, playlistUri, StringComparison.Ordinal))
                .OrderBy(o => o.Id).ToList();
        if (ops.Count == 0) return 0;

        int applied = 0;
        foreach (var op in ops)
        {
            if (OpRebaseStrategy.TryApply(op, _store)) { applied++; continue; }
            DeadLetterTerminal(op, PlaylistMutationFailure.Conflict, "reapply-torn");
        }
        return applied;
    }

    // Resolve an op's completion promise (if it has one). Called OUTSIDE the lock — a continuation must never run with
    // the outbox gate held.
    void SettleCompletion(long id, PlaylistMutationFailure? failure, string message)
    {
        TaskCompletionSource? promise;
        lock (_gate) { if (!_completions.Remove(id, out promise)) return; }
        if (failure is { } kind) promise!.TrySetException(new PlaylistMutationException(kind, message));
        else promise!.TrySetResult();
    }


    // Drop one op out of the outbox for good, record WHY so the awaiting caller can rethrow the exact kind, and log it.
    // Callers hold no lock. Never bumps Attempts: a terminal failure is not a retry.
    void DeadLetterTerminal(OutboxOp op, PlaylistMutationFailure kind, string reason)
    {
        var key = KeyOf(op);
        bool removed;
        lock (_gate)
        {
            removed = _outbox.TryGetValue(key, out var cur) && cur.Id == op.Id;
            if (removed)
            {
                _outbox.Remove(key);
                DeadLetter.Add(op);
                _nextAttemptAt.Remove(op.Id);
                _editSnapshots.Remove(op.Id);
                if (op.Type == "oprebase") _terminalByEditId[op.Id] = kind;
            }
        }
        if (!removed) return;
        _durable?.Remove(op.Id);
        _durable?.DeadLetter(op, reason + ":" + kind);
        PlaylistMutationDiagnostics.MutationTerminal(op.EntityKey, kind, reason);
        AfterDeadLetter(op, kind, reason);
        NotifyPending(op.EntityKey);
    }

    // What EVERY dead-letter owes, whichever path got there: settle the op's completion promise with the typed failure,
    // and — for a create — drop the rest of that playlist's recipe. The rootlist ADD and any seed-track edits describe a
    // playlist the server never made; replaying them would produce an orphan rootlist row pointing at nothing.
    void AfterDeadLetter(OutboxOp op, PlaylistMutationFailure kind, string reason)
    {
        SettleCompletion(op.Id, kind, DeadLetterMessage(op.Type, kind));
        if (op.Type != "create") return;

        List<OutboxOp> siblings;
        lock (_gate)
            siblings = _outbox.Values
                .Where(o => string.Equals(o.EntityKey, op.EntityKey, StringComparison.Ordinal))
                .ToList();
        foreach (var sibling in siblings)
        {
            bool removed;
            var key = KeyOf(sibling);
            lock (_gate)
            {
                removed = _outbox.TryGetValue(key, out var cur) && cur.Id == sibling.Id;
                if (removed)
                {
                    _outbox.Remove(key);
                    DeadLetter.Add(sibling);
                    _nextAttemptAt.Remove(sibling.Id);
                    _editSnapshots.Remove(sibling.Id);
                }
            }
            if (!removed) continue;
            _durable?.Remove(sibling.Id);
            _durable?.DeadLetter(sibling, "create-failed:" + reason);
            SettleCompletion(sibling.Id, kind, DeadLetterMessage(sibling.Type, kind));
        }
        // The create's own Rollback already removed the rootlist entry / saved pill / membership, so there is nothing
        // left for the siblings to undo.
        if (siblings.Count > 0) NotifyPending(op.EntityKey);
    }

    static string DeadLetterMessage(string type, PlaylistMutationFailure kind) => type == "create"
        ? "That playlist could not be created."
        : kind switch
        {
            PlaylistMutationFailure.Deleted => "That playlist no longer exists.",
            PlaylistMutationFailure.Forbidden => "You no longer have permission to edit that playlist.",
            PlaylistMutationFailure.Conflict => "That playlist changed while your edit was saving.",
            _ => "That change could not be saved.",
        };


    /// <summary>Reconnect drain: replay each op; on success reconcile (Confirmed); on terminal failure rollback + dead-letter.</summary>
    public async Task Drain(ITransport t, SessionContext ctx, CancellationToken ct = default)
    {
        List<OutboxOp> ops;
        lock (_gate) ops = _outbox.Values.OrderBy(o => o.Id).ToList();   // monotonic id = the drain tiebreak

        // ORDERED PER ENTITY. An op that did not land (retryable failure, backoff, terminal) BLOCKS every later op on
        // the same entity in this pass. The create recipe depends on it — a rootlist ADD for a playlist the server has
        // not created yet is an orphan row, and seed tracks posted before the create are a 404 storm.
        var blocked = new HashSet<string>(StringComparer.Ordinal);

        foreach (var op in ops)
        {
            if (op.EntityKey.Length > 0 && blocked.Contains(op.EntityKey)) continue;
            var s = _strategies[op.Type];
            var key = KeyOf(op);

            // §8.3 backoff: skip an op whose scheduled next-attempt time hasn't arrived — it stays Pending; the loop's
            // post-drain reschedule (§6.3.4) guarantees it's re-visited. A rage-click can't burn all 10 attempts in a burst.
            bool waiting;
            lock (_gate) waiting = _nextAttemptAt.TryGetValue(op.Id, out var due) && _now() < due;
            if (waiting) { blocked.Add(op.EntityKey); continue; }

            bool ok;
            try { ok = await s.Replay(op, t, ctx, ct).ConfigureAwait(false); }
            catch (PlaylistMutationException terminal)
            {
                // TERMINAL (the playlist is gone / we are not allowed to touch it). Dead-letter + roll back NOW: burning
                // the remaining 9 attempts would keep the user's edit "pending" for a minute and then fail anyway.
                RollbackOptimistic(op, s);
                DeadLetterTerminal(op, terminal.Kind, "replay-terminal");
                blocked.Add(op.EntityKey);
                continue;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { ok = false; }

            // Reconcile by IDENTITY: a Save that coalesced in during the (awaited) replay replaced this row with a newer
            // intent. We must not remove/clobber that newer op — else the user's latest action is silently lost.
            if (ok)
            {
                bool stillCurrent;
                lock (_gate)
                {
                    stillCurrent = _outbox.TryGetValue(key, out var cur) && cur.Id == op.Id;
                    if (stillCurrent) { _outbox.Remove(key); _editSnapshots.Remove(op.Id); }
                    _nextAttemptAt.Remove(op.Id);   // cleared on success
                }
                if (stillCurrent)
                {
                    _durable?.Remove(op.Id);
                    // "set" (collection saves) + "rootlist" (playlist follows) reconcile the saved pill to Confirmed;
                    // "oprebase" leaves the already-applied membership (the dealer echo / next /diff confirms).
                    if (op.Type == "set" || op.Type == "rootlist") _store.SetSaved(op.SetId, op.EntityKey, op.TargetSaved, SyncState.Confirmed);
                    SettleCompletion(op.Id, null, "");
                    NotifyPending(op.EntityKey);
                }
                // else: a newer Save superseded this op mid-replay → leave it Pending for the next drain.
            }
            else
            {
                blocked.Add(op.EntityKey);
                var bumped = op with { Attempts = op.Attempts + 1 };
                bool deadLetter = false, bumpedDurable = false;
                IReadOnlyList<PlaylistMember>? snapshot = null;
                Playlist? headerSnapshot = null;
                lock (_gate)
                {
                    if (_outbox.TryGetValue(key, out var cur) && cur.Id == op.Id)   // only touch the row if it's still ours
                    {
                        if (bumped.Attempts >= MaxAttempts)
                        {
                            _outbox.Remove(key); DeadLetter.Add(op); deadLetter = true;
                            _nextAttemptAt.Remove(op.Id);
                            if (_editSnapshots.Remove(op.Id, out var snap)) { snapshot = snap.Membership; headerSnapshot = snap.Header; }
                        }
                        else
                        {
                            _outbox[key] = bumped; bumpedDurable = true;
                            // Exponential backoff on the next attempt: min(60s, 1s · 2^attempts).
                            _nextAttemptAt[op.Id] = _now() + TimeSpan.FromSeconds(Math.Min(60d, Math.Pow(2, op.Attempts)));
                        }
                    }
                    // else: a newer Save superseded this op → drop this stale attempt; the newer op drains next.
                }
                if (bumpedDurable) _durable?.Save(bumped);   // persist the attempts bump
                if (deadLetter)   // revert the optimistic write OUTSIDE the lock (cardinal rule)
                {
                    _durable?.Remove(op.Id);
                    _durable?.DeadLetter(op, "max replay attempts exceeded");
                    if (op.Type == "oprebase")
                    {
                        if (snapshot is not null) _store.SetMembership(op.EntityKey, snapshot, op.BaseRev);
                        if (headerSnapshot is not null) _store.UpsertPlaylist(headerSnapshot);
                        lock (_gate) _terminalByEditId[op.Id] = PlaylistMutationFailure.Unknown;
                    }
                    else s.Rollback(op, _store);
                    PlaylistMutationDiagnostics.MutationTerminal(op.EntityKey, PlaylistMutationFailure.Unknown, "max-attempts");
                    AfterDeadLetter(op, PlaylistMutationFailure.Unknown, "max-attempts");
                    NotifyPending(op.EntityKey);
                }
            }
        }
    }

    // Undo one op's optimistic effect on the way to a terminal dead-letter. For a playlist edit that means restoring the
    // pre-edit membership/header snapshot captured at Edit() time; every other type has its own Rollback.
    void RollbackOptimistic(OutboxOp op, IMutationStrategy s)
    {
        if (op.Type != "oprebase") { s.Rollback(op, _store); return; }
        EditSnapshot? snap;
        lock (_gate) _editSnapshots.TryGetValue(op.Id, out snap);
        if (snap is null) return;
        _store.SetMembership(op.EntityKey, snap.Membership, op.BaseRev);
        if (snap.Header is not null) _store.UpsertPlaylist(snap.Header);
    }
}
