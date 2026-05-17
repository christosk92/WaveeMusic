using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Wavee.UI.Services.Infra;
using Xunit;

namespace Wavee.UI.Tests.Services.Infra;

public sealed class ReloadCoordinatorTests
{
    [Fact]
    public async Task SinglePublish_TriggersReloadOnce()
    {
        using var bus = new ChangeBus();
        var coordinator = new ReloadCoordinator(bus);
        var calls = 0;

        using var _ = coordinator.RegisterReload(
            ChangeScope.Library,
            _ => { Interlocked.Increment(ref calls); return Task.CompletedTask; },
            "test");

        bus.Publish(ChangeScope.Library);
        await Task.Delay(ChangeBus.CoalesceWindow + TimeSpan.FromMilliseconds(100));

        calls.Should().Be(1);
    }

    [Fact]
    public async Task DifferentScope_DoesNotTriggerReload()
    {
        using var bus = new ChangeBus();
        var coordinator = new ReloadCoordinator(bus);
        var calls = 0;

        using var _ = coordinator.RegisterReload(
            ChangeScope.Playlists,
            _ => { Interlocked.Increment(ref calls); return Task.CompletedTask; },
            "test");

        bus.Publish(ChangeScope.Library);
        await Task.Delay(ChangeBus.CoalesceWindow + TimeSpan.FromMilliseconds(100));

        calls.Should().Be(0);
    }

    [Fact]
    public async Task RapidPublishes_CancelInFlightReload()
    {
        using var bus = new ChangeBus();
        var coordinator = new ReloadCoordinator(bus);
        var inflightCount = 0;
        var cancelCount = 0;

        using var _ = coordinator.RegisterReload(
            ChangeScope.Library,
            async ct =>
            {
                Interlocked.Increment(ref inflightCount);
                try
                {
                    await Task.Delay(500, ct);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref cancelCount);
                    throw;
                }
            },
            "test");

        // First publish starts a slow reload
        bus.Publish(ChangeScope.Library);
        await Task.Delay(ChangeBus.CoalesceWindow + TimeSpan.FromMilliseconds(50));
        inflightCount.Should().Be(1);

        // Second publish cancels the first
        bus.Publish(ChangeScope.Library);
        await Task.Delay(ChangeBus.CoalesceWindow + TimeSpan.FromMilliseconds(100));

        inflightCount.Should().Be(2);
        cancelCount.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task DisposingRegistration_StopsFutureReloads()
    {
        using var bus = new ChangeBus();
        var coordinator = new ReloadCoordinator(bus);
        var calls = 0;

        var registration = coordinator.RegisterReload(
            ChangeScope.Library,
            _ => { Interlocked.Increment(ref calls); return Task.CompletedTask; },
            "test");

        registration.Dispose();

        bus.Publish(ChangeScope.Library);
        await Task.Delay(ChangeBus.CoalesceWindow + TimeSpan.FromMilliseconds(100));

        calls.Should().Be(0);
    }

    [Fact]
    public async Task ReloadException_DoesNotCrashCoordinator()
    {
        using var bus = new ChangeBus();
        var coordinator = new ReloadCoordinator(bus);
        var calls = 0;

        using var _ = coordinator.RegisterReload(
            ChangeScope.Library,
            _ =>
            {
                Interlocked.Increment(ref calls);
                throw new InvalidOperationException("boom");
            },
            "test");

        bus.Publish(ChangeScope.Library);
        await Task.Delay(ChangeBus.CoalesceWindow + TimeSpan.FromMilliseconds(100));

        bus.Publish(ChangeScope.Library);
        await Task.Delay(ChangeBus.CoalesceWindow + TimeSpan.FromMilliseconds(100));

        calls.Should().Be(2);
    }
}
