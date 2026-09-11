using Xunit;

namespace Wavee.Tests;

/// <summary>The one rule for which sidebar pins mirror Spotify's ylpin set (§1.2 of
/// docs/plans/wavee/pin-spotify-sync-implementation.md). Engine-free, drives the real
/// <see cref="Wavee.PinSyncRules"/>/<see cref="Wavee.SidebarPinId"/> production code.</summary>
public class PinSyncRulesTests
{
    const string User = "bob";

    [Theory]
    [InlineData("pl:spotify:playlist:37i9dQZF1DX4sWSpwq3LiO", "spotify:playlist:37i9dQZF1DX4sWSpwq3LiO")]
    [InlineData("album:spotify:album:4aawyAB79vO75wG7WLfDzB", "spotify:album:4aawyAB79vO75wG7WLfDzB")]
    [InlineData("artist:spotify:artist:4tZwfgrHOc3mvqYlEYSvVi", "spotify:artist:4tZwfgrHOc3mvqYlEYSvVi")]
    [InlineData("show:spotify:show:4rOoJ6Egrf8K2IrywzwOMk", "spotify:show:4rOoJ6Egrf8K2IrywzwOMk")]
    public void SpotifyKindPins_MapToTheirBareUri(string pinId, string expectedUri)
        => Assert.Equal(expectedUri, PinSyncRules.TryWireUri(pinId, User));

    [Fact]
    public void LikedRoute_MapsToTheUserNamespacedCollectionUri()
        // TODO(pin-sync-liked-uri): best-supported guess, unconfirmed against a live capture (§5).
        => Assert.Equal("spotify:user:bob:collection", PinSyncRules.TryWireUri("liked", User));

    [Fact]
    public void LikedRoute_WithNoUsernameYet_IsNotSyncable()
    {
        Assert.Null(PinSyncRules.TryWireUri("liked", ""));
        Assert.False(PinSyncRules.IsSyncable("liked", ""));
    }

    [Theory]
    [InlineData("pl:wavee:playlist:x")]     // session-local provider — never syncs
    [InlineData("folder:6a1f2c")]
    [InlineData("home")]
    [InlineData("search")]
    [InlineData("albums")]
    [InlineData("prerelease:spotify:prerelease:1")]
    [InlineData("browse:spotify:page:music")]
    [InlineData("module:wavee:module:x:eQ")]
    [InlineData(null)]
    [InlineData("")]
    public void LocalOnlyPins_NeverProduceAWireUri(string? pinId)
    {
        Assert.Null(PinSyncRules.TryWireUri(pinId, User));
        Assert.False(PinSyncRules.IsSyncable(pinId, User));
    }

    [Theory]
    [InlineData("pl:spotify:playlist:x", true)]
    [InlineData("album:spotify:album:x", true)]
    [InlineData("artist:spotify:artist:x", true)]
    [InlineData("show:spotify:show:x", true)]
    [InlineData("liked", true)]
    [InlineData("folder:x", false)]
    [InlineData("home", false)]
    [InlineData("pl:wavee:playlist:x", false)]
    public void IsSyncable_MatchesTryWireUri(string pinId, bool expected)
        => Assert.Equal(expected, PinSyncRules.IsSyncable(pinId, User));

    [Theory]
    [InlineData("spotify:playlist:37i9dQZF1DX4sWSpwq3LiO", "pl:spotify:playlist:37i9dQZF1DX4sWSpwq3LiO")]
    [InlineData("spotify:album:4aawyAB79vO75wG7WLfDzB", "album:spotify:album:4aawyAB79vO75wG7WLfDzB")]
    [InlineData("spotify:artist:4tZwfgrHOc3mvqYlEYSvVi", "artist:spotify:artist:4tZwfgrHOc3mvqYlEYSvVi")]
    [InlineData("spotify:show:4rOoJ6Egrf8K2IrywzwOMk", "show:spotify:show:4rOoJ6Egrf8K2IrywzwOMk")]
    public void WireUris_MapBackToTheirPinId(string wireUri, string expectedId)
        => Assert.Equal(expectedId, PinSyncRules.TryPinId(wireUri));

    // Every spelling EntityUri.IsLikedCollection recognises collapses onto the one "liked" route pin (§0.1).
    [Theory]
    [InlineData("spotify:collection:tracks")]
    [InlineData("spotify:user:bob:collection")]
    [InlineData("spotify:user:bob:collection:tracks")]
    public void EveryLikedSpelling_MapsToTheLikedRoutePin(string wireUri)
        => Assert.Equal("liked", PinSyncRules.TryPinId(wireUri));

    [Theory]
    [InlineData("spotify:track:4cOdK2wGLETKBW3PvgPWqT")]   // tracks are never pinnable
    [InlineData("spotify:episode:512ojhOuo1ktJprKbVcKyQ")]  // nor episodes
    [InlineData("wavee:playlist:x")]                        // not a spotify: uri
    [InlineData("")]
    [InlineData(null)]
    public void UnpinnableOrUnrecognisedWireUris_YieldNoPinId(string? wireUri)
        => Assert.Null(PinSyncRules.TryPinId(wireUri));
}
