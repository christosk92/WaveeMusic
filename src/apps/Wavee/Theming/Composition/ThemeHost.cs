// ── Theming/Composition/ThemeHost.cs — the active theme: install, current, facts, switch, apply ───────────────────────────
//
// The process-wide state of theming (UI thread only, like every signal in the app):
//   · Install     called once by App.cs with every composition; resolves the stored `theme.id` and activates it.
//   · Current     the active composition (a signal; only code allowed to read identity may use it, WVTH004).
//   · Facts       what core needs from it: policies, capabilities, metrics, tiers. Core models read this signal; templates
//                 read the same signal through `ThemeFactsContext`.
//   · Switch      another composition: persists `theme.id`, activates it, applies its definition and REMOUNTS the root.
//   · Apply       one option: a Live option is written to its cell (the views that read it re-render) and, when it moves
//                 the definition, applied live; a Transaction option takes the Switch path (remount).
//
// A CUT TRANSACTION until the engine's theme transaction (A4.2, plan 4.2) exists: Switch and a Transaction-mode Apply run
// persist -> activate -> apply the definition -> bump the root epoch, in one UI turn, with no freeze, no snapshot and no
// dissolve. The root remounts because `ThemeRoot` renders a different component type for the new epoch (the engine
// replaces a component whose type differs), which disposes every page below it: nothing is carried (owner Q26).
//
// NAME. The class is `Wavee.Theming.ThemeHost` (plan 5.3) and each theme lives in a namespace `Wavee.Themes.<Name>` (plan
// 1.2). Inside `namespace Wavee` the simple name `Themes` therefore binds to the NAMESPACE: callers there (App.cs) write the
// class as `Wavee.Theming.ThemeHost`, and tests alias it (`using ThemeHost = Wavee.Theming.ThemeHost;`).

using FluentGpu.Dsl;
using FluentGpu.Signals;
using FluentGpu.Theming;

namespace Wavee.Theming;

/// <summary>One option change request: which theme, which option (layer-qualified, <c>style.accent</c>) and the new value.
/// <see cref="ThemeHost.Apply"/> decides the path from the option's <see cref="ApplyMode"/>.</summary>
public sealed record OptionChange(string ThemeId, string LayerQualifiedId, object Value);

/// <summary>The active theme.</summary>
public static class ThemeHost
{
    private static readonly SettingKey<string> IdKey = new("theme.id", ThemeCatalog.FallbackId);

    private static readonly Signal<ThemeComposition> s_current = new(null!);
    private static readonly Signal<ThemeFacts> s_facts = new(ThemeFacts.Default);
    private static readonly Signal<ThemePolicies> s_policies = new(ThemePolicies.Default);
    private static readonly Signal<long> s_rootEpoch = new(0L);
    private static readonly OptionStore s_noOptions = new();

    private static ThemeOptionStore? s_store;
    private static ThemeOptionValues s_values = ThemeOptionValues.Empty;
    private static ViewRegistry s_registry = ViewRegistry.Empty;
    private static bool s_installed;

    /// <summary>The OS accent the style resolves its definition with. Defaults to the WinUI ramp (which every definition
    /// treats as "use the baked accent tokens"); the appearance seed sets the real one.</summary>
    public static AccentRamp OsAccent { get; set; } = AccentRamp.FluentDefault;

    /// <summary>True once <see cref="Install"/> ran.</summary>
    public static bool IsInstalled => s_installed;

    /// <summary>The active composition. Throws before <see cref="Install"/>.</summary>
    public static IReadSignal<ThemeComposition> Current
    {
        get
        {
            RequireInstalled();
            return s_current;
        }
    }

    /// <summary>The facts of the active composition; core models read this.</summary>
    public static IReadSignal<ThemeFacts> Facts => s_facts;

    /// <summary>The policies of the active composition (<see cref="ThemeFacts.Policies"/>) on a signal of their own, so a core
    /// state machine that reads only its policy is not re-run when another fact (the window tier) moves. Always a bundle the
    /// shell can run on: <see cref="ThemePolicies.Default"/> before <see cref="Install"/> and in place of an invalid one.</summary>
    public static IReadSignal<ThemePolicies> Policies => s_policies;

    /// <summary>The option values of the active composition, as the layers' functions see them.</summary>
    public static ThemeOptionValues Values => s_values;

    /// <summary>The definition of the active composition for its option values and the OS accent, or null before
    /// <see cref="Install"/>. This is what the appearance seed installs (<c>Design.SeedPalette</c>), so a light/dark flip keeps
    /// the active theme's roles and not the base definition's. The composition memoizes it: the same inputs return the same instance.</summary>
    public static ThemeDefinition? ActiveDefinition => s_installed ? s_current.Peek().Definition(s_values, OsAccent) : null;

    /// <summary>Bumped by every switch (and every Transaction-mode option change): <c>ThemeRoot</c> remounts the root on it.</summary>
    public static IReadSignal<long> RootEpoch => s_rootEpoch;

    /// <summary>The view registry of the active composition (the empty registry before <see cref="Install"/>): the value
    /// <c>ThemeRoot</c> provides as <see cref="ViewRegistry.Context"/>.</summary>
    public static ViewRegistry Registry => s_registry;

