using System.Runtime.InteropServices.Marshalling;

namespace Wavee.SpotifyLive.Audio;

// Core Audio device-ENUMERATION COM interfaces (the output-device picker + follow-default watcher consume these). These
// formerly lived at the tail of WasapiRenderer.cs; that render path is gone (the FluentGpu.Media engine owns output now),
// but the enumeration interop is still needed by AudioDeviceInterop.cs / OutputDeviceRouter.cs / AudioDeviceMonitor.cs.
// COM vtable order MUST match the native interface exactly; unused leading methods are declared to preserve slots.

[GeneratedComInterface, System.Runtime.InteropServices.Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
internal partial interface IMMDeviceEnumerator
{
    [System.Runtime.InteropServices.PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out nint devices);
    [System.Runtime.InteropServices.PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    [System.Runtime.InteropServices.PreserveSig] int GetDevice([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [System.Runtime.InteropServices.PreserveSig] int RegisterEndpointNotificationCallback(nint client);
    [System.Runtime.InteropServices.PreserveSig] int UnregisterEndpointNotificationCallback(nint client);
}

[GeneratedComInterface, System.Runtime.InteropServices.Guid("D666063F-1587-4E43-81F1-B948E807363F")]
internal partial interface IMMDevice
{
    [System.Runtime.InteropServices.PreserveSig] int Activate(ref System.Guid iid, int clsCtx, nint activationParams, out nint ppInterface);
    [System.Runtime.InteropServices.PreserveSig] int OpenPropertyStore(uint access, out nint properties);
    [System.Runtime.InteropServices.PreserveSig] int GetId(out nint strId);
    [System.Runtime.InteropServices.PreserveSig] int GetState(out uint state);
}
