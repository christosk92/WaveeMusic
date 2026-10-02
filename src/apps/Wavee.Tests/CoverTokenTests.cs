// ── Wavee.Tests/CoverTokenTests.cs — the cover tokens a wire may hand over instead of a url (profile pages plan, D2) ──
//
// `CoverToken` is pure: the decoder calls its UTF-8 half on an api thread (a `spotify:image:` token becomes its CDN url
// in the image column), Controls its char half on the UI thread (a mosaic's lead tile, its four 300-px tiles).

using System.Text;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class CoverTokenTests
{
    const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaa";          // 24 hex: the part of an album image id past its 16-char prefix
    const string Album640 = "ab67616d0000b273" + Hash;
    const string Album300 = "ab67616d00001e02" + Hash;
    const string Album64 = "ab67616d00004851" + Hash;

    static string Url(string wire)
    {
        Span<byte> into = stackalloc byte[CoverToken.MaxImageUrl];
        int n = CoverToken.ImageTokenUrl(Encoding.UTF8.GetBytes(wire), into);
        return Encoding.UTF8.GetString(into[..n]);
    }

    [Fact]
    public void ImageTokenUrl_rewrites_the_token_to_its_cdn_url()
        => Assert.Equal("https://i.scdn.co/image/" + Album640, Url("spotify:image:" + Album640));

    [Theory]
    [InlineData("spotify:image:")]                                                  // no id
    [InlineData("https://i.scdn.co/image/ab67616d0000b273aaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("spotify:mosaic:ab67616d0000b273aaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("ab67616d0000b273aaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("")]
    public void ImageTokenUrl_answers_nothing_for_anything_else(string wire)
        => Assert.Equal("", Url(wire));

    [Fact]
    public void ImageTokenUrl_refuses_an_id_that_does_not_fit()
    {
        Span<byte> tiny = stackalloc byte[16];
        Assert.Equal(0, CoverToken.ImageTokenUrl(Encoding.UTF8.GetBytes("spotify:image:" + Album640), tiny));
    }

    [Theory]
    [InlineData("https://i.scdn.co/image/ab67616d0000b273aaaaaaaaaaaaaaaaaaaaaaaa", false)]
    [InlineData("https://pickasso.spotifycdn.com/image/ab67617a000051fb/x", false)]
    [InlineData("spotify:mosaic:ab67616d0000b273aaaaaaaaaaaaaaaaaaaaaaaa:b:c:d", false)]
    [InlineData("ab67616d0000b273aaaaaaaaaaaaaaaaaaaaaaaa", false)]
    [InlineData("spotify:image:ab67616d0000b273aaaaaaaaaaaaaaaaaaaaaaaa", true)]
    public void Only_a_spotify_image_token_is_rewritten_everything_else_stays_verbatim(string wire, bool token)
        => Assert.Equal(token, CoverToken.IsImageToken(Encoding.UTF8.GetBytes(wire)));

    [Fact]
    public void FileIdOf_names_the_one_file_a_value_paints()
    {
        Assert.Equal(Album640, CoverToken.FileIdOf("spotify:image:" + Album640).ToString());
        Assert.Equal(Album640, CoverToken.FileIdOf($"spotify:mosaic:{Album640}:{Album64}:x:y").ToString());   // the LEAD tile
        Assert.Equal(Album640, CoverToken.FileIdOf($"spotify:mosaic:{Album640}").ToString());                  // a lone tile
        Assert.Equal(Album640, CoverToken.FileIdOf(Album640).ToString());                                      // a bare id is itself
        Assert.True(CoverToken.FileIdOf("spotify:other:abc").IsEmpty);
        Assert.True(CoverToken.FileIdOf("spotify:user:abc").IsEmpty);
    }

    [Fact]
    public void IsMosaic_is_the_mosaic_prefix_only()
    {
        Assert.True(CoverToken.IsMosaic("spotify:mosaic:a:b:c:d"));
        Assert.False(CoverToken.IsMosaic("spotify:image:a"));
        Assert.False(CoverToken.IsMosaic("https://i.scdn.co/image/a"));
        Assert.False(CoverToken.IsMosaic(""));
    }

    [Fact]
    public void MosaicTiles_returns_four_ranges_and_skips_empty_segments()
    {
        string value = "spotify:mosaic:aa::bb:cc::dd:ee";
        Span<Range> into = stackalloc Range[4];
        int n = CoverToken.MosaicTiles(value, into);

        Assert.Equal(4, n);
        Assert.Equal("aa", value[into[0]]);
        Assert.Equal("bb", value[into[1]]);
        Assert.Equal("cc", value[into[2]]);
        Assert.Equal("dd", value[into[3]]);                // the fifth part never fits: at most into.Length

        Span<Range> wide = stackalloc Range[8];
        Assert.Equal(5, CoverToken.MosaicTiles(value, wide));
        Assert.Equal("ee", value[wide[4]]);
        Assert.Equal(0, CoverToken.MosaicTiles("spotify:image:aa", wide));
    }

    // ── MosaicTileId: the 2×2 composes 300-px album tiles (research §5) ─────────────────────────────────────────────

    static string Tile(string part)
    {
        Span<char> scratch = stackalloc char[CoverToken.ImageIdLength];
        return CoverToken.MosaicTileId(part, scratch).ToString();
    }

    [Theory]
    [InlineData(Album640, Album300)]                   // the size marker becomes 300 px, the rest of the id is kept
    [InlineData(Album64, Album300)]
    [InlineData(Album300, Album300)]                   // already the 300-px tile
    public void An_album_image_id_becomes_the_300_px_tile(string part, string expected)
        => Assert.Equal(expected, Tile(part));

    [Theory]
    [InlineData("ab6761610000e5ebaaaaaaaaaaaaaaaaaaaaaaaa")]          // an artist portrait is not an album tile
    [InlineData("ab6775700000ee85aaaaaaaaaaaaaaaaaaaaaaaa")]          // a user avatar either
    [InlineData("ab67616d0000b273aaaaaaaaaaaaaaaaaaaaaaa")]           // 39 chars
    [InlineData("ab67616d0000b273aaaaaaaaaaaaaaaaaaaaaaaaa")]         // 41 chars
    [InlineData("ab67616d0000b273aaaaaaaaaaaaaaaaaaaaaaag")]          // not hex
    [InlineData("tile-one")]
    [InlineData("")]
    public void Anything_else_passes_through_unchanged(string part)
        => Assert.Equal(part, Tile(part));

    [Fact]
    public void A_pass_through_is_the_part_itself_and_a_rewrite_never_touches_the_input()
    {
        ReadOnlySpan<char> part = Album640;
        Span<char> scratch = stackalloc char[CoverToken.ImageIdLength];
        var rewritten = CoverToken.MosaicTileId(part, scratch);
        Assert.Equal(Album300, rewritten.ToString());
        Assert.Equal(Album640, part.ToString());           // the input stands
        Assert.Equal(Album300, new string(scratch));       // the rewrite lives in the scratch

        // too small a scratch cannot hold the rewrite: the part passes through rather than overrunning
        Span<char> tiny = stackalloc char[8];
        Assert.Equal(Album640, CoverToken.MosaicTileId(Album640, tiny).ToString());
    }
}
