// The prefilter policy: pattern hits first, fallback modules last, a pin overrides everything — ported from 0.2.9
// Wavee.Tests/Modules/ModuleRouterTests.cs onto `Modules.ModuleRouter`.

using Xunit;
using static Wavee.Modules;

namespace Wavee.Tests;

public class ModuleRouterTests
{
    static readonly InstalledModule YouTube = ModuleFixtures.Installed(
        ModuleFixtures.Manifest("wavee.youtube", capabilities: ["playback", "match"], urlPatterns: ["youtube.com", "youtu.be"]));

    static readonly InstalledModule Twitch = ModuleFixtures.Installed(
        ModuleFixtures.Manifest("wavee.twitch", capabilities: ["playback", "match"], urlPatterns: ["twitch.tv"]));

    static readonly InstalledModule Radio = ModuleFixtures.Installed(
        ModuleFixtures.Manifest("wavee.radio", capabilities: ["playback", "match", "fallback"], urlPatterns: []));

    static readonly InstalledModule NoMatch = ModuleFixtures.Installed(
        ModuleFixtures.Manifest("wavee.silent", capabilities: ["playback"], urlPatterns: ["youtube.com"]));

    static IReadOnlyList<InstalledModule> All => [YouTube, Twitch, Radio, NoMatch];

    static string[] Ids(IReadOnlyList<InstalledModule> modules) => modules.Select(m => m.Id).ToArray();

    [Fact]
    public void PatternHit_SelectsOnlyThatModule()
        => Assert.Equal(["wavee.youtube"], Ids(ModuleRouter.Prefilter(All, "https://www.youtube.com/watch?v=abc", null)));

    [Fact]
    public void PatternHit_IgnoresAModuleThatDoesNotDeclareMatch()
        => Assert.DoesNotContain("wavee.silent", Ids(ModuleRouter.Prefilter(All, "https://www.youtube.com/watch?v=abc", null)));

    [Fact]
    public void NoPatternHit_AsksEveryMatchModule_WithFallbackLast()
        => Assert.Equal(["wavee.youtube", "wavee.twitch", "wavee.radio"],
            Ids(ModuleRouter.Prefilter(All, "https://stream.example.test/live.mp3", null)));

    [Fact]
    public void PinnedModule_IsTheOnlyCandidate()
        => Assert.Equal(["wavee.twitch"], Ids(ModuleRouter.Prefilter(All, "https://www.youtube.com/watch?v=abc", "wavee.twitch")));

    [Fact]
    public void PinnedModule_ThatCannotMatch_SelectsNothing()
        => Assert.Empty(ModuleRouter.Prefilter(All, "https://www.youtube.com/watch?v=abc", "wavee.silent"));

    [Fact]
    public void PinnedModule_ThatIsNotInstalled_SelectsNothing()
        => Assert.Empty(ModuleRouter.Prefilter(All, "https://x.test/a", "wavee.absent"));

    [Fact]
    public void BlankInput_SelectsNothing()
    {
        Assert.Empty(ModuleRouter.Prefilter(All, "   ", null));
        Assert.Empty(ModuleRouter.Prefilter(All, null, null));
    }

    [Theory]
    [InlineData("https://WWW.YouTube.com/watch?v=x", "www.youtube.com")]
    [InlineData("http://twitch.tv/someone", "twitch.tv")]
    [InlineData("spotify:track:abc", "")]
    [InlineData("not a url at all", "")]
    public void HostOf_ParsesOnlyHttpUrls(string input, string expected)
    {
        Assert.Equal(expected, ModuleRouter.HostOf(input));
        Assert.Equal(expected.Length > 0, ModuleRouter.IsHttpUrl(input));
    }

    [Fact]
    public void FallbackOrder_IsStable_InsideEachHalf()
        => Assert.Equal(["wavee.youtube", "wavee.twitch", "wavee.radio"],
            Ids(ModuleRouter.Prefilter(new List<InstalledModule> { Radio, YouTube, Twitch }, "https://elsewhere.test/x", null)));

    [Fact]
    public void Permissions_RideTheCapabilityListUnderThePermissionPrefix()
    {
        var granted = ModuleFixtures.Manifest("wavee.spotify", capabilities: ["playback", "permission:auth.spotify"]);
        Assert.True(ModuleCapabilities.HasPermission(granted, "auth.spotify"));
        Assert.False(ModuleCapabilities.HasPermission(granted, "storage.private"));
        Assert.False(ModuleCapabilities.HasPermission(ModuleFixtures.Manifest(), "auth.spotify"));
    }
}
