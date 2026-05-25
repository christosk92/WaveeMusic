using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Wavee.UI.Models;
using Wavee.UI.Services;

namespace Wavee.UI.Tests.Services;

public sealed class LikedSongsByAlbumGrouperTests
{
    [Fact]
    public void Group_EmptyInput_ReturnsEmpty()
    {
        var result = LikedSongsByAlbumGrouper.Group(Array.Empty<LikedSongDto>(), new HashSet<string>());
        result.Should().BeEmpty();
    }

    [Fact]
    public void Group_NullSavedSet_DoesNotThrow_AndAllEntriesAreNotMarkedSaved()
    {
        var liked = new[]
        {
            MakeSong("spotify:track:t1", "spotify:album:a1", "Track 1", "Album One", "Artist", "img1"),
        };

        var result = LikedSongsByAlbumGrouper.Group(liked, null!);

        result.Should().HaveCount(1);
        result[0].IsAlsoSaved.Should().BeFalse();
    }

    [Fact]
    public void Group_BucketsByAlbumId_AndCountsLikedSongs()
    {
        var liked = new[]
        {
            MakeSong("spotify:track:t1", "spotify:album:a1", "T1", "Album A", "Artist X", "imgA"),
            MakeSong("spotify:track:t2", "spotify:album:a1", "T2", "Album A", "Artist X", "imgA"),
            MakeSong("spotify:track:t3", "spotify:album:a2", "T3", "Album B", "Artist Y", "imgB"),
        };

        var result = LikedSongsByAlbumGrouper.Group(liked, new HashSet<string>());

        result.Should().HaveCount(2);
        var albumA = result.Single(a => a.Id == "spotify:album:a1");
        albumA.LikedSongCount.Should().Be(2);
        albumA.LikedSongs.Select(s => s.Uri).Should().BeEquivalentTo(["spotify:track:t1", "spotify:track:t2"]);

        var albumB = result.Single(a => a.Id == "spotify:album:a2");
        albumB.LikedSongCount.Should().Be(1);
    }

    [Fact]
    public void Group_MostRecentLikedAt_IsTheMaxAddedAtInBucket()
    {
        var older = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Local);
        var newer = new DateTime(2026, 5, 24, 9, 0, 0, DateTimeKind.Local);

        var liked = new[]
        {
            MakeSong("spotify:track:t1", "spotify:album:a1", "T1", "Album A", "Artist", "img", addedAt: older),
            MakeSong("spotify:track:t2", "spotify:album:a1", "T2", "Album A", "Artist", "img", addedAt: newer),
        };

        var result = LikedSongsByAlbumGrouper.Group(liked, new HashSet<string>());

