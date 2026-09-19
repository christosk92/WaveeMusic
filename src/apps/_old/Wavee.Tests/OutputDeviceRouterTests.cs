using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.SpotifyLive.Audio;
using Xunit;

namespace Wavee.Tests;

// Deterministic (no COM) tests of the output-device state machine: a fake monitor + a manual clock + an inline (immediate)
// coalescer delay so every topology event recomputes synchronously. See plan §D1.
public class OutputDeviceRouterTests
{
    sealed class FakeMonitor : IAudioDeviceMonitor
    {
        public event Action<AudioDeviceEvent>? Changed;
        public List<AudioEndpointInfo> Active = new();
        public string? DefaultId;
        public IReadOnlyList<AudioEndpointInfo> EnumerateRenderEndpoints() => Active;
        public string? GetDefaultRenderId() => DefaultId;
        public string? GetFriendlyName(string deviceId)
        {
            foreach (var e in Active) if (string.Equals(e.Id, deviceId, StringComparison.OrdinalIgnoreCase)) return e.Name;
            return null;
        }
        public void Dispose() { }
        public void Raise(AudioDeviceEvent e) => Changed?.Invoke(e);
    }

    static AudioEndpointInfo Ep(string id, bool def = false) => new(id, id + " name", AudioEndpointFormFactor.Speakers, def);

    sealed class Harness
    {
        public readonly FakeMonitor Mon = new();
        public long Now;
        public readonly OutputDeviceRouter Router;
        public readonly List<OutputDeviceReroute> Reroutes = new();
        public readonly List<OutputDeviceNotice> Notices = new();
        public Harness()
        {
            Router = new OutputDeviceRouter(Mon, default, () => Now, debounceMs: 300, debounceDelay: (_, _) => Task.CompletedTask);
            Router.RouteInvalidated += r => Reroutes.Add(r);
            Router.Notice += n => Notices.Add(n);
        }
    }

    [Fact]
    public void FollowingDefault_DefaultChanged_OneRerouteNoPauseNoNotice()
    {
        var h = new Harness();
        h.Mon.DefaultId = "A"; h.Mon.Active = new() { Ep("A", def: true) };
        h.Router.NotifyOpened("A", asFallback: false);

        h.Mon.DefaultId = "B"; h.Mon.Active = new() { Ep("B", def: true), Ep("A") };
        h.Mon.Raise(new AudioDeviceEvent(AudioDeviceEventKind.DefaultRenderChanged, "B", 0));

        Assert.Single(h.Reroutes);
        Assert.False(h.Reroutes[0].PauseFirst);
        Assert.Null(h.Reroutes[0].TargetDeviceId);   // follow-default = re-open "the current default"
        Assert.Empty(h.Notices);
    }

    [Fact]
    public void ExplicitDeviceRemoved_PauseFirstFallback_DeviceLost()
    {
        var h = new Harness();
        h.Mon.DefaultId = "A"; h.Mon.Active = new() { Ep("A", def: true), Ep("BT") };
        h.Router.SetDesired("BT");
        h.Router.NotifyOpened("BT", asFallback: false);

        h.Mon.Active = new() { Ep("A", def: true) };   // BT gone
        h.Mon.Raise(new AudioDeviceEvent(AudioDeviceEventKind.DeviceRemoved, "BT", 0));

        Assert.Single(h.Reroutes);
        Assert.True(h.Reroutes[0].PauseFirst);
        Assert.Null(h.Reroutes[0].TargetDeviceId);   // fall back to system default
        Assert.Contains(h.Notices, n => n.Kind == OutputDeviceNoticeKind.DeviceLost && n.WasExplicit);
    }

    [Fact]
    public void ExplicitReturns_ReroutesBack_Restored_FlapFoldsToOne()
    {
        var h = new Harness();
        h.Mon.DefaultId = "A"; h.Mon.Active = new() { Ep("A", def: true), Ep("BT") };
        h.Router.SetDesired("BT");
        h.Router.NotifyOpened("BT", asFallback: false);
        h.Mon.Active = new() { Ep("A", def: true) };
        h.Mon.Raise(new AudioDeviceEvent(AudioDeviceEventKind.DeviceRemoved, "BT", 0));   // lost → awaiting BT
        h.Router.NotifyOpened("A", asFallback: true);                                     // opened default fallback
        h.Reroutes.Clear(); h.Notices.Clear();

        // Flapping add/remove/add burst inside the window → exactly one reroute back to BT.
        h.Mon.Active = new() { Ep("A", def: true), Ep("BT") };
        h.Mon.Raise(new AudioDeviceEvent(AudioDeviceEventKind.DeviceAdded, "BT", 0));
        h.Mon.Active = new() { Ep("A", def: true) };
        h.Mon.Raise(new AudioDeviceEvent(AudioDeviceEventKind.DeviceRemoved, "BT", 0));
        h.Mon.Active = new() { Ep("A", def: true), Ep("BT") };
        h.Mon.Raise(new AudioDeviceEvent(AudioDeviceEventKind.DeviceStateChanged, "BT", 1));

        Assert.Single(h.Reroutes);
        Assert.Equal("BT", h.Reroutes[0].TargetDeviceId);
        Assert.False(h.Reroutes[0].PauseFirst);
        Assert.Contains(h.Notices, n => n.Kind == OutputDeviceNoticeKind.DeviceRestored);
    }

