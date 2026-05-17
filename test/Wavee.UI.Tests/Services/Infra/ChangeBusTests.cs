using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Wavee.UI.Services.Infra;
using Xunit;

namespace Wavee.UI.Tests.Services.Infra;

public sealed class ChangeBusTests
{
    [Fact]
    public async Task SinglePublish_EmitsOnce()
    {
        using var bus = new ChangeBus();
        var emissions = new List<ChangeScope>();
        using var _ = bus.Changes.Subscribe(emissions.Add);

        bus.Publish(ChangeScope.Library);

        // Wait past the coalesce window
        await Task.Delay(ChangeBus.CoalesceWindow + System.TimeSpan.FromMilliseconds(50));

        emissions.Should().ContainSingle().Which.Should().Be(ChangeScope.Library);
    }

    [Fact]
    public async Task RepeatedSamePublish_CollapsesToOne()
    {
        using var bus = new ChangeBus();
        var emissions = new List<ChangeScope>();
        using var _ = bus.Changes.Subscribe(emissions.Add);

        // Burst — five calls inside the coalesce window
        for (var i = 0; i < 5; i++) bus.Publish(ChangeScope.Library);

        await Task.Delay(ChangeBus.CoalesceWindow + System.TimeSpan.FromMilliseconds(50));

        emissions.Should().ContainSingle().Which.Should().Be(ChangeScope.Library);
    }

    [Fact]
    public async Task DistinctScopes_EmittedSeparately()
    {
        using var bus = new ChangeBus();
        var emissions = new List<ChangeScope>();
        using var _ = bus.Changes.Subscribe(emissions.Add);

        bus.Publish(ChangeScope.Library);
        bus.Publish(ChangeScope.Playlists);
        bus.Publish(ChangeScope.Library);

        await Task.Delay(ChangeBus.CoalesceWindow + System.TimeSpan.FromMilliseconds(50));

        emissions.Should().HaveCount(2);
        emissions.Should().Contain(ChangeScope.Library);
        emissions.Should().Contain(ChangeScope.Playlists);
    }

    [Fact]
    public async Task SequentialBursts_EmitSeparately()
    {
        using var bus = new ChangeBus();
        var emissions = new List<ChangeScope>();
        using var _ = bus.Changes.Subscribe(emissions.Add);

        bus.Publish(ChangeScope.Library);
        await Task.Delay(ChangeBus.CoalesceWindow + System.TimeSpan.FromMilliseconds(50));

        bus.Publish(ChangeScope.Library);
        await Task.Delay(ChangeBus.CoalesceWindow + System.TimeSpan.FromMilliseconds(50));

        emissions.Should().HaveCount(2);
    }

    [Fact]
    public void DisposedBus_PublishIsNoOp()
    {
        var bus = new ChangeBus();
        bus.Dispose();

        // No exception
        bus.Publish(ChangeScope.Library);
    }
}
