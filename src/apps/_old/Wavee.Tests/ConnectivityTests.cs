using System.Collections.Generic;
using Wavee.Backend;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// Phase E: the observable dealer-socket status (so a network drop is SEEN, not silent). The transport's half-open
// watchdog + failover are live-only (verified by the --connect-live probe); the signal plumbing is unit-tested here.
public class ConnectivityTests
{
    [Fact]
    public void Set_EmitsOnChange_AndDedupsSameStatus()
    {
        var c = new Connectivity();
        var seen = new List<ConnectionStatus>();
        using var _ = c.StatusChanged.Subscribe(ConnectHarness.Obs<ConnectionStatus>(seen.Add));
        seen.Clear();   // drop the BehaviorSubject replay of the initial value

        c.Set(ConnectionStatus.Connecting);
        c.Set(ConnectionStatus.Connecting);   // no-op → deduped
        c.Set(ConnectionStatus.Online);

        Assert.Equal(new[] { ConnectionStatus.Connecting, ConnectionStatus.Online }, seen);
        Assert.Equal(ConnectionStatus.Online, c.Status);
    }

    [Fact]
    public void Switchable_ReflectsInner_AndFlowsLiveTransitions()
    {
        var sw = new SwitchableConnectivity(new Connectivity(ConnectionStatus.Offline));
        var seen = new List<ConnectionStatus>();
        using var _ = sw.StatusChanged.Subscribe(ConnectHarness.Obs<ConnectionStatus>(seen.Add));

        var live = new Connectivity(ConnectionStatus.Connecting);
        sw.SetInner(live);                    // go-live → re-emit current status against the live signal
        live.Set(ConnectionStatus.Online);    // subsequent socket transitions flow through the facade
        live.Set(ConnectionStatus.Reconnecting);

        Assert.Equal(ConnectionStatus.Reconnecting, sw.Status);
        Assert.Contains(ConnectionStatus.Connecting, seen);
        Assert.Contains(ConnectionStatus.Online, seen);
        Assert.Contains(ConnectionStatus.Reconnecting, seen);
    }
}
