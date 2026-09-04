using Wavee.Sdk.Streams;
using Xunit;

namespace Wavee.Tests.Audio;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// ChunkCacheAdmission.Decide is the pure decision behind ChunkDiskCache's CanCommit: given the volume's readiness,
// free space and total size, the cache's policy, and the bytes a write would add, should the write be admitted, and
// if not, why? It exists because ChunkDiskCache.Capacity() used to compute reserve = Max(5 GiB, total / 20) with no
// ceiling: on the dev machine (1,021,771,247,616 B total, 27,170,607,104 B free) that reserved 47.6 GiB against
// 25.3 GiB free, so every cache write was refused before the file was ever opened. The bug was silent — the audio
// body cache was completely dead and every replay re-downloaded from the CDN — because the decision lived inline
// inside a method that touches the filesystem and was never exercised on its own. No disk, no DriveInfo, no clock —
// arithmetic only.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
public class ChunkCacheAdmissionTests
{
    static readonly ChunkCachePolicy Unlimited = new(true, "C:\\cache", AudioCacheBudgetMode.Unlimited, 0, 0);

    // ── the regression this file exists to pin ──────────────────────────────────────────────────────────────────────
    [Fact]
    public void RealMachineConfiguration_ThatSilentlyKilledTheAudioCache_IsNowAllowed()
    {
        // Dev machine numbers, verbatim. Under the old unclamped rule, reserve = Max(5 GiB, total/20) = 47.6 GiB,
        // which is bigger than the 25.3 GiB actually free — so the cache refused to write anything, ever, and no
        // error surfaced anywhere. Never let an unclamped total/20 reserve creep back in here.
        const long totalBytes = 1_021_771_247_616L;
        const long freeBytes = 27_170_607_104L;
        var policy = new ChunkCachePolicy(true, "C:\\cache", AudioCacheBudgetMode.FixedBytes, 32L << 30, 0);

        var decision = ChunkCacheAdmission.Decide(volumeReady: true, freeBytes, totalBytes, policy,
            approxBytes: 0, growth: 64 * 1024 /* one chunk */);

        Assert.Equal(ChunkAdmission.Allow, decision);
    }

    // ── ReserveBytes: the floor, the ceiling, and the shape in between ──────────────────────────────────────────────
    [Fact]
    public void ReserveBytes_FloorBinds_OnSmallVolumes()
    {
        // total/20 on a small volume is a few hundred MB — far under the 5 GiB floor that protects tiny disks.
        Assert.Equal(5L << 30, ChunkCacheAdmission.ReserveBytes(10L << 30));  // 10 GiB drive: total/20 = 0.5 GiB
        Assert.Equal(5L << 30, ChunkCacheAdmission.ReserveBytes(50L << 30)); // 50 GiB drive: total/20 = 2.5 GiB
    }

    [Fact]
    public void ReserveBytes_CeilingBinds_OnLargeVolumes()
    {
        // An unclamped total/20 on a 4 TB drive would reserve ~200 GiB. The new rule caps the reserve at roughly
        // 20 GiB, so the actual reserve must land far below the naive figure — pinned as a relationship, not a
        // literal, since the exact ceiling is not part of the contract.
        long fourTb = 4L << 40;
        long naiveUnclamped = fourTb / 20;
        long reserve = ChunkCacheAdmission.ReserveBytes(fourTb);

        Assert.True(reserve < naiveUnclamped / 4,
            $"expected the ceiling to bind well under the unclamped {naiveUnclamped} B reserve, got {reserve} B");

        // The ceiling saturates: an even bigger volume must not reserve more than the 4 TB one already does.
        Assert.Equal(reserve, ChunkCacheAdmission.ReserveBytes(fourTb * 2));
    }

    [Fact]
    public void ReserveBytes_IsMonotoneNonDecreasing_AcrossVolumeSizes()
    {
        long[] totals =
        {
            0, 1L << 30, 5L << 30, 10L << 30, 50L << 30, 100L << 30, 500L << 30,
            1L << 40, 2L << 40, 4L << 40, 8L << 40, 16L << 40,
        };

        long previous = long.MinValue;
        foreach (long total in totals)
        {
            long reserve = ChunkCacheAdmission.ReserveBytes(total);
            Assert.True(reserve >= previous,
                $"reserve dropped from {previous} B to {reserve} B as total grew to {total} B");
            previous = reserve;
        }
    }

    [Fact]
    public void ReserveBytes_NeverThrows_ForZeroOrNegativeTotal()
    {
        Assert.Equal(5L << 30, ChunkCacheAdmission.ReserveBytes(0));
        Assert.Equal(5L << 30, ChunkCacheAdmission.ReserveBytes(-1));
        Assert.Equal(5L << 30, ChunkCacheAdmission.ReserveBytes(long.MinValue));
    }

