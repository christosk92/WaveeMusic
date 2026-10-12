using Microsoft.CodeAnalysis;
using Wavee.Analyzers.Themes;
using Xunit;
using static Wavee.Analyzers.Tests.Fixtures;

namespace Wavee.Analyzers.Tests;

/// <summary>WVTH013 (tiers): a view compares no measured width with a number; tiers and the structure's layout record decide.</summary>
public sealed class WindowTierFenceAnalyzerTests
{
    private const string ViewPath = "Themes/Zune/Structure/Page.cs";

    private static string[] Flagged(string path, string source, (string Path, string Content)[]? additional = null)
        => Spans(Run(new WindowTierFenceAnalyzer(), new[] { Src(path, source) }, additional), "WVTH013");

    private static string[] FlaggedFiles(params (string Path, string Source)[] files)
        => Spans(Run(new WindowTierFenceAnalyzer(), files), "WVTH013");

    private static string Page(string members, string baseList = "")
        => "namespace Wavee.Themes.Zune { using Wavee.Theming; class Page" + baseList + " { " + members + " } }";

    private const string Hooks = "FluentGpu.Hooks.RenderContext hooks";
    private const string ViewportSize = "FluentGpu.Hooks.Viewport.Size";
    private const string ReadViewport = "var vp = hooks.UseContext(" + ViewportSize + "); ";

    private const string ViaUseContext = "bool Narrow(" + Hooks + ") { " + ReadViewport + "return vp.Width < 640f; }";

    private const string ElBox = "(FluentGpu.Dsl.Element)new FluentGpu.Dsl.BoxEl()";
    private const string ElText = "new FluentGpu.Dsl.TextEl()";
    private const string OfBody = ElBox + " : " + ElText;
    private const string OfCall = "FluentGpu.Controls.Responsive.Of(w => w < 900f ? " + OfBody + ")";

    // ---- (a) one case per source kind ---------------------------------------------------------------------------------------

    [Fact]
    public void ViewportSize_ReadThroughUseContext_ComparedWithALiteral_IsFlagged()
        => Assert.Equal(new[] { "vp.Width < 640f" }, Flagged(ViewPath, Page(ViaUseContext)));

    [Fact]
    public void ViewportSize_ReadThroughUseContextSignal_ComparedWithALiteral_IsFlagged()
        => Assert.Equal(new[] { "UseContextSignal(" + ViewportSize + ").Value.Width >= 900f" },
            Flagged(ViewPath, Page("bool Wide() => UseContextSignal(" + ViewportSize + ").Value.Width >= 900f;", " : FluentGpu.Hooks.Component")));

    [Fact]
    public void UseMeasuredWidth_ComparedWithAConstantOutsideTiers_IsFlaggedAtTheComparisonAndTheCall()
        => Assert.Equal(new[] { "hooks.UseMeasuredWidth()", "w.Value >= Breakpoint" },
            Flagged(ViewPath, Page("const float Breakpoint = 800f; bool Wide(" + Hooks + ") { var w = hooks.UseMeasuredWidth(); return w.Value >= Breakpoint; }")));

    [Fact]
    public void UseMeasuredWidth_OfAComponent_ComparedWithALiteral_IsFlaggedAtTheComparisonAndTheCall()
        => Assert.Equal(new[] { "UseMeasuredWidth(4f)", "w.Value < 700f" },
            Flagged(ViewPath, Page("bool Narrow() { var w = UseMeasuredWidth(4f); return w.Value < 700f; }", " : FluentGpu.Hooks.Component")));

    [Fact]
    public void ResponsiveOf_WidthParameter_ComparedWithALiteral_IsFlaggedAtTheComparisonAndTheCall()
        => Assert.Equal(new[] { OfCall, "w < 900f" },
            Flagged(ViewPath, Page("object Build() => " + OfCall + ";")));

    [Fact]
    public void ResponsiveOf_StateOverload_WidthIsTheLastParameter()
        => Assert.Equal(
            new[] { "FluentGpu.Controls.Responsive.Of(3, (s, w) => w >= 900f ? " + OfBody + ")", "w >= 900f" },
            Flagged(ViewPath, Page("object Build() => FluentGpu.Controls.Responsive.Of(3, (s, w) => w >= 900f ? " + OfBody + ");")));

