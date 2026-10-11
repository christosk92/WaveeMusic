using Wavee.Analyzers.Themes;
using Xunit;
using static Wavee.Analyzers.Tests.Fixtures;

namespace Wavee.Analyzers.Tests;

/// <summary>WVTH005 (read/publish) and WVTH009 (request): theme options are touched only by their own layer.</summary>
public sealed class OptionLayerAnalyzerTests
{
    // An option declared in the Classic STYLE layer.
    private static readonly (string Path, string Source) StyleOption = Src("Themes/Classic/Style/StyleOptions.cs",
        "namespace Wavee.Themes.Classic { static class StyleOptions { public static readonly Wavee.Theming.ToggleOption Dense = new(\"dense\", false); } }");

    private static string[] Reads(string path, string body)
    {
        var diagnostics = Run(new OptionReadAnalyzer(), new[] { StyleOption, Src(path, "namespace Wavee.Themes.Classic { class User { bool M(FluentGpu.Theming.IOptionReader r) => " + body + "; } }") });
        return Spans(diagnostics, "WVTH005");
    }

    private static string[] Requests(string path, string body)
    {
        var diagnostics = Run(new OptionRequestAnalyzer(), new[] { StyleOption, Src(path, "namespace Wavee.Themes.Classic { class User { void M(FluentGpu.Theming.IOptionRequests r) { " + body + "; } } }") });
        return Spans(diagnostics, "WVTH009");
    }

    [Fact]
    public void StructureReadingAStyleOption_IsFlagged()
        => Assert.Equal(new[] { "r.Get(StyleOptions.Dense.Key)" }, Reads("Themes/Classic/Structure/Rail.cs", "r.Get(StyleOptions.Dense.Key)"));

    [Fact]
    public void CoreReadingAThemeOption_IsFlagged()
        => Assert.Single(Reads("Shell/User.cs", "r.Get(StyleOptions.Dense.Key)"));

    [Fact]
    public void StyleReadingItsOwnOption_IsClean()
        => Assert.Empty(Reads("Themes/Classic/Style/ClassicStyle.cs", "r.Get(StyleOptions.Dense.Key)"));

    [Fact]
    public void OptionsFolder_MayReadAnyOption()
        => Assert.Empty(Reads("Theming/Options/ThemeOptionsSection.cs", "r.Get(StyleOptions.Dense.Key)"));

    [Fact]
    public void KeyThatCannotBeTraced_IsClean()
        => Assert.Empty(Reads("Themes/Classic/Structure/Rail.cs", "r.Get(new FluentGpu.Theming.OptionKey<bool>(\"x\", false))"));

    [Fact]
    public void PublishOnTheStore_WithAnotherLayersKey_IsFlagged()
    {
        var diagnostics = Run(new OptionReadAnalyzer(), new[]
        {
            StyleOption,
            Src("Themes/Classic/Structure/Rail.cs",
                "namespace Wavee.Themes.Classic { class User { bool M(FluentGpu.Theming.OptionStore s) => s.Publish(StyleOptions.Dense.Key, true); } }"),
        });
        Assert.Single(Harness.Only(diagnostics, "WVTH005"));
    }

    [Fact]
    public void OptionValuesGet_WithAnotherLayersKey_IsFlagged()
    {
        var diagnostics = Run(new OptionReadAnalyzer(), new[]
        {
            StyleOption,
            Src("Themes/Classic/Structure/Rail.cs",
                "namespace Wavee.Themes.Classic { class User { bool M(Wavee.Theming.OptionValues v) => v.Get(StyleOptions.Dense.Key); } }"),
        });
        Assert.Single(Harness.Only(diagnostics, "WVTH005"));
    }

    [Fact]
    public void Baseline_IsDisallowed()
    {
        var diagnostics = Run(new OptionReadAnalyzer(), new[] { Src("Shell/C.cs", "namespace Wavee.X { class C { } }") }, new[] { Baseline("WVTH005", "Shell/C.cs") });
        Assert.Single(Harness.Only(diagnostics, "WVTH901"));
    }

    [Fact]
    public void StructureRequestingAStyleOption_IsFlagged()
        => Assert.Equal(new[] { "r.Set(StyleOptions.Dense.Key, true)" }, Requests("Themes/Classic/Structure/Rail.cs", "r.Set(StyleOptions.Dense.Key, true)"));

    [Fact]
    public void StyleRequestingItsOwnOption_IsClean()
        => Assert.Empty(Requests("Themes/Classic/Style/ClassicStyle.cs", "r.Set(StyleOptions.Dense.Key, true)"));

    [Fact]
    public void CompositionRouter_MayRequestAnyOption()
        => Assert.Empty(Requests("Theming/Composition/ThemeHost.cs", "r.Set(StyleOptions.Dense.Key, true)"));
}