    [Fact]
    public void OpenFailure_RetryLadder_ThenOutputFailed_LaterEventResetsAndRetries()
    {
        var h = new Harness();
        h.Mon.DefaultId = "A"; h.Mon.Active = new() { Ep("A", def: true), Ep("BT") };
        h.Router.NotifyOpened("A", asFallback: false);
        h.Router.SetDesired("BT");
        Assert.Equal("BT", h.Reroutes[^1].TargetDeviceId);

        // 3 retries (250ms / 1s / 3s), each fired by Tick past its deadline; the 4th failure exhausts the ladder.
        h.Router.ReportOpenFailed(-1);
        h.Now = 250; h.Router.Tick(h.Now);
        h.Router.ReportOpenFailed(-1);
        h.Now = 1250; h.Router.Tick(h.Now);
        h.Router.ReportOpenFailed(-1);
        h.Now = 4250; h.Router.Tick(h.Now);
        h.Router.ReportOpenFailed(-1);

        Assert.Contains(h.Notices, n => n.Kind == OutputDeviceNoticeKind.OutputFailed);

        // A later device event resets the (exhausted) ladder and a fresh reroute is emitted.
        h.Reroutes.Clear();
        h.Mon.Raise(new AudioDeviceEvent(AudioDeviceEventKind.DeviceStateChanged, "BT", 1));
        Assert.Contains(h.Reroutes, r => r.TargetDeviceId == "BT");
    }

    [Fact]
    public void EventsBeforeInit_AndAfterDispose_ProduceNoOutput()
    {
        var h = new Harness();
        h.Mon.DefaultId = "A"; h.Mon.Active = new() { Ep("A", def: true) };
        h.Mon.Raise(new AudioDeviceEvent(AudioDeviceEventKind.DefaultRenderChanged, "A", 0));   // before first Init
        Assert.Empty(h.Reroutes);
        Assert.Empty(h.Notices);

        h.Router.NotifyOpened("A", asFallback: false);
        h.Router.Dispose();
        h.Mon.Raise(new AudioDeviceEvent(AudioDeviceEventKind.DefaultRenderChanged, "B", 0));   // after Dispose
        Assert.Empty(h.Reroutes);
    }

    [Fact]
    public void SetDesired_DuringPendingFallback_CancelsAutoReturn()
    {
        var h = new Harness();
        h.Mon.DefaultId = "A"; h.Mon.Active = new() { Ep("A", def: true), Ep("BT") };
        h.Router.SetDesired("BT");
        h.Router.NotifyOpened("BT", asFallback: false);
        h.Mon.Active = new() { Ep("A", def: true) };
        h.Mon.Raise(new AudioDeviceEvent(AudioDeviceEventKind.DeviceRemoved, "BT", 0));   // lost → awaiting BT
        h.Router.NotifyOpened("A", asFallback: true);
        h.Reroutes.Clear(); h.Notices.Clear();

        h.Router.SetDesired("A");   // user pins the default → cancels the BT auto-return

        h.Mon.Active = new() { Ep("A", def: true), Ep("BT") };
        h.Mon.Raise(new AudioDeviceEvent(AudioDeviceEventKind.DeviceAdded, "BT", 0));
        Assert.DoesNotContain(h.Reroutes, r => r.TargetDeviceId == "BT");   // no auto-return to BT
    }

    [Fact]
    public void RerouteToAlreadyOpenedId_IsNoOp()
    {
        var h = new Harness();
        h.Mon.DefaultId = "A"; h.Mon.Active = new() { Ep("A", def: true), Ep("BT") };
        h.Router.SetDesired("BT");
        h.Router.NotifyOpened("BT", asFallback: false);
        h.Reroutes.Clear();

        h.Router.SetDesired("BT");   // same as opened → no-op
        Assert.Empty(h.Reroutes);

        h.Mon.Raise(new AudioDeviceEvent(AudioDeviceEventKind.DeviceStateChanged, "BT", 1));   // profile flap on the same device
        Assert.Empty(h.Reroutes);
    }
}
