using System;
using Wavee.Core.Sidebar;

namespace Wavee;

// TARGET-MODE RESOLUTION for a bound sidebar action (M1, plan REVISION 2 item 2; platform doc "Action registry":
// "Accepted target modes and argument schema … If the target is unavailable at runtime, the row remains visible but
// disabled with a concise explanation").
//
// ENGINE-FREE BY CONSTRUCTION (System + Wavee.Core + the engine-free SidebarPinId only), like ActionRules.cs /
// SidebarPinId.cs: src/apps/Wavee.Tests source-includes this file, so WaveeExtensionRegistryTests drive the REAL
// resolution matrix instead of a copy of it. Nothing here may reference Signal<T>, Element, Icons, Loc or Tok — the
// unavailability REASON is returned as an enum plus its loc KEY, and Loc.Get happens at the UI edge.

/// <summary>The target modes a descriptor ACCEPTS — the flag mirror of <see cref="SidebarActionTargetMode"/>, because a
/// descriptor accepts a SET while a binding names exactly one. Bit values are an implementation detail (never
/// persisted; the persisted vocabulary is <see cref="SidebarActionTargetMode"/>).</summary>
[Flags]
public enum WaveeActionTargetModes : byte
{
    /// <summary>Accepts nothing — an unbindable descriptor.</summary>
    Nothing = 0,
    /// <summary>Accepts a binding that needs no target at all (a global verb: play/pause, open a page).</summary>
    None = 1,
    FixedEntity = 2,
    FixedTrack = 4,
    NowPlaying = 8,
    ActiveRoute = 16,
    /// <summary>The two "an entity the user picked" forms.</summary>
    AnyFixed = FixedEntity | FixedTrack,
    /// <summary>The two forms resolved from live app state.</summary>
    AnyDynamic = NowPlaying | ActiveRoute,
    All = None | FixedEntity | FixedTrack | NowPlaying | ActiveRoute,
}

/// <summary>Why a bound action cannot run right now. The row still RENDERS (visible-but-disabled with
/// <see cref="WaveeActionTargets.LocKeyOf"/>'s explanation) — it never vanishes, because a vanishing row makes the
/// user's own sidebar look broken.</summary>
public enum WaveeActionUnavailable : byte
{
    None = 0,
    /// <summary>The binding names a mode this descriptor does not accept (a document authored by a newer build, or an
    /// extension that narrowed its accepted set in an update).</summary>
    ModeNotSupported = 1,
    /// <summary>A FixedEntity/FixedTrack binding with no <c>TargetKey</c>.</summary>
    MissingTargetKey = 2,
    /// <summary>A NowPlaying binding while nothing is playing.</summary>
    NoNowPlaying = 3,
    /// <summary>An ActiveRoute binding with no resolvable current page.</summary>
    NoActiveRoute = 4,
    /// <summary>No descriptor is registered for the binding's key (extension removed or disabled).</summary>
    ActionMissing = 5,
    /// <summary>The descriptor resolved but a service it needs is absent — including the deliberate refusal to run a
    /// confirmation-required action with no overlay to confirm in (never silently skip the confirm).</summary>
    HostUnavailable = 6,
    /// <summary>The descriptor's own enablement resolver said no (nothing to say beyond "not right now").</summary>
    NotApplicable = 7,
}

/// <summary>The live app facts the dynamic target modes resolve against. A plain readonly struct so the pure resolver
/// never touches a signal, a bridge or a service: the caller (<see cref="WaveeActionDescriptor"/>) snapshots it.</summary>
public readonly struct WaveeActionHostState
{
    public WaveeActionHostState(string? nowPlayingTrackUri, string? nowPlayingContextUri, string? activeRouteKey,
        string? fixedTargetName = null, string? nowPlayingTrackName = null, string? activeRouteName = null)
    {
        NowPlayingTrackUri = nowPlayingTrackUri;
        NowPlayingContextUri = nowPlayingContextUri;
        ActiveRouteKey = activeRouteKey;
        FixedTargetName = fixedTargetName;
        NowPlayingTrackName = nowPlayingTrackName;
        ActiveRouteName = activeRouteName;
    }

    public string? NowPlayingTrackUri { get; }
    public string? NowPlayingContextUri { get; }
    /// <summary>The best-known display title for a persisted FixedEntity/FixedTrack target.</summary>
    public string? FixedTargetName { get; }
    /// <summary>The current track title captured beside <see cref="NowPlayingTrackUri"/>.</summary>
    public string? NowPlayingTrackName { get; }
    /// <summary>The current page's nav route key (<c>home</c>, <c>pl:spotify:playlist:…</c>). Null when the host has not
    /// supplied a route provider — an <see cref="SidebarActionTargetMode.ActiveRoute"/> binding then resolves
    /// <see cref="WaveeActionUnavailable.NoActiveRoute"/> rather than guessing.</summary>
    public string? ActiveRouteKey { get; }
    /// <summary>The active destination's display title captured beside <see cref="ActiveRouteKey"/>.</summary>
    public string? ActiveRouteName { get; }

    public static WaveeActionHostState Empty => default;
}