/// <summary>WVTH006: key ids and props record shape.</summary>
public sealed class KeyContractAnalyzerTests
{
    private const string Props = "public sealed record Good(int A, string B);";

    private static string[] Messages(string path, string source)
        => Fixtures.Messages(Run(new KeyContractAnalyzer(), new[] { Src(path, source) }), "WVTH006");

    private static string Keys(string declarations, string types = Props)
        => "namespace Wavee.Theming.Contracts { " + types + " public static class ViewKeys { " + declarations + " } }";

    [Fact]
    public void ValidKeys_AreClean()
        => Assert.Empty(Messages("Theming/Contracts/ViewKeys.cs", Keys(
            "public static readonly FluentGpu.Theming.ViewKey<Good> Frame = new(\"Shell.Frame\"); " +
            "public static readonly FluentGpu.Theming.ViewKey<Good> Zone = new(\"Home.Zone.Show@2\");")));

    [Fact]
    public void KeyCreatedOutsideContracts_IsFlagged()
        => Assert.Contains(Messages("Shell/Keys.cs", Keys("public static readonly FluentGpu.Theming.ViewKey<Good> Frame = new(\"Shell.Frame\");")),
            m => m.Contains("outside Theming\\Contracts"));

    [Theory]
    [InlineData("shell.frame")]
    [InlineData("Frame")]
    [InlineData("Shell..Frame")]
    [InlineData("Shell.Frame@x")]
    public void MalformedId_IsFlagged(string id)
        => Assert.Contains(Messages("Theming/Contracts/ViewKeys.cs", Keys("public static readonly FluentGpu.Theming.ViewKey<Good> K = new(\"" + id + "\");")),
            m => m.Contains("dotted path"));

    [Fact]
    public void NonConstantId_IsFlagged()
        => Assert.Contains(Messages("Theming/Contracts/ViewKeys.cs", Keys("static string Id = \"Shell.Frame\"; public static readonly FluentGpu.Theming.ViewKey<Good> K = new(Id);")),
            m => m.Contains("constant string"));

    [Fact]
    public void DuplicateId_IsFlaggedOnce()
        => Assert.Single(Messages("Theming/Contracts/ViewKeys.cs", Keys(
            "public static readonly FluentGpu.Theming.ViewKey<Good> A = new(\"Shell.Frame\"); " +
            "public static readonly FluentGpu.Theming.ViewKey<Good> B = new(\"Shell.Frame\");")), m => m.Contains("more than once"));

    [Fact]
    public void StyleKeyWithTheSameIdAsAViewKey_IsNotADuplicate()
        => Assert.Empty(Messages("Theming/Contracts/StyleKeys.cs", Keys(
            "public static readonly FluentGpu.Theming.ViewKey<Good> A = new(\"Lyrics.View\"); " +
            "public static readonly FluentGpu.Theming.StyleKey<Good> B = new(\"Lyrics.View\");")));

    [Theory]
    [InlineData("public record Loose(int A);", "Loose", "must be sealed")]
    [InlineData("public sealed class NotARecord { public int A { get; init; } }", "NotARecord", "record class")]
    [InlineData("public sealed record Mutable { public int A { get; set; } }", "Mutable", "has a setter")]
    [InlineData("public sealed record Fielded { public int A; }", "Fielded", "field 'A' is mutable")]
    [InlineData("public sealed record Stateful { public FluentGpu.Signals.Signal<int> S { get; init; } = new(); }", "Stateful", "writable Signal")]
    public void IllegalPropsShape_IsFlagged(string type, string name, string expected)
    {
        var messages = Messages("Theming/Contracts/ViewKeys.cs", Keys(
            "public static readonly FluentGpu.Theming.ViewKey<" + name + "> K = new(\"Shell.Frame\");", type));
        Assert.Contains(messages, m => m.Contains(expected));
    }

    [Fact]
    public void ReadOnlySignalProperty_IsClean()
        => Assert.Empty(Messages("Theming/Contracts/ViewKeys.cs", Keys(
            "public static readonly FluentGpu.Theming.ViewKey<Watching> K = new(\"Shell.Frame\");",
            "public sealed record Watching(FluentGpu.Signals.IReadSignal<int> Count);")));

    [Fact]
    public void Baseline_IsDisallowed()
    {
        var diagnostics = Run(new KeyContractAnalyzer(), new[] { Src("Shell/C.cs", "namespace Wavee.X { class C { } }") }, new[] { Baseline("WVTH006", "Shell/C.cs") });
        Assert.Single(Harness.Only(diagnostics, "WVTH901"));
    }
}

/// <summary>WVTH007: theme.* settings are minted only by theming.</summary>
public sealed class ThemeSettingsWriteAnalyzerTests
{
    private static string[] Flagged(string path, string source)
        => Spans(Run(new ThemeSettingsWriteAnalyzer(), new[] { Src(path, source) }), "WVTH007");

