using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;
using Wavee.Core.Catalog;

namespace Wavee.Backend.Catalog;

/// <summary>Finite reads from complete local/demo catalogs enter the same normalized acceptance path as network reads.</summary>
public sealed class NativeCatalogResourceProvider(SourceRegistry registry, ISource source) : ICatalogResourceProvider
{
    public string Provider => source.Id;
    public bool RequiresNetwork(ResourceKey key) => false;

    public async ValueTask<IReadOnlyList<ResourceResponse>> FetchAsync(IReadOnlyList<ResourceRequest> requests, CancellationToken ct)
    {
        var responses = new List<ResourceResponse>(requests.Count);
        foreach (var request in requests)
        {
            ct.ThrowIfCancellationRequested();
            if (request.Key.Scope.Provider != source.Id)
                throw new ArgumentException("The native request belongs to another source.");
            try { responses.Add(await FetchOneAsync(request, ct).ConfigureAwait(false)); }
            catch (Exception error) when (error is not OperationCanceledException)
            { responses.Add(new(request, ResourceFetchResult.Failed(new(ResourceErrorKind.Decode, error.Message)))); }
        }
        return responses;
    }

    async Task<ResourceResponse> FetchOneAsync(ResourceRequest request, CancellationToken ct)
    {
        var key = request.Key;
        var catalog = source as ICatalogSource;
        var podcast = source as IPodcastSource;
        var seeds = new List<CatalogSeed>();
        CatalogValue? value = null;
        CatalogPatch? patch = null;
        if (key.Facet is not (FacetKind.Home or FacetKind.Search or FacetKind.SearchSuggestions)
            && registry.OwnerOf(key.Subject) is { } owner && owner.Id != source.Id)
            return Unsupported(request);
        if (key.Facet is not (FacetKind.Home or FacetKind.Search or FacetKind.SearchSuggestions)
            && registry.OwnerOf(key.Subject) is null && !source.Owns(key.Subject)
            && (source.Capabilities & SourceCapabilities.Fallback) == 0)
            return Unsupported(request);

        if (key.Facet is FacetKind.Home or FacetKind.Search or FacetKind.SearchSuggestions)
        {
            if (catalog is null) return Unsupported(request);
            CatalogDecodedFacet decoded;
            if (key.Facet == FacetKind.Home)
            {
                var home = await catalog.GetHomeAsync(key.Arguments.Filter, ct).ConfigureAwait(false);
                decoded = CatalogDomainSeeds.Home(key.Scope, new(home.Groups, home.Chips, home.Greeting, home.Sections, key.Arguments.Filter ?? ""));
            }
            else
            {
                const string prefix = "wavee:catalog:search:";
                if (!key.Subject.StartsWith(prefix, StringComparison.Ordinal)) throw new ArgumentException("Search subject is malformed.");
                var query = Uri.UnescapeDataString(key.Subject[prefix.Length..]);
                if (key.Facet == FacetKind.SearchSuggestions)
                    decoded = CatalogDomainSeeds.Suggestions(key.Scope, await catalog.SuggestRichAsync(query, ct).ConfigureAwait(false));
                else
                {
                    if (!Enum.TryParse<SearchFacet>(key.Arguments.Filter, out var facet)) facet = SearchFacet.All;
                    var result = await catalog.SearchAsync(query, facet, key.Arguments.Offset,
                        key.Arguments.Limit > 0 ? key.Arguments.Limit : 30, ct).ConfigureAwait(false);
                    decoded = CatalogDomainSeeds.Search(key.Scope, result, facet);
                }
            }
            return Present(request, decoded.Patch, decoded.Seeds);
        }

        if (key.Facet is FacetKind.TrackIdentity or FacetKind.PlayCount or FacetKind.Descriptors
            or FacetKind.AudioAttributes or FacetKind.Availability)
        {
            if (catalog is null) return Unsupported(request);
            var track = await catalog.GetTrackAsync(key.Subject, ct).ConfigureAwait(false);
            if (track is null) return Absent(request);
            CatalogDomainSeeds.Track(key.Scope, track, seeds);
            value = key.Facet switch
            {
                FacetKind.TrackIdentity => new TrackIdentityValue(track.Title, track.Artists.Select(artist => artist.Uri).ToArray(),
                    track.Album.Uri, track.DurationMs, track.IsExplicit, track.Image, track.Isrc, track.Year,
                    track.CanonicalUri, track.Source, track.Origin),
                FacetKind.PlayCount => new PlayCountValue(track.PlayCount),
                FacetKind.Descriptors when track.Tags is not null => new DescriptorsValue(track.Tags),
                FacetKind.AudioAttributes => new AudioAttributesValue(track.TempoBpm, track.MusicalKey, track.CamelotCode, track.CamelotColor),
                FacetKind.Availability when track.Availability is { } verdict => new AvailabilityValue(verdict, track.AvailableAt),
                _ => null,
            };
            if (value is null) return Unsupported(request);
        }
        else if (key.Facet == FacetKind.PlaylistHeader)
        {
            if (catalog is null) return Unsupported(request);
            var playlist = await catalog.GetPlaylistAsync(key.Subject, ct).ConfigureAwait(false);
            if (playlist is null) return Absent(request);
            patch = CatalogObservations.PlaylistHeader(key.Scope, playlist with { Uri = key.Subject }).Patch;
            CatalogDomainSeeds.Playlist(key.Scope, playlist, seeds);
        }
        else if (key.Facet is FacetKind.AlbumIdentity or FacetKind.AlbumDetail or FacetKind.AlbumTracks or FacetKind.AlbumVersions or FacetKind.Publishing)
        {
            if (catalog is null) return Unsupported(request);
            var album = await catalog.GetAlbumAsync(key.Subject, ct).ConfigureAwait(false);
            if (album is null) return Absent(request);
            CatalogDomainSeeds.Album(key.Scope, album, seeds);
            foreach (var related in album.MoreByArtist ?? []) CatalogDomainSeeds.Album(key.Scope, related, seeds);
            value = key.Facet switch
            {
                FacetKind.AlbumIdentity => new AlbumIdentityValue(album.Name, album.Cover, album.Artists.Select(artist => artist.Uri).ToArray(), album.Year, album.TrackCount, album.Kind),
                FacetKind.AlbumDetail => new AlbumDetailValue(album.Label, album.CourtesyLine, album.DiscCount, album.ShareUrl, album.IsPreRelease, album.PreReleaseEnd)
                    { MoreByArtistUris = album.MoreByArtist?.Select(related => related.Uri).ToArray() },
                FacetKind.Publishing => new PublishingValue(album.Copyright, album.ReleaseDate, album.ReleaseDatePrecision),
                FacetKind.AlbumTracks => TrackPage(key, album.Tracks ?? [], seeds),
                FacetKind.AlbumVersions => AlbumPage(key, album.OtherVersions ?? [], seeds),
                _ => null,
            };
        }
        else if (key.Facet is FacetKind.ArtistIdentity or FacetKind.ArtistOverview or FacetKind.ArtistPopular
            or FacetKind.ArtistDiscography or FacetKind.ArtistAppearsOn or FacetKind.ArtistRelated)
        {
            if (catalog is null) return Unsupported(request);
            var artist = await catalog.GetArtistAsync(key.Subject, ct).ConfigureAwait(false);
            if (artist is null) return Absent(request);
            CatalogDomainSeeds.Artist(key.Scope, artist, seeds);
            if (key.Facet == FacetKind.ArtistIdentity) value = new ArtistIdentityValue(artist.Name, artist.Image);
            else if (key.Facet == FacetKind.ArtistOverview) value = CatalogDomainSeeds.Overview(key.Scope, artist, seeds);
            else if (key.Facet == FacetKind.ArtistPopular) value = TrackPage(key, artist.TopTracks ?? [], seeds);
            else if (key.Facet == FacetKind.ArtistAppearsOn) value = AlbumPage(key, artist.AppearsOn ?? [], seeds);
            else if (key.Facet == FacetKind.ArtistDiscography)
            {
                var albums = artist.TopAlbums ?? [];
                if (Enum.TryParse<DiscographyKind>(key.Arguments.Filter, out var kind))
                    albums = albums.Where(album => AggregateCatalog.KindMatches(album.Kind, kind)).ToArray();
                value = AlbumPage(key, albums, seeds);
            }
            else
            {
                var related = artist.Extras?.Related ?? [];
                var rows = related.Select((item, index) => new CatalogRelationItem("related:" + index, item.Uri)).ToArray();
                foreach (var item in Window(related, key.Arguments))
                    seeds.Add(new(new(key.Scope, item.Uri, FacetKind.ArtistIdentity), new ArtistIdentityPatch(
                        FieldChange<string?>.Set(item.Name), FieldChange<Image?>.Set(item.Image))));
                value = Page(key, rows);
            }
        }
        else if (key.Facet is FacetKind.ShowIdentity or FacetKind.ShowEpisodes)
        {
            if (podcast is null) return Unsupported(request);
            var show = await podcast.GetShowAsync(key.Subject, ct).ConfigureAwait(false);
            if (show is null) return Absent(request);
            if (key.Facet == FacetKind.ShowIdentity)
                value = new ShowIdentityValue(show.Name, show.Publisher, show.Cover, show.Description, show.TotalEpisodes);
            else
            {
                var episodes = show.Episodes ?? [];
                foreach (var episode in Window(episodes, key.Arguments))
                    CatalogDomainSeeds.Episode(key.Scope, episode with { ShowUri = key.Subject }, seeds);
                value = Page(key, episodes.Select((episode, index) => new CatalogRelationItem("episode:" + index, episode.Uri)).ToArray());
            }
        }
        else if (key.Facet is FacetKind.EpisodeIdentity or FacetKind.EpisodeDetail)
        {
            if (podcast is null) return Unsupported(request);
            var episode = await podcast.GetEpisodeAsync(key.Subject, ct).ConfigureAwait(false);
            if (episode is null) return Absent(request);
            value = key.Facet == FacetKind.EpisodeDetail ? new EpisodeDetailValue(episode.Description)
                : new EpisodeIdentityValue(episode.Title, episode.ShowUri, episode.DurationMs, episode.Image, episode.PublishedAt, ShowName: episode.ShowName);
        }
        else return Unsupported(request);

        return Present(request, patch ?? new ReplaceFacetPatch(value!), seeds);
    }

