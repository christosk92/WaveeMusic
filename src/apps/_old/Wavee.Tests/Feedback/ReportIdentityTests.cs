using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// ReportIdentity.From (Diagnostics/ReportIdentity.cs): the install-source/architecture/Windows-version derivation
// every report channel prefills, over a real WaveeVersionInfo (constructed directly -- its ctor is the record's own).
public class ReportIdentityTests
{
    static WaveeVersionInfo Me(string channel, string quad = "0.2.5.6", string codename = "Breaker", string commit = "7e209e37")
        => new(SemVer: "0.2.5", Core: "0.2.5", Beta: null, Quad: quad, Codename: codename, Channel: channel,
               Commit: commit, BuildDate: "2026-09-01T10:15:00Z");

    // ── InstallSource: dev / store / sideload ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void DevBuild_IsBuiltFromSource_RegardlessOfPackaged()
    {
        var dev = Me(channel: "dev");

        var id = ReportIdentity.From(dev, isPackaged: true, osArch: "X64", osBuild: 26100);

        Assert.Equal(ReportChannels.InstallSources[2], id.InstallSource);   // "Built from source"
    }

    [Fact]
    public void StoreBuild_IsMicrosoftStore_RegardlessOfPackaged()
    {
        var store = Me(channel: "store");

        var id = ReportIdentity.From(store, isPackaged: false, osArch: "X64", osBuild: 26100);

        Assert.Equal(ReportChannels.InstallSources[0], id.InstallSource);   // "Microsoft Store"
        Assert.Equal("install: store", id.InstallLabel);
    }

    [Fact]
    public void PackagedNonDevNonStoreBuild_IsSideloaded()
    {
        var stable = Me(channel: "stable");

        var id = ReportIdentity.From(stable, isPackaged: true, osArch: "X64", osBuild: 26100);

        Assert.Equal(ReportChannels.InstallSources[1], id.InstallSource);   // "Sideloaded (.appinstaller or .msix from GitHub)"
        Assert.Equal("install: sideload", id.InstallLabel);
    }

    [Fact]
    public void UnpackagedNonDevNonStoreBuild_IsBuiltFromSource()
    {
        var stable = Me(channel: "stable");

        var id = ReportIdentity.From(stable, isPackaged: false, osArch: "X64", osBuild: 26100);

        Assert.Equal(ReportChannels.InstallSources[2], id.InstallSource);
        Assert.Equal("", id.InstallLabel);   // no "install: source" label exists
    }

    // ── Architecture ───────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("X64", "x64", "arch: x64")]
    [InlineData("Arm64", "ARM64", "arch: arm64")]
    [InlineData("X86", "Not sure", "")]
    public void Architecture_MapsTheKnownRuntimeArchitectures(string osArch, string expectedArch, string expectedLabel)
    {
        var id = ReportIdentity.From(Me("stable"), isPackaged: true, osArch: osArch, osBuild: 26100);

        Assert.Equal(expectedArch, id.Architecture);
        Assert.Equal(expectedLabel, id.ArchLabel);
    }

    // ── Windows version ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Build19045_IsWindows10()
    {
        var id = ReportIdentity.From(Me("stable"), isPackaged: true, osArch: "X64", osBuild: 19045);
        Assert.Equal("Windows 10 (build 19045)", id.WindowsVersion);
    }

    [Fact]
    public void Build26100_IsWindows11()
    {
        var id = ReportIdentity.From(Me("stable"), isPackaged: true, osArch: "X64", osBuild: 26100);
        Assert.Equal("Windows 11 (build 26100)", id.WindowsVersion);
    }

    [Fact]
    public void Build22000_TheWindows11Floor_IsWindows11()
    {
        var id = ReportIdentity.From(Me("stable"), isPackaged: true, osArch: "X64", osBuild: 22000);
        Assert.Equal("Windows 11 (build 22000)", id.WindowsVersion);
    }

    [Fact]
    public void Build21999_JustBelowTheFloor_IsWindows10()
    {
        var id = ReportIdentity.From(Me("stable"), isPackaged: true, osArch: "X64", osBuild: 21999);
        Assert.Equal("Windows 10 (build 21999)", id.WindowsVersion);
    }

    // ── VersionLine + passthrough fields ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void VersionLine_CombinesSemverCodenameQuadAndCommit()
    {
        var id = ReportIdentity.From(Me("stable", quad: "0.2.5.6", codename: "Breaker", commit: "7e209e37"),
            isPackaged: true, osArch: "X64", osBuild: 26100);

        Assert.Equal("0.2.5 Breaker (0.2.5.6) · 7e209e37", id.VersionLine);
        Assert.Equal("0.2.5.6", id.Quad);
        Assert.Equal("7e209e37", id.Commit);
        Assert.Equal("stable", id.Channel);
    }

    [Fact]
    public void VersionLine_DropsMissingPartsCleanly()
    {
        var me = new WaveeVersionInfo(SemVer: "0.3.0-dev", Core: "0.3.0", Beta: null, Quad: "", Codename: "",
            Channel: "dev", Commit: "", BuildDate: "");

        var id = ReportIdentity.From(me, isPackaged: false, osArch: "X64", osBuild: 26100);

        Assert.Equal("0.3.0-dev", id.VersionLine);
        Assert.Equal("", id.Quad);
        Assert.Equal("", id.Commit);
    }
}
