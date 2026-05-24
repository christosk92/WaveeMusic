using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.UI.WinUI.Helpers.Application;

namespace Wavee.UI.WinUI.Services;

internal static class PhiSilicaStructuredTextPipeline
{
    private const string PolicySanityProbeOperation = "PhiSilicaPolicySanityProbe";
    private const string PolicySanityProbePrompt =
        "Provide the molecular formula for glucose in one short sentence.";

    private static int _policySanityProbeStarted;

    public static async Task<LyricsAiResult> AwaitRequestAsync(
        Lazy<Task<LyricsAiResult>> request,
        bool fromExistingRequest,
        Action removeRequest,
        string operation,
        ILogger? logger,
        CancellationToken ct)
    {
        try
        {
            var result = await request.Value.WaitAsync(ct);
            if (result.Kind != LyricsAiResultKind.Ok)
                removeRequest();

            return result.Kind == LyricsAiResultKind.Ok
                ? result.WithCacheState(fromExistingRequest || result.FromCache)
                : result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            removeRequest();
            return ToExceptionResult(operation, logger, ex);
        }
    }

    public static async Task<LyricsAiResult> GenerateAsync(
        PhiSilicaStructuredTextRequest request,
        ILogger? logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return await Task.Run(
            () => GenerateCoreAsync(request, logger, ct),
            ct).ConfigureAwait(false);
    }

    private static async Task<LyricsAiResult> GenerateCoreAsync(
        PhiSilicaStructuredTextRequest request,
        ILogger? logger,
        CancellationToken ct)
    {
        try
        {
            string? lastDumpPath = null;

            var response = await GenerateModelCallAsync(
                request.Prompt,
                request,
                logger,
                "tier1-main",
                ct);
            if (response.Status != PhiSilicaStructuredGenerationStatus.Complete)
                lastDumpPath = TryWriteDiagnosticDump(request.Operation, "tier1-main", response);

            var usedFallback = false;
            if (ShouldRetryWithFallback(response, request))
            {
                response = await GenerateFallbackAsync(request, logger, response.Status, ct);
                usedFallback = true;
                if (response.Status != PhiSilicaStructuredGenerationStatus.Complete)
                    lastDumpPath = TryWriteDiagnosticDump(request.Operation, "tier2-fallback", response);
            }

            if (response.Status != PhiSilicaStructuredGenerationStatus.Complete)
            {
                if (response.Status == PhiSilicaStructuredGenerationStatus.BlockedByPolicy)
                    await RunPolicySanityProbeAsync(logger);

                return ToFailureResult(request, logger, response, lastDumpPath);
            }

            var text = request.CleanText(response.Text);

            // The empty-text retry only matters for schemas that emit user-facing
            // prose in a top-level `text` property. When BuildSuccessResult is set
            // (citation schema), the builder reconstructs prose from the structured
            // payload itself, so empty `response.Text` is expected and we hand off
            // immediately without burning another model call.
            if (string.IsNullOrWhiteSpace(text)
                && !usedFallback
                && request.BuildSuccessResult is null)
            {
                logger?.LogInformation(
                    "{Operation} retrying after Phi Silica returned no usable text.",
                    request.Operation);

                response = await GenerateModelCallAsync(
                    request.FallbackPrompt,
                    request,
                    logger,
                    "tier2-fallback-empty",
                    ct);

                if (response.Status != PhiSilicaStructuredGenerationStatus.Complete)
                {
                    lastDumpPath = TryWriteDiagnosticDump(request.Operation, "tier2-fallback", response);
                    if (response.Status == PhiSilicaStructuredGenerationStatus.BlockedByPolicy)
                        await RunPolicySanityProbeAsync(logger);

                    return ToFailureResult(request, logger, response, lastDumpPath);
                }

                text = request.CleanText(response.Text);
            }

            if (request.BuildSuccessResult is { } buildSuccess)
            {
                var built = buildSuccess(response, text.Trim());
                if (built.Kind == LyricsAiResultKind.Error)
                {
                    // The model returned Complete and the JSON parsed against the
                    // schema, but our domain validation rejected it. Dump the raw
                    // payload so we can see what shape the model actually emitted.
                    var dumpPath = TryWriteDiagnosticDump(request.Operation, "tier1-builder-rejected", response);
                    logger?.LogWarning(
                        "{Operation} structured payload was Complete but builder rejected it. error={Error}; dump={DumpPath}",
                        request.Operation,
                        built.ErrorMessage ?? "<none>",
                        dumpPath ?? "<not written>");
                }
                else if (built.IsSuccess && !built.HasCitations)
                {
                    // Builder accepted the payload but produced no citations —
                    // either the model emitted none, validation dropped them all,
                    // or no segment's citationLine could be linked to a citation.
                    // The user sees a paragraph with no underlines; the dump is
                    // the only way to tell which of those three things happened.
                    var dumpPath = TryWriteDiagnosticDump(request.Operation, "tier1-no-citations", response);
                    logger?.LogWarning(
                        "{Operation} returned Ok with zero linked citations. responseChars={ResponseChars}; dump={DumpPath}",
                        request.Operation,
                        response.RawResponseText?.Length ?? 0,
                        dumpPath ?? "<not written>");
                }

                return built;
            }

            return string.IsNullOrWhiteSpace(text)
                ? LyricsAiResult.Error(request.EmptyResultErrorMessage)
                : LyricsAiResult.Ok(text.Trim(), fromCache: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToExceptionResult(request.Operation, logger, ex);
        }
    }

    public static string ClampLength(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max)
            return s.Trim();

        var truncated = s[..max];
        var lastSpace = truncated.LastIndexOf(' ');
        if (lastSpace > max / 2)
            truncated = truncated[..lastSpace];

        return truncated.TrimEnd('.', ',', ';', ':') + "...";
    }

