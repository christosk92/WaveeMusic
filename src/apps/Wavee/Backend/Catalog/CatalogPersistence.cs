using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core.Catalog;

namespace Wavee.Backend.Catalog;

public sealed record CatalogCommit(long Revision, IReadOnlyList<CatalogRecord> Records,
    IReadOnlyList<CatalogTransportRecord>? Transports = null);

public sealed record PreparedCatalogObservations(CatalogCommit Commit, System.Action Publish);

/// <summary>Commits are invoked only by DataCommitQueue: completion means durable and failures must propagate.
/// Reads are NOT queue work — <see cref="ReadManyAsync"/> runs off the commit owner, so a cold cache never holds
/// the single worker while it walks a page of keys one statement at a time.</summary>
public interface ICatalogPersistence : ICatalogTransportReader
{
    /// <summary>Reads every key in one round trip. The result is index-aligned with <paramref name="keys"/>;
    /// a null element is a miss. Implementations must not assume the keys share a scope or a facet.</summary>
    ValueTask<IReadOnlyList<CatalogRecord?>> ReadManyAsync(IReadOnlyList<ResourceKey> keys, CancellationToken ct);
    ValueTask CommitAsync(CatalogCommit commit, CancellationToken ct);
}
