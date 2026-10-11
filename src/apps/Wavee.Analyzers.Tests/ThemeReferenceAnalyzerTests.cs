using Wavee.Analyzers.Themes;
using Xunit;

namespace Wavee.Analyzers.Tests;

/// <summary>WVTH001: <c>Wavee.Themes.*</c> is referenced only along the allowed edges. Every compilation is in memory; the
/// theme types come from fixture files placed where the real ones will live (<c>Themes\&lt;X&gt;\</c>).</summary>
public sealed class ThemeReferenceAnalyzerTests
{
    private const string Root = Harness.ProjectDir;

    // The referenced types. Each sits in its own theme folder and references only its own namespace, so the fixtures
    // themselves never trigger the rule.
    private static readonly (string Path, string Source)[] ThemeTypes =
    {
        (Root + @"Themes\Zune\ZuneTypes.cs",
            "namespace Wavee.Themes.Zune { public class ZunePanel { public static int Size = 1; public int Count; } public class ZuneMarkAttribute : System.Attribute { } }"),
        (Root + @"Themes\WinUi\WinUiTypes.cs", "namespace Wavee.Themes.WinUi { public class WinUiPanel { } }"),
        (Root + @"Themes\Classic\ClassicTypes.cs", "namespace Wavee.Themes.Classic { public class ClassicPanel { } }"),
        (Root + @"Themes\Common\CommonTypes.cs", "namespace Wavee.Themes.Common { public class CommonPanel { } }"),
    };

    private static string[] Flagged(string path, string source, string? projectDir = Harness.ProjectDir)
    {
        var files = ThemeTypes.Concat(new[] { (path, source) });
        var diagnostics = Harness.Analyze(new ThemeReferenceAnalyzer(), files, additionalFiles: null, projectDir);
        return Harness.Only(diagnostics, "WVTH001").Select(Harness.SpanText).OrderBy(s => s, StringComparer.Ordinal).ToArray();
    }

    // ---- diagnostic cases -------------------------------------------------------------------------------------------

    [Fact]
    public void CoreFile_ReferencingZuneType_IsFlagged()
        => Assert.Equal(new[] { "ZunePanel" }, Flagged(Root + @"Shell\X.cs",
            "namespace Wavee.Shell { class X { object M() => new Wavee.Themes.Zune.ZunePanel(); } }"));

    [Fact]
    public void CoreFile_ReferencingCommonType_IsFlagged()
        => Assert.Equal(new[] { "CommonPanel" }, Flagged(Root + @"Shell\X.cs",
            "namespace Wavee.Shell { class X { object M() => new Wavee.Themes.Common.CommonPanel(); } }"));

    [Fact]
    public void ZuneFile_ReferencingWinUiType_IsFlagged()
        => Assert.Equal(new[] { "WinUiPanel" }, Flagged(Root + @"Themes\Zune\ZuneView.cs",
            "namespace Wavee.Themes.Zune { class ZuneView { object M() => new WinUi.WinUiPanel(); } }"));

    [Fact]
    public void ZuneNonStructureFile_ReferencingCommon_IsFlagged()
        => Assert.Equal(new[] { "CommonPanel" }, Flagged(Root + @"Themes\Zune\ZuneView.cs",
            "namespace Wavee.Themes.Zune { class ZuneView { object M() => new Common.CommonPanel(); } }"));

    [Fact]
    public void CommonFile_ReferencingClassicType_IsFlagged()
        => Assert.Equal(new[] { "ClassicPanel" }, Flagged(Root + @"Themes\Common\Foo.cs",
            "namespace Wavee.Themes.Common { class Foo { object M() => new Classic.ClassicPanel(); } }"));

    [Fact]
    public void Structure_ReferencingAnotherTheme_IsFlagged()
        => Assert.Equal(new[] { "WinUiPanel" }, Flagged(Root + @"Themes\Zune\Structure\ZuneStructure.cs",
            "namespace Wavee.Themes.Zune.Structure { class S { object M() => new WinUi.WinUiPanel(); } }"));

