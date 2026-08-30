using System;

namespace Wavee.Core;

/// <summary>The realtime (dealer WebSocket) link status, surfaced so the UI can show "Reconnecting…" instead of silently
/// going stale on a network drop. Driven by the live transport's socket lifecycle.</summary>
public enum ConnectionStatus
{
    Offline,        // no live session / socket torn down
    Connecting,     // first connect in progress
    Online,         // socket open + receiving
    Reconnecting,   // socket dropped (or half-open) → backing off + retrying
}

/// <summary>Observable status of the realtime link. The UI binds this; the live dealer transport drives it.</summary>
public interface IConnectivity
{
    ConnectionStatus Status { get; }
    IObservable<ConnectionStatus> StatusChanged { get; }
}
