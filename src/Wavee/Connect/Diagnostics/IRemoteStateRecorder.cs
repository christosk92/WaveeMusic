using System;
using System.Collections.Generic;

namespace Wavee.Connect.Diagnostics;

public interface IRemoteStateRecorder
{
    void Record(RemoteStateEvent evt);
}

public static class RemoteStateRecorderExtensions
{
    public static void Record(
        this IRemoteStateRecorder? recorder,
        RemoteStateEventKind kind,
        RemoteStateDirection direction,
        string summary,
        string? correlationId = null,
        long? elapsedMs = null,
        int? payloadBytes = null,
        IReadOnlyDictionary<string, string>? headers = null,
        string? jsonBody = null,
        string? notes = null)
    {
        if (recorder is null) return;

        recorder.Record(new RemoteStateEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            Kind = kind,
            Direction = direction,
            Summary = summary,
            CorrelationId = correlationId,
            ElapsedMs = elapsedMs,
            PayloadBytes = payloadBytes,
            Headers = headers,
            JsonBody = jsonBody,
            Notes = notes,
        });
    }
}
