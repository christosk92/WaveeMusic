using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.AI.Generation;

public enum AiGenerationStatus
{
    Complete,
    InProgress,
    BlockedByPolicy,
    PromptLargerThanContext,
    PromptBlockedByContentModeration,
    ResponseBlockedByContentModeration,
    ResponseInvalidJson,
    Error,
    Unavailable,
}

public sealed record AiTextGenerationRequest(
    string Prompt,
    float Temperature = 0.35f,
    string Operation = "GenerateText");

public sealed record AiStructuredGenerationRequest(
    string Prompt,
    string JsonSchema,
    float Temperature = 0.0f,
    string Operation = "GenerateStructured");

public sealed record AiGenerationResult(
    AiGenerationStatus Status,
    string Text,
    string? ErrorMessage = null,
    string? DiagnosticMessage = null,
    string? RawPrompt = null,
    string? RawStructuredPrompt = null,
    string? RawResponseText = null)
{
    public bool IsComplete => Status == AiGenerationStatus.Complete;

    public static AiGenerationResult Unavailable(string? reason = null)
        => new(AiGenerationStatus.Unavailable, string.Empty, reason);
}

public interface ILanguageModelClient
{
    bool IsSupported { get; }

    string DescribeStatus();

    Task<bool> EnsureReadyAsync(
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);

    Task<AiGenerationResult> GenerateTextAsync(
        AiTextGenerationRequest request,
        IProgress<string>? deltaProgress = null,
        CancellationToken cancellationToken = default);

    Task<AiGenerationResult> GenerateStructuredJsonAsync(
        AiStructuredGenerationRequest request,
        CancellationToken cancellationToken = default);
}
