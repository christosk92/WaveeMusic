using System;

namespace Wavee.AI.Activity;

public enum AiActivityKind
{
    Started,
    Planning,
    ToolStarted,
    ToolCompleted,
    ToolSkipped,
    ModelStarted,
    ModelStreaming,
    ModelCompleted,
    Warning,
    Error,
}

public sealed record AiActivityEvent
{
    public AiActivityEvent(
        AiActivityKind Kind,
        string Message,
        string? ToolName = null,
        string? Detail = null,
        DateTimeOffset? Timestamp = null)
    {
        this.Kind = Kind;
        this.Message = Message;
        this.ToolName = ToolName;
        this.Detail = Detail;
        this.Timestamp = Timestamp ?? DateTimeOffset.Now;
    }

    public AiActivityKind Kind { get; init; }
    public string Message { get; init; }
    public string? ToolName { get; init; }
    public string? Detail { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}

public interface IAiActivitySink
{
    void Report(AiActivityEvent activity);
}

public sealed class NullAiActivitySink : IAiActivitySink
{
    public static NullAiActivitySink Instance { get; } = new();

    private NullAiActivitySink()
    {
    }

    public void Report(AiActivityEvent activity)
    {
    }
}
