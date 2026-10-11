// ── Wavee.ShellTests/ViewHarness.cs — mount ONE view key on the headless shell host (themes plan 2.4, T1) ───────────────
//
// A thin layer on `Shell.Headless`: it mounts a view key with fixture props under a theme composition, option values and a
// window size, on the engine's headless AppHost, and exposes the scene and the node-key lookup for assertions. It keeps no
// baselines and captures no images (owner Q23: no pixel identity); headless text has fixed advances, so assertions are about
// structure and geometry rules, never typography.
//
// THREE ways to use it.
//   · Under the INSTALLED theme (no composition): `Slots.View(key, props)` inside the providers the shell itself gives the
//     frame (view registry, option store, theme facts of `Themes`). What a P3 view test wants.
//   · Under ANY composition: the harness builds that composition's registry, option store (over a private in-memory settings
//     store, never the profile) and facts, provides them around the view, and applies the composition's definition to the
//     process tokens for the length of the mount (put back on dispose). What the cross-product probe (S3) wants.
//   · The real frame: `MountFrame` is `Shell.Headless.Mount` with the options' window size, for a probe that needs the
//     whole shell.
//
// UI THREAD. Every member runs on the thread that booted the host: use `ShellHost.Run` (or `With`, which wraps it and ends
// with the same network guard `ShellHost.Mounted` ends with). One mount at a time, like the shell mount underneath.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using WaveeShell = Wavee.Shell;

namespace Wavee.ShellTests;

/// <summary>How a view is mounted: under which composition, with which option values, at which window size.</summary>
public sealed record ViewHarnessOptions
{
    /// <summary>The composition to render under; null renders under the installed theme.</summary>
    public ThemeComposition? Composition { get; init; }

    /// <summary>Option values by layer-qualified id (<c>style.accent</c>), applied to <see cref="Composition"/>'s options.</summary>
    public IReadOnlyDictionary<string, object>? Options { get; init; }

    /// <summary>The window width in DIP.</summary>
    public float WidthDip { get; init; } = 1180f;

    /// <summary>The window height in DIP.</summary>
    public float HeightDip { get; init; } = 760f;

    /// <summary>The window DPI scale (the pixel size is the DIP size times this).</summary>
    public float Scale { get; init; } = 1f;

    /// <summary>Light or dark for the mount; null keeps the host's kind.</summary>
    public ThemeKind? Kind { get; init; }
}

/// <summary>One view mounted on the headless shell host.</summary>
public sealed class ViewHarness : IDisposable
{
    private readonly ThemeDefinition _restoreDefinition;
    private readonly ThemeKind _restoreKind;
    private bool _disposed;

    private ViewHarness(WaveeShell.HeadlessShell shell, ThemeComposition? composition, ThemeOptionStore? options,
        ThemeOptionValues values, IReadSignal<ThemeFacts> facts, ThemeDefinition restoreDefinition, ThemeKind restoreKind)
    {
        Shell = shell;
        Composition = composition;
        Store = options;
        Values = values;
        Facts = facts;
        _restoreDefinition = restoreDefinition;
        _restoreKind = restoreKind;
    }

    /// <summary>The mounted headless shell: <c>Host.Scene</c>, <c>Host.Animation</c>, <c>RunFrames</c>, <c>Window</c>.</summary>
    public WaveeShell.HeadlessShell Shell { get; }

    /// <summary>The composition the view renders under; null when it renders under the installed theme.</summary>
    public ThemeComposition? Composition { get; }

    /// <summary>The option cells the view reads; null under the installed theme (use <c>Themes.Options</c>).</summary>
    public ThemeOptionStore? Store { get; }

    /// <summary>The option values the composition resolved (empty under the installed theme).</summary>
    public ThemeOptionValues Values { get; }

    /// <summary>The facts the view reads through <c>ThemeFactsContext</c>.</summary>
    public IReadSignal<ThemeFacts> Facts { get; }

