using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Wavee.Analyzers.Baselines;
using Wavee.Analyzers.Themes;
using Xunit;

namespace Wavee.Analyzers.Tests;

/// <summary>A tests-only rule on the shared baseline helper: "a class named Forbidden is flagged; a baseline is allowed".
/// Its id follows the real naming (WVTHnnn) so the baseline file name is the one the helper looks for.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
internal sealed class ForbiddenClassAnalyzer : DiagnosticAnalyzer
{
    public const string RuleId = "WVTH990";

    private static readonly DiagnosticDescriptor Rule = new(
        RuleId, "Forbidden class", "Class '{0}' is forbidden", "Test", DiagnosticSeverity.Warning, isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Rule, BaselineDiagnostics.StaleEntry, BaselineDiagnostics.DisallowedBaseline);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(start =>
        {
            BaselineTracker tracker = AnalyzerBaseline.Register(start, RuleId, allowsBaseline: true);
            start.RegisterSymbolAction(c =>
            {
                var type = (INamedTypeSymbol)c.Symbol;
                if (type.Name != "Forbidden")
                    return;
                Location location = type.Locations[0];
                if (tracker.IsExempt(location, type.Name))
                    return;
                c.ReportDiagnostic(Diagnostic.Create(Rule, location, type.Name));
            }, SymbolKind.NamedType);
        });
    }
}

public sealed class BaselineTests
{
    private const string Root = Harness.ProjectDir;
    private const string BaselinePath = Root + @"analyzer-baseline\WVTH990.txt";
    private const string ForbiddenSource = "namespace Wavee.Shell { class Forbidden { } }";

    private static ImmutableArray<Diagnostic> RunForbidden(string? baseline, string baselinePath = BaselinePath)
        => Harness.Analyze(
            new ForbiddenClassAnalyzer(),
            new[] { (Root + @"Shell\F.cs", ForbiddenSource) },
            baseline is null ? null : new[] { (baselinePath, baseline) });

    // ---- the shared helper, driven by the test rule -------------------------------------------------------------------

    [Fact]
    public void NoBaseline_RuleReports_AndNothingIsStale()
    {
        var d = RunForbidden(null);
        Assert.Single(Harness.Only(d, "WVTH990"));
        Assert.Empty(Harness.Only(d, "WVTH900"));
    }

    [Fact]
    public void MatchedEntryWithDetail_Suppresses_AndIsNotStale()
    {
        var d = RunForbidden("Shell/F.cs :: Forbidden\n");
        Assert.Empty(Harness.Only(d, "WVTH990"));
        Assert.Empty(Harness.Only(d, "WVTH900"));
    }

    [Fact]
    public void MatchedEntryWithoutDetail_BackslashesCommentsAndBlankLines_Suppresses()
    {
        var d = RunForbidden("# a header\r\n\r\n   \r\nShell\\F.cs\r\n");
        Assert.Empty(Harness.Only(d, "WVTH990"));
        Assert.Empty(Harness.Only(d, "WVTH900"));
    }

    [Fact]
    public void EntryMatchingIsCaseInsensitive_AndAnchoredOnSegments()
    {
        var byFileName = RunForbidden("f.cs\n");
        Assert.Empty(Harness.Only(byFileName, "WVTH990"));
        Assert.Empty(Harness.Only(byFileName, "WVTH900"));

        // 'hell/F.cs' is a character suffix of 'Shell/F.cs' but not a path-segment suffix.
        var partialSegment = RunForbidden("hell/F.cs\n");
        Assert.Single(Harness.Only(partialSegment, "WVTH990"));
        Assert.Single(Harness.Only(partialSegment, "WVTH900"));
    }

