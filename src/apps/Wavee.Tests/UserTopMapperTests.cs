using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Wavee.SpotifyLive;
using Xunit;

namespace Wavee.Tests;

public sealed class UserTopMapperTests
{
    const string Response = """
    {
      "data": { "me": { "profile": {
        "topArtists": { "items": [
          { "data": { "uri": "spotify:artist:a1", "profile": { "name": "Artist One" } } }
        ] },
        "topTracks": { "items": [
          { "data": {
            "uri": "spotify:track:t1", "name": "Track One",
            "duration": { "totalMilliseconds": 201000 },
            "artists": { "items": [
              { "uri": "spotify:artist:a1", "profile": { "name": "Artist One" } }
            ] },
            "albumOfTrack": { "uri": "spotify:album:r1", "coverArt": { "sources": [] } }
          } },
          { "data": { "uri": "spotify:track:t1", "name": "Duplicate" } }
        ] }
      } } }
    }
    """;

    [Fact]
    public void UserTopContent_MapsArtistsAndTracksFromTheSameDocument()
    {
        using var doc = JsonDocument.Parse(Response);

        var artists = SpotifyExportMapper.TopArtistsFromUserTop(doc.RootElement);
        var tracks = SpotifyExportMapper.TopTracksFromUserTop(doc.RootElement);

        Assert.Single(artists);
        Assert.Equal("spotify:artist:a1", artists[0].Uri);
        Assert.Single(tracks);
        Assert.Equal("spotify:track:t1", tracks[0].Uri);
        Assert.Equal(201000, tracks[0].DurationMs);
    }

    // RETURN-ONLY (hydration-facade-plan.md 1.6): the service no longer writes the store. Both planes still come
    // out of ONE request - that shared-document contract is what this case pins; the store assertions moved out
    // with the thin UpsertArtist/UpsertTrack pass (a card opens through the facade, which hydrates properly).
    [Fact]
    public async Task ArtistsAndTracks_ShareOneRequest()
    {
        var http = new FakeExchange((_, _) => new HttpResp(200,
            new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase), Encoding.UTF8.GetBytes(Response)));
        var store = new InMemoryStore();
        var pf = new PathfinderResource(new PathfinderClient(http), static () =>
            new SessionContext("me", "US", "premium", "en", Tier.Premium, false));
        var service = new SpotifyUserTopService(pf);

        var artistsTask = service.GetTopArtistsAsync();
        var tracksTask = service.GetTopTracksAsync();
        await Task.WhenAll(artistsTask, tracksTask);

        Assert.Equal(1, http.Calls);
        Assert.Single(await artistsTask);
        Assert.Single(await tracksTask);
        Assert.Null(store.GetArtist("spotify:artist:a1"));   // return-only: nothing was minted
        Assert.Null(store.GetTrack("spotify:track:t1"));
    }

    // ── the negative cache window ──────────────────────────────────────────────────────────────────────────────
    // A failed fetch used to be stamped with the SUCCESS TTL (30 min), so one transient hiccup left Home's top-artist
    // row blank for half an hour: every re-navigation served the cached emptiness and never re-asked. A degraded answer
    // now expires in a minute; a REAL answer — including a genuinely empty one from a new account — keeps the window.
    static Dictionary<string, string> NoHeaders() => new(System.StringComparer.OrdinalIgnoreCase);

    [Fact]
    public async Task AFailedFetch_ExpiresInAMinute_AndTheRowRecoversOnTheNextOpen()
    {
        bool down = true;
        var http = new FakeExchange((_, _) => down
            ? new HttpResp(500, NoHeaders(), System.Array.Empty<byte>())
            : new HttpResp(200, NoHeaders(), Encoding.UTF8.GetBytes(Response)));
        var now = new System.DateTimeOffset(2026, 1, 1, 0, 0, 0, System.TimeSpan.Zero);
        var service = new SpotifyUserTopService(Pf(http), clock: () => now);

        Assert.Empty(await service.GetTopArtistsAsync());        // the failure is cached, but only briefly

        down = false;
        Assert.Empty(await service.GetTopArtistsAsync());        // still inside the negative window → not re-asked
        now = now.AddSeconds(61);
        Assert.Single(await service.GetTopArtistsAsync());       // window elapsed → re-asked, and the row is back
    }

    [Fact]
    public async Task ASuccessfulFetch_HoldsThroughTheNegativeWindow()
    {
        var http = new FakeExchange((_, _) => new HttpResp(200, NoHeaders(), Encoding.UTF8.GetBytes(Response)));
        var now = new System.DateTimeOffset(2026, 1, 1, 0, 0, 0, System.TimeSpan.Zero);
        var service = new SpotifyUserTopService(Pf(http), clock: () => now);

        Assert.Single(await service.GetTopArtistsAsync());
        int afterFirst = http.Calls;
        now = now.AddSeconds(61);
        Assert.Single(await service.GetTopArtistsAsync());
        Assert.Equal(afterFirst, http.Calls);                    // a real answer is NOT on the failure window
    }

    static PathfinderResource Pf(FakeExchange http)
        => new(new PathfinderClient(http), static () =>
            new SessionContext("me", "US", "premium", "en", Tier.Premium, false));
}