    [Fact]
    public void NotAComposition_FileNamedLikeOneButInASubfolder_IsFlagged()
        => Assert.Equal(new[] { "ZunePanel" }, Flagged(Root + @"Themes\Panorama\Sub\PanoramaTheme.cs",
            "namespace Wavee.Themes.Panorama { class PanoramaTheme { object M() => new Zune.ZunePanel(); } }"));

    [Fact]
    public void ThemeFile_NotNamedAfterItsTheme_ReferencingAnotherTheme_IsFlagged()
        => Assert.Equal(new[] { "ZunePanel" }, Flagged(Root + @"Themes\Panorama\Other.cs",
            "namespace Wavee.Themes.Panorama { class Other { object M() => new Zune.ZunePanel(); } }"));

    [Fact]
    public void UsingAlias_ToThemeType_IsFlagged()
    {
        string[] spans = Flagged(Root + @"Shell\Alias.cs",
            "using ZP = Wavee.Themes.Zune.ZunePanel;\nnamespace Wavee.Shell { class A { object M() => new ZP(); } }");
        Assert.Contains("ZunePanel", spans);
    }

    [Fact]
    public void Typeof_AndNameof_AreFlagged()
        => Assert.Equal(new[] { "WinUiPanel", "ZunePanel" }, Flagged(Root + @"Shell\X.cs",
            "namespace Wavee.Shell { class X { object T() => typeof(Wavee.Themes.Zune.ZunePanel); string N() => nameof(Wavee.Themes.WinUi.WinUiPanel); } }"));

    [Fact]
    public void BaseList_IsFlagged()
        => Assert.Equal(new[] { "ZunePanel" }, Flagged(Root + @"Shell\X.cs",
            "namespace Wavee.Shell { class X : Wavee.Themes.Zune.ZunePanel { } }"));

    [Fact]
    public void Attribute_IsFlagged()
        => Assert.Equal(new[] { "ZuneMark" }, Flagged(Root + @"Shell\X.cs",
            "namespace Wavee.Shell { [Wavee.Themes.Zune.ZuneMark] class X { } }"));

    [Fact]
    public void GenericArgument_IsFlagged()
        => Assert.Equal(new[] { "ZunePanel" }, Flagged(Root + @"Shell\X.cs",
            "namespace Wavee.Shell { class X { object M() => new System.Collections.Generic.List<Wavee.Themes.Zune.ZunePanel>(); } }"));

    [Fact]
    public void StaticMemberAccess_ReportsTheTypeOnce()
        => Assert.Equal(new[] { "ZunePanel" }, Flagged(Root + @"Shell\X.cs",
            "namespace Wavee.Shell { class X { int M() => Wavee.Themes.Zune.ZunePanel.Size; } }"));

    [Fact]
    public void InstanceMemberReachedThroughVar_IsFlaggedAtTheMember()
        => Assert.Equal(new[] { "Count", "ZunePanel" }, Flagged(Root + @"Shell\X.cs",
            "namespace Wavee.Shell { class X { object M() { var p = new Wavee.Themes.Zune.ZunePanel(); return p.Count; } } }"));

    // ---- clean cases ------------------------------------------------------------------------------------------------

    [Fact]
    public void AppCs_MayReferenceAnyTheme()
        => Assert.Empty(Flagged(Root + "App.cs",
            "namespace Wavee { class App { object M() => new Themes.Zune.ZunePanel(); object N() => new Themes.WinUi.WinUiPanel(); } }"));

    [Fact]
    public void ThemeCatalog_MayReferenceAnyTheme()
        => Assert.Empty(Flagged(Root + @"Theming\Composition\ThemeCatalog.cs",
            "namespace Wavee.Theming.Composition { class ThemeCatalog { object M() => new Wavee.Themes.Zune.ZunePanel(); object N() => new Wavee.Themes.Classic.ClassicPanel(); } }"));

    [Fact]
    public void AppCs_InANestedFolder_IsNotTheCompositionRoot()
        => Assert.Equal(new[] { "ZunePanel" }, Flagged(Root + @"Shell\App.cs",
            "namespace Wavee.Shell { class App { object M() => new Wavee.Themes.Zune.ZunePanel(); } }"));

