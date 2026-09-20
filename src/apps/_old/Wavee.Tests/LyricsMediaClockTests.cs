using System;
using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>LyricsMediaClock: maps QPC time onto a continuous media-ms estimate fed by
/// PlaybackBridge.LastPositionSample. Frequency 1000 throughout so QPC arithmetic reads directly as milliseconds
/// (the class's own ctor doc calls this out as the convenient test injection). These pin the exact bug LyricsView's
/// old TickCount64 extrapolation had — the karaoke wipe stepping even at a smooth, GPU-cheap frame rate — by driving
/// the clock the same way OnFrame does: an authoritative sample every ~200 ms, a frame query every ~8.33 ms.</summary>
public class LyricsMediaClockTests
{
    const long Freq = 1000;                     // 1 tick == 1 ms (the discrete tests below)
    const long FreqUs = 1_000_000;              // 1 tick == 1 µs — the cadence tests: whole-ms QPC would itself alternate
                                                // 8/9 ms frame gaps and blur the clock's own step bound (real QPC is 10 MHz)
    const double FrameDt = 1000.0 / 120.0;      // ~8.3333 ms — a 120 Hz panel producing frames at panel rate
    const double SampleDt = 200.0;              // the bridge's coarse ~1 Hz-ish IPC snapshot cadence
    const double SampleAgeMs = 30.0;            // a sample is always OLDER than the frame on screen (host tick → UI post
                                                // → a frame produced ~2 vblanks ahead) — the case the slew pivot exists for

    static long Us(double ms) => (long)(ms * 1000.0);

    [Fact]
    public void SmoothPlayback_NeverSteps_NeverGoesBackwards_AndZeroAdvanceStaysZero()
    {
        var clock = new LyricsMediaClock(FreqUs);
        var rng = new Random(12345);
        clock.OnSample(0, 0, true);

        double frameAccum = 0, sampleAccum = 0;
        long lastAt = long.MinValue;
        long totalStep = 0;
        int frames = 0;

        while (frameAccum < 5000.0)   // 5 simulated seconds
        {
            frameAccum += FrameDt;
            sampleAccum += FrameDt;
            long qpc = Us(frameAccum);

            if (sampleAccum >= SampleDt)
            {
                sampleAccum -= SampleDt;
                double jitter = (rng.NextDouble() * 2.0 - 1.0) * 15.0;   // +/- 15 ms IPC jitter
                double sampledAt = frameAccum - SampleAgeMs;               // true position at the sample's own instant
                clock.OnSample((long)(sampledAt + jitter), Us(sampledAt), true);
            }

            long at = clock.At(qpc);
            if (lastAt != long.MinValue)
            {
                long step = at - lastAt;
                Assert.True(step > 0, $"frame {frames}: step was {step} ms — never zero, never backwards");
                Assert.True(step <= 9, $"frame {frames}: step was {step} ms — expected <= ~8.33*1.05 ms");
                totalStep += step;
            }
            lastAt = at;
            frames++;
        }

        // The old bug's signature was an AVERAGE nowhere near nominal (TickCount64 alternated 0 / 15.6 ms per
        // frame at this rate); a smooth clock's average cadence stays close to the true 8.33 ms regardless of
        // individual-frame integer-rounding noise.
        double avgStep = (double)totalStep / (frames - 1);
        Assert.InRange(avgStep, FrameDt * 0.9, FrameDt * 1.1);

        var diag = clock.ReadAndResetDiagnostics();
        Assert.Equal(0, diag.ZeroAdvanceFrames);
        Assert.Equal(frames, diag.Frames);
    }

    [Fact]
    public void ConstantOffset_ConvergesWithinAFewSeconds_WithoutAnOversizedStep()
    {
        var clock = new LyricsMediaClock(FreqUs);
        clock.OnSample(0, 0, true);   // starts unbiased: anchor 0 at qpc 0, rate 1

        const double Bias = 40.0;    // the sample line is a constant 40 ms ahead of a naive rate-1 extrapolation
        double frameAccum = 0, sampleAccum = 0;
        long lastAt = long.MinValue;

        while (frameAccum < 3500.0)   // a little past the ~3 s convergence window
        {
            frameAccum += FrameDt;
            sampleAccum += FrameDt;
            long qpc = Us(frameAccum);

            if (sampleAccum >= SampleDt)
            {
                sampleAccum -= SampleDt;
                double sampledAt = frameAccum - SampleAgeMs;
                clock.OnSample((long)(sampledAt + Bias), Us(sampledAt), true);
            }

            long at = clock.At(qpc);
            if (lastAt != long.MinValue)
            {
                long step = at - lastAt;
                Assert.True(step > 0, $"step was {step} ms — never zero, never backwards while converging");
                Assert.True(step <= 9, $"step {step} ms exceeded ~8.33*1.05 ms during convergence");
            }
            lastAt = at;

            // The error closes geometrically (~0.8× per 200 ms sample) from the displayed frame onward, and At() returns
            // whole ms — so the 40 ms bias is under 3 ms a little past 3 s, and keeps shrinking.
            if (frameAccum >= 3250.0)
            {
                double target = frameAccum + Bias;
                Assert.True(Math.Abs(at - target) < 3.0, $"at={at} vs target={target} at t={frameAccum}ms — not converged");
            }
        }
    }

