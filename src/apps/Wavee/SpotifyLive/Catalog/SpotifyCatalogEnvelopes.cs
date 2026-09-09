using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Catalog;
using Wavee.Backend.Queries;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Wavee.Core.Catalog;

namespace Wavee.SpotifyLive.Catalog;

public sealed partial class SpotifyCatalogResourceProvider
{
    async Task<JsonDocument> FetchAlbumDocumentAsync(string uri, CancellationToken ct)
        => await _pathfinder.QueryOrThrowAsync(PathfinderOps.GetAlbum, PathfinderOps.GetAlbumHash,
            writer => { writer.WriteString("uri", uri); writer.WriteString("locale", "");
                writer.WriteNumber("offset", 0); writer.WriteNumber("limit", 50); },
            PathfinderClient.Platform.WebPlayer, ct).ConfigureAwait(false);

    static ResourceResponse ReadAlbumDocument(ResourceRequest request, JsonDocument document)
    {
        var key = request.Key;
        ValidateGraphQl(document.RootElement);
        if (ExplicitlyMissing(document.RootElement, "albumUnion")) return new(request, ResourceFetchResult.Absent());
        var album = SpotifyExportMapper.AlbumFromUnion(document.RootElement)
            ?? throw new JsonException("Album envelope omitted its album value.");
        var seeds = new List<CatalogSeed>();
        CatalogDomainSeeds.Album(key.Scope, album, seeds);
        foreach (var related in album.MoreByArtist ?? []) CatalogDomainSeeds.Album(key.Scope, related, seeds);
        CatalogValue value;
        if (key.Facet == FacetKind.AlbumDetail)
            value = new AlbumDetailValue(album.Label, album.CourtesyLine, album.DiscCount, album.ShareUrl,
                album.IsPreRelease, album.PreReleaseEnd) { MoreByArtistUris = album.MoreByArtist?.Select(a => a.Uri).ToArray() };
        else
        {
            var versions = album.OtherVersions ?? Array.Empty<Album>();
            foreach (var version in versions) CatalogDomainSeeds.Album(key.Scope, version, seeds);
            value = Relation(key, versions.Select(version => version.Uri).ToArray(), Snapshot(document.RootElement));
        }
        return Present(request, new(new ReplaceFacetPatch(value), seeds));
    }