    [Fact]
    public void CompositionFile_MayReferenceOtherThemesAndCommon()
        => Assert.Empty(Flagged(Root + @"Themes\Panorama\PanoramaTheme.cs",
            "namespace Wavee.Themes.Panorama { class PanoramaTheme { object A() => new Zune.ZunePanel(); object B() => new WinUi.WinUiPanel(); object C() => new Common.CommonPanel(); } }"));

    [Fact]
    public void Structure_MayReferenceCommonAndItsOwnTheme()
        => Assert.Empty(Flagged(Root + @"Themes\Zune\Structure\ZuneStructure.cs",
            "namespace Wavee.Themes.Zune.Structure { class S { object A() => new Common.CommonPanel(); object B() => new ZunePanel(); } }"));

    [Fact]
    public void Theme_MayReferenceItself()
        => Assert.Empty(Flagged(Root + @"Themes\Zune\ZuneView.cs",
            "namespace Wavee.Themes.Zune { class ZuneView { object M() => new ZunePanel(); int N() => ZunePanel.Size; } }"));

    [Fact]
    public void Common_MayReferenceCommon()
        => Assert.Empty(Flagged(Root + @"Themes\Common\Foo.cs",
            "namespace Wavee.Themes.Common { class Foo { object M() => new CommonPanel(); } }"));

    [Fact]
    public void PlainUsingOfAThemeNamespace_WithNoUse_IsNotAReference()
        => Assert.Empty(Flagged(Root + @"Shell\X.cs",
            "using Wavee.Themes.Zune;\nnamespace Wavee.Shell { class X { int M() => 1; } }"));

    [Fact]
    public void CodeWithNoThemesAtAll_ReportsNothing()
    {
        var diagnostics = Harness.Analyze(new ThemeReferenceAnalyzer(),
            (Root + @"Shell\X.cs", "namespace Wavee.Shell { class X { int M() => 1; } }"));
        Assert.Empty(diagnostics.ToList());
    }

    // ---- locating the project without build_property.ProjectDir -----------------------------------------------------

    [Fact]
    public void WithoutProjectDir_TheAppsWaveeSegmentLocatesTheProject()
    {
        Assert.Equal(new[] { "ZunePanel" }, Flagged(Root + @"Shell\X.cs",
            "namespace Wavee.Shell { class X { object M() => new Wavee.Themes.Zune.ZunePanel(); } }", projectDir: null));
        Assert.Empty(Flagged(Root + "App.cs",
            "namespace Wavee { class App { object M() => new Themes.Zune.ZunePanel(); } }", projectDir: null));
    }

    [Fact]
    public void WithoutProjectDir_AFolderNamedWaveeIsTheLastResort()
    {
        Assert.Equal(new[] { "ZunePanel" }, Flagged(@"D:\work\Wavee\Shell\X.cs",
            "namespace Wavee.Shell { class X { object M() => new Wavee.Themes.Zune.ZunePanel(); } }", projectDir: null));
        Assert.Empty(Flagged(@"D:\work\Wavee\Themes\Panorama\PanoramaTheme.cs",
            "namespace Wavee.Themes.Panorama { class PanoramaTheme { object M() => new Zune.ZunePanel(); } }", projectDir: null));
    }

    // ---- classification ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("App.cs", ScopeKind.Unrestricted)]
    [InlineData("Theming/Composition/ThemeCatalog.cs", ScopeKind.Unrestricted)]
    [InlineData("Shell/App.cs", ScopeKind.Core)]
    [InlineData("Themes/Common/Foo.cs", ScopeKind.Common)]
    [InlineData("Themes/Zune/ZuneTheme.cs", ScopeKind.Composition)]
    [InlineData("Themes/Zune/Structure/S.cs", ScopeKind.Structure)]
    [InlineData("Themes/Zune/Structure/Deep/S.cs", ScopeKind.Structure)]
    [InlineData("Themes/Zune/Views/V.cs", ScopeKind.ThemeOther)]
    [InlineData("Themes/Zune/Sub/ZuneTheme.cs", ScopeKind.ThemeOther)]
    [InlineData("Themes/Loose.cs", ScopeKind.Core)]
    [InlineData("Entities/Artist.cs", ScopeKind.Core)]
    public void Classify_PlacesFilesByPath(string relative, ScopeKind expected)
        => Assert.Equal(expected, ThemeLayering.Classify(relative).Kind);
}
