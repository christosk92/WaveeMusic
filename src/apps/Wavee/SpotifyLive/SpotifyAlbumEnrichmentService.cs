using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Metadata;
using Wavee.Core;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.SpotifyLive;

/// <summary>Spotify's below-the-fold album reads. Every method is best-effort and independently consumable by the
/// UI; no failure here can invalidate the already-loaded album or track list. RETURN-ONLY for CARDS (design 3):
/// similar albums and recommended playlists are display rows, so this service no longer mints thin store entities
/// for them - anything that needs a real ENTITY (the artist behind "fans also like", a recommended playlist's
/// header) asks <c>IEntityHydrator</c> for a rung and reads the store back, which is also what killed this file's
/// second 205 projector and its second queryArtistOverview caller. The Pathfinder ops carry the rich JSON
/// (about-artist / merch / similar albums); the NPV about-artist stays an authoritative artist projection.</summary>
sealed class SpotifyAlbumEnrichmentService : IAlbumEnrichmentService
{
    readonly PathfinderResource _pathfinder;
    readonly ExtendedMetadataSource _metadata;
    readonly ExtensionEtagCache? _extensions;
    readonly IStore _store;
    readonly IEntityHydrator _hydrator;
    readonly WaveeLogger _log;

    /// <param name="hydrator">REQUIRED. The one façade — "fans also like" asks for the artist's Rich rung through it
    /// instead of issuing its own queryArtistOverview (design §1.5).</param>
    public SpotifyAlbumEnrichmentService(PathfinderResource pathfinder, ExtendedMetadataSource metadata, IStore store,
        IEntityHydrator hydrator, WaveeLogger log = default, ExtensionEtagCache? extensions = null)
    {
        _pathfinder = pathfinder;
        _metadata = metadata;
        _extensions = extensions;
        _store = store;
        _hydrator = hydrator ?? throw new ArgumentNullException(nameof(hydrator));
        _log = log;
    }

    public async Task<NowPlayingInfo?> GetNowPlayingInfoAsync(string artistUri, string trackUri, CancellationToken ct = default)
    {
        if (artistUri.Length == 0 || trackUri.Length == 0)
            return new NowPlayingInfo(_store.GetArtist(artistUri), null);

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
        if (doc is null) return new NowPlayingInfo(_store.GetArtist(artistUri), null);

        var mapped = SpotifyExportMapper.ArtistFromNpv(doc.RootElement);
        if (mapped is not null) _store.UpsertArtist(mapped);
        var about = _store.GetArtist(artistUri) ?? mapped;
        return new NowPlayingInfo(about, SpotifyExportMapper.TrackNpvFromResponse(doc.RootElement));
    }

    public async Task<Artist?> GetAboutArtistAsync(string artistUri, string leadTrackUri, CancellationToken ct = default)
        => (await GetNowPlayingInfoAsync(artistUri, leadTrackUri, ct).ConfigureAwait(false))?.About;

    public async Task<IReadOnlyList<Artist>> GetRelatedArtistsAsync(string artistUri, CancellationToken ct = default)
    {
        if (artistUri.Length == 0) return Array.Empty<Artist>();
        // "Fans also like" is the artist overview's Related list — the SAME queryArtistOverview the artist ladder's
        // Rich rung already owns. This used to be the SECOND caller of that operation, with its own copy of the
        // stats-only-write rule; now it asks for the rung and reads the store, so an album page opened right after its
        // artist page costs zero extra requests (design §2.3, ArtistHydration).
        await _hydrator.EnsureAsync(artistUri, HydrationLevel.Rich, HydrationOptions.Default, ct).ConfigureAwait(false);
        return _store.GetArtist(artistUri)?.Extras?.Related is { Count: > 0 } related
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
        // RETURN-ONLY (design 3): the cards are display rows, not entities. Minting thin albums here made the
        // store hold rows no ladder had hydrated, and a click already opens through GetAlbumAsync -> the album
        // ladder, which fetches the real thing. Writing them twice bought nothing but a stale hero.
        var albums = SpotifyExportMapper.SimilarAlbumsFromTrack(doc.RootElement);
        return albums;
    }

    // The recommended-playlist shelf: kind 151 (RECOMMENDED_PLAYLISTS) yields the ordered playlist refs for the
    // album; the refs are then hydrated at Identity THROUGH THE FACADE, which is the one place a 205 is read and
    // projected. This service reads the resulting headers back out of the store and returns cards.
    public async Task<IReadOnlyList<PlaylistSummary>> GetRecommendedPlaylistsAsync(string albumUri, CancellationToken ct = default)
    {
        if (albumUri.Length == 0) return Array.Empty<PlaylistSummary>();

        // The getAlbum Full upgrade used to be triggered from here by calling back into LiveSessionHost. It is now
        // the album ladder's Full rung, asked for by DetailTrailing (the pane that needs it) through the façade — this
        // service is return-only again: it reads, it never fetches an entity into the store (design §3).
        ByteString? refsPayload;
        try
        {
            refsPayload = _extensions is not null
                ? await _extensions.GetPayloadAsync(albumUri, Xm.ExtensionKind.RecommendedPlaylists, ct).ConfigureAwait(false)
                : await _metadata.GetExtensionAsync(albumUri, Xm.ExtensionKind.RecommendedPlaylists, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.Info("RECOMMENDED_PLAYLISTS fetch: " + ex.Message); return Array.Empty<PlaylistSummary>(); }
        if (refsPayload is null) return Array.Empty<PlaylistSummary>();

        Xm.RecommendedPlaylists refs;
        try { refs = Xm.RecommendedPlaylists.Parser.ParseFrom(refsPayload); }
        catch (InvalidProtocolBufferException) { return Array.Empty<PlaylistSummary>(); }

        var uris = refs.Recommendation.Select(x => x.Uri).Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal).Take(12).ToArray();
        if (uris.Length == 0) return Array.Empty<PlaylistSummary>();

        // The refs are playlist POINTERS. Hydrating them is the facade's job at IDENTITY (step 0 is the same 205 read,
        // through the etag cache and the ONE ProjectPlaylist projector) - this service used to carry a SECOND 205
        // projector with its own cover picker, owner title-caser and header-minting write, which is exactly the kind of
        // duplicate the facade exists to delete (hydration-facade-plan.md 1.6).
        try { await _hydrator.EnsureManyAsync(uris, HydrationLevel.Identity, new HydrationOptions(Surface: TraitSurface.None), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _log.Info("recommended-playlist hydrate: " + ex.Message); }

        var result = new List<PlaylistSummary>(uris.Length);
        foreach (var uri in uris)   // preserve the recommended order
        {
            if (_store.GetPlaylist(uri) is not { Name.Length: > 0 } p) continue;   // a nameless ref is not a renderable card
            result.Add(new PlaylistSummary(uri, p.Name, p.OwnerName.Length > 0 ? p.OwnerName : "Spotify", p.TrackCount, p.Cover));
        }
        return result;
    }

    static IReadOnlyList<Artist> Artists(IReadOnlyList<RelatedArtist> related)
    {
        var result = new List<Artist>(Math.Min(8, related.Count));
        for (int i = 0; i < related.Count && result.Count < 8; i++)
            result.Add(new Artist(related[i].Id, related[i].Uri, related[i].Name, related[i].Image));
        return result;
    }

}
