using System;
using System.Collections.Generic;
using System.Linq;
using Wavee.Core;
using Wavee.Core.Catalog;

namespace Wavee.Backend.Catalog;

/// <summary>Inline mapper output contributes conservative seeds. Domain defaults never invent authoritative zeroes.</summary>
public static class CatalogDomainSeeds
{
    public static void Track(CatalogScope scope, Track track, List<CatalogSeed> seeds)
    {
        seeds.Add(new(new(scope, track.Uri, FacetKind.TrackIdentity), new TrackIdentityPatch(
            Title: Text(track.Title, track.Uri), ArtistUris: Spec<IReadOnlyList<string>?>(track.Artists.Count > 0,
                track.Artists.Select(artist => artist.Uri).Where(uri => uri.Length > 0).ToArray()),
            AlbumUri: Spec<string?>(track.Album.Uri.Length > 0, track.Album.Uri),
            DurationMs: Spec<long?>(track.DurationMs > 0, track.DurationMs), IsExplicit: Spec<bool?>(track.IsExplicit, true),
            Image: Spec(track.Image is not null, track.Image), Isrc: Spec(track.Isrc is not null, track.Isrc),
            Year: Spec<int?>(track.Year > 0, track.Year), CanonicalUri: Spec(track.CanonicalUri is not null, track.CanonicalUri),
            Source: Spec(track.Source is not null, track.Source), Origin: Spec(track.Origin != TrackOrigin.Streamed, track.Origin),
            UnlinkedArtistNames: Spec<IReadOnlyList<string>?>(track.Artists.Any(artist => artist.Uri.Length == 0 && artist.Name.Length > 0),
                track.Artists.Where(artist => artist.Uri.Length == 0 && artist.Name.Length > 0).Select(artist => artist.Name).ToArray()),
            UnlinkedAlbumName: Spec<string?>(track.Album.Uri.Length == 0 && track.Album.Name.Length > 0, track.Album.Name))));
        foreach (var artist in track.Artists) ArtistRef(scope, artist, seeds);
        if (track.Album.Uri.Length > 0)
            seeds.Add(new(new(scope, track.Album.Uri, FacetKind.AlbumIdentity), new AlbumIdentityPatch(
                Name: Text(track.Album.Name, track.Album.Uri), Cover: Spec(track.Image is not null, track.Image))));
        if (track.Availability is { } availability)
            seeds.Add(new(new(scope, track.Uri, FacetKind.Availability), new ReplaceFacetPatch(new AvailabilityValue(availability, track.AvailableAt))));
        if (track.PlayCount > 0) seeds.Add(new(new(scope, track.Uri, FacetKind.PlayCount), new ReplaceFacetPatch(new PlayCountValue(track.PlayCount))));
        if (track.Tags is not null) seeds.Add(new(new(scope, track.Uri, FacetKind.Descriptors), new ReplaceFacetPatch(new DescriptorsValue(track.Tags))));
        if (track.TempoBpm is not null || track.MusicalKey is not null)
            seeds.Add(new(new(scope, track.Uri, FacetKind.AudioAttributes), new ReplaceFacetPatch(new AudioAttributesValue(
                track.TempoBpm, track.MusicalKey, track.CamelotCode, track.CamelotColor))));
    }

    public static void Album(CatalogScope scope, Album album, List<CatalogSeed> seeds)
    {
        seeds.Add(new(new(scope, album.Uri, FacetKind.AlbumIdentity), new AlbumIdentityPatch(
            Name: Text(album.Name, album.Uri), Cover: Spec(album.Cover is not null, album.Cover),
            ArtistUris: Spec<IReadOnlyList<string>?>(album.Artists.Count > 0, album.Artists.Select(artist => artist.Uri).ToArray()),
            Year: Spec<int?>(album.Year > 0, album.Year), TrackCount: Spec<int?>(album.TrackCount > 0, album.TrackCount),
            Kind: FieldChange<AlbumKind>.Set(album.Kind))));
        foreach (var artist in album.Artists) ArtistRef(scope, artist, seeds);
        if (album.ArtistsDetailed is not null) foreach (var artist in album.ArtistsDetailed) Artist(scope, artist, seeds);
        if (album.Copyright is not null || album.ReleaseDate is not null)
            seeds.Add(new(new(scope, album.Uri, FacetKind.Publishing), new ReplaceFacetPatch(
                new PublishingValue(album.Copyright, album.ReleaseDate, album.ReleaseDatePrecision))));
    }

