using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Wavee.Analyzers.Themes;

/// <summary>
/// WVTH004 (S4) — no identity reads (themes technical plan, section 2.4). Code outside the composition layer must never ask
/// WHICH theme is active ("if the theme is Zune"): behaviour differences are options, policies, capabilities or roles. The
/// rule reports a reference to the identity of a theme:
/// <list type="bullet">
/// <item><c>ThemeHost.Current</c> (<c>Wavee.Theming</c>) and the <c>ThemeCatalog</c> type;</item>
/// <item><c>ThemeComposition.Manifest</c>, and <c>ThemeManifest.Id</c>, <c>DisplayNameKey</c>, <c>StructureId</c>,
/// <c>StyleId</c>, <c>InheritsFrom</c>;</item>
/// <item>the <c>Id</c> of an <c>IStructureLayer</c>, an <c>IStyleLayer</c> or the engine's <c>ThemeDefinition</c>.</item>
/// </list>
/// ALLOWED: <c>Theming\Composition\**</c> (which includes <c>ThemePickerFacts</c>, the one API for "installed + current"),
/// <c>Theming\Options\**</c> (every setting key embeds the theme id; the plan's list omits it but its own ThemeOptionStore
/// needs it), <c>Theming\ThemeBootstrap.cs</c>, <c>App.cs</c>, the composition files <c>Themes\X\XTheme.cs</c> (manifests), and
/// the feedback diagnostics line <c>Theming\ThemeFeedback.cs</c> (id and option stamp as data). No baseline.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class IdentityReadAnalyzer : ThemeRuleAnalyzer
{
    public const string DiagnosticId = "WVTH004";

    private static readonly DiagnosticDescriptor Rule = Describe(
        DiagnosticId,
        "Theme identity read",
        "'{0}' reads which theme is active; express the difference as an option, a policy, a capability or a role",
        "Only Theming\\Composition, Theming\\Options, ThemeBootstrap, App.cs, theme manifests and the feedback line may read theme identity.");

    protected override DiagnosticDescriptor Descriptor => Rule;

    protected override bool AllowsBaseline => false;

    protected override void OnStart(CompilationStartAnalysisContext start, RuleEnv env)
    {
        start.RegisterSyntaxNodeAction(context => Analyze(context, env), SyntaxKind.IdentifierName);
    }

    private static bool IsAllowedFile(string? relative)
    {
        if (relative is null)
            return false;
        if (relative.StartsWith("Theming/Composition/", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("Theming/Options/", StringComparison.OrdinalIgnoreCase)
            || string.Equals(relative, "Theming/ThemeBootstrap.cs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(relative, "Theming/ThemeFeedback.cs", StringComparison.OrdinalIgnoreCase))
            return true;
        ScopeKind kind = ThemeLayering.Classify(relative).Kind;
        return kind == ScopeKind.Unrestricted || kind == ScopeKind.Composition;
    }

    private static void Analyze(SyntaxNodeAnalysisContext context, RuleEnv env)
    {
        var name = (IdentifierNameSyntax)context.Node;
        string text = name.Identifier.ValueText;
        // Cheap syntactic pre-filter before the semantic query: only the few names the rule is about.
        if (text != "Current" && text != "ThemeCatalog" && text != "Manifest" && text != "Id" && text != "DisplayNameKey"
            && text != "StructureId" && text != "StyleId" && text != "InheritsFrom")
            return;
        if (IsAllowedFile(env.RelativePath(name.SyntaxTree)))
            return;

        ISymbol? symbol = context.SemanticModel.GetSymbolInfo(name, context.CancellationToken).Symbol;
        if (symbol is null)
            return;
        string? what = Identity(symbol);
        if (what is null)
            return;
        env.Report(context.ReportDiagnostic, name.GetLocation(), what, what);
    }

    private static string? Identity(ISymbol symbol)
    {
        if (symbol is INamedTypeSymbol type)
            return Symbols.IsType(type, "Wavee.Theming", "ThemeCatalog") ? "ThemeCatalog" : null;

        INamedTypeSymbol? owner = symbol.ContainingType;
        if (owner is null || symbol is not IPropertySymbol property)
            return null;

        if (property.Name == "Current" && Symbols.IsType(owner, "Wavee.Theming", "ThemeHost"))
            return "ThemeHost.Current";
        if (property.Name == "Manifest" && Symbols.IsType(owner, "Wavee.Theming", "ThemeComposition"))
            return "ThemeComposition.Manifest";
        if (Symbols.IsType(owner, "Wavee.Theming", "ThemeManifest")
            && (property.Name == "Id" || property.Name == "DisplayNameKey" || property.Name == "StructureId"
                || property.Name == "StyleId" || property.Name == "InheritsFrom"))
            return "ThemeManifest." + property.Name;
        if (property.Name == "Id")
        {
            if (Symbols.Implements(owner, "Wavee.Theming", "IStructureLayer"))
                return "IStructureLayer.Id";
            if (Symbols.Implements(owner, "Wavee.Theming", "IStyleLayer"))
                return "IStyleLayer.Id";
            if (Symbols.IsType(owner, "FluentGpu.Theming", "ThemeDefinition"))
                return "ThemeDefinition.Id";
        }
        return null;
    }
}
