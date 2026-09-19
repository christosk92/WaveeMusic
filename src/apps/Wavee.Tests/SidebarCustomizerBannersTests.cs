using Wavee;
using Xunit;

namespace Wavee.Tests;

// The customizer's banners and saved dot (ch 26 W7-W10): one voice per story, plus the two 0.3 fixes — a newer
// document is not called corrupt and is never offered "Start fresh".
public class SidebarCustomizerBannersTests
{
    static SidebarWriteResult Fault(SidebarPersistenceFault fault) => new(false, fault, 0, 0, null);

    [Theory]
    [InlineData(SidebarLoadFault.None, SidebarLoadBanner.None)]
    [InlineData(SidebarLoadFault.Corrupt, SidebarLoadBanner.Unreadable)]
    [InlineData(SidebarLoadFault.Unreadable, SidebarLoadBanner.Unreadable)]
    [InlineData(SidebarLoadFault.TooNew, SidebarLoadBanner.TooNew)]
    public void ALoadFaultPicksItsBanner(SidebarLoadFault fault, SidebarLoadBanner expected)
    {
        Assert.Equal(expected, SidebarCustomizerBanners.LoadBanner(fault, dismissed: false));
        Assert.Equal(SidebarLoadBanner.None, SidebarCustomizerBanners.LoadBanner(fault, dismissed: true));
    }

    [Fact]
    public void OnlyAnUnreadableFileIsOfferedStartFresh()
    {
        Assert.True(SidebarCustomizerBanners.OffersStartFresh(SidebarLoadBanner.Unreadable));
        Assert.False(SidebarCustomizerBanners.OffersStartFresh(SidebarLoadBanner.TooNew));
        Assert.False(SidebarCustomizerBanners.OffersStartFresh(SidebarLoadBanner.None));
    }

    [Theory]
    [InlineData(SidebarPersistenceFault.ConfigTooLarge, true)]
    [InlineData(SidebarPersistenceFault.DocumentTooLarge, true)]
    [InlineData(SidebarPersistenceFault.IoFailure, true)]
    [InlineData(SidebarPersistenceFault.Corrupt, false)]
    [InlineData(SidebarPersistenceFault.TooNew, false)]
    [InlineData(SidebarPersistenceFault.Unreadable, false)]
    public void OnlyTheThreeWriteFaultsRaiseTheErrorBar(SidebarPersistenceFault fault, bool shows)
        => Assert.Equal(shows, SidebarCustomizerBanners.ShowsSaveFault(Fault(fault), SidebarPersistenceFault.None));

    [Fact]
    public void ADismissedFaultStaysQuietButADifferentOneReshows()
    {
        var io = Fault(SidebarPersistenceFault.IoFailure);
        Assert.False(SidebarCustomizerBanners.ShowsSaveFault(io, SidebarPersistenceFault.IoFailure));
        Assert.True(SidebarCustomizerBanners.ShowsSaveFault(Fault(SidebarPersistenceFault.DocumentTooLarge),
                                                            SidebarPersistenceFault.IoFailure));
        Assert.False(SidebarCustomizerBanners.ShowsSaveFault(SidebarWriteResult.Healthy, SidebarPersistenceFault.None));
    }

    [Fact]
    public void AHealthyWriteForgetsTheDismissal()
    {
        Assert.Equal(SidebarPersistenceFault.None,
            SidebarCustomizerBanners.DismissedAfter(SidebarWriteResult.Healthy, SidebarPersistenceFault.IoFailure));
        Assert.Equal(SidebarPersistenceFault.IoFailure,
            SidebarCustomizerBanners.DismissedAfter(Fault(SidebarPersistenceFault.IoFailure), SidebarPersistenceFault.IoFailure));
    }

    [Fact]
    public void TheSavedDotIsHealthyOnly()
    {
        Assert.True(SidebarCustomizerBanners.ShowsSavedDot(SidebarWriteResult.Healthy));
        Assert.False(SidebarCustomizerBanners.ShowsSavedDot(Fault(SidebarPersistenceFault.Corrupt)));
    }

    [Fact]
    public void TheDetailRidesAsAParenthetical()
    {
        Assert.Equal("", SidebarCustomizerBanners.DetailSuffix(null));
        Assert.Equal("", SidebarCustomizerBanners.DetailSuffix(""));
        Assert.Equal("  (Document is 3 B)", SidebarCustomizerBanners.DetailSuffix("Document is 3 B"));
    }
}
