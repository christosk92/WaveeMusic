using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Wavee.Analyzers.Tests;

/// <summary>The stand-ins for the engine and Wavee types the WVTH rules recognise by namespace and name, plus the runner every
/// rule test uses. The harness compiles against the BCL only, so every type a fixture mentions is declared here; the rules
/// never see the real assemblies either (an analyzer runs inside the compiler), so name-based recognition is exactly what
/// production does.</summary>
internal static class Fixtures
{
    private const string Root = Harness.ProjectDir;

    private const string Engine = """
        namespace FluentGpu.Foundation
        {
            public readonly struct ColorF
            {
                public ColorF(float r, float g, float b, float a) { }
                public static ColorF FromRgba(byte r, byte g, byte b, byte a = 255) => default;
                public static ColorF FromHsv(float h, float s, float v) => default;
            }
        }
        namespace FluentGpu.Signals
        {
            public interface IReadSignal<out T> { T Value { get; } }
            public sealed class Signal<T> : IReadSignal<T> { public T Value { get; set; } = default!; }
        }
        namespace FluentGpu.Dsl
        {
            public abstract class Element { }
            public class BoxEl : Element { public float Width; public float Height; public float Opacity; public float Grow; public float Padding; }
            public class TextEl : Element { }
            public static class Tok { public static int Epoch; }
        }
        namespace FluentGpu.Theming
        {
            public readonly record struct OptionKey<T>(string Id, T Default);
            public readonly record struct ViewKey<TProps>(string Id) where TProps : class;
            public readonly record struct StyleKey<TStyle>(string Id) where TStyle : class;
            public interface IOptionReader { T Get<T>(OptionKey<T> key); }
            public interface IOptionRequests { void Set<T>(OptionKey<T> key, T value); }
            public sealed class OptionStore : IOptionReader
            {
                public T Get<T>(OptionKey<T> key) => key.Default;
                public T Peek<T>(OptionKey<T> key) => key.Default;
                public bool Publish<T>(OptionKey<T> key, T value, bool persist = true) => true;
            }
            public sealed class ThemeDefinition { public string Id { get; init; } = ""; }
        }
        """;

    private const string App = """
        namespace Wavee
        {
            public readonly record struct SettingKey<T>(string Name, T Default);
            public interface IAppSettings
            {
                T Get<T>(SettingKey<T> key);
                void Set<T>(SettingKey<T> key, T value);
                bool Has(string name);
                void Remove(string name);
            }
            public static class Platform { public static IAppSettings Settings => null!; }
            public static class Entities { public static int Count; }
            public static class Playback { public static int Position; }
            public static class Spotify { public static int Session; }
            public static class Store { public static int Rows; }
            public static class Shell { public static int Frame; }
            public static class Prefs { public static int Density; }
            public static class Design { public static float Gutter; }
        }
        namespace Wavee.Theming
        {
            public enum OptionVisibility : byte { Always, Developer, Hidden }
            public abstract record ThemeOption(string Id, OptionVisibility Visibility = OptionVisibility.Always);
            public sealed record ToggleOption(string Id, bool Default) : ThemeOption(Id)
            {
                public FluentGpu.Theming.OptionKey<bool> Key => new(Id, Default);
            }
            public sealed class OptionValues { public T Get<T>(FluentGpu.Theming.OptionKey<T> key) => key.Default; }
            public sealed record ThemeManifest(string Id, string StructureId, string StyleId);
            public interface IStructureLayer { string Id { get; } }
            public interface IStyleLayer { string Id { get; } }
            public sealed record ThemeComposition(ThemeManifest Manifest, IStructureLayer Structure, IStyleLayer Style);
            public static class ThemeHost
            {
                public static FluentGpu.Signals.IReadSignal<ThemeComposition> Current { get; } = null!;
                public static FluentGpu.Signals.IReadSignal<int> Facts { get; } = null!;
            }
            public static class ThemeCatalog { public const string FallbackId = "classic"; }
        }
        namespace Wavee.Theming.Roles
        {
            public static class R { public static float SectionGap; }
        }
        namespace Wavee.Theming.Contracts
        {
            [System.AttributeUsage(System.AttributeTargets.Class)]
            public sealed class CoreHostAttribute : System.Attribute { }
        }
        """;

    private static readonly (string Path, string Source)[] Stubs =
    {
        (Root + @"Stubs\Engine.cs", Engine),
        (Root + @"Stubs\App.cs", App),
    };

    /// <summary>Runs <paramref name="analyzer"/> over the stubs plus <paramref name="files"/>.</summary>
    public static ImmutableArray<Diagnostic> Run(DiagnosticAnalyzer analyzer, (string Path, string Source)[] files,
        (string Path, string Content)[]? additional = null)
        => Harness.Analyze(analyzer, Stubs.Concat(files), additional, Harness.ProjectDir);

    /// <summary>The spans of the diagnostics with <paramref name="id"/>, sorted for stable comparison.</summary>
    public static string[] Spans(ImmutableArray<Diagnostic> diagnostics, string id)
        => Harness.Only(diagnostics, id).Select(Harness.SpanText).OrderBy(s => s, StringComparer.Ordinal).ToArray();

    /// <summary>The messages of the diagnostics with <paramref name="id"/>, sorted.</summary>
    public static string[] Messages(ImmutableArray<Diagnostic> diagnostics, string id)
        => Harness.Only(diagnostics, id).Select(d => d.GetMessage()).OrderBy(s => s, StringComparer.Ordinal).ToArray();

    public static (string Path, string Source) Src(string relativePath, string source)
        => (Root + relativePath.Replace('/', '\\'), source);

    public static (string Path, string Content) Baseline(string rule, string content)
        => (Root + @"analyzer-baseline\" + rule + ".txt", content);

    public static (string Path, string Content) Input(string name, string content)
        => (Root + @"analyzer-input\" + name, content);
}
