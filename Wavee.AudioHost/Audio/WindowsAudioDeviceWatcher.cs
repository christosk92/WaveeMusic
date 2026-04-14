using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Wavee.AudioHost.Audio;

/// <summary>
/// Subscribes to Windows CoreAudio (MMDevice) endpoint notifications and raises
/// <see cref="DevicesChanged"/> whenever an audio endpoint is added, removed, or
/// changes state. This is the same mechanism the Windows Sound settings app uses
/// to update its device list in real time.
/// </summary>
/// <remarks>
/// Events arrive on a COM-managed worker thread; callers should marshal any real
/// work off that thread and should debounce bursts of events that typically fire
/// together during a Bluetooth connect (added, state-changed, default-changed,
/// property-changed — often within the same millisecond).
/// </remarks>
internal sealed class WindowsAudioDeviceWatcher : IDisposable
{
    /// <summary>Raised when any audio endpoint is added, removed, or changes state.</summary>
    public event Action? DevicesChanged;

    /// <summary>
    /// Raised when Windows changes the system default output device
    /// (e.g. Bluetooth headphones connected and Windows auto-selected them).
    /// Distinct from <see cref="DevicesChanged"/> so callers can decide whether to
    /// just refresh the device list or also follow the new default.
    /// </summary>
    public event Action? DefaultOutputDeviceChanged;

    private readonly ILogger? _logger;
    private IMMDeviceEnumerator? _enumerator;
    private NotificationClient? _client;
    private bool _disposed;

    public WindowsAudioDeviceWatcher(ILogger? logger = null)
    {
        _logger = logger;
        if (!OperatingSystem.IsWindows())
            return;
        TryInitialize();
    }

    [UnconditionalSuppressMessage("Trimming", "IL2050",
        Justification = "COM interop with Windows CoreAudio — types are defined in-assembly.")]
    private void TryInitialize()
    {
        try
        {
            var clsid = ClsidMMDeviceEnumerator;
            var iid = typeof(IMMDeviceEnumerator).GUID;
            var hr = CoCreateInstance(
                ref clsid,
                IntPtr.Zero,
                ClsCtxInprocServer,
                ref iid,
                out var instance);
            if (hr < 0 || instance == null)
            {
                _logger?.LogWarning("WindowsAudioDeviceWatcher: CoCreateInstance failed (hr=0x{Hr:X8})", hr);
                return;
            }

            _enumerator = (IMMDeviceEnumerator)instance;
            _client = new NotificationClient(RaiseChanged, RaiseDefaultDeviceChanged, _logger);
            var regHr = _enumerator.RegisterEndpointNotificationCallback(_client);
            if (regHr < 0)
            {
                _logger?.LogWarning("WindowsAudioDeviceWatcher: RegisterEndpointNotificationCallback failed (hr=0x{Hr:X8})", regHr);
                _client = null;
                _enumerator = null;
                return;
            }

            _logger?.LogDebug("WindowsAudioDeviceWatcher: registered for CoreAudio endpoint notifications");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "WindowsAudioDeviceWatcher: failed to initialize");
            _enumerator = null;
            _client = null;
        }
    }

    private void RaiseChanged()
    {
        try
        {
            DevicesChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "WindowsAudioDeviceWatcher: DevicesChanged subscriber threw");
        }
    }

    private void RaiseDefaultDeviceChanged()
    {
        try
        {
            DefaultOutputDeviceChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "WindowsAudioDeviceWatcher: DefaultOutputDeviceChanged subscriber threw");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_enumerator != null && _client != null)
                _enumerator.UnregisterEndpointNotificationCallback(_client);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "WindowsAudioDeviceWatcher: unregister failed");
        }
        finally
        {
            if (_enumerator != null)
                Marshal.ReleaseComObject(_enumerator);
            _enumerator = null;
            _client = null;
        }
    }

    // ── Native COM interop ──

    private static readonly Guid ClsidMMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private const int ClsCtxInprocServer = 0x1;

    [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int CoCreateInstance(
        ref Guid rclsid,
        IntPtr pUnkOuter,
        int dwClsContext,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object? ppv);

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IntPtr ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IntPtr ppEndpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IntPtr ppDevice);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient pClient);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient pClient);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public int pid;
    }

    [ComImport]
    [Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, int dwNewState);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? pwstrDefaultDeviceId);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, PropertyKey key);
    }

    private sealed class NotificationClient : IMMNotificationClient
    {
        private readonly Action _onChange;
        private readonly Action _onDefaultChanged;
        private readonly ILogger? _logger;

        public NotificationClient(Action onChange, Action onDefaultChanged, ILogger? logger)
        {
            _onChange = onChange;
            _onDefaultChanged = onDefaultChanged;
            _logger = logger;
        }

        public int OnDeviceStateChanged(string pwstrDeviceId, int dwNewState)
        {
            var stateStr = dwNewState switch
            {
                1 => "Active",
                2 => "Disabled",
                4 => "NotPresent",
                8 => "Unplugged",
                _ => $"0x{dwNewState:X}"
            };
            _logger?.LogInformation("[MMDevice] Device state changed → {State}: id={Id}", stateStr, pwstrDeviceId);
            _onChange();
            return 0;
        }

        public int OnDeviceAdded(string pwstrDeviceId)
        {
            _logger?.LogInformation("[MMDevice] Device added: id={Id}", pwstrDeviceId);
            _onChange();
            return 0;
        }

        public int OnDeviceRemoved(string pwstrDeviceId)
        {
            _logger?.LogInformation("[MMDevice] Device removed: id={Id}", pwstrDeviceId);
            _onChange();
            return 0;
        }

        public int OnDefaultDeviceChanged(int flow, int role, string? pwstrDefaultDeviceId)
        {
            // flow=0 is eRender (output), role=0 is eConsole (default multimedia output).
            // We only care about the render/console change — that's what affects playback.
            if (flow == 0 && role == 0)
            {
                _logger?.LogInformation(
                    "[MMDevice] Default OUTPUT device changed (eRender/eConsole): id={Id} — will follow new default",
                    pwstrDefaultDeviceId ?? "<null>");
                _onDefaultChanged();
            }
            else
            {
                var flowStr = flow == 0 ? "eRender" : flow == 1 ? "eCapture" : $"flow={flow}";
                var roleStr = role == 0 ? "eConsole" : role == 1 ? "eMultimedia" : role == 2 ? "eCommunications" : $"role={role}";
                _logger?.LogDebug(
                    "[MMDevice] Default device changed ({Flow}/{Role}): id={Id} — not output/console, ignoring for auto-switch",
                    flowStr, roleStr, pwstrDefaultDeviceId ?? "<null>");
            }
            _onChange();
            return 0;
        }

        public int OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
        {
            // Property changes (volume, name, icon etc.) don't affect our device list — ignore.
            _logger?.LogTrace("[MMDevice] Property changed on {Id} (fmtid={Fmtid}, pid={Pid}) — ignored",
                pwstrDeviceId, key.fmtid, key.pid);
            return 0;
        }
    }
}
