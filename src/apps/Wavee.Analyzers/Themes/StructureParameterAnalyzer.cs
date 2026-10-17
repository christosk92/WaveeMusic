using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Wavee.Analyzers.Themes;

/// <summary>
/// WVTH011 — every structure parameter is exercised with at least two values by the S3 probe matrix (themes technical
/// plan, section 2.4). A structure parameter is a theme option declared hidden:
/// <c>new ToggleOption("narrow", ...) with { Visibility = OptionVisibility.Hidden }</c> (or the same in an object
/// initializer). The probe matrix lists, in <c>analyzer-input\WVTH011.matrix.txt</c>, one line per parameter:
/// <code>
/// narrow = false, true
/// </code>
/// The rule reports a hidden option whose id is not a constant, has no line, or has fewer than two distinct values: a
/// parameter is a real, exercised variation and never an identity switch. The runtime probe (S3) asserts the matrix is what it
/// renders. No baseline.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StructureParameterAnalyzer : ThemeRuleAnalyzer
{
    public const string DiagnosticId = "WVTH011";
    public const string InputFile = "WVTH011.matrix.txt";

    private static readonly DiagnosticDescriptor Rule = Describe(
        DiagnosticId,
        "Structure parameter not exercised with two values",
        "{0}",
        "Each hidden structure option needs a line in analyzer-input\\WVTH011.matrix.txt with at least two distinct values.");

    protected override DiagnosticDescriptor Descriptor => Rule;

    protected override bool AllowsBaseline => false;

    protected override void OnStart(CompilationStartAnalysisContext start, RuleEnv env)
    {
        var matrix = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (string line in env.InputLines(InputFile, start.CancellationToken))
        {
            int eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            string id = line.Substring(0, eq).Trim();
            if (!matrix.TryGetValue(id, out HashSet<string>? values))
                matrix[id] = values = new HashSet<string>(StringComparer.Ordinal);
            foreach (string value in line.Substring(eq + 1).Split(','))
            {
                string v = value.Trim();
                if (v.Length > 0)
                    values.Add(v);
            }
        }
        start.RegisterSyntaxNodeAction(context => AnalyzeWith(context, matrix), SyntaxKind.WithExpression);
        start.RegisterSyntaxNodeAction(context => AnalyzeInitializer(context, matrix), SyntaxKind.ObjectCreationExpression);
    }

    private static void AnalyzeWith(SyntaxNodeAnalysisContext context, Dictionary<string, HashSet<string>> matrix)
    {
        var withExpression = (WithExpressionSyntax)context.Node;
        if (!SetsHidden(withExpression.Initializer, context))
            return;
        if (Unwrap(withExpression.Expression) is BaseObjectCreationExpressionSyntax creation)
            Check(context, matrix, creation, withExpression.GetLocation());
    }

    private static void AnalyzeInitializer(SyntaxNodeAnalysisContext context, Dictionary<string, HashSet<string>> matrix)
    {
        var creation = (ObjectCreationExpressionSyntax)context.Node;
        if (creation.Initializer is null || !SetsHidden(creation.Initializer, context))
            return;
        Check(context, matrix, creation, creation.GetLocation());
    }

    private static ExpressionSyntax Unwrap(ExpressionSyntax e)
        => e is ParenthesizedExpressionSyntax p ? Unwrap(p.Expression) : e;

    /// <summary>True when the initializer assigns <c>Visibility = OptionVisibility.Hidden</c>.</summary>
    private static bool SetsHidden(InitializerExpressionSyntax initializer, SyntaxNodeAnalysisContext context)
    {
        foreach (ExpressionSyntax expression in initializer.Expressions)
        {
            if (expression is not AssignmentExpressionSyntax { Left: IdentifierNameSyntax { Identifier.ValueText: "Visibility" } } assignment)
                continue;
            ISymbol? value = context.SemanticModel.GetSymbolInfo(assignment.Right, context.CancellationToken).Symbol;
            if (value is IFieldSymbol { Name: "Hidden", ContainingType: { } owner } && Symbols.IsType(owner, "Wavee.Theming", "OptionVisibility"))
                return true;
        }
        return false;
    }

    private static void Check(SyntaxNodeAnalysisContext context, Dictionary<string, HashSet<string>> matrix,
        BaseObjectCreationExpressionSyntax creation, Location location)
    {
        if (context.SemanticModel.GetTypeInfo(creation, context.CancellationToken).Type is not INamedTypeSymbol type
            || !Symbols.DerivesFrom(type, "Wavee.Theming", "ThemeOption"))
            return;
        string? id = null;
        if (creation.ArgumentList is { Arguments.Count: > 0 } args)
        {
            Optional<object?> constant = context.SemanticModel.GetConstantValue(args.Arguments[0].Expression, context.CancellationToken);
            id = constant.HasValue ? constant.Value as string : null;
        }
        if (id is null)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, location, "the id of a structure parameter must be a constant string"));
            return;
        }
        if (!matrix.TryGetValue(id, out HashSet<string>? values))
            context.ReportDiagnostic(Diagnostic.Create(Rule, location, "structure parameter '" + id + "' has no line in analyzer-input/" + InputFile));
        else if (values.Count < 2)
            context.ReportDiagnostic(Diagnostic.Create(Rule, location, "structure parameter '" + id + "' is exercised with " + values.Count + " value(s); the probe matrix needs at least two"));
    }
}
