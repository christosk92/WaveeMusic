using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Catalog;
using Wavee.Backend.Collections;
using Wavee.Backend.Playlists;
using Wavee.Backend.Sync;
using Wavee.Core;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class LibraryReplicaCoordinatorTests
{
    const string Uri = "spotify:playlist:replica";
    static readonly ReplicaScope Scope = new("alice", 1);

    [Fact]
    public async Task Local_intent_becomes_visible_only_after_durable_insert()
    {
        await using var queue = new DataCommitQueue();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disk = new MemoryPersistence { BeforeCommit = async _ => { entered.SetResult(); await release.Task; } };
        var sink = new Sink();
        var replica = New(queue, disk, sink);
        var pending = replica.StageAsync("set", "spotify:track:a", "liked", true);
        await entered.Task;
        Assert.Empty(replica.Intents);
        Assert.Empty(replica.ReadCollection("liked").Items);
        Assert.Empty(sink.Publications);
        release.SetResult();
        var intent = await pending;
        Assert.Single(replica.ReadCollection("liked").Items);
        Assert.Empty(replica.ReadConfirmedCollection("liked").Items);
        var transaction = Assert.Single(disk.Transactions);
        Assert.Equal(intent.Id, Assert.Single(transaction.SaveIntents).Id);
        Assert.Empty(Assert.Single(transaction.Collections).Items);
        Assert.Single(sink.Publications);
    }

    [Fact]
    public async Task Failed_persistence_does_not_publish_an_edit()
    {
        await using var queue = new DataCommitQueue();
        var disk = new MemoryPersistence { BeforeCommit = _ => Task.FromException(new InvalidOperationException("disk")) };
        var sink = new Sink();
        var replica = New(queue, disk, sink);
        await Assert.ThrowsAsync<InvalidOperationException>(() => replica.StageAsync("set", "spotify:track:a", "liked", true));
        Assert.Empty(replica.Intents);
        Assert.Empty(sink.Publications);
    }

    [Fact]
    public async Task Snapshot_rebases_local_overlay_and_preserves_duplicate_uri_occurrences()
    {
        await using var queue = new DataCommitQueue();
        var replica = New(queue);
        var first = Member("first", "same");
        var second = Member("second", "same");
        await replica.AdoptPlaylistAsync(Snapshot([first], 1));
        await replica.StageAsync("oprebase", Uri, Uri, false,
            [new PlaylistOp(PlaylistOpKind.Add, AddLast: true, Items: [second])], Head(1));
        await replica.AdoptPlaylistAsync(Snapshot([Member("foreign", "foreign"), first], 2));
        Assert.Equal(new[] { "foreign", "first", "second" }, replica.ReadPlaylist(Uri).Members.Select(x => x.ItemId));
        Assert.Equal(2, replica.ReadConfirmedPlaylist(Uri).Members.Length);
        Assert.Single(replica.Intents);
    }

    [Fact]
    public async Task Acknowledging_one_attempt_preserves_a_later_edit_and_its_order()
    {
        await using var queue = new DataCommitQueue();
        var replica = New(queue);
        await replica.AdoptPlaylistAsync(Snapshot([], 1));
        var a = Member("a", "same");
        var b = Member("b", "same");
        var first = await replica.StageAsync("oprebase", Uri, Uri, false,
            [new PlaylistOp(PlaylistOpKind.Add, AddLast: true, Items: [a])], Head(1));
        Assert.True(await replica.BeginAttemptAsync(first));
        var later = await replica.StageAsync("oprebase", Uri, Uri, false,
            [new PlaylistOp(PlaylistOpKind.Add, AddLast: true, Items: [b])], Head(1));
        await replica.CompleteAsync(first, Snapshot([a], 2), null, false);
        Assert.Equal(later.Id, Assert.Single(replica.Intents).Id);
        Assert.Equal(new[] { "a", "b" }, replica.ReadPlaylist(Uri).Members.Select(x => x.ItemId));
        Assert.Equal("a", Assert.Single(replica.ReadConfirmedPlaylist(Uri).Members).ItemId);
    }

    [Fact]
    public async Task Inflight_boolean_intent_cannot_be_coalesced_away()
    {
        await using var queue = new DataCommitQueue();
        var replica = New(queue);
        var first = await replica.StageAsync("set", "spotify:track:a", "liked", true);
        Assert.True(await replica.BeginAttemptAsync(first));
        var second = await replica.StageAsync("set", "spotify:track:a", "liked", false);
        await replica.CompleteAsync(first, null, null, false);
        Assert.True(replica.ReadConfirmedCollection("liked").Items.Any());
        Assert.Empty(replica.ReadCollection("liked").Items);
        Assert.Equal(second.Id, Assert.Single(replica.Intents).Id);
    }

    [Fact]
    public async Task Superseded_unsent_intent_cannot_start_a_transport_attempt()
    {
        await using var queue = new DataCommitQueue();
        var replica = New(queue);
        var old = await replica.StageAsync("set", "spotify:track:a", "liked", true);
        var latest = await replica.StageAsync("set", "spotify:track:a", "liked", false);
        Assert.False(await replica.BeginAttemptAsync(old));
        Assert.Equal(latest.Id, Assert.Single(replica.Intents).Id);
    }

    [Fact]
    public async Task Ambiguous_acceptance_survives_unproven_snapshot_and_is_never_replayable()
    {
        await using var queue = new DataCommitQueue();
        var replica = New(queue);
        await replica.AdoptPlaylistAsync(Snapshot([], 1));
        var item = Member("new", "same");
        var intent = await replica.StageAsync("oprebase", Uri, Uri, false,
            [new PlaylistOp(PlaylistOpKind.Add, AddLast: true, Items: [item])], Head(1));
        Assert.True(await replica.BeginAttemptAsync(intent));
        await replica.AdoptPlaylistAsync(Snapshot([], 2));
        var unknown = Assert.Single(replica.Intents);
        Assert.Equal(ReplicaIntentState.AwaitingVerification, unknown.State);
        Assert.False(replica.CanReplay(unknown, "alice"));
        Assert.Equal(item, Assert.Single(replica.ReadPlaylist(Uri).Members));
        await replica.AdoptPlaylistAsync(Snapshot([item], 3));
        Assert.Empty(replica.Intents);
        Assert.Single(replica.ReadPlaylist(Uri).Members);
    }

    [Fact]
    public async Task Verified_empty_collection_updates_baseline_without_erasing_pending_save()
    {
        await using var queue = new DataCommitQueue();
        var replica = New(queue);
        Assert.False(replica.IsCollectionKnown("liked"));
        await replica.StageAsync("set", "spotify:track:a", "liked", true);
        Assert.False(replica.IsCollectionKnown("liked"));
        await replica.AdoptCollectionAsync(new CollectionReadResult("collection", true, true,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null, "token", []));
        Assert.Empty(replica.ReadConfirmedCollection("liked").Items);
        Assert.Single(replica.ReadCollection("liked").Items);
        Assert.Equal("token", replica.ReadConfirmedCollection("liked").WireRevision);
        Assert.True(replica.IsCollectionKnown("liked"));
    }

    [Fact]
    public async Task Catalog_header_is_joined_for_ack_proof_without_a_retained_header_copy()
    {
        await using var queue = new DataCommitQueue();
        var disk = new MemoryPersistence();
        var replica = New(queue, disk);
        var original = new Playlist("replica", Uri, "Original", null, "alice", null, 0);
        await replica.AdoptPlaylistAsync(Snapshot([], 1) with { Header = original });
        Assert.Null(replica.ReadPlaylist(Uri).Header);
        Assert.Null(Assert.Single(disk.Transactions.Last().Playlists).Header);
        var rename = await replica.StageAsync("oprebase", Uri, Uri, false,
            [new PlaylistOp(PlaylistOpKind.UpdateList, ListPatch: new PlaylistListAttributePatch(Name: "Renamed"))]);
        Assert.True(await replica.BeginAttemptAsync(rename));
        await replica.AdoptPlaylistAsync(Snapshot([], 2));
        Assert.Single(replica.Intents);
        Assert.Equal("Original", replica.ReadConfirmedPlaylist(Uri).Header!.Name);
        await replica.AdoptPlaylistAsync(Snapshot([], 3) with { Header = original with { Name = "Renamed" } });
        Assert.Empty(replica.Intents);
        Assert.Equal("Renamed", replica.ReadConfirmedPlaylist(Uri).Header!.Name);
    }

    [Fact]
    public async Task Unknown_owner_recovery_is_quarantined_and_never_reapplies_old_overlay()
    {
        await using var queue = new DataCommitQueue();
        var item = Member("one", "one");
        var old = new OutboxOp(1, "oprebase", Uri, Uri, false, 1, 0,
            [new PlaylistOp(PlaylistOpKind.Add, AddLast: true, Items: [item])]);
        var boot = ReplicaBootstrap.Empty with { Playlists = [new(Uri, [item], null, null, ReplicaBaselineState.RecoveryOnly)], Intents = [old] };
        var replica = New(queue, bootstrap: boot);
        Assert.Empty(replica.ReadPlaylist(Uri).Members);
        Assert.False(replica.CanReplay(old, "alice"));
        await replica.AdoptPlaylistAsync(Snapshot([], 2));
        Assert.Empty(replica.ReadPlaylist(Uri).Members);
        Assert.Empty(replica.Intents);
    }

    [Fact]
    public async Task Stale_generation_response_is_rejected_before_persistence()
    {
        await using var queue = new DataCommitQueue();
        var current = Scope;
        var disk = new MemoryPersistence();
        var replica = new LibraryReplicaCoordinator(queue, disk, new Sink(), () => current, ReplicaBootstrap.Empty, Catalog(queue));
        current = new ReplicaScope("alice", 2);
        await Assert.ThrowsAsync<OperationCanceledException>(() => replica.AdoptPlaylistAsync(Snapshot([], 1), expectedScope: Scope));
        Assert.Empty(disk.Transactions);
        Assert.Equal(ReplicaBaselineState.Missing, replica.ReadConfirmedPlaylist(Uri).State);
    }

    [Fact]
    public async Task Create_and_follow_are_one_durable_recipe_and_reject_together()
    {
        await using var queue = new DataCommitQueue();
        var disk = new MemoryPersistence();
        var replica = New(queue, disk);
        var header = new Playlist("replica", Uri, "New", null, "alice", null, 0, Array.Empty<Track>());
        var create = await replica.StageCreateAsync(header, "folder");
        var transaction = Assert.Single(disk.Transactions);
        Assert.Equal(new[] { "create", "rootlist" }, transaction.SaveIntents.Select(x => x.Type));
        Assert.Equal(2, replica.Intents.Length);
        await replica.RejectAsync(create, PlaylistMutationFailure.Forbidden, "denied");
        Assert.Empty(replica.Intents);
        Assert.Empty(replica.ReadRootlist().Entries);
        Assert.Equal(2, disk.Transactions.Last().RemoveIntents.Length);
    }

    [Fact]
    public async Task Header_change_and_ack_do_not_rebuild_occurrence_order()
    {
        await using var queue = new DataCommitQueue();
        var replica = New(queue);
        await replica.AdoptPlaylistAsync(Snapshot([Member("a", "a")], 1));
        var order = replica.ReadPlaylist(Uri).OrderRevision;
        await replica.AdoptHeaderAsync(Uri, new Playlist("replica", Uri, "Renamed", null, "alice", null, 1));
        Assert.Equal(order, replica.ReadPlaylist(Uri).OrderRevision);
        var intent = await replica.StageAsync("oprebase", Uri, Uri, false,
            [new PlaylistOp(PlaylistOpKind.Add, AddLast: true, Items: [Member("b", "b")])], Head(1));
        var withPending = replica.ReadPlaylist(Uri).OrderRevision;
        Assert.True(withPending > order);
        await replica.BeginAttemptAsync(intent);
        Assert.Equal(withPending, replica.ReadPlaylist(Uri).OrderRevision);
        await replica.CompleteAsync(intent, Snapshot([Member("a", "a"), Member("b", "b")], 2), null, false);
        Assert.Equal(withPending, replica.ReadPlaylist(Uri).OrderRevision);
    }

    [Fact]
    public async Task Definite_conflict_durably_blocks_retry_until_a_fresh_baseline()
    {
        await using var queue = new DataCommitQueue();
        var disk = new MemoryPersistence();
        var replica = New(queue, disk);
        await replica.AdoptPlaylistAsync(Snapshot([], 1));
        var intent = await replica.StageAsync("oprebase", Uri, Uri, false,
            [new PlaylistOp(PlaylistOpKind.Add, AddLast: true, Items: [Member("a", "a")])], Head(1));
        await replica.BeginAttemptAsync(intent);
        await replica.RetryAsync(intent with { Attempts = 1 }, refreshBaseline: true);
        var pending = Assert.Single(replica.Intents);
        Assert.Equal(ReplicaIntentState.Pending, pending.State);
        Assert.False(replica.CanReplay(pending, "alice"));
        var transaction = disk.Transactions.Last();
        Assert.Equal(ReplicaBaselineState.NeedsResync, Assert.Single(transaction.Playlists).State);
        Assert.Equal(ReplicaIntentState.Pending, Assert.Single(transaction.SaveIntents).State);
        await replica.AdoptPlaylistAsync(Snapshot([], 2));
        Assert.True(replica.CanReplay(Assert.Single(replica.Intents), "alice"));
    }

    [Fact]
    public async Task Direct_rootlist_attempt_has_no_replayable_crash_window()
    {
        await using var queue = new DataCommitQueue();
        var disk = new MemoryPersistence();
        var replica = New(queue, disk);
        var intent = await replica.StageAsync("rootlist-edit", "folder", "rootlist", false, [], Head(1),
            initialState: ReplicaIntentState.AwaitingVerification);
        Assert.Equal(ReplicaIntentState.AwaitingVerification, Assert.Single(Assert.Single(disk.Transactions).SaveIntents).State);
        Assert.False(replica.CanReplay(intent, "alice"));
    }

    [Fact]
    public async Task Partial_protocol_header_preserves_newer_unrelated_catalog_fields()
    {
        await using var queue = new DataCommitQueue();
        var catalog = Catalog(queue);
        var replica = new LibraryReplicaCoordinator(queue, new MemoryPersistence(), new Sink(), () => Scope,
            ReplicaBootstrap.Empty, catalog);
        var original = new Playlist("replica", Uri, "Old", "Old description", "alice", null, 1);
        await replica.AdoptHeaderAsync(Uri, original);
        var observation = CatalogObservations.PlaylistHeader(catalog.Scope, original with { Name = "New catalog name" });
        await queue.CommitAsync(async ct =>
        {
            var prepared = await catalog.PrepareObservationsAsync([observation], ct);
            return new DataCommit<int>(_ => ValueTask.CompletedTask, prepared.Publish, 0);
        });
        await replica.ObservePermissionsAsync(Uri, true, "permission-head");
        var header = Assert.IsType<PlaylistHeaderValue>(catalog.Peek(observation.Key).Value);
        Assert.Equal("New catalog name", header.Name);
        Assert.True(header.IsPublic);
    }

    [Fact]
    public async Task Native_source_replacement_preserves_other_sources_and_never_writes_protocol_baselines()
    {
        await using var queue = new DataCommitQueue();
        var disk = new MemoryPersistence();
        var replica = New(queue, disk);
        var localUri = "local:playlist:a";
        NativeReplicaContribution Source(string id, int priority, string uri, string member) => new(id, priority,
            [new(uri, [Member(member, member)], null, null)], new([new RootlistEntry(0, 0, uri, null, 0)], null),
            [new("liked", [new SavedItem("spotify:track:" + member, 1)])], []);
        await replica.AdoptNativeAsync(Source("local", 0, localUri, "a"), Scope);
        await replica.AdoptNativeAsync(Source("export", 1, "local:playlist:b", "b"), Scope);
        Assert.Equal(2, replica.ReadRootlist().Entries.Length);
        Assert.Equal(2, replica.ReadCollection("liked").Items.Length);
        Assert.Equal(ReplicaBaselineState.Missing, replica.ReadConfirmedPlaylist(localUri).State);
        Assert.Empty(replica.ReadConfirmedRootlist().Entries);
        Assert.All(disk.Transactions, transaction => { Assert.Empty(transaction.Playlists); Assert.Null(transaction.Rootlist); Assert.Empty(transaction.Collections); });
        var oldOrder = replica.ReadPlaylist(localUri).OrderRevision;
        await replica.RemoveNativeAsync("local", Scope);
        Assert.Empty(replica.ReadPlaylist(localUri).Members);
        Assert.True(replica.ReadPlaylist(localUri).OrderRevision > oldOrder);
        Assert.Equal("local:playlist:b", Assert.Single(replica.ReadRootlist().Entries).Uri);
        Assert.Equal("spotify:track:b", Assert.Single(replica.ReadCollection("liked").Items).Uri);
    }

    [Fact]
    public async Task Native_source_priority_is_independent_of_arrival_order()
    {
        await using var queue = new DataCommitQueue();
        var replica = New(queue);
        await replica.AdoptNativeAsync(new NativeReplicaContribution("lower", 10,
            [new(Uri, [Member("low", "low")], null, null)], null, [], []), Scope);
        await replica.AdoptNativeAsync(new NativeReplicaContribution("higher", 0,
            [new(Uri, [Member("high", "high")], null, null)], null, [], []), Scope);
        var order = replica.ReadPlaylist(Uri).OrderRevision;
        await replica.RemoveNativeAsync("higher", Scope);
        Assert.Equal("low", Assert.Single(replica.ReadPlaylist(Uri).Members).ItemId);
        Assert.True(replica.ReadPlaylist(Uri).OrderRevision > order);
        Assert.Equal(ReplicaBaselineState.Missing, replica.ReadConfirmedPlaylist(Uri).State);
    }

    static LibraryReplicaCoordinator New(DataCommitQueue queue, MemoryPersistence? disk = null, Sink? sink = null,
        ReplicaBootstrap? bootstrap = null)
        => new(queue, disk ?? new MemoryPersistence(), sink ?? new Sink(), () => Scope, bootstrap ?? ReplicaBootstrap.Empty, Catalog(queue));
    static CatalogRepository Catalog(DataCommitQueue queue) => new(queue, new CatalogMemoryPersistence(), TimeProvider.System,
        new CatalogScope("spotify", "alice", "en", "US", "premium", 1, false), "alice");
    static PlaylistMember Member(string id, string uri) => new(id, "spotify:track:" + uri, "alice", 1);
    static byte[] Head(byte head) { var bytes = new byte[24]; bytes[0] = head; return bytes; }
    static PlaylistReadResult Snapshot(ImmutableArray<PlaylistMember> members, byte head)
        => new(Uri, PlaylistReadKind.Snapshot, null, Head(head), members, [], null);

    sealed class Sink : IReplicaProjectionSink
    {
        public List<ReplicaProjection> Publications { get; } = [];
        public void Publish(ReplicaProjection projection) => Publications.Add(projection);
    }
    sealed class MemoryPersistence : IReplicaPersistence
    {
        public List<ReplicaTransaction> Transactions { get; } = [];
        public Func<ReplicaTransaction, Task>? BeforeCommit { get; init; }
        public ValueTask<ReplicaBootstrap> LoadAsync(ReplicaScope scope, CancellationToken ct) => ValueTask.FromResult(ReplicaBootstrap.Empty);
        public ValueTask<PlaylistReplicaBaseline?> LoadPlaylistAsync(ReplicaScope scope, string uri, CancellationToken ct) => ValueTask.FromResult<PlaylistReplicaBaseline?>(null);
        public async ValueTask CommitAsync(ReplicaTransaction transaction, CancellationToken ct)
        {
            if (BeforeCommit is { } before) await before(transaction);
            Transactions.Add(transaction);
        }
    }
}