/// <summary>The resolved WHAT of a bound invocation: the entity uri, its nav route key, the surrounding context uri, or
/// the reason none of them could be produced. Passed to the descriptor's execution adapter, so an adapter never
/// re-resolves and therefore can never disagree with the enablement the row rendered.</summary>
public readonly struct WaveeActionTargetResolution
{
    internal WaveeActionTargetResolution(SidebarActionTargetMode mode, string uri, string? routeKey, string? contextUri,
                                         WaveeActionUnavailable reason, string? name = null)
    {
        Mode = mode;
        _uri = uri;
        _name = name;
        RouteKey = routeKey;
        ContextUri = contextUri;
        Reason = reason;
    }

    readonly string? _uri;
    readonly string? _name;

    public SidebarActionTargetMode Mode { get; }

    /// <summary>The entity/track uri this invocation acts on; "" when the mode carries none (None / a route with no
    /// entity behind it).</summary>
    public string Uri => _uri ?? "";

    /// <summary>The best-known target title for user-facing activity/history. Empty when the host cannot resolve one.</summary>
    public string Name => _name ?? "";

    /// <summary>The nav route key <c>Go(key, name)</c> takes, or null when there is none (a track, a folder).</summary>
    public string? RouteKey { get; }

    /// <summary>The surrounding context uri when the mode has one (NowPlaying's playing context). Null otherwise.</summary>
    public string? ContextUri { get; }

    public WaveeActionUnavailable Reason { get; }

    public bool Available => Reason == WaveeActionUnavailable.None;

    /// <summary>The loc key of the concise explanation a disabled row shows. Null when available.</summary>
    public string? ReasonLocKey => WaveeActionTargets.LocKeyOf(Reason);
}

/// <summary>The pure target-mode matrix. One function, so every surface (a Curated action-shortcut row, the
/// customizer's binding UI, a future command palette) agrees on what a binding means and on why it is disabled.</summary>
public static class WaveeActionTargets
{
    // The concise disabled-row explanations. LITERAL keys rather than generated `Strings.*` members because the loc
    // catalog is owned centrally (assets/loc/*.json are added in the localization wave) — a missing key renders
    // loudly as "[key]" by design, which is exactly the signal we want if the wave is skipped. See the HANDOFF.
    public const string LocKeyModeNotSupported = "sidebar.action.unavailable.mode";
    public const string LocKeyMissingTargetKey = "sidebar.action.unavailable.noTarget";
    public const string LocKeyNoNowPlaying = "sidebar.action.unavailable.noNowPlaying";
    public const string LocKeyNoActiveRoute = "sidebar.action.unavailable.noRoute";
    public const string LocKeyActionMissing = "sidebar.action.unavailable.missing";
    public const string LocKeyHostUnavailable = "sidebar.action.unavailable.host";
    public const string LocKeyNotApplicable = "sidebar.action.unavailable.notNow";

    public static string? LocKeyOf(WaveeActionUnavailable reason) => reason switch
    {
        WaveeActionUnavailable.None => null,
        WaveeActionUnavailable.ModeNotSupported => LocKeyModeNotSupported,
        WaveeActionUnavailable.MissingTargetKey => LocKeyMissingTargetKey,
        WaveeActionUnavailable.NoNowPlaying => LocKeyNoNowPlaying,
        WaveeActionUnavailable.NoActiveRoute => LocKeyNoActiveRoute,
        WaveeActionUnavailable.ActionMissing => LocKeyActionMissing,
        WaveeActionUnavailable.HostUnavailable => LocKeyHostUnavailable,
        _ => LocKeyNotApplicable,
    };

    /// <summary>The single-mode flag for a persisted mode value. An unknown (future) value maps to
    /// <see cref="WaveeActionTargetModes.Nothing"/>, so it is refused by <see cref="Accepts"/> rather than silently
    /// treated as <c>None</c>.</summary>
    public static WaveeActionTargetModes Bit(SidebarActionTargetMode mode) => mode switch
    {
        SidebarActionTargetMode.None => WaveeActionTargetModes.None,
        SidebarActionTargetMode.FixedEntity => WaveeActionTargetModes.FixedEntity,
        SidebarActionTargetMode.FixedTrack => WaveeActionTargetModes.FixedTrack,
        SidebarActionTargetMode.NowPlaying => WaveeActionTargetModes.NowPlaying,
        SidebarActionTargetMode.ActiveRoute => WaveeActionTargetModes.ActiveRoute,
        _ => WaveeActionTargetModes.Nothing,
    };

