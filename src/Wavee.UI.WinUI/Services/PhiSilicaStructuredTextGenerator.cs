using System;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.UI.WinUI.Services;

internal static class PhiSilicaStructuredTextGenerator
{
    internal const string TextResultJsonSchema = """
    {
      "type": "object",
      "properties": {
        "text": { "type": "string" },
        "disposition": {
          "type": "string",
          "enum": [ "clear", "ambiguous", "insufficient_context" ]
        }
      },
      "required": [ "text", "disposition" ]
    }
    """;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static async Task<PhiSilicaStructuredGenerationResult> GenerateTextAsync(
        string prompt,
        float temperature,
        CancellationToken ct)
        => await GenerateTextAsync(prompt, temperature, TextResultJsonSchema, ct);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static async Task<PhiSilicaStructuredGenerationResult> GenerateTextAsync(
        string prompt,
        float temperature,
        string jsonSchema,
        CancellationToken ct)
        => await GenerateStructuredTextCoreAsync(
            prompt,
            jsonSchema,
            BuildOptionsWithoutCustomContentFilters(),
            "structured-default-options",
            ct);

    private static async Task<PhiSilicaStructuredGenerationResult> GenerateStructuredTextCoreAsync(
        string prompt,
        string jsonSchema,
        Microsoft.Windows.AI.Text.Experimental.LanguageModelOptionsExperimental options,
        string diagnosticPath,
        CancellationToken ct)
    {
        using var languageModel = await Microsoft.Windows.AI.Text.LanguageModel.CreateAsync();
        var experimentalModel = new Microsoft.Windows.AI.Text.Experimental.LanguageModelExperimental(languageModel);
        var structuredPrompt = BuildStructuredPrompt(prompt);

        var op = experimentalModel.GenerateStructuredJsonResponseAsync(
            structuredPrompt,
            jsonSchema,
            options);

        using var ctReg = ct.Register(() =>
        {
            try { op.Cancel(); } catch { /* op may have completed */ }
        });

        var result = await op;
        if (result is null)
        {
            return new PhiSilicaStructuredGenerationResult(
                PhiSilicaStructuredGenerationStatus.Error,
                string.Empty,
                "LanguageModel returned no result.",
                BuildRequestDiagnostics(prompt, structuredPrompt, jsonSchema, diagnosticPath, null),
                prompt,
                structuredPrompt,
                string.Empty);
        }

        var status = MapStatus(result.Status);
        if (status != PhiSilicaStructuredGenerationStatus.Complete)
        {
            return new PhiSilicaStructuredGenerationResult(
                status,
                result.Text ?? string.Empty,
                DescribeException(result.ExtendedError),
                BuildRequestDiagnostics(prompt, structuredPrompt, jsonSchema, diagnosticPath, result),
                prompt,
                structuredPrompt,
                result.Text ?? string.Empty);
        }

        return TryExtractText(result.Text, out var text, out var parseError)
            ? new PhiSilicaStructuredGenerationResult(
                status,
                text,
                null,
                null,
                prompt,
                structuredPrompt,
                result.Text ?? string.Empty)
            : new PhiSilicaStructuredGenerationResult(
                PhiSilicaStructuredGenerationStatus.ResponseInvalidJson,
                result.Text ?? string.Empty,
                parseError,
                BuildRequestDiagnostics(prompt, structuredPrompt, jsonSchema, diagnosticPath, result),
                prompt,
                structuredPrompt,
                result.Text ?? string.Empty);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static async Task<PhiSilicaStructuredGenerationResult> GeneratePlainTextAsync(
        string prompt,
        CancellationToken ct)
    {
        using var languageModel = await Microsoft.Windows.AI.Text.LanguageModel.CreateAsync();
        var op = languageModel.GenerateResponseAsync(prompt);

        using var ctReg = ct.Register(() =>
        {
            try { op.Cancel(); } catch { /* op may have completed */ }
        });

        var result = await op;
        if (result is null)
        {
            return new PhiSilicaStructuredGenerationResult(
                PhiSilicaStructuredGenerationStatus.Error,
                string.Empty,
                "LanguageModel returned no result.",
                BuildPlainTextRequestDiagnostics(prompt, null),
                prompt,
                null,
                string.Empty);
        }

        var status = MapStatus(result.Status);
        return new PhiSilicaStructuredGenerationResult(
            status,
            result.Text ?? string.Empty,
            DescribeException(result.ExtendedError),
            BuildPlainTextRequestDiagnostics(prompt, result),
            prompt,
            null,
            result.Text ?? string.Empty);
    }

    // The suffix is schema-agnostic on purpose: a prompt whose schema does not
    // include a `text` or `disposition` field should not be told to populate
    // them. Schema-specific hints (e.g. "put the prose in text") live on the
    // per-request prompt builders.
    private static string BuildStructuredPrompt(string prompt)
        => prompt +
           "\n\nReturn only a JSON object matching the provided schema.";

    private static Microsoft.Windows.AI.Text.Experimental.LanguageModelOptionsExperimental BuildOptionsWithoutCustomContentFilters()
        // Do not set ContentFilterOptions here. On WinAppSDK 2.1.4-experimental8,
        // the structured JSON API returns BlockedByPolicy for neutral prompts when
        // custom ContentFilterOptions are attached, while the same call succeeds
        // with the documented default options object. The default moderation still
        // applies; prompt/response content blocks have their own statuses.
        => new();

    private static PhiSilicaStructuredGenerationStatus MapStatus(
        Microsoft.Windows.AI.Text.Experimental.GenerateStructuredJsonResponseStatus status)
        => status switch
        {
            Microsoft.Windows.AI.Text.Experimental.GenerateStructuredJsonResponseStatus.Complete =>
                PhiSilicaStructuredGenerationStatus.Complete,
            Microsoft.Windows.AI.Text.Experimental.GenerateStructuredJsonResponseStatus.InProgress =>
                PhiSilicaStructuredGenerationStatus.InProgress,
            Microsoft.Windows.AI.Text.Experimental.GenerateStructuredJsonResponseStatus.BlockedByPolicy =>
                PhiSilicaStructuredGenerationStatus.BlockedByPolicy,
            Microsoft.Windows.AI.Text.Experimental.GenerateStructuredJsonResponseStatus.PromptLargerThanContext =>
                PhiSilicaStructuredGenerationStatus.PromptLargerThanContext,
            Microsoft.Windows.AI.Text.Experimental.GenerateStructuredJsonResponseStatus.PromptBlockedByContentModeration =>
                PhiSilicaStructuredGenerationStatus.PromptBlockedByContentModeration,
            Microsoft.Windows.AI.Text.Experimental.GenerateStructuredJsonResponseStatus.ResponseBlockedByContentModeration =>
                PhiSilicaStructuredGenerationStatus.ResponseBlockedByContentModeration,
            Microsoft.Windows.AI.Text.Experimental.GenerateStructuredJsonResponseStatus.ResponseInvalidJson =>
                PhiSilicaStructuredGenerationStatus.ResponseInvalidJson,
            Microsoft.Windows.AI.Text.Experimental.GenerateStructuredJsonResponseStatus.Error =>
                PhiSilicaStructuredGenerationStatus.Error,
            _ => PhiSilicaStructuredGenerationStatus.Error,
        };

    private static PhiSilicaStructuredGenerationStatus MapStatus(
        Microsoft.Windows.AI.Text.LanguageModelResponseStatus status)
        => status switch
        {
            Microsoft.Windows.AI.Text.LanguageModelResponseStatus.Complete =>
                PhiSilicaStructuredGenerationStatus.Complete,
            Microsoft.Windows.AI.Text.LanguageModelResponseStatus.InProgress =>
                PhiSilicaStructuredGenerationStatus.InProgress,
            Microsoft.Windows.AI.Text.LanguageModelResponseStatus.BlockedByPolicy =>
                PhiSilicaStructuredGenerationStatus.BlockedByPolicy,
            Microsoft.Windows.AI.Text.LanguageModelResponseStatus.PromptLargerThanContext =>
                PhiSilicaStructuredGenerationStatus.PromptLargerThanContext,
            Microsoft.Windows.AI.Text.LanguageModelResponseStatus.PromptBlockedByContentModeration =>
                PhiSilicaStructuredGenerationStatus.PromptBlockedByContentModeration,
            Microsoft.Windows.AI.Text.LanguageModelResponseStatus.ResponseBlockedByContentModeration =>
                PhiSilicaStructuredGenerationStatus.ResponseBlockedByContentModeration,
            Microsoft.Windows.AI.Text.LanguageModelResponseStatus.Error =>
                PhiSilicaStructuredGenerationStatus.Error,
            _ => PhiSilicaStructuredGenerationStatus.Error,
        };

    internal static bool TryExtractText(string? json, out string text, out string? error)
    {
        text = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Structured JSON response was empty.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Structured JSON response root was not an object.";
                return false;
            }

            // Schemas without a top-level `text` property (e.g. the citation
            // schema, which emits only segments + citations) are valid: callers
            // that need a paragraph reconstruct it via
            // PhiSilicaStructuredTextRequest.BuildSuccessResult from RawResponseText.
            if (!document.RootElement.TryGetProperty("text", out var textElement)
                || textElement.ValueKind != JsonValueKind.String)
            {
                text = string.Empty;
                return true;
            }

            text = textElement.GetString() ?? string.Empty;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Structured JSON response could not be parsed: {ex.Message}";
            return false;
        }
    }

    private static string? DescribeException(Exception? ex)
        => ex is null
            ? null
            : $"{ex.GetType().Name}: 0x{ex.HResult:X8} {ex.Message}";

    private static string BuildRequestDiagnostics(
        string prompt,
        string structuredPrompt,
        string jsonSchema,
        string diagnosticPath,
        Microsoft.Windows.AI.Text.Experimental.GenerateStructuredJsonResponseResult? result)
    {
        var promptHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt)))[..12];
        var structuredPromptHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(structuredPrompt)))[..12];
        var responseLength = result?.Text?.Length ?? -1;

        return "path=" + diagnosticPath +
               ", promptChars=" + prompt.Length +
               ", structuredPromptChars=" + structuredPrompt.Length +
               ", schemaChars=" + jsonSchema.Length +
               ", promptHash=" + promptHash +
               ", structuredPromptHash=" + structuredPromptHash +
               ", responseChars=" + responseLength +
               ", resultType=" + (result?.GetType().FullName ?? "<null>") +
               ", resultProperties=[" + DescribeResultProperties(result) + "]";
    }

    private static string BuildPlainTextRequestDiagnostics(
        string prompt,
        Microsoft.Windows.AI.Text.LanguageModelResponseResult? result)
    {
        var promptHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt)))[..12];
        var responseLength = result?.Text?.Length ?? -1;

        return "path=plaintext" +
               ", promptChars=" + prompt.Length +
               ", promptHash=" + promptHash +
               ", responseChars=" + responseLength +
               ", resultType=" + (result?.GetType().FullName ?? "<null>") +
               ", resultProperties=[" + DescribeResultProperties(result) + "]";
    }

    private static string DescribeResultProperties(
        Microsoft.Windows.AI.Text.Experimental.GenerateStructuredJsonResponseResult? result)
    {
        if (result is null)
            return "<none>";

        try
        {
            var sb = new StringBuilder();
            var properties = result.GetType().GetProperties();
            for (var i = 0; i < properties.Length; i++)
            {
                var property = properties[i];
                if (!property.CanRead)
                    continue;

                var value = string.Equals(property.Name, "Text", StringComparison.Ordinal)
                    ? $"<chars={result.Text?.Length ?? 0}>"
                    : SafeGetPropertyValue(result, property);

                if (sb.Length > 0)
                    sb.Append(", ");
                sb.Append(property.Name).Append('=').Append(value);
            }

            return sb.Length == 0 ? "<none>" : sb.ToString();
        }
        catch (Exception ex)
        {
            return "property-inspection-failed:" + ex.GetType().Name;
        }
    }

    private static string DescribeResultProperties(
        Microsoft.Windows.AI.Text.LanguageModelResponseResult? result)
    {
        if (result is null)
            return "<none>";

        try
        {
            var sb = new StringBuilder();
            var properties = result.GetType().GetProperties();
            for (var i = 0; i < properties.Length; i++)
            {
                var property = properties[i];
                if (!property.CanRead)
                    continue;

                var value = string.Equals(property.Name, "Text", StringComparison.Ordinal)
                    ? $"<chars={result.Text?.Length ?? 0}>"
                    : SafeGetPropertyValue(result, property);

                if (sb.Length > 0)
                    sb.Append(", ");
                sb.Append(property.Name).Append('=').Append(value);
            }

            return sb.Length == 0 ? "<none>" : sb.ToString();
        }
        catch (Exception ex)
        {
            return "property-inspection-failed:" + ex.GetType().Name;
        }
    }

    private static string SafeGetPropertyValue(object instance, System.Reflection.PropertyInfo property)
    {
        try
        {
            var value = property.GetValue(instance);
            return value switch
            {
                null => "<null>",
                Exception ex => DescribeException(ex) ?? "<exception>",
                _ => value.ToString() ?? "<null>",
            };
        }
        catch (Exception ex)
        {
            return "<read-failed:" + ex.GetType().Name + ">";
        }
    }
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
