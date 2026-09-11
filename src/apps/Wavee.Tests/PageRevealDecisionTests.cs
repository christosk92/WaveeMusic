using Xunit;

namespace Wavee.Tests;

public sealed class PageRevealDecisionTests
{
    [Fact]
    public void ColdRevealWaitsForReadyContentToBeIncludedInARenderedFrame()
    {
        var watch = new PageRevealDecision("album:a", null);
        Assert.False(watch.Observe(1, "album:a", null, true, true, false, false));
        // Publication after layout is not evidence that this frame included the content.
        Assert.False(watch.Observe(1, "album:a", null, true, true, false, true));
        Assert.True(watch.Observe(1, "album:a", null, true, true, true, true));
        Assert.False(watch.ReadyAtFirstFrame);
        Assert.False(watch.RetainedUi);
        Assert.False(watch.Observe(1, "album:a", null, true, true, true, true));
    }

    [Fact]
    public void CachedActivationEmitsOnceForItsNewNavigationWithoutAnotherPublication()
    {
        var watch = new PageRevealDecision("home", null);
        Assert.True(watch.Observe(1, "home", null, true, true, true, true));
        Assert.False(watch.Observe(2, "albums", null, false, true, true, true));
        Assert.True(watch.Observe(3, "home", null, true, true, true, true));
        Assert.True(watch.RetainedUi);
        Assert.True(watch.ReadyAtFirstFrame);
        Assert.False(watch.Observe(3, "home", null, true, true, true, true));
    }

    [Theory]
    [InlineData("album:b", null, true)]
    [InlineData("album:a", "other-argument", true)]
    [InlineData("album:a", null, false)]
    public void LateForeignOrParkedPageNeverClaimsTheCurrentNavigation(string route, string? arg, bool active)
    {
        var watch = new PageRevealDecision("album:a", null);
        Assert.False(watch.Observe(2, route, arg, active, true, true, true));
        Assert.True(watch.Observe(3, "album:a", null, true, true, true, true));
        Assert.False(watch.RetainedUi); // The foreign frame did not activate this observer.
    }

    [Fact]
    public void SkeletonFailureAndNonRenderedFramesCannotReveal()
    {
        var watch = new PageRevealDecision("liked", null);
        Assert.False(watch.Observe(1, "liked", null, true, false, true, true));
        Assert.False(watch.Observe(1, "liked", null, true, true, true, false));
        Assert.False(watch.Observe(1, "liked", null, true, true, false, false));
        Assert.True(watch.Observe(1, "liked", null, true, true, true, true));
    }

    [Fact]
    public void NoNavigationAnchorCannotReveal_EmptyAndNullArgsAreEquivalent()
    {
        var watch = new PageRevealDecision("albums", null);
        Assert.False(watch.Observe(0, "albums", null, true, true, true, true));
        Assert.True(watch.Observe(1, "albums", "", true, true, true, true));
    }

    [Fact]
    public void RetainedPendingPageStillWaitsForContentOnItsNextActivation()
    {
        var watch = new PageRevealDecision("artist:a", "tab");
        Assert.False(watch.Observe(1, "artist:a", "tab", true, true, false, false));
        Assert.False(watch.Observe(3, "artist:a", "tab", true, true, false, true));
        Assert.True(watch.Observe(3, "artist:a", "tab", true, true, true, true));
        Assert.True(watch.RetainedUi);
        Assert.False(watch.ReadyAtFirstFrame);
    }
}