    [Theory]
    [InlineData("ContentWidth")]
    [InlineData("ViewportWidth")]
    [InlineData("ViewportHeight")]
    public void TierFactsSizeMember_ComparedWithALiteral_IsFlagged(string member)
        => Assert.Equal(new[] { "ctx.Tier()." + member + " > 1200f" },
            Flagged(ViewPath, Page("bool Wide(FluentGpu.Theming.ViewContext ctx) => ctx.Tier()." + member + " > 1200f;")));

    // ---- (a) the spellings of a comparison --------------------------------------------------------------------------------

    [Theory]
    [InlineData("vp.Width <= 640f", "vp.Width <= 640f")]
    [InlineData("vp.Width == 640f", "vp.Width == 640f")]
    [InlineData("vp.Width != 640f", "vp.Width != 640f")]
    [InlineData("640f > vp.Width", "640f > vp.Width")]
    [InlineData("vp.Height < 680f", "vp.Height < 680f")]
    [InlineData("vp.Width - 48f < 600f", "vp.Width - 48f < 600f")]
    [InlineData("(int)vp.Width < 640", "(int)vp.Width < 640")]
    [InlineData("System.MathF.Max(vp.Width, 300f) < 640f", "System.MathF.Max(vp.Width, 300f) < 640f")]
    [InlineData("vp.Width is < 640f", "vp.Width is < 640f")]
    [InlineData("vp.Width is >= 640f and < 900f", "vp.Width is >= 640f and < 900f")]
    [InlineData("vp.Width is 640f", "vp.Width is 640f")]
    public void Comparison_Spellings_AreFlagged(string condition, string span)
        => Assert.Equal(new[] { span },
            Flagged(ViewPath, Page("bool M(" + Hooks + ") { " + ReadViewport + "return " + condition + "; }")));

    [Fact]
    public void RelativeSwitchOnTheWidth_IsFlaggedOnceAtTheGoverningExpression()
        => Assert.Equal(new[] { "vp.Width" },
            Flagged(ViewPath, Page("int Columns(" + Hooks + ") { " + ReadViewport + "return vp.Width switch { < 640f => 1, < 1008f => 2, _ => 3 }; }")));

    [Fact]
    public void LocalAssignedFromAMeasurement_ComparedWithALiteral_IsFlagged()
        => Assert.Equal(new[] { "width < 640f" },
            Flagged(ViewPath, Page("bool M(" + Hooks + ") { " + ReadViewport + "float width = vp.Width; return width < 640f; }")));

    [Fact]
    public void UseMeasuredBounds_ComparedWithALiteral_IsFlaggedAtTheComparisonAndTheCall()
        => Assert.Equal(new[] { "b.Value.W < 600f", "hooks.UseMeasuredBounds()" },
            Flagged(ViewPath, Page("bool Narrow(" + Hooks + ") { var b = hooks.UseMeasuredBounds(); return b.Value.W < 600f; }")));

    // ---- (b) the calls -----------------------------------------------------------------------------------------------------

    [Fact]
    public void ResponsiveOf_WithAComparisonInItsLambda_IsFlaggedAtTheCall_OnceWhateverTheNumberOfComparisons()
    {
        const string call = "FluentGpu.Controls.Responsive.Of(w => w < 600f ? " + ElBox + " : w < 900f ? " + ElBox + " : " + ElText + ")";
        string[] spans = Flagged(ViewPath, Page("object Build() => " + call + ";"));
        Assert.Contains(call, spans);
        Assert.Contains("w < 600f", spans);
        Assert.Contains("w < 900f", spans);
        Assert.Equal(3, spans.Length);
    }

    // ---- clean: what the rule must not flag ---------------------------------------------------------------------------------

    [Fact]
    public void ComparisonAgainstAConstantDeclaredInRulesTiers_IsClean()
        => Assert.Empty(FlaggedFiles(
            Src("Themes/Zune/Structure/Rules/Tiers.cs", "namespace Wavee.Themes.Zune { static class ZuneTiers { public const float CompactBelow = 640f; } }"),
            Src(ViewPath, Page("bool Narrow(" + Hooks + ") { " + ReadViewport + "return vp.Width < Wavee.Themes.Zune.ZuneTiers.CompactBelow; }"))));

