using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Signals;
using Wavee.Backend;
using Wavee.SpotifyLive.Audio;

namespace Wavee;

/// <summary>The "This computer" output kind (folded from <see cref="AudioEndpointFormFactor"/>) → drives the picker glyph.</summary>
public enum LocalAudioDeviceKind { Speakers, Headphones, Headset, Hdmi, Spdif, LineLevel, Remote, Unknown }

/// <summary>A local (this-computer) render endpoint shown in the device picker's "This computer" section.</summary>
public sealed record LocalAudioDevice(string Id, string Name, LocalAudioDeviceKind Kind, bool IsDefault, bool IsCurrent);

/// <summary>Main-process picker service (plan §C1): owns its OWN device monitor, keeps a live roster, and routes the chosen
/// output through the audio host (+ transfers Connect playback home when a remote device is active). Constructed only when
/// the audio stack exists (the ctor deps are all REQUIRED — no nullable-with-silent-default); attached to the bridge via
/// <see cref="PlaybackBridge.AttachLocalOutputs"/> (null there is the legitimate no-audio backend).</summary>
public sealed class LocalAudioDeviceService : IDisposable
{
    readonly IAudioDeviceMonitor _monitor;
    readonly IAudioOutputDeviceControl _output;
    readonly Func<string, CancellationToken, Task> _transferHome;   // controller.TransferToAsync(us)
    readonly string _ourDeviceId;
    readonly Func<string?> _activeConnectDeviceId;
    readonly Action<string?, string?> _persist;                     // (deviceId, deviceName) → settings
    readonly Wavee.Backend.TrailingCoalescer _refresh;
    Action<Action>? _post;
    bool _disposed;

    public Signal<IReadOnlyList<LocalAudioDevice>> Devices { get; } = new(Array.Empty<LocalAudioDevice>());
    public Signal<string?> SelectedOutputId { get; }   // null = system default

    internal LocalAudioDeviceService(
        IAudioDeviceMonitor monitor,
        IAudioOutputDeviceControl output,
        Func<string, CancellationToken, Task> transferHome,
        string ourDeviceId,
        Func<string?> activeConnectDeviceId,
        Action<string?, string?> persist,
        string? initialDeviceId = null,
        int refreshDebounceMs = 300,
        Func<int, CancellationToken, Task>? refreshDelay = null,
        Func<long>? clock = null)
    {
        _monitor = monitor;
        _output = output;
        _transferHome = transferHome;
        _ourDeviceId = ourDeviceId;
        _activeConnectDeviceId = activeConnectDeviceId;
        _persist = persist;
        SelectedOutputId = new Signal<string?>(string.IsNullOrEmpty(initialDeviceId) ? null : initialDeviceId);
        _refresh = new Wavee.Backend.TrailingCoalescer(refreshDebounceMs, clock, refreshDelay);
    }

    /// <summary>Start monitoring + do the first enumeration, marshalling roster updates onto the UI thread.</summary>
    public void Activate(Action<Action> post)
    {
        _post = post;
        _monitor.Changed += OnMonitorChanged;
        RefreshNow();
    }

    void OnMonitorChanged(AudioDeviceEvent _) => _refresh.Post(RefreshNow);

    void RefreshNow()
    {
        var list = Enumerate();
        var post = _post;
        if (post is null) { Devices.Value = list; return; }
        post(() => Devices.Value = list);
    }

    IReadOnlyList<LocalAudioDevice> Enumerate()
    {
        string? selected = SelectedOutputId.Value;
        var eps = FilterForPicker(_monitor.EnumerateRenderEndpoints());
        var result = new List<LocalAudioDevice>(eps.Count);
        foreach (var e in eps)
            result.Add(new LocalAudioDevice(e.Id, e.Name, Fold(e.FormFactor), e.IsDefault,
                IsCurrent: selected is not null && string.Equals(e.Id, selected, StringComparison.OrdinalIgnoreCase)));
        return result;
    }

