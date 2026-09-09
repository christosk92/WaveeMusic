using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.InteropServices;
using Wavee.Backend.Playlists;
using Wavee.Backend.Sync;
using Wavee.Core;
using Wavee.Core.Catalog;

namespace Wavee.Backend.Queries;

/// <summary>Pure joins over normalized facts. This class has no source, transport or persistence dependency.
/// <para><b>Incremental and identity-stable.</b> Every entity join is cached with the exact catalog keys it was built
/// from. A re-read that finds all of those keys still carrying the same value instance returns the SAME record
/// instance — no re-materialization, no structural compare. That identity is what lets <see cref="ChunkedRows{T}"/>
/// keep its vector, a playlist keep its <c>Tracks</c>, and QueryService's <c>EqualityComparer&lt;T&gt;.Default</c>
/// short-circuit on reference instead of deep-comparing 1.5k rows per catalog publication.</para>
/// <para><b>Dependency registration stays complete.</b> The cached-hit path re-reads every cached key (that read IS
/// the registration) with <c>allowColdRead: false</c>, so nothing is fetched by observing. The path stops comparing
/// at the first changed key and rebuilds; the rebuild registers exactly the keys the NEW value depends on, which is
/// the correct dependency set — a key only the stale value read is no longer a dependency.</para></summary>
public sealed class CatalogReadView
{
    readonly CatalogScope _scope;
    readonly LibraryReplicaCoordinator _replicas;
    readonly Func<string, string> _ownerProvider;
    // One resolved scope per subject. Key() used to run the owner provider (a source-registry walk plus an
    // EntityUri.Parse) and allocate a fresh CatalogScope record for EVERY facet of EVERY member on EVERY read —
    // ~12k delegate calls and allocations per recompute of a 1.5k-row playlist before a single fact was read.
    // The source registry is immutable for the runtime's lifetime, so the answer is stable and the scope instance
    // is shared; ResourceKey stays a value type built from it without allocating.
    readonly Dictionary<string, CatalogScope> _scopes = new(StringComparer.Ordinal);
    /// <summary>A joined value plus the catalog keys and value instances it was derived from. Parallel arrays, not a
    /// dictionary: the hit path is a linear walk of ~6-8 keys and never needs a lookup.</summary>
    sealed class Join<T>(T value, ResourceKey[] keys, CatalogValue?[] values) where T : class
    {
        public T Value = value;
        public ResourceKey[] Keys = keys;
        public CatalogValue?[] Values = values;
    }
    readonly Dictionary<string, Join<Track>> _tracks = new(StringComparer.Ordinal);
    readonly Dictionary<string, Join<Episode>> _episodes = new(StringComparer.Ordinal);
    readonly Dictionary<string, Join<Album>> _albumCards = new(StringComparer.Ordinal);
    readonly Dictionary<string, Join<Artist>> _artistCards = new(StringComparer.Ordinal);
    readonly Dictionary<string, Join<Show>> _showCards = new(StringComparer.Ordinal);
    readonly Dictionary<string, PlaylistJoin> _playlists = new(StringComparer.Ordinal);
    readonly Dictionary<string, PlaylistRows> _playlistRows = new(StringComparer.Ordinal);
    readonly Dictionary<string, ChunkedRows<Track>> _albumTracks = new(StringComparer.Ordinal);
    // A DETAIL join (an album with its track rows, a show with its episode rows) cannot go through Cached: part of
    // its input is the definition's relation page, which no catalog key of this entity covers. It earns the same
    // identity the other way round — the freshly built record's freshly built lists are first reduced to the
    // previous instances (Reuse), then the record is compared with the previous one and the previous one wins.
    readonly Dictionary<string, Album> _albumDetails = new(StringComparer.Ordinal);
    readonly Dictionary<string, Show> _showDetails = new(StringComparer.Ordinal);
    sealed record PlaylistRows(ImmutableArray<PlaylistMember> Members,
        ChunkedRows<Track> Identities, ChunkedRows<Track> Rows);
    /// <summary>A playlist also joins REPLICA state (membership, pending header intents), which no catalog key covers,
    /// so its cache carries those inputs too and the projected record is compared before it replaces the cached one.</summary>
    sealed class PlaylistJoin
    {
        public bool IncludeRows, ApplyPending;
        public PlaylistHeaderValue? Header;
        public ImmutableArray<PlaylistMember> Members;
        public string[] CollaboratorIds = [];
        public Owner[] Collaborators = [];
        public Playlist Value = default!;
    }
    public CatalogReadView(CatalogScope scope, LibraryReplicaCoordinator replicas, Func<string, string> ownerProvider)
    { _scope = scope; _replicas = replicas; _ownerProvider = ownerProvider; }

