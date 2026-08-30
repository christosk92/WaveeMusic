using System;
using System.Collections.Generic;
using FluentGpu.Hooks;
using Wavee.Core.Sidebar;

namespace Wavee;

/// <summary>
/// THE contribution registry (plan REVISION 2 item 2; platform doc "Action registry" + "Data-source and contribution
/// registry"). It replaces the fixed-only <c>AppActions.All</c> lookup for everything BOUND — a
/// <c>SidebarActionBinding</c>, the customizer's action picker, a Curated section's data source — while the
/// <see cref="ActionId"/> enum and the <see cref="AppAction"/> context-menu table stay exactly as they are (first-party
/// descriptors wrap them). REVISION 2's guardrails, encoded here:
///
///   * <b>No new UI code looks up <c>AppActions.All</c>.</b> Bound UI resolves through <see cref="TryGetAction"/>.
///   * <b>Never <c>switch</c> on an extension id.</b> Section rendering resolves a contribution id through
///     <see cref="TryGetSource"/>; first-party ids are ordinary registry keys under the publisher <c>"wavee"</c>.
///   * <b>A stored binding always resolves to SOMETHING renderable.</b> Nothing is ever unregistered (a disabled
///     extension is filtered at the consumption site), and an unresolvable key yields
///     <see cref="WaveeActionUnavailable.ActionMissing"/> — a visible-but-disabled row with a reason, never a vanishing
///     one.
///
/// DUPLICATE-KEY POLICY: <b>first wins.</b> A second registration under a live key is refused and recorded in
/// <see cref="Diagnostics"/>; the existing contribution is untouched. Because <see cref="BuiltInExtensionTable"/> runs
/// first, no third-party extension can shadow a first-party action or data source — which is why the policy is
/// first-wins rather than last-wins.
///
/// THREADING: UI thread only, unsynchronized, and REGISTRATION-THEN-READ: every contribution is registered during
/// startup (<see cref="Build"/> → <see cref="BuiltInExtensionTable.RegisterAll"/>, and in M3 the sandboxed host's
/// manifest replay after it has marshalled onto the UI thread); afterwards the tables are read-only from the render
/// path. There is no lock and no off-thread producer — the same discipline as <c>SidebarPinStore</c> /
/// <c>SidebarPreferences</c>.
/// </summary>
public sealed class WaveeExtensionRegistry : IWaveeExtensionRegistrar
{
    /// <summary>The app-root context channel, so a component reaches the registry without a frozen ctor prop. The
    /// instance is reference-stable for the process lifetime, so the provide never churns its consumers (the
    /// <c>ActionServices</c> precedent).</summary>
    public static readonly Context<WaveeExtensionRegistry?> Slot = new(null);

    readonly WaveeRegistryTable<WaveeActionDescriptor> _actions = new();
    readonly WaveeRegistryTable<ISidebarDataSource> _sources = new();
    readonly List<string> _extensions = new();

    /// <summary>The process-wide instance <see cref="Build"/> produced. A static, deliberately: this table replaces the
    /// static <c>AppActions.All</c>, it is populated once at startup, and a composition root that has not built it yet
    /// must see null rather than an empty-but-plausible registry.</summary>
    public static WaveeExtensionRegistry? Current { get; private set; }

    /// <summary>Build the registry and register the first-party extension's contributions. Called ONCE from the
    /// composition root. Idempotent guard: a second call replaces <see cref="Current"/> with the new instance (the
    /// login-gate shell swap must not double-register into a live table).</summary>
    public static WaveeExtensionRegistry Build(ActionServices services)
    {
        var registry = new WaveeExtensionRegistry();
        registry.Register(BuiltInExtensionTable.ExtensionId, r => BuiltInExtensionTable.RegisterAll(r, services));
        Current = registry;
        return registry;
    }

    // ── enumeration (the future action picker) ────────────────────────────────────────────────────────────────────────

    /// <summary>Every registered action, in registration order — first-party first, because
    /// <see cref="BuiltInExtensionTable"/> registers first. This IS the customizer's action-picker source.</summary>
    public IReadOnlyList<WaveeActionDescriptor> Actions => _actions.Items;

    /// <summary>Every registered data source, in registration order.</summary>
    public IReadOnlyList<ISidebarDataSource> Sources => _sources.Items;

