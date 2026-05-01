using System;
using System.Collections.Generic;

namespace Wavee.Connect.Diagnostics;

public enum RemoteStateEventKind
{
    PutStateRequest,
    PutStateResponse,
    ClusterUpdate,
    PutStateResponseEcho,
    VolumeCommand,
    DealerCommand,
    DealerReply,
    DealerLifecycle,
    ConnectionIdAcquired,
    ConnectionIdChanged,
    SubscriptionRegistered,
}

public enum RemoteStateDirection
{
    Outbound,
    Inbound,
    Internal,
}

public sealed record RemoteStateEvent
{
    public required DateTimeOffset Timestamp { get; init; }
    public required RemoteStateEventKind Kind { get; init; }
    public required RemoteStateDirection Direction { get; init; }
    public required string Summary { get; init; }
    public string? CorrelationId { get; init; }
    public long? ElapsedMs { get; init; }
    public int? PayloadBytes { get; init; }
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    public string? JsonBody { get; init; }
    public string? Notes { get; init; }
}
