using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Catalog;
using Wavee.Backend.Sync;
using Wavee.Core;
using Wavee.Core.Catalog;

namespace Wavee.Backend.Queries;

/// <summary>Local thin-row preparation; the installed definition contains only selected URI/order references.</summary>
public sealed class LibrarySearchQueryDefinition : IAsyncQueryDefinition<LibrarySearchResults>
{
    public QueryReadResult<LibrarySearchResults> Pending => new(LibrarySearchResults.Empty, 0, false);
    readonly LibrarySearchQuery _query;
    readonly ICatalogSearchPersistence _persistence;
    readonly LibraryReplicaCoordinator _replicas;
    readonly Func<string, string> _owner;
    readonly string _set;
    readonly ResourceKey _diagnostic;

    public LibrarySearchQueryDefinition(LibrarySearchQuery query, ICatalogSearchPersistence persistence,
        LibraryReplicaCoordinator replicas, Func<string, string> ownerProvider)
    {
        _query = query; _persistence = persistence; _replicas = replicas; _owner = ownerProvider;
        _set = query.SearchScope == LibrarySearchScope.Artists ? "artists" : "albums";
        _diagnostic = new(query.Scope, CatalogSubjects.Search(query.Text), FacetKind.Search,
            new ResourceArguments(Filter: "library:" + query.SearchScope));
    }

    public QueryReadResult<LibrarySearchResults> Read(QueryReadContext read)
    {
        read.Read(_diagnostic, false);
        read.DependOnReplica(_set);
        return new(LibrarySearchResults.Empty, 0, false);
    }
    public QueryRequirements Requirements(LibrarySearchResults value, QueryDemand demand) => QueryRequirements.Empty;
    public bool IsInvalidatedBy(IReadOnlyList<ResourceKey> keys) => keys.Any(key =>
        CatalogSearchScopes.Rank(key.Scope, _query.Scope) >= 0 && key.Facet is
            FacetKind.TrackIdentity or FacetKind.AlbumIdentity or FacetKind.ArtistIdentity or FacetKind.AlbumTracks or FacetKind.ArtistDiscography);
    public bool IsInvalidatedByReplica(string id) => id == _set;

    public async ValueTask<IQueryDefinition<LibrarySearchResults>> PrepareAsync(CancellationToken ct)
    {
        var baseline = _replicas.ReadCollection(_set);
        var roots = baseline.Items.Select(item => item.Uri).ToArray();
        var corpus = await _persistence.ReadSearchCorpusAsync(_query.Scope, ct).ConfigureAwait(false);
        var selection = await Task.Run(() => LibrarySearchSelection.Select(corpus, _query.Scope, _query.SearchScope,
            roots, _query.Text, _owner, ct), ct).ConfigureAwait(false);
        return new Prepared(_query, _diagnostic, _set, selection, _replicas, _owner);
    }

