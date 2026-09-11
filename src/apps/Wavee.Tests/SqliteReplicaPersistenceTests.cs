using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Wavee.Backend;
using Wavee.Backend.Persistence;
using Wavee.Backend.Sync;
using Xunit;

namespace Wavee.Tests;

public sealed class SqliteReplicaPersistenceTests
{
    const string PlaylistUri = "spotify:playlist:37i9dQZF1EP6YuccBxUcC1";
    const string TrackUri = "spotify:track:4uLU6hMCjMI75M1A2tKUQC";
    static readonly ReplicaScope Alice = new("alice", 1);
    static CancellationToken Ct => TestContext.Current.CancellationToken;
    static ReplicaTransaction Change(ImmutableArray<PlaylistReplicaBaseline> playlists = default,
        RootlistReplicaBaseline? root = null, ImmutableArray<CollectionReplicaBaseline> collections = default,
        ImmutableArray<OutboxOp> save = default, ImmutableArray<long> remove = default, ReplicaScope? scope = null)
        => new(scope ?? Alice, playlists.IsDefault ? [] : playlists, root,
            collections.IsDefault ? [] : collections, save.IsDefault ? [] : save,
            remove.IsDefault ? [] : remove, [], []);

    [Fact]
    public async Task ConfirmedMembershipAndIntentTransitionSurviveRestartTogether()
    {
        using var database = new Database();
        var head = new byte[24]; head[0] = 9;
        using (var cold = database.Open())
            await cold.CommitAsync(Change([new(PlaylistUri, [new("occurrence", TrackUri, "alice", 123)], head, null)
                { OrderRevision = 6 }], save: [new(7, "oprebase", PlaylistUri, "", false, 7, 0,
                    OwnerAccount: "alice", State: ReplicaIntentState.AwaitingVerification,
                    AcknowledgedRevision: head, CreatedAtMs: 123456)]), Ct);
        using (var cold = database.Open())
        {
            var state = await cold.LoadAsync(Alice, Ct);
            Assert.Empty(state.Playlists);
            var playlist = Assert.IsType<PlaylistReplicaBaseline>(await cold.LoadPlaylistAsync(Alice, PlaylistUri, Ct));
            Assert.Equal("occurrence", Assert.Single(playlist.Members).ItemId);
            Assert.Equal(6, playlist.OrderRevision);
            Assert.Null(playlist.Header);
            var intent = Assert.Single(state.Intents);
            Assert.Equal("alice", intent.OwnerAccount);
            Assert.Equal(ReplicaIntentState.AwaitingVerification, intent.State);
            Assert.Equal(head, intent.AcknowledgedRevision);
            Assert.Equal(123456, intent.CreatedAtMs);
        }
    }

