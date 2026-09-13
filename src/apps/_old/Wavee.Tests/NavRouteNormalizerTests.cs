using FluentGpu.Controls;
using Wavee.Features.Browse;
using Xunit;

namespace Wavee.Tests;

public class NavRouteNormalizerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptySearch_BecomesBrowseHome(string? arg)
    {
        var r = NavRouteNormalizer.Apply("search", arg);
        Assert.Equal(BrowseRoutes.Home, r.Name);
        Assert.Null(r.Arg);
    }

    [Fact]
    public void NonEmptySearch_IsUnchanged()
    {
        var r = NavRouteNormalizer.Apply("search", "  radiohead  ");
        Assert.Equal("search", r.Name);
        Assert.Equal("  radiohead  ", r.Arg);
    }

    [Fact]
    public void LegacyRecents_BecomesRecents()
    {
        var r = NavRouteNormalizer.Apply(NavRouteNormalizer.LegacyRecentsRoute, "kept");
        Assert.Equal("recents", r.Name);
        Assert.Equal("kept", r.Arg);
    }

    [Fact]
    public void OtherRoutes_PassThrough()
    {
        var home = NavRouteNormalizer.Apply("home", null);
        Assert.Equal("home", home.Name);
        Assert.Null(home.Arg);

        var album = NavRouteNormalizer.Apply(new Route("album:x", "Kid A"));
        Assert.Equal("album:x", album.Name);
        Assert.Equal("Kid A", album.Arg);
    }
}
