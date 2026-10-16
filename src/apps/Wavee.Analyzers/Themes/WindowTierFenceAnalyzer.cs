using System;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Wavee.Analyzers.Themes;

/// <summary>
/// WVTH013 (tiers) - window-size decisions go through the tier system (themes technical plan, sections 2.4 and 3.2). A view
/// never compares a width with a number: it reads <c>ctx.Tier()</c> / <c>TierFacts</c> and its structure's layout record, and
/// the breakpoints themselves live in the structure's <c>Rules\Tiers*.cs</c>. A syntax rule (round-3 review R3-M4), over
/// <c>Themes\**</c> and over the types marked <c>[CoreHost]</c>:
/// <list type="bullet">
/// <item><b>(a)</b> a relational or equality comparison (<c>&lt; &lt;= &gt; &gt;= == !=</c>, a relational or constant pattern
/// of <c>is</c> / <c>switch</c>) with a measurement on one side and a numeric literal, or a constant declared outside
/// <c>Rules\Tiers*.cs</c>, on the other. A measurement is <c>Viewport.Size</c> (read through <c>UseContext</c> /
/// <c>UseContextSignal</c>), the result of <c>UseMeasuredWidth</c> / <c>UseMeasuredBounds</c>, the width parameter (the last
/// one) of a <c>Responsive.Of</c> build lambda, or a <c>TierFacts</c> width/height member (<c>ContentWidth</c>,
/// <c>ViewportWidth</c>, <c>ViewportHeight</c>); a member read, arithmetic, a <c>Math</c> call and a local assigned from one
/// carry it. Reported at the comparison.</item>
/// <item><b>(b)</b> a <c>Responsive.Of</c> call whose build lambda contains such a comparison, and a
/// <c>UseMeasuredWidth</c> / <c>UseMeasuredBounds</c> call whose result feeds one in its member. Reported at the call.</item>
/// <item>(c), page-level widths come from the layout record, is a review rule: an analyzer cannot tell a page-level region from
/// one of the ~255 control-size <c>Width = N</c> literals, so nothing here looks at those.</item>
/// </list>
/// Allowed: <c>Rules\**</c> of a structure, <c>Theming\Policies\**</c>, <c>Shell\WindowTiers.cs</c> (the measurement).
/// A comparison against zero is a usability guard, not a breakpoint, and is not reported. A hard gate with no baseline (Classic's
/// grandfathered files were emptied family by family by the Classic tier PRs and the file was deleted by TC10): an
/// <c>analyzer-baseline\WVTH013.txt</c> is WVTH901. The first argument of a report is the measurement kind (<c>Viewport.Size</c>,
/// <c>UseMeasuredWidth</c>, <c>UseMeasuredBounds</c>, <c>Responsive.Of</c>, <c>TierFacts.ContentWidth</c>...), the same string for
/// (a) and (b), and names what was measured in the message. The Common blocks (<c>Themes\Common\**</c>) are held to the rule like
/// a structure; their breakpoint constants, if they ever need any, live in <c>Themes\Common\Rules\Tiers*.cs</c>.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class WindowTierFenceAnalyzer : ThemeRuleAnalyzer
{
    public const string DiagnosticId = "WVTH013";

    private static readonly DiagnosticDescriptor Rule = Describe(
        DiagnosticId,
        "Window-size breakpoint outside the tier system",
        "{0}; decide by tier: read ctx.Tier() / TierFacts and the structure's layout record, and keep breakpoints in the structure's Rules\\Tiers*.cs",
        "Themes\\** and [CoreHost] types do not compare a measured width with a literal or a constant outside Rules\\Tiers*.cs.");

    private const int MaxDepth = 6;

    protected override DiagnosticDescriptor Descriptor => Rule;

    protected override bool AllowsBaseline => false;

    protected override void OnStart(CompilationStartAnalysisContext start, RuleEnv env)
    {
        start.RegisterSyntaxNodeAction(context => AnalyzeComparison(context, env),
            SyntaxKind.LessThanExpression, SyntaxKind.LessThanOrEqualExpression,
            SyntaxKind.GreaterThanExpression, SyntaxKind.GreaterThanOrEqualExpression,
            SyntaxKind.EqualsExpression, SyntaxKind.NotEqualsExpression,
            SyntaxKind.IsPatternExpression, SyntaxKind.SwitchExpression);
        start.RegisterSyntaxNodeAction(context => AnalyzeCall(context, env), SyntaxKind.InvocationExpression);
    }

    // ---- scope -----------------------------------------------------------------------------------------------------------

    /// <summary>True for a file the rule applies to: <c>Themes\**</c>, or a <c>[CoreHost]</c> type, minus the allowed files.</summary>
    private static bool Applies(RuleEnv env, SyntaxNodeAnalysisContext context)
    {
        string? relative = env.RelativePath(context.Node.SyntaxTree);
        if (relative is not null)
        {
            if (relative.StartsWith("Theming/Policies/", StringComparison.OrdinalIgnoreCase)
                || string.Equals(relative, "Shell/WindowTiers.cs", StringComparison.OrdinalIgnoreCase))
                return false;
            if (relative.StartsWith("Themes/", StringComparison.OrdinalIgnoreCase))
            {
                FileScope scope = ThemeLayering.Classify(relative);
                return !(scope.Kind == ScopeKind.Structure && relative.IndexOf("/Rules/", StringComparison.OrdinalIgnoreCase) >= 0);
            }
        }
        return IsInCoreHost(context.ContainingSymbol);
    }

    private static bool IsInCoreHost(ISymbol? containing)
    {
        for (INamedTypeSymbol? t = containing as INamedTypeSymbol ?? containing?.ContainingType; t is not null; t = t.ContainingType)
        {
            foreach (AttributeData attribute in t.GetAttributes())
            {
                if (Symbols.IsType(attribute.AttributeClass, "Wavee.Theming.Contracts", "CoreHostAttribute"))
                    return true;
            }
        }
        return false;
    }

    /// <summary>True for <c>Themes/&lt;X&gt;/Structure/Rules/Tiers*.cs</c>, the one home of breakpoint constants.</summary>
    private static bool IsTiersFile(string? relative)
    {
        if (relative is null)
            return false;
        int rules = relative.IndexOf("/Rules/Tiers", StringComparison.OrdinalIgnoreCase);
        return rules >= 0 && relative.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            && relative.IndexOf('/', rules + "/Rules/".Length) < 0;
    }

    // ---- (a) comparisons -------------------------------------------------------------------------------------------------

    private static void AnalyzeComparison(SyntaxNodeAnalysisContext context, RuleEnv env)
    {
        if (!Applies(env, context))
            return;
        (string Source, SyntaxNode Origin)? found = FindFencedComparison(context.Node, context.SemanticModel, env, context.CancellationToken);
        if (found is null)
            return;
        SyntaxNode shown = context.Node is SwitchExpressionSyntax switchExpression ? switchExpression.GoverningExpression : context.Node;
        env.Report(context.ReportDiagnostic, shown.GetLocation(), found.Value.Source,
            "'" + Shorten(shown.ToString()) + "' compares " + found.Value.Source + " with a number");
    }

    private static string Shorten(string text)
    {
        string flat = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= 80 ? flat : flat.Substring(0, 77) + "...";
    }

    /// <summary>The measurement of the fenced comparison <paramref name="node"/> is, or null when it is not one.</summary>
    private static (string Source, SyntaxNode Origin)? FindFencedComparison(SyntaxNode node, SemanticModel model, RuleEnv env, CancellationToken ct)
    {
        switch (node)
        {
            case BinaryExpressionSyntax binary when IsComparison(binary.Kind()):
            {
                (string Source, SyntaxNode Origin)? left = Measure(binary.Left, model, 0, ct);
                if (left is not null && IsFencedThreshold(binary.Right, model, env, ct))
                    return left;
                (string Source, SyntaxNode Origin)? right = Measure(binary.Right, model, 0, ct);
                if (right is not null && IsFencedThreshold(binary.Left, model, env, ct))
                    return right;
                return null;
            }
            case IsPatternExpressionSyntax isPattern:
            {
                (string Source, SyntaxNode Origin)? measured = Measure(isPattern.Expression, model, 0, ct);
                return measured is not null && PatternHasThreshold(isPattern.Pattern, model, env, ct) ? measured : null;
            }
            case SwitchExpressionSyntax switchExpression:
            {
                (string Source, SyntaxNode Origin)? measured = Measure(switchExpression.GoverningExpression, model, 0, ct);
                if (measured is null)
                    return null;
                foreach (SwitchExpressionArmSyntax arm in switchExpression.Arms)
                {
                    if (PatternHasThreshold(arm.Pattern, model, env, ct))
                        return measured;
                }
                return null;
            }
            default:
                return null;
        }
    }

    private static bool IsComparison(SyntaxKind kind)
        => kind is SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression
            or SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression
            or SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression;

    private static bool PatternHasThreshold(PatternSyntax pattern, SemanticModel model, RuleEnv env, CancellationToken ct)
    {
        foreach (SyntaxNode inner in pattern.DescendantNodesAndSelf())
        {
            ExpressionSyntax? value = inner switch
            {
                RelationalPatternSyntax relational => relational.Expression,
                ConstantPatternSyntax constant => constant.Expression,
                _ => null,
            };
            if (value is not null && IsFencedThreshold(value, model, env, ct))
                return true;
        }
        return false;
    }

    /// <summary>True for a non-zero numeric literal, or a numeric constant expression with a constant declared outside
    /// <c>Rules\Tiers*.cs</c> (or with none at all, as in <c>640 + 8</c>).</summary>
    private static bool IsFencedThreshold(ExpressionSyntax expression, SemanticModel model, RuleEnv env, CancellationToken ct)
    {
        Optional<object?> constant = model.GetConstantValue(expression, ct);
        if (!constant.HasValue || !TryToDouble(constant.Value, out double number) || number == 0d)
            return false;

        bool sawConstant = false;
        bool sawOutsideTiers = false;
        foreach (IdentifierNameSyntax name in expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            ISymbol? symbol = model.GetSymbolInfo(name, ct).Symbol;
            if (symbol is not (IFieldSymbol { IsConst: true } or ILocalSymbol { IsConst: true }))
                continue;
            sawConstant = true;
            SyntaxTree? tree = Symbols.DeclaringTree(symbol);
            if (tree is null || !IsTiersFile(env.RelativePath(tree)))
                sawOutsideTiers = true;
        }
        return !sawConstant || sawOutsideTiers;
    }

    private static bool TryToDouble(object? value, out double number)
    {
        number = 0d;
        if (value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal)
        {
            number = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        return false;
    }

    // ---- measurements ----------------------------------------------------------------------------------------------------

    /// <summary>The measurement <paramref name="expression"/> carries: its kind (the baseline detail) and the node it comes
    /// from (the hook call, the lambda, or the expression itself), or null.</summary>
    private static (string Source, SyntaxNode Origin)? Measure(ExpressionSyntax? expression, SemanticModel model, int depth, CancellationToken ct)
    {
        if (expression is null || depth > MaxDepth)
            return null;
        switch (expression)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return Measure(parenthesized.Expression, model, depth + 1, ct);
            case CastExpressionSyntax cast:
                return Measure(cast.Expression, model, depth + 1, ct);
            case PostfixUnaryExpressionSyntax postfix when postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                return Measure(postfix.Operand, model, depth + 1, ct);
            case BinaryExpressionSyntax binary when binary.Kind() is SyntaxKind.AddExpression or SyntaxKind.SubtractExpression
                or SyntaxKind.MultiplyExpression or SyntaxKind.DivideExpression:
                return Measure(binary.Left, model, depth + 1, ct) ?? Measure(binary.Right, model, depth + 1, ct);
            case InvocationExpressionSyntax invocation:
                return MeasureInvocation(invocation, model, depth, ct);
            case MemberAccessExpressionSyntax access:
                return MeasureMember(access, model, depth, ct);
            case IdentifierNameSyntax name:
                return MeasureName(name, model, depth, ct);
            default:
                return null;
        }
    }

    private static IMethodSymbol? MethodOf(InvocationExpressionSyntax invocation, SemanticModel model, CancellationToken ct)
    {
        SymbolInfo info = model.GetSymbolInfo(invocation, ct);
        if (info.Symbol is IMethodSymbol method)
            return method;
        return info.CandidateSymbols.IsDefaultOrEmpty ? null : info.CandidateSymbols[0] as IMethodSymbol;
    }

    private static (string Source, SyntaxNode Origin)? MeasureInvocation(InvocationExpressionSyntax invocation, SemanticModel model, int depth, CancellationToken ct)
    {
        IMethodSymbol? method = MethodOf(invocation, model, ct);
        if (method is null)
            return null;

        INamedTypeSymbol? owner = method.ContainingType;
        if (Symbols.Namespace(owner?.ContainingNamespace) == "FluentGpu.Hooks")
        {
            if (method.Name == "UseMeasuredWidth")
                return ("UseMeasuredWidth", invocation);
            if (method.Name == "UseMeasuredBounds")
                return ("UseMeasuredBounds", invocation);
            if ((method.Name == "UseContext" || method.Name == "UseContextSignal")
                && invocation.ArgumentList.Arguments.Count > 0
                && IsViewportSize(invocation.ArgumentList.Arguments[0].Expression, model, ct))
                return ("Viewport.Size", invocation);
            return null;
        }

        if (owner is { Name: "Math" or "MathF" } && Symbols.Namespace(owner.ContainingNamespace) == "System")
        {
            foreach (ArgumentSyntax argument in invocation.ArgumentList.Arguments)
            {
                (string Source, SyntaxNode Origin)? inner = Measure(argument.Expression, model, depth + 1, ct);
                if (inner is not null)
                    return inner;
            }
        }
        return null;
    }

    private static bool IsViewportSize(ExpressionSyntax expression, SemanticModel model, CancellationToken ct)
        => IsViewportSizeSymbol(model.GetSymbolInfo(expression, ct).Symbol);

    private static bool IsViewportSizeSymbol(ISymbol? symbol)
        => symbol is IFieldSymbol { Name: "Size" } field && Symbols.IsType(field.ContainingType, "FluentGpu.Hooks", "Viewport");

    private static (string Source, SyntaxNode Origin)? MeasureMember(MemberAccessExpressionSyntax access, SemanticModel model, int depth, CancellationToken ct)
    {
        ISymbol? symbol = model.GetSymbolInfo(access, ct).Symbol;
        if (IsViewportSizeSymbol(symbol))
            return ("Viewport.Size", access);
        if (symbol is IPropertySymbol or IFieldSymbol
            && symbol.ContainingType is { } owner && Symbols.IsType(owner, "Wavee.Theming", "TierFacts")
            && symbol.Name is "ContentWidth" or "ViewportWidth" or "ViewportHeight")
            return ("TierFacts." + symbol.Name, access);
        if (symbol is IMethodSymbol)
            return null;
        // A member read off a measurement (Viewport.Size.Default.Width, signal.Value, bounds.W) is still the measurement.
        return Measure(access.Expression, model, depth + 1, ct);
    }

    private static (string Source, SyntaxNode Origin)? MeasureName(IdentifierNameSyntax name, SemanticModel model, int depth, CancellationToken ct)
    {
        ISymbol? symbol = model.GetSymbolInfo(name, ct).Symbol;
        switch (symbol)
        {
            case ILocalSymbol local:
                // A local assigned from a measurement (the initializer is in the same tree as the use).
                foreach (SyntaxReference reference in local.DeclaringSyntaxReferences)
                {
                    if (reference.GetSyntax(ct) is VariableDeclaratorSyntax { Initializer.Value: { } initializer })
                        return Measure(initializer, model, depth + 1, ct);
                }
                return null;
            case IParameterSymbol parameter:
                return ResponsiveWidthParameter(parameter, model, ct);
            default:
                return null;
        }
    }

    /// <summary>The width parameter (the last one) of a lambda that is the build argument of <c>Responsive.Of</c>.</summary>
    private static (string Source, SyntaxNode Origin)? ResponsiveWidthParameter(IParameterSymbol parameter, SemanticModel model, CancellationToken ct)
    {
        if (parameter.ContainingSymbol is not IMethodSymbol { MethodKind: MethodKind.AnonymousFunction }
            || parameter.DeclaringSyntaxReferences.IsDefaultOrEmpty)
            return null;
        SyntaxNode declaration = parameter.DeclaringSyntaxReferences[0].GetSyntax(ct);
        LambdaExpressionSyntax? lambda = declaration.FirstAncestorOrSelf<LambdaExpressionSyntax>();
        if (lambda is null)
            return null;
        ParameterSyntax? last = lambda switch
        {
            SimpleLambdaExpressionSyntax simple => simple.Parameter,
            ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.ParameterList.Parameters.LastOrDefault(),
            _ => null,
        };
        if (last is null || !ReferenceEquals(last, declaration) && last.Span != declaration.Span)
            return null;
        if (lambda.Parent is ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax call } }
            && MethodOf(call, model, ct) is { } method && IsResponsiveOf(method))
            return ("Responsive.Of", lambda);
        return null;
    }

    private static bool IsResponsiveOf(IMethodSymbol method)
        => method.Name == "Of" && Symbols.IsType(method.ContainingType, "FluentGpu.Controls", "Responsive");

    // ---- (b) the calls ---------------------------------------------------------------------------------------------------

    private static void AnalyzeCall(SyntaxNodeAnalysisContext context, RuleEnv env)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        string? called = invocation.Expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
            GenericNameSyntax generic => generic.Identifier.ValueText,
            _ => null,
        };
        if (called is not ("Of" or "UseMeasuredWidth" or "UseMeasuredBounds"))
            return;
        if (!Applies(env, context))
            return;
        IMethodSymbol? method = MethodOf(invocation, context.SemanticModel, context.CancellationToken);
        if (method is null)
            return;

        if (IsResponsiveOf(method))
        {
            foreach (ArgumentSyntax argument in invocation.ArgumentList.Arguments)
            {
                if (argument.Expression is not LambdaExpressionSyntax lambda
                    || !ContainsFencedComparison(lambda.Body, null, context.SemanticModel, env, context.CancellationToken))
                    continue;
                env.Report(context.ReportDiagnostic, invocation.GetLocation(), "Responsive.Of",
                    "'Responsive.Of' builds from a width breakpoint");
                return;
            }
            return;
        }

        if (Symbols.Namespace(method.ContainingType?.ContainingNamespace) != "FluentGpu.Hooks")
            return;
        if (method.Name != "UseMeasuredWidth" && method.Name != "UseMeasuredBounds")
            return;
        SyntaxNode? scope = invocation.FirstAncestorOrSelf<MemberDeclarationSyntax>();
        if (scope is null || !ContainsFencedComparison(scope, invocation, context.SemanticModel, env, context.CancellationToken))
            return;
        env.Report(context.ReportDiagnostic, invocation.GetLocation(), method.Name,
            "'" + method.Name + "' feeds a width breakpoint");
    }

    /// <summary>True when <paramref name="scope"/> holds a fenced comparison; with <paramref name="origin"/>, only one that
    /// measures from that node.</summary>
    private static bool ContainsFencedComparison(SyntaxNode scope, SyntaxNode? origin, SemanticModel model, RuleEnv env, CancellationToken ct)
    {
        foreach (SyntaxNode node in scope.DescendantNodesAndSelf())
        {
            if (node is not (BinaryExpressionSyntax or IsPatternExpressionSyntax or SwitchExpressionSyntax))
                continue;
            (string Source, SyntaxNode Origin)? found = FindFencedComparison(node, model, env, ct);
            if (found is null)
                continue;
            if (origin is null || SameNode(found.Value.Origin, origin))
                return true;
        }
        return false;
    }

    private static bool SameNode(SyntaxNode a, SyntaxNode b)
        => ReferenceEquals(a.SyntaxTree, b.SyntaxTree) && a.RawKind == b.RawKind && a.FullSpan == b.FullSpan;
}
