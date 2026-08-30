using System;
using System.Text.Json;
using FluentGpu.Controls;
using FluentGpu.Localization;
using Wavee.Core.Sidebar;

namespace Wavee;

/// <summary>
/// ONE bindable action, as the extension platform sees it (platform doc "Action registry"; plan REVISION 2 item 2).
/// This is the descriptor a <c>SidebarActionBinding</c> resolves to through <see cref="WaveeExtensionRegistry"/> — the
/// registry is the path, and no new UI code looks up <c>AppActions.All</c> directly (REVISION 2's guardrail).
///
/// RELATIONSHIP TO <see cref="AppAction"/>: they are deliberately different shapes and neither replaces the other.
/// <see cref="AppAction"/> is the CONTEXT-MENU model — it acts on a live <see cref="ActionTarget"/> built at menu-open
/// time (a resolved track set, a playlist host with row ids) and its label is count-aware. A descriptor is the BOUND
/// model — it acts on a persisted <see cref="SidebarActionBinding"/> whose target is a mode plus a key, so it must
/// resolve the target itself, must be able to say WHY it cannot, and must survive an app restart. First-party
/// descriptors therefore WRAP the existing <see cref="ActionId"/> verbs (<see cref="LegacyId"/> records which one)
/// rather than re-implementing them; the <see cref="ActionId"/> enum stays internal, as specified.
///
/// AOT / GENERATOR SHAPE: a plain init-only class with delegate members, constructed by hand in
/// <see cref="BuiltInExtensionTable"/> today and emitted verbatim by the M4 source generator. No reflection, no
/// attributes, no runtime discovery.
///
/// THREADING: constructed at startup on the UI thread and then IMMUTABLE. The delegates run on the UI thread only.
/// </summary>
public sealed class WaveeActionDescriptor
{
    /// <summary>The namespaced stable key — <c>wavee.play</c>, <c>publisher.extension.refresh</c>. Persisted inside
    /// bindings, so it may never change once shipped.</summary>
    public required string Key { get; init; }

    /// <summary>Loc KEY of the display label (never a literal string — the row/picker calls <see cref="Label"/>).</summary>
    public required string LabelLocKey { get; init; }

    /// <summary>Semantic icon key resolved through <see cref="ActionIcons.Resolve"/> — never a raw glyph, per the
    /// documented <c>IconKey</c> rule.</summary>
    public required string IconKey { get; init; }

    /// <summary>The target modes this action accepts. The customizer's binding UI offers exactly these; a stored binding
    /// naming anything else resolves <see cref="WaveeActionUnavailable.ModeNotSupported"/>.</summary>
    public WaveeActionTargetModes AcceptedTargets { get; init; } = WaveeActionTargetModes.None;

    /// <summary>The argument schema, OPAQUE JSON in M1 (the customizer generates property controls from it in M2/M4).
    /// Null = the action takes no arguments. Arguments themselves ride on the binding
    /// (<c>SidebarActionBinding.Arguments</c>) and are likewise never interpreted here.</summary>
    public JsonElement? ArgumentSchema { get; init; }

    /// <summary>Extra enablement beyond target resolution (a service present, a capability live). Null ⇒ enabled
    /// whenever the target resolves. A false result renders the row visible-but-disabled with
    /// <see cref="WaveeActionUnavailable.NotApplicable"/>.</summary>
    public Func<ActionServices, SidebarActionBinding, bool>? IsEnabled { get; init; }

    /// <summary>Non-null ⇒ the action is a toggle (a saved heart, a follow state): the row renders checked/unchecked and
    /// <see cref="Icon"/> picks the filled variant.</summary>
    public Func<ActionServices, SidebarActionBinding, bool>? IsChecked { get; init; }

    /// <summary>Destructive verb (remove/delete). Carried for presentation (a future red-text row) — the SAFETY gate is
    /// <see cref="RequiresConfirmation"/>, which is what actually blocks a bypass.</summary>
    public bool Destructive { get; init; }

    /// <summary>True ⇒ <see cref="Execute"/> routes through the app's EXISTING confirmation surface
    /// (<c>SettingsShared.Confirm</c>, the <c>ContainerActions.DeletePlaylist</c> precedent) and REFUSES to run at all
    /// when there is no overlay to confirm in. "No sidebar binding can bypass it" (platform doc), and a null overlay
    /// must never degrade into an unconfirmed run.</summary>
    public bool RequiresConfirmation { get; init; }

