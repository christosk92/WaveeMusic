using System;
using System.Linq;
using FluentAssertions;
using Wavee.UI.Contracts;
using Wavee.UI.Models;
using Wavee.UI.Services;

namespace Wavee.UI.Tests.Services;

public sealed class ArtistDiscographyLibraryMapperTests
{
    [Fact]
    public void Map_MatchesLikedSongsByAlbumId_WhenArtistIdIsMissing()
    {
        var releases = new[]
        {
            MakeRelease("a1", "mood swings")
        };
        var likedSongs = new[]
        {
            MakeLikedSong("spotify:track:t1", "spotify:album:a1", "mood swings", artistId: "")
        };

        var result = ArtistDiscographyLibraryMapper.Map(releases, Array.Empty<LibraryAlbumDto>(), likedSongs);

        result.Should().HaveCount(1);
        result[0].ContainsLikedSongs.Should().BeTrue();
        result[0].IsInLibrary.Should().BeTrue();
    }

    [Fact]
    public void Map_MarksSavedOnlyMembershipForSavedAndLikedAlbums()
    {
        var releases = new[]
        {
            MakeRelease("saved", "Saved Album"),
            MakeRelease("liked", "Liked Album"),
            MakeRelease("outside", "Outside Album"),
        };
        var savedAlbums = new[]
        {
            MakeSavedAlbum("spotify:album:saved", "Saved Album")
        };
        var likedSongs = new[]
        {
            MakeLikedSong("spotify:track:t1", "spotify:album:liked", "Liked Album")
        };

        var result = ArtistDiscographyLibraryMapper.Map(releases, savedAlbums, likedSongs);
        var inLibrary = result.Where(album => album.IsInLibrary).Select(album => album.Id);

        inLibrary.Should().Equal("spotify:album:saved", "spotify:album:liked");
        result.Single(album => album.Id == "spotify:album:outside").IsInLibrary.Should().BeFalse();
    }

    [Fact]
    public void Map_UsesArtworkFallbackOrder()
    {
        var releases = new[]
        {
            MakeRelease("release-image", "Release Image", imageUrl: "discography-img"),
            MakeRelease("saved-image", "Saved Image"),
            MakeRelease("liked-image", "Liked Image"),
        };
        var savedAlbums = new[]
        {
            MakeSavedAlbum("spotify:album:release-image", "Release Image", imageUrl: "saved-ignored"),
            MakeSavedAlbum("spotify:album:saved-image", "Saved Image", imageUrl: "saved-img"),
        };
        var likedSongs = new[]
        {
            MakeLikedSong("spotify:track:t1", "spotify:album:release-image", "Release Image", imageUrl: "liked-ignored"),
            MakeLikedSong("spotify:track:t2", "spotify:album:saved-image", "Saved Image", imageUrl: "liked-ignored"),
            MakeLikedSong("spotify:track:t3", "spotify:album:liked-image", "Liked Image", imageUrl: "liked-img"),
        };

        var result = ArtistDiscographyLibraryMapper.Map(releases, savedAlbums, likedSongs);

        result.Single(album => album.Id == "spotify:album:release-image").ImageUrl.Should().Be("discography-img");
        result.Single(album => album.Id == "spotify:album:saved-image").ImageUrl.Should().Be("saved-img");
        result.Single(album => album.Id == "spotify:album:liked-image").ImageUrl.Should().Be("liked-img");
    }

    [Fact]
    public void Map_DoesNotMatchLikedSongWithDifferentAlbumIdEvenWhenNameMatches()
    {
        var releases = new[]
        {
            MakeRelease("a1", "Same Name")
        };
        var likedSongs = new[]
        {
            MakeLikedSong("spotify:track:t1", "spotify:album:a2", "Same Name")
        };

        var result = ArtistDiscographyLibraryMapper.Map(releases, Array.Empty<LibraryAlbumDto>(), likedSongs);

        result[0].ContainsLikedSongs.Should().BeFalse();
        result[0].IsInLibrary.Should().BeFalse();
    }

    [Fact]
    public void Map_UsesAlbumNameFallbackWhenLikedSongAlbumIdIsMissing()
    {
        var releases = new[]
        {
            MakeRelease("a1", "Name Only Match")
        };
        var likedSongs = new[]
        {
            MakeLikedSong("spotify:track:t1", "", "Name Only Match", imageUrl: "liked-img")
        };

        var result = ArtistDiscographyLibraryMapper.Map(releases, Array.Empty<LibraryAlbumDto>(), likedSongs);

        result[0].ContainsLikedSongs.Should().BeTrue();
        result[0].ImageUrl.Should().Be("liked-img");
    }

    private static ArtistReleaseResult MakeRelease(
        string id,
        string name,
        string type = "ALBUM",
        string? uri = null,
        string? imageUrl = null,
        int year = 2026) => new()
        {
            Id = id,
            Uri = uri,
            Name = name,
            Type = type,
            ImageUrl = imageUrl,
            Year = year
        };

    private static LibraryAlbumDto MakeSavedAlbum(
        string id,
        string name,
        string? imageUrl = null) => new()
        {
            Id = id,
            Name = name,
            ArtistName = "Artist",
            ImageUrl = imageUrl,
            AddedAt = DateTimeOffset.UtcNow
        };

    private static LikedSongDto MakeLikedSong(
        string uri,
        string albumId,
        string albumName,
        string artistId = "spotify:artist:artist",
        string? imageUrl = null) => new()
        {
            Id = uri.Split(':').Last(),
            Uri = uri,
            Title = "Track",
            ArtistName = "Artist",
            ArtistId = artistId,
            AlbumName = albumName,
            AlbumId = albumId,
            ImageUrl = imageUrl,
            Duration = TimeSpan.FromSeconds(180),
            AddedAt = DateTime.Now,
            OriginalIndex = 1
        };
}
