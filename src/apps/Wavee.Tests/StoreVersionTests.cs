using System;
using System.Collections.Generic;
using System.Threading;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// The Store's version shape (M.m.p.0, M >= 1) mapped from Wavee's 0.m.p.<build>, the Store deep links, and the
// store-channel version info / updater behaviour.
public class StoreVersionTests
{
    [Theory]
    [InlineData("0.2.1", 2, "1.2.102.0")]
    [InlineData("0.2.2", 3, "1.2.203.0")]
    [InlineData("0.3.0", 7, "1.3.7.0")]
    [InlineData("1.0.0", 12, "2.0.12.0")]
    public void Quad_LiftsTheMajor_AndFoldsTheBuildIntoThePatch(string core, int build, string expected)
        => Assert.Equal(expected, StoreVersion.Quad(core, build));

    [Fact]
    public void Quad_IsMonotonic_AcrossTheReleaseSequence()
    {
        // Every release bumps the build; the semver only ever goes up. The Store refuses a submission whose version
        // is not greater than the last one, so the sequence must climb.
        var seq = new[] { ("0.2.1", 2), ("0.2.2", 3), ("0.2.10", 4), ("0.3.0", 5), ("1.0.0", 6) };
        Version? last = null;
        foreach (var (core, build) in seq)
        {
            var v = Version.Parse(StoreVersion.Quad(core, build));
            if (last is not null) Assert.True(v > last, core + " build " + build + " did not climb past " + last);
            last = v;
        }
    }

    [Theory]
    [InlineData("1.2.102.0", true)]
    [InlineData("0.2.1.2", false)]    // major 0
    [InlineData("1.2.1.2", false)]    // Store owns the 4th part
    public void IsStoreShaped_ChecksTheTwoRules(string quad, bool ok) => Assert.Equal(ok, StoreVersion.IsStoreShaped(quad));

    [Fact]
    public void Quad_RejectsGarbage()
    {
        Assert.Throws<ArgumentException>(() => StoreVersion.Quad("0.2", 1));
        Assert.Throws<ArgumentException>(() => StoreVersion.Quad("", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => StoreVersion.Quad("0.2.1", -1));
    }

    [Fact]
    public void StoreLinks_OpenTheProductPage()
    {
        Assert.Equal("ms-windows-store://pdp/?productid=9NJPVWTQPT9H", StoreLinks.ProductPage("9NJPVWTQPT9H"));
        Assert.Equal("https://apps.microsoft.com/detail/9NJPVWTQPT9H", StoreLinks.WebPage("9NJPVWTQPT9H"));
    }

    [Fact]
    public void VersionInfo_StoreChannel_CarriesTheStoreId()
    {
        var info = WaveeVersionInfo.Parse("0.2.1+store.1.2.102.0.sha.abc1234", new Dictionary<string, string>
        {
            ["Channel"] = "store", ["PackageVersion"] = "1.2.102.0", ["StoreId"] = "9NJPVWTQPT9H", ["Codename"] = "Breaker",
        });
        Assert.True(info.IsStore);
        Assert.Equal("9NJPVWTQPT9H", info.StoreId);
        Assert.Equal("0.2.1", info.Core);
        Assert.False(info.IsDev);

        var feed = WaveeVersionInfo.Parse("0.2.1+build.2", new Dictionary<string, string> { ["Channel"] = "stable", ["PackageVersion"] = "0.2.1.2" });
        Assert.False(feed.IsStore);
        Assert.Equal("", feed.StoreId);
    }

    [Fact]
    public void StoreUpdateService_NeverStages_AndApplyOpensTheStorePage()
    {
        string? opened = null;
        var svc = new StoreUpdateService("9NJPVWTQPT9H", url => opened = url);

        Assert.Equal(AppUpdateState.None, svc.Current.State);
        svc.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None).GetAwaiter().GetResult();
        Assert.Equal(AppUpdateState.None, svc.Current.State);   // a check is not a feed poll here
        Assert.Null(opened);

        svc.ApplyAsync(CancellationToken.None).GetAwaiter().GetResult();
        Assert.Equal("ms-windows-store://pdp/?productid=9NJPVWTQPT9H", opened);
        // Idle on quit means the install-on-quit path never runs for a Store build.
        Assert.False(ShutdownUpdatePolicy.ShouldApply(installOnQuit: true, svc.Current.State));
    }
}