    ResourceResponse Present(ResourceRequest request, CatalogPatch patch, IReadOnlyList<CatalogSeed>? seeds)
        => new(request, ResourceFetchResult.Present(patch, TimeSpan.FromHours(24)), seeds?.Select(seed =>
            seed with { Key = seed.Key with { Scope = seed.Key.Scope with
                { Provider = registry.OwnerOf(seed.Key.Subject)?.Id ?? seed.Key.Scope.Provider } } }).ToArray());
    static ResourceResponse Absent(ResourceRequest request) => new(request, ResourceFetchResult.Absent());
    static ResourceResponse Unsupported(ResourceRequest request) => new(request, new(ResourceFetchStatus.Unsupported));

    static RelationPageValue TrackPage(ResourceKey key, IReadOnlyList<Track> tracks, List<CatalogSeed> seeds)
    {
        foreach (var track in Window(tracks, key.Arguments)) CatalogDomainSeeds.Track(key.Scope, track, seeds);
        return Page(key, tracks.Select((track, index) => new CatalogRelationItem(track.ContextUid ?? "track:" + index,
            track.Uri, new(Chart: track.Chart))).ToArray());
    }
    static RelationPageValue AlbumPage(ResourceKey key, IReadOnlyList<Album> albums, List<CatalogSeed> seeds)
    {
        foreach (var album in Window(albums, key.Arguments)) CatalogDomainSeeds.Album(key.Scope, album, seeds);
        return Page(key, albums.Select((album, index) => new CatalogRelationItem("album:" + index, album.Uri)).ToArray());
    }
    static IEnumerable<T> Window<T>(IReadOnlyList<T> values, ResourceArguments args)
        => values.Skip(Math.Max(0, args.Offset)).Take(args.Limit > 0 ? args.Limit : 50);
    static RelationPageValue Page(ResourceKey key, IReadOnlyList<CatalogRelationItem> rows)
    {
        var snapshot = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(rows.Select(row =>
            row.OccurrenceKey.Length.ToString(CultureInfo.InvariantCulture) + ":" + row.OccurrenceKey + row.EntityUri.Length + ":" + row.EntityUri)))));
        var page = Window(rows, key.Arguments).ToArray();
        var offset = Math.Max(0, key.Arguments.Offset);
        var next = offset + page.Length;
        return new(key.Facet, snapshot, null, offset, rows.Count,
            next < rows.Count ? next.ToString(CultureInfo.InvariantCulture) : null,
            offset == 0 && page.Length == rows.Count ? RelationCoverage.Complete : RelationCoverage.Partial, page);
    }
}
