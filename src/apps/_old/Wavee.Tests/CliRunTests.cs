using Wavee;
using Xunit;

namespace Wavee.Tests;

public class CliRunTests
{
    [Theory]
    [InlineData("--backend-selftest")]
    [InlineData("--qr-dump")]
    [InlineData("--spotify-login")]
    [InlineData("--spotify-metadata")]
    [InlineData("--spotify-video-manifest")]
    [InlineData("--spotify-video-traits")]
    [InlineData("--spotify-playlist")]
    [InlineData("--spotify-rootlist")]
    [InlineData("--spotify-collection")]
    [InlineData("--spotify-sync")]
    [InlineData("--connect-live")]
    public void IsHeadless_true_for_every_probe_flag(string flag)
    {
        Assert.True(CliRun.IsHeadless([flag]));
    }

    [Theory]
    [InlineData("--fake")]
    [InlineData("--spotify-syncx")]
    public void IsHeadless_false_for_non_probe_flags(string flag)
    {
        Assert.False(CliRun.IsHeadless([flag]));
    }

    [Fact]
    public void IsHeadless_false_for_screenshot_with_arg()
    {
        Assert.False(CliRun.IsHeadless(["--screenshot", "x"]));
    }

    [Fact]
    public void IsHeadless_false_for_relaunch_after_with_arg()
    {
        Assert.False(CliRun.IsHeadless(["--relaunch-after", "1"]));
    }

    [Fact]
    public void IsHeadless_false_for_empty_args()
    {
        Assert.False(CliRun.IsHeadless([]));
    }

    [Fact]
    public void IsHeadless_never_prefix_matches()
    {
        Assert.False(CliRun.IsHeadless(["--spotify-syncx"]));
    }

    [Fact]
    public void FlagOf_returns_the_first_flag_present()
    {
        Assert.Equal("--spotify-metadata", CliRun.FlagOf(["--fake", "--spotify-metadata", "spotify:track:1"]));
    }

    [Fact]
    public void FlagOf_returns_null_when_no_flag_present()
    {
        Assert.Null(CliRun.FlagOf(["--fake", "--screenshot", "x"]));
    }
}
