using Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Core.Storage.Abstractions;

/// <summary>
/// Open write-batch dispatcher: holds a SQLite write lock + transaction for
/// the lifetime of the using-block. Operations called on this instance share
/// the same connection and transaction. Disposing commits.
/// </summary>
/// <remarks>
/// We deliberately do NOT use <see cref="System.Threading.AsyncLocal{T}"/> to
/// route inner database calls into the scope, because AsyncLocal mutations do
/// not flow back from an awaited child to its caller — which made the previous
/// design self-deadlock (inner calls taking the self-locking path against the
/// lock the outer scope still held). Routing through this dispatcher carries
/// the connection + transaction explicitly.
/// </remarks>
public interface IWriteBatch : IAsyncDisposable
{
    /// <summary>
    /// Writes many extension rows in this batch's transaction.
    /// Promotes each row to the database hot cache.
    /// </summary>
    Task SetExtensionsBulkAsync(
        IReadOnlyList<ExtensionWriteRecord> records,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Bulk-refreshes the expires_at timestamp for many extension rows in
    /// this batch's transaction.
    /// </summary>
    Task RefreshExtensionTtlBulkAsync(
        IReadOnlyList<(string EntityUri, ExtensionKind Kind)> rows,
        long ttlSeconds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts an entity row in this batch's transaction. Mirrors
    /// <see cref="IMetadataDatabase.UpsertEntityAsync"/>.
    /// </summary>
    Task UpsertEntityAsync(
        string uri,
        EntityType entityType,
        SourceType sourceType = SourceType.Spotify,
        string? title = null,
        string? artistName = null,
        string? albumName = null,
        string? albumUri = null,
        int? durationMs = null,
        int? trackNumber = null,
        int? discNumber = null,
        int? releaseYear = null,
        string? imageUrl = null,
        string? genre = null,
        int? trackCount = null,
        int? followerCount = null,
        string? publisher = null,
        int? episodeCount = null,
        string? description = null,
        string? filePath = null,
        string? streamUrl = null,
        long? expiresAt = null,
        long? addedAt = null,
        CancellationToken cancellationToken = default);
}
