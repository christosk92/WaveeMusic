using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Hydration;
using Wavee.Core;

namespace Wavee.SpotifyLive.Hydration;

// ── The Pathfinder arm of the hydration façade (design §2.2) ─────────────────────────────────────────────────────────
// Three GraphQL envelopes, and NOTHING else: getAlbum, getTrack, queryArtistOverview. Each returns the MAPPED domain
// object; deciding what to write is the ladder's job, not the transport's (architecture.md §4.4 — no GraphQL type
// crosses a port). TTL/dedup of the underlying call belongs to PathfinderResource, which already owns it.
//
// The wire shapes here are CAPTURED, not guessed, and every deviation previously cost a real bug — so they are pinned:
//   getAlbum              locale "" (NOT pf.Locale — the captured client sends "" and lets the account's market/language
//                         headers decide), offset 0, limit 50, WebPlayer bundle.
//   getTrack              uri only, WebPlayer.
//   queryArtistOverview   locale "", preReleaseV2 TRUE (this is what makes artistUnion.preReleaseV2 populate at all),
//                         WebPlayer — the captured desktop client serves this op from its web-player bundle.
sealed class PathfinderEnvelopeFetch(PathfinderResource pathfinder) : IEnvelopeFetch
{
    readonly PathfinderResource _pathfinder = pathfinder ?? throw new ArgumentNullException(nameof(pathfinder));

    public async Task<Album?> AlbumAsync(string albumUri, CancellationToken ct)
    {
        if (albumUri.Length == 0) return null;
        using var doc = await _pathfinder.QueryAsync(PathfinderOps.GetAlbum, PathfinderOps.GetAlbumHash,
            w => { w.WriteString("uri", albumUri); w.WriteString("locale", ""); w.WriteNumber("offset", 0); w.WriteNumber("limit", 50); },
            PathfinderClient.Platform.WebPlayer, ct).ConfigureAwait(false);
        return doc is null ? null : SpotifyExportMapper.AlbumFromUnion(doc.RootElement);
    }

    public async Task<Track?> TrackAsync(string trackUri, CancellationToken ct)
    {
        if (trackUri.Length == 0) return null;
        using var doc = await _pathfinder.QueryAsync(PathfinderOps.GetTrack, PathfinderOps.GetTrackHash,
            w => w.WriteString("uri", trackUri), PathfinderClient.Platform.WebPlayer, ct).ConfigureAwait(false);
        return doc is null ? null : SpotifyExportMapper.TrackFromUnion(doc.RootElement);
    }

    public async Task<Artist?> ArtistOverviewAsync(string artistUri, CancellationToken ct)
    {
        if (artistUri.Length == 0) return null;
        using var doc = await _pathfinder.QueryAsync(PathfinderOps.QueryArtistOverview, PathfinderOps.QueryArtistOverviewHash,
            w => { w.WriteString("uri", artistUri); w.WriteString("locale", ""); w.WriteBoolean("preReleaseV2", true); },
            PathfinderClient.Platform.WebPlayer, ct).ConfigureAwait(false);
        return doc is null ? null : SpotifyExportMapper.ArtistFromOverview(doc.RootElement);
    }
}
