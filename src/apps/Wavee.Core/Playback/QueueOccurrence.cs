using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Wavee.Core;

/// <summary>Playback-owned identity and wire annotations. Catalog metadata is joined at the read boundary.</summary>
public sealed record QueueOccurrence(QueueItemId ItemId, string Uid, string EntityUri, QueueBucket Bucket,
    QueueProvider Provider, QueueRowKind Kind, string? ContextUri,
    IReadOnlyDictionary<string, string>? WireMetadata);

public sealed record QueueRuntimeOverride(QueueItemId ItemId, string? Title, string? Artist, long? DurationMs);

public sealed record QueueOccurrenceSnapshot(long StructuralRevision, ImmutableArray<QueueOccurrence> Rows,
    string? ContextUri, QueueRuntimeOverride? Runtime = null)
{
    public static QueueOccurrenceSnapshot Empty { get; } = new(0, [], null);
}

public interface IQueueOccurrenceSource
{
    QueueOccurrenceSnapshot Current { get; }
    IObservable<QueueOccurrenceSnapshot> Changes { get; }
}