    /// <summary>The option cells of the active composition (an empty store before <see cref="Install"/>): the value
    /// <c>ThemeRoot</c> provides as <see cref="OptionStore.Context"/>. A view reads it through <c>ctx.Options</c>; its
    /// <c>RequestForwarder</c> is what <c>ctx.OptionRequests</c> sets through, and lands in <see cref="Apply"/>.</summary>
    public static OptionStore Options => s_store?.Store ?? s_noOptions;

    /// <summary>Installs the compositions and activates the stored one (<c>theme.id</c>; an unknown or unavailable id
    /// activates Classic). App.cs only. A second call replaces the installed set, which tests use; production calls once.</summary>
    public static void Install(params ThemeComposition[] themes)
    {
        ArgumentNullException.ThrowIfNull(themes);
        if (themes.Length == 0) throw new ArgumentException("ThemeHost.Install needs at least one composition.", nameof(themes));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in themes)
        {
            ArgumentNullException.ThrowIfNull(t);
            var problems = t.Validate();
            if (problems.Count > 0)
                throw new ArgumentException("ThemeHost.Install: " + string.Join("; ", problems), nameof(themes));
            if (!seen.Add(t.Manifest.Id))
                throw new ArgumentException($"ThemeHost.Install: theme id '{t.Manifest.Id}' is installed twice.", nameof(themes));
        }
        foreach (var t in themes) _ = t.Registry;   // built once, here: a structure that throws in Register fails the boot, not a frame