    /// <summary>Reference-stable list reuse. A record compares an <c>IReadOnlyList</c> member by REFERENCE, so a
    /// freshly built list of the very same rows would still deny its owner structural equality and republish a whole
    /// page. A list whose members all equal the previous list's IS the previous list: hand that one back. Entity rows
    /// settle on their first (reference) test because every join here is instance-cached; <c>ArtistRef</c>-shaped rows
    /// cost three string compares. Returns <paramref name="next"/> unchanged when anything moved.</summary>
    public static IReadOnlyList<T>? Reuse<T>(IReadOnlyList<T>? previous, IReadOnlyList<T>? next)
    {
        if (previous is null || next is null || ReferenceEquals(previous, next) || previous.Count != next.Count) return next;
        for (int i = 0; i < next.Count; i++)
            if (!EqualityComparer<T>.Default.Equals(previous[i], next[i])) return next;
        return previous;
    }

    public ResourceKey Key(string uri, FacetKind facet, ResourceArguments arguments = default)
    {
        if (!_scopes.TryGetValue(uri, out var scope))
        {
            var provider = _ownerProvider(uri);
            scope = provider.Length == 0 || provider == _scope.Provider ? _scope : _scope with { Provider = provider };
            _scopes[uri] = scope;
        }
        return new(scope, uri, facet, arguments);
    }

    public static FacetKind IdentityFacet(string uri) => EntityUri.KindOf(uri) switch
    {
        Wavee.Core.EntityKind.Episode => FacetKind.EpisodeIdentity,
        Wavee.Core.EntityKind.Album => FacetKind.AlbumIdentity,
        Wavee.Core.EntityKind.Artist => FacetKind.ArtistIdentity,
        Wavee.Core.EntityKind.Playlist => FacetKind.PlaylistHeader,
        Wavee.Core.EntityKind.Show => FacetKind.ShowIdentity,
        Wavee.Core.EntityKind.User => FacetKind.UserIdentity,
        _ => FacetKind.TrackIdentity,
    };

    T? Fact<T>(QueryReadContext read, string uri, FacetKind facet, bool cold = false) where T : CatalogValue
        => read.Read<T>(Key(uri, facet), cold);

    ArtistRef ArtistRef(QueryReadContext read, string uri)
        => new(EntityUri.IdOf(uri), uri, Fact<ArtistIdentityValue>(read, uri, FacetKind.ArtistIdentity)?.Name ?? "");

    // Static so a call site does not allocate a closure per joined entity.
    static readonly Func<CatalogReadView, QueryReadContext, string, Track> BuildTrack = (view, read, uri) => view.TrackCore(read, uri);
    static readonly Func<CatalogReadView, QueryReadContext, string, Episode> BuildEpisode = (view, read, uri) => view.EpisodeCore(read, uri);
    static readonly Func<CatalogReadView, QueryReadContext, string, Album> BuildAlbum = (view, read, uri) => view.AlbumCore(read, uri, null);
    static readonly Func<CatalogReadView, QueryReadContext, string, Artist> BuildArtist = (view, read, uri) => view.ArtistCore(read, uri, false);
    static readonly Func<CatalogReadView, QueryReadContext, string, Show> BuildShow = (view, read, uri) => view.ShowCore(read, uri, null, null);