        result.Should().HaveCount(1);
        result[0].MostRecentLikedAt.LocalDateTime.Should().Be(newer);
    }

    [Fact]
    public void Group_SkipsSongsWithMissingOrWhitespaceAlbumId()
    {
        var liked = new[]
        {
            MakeSong("spotify:track:t1", "spotify:album:a1", "T1", "Album A", "Artist", "img"),
            MakeSong("spotify:track:t2", albumId: "", "T2", "Album ?", "Artist", "img"),
            MakeSong("spotify:track:t3", albumId: "   ", "T3", "Album ?", "Artist", "img"),
        };

        var result = LikedSongsByAlbumGrouper.Group(liked, new HashSet<string>());

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("spotify:album:a1");
    }

    [Fact]
    public void Group_PrefersFirstNonEmptyImageAndArtistFromBucket()
    {
        // First-encountered row has blank image/artist; the second row
        // populates them. The grouper should pick the populated fields.
        var liked = new[]
        {
            MakeSong("spotify:track:t1", "spotify:album:a1", "T1", "", "", imageUrl: null),
            MakeSong("spotify:track:t2", "spotify:album:a1", "T2", "Album A", "Artist X", "imgA", artistId: "spotify:artist:x"),
        };

        var result = LikedSongsByAlbumGrouper.Group(liked, new HashSet<string>());

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("Album A");
        result[0].ArtistName.Should().Be("Artist X");
        result[0].ArtistId.Should().Be("spotify:artist:x");
        result[0].ImageUrl.Should().Be("imgA");
    }

    [Fact]
    public void Group_SetsIsAlsoSavedWhenAlbumIdIsInSavedSet()
    {
        var liked = new[]
        {
            MakeSong("spotify:track:t1", "spotify:album:a1", "T1", "Album A", "Artist", "img"),
            MakeSong("spotify:track:t2", "spotify:album:a2", "T2", "Album B", "Artist", "img"),
        };
        var savedAlbumIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "spotify:album:a1" };

        var result = LikedSongsByAlbumGrouper.Group(liked, savedAlbumIds);

        result.Single(a => a.Id == "spotify:album:a1").IsAlsoSaved.Should().BeTrue();
        result.Single(a => a.Id == "spotify:album:a2").IsAlsoSaved.Should().BeFalse();
    }

    [Fact]
    public void Group_IsAlsoSavedMatchIsCaseInsensitive()
    {
        var liked = new[]
        {
            MakeSong("spotify:track:t1", "Spotify:Album:A1", "T1", "Album A", "Artist", "img"),
        };
        var savedAlbumIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "spotify:album:a1" };

        var result = LikedSongsByAlbumGrouper.Group(liked, savedAlbumIds);

        result.Should().HaveCount(1);
        result[0].IsAlsoSaved.Should().BeTrue();
    }

    [Fact]
    public void Group_PreservesOriginalLikedSongsOrderWithinBucket()
    {
        // Original index = the position in the parent Liked Songs list.
        // The grouper sorts ascending by OriginalIndex so the tracks shown
        // in the detail pane match the user's Liked Songs ordering.
        var liked = new[]
        {
            MakeSong("spotify:track:t3", "spotify:album:a1", "T3", "Album A", "Artist", "img", originalIndex: 3),
            MakeSong("spotify:track:t1", "spotify:album:a1", "T1", "Album A", "Artist", "img", originalIndex: 1),
            MakeSong("spotify:track:t2", "spotify:album:a1", "T2", "Album A", "Artist", "img", originalIndex: 2),
        };

        var result = LikedSongsByAlbumGrouper.Group(liked, new HashSet<string>());

        result.Should().HaveCount(1);
        result[0].LikedSongs.Select(s => s.Uri).Should().Equal("spotify:track:t1", "spotify:track:t2", "spotify:track:t3");
    }

    [Fact]
    public void Group_SubtitleLabel_PluralisesOne()
    {
        var liked = new[]
        {
            MakeSong("spotify:track:t1", "spotify:album:a1", "T1", "Album A", "Artist", "img"),
        };

        var result = LikedSongsByAlbumGrouper.Group(liked, new HashSet<string>());

        result[0].LikedSongCountLabel.Should().Be("1 liked song");
        result[0].Subtitle.Should().Be("1 liked song");
    }

    [Fact]
    public void Group_SubtitleLabel_AppendsSavedSuffixWhenAlsoInSavedSet()
    {
        var liked = new[]
        {
            MakeSong("spotify:track:t1", "spotify:album:a1", "T1", "Album A", "Artist", "img"),
            MakeSong("spotify:track:t2", "spotify:album:a1", "T2", "Album A", "Artist", "img"),
        };
        var saved = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "spotify:album:a1" };

        var result = LikedSongsByAlbumGrouper.Group(liked, saved);

        result[0].Subtitle.Should().Be("2 liked songs · saved");
    }

    private static LikedSongDto MakeSong(
        string uri,
        string albumId,
        string title,
        string albumName,
        string artistName,
        string? imageUrl,
        string artistId = "",
        DateTime? addedAt = null,
        int originalIndex = 0) => new()
        {
            Id = uri.Split(':').Last(),
            Uri = uri,
            Title = title,
            ArtistName = artistName,
            ArtistId = artistId,
            AlbumName = albumName,
            AlbumId = albumId,
            ImageUrl = imageUrl,
            Duration = TimeSpan.FromSeconds(180),
            AddedAt = addedAt ?? DateTime.Now,
            OriginalIndex = originalIndex,
        };
}