    public static void Artist(CatalogScope scope, Artist artist, List<CatalogSeed> seeds)
        => seeds.Add(new(new(scope, artist.Uri, FacetKind.ArtistIdentity),
            new ArtistIdentityPatch(Text(artist.Name, artist.Uri), Spec(artist.Image is not null, artist.Image))));

    public static void Playlist(CatalogScope scope, Playlist playlist, List<CatalogSeed> seeds)
    {
        seeds.Add(new(new(scope, playlist.Uri, FacetKind.PlaylistHeader), new PlaylistHeaderPatch(
            Name: Text(playlist.Name, playlist.Uri), Description: Spec(playlist.Description is not null, playlist.Description),
            Cover: Spec(playlist.Cover is not null, playlist.Cover), TrackCount: Spec<int?>(playlist.TrackCount > 0, playlist.TrackCount),
            OwnerUri: Spec<string?>(playlist.Owner is { Id.Length: > 0 }, playlist.Owner is { } owner ? UserUri(owner.Id) : null),
            OwnerName: Text(playlist.OwnerName, ""), Format: Spec(playlist.Format is not null, playlist.Format),
            Source: Spec(playlist.Source is not null, playlist.Source),
            NextUpdateAt: Spec<DateTimeOffset?>(playlist.DaylistExpiresAtMs > 0, Instant(playlist.DaylistExpiresAtMs)),
            CreatedAt: Spec<DateTimeOffset?>(playlist.DaylistCreatedAtMs > 0, Instant(playlist.DaylistCreatedAtMs)))));
        if (playlist.Owner is { } profile)
            seeds.Add(new(new(scope, UserUri(profile.Id), FacetKind.UserIdentity), new UserIdentityPatch(
                Text(profile.Name, profile.Id), Spec(profile.Avatar is not null, profile.Avatar))));
    }

    public static void Episode(CatalogScope scope, Episode episode, List<CatalogSeed> seeds)
    {
        seeds.Add(new(new(scope, episode.Uri, FacetKind.EpisodeIdentity), new EpisodeIdentityPatch(
            Text(episode.Title, episode.Uri), Spec(episode.ShowUri is not null, episode.ShowUri),
            Spec<long?>(episode.DurationMs > 0, episode.DurationMs), Spec(episode.Image is not null, episode.Image),
            Spec<DateTimeOffset?>(episode.PublishedAt > DateTimeOffset.UnixEpoch, episode.PublishedAt),
            ShowName: Text(episode.ShowName, ""))));
        if (episode.ShowUri is { Length: > 0 } show)
            seeds.Add(new(new(scope, show, FacetKind.ShowIdentity), new ShowIdentityPatch(Name: Text(episode.ShowName, show))));
    }

    public static void Show(CatalogScope scope, Show show, List<CatalogSeed> seeds)
        => seeds.Add(new(new(scope, show.Uri, FacetKind.ShowIdentity), new ShowIdentityPatch(Text(show.Name, show.Uri),
            Text(show.Publisher, ""), Spec(show.Cover is not null, show.Cover), Spec(show.Description is not null, show.Description),
            Spec<int?>(show.TotalEpisodes > 0, show.TotalEpisodes))));

