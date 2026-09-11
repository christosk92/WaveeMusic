namespace Wavee.Sdk.Streams;

/// <summary>Why <see cref="ChunkCacheAdmission.Decide"/> would or wouldn't let a chunk write proceed. Surfaced on
/// <see cref="AudioBodyCacheStatus.Admission"/> so a caller (settings UI, diagnostics) can tell "the cache is empty
/// because nothing has played yet" apart from "the cache is empty because every write is being silently refused" —
/// the bug this type exists to end.</summary>
public enum ChunkAdmission
{
    /// <summary>Nothing stops the write.</summary>
    Allow,
    /// <summary>The volume could not be inspected (unmounted, inaccessible path, etc.) — see
    /// <see cref="AudioBodyCacheStatus.Available"/>.</summary>
    VolumeUnavailable,
    /// <summary>Writing would leave the volume with less free space than <see cref="ChunkCacheAdmission.ReserveBytes"/>
    /// demands. Independent of the cache's own budget — this is "don't fill the user's drive", not "don't grow past
    /// what the user asked for".</summary>
    BelowFreeSpaceReserve,
    /// <summary>The cache's own measured size would exceed <see cref="ChunkCacheAdmission.BudgetBytes"/>.</summary>
    OverBudget,
}

/// <summary>
/// The pure arithmetic behind <see cref="ChunkDiskCache"/>'s admission control: how big a free-space reserve and a
/// size budget are for a given volume/policy, and whether a given write clears both. No I/O, no <see cref="DriveInfo"/>,
/// no clock — every input is a plain value, so this is unit-testable without a filesystem and without a
/// <see cref="ChunkDiskCache"/> instance. <see cref="ChunkDiskCache"/> owns reading the volume (and caching that
/// read); this owns deciding what to do with what it read.
/// </summary>
public static class ChunkCacheAdmission
{
    /// <summary>The smallest free-space reserve, regardless of volume size. Below this, ordinary OS/browser/update
    /// churn on a small drive could plausibly run the volume dry even with the cache behaving.</summary>
    public const long MinReserveBytes = 5L << 30;

    /// <summary>The largest free-space reserve, regardless of volume size. A flat 5% (the old, un-clamped
    /// <c>total / 20</c>) reserves 200 GiB on a 4 TB drive — indefensible for what is, at most, a "don't starve the
    /// OS" margin. 20 GiB is comfortably more headroom than a single Wavee download session could plausibly need in
    /// flight.</summary>
    public const long MaxReserveBytes = 20L << 30;

    /// <summary>
    /// The free-space reserve for a volume of <paramref name="totalBytes"/>: 5% of the volume, clamped to
    /// [<see cref="MinReserveBytes"/>, <see cref="MaxReserveBytes"/>]. The floor protects small drives; the cap
    /// stops the reserve from scaling into indefensible territory on large ones. On the machine that exposed this
    /// bug (1,021,771,247,616 B total, 27,170,607,104 B free) the old unclamped formula reserved 47.6 GiB — more
    /// than the 25.3 GiB actually free, so every write was refused. Clamped to the 20 GiB ceiling, the same drive
    /// reserves 20 GiB, comfortably under its free space, and the cache writes again.
    /// </summary>
    public static long ReserveBytes(long totalBytes) => Math.Clamp(totalBytes / 20, MinReserveBytes, MaxReserveBytes);

    /// <summary>
    /// The cache's size budget for a volume of <paramref name="totalBytes"/> under <paramref name="policy"/>, or
    /// null when the policy is <see cref="AudioCacheBudgetMode.Unlimited"/> (no ceiling — only
    /// <see cref="ReserveBytes"/> constrains it). Mirrors the previous inline <c>Mode</c> switch in
    /// <c>ChunkDiskCache.Capacity</c> unchanged: a <see cref="AudioCacheBudgetMode.FixedBytes"/> policy is floored at
    /// <see cref="ChunkDiskCache.MinBudgetBytes"/>; an auto <see cref="AudioCacheBudgetMode.DriveShare"/>
    /// (<c>Percent == 0</c>) is 10% of the volume clamped to [16 GiB, 128 GiB]; an explicit percentage is that share
    /// of the volume, floored at <see cref="ChunkDiskCache.MinBudgetBytes"/>.
    /// </summary>
    public static long? BudgetBytes(long totalBytes, ChunkCachePolicy policy) => policy.Mode switch
    {
        AudioCacheBudgetMode.Unlimited => null,
        AudioCacheBudgetMode.FixedBytes => Math.Max(ChunkDiskCache.MinBudgetBytes, policy.FixedBytes),
        _ when policy.Percent == 0 => Math.Clamp(totalBytes / 10, 16L << 30, 128L << 30),
        _ => Math.Max(ChunkDiskCache.MinBudgetBytes, (long)(totalBytes * (policy.Percent / 100d))),
    };

    /// <summary>
    /// Whether a write that would grow the cache's on-disk footprint by <paramref name="growth"/> bytes may proceed.
    /// Checked in order: the volume must be readable; the write must not leave free space below
    /// <see cref="ReserveBytes"/>; the cache's resulting measured size (<paramref name="approxBytes"/> +
    /// <paramref name="growth"/>) must not exceed <see cref="BudgetBytes"/> (skipped when that is null). Pure —
    /// callers needing to *fix* a non-<see cref="ChunkAdmission.Allow"/> result (trimming, logging) do so themselves
    /// and re-call this to check whether it worked.
    /// </summary>
    public static ChunkAdmission Decide(bool volumeReady, long freeBytes, long totalBytes, ChunkCachePolicy policy,
        long approxBytes, long growth)
    {
        if (!volumeReady) return ChunkAdmission.VolumeUnavailable;

        // freeBytes - growth, without risking a signed overflow when growth is absurdly large (a caller bug, or a
        // synthetic test — e.g. growth == long.MaxValue against a real-world freeBytes): if growth alone already
        // exceeds every byte currently free, the write is refused outright and there is no need (or safe way, that
        // close to the Int64 range's edge) to compute the negative remainder. When growth <= freeBytes the
        // subtraction is safe by construction — the result lies in [0, freeBytes], nowhere near overflowing — and
        // this also transitively bounds growth before it is added to approxBytes below.
        if (growth > freeBytes || freeBytes - growth < ReserveBytes(totalBytes)) return ChunkAdmission.BelowFreeSpaceReserve;

        if (BudgetBytes(totalBytes, policy) is { } budget && approxBytes + growth > budget) return ChunkAdmission.OverBudget;
        return ChunkAdmission.Allow;
    }
}