    async Task<ResourceResponse> FetchEnvelopeAsync(ResourceRequest request, CancellationToken ct)
    {
        var key = request.Key;
        if (key.Facet == FacetKind.SearchSuggestions)
        {
            const string prefix = "wavee:catalog:search:";
            if (!key.Subject.StartsWith(prefix, StringComparison.Ordinal)) throw new ArgumentException("Suggestion subject is malformed.");
            using var doc = await _pathfinder.QueryOrThrowAsync(PathfinderOps.SearchSuggestions, PathfinderOps.SearchSuggestionsHash,
                writer =>
                {
                    writer.WriteString("query", Uri.UnescapeDataString(key.Subject[prefix.Length..]));
                    writer.WriteNumber("limit", 30); writer.WriteNumber("numberOfTopResults", 30); writer.WriteNumber("offset", 0);
                    writer.WriteBoolean("includeAuthors", true); writer.WriteBoolean("includeAlbumPreReleases", false);
                    writer.WriteBoolean("includeEpisodeContentRatingsV2", true);
                }, PathfinderClient.Platform.WebPlayer, ct).ConfigureAwait(false);
            ValidateGraphQl(doc.RootElement);
            return Present(request, CatalogDomainSeeds.Suggestions(key.Scope, SpotifyExportMapper.SuggestionsFromV2(doc.RootElement)));
        }
        if (key.Facet is FacetKind.PlaylistHeader or FacetKind.PlaylistRevision)
        {
            var fetcher = new Wavee.Backend.Playlists.PlaylistFetcher(_http, _baseUrl, () => request.Stamp.ActualAccount);
            if (key.Facet == FacetKind.PlaylistRevision)
            {
                var revision = await fetcher.FetchPlaylistRevisionAsync(key.Subject, ct).ConfigureAwait(false);
                if (!Wavee.Backend.Playlists.PlaylistRevisions.IsWellFormed(revision))
                    throw new FormatException("The playlist revision response omitted a valid head.");
                return new(request, ResourceFetchResult.Present(new ReplaceFacetPatch(
                    new PlaylistRevisionValue(Convert.ToHexString(revision!))), TimeSpan.FromSeconds(5)));
            }
            var read = await fetcher.ReadHeaderAsync(key.Subject, ct).ConfigureAwait(false);
            var patch = (PlaylistHeaderPatch)CatalogObservations.PlaylistHeader(key.Scope, read.Header).Patch;
            if (!Wavee.Backend.Playlists.PlaylistRevisions.IsWellFormed(read.Revision))
                throw new FormatException("The playlist header response omitted its coherent revision.");
            patch = patch with { Edition = FieldChange<string?>.Set(Convert.ToHexString(read.Revision!)) };
            return new(request, ResourceFetchResult.Present(patch));
        }
        if (key.Facet == FacetKind.HomeSection)
        {
            using var document = await _pathfinder.QueryOrThrowAsync(PathfinderOps.HomeSection, PathfinderOps.HomeSectionHash, writer =>
            {
                writer.WriteString("uri", key.Subject); writer.WriteString("homeEndUserIntegration", "INTEGRATION_DESKTOP");
                writer.WriteString("timeZone", SpotifyTimeZone.LocalIana); writer.WriteString("sp_t", "");
                writer.WriteNumber("sectionItemsOffset", key.Arguments.Offset);
                writer.WriteNumber("sectionItemsLimit", key.Arguments.Limit > 0 ? key.Arguments.Limit : 50);
                writer.WriteBoolean("includeEpisodeContentRatingsV2", true);
            }, PathfinderClient.Platform.Desktop, ct).ConfigureAwait(false);
            ValidateGraphQl(document.RootElement);
            var page = SpotifyHomeComposer.SectionPage(document.RootElement)
                ?? throw new JsonException("Home section response omitted its section document.");
            var decoded = CatalogDomainSeeds.Home(key.Scope, new([], null, Sections: [page.Section]));
            var value = (CatalogDocumentValue)decoded.Patch.Apply(null);
            return Present(request, decoded with { Patch = new ReplaceFacetPatch(value with
            {
                Kind = FacetKind.HomeSection,
                NextCursor = page.NextOffset is { } next && next > key.Arguments.Offset
                    ? next.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            }) });
        }
        if (key.Facet == FacetKind.Home)
        {
            string facet = key.Arguments.Filter ?? "";
            using var document = await _pathfinder.QueryOrThrowAsync(PathfinderOps.Home, PathfinderOps.HomeHash, writer =>
            {
                writer.WriteString("homeEndUserIntegration", "INTEGRATION_DESKTOP");
                writer.WriteString("timeZone", SpotifyTimeZone.LocalIana);
                writer.WriteString("sp_t", ""); writer.WriteString("facet", facet);
                writer.WriteNumber("sectionItemsLimit", 10);
                writer.WriteBoolean("includeEpisodeContentRatingsV2", true);
            }, PathfinderClient.Platform.Desktop, ct).ConfigureAwait(false);
            ValidateGraphQl(document.RootElement);
            var home = SpotifyExportMapper.Dig(document.RootElement, "data", "home");
            RequireObject(home, "Home response omitted its home document.");
            var contribution = SpotifyHomeComposer.Compose(home, Array.Empty<PlaylistSummary>(), _homeTitles());
            var decoded = CatalogDomainSeeds.Home(key.Scope, new LiveHomeResult(contribution.Groups, contribution.Chips,
                contribution.Greeting, contribution.Sections, facet));
            return Present(request, decoded);
        }
        if (key.Facet == FacetKind.Search)
        {
            if (!Enum.TryParse<SearchFacet>(key.Arguments.Filter, out var facet)) facet = SearchFacet.All;
            const string prefix = "wavee:catalog:search:";
            if (!key.Subject.StartsWith(prefix, StringComparison.Ordinal)) throw new ArgumentException("Search subject is malformed.");
            var results = await FetchSearchAsync(_pathfinder, Uri.UnescapeDataString(key.Subject[prefix.Length..]),
                facet, key.Arguments.Offset, key.Arguments.Limit > 0 ? key.Arguments.Limit : 30, ct).ConfigureAwait(false)
                ?? throw new JsonException("Search response omitted its result document.");
            return Present(request, CatalogDomainSeeds.Search(key.Scope, results, facet));
        }
        if (key.Facet is FacetKind.ArtistOverview or FacetKind.ArtistRelated)
        {
            using var document = await _pathfinder.QueryOrThrowAsync(PathfinderOps.QueryArtistOverview, PathfinderOps.QueryArtistOverviewHash,
                writer => { writer.WriteString("uri", key.Subject); writer.WriteString("locale", ""); writer.WriteBoolean("preReleaseV2", true); },
                PathfinderClient.Platform.WebPlayer, ct).ConfigureAwait(false);
            ValidateGraphQl(document.RootElement);
            if (ExplicitlyMissing(document.RootElement, "artistUnion")) return new(request, ResourceFetchResult.Absent());
            var artist = SpotifyExportMapper.ArtistFromOverview(document.RootElement)
                ?? throw new JsonException("Artist overview omitted its artist value.");
            var seeds = new List<CatalogSeed>();
            var overview = CatalogDomainSeeds.Overview(key.Scope, artist, seeds);
            if (artist.TopTracks is { } popular)
            {
                foreach (var track in popular) CatalogDomainSeeds.Track(key.Scope, track, seeds);
                // The overview is only a partial popular-list observation, never the full chart baseline. The
                // relation key must match what RelationProjection reads/requires (offset 0, page-sized) — a
                // default Arguments seeds a key nothing ever reads, an orphan.
                var relationKey = key with { Facet = FacetKind.ArtistPopular, Arguments = new(0, RelationProjection.PageSize) };
                seeds.Add(new(relationKey, new ReplaceFacetPatch(new RelationPageValue(FacetKind.ArtistPopular,
                    Snapshot(document.RootElement), null, 0, null, null, RelationCoverage.Partial,
                    popular.Select((track, index) => new CatalogRelationItem("popular:" + index, track.Uri)).ToArray()))));
            }
            CatalogValue value = key.Facet == FacetKind.ArtistOverview ? overview
                : Relation(key, artist.Extras?.Related?.Select(related => related.Uri).ToArray() ?? [], Snapshot(document.RootElement));
            return Present(request, new(new ReplaceFacetPatch(value), seeds));
        }
        if (key.Facet == FacetKind.Availability)
        {
            using var document = await _pathfinder.QueryOrThrowAsync(PathfinderOps.GetTrack, PathfinderOps.GetTrackHash,
                writer => writer.WriteString("uri", key.Subject), PathfinderClient.Platform.WebPlayer, ct).ConfigureAwait(false);
            ValidateGraphQl(document.RootElement);
            if (ExplicitlyMissing(document.RootElement, "trackUnion")) return new(request, ResourceFetchResult.Absent());
            var track = SpotifyExportMapper.TrackFromUnion(document.RootElement)
                ?? throw new JsonException("Track envelope omitted its track value.");
            if (track.Availability is not { } verdict) throw new JsonException("Track envelope omitted playability.");
            var seeds = new List<CatalogSeed>();
            CatalogDomainSeeds.Track(key.Scope, track, seeds);
            return Present(request, new(new ReplaceFacetPatch(new AvailabilityValue(verdict, track.AvailableAt)), seeds));
        }
        if (key.Facet == FacetKind.ArtistPopular) return await FetchChartAsync(request, ct).ConfigureAwait(false);
        if (key.Facet == FacetKind.UserIdentity) return await FetchUserAsync(request, ct).ConfigureAwait(false);
        return new(request, new ResourceFetchResult(ResourceFetchStatus.Unsupported));
    }

