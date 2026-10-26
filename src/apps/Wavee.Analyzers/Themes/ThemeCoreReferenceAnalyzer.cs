using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Wavee.Analyzers.Themes;

/// <summary>
/// WVTH002 — <c>Themes\**</c> may not reference core state (themes technical plan, section 2.4): <c>Entities</c>,
/// <c>Playback</c>, <c>Spotify</c>, <c>Store</c>, <c>Shell</c>, <c>Prefs</c> and <c>Platform.Settings</c>. A theme receives
/// what it shows through props records and verbs of the contract (<c>Wavee.Theming.Contracts</c>), never by reaching into
/// the app. The referenced symbol's outermost type decides: a type (or a namespace segment under <c>Wavee</c>) with one of the
/// listed names, or the <c>Settings</c> property of <c>Platform</c>. Grandfathered files live in
/// <c>analyzer-baseline\WVTH002.txt</c> (Classic's <c>Structure\LegacyPrefs.cs</c> only, shrink-only); the baseline detail is
/// the banned name (<c>Prefs</c>, <c>Platform.Settings</c>).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ThemeCoreReferenceAnalyzer : ThemeRuleAnalyzer
{
    public const string DiagnosticId = "WVTH002";

    private static readonly DiagnosticDescriptor Rule = Describe(
        DiagnosticId,
        "Theme code references core state",
        "'{0}' is core state and may not be referenced from a theme ({1}); take it through a props record or a verb of the contract",
        "Themes\\** may not reference Entities, Playback, Spotify, Store, Shell state, Platform.Settings or Prefs.");

    private static readonly HashSet<string> BannedNames = new HashSet<string>
    {
        "Entities", "Playback", "Spotify", "Store", "Shell", "Prefs",
    };

    protected override DiagnosticDescriptor Descriptor => Rule;

    protected override void OnStart(CompilationStartAnalysisContext start, RuleEnv env)
    {
        start.RegisterSyntaxNodeAction(context => Analyze(context, env), SyntaxKind.IdentifierName, SyntaxKind.GenericName);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context, RuleEnv env)
    {
        var name = (SimpleNameSyntax)context.Node;
        string? relative = env.RelativePath(name.SyntaxTree);
        if (relative is null || !relative.StartsWith("Themes/", System.StringComparison.OrdinalIgnoreCase))
            return;
        if (name is IdentifierNameSyntax && name.Identifier.ValueText == "var")
            return;
        if (name.Parent is NameEqualsSyntax { Parent: UsingDirectiveSyntax })
            return;

        SymbolInfo info = context.SemanticModel.GetSymbolInfo(name, context.CancellationToken);
        ISymbol? symbol = info.Symbol;
        if (symbol is null && !info.CandidateSymbols.IsDefaultOrEmpty)
            symbol = info.CandidateSymbols[0];
        if (symbol is IAliasSymbol alias)
            symbol = alias.Target;
        if (symbol is null)
            return;

        string? banned = Banned(symbol);
        if (banned is null)
            return;
        // `Wavee.Store.Rows` is one reference: the qualifier already carries it, the member is not reported again.
        ExpressionSyntax? qualifier = name.Parent switch
        {
            MemberAccessExpressionSyntax access when access.Name == name => access.Expression,
            QualifiedNameSyntax qualified when qualified.Right == name => qualified.Left,
            _ => null,
        };
        if (qualifier is not null && context.SemanticModel.GetSymbolInfo(qualifier, context.CancellationToken).Symbol is { } outer
            && Banned(outer is IAliasSymbol a ? a.Target : outer) == banned)
            return;
        env.Report(context.ReportDiagnostic, name.GetLocation(), banned, banned, DescribeScope(relative));
    }

    private static string DescribeScope(string relative)
    {
        string[] seg = relative.Split('/');
        return seg.Length > 1 ? "Themes\\" + seg[1] : "Themes";
    }

    /// <summary>The banned name the symbol belongs to, or null.</summary>
    private static string? Banned(ISymbol symbol)
    {
        INamedTypeSymbol? owner = Symbols.OwnerType(symbol);
        INamedTypeSymbol? top = Symbols.Outermost(owner);
        if (top is null)
            return null;

        string ns = Symbols.Namespace(top.ContainingNamespace);
        if (ns == "Wavee")
        {
            if (BannedNames.Contains(top.Name))
                return top.Name;
            if (top.Name == "Platform" && symbol is IPropertySymbol { Name: "Settings" })
                return "Platform.Settings";
            return null;
        }

        if (ns.StartsWith("Wavee.", System.StringComparison.Ordinal))
        {
            string rest = ns.Substring("Wavee.".Length);
            int dot = rest.IndexOf('.');
            string first = dot < 0 ? rest : rest.Substring(0, dot);
            if (BannedNames.Contains(first))
                return first;
        }
        return null;
    }
}
