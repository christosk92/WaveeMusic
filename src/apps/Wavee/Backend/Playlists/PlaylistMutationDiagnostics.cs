using Wavee.Core;

namespace Wavee.Backend.Playlists;

/// <summary>Structured logging for Spotify playlist permission / rootlist owner mutations.</summary>
static class PlaylistMutationDiagnostics
{
    const string Category = "playlist-mutations";

    public static void PermissionGetFailed(string playlistUri, int status) =>
        WaveeLog.Instance.Warn(Category, "permission.base.get.failed", "GET permission/base failed",
            WaveeLogField.Of("uri", playlistUri), WaveeLogField.Of("status", status));

    public static void PermissionSetFailed(string playlistUri, int status, PlaylistPermissionLevel level) =>
        WaveeLog.Instance.Warn(Category, "permission.base.set.failed", "POST permission/base failed",
            WaveeLogField.Of("uri", playlistUri), WaveeLogField.Of("status", status),
            WaveeLogField.Of("level", level.ToString()));

    public static void PermissionGrantFailed(string playlistUri, int status) =>
        WaveeLog.Instance.Warn(Category, "permission.grant.failed", "POST permission-grant failed",
            WaveeLogField.Of("uri", playlistUri), WaveeLogField.Of("status", status));

    public static void ExtendFailed(string playlistUri, int status) =>
        WaveeLog.Instance.Warn(Category, "playlistextender.extend.failed", "POST playlistextender/extendp failed",
            WaveeLogField.Of("uri", playlistUri), WaveeLogField.Of("status", status));

    public static void PermissionConflict(string playlistUri) =>
        WaveeLog.Instance.Info(Category, "permission.base.conflict", "permission base revision conflict — retrying",
            WaveeLogField.Of("uri", playlistUri));

    public static void RootlistPostFailed(string playlistUri, int status, string op) =>
        WaveeLog.Instance.Warn(Category, "rootlist.changes.failed", "POST rootlist/changes failed",
            WaveeLogField.Of("uri", playlistUri), WaveeLogField.Of("status", status),
            WaveeLogField.Of("op", op));

    /// <summary>A rootlist move was applied to the LOCAL marker stream and acked by the server. The reply carries no
    /// contents, so this is the only record that the tree the user is looking at is the tree the server now holds.</summary>
    public static void RootlistMoveApplied(string source, string placement, long ms) =>
        WaveeLog.Instance.Info(Category, "rootlist.move.applied", "rootlist move applied locally and acked",
            WaveeLogField.Of("source", source), WaveeLogField.Of("placement", placement),
            WaveeLogField.Of("ms", ms));

    /// <summary>The optimistic rootlist rows were put back because the write did not land. Never silent: the row the
    /// user watched move is about to jump back, and this is why.</summary>
    public static void RootlistMoveRolledBack(string reason) =>
        WaveeLog.Instance.Info(Category, "rootlist.move.rolledback", "rootlist rows restored — the write did not land",
            WaveeLogField.Of("reason", reason));

    public static void RootlistConflict(string playlistUri) =>
        WaveeLog.Instance.Info(Category, "rootlist.revision.conflict", "rootlist revision conflict — rebased",
            WaveeLogField.Of("uri", playlistUri));

    public static void DeleteFailed(string playlistUri, Exception ex) =>
        WaveeLog.Instance.Error(Category, "playlist.delete.failed", "delete playlist failed", ex,
            WaveeLogField.Of("uri", playlistUri));

    // ── inbound dealer accounting (I7: every frame is accounted for — nothing is silently swallowed) ──────────────────

    public static void DealerDrop(string topic, string reason, int payloadLength) =>
        WaveeLog.Instance.Info(Category, "dealer.push.dropped", "dealer push dropped",
            WaveeLogField.Of("topic", topic), WaveeLogField.Of("reason", reason),
            WaveeLogField.Of("bytes", payloadLength));

    public static void RootlistPushApplied(int ops) =>
        WaveeLog.Instance.Info(Category, "rootlist.push.applied", "rootlist push applied in place",
            WaveeLogField.Of("ops", ops));

    public static void RootlistPushGet(string reason) =>
        WaveeLog.Instance.Info(Category, "rootlist.push.get", "rootlist push converged via a full GET",
            WaveeLogField.Of("reason", reason));

    public static void RootlistPushDeduped(string topic) =>
        WaveeLog.Instance.Info(Category, "rootlist.push.deduped", "rootlist head already seen — not enqueued",
            WaveeLogField.Of("topic", topic));

    public static void RootlistRevisionHealed(int length) =>
        WaveeLog.Instance.Warn(Category, "rootlist.revision.healed", "cleared a malformed stored rootlist revision",
            WaveeLogField.Of("bytes", length));

    // ── P1: tombstones, permission pushes, terminal mutations, sync_result folding ─────────────────────────────────────

