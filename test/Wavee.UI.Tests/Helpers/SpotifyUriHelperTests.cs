using FluentAssertions;
using Wavee.UI.Helpers;
using Xunit;

namespace Wavee.UI.Tests.Helpers;

public sealed class SpotifyUriHelperTests
{
    [Theory]
    [InlineData("spotify:track:4tZwfgrHOc3mvqYlEYSvVi", SpotifyEntityKind.Track, "4tZwfgrHOc3mvqYlEYSvVi")]
    [InlineData("spotify:album:1DFixLWuPkv3KT3TnV35m3", SpotifyEntityKind.Album, "1DFixLWuPkv3KT3TnV35m3")]
    [InlineData("spotify:artist:0OdUWJ0sBjDrqHygGUXeCF", SpotifyEntityKind.Artist, "0OdUWJ0sBjDrqHygGUXeCF")]
    [InlineData("spotify:playlist:37i9dQZF1DXcBWIGoYBM5M", SpotifyEntityKind.Playlist, "37i9dQZF1DXcBWIGoYBM5M")]
    [InlineData("spotify:episode:512ojhOuo1ktJprKbVcKyQ", SpotifyEntityKind.Episode, "512ojhOuo1ktJprKbVcKyQ")]
    [InlineData("spotify:show:38bS44xjbVVZ3No3ByF1dJ", SpotifyEntityKind.Show, "38bS44xjbVVZ3No3ByF1dJ")]
    [InlineData("spotify:user:wizzler", SpotifyEntityKind.User, "wizzler")]
    public void TryParse_ValidUris_ReturnsKindAndId(string uri, SpotifyEntityKind expectedKind, string expectedId)
    {
        var ok = SpotifyUriHelper.TryParse(uri, out var kind, out var id);
        ok.Should().BeTrue();
        kind.Should().Be(expectedKind);
        id.Should().Be(expectedId);
    }

    [Fact]
    public void TryParse_Collection_AllowsMissingIdSegment()
    {
        var ok = SpotifyUriHelper.TryParse("spotify:collection", out var kind, out var id);
        ok.Should().BeTrue();
        kind.Should().Be(SpotifyEntityKind.Collection);
        id.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-uri")]
    [InlineData("https://open.spotify.com/track/4tZwfgr")]
    [InlineData("spotify:bogus:xxx")]
    [InlineData("spotify:track:")]
    public void TryParse_InvalidInputs_ReturnsFalse(string? uri)
    {
        SpotifyUriHelper.TryParse(uri, out _, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("spotify:track:abc", SpotifyEntityKind.Track, true)]
    [InlineData("spotify:track:abc", SpotifyEntityKind.Album, false)]
    [InlineData("spotify:album:xyz", SpotifyEntityKind.Album, true)]
    [InlineData(null, SpotifyEntityKind.Track, false)]
    [InlineData("", SpotifyEntityKind.Track, false)]
    [InlineData("spotify:collection", SpotifyEntityKind.Collection, true)]
    [InlineData("spotify:collection:tracks", SpotifyEntityKind.Collection, true)]
    public void IsKind_MatchesPrefix(string? uri, SpotifyEntityKind kind, bool expected)
    {
        SpotifyUriHelper.IsKind(uri, kind).Should().Be(expected);
    }

    [Fact]
    public void ToUri_BuildsCanonicalForm()
    {
        SpotifyUriHelper.ToUri(SpotifyEntityKind.Track, "abc").Should().Be("spotify:track:abc");
        SpotifyUriHelper.ToUri(SpotifyEntityKind.Playlist, "xyz").Should().Be("spotify:playlist:xyz");
    }

    [Fact]
    public void ToUri_Collection_WithoutId_ReturnsBareForm()
    {
        SpotifyUriHelper.ToUri(SpotifyEntityKind.Collection, string.Empty).Should().Be("spotify:collection");
    }

    [Fact]
    public void ToUri_Unknown_Throws()
    {
        var act = () => SpotifyUriHelper.ToUri(SpotifyEntityKind.Unknown, "id");
        act.Should().Throw<System.ArgumentException>();
    }

    [Fact]
    public void RoundTrip_TryParse_ToUri_IsStable()
    {
        const string original = "spotify:track:4tZwfgrHOc3mvqYlEYSvVi";
        SpotifyUriHelper.TryParse(original, out var kind, out var id).Should().BeTrue();
        SpotifyUriHelper.ToUri(kind, id).Should().Be(original);
    }

    [Theory]
    [InlineData("spotify:track:abc", "https://open.spotify.com/track/abc")]
    [InlineData("spotify:album:xyz", "https://open.spotify.com/album/xyz")]
    [InlineData("spotify:artist:111", "https://open.spotify.com/artist/111")]
    [InlineData("spotify:playlist:p1", "https://open.spotify.com/playlist/p1")]
    public void ToHttps_ProducesPublicUrl(string uri, string expected)
    {
        SpotifyUriHelper.ToHttps(uri).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-uri")]
    [InlineData("spotify:folder")]    // only two segments
    public void ToHttps_BadInputs_ReturnNull(string? uri)
    {
        SpotifyUriHelper.ToHttps(uri).Should().BeNull();
    }

    [Theory]
    [InlineData("spotify:track:abc", "abc")]
    [InlineData("spotify:playlist:xyz", "xyz")]
    [InlineData("bare-id", "bare-id")]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void BareId_StripsToLastSegment(string? input, string expected)
    {
        SpotifyUriHelper.BareId(input).Should().Be(expected);
    }
}
