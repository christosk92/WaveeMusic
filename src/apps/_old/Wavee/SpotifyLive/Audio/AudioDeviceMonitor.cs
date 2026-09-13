using System;
using System.Collections.Generic;

namespace Wavee.SpotifyLive.Audio;

// ── Device-monitor CONTRACT (COM-free) ───────────────────────────────────────────────────────────────────────────────
// This file is deliberately free of any COM / P/Invoke so the OutputDeviceRouter state machine and its unit tests can
// compile against the interface + POD without pulling the WASAPI interop (the real WasapiAudioDeviceMonitor lives in the
// COM-bearing AudioDeviceInterop.cs). See plan §A2/§A3 + §D1.

/// <summary>WASAPI endpoint form factor (mmdeviceapi EndpointFormFactor order). Folded to a glyph in the UI layer.</summary>
public enum AudioEndpointFormFactor
{
    RemoteNetworkDevice = 0,
    Speakers = 1,
    LineLevel = 2,
    Headphones = 3,
    Microphone = 4,
    Headset = 5,
    Handset = 6,
    UnknownDigitalPassthrough = 7,
    Spdif = 8,
    DigitalAudioDisplayDevice = 9,
    Unknown = 10,
}

/// <summary>One active render endpoint as enumerated from MMDevice: id, short display name (DeviceDesc-first — "Speakers",
/// not "Speakers (Qualcomm(R) Aqstic(TM) Audio Adapter Device)"), form factor, whether it is the system default eConsole
/// render device, and the full FriendlyName kept for disambiguating duplicate short names.</summary>
public readonly record struct AudioEndpointInfo(string Id, string Name, AudioEndpointFormFactor FormFactor, bool IsDefault, string? FullName = null);

/// <summary>Endpoint display-name policy (pure, COM-free, unit-tested): prefer the short DeviceDesc; else strip the
/// " (adapter)" suffix Windows appends to FriendlyName ("{DeviceDesc} ({adapter})"); else the raw FriendlyName.</summary>
public static class AudioDeviceNaming
{
    public static string? Shorten(string? deviceDesc, string? friendlyName)
    {
        if (!string.IsNullOrWhiteSpace(deviceDesc)) return deviceDesc.Trim();
        if (string.IsNullOrWhiteSpace(friendlyName)) return friendlyName;
        string fn = friendlyName.Trim();
        int open = fn.IndexOf(" (", StringComparison.Ordinal);
        return open > 0 && fn.EndsWith(")", StringComparison.Ordinal) ? fn[..open] : fn;
    }
}

/// <summary>The kinds of device-topology change the monitor forwards (default-render change already role-filtered at the
/// source to eRender/eConsole; state/add/remove are raw).</summary>
public enum AudioDeviceEventKind { DefaultRenderChanged, DeviceStateChanged, DeviceAdded, DeviceRemoved }

/// <summary>A single device-topology change. <paramref name="State"/> carries the DEVICE_STATE_* mask for
/// <see cref="AudioDeviceEventKind.DeviceStateChanged"/> (0 otherwise).</summary>
public readonly record struct AudioDeviceEvent(AudioDeviceEventKind Kind, string? DeviceId, uint State);

/// <summary>Watches the OS render-endpoint topology (IMMNotificationClient under the hood) and enumerates active render
/// endpoints. <see cref="Changed"/> fires INLINE on the OS callback thread — consumers must hop threads themselves and
/// must NEVER take a renderer lock from the handler (deadlock rule, plan risk 6).</summary>
internal interface IAudioDeviceMonitor : IDisposable
{
    /// <summary>Raised on every forwarded topology change. Invoked on the OS callback thread.</summary>
    event Action<AudioDeviceEvent>? Changed;
    /// <summary>The current ACTIVE (DEVICE_STATE_ACTIVE) render endpoints.</summary>
    IReadOnlyList<AudioEndpointInfo> EnumerateRenderEndpoints();
    /// <summary>The current default eConsole render endpoint id, or null when there is none.</summary>
    string? GetDefaultRenderId();
    /// <summary>Best-effort friendly name for a device id (the device may already be gone → null).</summary>
    string? GetFriendlyName(string deviceId);
}
