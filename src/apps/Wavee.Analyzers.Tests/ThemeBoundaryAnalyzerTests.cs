using Wavee.Analyzers.Themes;
using Xunit;
using static Wavee.Analyzers.Tests.Fixtures;

namespace Wavee.Analyzers.Tests;

/// <summary>WVTH002: a theme file may not reference core state.</summary>
public sealed class ThemeCoreReferenceAnalyzerTests
{
    private static string[] Flagged(string path, string source, (string Path, string Content)[]? additional = null)
        => Spans(Run(new ThemeCoreReferenceAnalyzer(), new[] { Src(path, source) }, additional), "WVTH002");

    [Theory]
    [InlineData("Prefs", "Wavee.Prefs.Density")]
    [InlineData("Entities", "Wavee.Entities.Count")]
    [InlineData("Playback", "Wavee.Playback.Position")]
    [InlineData("Spotify", "Wavee.Spotify.Session")]
    [InlineData("Store", "Wavee.Store.Rows")]
    [InlineData("Shell", "Wavee.Shell.Frame")]
    public void CoreStateType_InAStructure_IsFlagged(string expected, string expression)
        => Assert.Contains(expected, Flagged("Themes/Zune/Structure/Rail.cs",
            "namespace Wavee.Themes.Zune { class Rail { int M() => " + expression + "; } }"));

    [Fact]
    public void PlatformSettings_InATheme_IsFlagged()
        => Assert.Equal(new[] { "Settings" }, Flagged("Themes/Zune/Style/Look.cs",
            "namespace Wavee.Themes.Zune { class Look { bool M() => Wavee.Platform.Settings.Has(\"x\"); } }"));

    [Fact]
    public void CommonFile_ReferencingStore_IsFlagged()
        => Assert.Equal(new[] { "Store" }, Flagged("Themes/Common/Block.cs",
            "namespace Wavee.Themes.Common { class Block { int M() => Wavee.Store.Rows; } }"));

    [Fact]
    public void SameCode_InCoreFile_IsClean()
        => Assert.Empty(Flagged("Shell/Rail.cs", "namespace Wavee { class Rail { int M() => Prefs.Density + Entities.Count; } }"));

    [Fact]
    public void ThemeFile_ReferencingDesignAndContracts_IsClean()
        => Assert.Empty(Flagged("Themes/Zune/Structure/Rail.cs",
            "namespace Wavee.Themes.Zune { class Rail { float M(Wavee.IAppSettings s) => Wavee.Design.Gutter; } }"));

    [Fact]
    public void Baseline_ExemptsTheGrandfatheredFile_ByDetail()
        => Assert.Empty(Flagged("Themes/Classic/Structure/LegacyPrefs.cs",
            "namespace Wavee.Themes.Classic { class LegacyPrefs { int M() => Wavee.Prefs.Density; } }",
            new[] { Baseline("WVTH002", "Themes/Classic/Structure/LegacyPrefs.cs :: Prefs") }));

    [Fact]
    public void Baseline_EntryThatMatchesNothing_IsReportedStale()
    {
        var diagnostics = Run(new ThemeCoreReferenceAnalyzer(),
            new[] { Src("Themes/Classic/Structure/LegacyPrefs.cs", "namespace Wavee.Themes.Classic { class LegacyPrefs { } }") },
            new[] { Baseline("WVTH002", "Themes/Classic/Structure/LegacyPrefs.cs") });
        Assert.Single(Harness.Only(diagnostics, "WVTH900"));
    }
}

/// <summary>WVTH003 (S1): no literal looks in a structure or in Common outside Rules.</summary>
public sealed class LiteralLookAnalyzerTests
{
    private static string[] Flagged(string path, string source, (string Path, string Content)[]? additional = null)
        => Spans(Run(new LiteralLookAnalyzer(), new[] { Src(path, source) }, additional), "WVTH003");

    private const string Literals =
        "namespace Wavee.Themes.Zune { class Rail { object M() => new FluentGpu.Dsl.BoxEl { Width = 32f, Opacity = 0.5f }; } }";

    [Fact]
    public void LookLiterals_InAStructure_AreFlagged()
        => Assert.Equal(new[] { "0.5f", "32f" }, Flagged("Themes/Zune/Structure/Rail.cs", Literals));

    [Fact]
    public void LookLiterals_InCommon_AreFlagged()
        => Assert.Equal(new[] { "0.5f", "32f" }, Flagged("Themes/Common/Rail.cs", Literals));

    [Fact]
    public void NegativeLiteral_IsFlagged()
        => Assert.Equal(new[] { "-4f" }, Flagged("Themes/Zune/Structure/Rail.cs",
            "namespace Wavee.Themes.Zune { class Rail { object M() => new FluentGpu.Dsl.BoxEl { Padding = -4f }; } }"));

    [Fact]
    public void NamedArgumentLiteral_IsFlagged()
        => Assert.Equal(new[] { "12" }, Flagged("Themes/Zune/Structure/Rail.cs",
            "namespace Wavee.Themes.Zune { class Rail { int M() => Box(width: 12); static int Box(int width) => width; } }"));

    [Fact]
    public void ColourBuiltFromComponents_IsFlagged()
        => Assert.Equal(new[] { "FluentGpu.Foundation.ColorF.FromRgba(1, 2, 3)", "new FluentGpu.Foundation.ColorF(1f, 1f, 1f, 1f)" },
            Flagged("Themes/Zune/Structure/Rail.cs",
                "namespace Wavee.Themes.Zune { class Rail { object M() => (FluentGpu.Foundation.ColorF.FromRgba(1, 2, 3), new FluentGpu.Foundation.ColorF(1f, 1f, 1f, 1f)); } }"));

