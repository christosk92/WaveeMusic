using System;
using System.Collections.Generic;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Backend.Metadata;

// ── The transport's shared vocabulary ────────────────────────────────────────────────────────────────────────────────
// What survives here after the hydration façade landed: the PERSISTED kind enum (the cold store's entity.kind column)
// and the request chunker. The old IMetadataSource seam + EntityRef lived only to serve MetadataService's unconditional
// bulk arm; both died with it — routing speaks Wavee.Core.EntityUri/EntityKind, and the one transport-kind map is
// XmKinds.CatalogKindOf (hydration-facade-plan.md §1.6).

/// <summary>The TRANSPORT's kind vocabulary — the six entity types extended-metadata can fetch, plus Unknown.
/// <para>DELIBERATELY NOT an alias of <see cref="Wavee.Core.EntityKind"/> (which is the routing/ladder vocabulary and
/// carries User/Collection/Prerelease/Concert too): these numbers are PERSISTED — they are the <c>entity.kind</c>
/// column of the cold store — so re-basing them onto Core's ordering would silently make every cached Album read back
/// as an Episode. Nothing ROUTES on this enum; it exists to name a persisted row.</para></summary>
// NOTE: these numbers are PERSISTED (`entity.kind`). Append ONLY — renumbering makes every cached Album read back as
// something else. `User` (P4-C) is the newest trailing value: the owner rows `IStore.UpsertOwner` persists.
public enum EntityKind { Unknown, Track, Album, Artist, Playlist, Show, Episode, User }

/// <summary>Packs extension queries into request chunks by serialized BODY SIZE and by distinct-uri count, so each POST
/// is pushed as large as the server allows — a 10k-track playlist becomes ~34 requests, not 10k. Memory-efficient:
/// yields index ranges, never sub-lists. <c>XmCatalogFetch</c> and <c>ExtensionEtagCache</c> iterate these ranges,
/// serializing + gzipping each chunk's body.</summary>
public static class MetadataChunking
{
    public const int DefaultMaxBodyBytes = 4 * 1024 * 1024;   // tune toward the real spclient POST-body ceiling (confirm live)

    /// <summary>THE per-POST entity ceiling — the measured server-side limit, and the ONE copy of it. Seven services
    /// each carried their own "300" (adornments, play counts, video detect, expansion, the closure, the paged hydrate,
    /// show episodes); they all point here now, so the wire shape is one edit away from changing.
    /// <para>Body size is the OTHER bound: a chunk flushes on whichever limit it hits first.</para></summary>
    public const int MaxEntitiesPerRequest = 300;

    /// <summary>THE chunker: its unit is a (uri, kind, etag) query rather than a whole entity, bounded by body size AND
    /// by <paramref name="maxEntities"/> DISTINCT uris.
    /// <para>A uri's kinds never split across chunks — a chunk only ever flushes on a uri boundary. Splitting them
    /// would send the same entity in two POSTs, so the server answers two partial entity groups and the projector sees
    /// the uri twice. (Callers list a uri's kinds contiguously, which is what makes the boundary test sufficient.)</para></summary>
    public static IEnumerable<(int Start, int Count)> ExtensionRanges(
        IReadOnlyList<(string Uri, Xm.ExtensionKind Kind, string? Etag)> reqs,
        int maxBodyBytes = DefaultMaxBodyBytes, int headerBytes = 64, int maxEntities = MaxEntitiesPerRequest)
    {
        int start = 0, size = headerBytes, entities = 0;
        for (int i = 0; i < reqs.Count; i++)
        {
            bool newUri = i == 0 || !string.Equals(reqs[i].Uri, reqs[i - 1].Uri, StringComparison.Ordinal);
            int cost = reqs[i].Uri.Length + (reqs[i].Etag?.Length ?? 0) + 16;   // uri + etag + tags
            if (newUri && i > start && (size + cost > maxBodyBytes || entities >= maxEntities))
            {
                yield return (start, i - start);
                start = i;
                size = headerBytes;
                entities = 0;
            }
            if (newUri) entities++;
            size += cost;
        }
        if (reqs.Count > start) yield return (start, reqs.Count - start);
    }
}
