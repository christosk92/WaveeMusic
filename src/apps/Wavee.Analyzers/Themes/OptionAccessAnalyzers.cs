using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Wavee.Analyzers.Themes;

/// <summary>Where an option is declared and who may touch it: shared by WVTH005 (read/publish) and WVTH009 (set).</summary>
internal static class OptionScopes
{
    /// <summary>The option layer a project-relative path belongs to: <c>classic/structure</c> for
    /// <c>Themes/Classic/Structure/**</c>, <c>classic/style</c> for <c>Themes/Classic/Style/**</c>, <c>classic/composition</c>
    /// for <c>Themes/Classic/ClassicTheme.cs</c>, <c>common</c> for <c>Themes/Common/**</c>; null for anything else.</summary>
    public static string? LayerOf(string? relative)
    {
        if (relative is null)
            return null;
        FileScope scope = ThemeLayering.Classify(relative);
        switch (scope.Kind)
        {
            case ScopeKind.Common:
                return "common";
            case ScopeKind.Composition:
                return Lower(scope.Theme) + "/composition";
            case ScopeKind.Structure:
                return Lower(scope.Theme) + "/structure";
            case ScopeKind.ThemeOther:
                string[] seg = relative.Split('/');
                if (seg.Length >= 4 && string.Equals(seg[2], "Style", StringComparison.OrdinalIgnoreCase))
                    return Lower(scope.Theme) + "/style";
                return Lower(scope.Theme) + "/other";
            default:
                return null;
        }
    }

    private static string Lower(string? s) => (s ?? "").ToLowerInvariant();

    /// <summary>Files that own every option by design: the store, the generated section, the reset (<c>Theming\Options\**</c>)
    /// and the composition layer that routes requests (<c>Theming\Composition\**</c>).</summary>
    public static bool IsExemptCaller(string? relative)
        => relative is not null
           && (relative.StartsWith("Theming/Options/", StringComparison.OrdinalIgnoreCase)
               || relative.StartsWith("Theming/Composition/", StringComparison.OrdinalIgnoreCase));

    /// <summary>The field or property that declares the option behind a key argument: <c>Density.Key</c> resolves to
    /// <c>Density</c>, a key-typed field resolves to itself; null for anything the analyzer cannot trace (a local, a
    /// parameter, a call).</summary>
    public static ISymbol? KeySource(ExpressionSyntax argument, SemanticModel model, System.Threading.CancellationToken ct)
    {
        ExpressionSyntax e = argument;
        if (e is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Key" } access)
            e = access.Expression;
        ISymbol? symbol = model.GetSymbolInfo(e, ct).Symbol;
        return (symbol is IFieldSymbol or IPropertySymbol) ? symbol : null;
    }

    /// <summary>The layer scope of the file that declares <paramref name="source"/>, or null.</summary>
    public static string? DeclaringLayer(ISymbol source, RuleEnv env)
    {
        SyntaxTree? tree = Symbols.DeclaringTree(source);
        return tree is null ? null : LayerOf(env.RelativePath(tree));
    }
}

/// <summary>
/// WVTH005 — theme options are read and published only by their own layer (themes technical plan, section 2.4). A call of
/// <c>Get</c> or <c>Peek</c> on an option reader (<c>IOptionReader</c>, <c>OptionStore</c>, <c>OptionValues</c>) or of
/// <c>Publish</c> on an <c>OptionStore</c> whose key argument is declared in another theme layer (a field or property whose
/// file belongs to <c>Themes\X\Structure</c>, <c>Themes\X\Style</c> or the composition file <c>Themes\X\XTheme.cs</c>) is
/// reported at the call, unless the caller is in the same layer or in <c>Theming\Options\**</c> / <c>Theming\Composition\**</c>.
/// A key the analyzer cannot trace to a declaration is left alone. No baseline.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class OptionReadAnalyzer : ThemeRuleAnalyzer
{
    public const string DiagnosticId = "WVTH005";

    private static readonly DiagnosticDescriptor Rule = Describe(
        DiagnosticId,
        "Theme option accessed outside its layer",
        "'{0}' belongs to layer '{1}' and may not be read or published from layer '{2}'",
        "A theme option is read and published only by the layer that declares it (and by Theming\\Options and Theming\\Composition).");

    protected override DiagnosticDescriptor Descriptor => Rule;

    protected override bool AllowsBaseline => false;

    protected override void OnStart(CompilationStartAnalysisContext start, RuleEnv env)
    {
        start.RegisterSyntaxNodeAction(context => Analyze(context, env), SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context, RuleEnv env)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.ArgumentList.Arguments.Count == 0)
            return;
        string? called = invocation.Expression switch
        {
            MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
            IdentifierNameSyntax i => i.Identifier.ValueText,
            _ => null,
        };
        if (called != "Get" && called != "Peek" && called != "Publish")
            return;

        string? caller = env.RelativePath(invocation.SyntaxTree);
        if (OptionScopes.IsExemptCaller(caller))
            return;
        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method
            || !IsOptionReaderMethod(method))
            return;

