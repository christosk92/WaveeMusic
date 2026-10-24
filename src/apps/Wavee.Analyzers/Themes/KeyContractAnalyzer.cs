using System;
using System.Collections.Concurrent;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Wavee.Analyzers.Themes;

/// <summary>
/// WVTH006 — key ids and props record shape (themes technical plan, section 2.4). The published keys are the contract
/// between core and every theme, so:
/// <list type="number">
/// <item>a <c>ViewKey&lt;TProps&gt;</c> or <c>StyleKey&lt;TStyle&gt;</c> is created only under <c>Theming\Contracts\</c>
/// (<c>ViewKeys.cs</c>, <c>StyleKeys.cs</c>);</item>
/// <item>its id is a constant dotted path whose segments start with an upper-case letter (<c>Shell.Frame</c>,
/// <c>Home.Zone.Show</c>), optionally ending in <c>@N</c> (a versioned key), and is unique in the compilation;</item>
/// <item>the props type of a view key is a sealed record class whose members are read-only (no setter other than
/// <c>init</c>, no mutable public field, no writable <c>Signal&lt;T&gt;</c>): props freeze at mount, state does not travel
/// in them.</item>
/// </list>
/// No baseline.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class KeyContractAnalyzer : ThemeRuleAnalyzer
{
    public const string DiagnosticId = "WVTH006";

    private static readonly DiagnosticDescriptor Rule = Describe(
        DiagnosticId,
        "Key id or props record shape",
        "{0}",
        "View and style keys are declared under Theming\\Contracts with unique dotted ids, and a view's props are a sealed record with read-only members.");

    protected override DiagnosticDescriptor Descriptor => Rule;

    protected override bool AllowsBaseline => false;

    protected override void OnStart(CompilationStartAnalysisContext start, RuleEnv env)
    {
        var ids = new ConcurrentBag<(string Id, Location Location)>();
        start.RegisterSyntaxNodeAction(context => Analyze(context, env, ids),
            SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
        start.RegisterCompilationEndAction(context => ReportDuplicates(context, ids));
    }

    private static void Analyze(SyntaxNodeAnalysisContext context, RuleEnv env, ConcurrentBag<(string, Location)> ids)
    {
        var creation = (BaseObjectCreationExpressionSyntax)context.Node;
        if (context.SemanticModel.GetTypeInfo(creation, context.CancellationToken).Type is not INamedTypeSymbol type)
            return;
        bool view = Symbols.IsType(type, "FluentGpu.Theming", "ViewKey");
        bool style = Symbols.IsType(type, "FluentGpu.Theming", "StyleKey");
        if (!view && !style)
            return;

        string? relative = env.RelativePath(creation.SyntaxTree);
        Location location = creation.GetLocation();
        string kind = view ? "ViewKey" : "StyleKey";

        if (relative is null || !relative.StartsWith("Theming/Contracts/", StringComparison.OrdinalIgnoreCase))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, location,
                kind + " is created outside Theming\\Contracts; declare it in ViewKeys.cs or StyleKeys.cs"));
            return;
        }

        // The id: the first argument must be a constant string of the dotted form.
        string? id = null;
        if (creation.ArgumentList is { Arguments.Count: > 0 } args)
        {
            Optional<object?> constant = context.SemanticModel.GetConstantValue(args.Arguments[0].Expression, context.CancellationToken);
            id = constant.HasValue ? constant.Value as string : null;
        }
        if (id is null)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, location, kind + " id must be a constant string"));
            return;
        }
        if (!IsValidId(id))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, location,
                kind + " id '" + id + "' must be dotted path segments starting with an upper-case letter (Shell.Frame), optionally ending in @N"));
        }
        ids.Add((kind + ":" + id, location));

        if (view && type.TypeArguments.Length == 1 && type.TypeArguments[0] is INamedTypeSymbol props)
        {
            string? problem = PropsShapeProblem(props);
            if (problem is not null)
                context.ReportDiagnostic(Diagnostic.Create(Rule, location, "props type '" + props.Name + "' of " + id + ": " + problem));
        }
    }

    private static void ReportDuplicates(CompilationAnalysisContext context, ConcurrentBag<(string Id, Location Location)> ids)
    {
        foreach (IGrouping<string, (string Id, Location Location)> group in ids.GroupBy(i => i.Id))
        {
            if (group.Count() < 2)
                continue;
            // Order by file then position so the same one is kept on every run; report every other.
            foreach ((string Id, Location Location) duplicate in group
                         .OrderBy(i => i.Location.SourceTree?.FilePath ?? "", StringComparer.Ordinal)
                         .ThenBy(i => i.Location.SourceSpan.Start)
                         .Skip(1))
                context.ReportDiagnostic(Diagnostic.Create(Rule, duplicate.Location, "key id '" + group.Key.Substring(group.Key.IndexOf(':') + 1) + "' is declared more than once"));
        }
    }

    /// <summary>Segments of letters and digits starting upper-case, separated by dots, at least two segments, with an
    /// optional <c>@N</c> version suffix.</summary>
    internal static bool IsValidId(string id)
    {
        int at = id.LastIndexOf('@');
        string path = id;
        if (at >= 0)
        {
            string version = id.Substring(at + 1);
            if (version.Length == 0 || !version.All(char.IsDigit))
                return false;
            path = id.Substring(0, at);
        }
        string[] segments = path.Split('.');
        if (segments.Length < 2)
            return false;
        foreach (string segment in segments)
        {
            if (segment.Length == 0 || segment[0] < 'A' || segment[0] > 'Z')
                return false;
            foreach (char c in segment)
            {
                if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')))
                    return false;
            }
        }
        return true;
    }

    /// <summary>Why <paramref name="props"/> is not a legal props type, or null.</summary>
    internal static string? PropsShapeProblem(INamedTypeSymbol props)
    {
        if (props.TypeKind != TypeKind.Class || !props.IsRecord)
            return "must be a record class";
        if (!props.IsSealed)
            return "must be sealed";
        foreach (ISymbol member in props.GetMembers())
        {
            if (member.IsStatic || member.DeclaredAccessibility != Accessibility.Public)
                continue;
            if (member is IFieldSymbol { IsReadOnly: false, IsImplicitlyDeclared: false } field)
                return "public field '" + field.Name + "' is mutable";
            if (member is IPropertySymbol property)
            {
                if (property.SetMethod is { IsInitOnly: false } && !property.IsImplicitlyDeclared)
                    return "property '" + property.Name + "' has a setter; use init or get-only";
                if (Symbols.IsType(property.Type, "FluentGpu.Signals", "Signal"))
                    return "property '" + property.Name + "' is a writable Signal; props carry values or read-only signals";
            }
        }
        return null;
    }
}
