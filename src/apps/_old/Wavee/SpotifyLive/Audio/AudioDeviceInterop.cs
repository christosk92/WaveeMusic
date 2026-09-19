using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Wavee.SpotifyLive.Audio;

// ── WASAPI device-change + session-volume interop (cold-path source-gen COM, AOT-clean; single-assembly hierarchy per
//    design/subsystems/com-interop.md FGCOM0007). Lives next to WasapiRenderer and reuses its IMMDeviceEnumerator /
//    IMMDevice declarations. ALL callback string params are IntPtr + Marshal.PtrToStringUni (OnDefaultDeviceChanged can
//    receive a NULL LPWSTR when the last render device disappears). Out-strings (GetId/GetValue) are CoTaskMem → freed. ──

/// <summary>Thrown by the renderer when a WASAPI call returns a device-loss HRESULT (e.g.
/// <c>AUDCLNT_E_DEVICE_INVALIDATED</c>). Carries the raw hr so the engine can route it to the device state machine.</summary>
internal sealed class AudioDeviceInvalidatedException(int hr) : Exception($"audio device invalidated: 0x{hr:X8}")
{
    public int Hr { get; } = hr;
}

internal static class AudioDeviceConstants
{
    public const int EDataFlowRender = 0;      // eRender
    public const int ERoleConsole = 0;         // eConsole
    public const uint DEVICE_STATE_ACTIVE = 0x1;
    public const uint DEVICE_STATE_DISABLED = 0x2;
    public const uint DEVICE_STATE_NOTPRESENT = 0x4;
    public const uint DEVICE_STATE_UNPLUGGED = 0x8;
    public const uint STGM_READ = 0x0;
    public const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);

    public const ushort VT_UI4 = 19;
    public const ushort VT_LPWSTR = 31;

    public const int DisconnectReasonDeviceRemoval = 2;   // AudioSessionDisconnectReason

    public static readonly Guid IID_IAudioSessionControl = new("F4B1A599-7266-4319-A8CA-E70ACB11E8CD");
    public static readonly Guid IID_ISimpleAudioVolume = new("87CE5498-68D6-44E5-9215-6DA47EF883D8");

    // PKEY_Device_FriendlyName = {A45C254E-DF1C-4EFD-8020-67D146A850E0},14  (VT_LPWSTR)
    public static readonly PropertyKey PKEY_Device_FriendlyName =
        new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
    // PKEY_Device_DeviceDesc = {A45C254E-DF1C-4EFD-8020-67D146A850E0},2  (VT_LPWSTR) — the short endpoint label
    // ("Speakers", "LC34G55T"); FriendlyName is "{DeviceDesc} ({adapter})" and explodes the picker width.
    public static readonly PropertyKey PKEY_Device_DeviceDesc =
        new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 2);
    // PKEY_AudioEndpoint_FormFactor = {1DA5D803-D492-4EDD-8C23-E0C0FFEE7F0E},0  (VT_UI4)
    public static readonly PropertyKey PKEY_AudioEndpoint_FormFactor =
        new(new Guid("1DA5D803-D492-4EDD-8C23-E0C0FFEE7F0E"), 0);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey(Guid fmtId, uint pid)
{
    public Guid FmtId = fmtId;
    public uint Pid = pid;
}

/// <summary>Blittable PROPVARIANT (WORD vt + 3 reserved WORDs + an 8-byte-aligned union modelled as two IntPtrs). Only
/// VT_LPWSTR / VT_UI4 are read; anything else falls back to defaults. Always PropVariantClear'd after a read.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    public ushort Vt;
    public ushort R1;
    public ushort R2;
    public ushort R3;
    public IntPtr P1;   // pwszVal (VT_LPWSTR) or the low bytes hold ulVal (VT_UI4)
    public IntPtr P2;
}

// ── COM interfaces (exact vtable order) ──────────────────────────────────────────────────────────────────────────────

[GeneratedComInterface, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
internal partial interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Item(uint index, out IMMDevice device);
}