    async Task<ResourceResponse> FetchChartAsync(ResourceRequest request, CancellationToken ct)
    {
        string url = _baseUrl() + "/artistplaycontext/v1/page/spotify/artist-top-tracks-extensions/" + Uri.EscapeDataString(request.Key.Subject);
        using var response = await _http.SendAsync(new HttpReq("GET", url,
            new Dictionary<string, string> { ["Accept"] = "application/json" }, null), ct).ConfigureAwait(false);
        if (response.Status == 404) return new(request, ResourceFetchResult.Absent());
        if (response.Status is < 200 or > 299) return Failed(request, HttpError(response.Status));
        using var document = await JsonDocument.ParseAsync(response.Body, cancellationToken: ct).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Array)
            throw new JsonException("Artist chart omitted its ordered tracks array.");
        var uris = new List<string>();
        foreach (var track in tracks.EnumerateArray())
        {
            if (!track.TryGetProperty("uri", out var uri) || uri.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(uri.GetString()))
                throw new JsonException("Artist chart row omitted its identity.");
            uris.Add(uri.GetString()!);
        }
        return Present(request, new(new ReplaceFacetPatch(Relation(request.Key, uris, Snapshot(document.RootElement))), []));
    }

    async Task<ResourceResponse> FetchUserAsync(ResourceRequest request, CancellationToken ct)
    {
        // The profile endpoint is a finite source operation; it never invents a display name from the account ID.
        string url = _baseUrl() + "/user-profile-view/v3/profile/" + Uri.EscapeDataString(EntityUri.IdOf(request.Key.Subject)) + "?market=from_token";
        using var response = await _http.SendAsync(new HttpReq("GET", url,
            new Dictionary<string, string> { ["Accept"] = "application/json" }, null), ct).ConfigureAwait(false);
        if (response.Status == 404) return new(request, ResourceFetchResult.Absent());
        if (response.Status is < 200 or > 299) return Failed(request, HttpError(response.Status));
        using var document = await JsonDocument.ParseAsync(response.Body, cancellationToken: ct).ConfigureAwait(false);
        RequireObject(document.RootElement, "Profile response was not an object.");
        var root = document.RootElement;
        string? name = String(root, "name") ?? String(root, "display_name");
        string? avatar = String(root, "image_url");
        if (avatar is null && root.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array && images.GetArrayLength() > 0)
            avatar = String(images[0], "url");
        return Present(request, new(new ReplaceFacetPatch(new UserIdentityValue(name, avatar is null ? null : new Image(avatar))), []));
    }

    static ResourceResponse Present(ResourceRequest request, CatalogDecodedFacet decoded)
        => new(request, ResourceFetchResult.Present(decoded.Patch), decoded.Seeds);
    static void ValidateGraphQl(JsonElement root)
    {
        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            throw new JsonException("Provider returned GraphQL errors; partial data is not an authoritative empty answer.");
    }
    static bool ExplicitlyMissing(JsonElement root, string union)
    {
        var item = SpotifyExportMapper.Dig(root, "data", union);
        return item.ValueKind == JsonValueKind.Object && item.TryGetProperty("__typename", out var type)
            && type.GetString() is "NotFound" or "NotFoundError";
    }
    static void RequireObject(JsonElement element, string error)
    { if (element.ValueKind != JsonValueKind.Object) throw new JsonException(error); }
    static string? String(JsonElement element, string field) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    static string Snapshot(JsonElement element) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(element.GetRawText())));
    static RelationPageValue Relation(ResourceKey key, IReadOnlyList<string> uris, string snapshot)
    {
        int offset = Math.Clamp(key.Arguments.Offset, 0, uris.Count);
        int count = key.Arguments.Limit > 0 ? Math.Min(key.Arguments.Limit, uris.Count - offset) : uris.Count - offset;
        return new(key.Facet, snapshot, null, offset, uris.Count,
            offset + count < uris.Count ? (offset + count).ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            offset == 0 && count == uris.Count ? RelationCoverage.Complete : RelationCoverage.Partial,
            uris.Skip(offset).Take(count).Select((uri, index) => new CatalogRelationItem("row:" + (offset + index), uri)).ToArray());
    }
}
