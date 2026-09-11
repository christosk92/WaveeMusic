using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend.Catalog;
using Wavee.Backend.Metadata;
using Wavee.Core.Catalog;
using Lean = Wavee.Protocol.Lean;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.SpotifyLive.Catalog;

public sealed partial class SpotifyCatalogResourceProvider
{
    // One finite batched alias recipe. Its result owns the same resource generation as the original kind-99 ask.
    async Task RecoverVideosAsync(ResourceRequest[] requests,
        IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), ExtendedMetadataSource.ExtensionResult> initial,
        ConcurrentDictionary<long, ResourceResponse> responses, CancellationToken ct)
    {
        var suspectsList = new List<ResourceRequest>();
        foreach (var request in requests)
        {
            if (request.Key.Facet != FacetKind.VideoAssociation || !responses.TryGetValue(request.Stamp.RequestId, out var response)
                || !(response.Result.Status == ResourceFetchStatus.Absent || response.Result.Patch?.Apply(null) is VideoAssociationValue { Association.HasVideo: false })
                || !initial.TryGetValue((request.Key.Subject, Xm.ExtensionKind.ConsumptionExperienceTrait), out var consumption)) continue;
            try { if (HasVideoExperience(consumption.Payload)) suspectsList.Add(request); }
            catch (InvalidProtocolBufferException error) { responses[request.Stamp.RequestId] = Failed(request, Error(error)); }
        }
        var suspects = suspectsList.ToArray();
        if (suspects.Length == 0) return;
        try
        {
            var followup = suspects.SelectMany(request => new[]
                { (request.Key.Subject, Xm.ExtensionKind.TrackV4), (request.Key.Subject, Xm.ExtensionKind.PlaybackTrait) })
                .Where(key => !initial.ContainsKey(key)).Distinct().ToArray();
            var fetched = followup.Length == 0
                ? new Dictionary<(string Uri, Xm.ExtensionKind Kind), ExtendedMetadataSource.ExtensionResult>()
                : await FetchRawAsync(suspects[0].Key.Scope, followup, suspects[0].ClientFeatureId, ct).ConfigureAwait(false);
            var source = initial.Concat(fetched).ToDictionary(pair => pair.Key, pair => pair.Value);
            var pairs = new List<(ResourceRequest Request, string Canonical, string? VideoGid)>();
            foreach (var request in suspects)
            {
                if (!source.TryGetValue((request.Key.Subject, Xm.ExtensionKind.TrackV4), out var identity)
                    || identity.Status != 200 || identity.Payload is null) continue;
                var track = Lean.LeanTrack.Parser.WithDiscardUnknownFields(true).ParseFrom(identity.Payload);
                if (SpotifyCatalogDecoder.CanonicalUri(track, request.Key.Subject) is not { } canonical) continue;
                source.TryGetValue((request.Key.Subject, Xm.ExtensionKind.PlaybackTrait), out var playback);
                pairs.Add((request, canonical, AssociatedVideoGid(playback.Payload)));
            }
            if (pairs.Count == 0)
            {
                foreach (var request in suspects)
                    responses[request.Stamp.RequestId] = Failed(request, new(ResourceErrorKind.InvalidResponse,
                        "Video experience exists but the provider omitted a resolvable canonical identity."));
                return;
            }
            var canonicalBytes = await FetchRawAsync(suspects[0].Key.Scope,
                pairs.Select(pair => (pair.Canonical, Xm.ExtensionKind.VideoAssociations)).Distinct().ToArray(),
                suspects[0].ClientFeatureId, ct).ConfigureAwait(false);
            foreach (var (request, canonical, gid) in pairs)
            {
                if (!canonicalBytes.TryGetValue((canonical, Xm.ExtensionKind.VideoAssociations), out var answer)
                    || answer.Status != 200 || answer.Payload is null)
                {
                    responses[request.Stamp.RequestId] = Failed(request, new(ResourceErrorKind.InvalidResponse,
                        "Canonical video association did not resolve the observed video experience."));
                    continue;
                }
                var decoded = SpotifyCatalogDecoder.Decode(request.Key, answer.Payload);
                var association = ((VideoAssociationValue)decoded.Patch.Apply(null)).Association;
                if (!association.HasVideo)
                {
                    responses[request.Stamp.RequestId] = Failed(request, new(ResourceErrorKind.InvalidResponse,
                        "Canonical association contradicts the video experience."));
                    continue;
                }
                var transports = new List<CatalogTransportRecord>
                {
                    new(request.Key.Scope, canonical, (int)Xm.ExtensionKind.VideoAssociations, answer.Etag,
                        answer.Payload.ToByteArray(), _time.GetUtcNow()),
                };
                foreach (var kind in new[] { Xm.ExtensionKind.TrackV4, Xm.ExtensionKind.PlaybackTrait, Xm.ExtensionKind.ConsumptionExperienceTrait })
                    if (source.TryGetValue((request.Key.Subject, kind), out var bytes) && bytes.Status == 200 && bytes.Payload is not null)
                        transports.Add(new(request.Key.Scope, request.Key.Subject, (int)kind, bytes.Etag, bytes.Payload.ToByteArray(), _time.GetUtcNow()));
                responses[request.Stamp.RequestId] = new(request,
                    ResourceFetchResult.Present(new ReplaceFacetPatch(new VideoAssociationValue(association with { VideoGidHex = gid })),
                        answer.OfflineTtlSeconds > 0 ? TimeSpan.FromSeconds(answer.OfflineTtlSeconds) : null),
                    [new(request.Key with { Facet = FacetKind.TrackIdentity, Arguments = default },
                        new TrackIdentityPatch(CanonicalUri: FieldChange<string?>.Set(canonical)))], transports);
            }
            foreach (var request in suspects)
                if (!pairs.Any(pair => pair.Request == request))
                    responses[request.Stamp.RequestId] = Failed(request, new(ResourceErrorKind.InvalidResponse,
                        "Video experience could not be resolved from the provider identity."));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // A recovery failure preserves the old value; it must not turn a known positive into cached absence.
            foreach (var request in suspects) responses[request.Stamp.RequestId] = Failed(request, Error(error));
        }
    }

    internal static bool HasVideoExperience(ByteString? payload)
    {
        if (payload is null) return false;
        using var input = payload.CreateCodedInput();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (tag == 34)
            {
                var experiences = input.ReadBytes();
                if (experiences.Span.IndexOf((byte)2) >= 0) return true;
            }
            else input.SkipLastField();
        }
        return false;
    }
    internal static string? AssociatedVideoGid(ByteString? payload)
    {
        if (payload is null) return null;
        using var input = payload.CreateCodedInput();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (tag == 18)
            {
                var bytes = input.ReadBytes();
                if (bytes.Length == 16) return Convert.ToHexStringLower(bytes.Span);
                return NestedGid(bytes, 3);
            }
            input.SkipLastField();
        }
        return null;
    }
    static string? NestedGid(ByteString bytes, int depth)
    {
        using var input = bytes.CreateCodedInput();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited)
            {
                var value = input.ReadBytes();
                if (value.Length == 16) return Convert.ToHexStringLower(value.Span);
                if (depth > 0 && NestedGid(value, depth - 1) is { } nested) return nested;
            }
            else input.SkipLastField();
        }
        return null;
    }
}
