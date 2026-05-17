using FluentAssertions;
using Wavee.UI.Helpers.Artist;
using Xunit;

namespace Wavee.UI.Tests.Helpers.Artist;

public sealed class ArtistContextMatcherTests
{
    [Fact]
    public void Matches_FullUri_AgainstFullUri()
    {
        ArtistContextMatcher
            .IsActive("spotify:artist:4tZwfgrHOc3mvqYlEYSvVi", "spotify:artist:4tZwfgrHOc3mvqYlEYSvVi")
            .Should().BeTrue();
    }

    [Fact]
    public void Matches_FullUri_AgainstBareId()
    {
        ArtistContextMatcher
            .IsActive("spotify:artist:4tZwfgrHOc3mvqYlEYSvVi", "4tZwfgrHOc3mvqYlEYSvVi")
            .Should().BeTrue();
    }

    [Fact]
    public void Mismatch_ReturnsFalse()
    {
        ArtistContextMatcher
            .IsActive("spotify:artist:4tZwfgrHOc3mvqYlEYSvVi", "0OdUWJ0sBjDrqHygGUXeCF")
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(null, "abc")]
    [InlineData("", "abc")]
    [InlineData("   ", "abc")]
    [InlineData("spotify:artist:abc", null)]
    [InlineData("spotify:artist:abc", "")]
    [InlineData("spotify:artist:abc", "  ")]
    public void NullOrEmpty_ReturnsFalse(string? contextUri, string? artistId)
    {
        ArtistContextMatcher.IsActive(contextUri, artistId).Should().BeFalse();
    }

    [Fact]
    public void CaseInsensitiveMatch()
    {
        ArtistContextMatcher
            .IsActive("SPOTIFY:ARTIST:abc", "abc")
            .Should().BeTrue();
    }
}
