using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Wavee.Backend.Playlists;
using Wavee.Backend.Sync;
using Wavee.Core;

namespace Wavee.Backend.Persistence;

public sealed partial class SqliteColdStore
{
    void EnsureReplicaSchema()
    {
        lock (_connLock)
        {
            ExecLocked("CREATE TABLE IF NOT EXISTS replica_state(storage_account TEXT NOT NULL,owner_account TEXT NOT NULL," +
                "kind TEXT NOT NULL,id TEXT NOT NULL,baseline_state INTEGER NOT NULL,version INTEGER NOT NULL," +
                "order_revision INTEGER NOT NULL DEFAULT 0,revision BLOB,wire_revision TEXT," +
                "PRIMARY KEY(storage_account,owner_account,kind,id)) WITHOUT ROWID;" +
                "CREATE TABLE IF NOT EXISTS replica_playlist_item(storage_account TEXT NOT NULL,owner_account TEXT NOT NULL," +
                "playlist_uri TEXT NOT NULL,position INTEGER NOT NULL,item_id TEXT NOT NULL,item_uri TEXT NOT NULL,added_by TEXT,added_at INTEGER NOT NULL," +
                "chart_status INTEGER NOT NULL DEFAULT 0,chart_current_pos INTEGER NOT NULL DEFAULT 0,chart_previous_pos INTEGER NOT NULL DEFAULT 0," +
                "chart_rank INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(storage_account,owner_account,playlist_uri,position)) WITHOUT ROWID;" +
                "CREATE TABLE IF NOT EXISTS replica_rootlist_entry(storage_account TEXT NOT NULL,owner_account TEXT NOT NULL," +
                "position INTEGER NOT NULL,kind INTEGER NOT NULL,uri TEXT NOT NULL,group_name TEXT,depth INTEGER NOT NULL,added_at INTEGER NOT NULL," +
                "PRIMARY KEY(storage_account,owner_account,position)) WITHOUT ROWID;" +
                "CREATE TABLE IF NOT EXISTS replica_collection_item(storage_account TEXT NOT NULL,owner_account TEXT NOT NULL," +
                "set_id TEXT NOT NULL,item_uri TEXT NOT NULL,added_at INTEGER NOT NULL," +
                "PRIMARY KEY(storage_account,owner_account,set_id,item_uri)) WITHOUT ROWID;" +
                "CREATE TABLE IF NOT EXISTS replica_recovery(storage_account TEXT NOT NULL,owner_account TEXT NOT NULL DEFAULT ''," +
                "kind TEXT NOT NULL,id TEXT NOT NULL,payload BLOB,source_table TEXT," +
                "PRIMARY KEY(storage_account,owner_account,kind,id)) WITHOUT ROWID;");
            // A reset drops both tables, and a v12 file already carries every column — no ALTER TABLE needed.
            ExecLocked("CREATE TABLE IF NOT EXISTS outbox(id INTEGER PRIMARY KEY,type TEXT NOT NULL,entity_key TEXT NOT NULL," +
                "set_id TEXT,target_saved INTEGER,op BLOB,base_rev BLOB,attempts INTEGER NOT NULL DEFAULT 0,parent_folder TEXT," +
                "storage_account TEXT NOT NULL DEFAULT 'default',owner_account TEXT,intent_state INTEGER NOT NULL DEFAULT 0," +
                "acknowledged_revision BLOB,created_at_ms INTEGER NOT NULL DEFAULT 0);" +
                "CREATE TABLE IF NOT EXISTS dead_letter(id INTEGER PRIMARY KEY,type TEXT,entity_key TEXT,reason TEXT,created_at INTEGER," +
                "storage_account TEXT NOT NULL DEFAULT 'default',owner_account TEXT);" +
                "CREATE INDEX IF NOT EXISTS ix_outbox_scoped_owner ON outbox(storage_account,owner_account,intent_state,id);");
        }
    }

