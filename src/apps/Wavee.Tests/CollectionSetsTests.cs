using System.Linq;
using Wavee.Backend.Collections;
using Xunit;

namespace Wavee.Tests;

/// <summary>The ylpin↔"pins" wiring in <see cref="CollectionSets"/> (§1.3 of
/// docs/plans/wavee/pin-spotify-sync-implementation.md) — the one place the mapping lives.</summary>
public class CollectionSetsTests
{
    [Fact]
    public void Pins_MapsToTheYlpinWireSet()
    {
        Assert.Equal("ylpin", CollectionSets.WireSet("pins"));
        Assert.Contains("ylpin", CollectionSets.WireSets);
        Assert.Null(CollectionSets.UriPrefix("pins"));   // mixed kinds — membership is filtered by AcceptsUri instead
    }

    [Fact]
    public void Ylpin_FansOutToExactlyThePinsLogicalSet()
    {
        Assert.Equal(new[] { "pins" }, CollectionSets.LogicalSetsForWireSet("ylpin"));
    }

    [Fact]
    public void UnknownWireSet_FansOutToNothing()
    {
        Assert.Empty(CollectionSets.LogicalSetsForWireSet("artistban"));
    }

    [Fact]
    public void Ylpin_NeverDirectApplies()
    {
        Assert.False(CollectionSets.PushDirectApplies("ylpin"));
    }

    [Theory]
    [InlineData("collection")]
    [InlineData("artist")]
    [InlineData("show")]
    [InlineData("listenlater")]
    public void EveryOtherWireSet_DirectApplies(string wireSet)
    {
        Assert.True(CollectionSets.PushDirectApplies(wireSet));
    }

    [Theory]
    [InlineData("spotify:playlist:1", true)]
    [InlineData("spotify:album:1", true)]
    [InlineData("spotify:artist:1", true)]
    [InlineData("spotify:show:1", true)]
    [InlineData("spotify:collection", true)]              // Liked Songs, the actual wire spelling (captured 2026-09-11)
    [InlineData("spotify:collection:tracks", true)]
    [InlineData("spotify:user:bob:collection", true)]
    [InlineData("spotify:folder:36405e1711f88d9c", true)]  // a rootlist folder
    [InlineData("spotify:track:1", false)]
    [InlineData("spotify:episode:1", false)]
    [InlineData("wavee:playlist:1", false)]
    [InlineData("not-a-uri", false)]
    // Unrepresentable server pins (§3 preservation invariant): never admitted into the local mirror, so they can
    // never become a local pin and never trigger a write that would erase them off the server.
    [InlineData("spotify:collection:your-episodes", false)]
    [InlineData("spotify:user:bob:collection:your-episodes", false)]
    [InlineData("spotify:local-files", false)]
    [InlineData("spotify:audiobook:x", false)]
    [InlineData("spotify:prerelease:x", false)]
    [InlineData("spotify:station:x", false)]
    public void AcceptsUri_Pins_OnlyPinnableSpotifyEntitiesAndLiked(string uri, bool expected)
        => Assert.Equal(expected, CollectionSets.AcceptsUri("pins", uri));

    [Theory]
    [InlineData("liked")]
    [InlineData("albums")]
    [InlineData("artists")]
    [InlineData("shows")]
    [InlineData("episodes")]
    [InlineData("playlists")]
    public void AcceptsUri_EveryOtherSet_AcceptsAnyUri(string setId)
    {
        // AcceptsUri is a "pins"-only filter; every other logical set admits whatever its prefix already filtered to.
        Assert.True(CollectionSets.AcceptsUri(setId, "spotify:track:1"));
        Assert.True(CollectionSets.AcceptsUri(setId, "anything"));
    }
}
