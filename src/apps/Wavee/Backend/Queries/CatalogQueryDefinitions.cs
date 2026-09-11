using System;
using System.Collections.Generic;
using System.Linq;
using Wavee.Backend.Playlists;
using Wavee.Backend.Sync;
using Wavee.Core;
using Wavee.Core.Catalog;
using CatalogSearchQuery = Wavee.Core.Catalog.SearchQuery;

namespace Wavee.Backend.Queries;

public static class CatalogQueryDefinitions
{
    public static void Register(QueryService service, LibraryReplicaCoordinator replicas, Func<string, string> ownerProvider)
    {
        service.Register<EntityCardQuery, EntityCardSnapshot>(q => new EntityCardDefinition(q, replicas, ownerProvider));
        service.Register<PlaylistDetailQuery, Playlist>(q => new PlaylistDefinition(q, replicas, ownerProvider));
        service.Register<AlbumDetailQuery, Album>(q => new AlbumDefinition(q, replicas, ownerProvider));
        service.Register<ArtistDetailQuery, Artist>(q => new ArtistDefinition(q, replicas, ownerProvider));
        service.Register<ArtistIdentityQuery, Artist>(q => new ArtistIdentityDefinition(q, replicas, ownerProvider));
        service.Register<ArtistDiscographyQuery, Wavee.Core.DiscographyPage>(q => new DiscographyDefinition(q, replicas, ownerProvider));
        service.Register<ArtistReleasesQuery, Wavee.Core.DiscographyPage>(q => new ArtistReleasesDefinition(q, replicas, ownerProvider));
        service.Register<HomeSectionQuery, HomeSectionPageResult?>(q => new HomeSectionDefinition(q, replicas, ownerProvider));
        service.Register<ShowDetailQuery, Show>(q => new ShowDefinition(q, replicas, ownerProvider));
        service.Register<LikedSongsQuery, IReadOnlyList<Track>>(q => new LikedDefinition(q, replicas, ownerProvider));
        service.Register<SavedAlbumsQuery, IReadOnlyList<Album>>(q => new SavedDefinition<Album>(q.Scope, replicas, ownerProvider, "albums", FacetKind.AlbumIdentity, (v, r, u) => v.Album(r, u), a => a.Uri));
        service.Register<SavedArtistsQuery, IReadOnlyList<Artist>>(q => new SavedDefinition<Artist>(q.Scope, replicas, ownerProvider, "artists", FacetKind.ArtistIdentity, (v, r, u) => v.Artist(r, u), a => a.Uri));
        service.Register<SavedShowsQuery, IReadOnlyList<Show>>(q => new SavedDefinition<Show>(q.Scope, replicas, ownerProvider, "shows", FacetKind.ShowIdentity, (v, r, u) => v.Show(r, u), a => a.Uri));
        service.Register<HomeQuery, HomeFeed>(q => new HomeDefinition(q, replicas, ownerProvider));
        service.Register<SidebarLibraryQuery, LibraryQuerySnapshot>(q => new LibraryDefinition(q, replicas, ownerProvider));
        service.Register<PlaylistTargetsQuery, PlaylistTargetsSnapshot>(q => new PlaylistTargetsQueryDefinition(q, replicas, ownerProvider));
        service.Register<CatalogSearchQuery, SearchResults>(q => new SearchDefinition(q, replicas, ownerProvider));
        service.Register<SearchSuggestionsQuery, SearchSuggestions>(q => new SuggestionsDefinition(q, replicas, ownerProvider));
    }

    abstract class Definition<T>(CatalogScope scope, LibraryReplicaCoordinator replicas, Func<string, string> ownerProvider) : IQueryDefinition<T>
    {
        protected CatalogReadView View { get; } = new(scope, replicas, ownerProvider);
        protected LibraryReplicaCoordinator Replicas => replicas;
        public abstract QueryReadResult<T> Pending { get; }
        public abstract QueryReadResult<T> Read(QueryReadContext read);
        public abstract QueryRequirements Requirements(T value, QueryDemand demand);
        protected IEnumerable<ResourceKey> RowKeys(IReadOnlyList<Track>? tracks, IReadOnlyList<FacetKind> facets)
        {
            if (tracks is null) yield break;
            foreach (var track in tracks)
            {
                yield return View.Key(track.Uri, CatalogReadView.IdentityFacet(track.Uri));
                if (!string.IsNullOrEmpty(track.Album.Uri)) yield return View.Key(track.Album.Uri, CatalogReadView.IdentityFacet(track.Album.Uri));
                foreach (var artist in track.Artists) if (!string.IsNullOrEmpty(artist.Uri)) yield return View.Key(artist.Uri, FacetKind.ArtistIdentity);
                foreach (var facet in facets) yield return View.Key(track.Uri, facet);
            }
        }

        protected IEnumerable<ResourceKey> AlbumKeys(IReadOnlyList<Album>? albums)
        {
            if (albums is null) yield break;
            foreach (var album in albums)
            {
                yield return View.Key(album.Uri, FacetKind.AlbumIdentity);
                foreach (var artist in album.Artists)
                    if (!string.IsNullOrEmpty(artist.Uri)) yield return View.Key(artist.Uri, FacetKind.ArtistIdentity);
            }
        }
        // QueryService runs Requirements two to four times per projection — the pre-join sizing pass, the read
        // recipe, the active recipe, one per finite demand — and each of them used to push every key through LINQ's
        // Distinct into a FRESH array, ~1.4k keys for a playlist with a global sort. With an identity-stable value
        // and the same demand that answer is the same answer, so the dedupe buffers are reused and the previous
        // array is handed back whenever the sequence matches: the steady state costs one walk and no allocation,
        // and QuerySnapshot.Demanded keeps its reference across a scroll burst.
        readonly HashSet<ResourceKey> _requiredSeen = [];
        readonly List<ResourceKey> _requiredBuffer = [];
        ResourceKey[] _requiredKeys = [];
        protected QueryRequirements Required(IEnumerable<ResourceKey> keys, params ReplicaRequest[] replicas)
        {
            _requiredSeen.Clear();
            _requiredBuffer.Clear();
            // The walk itself is load-bearing — RelationProjection.Require moves a paged relation's requested end
            // while it is enumerated — so it always runs to completion. Only the RESULT is reused.
            foreach (var key in keys) if (_requiredSeen.Add(key)) _requiredBuffer.Add(key);
            var previous = _requiredKeys;
            if (previous.Length == _requiredBuffer.Count)
            {
                bool same = true;
                for (int i = 0; i < previous.Length && same; i++) same = previous[i] == _requiredBuffer[i];
                if (same) return new(previous, replicas);
            }
            return new(_requiredKeys = _requiredBuffer.ToArray(), replicas);
        }
    }