    public static void PermissionPushApplied(string playlistUri, PlaylistPermissionLevel level, bool isCollaborative) =>
        WaveeLog.Instance.Info(Category, "permission.push.applied", "permission push applied to the resident header",
            WaveeLogField.Of("uri", playlistUri), WaveeLogField.Of("level", level.ToString()),
            WaveeLogField.Of("collaborative", isCollaborative));

    public static void PermissionPushIgnored(string playlistUri, string reason) =>
        WaveeLog.Instance.Info(Category, "permission.push.ignored", "permission push ignored",
            WaveeLogField.Of("uri", playlistUri), WaveeLogField.Of("reason", reason));

    public static void PlaylistTombstoned(string playlistUri, string source) =>
        WaveeLog.Instance.Info(Category, "playlist.tombstoned", "playlist deleted by its owner — evicted locally",
            WaveeLogField.Of("uri", playlistUri), WaveeLogField.Of("source", source));

    public static void MutationTerminal(string entityKey, PlaylistMutationFailure kind, string reason) =>
        WaveeLog.Instance.Warn(Category, "mutation.terminal", "mutation failed terminally — dead-lettered",
            WaveeLogField.Of("uri", entityKey), WaveeLogField.Of("kind", kind.ToString()),
            WaveeLogField.Of("reason", reason));

    public static void SyncResultApplied(string playlistUri, int ops) =>
        WaveeLog.Instance.Info(Category, "changes.syncresult.applied", "applied the /changes sync_result ops",
            WaveeLogField.Of("uri", playlistUri), WaveeLogField.Of("ops", ops));

    public static void SyncResultTorn(string playlistUri, string reason) =>
        WaveeLog.Instance.Info(Category, "changes.syncresult.torn", "could not fold the /changes response — revalidating",
            WaveeLogField.Of("uri", playlistUri), WaveeLogField.Of("reason", reason));

    public static void ReapplyPending(string playlistUri, int ops) =>
        WaveeLog.Instance.Info(Category, "mutation.reapply.pending", "re-applied pending ops onto a fresh snapshot",
            WaveeLogField.Of("uri", playlistUri), WaveeLogField.Of("ops", ops));

    // ── P2: I5 — an index op replayed against a base that moved under it ───────────────────────────────────────────────

    public static void OpsRebased(string playlistUri, int ops) =>
        WaveeLog.Instance.Info(Category, "mutation.ops.rebased", "re-expressed index ops against the current membership",
            WaveeLogField.Of("uri", playlistUri), WaveeLogField.Of("ops", ops));

    // ── P3: create-via-/changes + rootlist folder CRUD ────────────────────────────────────────────────────────────────

    public static void CreateQueued(string playlistUri, string name) =>
        WaveeLog.Instance.Info(Category, "playlist.create.queued", "playlist created optimistically — create queued",
            WaveeLogField.Of("uri", playlistUri), WaveeLogField.Of("name", name));

    public static void CreateAcked(string playlistUri) =>
        WaveeLog.Instance.Info(Category, "playlist.create.acked", "the server accepted the create",
            WaveeLogField.Of("uri", playlistUri));

    public static void CreateFailed(string playlistUri, string reason) =>
        WaveeLog.Instance.Warn(Category, "playlist.create.failed", "create rejected — rolling the optimistic row back",
            WaveeLogField.Of("uri", playlistUri), WaveeLogField.Of("reason", reason));

    public static void FolderCreated(string groupId, string name) =>
        WaveeLog.Instance.Info(Category, "rootlist.folder.created", "folder marker pair added to the rootlist",
            WaveeLogField.Of("group", groupId), WaveeLogField.Of("name", name));

    public static void FolderRenamed(string groupId, string name) =>
        WaveeLog.Instance.Info(Category, "rootlist.folder.renamed", "folder start marker replaced with the new name",
            WaveeLogField.Of("group", groupId), WaveeLogField.Of("name", name));

    public static void FolderDeleted(string groupId) =>
        WaveeLog.Instance.Info(Category, "rootlist.folder.deleted", "folder markers removed — children moved up a level",
            WaveeLogField.Of("group", groupId));

    /// <summary>A folder rename could not find the marker's original ADD timestamp even after a bootstrap GET, so the
    /// write carries "now" instead. Never silent: the rootlist keeps a wrong ordering fact when this fires.</summary>
    public static void RootlistTimestampMissing(string groupId) =>
        WaveeLog.Instance.Warn(Category, "rootlist.timestamp.missing", "no original timestamp for a folder marker — using now",
            WaveeLogField.Of("group", groupId));

    /// <summary>A queued rootlist ADD named a folder that no longer exists; the row lands at the top level instead.</summary>
    public static void RootlistPlacementLost(string playlistUri, string groupId) =>
        WaveeLog.Instance.Info(Category, "rootlist.placement.lost", "target folder is gone — adding at the top level",
            WaveeLogField.Of("uri", playlistUri), WaveeLogField.Of("group", groupId));


    public static void RootlistBadRevision(int length, string source) =>
        WaveeLog.Instance.Warn(Category, "playlist.revision.rejected", "refused to store a malformed revision",
            WaveeLogField.Of("bytes", length), WaveeLogField.Of("source", source));
}