    private const string MintsKey =
        "namespace Wavee.X { class C { object M() => new Wavee.SettingKey<bool>(\"theme.density\", false); } }";

    [Fact]
    public void ThemeKey_MintedInCore_IsFlagged()
        => Assert.Equal(new[] { "new Wavee.SettingKey<bool>(\"theme.density\", false)" }, Flagged("Shell/C.cs", MintsKey));

    [Fact]
    public void TargetTypedThemeKey_IsFlagged()
        => Assert.Single(Flagged("Shell/C.cs",
            "namespace Wavee.X { class C { Wavee.SettingKey<int> K = new(\"theme.id\", 0); } }"));

    [Fact]
    public void RemoveOfAThemeKey_IsFlagged()
        => Assert.Equal(new[] { "s.Remove(\"theme.density\")" }, Flagged("Shell/C.cs",
            "namespace Wavee.X { class C { void M(Wavee.IAppSettings s) { s.Remove(\"theme.density\"); } } }"));

    [Fact]
    public void InterpolatedThemeKey_IsFlagged()
        => Assert.Single(Flagged("Shell/C.cs",
            "namespace Wavee.X { class C { bool M(Wavee.IAppSettings s, string id) => s.Has($\"theme.{id}.a\"); } }"));

    [Theory]
    [InlineData("Theming/Options/ThemeOptionStore.cs")]
    [InlineData("Theming/ThemeBootstrap.cs")]
    [InlineData("Theming/Composition/ThemeHost.cs")]
    public void OwnerFiles_AreClean(string path)
        => Assert.Empty(Flagged(path, MintsKey));

    [Fact]
    public void ThemeMode_IsTheCoreKey()
        => Assert.Empty(Flagged("Platform/Platform.cs",
            "namespace Wavee.X { class C { object M() => new Wavee.SettingKey<int>(\"theme.mode\", 0); } }"));

    [Fact]
    public void OtherKeys_AreClean()
        => Assert.Empty(Flagged("Shell/C.cs",
            "namespace Wavee.X { class C { object M() => new Wavee.SettingKey<float>(\"appearance.zoom\", 1f); } }"));
}

/// <summary>WVTH008: a [CoreHost] type builds no presentation.</summary>
public sealed class CoreHostPresentationAnalyzerTests
{
    private static string[] Flagged(string source, (string Path, string Content)[]? additional = null)
        => Spans(Run(new CoreHostPresentationAnalyzer(), new[] { Src("Shell/Host.cs", source) }, additional), "WVTH008");

    private const string Body =
        "object Box() => new FluentGpu.Dsl.BoxEl(); int Epoch() => FluentGpu.Dsl.Tok.Epoch; float Gutter() => Wavee.Design.Gutter;";

    [Fact]
    public void MarkedHost_BuildingElementsAndReadingTokens_IsFlagged()
        => Assert.Equal(new[] { "FluentGpu.Dsl.Tok.Epoch", "Wavee.Design.Gutter", "new FluentGpu.Dsl.BoxEl()" },
            Flagged("namespace Wavee.X { [Wavee.Theming.Contracts.CoreHost] class Host { " + Body + " } }"));

    [Fact]
    public void NestedTypeAndLambda_OfAMarkedHost_AreFlagged()
        => Assert.Single(Flagged("namespace Wavee.X { [Wavee.Theming.Contracts.CoreHost] class Host { class Inner { System.Func<object> F = () => new FluentGpu.Dsl.TextEl(); } } }"));

    [Fact]
    public void UnmarkedType_IsClean()
        => Assert.Empty(Flagged("namespace Wavee.X { class Host { " + Body + " } }"));

    [Fact]
    public void MarkedHost_WithoutPresentation_IsClean()
        => Assert.Empty(Flagged("namespace Wavee.X { [Wavee.Theming.Contracts.CoreHost] class Host { int N(int a) => a + 1; } }"));

    [Fact]
    public void Baseline_ExemptsByDetail_AndGoesStaleWhenFixed()
    {
        string source = "namespace Wavee.X { [Wavee.Theming.Contracts.CoreHost] class Host { object Box() => new FluentGpu.Dsl.BoxEl(); } }";
        Assert.Empty(Flagged(source, new[] { Baseline("WVTH008", "Shell/Host.cs :: BoxEl") }));

        var diagnostics = Run(new CoreHostPresentationAnalyzer(),
            new[] { Src("Shell/Host.cs", "namespace Wavee.X { [Wavee.Theming.Contracts.CoreHost] class Host { } }") },
            new[] { Baseline("WVTH008", "Shell/Host.cs :: BoxEl") });
        Assert.Single(Harness.Only(diagnostics, "WVTH900"));
    }
}