    sealed class EntityCardDefinition(EntityCardQuery query, LibraryReplicaCoordinator replicas, Func<string, string> ownerProvider)
        : Definition<EntityCardSnapshot>(query.Scope, replicas, ownerProvider)
    {
        ResourceKey[] _identityKeys = [];
        public override QueryReadResult<EntityCardSnapshot> Pending => new(new(query.Uri, "", null, null, null), 0, false);
        public override QueryReadResult<EntityCardSnapshot> Read(QueryReadContext read)
        {
            var identity = read.Read(View.Key(query.Uri, CatalogReadView.IdentityFacet(query.Uri)));
            var card = View.HomeCard(read, new(query.Uri, query.Uri));
            var track = EntityUri.Parse(query.Uri).IsPlayable ? View.Track(read, query.Uri) : null;
            _identityKeys = read.Resources.Keys.Where(key => key.Facet is FacetKind.TrackIdentity or FacetKind.EpisodeIdentity
                or FacetKind.AlbumIdentity or FacetKind.ArtistIdentity or FacetKind.ShowIdentity or FacetKind.UserIdentity or FacetKind.PlaylistHeader).ToArray();
            int? childCount = identity.Value switch
            {
                AlbumIdentityValue album => album.TrackCount,
                ShowIdentityValue show => show.EpisodeCount,
                PlaylistHeaderValue playlist => playlist.TrackCount,
                _ => null,
            };
            return new(new(query.Uri, card.Title, card.Subtitle, card.Image, track) { ChildCount = childCount },
                0, identity.Value is not null);
        }
        public override QueryRequirements Requirements(EntityCardSnapshot value, QueryDemand demand)
            => Required(RowKeys(value.Playable is { } track ? [track] : [], demand.Facets).Concat(_identityKeys));
    }

    sealed class ArtistIdentityDefinition(ArtistIdentityQuery query, LibraryReplicaCoordinator replicas, Func<string, string> ownerProvider) : Definition<Artist>(query.Scope, replicas, ownerProvider)
    {
        public override QueryReadResult<Artist> Pending => new(new("", query.Uri, "", null), 0, false);
        public override QueryReadResult<Artist> Read(QueryReadContext read)
        {
            var identity = read.Read(View.Key(query.Uri, FacetKind.ArtistIdentity));
            return new(View.Artist(read, query.Uri), 0, identity.Value is not null);
        }
        public override QueryRequirements Requirements(Artist value, QueryDemand demand)
            => Required([View.Key(query.Uri, FacetKind.ArtistIdentity)]);
    }