    public static ArtistOverviewValue Overview(CatalogScope scope, Artist artist, List<CatalogSeed> seeds)
    {
        Artist(scope, artist, seeds);
        if (artist.LatestRelease is { } latest) Album(scope, latest, seeds);
        if (artist.PopularReleases is not null) foreach (var album in artist.PopularReleases) Album(scope, album, seeds);
        var extras = artist.Extras;
        if (extras?.Playlists is not null)
            foreach (var playlist in extras.Playlists)
                seeds.Add(new(new(scope, playlist.Uri, FacetKind.PlaylistHeader), new PlaylistHeaderPatch(
                    Name: Text(playlist.Name, playlist.Uri), Cover: Spec(playlist.Cover is not null, playlist.Cover),
                    OwnerName: Text(playlist.Subtitle, ""))));
        if (extras?.MusicVideos is not null)
            foreach (var video in extras.MusicVideos)
                seeds.Add(new(new(scope, video.TrackUri, FacetKind.TrackIdentity), new TrackIdentityPatch(
                    Title: Text(video.Title, video.TrackUri), Image: Spec(video.Thumbnail is not null, video.Thumbnail),
                    DurationMs: Spec<long?>(video.DurationMs > 0, video.DurationMs), IsExplicit: Spec<bool?>(true, video.IsExplicit))));
        if (extras?.Related is not null)
            foreach (var related in extras.Related)
                seeds.Add(new(new(scope, related.Uri, FacetKind.ArtistIdentity), new ArtistIdentityPatch(
                    Text(related.Name, related.Uri), Spec(related.Image is not null, related.Image))));
        return new(artist.MonthlyListeners, artist.Followers, artist.Verified, artist.WorldRank, artist.Bio,
            artist.HeaderImage, artist.LatestRelease?.Uri, artist.Pinned?.TargetUri)
        {
            Pinned = artist.Pinned,
            PopularReleaseUris = artist.PopularReleases?.Select(album => album.Uri).ToArray(),
            AlbumsTotal = artist.AlbumsTotal, SinglesTotal = artist.SinglesTotal, CompilationsTotal = artist.CompilationsTotal,
            Extras = extras is null ? null : new(extras.Concerts, extras.Merch, extras.Playlists?.Select(playlist => playlist.Uri).ToArray(),
                extras.MusicVideos?.Select(video => video.TrackUri).ToArray(), extras.TopCities, extras.ExternalLinks, extras.Gallery,
                extras.Related?.Select(related => related.Uri).ToArray(), extras.Tour, extras.WatchFeed, extras.PreRelease),
        };
    }

    public static CatalogDecodedFacet Home(CatalogScope scope, LiveHomeResult home)
    {
        var seeds = new List<CatalogSeed>();
        CatalogDocumentItem Card(HomeCard card, string occurrence)
        {
            SeedCard(scope, card, seeds);
            return new(occurrence, card.Uri) { HomeKind = card.Kind, Eyebrow = card.Eyebrow, HomeMeta = card.Meta };
        }
        var groups = home.Groups.Select((group, index) =>
        {
            string occurrence = "group:" + (group.Uri ?? group.Kind.ToString()) + ":" + index;
            return new CatalogHomeGroup(occurrence, group.Kind, group.Title,
                group.Cards.Select((card, cardIndex) => Card(card, occurrence + ":" + card.Uri + ":" + cardIndex)).ToArray(),
                group.Subtitle, group.Uri, group.TotalCount);
        }).ToArray();
        var sections = home.Sections?.Select((section, index) =>
        {
            string occurrence = "section:" + (section.Uri ?? index.ToString());
            return new CatalogDocumentSection(occurrence, section.Title,
                section.Cards.Select((card, cardIndex) => Card(card, occurrence + ":" + card.Uri + ":" + cardIndex)).ToArray())
            {
                Uri = section.Uri, Subtitle = section.Subtitle, TotalCount = section.TotalCount, RawItemCount = section.RawItemCount,
                UnsupportedCount = section.UnsupportedCount, DuplicateCount = section.DuplicateCount,
            };
        }).ToArray() ?? [];
        return new(new ReplaceFacetPatch(new CatalogDocumentValue(FacetKind.Home, sections)
            { HomeGroups = groups, HomeChips = home.Chips, Greeting = home.Greeting, HomeFacet = home.Facet }), seeds);
    }

