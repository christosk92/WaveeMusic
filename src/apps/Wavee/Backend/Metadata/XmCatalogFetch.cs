using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Wavee.Backend.Hydration;
using Wavee.Core;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Backend.Metadata;

// ── THE catalogue arm (design §2.2) ──────────────────────────────────────────────────────────────────────────────────
// Step 0 of every ladder. Fuses what MetadataService.SyncAllConditionalAsync + ProjectCachedExtensions did — build the
// (uri, kind) request list, ask the ETag cache, project whatever came back — with one shape change that is the whole
// point of the façade: the batch is MIXED-KIND and carries EXTRA kinds under a uri when a ladder wants them. A Rich
// album open therefore sends AlbumV4 and 183 in the SAME EntityRequest as one POST, instead of the V4 pass plus a
// separate publishing pass the album page used to fire.
//
// ExtensionEtagCache is REQUIRED (design §2.4): the "etag cache if wired, raw source otherwise" fork existed in seven
// places, and the raw arm is exactly the one that re-downloads payloads already on disk.
public sealed class XmCatalogFetch : ICatalogFetch
{
    readonly ExtensionEtagCache _cache;
    readonly IStore _store;
    readonly WaveeLogger _log;

    public XmCatalogFetch(ExtensionEtagCache cache, IStore store, WaveeLogger log = default)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _log = log;
    }

    public async Task<IReadOnlyCollection<string>> FetchAsync(IReadOnlyList<EntityUri> uris,
        IReadOnlyList<(string Uri, int Kind)>? extraKinds, TraitSurface surface, CancellationToken ct, bool revalidate = false)
    {
        if (uris is null || uris.Count == 0) return Array.Empty<string>();

        // Group the extras by uri up front so the request list can stay CONTIGUOUS per uri — MetadataChunking's
        // ExtensionRanges only ever flushes on a uri boundary, and it can only do that if a uri's kinds are adjacent.
        Dictionary<string, List<Xm.ExtensionKind>>? extras = null;
        if (extraKinds is { Count: > 0 })
        {
            extras = new Dictionary<string, List<Xm.ExtensionKind>>(StringComparer.Ordinal);
            for (int i = 0; i < extraKinds.Count; i++)
            {
                var (uri, kind) = extraKinds[i];
                if (string.IsNullOrEmpty(uri) || kind <= 0) continue;
                if (!extras.TryGetValue(uri, out var list)) extras[uri] = list = new List<Xm.ExtensionKind>(2);
                list.Add((Xm.ExtensionKind)kind);
            }
        }

        // The etag is filled in by the cache itself (it holds the per-(uri, kind) row); the null here is only what the
        // chunker measures against. That is honest: this chunking bounds the PROJECTION working set and the distinct
        // uris per POST, and the cache re-runs the same chunker with real ETags before anything reaches the wire.
        var reqs = new List<(string Uri, Xm.ExtensionKind Kind, string? Etag)>(uris.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < uris.Count; i++)
        {
            var e = uris[i];
            if (e.Uri.Length == 0 || !seen.Add(e.Uri)) continue;
            var kind = XmKinds.CatalogKindOf(e.Kind);
            if (kind == Xm.ExtensionKind.UnknownExtension) continue;   // no catalogue facts exist for this kind
            reqs.Add((e.Uri, kind, null));
            if (extras is not null && extras.TryGetValue(e.Uri, out var fused))
                for (int k = 0; k < fused.Count; k++) reqs.Add((e.Uri, fused[k], null));
        }
        if (reqs.Count == 0) return Array.Empty<string>();

        // A user-initiated revalidate must not be quietly served the TTL-fresh cached row underneath (§2.4's own
        // etag cache runs a multi-hour window independent of the hydration ledger this bypassed). MarkStale forces
        // the batch below through a real conditional GET per (uri, kind) instead of re-projecting the cached body —
        // the fix for a rolling-identity playlist (a daylist) whose LIST_METADATA_V2 (205) sits inside that window
        // while the edition it names has already rolled over server-side.
        if (revalidate)
            for (int i = 0; i < reqs.Count; i++) _cache.MarkStale(reqs[i].Uri, reqs[i].Kind);

        string? clientFeatureId = surface.ClientFeatureId();
        var landed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (start, count) in MetadataChunking.ExtensionRanges(reqs))
        {
            ct.ThrowIfCancellationRequested();
            var slice = new List<(string Uri, Xm.ExtensionKind Kind)>(count);
            for (int i = start; i < start + count; i++) slice.Add((reqs[i].Uri, reqs[i].Kind));
            var cached = await _cache.GetAsync(slice, ct, clientFeatureId).ConfigureAwait(false);
            Project(cached, _store, landed);
        }

        _log.Event(WaveeLogLevel.Debug, "hydration.catalog.fetch", "extended-metadata catalogue pass",
            fields: [WaveeLogField.Of("asked", seen.Count), WaveeLogField.Of("queries", reqs.Count),
                     WaveeLogField.Of("landed", landed.Count), WaveeLogField.Of("surface", surface.ToString()),
                     WaveeLogField.Of("cfid", clientFeatureId)]);
        return landed;
    }

    /// <summary>Re-serialize the cached payloads into the response shape <c>ProjectResponse</c> parses, and project
    /// them. Returns via <paramref name="landed"/> the uris a projection actually WROTE — never merely requested, so
    /// the ledger seals on outcome (the outcome-seeding contract, inherited from the deleted IMetadataSource seam).</summary>
    static void Project(IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), CachedExtension> cached,
                        IStore store, HashSet<string> landed)
    {
        Dictionary<Xm.ExtensionKind, Xm.EntityExtensionDataArray>? arrays = null;
        foreach (var ((uri, kind), ext) in cached)
        {
            if (ext.Missing || ext.Payload is null || ext.Payload.IsEmpty) continue;
            // Fused trait kinds (183 on a Rich album, 178/220 for wire fidelity) are CACHED — which is what keeps the
            // next window's request conditional — but they have no entity to project, so they never enter the pass.
            if (!XmKinds.IsCatalogKind(kind)) continue;
            arrays ??= new Dictionary<Xm.ExtensionKind, Xm.EntityExtensionDataArray>();
            if (!arrays.TryGetValue(kind, out var array))
                arrays[kind] = array = new Xm.EntityExtensionDataArray { ExtensionKind = kind };
            array.ExtensionData.Add(new Xm.EntityExtensionData
            {
                EntityUri = uri,
                ExtensionData = new Any { Value = ext.Payload },
            });
        }
        if (arrays is null) return;

        var resp = new Xm.BatchedExtensionResponse();
        int entities = 0;
        foreach (var array in arrays.Values) { resp.ExtendedMetadata.Add(array); entities += array.ExtensionData.Count; }
        // ONE change signal for the whole page, not one per entity - a 300-row page must wake the UI once.
        var bulk = entities > 1 ? store.BeginBulk() : null;
        try { foreach (var uri in ExtendedMetadataSource.ProjectResponse(resp.ToByteArray(), store)) landed.Add(uri); }
        finally { bulk?.Dispose(); }
    }
}
