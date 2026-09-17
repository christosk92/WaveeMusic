// ── Wavee.Tests/DetailHeaderMergeRulesTests.cs — the rolling-identity initial-load merge ───────────────────────────
//
// Ported from _old/Wavee.Tests/Actions/DetailHeaderMergeRulesTests.cs onto `Detail.HeaderMerge` (Entities/Detail.cs).
// Every assertion is 0.2.9's; the cover is a url string in 0.3 (was an `Image` record), so `Assert.Same` compares the
// url instances the rule was handed.
//
// A rolling-identity container (a daylist) trusts the fresh nav preview for title/cover on the first paint; every other
// container keeps the composed row, because ITS preview carries no such freshness guarantee.

using Xunit;
using HeaderMerge = Wavee.Detail.HeaderMerge;

namespace Wavee.Tests;

public class DetailHeaderMergeRulesTests
{
    [Fact]
    public void IsRollingIdentity_TrueWhenEitherSideCarriesAWindow()
    {
        Assert.True(HeaderMerge.IsRollingIdentity(loadedExpiresAtMs: 123, previewExpiresAtMs: 0));
        Assert.True(HeaderMerge.IsRollingIdentity(loadedExpiresAtMs: 0, previewExpiresAtMs: 456));
        Assert.False(HeaderMerge.IsRollingIdentity(loadedExpiresAtMs: 0, previewExpiresAtMs: 0));
    }

    [Fact]
    public void ResolveTitle_RollingIdentity_PrefersThePreview()
        => Assert.Equal("beat drop 165 bpm friday morning", HeaderMerge.ResolveTitle(
            rollingIdentity: true,
            loadedTitle: "contemporary dance breakdown wednesday night",
            previewTitle: "beat drop 165 bpm friday morning"));

    [Fact]
    public void ResolveTitle_RollingIdentity_ButNoPreviewTitle_KeepsTheLoadedOne()
        => Assert.Equal("contemporary dance breakdown wednesday night", HeaderMerge.ResolveTitle(
            rollingIdentity: true,
            loadedTitle: "contemporary dance breakdown wednesday night",
            previewTitle: null));

    [Fact]
    public void ResolveTitle_NotRollingIdentity_KeepsTheComposedRow_EvenWithAPreviewTitle()
        => Assert.Equal("Composed Name", HeaderMerge.ResolveTitle(
            rollingIdentity: false,
            loadedTitle: "Composed Name",
            previewTitle: "Stale Card Name"));

    [Fact]
    public void ResolveIncomingCover_RollingIdentity_PrefersThePreviewCover()
    {
        const string loaded = "https://i.scdn.co/image/generic-editorial";
        const string preview = "https://i.scdn.co/image/morning-xl";

        Assert.Same(preview, HeaderMerge.ResolveIncomingCover(rollingIdentity: true, loaded, preview));
    }

    [Fact]
    public void ResolveIncomingCover_RollingIdentity_ButNoPreviewCover_KeepsTheLoadedOne()
    {
        const string loaded = "https://i.scdn.co/image/generic-editorial";

        Assert.Same(loaded, HeaderMerge.ResolveIncomingCover(rollingIdentity: true, loaded, previewCoverUrl: null));
    }

    [Fact]
    public void ResolveIncomingCover_NotRollingIdentity_KeepsTheLoadedCover_EvenWithAPreviewCover()
    {
        const string loaded = "https://i.scdn.co/image/composed";
        const string preview = "https://i.scdn.co/image/stale-card";

        Assert.Same(loaded, HeaderMerge.ResolveIncomingCover(rollingIdentity: false, loaded, preview));
    }

    /// <summary>A blank preview url is no cover (the 0.2.9 record could not be blank-but-present), so it never beats the
    /// loaded one.</summary>
    [Fact]
    public void ResolveIncomingCover_RollingIdentity_BlankPreviewUrl_KeepsTheLoadedOne()
    {
        const string loaded = "https://i.scdn.co/image/generic-editorial";

        Assert.Same(loaded, HeaderMerge.ResolveIncomingCover(rollingIdentity: true, loaded, previewCoverUrl: ""));
    }
}
