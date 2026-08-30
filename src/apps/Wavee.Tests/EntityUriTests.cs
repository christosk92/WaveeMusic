using System;
using System.Linq;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>THE uri parser (design §1.1). These cases are the whole surface every routing site now depends on — before
/// this type the app carried six IdOf copies and ~40 hand-rolled <c>StartsWith("spotify:track:")</c> gates, and every
/// scheme below is one that some gate got wrong (episodes dropped, user-namespaced playlists unrecognized, local rows
/// treated as Spotify).</summary>
public class EntityUriTests
{
    [Theory]
    // spotify — the six fetchable kinds
    [InlineData("spotify:track:abc", EntityProviders.Spotify, EntityKind.Track, "abc")]
    [InlineData("spotify:episode:e1", EntityProviders.Spotify, EntityKind.Episode, "e1")]
    [InlineData("spotify:album:xyz", EntityProviders.Spotify, EntityKind.Album, "xyz")]
    [InlineData("spotify:artist:a1", EntityProviders.Spotify, EntityKind.Artist, "a1")]
    [InlineData("spotify:playlist:p1", EntityProviders.Spotify, EntityKind.Playlist, "p1")]
    [InlineData("spotify:show:s1", EntityProviders.Spotify, EntityKind.Show, "s1")]
    // spotify — the routing-only kinds
    [InlineData("spotify:collection:tracks", EntityProviders.Spotify, EntityKind.Collection, "tracks")]
    [InlineData("spotify:collection:albums", EntityProviders.Spotify, EntityKind.Collection, "albums")]
    [InlineData("spotify:collection:your-episodes", EntityProviders.Spotify, EntityKind.Collection, "your-episodes")]
    [InlineData("spotify:prerelease:pr1", EntityProviders.Spotify, EntityKind.Prerelease, "pr1")]
    [InlineData("spotify:concert:c1", EntityProviders.Spotify, EntityKind.Concert, "c1")]
    // spotify:user — the one multiplexed head
    [InlineData("spotify:user:bob", EntityProviders.Spotify, EntityKind.User, "bob")]
    [InlineData("spotify:user:bob:collection", EntityProviders.Spotify, EntityKind.Collection, "collection")]
    [InlineData("spotify:user:bob:collection:your-episodes", EntityProviders.Spotify, EntityKind.Collection, "your-episodes")]
    [InlineData("spotify:user:bob:playlist:p9", EntityProviders.Spotify, EntityKind.Playlist, "p9")]
    // local files (both spellings LocalSource.Owns accepts)
    [InlineData("local:track:3", EntityProviders.Local, EntityKind.Track, "3")]
    [InlineData("wavee:local:track:3", EntityProviders.Local, EntityKind.Track, "3")]
    [InlineData("wavee:local:album:3", EntityProviders.Local, EntityKind.Album, "3")]
    [InlineData("wavee:local:artist:3", EntityProviders.Local, EntityKind.Artist, "3")]
    [InlineData("wavee:local:file:QzpcbXVzaWM", EntityProviders.Local, EntityKind.Track, "QzpcbXVzaWM")]
    // session-created user playlists
    [InlineData("wavee:playlist:7", EntityProviders.User, EntityKind.Playlist, "7")]
    // the synthetic podcast source
    [InlineData("wavee:show:2", EntityProviders.WaveePodcast, EntityKind.Show, "2")]
    [InlineData("wavee:episode:2:5", EntityProviders.WaveePodcast, EntityKind.Episode, "5")]
    // the fake catalog, explicit and legacy-bare-id
    [InlineData("fake:track:9", EntityProviders.Fake, EntityKind.Track, "9")]
    [InlineData("tr7", EntityProviders.Fake, EntityKind.Track, "tr7")]
    [InlineData("al7", EntityProviders.Fake, EntityKind.Album, "al7")]
    [InlineData("pl7", EntityProviders.Fake, EntityKind.Playlist, "pl7")]
    [InlineData("ar7", EntityProviders.Fake, EntityKind.Artist, "ar7")]
    public void Parse_Table(string uri, string provider, EntityKind kind, string id)
    {
        var e = EntityUri.Parse(uri);
        Assert.Equal(uri, e.Uri);
        Assert.Equal(provider, e.Provider);
        Assert.Equal(kind, e.Kind);
        Assert.Equal(id, e.Id);
        Assert.Equal(kind, EntityUri.KindOf(uri));   // KindOf must never disagree with Parse
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-uri")]
    [InlineData("trx")]                              // the legacy shape needs DIGITS after the two-letter prefix
    [InlineData("tr")]
    [InlineData("xy7")]
    [InlineData("wavee:skeleton:section:hero")]      // a wavee route, not an entity — nobody owns it
    [InlineData("wavee:media:whatever")]
    [InlineData("https://open.spotify.com/track/abc")]
    public void Parse_Garbage_IsUnownedAndUnknown(string uri)
    {
        var e = EntityUri.Parse(uri);
        Assert.Equal(EntityProviders.None, e.Provider);
        Assert.Equal(EntityKind.Unknown, e.Kind);
        Assert.Equal("", e.Id);                      // never mint an id for something we cannot route
        Assert.False(e.IsPlayable);
        Assert.False(e.IsContainer);
        Assert.False(e.IsSpotify);
    }

    [Fact]
    public void Parse_UnknownSpotifyType_StaysSpotifyOwned()
    {
        // Routing and the ladder are separate answers: Spotify still OWNS the uri, we just have no ladder for it.
        var e = EntityUri.Parse("spotify:wibble:x");
        Assert.Equal(EntityProviders.Spotify, e.Provider);
        Assert.True(e.IsSpotify);
        Assert.Equal(EntityKind.Unknown, e.Kind);
    }

    [Theory]
    [InlineData("spotify:user:x:playlist:y", "y")]
    [InlineData("spotify:track:abc", "abc")]
    [InlineData("wavee:episode:2:5", "5")]
    [InlineData("bare", "bare")]
    [InlineData("spotify:track:", "")]
    [InlineData("", "")]
    public void IdOf_IsTheTrailingSegment(string uri, string id) => Assert.Equal(id, EntityUri.IdOf(uri));

    [Theory]
    [InlineData("spotify:track:t", true)]
    [InlineData("spotify:episode:e", true)]
    [InlineData("wavee:episode:1:2", true)]
    [InlineData("wavee:local:file:QQ", true)]        // a local file IS a playable — the queue carries it
    [InlineData("spotify:album:a", false)]
    [InlineData("spotify:show:s", false)]
    [InlineData("spotify:user:u", false)]
    public void IsPlayable_CoversEpisodesToo(string uri, bool playable)
        => Assert.Equal(playable, EntityUri.Parse(uri).IsPlayable);

    [Theory]
    [InlineData("spotify:album:a", true)]
    [InlineData("spotify:playlist:p", true)]
    [InlineData("spotify:show:s", true)]
    [InlineData("spotify:artist:a", true)]
    [InlineData("spotify:collection:tracks", true)]
    [InlineData("spotify:track:t", false)]
    [InlineData("spotify:user:u", false)]
    public void IsContainer_IsTheSurfacesThatOpenToPlayables(string uri, bool container)
        => Assert.Equal(container, EntityUri.Parse(uri).IsContainer);

    [Fact]
    public void KindOf_IsAllocationFree()   // routing runs per entity at 10k+ scale — String.Split would cost ~4 objects/call
    {
        var uris = Enumerable.Range(0, 1000).Select(i => $"spotify:track:t{i}").ToArray();
        foreach (var u in uris) _ = EntityUri.KindOf(u);   // warm up JIT

        long before = GC.GetAllocatedBytesForCurrentThread();
        var acc = EntityKind.Unknown;
        foreach (var u in uris) acc |= EntityUri.KindOf(u);
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(EntityKind.Track, acc);   // keep the result live
        Assert.True(delta < 200, $"EntityUri.KindOf allocated {delta} bytes for 1000 parses (expected ~0)");
    }

    [Fact]
    public void Parse_AllocatesOnlyTheId()
    {
        // Parse materializes exactly ONE small substring (the id). The provider is a const and the kind is a byte, so
        // the ceiling below is "one short string per uri" — an order of magnitude under String.Split's four objects.
        var uris = Enumerable.Range(0, 1000).Select(i => $"spotify:track:t{i}").ToArray();
        foreach (var u in uris) _ = EntityUri.Parse(u);

        long before = GC.GetAllocatedBytesForCurrentThread();
        int live = 0;
        foreach (var u in uris) live += EntityUri.Parse(u).Id.Length;
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(live > 0);
        Assert.True(delta < 1000 * 48, $"EntityUri.Parse allocated {delta} bytes for 1000 parses (expected ~1 string each)");
    }
}
