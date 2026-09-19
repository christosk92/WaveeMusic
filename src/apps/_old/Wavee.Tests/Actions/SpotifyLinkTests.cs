using System;
using Wavee;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests.Actions;

// The consolidated spotify-uri → open.spotify.com converter (Actions/SpotifyLink.cs) — the uri→url matrix, the
// non-spotify null cases, and the multi-track newline join behind "Copy links (N)".
public class SpotifyLinkTests
{
    [Theory]
    [InlineData("spotify:track:4uLU6hMCjMI75M1A2tKUQC", "https://open.spotify.com/track/4uLU6hMCjMI75M1A2tKUQC")]
    [InlineData("spotify:album:41b0hsQwhVkMc3NQcvB0NF", "https://open.spotify.com/album/41b0hsQwhVkMc3NQcvB0NF")]
    [InlineData("spotify:artist:7hr9W3IjXcm3UlLY7guLk5", "https://open.spotify.com/artist/7hr9W3IjXcm3UlLY7guLk5")]
    [InlineData("spotify:playlist:0dijb70Boi9TIdmiLLq13V", "https://open.spotify.com/playlist/0dijb70Boi9TIdmiLLq13V")]
    [InlineData("spotify:collection:tracks", "https://open.spotify.com/collection/tracks")]
    [InlineData("spotify:user:alice:playlist:abc", "https://open.spotify.com/user/alice/playlist/abc")]
    public void WebUrl_SpotifyUris_MapToOpenSpotify(string uri, string expected)
        => Assert.Equal(expected, SpotifyLink.WebUrl(uri));

    [Theory]
    [InlineData("wavee:local:track:1")]
    [InlineData("wavee:playlist:p1")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("https://open.spotify.com/track/x")]   // already a url — not a spotify: uri
    public void WebUrl_NonSpotify_IsNull(string? uri)
        => Assert.Null(SpotifyLink.WebUrl(uri));

    [Fact]
    public void LinkText_SingleTrack_IsItsUrl()
    {
        var target = ActionTarget.ForTracks(new[] { T.Mk("a") });
        Assert.Equal("https://open.spotify.com/track/a", SpotifyLink.LinkText(in target));
    }

    [Fact]
    public void LinkText_MultiTracks_JoinsWithNewline_SkippingNonSpotify()
    {
        var target = ActionTarget.ForTracks(new[]
        {
            T.Mk("a"),
            T.Mk("local", uriOverride: "wavee:local:track:1"),   // skipped — no web url
            T.Mk("b"),
        });
        Assert.Equal("https://open.spotify.com/track/a\nhttps://open.spotify.com/track/b", SpotifyLink.LinkText(in target));
    }

    [Fact]
    public void LinkText_ContainerTarget_FallsBackToItsUri()
    {
        var target = ActionTarget.ForPlaylist("spotify:playlist:p1", "P");
        Assert.Equal("https://open.spotify.com/playlist/p1", SpotifyLink.LinkText(in target));
    }

    [Fact]
    public void HasLink_FalseForLocalOnlyTracks_AndWaveePlaylists()
    {
        var local = ActionTarget.ForTracks(new[] { T.Mk("x", uriOverride: "wavee:local:track:1") });
        Assert.False(SpotifyLink.HasLink(in local));
        Assert.Null(SpotifyLink.LinkText(in local));

        var waveePl = ActionTarget.ForPlaylist("wavee:playlist:p", "Local list");
        Assert.False(SpotifyLink.HasLink(in waveePl));
    }

    [Fact]
    public void HasLink_TrueWhenAnyTrackIsSpotify()
    {
        var mixed = ActionTarget.ForTracks(new[] { T.Mk("x", uriOverride: "wavee:local:track:1"), T.Mk("y") });
        Assert.True(SpotifyLink.HasLink(in mixed));
    }

    // ── SingleUri: the raw-uri the Share submenu's "Copy Spotify URI" / "Open in Spotify Web" variants act on. Non-null
    // ONLY for a single shareable spotify entity — which is exactly WHEN those two Share rows appear (multi-select and
    // non-spotify targets collapse the Share submenu to just "Copy link(s)"). ─────────────────────────────────────────
    [Fact]
    public void SingleUri_SingleSpotifyTrack_IsItsUri()
    {
        var target = ActionTarget.ForTracks(new[] { T.Mk("a") });
        Assert.Equal("spotify:track:a", SpotifyLink.SingleUri(in target));
    }

    [Fact]
    public void SingleUri_MultiSelect_IsNull()   // uri/web Share variants are single-target only
    {
        var target = ActionTarget.ForTracks(new[] { T.Mk("a"), T.Mk("b") });
        Assert.Null(SpotifyLink.SingleUri(in target));
    }

    [Fact]
    public void SingleUri_SingleNonSpotifyTrack_IsNull()
    {
        var target = ActionTarget.ForTracks(new[] { T.Mk("x", uriOverride: "wavee:local:track:1") });
        Assert.Null(SpotifyLink.SingleUri(in target));
    }

    [Fact]
    public void SingleUri_SpotifyContainer_IsItsUri()
    {
        var artist = ActionTarget.ForArtist("spotify:artist:y", "B");
        Assert.Equal("spotify:artist:y", SpotifyLink.SingleUri(in artist));

        var playlist = ActionTarget.ForPlaylist("spotify:playlist:p1", "P");
        Assert.Equal("spotify:playlist:p1", SpotifyLink.SingleUri(in playlist));
    }

    [Fact]
    public void SingleUri_NonSpotifyContainer_IsNull()
    {
        var waveePl = ActionTarget.ForPlaylist("wavee:playlist:p", "Local list");
        Assert.Null(SpotifyLink.SingleUri(in waveePl));
    }
}