    /// <summary>Picker roster policy (pure, unit-tested): hide display-audio sinks — a monitor advertises an HDMI/DP audio
    /// endpoint whether or not it has speakers, so they read as dead outputs (still reachable via "System default" if
    /// Windows routes there). Duplicate short names ("Speakers" x2 adapters) fall back to the full FriendlyName.</summary>
    internal static List<AudioEndpointInfo> FilterForPicker(IReadOnlyList<AudioEndpointInfo> eps)
    {
        var visible = new List<AudioEndpointInfo>(eps.Count);
        foreach (var e in eps)
            if (e.FormFactor != AudioEndpointFormFactor.DigitalAudioDisplayDevice) visible.Add(e);
        for (int i = 0; i < visible.Count; i++)
        {
            for (int j = i + 1; j < visible.Count; j++)
            {
                if (!string.Equals(visible[i].Name, visible[j].Name, StringComparison.OrdinalIgnoreCase)) continue;
                if (visible[i].FullName is { Length: > 0 } fi) visible[i] = visible[i] with { Name = fi };
                if (visible[j].FullName is { Length: > 0 } fj) visible[j] = visible[j] with { Name = fj };
            }
        }
        return visible;
    }

    /// <summary>THE picker intent: persist + route the chosen output FIRST, then (if a remote Connect device owns playback)
    /// transfer playback home so the first local audio lands on the just-routed endpoint. Fire-and-forget from the UI.</summary>
    public async Task SelectAsync(string? deviceId)
    {
        deviceId = string.IsNullOrEmpty(deviceId) ? null : deviceId;
        string name = "";
        foreach (var d in Devices.Value) if (string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase)) { name = d.Name; break; }
        _persist(deviceId, name);
        SelectedOutputId.Value = deviceId;
        _output.SetOutputDevice(deviceId);          // route FIRST
        var active = _activeConnectDeviceId();
        if (!string.IsNullOrEmpty(active) && !string.Equals(active, _ourDeviceId, StringComparison.OrdinalIgnoreCase))
            await _transferHome(_ourDeviceId, CancellationToken.None).ConfigureAwait(false);   // then transfer home (ghost-resume locally)
    }

    /// <summary>The friendly name of the endpoint we are currently rendering to — the explicitly selected one, else the
    /// system default. Feeds Connect's <c>DeviceInfo.audio_output_device_info.device_name</c> (desktop parity). Returns null
    /// before the first enumeration, or when the selected id is no longer in the roster.</summary>
    public string? CurrentOutputDeviceName()
    {
        string? selected = SelectedOutputId.Value;
        var devices = Devices.Value;
        foreach (var d in devices)
            if (selected is null ? d.IsDefault : string.Equals(d.Id, selected, StringComparison.OrdinalIgnoreCase))
                return d.Name;
        return null;
    }

    /// <summary>Mute/unmute the Windows session (Phase B4). Our own set is filtered by the engine's context guard, so the
    /// caller updates its optimistic UI directly.</summary>
    public void SetMuted(bool muted) => _output.SetOutputMuted(muted);

    static LocalAudioDeviceKind Fold(AudioEndpointFormFactor f) => f switch
    {
        AudioEndpointFormFactor.Speakers => LocalAudioDeviceKind.Speakers,
        AudioEndpointFormFactor.Headphones => LocalAudioDeviceKind.Headphones,
        AudioEndpointFormFactor.Headset => LocalAudioDeviceKind.Headset,
        AudioEndpointFormFactor.DigitalAudioDisplayDevice => LocalAudioDeviceKind.Hdmi,
        AudioEndpointFormFactor.Spdif => LocalAudioDeviceKind.Spdif,
        AudioEndpointFormFactor.LineLevel => LocalAudioDeviceKind.LineLevel,
        AudioEndpointFormFactor.RemoteNetworkDevice => LocalAudioDeviceKind.Remote,
        _ => LocalAudioDeviceKind.Unknown,
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _monitor.Changed -= OnMonitorChanged;
        _refresh.Dispose();
        _monitor.Dispose();
    }
}
