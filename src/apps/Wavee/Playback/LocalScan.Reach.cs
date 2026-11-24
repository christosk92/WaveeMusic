// ── Playback/LocalScan.Reach.cs ─────────────────────────────────────────────────────────────────────────────────────
// is a network root there? One probe on its own thread before the scan or the watcher touches it
//
// Role: CORE (the scan thread waits; the probe thread may hang and is abandoned)
// Spec: C:\WAVEE\fgpu\handoff\request-plans\240-local-playlist-files-implementation.md §3.16 "Reachability before every touch"
//
// WHY. A dead SMB server blocks an open or a listing for 60-240 s (the redirector's session timeout, ×4 when extended). The
// probe asks for the root's attributes on a throwaway thread with a 3 s budget, and a TCP connect to port 445 of the server beside
// it; when the attributes do not answer in 3 s and the server did not answer either, the root is Offline at once (a dead host: ~3 s);
// when the server is there it waits up to 20 s for the attributes (a NAS spinning up its disks). Online (< 3 s), Waking (3-20 s), Slow (the server answers TCP but not the share within 20 s), Offline,
// SignInNeeded (access denied or a logon failure: Windows signs in, Wavee never stores credentials).

using System.Net.Sockets;

namespace Wavee;

/// <summary>A music folder's state as the scan and the watcher see it (the façade's per-root fact; P2 draws it).</summary>
/// <para><see cref="Checking"/>: a folder just added, whose existence (a local path) or reachability (a network one) is being asked off the
/// UI thread before it is kept (F4: a dead share blocked the UI thread 44 s).</para>
public enum LocalRootState : byte { Unknown, Scanning, Online, Waking, Slow, Offline, SignInNeeded, Checking }

public sealed partial class LocalScan
{
    /// <summary>Classify a network root's reachability without blocking more than 20 s (§3.16). Local roots: Online.</summary>
    public static LocalRootState ProbeReach(string rootPath)
    {
        if (!NtDirectory.IsNetworkPath(rootPath)) return LocalRootState.Online;
        int answer = -1;   // -1 pending, 0 ok, 1 sign-in, 2 unreachable
        using var done = new ManualResetEventSlim(false);
        var probe = new Thread(() =>
        {
            int a;
            try { File.GetAttributes(rootPath); a = 0; }
            catch (UnauthorizedAccessException) { a = 1; }
            catch (IOException ex) { a = (ex.HResult & 0xFFFF) is 5 or 1326 or 1311 ? 1 : 2; }   // access denied, logon failure, no logon servers
            Volatile.Write(ref answer, a);
            try { done.Set(); } catch (ObjectDisposedException) { }
        })
        { IsBackground = true, Name = "Wavee.LocalScan.Reach", Priority = ThreadPriority.BelowNormal };
        probe.Start();
        // The server's SMB port is asked BESIDE the attribute wait (F4), so a dead host is Offline at the 3 s mark, not 3 + 2 s later.
        int tcp = -1;   // -1 pending, 0 no, 1 yes
        string? host = ServerHost(rootPath);
        if (host is not null)
            new Thread(() => Volatile.Write(ref tcp, TcpAnswers(host, 445, 2500) ? 1 : 0))
            { IsBackground = true, Name = "Wavee.LocalScan.Reach.Tcp", Priority = ThreadPriority.BelowNormal }.Start();
        if (done.Wait(3000)) return Verdict(answer, LocalRootState.Online);
        bool serverAnswers = host is not null && (Volatile.Read(ref tcp) == 1 || (Volatile.Read(ref tcp) == -1 && WaitTcp(ref tcp, 500)));
        if (!serverAnswers) return LocalRootState.Offline;
        return done.Wait(17_000) ? Verdict(Volatile.Read(ref answer), LocalRootState.Waking) : LocalRootState.Slow;

        static LocalRootState Verdict(int a, LocalRootState ok) => a switch { 0 => ok, 1 => LocalRootState.SignInNeeded, _ => LocalRootState.Offline };
    }

    /// <summary>The TCP ask still running at the 3 s mark: wait up to <paramref name="ms"/> more for it.</summary>
    static bool WaitTcp(ref int tcp, int ms)
    {
        long until = Environment.TickCount64 + ms;
        while (Volatile.Read(ref tcp) == -1 && Environment.TickCount64 < until) Thread.Sleep(25);
        return Volatile.Read(ref tcp) == 1;
    }

    /// <summary>The server of a UNC path (<c>\\nas\music</c> → <c>nas</c>); null for a mapped letter (no TCP step then).</summary>
    static string? ServerHost(string path)
    {
        string p = path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + path[8..] : path;
        if (!p.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        int end = p.IndexOf('\\', 2);
        return end > 2 ? p[2..end] : p.Length > 2 ? p[2..] : null;
    }

    static bool TcpAnswers(string host, int port, int timeoutMs)
    {
        try
        {
            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            using var cts = new CancellationTokenSource(timeoutMs);
            socket.ConnectAsync(host, port, cts.Token).AsTask().Wait(timeoutMs);
            return socket.Connected;
        }
        catch (Exception ex) when (ex is SocketException or AggregateException or OperationCanceledException or ArgumentException)
        {
            return false;
        }
    }
}