    [Fact]
    public void StaleEntry_ProducesWvth900_NamingRuleEntryAndFile_AtTheEntryLine()
    {
        var d = RunForbidden("# keep this\nShell/Gone.cs\n");

        Diagnostic stale = Assert.Single(Harness.Only(d, "WVTH900"));
        string message = stale.GetMessage();
        Assert.Contains("WVTH990", message);
        Assert.Contains("Shell/Gone.cs", message);
        Assert.Contains("WVTH990.txt", message);

        FileLinePositionSpan span = stale.Location.GetLineSpan();
        Assert.EndsWith("WVTH990.txt", span.Path);
        Assert.Equal(1, span.StartLinePosition.Line);

        // The real violation is still reported: the stale entry exempted nothing.
        Assert.Single(Harness.Only(d, "WVTH990"));
    }

    [Fact]
    public void DetailMismatch_DoesNotSuppress_AndTheEntryIsStale()
    {
        var d = RunForbidden("Shell/F.cs :: SomethingElse\n");
        Assert.Single(Harness.Only(d, "WVTH990"));
        Assert.Single(Harness.Only(d, "WVTH900"));
    }

    [Fact]
    public void OnlyTheUnmatchedEntryIsReportedStale()
    {
        var d = RunForbidden("Shell/F.cs :: Forbidden\nShell/Old.cs :: Forbidden\n");
        Assert.Empty(Harness.Only(d, "WVTH990"));
        Diagnostic stale = Assert.Single(Harness.Only(d, "WVTH900"));
        Assert.Contains("Shell/Old.cs", stale.GetMessage());
    }

    [Fact]
    public void BaselineFile_OutsideTheAnalyzerBaselineDirectory_IsIgnored()
    {
        var d = RunForbidden("Shell/F.cs\n", baselinePath: Root + @"other\WVTH990.txt");
        Assert.Single(Harness.Only(d, "WVTH990"));
        Assert.Empty(Harness.Only(d, "WVTH900"));
    }

    [Fact]
    public void BaselineFile_ForAnotherRule_IsIgnored()
    {
        var d = RunForbidden("Shell/F.cs\n", baselinePath: Root + @"analyzer-baseline\WVTH991.txt");
        Assert.Single(Harness.Only(d, "WVTH990"));
        Assert.Empty(Harness.Only(d, "WVTH900"));
    }

    // ---- a rule that allows no baseline: WVTH001 ----------------------------------------------------------------------

    [Fact]
    public void Wvth001_WithABaselineFile_ReportsWvth901()
    {
        var d = Harness.Analyze(
            new ThemeReferenceAnalyzer(),
            new[] { (Root + @"Shell\X.cs", "namespace Wavee.Shell { class X { } }") },
            new[] { (Root + @"analyzer-baseline\WVTH001.txt", "Shell/X.cs\n") });

        Diagnostic disallowed = Assert.Single(Harness.Only(d, "WVTH901"));
        Assert.Contains("WVTH001", disallowed.GetMessage());
        Assert.EndsWith("WVTH001.txt", disallowed.Location.GetLineSpan().Path);
        Assert.Empty(Harness.Only(d, "WVTH900")); // not additionally reported stale: it is not a baseline at all
    }

    [Fact]
    public void Wvth001_WithoutABaselineFile_ReportsNoBaselineDiagnostics()
    {
        var d = Harness.Analyze(
            new ThemeReferenceAnalyzer(),
            new[] { (Root + @"Shell\X.cs", "namespace Wavee.Shell { class X { } }") },
            new[] { (Root + @"analyzer-baseline\WVTH002.txt", "Shell/X.cs\n") });

        Assert.Empty(d.ToList());
    }

    [Fact]
    public void BaselineFileName_IsMatchedByRuleIdAndDirectory()
    {
        Assert.True(AnalyzerBaseline.IsBaselineFileFor(@"C:\x\analyzer-baseline\WVTH007.txt", "WVTH007"));
        Assert.False(AnalyzerBaseline.IsBaselineFileFor(@"C:\x\baseline\WVTH007.txt", "WVTH007"));
    }
}
