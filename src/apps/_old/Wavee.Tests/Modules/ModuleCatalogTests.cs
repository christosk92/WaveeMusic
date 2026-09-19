using System.Collections.Generic;
using System.IO;
using Wavee.Backend.Modules;
using Wavee.Sdk;
using Xunit;

namespace Wavee.Tests.Modules;

/// <summary>Discovery across the two roots, the whole manifest gate (with its reasons), and the per-id ranking.</summary>
public class ModuleCatalogTests
{
    const string Bundled = @"C:\app\modules";
    const string UserStore = @"C:\users\me\AppData\Local\Wavee\modules";

    static Dictionary<string, string> Files() => new(System.StringComparer.OrdinalIgnoreCase);

    static void Put(Dictionary<string, string> files, string dir, ModuleManifest m)
        => files[Path.Combine(dir, ModuleCatalog.ManifestFileName)] = ModuleFixtures.ManifestJson(m);

    [Fact]
    public void Discover_FindsBundledModules()
    {
        var files = Files();
        Put(files, Path.Combine(Bundled, "wavee.youtube"), ModuleFixtures.Manifest("wavee.youtube"));

        var catalog = ModuleCatalog.Discover(Bundled, UserStore, ModuleFixtures.FileSystem(files));

        Assert.Single(catalog.Modules);
        Assert.Equal("wavee.youtube", catalog.Modules[0].Id);
        Assert.True(catalog.Modules[0].Bundled);
        Assert.Empty(catalog.Rejections);
    }

    [Fact]
    public void Discover_UserStoreVersionWins_OverTheBundledFloor()
    {
        var files = Files();
        Put(files, Path.Combine(Bundled, "wavee.radio"), ModuleFixtures.Manifest("wavee.radio", "1.0.0"));
        Put(files, Path.Combine(UserStore, "wavee.radio", "1.4.0"), ModuleFixtures.Manifest("wavee.radio", "1.4.0"));

        var catalog = ModuleCatalog.Discover(Bundled, UserStore, ModuleFixtures.FileSystem(files));

        Assert.Single(catalog.Modules);
        Assert.Equal("1.4.0", catalog.Modules[0].Version);
        Assert.False(catalog.Modules[0].Bundled);
    }

    [Fact]
    public void Discover_KeepsTheBundledFloor_WhenTheUserVersionIsOlder()
    {
        var files = Files();
        Put(files, Path.Combine(Bundled, "wavee.radio"), ModuleFixtures.Manifest("wavee.radio", "2.0.0"));
        Put(files, Path.Combine(UserStore, "wavee.radio", "1.9.9"), ModuleFixtures.Manifest("wavee.radio", "1.9.9"));

        var catalog = ModuleCatalog.Discover(Bundled, UserStore, ModuleFixtures.FileSystem(files));

        Assert.Equal("2.0.0", Assert.Single(catalog.Modules).Version);
    }

    [Fact]
    public void Discover_ProtocolVersionOutranksVersion()
    {
        var files = Files();
        Put(files, Path.Combine(Bundled, "wavee.radio"),
            ModuleFixtures.Manifest("wavee.radio", "1.0.0", protocolVersion: ModuleCatalog.MaxProtocol));
        // A newer VERSION that speaks a protocol this host does not support is refused outright, not preferred.
        Put(files, Path.Combine(UserStore, "wavee.radio", "9.0.0"),
            ModuleFixtures.Manifest("wavee.radio", "9.0.0", protocolVersion: ModuleCatalog.MaxProtocol + 7));

        var catalog = ModuleCatalog.Discover(Bundled, UserStore, ModuleFixtures.FileSystem(files));

        Assert.Equal("1.0.0", Assert.Single(catalog.Modules).Version);
        Assert.Contains(catalog.Rejections, r => r.Reason.Contains("protocolVersion"));
    }

    [Theory]
    [InlineData("nodots", "invalid module id")]
    [InlineData("", "invalid module id")]
    public void Validate_RefusesAnIdThatIsNotAWaveeExtensionKey(string id, string expected)
    {
        var m = ModuleFixtures.Manifest(id);
        string? reason = ModuleCatalog.Validate(m, Path.Combine(Bundled, id), bundled: true);
        Assert.NotNull(reason);
        Assert.Contains(expected, reason);
    }

    [Fact]
    public void Validate_RefusesAnEntryThatEscapesTheModuleDirectory()
    {
        var m = ModuleFixtures.Manifest("wavee.evil", entry: @"..\..\Windows\System32\cmd.exe");
        string? reason = ModuleCatalog.Validate(m, Path.Combine(Bundled, "wavee.evil"), bundled: true);
        Assert.Contains("escapes the module directory", reason);
    }

    [Fact]
    public void Validate_RefusesAnAbsoluteEntry()
    {
        var m = ModuleFixtures.Manifest("wavee.evil", entry: @"C:\Windows\System32\cmd.exe");
        Assert.Contains("escapes the module directory",
            ModuleCatalog.Validate(m, Path.Combine(Bundled, "wavee.evil"), bundled: true));
    }

    [Fact]
    public void Validate_RefusesAnIdThatDoesNotMatchItsDirectory()
    {
        var m = ModuleFixtures.Manifest("wavee.imposter");
        Assert.Contains("does not match manifest id",
            ModuleCatalog.Validate(m, Path.Combine(Bundled, "wavee.youtube"), bundled: true));
    }

    [Fact]
    public void Validate_RefusesAnUnknownSchemaVersion()
        => Assert.Contains("schemaVersion",
            ModuleCatalog.Validate(ModuleFixtures.Manifest("wavee.x", schemaVersion: 0),
                Path.Combine(Bundled, "wavee.x"), bundled: true));

    [Fact]
    public void Validate_AcceptsAGoodManifest()
        => Assert.Null(ModuleCatalog.Validate(ModuleFixtures.Manifest("wavee.youtube"),
            Path.Combine(Bundled, "wavee.youtube"), bundled: true));

    [Fact]
    public void Discover_RecordsADirectoryWithNoManifest()
    {
        var files = Files();
        // A directory that exists only because a sibling file put it on the map.
        files[Path.Combine(Bundled, "wavee.broken", "readme.txt")] = "hello";

        var catalog = ModuleCatalog.Discover(Bundled, UserStore, ModuleFixtures.FileSystem(files));

        Assert.Empty(catalog.Modules);
        Assert.Contains(catalog.Rejections, r => r.Reason.Contains(ModuleCatalog.ManifestFileName));
    }

    [Fact]
    public void Discover_RecordsAnUnreadableManifest()
    {
        var files = Files();
        files[Path.Combine(Bundled, "wavee.bad", ModuleCatalog.ManifestFileName)] = "{ this is not json";

        var catalog = ModuleCatalog.Discover(Bundled, UserStore, ModuleFixtures.FileSystem(files));

        Assert.Empty(catalog.Modules);
        Assert.Contains(catalog.Rejections, r => r.Reason.Contains("unreadable manifest"));
    }

    [Fact]
    public void Discover_MissingRoots_AreEmptyNotAThrow()
    {
        var catalog = ModuleCatalog.Discover(Bundled, UserStore, ModuleFixtures.FileSystem(Files()));
        Assert.Empty(catalog.Modules);
        Assert.Empty(catalog.Rejections);
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("1.2.0", "1.10.0", -1)]
    [InlineData("2.0", "1.999.999", 1)]
    [InlineData("1.0.1", "1.0", 1)]
    public void CompareVersions_IsNumericPerSegment(string a, string b, int expected)
        => Assert.Equal(expected, System.Math.Sign(ModuleCatalog.CompareVersions(a, b)));
}
