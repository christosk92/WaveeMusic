using Wavee.SpotifyLive.Audio;
using Xunit;

namespace Wavee.Tests;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// XrunLogLine is the pure half of FluentMediaAudioHost.DrainXruns: turning one drained RT-feed ring-underrun event into
// the always-on per-incident Warning line (WHEN, how much audio was actually lost, how starved the ring was, where
// playback was, session state, and whether a GC pause was implicated) that replaced the old boundary-only cumulative
// "xruns=17" count. The host itself opens a real WASAPI device and cannot be instantiated headlessly, so this arithmetic
// (gap-in-ms, event age) and message assembly is pinned here instead.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
public class XrunLogLineTests
{
    [Theory]
    [InlineData(480, 48000, 10.0)]      // exactly one 10 ms block lost at 48 kHz
    [InlineData(4410, 44100, 100.0)]    // 100 ms lost at 44.1 kHz
    [InlineData(0, 48000, 0.0)]         // no frames lost — never a spurious non-zero gap
    public void GapMs_ConvertsFramesAtTheMixRate(int gapFrames, int sampleRate, double expectedMs)
    {
        Assert.Equal(expectedMs, XrunLogLine.GapMs(gapFrames, sampleRate), 6);
    }

    [Fact]
    public void GapMs_NeverDividesByZero()
    {
        // A device that hasn't negotiated a rate yet (or a malformed event) must report 0, never throw or report NaN/Inf.
        Assert.Equal(0.0, XrunLogLine.GapMs(480, 0));
        Assert.Equal(0.0, XrunLogLine.GapMs(480, -1));
        Assert.Equal(0.0, XrunLogLine.GapMs(-5, 48000));
    }

    [Fact]
    public void AgeMs_IsTheElapsedTickCount()
    {
        Assert.Equal(250L, XrunLogLine.AgeMs(eventTimestampTicks64: 1_000L, nowTicks64: 1_250L));
        Assert.Equal(0L, XrunLogLine.AgeMs(eventTimestampTicks64: 1_000L, nowTicks64: 1_000L));
    }

    [Fact]
    public void AgeMs_ClampsToZero_NeverReportsTheFuture()
    {
        // A drained event's timestamp arriving "after now" (a misordered/wrapped tick count) must never print a
        // negative age — that would read as the incident hasn't happened yet.
        Assert.Equal(0L, XrunLogLine.AgeMs(eventTimestampTicks64: 2_000L, nowTicks64: 1_000L));
    }

    [Fact]
    public void Format_CarriesEveryFieldTheOldCumulativeCountCouldNotSay()
    {
        string line = XrunLogLine.Format(voiceId: 7, gapFrames: 480, totalFramesLost: 9600, ringFrames: 12,
            gcPauseTicksDelta: 3500, gapMs: 10.0, ageMs: 42, positionMs: 123_456, sessionState: "Playing");

        Assert.Contains("voice=7", line);
        Assert.Contains("gapFrames=480", line);
        Assert.Contains("gapMs=10.0", line);
        Assert.Contains("totalFramesLost=9600", line);
        Assert.Contains("ringFramesAtMiss=12", line);
        Assert.Contains("posMs=123456", line);
        Assert.Contains("state=Playing", line);
        Assert.Contains("gcPauseTicksDelta=3500", line);
        Assert.Contains("ageMs=42", line);
        Assert.StartsWith("[audio] xrun", line);
    }
}
