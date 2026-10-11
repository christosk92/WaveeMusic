using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Wavee.Analyzers.Tests;

/// <summary>
/// A self-contained Roslyn driver after the engine's FluentGpu.SourceGen.Tests Harness: an in-memory CSharpCompilation of
/// (path, source) pairs with explicit file paths (the rules key on the path), BCL references only, AdditionalFiles as
/// in-memory texts and <c>build_property.ProjectDir</c> through a test options provider. Nothing is read from disk.
/// </summary>
internal static class Harness
{
    /// <summary>A stand-in for the Wavee project folder; sources are given paths under it.</summary>
    public const string ProjectDir = @"C:\repo\src\apps\Wavee\";

    private static readonly ImmutableArray<MetadataReference> BclRefs = BuildRefs();
    private static readonly CSharpParseOptions ParseOpts = new(LanguageVersion.Preview);

    private static ImmutableArray<MetadataReference> BuildRefs()
    {
        var tpa = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var builder = ImmutableArray.CreateBuilder<MetadataReference>();
        foreach (string path in tpa)
        {
            string file = Path.GetFileName(path);
            bool bcl = file.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
                || file.Equals("netstandard.dll", StringComparison.OrdinalIgnoreCase)
                || file.Equals("mscorlib.dll", StringComparison.OrdinalIgnoreCase)
                || file.Equals("Microsoft.CSharp.dll", StringComparison.OrdinalIgnoreCase);
            if (bcl)
                builder.Add(MetadataReference.CreateFromFile(path));
        }
        return builder.ToImmutable();
    }

    /// <summary>Runs <paramref name="analyzer"/> over <paramref name="sources"/> and returns its diagnostics (compilation-end
    /// ones included). Throws when a fixture does not compile, so a typo cannot make a "clean" test pass vacuously.</summary>
    public static ImmutableArray<Diagnostic> Analyze(
        DiagnosticAnalyzer analyzer,
        IEnumerable<(string Path, string Source)> sources,
        IEnumerable<(string Path, string Content)>? additionalFiles = null,
        string? projectDir = ProjectDir)
    {
        var trees = sources
            .Select(s => CSharpSyntaxTree.ParseText(s.Source, ParseOpts, s.Path))
            .ToArray();
        var compilation = CSharpCompilation.Create(
            "WaveeAnalyzerTest_" + Guid.NewGuid().ToString("N"),
            trees,
            BclRefs,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable,
                allowUnsafe: true));

        var compileErrors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (compileErrors.Length > 0)
            throw new InvalidOperationException("Fixture does not compile: " + string.Join(" | ", compileErrors.Select(d => d.ToString())));

        ImmutableArray<AdditionalText> additional = additionalFiles is null
            ? ImmutableArray<AdditionalText>.Empty
            : additionalFiles.Select(t => (AdditionalText)new InMemoryText(t.Path, t.Content)).ToImmutableArray();

        var global = new List<(string Key, string Value)>();
        if (projectDir is not null)
            global.Add(("build_property.ProjectDir", projectDir));
        var options = new AnalyzerOptions(additional, new DictOptionsProvider(global));

        var withAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create(analyzer),
            new CompilationWithAnalyzersOptions(options,
                onAnalyzerException: null, concurrentAnalysis: false, logAnalyzerExecutionTime: false));
        return withAnalyzers.GetAnalyzerDiagnosticsAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public static ImmutableArray<Diagnostic> Analyze(
        DiagnosticAnalyzer analyzer, params (string Path, string Source)[] sources)
        => Analyze(analyzer, sources, additionalFiles: null, projectDir: ProjectDir);

    public static List<Diagnostic> Only(ImmutableArray<Diagnostic> diagnostics, string id)
        => diagnostics.Where(d => d.Id == id).ToList();

    /// <summary>The source text the diagnostic points at, so a test asserts the flagged span and not just the count.</summary>
    public static string SpanText(Diagnostic diagnostic)
        => diagnostic.Location.SourceTree?.GetText().ToString(diagnostic.Location.SourceSpan) ?? "";

    private sealed class InMemoryText : AdditionalText
    {
        private readonly string _content;
        public InMemoryText(string path, string content) { Path = path; _content = content; }
        public override string Path { get; }
        public override SourceText GetText(CancellationToken cancellationToken = default)
            => SourceText.From(_content, System.Text.Encoding.UTF8);
    }

    private sealed class DictOptionsProvider : AnalyzerConfigOptionsProvider
    {
        public DictOptionsProvider(List<(string Key, string Value)> opts) => GlobalOptions = new DictOptions(opts);
        public override AnalyzerConfigOptions GlobalOptions { get; }
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => GlobalOptions;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => GlobalOptions;
    }

    private sealed class DictOptions : AnalyzerConfigOptions
    {
        private readonly Dictionary<string, string> _d = new(StringComparer.OrdinalIgnoreCase);
        public DictOptions(List<(string Key, string Value)> opts)
        {
            foreach (var (k, v) in opts) _d[k] = v;
        }
        public override bool TryGetValue(string key, [NotNullWhen(true)] out string? value) => _d.TryGetValue(key, out value);
    }
}