    private static async Task<PhiSilicaStructuredGenerationResult> GenerateFallbackAsync(
        PhiSilicaStructuredTextRequest request,
        ILogger? logger,
        PhiSilicaStructuredGenerationStatus previousStatus,
        CancellationToken ct)
    {
        logger?.LogInformation(
            "{Operation} retrying with fallback prompt after Phi Silica status {Status}.",
            request.Operation,
            previousStatus);

        return await GenerateModelCallAsync(
            request.FallbackPrompt,
            request,
            logger,
            "tier2-fallback",
            ct);
    }

    // Single chokepoint for every model call this pipeline issues, so we can
    // see exactly how long each call takes and how much JSON came back. This is
    // the only knob we have for diagnosing the >4 min whole-song generations:
    // Phi Silica's experimental options surface does not expose a
    // MaxGeneratedTokens cap, so output token count is the dominant cost
    // and we want it visible per call.
    private static async Task<PhiSilicaStructuredGenerationResult> GenerateModelCallAsync(
        string prompt,
        PhiSilicaStructuredTextRequest request,
        ILogger? logger,
        string tier,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var response = await PhiSilicaStructuredTextGenerator.GenerateTextAsync(
            prompt,
            request.Temperature,
            request.JsonSchema,
            ct);
        sw.Stop();

        logger?.LogInformation(
            "{Operation} {Tier}: {ElapsedMs} ms, status={Status}, promptChars={PromptChars}, responseChars={ResponseChars}",
            request.Operation,
            tier,
            sw.ElapsedMilliseconds,
            response.Status,
            prompt.Length,
            response.RawResponseText?.Length ?? 0);

        return response;
    }

