using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee;

/// <summary>Postman-style request runner for <see cref="ApiConsolePage"/>.</summary>
static class ApiDebugExecutor
{
    public readonly record struct Result(
        bool Ok, int Status, string Elapsed, string? Error,
        IReadOnlyDictionary<string, string> ResponseHeaders, byte[] Body,
        string? ResponseContentType, string? RequestPreview);

    public static readonly string[] ChannelLabels = ["Spclient", "SpclientWg"];
    public static readonly string[] MethodLabels = ["GET", "POST", "PUT", "DELETE", "PATCH"];

    public static async Task<Result> SendAsync(
        Services svc,
        int channelIndex,
        string method,
        string urlOrPath,
        string headersText,
        int bodyMode,
        string bodyText,
        bool gzipTextBody,
        bool bulkHydration,
        CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var headers = ApiDebugBodyBuilder.ParseHeaders(headersText);
            string? preview = null;
            byte[]? body = BuildRequestBody(svc, bodyMode, bodyText, gzipTextBody, bulkHydration, headers, out preview, out string? buildError);
            if (buildError is not null) return Fail(sw, buildError);

            bool absolute = urlOrPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                         || urlOrPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

            if (svc.LiveHttp is { } http)
            {
                string url = ResolveUrl(svc, urlOrPath, absolute);
                if (url.Length == 0) return Fail(sw, "Not connected — log in with the real backend first.");
                return await SendHttpAsync(http, method, url, headers, body, preview, sw, ct).ConfigureAwait(false);
            }

            if (svc.MutTransport is { } transport)
            {
                if (absolute) return Fail(sw, "Relative path required for channel transport (or log in for HTTP pipeline).");
                var ch = channelIndex == 1 ? Channel.SpclientWg : Channel.Spclient;
                var resp = await transport.Request(ch, urlOrPath, body ?? ReadOnlyMemory<byte>.Empty, ct,
                    method: method, headers: headers.Count > 0 ? headers : null).ConfigureAwait(false);
                sw.Stop();
                return new Result(resp.Ok, resp.Status, $"{sw.ElapsedMilliseconds} ms", null,
                    EmptyHeaders, resp.Body, GuessContentType(headers), preview);
            }

            return Fail(sw, "No live session — log in with --real-backend first.");
        }
        catch (Exception ex)
        {
            sw.Stop();
            svc.Log.Event(WaveeLogLevel.Warning, "probe", "api.debug.failed", "API debug request failed", ex: ex);
            return Fail(sw, ex.Message);
        }
    }

    static string ResolveUrl(Services svc, string urlOrPath, bool absolute)
    {
        if (absolute) return urlOrPath;
        var baseUrl = svc.RealSpclientBaseUrl?.Value ?? "";
        if (baseUrl.Length == 0) return "";
        return baseUrl.TrimEnd('/') + (urlOrPath.StartsWith('/') ? urlOrPath : "/" + urlOrPath);
    }

    static byte[]? BuildRequestBody(Services svc, int bodyMode, string bodyText, bool gzipText, bool bulkHydration,
        Dictionary<string, string> headers, out string? preview, out string? error)
    {
        preview = null;
        error = null;
        if (bodyMode == 0) return null;

        if (bodyMode == 1)
        {
            var bytes = ApiDebugBodyBuilder.BuildTextBody(bodyText, gzipText, out error);
            if (error is not null) return null;
            if (gzipText && bytes is { Length: > 0 })
                headers.TryAdd("Content-Encoding", "gzip");
            preview = bytes is { Length: > 0 } ? $"text body · {bytes.Length:N0} bytes{(gzipText ? " (gzip)" : "")}" : "empty text body";
            return bytes;
        }

        // extended-metadata
        var session = svc.RealSessionHost?.Current;
        if (session is null) { error = "No session context — log in first."; return null; }

        ApiDebugBodyBuilder.ApplyExtendedMetadataHeaders(headers);

        if (bulkHydration)
        {
            var (gz, err) = ApiDebugBodyBuilder.BuildBulkHydration(bodyText, session);
            if (err is not null) { error = err; return null; }
            preview = $"bulk hydration · {gz!.Length:N0} bytes gzip";
            return gz;
        }

        var (lines, parseErr) = ApiDebugBodyBuilder.ParseEntityLines(bodyText);
        if (parseErr is not null) { error = parseErr; return null; }
        var (gzipped, plain, buildErr) = ApiDebugBodyBuilder.BuildExtendedMetadata(lines, session);
        if (buildErr is not null) { error = buildErr; return null; }
        preview = ApiDebugProto.ForDisplay($"BatchedEntityRequest → gzip ({gzipped.Length:N0} B)\n{JsonFormatter.ToDiagnosticString(plain)}");
        return gzipped;
    }

    static async Task<Result> SendHttpAsync(IHttpExchange http, string method, string url,
        IReadOnlyDictionary<string, string> headers, byte[]? body, string? preview,
        System.Diagnostics.Stopwatch sw, CancellationToken ct)
    {
        using var resp = await http.SendAsync(new HttpReq(method, url, headers, body), ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        await resp.Body.CopyToAsync(ms, ct).ConfigureAwait(false);
        sw.Stop();
        string? contentType = null;
        foreach (var kv in resp.Headers)
            if (kv.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) { contentType = kv.Value; break; }
        return new Result(resp.Status is >= 200 and < 300, resp.Status, $"{sw.ElapsedMilliseconds} ms", null,
            resp.Headers, ms.ToArray(), contentType, preview);
    }

    static string? GuessContentType(IReadOnlyDictionary<string, string> reqHeaders)
    {
        foreach (var kv in reqHeaders)
            if (kv.Key.Equals("Accept", StringComparison.OrdinalIgnoreCase)) return kv.Value.Split(',')[0].Trim();
        foreach (var kv in reqHeaders)
            if (kv.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }

    static Result Fail(System.Diagnostics.Stopwatch sw, string message)
    {
        sw.Stop();
        return new Result(false, 0, $"{sw.ElapsedMilliseconds} ms", message, EmptyHeaders, Array.Empty<byte>(), null, null);
    }

    static readonly IReadOnlyDictionary<string, string> EmptyHeaders
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