    // ── mounting ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Mounts <paramref name="key"/> with <paramref name="props"/> and runs the first frames.</summary>
    public static ViewHarness Mount<TProps>(ViewKey<TProps> key, TProps props, ViewHarnessOptions? options = null) where TProps : class
    {
        ArgumentNullException.ThrowIfNull(props);
        options ??= new ViewHarnessOptions();
        ThemeDefinition restoreDefinition = Tok.Definition;
        ThemeKind restoreKind = Tok.Theme;

        ThemeComposition? composition = options.Composition;
        ThemeOptionStore? store = null;
        ThemeOptionValues values = ThemeOptionValues.Empty;
        IReadSignal<ThemeFacts> facts = ThemeHost.Facts;
        Func<Element> content;

        if (composition is null)
        {
            content = () => Slots.View(key, props);
        }
        else
        {
            store = new ThemeOptionStore(composition, new InMemorySettings());
            values = ApplyOptions(composition, store, options.Options);
            var ownFacts = new Signal<ThemeFacts>(composition.Facts(values));
            facts = ownFacts;
            TokenState.UseDefinition(composition.Definition(values, ThemeHost.OsAccent), options.Kind ?? restoreKind);
            ViewRegistry registry = composition.Registry;
            OptionStore cells = store.Store;
            content = () => Ctx.Provide(ViewRegistry.Context, registry,
                Ctx.Provide(OptionStore.Context, cells,
                    Ctx.Provide(ThemeFactsContext.Signal, ownFacts, Slots.View(key, props))));
        }
        if (composition is null && options.Kind is { } kind && kind != restoreKind)
            TokenState.UseDefinition(restoreDefinition, kind);

        WaveeShell.HeadlessShell shell;
        try
        {
            shell = WaveeShell.Headless.Mount(content, options.WidthDip, options.HeightDip, options.Scale);
        }
        catch
        {
            TokenState.Restore(restoreDefinition, restoreKind);
            throw;
        }
        var harness = new ViewHarness(shell, composition, store, values, facts, restoreDefinition, restoreKind);
        shell.RunFrames(2);
        return harness;
    }

    /// <summary>Mounts the real frame at the options' window size (the composition and options are not used: the frame is the
    /// installed theme's).</summary>
    public static WaveeShell.HeadlessShell MountFrame(ViewHarnessOptions? options = null)
    {
        options ??= new ViewHarnessOptions();
        return WaveeShell.Headless.Mount(options.WidthDip, options.HeightDip, options.Scale);
    }

    /// <summary>Mounts the view, runs <paramref name="body"/> against it and disposes it, on the UI thread, and ends with the
    /// network guard (one request that left the process fails the fact), like <see cref="ShellHost.Mounted"/>.</summary>
    public static void With<TProps>(ShellHost host, ViewKey<TProps> key, TProps props, Action<ViewHarness> body,
        ViewHarnessOptions? options = null) where TProps : class
        => host.Run(() =>
        {
            using (var harness = Mount(key, props, options)) body(harness);
            ShellHost.AssertNoNetwork();
        });

    private static ThemeOptionValues ApplyOptions(ThemeComposition composition, ThemeOptionStore store,
        IReadOnlyDictionary<string, object>? requested)
    {
        if (requested is not null)
        {
            foreach (var (qualifiedId, value) in requested)
            {
                LayerOption option = composition.FindQualified(qualifiedId)
                    ?? throw new ArgumentException($"ViewHarness: theme '{composition.Manifest.Id}' has no option '{qualifiedId}'.");
                if (!composition.IsUserOption(option))
                    throw new ArgumentException($"ViewHarness: option '{qualifiedId}' cannot be set (a structure parameter, hidden or replaced option).");
                store.Publish(option, value);
                object? applied = store.Snapshot().Of(option.Layer).GetBoxed(option.Option.Id);
                if (!SameValue(applied, value))
                    throw new ArgumentException($"ViewHarness: '{value}' is not a valid value of option '{qualifiedId}'.");
            }
        }
        return store.Snapshot();
    }

    /// <summary>Equal as option values: a number given as an int or double equals the float the option holds.</summary>
    private static bool SameValue(object? applied, object requested)
        => Equals(applied, requested)
           || (applied is float f && requested is int or double && f == Convert.ToSingle(requested, System.Globalization.CultureInfo.InvariantCulture));

