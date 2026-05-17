using System.Collections.Generic;
using FluentAssertions;
using Wavee.UI.Helpers.Artist;
using Xunit;

namespace Wavee.UI.Tests.Helpers.Artist;

public sealed class ArtistTrackImageResolverTests
{
    [Fact]
    public void Resolves_ByFullUri()
    {
        var dict = new Dictionary<string, string?> { ["spotify:track:abc"] = "https://img/abc.jpg" };

        var found = ArtistTrackImageResolver.TryResolve(dict, "spotify:track:abc", out var url);

        found.Should().BeTrue();
        url.Should().Be("https://img/abc.jpg");
    }

    [Fact]
    public void Resolves_ByBareId_WhenDictKeyedByBareId()
    {
        var dict = new Dictionary<string, string?> { ["abc"] = "https://img/abc.jpg" };

        var found = ArtistTrackImageResolver.TryResolve(dict, "spotify:track:abc", out var url);

        found.Should().BeTrue();
        url.Should().Be("https://img/abc.jpg");
    }

    [Fact]
    public void Resolves_ByFullUri_WhenDictKeyedByFullUri_ButQueryBare()
    {
        var dict = new Dictionary<string, string?> { ["spotify:track:abc"] = "https://img/abc.jpg" };

        var found = ArtistTrackImageResolver.TryResolve(dict, "abc", out var url);

        found.Should().BeTrue();
        url.Should().Be("https://img/abc.jpg");
    }

    [Fact]
    public void Miss_ReturnsFalse_OutNull()
    {
        var dict = new Dictionary<string, string?> { ["spotify:track:abc"] = "https://img/abc.jpg" };

        var found = ArtistTrackImageResolver.TryResolve(dict, "spotify:track:zzz", out var url);

        found.Should().BeFalse();
        url.Should().BeNull();
    }

    [Fact]
    public void NullValue_StillReturnsTrue_WithNullUrl()
    {
        // A dictionary entry can legitimately store null when an image is known
        // to not exist for that track — the lookup is "key present" semantics.
        var dict = new Dictionary<string, string?> { ["spotify:track:abc"] = null };

        var found = ArtistTrackImageResolver.TryResolve(dict, "spotify:track:abc", out var url);

        found.Should().BeTrue();
        url.Should().BeNull();
    }
}
