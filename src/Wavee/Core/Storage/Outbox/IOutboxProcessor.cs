using System.Threading;
using System.Threading.Tasks;

namespace Wavee.Core.Storage.Outbox;

/// <summary>
/// Drives the outbox: dequeue → dispatch to <see cref="IOutboxHandler"/> →
/// complete or fail (with retry-count increment) → drop entries that exceed
/// <see cref="OutboxProcessor.MaxRetries"/>.
///
/// One singleton instance owns the entire retry loop; both library mutations
/// and playlist bulk-adds enqueue against the same underlying table and are
/// drained by this processor.
/// </summary>
public interface IOutboxProcessor
{
    /// <summary>
    /// Process up to <paramref name="limit"/> queued ops. Safe to call concurrently
    /// — internal flag serializes overlapping invocations to a single in-flight run.
    /// Returns the number of entries that failed this run (still queued for retry).
    /// </summary>
    Task<int> RunAsync(int limit = 50, CancellationToken ct = default);
}
