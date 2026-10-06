// ── Wavee.Tests/SpotifyEgressTests.cs — what may leave for Spotify (`Spotify/Spotify.Egress.cs`) ──────────────────────
//
// A local file's id is `wavee:local:file:<base64url(full path)>`: the user's folders and Windows account name. 0.3.3
// put it in a connect-state PUT. Pure: no session, no table, no interner.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class SpotifyEgressTests
{
    [Theory]
    [InlineData("spotify:track:4uLU6hMCjMI75M1A2tKUQC", true)]
    [InlineData("spotify:user:me:collection", true)]
    [InlineData("wavee:local:file:QzpcVXNlcnNcbWVcVmlkZW9zXGEubXA0", false)]
    [InlineData("wavee:local:playlist:all", false)]
    [InlineData("wavee:module:yt:abc", false)]
    [InlineData("local:track:1", false)]
    [InlineData("fake:track:1", false)]
    [InlineData("", false)]
    public void Only_spotify_uris_leave(string uri, bool admitted)
    {
        Assert.Equal(admitted, Spotify.Egress.Admits(uri.AsSpan()));
        Assert.Equal(admitted ? uri : "", Spotify.Egress.Filter(uri));
    }

    [Fact]
    public void A_catalog_gid_leaves_and_no_identity_does_not()
    {
        Assert.True(Spotify.Egress.Admits(EntityId.ForGid(EntityKind.Track, (UInt128)0x1234UL)));
        Assert.False(Spotify.Egress.Admits(default(EntityId)));
    }

    [Theory]
    [InlineData("ab67616d0000b273e8b066f70c206551210d902b", true)]
    [InlineData("https://i.scdn.co/image/ab67616d0000b273e8b066f70c206551210d902b", true)]
    [InlineData("https://mosaic.scdn.co/640/ab67", true)]
    [InlineData("spotify:image:ab67616d0000b273", true)]
    [InlineData(@"C:\Users\me\Music\cover.jpg", false)]
    [InlineData("file:///C:/Users/me/Music/cover.jpg", false)]
    [InlineData("https://i.ytimg.com/vi/x/hqdefault.jpg", false)]
    [InlineData("https://i.scdn.co.evil.example/x", false)]
    public void Only_spotify_covers_leave(string image, bool admitted)
        => Assert.Equal(admitted, Spotify.Egress.AdmitsImage(image));

    [Fact]
    public void A_local_track_is_named_by_its_tags_as_the_official_client_names_it()
    {
        Span<byte> into = stackalloc byte[Spotify.Egress.MaxLocalUriBytes];
        int n = Spotify.Egress.LocalTrackUri("Daft Punk", "Discovery", "One More Time: Edit", 320_500, into);
        Assert.Equal("spotify:local:Daft+Punk:Discovery:One+More+Time%3A+Edit:320", System.Text.Encoding.UTF8.GetString(into[..n]));

        n = Spotify.Egress.LocalTrackUri(null, "", "Café", 1_000, into);
        Assert.Equal("spotify:local:::Caf%C3%A9:1", System.Text.Encoding.UTF8.GetString(into[..n]));
    }

    [Fact]
    public void A_local_track_with_no_title_or_too_long_a_name_is_not_named()
    {
        Span<byte> into = stackalloc byte[Spotify.Egress.MaxLocalUriBytes];
        Assert.Equal(0, Spotify.Egress.LocalTrackUri("Artist", "Album", "", 1_000, into));
        Assert.Equal(0, Spotify.Egress.LocalTrackUri("Artist", "Album", new string('é', 400), 1_000, into));
    }
}
