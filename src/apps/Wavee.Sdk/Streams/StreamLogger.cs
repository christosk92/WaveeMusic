using System.Globalization;

namespace Wavee.Sdk.Streams;

/// <summary>Severity of a record written through <see cref="StreamLogger"/>.</summary>
public enum StreamLogLevel : byte
{
    /// <summary>Very fine-grained tracing.</summary>
    Trace,
    /// <summary>Developer diagnostics.</summary>
    Debug,
    /// <summary>Normal operational information.</summary>
    Info,
    /// <summary>Something recoverable went wrong.</summary>
    Warning,
    /// <summary>An operation failed.</summary>
    Error,
    /// <summary>The process cannot continue.</summary>
    Critical,
}

/// <summary>One bounded, already-rendered key/value attached to a structured stream log event.</summary>
/// <param name="Name">The field name.</param>
/// <param name="Value">The rendered value.</param>
public readonly record struct StreamLogField(string Name, string Value)
{
    /// <summary>A field with a string value (null becomes empty).</summary>
    public static StreamLogField Of(string name, string? value) => new(name, value ?? "");

    /// <summary>A field with an invariant-formatted integer value.</summary>
    public static StreamLogField Of(string name, long value) => new(name, value.ToString(CultureInfo.InvariantCulture));

    /// <summary>A field with an invariant-formatted integer value.</summary>
    public static StreamLogField Of(string name, int value) => new(name, value.ToString(CultureInfo.InvariantCulture));
}

/// <summary>
/// The sink a host adapts to its own logger so the SDK's stream building blocks
/// (<see cref="RangedHttpSource"/>, <see cref="ChunkDiskCache"/>) can log without depending on it.
/// </summary>
public interface IStreamLogSink
{
    /// <summary>True when a record at <paramref name="level"/> would be kept — checked BEFORE a message is built.</summary>
    bool IsEnabled(StreamLogLevel level);

    /// <summary>Write a plain message.</summary>
    void Log(StreamLogLevel level, string message);

    /// <summary>Write a structured event.</summary>
    /// <param name="level">Severity.</param>
    /// <param name="eventId">Stable dotted event id (e.g. <c>audio.cache.scan</c>).</param>
    /// <param name="message">Human-readable message.</param>
    /// <param name="elapsedMs">Duration of the operation, or -1 when not timed.</param>
    /// <param name="fields">Bounded key/value pairs.</param>
    void Event(StreamLogLevel level, string eventId, string message, long elapsedMs, ReadOnlySpan<StreamLogField> fields);
}

/// <summary>Logger facade over an optional <see cref="IStreamLogSink"/>. <c>default</c> is a safe no-op.</summary>
public readonly struct StreamLogger
{
    readonly IStreamLogSink? _sink;

    /// <summary>Bind the facade to a sink (null = no-op).</summary>
    public StreamLogger(IStreamLogSink? sink) => _sink = sink;

    /// <summary>The no-op logger.</summary>
    public static StreamLogger Null => default;

    /// <summary>True when a record at <paramref name="level"/> would be kept.</summary>
    public bool IsEnabled(StreamLogLevel level) => _sink is { } s && s.IsEnabled(level);

    /// <summary>Write an informational message.</summary>
    public void Info(string message) => _sink?.Log(StreamLogLevel.Info, message);

    /// <summary>Write a message at an explicit level.</summary>
    public void Log(StreamLogLevel level, string message) => _sink?.Log(level, message);

    /// <summary>Write a structured event. The fields are materialized only when the level is enabled.</summary>
    public void Event(StreamLogLevel level, string eventId, string message, long elapsedMs = -1,
        params ReadOnlySpan<StreamLogField> fields)
    {
        if (_sink is not { } sink || !sink.IsEnabled(level)) return;
        sink.Event(level, eventId, message, elapsedMs, fields);
    }
}
