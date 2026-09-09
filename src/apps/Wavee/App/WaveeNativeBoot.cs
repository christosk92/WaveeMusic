using System;
using System.Runtime.Versioning;
using FluentGpu.WindowsApi.Notifications;

namespace Wavee;

/// <summary>
/// Process-wide toast activator wiring: hops <see cref="ToastNotifier.Activated"/> onto the UI thread and posts any
/// <c>wavee://</c> launch argument through <see cref="DeepLinkChannel"/>. Registration is fail-soft — a missing
/// AUMID / elevated process / older OS must never block playback.
/// <para>
/// TWO entry points, on purpose (see <see cref="StartupActivation"/>): <see cref="InstallDispatcher"/> is free and runs
/// in the startup schedule's core phase, so an activation arriving in the first milliseconds has somewhere to land;
/// <see cref="Register"/> is the registry + COM half and runs as a window-phase step, on the UI thread but in its own
/// posted drain. Both need a real UI session, which is why neither is called from the composition root.
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

    /// <summary>Install the UI-thread dispatcher and subscribe <see cref="ToastNotifier.Activated"/>. Idempotent, and
    /// FREE — two field writes, no registry, no COM. <paramref name="post"/> is the same marshal the playback bridge
    /// uses.
    ///
    /// <para>Split from <see cref="Register"/> so it can run in the startup schedule's core phase: the dispatcher must
    /// be in place before an activation can arrive, but the identity registration behind it is ~9 HKCU writes plus one
    /// or two <c>CoRegisterClassObject</c> calls and has no business inside a rendered frame.</para></summary>
    public static void InstallDispatcher(Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(post);
        ToastNotifier.Default.ActivationDispatcher = post;
        if (System.Threading.Interlocked.Exchange(ref _installed, 1) != 0) return;
        ToastNotifier.Default.Activated += OnActivated;
    }

    /// <summary>Register Wavee's toast activator identity: the unpackaged AUMID under
    /// <c>HKCU\Software\Classes\AppUserModelId</c>, the <c>LocalServer32</c> CLSID entry, the display name/icon
    /// assets, and the class object itself. Idempotent and fail-soft.
    ///
    /// <para>UI THREAD. The engine's activator falls back to a NON-agile class object (AGILE is rejected for the
    /// ComWrappers CCW), which lives in the registering thread's apartment, and the notifier caches raw pointers that
    /// bind every later <c>Show</c>/<c>Update</c> to it — so this cannot be handed to the thread pool. It is a
    /// window-phase startup step instead: same thread, its own posted drain.</para>
    ///
    /// <para>ORDERING: everything that needs an AUMID must come after this — the Jump List (the shell keys a custom
    /// destination list by AUMID, and <c>ToastNotifier.Default.Aumid</c> is empty until now) and the scheduled
    /// release/daylist toasts.</para></summary>
    public static void Register()
    {
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
