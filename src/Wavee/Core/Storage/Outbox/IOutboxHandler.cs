using System.Threading;
using System.Threading.Tasks;
using Wavee.Core.Storage.Abstractions;

namespace Wavee.Core.Storage.Outbox;

/// <summary>
/// One handler per <see cref="OpKind"/>. Handlers are registered with DI as
/// <c>IOutboxHandler</c>; <see cref="OutboxProcessor"/> dispatches dequeued
/// entries to the handler whose <see cref="OpKind"/> matches the entry's
/// <see cref="OutboxEntry.OpKind"/>.
/// </summary>
public interface IOutboxHandler
{
    /// <summary>
    /// Handler discriminator. Must be globally unique across registered handlers.
    /// Convention: dotted prefix per area (e.g. <c>"library.save"</c>,
    /// <c>"library.remove"</c>, <c>"playlist.add-tracks"</c>).
    /// </summary>
    string OpKind { get; }

    /// <summary>
    /// Process a single dequeued entry. Throw on transient failure — the
    /// processor will increment retry count and re-dequeue on the next run.
    /// Use <see cref="IMetadataDatabase.AdvanceOutboxProgressAsync"/> from
    /// inside the handler to persist mid-flight progress for chunked ops so
    /// retries can resume rather than replay.
    /// </summary>
    Task ProcessAsync(OutboxEntry entry, CancellationToken ct);
}