    [Fact]
    public async Task FailureRollsBackMembershipAndOutbox()
    {
        using var database = new Database(); using var cold = database.Open();
        await cold.CommitAsync(Change([new(PlaylistUri, [new("original", TrackUri, null, 0)], [1], null)]), Ct);
        await Assert.ThrowsAsync<SqliteException>(async () => await cold.CommitAsync(
            Change([new(PlaylistUri, [], [2], null)], collections: [new("tracks", [new(TrackUri, 1), new(TrackUri, 2)], "invalid")],
                save: [new(8, "set", TrackUri, "tracks", true, 8, 0, OwnerAccount: "alice")]), Ct));
        Assert.Equal("original", Assert.Single(Assert.IsType<PlaylistReplicaBaseline>(await cold.LoadPlaylistAsync(Alice, PlaylistUri, Ct)).Members).ItemId);
        Assert.Empty((await cold.LoadAsync(Alice, Ct)).Intents);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AccountsAndStorageProfilesHaveIndependentRowsHeadsAndIntents(bool sqlite)
    {
        using var database = new Database(); using var cold = sqlite ? database.Open() : null;
        IReplicaPersistence persistence = cold is null ? new MemoryDataPersistence() : cold;
        var scopes = new[] { Alice, new ReplicaScope("bob", 1), new ReplicaScope("alice", 1, "second-profile") };
        for (int i = 0; i < scopes.Length; i++)
            await persistence.CommitAsync(Change([new(PlaylistUri, [new("occurrence-" + i, TrackUri, null, i)], [(byte)i], null)],
                root: new([new(0, 0, PlaylistUri, null, 0, i)], [(byte)i]), collections: [new("tracks", [new(TrackUri, i)], "head-" + i)],
                save: [new(i + 1, "set", TrackUri, "tracks", true, i + 1, 0, OwnerAccount: scopes[i].Account,
                    StorageAccount: scopes[i].StorageAccount)], scope: scopes[i]), Ct);
        for (int i = 0; i < scopes.Length; i++)
        {
            var boot = await persistence.LoadAsync(scopes[i], Ct);
            Assert.Empty(boot.Playlists);
            var playlist = Assert.IsType<PlaylistReplicaBaseline>(await persistence.LoadPlaylistAsync(scopes[i], PlaylistUri, Ct));
            Assert.Equal("occurrence-" + i, Assert.Single(playlist.Members).ItemId);
            Assert.Equal(new byte[] { (byte)i }, playlist.Revision);
            Assert.Equal(new byte[] { (byte)i }, boot.Rootlist.Revision);
            Assert.Equal(i, Assert.Single(boot.Rootlist.Entries).AddedAtMs);
            Assert.Equal("head-" + i, Assert.Single(boot.Collections).WireRevision);
            Assert.Equal(i + 1, Assert.Single(boot.Intents).Id);
        }
        foreach (var scope in new[] { new ReplicaScope(null, 2), new ReplicaScope("charlie", 2), new ReplicaScope("alice", 1, "unknown-profile") })
        {
            var boot = await persistence.LoadAsync(scope, Ct);
            Assert.Empty(boot.Playlists); Assert.Empty(boot.Rootlist.Entries); Assert.Empty(boot.Collections); Assert.Empty(boot.Intents);
            Assert.Null(await persistence.LoadPlaylistAsync(scope, PlaylistUri, Ct));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task KnownEmptyRowsAndDuplicateOccurrencesSurviveLazyReads(bool sqlite)
    {
        using var database = new Database(); using var cold = sqlite ? database.Open() : null;
        IReplicaPersistence persistence = cold is null ? new MemoryDataPersistence() : cold;
        await persistence.CommitAsync(Change([new(PlaylistUri,
            [new("first", TrackUri, null, 0), new("second", TrackUri, null, 0)], [1], null)],
            root: new([], [2]), collections: [new("tracks", [], "empty-head")]), Ct);
        var playlist = Assert.IsType<PlaylistReplicaBaseline>(await persistence.LoadPlaylistAsync(Alice, PlaylistUri, Ct));
        Assert.Equal(2, playlist.Members.Length); Assert.Equal("first", playlist.Members[0].ItemId); Assert.Equal("second", playlist.Members[1].ItemId);
        await persistence.CommitAsync(Change([new(PlaylistUri, [], [3], null)]), Ct);
        playlist = Assert.IsType<PlaylistReplicaBaseline>(await persistence.LoadPlaylistAsync(Alice, PlaylistUri, Ct));
        Assert.Empty(playlist.Members); Assert.Equal(ReplicaBaselineState.Verified, playlist.State);
        var boot = await persistence.LoadAsync(Alice, Ct);
        Assert.Equal(ReplicaBaselineState.Verified, boot.Rootlist.State);
        Assert.Equal("empty-head", Assert.Single(boot.Collections).WireRevision); Assert.Empty(Assert.Single(boot.Collections).Items);
    }

    [Fact]
    public async Task RemovedIntentIdIsNotReusedAfterRestartOrAccountChange()
    {
        using var database = new Database();
        using (var cold = database.Open())
        {
            await cold.CommitAsync(Change(save: [new(82, "set", TrackUri, "tracks", true, 82, 0, OwnerAccount: "alice")]), Ct);
            await cold.CommitAsync(Change(remove: [82]), Ct);
        }
        using (var cold = database.Open()) Assert.Equal(82, (await cold.LoadAsync(new("bob", 2), Ct)).LastIntentId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CrossAccountIntentCollisionCannotPublishOrOverwriteAnEdit(bool sqlite)
    {
        using var database = new Database(); using var cold = sqlite ? database.Open() : null;
        IReplicaPersistence persistence = cold is null ? new MemoryDataPersistence() : cold;
        await persistence.CommitAsync(Change(save: [new(17, "set", TrackUri, "tracks", true, 17, 0, OwnerAccount: "alice")]), Ct);
        var bob = new ReplicaScope("bob", 1);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await persistence.CommitAsync(
            Change([new(PlaylistUri, [], [1], null)], save: [new(17, "set", TrackUri, "tracks", false, 17, 0, OwnerAccount: "bob")], scope: bob), Ct));
        Assert.Null(await persistence.LoadPlaylistAsync(bob, PlaylistUri, Ct));
        Assert.True(Assert.Single((await persistence.LoadAsync(Alice, Ct)).Intents).TargetSaved);
    }

    [Fact]
    public async Task AnOlderSchemaIsDroppedAndRebuilt()
    {
        using var database = new Database();
        database.Execute("CREATE TABLE meta(key TEXT PRIMARY KEY,value TEXT); INSERT INTO meta VALUES('schema_version','8');" +
            "CREATE TABLE playlists(uri TEXT PRIMARY KEY,base_rev BLOB); INSERT INTO playlists VALUES('" + PlaylistUri + "',X'01');" +
            "CREATE TABLE playlist_items(playlist_uri TEXT,position INTEGER,item_id TEXT,item_uri TEXT,added_by TEXT,added_at INTEGER);" +
            "INSERT INTO playlist_items VALUES('" + PlaylistUri + "',0,'legacy-occurrence','" + TrackUri + "',NULL,0);" +
            "CREATE TABLE outbox(id INTEGER PRIMARY KEY,type TEXT,entity_key TEXT); INSERT INTO outbox VALUES(44,'oprebase','" + PlaylistUri + "');");
        using var cold = database.Open();
        var boot = await cold.LoadAsync(Alice, Ct);
        Assert.Empty(boot.Playlists); Assert.Empty(boot.Intents); Assert.Equal(0, boot.LastIntentId);
        Assert.Null(await cold.LoadPlaylistAsync(Alice, PlaylistUri, Ct));
        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name='playlists';"));
        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM outbox;"));
        Assert.Equal("12", database.Scalar("SELECT value FROM meta WHERE key='schema_version';"));
        Assert.Equal(8, cold.ResetFromSchema);
    }

    sealed class Database : IDisposable
    {
        readonly string _path = Path.Combine(Path.GetTempPath(), "wavee-replica-" + Guid.NewGuid().ToString("N") + ".db");
        public SqliteColdStore Open() => new(_path, SqliteColdStore.DefaultAccount, "en");
        public void Execute(string sql) { using var c = Connect(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
        public object? Scalar(string sql) { using var c = Connect(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; return cmd.ExecuteScalar(); }
        SqliteConnection Connect() { var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString()); c.Open(); return c; }
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { _path, _path + "-wal", _path + "-shm" }) File.Delete(path);
        }
    }
}
