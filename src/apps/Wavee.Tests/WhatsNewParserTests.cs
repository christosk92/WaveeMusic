using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Wavee.Core;
using Wavee.SpotifyLive;
using Xunit;

// SpotifyWhatsNewService.Parse against a fixture with an Album, an Episode, and an unknown typename (skipped), plus the
// SEEN → read mapping. Pure parsing (no network); the fixture mirrors the pathfinder queryWhatsNewFeed response shape.
public class WhatsNewParserTests
{
    static JsonElement Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "whatsnew-feed.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Parse_KeepsKnownTypenames_SkipsUnknown()
    {
        var items = SpotifyWhatsNewService.Parse(Load());
        Assert.Equal(2, items.Count);   // Chapter skipped
        Assert.Contains(items, i => i.Kind == NewReleaseKind.Album);
        Assert.Contains(items, i => i.Kind == NewReleaseKind.Episode);
        Assert.DoesNotContain(items, i => i.Uri.Contains("chapter"));
    }

    [Fact]
    public void Parse_Album_MapsFields_SeenIsRead()
    {
        var album = SpotifyWhatsNewService.Parse(Load()).First(i => i.Kind == NewReleaseKind.Album);
        Assert.Equal("spotify:album:fakealb1", album.Uri);
        Assert.Equal("Fake Album One", album.Name);
        Assert.Equal("ALBUM", album.AlbumType);
        Assert.Equal("Fake Artist, Guest", album.CreatorName);
        Assert.Equal("https://i.example/alb1.jpg", album.ImageUrl);
        Assert.False(album.IsUnread);   // state == SEEN
        Assert.Equal(DateTimeOffset.Parse("2024-01-15T00:00:00Z").ToUnixTimeMilliseconds(), album.Timestamp);
    }

    [Fact]
    public void Parse_Episode_MapsFields_UnseenIsUnread()
    {
        var ep = SpotifyWhatsNewService.Parse(Load()).First(i => i.Kind == NewReleaseKind.Episode);
        Assert.Equal("spotify:episode:fakeep1", ep.Uri);
        Assert.Equal("Fake Episode One", ep.Name);
        Assert.Equal("Fake Podcast", ep.CreatorName);
        Assert.Equal("https://i.example/ep1.jpg", ep.ImageUrl);
        Assert.True(ep.IsUnread);   // state != SEEN
        Assert.True(ep.Played);     // playedState == FULLY_PLAYED
    }

    [Fact]
    public void Parse_EmptyOrMalformed_ReturnsEmpty()
    {
        using var doc = JsonDocument.Parse("{\"data\":{}}");
        Assert.Empty(SpotifyWhatsNewService.Parse(doc.RootElement));
    }
}
