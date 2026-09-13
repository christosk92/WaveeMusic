using Wavee.Sdk.Streams;
using Xunit;

namespace Wavee.Tests.Audio;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// ReadAheadPolicy.Compute is the pure decision behind RangedHttpSource.ConfigureReadAhead: given a measured throughput,
// the track's bitrate, whether the connection is metered, and this stream's share of the read-ahead memory budget, what
// window (in bytes AND seconds) should the CDN read-ahead hold, and why? Table-driven over the combinations the spec
// pins: bandwidth growth toward whole-track prefetch at >=3x realtime, a floor at (roughly) realtime, the metered clamp,
// the memory cap, and the never-below-256-KiB floor. No network, no clock — arithmetic only.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
public class ReadAheadPolicyTests
{
    const long OneHundredMb = 100L * 1024 * 1024;
    const long TwelveMb = 12L * 1024 * 1024;

    [Theory]
    // measuredBytesPerSec, bitrateBitsPerSec, metered, memoryCapBytes, expectedWindowBytes, expectedWindowSeconds, expectedReason

    // ── metered: clamps to a modest window regardless of throughput (measured is irrelevant on this branch) ──────────
    [InlineData(0, 320_000, true, OneHundredMb, 600_000, 15, ReadAheadPolicy.Reason.Metered)]
    [InlineData(5_000_000, 320_000, true, OneHundredMb, 600_000, 15, ReadAheadPolicy.Reason.Metered)]
    // metered + a tiny bitrate still never dips below the 256 KiB floor
    [InlineData(0, 8_000, true, OneHundredMb, ReadAheadPolicy.FloorBytes, 262, ReadAheadPolicy.Reason.Metered)]

    // ── unmetered, near-realtime throughput (< 3x) — holds the floor-of-seconds window, not the whole track ──────────
    [InlineData(40_000, 320_000, false, OneHundredMb, 1_200_000, 30, ReadAheadPolicy.Reason.Bandwidth)]
    // throughput unmeasured (0) behaves the same as near-realtime — the conservative assumption, never optimistic
    [InlineData(0, 320_000, false, OneHundredMb, 1_200_000, 30, ReadAheadPolicy.Reason.Bandwidth)]
    // unknown bitrate (<= 0) assumes a high-quality Ogg rate rather than starving the stream
    [InlineData(0, 0, false, OneHundredMb, 1_200_000, 30, ReadAheadPolicy.Reason.Bandwidth)]

    // ── unmetered, >= 3x realtime — grows toward whole-track prefetch (96 kbps: a long track is still a few MB) ──────
    [InlineData(200_000, 96_000, false, OneHundredMb, 7_200_000, 600, ReadAheadPolicy.Reason.Bandwidth)]

    // ── unmetered, >= 3x realtime, but the memory cap bites before "whole track" is reached ────────────────────────
    [InlineData(200_000, 320_000, false, TwelveMb, 12_582_912, 314, ReadAheadPolicy.Reason.MemoryCap)]

    // ── unmetered, near-realtime, tiny bitrate — the target itself is below the floor, so the floor wins but the
    //    reason stays Bandwidth (the floor is a clamp, not a distinct decision reason) ────────────────────────────────
    [InlineData(200, 1_600, false, OneHundredMb, ReadAheadPolicy.FloorBytes, 1_310, ReadAheadPolicy.Reason.Bandwidth)]
    public void Compute_MatchesTheDecisionTable(long measuredBytesPerSec, int bitrateBitsPerSec, bool metered,
        long memoryCapBytes, int expectedWindowBytes, int expectedWindowSeconds, ReadAheadPolicy.Reason expectedReason)
    {
        var decision = ReadAheadPolicy.Compute(measuredBytesPerSec, bitrateBitsPerSec, metered, memoryCapBytes);

        Assert.Equal(expectedWindowBytes, decision.WindowBytes);
        Assert.Equal(expectedWindowSeconds, decision.WindowSeconds);
        Assert.Equal(expectedReason, decision.Reason);
    }

    [Fact]
    public void NeverGoesBelowTheFloor_EvenWithATinyMemoryCap()
    {
        var decision = ReadAheadPolicy.Compute(measuredBytesPerSec: 0, bitrateBitsPerSec: 320_000, metered: true,
            memoryCapBytes: 1024);

        Assert.Equal(ReadAheadPolicy.FloorBytes, decision.WindowBytes);
    }

    [Fact]
    public void FastThroughput_GrowsTowardWholeTrackPrefetch_ForA320kbpsFourMinuteTrack()
    {
        // A 4-minute 320 kbps track is ~9.6 MB — well inside the fast-lane window at a generous memory cap, so a fast
        // connection can prefetch the whole thing rather than trickling it in.
        const long fourMinutes = 240;
        const int bitrateBitsPerSec = 320_000;
        long trackBytes = bitrateBitsPerSec / 8 * fourMinutes;

        var decision = ReadAheadPolicy.Compute(measuredBytesPerSec: 5_000_000, bitrateBitsPerSec, metered: false,
            memoryCapBytes: OneHundredMb);

        Assert.True(decision.WindowBytes >= trackBytes,
            $"expected the fast-lane window ({decision.WindowBytes}B) to cover the whole ~{trackBytes}B track");
        Assert.Equal(ReadAheadPolicy.Reason.Bandwidth, decision.Reason);
    }

    [Fact]
    public void Metered_AlwaysWinsOverFastThroughput()
    {
        var decision = ReadAheadPolicy.Compute(measuredBytesPerSec: 10_000_000, bitrateBitsPerSec: 320_000,
            metered: true, memoryCapBytes: OneHundredMb);

        Assert.Equal(ReadAheadPolicy.Reason.Metered, decision.Reason);
        Assert.True(decision.WindowSeconds <= ReadAheadPolicy.MeteredWindowSeconds);
    }
}
