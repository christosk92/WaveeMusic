using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Wavee.Analyzers.Themes;

/// <summary>
/// WVTH010 — legacy keys and deleted member names appear only in <c>Theming\ThemeBootstrap.cs</c> (themes technical plan,
/// section 2.4). Each migration PR that retires a presentation switch lists what it retired in
/// <c>analyzer-input\WVTH010.legacy.txt</c>, one entry per line:
/// <code>
/// key sidebar.density          # a persisted setting name (a string constant equal to it is reported)
/// member Prefs.Appearance.Density   # a deleted member (a reference whose symbol's display name is Type.Member...)
/// </code>
/// Anywhere else (including the key's old declaration in <c>Platform.Keys</c>, until the PR that removes it) the name is
/// reported; the grandfathered files live in <c>analyzer-baseline\WVTH010.txt</c> with the entry as detail, and that
/// baseline is empty after F4 (the bootstrap removal at the next minor). With no input file the rule reports nothing.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LegacyNameAnalyzer : ThemeRuleAnalyzer
{
    public const string DiagnosticId = "WVTH010";
    public const string InputFile = "WVTH010.legacy.txt";

    private static readonly DiagnosticDescriptor Rule = Describe(
        DiagnosticId,
        "Legacy key or deleted member used outside ThemeBootstrap",
        "'{0}' is a retired presentation switch and may be named only in Theming\\ThemeBootstrap.cs",
        "Legacy setting keys and deleted member names live only in the bootstrap steps that migrate them.");

    protected override DiagnosticDescriptor Descriptor => Rule;

    protected override void OnStart(CompilationStartAnalysisContext start, RuleEnv env)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var members = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in env.InputLines(InputFile, start.CancellationToken))
        {
            string entry = line;
            int hash = entry.IndexOf('#');
            if (hash >= 0)
                entry = entry.Substring(0, hash).Trim();
            if (entry.StartsWith("key ", StringComparison.Ordinal))
                keys.Add(entry.Substring(4).Trim());
            else if (entry.StartsWith("member ", StringComparison.Ordinal))
                members.Add(entry.Substring(7).Trim());
        }
        if (keys.Count == 0 && members.Count == 0)
            return;

        if (keys.Count > 0)
            start.RegisterSyntaxNodeAction(context => AnalyzeLiteral(context, env, keys), SyntaxKind.StringLiteralExpression);
        if (members.Count > 0)
            start.RegisterSyntaxNodeAction(context => AnalyzeMember(context, env, members), SyntaxKind.IdentifierName);
    }

    private static bool IsBootstrap(string? relative)
        => string.Equals(relative, "Theming/ThemeBootstrap.cs", StringComparison.OrdinalIgnoreCase);

    private static void AnalyzeLiteral(SyntaxNodeAnalysisContext context, RuleEnv env, HashSet<string> keys)
    {
        var literal = (LiteralExpressionSyntax)context.Node;
        string value = literal.Token.ValueText;
        if (!keys.Contains(value) || IsBootstrap(env.RelativePath(literal.SyntaxTree)))
            return;
        env.Report(context.ReportDiagnostic, literal.GetLocation(), "key " + value, value);
    }

    private static void AnalyzeMember(SyntaxNodeAnalysisContext context, RuleEnv env, HashSet<string> members)
    {
        var name = (IdentifierNameSyntax)context.Node;
        string text = name.Identifier.ValueText;
        if (IsBootstrap(env.RelativePath(name.SyntaxTree)))
            return;
        ISymbol? symbol = context.SemanticModel.GetSymbolInfo(name, context.CancellationToken).Symbol;
        if (symbol is null || symbol.ContainingType is null || symbol.Name != text)
            return;
        // 'Type.Member' for a member of a top-level type, 'Outer.Inner.Member' for a nested one.
        string display = TypePath(symbol.ContainingType) + "." + symbol.Name;
        if (!members.Contains(display))
            return;
        env.Report(context.ReportDiagnostic, name.GetLocation(), "member " + display, display);
    }

    private static string TypePath(INamedTypeSymbol type)
        => type.ContainingType is { } outer ? TypePath(outer) + "." + type.Name : type.Name;
}