    // ── looking at the scene ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The first live node mounted with exactly <paramref name="key"/>, or <see cref="NodeHandle.Null"/>.</summary>
    public NodeHandle Find(string key) => Shell.Find(key);

    /// <summary>The node keyed <paramref name="key"/>, failing when it is not mounted.</summary>
    public NodeHandle Node(string key) => ShellProbe.Node(Shell, key);

    /// <summary>True when a node with exactly <paramref name="key"/> is mounted.</summary>
    public bool Contains(string key) => !Find(key).IsNull;

    /// <summary>Every live node whose key starts with <paramref name="prefix"/>.</summary>
    public List<(NodeHandle Node, string Key)> FindPrefix(string prefix) => Shell.FindPrefix(prefix);

    /// <summary>The node and every live descendant.</summary>
    public IEnumerable<NodeHandle> Subtree(NodeHandle root) => ShellProbe.Subtree(Shell.Host.Scene, root);

    /// <summary>The node's laid-out rectangle in its parent's coordinates (DIP).</summary>
    public RectF LocalBounds(NodeHandle node) => Shell.Host.Scene.Bounds(node);

    /// <summary>The node's rectangle in window coordinates (DIP).</summary>
    public RectF AbsoluteBounds(NodeHandle node) => Shell.Host.Scene.PresentedAbsoluteRect(node);

    /// <summary>Runs <paramref name="count"/> frames.</summary>
    public void RunFrames(int count) => Shell.RunFrames(count);

    /// <summary>Resizes the window; the next frame lays out in the new viewport.</summary>
    public void Resize(float widthDip, float heightDip) => Shell.Resize(widthDip, heightDip);

    /// <summary>Writes an option of the composition the view renders under and runs frames until it settles (the views that
    /// read the cell re-render). The facts follow.</summary>
    public void SetOption(string qualifiedId, object value, int frames = 3)
    {
        if (Composition is null || Store is null)
            throw new InvalidOperationException("ViewHarness.SetOption needs a harness mounted under a composition; under the installed theme use ThemeHost.Apply.");
        LayerOption option = Composition.FindQualified(qualifiedId)
            ?? throw new ArgumentException($"ViewHarness: theme '{Composition.Manifest.Id}' has no option '{qualifiedId}'.");
        if (!Store.Publish(option, value))
            throw new ArgumentException($"ViewHarness: '{value}' is not a new valid value of option '{qualifiedId}'.");
        ((Signal<ThemeFacts>)Facts).Value = Composition.Facts(Store.Snapshot());
        Shell.RunFrames(frames);
    }

    /// <summary>Ends the mount and puts the process tokens back.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Shell.Dispose(); }
        finally { TokenState.Restore(_restoreDefinition, _restoreKind); }
    }

    /// <summary>The process-wide token state a mount may change, and putting it back.</summary>
    private static class TokenState
    {
        public static void UseDefinition(ThemeDefinition definition, ThemeKind kind)
        {
            if (!ReferenceEquals(definition, Tok.Definition) || kind != Tok.Theme) Tok.Use(definition, kind);
        }

        public static void Restore(ThemeDefinition definition, ThemeKind kind) => UseDefinition(definition, kind);
    }

    /// <summary>A private settings store: a harness composition's options never touch the profile the host booted.</summary>
    private sealed class InMemorySettings : IAppSettings
    {
        private readonly Dictionary<string, object> _values = new(StringComparer.Ordinal);

        public T Get<T>(SettingKey<T> key) => _values.TryGetValue(key.Name, out var value) && value is T typed ? typed : key.Default;
        public void Set<T>(SettingKey<T> key, T value) { if (value is not null) _values[key.Name] = value; }

        public bool TryGet<T>(SettingKey<T> key, out T value)
        {
            if (!_values.ContainsKey(key.Name)) { value = key.Default; return false; }
            value = Get(key);
            return true;
        }

        public bool Has(string name) => _values.ContainsKey(name);
        public void Remove(string name) => _values.Remove(name);
    }
}
