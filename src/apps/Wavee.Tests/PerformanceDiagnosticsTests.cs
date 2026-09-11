using System;
using System.Threading.Tasks;
using Wavee.Core.Diagnostics;
using Xunit;

namespace Wavee.Tests;

public sealed class PerformanceDiagnosticsTests
{
    sealed class Clock : TimeProvider
    {
        public long Ticks;
        public DateTimeOffset Utc = DateTimeOffset.UnixEpoch;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Ticks;
        public override DateTimeOffset GetUtcNow() => Utc;
    }

    [Fact]
    public void Navigation_requires_anchor_and_uses_monotonic_time_across_routes()
    {
        var time = new Clock();
        var navigation = new NavigationDiagnosticClock(time);
        Assert.True(double.IsNaN(navigation.SinceNavigationMs));
        navigation.MarkNavigation(); // Timestamp zero is a valid first navigation.
        time.Ticks = 119;
        time.Utc = time.Utc.AddDays(-10);
        Assert.Equal(119d, navigation.SinceNavigationMs);
        navigation.MarkNavigation();
        time.Ticks = 150;
        Assert.Equal(31d, navigation.SinceNavigationMs);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Disposing_one_runtime_does_not_remove_another_with_the_same_owner_name(bool disposeOldFirst)
    {
        var owners = new DiagnosticOwnerRegistry();
        using var oldRuntime = owners.Register("catalog", () => "resident=10");
        using var newRuntime = owners.Register("catalog", () => "resident=20");
        var reports = owners.Sample();
        Assert.Equal(2, reports.Length);
        Assert.NotEqual(reports[0].RegistrationId, reports[1].RegistrationId);
        var removed = disposeOldFirst ? oldRuntime : newRuntime;
        removed.Dispose();
        removed.Dispose();
        Assert.Equal(disposeOldFirst ? "resident=20" : "resident=10", Assert.Single(owners.Sample()).Value);
    }

    [Fact]
    public void Samples_read_current_values_and_report_callback_failures_without_hiding_other_owners()
    {
        var owners = new DiagnosticOwnerRegistry();
        int resident = 10;
        using var catalog = owners.Register("catalog", () => "resident=" + resident);
        using var failed = owners.Register("unavailable", () => throw new InvalidOperationException());
        using var queries = owners.Register("queries", () => "nodes=5");
        Assert.Equal("resident=10", owners.Sample()[0].Value);
        resident = 20;
        var reports = owners.Sample();
        Assert.Equal("resident=20", reports[0].Value);
        Assert.Equal("error=InvalidOperationException", reports[1].Value);
        Assert.Equal("nodes=5", reports[2].Value);
    }

    [Fact]
    public void Callback_can_unregister_itself_and_next_sample_releases_it()
    {
        var owners = new DiagnosticOwnerRegistry();
        IDisposable? registration = null;
        registration = owners.Register("temporary", () => { registration!.Dispose(); return "done=1"; });
        Assert.Equal("done=1", Assert.Single(owners.Sample()).Value);
        Assert.Empty(owners.Sample());
    }

    [Fact]
    public void Sampling_does_not_hold_registry_lock_while_owner_waits_for_other_work()
    {
        var owners = new DiagnosticOwnerRegistry();
        using var registration = owners.Register("catalog", () =>
        {
            var work = Task.Run(() => { using var worker = owners.Register("worker", () => "done=1"); });
            return work.Wait(TimeSpan.FromSeconds(5)) ? "available=1" : "blocked=1";
        });
        Assert.Equal("available=1", Assert.Single(owners.Sample()).Value);
    }
}