        ThemeCatalog.Set((ThemeComposition[])themes.Clone());
        s_installed = true;
        // The stored id is NOT rewritten when it falls back: a Developer-only theme resumes when Developer mode is on again.
        Activate(ThemeCatalog.Resolve(Platform.Settings.Get(IdKey)), Platform.Settings);
    }

    /// <summary>Publishes the window's tier facts into <see cref="Facts"/>. The frame root (<c>WindowTiers</c>) calls it on mount
    /// and at every tier crossing; the view harness calls it for a view mounted without a frame. The tiers are the window's
    /// fact, not the composition's: a switch or an option change republishes the policies and keeps these until the new root
    /// resolves its own.</summary>
    public static void PublishTiers(in TierFacts tiers) => s_facts.SetIfChanged(s_facts.Peek() with { Tiers = tiers });

    /// <summary>Back to the not-installed state (tests).</summary>
    public static void Reset()
    {
        ThemeCatalog.Set([]);
        s_installed = false;
        s_store = null;
        s_values = ThemeOptionValues.Empty;
        s_registry = ViewRegistry.Empty;
        s_current.Value = null!;
        s_facts.Value = ThemeFacts.Default;
        s_policies.Value = ThemePolicies.Default;
        s_rootEpoch.Value = 0L;
        ThemePickerFacts.Publish(PickerState.Empty);
        ThemeState.Bind(ThemeCatalog.FallbackId);
    }

    /// <summary>Activates the composition with <paramref name="id"/> (unknown or unavailable ids resolve to Classic): persists
    /// <c>theme.id</c>, publishes its facts and options, applies its definition and remounts the root. A cut until A4.2.</summary>
    public static Task Switch(string id)
    {
        RequireInstalled();
        ThemeComposition target = ThemeCatalog.Resolve(id);
        if (ReferenceEquals(target, s_current.Peek())) return Task.CompletedTask;

        Platform.Settings.Set(IdKey, target.Manifest.Id);
        Activate(target, Platform.Settings);
        ApplyDefinition(target);
        Remount();
        return Task.CompletedTask;
    }

    /// <summary>Applies one option change. The value is validated against the option (an invalid value, an unknown option or
    /// a structure parameter throws <see cref="ArgumentException"/>, a programming error). A Live option is written to its cell and the definition
    /// is re-applied live; a Transaction option also remounts the root. A change to a theme that is not active is persisted
    /// and nothing else happens.</summary>
    public static Task Apply(OptionChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        RequireInstalled();
        ThemeComposition active = s_current.Peek();

        if (!string.Equals(change.ThemeId, active.Manifest.Id, StringComparison.OrdinalIgnoreCase))
        {
            ThemeComposition other = ThemeCatalog.Find(change.ThemeId)
                ?? throw new ArgumentException($"ThemeHost.Apply: no theme '{change.ThemeId}' is installed.", nameof(change));
            (LayerOption otherOption, object otherValue) = Resolve(other, change);
            otherOption.Option.Write(Platform.Settings,
                ThemeOptionStore.SettingName(other.Manifest.Id, otherOption.Layer, otherOption.Option.Id), otherValue);
            return Task.CompletedTask;
        }

        (LayerOption option, object value) = Resolve(active, change);
        ThemeOptionStore store = s_store!;
        if (!store.Publish(option, value)) return Task.CompletedTask;

        s_values = store.Snapshot();
        PublishFacts(active);
        ApplyDefinition(active);
        if (option.Option.Apply == ApplyMode.Transaction) Remount();
        return Task.CompletedTask;
    }

    /// <summary>Forgets every persisted option of the installed theme <paramref name="themeId"/> (its UI state stays) and, when it
    /// is the active theme, puts the cells back to the composition's defaults: a Live option re-renders its readers, a
    /// Transaction option remounts the root once. The Settings "Reset ‹theme› options" verb. No-op for an id that is not installed.</summary>
    public static void ResetOptions(string themeId)
    {
        ArgumentNullException.ThrowIfNull(themeId);
        RequireInstalled();
        ThemeComposition? target = ThemeCatalog.Find(themeId);
        if (target is null) return;
        ThemeOptionStore.ResetOptions(target, Platform.Settings);

        ThemeComposition active = s_current.Peek();
        if (!ReferenceEquals(target, active)) return;
        ThemeOptionStore store = s_store!;
        bool remount = false;
        bool changed = false;
        foreach (LayerOption lo in active.AllOptions)
        {
            if (!active.IsUserOption(lo) || active.DefaultOf(lo) is not { } fallback) continue;
            if (!lo.Option.Publish(store.Store, fallback, persist: false)) continue;
            changed = true;
            remount |= lo.Option.Apply == ApplyMode.Transaction;
        }
        if (!changed) return;
        s_values = store.Snapshot();
        PublishFacts(active);
        ApplyDefinition(active);
        if (remount) Remount();
    }

    private static (LayerOption Option, object Value) Resolve(ThemeComposition theme, OptionChange change)
    {
        LayerOption option = theme.FindQualified(change.LayerQualifiedId)
            ?? throw new ArgumentException($"ThemeHost.Apply: theme '{theme.Manifest.Id}' has no option '{change.LayerQualifiedId}'.", nameof(change));
        if (!theme.IsUserOption(option))
            throw new ArgumentException($"ThemeHost.Apply: option '{change.LayerQualifiedId}' of theme '{theme.Manifest.Id}' cannot be set by the user.", nameof(change));
        if (!option.Option.TryCoerce(change.Value, out object? coerced) || coerced is null)
            throw new ArgumentException($"ThemeHost.Apply: '{change.Value}' is not a valid value of option '{change.LayerQualifiedId}'.", nameof(change));
        return (option, coerced);
    }

    private static void Activate(ThemeComposition theme, IAppSettings settings)
    {
        var store = new ThemeOptionStore(theme, settings);
        store.Store.Requests = new DelegateOptionRequests(OnOptionRequest);
        s_store = store;
        s_values = store.Snapshot();
        s_registry = theme.Registry;
        ThemeState.Bind(theme.Manifest.Id);
        PublishFacts(theme);
        s_current.Value = theme;
        ThemePickerFacts.Publish(new PickerState(ThemeCatalog.Installed, theme.Manifest.Id));
    }

    private static void PublishFacts(ThemeComposition theme)
    {
        // The tiers are the window's fact (T3b), not the composition's: keep what is published.
        ThemeFacts facts = theme.Facts(s_values) with { Tiers = s_facts.Peek().Tiers };
        // A policy the core state machines cannot run on (an inverted band, a level below a hit-target floor) never reaches
        // them: the structure is told in the log and the shell's baseline policies stand in, so a theme's bug is a wrong
        // look and not a frozen or collapsed window.
        IReadOnlyList<string> problems = facts.Policies.Validate();
        if (problems.Count > 0)
        {
            Log.Warn("theme", "theme '" + theme.Manifest.Id + "' published policies the shell cannot run on; using the baseline: " + string.Join("; ", problems));
            facts = facts with { Policies = ThemePolicies.Default };
        }
        s_facts.Value = facts;
        s_policies.Value = facts.Policies;
    }

    /// <summary>Hands the composition's definition to the engine when it is not the one already installed. For a plain
    /// pairing (Classic) the definition IS the one the appearance seed installed, so nothing happens.</summary>
    private static void ApplyDefinition(ThemeComposition theme)
    {
        ThemeDefinition definition = theme.Definition(s_values, OsAccent);
        if (!ReferenceEquals(definition, Tok.Definition)) Tok.Use(definition, Tok.Theme);
    }

    private static void Remount() => s_rootEpoch.Value = s_rootEpoch.Peek() + 1;

    /// <summary>A view asked for an option change through <c>ctx.OptionRequests</c>: the request names the option by its id.</summary>
    private static void OnOptionRequest(string optionId, object? raw)
    {
        ThemeComposition? active = s_installed ? s_current.Peek() : null;
        if (active is null || raw is null) return;
        LayerOption? option = active.Find(optionId);
        if (option is null)
        {
            Log.Warn("theme", "option request for '" + optionId + "' ignored: the active theme declares no such option");
            return;
        }
        try { _ = Apply(new OptionChange(active.Manifest.Id, option.Value.QualifiedId, raw)); }
        catch (ArgumentException ex) { Log.Warn("theme", "option request for '" + optionId + "' rejected", ex); }
    }

    private static void RequireInstalled()
    {
        if (!s_installed) throw new InvalidOperationException("ThemeHost.Install has not run (App.BootCore installs the compositions).");
    }
}
