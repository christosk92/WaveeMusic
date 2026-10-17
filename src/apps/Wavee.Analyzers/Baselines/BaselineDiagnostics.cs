using Microsoft.CodeAnalysis;

namespace Wavee.Analyzers.Baselines;

/// <summary>The two diagnostics every baseline-aware rule can produce. Each rule lists both in its
/// <c>SupportedDiagnostics</c>; <see cref="AnalyzerBaseline.Register"/> reports them.</summary>
public static class BaselineDiagnostics
{
    public const string StaleEntryId = "WVTH900";
    public const string DisallowedBaselineId = "WVTH901";

    private const string HelpLink =
        "https://github.com/christosk92/WaveeMusic/blob/main/docs/themes/plan/technical-plan.md";

    /// <summary>A baseline entry that matched nothing in this compilation: the violation was fixed (or the entry is
    /// misspelled), so the entry must go. Keeps baselines shrink-only.</summary>
    public static readonly DiagnosticDescriptor StaleEntry = new(
        id: StaleEntryId,
        title: "Stale analyzer baseline entry",
        messageFormat: "Baseline entry '{1}' for rule {0} in '{2}' matches nothing; remove it from the baseline",
        category: "Wavee.Themes",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A baseline entry lists a known violation of a rule. Once the violation is fixed the entry must be "
                   + "deleted, so a baseline can only shrink. Entries are 'path/suffix.cs' optionally followed by ' :: detail'.",
        helpLinkUri: HelpLink,
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    /// <summary>A baseline file exists for a rule that allows none.</summary>
    public static readonly DiagnosticDescriptor DisallowedBaseline = new(
        id: DisallowedBaselineId,
        title: "Rule allows no baseline",
        messageFormat: "Rule {0} allows no baseline, so '{1}' must not exist; fix the violations instead of listing them",
        category: "Wavee.Themes",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Some rules (WVTH001 and others) are hard gates with no grandfathered violations. A baseline file named "
                   + "after such a rule is an error.",
        helpLinkUri: HelpLink,
        customTags: WellKnownDiagnosticTags.CompilationEnd);
}