    public async ValueTask<ReplicaBootstrap> LoadAsync(ReplicaScope scope, CancellationToken ct)
    {
        return await Task.Run(() => LoadReplica(scope, ct), ct).ConfigureAwait(false);
    }
    ReplicaBootstrap LoadReplica(ReplicaScope scope, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_readLock)
        {
            using var tx = _read.BeginTransaction(deferred: true);
            long lastId;
            using (var max = ReplicaReadCommand(tx, "SELECT MAX(id) FROM (SELECT COALESCE(MAX(id),0) id FROM outbox UNION ALL SELECT COALESCE(CAST(value AS INTEGER),0) FROM meta WHERE key='replica_last_intent_id');")) lastId = (long)max.ExecuteScalar()!;
            if (string.IsNullOrEmpty(scope.Account)) return ReplicaBootstrap.Empty with { LastIntentId = lastId };
            var rootState = ReadReplicaStateLocked(scope, tx, "rootlist", "rootlist");
            var rootRows = ImmutableArray.CreateBuilder<RootlistEntry>();
            using (var command = ScopedRead(scope, tx, "SELECT position,kind,uri,group_name,depth,added_at FROM replica_rootlist_entry WHERE storage_account=$s AND owner_account=$o ORDER BY position;"))
            using (var rows = command.ExecuteReader())
                while (rows.Read()) rootRows.Add(new(rows.GetInt32(0), rows.GetInt32(1), rows.GetString(2), rows.IsDBNull(3) ? null : rows.GetString(3), rows.GetInt32(4), rows.GetInt64(5)));
            var root = new RootlistReplicaBaseline(rootRows.ToImmutable(), rootState.Revision, rootState.State, rootState.Version);
            var collectionRows = new Dictionary<string, ImmutableArray<SavedItem>.Builder>(StringComparer.Ordinal);
            using (var command = ScopedRead(scope, tx, "SELECT set_id,item_uri,added_at FROM replica_collection_item WHERE storage_account=$s AND owner_account=$o ORDER BY set_id,item_uri;"))
            using (var rows = command.ExecuteReader())
                while (rows.Read())
                {
                    string set = rows.GetString(0);
                    if (!collectionRows.TryGetValue(set, out var items)) collectionRows[set] = items = ImmutableArray.CreateBuilder<SavedItem>();
                    items.Add(new(rows.GetString(1), rows.GetInt64(2)));
                }
            var collections = ImmutableArray.CreateBuilder<CollectionReplicaBaseline>();
            using (var command = ScopedRead(scope, tx, "SELECT id,version,wire_revision,baseline_state FROM replica_state WHERE storage_account=$s AND owner_account=$o AND kind='collection' ORDER BY id;"))
            using (var rows = command.ExecuteReader())
                while (rows.Read()) collections.Add(new(rows.GetString(0), collectionRows.GetValueOrDefault(rows.GetString(0))?.ToImmutable() ?? [],
                    rows.IsDBNull(2) ? null : rows.GetString(2), rows.GetInt64(1))
                    { IsKnown = (ReplicaBaselineState)rows.GetInt32(3) is ReplicaBaselineState.Verified or ReplicaBaselineState.Cached });
            var intents = ImmutableArray.CreateBuilder<OutboxOp>();
            using (var command = ScopedRead(scope, tx, "SELECT id,type,entity_key,set_id,target_saved,op,base_rev,attempts,parent_folder,intent_state,acknowledged_revision,created_at_ms FROM outbox WHERE storage_account=$s AND owner_account=$o ORDER BY id;"))
            using (var rows = command.ExecuteReader())
                while (rows.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    IReadOnlyList<PlaylistOp>? ops = null;
                    byte[]? revision = rows.IsDBNull(6) ? null : rows.GetFieldValue<byte[]>(6);
                    if (!rows.IsDBNull(5)) { var parsed = PlaylistWireMapper.ParseOutboxBlob(rows.GetFieldValue<byte[]>(5)); ops = parsed.Ops; revision ??= parsed.BaseRev; }
                    intents.Add(new(rows.GetInt64(0), rows.GetString(1), rows.GetString(2), rows.IsDBNull(3) ? "" : rows.GetString(3),
                        !rows.IsDBNull(4) && rows.GetInt64(4) != 0, rows.GetInt64(0), rows.GetInt32(7), ops, revision,
                        rows.IsDBNull(8) ? null : rows.GetString(8), scope.Account, (ReplicaIntentState)rows.GetInt32(9),
                        rows.IsDBNull(10) ? null : rows.GetFieldValue<byte[]>(10), rows.GetInt64(11), scope.StorageAccount));
                }
            return new([], root, collections.ToImmutable(), intents.ToImmutable()) { LastIntentId = lastId };
        }
    }

    public async ValueTask<PlaylistReplicaBaseline?> LoadPlaylistAsync(ReplicaScope scope, string uri, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(scope.Account)) return null;
        return await Task.Run(() =>
        {
            lock (_readLock)
            {
                using var tx = _read.BeginTransaction(deferred: true);
                var state = ReadReplicaStateLocked(scope, tx, "playlist", uri);
                if (state.State == ReplicaBaselineState.Missing) return null;
                var members = ImmutableArray.CreateBuilder<PlaylistMember>();
                using var command = ScopedRead(scope, tx, "SELECT item_id,item_uri,added_by,added_at,chart_status,chart_current_pos,chart_previous_pos,chart_rank FROM replica_playlist_item WHERE storage_account=$s AND owner_account=$o AND playlist_uri=$u ORDER BY position;", ("$u", uri));
                using var rows = command.ExecuteReader();
                while (rows.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    ChartEntry? chart = rows.GetInt32(4) == 0 && rows.GetInt32(5) == 0 && rows.GetInt32(6) == 0 && rows.GetInt64(7) == 0 ? null
                        : new ChartEntry((ChartEntryStatus)rows.GetInt32(4), rows.GetInt32(5), rows.GetInt32(6), rows.GetInt64(7));
                    members.Add(new(rows.GetString(0), rows.GetString(1), rows.IsDBNull(2) ? null : rows.GetString(2), rows.GetInt64(3), chart));
                }
                return new PlaylistReplicaBaseline(uri, members.ToImmutable(), state.Revision, null, state.State, state.Version) { OrderRevision = state.OrderRevision };
            }
        }, ct).ConfigureAwait(false);
    }
    (ReplicaBaselineState State, long Version, long OrderRevision, byte[]? Revision) ReadReplicaStateLocked(ReplicaScope scope, SqliteTransaction tx, string kind, string id)
    {
        using var command = ScopedRead(scope, tx, "SELECT baseline_state,version,order_revision,revision FROM replica_state WHERE storage_account=$s AND owner_account=$o AND kind=$k AND id=$i;", ("$k", kind), ("$i", id));
        using var row = command.ExecuteReader();
        return row.Read() ? ((ReplicaBaselineState)row.GetInt32(0), row.GetInt64(1), row.GetInt64(2), row.IsDBNull(3) ? null : row.GetFieldValue<byte[]>(3))
            : (ReplicaBaselineState.Missing, 0, 0, null);
    }

    public ValueTask CommitAsync(ReplicaTransaction change, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        bool hasWrites = !change.Playlists.IsEmpty || change.Rootlist is not null || !change.Collections.IsEmpty || !change.SaveIntents.IsEmpty || !change.RemoveIntents.IsEmpty;
        if (hasWrites && string.IsNullOrEmpty(change.Scope.Account)) throw new InvalidOperationException("Replica writes require an authenticated owner.");
        lock (_connLock)
        {
            using var tx = _conn.BeginTransaction();
            var scope = change.Scope;
            if (change.Catalog is { } catalog) WriteCatalogCommitLocked(catalog, tx, ct);
            foreach (var playlist in change.Playlists)
            {
                if (playlist.State == ReplicaBaselineState.RecoveryOnly) throw new InvalidOperationException("Recovery evidence is not a confirmed baseline.");
                ExecuteReplicaLocked(tx, "DELETE FROM replica_playlist_item WHERE storage_account=$s AND owner_account=$o AND playlist_uri=$u;", ("$s", scope.StorageAccount), ("$o", scope.Account), ("$u", playlist.Uri));
                using var insert = ReplicaCommand(tx, "INSERT INTO replica_playlist_item VALUES($s,$o,$u,$p,$i,$uri,$by,$at,$cs,$cc,$cp,$cr);",
                    ("$s", scope.StorageAccount), ("$o", scope.Account), ("$u", playlist.Uri), ("$p", 0), ("$i", ""), ("$uri", ""), ("$by", null), ("$at", 0L), ("$cs", 0), ("$cc", 0), ("$cp", 0), ("$cr", 0L));
                for (int i = 0; i < playlist.Members.Length; i++)
                {
                    ct.ThrowIfCancellationRequested(); var row = playlist.Members[i];
                    Set(insert, "$p", i); Set(insert, "$i", row.ItemId); Set(insert, "$uri", row.ItemUri); Set(insert, "$by", row.AddedBy); Set(insert, "$at", row.AddedAt);
                    Set(insert, "$cs", (int)(row.Chart?.Status ?? 0)); Set(insert, "$cc", row.Chart?.CurrentPos ?? 0); Set(insert, "$cp", row.Chart?.PreviousPos ?? 0); Set(insert, "$cr", row.Chart?.Rank ?? 0);
                    insert.ExecuteNonQuery();
                }
                SaveReplicaStateLocked(tx, scope, "playlist", playlist.Uri, playlist.State, playlist.Version, playlist.OrderRevision, playlist.Revision);
            }
            if (change.Rootlist is { } root)
            {
                ExecuteReplicaLocked(tx, "DELETE FROM replica_rootlist_entry WHERE storage_account=$s AND owner_account=$o;", ("$s", scope.StorageAccount), ("$o", scope.Account));
                using var insert = ReplicaCommand(tx, "INSERT INTO replica_rootlist_entry VALUES($s,$o,$p,$k,$u,$g,$d,$at);", ("$s", scope.StorageAccount), ("$o", scope.Account), ("$p", 0), ("$k", 0), ("$u", ""), ("$g", null), ("$d", 0), ("$at", 0L));
                for (int i = 0; i < root.Entries.Length; i++)
                {
                    var row = root.Entries[i]; Set(insert, "$p", i); Set(insert, "$k", row.Kind); Set(insert, "$u", row.Uri); Set(insert, "$g", row.GroupName); Set(insert, "$d", row.Depth); Set(insert, "$at", row.AddedAtMs); insert.ExecuteNonQuery();
                }
                SaveReplicaStateLocked(tx, scope, "rootlist", "rootlist", root.State, root.Version, root.Version, root.Revision);
            }
            foreach (var collection in change.Collections)
            {
                ExecuteReplicaLocked(tx, "DELETE FROM replica_collection_item WHERE storage_account=$s AND owner_account=$o AND set_id=$id;", ("$s", scope.StorageAccount), ("$o", scope.Account), ("$id", collection.SetId));
                using var insert = ReplicaCommand(tx, "INSERT INTO replica_collection_item VALUES($s,$o,$id,$u,$at);", ("$s", scope.StorageAccount), ("$o", scope.Account), ("$id", collection.SetId), ("$u", ""), ("$at", 0L));
                foreach (var row in collection.Items) { Set(insert, "$u", row.Uri); Set(insert, "$at", row.AddedAtMs); insert.ExecuteNonQuery(); }
                SaveReplicaStateLocked(tx, scope, "collection", collection.SetId, collection.IsKnown ? ReplicaBaselineState.Verified : ReplicaBaselineState.Missing,
                    collection.Version, collection.Version, null, collection.WireRevision);
            }
            foreach (var intent in change.SaveIntents)
            {
                if (intent.OwnerAccount != scope.Account || intent.StorageAccount != scope.StorageAccount) throw new InvalidOperationException("Intent account mismatch.");
                SaveReplicaIntentLocked(intent, tx);
            }
            foreach (var id in change.RemoveIntents) ExecuteReplicaLocked(tx, "DELETE FROM outbox WHERE id=$i AND storage_account=$s AND owner_account=$o;", ("$i", id), ("$s", scope.StorageAccount), ("$o", scope.Account));
            foreach (var dead in change.DeadLetters) ExecuteReplicaLocked(tx, "INSERT OR REPLACE INTO dead_letter(id,type,entity_key,reason,created_at,storage_account,owner_account) VALUES($i,$t,$e,$r,$at,$s,$o);", ("$i", dead.Intent.Id), ("$t", dead.Intent.Type), ("$e", dead.Intent.EntityKey), ("$r", dead.Reason), ("$at", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ("$s", scope.StorageAccount), ("$o", scope.Account));
            foreach (var uri in change.RemoveRecovery) ExecuteReplicaLocked(tx, "DELETE FROM replica_recovery WHERE storage_account=$s AND owner_account=$o AND kind='playlist' AND id=$i;", ("$s", scope.StorageAccount), ("$o", scope.Account), ("$i", uri));
            tx.Commit();
        }
        return ValueTask.CompletedTask;
    }
    static void Set(SqliteCommand command, string name, object? value) => command.Parameters[name].Value = value ?? DBNull.Value;
    void SaveReplicaStateLocked(SqliteTransaction tx, ReplicaScope scope, string kind, string id, ReplicaBaselineState state, long version, long orderRevision, byte[]? revision, string? wireRevision = null)
        => ExecuteReplicaLocked(tx, "INSERT INTO replica_state VALUES($s,$o,$k,$i,$state,$v,$order,$r,$wire) ON CONFLICT(storage_account,owner_account,kind,id) DO UPDATE SET baseline_state=excluded.baseline_state,version=excluded.version,order_revision=excluded.order_revision,revision=excluded.revision,wire_revision=excluded.wire_revision;",
            ("$s", scope.StorageAccount), ("$o", scope.Account), ("$k", kind), ("$i", id), ("$state", (int)state), ("$v", version), ("$order", orderRevision), ("$r", revision), ("$wire", wireRevision));
    void SaveReplicaIntentLocked(OutboxOp op, SqliteTransaction? tx)
    {
        byte[]? blob = op.Ops is null ? null : PlaylistWireMapper.BuildOutboxBlob(op.BaseRev, op.Ops);
        using var command = ReplicaCommand(tx, "INSERT INTO outbox(id,type,entity_key,set_id,target_saved,op,base_rev,attempts,parent_folder,storage_account,owner_account,intent_state,acknowledged_revision,created_at_ms) VALUES($i,$t,$e,$set,$target,$op,$rev,$tries,$folder,$storage,$owner,$state,$ack,$created) ON CONFLICT(id) DO UPDATE SET target_saved=excluded.target_saved,op=excluded.op,base_rev=excluded.base_rev,attempts=excluded.attempts,parent_folder=excluded.parent_folder,intent_state=excluded.intent_state,acknowledged_revision=excluded.acknowledged_revision,created_at_ms=excluded.created_at_ms WHERE outbox.storage_account=excluded.storage_account AND outbox.owner_account IS excluded.owner_account;",
            ("$i", op.Id), ("$t", op.Type), ("$e", op.EntityKey), ("$set", op.SetId), ("$target", op.TargetSaved ? 1 : 0), ("$op", blob), ("$rev", op.BaseRev), ("$tries", op.Attempts), ("$folder", op.ParentFolderId), ("$storage", op.StorageAccount), ("$owner", op.OwnerAccount), ("$state", (int)op.State), ("$ack", op.AcknowledgedRevision), ("$created", op.CreatedAtMs));
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("This intent ID already belongs to another account.");
        ExecuteReplicaLocked(tx, "INSERT INTO meta(key,value) VALUES('replica_last_intent_id',$id) ON CONFLICT(key) DO UPDATE SET value=MAX(CAST(meta.value AS INTEGER),CAST(excluded.value AS INTEGER));", ("$id", op.Id));
    }
    SqliteCommand ReplicaReadCommand(SqliteTransaction tx, string sql, params (string Name, object? Value)[] args)
    {
        var command = _read.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        foreach (var arg in args) command.Parameters.AddWithValue(arg.Name, arg.Value ?? DBNull.Value);
        return command;
    }
    SqliteCommand ScopedRead(ReplicaScope scope, SqliteTransaction tx, string sql, params (string Name, object? Value)[] args)
        => ReplicaReadCommand(tx, sql, [("$s", scope.StorageAccount), ("$o", scope.Account), .. args]);
    SqliteCommand ReplicaCommand(SqliteTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        var command = _conn.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        foreach (var arg in args) command.Parameters.AddWithValue(arg.Name, arg.Value ?? DBNull.Value);
        return command;
    }
    void ExecuteReplicaLocked(SqliteTransaction? tx, string sql, params (string Name, object? Value)[] args)
    { using var command = ReplicaCommand(tx, sql, args); command.ExecuteNonQuery(); }
}