    public static CatalogDecodedFacet Search(CatalogScope scope, SearchResults results, SearchFacet facet)
    {
        var seeds = new List<CatalogSeed>();
        var sections = new List<CatalogDocumentSection>();
        void Add(SearchFacet kind, IEnumerable<string> uris)
            => sections.Add(new(kind.ToString(), null, uris.Select((uri, index) => new CatalogDocumentItem(
                kind + ":" + uri + ":" + index, uri)).ToArray()) { SearchFacet = kind, TotalCount = results.TotalFor(kind) });
        foreach (var track in results.Tracks) Track(scope, track, seeds);
        foreach (var album in results.Albums) Album(scope, album, seeds);
        foreach (var artist in results.Artists) Artist(scope, artist, seeds);
        foreach (var playlist in results.Playlists) Playlist(scope, playlist, seeds);
        if (results.Shows is not null) foreach (var show in results.Shows) Show(scope, show, seeds);
        if (results.Episodes is not null) foreach (var episode in results.Episodes) Episode(scope, episode, seeds);
        Add(SearchFacet.Tracks, results.Tracks.Select(track => track.Uri));
        Add(SearchFacet.Albums, results.Albums.Select(album => album.Uri));
        Add(SearchFacet.Artists, results.Artists.Select(artist => artist.Uri));
        Add(SearchFacet.Playlists, results.Playlists.Select(playlist => playlist.Uri));
        Add(SearchFacet.Podcasts, results.Shows?.Select(show => show.Uri) ?? []);
        Add(SearchFacet.Episodes, results.Episodes?.Select(episode => episode.Uri) ?? []);
        void Hits(SearchFacet kind, IReadOnlyList<SearchTopHit>? hits)
        {
            if (hits is null) return;
            var items = hits.Select((hit, index) =>
            {
                SeedHit(scope, hit, seeds);
                return new CatalogDocumentItem(kind + ":" + hit.Uri + ":" + index, hit.Uri)
                { PresentationTitle = hit.Kind is SearchHitKind.Unknown or SearchHitKind.Genre ? hit.Name : null,
                    PresentationImage = hit.Kind is SearchHitKind.Unknown or SearchHitKind.Genre ? hit.Image : null,
                    SearchMeta = new(hit.Kind, hit.Subtitle, hit.TypeLabel, hit.RoundImage, hit.Followable,
                    hit.MatchedLyrics, hit.AccessLabel, hit.Detail, hit.Meta, hit.MatchedTitle) };
            }).ToArray();
            sections.Add(new(kind.ToString(), null, items) { SearchFacet = kind, TotalCount = results.TotalFor(kind) });
        }
        Hits(SearchFacet.All, results.TopHits); Hits(SearchFacet.Audiobooks, results.Audiobooks);
        Hits(SearchFacet.Profiles, results.Profiles); Hits(SearchFacet.Authors, results.Authors);
        var totals = Enum.GetValues<SearchFacet>().ToDictionary(kind => kind, results.TotalFor);
        return new(new ReplaceFacetPatch(new CatalogDocumentValue(FacetKind.Search, sections)
        { SearchFacet = facet, SearchChips = results.ChipOrder, SearchGenres = results.Genres, SearchTotals = totals }), seeds);
    }

    public static CatalogDecodedFacet Suggestions(CatalogScope scope, SearchSuggestions suggestions)
    {
        var seeds = new List<CatalogSeed>();
        var items = suggestions.Items.Select((item, index) =>
        {
            if (item.Kind == SearchSuggestionKind.User)
                seeds.Add(new(new(scope, item.Uri, FacetKind.UserIdentity), new UserIdentityPatch(Text(item.Title, item.Uri), Spec(item.Image is not null, item.Image))));
            else if (item.Kind != SearchSuggestionKind.Genre)
            {
                var kind = item.Kind switch
                {
                    SearchSuggestionKind.Track => HomeCardKind.Track, SearchSuggestionKind.Album => HomeCardKind.Album,
                    SearchSuggestionKind.Artist => HomeCardKind.Artist, SearchSuggestionKind.Playlist => HomeCardKind.Playlist,
                    SearchSuggestionKind.Episode => HomeCardKind.Episode, SearchSuggestionKind.Audiobook => HomeCardKind.Audiobook,
                    _ => HomeCardKind.Podcast,
                };
                SeedCard(scope, new(item.Uri, item.Title, item.Subtitle, item.Image, kind), seeds);
                if (item.Kind == SearchSuggestionKind.Track)
                    seeds.Add(new(new(scope, item.Uri, FacetKind.TrackIdentity), new TrackIdentityPatch(IsExplicit: FieldChange<bool?>.Set(item.IsExplicit))));
            }
            return new CatalogDocumentItem("suggestion:" + item.Uri + ":" + index, item.Uri)
            {
                SuggestionKind = item.Kind, Eyebrow = item.Subtitle,
                PresentationTitle = item.Kind == SearchSuggestionKind.Genre ? item.Title : null,
                PresentationImage = item.Kind == SearchSuggestionKind.Genre ? item.Image : null,
            };
        }).ToArray();
        return new(new ReplaceFacetPatch(new CatalogDocumentValue(FacetKind.SearchSuggestions,
            [new("suggestions", null, items)]) { SuggestedQueries = suggestions.Queries }), seeds);
    }