/// <summary>WVTH010: retired keys and deleted members are named only by ThemeBootstrap.</summary>
public sealed class LegacyNameAnalyzerTests
{
    private static readonly (string Path, string Content) Legacy = Input("WVTH010.legacy.txt",
        "# retired\nkey sidebar.density   # the old density switch\nmember Prefs.Density\n");

    private static string[] Flagged(string path, string source, params (string Path, string Content)[] additional)
        => Spans(Run(new LegacyNameAnalyzer(), new[] { Src(path, source) }, additional), "WVTH010");

    private const string UsesKey = "namespace Wavee.X { class C { string K = \"sidebar.density\"; } }";
    private const string UsesMember = "namespace Wavee.X { class C { int M() => Wavee.Prefs.Density; } }";

    [Fact]
    public void LegacyKeyLiteral_OutsideBootstrap_IsFlagged()
        => Assert.Equal(new[] { "\"sidebar.density\"" }, Flagged("Shell/C.cs", UsesKey, Legacy));

    [Fact]
    public void DeletedMember_OutsideBootstrap_IsFlagged()
        => Assert.Equal(new[] { "Density" }, Flagged("Shell/C.cs", UsesMember, Legacy));

    [Fact]
    public void Bootstrap_MayNameBoth()
    {
        Assert.Empty(Flagged("Theming/ThemeBootstrap.cs", UsesKey, Legacy));
        Assert.Empty(Flagged("Theming/ThemeBootstrap.cs", UsesMember, Legacy));
    }

    [Fact]
    public void UnlistedNames_AreClean()
        => Assert.Empty(Flagged("Shell/C.cs", "namespace Wavee.X { class C { string K = \"sidebar.width\"; int M() => Wavee.Shell.Frame; } }", Legacy));

    [Fact]
    public void NoInputFile_ReportsNothing()
        => Assert.Empty(Flagged("Shell/C.cs", UsesKey + UsesMember.Replace("namespace Wavee.X", "namespace Wavee.Y")));

    [Fact]
    public void Baseline_ExemptsByEntry_AndGoesStaleWhenFixed()
    {
        Assert.Empty(Flagged("Platform/Platform.cs", UsesKey, Legacy, Baseline("WVTH010", "Platform/Platform.cs :: key sidebar.density")));

        var diagnostics = Run(new LegacyNameAnalyzer(),
            new[] { Src("Platform/Platform.cs", "namespace Wavee.X { class C { } }") },
            new[] { Legacy, Baseline("WVTH010", "Platform/Platform.cs :: key sidebar.density") });
        Assert.Single(Harness.Only(diagnostics, "WVTH900"));
    }
}

/// <summary>WVTH011: every structure parameter is exercised with at least two values.</summary>
public sealed class StructureParameterAnalyzerTests
{
    private static string[] Messages(string source, params (string Path, string Content)[] additional)
        => Fixtures.Messages(Run(new StructureParameterAnalyzer(), new[] { Src("Themes/Zune/Structure/ZuneStructure.cs", source) }, additional), "WVTH011");

    private static string Declares(string option)
        => "namespace Wavee.Themes.Zune { class ZuneStructure { object M() => " + option + "; } }";

    private const string HiddenWith = "new Wavee.Theming.ToggleOption(\"narrow\", false) with { Visibility = Wavee.Theming.OptionVisibility.Hidden }";

    [Fact]
    public void HiddenOption_WithoutMatrixLine_IsFlagged()
        => Assert.Contains(Messages(Declares(HiddenWith)), m => m.Contains("'narrow' has no line"));

    [Fact]
    public void HiddenOption_WithOneValue_IsFlagged()
        => Assert.Contains(Messages(Declares(HiddenWith), Input("WVTH011.matrix.txt", "narrow = false, false\n")), m => m.Contains("at least two"));

    [Fact]
    public void HiddenOption_WithTwoValues_IsClean()
        => Assert.Empty(Messages(Declares(HiddenWith), Input("WVTH011.matrix.txt", "# matrix\nnarrow = false, true\n")));

    [Fact]
    public void ObjectInitializerForm_IsChecked()
        => Assert.Single(Messages(Declares("new Wavee.Theming.ToggleOption(\"wide\", false) { Visibility = Wavee.Theming.OptionVisibility.Hidden }")));

    [Fact]
    public void VisibleOption_NeedsNoMatrix()
        => Assert.Empty(Messages(Declares("new Wavee.Theming.ToggleOption(\"density\", false)")));

    [Fact]
    public void NonConstantId_IsFlagged()
        => Assert.Contains(Messages("namespace Wavee.Themes.Zune { class ZuneStructure { static string Id = \"x\"; object M() => new Wavee.Theming.ToggleOption(Id, false) with { Visibility = Wavee.Theming.OptionVisibility.Hidden }; } }"),
            m => m.Contains("constant string"));
}
