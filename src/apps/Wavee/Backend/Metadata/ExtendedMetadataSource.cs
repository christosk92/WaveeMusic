using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Backend.Metadata;

// Finite extended-metadata HTTP transport. Resource policy, projection and durability belong to the catalog.
public sealed class ExtendedMetadataSource
{
    const string Path = "/extended-metadata/v0/extended-metadata";

    readonly IHttpExchange _http;
    readonly Func<string> _baseUrl;
    readonly Func<SessionContext> _ctx;
    public SessionContext Session => _ctx();

    public ExtendedMetadataSource(IHttpExchange http, Func<string> baseUrl, Func<SessionContext> ctx)
    {
        _http = http;
        _baseUrl = baseUrl;
        _ctx = ctx;
    }

    // ── Arbitrary-kind reads (feature payloads beyond bulk Track/Album/Artist hydration) ──────────────────────────────
    // Same endpoint, auth pipeline, protobuf envelope and gzip framing as FetchAsync, but the caller chooses the
    // ExtensionKind per entity and gets the RAW extension payload back (parsed by the feature, NOT projected into the
    // Store here). E.g. an album's RECOMMENDED_PLAYLISTS (151) refs, then those playlists' LIST_METADATA_V2 (205) heroes.
    static readonly IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), ByteString> NoExtensions
        = new Dictionary<(string, Xm.ExtensionKind), ByteString>();

    public async Task<IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), ByteString>> GetExtensionsAsync(
        IReadOnlyList<(string Uri, Xm.ExtensionKind Kind)> requests, CancellationToken ct = default, string? clientFeatureId = null)
    {
        if (requests.Count == 0) return NoExtensions;
        var queries = new (string Uri, Xm.ExtensionKind Kind, string? Etag)[requests.Count];
        for (int i = 0; i < requests.Count; i++) queries[i] = (requests[i].Uri, requests[i].Kind, null);
        var answers = await GetExtensionsWithHeadersAsync(queries, ct, clientFeatureId).ConfigureAwait(false);
        var result = new Dictionary<(string, Xm.ExtensionKind), ByteString>();
        foreach (var pair in answers)
        {
            if (pair.Value.Status == 200 && pair.Value.Payload is { } payload) result[pair.Key] = payload;
            else if (pair.Value.Status != 404) throw new System.Net.Http.HttpRequestException(
                $"Extended metadata extension failed ({pair.Value.Status}).", null,
                pair.Value.Status > 0 ? (System.Net.HttpStatusCode)pair.Value.Status : null);
        }
        return result;
    }

    /// <summary>Convenience for a single (uri, kind) read; null when the entity carried no such extension.</summary>
    public async Task<ByteString?> GetExtensionAsync(string uri, Xm.ExtensionKind kind, CancellationToken ct = default, string? clientFeatureId = null)
    {
        var values = await GetExtensionsAsync(new[] { (uri, kind) }, ct, clientFeatureId).ConfigureAwait(false);
        return values.TryGetValue((uri, kind), out var value) ? value : null;
    }

    // ── Conditional reads (etag + 304) ────────────────────────────────────────────────────────────────────────────────
    // Like GetExtensionsAsync, but the caller passes the etag it last cached per (uri, kind) — sent as ExtensionQuery.etag
    // so the server can answer 304 (not-modified) — and gets back the per-entity status_code + (new) etag + offline TTL,
    // not just the 200 payload. This is the "cache it like a normal extended-metadata thing" path: 200 = fresh payload,
    // 304 = keep cached, 404 = no such extension. Large request lists are chunked by body size (one POST is not unbounded).
    public readonly record struct ExtensionResult(int Status, string? Etag, long OfflineTtlSeconds, ByteString? Payload);

    static readonly IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), ExtensionResult> NoResults
        = new Dictionary<(string, Xm.ExtensionKind), ExtensionResult>();

    public async Task<IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), ExtensionResult>> GetExtensionsWithHeadersAsync(
        IReadOnlyList<(string Uri, Xm.ExtensionKind Kind, string? Etag)> requests, CancellationToken ct = default, string? clientFeatureId = null)
    {
        if (requests.Count == 0) return NoResults;
        var session = _ctx();
        var result = new Dictionary<(string, Xm.ExtensionKind), ExtensionResult>(requests.Count);
        foreach (var (start, count) in MetadataChunking.ExtensionRanges(requests))
        {
            using var resp = await SendAsync(GzipExtensionRequest(requests, start, count, session), ct, clientFeatureId).ConfigureAwait(false);
            if (resp.Status != 200) throw new System.Net.Http.HttpRequestException(
                $"extended-metadata fetch failed ({resp.Status})", null, (System.Net.HttpStatusCode)resp.Status);
            var parsed = Xm.BatchedExtensionResponse.Parser.ParseFrom(resp.Body);   // streamed, no LOH byte[]
            foreach (var array in parsed.ExtendedMetadata)
            {
                long arrayOfflineTtl = array.Header?.OfflineTtlInSeconds ?? 0;   // per-array fallback for the per-entity TTL
                foreach (var data in array.ExtensionData)
                {
                    var hdr = data.Header;
                    int status = hdr is { HasStatusCode: true } ? hdr.StatusCode : (data.ExtensionData is null ? 0 : 200);
                    string? etag = hdr is { HasEtag: true, Etag.Length: > 0 } ? hdr.Etag : null;
                    long offlineTtl = hdr is { HasOfflineTtlInSeconds: true } ? hdr.OfflineTtlInSeconds : arrayOfflineTtl;
                    // Empty protobuf messages (for example descriptors) are successful bytes, not absence.
                    ByteString? payload = data.ExtensionData?.Value;
                    result[(data.EntityUri, array.ExtensionKind)] = new ExtensionResult(status, etag, offlineTtl, payload);
                }
            }
        }
        return result;
    }

    // The conditional sibling of GzipExtensionRequest(requests, ctx): builds one chunk [start, start+count) and sets
    // ExtensionQuery.etag when the caller cached one (so the server can 304). Multiple kinds under a uri group as before.
    static byte[] GzipExtensionRequest(IReadOnlyList<(string Uri, Xm.ExtensionKind Kind, string? Etag)> requests,
        int start, int count, SessionContext ctx)
    {
        Span<byte> taskId = stackalloc byte[16];
        RandomNumberGenerator.Fill(taskId);
        var request = new Xm.BatchedEntityRequest
        {
            Header = new Xm.BatchedEntityRequestHeader { Country = ctx.Market, Catalogue = ctx.Catalogue, TaskId = ByteString.CopyFrom(taskId) },
        };
        var byUri = new Dictionary<string, Xm.EntityRequest>(StringComparer.Ordinal);
        for (int i = start; i < start + count; i++)
        {
            var (uri, kind, etag) = requests[i];
            if (!byUri.TryGetValue(uri, out var er))
            {
                er = new Xm.EntityRequest { EntityUri = uri };
                byUri[uri] = er;
                request.EntityRequest.Add(er);
            }
            var query = new Xm.ExtensionQuery { ExtensionKind = kind };
            if (!string.IsNullOrEmpty(etag)) query.Etag = etag;
            er.Query.Add(query);
        }

        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true)) request.WriteTo(gz);
        return ms.ToArray();
    }

    // clientFeatureId (default null) stamps the desktop client's `client-feature-id` attribution header (e.g.
    // "mdata_esperanto" from the recents viewport hydrator). Null = header omitted = current behaviour unchanged.
    async Task<HttpResp> SendAsync(byte[] gzippedBody, CancellationToken ct, string? clientFeatureId = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/protobuf",
            ["Content-Encoding"] = "gzip",
            ["Accept"] = "application/protobuf",
            ["Accept-Encoding"] = "gzip, deflate, br",
            ["Accept-Language"] = SpotifyHeaders.NormalizeLanguage(_ctx().Locale),
        };
        if (!string.IsNullOrEmpty(clientFeatureId)) headers["client-feature-id"] = clientFeatureId;
        return await _http.SendAsync(new HttpReq("POST", _baseUrl() + Path, headers, gzippedBody), ct).ConfigureAwait(false);
    }

}
