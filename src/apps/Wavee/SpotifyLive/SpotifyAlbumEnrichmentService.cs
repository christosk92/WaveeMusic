using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Catalog;
using Wavee.Backend.Metadata;
using Wavee.Core.Catalog;
using Wavee.Core;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.SpotifyLive;

/// <summary>Finite below-the-fold album reads. Shared entity facts enter the catalog as conservative seeds;
/// entity relationships and headers are read through the same typed queries as other surfaces.</summary>
sealed class SpotifyAlbumEnrichmentService : IAlbumEnrichmentService
{
    readonly PathfinderResource _pathfinder;
    readonly ExtendedMetadataSource _metadata;
    readonly CatalogRepository _catalog;
    readonly IResourceCoordinator _resources;
    readonly IQueryService _queries;
    readonly Func<string, CatalogScope> _scope;
    readonly WaveeLogger _log;

    public SpotifyAlbumEnrichmentService(PathfinderResource pathfinder, ExtendedMetadataSource metadata,
        CatalogRepository catalog, IResourceCoordinator resources, IQueryService queries,
        Func<string, CatalogScope> scope, WaveeLogger log = default)
    {
        _pathfinder = pathfinder; _metadata = metadata; _catalog = catalog;
        _resources = resources; _queries = queries; _scope = scope; _log = log;
    }

    public async Task<NowPlayingInfo?> GetNowPlayingInfoAsync(string artistUri, string trackUri, CancellationToken ct = default)
    {
        var scope = _scope(artistUri);
        long epoch = _catalog.Epoch;
        if (artistUri.Length == 0 || trackUri.Length == 0)
            return new NowPlayingInfo(await ReadArtistAsync(scope, artistUri, ct).ConfigureAwait(false), null);

        using var doc = await _pathfinder.UseQueryAsync(PathfinderOps.QueryNpvArtist, PathfinderOps.QueryNpvArtistHash,
            w =>
            {
                w.WriteString("artistUri", artistUri);
                w.WriteString("trackUri", trackUri);
                w.WriteNumber("contributorsLimit", 10);
                w.WriteNumber("contributorsOffset", 0);
                w.WriteBoolean("enableRelatedVideos", true);
                w.WriteBoolean("enableRelatedAudioTracks", true);
            // Desktop identity + the desktop document: the captured client uses b2cedf7e… here, which is a strict
            // superset of the web-player variant (it adds onPlatformReputationTrait for the verified badge).
            }, PathfinderClient.Platform.Desktop, ct).ConfigureAwait(false);
        RequireEpoch(epoch);
        if (doc is null) return new NowPlayingInfo(await ReadArtistAsync(scope, artistUri, ct).ConfigureAwait(false), null);

        var mapped = SpotifyExportMapper.ArtistFromNpv(doc.RootElement);
        if (mapped is not null)
        {
            var seeds = new List<CatalogSeed>();
            CatalogDomainSeeds.Artist(scope, mapped, seeds);
            await _catalog.SeedManyAsync(seeds, epoch, ct).ConfigureAwait(false);
        }
        // NPV's biography and track extras belong to this finite response. They do not claim overview authority.
        return new NowPlayingInfo(mapped ?? await ReadArtistAsync(scope, artistUri, ct).ConfigureAwait(false),
            SpotifyExportMapper.TrackNpvFromResponse(doc.RootElement));
    }

    public async Task<Artist?> GetAboutArtistAsync(string artistUri, string leadTrackUri, CancellationToken ct = default)
        => (await GetNowPlayingInfoAsync(artistUri, leadTrackUri, ct).ConfigureAwait(false))?.About;

    public async Task<IReadOnlyList<Artist>> GetRelatedArtistsAsync(string artistUri, CancellationToken ct = default)
    {
        if (artistUri.Length == 0) return Array.Empty<Artist>();
        var result = await _queries.ReadOnceAsync(new ArtistDetailQuery(_scope(artistUri), artistUri),
            QueryDemand.Initial, ct).ConfigureAwait(false);
        return result.Value.Extras?.Related is { Count: > 0 } related
            ? Artists(related) : Array.Empty<Artist>();
    }

    public async Task<AlbumTrackContext?> GetTrackContextAsync(string trackUri, CancellationToken ct = default)
    {
        if (trackUri.Length == 0) return null;
        using var doc = await _pathfinder.QueryAsync(PathfinderOps.GetTrack, PathfinderOps.GetTrackHash,
            w => w.WriteString("uri", trackUri), PathfinderClient.Platform.WebPlayer, ct).ConfigureAwait(false);
        return doc is null ? null : SpotifyExportMapper.TrackContextFromUnion(doc.RootElement);
    }

