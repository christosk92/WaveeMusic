using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Wavee.UI.Models;
using Wavee.UI.Services;

namespace Wavee.UI.Tests.Services;

public sealed class LikedSongsByArtistGrouperTests
{
    [Fact]
    public void Group_EmptyInput_ReturnsEmpty()
    {
        var result = LikedSongsByArtistGrouper.Group(Array.Empty<LikedSongDto>(), Array.Empty<LibraryArtistDto>());

        result.Should().BeEmpty();
    }

    [Fact]
    public void Group_BucketsByArtistId_AndCountsLikedSongs()
    {
        var liked = new[]
        {
            MakeSong("spotify:track:t1", "Artist A", "spotify:artist:a"),
            MakeSong("spotify:track:t2", "Artist A", "spotify:artist:a"),
            MakeSong("spotify:track:t3", "Artist B", "spotify:artist:b"),
        };

        var result = LikedSongsByArtistGrouper.Group(liked, Array.Empty<LibraryArtistDto>());

        result.Should().HaveCount(2);
        result.Single(a => a.Id == "spotify:artist:a").LikedSongCount.Should().Be(2);
        result.Single(a => a.Id == "spotify:artist:b").LikedSongCount.Should().Be(1);
    }

    [Fact]
    public void Group_FallsBackToArtistName_WhenArtistIdIsMissing()
    {
        var liked = new[]
        {
            MakeSong("spotify:track:t1", "Artist A", ""),
            MakeSong("spotify:track:t2", "artist a", ""),
        };

        var result = LikedSongsByArtistGrouper.Group(liked, Array.Empty<LibraryArtistDto>());

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("artist:name:artist a");
        result[0].LikedSongCount.Should().Be(2);
        result[0].CanOpenArtist.Should().BeFalse();
    }

    [Fact]
    public void Group_UsesFollowedArtistMetadata_WhenNameFallbackMatches()
    {
        var liked = new[]
        {
            MakeSong("spotify:track:t1", "Artist A", ""),
        };
        var followed = new[]
        {
            new LibraryArtistDto
            {
                Id = "spotify:artist:a",
                Name = "Artist A",
                ImageUrl = "artist-image",
                AddedAt = DateTimeOffset.UtcNow
            }
        };

        var result = LikedSongsByArtistGrouper.Group(liked, followed);

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("spotify:artist:a");
        result[0].ImageUrl.Should().Be("artist-image");
        result[0].IsAlsoFollowed.Should().BeTrue();
        result[0].CanOpenArtist.Should().BeTrue();
    }

    [Fact]
    public void Group_PreservesOriginalLikedSongsOrderWithinBucket()
    {
        var liked = new[]
        {
            MakeSong("spotify:track:t3", "Artist A", "spotify:artist:a", originalIndex: 3),
            MakeSong("spotify:track:t1", "Artist A", "spotify:artist:a", originalIndex: 1),
            MakeSong("spotify:track:t2", "Artist A", "spotify:artist:a", originalIndex: 2),
        };

        var result = LikedSongsByArtistGrouper.Group(liked, Array.Empty<LibraryArtistDto>());

        result.Should().HaveCount(1);
        result[0].LikedSongs.Select(s => s.Uri).Should().Equal("spotify:track:t1", "spotify:track:t2", "spotify:track:t3");
    }

    [Fact]
    public void Group_MostRecentLikedAt_IsTheMaxAddedAtInBucket()
    {
        var older = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Local);
        var newer = new DateTime(2026, 5, 24, 9, 0, 0, DateTimeKind.Local);
        var liked = new[]
        {
            MakeSong("spotify:track:t1", "Artist A", "spotify:artist:a", addedAt: older),
            MakeSong("spotify:track:t2", "Artist A", "spotify:artist:a", addedAt: newer),
        };

        var result = LikedSongsByArtistGrouper.Group(liked, Array.Empty<LibraryArtistDto>());

        result[0].MostRecentLikedAt.LocalDateTime.Should().Be(newer);
    }

    [Fact]
    public void Group_Subtitle_PluralisesAndAppendsFollowingSuffix()
    {
        var liked = new[]
        {
            MakeSong("spotify:track:t1", "Artist A", "spotify:artist:a"),
            MakeSong("spotify:track:t2", "Artist A", "spotify:artist:a"),
        };
        var followed = new[]
        {
            new LibraryArtistDto
            {
                Id = "spotify:artist:a",
                Name = "Artist A",
                AddedAt = DateTimeOffset.UtcNow
            }
        };

        var result = LikedSongsByArtistGrouper.Group(liked, followed);

        result[0].LikedSongCountLabel.Should().Be("2 liked songs");
        result[0].Subtitle.Should().Be("2 liked songs \u00b7 following");
    }

    private static LikedSongDto MakeSong(
        string uri,
        string artistName,
        string artistId,
        DateTime? addedAt = null,
        int originalIndex = 0) => new()
        {
            Id = uri.Split(':').Last(),
            Uri = uri,
            Title = uri,
            ArtistName = artistName,
            ArtistId = artistId,
            AlbumName = "Album",
            AlbumId = "spotify:album:a",
            Duration = TimeSpan.FromSeconds(180),
            AddedAt = addedAt ?? DateTime.Now,
            OriginalIndex = originalIndex,
        };
}