    /// <summary>Confirmation dialog copy (loc KEYS). Title/primary fall back to <see cref="LabelLocKey"/>; the body
    /// falls back to the title. Ignored unless <see cref="RequiresConfirmation"/>.</summary>
    public string? ConfirmTitleLocKey { get; init; }
    public string? ConfirmBodyLocKey { get; init; }
    public string? ConfirmPrimaryLocKey { get; init; }

    /// <summary>Capability names this action needs (platform doc "Permissions": <c>playback.control</c>,
    /// <c>library.read</c>, …). RECORDED but UNENFORCED until M3, when the sandboxed host gates invocation on the
    /// extension's granted set. First-party descriptors still declare them honestly, so M3 turns enforcement on without
    /// re-authoring the table.</summary>
    public string[] RequiredPermissions { get; init; } = Array.Empty<string>();

    /// <summary>The <see cref="ActionId"/> this first-party descriptor wraps (<see cref="ActionId.None"/> for a
    /// third-party contribution). Diagnostics/telemetry only — nothing dispatches on it.</summary>
    public ActionId LegacyId { get; init; }

    /// <summary>The execution adapter. Receives the ALREADY-RESOLVED target, so an adapter never re-resolves and
    /// therefore can never disagree with the enablement the row rendered. (The platform doc sketches a two-argument
    /// adapter; the third argument is the resolution, which is the one thing the adapter must not recompute.)</summary>
    public required Action<ActionServices, SidebarActionBinding, WaveeActionTargetResolution> Run { get; init; }

    // ── projections ──────────────────────────────────────────────────────────────────────────────────────────────────

    public string Label() => Loc.Get(LabelLocKey);

    public IconRef Icon(bool isChecked = false) => ActionIcons.Resolve(IconKey, isChecked);

    /// <summary>Resolve the binding's target AND fold in every non-target reason a row can be disabled: a mode this
    /// descriptor does not accept, a missing target key, no now-playing / no active route, the descriptor's own
    /// enablement veto, and a confirmation-required action with no overlay to confirm in. One call, so the row's
    /// disabled state and <see cref="Execute"/>'s refusal can never disagree.
    ///
    /// <paramref name="peek"/> false (the default) reads the live signals REACTIVELY, which is what a rendering row
    /// wants; true snapshots them without subscribing, which is what an invoke does.</summary>
    public WaveeActionTargetResolution Resolve(ActionServices services, SidebarActionBinding binding, bool peek = false)
    {
        var host = HostStateOf(services, binding, peek);
        var target = WaveeActionTargets.Resolve(binding, AcceptedTargets, in host);
        if (!target.Available) return target;

        if (RequiresConfirmation && services.Overlay is null)
            return WaveeActionTargets.Unavailable(binding.TargetMode, WaveeActionUnavailable.HostUnavailable);

        if (IsEnabled is { } gate && !gate(services, binding))
            return WaveeActionTargets.Unavailable(binding.TargetMode, WaveeActionUnavailable.NotApplicable);

        return target;
    }

    public bool Checked(ActionServices services, SidebarActionBinding binding)
        => IsChecked is { } isChecked && isChecked(services, binding);

    /// <summary>Invoke. No-ops (returning the reason, never throwing) when the target is unavailable; routes a
    /// confirmation-required action through the existing confirm dialog and runs nothing until the user confirms.</summary>
    public WaveeActionUnavailable Execute(ActionServices services, in SidebarActionBinding binding)
    {
        var b = binding;
        var target = Resolve(services, b, peek: true);
        if (!target.Available) return target.Reason;

        if (!RequiresConfirmation)
        {
            Run(services, b, target);
            return WaveeActionUnavailable.None;
        }

        // Resolve() already refused a null overlay, so this cannot silently skip the confirmation.
        if (services.Overlay is not { } overlay) return WaveeActionUnavailable.HostUnavailable;
        var run = Run;
        var s = services;
        SettingsShared.Confirm(overlay,
            Loc.Get(ConfirmTitleLocKey ?? LabelLocKey),
            Loc.Get(ConfirmBodyLocKey ?? ConfirmTitleLocKey ?? LabelLocKey),
            Loc.Get(ConfirmPrimaryLocKey ?? LabelLocKey),
            () => run(s, b, target));
        return WaveeActionUnavailable.None;
    }

