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
    [InlineData("spotify:collection:tracks", true)]
    [InlineData("spotify:user:bob:collection", true)]
    [InlineData("spotify:track:1", false)]
    [InlineData("spotify:episode:1", false)]
    [InlineData("wavee:playlist:1", false)]
    [InlineData("not-a-uri", false)]
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
