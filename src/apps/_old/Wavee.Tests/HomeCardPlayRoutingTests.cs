using System;
using Wavee.Core;
using Wavee.Features.Home;
using Xunit;

namespace Wavee.Tests;

public sealed class HomeCardPlayRoutingTests
{
    [Theory]
    [InlineData(HomeCardKind.Track)]
    [InlineData(HomeCardKind.Episode)]
    public void SingleItems_PlayAsTheItem(HomeCardKind kind)
        => Assert.True(HomeCardPlayRouting.PlaysAsItem(kind));

    [Fact]
    public void EveryOtherKind_PlaysAsAContext()
    {
        foreach (HomeCardKind kind in Enum.GetValues<HomeCardKind>())
        {
            if (kind is HomeCardKind.Track or HomeCardKind.Episode) continue;
            Assert.False(HomeCardPlayRouting.PlaysAsItem(kind));
        }
    }
}
