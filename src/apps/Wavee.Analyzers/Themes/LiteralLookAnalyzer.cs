using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Wavee.Analyzers.Themes;

/// <summary>
/// WVTH003 (S1) — no literal looks in a structure or in Common (themes technical plan, section 2.4). A structure decides
/// WHICH views and HOW they are arranged; every number and colour that makes something look a particular way (a size, a
/// radius, a gap, an opacity, a colour) is a role or metric of the style, or a rule under <c>Rules\**</c>. The rule covers
/// <c>Themes\&lt;X&gt;\Structure\**</c> and <c>Themes\Common\**</c> except <c>Rules\**</c> and reports, syntactically:
/// <list type="bullet">
/// <item>a numeric literal other than 0 and +-1 assigned (object initializer or named argument) to a look member
/// (<c>Width</c>, <c>Height</c>, <c>Padding</c>, <c>Gap</c>, <c>CornerRadius</c>, <c>FontSize</c>, <c>Opacity</c>...);</item>
/// <item>a colour built from components: <c>new ColorF(...)</c>, <c>ColorF.FromRgba/FromHsv(...)</c>.</item>
/// </list>
/// Classic's grandfathered files are listed in <c>analyzer-baseline\WVTH003.txt</c>, emptied by P3c; the baseline detail is
/// the member name (<c>Width</c>) or <c>ColorF</c>.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LiteralLookAnalyzer : ThemeRuleAnalyzer
{
    public const string DiagnosticId = "WVTH003";

    private static readonly DiagnosticDescriptor Rule = Describe(
        DiagnosticId,
        "Literal look in a structure",
        "'{0}' sets {1} from a literal; take it from a style role or metric, or move it into Rules\\**",
        "Structures and Themes\\Common carry no literal looks outside Rules\\**.");

    private static readonly HashSet<string> LookMembers = new HashSet<string>
    {
        "Width", "Height", "MinWidth", "MaxWidth", "MinHeight", "MaxHeight", "Size",
        "Padding", "Margin", "Gap", "RowGap", "ColumnGap", "Spacing",
        "Radius", "CornerRadius", "StrokeWidth", "Thickness", "BorderWidth",
        "FontSize", "LineHeight", "Opacity",
    };

    protected override DiagnosticDescriptor Descriptor => Rule;

    protected override void OnStart(CompilationStartAnalysisContext start, RuleEnv env)
    {
        start.RegisterSyntaxNodeAction(context => AnalyzeAssignment(context, env), SyntaxKind.SimpleAssignmentExpression);
        start.RegisterSyntaxNodeAction(context => AnalyzeNamedArgument(context, env), SyntaxKind.NameColon);
        start.RegisterSyntaxNodeAction(context => AnalyzeColour(context, env), SyntaxKind.ObjectCreationExpression, SyntaxKind.InvocationExpression);
    }

    /// <summary>True for <c>Themes/&lt;X&gt;/Structure/**</c> and <c>Themes/Common/**</c> outside a <c>Rules/</c> folder.</summary>
    private static bool InScope(string? relative)
    {
        if (relative is null)
            return false;
        FileScope scope = ThemeLayering.Classify(relative);
        if (scope.Kind != ScopeKind.Structure && scope.Kind != ScopeKind.Common)
            return false;
        return relative.IndexOf("/Rules/", System.StringComparison.OrdinalIgnoreCase) < 0;
    }

    private static void AnalyzeAssignment(SyntaxNodeAnalysisContext context, RuleEnv env)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;
        if (!InScope(env.RelativePath(assignment.SyntaxTree)))
            return;
        // Only object-initializer members (Width = 32) and property assignments; a local 'x = 4' is not a look member.
        string? member = assignment.Left switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
            _ => null,
        };
        if (member is null || !LookMembers.Contains(member))
            return;
        CheckLiteral(context, env, member, assignment.Right);
    }

    private static void AnalyzeNamedArgument(SyntaxNodeAnalysisContext context, RuleEnv env)
    {
        var nameColon = (NameColonSyntax)context.Node;
        if (nameColon.Parent is not ArgumentSyntax argument)
            return;
        if (!InScope(env.RelativePath(nameColon.SyntaxTree)))
            return;
        string member = nameColon.Name.Identifier.ValueText;
        // Named arguments use parameter names (width:, padding:); compare case-insensitively with the member list.
        foreach (string look in LookMembers)
        {
            if (string.Equals(look, member, System.StringComparison.OrdinalIgnoreCase))
            {
                CheckLiteral(context, env, look, argument.Expression);
                return;
            }
        }
    }

    private static void CheckLiteral(SyntaxNodeAnalysisContext context, RuleEnv env, string member, ExpressionSyntax value)
    {
        ExpressionSyntax inner = value;
        if (inner is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.UnaryMinusExpression } minus)
            inner = minus.Operand;
        if (inner is not LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } literal)
            return;
        if (IsNeutral(literal.Token.Value))
            return;
        env.Report(context.ReportDiagnostic, value.GetLocation(), member, value.ToString(), member);
    }

    /// <summary>0 and 1 (any numeric spelling) are structure, not looks: a zero gap, a unit opacity, a one-fraction.</summary>
    private static bool IsNeutral(object? value)
        => value is System.IConvertible convertible
           && convertible.ToDouble(System.Globalization.CultureInfo.InvariantCulture) is var d
           && (d == 0d || d == 1d);

    private static void AnalyzeColour(SyntaxNodeAnalysisContext context, RuleEnv env)
    {
        if (!InScope(env.RelativePath(context.Node.SyntaxTree)))
            return;
        if (context.Node is ObjectCreationExpressionSyntax creation)
        {
            if (TypeName(creation.Type) == "ColorF" && creation.ArgumentList is { Arguments.Count: > 0 })
                env.Report(context.ReportDiagnostic, creation.GetLocation(), "ColorF", creation.ToString(), "a colour");
            return;
        }
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is MemberAccessExpressionSyntax { Expression: var target, Name.Identifier.ValueText: var method }
            && (method == "FromRgba" || method == "FromHsv")
            && TypeName(target) == "ColorF")
            env.Report(context.ReportDiagnostic, invocation.GetLocation(), "ColorF", invocation.ToString(), "a colour");
    }

    private static string TypeName(SyntaxNode? syntax) => syntax switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        QualifiedNameSyntax q => q.Right.Identifier.ValueText,
        MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
        _ => "",
    };
}
