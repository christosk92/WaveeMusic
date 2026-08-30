using System.Collections.Generic;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// The build's self-knowledge, parsed once from assembly metadata. Every string here reaches a user surface (About hero,
// crash header, copied diagnostics, the GitHub user agent) or a decision (IsDev gates the update prompt entirely, and
// LastRunKey is what "you were updated" compares), so the unstamped/dev cases matter as much as the happy path.
public class WaveeVersionInfoTests
{
    static Dictionary<string, string> Meta(string channel, string quad, string codename = "Breaker",
                                           string commit = "d4227b3", string date = "2026-08-27T10:00:00Z",
                                           string? feed = null, string? baseUrl = null)
    {
        var m = new Dictionary<string, string>
        {
            ["Channel"] = channel,
            ["PackageVersion"] = quad,
            ["Codename"] = codename,
            ["Commit"] = commit,
            ["BuildDate"] = date,
        };
        if (feed is not null) m["FeedRelease"] = feed;
        if (baseUrl is not null) m["UpdateBaseUrl"] = baseUrl;
        return m;
    }

    // ── stable ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AStableRelease_ParsesEveryPart()
    {
        var info = WaveeVersionInfo.Parse("0.2.0+build.17.sha.d4227b3", Meta("stable", "0.2.0.17"));

        Assert.Equal("0.2.0", info.SemVer);                 // build metadata is not identity
        Assert.Equal("0.2.0", info.Core);
        Assert.Null(info.Beta);
        Assert.Equal("0.2.0.17", info.Quad);
        Assert.Equal("Breaker", info.Codename);
        Assert.Equal("stable", info.Channel);
        Assert.Equal("d4227b3", info.Commit);
        Assert.False(info.IsDev);
        Assert.Equal("wavee-stable", info.FeedRelease);
        Assert.Equal("Wavee 0.2.0 “Breaker”", info.Display);
        Assert.Equal("0.2.0.17", info.LastRunKey);
    }

    // ── beta ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ABeta_KeepsItsOrdinal_AndSaysSo()
    {
        var info = WaveeVersionInfo.Parse("0.4.0-beta.2+build.29.sha.abc1234", Meta("beta", "0.4.0.29", codename: "Drift"));

        Assert.Equal("0.4.0-beta.2", info.SemVer);
        Assert.Equal("0.4.0", info.Core);
        Assert.Equal(2, info.Beta);
        Assert.Equal("beta", info.Channel);
        Assert.False(info.IsDev);
        Assert.Equal("Wavee 0.4.0 “Drift” · Beta 2", info.Display);
        Assert.Equal("0.4.0.29", info.LastRunKey);
    }

    [Theory]
    [InlineData("0.4.0-rc.1")]        // not a beta ordinal
    [InlineData("0.4.0-dev")]
    [InlineData("0.4.0-beta")]        // no ".N"
    [InlineData("0.4.0-beta.x")]
    public void OtherPrereleaseSuffixes_HaveNoBetaOrdinal(string informational)
    {
        var info = WaveeVersionInfo.Parse(informational, Meta("beta", "0.4.0.29"));
        Assert.Null(info.Beta);
        Assert.Equal("0.4.0", info.Core);
    }

    // ── dev / unstamped ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ALocalDotnetRun_IsADevBuild()
    {
        var info = WaveeVersionInfo.Parse("0.2.0-dev", new Dictionary<string, string> { ["Codename"] = "Breaker" });

        Assert.Equal("0.2.0-dev", info.SemVer);
        Assert.Equal("0.2.0", info.Core);
        Assert.Equal("", info.Quad);
        Assert.Equal("dev", info.Channel);                 // no Channel metadata at all
        Assert.True(info.IsDev);
        Assert.Equal("Wavee 0.2.0-dev", info.Display);     // no codename theatre on a dev build
        Assert.Equal("0.2.0-dev", info.LastRunKey);        // a semver, so a dev build never "updates"
        Assert.Equal("wavee-stable", info.FeedRelease);
    }

