using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Wavee.Analyzers.Baselines;

namespace Wavee.Analyzers.Themes;

/// <summary>What one rule needs while it runs over a compilation: its descriptor, its baseline, the Wavee project folder
/// (to turn tree paths into project-relative ones), the AdditionalFiles it reads, and the reporting helper that applies the
/// baseline. One instance per compilation, shared by every action of the rule (thread-safe).</summary>
public sealed class RuleEnv
{
    private readonly ConcurrentDictionary<SyntaxTree, string?> _relative = new();
    private readonly string? _projectDir;
    private readonly AnalyzerOptions _options;

    public RuleEnv(AnalyzerOptions options, DiagnosticDescriptor rule, BaselineTracker baseline)
    {
        Rule = rule;
        Baseline = baseline;
        _options = options;
        if (options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue("build_property.ProjectDir", out string? dir))
            _projectDir = ThemeLayering.NormalizeProjectDir(dir);
    }

    public DiagnosticDescriptor Rule { get; }

    public BaselineTracker Baseline { get; }

    /// <summary>The tree's path relative to the Wavee project, forward slashes, or null when it cannot be placed.</summary>
    public string? RelativePath(SyntaxTree tree)
        => _relative.GetOrAdd(tree, t => ThemeLayering.ToProjectRelative(t.FilePath, _projectDir));

    public string? RelativePath(Location location)
        => location.SourceTree is { } tree ? RelativePath(tree) : null;

    /// <summary>Reports the rule at <paramref name="location"/> unless the baseline exempts <paramref name="detail"/> there.</summary>
    public void Report(Action<Diagnostic> report, Location location, string detail, params object?[] messageArgs)
    {
        if (Baseline.IsExempt(location, detail))
            return;
        report(Diagnostic.Create(Rule, location, messageArgs));
    }

    /// <summary>The lines of the AdditionalFiles item <c>analyzer-input/&lt;name&gt;</c> (comments and blank lines dropped,
    /// trimmed); empty when the file is absent.</summary>
    public ImmutableArray<string> InputLines(string name, CancellationToken cancellationToken)
    {
        var lines = ImmutableArray.CreateBuilder<string>();
        foreach (AdditionalText file in _options.AdditionalFiles)
        {
            string norm = BaselinePaths.Normalize(file.Path);
            if (!string.Equals(BaselinePaths.FileName(norm), name, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(BaselinePaths.ParentSegment(norm), "analyzer-input", StringComparison.OrdinalIgnoreCase))
                continue;
            var text = file.GetText(cancellationToken);
            if (text is null)
                continue;
            foreach (var line in text.Lines)
            {
                string trimmed = line.ToString().Trim();
                if (trimmed.Length == 0 || trimmed[0] == '#')
                    continue;
                lines.Add(trimmed);
            }
        }
        return lines.ToImmutable();
    }
}

/// <summary>The common shape of the WVTH rules: one descriptor, a baseline (or none), a compilation-start hook that gets a
/// <see cref="RuleEnv"/>. Keeps each rule down to its own detection logic.</summary>
public abstract class ThemeRuleAnalyzer : DiagnosticAnalyzer
{
    private ImmutableArray<DiagnosticDescriptor> _supported;

    /// <summary>The rule's descriptor.</summary>
    protected abstract DiagnosticDescriptor Descriptor { get; }

    /// <summary>False for a rule that is a hard gate: an <c>analyzer-baseline\WVTHnnn.txt</c> then reports WVTH901.</summary>
    protected virtual bool AllowsBaseline => true;

    public sealed override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
    {
        get
        {
            if (_supported.IsDefault)
                _supported = ImmutableArray.Create(Descriptor, BaselineDiagnostics.StaleEntry, BaselineDiagnostics.DisallowedBaseline);
            return _supported;
        }
    }

    // RS1013 cannot see through OnStart, where each rule registers its actions on the start context.
#pragma warning disable RS1013
    public sealed override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(start =>
        {
            BaselineTracker tracker = AnalyzerBaseline.Register(start, Descriptor.Id, AllowsBaseline);
            OnStart(start, new RuleEnv(start.Options, Descriptor, tracker));
        });
    }
#pragma warning restore RS1013

    /// <summary>Registers the rule's actions on the compilation-start context.</summary>
    protected abstract void OnStart(CompilationStartAnalysisContext start, RuleEnv env);

    protected static DiagnosticDescriptor Describe(string id, string title, string messageFormat, string description)
        => new(id, title, messageFormat, "Wavee.Themes", DiagnosticSeverity.Error, isEnabledByDefault: true,
               description: description,
               helpLinkUri: "https://github.com/christosk92/WaveeMusic/blob/main/docs/themes/plan/technical-plan.md");
}

/// <summary>Name-based symbol tests. The analyzers never reference the Wavee or FluentGpu assemblies (they run inside the
/// compiler), so every type is recognised by namespace and name.</summary>
internal static class Symbols
{
    public static string Namespace(INamespaceSymbol? ns)
        => ns is null || ns.IsGlobalNamespace ? "" : ns.ToDisplayString();

    public static bool IsType(ITypeSymbol? type, string ns, string name)
        => type is INamedTypeSymbol named && named.Name == name && Namespace(named.ContainingNamespace) == ns;

    /// <summary>True when <paramref name="type"/> is, or derives from, the named class.</summary>
    public static bool DerivesFrom(ITypeSymbol? type, string ns, string name)
    {
        for (ITypeSymbol? t = type; t is not null; t = t.BaseType)
        {
            if (IsType(t, ns, name))
                return true;
        }
        return false;
    }

    /// <summary>True when <paramref name="type"/> is, or implements, the named interface.</summary>
    public static bool Implements(ITypeSymbol? type, string ns, string name)
    {
        if (IsType(type, ns, name))
            return true;
        if (type is null)
            return false;
        foreach (INamedTypeSymbol i in type.AllInterfaces)
        {
            if (IsType(i, ns, name))
                return true;
        }
        return false;
    }

    public static INamedTypeSymbol? Outermost(INamedTypeSymbol? type)
    {
        while (type?.ContainingType is { } outer)
            type = outer;
        return type;
    }

    /// <summary>The type that declares <paramref name="symbol"/>, or the symbol itself when it is a type.</summary>
    public static INamedTypeSymbol? OwnerType(ISymbol symbol)
        => symbol as INamedTypeSymbol ?? symbol.ContainingType;

    /// <summary>The path of the first source tree that declares <paramref name="symbol"/>, or null for a metadata symbol.</summary>
    public static SyntaxTree? DeclaringTree(ISymbol symbol)
    {
        ImmutableArray<SyntaxReference> references = symbol.DeclaringSyntaxReferences;
        return references.IsDefaultOrEmpty ? null : references[0].SyntaxTree;
    }
}