    [Fact]
    public void SameLiterals_UnderRules_AreClean()
        => Assert.Empty(Flagged("Themes/Zune/Structure/Rules/RailRules.cs", Literals));

    [Fact]
    public void SameLiterals_InAStyle_AreClean()
        => Assert.Empty(Flagged("Themes/Zune/Style/ZuneStyle.cs", Literals));

    [Fact]
    public void SameLiterals_InCore_AreClean()
        => Assert.Empty(Flagged("Shell/Rail.cs", Literals));

    [Fact]
    public void ZeroOneAndNonLiteralValues_AreClean()
        => Assert.Empty(Flagged("Themes/Zune/Structure/Rail.cs",
            "namespace Wavee.Themes.Zune { class Rail { object M() => new FluentGpu.Dsl.BoxEl { Width = 0f, Opacity = 1f, Padding = Wavee.Design.Gutter, Grow = 3f }; } }"));

    [Fact]
    public void Baseline_IsDisallowed_AndExemptsNothing()
    {
        string source = Literals.Replace("Zune", "Classic");
        string[] unbaselined = Flagged("Themes/Classic/Structure/Rail.cs", source);
        Assert.Equal(unbaselined, Flagged("Themes/Classic/Structure/Rail.cs", source,
            new[] { Baseline("WVTH003", "Themes/Classic/Structure/Rail.cs :: Width") }));

        var diagnostics = Run(new LiteralLookAnalyzer(),
            new[] { Src("Themes/Classic/Structure/Rail.cs", "namespace Wavee.Themes.Classic { class Rail { } }") },
            new[] { Baseline("WVTH003", "Themes/Classic/Structure/Rail.cs :: Width") });
        Assert.Single(Harness.Only(diagnostics, "WVTH901"));
    }
}

/// <summary>WVTH004 (S4): no identity reads outside the composition layer.</summary>
public sealed class IdentityReadAnalyzerTests
{
    private static string[] Flagged(string path, string source, (string Path, string Content)[]? additional = null)
        => Spans(Run(new IdentityReadAnalyzer(), new[] { Src(path, source) }, additional), "WVTH004");

    private const string ReadsCurrent = "namespace Wavee.X { class C { object M() => Wavee.Theming.ThemeHost.Current; } }";
    private const string ReadsManifestId = "namespace Wavee.X { class C { string M(Wavee.Theming.ThemeComposition c) => c.Manifest.Id; } }";

    [Fact]
    public void ThemesCurrent_InCore_IsFlagged()
        => Assert.Equal(new[] { "Current" }, Flagged("Shell/C.cs", ReadsCurrent));

    [Fact]
    public void ManifestId_InCore_IsFlagged()
        => Assert.Equal(new[] { "Id", "Manifest" }, Flagged("Shell/C.cs", ReadsManifestId));

    [Fact]
    public void ThemeCatalogType_InATheme_IsFlagged()
        => Assert.Equal(new[] { "ThemeCatalog" }, Flagged("Themes/Zune/Structure/S.cs",
            "namespace Wavee.Themes.Zune { class S { string M() => Wavee.Theming.ThemeCatalog.FallbackId; } }"));

    [Fact]
    public void LayerId_IsFlagged()
        => Assert.Equal(new[] { "Id", "Id" }, Flagged("Shell/C.cs",
            "namespace Wavee.X { class C { string M(Wavee.Theming.IStructureLayer s, Wavee.Theming.IStyleLayer y) => s.Id + y.Id; } }"));

    [Fact]
    public void EngineDefinitionId_IsFlagged()
        => Assert.Equal(new[] { "Id" }, Flagged("Shell/C.cs",
            "namespace Wavee.X { class C { string M(FluentGpu.Theming.ThemeDefinition d) => d.Id; } }"));

    [Theory]
    [InlineData("Theming/Composition/ThemeHost.cs")]
    [InlineData("Theming/Composition/ThemePickerFacts.cs")]
    [InlineData("Theming/Options/ThemeOptionStore.cs")]
    [InlineData("Theming/ThemeBootstrap.cs")]
    [InlineData("Theming/ThemeFeedback.cs")]
    [InlineData("App.cs")]
    [InlineData("Themes/Classic/ClassicTheme.cs")]
    public void AllowedFiles_AreClean(string path)
        => Assert.Empty(Flagged(path, ReadsManifestId + ReadsCurrent.Replace("namespace Wavee.X", "namespace Wavee.Y").Replace("class C", "class D")));

    [Fact]
    public void ThemeStructureFile_IsNotAllowed()
        => Assert.NotEmpty(Flagged("Themes/Classic/Structure/S.cs", ReadsManifestId));

    [Fact]
    public void NonIdentityMembers_AreClean()
        => Assert.Empty(Flagged("Shell/C.cs",
            "namespace Wavee.X { class C { object M() => Wavee.Theming.ThemeHost.Facts; } class P { public string Id { get; } = \"\"; string N(P p) => p.Id; } }"));

    [Fact]
    public void Baseline_IsDisallowed()
    {
        var diagnostics = Run(new IdentityReadAnalyzer(), new[] { Src("Shell/C.cs", "namespace Wavee.X { class C { } }") },
            new[] { Baseline("WVTH004", "Shell/C.cs") });
        Assert.Single(Harness.Only(diagnostics, "WVTH901"));
    }
}
