using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Wavee.Analyzers.Themes;

/// <summary>
/// WVTH008 — core hosts build no presentation (themes technical plan, section 2.4). A type marked
/// <c>[CoreHost]</c> (<c>Wavee.Theming.Contracts.CoreHostAttribute</c>) positions slots and wires engine behaviour controls
/// that arrive as slots; it constructs no element (<c>new BoxEl</c>, <c>new TextEl</c>... any type deriving from
/// <c>FluentGpu.Dsl.Element</c>, except an element obtained from a slot or a call) and reads no token or role (<c>Tok.*</c>,
/// <c>Design.*</c>, the style-role facade <c>R.*</c> of <c>Wavee.Theming.Roles</c> and the live stage-ink facade <c>StageInk.*</c> next
/// to it, the colour roles' static face). The rule shrinks per surface PR: a surface PR puts <c>[CoreHost]</c> on the host it extracts, and the
/// violations that host still has are listed in <c>analyzer-baseline\WVTH008.txt</c> (path, detail = the constructed type or
/// the member read) until the PR that moves them out; T13 deletes the baseline. With no type marked, the rule reports nothing.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CoreHostPresentationAnalyzer : ThemeRuleAnalyzer
{
    public const string DiagnosticId = "WVTH008";

    private static readonly DiagnosticDescriptor Rule = Describe(
        DiagnosticId,
        "Presentation built in a core host",
        "'{0}' is presentation and is built in the core host '{1}'; the host positions slots, the theme's views build the look",
        "A [CoreHost] type constructs no element and reads no token or role.");

    protected override DiagnosticDescriptor Descriptor => Rule;

    protected override void OnStart(CompilationStartAnalysisContext start, RuleEnv env)
    {
        start.RegisterSyntaxNodeAction(context => AnalyzeCreation(context, env),
            SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
        start.RegisterSyntaxNodeAction(context => AnalyzeToken(context, env), SyntaxKind.IdentifierName);
    }

    private static INamedTypeSymbol? HostOf(ISymbol? containing)
    {
        for (INamedTypeSymbol? t = containing as INamedTypeSymbol ?? containing?.ContainingType; t is not null; t = t.ContainingType)
        {
            foreach (AttributeData attribute in t.GetAttributes())
            {
                if (Symbols.IsType(attribute.AttributeClass, "Wavee.Theming.Contracts", "CoreHostAttribute"))
                    return t;
            }
        }
        return null;
    }

    private static void AnalyzeCreation(SyntaxNodeAnalysisContext context, RuleEnv env)
    {
        INamedTypeSymbol? host = HostOf(context.ContainingSymbol);
        if (host is null)
            return;
        var creation = (BaseObjectCreationExpressionSyntax)context.Node;
        if (context.SemanticModel.GetTypeInfo(creation, context.CancellationToken).Type is not INamedTypeSymbol type
            || !Symbols.DerivesFrom(type, "FluentGpu.Dsl", "Element"))
            return;
        env.Report(context.ReportDiagnostic, creation.GetLocation(), type.Name, type.Name, host.Name);
    }

    private static void AnalyzeToken(SyntaxNodeAnalysisContext context, RuleEnv env)
    {
        var name = (IdentifierNameSyntax)context.Node;
        string text = name.Identifier.ValueText;
        if (text != "Tok" && text != "Design" && text != "R" && text != "StageInk")
            return;
        INamedTypeSymbol? host = HostOf(context.ContainingSymbol);
        if (host is null)
            return;
        // Only the type name as the receiver of a member access: 'Tok.Epoch', 'Design.Motion.Fast', 'R.Type.TrackTitle', 'StageInk.Ink', and the
        // qualified spellings 'FluentGpu.Dsl.Tok.Epoch' / 'Wavee.Design.Gutter' (where the name is itself the right side of an access).
        ExpressionSyntax typeExpression = name;
        if (name.Parent is MemberAccessExpressionSyntax qualified && ReferenceEquals(qualified.Name, name))
            typeExpression = qualified;
        if (typeExpression.Parent is not MemberAccessExpressionSyntax access || !ReferenceEquals(access.Expression, typeExpression))
            return;
        ISymbol? symbol = context.SemanticModel.GetSymbolInfo(name, context.CancellationToken).Symbol;
        bool tok = Symbols.IsType(symbol as ITypeSymbol, "FluentGpu.Dsl", "Tok");
        bool design = Symbols.IsType(symbol as ITypeSymbol, "Wavee", "Design");
        bool roles = Symbols.IsType(symbol as ITypeSymbol, "Wavee.Theming.Roles", "R")
            || Symbols.IsType(symbol as ITypeSymbol, "Wavee.Theming.Roles", "StageInk");
        if (!tok && !design && !roles)
            return;
        string member = text + "." + access.Name.Identifier.ValueText;
        env.Report(context.ReportDiagnostic, access.GetLocation(), member, member, host.Name);
    }
}
