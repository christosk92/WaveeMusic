using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;
using Wavee.Backend.Playlists;

namespace Wavee.Backend.Sync;

public enum ReplicaBaselineState : byte { Missing, Cached, Verified, RecoveryOnly, AwaitingCreate, NeedsResync }

/// <summary>Unresolved writes are never replayed automatically. Their overlay survives until server evidence resolves it.</summary>
public enum ReplicaIntentState : byte { Pending, AwaitingVerification, NeedsAttention }

public readonly record struct ReplicaScope(string? Account, long Generation, string StorageAccount = "default");

public sealed record PlaylistReplicaBaseline(string Uri, ImmutableArray<PlaylistMember> Members,
    byte[]? Revision, Playlist? Header, ReplicaBaselineState State = ReplicaBaselineState.Verified, long Version = 0)
{
    public long OrderRevision { get; init; }
}

public sealed record RootlistReplicaBaseline(ImmutableArray<RootlistEntry> Entries, byte[]? Revision,
    ReplicaBaselineState State = ReplicaBaselineState.Verified, long Version = 0);

public sealed record CollectionReplicaBaseline(string SetId, ImmutableArray<SavedItem> Items,
    string? WireRevision = null, long Version = 0)
{
    public bool IsKnown { get; init; } = true;
}

public sealed record ReplicaDeadLetter(OutboxOp Intent, PlaylistMutationFailure Failure, string Reason);

/// <summary>One retained transaction. Membership here is CONFIRMED; effective pending overlays never enter these rows.</summary>
public sealed record ReplicaTransaction(
    ReplicaScope Scope,
    ImmutableArray<PlaylistReplicaBaseline> Playlists,
    RootlistReplicaBaseline? Rootlist,
    ImmutableArray<CollectionReplicaBaseline> Collections,
    ImmutableArray<OutboxOp> SaveIntents,
    ImmutableArray<long> RemoveIntents,
    ImmutableArray<ReplicaDeadLetter> DeadLetters,
    ImmutableArray<string> RemoveRecovery,
    Wavee.Backend.Catalog.CatalogCommit? Catalog = null);

public sealed record ReplicaBootstrap(
    ImmutableArray<PlaylistReplicaBaseline> Playlists,
    RootlistReplicaBaseline Rootlist,
    ImmutableArray<CollectionReplicaBaseline> Collections,
    ImmutableArray<OutboxOp> Intents)
{
    public long LastIntentId { get; init; }
    public static ReplicaBootstrap Empty { get; } = new([], new([], null, ReplicaBaselineState.Missing), [], []);
}

/// <summary>Called only inside the shared DataCommitQueue. Implementations commit every member of a transaction atomically.</summary>
public interface IReplicaPersistence
{
    ValueTask<ReplicaBootstrap> LoadAsync(ReplicaScope scope, CancellationToken ct);
    ValueTask<PlaylistReplicaBaseline?> LoadPlaylistAsync(ReplicaScope scope, string uri, CancellationToken ct);
    ValueTask CommitAsync(ReplicaTransaction transaction, CancellationToken ct);
}

public sealed record ReplicaSavedProjection(string SetId, ImmutableArray<SavedItem> Items,
    ImmutableHashSet<string> PendingUris);

public sealed record ReplicaProjection(
    ImmutableArray<PlaylistReplicaBaseline> Playlists,
    RootlistReplicaBaseline? Rootlist,
    ImmutableArray<ReplicaSavedProjection> Collections);

/// <summary>Publishes EFFECTIVE rows after durability. CachedStore implementations must update their hot view only,
/// bypassing membership write-behind; the matching confirmed rows were already persisted by IReplicaPersistence.</summary>
public interface IReplicaProjectionSink
{
    void Publish(ReplicaProjection projection);
}

public enum PlaylistReadKind : byte { Snapshot, Delta, Unchanged }

/// <summary>A parsed network observation, never a store mutation. Empty Snapshot contents are an authoritative empty list.</summary>
public sealed record PlaylistReadResult(string Uri, PlaylistReadKind Kind, byte[]? ExpectedBase, byte[]? Revision,
    ImmutableArray<PlaylistMember> Members, ImmutableArray<PlaylistOp> Ops, Playlist? Header, bool HeaderIsComplete = true);

public sealed record RootlistReadResult(ImmutableArray<RootlistEntry> Entries, byte[]? Revision);

public sealed record CollectionReadResult(string WireSet, bool IsSnapshot, bool Verified, long StartedAtMs,
    string? ExpectedToken, string? Token, ImmutableArray<Collections.CollectionItem> Items);

public sealed record ReplicaChange(string AggregateId, long Version, bool MembershipChanged, bool PendingChanged);

/// <summary>Replaceable facts already durably owned by a native source. The caller filters playlist ownership through
/// SourceRegistry.OwnerOf and maps headers using the actual provider scope; these rows never become Spotify wire baselines.</summary>
public sealed record NativeReplicaContribution(string SourceId, int Priority,
    ImmutableArray<PlaylistReplicaBaseline> Playlists, RootlistReplicaBaseline? Rootlist,
    ImmutableArray<CollectionReplicaBaseline> Collections,
    ImmutableArray<Wavee.Core.Catalog.CatalogObservation> Headers);

/// <summary>Session protocol lane. A rootlist operation is built and sent only while this lane owns the baseline.</summary>
public interface IRootlistCommandQueue
{
    Task ExecuteRootlistAsync(Func<CancellationToken, Task> command, CancellationToken ct = default);
    Task DrainWritesAsync(CancellationToken ct);
}
