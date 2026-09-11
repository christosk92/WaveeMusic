using System;

namespace Wavee.Backend.Persistence;

/// <summary>Durable normalized cache accounting. The budget governs bytes that are not retained by active or saved roots.</summary>
public readonly record struct EntityCacheStats(
    long DbBytes, long ReclaimableBytes, long CacheBytes, long PinnedBytes, long BudgetBytes,
    long EntityBytes, long EntityRows, long PinnedRows, long OverviewRows, long ExtensionRows)
{
    public long EvictableBytes => Math.Max(0, EntityBytes - PinnedBytes);
}