[GeneratedComInterface, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
internal partial interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint props);
    [PreserveSig] int GetAt(uint prop, out PropertyKey key);
    [PreserveSig] int GetValue(in PropertyKey key, out PropVariant value);
    [PreserveSig] int SetValue(in PropertyKey key, in PropVariant value);
    [PreserveSig] int Commit();
}

[GeneratedComInterface, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
internal partial interface ISimpleAudioVolume
{
    [PreserveSig] int SetMasterVolume(float level, in Guid eventContext);
    [PreserveSig] int GetMasterVolume(out float level);
    [PreserveSig] int SetMute(int mute, in Guid eventContext);
    [PreserveSig] int GetMute(out int mute);
}

/// <summary>Per-render-stream gain. Unlike ISimpleAudioVolume this does not alter Wavee's shared Windows session volume,
/// so two prepared WASAPI streams can run an equal-power hand-off without exposing a second user volume control.</summary>
[GeneratedComInterface, Guid("93014887-242D-4068-8A15-CF5E93B90FE3")]
internal partial interface IAudioStreamVolume
{
    [PreserveSig] int GetChannelCount(out uint channelCount);
    [PreserveSig] int SetChannelVolume(uint channelIndex, float level);
    [PreserveSig] int GetChannelVolume(uint channelIndex, out float level);
    [PreserveSig] int SetAllVolumes(uint channelCount, IntPtr levels);
    [PreserveSig] int GetAllVolumes(uint channelCount, IntPtr levels);
}

[GeneratedComInterface, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
internal partial interface IAudioSessionControl
{
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetDisplayName(out IntPtr name);                 // CoTaskMem LPWSTR
    [PreserveSig] int SetDisplayName(IntPtr value, in Guid ctx);
    [PreserveSig] int GetIconPath(out IntPtr path);
    [PreserveSig] int SetIconPath(IntPtr value, in Guid ctx);
    [PreserveSig] int GetGroupingParam(out Guid param);
    [PreserveSig] int SetGroupingParam(in Guid param, in Guid ctx);
    [PreserveSig] int RegisterAudioSessionNotification(IntPtr events); // IAudioSessionEvents* for the sink CCW
    [PreserveSig] int UnregisterAudioSessionNotification(IntPtr events);
}

// ── Callback interfaces + sinks ──────────────────────────────────────────────────────────────────────────────────────

[GeneratedComInterface, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
internal partial interface IMMNotificationClient
{
    [PreserveSig] int OnDeviceStateChanged(IntPtr deviceId, uint newState);
    [PreserveSig] int OnDeviceAdded(IntPtr deviceId);
    [PreserveSig] int OnDeviceRemoved(IntPtr deviceId);
    [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, IntPtr defaultDeviceId);   // defaultDeviceId may be NULL
    [PreserveSig] int OnPropertyValueChanged(IntPtr deviceId, PropertyKey key);
}

[GeneratedComInterface, Guid("24918ACC-64B3-37C1-8CA9-74A66E9957A8")]
internal partial interface IAudioSessionEvents
{
    [PreserveSig] int OnDisplayNameChanged(IntPtr name, in Guid ctx);
    [PreserveSig] int OnIconPathChanged(IntPtr path, in Guid ctx);
    [PreserveSig] int OnSimpleVolumeChanged(float newVolume, int newMute, in Guid eventContext);
    [PreserveSig] int OnChannelVolumeChanged(uint count, IntPtr volumes, uint changed, in Guid ctx);
    [PreserveSig] int OnGroupingParamChanged(in Guid param, in Guid ctx);
    [PreserveSig] int OnStateChanged(int state);
    [PreserveSig] int OnSessionDisconnected(int reason);
}

/// <summary>Sink for endpoint-topology notifications. Forwards default-render changes only for eRender/eConsole (kills the
/// per-role×3 duplicate); state/add/remove raw. Swallows all exceptions and returns S_OK; NEVER touches renderer COM.</summary>
[GeneratedComClass]
internal sealed partial class MmNotificationClientSink(Action<AudioDeviceEvent> onEvent) : IMMNotificationClient
{
    public int OnDeviceStateChanged(IntPtr deviceId, uint newState)
    {
        try { onEvent(new AudioDeviceEvent(AudioDeviceEventKind.DeviceStateChanged, Marshal.PtrToStringUni(deviceId), newState)); } catch { }
        return 0;
    }

    public int OnDeviceAdded(IntPtr deviceId)
    {
        try { onEvent(new AudioDeviceEvent(AudioDeviceEventKind.DeviceAdded, Marshal.PtrToStringUni(deviceId), 0)); } catch { }
        return 0;
    }

    public int OnDeviceRemoved(IntPtr deviceId)
    {
        try { onEvent(new AudioDeviceEvent(AudioDeviceEventKind.DeviceRemoved, Marshal.PtrToStringUni(deviceId), 0)); } catch { }
        return 0;
    }

    public int OnDefaultDeviceChanged(int flow, int role, IntPtr defaultDeviceId)
    {
        try
        {
            if (flow == AudioDeviceConstants.EDataFlowRender && role == AudioDeviceConstants.ERoleConsole)
                onEvent(new AudioDeviceEvent(AudioDeviceEventKind.DefaultRenderChanged, Marshal.PtrToStringUni(defaultDeviceId), 0));
        }
        catch { }
        return 0;
    }

    public int OnPropertyValueChanged(IntPtr deviceId, PropertyKey key) => 0;
}

/// <summary>Sink for the current WASAPI session (per-Init). <see cref="OnSimpleVolumeChanged"/> filters self-originated
/// echoes by event context (our sets carry our context GUID). All callbacks swallow exceptions and return S_OK; delivered
/// inline on the OS thread — consumers hop.</summary>
[GeneratedComClass]
internal sealed partial class AudioSessionEventsSink(
    Guid ownContext,
    Action<double, bool> onExternalVolume,
    Action onDisconnected) : IAudioSessionEvents
{
    public int OnDisplayNameChanged(IntPtr name, in Guid ctx) => 0;
    public int OnIconPathChanged(IntPtr path, in Guid ctx) => 0;

    public int OnSimpleVolumeChanged(float newVolume, int newMute, in Guid eventContext)
    {
        try
        {
            if (Wavee.Backend.Audio.SessionVolumeSync.TryReadExternal(eventContext, ownContext, newVolume, newMute, out double slider, out bool muted))
                onExternalVolume(slider, muted);
        }
        catch { }
        return 0;
    }

    public int OnChannelVolumeChanged(uint count, IntPtr volumes, uint changed, in Guid ctx) => 0;
    public int OnGroupingParamChanged(in Guid param, in Guid ctx) => 0;
    public int OnStateChanged(int state) => 0;

    public int OnSessionDisconnected(int reason)
    {
        try { if (reason == AudioDeviceConstants.DisconnectReasonDeviceRemoval) onDisconnected(); } catch { }
        return 0;
    }
}

// ── The real device monitor ──────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Live WASAPI render-endpoint monitor (plan §A2). Creates the MMDeviceEnumerator once, registers an
/// <see cref="IMMNotificationClient"/> sink, and enumerates active render endpoints with friendly name + form factor.</summary>
internal sealed unsafe partial class WasapiAudioDeviceMonitor : IAudioDeviceMonitor
{
    static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    static readonly Guid IID_IMMNotificationClient = new("7991EEC9-7E89-4D85-8390-6C703CEC60C0");
    const int CLSCTX_ALL = 23;

    static readonly StrategyBasedComWrappers ComWrappers = new();

    readonly object _lock = new();
    readonly WaveeLogger _log;
    IMMDeviceEnumerator? _enumerator;
    MmNotificationClientSink? _sink;
    IntPtr _sinkInterface;
    bool _registered;
    bool _disposed;

    public event Action<AudioDeviceEvent>? Changed;

    public WasapiAudioDeviceMonitor(WaveeLogger log = default)
    {
        _log = log;
        try
        {
            CoInitializeEx(IntPtr.Zero, 0);   // defensive on the creating thread (S_FALSE/RPC_E_CHANGED_MODE tolerated)
            Guid clsid = CLSID_MMDeviceEnumerator, iid = IID_IMMDeviceEnumerator;
            if (CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_ALL, ref iid, out IntPtr pEnum) >= 0 && pEnum != IntPtr.Zero)
            {
                _enumerator = (IMMDeviceEnumerator)ComWrappers.GetOrCreateObjectForComInstance(pEnum, CreateObjectFlags.None);
                Marshal.Release(pEnum);
                Register();
            }
        }
        catch (Exception ex) { _log.Info("audio.device monitor init failed: " + ex.Message); }
    }

    void Register()
    {
        if (_enumerator is null) return;
        var sink = new MmNotificationClientSink(RaiseChanged);
        IntPtr unknown = ComWrappers.GetOrCreateComInterfaceForObject(sink, CreateComInterfaceFlags.None);
        IntPtr notificationClient = IntPtr.Zero;
        try
        {
            Guid iid = IID_IMMNotificationClient;
            int hr = Marshal.QueryInterface(unknown, in iid, out notificationClient);
            if (hr < 0 || notificationClient == IntPtr.Zero)
            {
                _log.Info($"audio.device IMMNotificationClient QueryInterface failed: 0x{hr:X8}");
                return;
            }

            if (_enumerator.RegisterEndpointNotificationCallback(notificationClient) >= 0)
            {
                _sink = sink;
                _sinkInterface = notificationClient;
                notificationClient = IntPtr.Zero; // retained until UnregisterEndpointNotificationCallback
                _registered = true;
            }
            else
            {
                _log.Info("audio.device RegisterEndpointNotificationCallback failed");
            }
        }
        finally
        {
            Marshal.Release(unknown);
            if (notificationClient != IntPtr.Zero) Marshal.Release(notificationClient);
        }
    }

    void RaiseChanged(AudioDeviceEvent e) { try { Changed?.Invoke(e); } catch { } }

    public IReadOnlyList<AudioEndpointInfo> EnumerateRenderEndpoints()
    {
        var result = new List<AudioEndpointInfo>();
        lock (_lock)
        {
            if (_enumerator is null || _disposed) return result;
            string? defaultId = GetDefaultRenderIdLocked();
            try
            {
                if (_enumerator.EnumAudioEndpoints(AudioDeviceConstants.EDataFlowRender, AudioDeviceConstants.DEVICE_STATE_ACTIVE, out IntPtr pColl) < 0 || pColl == IntPtr.Zero)
                    return result;
                var coll = (IMMDeviceCollection)ComWrappers.GetOrCreateObjectForComInstance(pColl, CreateObjectFlags.None);
                Marshal.Release(pColl);
                if (coll.GetCount(out uint count) < 0) return result;
                for (uint i = 0; i < count; i++)
                {
                    if (coll.Item(i, out IMMDevice dev) < 0 || dev is null) continue;
                    var info = ReadEndpoint(dev, defaultId);
                    if (info is { } e) result.Add(e);
                }
            }
            catch (Exception ex) { _log.Info("audio.device enumerate failed: " + ex.Message); }
        }
        return result;
    }

    AudioEndpointInfo? ReadEndpoint(IMMDevice dev, string? defaultId)
    {
        string id = "";
        if (dev.GetId(out IntPtr pId) >= 0 && pId != IntPtr.Zero)
        {
            id = Marshal.PtrToStringUni(pId) ?? "";
            Marshal.FreeCoTaskMem(pId);
        }
        if (id.Length == 0) return null;
        string name = "Audio device";
        string? full = null;
        var form = AudioEndpointFormFactor.Unknown;
        try
        {
            if (dev.OpenPropertyStore(AudioDeviceConstants.STGM_READ, out IntPtr pStore) >= 0 && pStore != IntPtr.Zero)
            {
                var store = (IPropertyStore)ComWrappers.GetOrCreateObjectForComInstance(pStore, CreateObjectFlags.None);
                Marshal.Release(pStore);
                string? desc = TryReadString(store, AudioDeviceConstants.PKEY_Device_DeviceDesc);
                full = TryReadString(store, AudioDeviceConstants.PKEY_Device_FriendlyName);
                if (AudioDeviceNaming.Shorten(desc, full) is { Length: > 0 } n) name = n;
                if (TryReadUInt(store, AudioDeviceConstants.PKEY_AudioEndpoint_FormFactor) is { } ff && ff <= (uint)AudioEndpointFormFactor.Unknown)
                    form = (AudioEndpointFormFactor)ff;
            }
        }
        catch { }
        return new AudioEndpointInfo(id, name, form, string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase), full);
    }

    static string? TryReadString(IPropertyStore store, in PropertyKey key)
    {
        if (store.GetValue(key, out PropVariant pv) < 0) return null;
        string? s = pv.Vt == AudioDeviceConstants.VT_LPWSTR && pv.P1 != IntPtr.Zero ? Marshal.PtrToStringUni(pv.P1) : null;
        PropVariantClear(ref pv);
        return s;
    }

    static uint? TryReadUInt(IPropertyStore store, in PropertyKey key)
    {
        if (store.GetValue(key, out PropVariant pv) < 0) return null;
        uint? v = pv.Vt == AudioDeviceConstants.VT_UI4 ? (uint)(pv.P1.ToInt64() & 0xFFFFFFFF) : null;
        PropVariantClear(ref pv);
        return v;
    }

    public string? GetDefaultRenderId() { lock (_lock) return GetDefaultRenderIdLocked(); }

    string? GetDefaultRenderIdLocked()
    {
        if (_enumerator is null || _disposed) return null;
        try
        {
            if (_enumerator.GetDefaultAudioEndpoint(AudioDeviceConstants.EDataFlowRender, AudioDeviceConstants.ERoleConsole, out IMMDevice dev) < 0 || dev is null)
                return null;   // no active render device
            if (dev.GetId(out IntPtr pId) >= 0 && pId != IntPtr.Zero)
            {
                var id = Marshal.PtrToStringUni(pId);
                Marshal.FreeCoTaskMem(pId);
                return id;
            }
        }
        catch { }
        return null;
    }

    public string? GetFriendlyName(string deviceId)
    {
        lock (_lock)
        {
            if (_enumerator is null || _disposed || string.IsNullOrEmpty(deviceId)) return null;
            try
            {
                if (_enumerator.GetDevice(deviceId, out IMMDevice dev) < 0 || dev is null) return null;
                if (dev.OpenPropertyStore(AudioDeviceConstants.STGM_READ, out IntPtr pStore) >= 0 && pStore != IntPtr.Zero)
                {
                    var store = (IPropertyStore)ComWrappers.GetOrCreateObjectForComInstance(pStore, CreateObjectFlags.None);
                    Marshal.Release(pStore);
                    return AudioDeviceNaming.Shorten(
                        TryReadString(store, AudioDeviceConstants.PKEY_Device_DeviceDesc),
                        TryReadString(store, AudioDeviceConstants.PKEY_Device_FriendlyName));
                }
            }
            catch { }
            return null;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            try { if (_registered && _enumerator is not null && _sinkInterface != IntPtr.Zero) _enumerator.UnregisterEndpointNotificationCallback(_sinkInterface); } catch { }
            if (_sinkInterface != IntPtr.Zero) { Marshal.Release(_sinkInterface); _sinkInterface = IntPtr.Zero; }
            _registered = false;
            _enumerator = null;
            GC.KeepAlive(_sink);
            _sink = null;
        }
    }

    [System.Runtime.InteropServices.LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(IntPtr reserved, uint coInit);

    [System.Runtime.InteropServices.LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(ref Guid clsid, IntPtr outer, int clsContext, ref Guid iid, out IntPtr ppv);

    [System.Runtime.InteropServices.LibraryImport("ole32.dll")]
    private static partial int PropVariantClear(ref PropVariant pvar);
}