    public static bool Accepts(WaveeActionTargetModes accepted, SidebarActionTargetMode mode)
    {
        var bit = Bit(mode);
        return bit != WaveeActionTargetModes.Nothing && (accepted & bit) != 0;
    }

    /// <summary>Resolve a binding's target. <paramref name="accepted"/> is checked FIRST: a mode the descriptor does not
    /// accept is <see cref="WaveeActionUnavailable.ModeNotSupported"/> even when the app state would have satisfied it,
    /// so narrowing a descriptor's accepted set can never widen what an old binding does.</summary>
    public static WaveeActionTargetResolution Resolve(SidebarActionTargetMode mode, string? targetKey,
        WaveeActionTargetModes accepted, in WaveeActionHostState host)
    {
        if (!Accepts(accepted, mode))
            return Fail(mode, WaveeActionUnavailable.ModeNotSupported);

        switch (mode)
        {
            case SidebarActionTargetMode.None:
                return new WaveeActionTargetResolution(mode, "", null, null, WaveeActionUnavailable.None);

            case SidebarActionTargetMode.FixedEntity:
            {
                if (string.IsNullOrEmpty(targetKey)) return Fail(mode, WaveeActionUnavailable.MissingTargetKey);
                // A stored TargetKey may be EITHER an entity uri ("spotify:album:…") or the pin/route id form
                // ("album:spotify:album:…"). Normalize both, so a key captured from a menu and a key captured from a
                // sidebar row resolve to the same target (the SidebarPinId.FromUri/FromRoute pair is the one authority).
                if (SidebarPinId.FromUri(targetKey) is { } routeFromUri)
                    return new WaveeActionTargetResolution(mode, targetKey!, routeFromUri, null,
                        WaveeActionUnavailable.None, host.FixedTargetName);
                if (SidebarPinId.FromRoute(targetKey) is { } routeKey)
                    return new WaveeActionTargetResolution(mode, SidebarPinId.UriOf(routeKey), routeKey, null,
                        WaveeActionUnavailable.None, host.FixedTargetName);
                // An unrecognized key is still handed through as a bare uri: a future/third-party entity scheme must
                // not be silently unbindable just because the pin-id scheme does not know it.
                return new WaveeActionTargetResolution(mode, targetKey!, null, null,
                    WaveeActionUnavailable.None, host.FixedTargetName);
            }

            case SidebarActionTargetMode.FixedTrack:
                // Tracks have no route and are never pinnable (locked decision 4) — the uri IS the whole target.
                return string.IsNullOrEmpty(targetKey)
                    ? Fail(mode, WaveeActionUnavailable.MissingTargetKey)
                    : new WaveeActionTargetResolution(mode, targetKey!, null, null,
                        WaveeActionUnavailable.None, host.FixedTargetName);

            case SidebarActionTargetMode.NowPlaying:
                if (string.IsNullOrEmpty(host.NowPlayingTrackUri))
                    return Fail(mode, WaveeActionUnavailable.NoNowPlaying);
                return new WaveeActionTargetResolution(mode, host.NowPlayingTrackUri!,
                    SidebarPinId.FromUri(host.NowPlayingContextUri), host.NowPlayingContextUri,
                    WaveeActionUnavailable.None, host.NowPlayingTrackName);

            case SidebarActionTargetMode.ActiveRoute:
                if (string.IsNullOrEmpty(host.ActiveRouteKey))
                    return Fail(mode, WaveeActionUnavailable.NoActiveRoute);
                // The route key IS the pin id for every navigable kind (F.5.4), so the entity behind it (when there is
                // one) comes straight back out of the scheme.
                return new WaveeActionTargetResolution(mode, SidebarPinId.UriOf(host.ActiveRouteKey!),
                    host.ActiveRouteKey, null, WaveeActionUnavailable.None, host.ActiveRouteName);

            default:
                return Fail(mode, WaveeActionUnavailable.ModeNotSupported);
        }
    }

    /// <summary>Binding overload — the form every call site actually uses.</summary>
    public static WaveeActionTargetResolution Resolve(SidebarActionBinding binding, WaveeActionTargetModes accepted,
        in WaveeActionHostState host)
        => Resolve(binding.TargetMode, binding.TargetKey, accepted, in host);

    /// <summary>An unavailability produced OUTSIDE the target matrix (a missing descriptor, a refused confirmation
    /// surface, a descriptor's own enablement veto), shaped as a resolution so one struct carries every disabled
    /// reason a row can show.</summary>
    public static WaveeActionTargetResolution Unavailable(SidebarActionTargetMode mode, WaveeActionUnavailable reason)
        => Fail(mode, reason);

    static WaveeActionTargetResolution Fail(SidebarActionTargetMode mode, WaveeActionUnavailable reason)
        => new(mode, "", null, null, reason);
}
