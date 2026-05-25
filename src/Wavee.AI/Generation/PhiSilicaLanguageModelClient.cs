using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Wavee.AI.Generation;

public sealed class PhiSilicaLanguageModelClient : ILanguageModelClient
{
    public const string TextResultJsonSchema = """
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

    private const string LanguageModelLafFeatureId = "com.microsoft.windows.ai.languagemodel";
    private const string LanguageModelLafToken = "4+g4v/xx6B81Wc6Z0sO0bg==";
    private const string LanguageModelLafAttribution =
        "s6dvdzhx5m6rm has registered their use of com.microsoft.windows.ai.languagemodel with Microsoft and agrees to the terms of use.";

    private static readonly object LafUnlockGate = new();
    private static bool? _lafUnlockedCache;
    private static string? _lafUnlockStatusLabel;

    private readonly ILogger? _logger;
    private bool? _runtimeAvailableCache;

    public PhiSilicaLanguageModelClient(ILogger<PhiSilicaLanguageModelClient>? logger = null)
    {
        _logger = logger;
    }

    public bool IsSupported
    {
        get
        {
            if (_runtimeAvailableCache.HasValue)
                return _runtimeAvailableCache.Value;

            _runtimeAvailableCache = ProbeLanguageModelAvailable();
            return _runtimeAvailableCache.Value;
        }
    }

    public string DescribeStatus()
    {
        if (!IsSupported)
            return "Requires a Copilot+ PC";
        if (_lafUnlockedCache == false && !string.IsNullOrEmpty(_lafUnlockStatusLabel))
            return _lafUnlockStatusLabel!;
        return ReadyStateLabel();
    }

    public async Task<bool> EnsureReadyAsync(
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
        => await Task.Run(
            () => EnsureReadyCoreAsync(progress, cancellationToken),
            cancellationToken).ConfigureAwait(false);

    public async Task<AiGenerationResult> GenerateTextAsync(
        AiTextGenerationRequest request,
        IProgress<string>? deltaProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Prompt))
            return new AiGenerationResult(AiGenerationStatus.Error, string.Empty, "Prompt was empty.");

        try
        {
            return await GeneratePlainTextCoreAsync(request.Prompt, deltaProgress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "{Operation} failed before Phi Silica returned a result.", request.Operation);
            return ToExceptionResult(request.Operation, ex);
        }
    }

    public async Task<AiGenerationResult> GenerateStructuredJsonAsync(
        AiStructuredGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Prompt))
            return new AiGenerationResult(AiGenerationStatus.Error, string.Empty, "Prompt was empty.");
        if (string.IsNullOrWhiteSpace(request.JsonSchema))
            return new AiGenerationResult(AiGenerationStatus.Error, string.Empty, "JSON schema was empty.");

        try
        {
            return await GenerateStructuredJsonCoreAsync(
                    request.Prompt,
                    request.JsonSchema,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "{Operation} failed before Phi Silica returned a result.", request.Operation);
            return ToExceptionResult(request.Operation, ex);
        }
    }

    private async Task<bool> EnsureReadyCoreAsync(
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (!IsSupported)
            return false;
        if (!EnsureLanguageModelLafUnlocked())
            return false;

        try
        {
            return await EnsureReadyCore(progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TypeLoadException ex)
        {
            _logger?.LogWarning(ex, "Phi Silica type could not load at runtime.");
            return false;
        }
        catch (FileNotFoundException ex)
        {
            _logger?.LogWarning(ex, "Phi Silica projection assembly missing at runtime.");
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger?.LogWarning(ex, "Windows denied access to the LanguageModel limited access feature.");
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "LanguageModel.EnsureReadyAsync failed.");
            return false;
        }
    }

    private bool EnsureLanguageModelLafUnlocked()
    {
        if (_lafUnlockedCache.HasValue)
            return _lafUnlockedCache.Value;

        lock (LafUnlockGate)
        {
            if (_lafUnlockedCache.HasValue)
                return _lafUnlockedCache.Value;

            try
            {
                var unlocked = TryUnlockLanguageModelLafCore(out var statusLabel);
                _lafUnlockStatusLabel = statusLabel;
                _lafUnlockedCache = unlocked;
                if (unlocked)
                {
                    _logger?.LogInformation(
                        "LimitedAccessFeature {FeatureId} unlocked: {Status}",
                        LanguageModelLafFeatureId,
                        statusLabel);
                }
                else
                {
                    _logger?.LogWarning(
                        "LimitedAccessFeature {FeatureId} unlock denied: {Status}",
                        LanguageModelLafFeatureId,
                        statusLabel);
                }

                return unlocked;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "LimitedAccessFeatures.TryUnlockFeature threw.");
                _lafUnlockStatusLabel = "Limited Access Feature unlock failed";
                _lafUnlockedCache = false;
                return false;
            }
        }
    }

    private bool ProbeLanguageModelAvailable()
    {
        try
        {
            return ProbeLanguageModelAvailableCore();
        }
        catch (TypeLoadException)
        {
            _logger?.LogWarning("Phi Silica probe failed: AI projection type could not load.");
            return false;
        }
        catch (FileNotFoundException ex)
        {
            _logger?.LogWarning(ex, "Phi Silica probe failed: AI projection assembly missing.");
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Phi Silica probe threw unexpectedly.");
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TryUnlockLanguageModelLafCore(out string statusLabel)
    {
        var access = Windows.ApplicationModel.LimitedAccessFeatures.TryUnlockFeature(
            LanguageModelLafFeatureId,
            LanguageModelLafToken,
            LanguageModelLafAttribution);

        switch (access.Status)
        {
            case Windows.ApplicationModel.LimitedAccessFeatureStatus.Available:
                statusLabel = "Available";
                return true;
            case Windows.ApplicationModel.LimitedAccessFeatureStatus.AvailableWithoutToken:
                statusLabel = "AvailableWithoutToken";
                return true;
            case Windows.ApplicationModel.LimitedAccessFeatureStatus.Unavailable:
                statusLabel = "Unavailable";
                return false;
            case Windows.ApplicationModel.LimitedAccessFeatureStatus.Unknown:
                statusLabel = "User not eligible";
                return false;
            default:
                statusLabel = $"Unknown LAF status ({(int)access.Status})";
                return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ProbeLanguageModelAvailableCore()
    {
        var state = Microsoft.Windows.AI.Text.LanguageModel.GetReadyState();
        return state == Microsoft.Windows.AI.AIFeatureReadyState.Ready
            || state == Microsoft.Windows.AI.AIFeatureReadyState.NotReady;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<bool> EnsureReadyCore(
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var state = Microsoft.Windows.AI.Text.LanguageModel.GetReadyState();
        if (state == Microsoft.Windows.AI.AIFeatureReadyState.Ready)
        {
            progress?.Report(1.0);
            return true;
        }

        if (state != Microsoft.Windows.AI.AIFeatureReadyState.NotReady)
        {
            _logger?.LogWarning("LanguageModel cannot be prepared from readyState={ReadyState}.", state);
            return false;
        }

        var op = Microsoft.Windows.AI.Text.LanguageModel.EnsureReadyAsync();
        if (progress is not null)
        {
            op.Progress = (_, p) => progress.Report(Math.Clamp(p, 0.0, 1.0));
        }

        using var ctReg = cancellationToken.Register(() =>
        {
            try { op.Cancel(); } catch { }
        });

        var result = await op;
        var finalState = Microsoft.Windows.AI.Text.LanguageModel.GetReadyState();
        var succeeded = result.Status == Microsoft.Windows.AI.AIFeatureReadyResultState.Success;
        if (succeeded)
        {
            progress?.Report(1.0);
            return true;
        }

        _logger?.LogWarning(
            "LanguageModel.EnsureReadyAsync failed. initialState={InitialState}, resultStatus={ResultStatus}, finalState={FinalState}, errorDisplayText={ErrorDisplayText}, error={Error}, extendedError={ExtendedError}",
            state,
            result.Status,
            finalState,
            string.IsNullOrWhiteSpace(result.ErrorDisplayText) ? "<empty>" : result.ErrorDisplayText,
            DescribeException(result.Error),
            DescribeException(result.ExtendedError));
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<AiGenerationResult> GeneratePlainTextCoreAsync(
        string prompt,
        IProgress<string>? deltaProgress,
        CancellationToken cancellationToken)
    {
        using var languageModel = await Microsoft.Windows.AI.Text.LanguageModel.CreateAsync();
        var op = languageModel.GenerateResponseAsync(prompt);
        if (deltaProgress is not null)
            op.Progress = (_, token) => deltaProgress.Report(token);

        using var ctReg = cancellationToken.Register(() =>
        {
            try { op.Cancel(); } catch { }
        });

        var result = await op;
        if (result is null)
        {
            return new AiGenerationResult(
                AiGenerationStatus.Error,
                string.Empty,
                "LanguageModel returned no result.",
                BuildPlainTextRequestDiagnostics(prompt, null),
                RawPrompt: prompt,
                RawResponseText: string.Empty);
        }

        var status = MapStatus(result.Status);
        return new AiGenerationResult(
            status,
            result.Text ?? string.Empty,
            DescribeException(result.ExtendedError),
            BuildPlainTextRequestDiagnostics(prompt, result),
            RawPrompt: prompt,
            RawResponseText: result.Text ?? string.Empty);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<AiGenerationResult> GenerateStructuredJsonCoreAsync(
        string prompt,
        string jsonSchema,
        CancellationToken cancellationToken)
    {
        using var languageModel = await Microsoft.Windows.AI.Text.LanguageModel.CreateAsync();
        var experimentalModel = new Microsoft.Windows.AI.Text.Experimental.LanguageModelExperimental(languageModel);
        var structuredPrompt = BuildStructuredPrompt(prompt);
        var op = experimentalModel.GenerateStructuredJsonResponseAsync(
            structuredPrompt,
            jsonSchema,
            new Microsoft.Windows.AI.Text.Experimental.LanguageModelOptionsExperimental());

        using var ctReg = cancellationToken.Register(() =>
        {
            try { op.Cancel(); } catch { }
        });

        var result = await op;
        if (result is null)
        {
            return new AiGenerationResult(
                AiGenerationStatus.Error,
                string.Empty,
                "LanguageModel returned no result.",
                BuildRequestDiagnostics(prompt, structuredPrompt, jsonSchema, "structured-default-options", null),
                RawPrompt: prompt,
                RawStructuredPrompt: structuredPrompt,
                RawResponseText: string.Empty);
        }

        var status = MapStatus(result.Status);
        if (status != AiGenerationStatus.Complete)
        {
            return new AiGenerationResult(
                status,
                result.Text ?? string.Empty,
                DescribeException(result.ExtendedError),
                BuildRequestDiagnostics(prompt, structuredPrompt, jsonSchema, "structured-default-options", result),
                RawPrompt: prompt,
                RawStructuredPrompt: structuredPrompt,
                RawResponseText: result.Text ?? string.Empty);
        }

        return TryExtractText(result.Text, out var text, out var parseError)
            ? new AiGenerationResult(
                status,
                text,
                RawPrompt: prompt,
                RawStructuredPrompt: structuredPrompt,
                RawResponseText: result.Text ?? string.Empty)
            : new AiGenerationResult(
                AiGenerationStatus.ResponseInvalidJson,
                result.Text ?? string.Empty,
                parseError,
                BuildRequestDiagnostics(prompt, structuredPrompt, jsonSchema, "structured-default-options", result),
                RawPrompt: prompt,
                RawStructuredPrompt: structuredPrompt,
                RawResponseText: result.Text ?? string.Empty);
    }

    private static string BuildStructuredPrompt(string prompt)
        => prompt + "\n\nReturn only a JSON object matching the provided schema.";

    private static AiGenerationStatus MapStatus(
        Microsoft.Windows.AI.Text.Experimental.GenerateStructuredJsonResponseStatus status)
        => status switch
        {
            Microsoft.Windows.AI.Text.Experimental.GenerateStructuredJsonResponseStatus.Complete => AiGenerationStatus.Complete,
            Microsoft.Windows.AI.Text.Experimental.GenerateStructuredJsonResponseStatus.InProgress => AiGenerationStatus.InProgress,
            Microsoft.Windows.AI.Text.Experimental.GenerateStructuredJsonResponseStatus.BlockedByPolicy => AiGenerationStatus.BlockedByPolicy,
            Microsoft.Windows.AI.Text.Experimental.GenerateStructuredJsonResponseStatus.PromptLargerThanContext => AiGenerationStatus.PromptLargerThanContext,
            Microsoft.Windows.AI.Text.Experimental.GenerateStructuredJsonResponseStatus.PromptBlockedByContentModeration => AiGenerationStatus.PromptBlockedByContentModeration,
            Microsoft.Windows.AI.Text.Experimental.GenerateStructuredJsonResponseStatus.ResponseBlockedByContentModeration => AiGenerationStatus.ResponseBlockedByContentModeration,
            Microsoft.Windows.AI.Text.Experimental.GenerateStructuredJsonResponseStatus.ResponseInvalidJson => AiGenerationStatus.ResponseInvalidJson,
            Microsoft.Windows.AI.Text.Experimental.GenerateStructuredJsonResponseStatus.Error => AiGenerationStatus.Error,
            _ => AiGenerationStatus.Error,
        };

    private static AiGenerationStatus MapStatus(
        Microsoft.Windows.AI.Text.LanguageModelResponseStatus status)
        => status switch
        {
            Microsoft.Windows.AI.Text.LanguageModelResponseStatus.Complete => AiGenerationStatus.Complete,
            Microsoft.Windows.AI.Text.LanguageModelResponseStatus.InProgress => AiGenerationStatus.InProgress,
            Microsoft.Windows.AI.Text.LanguageModelResponseStatus.BlockedByPolicy => AiGenerationStatus.BlockedByPolicy,
            Microsoft.Windows.AI.Text.LanguageModelResponseStatus.PromptLargerThanContext => AiGenerationStatus.PromptLargerThanContext,
            Microsoft.Windows.AI.Text.LanguageModelResponseStatus.PromptBlockedByContentModeration => AiGenerationStatus.PromptBlockedByContentModeration,
            Microsoft.Windows.AI.Text.LanguageModelResponseStatus.ResponseBlockedByContentModeration => AiGenerationStatus.ResponseBlockedByContentModeration,
            Microsoft.Windows.AI.Text.LanguageModelResponseStatus.Error => AiGenerationStatus.Error,
            _ => AiGenerationStatus.Error,
        };

    private static bool TryExtractText(string? json, out string text, out string? error)
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

    private static string ReadyStateLabel()
    {
        try
        {
            return ReadyStateLabelCore();
        }
        catch
        {
            return "Requires a Copilot+ PC";
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string ReadyStateLabelCore()
        => Microsoft.Windows.AI.Text.LanguageModel.GetReadyState() switch
        {
            Microsoft.Windows.AI.AIFeatureReadyState.Ready => "Ready",
            Microsoft.Windows.AI.AIFeatureReadyState.NotReady => "Available - first use will download the model",
            Microsoft.Windows.AI.AIFeatureReadyState.DisabledByUser => "Disabled in Windows AI settings",
            Microsoft.Windows.AI.AIFeatureReadyState.OSUpdateNeeded => "A Windows update is required",
            Microsoft.Windows.AI.AIFeatureReadyState.NotCompatibleWithSystemHardware => "Requires a Copilot+ PC",
            Microsoft.Windows.AI.AIFeatureReadyState.NotSupportedOnCurrentSystem => "Requires a Copilot+ PC",
            Microsoft.Windows.AI.AIFeatureReadyState.CapabilityMissing => "AI capability missing",
            _ => "Requires a Copilot+ PC",
        };

    private static AiGenerationResult ToExceptionResult(string operation, Exception ex)
        => ex switch
        {
            TypeLoadException => AiGenerationResult.Unavailable($"{operation}: AI projection type could not load."),
            FileNotFoundException => AiGenerationResult.Unavailable($"{operation}: AI projection assembly missing."),
            UnauthorizedAccessException => AiGenerationResult.Unavailable($"{operation}: Windows denied access to the LanguageModel limited access feature."),
            _ => new AiGenerationResult(AiGenerationStatus.Error, string.Empty, ex.Message),
        };

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
               ", resultType=" + (result?.GetType().FullName ?? "<null>");
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
               ", resultType=" + (result?.GetType().FullName ?? "<null>");
    }
}