    /// <summary>The extension ids that registered anything, in order.</summary>
    public IReadOnlyList<string> Extensions => _extensions;

    /// <summary>Refused registrations (invalid key, duplicate, null) across BOTH tables. Empty on a healthy startup.
    /// Surfaced by devtools / the extensions page — never a toast: a registration fault is a developer or publisher
    /// fact, not a user interruption.</summary>
    public IReadOnlyList<WaveeRegistryDiagnostic> Diagnostics
    {
        get
        {
            if (_actions.Diagnostics.Count == 0) return _sources.Diagnostics;
            if (_sources.Diagnostics.Count == 0) return _actions.Diagnostics;
            var all = new List<WaveeRegistryDiagnostic>(_actions.Diagnostics.Count + _sources.Diagnostics.Count);
            all.AddRange(_actions.Diagnostics);
            all.AddRange(_sources.Diagnostics);
            return all;
        }
    }

    // ── registration ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Run one trusted extension's <see cref="IWaveeExtension.Register"/>.</summary>
    public WaveeExtensionRegistry Register(string extensionId, IWaveeExtension extension)
        => Register(extensionId, extension.Register);

    /// <summary>Run one registration pass under a named extension id. The delegate form is what
    /// <see cref="BuiltInExtensionTable"/> (and the M4 generator) uses — a hand-written/generated static table is not an
    /// <see cref="IWaveeExtension"/> instance, and inventing one would add an allocation and a type per extension for
    /// nothing.</summary>
    public WaveeExtensionRegistry Register(string extensionId, Action<IWaveeExtensionRegistrar> register)
    {
        if (!string.IsNullOrEmpty(extensionId) && !_extensions.Contains(extensionId)) _extensions.Add(extensionId);
        register(this);
        return this;
    }

    // Both accept a null defensively: the interface says non-null, but in M3 the caller is a sandboxed extension's
    // manifest replay, and a hostile/broken contribution must be a diagnostic rather than an NRE at startup.
    public void RegisterAction(WaveeActionDescriptor descriptor)
        => _actions.Add(descriptor is null ? null : descriptor.Key, descriptor);

    public void RegisterDataSource(ISidebarDataSource source)
        => _sources.Add(source is null ? null : source.Id, source);

    // ── lookup ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Lookup by namespaced key (<c>"wavee.play"</c>, <c>"publisher.ext.x"</c>).</summary>
    public bool TryGetAction(string? key, out WaveeActionDescriptor descriptor) => _actions.TryGet(key, out descriptor);

    /// <summary>Lookup for a stored binding: the key is <c>ProviderId + '.' + ActionId</c>, tolerating a document that
    /// already stored the fully-qualified form (<see cref="WaveeExtensionKey.Compose"/>).</summary>
    public bool TryGetAction(SidebarActionBinding binding, out WaveeActionDescriptor descriptor)
        => _actions.TryGet(KeyOf(binding), out descriptor);

    public bool TryGetSource(string? id, out ISidebarDataSource source) => _sources.TryGet(id, out source);

    public bool HasAction(string? key) => _actions.Contains(key);
    public bool HasSource(string? id) => _sources.Contains(id);

    /// <summary>The registry key a binding resolves to.</summary>
    public static string KeyOf(SidebarActionBinding binding)
        => WaveeExtensionKey.Compose(binding.ProviderId, binding.ActionId);

    // ── bound invocation (the ONE path bound UI takes) ────────────────────────────────────────────────────────────────

    /// <summary>What a bound row should render: available, or visible-but-disabled with a reason. A key that resolves to
    /// nothing is <see cref="WaveeActionUnavailable.ActionMissing"/> — the extension was removed or disabled, and the
    /// user must be told that rather than have their row silently disappear.</summary>
    public WaveeActionTargetResolution Resolve(ActionServices services, SidebarActionBinding binding)
        => TryGetAction(binding, out var descriptor)
            ? descriptor.Resolve(services, binding)
            : WaveeActionTargets.Unavailable(binding.TargetMode, WaveeActionUnavailable.ActionMissing);

    /// <summary>Invoke a bound action. Returns the refusal reason (never throws) when nothing ran.</summary>
    public WaveeActionUnavailable Execute(ActionServices services, in SidebarActionBinding binding)
        => TryGetAction(binding, out var descriptor)
            ? descriptor.Execute(services, in binding)
            : WaveeActionUnavailable.ActionMissing;
}
