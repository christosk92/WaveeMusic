using Wavee.Features.Concerts;
using Xunit;

namespace Wavee.Tests;

public sealed class ConcertRouteTests
{
    [Fact]
    public void Hub_IsAnExactRoute()
    {
        Assert.True(ConcertRoutes.TryParse("concerts", out var route));
        Assert.Equal(ConcertRouteKind.Hub, route.Kind);
        Assert.Null(route.EntityId);
        Assert.False(ConcertRoutes.Is("concerts-extra"));
    }

    [Fact]
    public void ArtistSchedule_RoundTripsOpaqueProviderIdentifier()
    {
        const string artist = "spotify:artist:abc:def";
        string name = ConcertRoutes.ArtistSchedule(artist);

        Assert.Equal("artist-concerts:spotify:artist:abc:def", name);
        Assert.True(ConcertRoutes.TryParse(name, out var route));
        Assert.Equal(ConcertRouteKind.ArtistSchedule, route.Kind);
        Assert.Equal(artist, route.EntityId);
    }

    [Fact]
    public void Detail_RoundTripsNonSpotifyProviderIdentifier()
    {
        const string concert = "wavee:concert:local:42";
        string name = ConcertRoutes.Detail(concert);

        Assert.Equal("concert:wavee:concert:local:42", name);
        Assert.True(ConcertRoutes.TryParse(name, out var route));
        Assert.Equal(ConcertRouteKind.Detail, route.Kind);
        Assert.Equal(concert, route.EntityId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("concert:")]
    [InlineData("artist-concerts:")]
    [InlineData("artist:spotify:artist:x")]
    public void InvalidRoute_IsRejected(string name) => Assert.False(ConcertRoutes.Is(name));

    [Fact]
    public void Builders_RejectMissingEntityIdentifiers()
    {
        Assert.Throws<ArgumentException>(() => ConcertRoutes.Detail(" "));
        Assert.Throws<ArgumentException>(() => ConcertRoutes.ArtistSchedule(string.Empty));
    }
}
