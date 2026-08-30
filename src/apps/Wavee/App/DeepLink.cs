using System;
using System.Runtime.InteropServices;
using FluentGpu;
using FluentGpu.Signals;
using FluentGpu.WindowsApi.Activation;

namespace Wavee;

/// <summary>
/// The OS-facing half of the deep-link surface: scheme registration, bring-to-front, and the process-wide intake
/// channel. The parser itself (raw string → <see cref="DeepLinkVerb"/>) is the other half of this partial class, in
/// <c>DeepLinkParse.cs</c> — engine-free so the tests can compile it. Consumption (navigate / play / resume) is the
/// shell's job; neither half does it.
/// </summary>
public static partial class DeepLink
{
    const int SwShow = 5;
    const int SwRestore = 9;

    /// <summary>Register or unregister the OPT-IN <c>spotify:</c> scheme association (HKCU) to match
    /// <c>WaveeSettings.HandleSpotifyLinks</c>. Called at boot and again whenever the setting is toggled, so the two can
    /// never drift. Off is the default and unregisters: taking the scheme from an installed Spotify without being asked
    /// would break the user's muscle memory. Never throws — a registry write we are not allowed to make must not stop
    /// the app from starting.</summary>
    public static void SyncSpotifySchemeRegistration(bool handleSpotifyLinks)
    {
        try
        {
            if (!handleSpotifyLinks) { ProtocolRegistrar.UnregisterProtocol("spotify"); return; }
            string? exe = Environment.ProcessPath;
            if (exe is { Length: > 0 })
                ProtocolRegistrar.RegisterProtocol("spotify", exe, "Wavee", iconPath: WaveeAppIcon.Path());
        }
        catch (Exception ex)
        {
            WaveeLog.Instance.Warn("app", "spotify: protocol registration sync failed", ex);
        }
    }

    /// <summary>Register or unregister the logon-startup entry (the HKCU <c>Run</c> value) to match
    /// <c>WaveeSettings.StartOnLogin</c>. Same both-directions contract as
    /// <see cref="SyncSpotifySchemeRegistration"/>: called at boot and again whenever the setting is toggled, so
    /// switching it off actually removes the <c>Run</c> value instead of leaving a stale entry pointing at an exe the
    /// user has since moved. Unpackaged only — a packaged build declares its startup task in the manifest and the OS
    /// owns it; Program.cs gates the call. Never throws: a registry write we are not allowed to make must not stop the
    /// app from starting.</summary>
    public static void SyncStartupRegistration(bool on)
    {
        try
        {
            if (!on) { ProtocolRegistrar.UnregisterStartup("Wavee"); return; }
            string? exe = Environment.ProcessPath;
            if (exe is { Length: > 0 })
                ProtocolRegistrar.RegisterStartup("Wavee", exe);
        }
        catch (Exception ex)
        {
            WaveeLog.Instance.Warn("app", "startup (run-at-logon) registration sync failed", ex);
        }
    }

    /// <summary>Restore (if minimized) and foreground the FluentApp window. No-op when the HWND is not up yet.
    /// App-side P/Invoke — <c>FluentGpu.Windows</c> has no public wake/activate helper.</summary>
    public static void WakeWindow()
    {
        nint hwnd = FluentApp.WindowHandle;
        if (hwnd == 0) return;
        ShowWindow(hwnd, IsIconic(hwnd) ? SwRestore : SwShow);
        SetForegroundWindow(hwnd);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint hWnd);
}

/// <summary>
/// Process-wide intake for <c>wavee://</c> activations. <see cref="Post"/> parses and enqueues (garbage is dropped);
/// the shell drains with <see cref="TryDequeue"/> after reading <see cref="Pending"/>. Navigation / playback is not
/// this type's job.
/// </summary>
public static class DeepLinkChannel
{
    static readonly object Sync = new();
    static readonly Queue<DeepLinkVerb> Queue = new();

    /// <summary>Monotonic ticket — bump on every accepted <see cref="Post"/>. Read <c>.Value</c> to subscribe, then
    /// drain with <see cref="TryDequeue"/>. Same shape as <c>OpenVideoOverrides</c> / <c>_searchFocusRequest</c>.</summary>
    public static readonly Signal<int> Pending = new(0);

    /// <summary>Parse <paramref name="rawArgs"/> and enqueue a verb. No-op on unknown/garbage (never throws).</summary>
    public static void Post(string? rawArgs)
    {
        if (!DeepLink.TryParse(rawArgs, out DeepLinkVerb verb)) return;
        lock (Sync) Queue.Enqueue(verb);
        Pending.Value = Pending.Peek() + 1;
    }

    /// <summary>Pop the next accepted verb. Returns <c>false</c> when the queue is empty.</summary>
    public static bool TryDequeue(out DeepLinkVerb verb)
    {
        lock (Sync) return Queue.TryDequeue(out verb);
    }
}
