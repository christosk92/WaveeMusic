using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.AI.Generation;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Compatibility bridge for older WinUI structured-generation helpers.
/// The actual Phi Silica implementation now lives in Wavee.AI.
/// </summary>
internal static class PhiSilicaStructuredTextGenerator
{
    internal const string TextResultJsonSchema = PhiSilicaLanguageModelClient.TextResultJsonSchema;

    private static readonly PhiSilicaLanguageModelClient Client = new();

    public static async Task<PhiSilicaStructuredGenerationResult> GenerateTextAsync(
        string prompt,
        float temperature,
        CancellationToken ct)
        => await GenerateTextAsync(prompt, temperature, TextResultJsonSchema, ct);

    public static async Task<PhiSilicaStructuredGenerationResult> GenerateTextAsync(
        string prompt,
        float temperature,
        string jsonSchema,
        CancellationToken ct)
    {
        var result = await Client.GenerateStructuredJsonAsync(
            new AiStructuredGenerationRequest(prompt, jsonSchema, temperature, "WinUiStructuredTextCompatibility"),
            ct);
        return ToLegacyResult(result);
    }

    public static async Task<PhiSilicaStructuredGenerationResult> GeneratePlainTextAsync(
        string prompt,
        CancellationToken ct)
        => await GeneratePlainTextAsync(prompt, deltaProgress: null, ct);

    public static async Task<PhiSilicaStructuredGenerationResult> GeneratePlainTextAsync(
        string prompt,
        IProgress<string>? deltaProgress,
        CancellationToken ct)
    {
        var result = await Client.GenerateTextAsync(
            new AiTextGenerationRequest(prompt, Operation: "WinUiPlainTextCompatibility"),
            deltaProgress,
            ct);
        return ToLegacyResult(result);
    }

    private static PhiSilicaStructuredGenerationResult ToLegacyResult(AiGenerationResult result)
        => new(
            ToLegacyStatus(result.Status),
            result.Text,
            result.ErrorMessage,
            result.DiagnosticMessage,
            result.RawPrompt,
            result.RawStructuredPrompt,
            result.RawResponseText);

    private static PhiSilicaStructuredGenerationStatus ToLegacyStatus(AiGenerationStatus status)
        => status switch
        {
            AiGenerationStatus.Complete => PhiSilicaStructuredGenerationStatus.Complete,
            AiGenerationStatus.InProgress => PhiSilicaStructuredGenerationStatus.InProgress,
            AiGenerationStatus.BlockedByPolicy => PhiSilicaStructuredGenerationStatus.BlockedByPolicy,
            AiGenerationStatus.PromptLargerThanContext => PhiSilicaStructuredGenerationStatus.PromptLargerThanContext,
            AiGenerationStatus.PromptBlockedByContentModeration => PhiSilicaStructuredGenerationStatus.PromptBlockedByContentModeration,
            AiGenerationStatus.ResponseBlockedByContentModeration => PhiSilicaStructuredGenerationStatus.ResponseBlockedByContentModeration,
            AiGenerationStatus.ResponseInvalidJson => PhiSilicaStructuredGenerationStatus.ResponseInvalidJson,
            _ => PhiSilicaStructuredGenerationStatus.Error,
        };
}

internal readonly record struct PhiSilicaStructuredGenerationResult(
    PhiSilicaStructuredGenerationStatus Status,
    string Text,
    string? ErrorMessage,
    string? DiagnosticMessage = null,
    string? RawPrompt = null,
    string? RawStructuredPrompt = null,
    string? RawResponseText = null);

internal enum PhiSilicaStructuredGenerationStatus
{
    Complete,
    InProgress,
    BlockedByPolicy,
    PromptLargerThanContext,
    PromptBlockedByContentModeration,
    ResponseBlockedByContentModeration,
    ResponseInvalidJson,
    Error,
}
