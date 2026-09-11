using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Catalog;
using Wavee.Backend.Metadata;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Wavee.Core.Catalog;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.SpotifyLive.Catalog;

/// <summary>Finite provider recipes return typed observations. The repository alone accepts and persists them.</summary>
public sealed partial class SpotifyCatalogResourceProvider : ICatalogResourceProvider
{
    readonly ExtendedMetadataSource _xm;
    readonly ICatalogTransportReader _transportBytes;
    readonly PathfinderClient _pathfinder;
    readonly IHttpExchange _http;
    readonly Func<string> _baseUrl;
    readonly Func<HomeModuleTitles> _homeTitles;
    readonly TimeProvider _time;
    readonly SessionContext _session;
    public string Provider => "spotify";

    public SpotifyCatalogResourceProvider(ExtendedMetadataSource xm, ICatalogTransportReader transportBytes,
        PathfinderClient pathfinder, IHttpExchange http, Func<string> spclientBaseUrl,
        Func<HomeModuleTitles> homeTitles, TimeProvider time)
    {
        _xm = xm ?? throw new ArgumentNullException(nameof(xm));
        _session = xm.Session;
        _transportBytes = transportBytes ?? throw new ArgumentNullException(nameof(transportBytes));
        _pathfinder = pathfinder ?? throw new ArgumentNullException(nameof(pathfinder));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _baseUrl = spclientBaseUrl ?? throw new ArgumentNullException(nameof(spclientBaseUrl));
        _homeTitles = homeTitles ?? throw new ArgumentNullException(nameof(homeTitles));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    const int FetchParallelism = 4;

    public string BatchGroup(ResourceKey key) => SpotifyCatalogDecoder.ExtensionFor(key) is not null ? "extended-metadata"
        : key.Facet is FacetKind.AlbumDetail or FacetKind.AlbumVersions ? "album-envelope"
        : "envelope:" + key.Facet;

    public async ValueTask<IReadOnlyList<ResourceResponse>> FetchAsync(IReadOnlyList<ResourceRequest> requests, CancellationToken ct)
    {
        if (requests.Count == 0) return Array.Empty<ResourceResponse>();
        // A target can be replaced while an older dispatch is queued. The authenticated transport
        // must never answer another account or context, even before its epoch transition publishes.
        if (requests.Any(request => !MatchesSession(request)))
            return requests.Select(request => Failed(request,
                new(ResourceErrorKind.Forbidden, "The resource scope does not match the installed provider session."))).ToArray();
        var responses = new ConcurrentDictionary<long, ResourceResponse>();
        // A document kind asked of the wrong entity kind is Unsupported for THAT key only — never put on the wire, where
        // one such pair fails the whole 300-subject POST (HTTP 400) and every legitimate subject riding in it.
        foreach (var request in requests)
            if (!SpotifyCatalogDecoder.SubjectMatches(request.Key))
                responses[request.Stamp.RequestId] = new(request, new ResourceFetchResult(ResourceFetchStatus.Unsupported));
        var xmRequests = requests.Where(request => SpotifyCatalogDecoder.ExtensionFor(request.Key) is not null
            && !responses.ContainsKey(request.Stamp.RequestId)).ToArray();
        if (xmRequests.Length > 0)
        {
            try
            {
                var keys = xmRequests.Select(request => (request.Key.Subject, Kind: SpotifyCatalogDecoder.ExtensionFor(request.Key)!.Value))
                    .Concat(xmRequests.Where(request => request.Key.Facet == FacetKind.VideoAssociation)
                        .Select(request => (request.Key.Subject, Xm.ExtensionKind.ConsumptionExperienceTrait)))
                    .Distinct().ToArray();
                var attribution = requests[0].ClientFeatureId
                    ?? (keys.Any(key => key.Item2 is Xm.ExtensionKind.CreditsV2Trait or Xm.ExtensionKind.Prerelease)
                        ? "track_metadata_loader" : "mdata_esperanto");
                var raw = await FetchRawAsync(requests[0].Key.Scope, keys, attribution, ct).ConfigureAwait(false);
                // One immutable transport body may answer every page key of every facet sharing its (subject, kind) —
                // relation paging (Result A) turns that into dozens of keys in one batch. Decode the protobuf ONCE per
                // document and slice each request from the SAME parsed message instead of re-parsing per key.
                var documentDecoders = new Dictionary<(string Uri, Xm.ExtensionKind Kind), Func<ResourceKey, CatalogDecodedFacet>>();
                var transportRecorded = new HashSet<(string Uri, Xm.ExtensionKind Kind)>();
                foreach (var request in xmRequests)
                {
                    var kind = SpotifyCatalogDecoder.ExtensionFor(request.Key)!.Value;
                    try
                    {
                        if (!raw.TryGetValue((request.Key.Subject, kind), out var answer))
                            responses[request.Stamp.RequestId] = Failed(request, new(ResourceErrorKind.InvalidResponse, "Extended metadata omitted the requested extension."));
                        else if (answer.Status == 404)
                            responses[request.Stamp.RequestId] = new(request, ResourceFetchResult.Absent());
                        else if (answer.Status != 200)
                            responses[request.Stamp.RequestId] = Failed(request, HttpError(answer.Status));
                        else if (answer.Payload is null)
                            responses[request.Stamp.RequestId] = Failed(request, new(ResourceErrorKind.InvalidResponse, "Successful extension omitted its payload."));
                        else
                        {
                            var docKey = (request.Key.Subject, kind);
                            if (!documentDecoders.TryGetValue(docKey, out var decode))
                                documentDecoders[docKey] = decode = SpotifyCatalogDecoder.CreateDocumentDecoder((int)kind, answer.Payload, answer.Etag);
                            var decoded = decode(request.Key);
                            // The transport blob is identical for every key sharing this document — record it once so
                            // the byte-budget accounting and the durable write never multiply the same payload per page.
                            IReadOnlyList<CatalogTransportRecord>? transport = transportRecorded.Add(docKey)
                                ? [new CatalogTransportRecord(request.Key.Scope, request.Key.Subject, (int)kind,
                                    answer.Etag, answer.Payload.ToByteArray(), _time.GetUtcNow())]
                                : null;
                            responses[request.Stamp.RequestId] = new(request,
                                ResourceFetchResult.Present(decoded.Patch, answer.OfflineTtlSeconds > 0 ? TimeSpan.FromSeconds(answer.OfflineTtlSeconds) : null),
                                decoded.Seeds, transport);
                        }
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    { responses[request.Stamp.RequestId] = Failed(request, Error(error)); }
                }
                await RecoverVideosAsync(xmRequests, raw, responses, ct).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                foreach (var request in xmRequests) responses[request.Stamp.RequestId] = Failed(request, Error(error));
            }
        }
        var albumGroups = requests.Where(request => request.Key.Facet is FacetKind.AlbumDetail or FacetKind.AlbumVersions)
            .GroupBy(request => (request.Key.Scope, request.Key.Subject)).ToArray();
        if (albumGroups.Length > 0)
        {
            using var albumGate = new SemaphoreSlim(FetchParallelism, FetchParallelism);
            await Task.WhenAll(albumGroups.Select(async albumGroup =>
            {
                await albumGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    using var document = await FetchAlbumDocumentAsync(albumGroup.Key.Subject, ct).ConfigureAwait(false);
                    foreach (var request in albumGroup) responses[request.Stamp.RequestId] = ReadAlbumDocument(request, document);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                { foreach (var request in albumGroup) responses[request.Stamp.RequestId] = Failed(request, Error(error)); }
                finally { albumGate.Release(); }
            })).ConfigureAwait(false);
        }
        using (var envelopeGate = new SemaphoreSlim(FetchParallelism, FetchParallelism))
        {
            await Task.WhenAll(requests.Select(async request =>
            {
                if (responses.TryGetValue(request.Stamp.RequestId, out var decoded)
                    && !Wavee.Backend.Catalog.BatchAnswerPolicy.NeedsEnvelopeFallback(request.Key.Facet, decoded.Result))
                    return;   // TrackV4 may omit the file plane; a USER_PROFILE may carry only a username — see the policy
                await envelopeGate.WaitAsync(ct).ConfigureAwait(false);
                try { responses[request.Stamp.RequestId] = await FetchEnvelopeAsync(request, ct).ConfigureAwait(false); }
                catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.NotFound
                    && request.Key.Facet is FacetKind.PlaylistHeader or FacetKind.PlaylistRevision)
                { responses[request.Stamp.RequestId] = new(request, ResourceFetchResult.Absent()); }
                catch (Exception error) when (error is not OperationCanceledException)
                { responses[request.Stamp.RequestId] = Failed(request, Error(error)); }
                finally { envelopeGate.Release(); }
            })).ConfigureAwait(false);
        }
        return requests.Select(request => responses[request.Stamp.RequestId]).ToArray();
    }

    bool MatchesSession(ResourceRequest request)
    {
        var scope = request.Key.Scope;
        return scope.ContextKnown && scope.Provider == Provider && request.Stamp.Scope == scope
            && scope.ProviderAccount == _session.Account && request.Stamp.ActualAccount == _session.Account
            && scope.Locale == _session.Locale && scope.Market == _session.Market
            && scope.Catalogue == _session.Catalogue && scope.Tier == (int)_session.Tier
            && scope.ExplicitFilter == _session.ExplicitFilter && _xm.Session == _session;
    }

    async Task<IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), ExtendedMetadataSource.ExtensionResult>> FetchRawAsync(
        CatalogScope scope, IReadOnlyList<(string Uri, Xm.ExtensionKind Kind)> keys, string? clientFeatureId, CancellationToken ct)
    {
        // Deterministic order first (the asks list downstream is order-sensitive for dedupe/etag matching),
        // then read every key's cached transport bytes concurrently instead of one round trip at a time.
        var ordered = keys.OrderBy(key => key.Uri, StringComparer.Ordinal).ThenBy(key => key.Kind).ToArray();
        var reads = await Task.WhenAll(ordered.Select(key =>
            _transportBytes.ReadTransportAsync(scope, key.Uri, (int)key.Kind, ct).AsTask())).ConfigureAwait(false);
        var cached = new Dictionary<(string Uri, Xm.ExtensionKind Kind), CatalogTransportRecord>();
        var asks = new List<(string Uri, Xm.ExtensionKind Kind, string? Etag)>(ordered.Length);
        for (int i = 0; i < ordered.Length; i++)
        {
            var key = ordered[i];
            var bytes = reads[i];
            if (bytes is not null) cached[key] = bytes;
            asks.Add((key.Uri, key.Kind, bytes?.Etag));
        }
        var received = await _xm.GetExtensionsWithHeadersAsync(asks, ct, clientFeatureId).ConfigureAwait(false);
        var result = received.ToDictionary(pair => pair.Key, pair => pair.Value);
        var unconditional = new List<(string Uri, Xm.ExtensionKind Kind, string? Etag)>();
        foreach (var key in keys)
            if (result.TryGetValue(key, out var answer) && answer.Status == 304)
            {
                if (cached.TryGetValue(key, out var bytes) && !string.IsNullOrEmpty(bytes.Etag)
                    && (string.IsNullOrEmpty(answer.Etag) || answer.Etag == bytes.Etag))
                    result[key] = answer with { Status = 200, Payload = ByteString.CopyFrom(bytes.Payload), Etag = answer.Etag ?? bytes.Etag };
                else unconditional.Add((key.Uri, key.Kind, null));
            }
        if (unconditional.Count > 0)
        {
            var replacement = await _xm.GetExtensionsWithHeadersAsync(unconditional, ct, clientFeatureId).ConfigureAwait(false);
            foreach (var (key, value) in replacement) result[key] = value;
        }
        return result;
    }

    static ResourceResponse Failed(ResourceRequest request, ResourceError error) => new(request, ResourceFetchResult.Failed(error));
    static ResourceError HttpError(int status) => new(status == 429 ? ResourceErrorKind.RateLimited
        : status == 403 ? ResourceErrorKind.Forbidden : status >= 500 || status == 0 ? ResourceErrorKind.Transport
        : ResourceErrorKind.InvalidResponse, "Provider returned HTTP " + status, status);
    static ResourceError Error(Exception error) => error switch
    {
        HttpRequestException { StatusCode: { } status } => HttpError((int)status),
        PathfinderRequestException { HttpStatus: { } status } => HttpError(status),
        InvalidProtocolBufferException or System.Text.Json.JsonException or FormatException or ArgumentException
            => new(ResourceErrorKind.Decode, error.Message),
        _ => new(ResourceErrorKind.Transport, error.Message),
    };
}
