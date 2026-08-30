using System;
using System.Runtime.Versioning;
using FluentGpu.WindowsApi.Notifications;

namespace Wavee;

/// <summary>
/// Process-wide toast activator wiring: hops <see cref="ToastNotifier.Activated"/> onto the UI thread and posts any
/// <c>wavee://</c> launch argument through <see cref="DeepLinkChannel"/>. Registration is fail-soft — a missing
/// AUMID / elevated process / older OS must never block playback.
/// <para>
/// Called from <see cref="PlaybackBridge.Activate"/> (window exists, UI thread). Boot-time register is optional;
/// <c>Program.cs</c> already posts toast-activated command-line args into <see cref="DeepLinkChannel"/> on cold launch.
/// </para>
/// </summary>
public static class WaveeNativeBoot
{
    /// <summary>
    /// Stable toast-activator CLSID for Wavee. Same value must appear in a packaged manifest
    /// <c>ToastActivatorCLSID</c> / <c>com:ExeServer</c> if Wavee ships MSIX. No prior Wavee CLSID existed in-tree
    /// (the gallery demo uses a different GUID); this one is the documented identity going forward.
    /// </summary>
    public static readonly Guid ToastActivatorClsid = new("C8E4A91B-3D52-4F07-9B6A-1E7C4D8F2A30");

    static int _installed;

    /// <summary>Install the UI-thread dispatcher, subscribe <see cref="ToastNotifier.Activated"/>, then
    /// <see cref="ToastNotifier.Register"/>. Idempotent. <paramref name="post"/> is the same marshal
    /// <see cref="PlaybackBridge.Activate"/> already uses.</summary>
    public static void Install(Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(post);
        if (System.Threading.Interlocked.Exchange(ref _installed, 1) != 0)
        {
            ToastNotifier.Default.ActivationDispatcher = post;
            return;
        }

        ToastNotifier.Default.ActivationDispatcher = post;
        ToastNotifier.Default.Activated += OnActivated;
        try
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0) && ToastNotifier.IsSupported)
                RegisterCore();
        }
        catch (Exception)
        {
            // Unpackaged registry / CoRegisterClassObject / elevated process — playback continues without toasts.
        }
    }

    [SupportedOSPlatform("windows10.0.10240.0")]
    static void RegisterCore() => ToastNotifier.Default.Register(ToastActivatorClsid, "Wavee", iconPath: WaveeAppIcon.Path());

    static void OnActivated(ToastActivatedArgs args)
    {
        if (TryPostWavee(args.Argument)) return;
        foreach (var kv in args.Arguments)
        {
            if (TryPostWavee(kv.Value) || TryPostWavee(kv.Key)) return;
        }
    }

    static bool TryPostWavee(string? raw)
    {
        if (!LooksLikeWavee(raw)) return false;
        DeepLinkChannel.Post(raw);
        DeepLink.WakeWindow();
        return true;
    }

    static bool LooksLikeWavee(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return false;
        return raw.Contains("wavee://", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("wavee:", StringComparison.OrdinalIgnoreCase);
    }
}