    private static async Task RunPolicySanityProbeAsync(ILogger? logger)
    {
        if (Interlocked.Exchange(ref _policySanityProbeStarted, 1) != 0)
            return;

        try
        {
            var probeSw = Stopwatch.StartNew();
            var probe = await PhiSilicaStructuredTextGenerator.GenerateTextAsync(
                PolicySanityProbePrompt,
                0.0f,
                CancellationToken.None);
            probeSw.Stop();
            logger?.LogInformation(
                "{Operation} structured: {ElapsedMs} ms, status={Status}",
                PolicySanityProbeOperation,
                probeSw.ElapsedMilliseconds,
                probe.Status);

            var dumpPath = probe.Status == PhiSilicaStructuredGenerationStatus.Complete
                ? null
                : TryWriteDiagnosticDump(PolicySanityProbeOperation, "neutral-structured", probe);

            if (probe.Status == PhiSilicaStructuredGenerationStatus.Complete)
            {
                logger?.LogInformation(
                    "Phi Silica policy sanity probe succeeded after a BlockedByPolicy result. The original request was likely prompt/content-specific.");
                return;
            }

            logger?.LogWarning(
                "Phi Silica structured policy sanity probe returned status {Status}. A neutral structured prompt is blocked too. error={ErrorMessage}; diagnostics={Diagnostics}; dump={DumpPath}",
                probe.Status,
                string.IsNullOrWhiteSpace(probe.ErrorMessage) ? "<no extended error>" : probe.ErrorMessage,
                string.IsNullOrWhiteSpace(probe.DiagnosticMessage) ? "<none>" : probe.DiagnosticMessage,
                dumpPath ?? "<not written>");

            var plainSw = Stopwatch.StartNew();
            var plainProbe = await PhiSilicaStructuredTextGenerator.GeneratePlainTextAsync(
                PolicySanityProbePrompt,
                CancellationToken.None);
            plainSw.Stop();
            logger?.LogInformation(
                "{Operation} plaintext: {ElapsedMs} ms, status={Status}",
                PolicySanityProbeOperation,
                plainSw.ElapsedMilliseconds,
                plainProbe.Status);

            var plainDumpPath = plainProbe.Status == PhiSilicaStructuredGenerationStatus.Complete
                ? null
                : TryWriteDiagnosticDump(PolicySanityProbeOperation, "neutral-plaintext", plainProbe);

            if (plainProbe.Status == PhiSilicaStructuredGenerationStatus.Complete)
            {
                logger?.LogWarning(
                    "Phi Silica plaintext policy sanity probe succeeded. The system can generate text, but the structured JSON path is blocked.");
                return;
            }

            logger?.LogWarning(
                "Phi Silica plaintext policy sanity probe returned status {Status}. A neutral plaintext prompt is blocked too, so the failure is likely system/user policy. error={ErrorMessage}; diagnostics={Diagnostics}; dump={DumpPath}",
                plainProbe.Status,
                string.IsNullOrWhiteSpace(plainProbe.ErrorMessage) ? "<no extended error>" : plainProbe.ErrorMessage,
                string.IsNullOrWhiteSpace(plainProbe.DiagnosticMessage) ? "<none>" : plainProbe.DiagnosticMessage,
                plainDumpPath ?? "<not written>");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Phi Silica policy sanity probe failed before returning a model status.");
        }
    }

    private static LyricsAiResult ToFailureResult(
        PhiSilicaStructuredTextRequest request,
        ILogger? logger,
        PhiSilicaStructuredGenerationResult generated,
        string? lastDumpPath)
    {
        request.ObserveTerminalStatus?.Invoke(generated.Status);

        logger?.LogWarning(
            "{Operation} returned Phi Silica status {Status}. error={ErrorMessage}; diagnostics={Diagnostics}; dump={DumpPath}",
            request.Operation,
            generated.Status,
            string.IsNullOrWhiteSpace(generated.ErrorMessage) ? "<no extended error>" : generated.ErrorMessage,
            string.IsNullOrWhiteSpace(generated.DiagnosticMessage) ? "<none>" : generated.DiagnosticMessage,
            lastDumpPath ?? "<not written>");

        return generated.Status switch
        {
            PhiSilicaStructuredGenerationStatus.BlockedByPolicy => LyricsAiResult.Filtered,
            PhiSilicaStructuredGenerationStatus.PromptBlockedByContentModeration => LyricsAiResult.Filtered,
            PhiSilicaStructuredGenerationStatus.ResponseBlockedByContentModeration => LyricsAiResult.Filtered,
            PhiSilicaStructuredGenerationStatus.PromptLargerThanContext =>
                LyricsAiResult.Error("Prompt exceeded Phi Silica's context window."),
            PhiSilicaStructuredGenerationStatus.ResponseInvalidJson =>
                LyricsAiResult.Error("Phi Silica returned text that did not match the expected JSON schema."),
            _ => LyricsAiResult.Error(generated.ErrorMessage ?? generated.Status.ToString()),
        };
    }

    private static bool ShouldRetryWithFallback(
        PhiSilicaStructuredGenerationResult generated,
        PhiSilicaStructuredTextRequest request)
    {
        if (generated.Status is PhiSilicaStructuredGenerationStatus.PromptLargerThanContext
            or PhiSilicaStructuredGenerationStatus.PromptBlockedByContentModeration
            or PhiSilicaStructuredGenerationStatus.ResponseBlockedByContentModeration
            or PhiSilicaStructuredGenerationStatus.BlockedByPolicy
            or PhiSilicaStructuredGenerationStatus.ResponseInvalidJson)
        {
            return true;
        }

        // Complete-but-empty-text triggers retry only for schemas that emit prose
        // in a top-level `text` property. The citation schema reconstructs prose
        // from segments via BuildSuccessResult, so empty response.Text is the
        // expected steady-state and must NOT spend a second model call.
        return generated.Status == PhiSilicaStructuredGenerationStatus.Complete
               && string.IsNullOrWhiteSpace(generated.Text)
               && request.BuildSuccessResult is null;
    }