    [Fact]
    public void AStampedBuildWithNoQuad_IsStillDev()
    {
        var info = WaveeVersionInfo.Parse("0.2.0", Meta("stable", quad: ""));
        Assert.True(info.IsDev);
        Assert.Equal("0.2.0", info.LastRunKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoInformationalVersionAtAll_DegradesToDev(string? informational)
    {
        var info = WaveeVersionInfo.Parse(informational, new Dictionary<string, string>());

        Assert.Equal("dev", info.SemVer);
        Assert.Equal("dev", info.Core);
        Assert.Equal("dev", info.Channel);
        Assert.True(info.IsDev);
        Assert.Equal("", info.Codename);
        Assert.Equal("", info.Commit);
        Assert.Equal("", info.BuildDate);
    }

    // ── the strings that leave the process ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void UserAgent_IsAnRfc9110ProductToken()
    {
        var info = WaveeVersionInfo.Parse("0.2.0", Meta("stable", "0.2.0.17"));
        Assert.Equal("Wavee/0.2.0 (build 0.2.0.17; stable; Windows 11; arm64)", info.UserAgent("Windows 11", "arm64"));
    }

    [Fact]
    public void OneLine_CarriesEverythingASupportRequestNeeds()
    {
        var info = WaveeVersionInfo.Parse("0.2.0", Meta("stable", "0.2.0.17"));
        Assert.Equal("Wavee 0.2.0 “Breaker” · build 0.2.0.17 · d4227b3 · 2026-08-27T10:00:00Z · arm64",
                     info.OneLine("Windows 11", "arm64"));
    }

    // ── the feed release (build-time metadata, never an env var) ─────────────────────────────────────────────────────

    [Fact]
    public void AScratchFeed_IsStampedIn_NotSwitchedAtRuntime()
    {
        var info = WaveeVersionInfo.Parse("0.2.0", Meta("stable", "0.2.0.17", feed: "wavee-stable-test"));
        Assert.Equal("wavee-stable-test", info.FeedRelease);
    }

    [Fact]
    public void AnEmptyFeedStamp_FallsBackToStable()
    {
        var info = WaveeVersionInfo.Parse("0.2.0", Meta("stable", "0.2.0.17", feed: ""));
        Assert.Equal("wavee-stable", info.FeedRelease);
    }

    // ── the update base URL (the OTHER half of the feed URL; same build-time-metadata rule) ──────────────────────────
    // The local end-to-end update test packs a build that polls http://127.0.0.1:8099/ instead of GitHub. That has to be
    // a STAMP, not a switch: a shipping package must have no reachable code path that repoints its own update feed.

    [Fact]
    public void AShippingBuild_PollsGitHubsReleaseDownloadRoot()
    {
        var info = WaveeVersionInfo.Parse("0.2.0", Meta("stable", "0.2.0.17"));
        Assert.Equal("https://github.com/christosk92/WaveeMusic/releases/download/", info.UpdateBaseUrl);
        Assert.Equal(WaveeVersionInfo.DefaultUpdateBaseUrl, info.UpdateBaseUrl);
    }

    [Fact]
    public void ALoopbackFeed_IsStampedIn_NotSwitchedAtRuntime()
    {
        var info = WaveeVersionInfo.Parse("0.2.0", Meta("stable", "0.2.0.17", baseUrl: "http://127.0.0.1:8099/"));
        Assert.Equal("http://127.0.0.1:8099/", info.UpdateBaseUrl);
    }

    [Fact]
    public void AStampWithNoTrailingSlash_GetsOne()
    {
        // Every caller concatenates "<release>/<asset>" onto this, so the slash is the store's problem, not theirs.
        var info = WaveeVersionInfo.Parse("0.2.0", Meta("stable", "0.2.0.17", baseUrl: "  http://127.0.0.1:8099  "));
        Assert.Equal("http://127.0.0.1:8099/", info.UpdateBaseUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AnEmptyOrAbsentBaseUrl_FallsBackToTheDefault(string? raw)
    {
        Assert.Equal(WaveeVersionInfo.DefaultUpdateBaseUrl, WaveeVersionInfo.NormalizeUpdateBaseUrl(raw));
        var info = WaveeVersionInfo.Parse("0.2.0", Meta("stable", "0.2.0.17", baseUrl: raw));
        Assert.Equal(WaveeVersionInfo.DefaultUpdateBaseUrl, info.UpdateBaseUrl);
    }

    // ── Display with no codename ──────────────────────────────────────────────────────────────
    // A packaged build whose Codename metadata was never stamped is a real case (a scratch pack, a hand-built MSIX),
    // and it used to print a pair of EMPTY QUOTES in the About hero and the crash header — which reads as a bug in the
    // app rather than a gap in the build stamp.

    [Fact]
    public void APackagedBuildWithNoCodename_PrintsNoEmptyQuotes()
    {
        var info = WaveeVersionInfo.Parse("0.3.0", Meta("stable", "0.3.0.22", codename: ""));

        Assert.False(info.IsDev);
        Assert.Equal("Wavee 0.3.0", info.Display);
        Assert.DoesNotContain("“", info.Display);
        Assert.DoesNotContain("”", info.Display);
    }

    [Fact]
    public void ABetaWithNoCodename_KeepsItsOrdinal_WithoutTheQuotes()
    {
        var info = WaveeVersionInfo.Parse("0.4.0-beta.2", Meta("beta", "0.4.0.29", codename: ""));
        Assert.Equal("Wavee 0.4.0 · Beta 2", info.Display);
    }
}