    static void SeedCard(CatalogScope scope, HomeCard card, List<CatalogSeed> seeds)
    {
        var image = card.Image ?? (card.MosaicTiles is { Count: > 0 } tiles ? new Image("", MosaicTiles: tiles) : null);
        switch (card.Kind)
        {
            case HomeCardKind.Playlist:
                seeds.Add(new(new(scope, card.Uri, FacetKind.PlaylistHeader), new PlaylistHeaderPatch(
                    Name: card.Meta?.NeedsHydration == true ? default : Text(card.Title, card.Uri),
                    Description: Text(card.Subtitle, ""), Cover: Spec(image is not null, image),
                    OwnerName: Text(card.Meta?.OwnerName, ""), Format: Text(card.Meta?.Format, ""),
                    TrackCount: Spec<int?>(card.Meta?.TrackCount > 0, card.Meta?.TrackCount),
                    NextUpdateAt: Spec<DateTimeOffset?>(card.Meta?.ExpiresAtMs > 0, Instant(card.Meta?.ExpiresAtMs ?? 0)),
                    CreatedAt: Spec<DateTimeOffset?>(card.Meta?.CreatedAtMs > 0, Instant(card.Meta?.CreatedAtMs ?? 0)))));
                break;
            case HomeCardKind.Album:
                seeds.Add(new(new(scope, card.Uri, FacetKind.AlbumIdentity), new AlbumIdentityPatch(Text(card.Title, card.Uri), Spec(image is not null, image)))); break;
            case HomeCardKind.Artist:
                seeds.Add(new(new(scope, card.Uri, FacetKind.ArtistIdentity), new ArtistIdentityPatch(Text(card.Title, card.Uri), Spec(image is not null, image)))); break;
            case HomeCardKind.Track:
                seeds.Add(new(new(scope, card.Uri, FacetKind.TrackIdentity), new TrackIdentityPatch(Title: Text(card.Title, card.Uri), Image: Spec(image is not null, image)))); break;
            case HomeCardKind.Episode:
                seeds.Add(new(new(scope, card.Uri, FacetKind.EpisodeIdentity), new EpisodeIdentityPatch(Title: Text(card.Title, card.Uri),
                    Image: Spec(image is not null, image), DurationMs: Spec<long?>(card.Meta?.DurationMs > 0, card.Meta?.DurationMs)))); break;
            case HomeCardKind.Audiobook or HomeCardKind.Podcast:
                seeds.Add(new(new(scope, card.Uri, FacetKind.ShowIdentity), new ShowIdentityPatch(Name: Text(card.Title, card.Uri),
                    Publisher: Text(card.Subtitle, ""), Cover: Spec(image is not null, image)))); break;
        }
    }

    static void SeedHit(CatalogScope scope, SearchTopHit hit, List<CatalogSeed> seeds)
    {
        var homeKind = hit.Kind switch
        {
            SearchHitKind.Track => HomeCardKind.Track, SearchHitKind.Artist => HomeCardKind.Artist,
            SearchHitKind.Album => HomeCardKind.Album, SearchHitKind.Playlist => HomeCardKind.Playlist,
            SearchHitKind.Episode => HomeCardKind.Episode, SearchHitKind.Audiobook => HomeCardKind.Audiobook,
            _ => HomeCardKind.Podcast,
        };
        if (hit.Kind is SearchHitKind.User or SearchHitKind.Author)
            seeds.Add(new(new(scope, hit.Uri, FacetKind.UserIdentity), new UserIdentityPatch(Text(hit.Name, hit.Uri), Spec(hit.Image is not null, hit.Image))));
        else if (hit.Kind is not (SearchHitKind.Genre or SearchHitKind.Unknown))
            SeedCard(scope, new(hit.Uri, hit.Name, hit.Subtitle, hit.Image, homeKind), seeds);
    }
    static void ArtistRef(CatalogScope scope, ArtistRef artist, List<CatalogSeed> seeds)
    {
        if (artist.Uri.Length > 0)
            seeds.Add(new(new(scope, artist.Uri, FacetKind.ArtistIdentity), new ArtistIdentityPatch(Name: Text(artist.Name, artist.Uri))));
    }
    static string UserUri(string id) => id.StartsWith("spotify:user:", StringComparison.Ordinal) ? id : "spotify:user:" + id;
    static DateTimeOffset? Instant(long milliseconds) => milliseconds > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) : null;
    static FieldChange<string?> Text(string? text, string uri) => new(!string.IsNullOrEmpty(text) && text != uri, text);
    static FieldChange<T> Spec<T>(bool present, T value) => new(present, value);
}