    [Fact]
    public void ForwardJump_Snaps_AndLandsOnTheNewPosition()
    {
        var clock = new LyricsMediaClock(Freq);
        clock.OnSample(10_000, 0, true);
        Assert.Equal(10_000, clock.At(0));

        // 100 ms later the clock's own prediction would be 10_100; report 400 ms AHEAD of that — a Connect
        // transfer or a track change, never an ordinary IPC disagreement (the 250 ms snap threshold).
        bool snapped = clock.OnSample(10_500, 100, true);
        Assert.True(snapped);
        Assert.Equal(10_500, clock.At(100));
        Assert.Equal(10_508, clock.At(108));   // continues forward at rate 1 immediately after the snap
    }

    [Fact]
    public void BackwardJump_Snaps_AndLandsOnTheNewPosition()
    {
        var clock = new LyricsMediaClock(Freq);
        clock.OnSample(10_000, 0, true);
        Assert.Equal(10_000, clock.At(0));

        bool snapped = clock.OnSample(9_700, 100, true);   // 400 ms BEHIND the 10_100 prediction
        Assert.True(snapped);
        Assert.Equal(9_700, clock.At(100));
        Assert.Equal(9_708, clock.At(108));
    }

    [Fact]
    public void Paused_PinsToTheSample_AndIgnoresFrameTime()
    {
        var clock = new LyricsMediaClock(Freq);
        clock.OnSample(5_000, 0, true);
        clock.OnSample(5_000, 100, false);   // pause at 5_000
        Assert.Equal(5_000, clock.At(100));
        Assert.Equal(5_000, clock.At(1_000_000));   // frame time can run arbitrarily far ahead; the pin does not move

        // A scrub while paused (a fresh sample at a different position, still not playing) is reflected immediately.
        clock.OnSample(7_777, 100_050, false);
        Assert.Equal(7_777, clock.At(999_999));
    }

    [Fact]
    public void Resume_RebasesFromTheFirstPlayingSample_WithNoJump()
    {
        var clock = new LyricsMediaClock(Freq);
        clock.OnSample(5_000, 0, true);
        clock.OnSample(5_000, 100, false);   // pause
        Assert.Equal(5_000, clock.At(500));   // still pinned regardless of qpc while paused

        bool snapped = clock.OnSample(5_000, 1_000, true);   // resume: first playing sample after the pause
        Assert.False(snapped);                  // a rebase, not a seek — the caller's seek recovery must not fire
        Assert.Equal(5_000, clock.At(1_000));   // no jump at the exact resume instant
        Assert.Equal(5_008, clock.At(1_008));   // continues forward smoothly at rate 1
    }

    [Fact]
    public void Resume_AtAPositionThatMovedWhilePaused_ReportsTheJump()
    {
        var clock = new LyricsMediaClock(Freq);
        clock.OnSample(5_000, 0, true);
        clock.OnSample(5_000, 100, false);          // pause at 5 s
        Assert.True(clock.OnSample(9_000, 1_000, true));   // resumed 4 s further on (a transfer / an outside seek)
        Assert.Equal(9_000, clock.At(1_000));
    }

    [Fact]
    public void ReadAndResetDiagnostics_ZeroesTheCounters()
    {
        var clock = new LyricsMediaClock(Freq);
        clock.OnSample(0, 0, true);
        clock.At(10);
        clock.At(20);
        var first = clock.ReadAndResetDiagnostics();
        Assert.True(first.Frames > 0);

        var second = clock.ReadAndResetDiagnostics();
        Assert.Equal(0, second.Frames);
        Assert.Equal(0, second.Snaps);
        Assert.Equal(0, second.ZeroAdvanceFrames);
        Assert.Equal(0, second.MaxStepMs);
        Assert.Equal(0.0, second.SlewMs);
    }
}
