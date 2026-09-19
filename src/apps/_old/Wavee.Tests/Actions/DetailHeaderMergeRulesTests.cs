using Wavee;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests.Actions;

/// <summary>
/// DetailPage's initial-load merge rule (cause 1 of the stale-daylist-header defect). A rolling-identity container
/// (a daylist) trusts the fresh nav preview for Title/Cover on the first paint; every other container keeps trusting
/// the composed store row, because ITS preview carries no such freshness guarantee.
/// </summary>
public class DetailHeaderMergeRulesTests
{
    [Fact]
    public void IsRollingIdentity_TrueWhenEitherSideCarriesAWindow()
    {
        Assert.True(DetailHeaderMergeRules.IsRollingIdentity(loadedExpiresAtMs: 123, previewExpiresAtMs: 0));
        Assert.True(DetailHeaderMergeRules.IsRollingIdentity(loadedExpiresAtMs: 0, previewExpiresAtMs: 456));
        Assert.False(DetailHeaderMergeRules.IsRollingIdentity(loadedExpiresAtMs: 0, previewExpiresAtMs: 0));
    }

    [Fact]
    public void ResolveTitle_RollingIdentity_PrefersThePreview()
        => Assert.Equal("beat drop 165 bpm friday morning", DetailHeaderMergeRules.ResolveTitle(
            rollingIdentity: true,
            loadedTitle: "contemporary dance breakdown wednesday night",
            previewTitle: "beat drop 165 bpm friday morning"));

    [Fact]
    public void ResolveTitle_RollingIdentity_ButNoPreviewTitle_KeepsTheLoadedOne()
        => Assert.Equal("contemporary dance breakdown wednesday night", DetailHeaderMergeRules.ResolveTitle(
            rollingIdentity: true,
            loadedTitle: "contemporary dance breakdown wednesday night",
            previewTitle: null));

    [Fact]
    public void ResolveTitle_NotRollingIdentity_KeepsTheComposedRow_EvenWithAPreviewTitle()
        => Assert.Equal("Composed Name", DetailHeaderMergeRules.ResolveTitle(
            rollingIdentity: false,
            loadedTitle: "Composed Name",
            previewTitle: "Stale Card Name"));

    [Fact]
    public void ResolveIncomingCover_RollingIdentity_PrefersThePreviewCover()
    {
        var loaded = new Image("https://i.scdn.co/image/generic-editorial");
        var preview = new Image("https://i.scdn.co/image/morning-xl");

        Assert.Same(preview, DetailHeaderMergeRules.ResolveIncomingCover(rollingIdentity: true, loaded, preview));
    }

    [Fact]
    public void ResolveIncomingCover_RollingIdentity_ButNoPreviewCover_KeepsTheLoadedOne()
    {
        var loaded = new Image("https://i.scdn.co/image/generic-editorial");

        Assert.Same(loaded, DetailHeaderMergeRules.ResolveIncomingCover(rollingIdentity: true, loaded, previewCover: null));
    }

    [Fact]
    public void ResolveIncomingCover_NotRollingIdentity_KeepsTheLoadedCover_EvenWithAPreviewCover()
    {
        var loaded = new Image("https://i.scdn.co/image/composed");
        var preview = new Image("https://i.scdn.co/image/stale-card");

        Assert.Same(loaded, DetailHeaderMergeRules.ResolveIncomingCover(rollingIdentity: false, loaded, preview));
    }
}