    /// <summary>Project into a menu row (the Curated "Action shortcut" row's overflow, the customizer's picker preview).
    /// An unavailable target renders VISIBLE BUT DISABLED — the platform doc's rule — with the reason appended to the
    /// accelerator column, which is the one place a menu row can carry a short explanation without a second line.</summary>
    public MenuFlyoutItem ToMenuItem(ActionServices services, SidebarActionBinding binding, Action? after = null)
    {
        var target = Resolve(services, binding);
        bool enabled = target.Available;
        string label = Label();
        string? reason = enabled ? null : Loc.Get(target.ReasonLocKey!);
        var self = this;
        var s = services;
        var b = binding;
        var post = after;
        Action invoke = () => { self.Execute(s, in b); post?.Invoke(); };

        if (IsChecked is { } isChecked)
        {
            bool on = isChecked(services, binding);
            return MenuFlyoutItem.Toggle(label, on, invoke, Icon(on), enabled) with { AcceleratorText = reason };
        }
        return new MenuFlyoutItem(label, Icon(), enabled, invoke) { AcceleratorText = reason };
    }

    /// <summary>Snapshot the live app facts the dynamic target modes resolve against. The route provider is the host's
    /// (<c>ActionServices.CurrentRoute</c>); when the host supplied none, an <c>ActiveRoute</c> binding resolves
    /// unavailable rather than guessing.</summary>
    static WaveeActionHostState HostStateOf(ActionServices services, SidebarActionBinding binding, bool peek)
    {
        var playing = services.Playback is { } pb
            ? (peek ? pb.CurrentTrack.Peek() : pb.CurrentTrack.Value)
            : null;
        string? context = services.Playback is { } playback
            ? (peek ? playback.CurrentContext.Peek() : playback.CurrentContext.Value)
            : null;
        var destination = services.CurrentDestination?.Invoke();
        string? fixedName = FixedTargetNameOf(services, binding);
        if (fixedName is null && binding.TargetMode == SidebarActionTargetMode.FixedTrack
            && playing is { } track && string.Equals(track.Uri, binding.TargetKey, StringComparison.Ordinal))
            fixedName = track.Title;

        return new WaveeActionHostState(
            playing?.Uri,
            context,
            services.CurrentRoute?.Invoke(),
            fixedTargetName: fixedName,
            nowPlayingTrackName: playing?.Title,
            activeRouteName: destination?.Name);
    }

    static string? FixedTargetNameOf(ActionServices services, SidebarActionBinding binding)
    {
        if (binding.TargetMode is not (SidebarActionTargetMode.FixedEntity or SidebarActionTargetMode.FixedTrack)
            || binding.TargetKey is not { Length: > 0 } key)
            return null;
        var byKey = services.Sidebar?.Binder?.CurrentInput.ByUri;
        return byKey is not null && byKey.TryGetValue(key, out var entry) && entry.Name.Length > 0
            ? entry.Name
            : null;
    }
}

/// <summary>The capability names <see cref="WaveeActionDescriptor.RequiredPermissions"/> declares — the platform doc's
/// permission vocabulary, as consts so first-party descriptors and (M3) manifest parsing share one spelling. Unenforced
/// until M3.</summary>
public static class WaveePermissions
{
    public const string LibraryRead = "library.read";
    public const string LibraryWrite = "library.write";
    public const string HistoryRead = "history.read";
    public const string PlaybackRead = "playback.read";
    public const string PlaybackControl = "playback.control";
    public const string NavigationContribute = "navigation.contribute";
    public const string ActionsInvoke = "actions.invoke";
    public const string StoragePrivate = "storage.private";
    public const string SecretsPrivate = "secrets.private";
    public const string ClipboardWrite = "clipboard.write";
    public const string ExternalOpen = "external.open";
    /// <summary>Local presentation state (pins, section expansion) — not in the doc's third-party list on purpose: an
    /// external extension does not get to rewrite the user's sidebar behind their back.</summary>
    public const string SidebarPins = "sidebar.pins";
}
