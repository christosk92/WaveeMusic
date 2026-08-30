using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Wavee;

/// <summary>
/// Restart-after-exit. A process cannot relaunch itself: the single-instance mutex and <c>library.db</c> stay held until
/// it is actually gone, so a new instance started from inside it either loses the mutex race or opens a locked database.
/// The fix is one short-lived broker — this same exe re-invoked as <c>--relaunch-after &lt;pid&gt;</c> — that waits for us
/// to exit and only then starts a fresh Wavee. The broker arm is the first thing in <c>Program.Main</c>.
/// </summary>
/// <remarks>
/// This replaces the old <c>cmd.exe /c ping 127.0.0.1 -n 3 &gt;nul &amp; start "" "…"</c> trick, which had three real
/// problems: it spawned a console host (a visible flash on some machines and a chunk of AV attention), it hand-quoted the
/// exe path into a shell command line, and its 2-second <c>ping</c> was a guess — too short if shutdown drags, needlessly
/// long otherwise. Waiting on the actual pid is exact, and going through our own exe means the relaunch can be packaging-
/// aware (a packaged build is re-activated by AUMID, which a raw <c>start</c> cannot do).
/// </remarks>
static class AppRelaunch
{
    /// <summary>
    /// The token appended to the command line Wavee registers with <c>RegisterApplicationRestart</c> before an MSIX
    /// deployment (<c>PackageUpdater.RestartArgument</c>), so the process Windows brings back AFTER the update can say
    /// so in its log. <c>RegisterApplicationRestart(null, 0)</c> reuses the original command line verbatim, which left
    /// the relaunched process indistinguishable from a plain launch.
    /// <para>It is deliberately INERT: <c>Program.Main</c> logs one line for it and nothing else branches on it. It
    /// parses as neither an absolute URI nor a file path, so <c>ActivationArgs.Classify</c> still reports
    /// <c>ActivationKind.Launch</c> and the single-instance / deep-link path never sees it.</para>
    /// </summary>
    public const string RelaunchedAfterUpdateFlag = "--relaunched-after-update";

    /// <summary>Spawn the broker that restarts Wavee once this process ends. The caller MUST then end the process —
    /// <c>Environment.Exit(0)</c> — because the broker is blocked on this pid. Never throws: if the spawn fails the user
    /// is left to relaunch by hand, which is strictly better than taking down the shutdown path.</summary>
    public static void RestartAfterExit()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            try { exe = Process.GetCurrentProcess().MainModule?.FileName; }
            catch { exe = null; }
        }
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;

        try
        {
            // ArgumentList (not a hand-built Arguments string): the framework does the quoting, so an install path with
            // spaces or quotes cannot corrupt the command line.
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            psi.ArgumentList.Add("--relaunch-after");
            psi.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            Process.Start(psi);
        }
        catch { }
    }
}
