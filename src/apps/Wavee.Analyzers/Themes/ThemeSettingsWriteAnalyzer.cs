using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Wavee.Analyzers.Themes;

/// <summary>
/// WVTH007 — <c>theme.*</c> settings are written only where theming owns them (themes technical plan, section 2.4). A
/// persisted key is minted by <c>new SettingKey&lt;T&gt;("theme.x", default)</c>; a key whose name starts with
/// <c>theme.</c> may be created (and so read, written or removed) only in <c>Theming\Options\**</c>,
/// <c>Theming\ThemeBootstrap.cs</c> and <c>Theming\Composition\ThemeHost.cs</c> (<c>theme.id</c>). <c>theme.mode</c> is the one
/// exception: it stays a core key, declared in <c>Platform.Keys</c> and written by <c>Appearance</c>. The rule also reports a
/// constant string starting with <c>theme.</c> passed to <c>IAppSettings.Has</c> or <c>Remove</c> outside those files. No
/// baseline.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ThemeSettingsWriteAnalyzer : ThemeRuleAnalyzer
{
    public const string DiagnosticId = "WVTH007";

    private const string Prefix = "theme.";
    private const string CoreKey = "theme.mode";

    private static readonly DiagnosticDescriptor Rule = Describe(
        DiagnosticId,
        "theme.* setting outside theming",
        "The setting '{0}' may be declared or touched only in Theming\\Options, Theming\\ThemeBootstrap.cs and Theming\\Composition\\ThemeHost.cs",
        "theme.* keys belong to Theming; theme.mode is the one core key and is written by Appearance.");

    protected override DiagnosticDescriptor Descriptor => Rule;

    protected override bool AllowsBaseline => false;

    protected override void OnStart(CompilationStartAnalysisContext start, RuleEnv env)
    {
        start.RegisterSyntaxNodeAction(context => AnalyzeCreation(context, env),
            SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
        start.RegisterSyntaxNodeAction(context => AnalyzeCall(context, env), SyntaxKind.InvocationExpression);
    }

    private static bool IsOwner(string? relative)
        => relative is not null
           && (relative.StartsWith("Theming/Options/", StringComparison.OrdinalIgnoreCase)
               || string.Equals(relative, "Theming/ThemeBootstrap.cs", StringComparison.OrdinalIgnoreCase)
               || string.Equals(relative, "Theming/Composition/ThemeHost.cs", StringComparison.OrdinalIgnoreCase));

    private static bool IsThemeKey(string? name)
        => name is not null && name.StartsWith(Prefix, StringComparison.Ordinal) && !string.Equals(name, CoreKey, StringComparison.Ordinal);

    private static void AnalyzeCreation(SyntaxNodeAnalysisContext context, RuleEnv env)
    {
        var creation = (BaseObjectCreationExpressionSyntax)context.Node;
        if (creation.ArgumentList is not { Arguments.Count: > 0 } args)
            return;
        if (IsOwner(env.RelativePath(creation.SyntaxTree)))
            return;
        if (context.SemanticModel.GetTypeInfo(creation, context.CancellationToken).Type is not INamedTypeSymbol type
            || !Symbols.IsType(type, "Wavee", "SettingKey"))
            return;
        string? name = ConstantName(args.Arguments[0].Expression, context);
        if (IsThemeKey(name))
            env.Report(context.ReportDiagnostic, creation.GetLocation(), name!, name);
    }

    private static void AnalyzeCall(SyntaxNodeAnalysisContext context, RuleEnv env)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.ArgumentList.Arguments.Count != 1
            || invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: var method }
            || (method != "Has" && method != "Remove"))
            return;
        if (IsOwner(env.RelativePath(invocation.SyntaxTree)))
            return;
        string? name = ConstantName(invocation.ArgumentList.Arguments[0].Expression, context);
        if (!IsThemeKey(name))
            return;
        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol symbol
            || !Symbols.Implements(symbol.ContainingType, "Wavee", "IAppSettings"))
            return;
        env.Report(context.ReportDiagnostic, invocation.GetLocation(), name!, name);
    }

    /// <summary>The constant string value of <paramref name="expression"/> (a literal, a const, a constant concatenation or
    /// the constant prefix of an interpolated string), or null.</summary>
    private static string? ConstantName(ExpressionSyntax expression, SyntaxNodeAnalysisContext context)
    {
        Optional<object?> constant = context.SemanticModel.GetConstantValue(expression, context.CancellationToken);
        if (constant.HasValue && constant.Value is string text)
            return text;
        if (expression is InterpolatedStringExpressionSyntax interpolated
            && interpolated.Contents.Count > 0
            && interpolated.Contents[0] is InterpolatedStringTextSyntax first)
            return first.TextToken.ValueText;
        return null;
    }
}