    // ── Decide: every outcome is reachable, for the right reason, with the boundary pinned ─────────────────────────
    [Fact]
    public void Decide_ReturnsVolumeUnavailable_WhenTheVolumeIsNotReady()
    {
        // Not-ready wins over every other input, however generous the numbers look.
        var decision = ChunkCacheAdmission.Decide(volumeReady: false, freeBytes: long.MaxValue,
            totalBytes: 1L << 40, Unlimited, approxBytes: 0, growth: 0);

        Assert.Equal(ChunkAdmission.VolumeUnavailable, decision);
    }

    [Fact]
    public void Decide_BoundaryAtTheReserve_AdmitsAtAndAboveButRefusesBelow()
    {
        const long totalBytes = 200L << 30; // 200 GiB: comfortably clear of both the floor and the ceiling
        long reserve = ChunkCacheAdmission.ReserveBytes(totalBytes);

        Assert.Equal(ChunkAdmission.Allow,
            ChunkCacheAdmission.Decide(true, reserve, totalBytes, Unlimited, approxBytes: 0, growth: 0));
        Assert.Equal(ChunkAdmission.Allow,
            ChunkCacheAdmission.Decide(true, reserve + 1, totalBytes, Unlimited, approxBytes: 0, growth: 0));
        Assert.Equal(ChunkAdmission.BelowFreeSpaceReserve,
            ChunkCacheAdmission.Decide(true, reserve - 1, totalBytes, Unlimited, approxBytes: 0, growth: 0));
    }

    [Fact]
    public void Decide_ReserveCheck_SubtractsGrowthFromFree_NotJustFreeAlone()
    {
        // Free space alone clears the reserve, but the write's own growth eats into that headroom too.
        const long totalBytes = 200L << 30;
        long reserve = ChunkCacheAdmission.ReserveBytes(totalBytes);
        long free = reserve + 1_000;

        Assert.Equal(ChunkAdmission.Allow,
            ChunkCacheAdmission.Decide(true, free, totalBytes, Unlimited, approxBytes: 0, growth: 999));
        Assert.Equal(ChunkAdmission.BelowFreeSpaceReserve,
            ChunkCacheAdmission.Decide(true, free, totalBytes, Unlimited, approxBytes: 0, growth: 1_001));
    }

    [Fact]
    public void Decide_BoundaryAtTheBudget_AdmitsAtAndBelowButRefusesOver()
    {
        const long totalBytes = 200L << 30;
        var policy = new ChunkCachePolicy(true, "C:\\cache", AudioCacheBudgetMode.FixedBytes, 10L << 30, 0);
        long budget = ChunkCacheAdmission.BudgetBytes(totalBytes, policy)!.Value;
        const long free = 190L << 30; // plenty of headroom against the reserve; only the budget check is in play

        Assert.Equal(ChunkAdmission.Allow,
            ChunkCacheAdmission.Decide(true, free, totalBytes, policy, approxBytes: 0, growth: budget));
        Assert.Equal(ChunkAdmission.OverBudget,
            ChunkCacheAdmission.Decide(true, free, totalBytes, policy, approxBytes: 0, growth: budget + 1));
        Assert.Equal(ChunkAdmission.OverBudget,
            ChunkCacheAdmission.Decide(true, free, totalBytes, policy, approxBytes: budget, growth: 1));
    }

    [Fact]
    public void Decide_ApproxBytesAlreadyOverBudget_RefusesEvenWithZeroGrowth()
    {
        const long totalBytes = 200L << 30;
        var policy = new ChunkCachePolicy(true, "C:\\cache", AudioCacheBudgetMode.FixedBytes, 10L << 30, 0);
        long budget = ChunkCacheAdmission.BudgetBytes(totalBytes, policy)!.Value;

        var decision = ChunkCacheAdmission.Decide(true, freeBytes: 190L << 30, totalBytes, policy,
            approxBytes: budget + 500, growth: 0);

        Assert.Equal(ChunkAdmission.OverBudget, decision);
    }

    [Fact]
    public void Decide_UnlimitedBudget_NeverRefusesOnBudget_NoMatterHowMuchIsAlreadyStored()
    {
        // Unlimited means the free-space reserve is the only gate; a huge approxBytes and a large growth are fine
        // as long as there is still headroom above the reserve.
        const long totalBytes = 16L << 40; // 16 TB
        var decision = ChunkCacheAdmission.Decide(true, freeBytes: 10L << 40, totalBytes, Unlimited,
            approxBytes: 8L << 40, growth: 1L << 40);

        Assert.Equal(ChunkAdmission.Allow, decision);
    }