    [Fact]
    public void ComparisonAgainstAConstantDeclaredInRulesTiersParts_IsClean()
        => Assert.Empty(FlaggedFiles(
            Src("Themes/Zune/Structure/Rules/Tiers.Home.cs", "namespace Wavee.Themes.Zune { static class HomeTiers { public const float Compact = 640f; } }"),
            Src(ViewPath, Page("bool Narrow(" + Hooks + ") { " + ReadViewport + "return vp.Width < Wavee.Themes.Zune.HomeTiers.Compact; }"))));

    [Fact]
    public void ComparisonAgainstAConstantDeclaredInOtherRulesFiles_IsFlagged()
        => Assert.Equal(new[] { "vp.Width < Wavee.Themes.Zune.ZuneMetrics.Narrow" }, FlaggedFiles(
            Src("Themes/Zune/Structure/Rules/Metrics.cs", "namespace Wavee.Themes.Zune { static class ZuneMetrics { public const float Narrow = 640f; } }"),
            Src(ViewPath, Page("bool Narrow(" + Hooks + ") { " + ReadViewport + "return vp.Width < Wavee.Themes.Zune.ZuneMetrics.Narrow; }"))));

    [Fact]
    public void ComparisonAgainstALayoutRecordMember_IsClean()
        => Assert.Empty(Flagged(ViewPath, Page(
            "sealed record PageLayout(float Max, int Columns); "
            + "bool Fits(FluentGpu.Theming.ViewContext ctx, PageLayout layout) => ctx.Tier().ContentWidth > layout.Max; "
            + "bool Wide(PageLayout layout) => layout.Columns > 3;")));

    [Fact]
    public void ComparisonOfTheTierItself_IsClean()
        => Assert.Empty(Flagged(ViewPath, Page(
            "bool Huge(FluentGpu.Theming.ViewContext ctx) => ctx.Tier().Content >= Wavee.Theming.Contracts.WidthTier.Huge;")));

    [Fact]
    public void ControlSizeLiterals_AreClean()
        => Assert.Empty(Flagged(ViewPath, Page(
            "object Icon() => new FluentGpu.Dsl.BoxEl { Width = 32f, Height = 32f }; "
            + "void Resize(FluentGpu.Dsl.BoxEl box) { box.Width = 48f; }")));

    [Fact]
    public void ComparisonOfAnUnrelatedValueWithALiteral_IsClean()
        => Assert.Empty(Flagged(ViewPath, Page("bool Many(int[] items, " + Hooks + ") { " + ReadViewport + "return items.Length > 3 && vp.Width.ToString().Length > 3; }")));

    [Fact]
    public void ComparisonOfAMeasurementWithZero_IsClean()
        => Assert.Empty(Flagged(ViewPath, Page("bool Known(" + Hooks + ") { " + ReadViewport + "return vp.Width > 0f; }")));

    [Fact]
    public void ComparisonOfTwoNonConstants_IsClean()
        => Assert.Empty(Flagged(ViewPath, Page("bool M(" + Hooks + ", float other) { " + ReadViewport + "return vp.Width < other; }")));

    [Fact]
    public void ResponsiveOf_NonWidthParameter_ComparedWithALiteral_IsClean()
        => Assert.Empty(Flagged(ViewPath, Page(
            "object Build() => FluentGpu.Controls.Responsive.Of(3, (s, w) => s > 900 ? " + OfBody + ");")));

    [Fact]
    public void ResponsiveOf_WithoutABreakpoint_IsClean()
        => Assert.Empty(Flagged(ViewPath, Page("object Build() => FluentGpu.Controls.Responsive.Of(w => new FluentGpu.Dsl.BoxEl { Width = w });")));

    [Fact]
    public void UseMeasuredWidth_WithoutABreakpoint_IsClean()
        => Assert.Empty(Flagged(ViewPath, Page("float Doubled(" + Hooks + ") { float v = hooks.UseMeasuredWidth().Value; return v * 2f; }")));

    // ---- where the rule applies --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Themes/Zune/Structure/Rules/Tiers.cs")]
    [InlineData("Themes/Zune/Structure/Rules/ZunePolicies.cs")]
    public void SameCode_UnderRulesOfAStructure_IsClean(string path)
        => Assert.Empty(Flagged(path, Page(ViaUseContext)));