        ISymbol? source = OptionScopes.KeySource(invocation.ArgumentList.Arguments[0].Expression, context.SemanticModel, context.CancellationToken);
        if (source is null)
            return;
        string? declared = OptionScopes.DeclaringLayer(source, env);
        if (declared is null)
            return;
        string callerLayer = OptionScopes.LayerOf(caller) ?? "core";
        if (string.Equals(declared, callerLayer, StringComparison.Ordinal))
            return;
        env.Report(context.ReportDiagnostic, invocation.GetLocation(), source.Name, source.Name, declared, callerLayer);
    }

    private static bool IsOptionReaderMethod(IMethodSymbol method)
    {
        INamedTypeSymbol? owner = method.ContainingType;
        if (owner is null)
            return false;
        if (method.Name == "Publish")
            return Symbols.IsType(owner, "FluentGpu.Theming", "OptionStore");
        return Symbols.Implements(owner, "FluentGpu.Theming", "IOptionReader")
               || Symbols.IsType(owner, "Wavee.Theming", "OptionValues");
    }
}

/// <summary>
/// WVTH009 — an option change is requested only with own-layer keys (themes technical plan, section 2.4). A call of
/// <c>Set</c> on an <c>IOptionRequests</c> (<c>ctx.OptionRequests.Set(...)</c>) whose key argument is declared in another theme
/// layer is reported; the same exemptions as WVTH005 apply. A key the analyzer cannot trace is left alone. No baseline.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class OptionRequestAnalyzer : ThemeRuleAnalyzer
{
    public const string DiagnosticId = "WVTH009";

    private static readonly DiagnosticDescriptor Rule = Describe(
        DiagnosticId,
        "Option change requested with another layer's key",
        "'{0}' belongs to layer '{1}'; a request from layer '{2}' may only set its own layer's options",
        "ctx.OptionRequests.Set is called only with keys of the calling layer.");

    protected override DiagnosticDescriptor Descriptor => Rule;

    protected override bool AllowsBaseline => false;

    protected override void OnStart(CompilationStartAnalysisContext start, RuleEnv env)
    {
        start.RegisterSyntaxNodeAction(context => Analyze(context, env), SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context, RuleEnv env)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.ArgumentList.Arguments.Count == 0)
            return;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Set" })
            return;

        string? caller = env.RelativePath(invocation.SyntaxTree);
        if (OptionScopes.IsExemptCaller(caller))
            return;
        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method
            || !Symbols.Implements(method.ContainingType, "FluentGpu.Theming", "IOptionRequests"))
            return;

        ISymbol? source = OptionScopes.KeySource(invocation.ArgumentList.Arguments[0].Expression, context.SemanticModel, context.CancellationToken);
        if (source is null)
            return;
        string? declared = OptionScopes.DeclaringLayer(source, env);
        if (declared is null)
            return;
        string callerLayer = OptionScopes.LayerOf(caller) ?? "core";
        if (string.Equals(declared, callerLayer, StringComparison.Ordinal))
            return;
        env.Report(context.ReportDiagnostic, invocation.GetLocation(), source.Name, source.Name, declared, callerLayer);
    }
}
