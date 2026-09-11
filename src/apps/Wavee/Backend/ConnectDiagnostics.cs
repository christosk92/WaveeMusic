using System;

namespace Wavee.Backend;

// ── Connect diagnostics — the latest PUT / echo, for the Settings diagnostics page (G5) ────────────────────────────────
// A static LATEST-VALUE holder, not a log: WaveeLog already carries every put-state/connect.echo line as scrollback, but
// the diagnostics page wants "what did we last tell the connect-state service, and what did it last answer" as one live
// snapshot it can render without scanning the ring buffer. Engine-free and dependency-free (no FluentGpu, no UI, no
// WaveeLog) so both Backend/DeviceStatePublisher.cs and SpotifyLive/ClusterMapper.cs can write to it directly.
public readonly record struct PutTrace(
    uint MsgId, string Reason, bool IsActive, long StartedPlayingAtMs, long HasBeenPlayingForMs, long AtMs)
{
    public static PutTrace Empty => new(0, "", false, 0, 0, 0);
}

public readonly record struct EchoTrace(
    uint MsgId, string ActiveId, long ServerTs, long ClusterStartedAtMs, bool Adopted, long AtMs)
{
    public static EchoTrace Empty => new(0, "", 0, 0, false, 0);
}

public static class ConnectDiagnostics
{
    static readonly object _gate = new();
    static PutTrace _lastPut = PutTrace.Empty;
    static EchoTrace _lastEcho = EchoTrace.Empty;

    public static PutTrace LastPut { get { lock (_gate) return _lastPut; } }
    public static EchoTrace LastEcho { get { lock (_gate) return _lastEcho; } }

    /// <summary>Raised after either trace updates — the diagnostics page's cue to re-read <see cref="LastPut"/> /
    /// <see cref="LastEcho"/> rather than polling.</summary>
    public static event Action? Changed;

    /// <summary>Record the PUT we just attempted (every send, not only ones the server accepted — a diagnostics page
    /// debugging a stuck claim wants to see what we last ASKED for).</summary>
    public static void RecordPut(uint msgId, string reason, bool isActive, long startedPlayingAtMs, long hasBeenPlayingForMs)
    {
        lock (_gate) _lastPut = new PutTrace(msgId, reason, isActive, startedPlayingAtMs, hasBeenPlayingForMs, Now());
        Changed?.Invoke();
    }

    /// <summary>Record the put-state RESPONSE (the announce-response Cluster) — the one frame that proves whether the
    /// server adopted our claim.</summary>
    public static void RecordEcho(uint msgId, string activeId, long serverTs, long clusterStartedAtMs, bool adopted)
    {
        lock (_gate) _lastEcho = new EchoTrace(msgId, activeId, serverTs, clusterStartedAtMs, adopted, Now());
        Changed?.Invoke();
    }

    static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