    private static string? TryWriteDiagnosticDump(
        string operation,
        string tier,
        PhiSilicaStructuredGenerationResult generated)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.PhiSilicaDiagnosticsDirectory);

            // Milliseconds (fff) so back-to-back dumps in the same second
            // (tier1-main + tier2-fallback of one request) get distinct filenames.
            var fileName = string.Format(
                CultureInfo.InvariantCulture,
                "{0:yyyyMMdd-HHmmssfff}-{1}-{2}-{3}.txt",
                DateTime.Now,
                SanitizeForFileName(operation),
                SanitizeForFileName(tier),
                SanitizeForFileName(generated.Status.ToString()));
            var path = Path.Combine(AppPaths.PhiSilicaDiagnosticsDirectory, fileName);

            var prompt = generated.RawPrompt ?? "<unavailable>";
            var structuredPrompt = generated.RawStructuredPrompt ?? "<n/a — plain-text path>";
            var response = string.IsNullOrEmpty(generated.RawResponseText) ? "<empty>" : generated.RawResponseText;

            var sb = new StringBuilder();
            sb.Append("operation: ").AppendLine(operation);
            sb.Append("tier: ").AppendLine(tier);
            sb.Append("status: ").AppendLine(generated.Status.ToString());
            sb.Append("extendedError: ").AppendLine(
                string.IsNullOrWhiteSpace(generated.ErrorMessage) ? "<none>" : generated.ErrorMessage);
            sb.Append("diagnostics: ").AppendLine(
                string.IsNullOrWhiteSpace(generated.DiagnosticMessage) ? "<none>" : generated.DiagnosticMessage);
            sb.AppendLine();
            sb.Append("--- prompt (").Append((generated.RawPrompt ?? string.Empty).Length).AppendLine(" chars) ---");
            sb.AppendLine(prompt);
            sb.AppendLine();
            sb.Append("--- structuredPrompt (").Append((generated.RawStructuredPrompt ?? string.Empty).Length).AppendLine(" chars) ---");
            sb.AppendLine(structuredPrompt);
            sb.AppendLine();
            sb.Append("--- response (").Append((generated.RawResponseText ?? string.Empty).Length).AppendLine(" chars) ---");
            sb.AppendLine(response);

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static string SanitizeForFileName(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.ToString();
    }

    private static LyricsAiResult ToExceptionResult(string operation, ILogger? logger, Exception ex)
    {
        switch (ex)
        {
            case TypeLoadException:
                logger?.LogWarning(
                    ex,
                    "{Operation} hit TypeLoadException; AI projection assembly missing at runtime.",
                    operation);
                return LyricsAiResult.Unavailable;

            case FileNotFoundException:
                logger?.LogWarning(
                    ex,
                    "{Operation} hit FileNotFoundException; AI projection assembly missing at runtime.",
                    operation);
                return LyricsAiResult.Unavailable;

            case UnauthorizedAccessException:
                logger?.LogWarning(
                    ex,
                    "{Operation} hit UnauthorizedAccessException; Windows denied access to the LanguageModel limited access feature.",
                    operation);
                return LyricsAiResult.Unavailable;

            default:
                logger?.LogWarning(ex, "{Operation} failed.", operation);
                return LyricsAiResult.Error(ex.Message);
        }
    }
}

internal sealed record PhiSilicaStructuredTextRequest(
    string Operation,
    string Prompt,
    string FallbackPrompt,
    float Temperature,
    Func<string, string> CleanText,
    string EmptyResultErrorMessage)
{
    public string JsonSchema { get; init; } = PhiSilicaStructuredTextGenerator.TextResultJsonSchema;
    public Func<PhiSilicaStructuredGenerationResult, string, LyricsAiResult>? BuildSuccessResult { get; init; }
    public Action<PhiSilicaStructuredGenerationStatus>? ObserveTerminalStatus { get; init; }
}