    // ── degenerate inputs: refuse, never throw, never overflow ──────────────────────────────────────────────────────
    [Fact]
    public void Decide_RefusesRatherThanThrows_WhenFreeIsZero()
    {
        var decision = ChunkCacheAdmission.Decide(true, freeBytes: 0, totalBytes: 1L << 40, Unlimited,
            approxBytes: 0, growth: 0);

        Assert.Equal(ChunkAdmission.BelowFreeSpaceReserve, decision);
    }

    [Fact]
    public void Decide_HugeGrowth_RefusesRatherThanWrappingThroughOverflow()
    {
        // freeBytes - growth must saturate, not wrap: naively subtracting long.MaxValue from a small free value
        // wraps around (two's complement) to a huge *positive* number, which would wrongly read as "plenty of
        // headroom" and admit a write that should be refused.
        var decision = ChunkCacheAdmission.Decide(true, freeBytes: 1_000, totalBytes: 1L << 40, Unlimited,
            approxBytes: 0, growth: long.MaxValue);

        Assert.Equal(ChunkAdmission.BelowFreeSpaceReserve, decision);
    }

    [Fact]
    public void Decide_ZeroOrNegativeTotal_StillRefusesOnTheFloorReserve_WithoutThrowing()
    {
        // total <= 0 still yields the 5 GiB floor reserve; a free value under that floor must be refused, not
        // crash the caller.
        var decision = ChunkCacheAdmission.Decide(true, freeBytes: 1L << 20, totalBytes: 0, Unlimited,
            approxBytes: 0, growth: 0);

        Assert.Equal(ChunkAdmission.BelowFreeSpaceReserve, decision);
    }

    // ── BudgetBytes: one behaviour per policy mode ──────────────────────────────────────────────────────────────────
    [Fact]
    public void BudgetBytes_Unlimited_IsNull()
    {
        var policy = new ChunkCachePolicy(true, "C:\\cache", AudioCacheBudgetMode.Unlimited, 999, 99);
        Assert.Null(ChunkCacheAdmission.BudgetBytes(1L << 40, policy));
    }

    [Fact]
    public void BudgetBytes_FixedBytes_UsesThePolicyValue_AboveTheMinimum()
    {
        var policy = new ChunkCachePolicy(true, "C:\\cache", AudioCacheBudgetMode.FixedBytes, 10L << 30, 0);
        Assert.Equal(10L << 30, ChunkCacheAdmission.BudgetBytes(1L << 40, policy));
    }

    [Fact]
    public void BudgetBytes_FixedBytes_FloorsAtTheMinimumBudget()
    {
        var policy = new ChunkCachePolicy(true, "C:\\cache", AudioCacheBudgetMode.FixedBytes, 1L << 20 /* 1 MiB */, 0);
        Assert.Equal(ChunkDiskCache.MinBudgetBytes, ChunkCacheAdmission.BudgetBytes(1L << 40, policy));
    }

    [Fact]
    public void BudgetBytes_DriveShareAuto_IsATenthOfTheVolume_ClampedToABand()
    {
        var auto = new ChunkCachePolicy(true, "C:\\cache", AudioCacheBudgetMode.DriveShare, 0, 0);

        // Mid-range: a tenth of the volume, comfortably inside the band, so no clamp fires. Derived rather than
        // written out: 1 TiB / 10 is 109,951,162,777 B, NOT the round 100 GiB it reads as — spelling the expectation
        // as a literal is how this assertion was wrong the first time.
        const long oneTiB = 1L << 40;
        Assert.Equal(oneTiB / 10, ChunkCacheAdmission.BudgetBytes(oneTiB, auto));

        // Small volume: 10 GiB / 10 = 1 GiB, under the band's floor.
        long small = ChunkCacheAdmission.BudgetBytes(10L << 30, auto)!.Value;
        Assert.True(small >= 16L << 30, $"expected the drive-share floor to bind, got {small} B");

        // Huge volume: 2 TB / 10 = 200 GiB, over the band's ceiling.
        long huge = ChunkCacheAdmission.BudgetBytes(2L << 40, auto)!.Value;
        Assert.True(huge <= 128L << 30, $"expected the drive-share ceiling to bind, got {huge} B");
    }

    [Fact]
    public void BudgetBytes_DriveShareExplicitPercent_IsThatShareOfTheVolume()
    {
        var policy = new ChunkCachePolicy(true, "C:\\cache", AudioCacheBudgetMode.DriveShare, 0, 50);
        Assert.Equal(50L << 30, ChunkCacheAdmission.BudgetBytes(100L << 30, policy));
    }
}
