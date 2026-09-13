using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.Backend.Spotify;

// The real IHttpExchange over the shared HttpClient (the production transport for spclient/Pathfinder). Response headers
// are exposed case-insensitively (HTTP/2 lowercases them) so middleware lookups (Retry-After, etc.) hit. Request bodies
// are pre-built bytes (e.g. a gzipped protobuf); Content-Type/Content-Encoding ride on the content headers.
public sealed class HttpClientExchange : IHttpExchange
{
    readonly HttpClient _http;

    public HttpClientExchange(HttpClient? http = null)
    {
        _http = http ?? HttpPools.Get(HttpPool.ControlPlane);
    }

    public async Task<HttpResp> SendAsync(HttpReq req, CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(new HttpMethod(req.Method), req.Url);
        if (req.Body is { } body) msg.Content = new ByteArrayContent(body);
        foreach (var (k, v) in req.Headers)
            if (!msg.Headers.TryAddWithoutValidation(k, v))   // content headers (Content-Type/-Encoding) fall through to here
                msg.Content?.Headers.TryAddWithoutValidation(k, v);

        // ResponseHeadersRead: status + headers are available WITHOUT buffering the body — we stream the (auto-decompressed)
        // body straight into the protobuf parser, so a multi-MB metadata batch never materializes as one LOH byte[].
        var resp = await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        try
        {
            LogProtocolOnce(resp);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in resp.Headers) headers[h.Key] = string.Join(",", h.Value);
            foreach (var h in resp.Content.Headers) headers[h.Key] = string.Join(",", h.Value);
            var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return new HttpResp((int)resp.StatusCode, headers, stream, owner: resp);   // ownership transfers here
        }
        catch { resp.Dispose(); throw; }   // a throw before ownership transfers (e.g. ct cancels) must not leak the connection
    }

    static void LogProtocolOnce(HttpResponseMessage resp)
    {
        if (resp.RequestMessage?.RequestUri?.Host is not { Length: > 0 } host) return;
        if (!ProtocolHosts.TryAdd(host, 0)) return;   // once per host — the log level gates whether it surfaces
        WaveeLog.Instance.Debug("spclient", $"http {host} protocol={resp.Version}");
    }

    static readonly ConcurrentDictionary<string, byte> ProtocolHosts = new(StringComparer.OrdinalIgnoreCase);
}
