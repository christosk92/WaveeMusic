using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Wavee.Analyzers.Baselines;

namespace Wavee.Analyzers.Themes;

/// <summary>
/// WVTH001 — <c>Wavee.Themes.*</c> is referenced only along the allowed edges (themes technical plan, section 2.4):
/// <c>App.cs</c> and <c>Theming\Composition\ThemeCatalog.cs</c> may reference anything; inside <c>Themes\</c> a composition
/// file <c>Themes\X\XTheme.cs</c> may reference every theme and Common, a structure <c>Themes\X\Structure\**</c> its own theme
/// and Common, any other file of theme X only X, and <c>Themes\Common\**</c> only Common. Everything else is an error at the
/// reference site. The referenced theme is the namespace (<c>Wavee.Themes.&lt;layer&gt;</c>), the referencing file's place is
/// its path under the Wavee project (<c>build_property.ProjectDir</c>, else a path-suffix match). A bare
/// <c>using Wavee.Themes.X;</c> references no symbol and is not flagged; a using alias or <c>using static</c> naming a type is.
/// WVTH001 allows no baseline: a <c>analyzer-baseline\WVTH001.txt</c> is reported as WVTH901.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ThemeReferenceAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "WVTH001";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Wavee.Themes type referenced from outside its allowed layers",
        messageFormat: "'{0}' ({1}) may not be referenced from {2}",
        category: "Wavee.Themes",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Wavee.Themes.* types are reachable only from App.cs, Theming\\Composition\\ThemeCatalog.cs and along the "
                   + "theme layering edges: a composition file may reference every theme and Common, a structure its own theme "
                   + "and Common, other theme files only their own theme, and Common only Common.",
        helpLinkUri: "https://github.com/christosk92/WaveeMusic/blob/main/docs/themes/plan/technical-plan.md");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Rule, BaselineDiagnostics.StaleEntry, BaselineDiagnostics.DisallowedBaseline);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        // No baseline for this rule: a baseline file is itself a finding (WVTH901).
        AnalyzerBaseline.Register(context, DiagnosticId, allowsBaseline: false);

        // Wavee.Themes does not exist in most compilations (and not yet in the app): register nothing, cost nothing.
        if (!HasThemesNamespace(context.Compilation))
            return;

        string? projectDir = null;
        if (context.Options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue("build_property.ProjectDir", out string? dir))
            projectDir = ThemeLayering.NormalizeProjectDir(dir);

        var state = new State(projectDir);
        context.RegisterSyntaxNodeAction(state.Analyze, SyntaxKind.IdentifierName, SyntaxKind.GenericName);
    }

    private static bool HasThemesNamespace(Compilation compilation)
    {
        foreach (INamespaceSymbol wavee in compilation.GlobalNamespace.GetNamespaceMembers())
        {
            if (wavee.Name != "Wavee")
                continue;
            foreach (INamespaceSymbol child in wavee.GetNamespaceMembers())
            {
                if (child.Name == "Themes")
                    return true;
            }
        }
        return false;
    }

    private sealed class State
    {
        private readonly string? _projectDir;
        private readonly ConcurrentDictionary<SyntaxTree, FileScope> _scopes = new();

        public State(string? projectDir) => _projectDir = projectDir;

        private FileScope GetScope(SyntaxTree tree)
        {
            if (_scopes.TryGetValue(tree, out FileScope cached))
                return cached;
            FileScope scope = ThemeLayering.Classify(ThemeLayering.ToProjectRelative(tree.FilePath, _projectDir));
            return _scopes.GetOrAdd(tree, scope);
        }

        public void Analyze(SyntaxNodeAnalysisContext context)
        {
            var name = (SimpleNameSyntax)context.Node;
            FileScope scope = GetScope(name.SyntaxTree);
            if (scope.Kind == ScopeKind.Unrestricted)
                return;

            // 'var' resolves to the initializer's type; the type is reported where it is actually written.
            if (name is IdentifierNameSyntax && name.Identifier.ValueText == "var")
                return;
            // The alias name in 'using X = ...;' declares a name; its target is flagged on its own identifier.
            if (name.Parent is NameEqualsSyntax { Parent: UsingDirectiveSyntax })
                return;

            SemanticModel model = context.SemanticModel;
            SymbolInfo info = model.GetSymbolInfo(name, context.CancellationToken);
            ISymbol? symbol = info.Symbol;
            if (symbol is null && !info.CandidateSymbols.IsDefaultOrEmpty)
                symbol = info.CandidateSymbols[0];
            if (symbol is IAliasSymbol alias)
                symbol = alias.Target;
            if (symbol is null)
                return;

            INamespaceSymbol? ns;
            switch (symbol.Kind)
            {
                case SymbolKind.NamedType:
                    ns = symbol.ContainingNamespace;
                    break;
                case SymbolKind.Method:
                case SymbolKind.Property:
                case SymbolKind.Field:
                case SymbolKind.Event:
                    // 'Zune.Panel.Size': the type name is already reported; only an instance member reached without
                    // naming the type (a var local, a returned value) is reported at the member.
                    if (IsMemberOfNamedType(name, model, context.CancellationToken))
                        return;
                    ns = symbol.ContainingType?.ContainingNamespace;
                    break;
                default:
                    return;
            }

            if (!ThemeLayering.TryGetLayer(ns, out string? layer))
                return;
            if (ThemeLayering.IsAllowed(scope, layer))
                return;

            string target = layer is null ? "Wavee.Themes" : "Wavee.Themes." + layer;
            context.ReportDiagnostic(Diagnostic.Create(Rule, name.GetLocation(), symbol.Name, target, scope.Describe()));
        }

        private static bool IsMemberOfNamedType(SimpleNameSyntax name, SemanticModel model, System.Threading.CancellationToken ct)
        {
            if (name.Parent is not MemberAccessExpressionSyntax access || !ReferenceEquals(access.Name, name))
                return false;
            ISymbol? receiver = model.GetSymbolInfo(access.Expression, ct).Symbol;
            return receiver is INamedTypeSymbol || receiver is IAliasSymbol { Target: INamedTypeSymbol };
        }
    }
}
