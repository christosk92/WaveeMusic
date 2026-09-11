using Wavee.SpotifyLive.Audio;
using Xunit;

namespace Wavee.Tests;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// #112 — audio stops on headphone plug/unplug until the next track. On the 0.2.9 engine a rate-changed RebuildSink latches
// PcmAudioSession.RequiresGraphRebuild one-way: the mixer/decoder graph bound at prepare time cannot render on the new
// device and the session stays SILENT until a new graph exists. The 0.2.8-shaped host's SoftReloadAsync used to treat every
// early exit ("not re-openable", "attach threw", "open skipped") as "leave the old session playing" — benign for a same-rate
// swap, silence-until-next-track for a rate change. DeviceRecoveryPlan is the pure decision the host now routes those exits
// through; FluentMediaAudioHost opens a real WASAPI device and cannot be instantiated headlessly, so the decision is pinned
// here (mirrors GaplessJoinClockTests in this same project).
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
public class DeviceRecoveryPlanTests
{
    [Fact]
    public void RateChanged_ReopenableBody_ReopensOnANewGraph()
    {
        // A Spotify CDN track (SpotifyEncrypted with a ReopenBody) on a device that now clocks at a different rate: the
        // only way to be audible again is a NEW session/graph at the live rate with the playhead restored.
        Assert.Equal(DeviceRecoveryAction.ReopenNewGraph, DeviceRecoveryPlan.Decide(requiresGraphRebuild: true, canReopen: true));
    }

    [Fact]
    public void SameRate_ReopenableBody_AdoptsIntoTheExistingGraph()
    {
        // Same rate: the existing graph can still render on the swapped sink — no new graph is required.
        Assert.Equal(DeviceRecoveryAction.AdoptIntoExistingGraph, DeviceRecoveryPlan.Decide(requiresGraphRebuild: false, canReopen: true));
    }

    [Fact]
    public void SameRate_UnreopenableBody_KeepsTheSession()
    {
        // A podcast / external / live body on a same-rate swap: the old session is still able to render — keep it (and
        // make sure it is audible). This is the ONLY case where the old "leave old playing" exit was actually benign.
        Assert.Equal(DeviceRecoveryAction.KeepSession, DeviceRecoveryPlan.Decide(requiresGraphRebuild: false, canReopen: false));
    }

    [Fact]
    public void RateChanged_UnreopenableBody_ReloadsThroughTheController()
    {
        // The reporter's failure mode for un-reopenable sources: the graph cannot render and the host cannot rebuild it
        // in place — the controller must record a failure checkpoint and offer Retry instead of the session going quiet
        // until the next track.
        Assert.Equal(DeviceRecoveryAction.ReloadThroughController, DeviceRecoveryPlan.Decide(requiresGraphRebuild: true, canReopen: false));
    }
}