    sealed class PlaylistDefinition(PlaylistDetailQuery query, LibraryReplicaCoordinator replicas, Func<string, string> ownerProvider) : Definition<Playlist>(query.Scope, replicas, ownerProvider)
    {
        // Rebuilt only when the header's collaborator LIST or owner identity actually changes (a re-Peek that lands
        // the same header hands back the same CollaboratorUris reference), not on every Read — Read runs once per
        // pass, same as Requirements' own Required() reuse below.
        IReadOnlyList<string>? _headerCollaborators;
        string? _headerOwner;
        readonly List<ResourceKey> _headerProfileBuffer = new();
        ResourceKey[] _headerProfiles = [];
        // Reused across every Requirements() call this definition ever runs (QueryService calls it three to four
        // times per projection) instead of building a fresh LINQ Prepend/Concat/Select/Where chain each time.
        readonly List<ResourceKey> _requirementsBuffer = new();
        IReadOnlyList<Track>? _requirementTracks;
        ResourceKey[] _addedByKeys = [];
        public override QueryReadResult<Playlist> Pending => new(new("", query.Uri, "", null, "", null, 0) { MembershipLoaded = false }, 0, false);
        public override QueryReadResult<Playlist> Read(QueryReadContext read)
        {
            var value = View.Playlist(read, query.Uri, true);
            var header = read.Read<PlaylistHeaderValue>(View.Key(query.Uri, FacetKind.PlaylistHeader));
            var collaborators = header?.CollaboratorUris;
            var owner = header?.OwnerUri;
            if (!ReferenceEquals(_headerCollaborators, collaborators) || _headerOwner != owner)
            {
                _headerCollaborators = collaborators;
                _headerOwner = owner;
                _headerProfileBuffer.Clear();
                var normalizedOwner = UserProfileIds.Normalize(owner ?? "");
                if (normalizedOwner is not null) _headerProfileBuffer.Add(View.Key(normalizedOwner, FacetKind.UserIdentity));
                if (collaborators is not null)
                    foreach (var uri in collaborators)
                    {
                        var normalized = UserProfileIds.Normalize(uri);
                        if (normalized is not null) _headerProfileBuffer.Add(View.Key(normalized, FacetKind.UserIdentity));
                    }
                _headerProfiles = _headerProfileBuffer.ToArray();
            }
            var baseline = Replicas.ReadPlaylist(query.Uri);
            // Membership is the only primary-data gate. A resident header used to count as "ready" and then
            // PageReadiness waited for every demanded identity, so a cached playlist sat behind the skeleton
            // for a two-track network fetch. Missing identities are loading rows, not a pending page.
            return new(value, baseline.OrderRevision, value.MembershipLoaded);
        }
        public override QueryRequirements Requirements(Playlist value, QueryDemand demand)
        {
            _requirementsBuffer.Clear();
            _requirementsBuffer.Add(View.Key(query.Uri, FacetKind.PlaylistHeader));
            foreach (var key in RowKeys(value.Tracks, demand.Facets)) _requirementsBuffer.Add(key);
            foreach (var key in _headerProfiles) _requirementsBuffer.Add(key);
            var tracks = value.Tracks;
            if (!ReferenceEquals(_requirementTracks, tracks))
            {
                _requirementTracks = tracks;
                if (tracks is null || tracks.Count == 0) _addedByKeys = [];
                else
                {
                    var added = new List<ResourceKey>();
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var track in tracks)
                    {
                        var normalized = UserProfileIds.Normalize(track.AddedBy);
                        if (normalized is not null && seen.Add(normalized))
                            added.Add(View.Key(normalized, FacetKind.UserIdentity));
                    }
                    _addedByKeys = added.Count == 0 ? [] : added.ToArray();
                }
            }
            foreach (var key in _addedByKeys) _requirementsBuffer.Add(key);
            return Required(_requirementsBuffer, demand.Active ? [new("playlist", query.Uri)] : []);
        }
    }

    sealed class AlbumDefinition : Definition<Album>
    {
        public override QueryReadResult<Album> Pending => new(new("", _query.Uri, "", null, [], 0, 0), 0, false);
        readonly AlbumDetailQuery _query;
        readonly RelationProjection _tracks, _versions;
        public AlbumDefinition(AlbumDetailQuery query, LibraryReplicaCoordinator replicas, Func<string, string> ownerProvider) : base(query.Scope, replicas, ownerProvider)
        {
            _query = query; _tracks = new(args => View.Key(query.Uri, FacetKind.AlbumTracks, args));
            _versions = new(args => View.Key(query.Uri, FacetKind.AlbumVersions, args));
        }
        Album? _value;
        public override QueryReadResult<Album> Read(QueryReadContext read)
        {
            var identity = read.Read(View.Key(_query.Uri, FacetKind.AlbumIdentity));
            var detail = read.Read<AlbumDetailValue>(View.Key(_query.Uri, FacetKind.AlbumDetail));
            _tracks.Read(read); _versions.Read(read);
            // The joined album is identity-stable, but `with` always allocates and these two card lists are built
            // fresh — and a record compares a list member by REFERENCE. Reduce them to the previous instances and
            // the unchanged read hands back the previous album, so QueryService's value test short-circuits.
            var album = View.Album(read, _query.Uri, _tracks.Loaded ? _tracks.Items : null) with
            {
                OtherVersions = CatalogReadView.Reuse(_value?.OtherVersions,
                    _versions.Loaded ? _versions.Items.Select(item => View.Album(read, item.EntityUri)).ToArray() : null),
                MoreByArtist = CatalogReadView.Reuse(_value?.MoreByArtist,
                    detail?.MoreByArtistUris?.Select(uri => View.Album(read, uri)).ToArray()),
            };
            if (_value is not null && _value == album) album = _value; else _value = album;
            return new(album, _tracks.OrderRevision, identity.Value is not null);
        }
        public override QueryRequirements Requirements(Album value, QueryDemand demand)
            => Required(new[] { View.Key(_query.Uri, FacetKind.AlbumIdentity), View.Key(_query.Uri, FacetKind.AlbumDetail),
                    View.Key(_query.Uri, FacetKind.Publishing) }
                .Concat(_tracks.Require(_tracks.Total ?? (value.TrackCount > 0 ? value.TrackCount : int.MaxValue)))
                .Concat(RowKeys(value.Tracks, demand.Facets))
                .Concat(_versions.Require(_versions.Total ?? int.MaxValue))
                .Concat(value.Artists.Where(artist => artist.Uri.Length > 0).Select(artist => View.Key(artist.Uri, FacetKind.ArtistIdentity)))
                .Concat(AlbumKeys(value.OtherVersions))
                .Concat(AlbumKeys(value.MoreByArtist)));
    }

    sealed class ArtistDefinition : Definition<Artist>
    {
        public override QueryReadResult<Artist> Pending => new(new("", _query.Uri, "", null), 0, false);
        readonly ArtistDetailQuery _query;
        readonly RelationProjection _popular, _albums, _singles, _compilations, _appears;
        public ArtistDefinition(ArtistDetailQuery query, LibraryReplicaCoordinator replicas, Func<string, string> ownerProvider) : base(query.Scope, replicas, ownerProvider)
        {
            _query = query; _popular = new(args => View.Key(query.Uri, FacetKind.ArtistPopular, args));
            _albums = new(args => View.Key(query.Uri, FacetKind.ArtistDiscography, args), "Albums");
            _singles = new(args => View.Key(query.Uri, FacetKind.ArtistDiscography, args), "Singles");
            _compilations = new(args => View.Key(query.Uri, FacetKind.ArtistDiscography, args), "Compilations");
            _appears = new(args => View.Key(query.Uri, FacetKind.ArtistAppearsOn, args));
        }
        public override QueryReadResult<Artist> Read(QueryReadContext read)
        {
            var identity = read.Read(View.Key(_query.Uri, FacetKind.ArtistIdentity));
            _popular.Read(read); _albums.Read(read); _singles.Read(read); _compilations.Read(read); _appears.Read(read);
            var artist = View.Artist(read, _query.Uri, true);
            var value = artist with
            {
                TopTracks = _popular.Items.Select(row => View.Track(read, row.EntityUri)).ToArray(),
                TopAlbums = _albums.Items.Select(row => ReleaseAlbum(View, read, row.EntityUri, DiscographyKind.Albums))
                    .Concat(_singles.Items.Select(row => ReleaseAlbum(View, read, row.EntityUri, DiscographyKind.Singles)))
                    .Concat(_compilations.Items.Select(row => ReleaseAlbum(View, read, row.EntityUri, DiscographyKind.Compilations))).ToArray(),
                AppearsOn = _appears.Items.Select(row => View.Album(read, row.EntityUri)).ToArray(),
                AlbumsTotal = _albums.Total ?? artist.AlbumsTotal, SinglesTotal = _singles.Total ?? artist.SinglesTotal,
                CompilationsTotal = _compilations.Total ?? artist.CompilationsTotal,
            };
            return new(value, _popular.OrderRevision + _albums.OrderRevision + _singles.OrderRevision + _compilations.OrderRevision + _appears.OrderRevision, identity.Value is not null);
        }
        public override QueryRequirements Requirements(Artist value, QueryDemand demand)
            => Required(new[] { View.Key(_query.Uri, FacetKind.ArtistIdentity), View.Key(_query.Uri, FacetKind.ArtistOverview) }
                .Concat(_popular.Require(_popular.Total ?? int.MaxValue))
                .Concat(_albums.Require(_albums.Total ?? int.MaxValue))
                .Concat(_singles.Require(_singles.Total ?? int.MaxValue))
                .Concat(_compilations.Require(_compilations.Total ?? int.MaxValue))
                .Concat(_appears.Require(_appears.Total ?? int.MaxValue))
                .Concat(RowKeys(value.TopTracks, demand.Facets))
                .Concat(AlbumKeys(value.TopAlbums))
                .Concat(AlbumKeys(value.AppearsOn))
                .Concat(ArtistCardKeys(value)));

        IEnumerable<ResourceKey> ArtistCardKeys(Artist value)
        {
            if (value.LatestRelease is { } latest)
                foreach (var key in AlbumKeys([latest])) yield return key;
            var extras = value.Extras;
            if (extras?.Related is { } related)
                foreach (var artist in related) yield return View.Key(artist.Uri, FacetKind.ArtistIdentity);
            if (extras?.Playlists is { } playlists)
                foreach (var playlist in playlists) yield return View.Key(playlist.Uri, FacetKind.PlaylistHeader);
            if (extras?.MusicVideos is { } videos)
                foreach (var video in videos) yield return View.Key(video.TrackUri, FacetKind.TrackIdentity);
        }
    }

    static Album ReleaseAlbum(CatalogReadView view, QueryReadContext read, string uri, DiscographyKind kind)
    {
        var album = view.Album(read, uri);
        if (read.Read(view.Key(uri, FacetKind.AlbumIdentity)).Value is not null) return album;
        return album with { Kind = kind switch { DiscographyKind.Singles => AlbumKind.Single,
            DiscographyKind.Compilations => AlbumKind.Compilation, _ => AlbumKind.Album } };
    }

    sealed class ArtistReleasesDefinition : Definition<Wavee.Core.DiscographyPage>
    {
        public override QueryReadResult<Wavee.Core.DiscographyPage> Pending => new(new([], 0), 0, false);
        readonly (DiscographyKind Kind, RelationProjection Relation)[] _relations;
        public ArtistReleasesDefinition(ArtistReleasesQuery query, LibraryReplicaCoordinator replicas, Func<string, string> ownerProvider)
            : base(query.Scope, replicas, ownerProvider)
        {
            var kinds = query.Kind is { } kind ? new[] { kind } : Enum.GetValues<DiscographyKind>();
            _relations = kinds.Select(kind => (kind, new RelationProjection(
                args => View.Key(query.Uri, FacetKind.ArtistDiscography, args), kind.ToString()))).ToArray();
        }
        public override QueryReadResult<Wavee.Core.DiscographyPage> Read(QueryReadContext read)
        {
            foreach (var (_, relation) in _relations) relation.Read(read);
            var items = _relations.SelectMany(pair => pair.Relation.Items.Select(row => ReleaseAlbum(View, read, row.EntityUri, pair.Kind)))
                .DistinctBy(album => album.Uri).ToArray();
            return new(new(items, _relations.Sum(pair => pair.Relation.Total ?? pair.Relation.Items.Count)),
                _relations.Sum(pair => pair.Relation.OrderRevision), _relations.Any(pair => pair.Relation.Loaded));
        }
        public override QueryRequirements Requirements(Wavee.Core.DiscographyPage value, QueryDemand demand)
            => Required(_relations.SelectMany(pair => pair.Relation.Require(pair.Relation.Total ?? int.MaxValue))
                .Concat(AlbumKeys(value.Items)));
    }

    sealed class ShowDefinition : Definition<Show>
    {
        public override QueryReadResult<Show> Pending => new(new("", _query.Uri, "", "", null), 0, false);
        readonly ShowDetailQuery _query;
        readonly RelationProjection _episodes;
        public ShowDefinition(ShowDetailQuery query, LibraryReplicaCoordinator replicas, Func<string, string> ownerProvider) : base(query.Scope, replicas, ownerProvider)
        { _query = query; _episodes = new(args => View.Key(query.Uri, FacetKind.ShowEpisodes, args)); }
        public override QueryReadResult<Show> Read(QueryReadContext read)
        {
            var identity = read.Read(View.Key(_query.Uri, FacetKind.ShowIdentity));
            _episodes.Read(read);
            return new(View.Show(read, _query.Uri, _episodes.Loaded ? _episodes.Items : null, _episodes.Total), _episodes.OrderRevision, identity.Value is not null);
        }
        public override QueryRequirements Requirements(Show value, QueryDemand demand)
            => Required(_episodes.Require(_episodes.Total ?? (value.TotalEpisodes > 0 ? value.TotalEpisodes : int.MaxValue))
                .Prepend(View.Key(_query.Uri, FacetKind.ShowIdentity))
                .Concat((value.Episodes ?? []).Select(episode => View.Key(episode.Uri, FacetKind.EpisodeIdentity))));
    }

    sealed class LikedDefinition(LikedSongsQuery query, LibraryReplicaCoordinator replicas, Func<string, string> ownerProvider) : Definition<IReadOnlyList<Track>>(query.Scope, replicas, ownerProvider)
    {
        public override QueryReadResult<IReadOnlyList<Track>> Pending => new([], 0, false);
        System.Collections.Immutable.ImmutableArray<Wavee.Backend.SavedItem> _source;
        Wavee.Backend.SavedItem[] _sorted = [];
        ChunkedRows<Track>? _identities, _rows;
        long _orderRevision;
        public override QueryReadResult<IReadOnlyList<Track>> Read(QueryReadContext read)
        {
            read.DependOnReplica("liked");
            var saved = Replicas.ReadCollection("liked");
            bool membershipChanged = _source.IsDefault || _source != saved.Items;
            if (membershipChanged)
            {
                var sorted = saved.Items.OrderByDescending(s => s.AddedAtMs).ThenBy(s => s.Uri, StringComparer.Ordinal).ToArray();
                if (!_sorted.Select(s => s.Uri).SequenceEqual(sorted.Select(s => s.Uri))) _orderRevision++;
                membershipChanged = !_sorted.SequenceEqual(sorted);
                _sorted = sorted; _source = saved.Items;
            }
            var identities = ChunkedRows<Track>.Project(_identities, _sorted.Length, i => View.Track(read, _sorted[i].Uri));
            var rows = ChunkedRows<Track>.Project(_rows, _sorted.Length, i =>
            {
                DateTimeOffset? added = _sorted[i].AddedAtMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(_sorted[i].AddedAtMs) : null;
                return !membershipChanged && _rows is not null && ReferenceEquals(identities[i], _identities![i])
                    ? _rows[i] : identities[i] with { AddedAt = added };
            });
            _identities = identities; _rows = rows;
            return new(rows, _orderRevision, Replicas.IsCollectionKnown("liked"));
        }
        public override QueryRequirements Requirements(IReadOnlyList<Track> value, QueryDemand demand)
            => Required(RowKeys(value, demand.Facets), new ReplicaRequest("collection", "liked"));
    }

    sealed class SavedDefinition<T>(CatalogScope scope, LibraryReplicaCoordinator replicas, Func<string, string> ownerProvider, string set, FacetKind facet,
        Func<CatalogReadView, QueryReadContext, string, T> project, Func<T, string> uriOf) : Definition<IReadOnlyList<T>>(scope, replicas, ownerProvider)
    {
        public override QueryReadResult<IReadOnlyList<T>> Pending => new([], 0, false);
        public override QueryReadResult<IReadOnlyList<T>> Read(QueryReadContext read)
        {
            read.DependOnReplica(set);
            var baseline = Replicas.ReadConfirmedCollection(set);
            var items = Replicas.ReadCollection(set).Items.OrderByDescending(s => s.AddedAtMs).ThenBy(s => s.Uri, StringComparer.Ordinal)
                .Select(item => project(View, read, item.Uri)).ToArray();
            return new(items, baseline.Version, Replicas.IsCollectionKnown(set));
        }
        public override QueryRequirements Requirements(IReadOnlyList<T> value, QueryDemand demand)
            => Required(IdentityRequirements(value, demand), new ReplicaRequest("collection", set));

        IEnumerable<ResourceKey> IdentityRequirements(IReadOnlyList<T> value, QueryDemand demand)
        {
            foreach (var item in value)
            {
                var uri = uriOf(item);
                yield return View.Key(uri, facet);
                if (item is Album album)
                    foreach (var artist in album.Artists)
                        if (!string.IsNullOrEmpty(artist.Uri)) yield return View.Key(artist.Uri, FacetKind.ArtistIdentity);
                foreach (var extra in demand.Facets)
                    if (extra != facet && !(item is Album && extra == FacetKind.ArtistIdentity)) yield return View.Key(uri, extra);
            }
        }
    }

    sealed class DiscographyDefinition(ArtistDiscographyQuery query, LibraryReplicaCoordinator replicas, Func<string, string> ownerProvider) : Definition<Wavee.Core.DiscographyPage>(query.Scope, replicas, ownerProvider)
    {
        public override QueryReadResult<Wavee.Core.DiscographyPage> Pending => new(new([], 0), 0, false);
        ResourceKey Page => View.Key(query.Uri, FacetKind.ArtistDiscography, new(query.Offset, query.Limit, Filter: query.Kind.ToString()));
        public override QueryReadResult<Wavee.Core.DiscographyPage> Read(QueryReadContext read)
        {
            var page = read.Read<RelationPageValue>(Page);
            return new(new((page?.Items ?? []).Select(item => View.Album(read, item.EntityUri)).ToArray(), page?.Total ?? 0),
                read.Read(Page).Revision, page is not null);
        }
        public override QueryRequirements Requirements(Wavee.Core.DiscographyPage value, QueryDemand demand)
            => Required(value.Items.Select(a => View.Key(a.Uri, FacetKind.AlbumIdentity)).Prepend(Page));
    }

    sealed class HomeSectionDefinition(HomeSectionQuery query, LibraryReplicaCoordinator replicas, Func<string, string> ownerProvider) : Definition<HomeSectionPageResult?>(query.Scope, replicas, ownerProvider)
    {
        public override QueryReadResult<HomeSectionPageResult?> Pending => new(null, 0, false);
        readonly List<ResourceKey> _pages = [];
        readonly Dictionary<ResourceKey, (CatalogValue? Value, long Generation)> _blockedTails = new();
        CatalogDocumentValue? _firstDocument;
        CatalogDocumentSection? _publishedSection;
        IReadOnlyList<CatalogDocumentItem> _publishedItems = [];
        int _wanted = query.Limit, _candidateCount;
        int? _next, _publishedNext;
        bool _replacementPending;
        long _basisRevision, _orderRevision;
        int _raw, _unsupported, _duplicates;
        ResourceKey Page(int offset) => new(query.Scope, query.Uri, FacetKind.HomeSection, new(offset, query.Limit));
        public override QueryReadResult<HomeSectionPageResult?> Read(QueryReadContext read)
        {
            var firstKey = Page(query.Offset);
            var firstSnapshot = read.Read(firstKey);
            var firstDocument = firstSnapshot.Value as CatalogDocumentValue;
            bool replacing = _firstDocument is not null && !ReferenceEquals(_firstDocument, firstDocument);
            if (firstDocument is not null && !ReferenceEquals(_firstDocument, firstDocument))
            {
                _firstDocument = firstDocument; _basisRevision = firstSnapshot.Revision;
                _blockedTails.Clear();
                if (replacing)
                    foreach (var key in _pages.Where(key => key != firstKey))
                    { var old = read.Read(key); _blockedTails[key] = (old.Value, old.Generation); }
            }
            _pages.Clear();
            int offset = query.Offset;
            var candidate = new List<CatalogDocumentItem>();
            CatalogDocumentSection? first = null;
            int raw = 0, unsupported = 0, duplicates = 0;
            for (int pageIndex = 0; pageIndex < 2000; pageIndex++)
            {
                var key = Page(offset); _pages.Add(key);
                var snapshot = read.Read(key);
                var document = snapshot.Value as CatalogDocumentValue;
                if (document?.Sections.FirstOrDefault() is not { } section) { _next = offset; break; }
                if (pageIndex > 0)
                {
                    if (!_blockedTails.ContainsKey(key) && snapshot.Revision < _basisRevision)
                        _blockedTails[key] = (snapshot.Value, snapshot.Generation);
                    if (_blockedTails.TryGetValue(key, out var blocked))
                    {
                        // The wire has no shared snapshot token. A changed first page starts a local generation:
                        // force a new tail request and reject both its prior cache value and an older in-flight reply.
                        if (snapshot.Generation <= blocked.Generation || ReferenceEquals(snapshot.Value, blocked.Value))
                        { read.RequireRevalidation(key, _basisRevision); _next = offset; break; }
                        _blockedTails.Remove(key);
                    }
                }
                first ??= section;
                candidate.AddRange(section.Items);
                raw += section.RawItemCount ?? section.Items.Count;
                unsupported += section.UnsupportedCount; duplicates += section.DuplicateCount;
                _next = int.TryParse(document.NextCursor, out int next) && next > offset ? next : null;
                if (_next is null || candidate.Count >= _wanted || query.Offset != 0) break;
                offset = _next.Value;
            }
            _candidateCount = candidate.Count;
            _replacementPending = _publishedSection is not null && _next is not null
                && candidate.Count < Math.Min(_publishedItems.Count, _wanted);
            if (first is not null && !_replacementPending)
            {
                if (!_publishedItems.Select(item => (item.OccurrenceKey, item.EntityUri)).SequenceEqual(
                    candidate.Select(item => (item.OccurrenceKey, item.EntityUri)))) _orderRevision++;
                _publishedSection = first; _publishedItems = candidate.ToArray(); _publishedNext = _next;
                _raw = raw; _unsupported = unsupported; _duplicates = duplicates;
            }
            if (_publishedSection is not { } published) return new(null, 0, false);
            // Re-join the retained coherent document so a metadata-only commit still updates visible old rows
            // while replacement pages are pending. No list-merging state belongs to the page component.
            var cards = _publishedItems.Select(item => View.HomeCard(read, item)).ToArray();
            return new(new(new(published.Uri ?? query.Uri, published.Title, published.Subtitle, cards,
                published.TotalCount ?? cards.Length, _raw, _unsupported, _duplicates), _publishedNext), _orderRevision, true);
        }
        public override QueryRequirements Requirements(HomeSectionPageResult? value, QueryDemand demand)
        {
            _wanted = demand.Active ? int.MaxValue : query.Limit;
            IEnumerable<ResourceKey> keys = _pages.Count == 0 ? [Page(query.Offset)] : _pages;
            if (_next is { } next && (_replacementPending || _candidateCount < _wanted)) keys = keys.Append(Page(next));
            if (value is not null) keys = keys.Concat(value.Section.Cards
                .Select(card => View.Key(card.Uri, CatalogReadView.IdentityFacet(card.Uri))));
            return Required(keys);
        }
    }

    sealed class HomeDefinition(HomeQuery query, LibraryReplicaCoordinator replicas, Func<string, string> ownerProvider) : Definition<HomeFeed>(query.Scope, replicas, ownerProvider)
    {
        public override QueryReadResult<HomeFeed> Pending => new(HomeFeed.Empty with { Facet = query.Facet ?? "" }, 0, false);
        ResourceKey Document => new(query.Scope, CatalogSubjects.Home, FacetKind.Home, new(Filter: query.Facet ?? ""));
        long _order;
        IReadOnlyList<string> _occurrences = [];
        public override QueryReadResult<HomeFeed> Read(QueryReadContext read)
        {
            var document = read.Read<CatalogDocumentValue>(Document);
            if (document is null) return new(HomeFeed.Empty with { Facet = query.Facet ?? "" }, _order, false);
            var groups = (document.HomeGroups ?? []).Select(group => new HomeGroup(group.Kind, group.Title,
                group.Items.Select(item => View.HomeCard(read, item)).ToArray(), group.Subtitle, group.Uri, group.TotalCount)).ToArray();
            var sections = document.Sections.Select(section => new HomeSection(section.Uri, section.Title, section.Subtitle,
                section.Items.Select(item => View.HomeCard(read, item)).ToArray(), section.TotalCount ?? section.Items.Count,
                section.RawItemCount ?? section.Items.Count, section.UnsupportedCount, section.DuplicateCount)).ToArray();
            var order = (document.HomeGroups ?? []).SelectMany(g => g.Items.Select(i => "group/" + g.OccurrenceKey + "/" + i.OccurrenceKey + "/" + i.EntityUri))
                .Concat(document.Sections.SelectMany(s => s.Items.Select(i => "section/" + s.OccurrenceKey + "/" + i.OccurrenceKey + "/" + i.EntityUri))).ToArray();
            if (!_occurrences.SequenceEqual(order)) { _occurrences = order; _order++; }
            return new(new(document.Greeting ?? "", groups, document.HomeChips, sections, query.Facet ?? ""), _order, true);
        }
        public override QueryRequirements Requirements(HomeFeed value, QueryDemand demand)
        {
            var cards = value.Groups.SelectMany(g => g.Cards)
                .Concat((value.Sections ?? []).SelectMany(section => section.Cards));
            return Required(cards.Select(card => View.Key(card.Uri, CatalogReadView.IdentityFacet(card.Uri))).Prepend(Document));
        }
    }

    /// <summary>The sidebar's library join. It re-runs on every catalog publication and every replica change, so it
    /// is written to answer "nothing moved" without rebuilding anything: each set is projected through the
    /// instance-cached view and keeps its previous array when every member came back BY REFERENCE, the rootlist tree
    /// and the entry list are rebuilt only when their own inputs moved, the added-at map only when its fold moved,
    /// and when nothing moved at all the PREVIOUS snapshot instance is returned. Rebuilding it wholesale published a
    /// "changed" sidebar on every catalog publication — the snapshot's members are reference-compared, so a fresh
    /// instance was never equal to the last one — and the sidebar rebuilt itself entirely each time.</summary>
    sealed class LibraryDefinition(SidebarLibraryQuery query, LibraryReplicaCoordinator replicas, Func<string, string> ownerProvider) : Definition<LibraryQuerySnapshot>(query.Scope, replicas, ownerProvider)
    {
        public override QueryReadResult<LibraryQuerySnapshot> Pending => new(new([], [], new(0, 0, 0, 0), new Dictionary<string, long>()), 0, false);
        static readonly string[] Sets = ["liked", "albums", "artists", "shows", "playlists"];
        readonly List<string> _rootPlaylists = [];
        LibraryQuerySnapshot? _value;
        Album[] _albums = []; Artist[] _artists = []; Show[] _shows = []; Playlist[] _playlists = [];
        System.Collections.Immutable.ImmutableArray<RootlistEntry> _rootEntries;
        IReadOnlyList<PlaylistNode> _tree = [];
        IReadOnlyList<PlaylistSummary> _flat = [];
        IReadOnlyList<LibraryItem> _entries = [];
        IReadOnlyDictionary<string, long> _addedAt = new Dictionary<string, long>(StringComparer.Ordinal);
        LibraryStats _stats = new(0, 0, 0, 0);
        long _addedFold;

        public override QueryReadResult<LibraryQuerySnapshot> Read(QueryReadContext read)
        {
            read.DependOnReplica("rootlist");
            foreach (var set in Sets) read.DependOnReplica(set);
            // ReadCollection PROJECTS (baseline + native rows + pending intents) into a fresh array per call, so each
            // set is read exactly once here and that answer is carried through every use below.
            var sets = new[] { Replicas.ReadCollection("liked").Items, Replicas.ReadCollection("albums").Items,
                Replicas.ReadCollection("artists").Items, Replicas.ReadCollection("shows").Items,
                Replicas.ReadCollection("playlists").Items };
            var root = Replicas.ReadRootlist();
            long revision = root.Version;
            foreach (var set in Sets) revision += Replicas.ReadConfirmedCollection(set).Version;

            var (likedItems, albumItems, artistItems, showItems) = (sets[0], sets[1], sets[2], sets[3]);
            var albums = Join(albumItems.Length, i => View.Album(read, albumItems[i].Uri), _albums);
            var artists = Join(artistItems.Length, i => View.Artist(read, artistItems[i].Uri), _artists);
            var shows = Join(showItems.Length, i => View.Show(read, showItems[i].Uri), _shows);
            _rootPlaylists.Clear();
            foreach (var entry in root.Entries) if (entry.Kind == 0) _rootPlaylists.Add(entry.Uri);
            var playlists = Join(_rootPlaylists.Count, i => View.Playlist(read, _rootPlaylists[i]), _playlists);
            bool moved = _value is null || !ReferenceEquals(albums, _albums) || !ReferenceEquals(artists, _artists)
                || !ReferenceEquals(shows, _shows) || !ReferenceEquals(playlists, _playlists);

            // The tree also carries FOLDERS, which exist only in the rootlist marker stream — a folder rename or a
            // reorder moves no playlist instance — so the marker stream is compared too (one walk of small structs).
            if (moved || _rootEntries.IsDefault || !_rootEntries.SequenceEqual(root.Entries))
            {
                var byUri = new Dictionary<string, Playlist>(playlists.Length, StringComparer.Ordinal);
                foreach (var playlist in playlists) byUri.TryAdd(playlist.Uri, playlist);
                var tree = RootlistTreeBuilder.Build(root.Entries, uri =>
                {
                    var p = byUri.TryGetValue(uri, out var found) ? found : View.Playlist(read, uri);
                    return new(p.Uri, p.Name, p.OwnerName, p.TrackCount, p.Cover, CanEdit: p.Capabilities.CanEditItems, IsOwner: p.Capabilities.IsOwner);
                });
                _rootEntries = root.Entries; _tree = tree; _flat = SidebarTree.Flatten(tree);
                moved = true;
            }
            if (moved)
            {
                var entries = new LibraryItem[albums.Length + artists.Length + playlists.Length];
                int at = 0;
                foreach (var album in albums)
                    entries[at++] = new(album.Uri, album.Name, string.Join(", ", album.Artists.Select(r => r.Name)), album.Cover, LibraryItemKind.Album);
                foreach (var artist in artists) entries[at++] = new(artist.Uri, artist.Name, null, artist.Image, LibraryItemKind.Artist);
                foreach (var playlist in playlists)
                    entries[at++] = new(playlist.Uri, playlist.Name, playlist.OwnerName, playlist.Cover, LibraryItemKind.Playlist);
                _entries = CatalogReadView.Reuse(_entries, entries)!;
            }
            var stats = new LibraryStats(albums.Length, artists.Length, likedItems.Length, shows.Length);
            if (_stats == stats) stats = _stats; else { _stats = stats; moved = true; }
            long fold = 0;
            foreach (var items in sets) fold = Fold(fold, items);
            if (fold != _addedFold || _value is null)
            {
                var addedAt = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var items in sets)
                    foreach (var item in items)
                        addedAt[item.Uri] = addedAt.TryGetValue(item.Uri, out long existing)
                            ? Math.Max(existing, item.AddedAtMs) : item.AddedAtMs;
                _addedAt = addedAt; _addedFold = fold; moved = true;
            }
            _albums = albums; _artists = artists; _shows = shows; _playlists = playlists;
            // The replica VERSIONS are deliberately not part of the value: they ride the snapshot's OrderRevision,
            // which QueryService compares on its own, so a version bump that changed no fact still publishes — with
            // the value's identity intact, which is what keeps the sidebar from rebuilding for nothing.
            if (moved)
                _value = new LibraryQuerySnapshot(_entries, _tree, stats, _addedAt)
                {
                    Albums = albums, Artists = artists, Shows = shows, Playlists = _flat,
                    ContentHash = ContentFold(),
                };
            return new(_value!, revision, true);
        }

        /// <summary>Membership projection with array identity: the previous array is kept when every projected member
        /// came back by reference (which the view's instance cache guarantees while its facts hold), and a change
        /// copies once from the row that moved.</summary>
        static T[] Join<T>(int count, Func<int, T> project, T[] previous) where T : class
        {
            T[]? next = previous.Length == count ? null : new T[count];
            for (int i = 0; i < count; i++)
            {
                var value = project(i);
                if (next is not null) next[i] = value;
                else if (!ReferenceEquals(previous[i], value))
                { next = new T[count]; Array.Copy(previous, next, i); next[i] = value; }
            }
            return next ?? previous;
        }

        static long Fold(long fold, int value) => unchecked(fold * 1099511628211L + value);
        static long Fold(long fold, System.Collections.Immutable.ImmutableArray<SavedItem> items)
        {
            fold = Fold(fold, items.Length);
            foreach (var item in items)
                fold = Fold(Fold(Fold(fold, item.Uri.GetHashCode(StringComparison.Ordinal)),
                    (int)item.AddedAtMs), (int)(item.AddedAtMs >> 32));
            return fold;
        }

        /// <summary>The value's content identity — every row the sidebar can render, folded through the records'
        /// own structural hashes (uri, title, subtitle, cover, kind), plus the added-at fold. Computed only when
        /// something actually moved; it is what lets two snapshots that carry the same library compare equal.</summary>
        long ContentFold()
        {
            long fold = Fold(Fold(Fold(_addedFold, _entries.Count), _flat.Count), _shows.Length);
            foreach (var entry in _entries) fold = Fold(fold, entry.GetHashCode());
            foreach (var summary in _flat) fold = Fold(fold, summary.GetHashCode());
            foreach (var show in _shows) fold = Fold(fold, HashCode.Combine(show.Uri, show.Name, show.Cover));
            return fold;
        }
        readonly List<ReplicaRequest> _replicaRequests = new();

        public override QueryRequirements Requirements(LibraryQuerySnapshot value, QueryDemand demand)
        {
            _replicaRequests.Clear();
            foreach (var set in Sets) _replicaRequests.Add(new("collection", set));
            _replicaRequests.Add(new("rootlist", "rootlist"));
            // A playlist whose header carries no picture wears a MOSAIC of its first members' covers
            // (CatalogReadView.MosaicCover) — which needs the playlist's membership replica. Spotify's own client
            // syncs every rootlist playlist for the same reason; without this the sidebar painted an empty square
            // until the user happened to open the page (whose own definition asks for the replica).
            foreach (var playlist in _playlists)
                if (NeedsMosaic(playlist)) _replicaRequests.Add(new("playlist", playlist.Uri));
            return Required(IdentityRequirements(value, demand), _replicaRequests.ToArray());
        }

        /// <summary>The header landed (an owner or a name is only ever known from it) and it named no cover.</summary>
        static bool NeedsMosaic(Playlist playlist)
            => playlist.Cover is null && (playlist.Owner is not null || playlist.Name.Length > 0);

        /// <summary>Every member's OWN identity facet (its kind decides it) plus the demand's display facets on every
        /// member. Identity is never a display facet — <see cref="QueryDemand"/> refuses one at construction.
        /// A playlist's model also includes its OWNER'S display name (the sidebar's "Playlist · Luhkas" line — a raw
        /// user id is not a name) and, for a playlist without a picture, the covers of the members its mosaic
        /// samples; both are demanded here so the whole sidebar model lands without anyone opening a page.</summary>
        IEnumerable<ResourceKey> IdentityRequirements(LibraryQuerySnapshot value, QueryDemand demand)
        {
            foreach (var playlist in _playlists)
            {
                if (playlist.Owner is { Id.Length: > 0 } owner && UserProfileIds.Normalize(owner.Id) is { } ownerUri)
                    yield return View.Key(ownerUri, FacetKind.UserIdentity);
                if (!NeedsMosaic(playlist)) continue;
                var members = Replicas.ReadPlaylist(playlist.Uri).Members;
                int sampled = Math.Min(members.Length, CatalogReadView.MosaicSampleMembers);
                for (int i = 0; i < sampled; i++)
                    yield return View.Key(members[i].ItemUri, CatalogReadView.IdentityFacet(members[i].ItemUri));
            }
            foreach (var album in value.Albums)
            {
                yield return View.Key(album.Uri, FacetKind.AlbumIdentity);
                foreach (var artist in album.Artists)
                    if (!string.IsNullOrEmpty(artist.Uri)) yield return View.Key(artist.Uri, FacetKind.ArtistIdentity);
                foreach (var extra in demand.Facets) yield return View.Key(album.Uri, extra);
            }
            foreach (var artist in value.Artists)
            {
                yield return View.Key(artist.Uri, FacetKind.ArtistIdentity);
                foreach (var extra in demand.Facets) yield return View.Key(artist.Uri, extra);
            }
            foreach (var playlist in value.Playlists)
            {
                yield return View.Key(playlist.Uri, FacetKind.PlaylistHeader);
                foreach (var extra in demand.Facets) yield return View.Key(playlist.Uri, extra);
            }
            foreach (var show in value.Shows)
            {
                yield return View.Key(show.Uri, FacetKind.ShowIdentity);
                foreach (var extra in demand.Facets) yield return View.Key(show.Uri, extra);
            }
            foreach (var entry in value.Entries)
            {
                yield return View.Key(entry.Uri, CatalogReadView.IdentityFacet(entry.Uri));
                foreach (var extra in demand.Facets) yield return View.Key(entry.Uri, extra);
            }
        }
    }

    sealed class SuggestionsDefinition(SearchSuggestionsQuery query, LibraryReplicaCoordinator replicas, Func<string, string> ownerProvider)
        : Definition<SearchSuggestions>(query.Scope, replicas, ownerProvider)
    {
        public override QueryReadResult<SearchSuggestions> Pending => new(SearchSuggestions.Empty, 0, false);
        ResourceKey Document => new(query.Scope, CatalogSubjects.Search(query.Text), FacetKind.SearchSuggestions);
        public override QueryReadResult<SearchSuggestions> Read(QueryReadContext read)
        {
            var document = read.Read<CatalogDocumentValue>(Document);
            if (document is null) return new(SearchSuggestions.Empty, 0, false);
            var items = document.Sections.SelectMany(section => section.Items).Select(item =>
            {
                var card = View.HomeCard(read, item);
                return new SearchSuggestionItem(item.SuggestionKind ?? SearchSuggestionKind.Track, item.EntityUri,
                    item.PresentationTitle ?? card.Title, card.Subtitle, item.PresentationImage ?? card.Image,
                    EntityUri.KindOf(item.EntityUri) == EntityKind.Track && View.Track(read, item.EntityUri).IsExplicit);
            }).ToArray();
            return new(new(document.SuggestedQueries ?? [], items), read.Read(Document).Revision, true);
        }
        public override QueryRequirements Requirements(SearchSuggestions value, QueryDemand demand)
            => Required(value.Items.Where(item => item.Kind != SearchSuggestionKind.Genre)
                .Select(item => View.Key(item.Uri, CatalogReadView.IdentityFacet(item.Uri))).Prepend(Document));
    }

    sealed class SearchDefinition(CatalogSearchQuery query, LibraryReplicaCoordinator replicas, Func<string, string> ownerProvider) : Definition<SearchResults>(query.Scope, replicas, ownerProvider)
    {
        public override QueryReadResult<SearchResults> Pending => new(SearchResults.Empty, 0, false);
        ResourceKey Document => new(query.Scope, CatalogSubjects.Search(query.Text), FacetKind.Search,
            new(query.Offset, query.Limit, Filter: query.Facet.ToString()));
        public override QueryReadResult<SearchResults> Read(QueryReadContext read)
        {
            var doc = read.Read<CatalogDocumentValue>(Document);
            if (doc is null) return new(SearchResults.Empty, 0, false);
            IEnumerable<CatalogDocumentItem> Items(SearchFacet facet) => doc.Sections.Where(s => s.SearchFacet == facet).SelectMany(s => s.Items);
            int Total(SearchFacet facet) => doc.SearchTotals?.GetValueOrDefault(facet) ?? -1;
            SearchTopHit Hit(CatalogDocumentItem item)
            {
                var card = View.HomeCard(read, item);
                var context = item.SearchMeta ?? new(SearchHitKind.Unknown, card.Subtitle ?? "", "", false, false, false, null);
                return new(context.Kind, item.EntityUri, card.Title, context.Subtitle, context.TypeLabel, card.Image,
                    context.RoundImage, context.Followable, context.MatchedLyrics, context.AccessLabel, context.Detail, context.Meta, context.MatchedTitle);
            }
            return new(new(Items(SearchFacet.Tracks).Select(i => View.Track(read, i.EntityUri)).ToArray(),
                Items(SearchFacet.Albums).Select(i => View.Album(read, i.EntityUri)).ToArray(),
                Items(SearchFacet.Artists).Select(i => View.Artist(read, i.EntityUri)).ToArray(),
                Items(SearchFacet.Playlists).Select(i => View.Playlist(read, i.EntityUri)).ToArray(),
                Items(SearchFacet.All).Select(Hit).ToArray(), Total(SearchFacet.Tracks), Total(SearchFacet.Albums), Total(SearchFacet.Artists), Total(SearchFacet.Playlists),
                Items(SearchFacet.Podcasts).Select(i => View.Show(read, i.EntityUri)).ToArray(), Total(SearchFacet.Podcasts),
                Items(SearchFacet.Episodes).Select(i => View.Episode(read, i.EntityUri)).ToArray(), Total(SearchFacet.Episodes),
                Items(SearchFacet.Audiobooks).Select(Hit).ToArray(), Total(SearchFacet.Audiobooks),
                Items(SearchFacet.Profiles).Select(Hit).ToArray(), Total(SearchFacet.Profiles), doc.SearchChips, doc.SearchGenres, Total(SearchFacet.Genres),
                Items(SearchFacet.Authors).Select(Hit).ToArray(), Total(SearchFacet.Authors)), read.Read(Document).Revision, true);
        }
        public override QueryRequirements Requirements(SearchResults value, QueryDemand demand)
            => Required(CardKeys(value, demand).Prepend(Document));

        IEnumerable<ResourceKey> CardKeys(SearchResults value, QueryDemand demand)
        {
            foreach (var key in RowKeys(value.Tracks, demand.Facets)) yield return key;
            foreach (var key in AlbumKeys(value.Albums)) yield return key;
            foreach (var artist in value.Artists)
                yield return View.Key(artist.Uri, FacetKind.ArtistIdentity);
            foreach (var playlist in value.Playlists)
            {
                yield return View.Key(playlist.Uri, FacetKind.PlaylistHeader);
                if (UserProfileIds.Normalize(playlist.Owner?.Id) is { } owner) yield return View.Key(owner, FacetKind.UserIdentity);
            }
            foreach (var show in value.Shows ?? [])
                yield return View.Key(show.Uri, FacetKind.ShowIdentity);
            foreach (var episode in value.Episodes ?? [])
            {
                yield return View.Key(episode.Uri, FacetKind.EpisodeIdentity);
                if (episode.ShowUri is { Length: > 0 } show) yield return View.Key(show, FacetKind.ShowIdentity);
            }
            foreach (var hits in new[] { value.TopHits, value.Audiobooks, value.Profiles, value.Authors })
                foreach (var hit in hits ?? [])
                {
                    var uri = hit.Uri;
                    if (EntityUri.KindOf(uri) is EntityKind.Track or EntityKind.Episode)
                    {
                        var track = value.Tracks.FirstOrDefault(track => track.Uri == uri);
                        yield return View.Key(uri, CatalogReadView.IdentityFacet(uri));
                        if (track is not null)
                            foreach (var key in RowKeys([track], demand.Facets)) yield return key;
                    }
                    else if (EntityUri.KindOf(uri) is EntityKind.Album or EntityKind.Artist or EntityKind.Playlist or EntityKind.Show or EntityKind.User)
                        yield return View.Key(uri, CatalogReadView.IdentityFacet(uri));
                }
        }
    }
}