    [Theory]
    [InlineData("Themes/Zune/Structure/Page.cs")]
    [InlineData("Themes/Zune/Style/Look.cs")]
    [InlineData("Themes/Zune/ZuneTheme.cs")]
    [InlineData("Themes/Common/Block.cs")]
    public void SameCode_AnywhereElseInThemes_IsFlagged(string path)
        => Assert.Equal(new[] { "vp.Width < 640f" }, Flagged(path, Page(ViaUseContext)));

    [Fact]
    public void SameCode_InCoreFile_IsClean()
        => Assert.Empty(Flagged("Shell/Page.cs", Page(ViaUseContext)));

    private static string Host(string members)
        => "namespace Wavee.Shell2 { [Wavee.Theming.Contracts.CoreHost] class Host { " + members + " } }";

    [Fact]
    public void SameCode_InACoreHostType_IsFlagged()
        => Assert.Equal(new[] { "vp.Width < 640f" }, Flagged("Shell/Host.cs", Host(ViaUseContext)));

    [Theory]
    [InlineData("Theming/Policies/Host.cs")]
    [InlineData("Shell/WindowTiers.cs")]
    public void SameCode_InAnAllowedCoreFile_IsClean(string path)
        => Assert.Empty(Flagged(path, Host(ViaUseContext)));

    // ---- the baseline ------------------------------------------------------------------------------------------------------

    private const string ClassicMeasured =
        "namespace Wavee.Themes.Classic { class Page { bool Wide(" + Hooks + ") { var w = hooks.UseMeasuredWidth(); return w.Value >= 800f; } } }";

    [Fact]
    public void Baseline_ByDetail_ExemptsTheComparisonAndTheCall_AndIsNotStale()
    {
        var diagnostics = Run(new WindowTierFenceAnalyzer(), new[] { Src("Themes/Classic/Structure/Page.cs", ClassicMeasured) },
            new[] { Baseline("WVTH013", "Themes/Classic/Structure/Page.cs :: UseMeasuredWidth") });
        Assert.Empty(Harness.Only(diagnostics, "WVTH013"));
        Assert.Empty(Harness.Only(diagnostics, "WVTH900"));
    }

    [Fact]
    public void Baseline_WithoutDetail_ExemptsTheWholeFile()
    {
        var diagnostics = Run(new WindowTierFenceAnalyzer(), new[] { Src("Themes/Classic/Structure/Page.cs", ClassicMeasured) },
            new[] { Baseline("WVTH013", "# Classic, emptied by the tier PRs; deleted by TC10\r\nThemes/Classic/Structure/Page.cs\r\n") });
        Assert.Empty(Harness.Only(diagnostics, "WVTH013"));
        Assert.Empty(Harness.Only(diagnostics, "WVTH900"));
    }

    [Fact]
    public void Baseline_ForAnotherMeasurementKind_DoesNotExempt_AndIsStale()
    {
        var diagnostics = Run(new WindowTierFenceAnalyzer(), new[] { Src("Themes/Classic/Structure/Page.cs", ClassicMeasured) },
            new[] { Baseline("WVTH013", "Themes/Classic/Structure/Page.cs :: Viewport.Size") });
        Assert.Equal(2, Harness.Only(diagnostics, "WVTH013").Count);
        Assert.Single(Harness.Only(diagnostics, "WVTH900"));
    }

    [Fact]
    public void Baseline_EntryThatMatchesNothing_IsReportedStale()
    {
        var diagnostics = Run(new WindowTierFenceAnalyzer(),
            new[] { Src("Themes/Classic/Structure/Page.cs", "namespace Wavee.Themes.Classic { class Page { } }") },
            new[] { Baseline("WVTH013", "Themes/Classic/Structure/Page.cs :: UseMeasuredWidth") });
        Assert.Empty(Harness.Only(diagnostics, "WVTH013"));
        Assert.Single(Harness.Only(diagnostics, "WVTH900"));
    }

    [Fact]
    public void Descriptor_IsAnError_WithTheRuleId()
    {
        DiagnosticDescriptor descriptor = new WindowTierFenceAnalyzer().SupportedDiagnostics.Single(d => d.Id == "WVTH013");
        Assert.Equal(DiagnosticSeverity.Error, descriptor.DefaultSeverity);
    }
}