    /// <summary>The incremental join. See the class remarks for why the hit path still reads every cached key and why
    /// stopping the comparison at the first change leaves the dependency set correct.</summary>
    T Cached<T>(Dictionary<string, Join<T>> cache, QueryReadContext read, string uri,
        Func<CatalogReadView, QueryReadContext, string, T> build) where T : class
    {
        if (cache.TryGetValue(uri, out var previous))
        {
            var keys = previous.Keys;
            var values = previous.Values;
            bool unchanged = true;
            for (int i = 0; i < keys.Length; i++)
                if (!ReferenceEquals(read.Read(keys[i], false).Value, values[i])) { unchanged = false; break; }
            if (unchanged) return previous.Value;
        }
        var isolated = read.CreateDependencyScope();
        var value = build(this, isolated, uri);
        var nextKeys = new ResourceKey[isolated.Resources.Count];
        var nextValues = new CatalogValue?[nextKeys.Length];
        int index = 0;
        foreach (var pair in isolated.Resources)
        {
            nextKeys[index] = pair.Key;
            nextValues[index] = pair.Value.Value;
            index++;
            read.Read(pair.Key, false);
        }
        if (previous is null) cache[uri] = new(value, nextKeys, nextValues);
        else { previous.Value = value; previous.Keys = nextKeys; previous.Values = nextValues; }
        return value;
    }

    public Track Track(QueryReadContext read, string uri) => Cached(_tracks, read, uri, BuildTrack);

    Track TrackCore(QueryReadContext read, string uri)
    {
        if (EntityUri.KindOf(uri) == Wavee.Core.EntityKind.Episode)
        {
            var episode = Episode(read, uri);
            return new(episode.Id, uri, episode.Title, [], new(EntityUri.IdOf(episode.ShowUri ?? ""), episode.ShowUri ?? "", episode.ShowName),
                episode.DurationMs, false, episode.Image, Source: "podcast");
        }
        var identity = Fact<TrackIdentityValue>(read, uri, FacetKind.TrackIdentity);
        var album = string.IsNullOrEmpty(identity?.AlbumUri) ? null : Fact<AlbumIdentityValue>(read, identity.AlbumUri, FacetKind.AlbumIdentity);
        var audio = Fact<AudioAttributesValue>(read, uri, FacetKind.AudioAttributes);
        var availability = Fact<AvailabilityValue>(read, uri, FacetKind.Availability);
        var descriptors = Fact<DescriptorsValue>(read, uri, FacetKind.Descriptors);
        var count = Fact<PlayCountValue>(read, uri, FacetKind.PlayCount);
        var artistUris = identity?.ArtistUris;
        var unlinked = identity?.UnlinkedArtistNames;
        int artistCount = (artistUris?.Count ?? 0) + (unlinked?.Count ?? 0);
        ArtistRef[] artists;
        if (artistCount == 0) artists = [];
        else
        {
            artists = new ArtistRef[artistCount];
            int i = 0;
            if (artistUris is not null) foreach (var u in artistUris) artists[i++] = ArtistRef(read, u);
            if (unlinked is not null) foreach (var name in unlinked) artists[i++] = new("", "", name);
        }
        return new(EntityUri.IdOf(uri), uri, identity?.Title ?? "", artists,
            new(EntityUri.IdOf(identity?.AlbumUri ?? ""), identity?.AlbumUri ?? "",
                album?.Name ?? (string.IsNullOrEmpty(identity?.AlbumUri) ? identity?.UnlinkedAlbumName : null) ?? ""),
            identity?.DurationMs ?? 0, identity?.IsExplicit ?? false, identity?.Image ?? album?.Cover,
            PlayCount: count?.Count ?? 0, Origin: identity?.Origin ?? TrackOrigin.Streamed,
            Availability: availability?.Verdict, AvailableAt: availability?.AvailableAt,
            Source: identity?.Source ?? EntityUri.Parse(uri).Provider, Isrc: identity?.Isrc,
            TempoBpm: audio?.TempoBpm, MusicalKey: audio?.MusicalKey, CamelotCode: audio?.CamelotCode,
            CamelotColor: audio?.CamelotColor, Tags: descriptors?.Tags, CanonicalUri: identity?.CanonicalUri, Year: identity?.Year ?? 0);
    }

    public Episode Episode(QueryReadContext read, string uri) => Cached(_episodes, read, uri, BuildEpisode);

