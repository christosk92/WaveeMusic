using Wavee.Backend;
using Xunit;

namespace Wavee.Tests;

// Bug 3 — video duration adopted from the previous video. See VideoDurationAdoption's doc comment for the log
// evidence (a late DurationKnown from a source the host already switched away from landing on the new track).
public class VideoDurationAdoptionTests
{
    [Fact]
    public void SameKey_Adopts()
        => Assert.True(VideoDurationAdoption.ShouldAdopt("key-a", "key-a"));

    [Fact]
    public void DifferentKnownKey_Refuses()
        => Assert.False(VideoDurationAdoption.ShouldAdopt("key-a", "key-b"));

    [Fact]
    public void NoCurrentKey_FailsOpen_Adopts()
    {
        // Nothing is currently known (an unwired build, or a cold host) — must not silently drop every duration.
        Assert.True(VideoDurationAdoption.ShouldAdopt(null, "key-a"));
        Assert.True(VideoDurationAdoption.ShouldAdopt("", "key-a"));
    }
}
