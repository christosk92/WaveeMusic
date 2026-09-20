using System;
using FluentGpu.WindowsApi.Power;
using Wavee.Core;
using Wavee.SpotifyLive;

namespace Wavee;

/// <summary>
/// Wavee's power-session policy: keep-awake while audio (or an active video placement — Docked, Floating, Detached or
/// Fullscreen) is playing, pause + flush on suspend, re-announce Connect on resume when reachable. Distinct from
/// <see cref="AmbientPowerPolicy"/> (render cadence).
/// </summary>
/// <remarks>
/// <b>KeepAwake is per-thread.</b> Acquire and dispose the handle on the UI thread only — <c>SetThreadExecutionState</c>
/// is a per-calling-thread flag. OS <see cref="PowerSession.Suspending"/> / <see cref="PowerSession.Resumed"/> fire on
/// a power-broadcast worker; every handler hops through the stored <c>post</c> before touching playback or KeepAwake.
/// Fail-soft: nothing thrown out of an OS callback.
/// </remarks>
static class PowerBridge
{
    static readonly object Gate = new();
    static PlaybackBridge? _bridge;
    static IPlaybackPlayer? _player;
    static Action<Action>? _post;
    static Services? _services;
    static IDisposable? _subscription;
    static IDisposable? _keepAwake;
    static bool _awake;
    static bool _keepDisplay;
    static bool _attached;

    /// <summary>Composition-root install. Idempotent. Call from <see cref="WaveeApp"/> after <c>PlaybackBridge.Activate</c>.</summary>
    public static void Attach(PlaybackBridge bridge, Action<Action> post, Services services)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(services);
        lock (Gate)
        {
            if (_attached) return;
            _attached = true;
            _bridge = bridge;
            _player = bridge.Player;
            _post = post;
            _services = services;
        }

        NetworkPolicy.Install(services.Settings, post);

        try
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(8, 0))
            {
                PowerSession.Suspending += OnSuspending;
                PowerSession.Resumed += OnResumed;
                _subscription = PowerSession.Subscribe();
            }
        }
        catch
        {
            // Registration failed — playback still works; we just will not see suspend/resume.
            _subscription = null;
        }

        try
        {
            bool playing = bridge.IsPlaying.Peek();
            ApplyKeepAwake(playing, playing && IsActiveVideo(bridge, subscribe: false));
        }
        catch { }
    }

    /// <summary>No-op when <see cref="Attach"/> already ran (the WaveeApp composition root). Parameterless fallback.</summary>
    public static void TryInstallFromContext() { }

    /// <summary>
    /// Edge-triggered keep-awake from <see cref="PlaybackBridge.IsPlaying"/> + <see cref="PlaybackBridge.VideoSurface"/>.
    /// Call from an auto-tracked <c>UseEffect</c> so signal reads subscribe the effect, not a component render.
    /// </summary>
    public static void SyncFromSignals()
    {
        var bridge = _bridge;
        if (bridge is null) return;
        try
        {
            bool playing = bridge.IsPlaying.Value;
            ApplyKeepAwake(playing, playing && IsActiveVideo(bridge, subscribe: true));
        }
        catch { }
    }

    /// <summary>Whether video is active in ANY placement right now — Docked, Floating, Detached or Fullscreen — via
    /// the single derived <see cref="PlacementCore.IsActive"/> truth. Previously gated on Fullscreen only, which
    /// meant a playing docked card or mini player let the display sleep mid-video; broadened as part of the
    /// docked-video work so keep-awake tracks every placement that actually shows a moving picture, not just the one
    /// that happens to fill the screen.</summary>
    static bool IsActiveVideo(PlaybackBridge bridge, bool subscribe)
    {
        var s = subscribe ? bridge.VideoSurface.Value : bridge.VideoSurface.Peek();
        return PlacementCore.IsActive(s);
    }

    static void ApplyKeepAwake(bool playing, bool keepDisplayOn)
    {
        if (!playing)
        {
            DropKeepAwake();
            return;
        }
        if (_awake && _keepDisplay == keepDisplayOn) return;
        DropKeepAwake();
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(8, 0)) return;
            _keepAwake = PowerSession.KeepAwake(keepDisplayOn);
            _awake = true;
            _keepDisplay = keepDisplayOn;
        }
        catch
        {
            _keepAwake = null;
            _awake = false;
            _keepDisplay = false;
        }
    }

    static void DropKeepAwake()
    {
        try { _keepAwake?.Dispose(); }
        catch { }
        _keepAwake = null;
        _awake = false;
        _keepDisplay = false;
    }

    static void OnSuspending()
    {
        try { _post?.Invoke(OnSuspendingUi); }
        catch { }
    }

    static void OnResumed()
    {
        try { _post?.Invoke(OnResumedUi); }
        catch { }
    }

    static void OnSuspendingUi()
    {
        try
        {
            DropKeepAwake();
            // Pause first so a later playback-snapshot writer sees Paused; then fsync the session document. Gated on
            // ShouldPauseOnSuspend (RouteLocal() && the local host's clock is valid, i.e. audible LOCAL media): a
            // sleeping laptop must pause the playback this machine is actually making sound with, never forward a
            // pause to a phone or speaker that happens to be the Connect-active device right now.
            if (_player is { ShouldPauseOnSuspend: true } player)
            {
                try { _ = player.PauseAsync(); }
                catch { }
            }
            SessionSnapshotStore.FlushActive();
        }
        catch { }
    }

    static void OnResumedUi()
    {
        try
        {
            var bridge = _bridge;
            if (bridge is not null)
            {
                bool playing = bridge.IsPlaying.Peek();
                ApplyKeepAwake(playing, playing && IsActiveVideo(bridge, subscribe: false));
            }
            ReannounceConnect();
        }
        catch { }
    }

    /// <summary>Re-announce this device to the Connect cluster after an OS resume (UI thread). A sleeping machine loses its
    /// device registration server-side even when the socket still looks alive, so without this the device quietly stops
    /// appearing in other clients' picker until some other publish happens to fire. No-op before login (no live host).</summary>
    public static void ReannounceConnect()
    {
        try { _services?.LiveHost?.Connect?.AnnounceNewConnection(); }
        catch (Exception) { /* a resume must never fault on a publish; the next real edge republishes */ }
    }

    public static void Shutdown()
    {
        try { PowerSession.Suspending -= OnSuspending; } catch { }
        try { PowerSession.Resumed -= OnResumed; } catch { }
        try { _subscription?.Dispose(); } catch { }
        _subscription = null;
        DropKeepAwake();
        NetworkPolicy.Shutdown();
        lock (Gate)
        {
            _attached = false;
            _bridge = null;
            _player = null;
            _post = null;
            _services = null;
        }
    }
}