    Episode EpisodeCore(QueryReadContext read, string uri)
    {
        var value = Fact<EpisodeIdentityValue>(read, uri, FacetKind.EpisodeIdentity);
        var show = value?.ShowUri is { } showUri ? Fact<ShowIdentityValue>(read, showUri, FacetKind.ShowIdentity) : null;
        return new(EntityUri.IdOf(uri), uri, value?.Title ?? "", show?.Name ?? value?.ShowName ?? "", value?.Image,
            value?.DurationMs ?? 0, value?.PublishedAt ?? default,
            Fact<EpisodeDetailValue>(read, uri, FacetKind.EpisodeDetail)?.Description, ShowUri: value?.ShowUri);
    }

    public Owner? Owner(QueryReadContext read, string? uri)
    {
        if (UserProfileIds.Normalize(uri) is not { } canonical) return null;
        var owner = Fact<UserIdentityValue>(read, canonical, FacetKind.UserIdentity);
        return new(UserProfileIds.BareId(canonical), owner?.Name ?? "", owner?.Avatar);
    }

    public Playlist Playlist(QueryReadContext read, string uri, bool includeRows = false, bool applyPendingHeader = true)
    {
        var header = Fact<PlaylistHeaderValue>(read, uri, FacetKind.PlaylistHeader, includeRows);
        read.DependOnReplica(uri);
        var replica = _replicas.ReadPlaylist(uri);
        var owner = Owner(read, header?.OwnerUri);
        _playlists.TryGetValue(uri, out var cached);
        if (cached is not null && (cached.IncludeRows != includeRows || cached.ApplyPending != applyPendingHeader)) cached = null;
        // With rows included this walks the WHOLE membership for its distinct added-by ids. Both of its inputs — the
        // header value and the member vector — are reference-stable while nothing changed, so the walk is paid once
        // per real header/membership change rather than once per catalog publication.
        var collaboratorIds = cached is not null && ReferenceEquals(cached.Header, header)
            && (!includeRows || cached.Members == replica.Members)
            ? cached.CollaboratorIds
            : (header?.CollaboratorUris ?? []).Concat(includeRows
                ? replica.Members.Select(member => member.AddedBy).Where(id => id is not null).Select(id => id!) : [])
                .Prepend(header?.OwnerUri ?? "").Select(UserProfileIds.Normalize).Where(id => id is not null)
                .Select(id => id!).Distinct(StringComparer.Ordinal).ToArray();
        var collaborators = new Owner[collaboratorIds.Length];
        for (int i = 0; i < collaborators.Length; i++) collaborators[i] = Owner(read, collaboratorIds[i])!;
        // An array field is compared by REFERENCE inside the Playlist record's equality, so a fresh-but-identical
        // collaborator array alone would deny the record its identity and republish the page.
        if (cached is not null && cached.Collaborators.Length == collaborators.Length)
        {
            bool same = true;
            for (int i = 0; i < collaborators.Length && same; i++) same = cached.Collaborators[i] == collaborators[i];
            if (same) collaborators = cached.Collaborators;
        }
        bool loaded = replica.State is ReplicaBaselineState.Cached or ReplicaBaselineState.Verified or ReplicaBaselineState.AwaitingCreate;
        IReadOnlyList<Track>? rows = null;
        if (includeRows && loaded)
        {
            _playlistRows.TryGetValue(uri, out var previous);
            var identities = ChunkedRows<Track>.Project(previous?.Identities, replica.Members.Length,
                i => Track(read, replica.Members[i].ItemUri));
            var projectedRows = ChunkedRows<Track>.Project(previous?.Rows, replica.Members.Length, i =>
            {
                var member = replica.Members[i];
                if (previous is not null && i < previous.Members.Length && member == previous.Members[i]
                    && ReferenceEquals(identities[i], previous.Identities[i])) return previous.Rows[i];
                return identities[i] with { ContextUid = member.ItemId, AddedBy = member.AddedBy,
                    AddedAt = member.AddedAt > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(member.AddedAt) : null,
                    Chart = member.Chart };
            });
            _playlistRows[uri] = new(replica.Members, identities, projectedRows);
            rows = projectedRows;
        }
        var tuning = header?.Tuning;
        if (tuning is not null && !PlaylistRevisions.Equal(tuning.Revision, replica.Revision)) tuning = null;
        var cover = header?.Cover ?? (loaded ? MosaicCover(read, uri, replica.Members) : null);
        var projected = new Playlist(EntityUri.IdOf(uri), uri, header?.Name ?? "",
            header?.Description, owner?.Name is { Length: > 0 } name ? name : header?.OwnerName ?? "",
            cover, loaded ? replica.Members.Length : header?.TrackCount ?? 0, rows, owner,
            header?.Capabilities ?? default, header?.Format, header?.Source ?? EntityUri.Parse(uri).Provider,
            collaborators, header?.IsPublic ?? true,
            header?.BasePermissionRevision, tuning, header?.NextUpdateAt?.ToUnixTimeMilliseconds() ?? 0,
            header?.CreatedAt?.ToUnixTimeMilliseconds() ?? 0, header?.DeletedByOwner ?? false,
            header?.ChartNewEntries ?? 0, header?.ChartUpdatedAt?.ToUnixTimeMilliseconds() ?? 0, header?.ChartRankType)
            { MembershipLoaded = loaded };
        var result = applyPendingHeader ? _replicas.ApplyPendingHeader(uri, projected) : projected;
        // Every part above is identity-stable, so a structural match here means literally nothing changed — hand the
        // previous instance back so QueryService's value comparison short-circuits and no consumer re-maps.
        if (cached is not null && cached.Value == result) result = cached.Value;
        if (cached is null)
            _playlists[uri] = new() { IncludeRows = includeRows, ApplyPending = applyPendingHeader, Header = header,
                Members = replica.Members, CollaboratorIds = collaboratorIds, Collaborators = collaborators, Value = result };
        else
        {
            cached.Header = header; cached.Members = replica.Members;
            cached.CollaboratorIds = collaboratorIds; cached.Collaborators = collaborators; cached.Value = result;
        }
        return result;
    }