    sealed class Prepared(LibrarySearchQuery query, ResourceKey diagnostic, string set,
        LibrarySearchSelection selection, LibraryReplicaCoordinator replicas, Func<string, string> owner)
        : IQueryDefinition<LibrarySearchResults>
    {
        public QueryReadResult<LibrarySearchResults> Pending => new(LibrarySearchResults.Empty, 0, false);
        readonly CatalogReadView _view = new(query.Scope, replicas, owner);
        public QueryRequirements Requirements(LibrarySearchResults value, QueryDemand demand) => QueryRequirements.Empty;
        public QueryReadResult<LibrarySearchResults> Read(QueryReadContext read)
        {
            read.Read(diagnostic, false);
            read.DependOnReplica(set);
            var text = query.Text.Trim();
            int Match(string value) => text.Length == 0 ? -1 : value.IndexOf(text, StringComparison.OrdinalIgnoreCase);
            LibraryAlbumGroup Album(LibrarySearchAlbum selected)
            {
                read.Read(_view.Key(selected.Uri, FacetKind.AlbumIdentity));
                var album = _view.Album(read, selected.Uri);
                int match = Match(album.Name);
                var tracks = selected.Tracks.Select(selectedTrack =>
                {
                    read.Read(_view.Key(selectedTrack.Uri, FacetKind.TrackIdentity));
                    var track = _view.Track(read, selectedTrack.Uri);
                    int trackMatch = Match(track.Title);
                    return new LibraryTrackHit(track.Uri, track.Title, track.Image ?? album.Cover, selectedTrack.AlbumIndex,
                        Math.Max(0, trackMatch), trackMatch < 0 ? 0 : text.Length);
                }).ToArray();
                var reason = match >= 0 || selected.InheritedMatch ? default : tracks.FirstOrDefault(track => track.MatchLen > 0)
                    is { MatchLen: > 0 } hit ? new MatchReason(LibraryMatchKind.Track, hit.Title) : default;
                return new(album.Uri, album.Name, album.Cover, album.Year, album.Kind, Math.Max(0, match), match < 0 ? 0 : text.Length, tracks, reason);
            }
            var artists = selection.Artists.Select(selected =>
            {
                read.Read(_view.Key(selected.Uri, FacetKind.ArtistIdentity));
                var artist = _view.Artist(read, selected.Uri);
                int match = Match(artist.Name);
                var albums = selected.Albums.Select(Album).OrderByDescending(album => album.MatchLen > 0)
                    .ThenByDescending(album => album.Year).ThenBy(album => album.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(album => album.Uri, StringComparer.Ordinal).ToArray();
                var matchedAlbum = albums.FirstOrDefault(album => album.MatchLen > 0);
                var matchedTrack = albums.SelectMany(album => album.Tracks).FirstOrDefault(track => track.MatchLen > 0);
                var reason = match >= 0 ? default : matchedAlbum is not null ? new MatchReason(LibraryMatchKind.Album, matchedAlbum.Name)
                    : matchedTrack.MatchLen > 0 ? new MatchReason(LibraryMatchKind.Track, matchedTrack.Title) : default;
                return new LibraryArtistGroup(artist.Uri, artist.Name, artist.Image, Math.Max(0, match), match < 0 ? 0 : text.Length, albums, reason);
            }).ToArray();
            var albumResults = selection.Albums.Select(Album).OrderByDescending(album => album.MatchLen > 0)
                .ThenByDescending(album => album.Year).ThenBy(album => album.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(album => album.Uri, StringComparer.Ordinal).ToArray();
            return new(new(artists, albumResults), 0, true);
        }
    }
}

public sealed record LibrarySearchTrack(string Uri, int AlbumIndex);
public sealed record LibrarySearchAlbum(string Uri, bool InheritedMatch, IReadOnlyList<LibrarySearchTrack> Tracks);
public sealed record LibrarySearchArtist(string Uri, IReadOnlyList<LibrarySearchAlbum> Albums);
public sealed record LibrarySearchSelection(IReadOnlyList<LibrarySearchArtist> Artists, IReadOnlyList<LibrarySearchAlbum> Albums)
{
    public const int ResultEntityLimit = 2048;
    public static LibrarySearchSelection Select(CatalogSearchCorpus corpus, CatalogScope scope, LibrarySearchScope searchScope,
        IReadOnlyList<string> roots, string text, Func<string, string> ownerProvider, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var query = text.Trim();
        if (query.Length == 0) return new([], []);
        bool Owned(CatalogScope candidate, string uri) => candidate.Provider == ownerProvider(uri);
        var rows = corpus.Rows.Where(row => CatalogSearchScopes.Rank(row.Scope, scope) >= 0 && Owned(row.Scope, row.Uri))
            .GroupBy(row => row.Uri, StringComparer.Ordinal).ToDictionary(group => group.Key,
                group => group.OrderBy(row => CatalogSearchScopes.Rank(row.Scope, scope)).First(), StringComparer.Ordinal);
        var albumsByArtist = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var tracksByAlbum = new Dictionary<string, List<LibrarySearchTrack>>(StringComparer.Ordinal);
        void AddAlbum(string artist, string album)
        {
            if (!albumsByArtist.TryGetValue(artist, out var albums)) albumsByArtist.Add(artist, albums = new(StringComparer.Ordinal));
            albums.Add(album);
        }
        var chosenPages = corpus.Pages.Where(page => CatalogSearchScopes.Rank(page.Scope, scope) >= 0 && Owned(page.Scope, page.ParentUri))
            .GroupBy(page => (page.ParentUri, page.Facet, page.ArgumentsKey))
            .Select(group => group.OrderBy(page => CatalogSearchScopes.Rank(page.Scope, scope)).First());
        foreach (var relation in chosenPages.GroupBy(page => (page.ParentUri, page.Facet,
                     Filter: ResourceArguments.FromStorageKey(page.ArgumentsKey).Filter)))
        {
            ct.ThrowIfCancellationRequested();
            var first = relation.Where(page => page.Offset == 0).OrderBy(page => CatalogSearchScopes.Rank(page.Scope, scope))
                .ThenByDescending(page => page.Revision).FirstOrDefault();
            if (first is null) continue;
            var pages = relation.Where(page => page.SnapshotId == first.SnapshotId && page.Scope == first.Scope)
                .OrderBy(page => page.Offset).ThenByDescending(page => page.Revision);
            if (relation.Key.Facet == FacetKind.AlbumTracks)
            {
                // An authoritative empty first page also suppresses obsolete Track.AlbumUri fallback links.
                var tracks = new List<LibrarySearchTrack>();
                tracksByAlbum[relation.Key.ParentUri] = tracks;
                var ordinals = new HashSet<int>();
                foreach (var page in pages)
                    for (int i = 0; i < page.Children.Count; i++)
                        if (ordinals.Add(page.Offset + i)) tracks.Add(new(page.Children[i], page.Offset + i));
            }
            else foreach (var page in pages)
                foreach (var album in page.Children) AddAlbum(relation.Key.ParentUri, album);
        }
        foreach (var row in rows.Values)
        {
            ct.ThrowIfCancellationRequested();
            if (row.Kind == EntityKind.Album) foreach (var artist in row.ArtistUris) AddAlbum(artist, row.Uri);
        }
        var relatedAlbums = new HashSet<string>(tracksByAlbum.Keys, StringComparer.Ordinal);
        foreach (var row in rows.Values)
        {
            ct.ThrowIfCancellationRequested();
            if (row.Kind != EntityKind.Track || string.IsNullOrEmpty(row.AlbumUri) || relatedAlbums.Contains(row.AlbumUri)) continue;
            if (!tracksByAlbum.TryGetValue(row.AlbumUri, out var tracks)) tracksByAlbum.Add(row.AlbumUri, tracks = []);
            tracks.Add(new(row.Uri, -1)); // Old imports cannot recover an album ordinal from an AlbumUri scalar.
        }
        bool Matches(string uri) => rows.TryGetValue(uri, out var row) && row.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
        string Name(string uri) => rows.GetValueOrDefault(uri)?.Title ?? "";
        LibrarySearchAlbum? SelectAlbum(string uri, bool inherited)
        {
            ct.ThrowIfCancellationRequested();
            bool own = Matches(uri);
            var tracks = tracksByAlbum.GetValueOrDefault(uri, []).Where(track => inherited || own || Matches(track.Uri)).Take(500).ToArray();
            return inherited || own || tracks.Length > 0 ? new(uri, inherited, tracks) : null;
        }
        int remaining = ResultEntityLimit;
        LibrarySearchAlbum? BoundAlbum(LibrarySearchAlbum album)
        {
            if (remaining-- <= 0) return null;
            var tracks = album.Tracks.Take(Math.Max(0, remaining)).ToArray();
            remaining -= tracks.Length;
            return album with { Tracks = tracks };
        }
        var orderedRoots = roots.Distinct(StringComparer.Ordinal).OrderByDescending(Matches)
            .ThenBy(Name, StringComparer.OrdinalIgnoreCase).ThenBy(uri => uri, StringComparer.Ordinal);
        if (searchScope == LibrarySearchScope.Albums)
        {
            var selected = new List<LibrarySearchAlbum>();
            foreach (var uri in orderedRoots)
            {
                if (remaining <= 0) break;
                if (SelectAlbum(uri, false) is { } album && BoundAlbum(album) is { } bounded) selected.Add(bounded);
            }
            return new([], selected.ToArray());
        }
        var artists = new List<LibrarySearchArtist>();
        foreach (var uri in orderedRoots)
        {
            ct.ThrowIfCancellationRequested();
            if (remaining <= 0 || artists.Count >= 200) break;
            bool own = Matches(uri);
            var candidates = (albumsByArtist.GetValueOrDefault(uri) ?? []).OrderByDescending(Matches)
                .ThenBy(Name, StringComparer.OrdinalIgnoreCase).ThenBy(album => album, StringComparer.Ordinal)
                .Select(album => SelectAlbum(album, own)).Where(album => album is not null).ToArray();
            if (!own && candidates.Length == 0) continue;
            remaining--;
            var albums = new List<LibrarySearchAlbum>();
            foreach (var candidate in candidates)
                if (BoundAlbum(candidate!) is { } bounded) albums.Add(bounded); else break;
            artists.Add(new(uri, albums.ToArray()));
        }
        return new(artists.ToArray(), []);
    }
}
