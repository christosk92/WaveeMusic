using System.Linq;
using Wavee.Backend;
using Wavee.Backend.Catalog;
using Wavee.Backend.Persistence;
using Wavee.Core.Catalog;
using Wavee.SpotifyLive.Catalog;
using Wavee.Backend.Metadata;
using Wavee.Backend.Spotify;
using Wavee.Core;
using EntityKind = Wavee.Core.EntityKind;   // disambiguate: Wavee.Backend.Metadata has its own PERSISTED kind enum; this file speaks the ROUTING one

namespace Wavee.SpotifyLive;

// LIVE metadata round-trip: login (AP) -> login5 (spclient access token) + client-token (attestation) -> POST spclient
// extended-metadata for ONE uri -> project into the Store -> print. The metadata equivalent of --spotify-login: end-to-end
// proof the whole chain works. Needs creds + network, so the USER runs it (`--spotify-metadata spotify:track:...`).
public static class SpotifyMetadataProbe
{
    public static async Task<int> RunAsync(string uri, WaveeLogger log, CancellationToken ct, string language = "en")
    {
        var live = await SpotifyLiveSpclient.ConnectAsync(log, ct, language: language).ConfigureAwait(false);
        if (live is null) return 1;

        FacetKind? facet = EntityUri.KindOf(uri) switch
        {
            EntityKind.Track => FacetKind.TrackIdentity, EntityKind.Album => FacetKind.AlbumIdentity,
            EntityKind.Artist => FacetKind.ArtistIdentity, EntityKind.Episode => FacetKind.EpisodeIdentity,
            EntityKind.Show => FacetKind.ShowIdentity, EntityKind.Playlist => FacetKind.PlaylistHeader,
            EntityKind.User => FacetKind.UserIdentity, _ => null,
        };
        if (facet is null) { log.Info("Unsupported metadata subject: " + uri); return 2; }
        var scope = new CatalogScope("spotify", live.Username, live.Session.Locale, live.Session.Market,
            live.Session.Catalogue, (int)live.Session.Tier, live.Session.ExplicitFilter);
        var persistence = new MemoryDataPersistence();
        await using var commits = new DataCommitQueue();
        var catalog = new CatalogRepository(commits, persistence, TimeProvider.System, scope, live.Username);
        var metadata = new ExtendedMetadataSource(live.Pipeline, () => live.BaseUrl, () => live.Session);
        var provider = new SpotifyCatalogResourceProvider(metadata, persistence, new PathfinderClient(live.Pipeline),
            live.Pipeline, () => live.BaseUrl, () => HomeModuleTitles.Default, TimeProvider.System);
        await using var resources = new ResourceCoordinator(catalog, [provider], TimeProvider.System);
        var key = new ResourceKey(scope, uri, facet.Value);
        await resources.EnsureAsync([key], ct: ct).ConfigureAwait(false);
        var result = catalog.Peek(key);
        log.Info($"{uri}: {result.Knowledge}; {result.Error?.Message ?? result.Value?.ToString()}");
        return result.Knowledge == Knowledge.Present ? 0 : 1;
    }
}