    // A playlist with no custom picture wears a 2x2 mosaic of its first four distinct album covers (a single tile
    // below four), the way Spotify's own client and the retired StoreLibrarySource did; without it a user-made
    // playlist paints an empty square in the sidebar and the hero. Sampled from the first members only (each sample
    // is a track join, registered as a dependency like any other read), and the Image instance is kept while the
    // sampled tiles are unchanged so the playlist's own identity reuse still holds.
    public const int MosaicSampleMembers = 24;
    readonly Dictionary<string, (ImmutableArray<PlaylistMember> Members, string[] Tiles, Image Image)> _mosaics = new(StringComparer.Ordinal);

    Image? MosaicCover(QueryReadContext read, string uri, ImmutableArray<PlaylistMember> members)
    {
        if (members.IsDefaultOrEmpty) return null;
        var tiles = new List<string>(4);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < members.Length && i < MosaicSampleMembers && tiles.Count < 4; i++)
        {
            var track = Track(read, members[i].ItemUri);
            if (track.Image?.Url is not { Length: > 0 } url) continue;
            if (!seen.Add(track.Album.Uri is { Length: > 0 } album ? album : url)) continue;   // one tile per album
            tiles.Add(url);
        }
        if (tiles.Count == 0) return null;
        if (_mosaics.TryGetValue(uri, out var cached) && cached.Tiles.AsSpan().SequenceEqual(CollectionsMarshal.AsSpan(tiles))) return cached.Image;
        var array = tiles.ToArray();
        var image = array.Length >= 4 ? new Image("", MosaicTiles: array) : new Image(array[0]);
        _mosaics[uri] = (members, array, image);
        return image;
    }

    public Album Album(QueryReadContext read, string uri, IReadOnlyList<CatalogRelationItem>? rows = null)
        => rows is null ? Cached(_albumCards, read, uri, BuildAlbum) : AlbumCore(read, uri, rows);

    Album AlbumCore(QueryReadContext read, string uri, IReadOnlyList<CatalogRelationItem>? rows)
    {
        var identity = Fact<AlbumIdentityValue>(read, uri, FacetKind.AlbumIdentity);
        var detail = Fact<AlbumDetailValue>(read, uri, FacetKind.AlbumDetail);
        var publishing = Fact<PublishingValue>(read, uri, FacetKind.Publishing);
        // The card path (rows null) is instance-cached by Cached<T>, so it must not consult — or poison — the
        // detail slot: the two carry different Tracks and a different TrackCount.
        var cached = rows is null ? null : _albumDetails.GetValueOrDefault(uri);
        ChunkedRows<Track>? tracks = null;
        if (rows is not null)
        {
            _albumTracks.TryGetValue(uri, out var previous);
            tracks = ChunkedRows<Track>.Project(previous, rows.Count, i => Track(read, rows[i].EntityUri));
            _albumTracks[uri] = tracks;
        }
        var artists = (identity?.ArtistUris ?? []).Select(u => ArtistRef(read, u)).ToArray();
        var artistsDetailed = identity?.ArtistUris?.Select(u => Artist(read, u)).ToArray();
        var album = new Album(EntityUri.IdOf(uri), uri, identity?.Name ?? "", identity?.Cover,
            Reuse(cached?.Artists, artists)!, identity?.Year ?? 0,
            identity?.TrackCount ?? rows?.Count ?? 0, tracks,
            identity?.Kind ?? AlbumKind.Album, Label: detail?.Label, Copyright: publishing?.Copyright,
            ReleaseDate: publishing?.ReleaseDate, ArtistsDetailed: Reuse(cached?.ArtistsDetailed, artistsDetailed),
            CourtesyLine: detail?.CourtesyLine, ReleaseDatePrecision: publishing?.Precision, DiscCount: detail?.DiscCount ?? 1,
            ShareUrl: detail?.ShareUrl, IsPreRelease: detail?.IsPreRelease ?? false, PreReleaseEnd: detail?.PreReleaseEnd);
        if (rows is null) return album;
        if (cached is not null && cached == album) return cached;
        return _albumDetails[uri] = album;
    }

    public Artist Artist(QueryReadContext read, string uri, bool detail = false)
        => detail ? ArtistCore(read, uri, true) : Cached(_artistCards, read, uri, BuildArtist);

    Artist ArtistCore(QueryReadContext read, string uri, bool detail)
    {
        var identity = Fact<ArtistIdentityValue>(read, uri, FacetKind.ArtistIdentity);
        var overview = detail ? Fact<ArtistOverviewValue>(read, uri, FacetKind.ArtistOverview, true) : null;
        var extras = overview?.Extras;
        return new(EntityUri.IdOf(uri), uri, identity?.Name ?? "", identity?.Image,
            MonthlyListeners: overview?.MonthlyListeners ?? 0, Followers: overview?.Followers ?? 0,
            Bio: overview?.Bio, Verified: overview?.Verified ?? false, WorldRank: overview?.WorldRank ?? 0,
            HeaderImage: overview?.HeaderImage, Pinned: overview?.Pinned,
            Extras: extras is null ? null : new ArtistExtras(extras.Concerts, extras.Merch,
                extras.PlaylistUris?.Select(u => { var p = Playlist(read, u); return new PlaylistRef(u, p.Name, p.Cover, p.OwnerName); }).ToArray(),
                extras.MusicVideoUris?.Select(u => { var t = Track(read, u); return new MusicVideo(u, t.Title, t.Image, t.DurationMs, t.IsExplicit); }).ToArray(),
                extras.TopCities, extras.ExternalLinks, extras.Gallery,
                extras.RelatedArtistUris?.Select(u => { var a = Artist(read, u); return new RelatedArtist(a.Id, u, a.Name, a.Image); }).ToArray(),
                extras.Tour, extras.WatchFeed, extras.PreRelease),
            AlbumsTotal: overview?.AlbumsTotal ?? 0, SinglesTotal: overview?.SinglesTotal ?? 0, CompilationsTotal: overview?.CompilationsTotal ?? 0,
            LatestRelease: overview?.LatestReleaseUri is { } latest ? Album(read, latest) : null,
            PopularReleases: overview?.PopularReleaseUris?.Select(u => Album(read, u)).ToArray());
    }

    public Show Show(QueryReadContext read, string uri, IReadOnlyList<CatalogRelationItem>? rows = null, int? total = null)
        => rows is null && total is null ? Cached(_showCards, read, uri, BuildShow) : ShowCore(read, uri, rows, total);

    Show ShowCore(QueryReadContext read, string uri, IReadOnlyList<CatalogRelationItem>? rows, int? total)
    {
        var value = Fact<ShowIdentityValue>(read, uri, FacetKind.ShowIdentity);
        var cached = _showDetails.GetValueOrDefault(uri);
        var episodes = rows?.Select(row => Episode(read, row.EntityUri)).ToArray();
        var show = new Show(EntityUri.IdOf(uri), uri, value?.Name ?? "", value?.Publisher ?? "", value?.Cover, value?.Description,
            Reuse(cached?.Episodes, episodes), total ?? value?.EpisodeCount ?? rows?.Count ?? 0, rows?.Count ?? 0);
        if (cached is not null && cached == show) return cached;
        return _showDetails[uri] = show;
    }

    public HomeCard HomeCard(QueryReadContext read, CatalogDocumentItem item)
    {
        var uri = item.EntityUri;
        var meta = item.HomeMeta;
        if (EntityUri.KindOf(uri) == Wavee.Core.EntityKind.User || item.SearchMeta?.Kind is SearchHitKind.Author or SearchHitKind.User)
        {
            var user = Fact<UserIdentityValue>(read, uri, FacetKind.UserIdentity);
            return new(uri, user?.Name ?? "", item.Eyebrow, user?.Avatar, HomeCardKind.Artist);
        }
        if (item.PresentationTitle is { } title)
            return new(uri, title, item.Eyebrow, item.PresentationImage, item.HomeKind ?? HomeCardKind.Track);
        switch (EntityUri.KindOf(uri))
        {
            case Wavee.Core.EntityKind.Playlist:
                var playlist = Playlist(read, uri);
                var header = Fact<PlaylistHeaderValue>(read, uri, FacetKind.PlaylistHeader);
                if (header is not null) meta = (meta ?? new()) with
                {
                    Format = playlist.Format, OwnerName = playlist.OwnerName, TrackCount = playlist.TrackCount,
                    ExpiresAtMs = playlist.DaylistExpiresAtMs, CreatedAtMs = playlist.DaylistCreatedAtMs,
                    NeedsHydration = string.IsNullOrWhiteSpace(header.Name) || header.Name == meta?.GenericTitle,
                };
                return new(uri, playlist.Name, playlist.Description ?? playlist.OwnerName, playlist.Cover,
                    item.HomeKind ?? HomeCardKind.Playlist, Eyebrow: item.Eyebrow, Meta: meta);
            case Wavee.Core.EntityKind.Album:
                var album = Album(read, uri);
                return new(uri, album.Name, string.Join(", ", album.Artists.Select(a => a.Name)), album.Cover,
                    item.HomeKind ?? HomeCardKind.Album, Eyebrow: item.Eyebrow, Meta: meta);
            case Wavee.Core.EntityKind.Artist:
                var artist = Artist(read, uri);
                return new(uri, artist.Name, null, artist.Image, item.HomeKind ?? HomeCardKind.Artist, Eyebrow: item.Eyebrow, Meta: meta);
            case Wavee.Core.EntityKind.Show:
                var show = Show(read, uri);
                return new(uri, show.Name, show.Publisher, show.Cover, item.HomeKind ?? HomeCardKind.Podcast, Eyebrow: item.Eyebrow, Meta: meta);
            case Wavee.Core.EntityKind.Episode:
                var episode = Episode(read, uri);
                return new(uri, episode.Title, episode.ShowName, episode.Image, item.HomeKind ?? HomeCardKind.Episode,
                    Eyebrow: item.Eyebrow, Meta: (meta ?? new()) with { DurationMs = episode.DurationMs });
            default:
                var track = Track(read, uri);
                return new(uri, track.Title, string.Join(", ", track.Artists.Select(a => a.Name)), track.Image,
                    item.HomeKind ?? HomeCardKind.Track, Eyebrow: item.Eyebrow, Meta: meta);
        }
    }
}
