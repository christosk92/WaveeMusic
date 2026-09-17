// ── Wavee.Tests/ConcertRouteTests.cs — the concert routes (ch 17 §8 `ConcertRoutes`), ported from 0.2.9 ─────────────────
//
// Ported from `_old/Wavee.Tests/ConcertRouteTests.cs` (5 facts). 0.3 moved the route PARSE into the one Shell table
// (`Shell.Parse` / `Shell.NameOf`, which carry a BARE id and rebuild `spotify:concert:` / `spotify:artist:`), so the
// builders take handles (`Concert.DetailRoute`, `Concert.ScheduleRoute`) and two assertions change with the model, each
// named where it happens: an id is bare in the key (not the whole opaque uri), and a non-Spotify provider id cannot
// round-trip (the family rebuilds a Spotify uri); a dead handle answers `Route.None` instead of throwing.

using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public sealed class ConcertRouteTests
{
    [Fact]
    public void Hub_IsAnExactRoute()
    {
        var route = Shell.Parse("concerts");
        Assert.Equal(Shell.RouteKind.Concerts, route.Kind);
        Assert.False(route.Subject.IsValid);
        Assert.Equal(Concert.HubRoute, new Shell.Route(Shell.RouteKind.Concerts));
        Assert.NotEqual(Shell.RouteKind.Concerts, Shell.Parse("concerts-extra").Kind);
    }

    [Fact]
    public void ArtistSchedule_RoundTripsTheArtistIdentifier()
    {
        TestScope.Fresh();
        var artist = Entities.Artist(EntityUri.Parse("spotify:artist:abcdef"));
        var route = Concert.ScheduleRoute(artist);
        string name = Shell.NameOf(route);

        // 0.3: the key carries the BARE id (0.2.9 carried the whole opaque uri).
        Assert.Equal("artist-concerts:abcdef", name);
        var parsed = Shell.Parse(name);
        Assert.Equal(Shell.RouteKind.ArtistConcerts, parsed.Kind);
        Assert.Equal(artist.Uri.Text, parsed.Subject.Text);
    }

    [Fact]
    public void Detail_RoundTripsTheConcertIdentifier()
    {
        TestScope.Fresh();
        var concert = Entities.Concert(EntityUri.Parse("spotify:concert:local42"));
        var route = Concert.DetailRoute(concert);
        string name = Shell.NameOf(route);

        Assert.Equal("concert:local42", name);
        var parsed = Shell.Parse(name);
        Assert.Equal(Shell.RouteKind.Concert, parsed.Kind);
        // 0.3: the family rebuilds a SPOTIFY concert uri, so a non-Spotify provider id does not round-trip.
        Assert.Equal("spotify:concert:local42", parsed.Subject.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("concert:")]
    [InlineData("artist-concerts:")]
    [InlineData("artist:spotify:artist:x")]
    public void InvalidRoute_IsRejected(string name)
    {
        var route = Shell.Parse(name);
        bool concertRoute = route.Kind is Shell.RouteKind.Concerts or Shell.RouteKind.ArtistConcerts or Shell.RouteKind.Concert;
        Assert.False(concertRoute && Shell.IsKnown(route));
    }

    [Fact]
    public void Builders_RejectMissingEntityIdentifiers()
    {
        TestScope.Fresh();
        Assert.True(Concert.DetailRoute(default).IsNone);
        Assert.True(Concert.ScheduleRoute(default).IsNone);
    }
}
