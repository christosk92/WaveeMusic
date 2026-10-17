// ── Theming/Composition/ThemeHost.cs — the active theme: install, current, facts, switch, apply ───────────────────────────
//
// The process-wide state of theming (UI thread only, like every signal in the app):
//   · Install     called once by App.cs with every composition; resolves the stored `theme.id` and activates it.
//   · Current     the active composition (a signal; only code allowed to read identity may use it, WVTH004).
//   · Facts       what core needs from it: policies, capabilities, metrics, tiers. Core models read this signal; templates
//                 read the same signal through `ThemeFactsContext`.
//   · Switch      another composition, on the engine's THEME TRANSACTION (E5, A4.2): prewarm the new fonts with the UI live,
//                 freeze the window and its pop-outs, then in one apply flush persist `theme.id`, activate the composition,
//                 install its definition and REMOUNT the root; settle; thaw with the 250 ms dissolve.
//   · Apply       one option: a Live option is written to its cell (the views that read it re-render) and, when it moves
//                 the definition, applied live; a Transaction option is published inside a transaction IN PLACE (no
//                 remount: a slot whose template did not change keeps its state, plan 5.6).
//
// THE REMOUNT. The root remounts because `ThemeRoot` renders a different component type for the new epoch (the engine replaces a
// component whose type differs), which disposes every page below it: nothing is carried (owner Q26). Under the freeze the new
// pages mount fresh and settle; the user sees the old frame, then the dissolve to the settled new one.
//
// THE RUNNER. The frame root binds the window's transaction runner (`BindTransactions`, from its `InputHooks`). With none bound
// (boot before the window, unit tests) a change applies at once: there is no frame to hold. A failed transaction is rolled back
// under the freeze by the engine (the previous composition is re-activated and the root remounted) and told in a toast.
//
// NAME. The class is `Wavee.Theming.ThemeHost` (plan 5.3) and each theme lives in a namespace `Wavee.Themes.<Name>` (plan
// 1.2). Inside `namespace Wavee` the simple name `Themes` therefore binds to the NAMESPACE: callers there (App.cs) write the
// class as `Wavee.Theming.ThemeHost`, and tests alias it (`using ThemeHost = Wavee.Theming.ThemeHost;`).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Localization;
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
    private static readonly Signal<StyleMetrics> s_metrics = new(StyleMetrics.Unset);
    private static readonly Signal<long> s_rootEpoch = new(0L);
    private static readonly OptionStore s_noOptions = new();

    private static ThemeOptionStore? s_store;
    private static ThemeOptionValues s_values = ThemeOptionValues.Empty;
    private static ViewRegistry s_registry = ViewRegistry.Empty;
    private static bool s_installed;

    private static Func<ThemeTransactionRequest, Task<ThemeTransactionResult>>? s_runTransaction;
    private static IReadSignal<bool>? s_transactionBusy;
    private static Func<bool>? s_transactionRunning;
    private static Action? s_heldNavigation;
    private static readonly Signal<string?> s_pendingSwitch = new(null);
    private static readonly Task<ThemeTransactionResult> s_nothingToDo =
        Task.FromResult(new ThemeTransactionResult(ThemeTransactionOutcome.Immediate));

    /// <summary>The OS accent the style resolves its definition with. It is an INPUT, not an override: the active style's
    /// <c>Definition(options, OsAccent)</c> decides whether it uses it (Classic follows it; Zune only with "use the windows
    /// accent"), and the engine's accent follows the resolved <c>ThemeDefinition.Accent</c>. Defaults to the WinUI ramp (which every
    /// definition treats as "use the baked accent tokens"); <c>Appearance</c> feeds the real one at boot and on every OS colour change
    /// (<see cref="FeedOsAccent"/>). Nothing may push the OS accent through <c>Tok.SetAccent</c>: an engine override wins over the definition.</summary>
    public static AccentRamp OsAccent { get; set; } = AccentRamp.FluentDefault;

    /// <summary>Assigns <see cref="OsAccent"/> from what the OS answered: the exact <paramref name="ramp"/> when it has one, else (only
    /// when <paramref name="allowPlainFallback"/>) the ramp derived from the <paramref name="plain"/> accent colour, else nothing (the
    /// previous value stands). Returns true when <see cref="OsAccent"/> changed: the caller then re-installs the active definition
    /// (<c>Design.SeedPalette</c>), which resolves with the new accent. Pure state, no token set is touched.</summary>
    public static bool FeedOsAccent(AccentRamp? ramp, ColorF? plain, bool allowPlainFallback)
    {
        AccentRamp? next = ramp;
        if (next is null && allowPlainFallback && plain is { } colour) next = AccentRamp.Derive(colour);
        if (next is not { } accent || accent == OsAccent) return false;
        OsAccent = accent;
        return true;
    }

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

    /// <summary>The style's metrics (<see cref="ThemeFacts.Metrics"/>) on a signal of their own, for the same reason as <see cref="Policies"/>: a core model that sizes
    /// its rows from them (the track table, M3) is not re-run when the window tier moves.</summary>
    public static IReadSignal<StyleMetrics> Metrics => s_metrics;

    /// <summary>The option values of the active composition, as the layers' functions see them.</summary>
    public static ThemeOptionValues Values => s_values;

    /// <summary>The definition of the active composition for its option values and the OS accent, or null before
    /// <see cref="Install"/>. This is what the appearance seed installs (<c>Design.SeedPalette</c>), so a light/dark flip keeps
    /// the active theme's roles and not the base definition's. The composition memoizes it: the same inputs return the same instance.</summary>
    public static ThemeDefinition? ActiveDefinition => s_installed ? s_current.Peek().Definition(s_values, OsAccent) : null;

    /// <summary>Bumped by every switch (inside its transaction's apply flush): <c>ThemeRoot</c> remounts the root on it. A Transaction-mode
    /// option change does not bump it (it re-renders in place).</summary>
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

    /// <summary>The frame root binds its window's theme-transaction runner (<see cref="InputHooks.RunThemeTransaction"/>) on
    /// every render, and the root's scope unbinds it (null) when that root goes. A pop-out's hooks carry no runner and never bind.</summary>
    public static void BindTransactions(InputHooks? hooks)
    {
        if (hooks is not null && hooks.RunThemeTransaction is null) return;
        s_runTransaction = hooks?.RunThemeTransaction;
        s_transactionBusy = hooks?.ThemeTransactionBusy;
        s_transactionRunning = hooks?.IsThemeTransactionRunning;
    }

    /// <summary>True while a theme transaction runs or waits on the bound window (from the request to its thaw start).</summary>
    public static bool IsTransactionRunning => s_transactionRunning?.Invoke() == true;

    /// <summary>Navigation during a transaction waits for its thaw start (plan 4.2 f): while one runs, <paramref name="replay"/> is
    /// kept (the latest wins) and run when the transaction commits or fails, and this returns true. False: navigate now.</summary>
    public static bool HoldNavigation(Action replay)
    {
        ArgumentNullException.ThrowIfNull(replay);
        if (!IsTransactionRunning) return false;
        s_heldNavigation = replay;
        return true;
    }

    /// <summary>A transaction committed (thaw start) or failed: the navigation it held runs now.</summary>
    private static void ReleaseNavigation()
    {
        var replay = s_heldNavigation;
        s_heldNavigation = null;
        replay?.Invoke();
    }

    /// <summary>The theme a <see cref="Switch"/> is heading to while its transaction runs, or null. The picker shows its card
    /// pressed, with a busy ring once <see cref="SwitchBusy"/> says the prewarm has held the UI for 100 ms.</summary>
    public static IReadSignal<string?> PendingSwitch => s_pendingSwitch;

    /// <summary>True while a transaction's prewarm has held the live UI for 100 ms (a tracked read inside a render).</summary>
    public static bool SwitchBusy => s_transactionBusy?.Value ?? false;

    /// <summary>Back to the not-installed state (tests).</summary>
    public static void Reset()
    {
        ThemeCatalog.Set([]);
        OsAccent = AccentRamp.FluentDefault;
        s_installed = false;
        s_store = null;
        s_values = ThemeOptionValues.Empty;
        s_registry = ViewRegistry.Empty;
        s_current.Value = null!;
        s_facts.Value = ThemeFacts.Default;
        s_policies.Value = ThemePolicies.Default;
        s_metrics.Value = StyleMetrics.Unset;
        s_rootEpoch.Value = 0L;
        s_pendingSwitch.Value = null;
        s_heldNavigation = null;
        ThemePickerFacts.Publish(PickerState.Empty);
        ThemeState.Bind(ThemeCatalog.FallbackId);
    }

    /// <summary>Activates the composition with <paramref name="id"/> (unknown or unavailable ids resolve to Classic) on a theme
    /// transaction: its fonts prewarm with the UI live, then under the frozen frame <c>theme.id</c> is persisted, its facts and
    /// options are published, its definition is installed and the root remounts; the new frame settles and dissolves in. A
    /// switch to the active theme completes at once. The task completes on a later turn (or at once when no window is bound).</summary>
    public static Task<ThemeTransactionResult> Switch(string id)
    {
        RequireInstalled();
        ThemeComposition target = ThemeCatalog.Resolve(id);
        ThemeComposition previous = s_current.Peek();
        if (ReferenceEquals(target, previous)) return s_nothingToDo;

        ThemeDefinition next = target.Definition(ThemeOptionStore.Load(target), OsAccent);
        s_pendingSwitch.Value = target.Manifest.Id;
        return Transact(new ThemeTransactionRequest(next, Tok.Theme, () =>
        {
            Platform.Settings.Set(IdKey, target.Manifest.Id);
            Activate(target, Platform.Settings);
            Remount();
        })
        {
            Prewarm = new PrewarmSet { Fonts = next.Fonts },
            Rollback = () =>
            {
                Platform.Settings.Set(IdKey, previous.Manifest.Id);
                Activate(previous, Platform.Settings);
                Remount();
            },
            OnCommitted = static () => s_pendingSwitch.Value = null,
            OnFailed = ex => SwitchFailed(target.Manifest.Id, ex),
            Label = "switch:" + target.Manifest.Id,
        });
    }

    /// <summary>Switches the theme kind on a theme transaction (the appearance service's choice when the active definition changes
    /// more than colours with the kind, e.g. a window material per kind). The definition stays the active one.</summary>
    public static Task<ThemeTransactionResult> SwitchKind(ThemeKind kind)
    {
        ThemeDefinition definition = ActiveDefinition ?? Tok.Definition;
        if (kind == Tok.Theme) return s_nothingToDo;
        return Transact(new ThemeTransactionRequest(definition, kind, static () => { })
        {
            OnFailed = ex => Log.Warn("theme", "switching the theme kind failed and was rolled back", ex),
            Label = "kind:" + kind,
        });
    }

    /// <summary>Applies one option change. The value is validated against the option (an invalid value, an unknown option or
    /// a structure parameter throws <see cref="ArgumentException"/>, a programming error). A Live option is written to its cell and the definition
    /// is re-applied live; a Transaction option is published on a theme transaction, in place (the root does not remount). A
    /// change to a theme that is not active is persisted and nothing else happens.</summary>
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
        if (option.Option.Apply == ApplyMode.Transaction)
        {
            object? old = option.Option.Peek(store.Store);
            if (Equals(old, value)) return s_nothingToDo;
            return PublishInTransaction(active, store, [(option, value, old)], "option:" + option.QualifiedId);
        }
        if (!store.Publish(option, value)) return Task.CompletedTask;

        s_values = store.Snapshot();
        PublishFacts(active);
        ApplyDefinition(active);
        return Task.CompletedTask;
    }

    /// <summary>Publishes Transaction-mode option values on a theme transaction, IN PLACE: under the frozen frame the cells are
    /// written (and persisted), the facts republished and the definition for the new values installed; the views re-render where
    /// they stand, so a slot whose template did not change keeps its state (plan 5.6) and the transient hosting the option's
    /// control stays open. A failure puts the old values back.</summary>
    private static Task<ThemeTransactionResult> PublishInTransaction(ThemeComposition active, ThemeOptionStore store,
        (LayerOption Option, object Value, object? Old)[] changes, string label)
    {
        ThemeOptionValues nextValues = active.Build(lo =>
        {
            foreach (var c in changes) if (c.Option.Equals(lo)) return c.Value;
            return lo.Option.Peek(store.Store);
        });
        ThemeDefinition next = active.Definition(nextValues, OsAccent);
        return Transact(new ThemeTransactionRequest(next, Tok.Theme, () =>
        {
            foreach (var c in changes) store.Publish(c.Option, c.Value);
            s_values = store.Snapshot();
            PublishFacts(active);
        })
        {
            Prewarm = new PrewarmSet { Fonts = next.Fonts },
            Rollback = () =>
            {
                foreach (var c in changes) if (c.Old is not null) store.Publish(c.Option, c.Old);
                s_values = store.Snapshot();
                PublishFacts(active);
            },
            OnFailed = ex => Log.Warn("theme", "option change '" + label + "' failed and was rolled back", ex),
            Label = label,
        });
    }

    /// <summary>Forgets every persisted option of the installed theme <paramref name="themeId"/> (its UI state stays) and, when it
    /// is the active theme, puts the cells back to the composition's defaults: a Live option re-renders its readers; when a
    /// Transaction option changes, all of them are put back in one theme transaction. The Settings "Reset ‹theme› options" verb.
    /// No-op for an id that is not installed.</summary>
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
        var structural = new List<(LayerOption, object, object?)>();
        bool changed = false;
        foreach (LayerOption lo in active.AllOptions)
        {
            if (!active.IsUserOption(lo) || active.DefaultOf(lo) is not { } fallback) continue;
            if (lo.Option.Apply == ApplyMode.Transaction)
            {
                object? old = lo.Option.Peek(store.Store);
                if (!Equals(old, fallback)) structural.Add((lo, fallback, old));
                continue;
            }
            if (lo.Option.Publish(store.Store, fallback, persist: false)) changed = true;
        }
        if (changed)
        {
            s_values = store.Snapshot();
            PublishFacts(active);
            ApplyDefinition(active);
        }
        if (structural.Count > 0) _ = PublishInTransaction(active, store, structural.ToArray(), "reset:" + active.Manifest.Id);
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
        s_metrics.Value = facts.Metrics;
    }

    /// <summary>Hands the composition's definition to the engine when it is not the one already installed. For a plain
    /// pairing (Classic) the definition IS the one the appearance seed installed, so nothing happens.</summary>
    private static void ApplyDefinition(ThemeComposition theme)
    {
        ThemeDefinition definition = theme.Definition(s_values, OsAccent);
        if (!ReferenceEquals(definition, Tok.Definition)) Tok.Use(definition, Tok.Theme);
    }

    private static void Remount() => s_rootEpoch.Value = s_rootEpoch.Peek() + 1;

    /// <summary>Hands <paramref name="request"/> to the bound window's transaction. With no window bound (the boot before the
    /// first frame, unit tests) the change applies at once: there is no frame to hold.</summary>
    private static Task<ThemeTransactionResult> Transact(ThemeTransactionRequest request)
    {
        if (s_runTransaction is { } run)
        {
            var onCommitted = request.OnCommitted;
            var onFailed = request.OnFailed;
            return run(request with
            {
                OnCommitted = () => { onCommitted?.Invoke(); ReleaseNavigation(); },
                OnFailed = ex => { onFailed?.Invoke(ex); ReleaseNavigation(); },
            });
        }
        Tok.Use(request.Next, request.Kind);
        request.Apply();
        request.OnCommitted?.Invoke();
        return Task.FromResult(new ThemeTransactionResult(ThemeTransactionOutcome.Immediate));
    }

    private static void SwitchFailed(string id, Exception error)
    {
        s_pendingSwitch.Value = null;
        Log.Warn("theme", "switching to theme '" + id + "' failed; the current theme was kept", error);
        Notify.Say(Loc.Get(Strings.Settings.ThemePicker.SwitchFailed), FluentGpu.Controls.InfoBarSeverity.Error, dedupeKey: "theme.switch.failed");
    }

    /// <summary>A core verb (a pane menu row, a migration, an undo) asks the ACTIVE theme to set one of its options by plain id, without knowing
    /// which layer declares it or which theme is active (<see cref="NavPaneOptionIds"/> are the ids the sidebar asks for). A theme that declares no such
    /// option ignores the request (logged); a value the option refuses is logged and changes nothing. A no-op before <see cref="Install"/>.</summary>
    public static void Request(string optionId, object value)
    {
        ArgumentException.ThrowIfNullOrEmpty(optionId);
        ArgumentNullException.ThrowIfNull(value);
        OnOptionRequest(optionId, value);
    }

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
