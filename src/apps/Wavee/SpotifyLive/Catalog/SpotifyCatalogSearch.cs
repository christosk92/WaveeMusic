using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.SpotifyLive.Catalog;

public sealed partial class SpotifyCatalogResourceProvider
{
    async Task<SearchResults?> FetchSearchAsync(PathfinderClient pf, string query, SearchFacet facet, int offset, int limit, CancellationToken ct)
    {
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 50);

        void Vars(Utf8JsonWriter w)
        {
            w.WriteBoolean("includePreReleases", false);
            w.WriteBoolean("includeAlbumPreReleases", true);
            w.WriteNumber("numberOfTopResults", limit);
            w.WriteString("searchTerm", query);
            w.WriteNumber("offset", offset);
            w.WriteNumber("limit", limit);
            w.WriteBoolean("includeAudiobooks", true);
            w.WriteBoolean("includeAuthors", true);
            w.WriteBoolean("includeEpisodeContentRatingsV2", true);
        }
        // The unified top-results op (the "All" tab) declares a DIFFERENT variable set, keyed on "query" (not "searchTerm").
        void VarsTop(Utf8JsonWriter w)
        {
            w.WriteString("query", query);
            w.WriteNumber("limit", limit);
            w.WriteNumber("offset", offset);
            w.WriteNumber("numberOfTopResults", 50);
            w.WriteBoolean("includeArtistHasConcertsField", false);
            w.WriteBoolean("includeAudiobooks", true);
            w.WriteBoolean("includeAuthors", true);
            w.WriteBoolean("includePreReleases", true);
            w.WriteBoolean("includeAlbumPreReleases", false);
            w.WriteBoolean("includeEpisodeContentRatingsV2", true);
            w.WriteNull("isPrefix");
            w.WriteStartArray("sectionFilters");
            w.WriteStringValue("GENERIC");
            w.WriteStringValue("VIDEO_CONTENT");
            w.WriteEndArray();
        }

        // Audiobooks is the ONE facet whose op sends includePreReleases:true (wire-verified, omg.saz sid 0671).
        void VarsAudiobooks(Utf8JsonWriter w)
        {
            w.WriteBoolean("includePreReleases", true);
            w.WriteBoolean("includeAlbumPreReleases", true);
            w.WriteNumber("numberOfTopResults", limit);
            w.WriteString("searchTerm", query);
            w.WriteNumber("offset", offset);
            w.WriteNumber("limit", limit);
            w.WriteBoolean("includeAudiobooks", true);
            w.WriteBoolean("includeAuthors", true);
            w.WriteBoolean("includeEpisodeContentRatingsV2", true);
        }

        // searchFullEpisodes takes a MINIMAL shape — sending the shared one would not match the persisted query.
        void VarsEpisodes(Utf8JsonWriter w)
        {
            w.WriteString("searchTerm", query);
            w.WriteNumber("offset", offset);
            w.WriteNumber("limit", limit);
            w.WriteBoolean("includeEpisodeContentRatingsV2", true);
        }

        // searchGenres: capture 1.2.96.518 (search.saz SID 098). Keyed on searchTerm; includeAlbumPreReleases false.
        void VarsGenres(Utf8JsonWriter w)
        {
            w.WriteBoolean("includePreReleases", false);
            w.WriteBoolean("includeAlbumPreReleases", false);
            w.WriteNumber("numberOfTopResults", 20);
            w.WriteString("searchTerm", query);
            w.WriteNumber("offset", offset);
            w.WriteNumber("limit", limit);
            w.WriteBoolean("includeAudiobooks", true);
            w.WriteBoolean("includeAuthors", true);
            w.WriteBoolean("includeEpisodeContentRatingsV2", true);
        }

        var callerCt = ct;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8), _time);
        using var searchCts = CancellationTokenSource.CreateLinkedTokenSource(callerCt, timeout.Token);
        ct = searchCts.Token;

        try
        {
            if (facet == SearchFacet.All)
            {
                using var topd = await pf.QueryOrThrowAsync(PathfinderOps.SearchTopResults, PathfinderOps.SearchTopResultsHash, VarsTop, PathfinderClient.Platform.WebPlayer, ct).ConfigureAwait(false);
                ValidateGraphQl(topd.RootElement);
                RequireObject(SpotifyExportMapper.Dig(topd.RootElement, "data", "searchV2"), "Search response omitted searchV2.");
                var topHits = Wavee.Core.SpotifyExportMapper.TopHitsFromV2(topd.RootElement);
                var totals = Wavee.Core.SpotifyExportMapper.SearchFromV2(topd.RootElement);
                return totals with { TopHits = topHits };
            }

            var (op, hash) = facet switch
            {
                SearchFacet.Tracks => (PathfinderOps.SearchTracks, PathfinderOps.SearchTracksHash),
                SearchFacet.Albums => (PathfinderOps.SearchAlbums, PathfinderOps.SearchAlbumsHash),
                SearchFacet.Artists => (PathfinderOps.SearchArtists, PathfinderOps.SearchArtistsHash),
                SearchFacet.Playlists => (PathfinderOps.SearchPlaylists, PathfinderOps.SearchPlaylistsHash),
                SearchFacet.Podcasts => (PathfinderOps.SearchPodcasts, PathfinderOps.SearchPodcastsHash),
                SearchFacet.Audiobooks => (PathfinderOps.SearchAudiobooks, PathfinderOps.SearchAudiobooksHash),
                SearchFacet.Episodes => (PathfinderOps.SearchFullEpisodes, PathfinderOps.SearchFullEpisodesHash),
                SearchFacet.Profiles => (PathfinderOps.SearchUsers, PathfinderOps.SearchUsersHash),
                SearchFacet.Genres => (PathfinderOps.SearchGenres, PathfinderOps.SearchGenresHash),
                SearchFacet.Authors => (PathfinderOps.SearchAuthors, PathfinderOps.SearchAuthorsHash),
                // Unreachable: every SearchFacet member is mapped above. Kept as a loud failure so a NEW enum member
                // added without an operation fails at the call instead of silently returning empty results.
                _ => throw new NotSupportedException($"Search facet '{facet}' is not wired to a Pathfinder operation."),
            };

            // Two ops do NOT take the shared variable shape:
            //   searchAudiobooks  — the only op sending includePreReleases:TRUE
            //   searchFullEpisodes — a completely different, minimal shape (no numberOfTopResults / include* flags)
            Action<Utf8JsonWriter> vars = facet switch
            {
                SearchFacet.Audiobooks => VarsAudiobooks,
                SearchFacet.Episodes => VarsEpisodes,
                SearchFacet.Genres => VarsGenres,
                _ => Vars,
            };

            using var doc = await pf.QueryOrThrowAsync(op, hash, vars, PathfinderClient.Platform.WebPlayer, ct).ConfigureAwait(false);
            ValidateGraphQl(doc.RootElement);
            RequireObject(SpotifyExportMapper.Dig(doc.RootElement, "data", "searchV2"), "Search response omitted searchV2.");
            return Wavee.Core.SpotifyExportMapper.SearchFromV2(doc.RootElement);
        }
        catch (OperationCanceledException) when (!callerCt.IsCancellationRequested)
        {
            throw new TimeoutException($"Spotify {facet} search timed out.");
        }
    }


}