    public async Task<IReadOnlyList<MerchItem>> GetMerchAsync(string albumUri, CancellationToken ct = default)
    {
        if (albumUri.Length == 0) return Array.Empty<MerchItem>();
        using var doc = await _pathfinder.QueryAsync(PathfinderOps.QueryAlbumMerch, PathfinderOps.QueryAlbumMerchHash,
            w => w.WriteString("uri", albumUri), PathfinderClient.Platform.Desktop, ct).ConfigureAwait(false);
        return doc is null ? Array.Empty<MerchItem>() : SpotifyExportMapper.AlbumMerch(doc.RootElement);
    }

    public async Task<IReadOnlyList<Album>> GetSimilarAlbumsAsync(string seedTrackUri, int limit = 24, CancellationToken ct = default)
    {
        if (seedTrackUri.Length == 0) return Array.Empty<Album>();
        using var doc = await _pathfinder.QueryAsync(PathfinderOps.SimilarAlbumsBasedOnThisTrack,
            PathfinderOps.SimilarAlbumsBasedOnThisTrackHash,
            w => { w.WriteString("uri", seedTrackUri); w.WriteNumber("limit", limit); w.WriteBoolean("albumsOnly", true); },
            PathfinderClient.Platform.Desktop, ct).ConfigureAwait(false);
        if (doc is null) return Array.Empty<Album>();
        return SpotifyExportMapper.SimilarAlbumsFromTrack(doc.RootElement);
    }

    // Extension 151 returns an ordered set of references. One coordinator batch resolves their header facts.
    public async Task<IReadOnlyList<PlaylistSummary>> GetRecommendedPlaylistsAsync(string albumUri, CancellationToken ct = default)
    {
        if (albumUri.Length == 0) return Array.Empty<PlaylistSummary>();
        long epoch = _catalog.Epoch;

        ByteString? refsPayload;
        try
        {
            refsPayload = await _metadata.GetExtensionAsync(albumUri, Xm.ExtensionKind.RecommendedPlaylists, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.Info("RECOMMENDED_PLAYLISTS fetch: " + ex.Message); return Array.Empty<PlaylistSummary>(); }
        if (refsPayload is null) return Array.Empty<PlaylistSummary>();
        RequireEpoch(epoch);

        Xm.RecommendedPlaylists refs;
        try { refs = Xm.RecommendedPlaylists.Parser.ParseFrom(refsPayload); }
        catch (InvalidProtocolBufferException) { return Array.Empty<PlaylistSummary>(); }

        var uris = refs.Recommendation.Select(x => x.Uri).Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal).Take(12).ToArray();
        if (uris.Length == 0) return Array.Empty<PlaylistSummary>();

        var keys = uris.Select(uri => new ResourceKey(_scope(uri), uri, FacetKind.PlaylistHeader)).ToArray();
        try { await _resources.EnsureAsync(keys, ResourcePriority.Visible, ct: ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _log.Info("recommended-playlist headers: " + ex.Message); }
        RequireEpoch(epoch);

        // These headers were already resolved above. Await their local projection without adding any remote demand.
        var reads = keys.Select(key => _queries.ReadOnceAsync(new PlaylistDetailQuery(key.Scope, key.Subject),
            QueryDemand.None, ct)).ToArray();
        var snapshots = await Task.WhenAll(reads).ConfigureAwait(false);
        RequireEpoch(epoch);
        var result = new List<PlaylistSummary>(uris.Length);
        for (int i = 0; i < uris.Length; i++)   // preserve the recommended order and captured scope
        {
            string uri = uris[i];
            if (snapshots[i].Value is not { Name.Length: > 0 } p) continue;
            result.Add(new PlaylistSummary(uri, p.Name, p.OwnerName.Length > 0 ? p.OwnerName : "Spotify", p.TrackCount, p.Cover));
        }
        return result;
    }

    async Task<Artist?> ReadArtistAsync(CatalogScope scope, string uri, CancellationToken ct)
    {
        if (uri.Length == 0) return null;
        var snapshot = await _queries.ReadOnceAsync(new ArtistIdentityQuery(scope, uri), QueryDemand.None, ct)
            .ConfigureAwait(false);
        return snapshot.Status.HasPrimaryData ? snapshot.Value : null;
    }

    void RequireEpoch(long expected)
    {
        if (_catalog.Epoch != expected) throw new OperationCanceledException("The catalog session changed.");
    }

    static IReadOnlyList<Artist> Artists(IReadOnlyList<RelatedArtist> related)
    {
        var result = new List<Artist>(Math.Min(8, related.Count));
        for (int i = 0; i < related.Count && result.Count < 8; i++)
            result.Add(new Artist(related[i].Id, related[i].Uri, related[i].Name, related[i].Image));
        return result;
    }

}
