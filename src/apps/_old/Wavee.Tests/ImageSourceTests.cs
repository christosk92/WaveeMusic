using System.Collections.Generic;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

public sealed class ImageSourceTests
{
    [Fact]
    public void Image_NormalizesSpotifyImageToken_OnConstruction()
    {
        var image = new Image(" spotify:image:ab67616d00001e02870c1c64b1d77eb4456e4283 ", 300, 300);

        Assert.Equal("https://i.scdn.co/image/ab67616d00001e02870c1c64b1d77eb4456e4283", image.Url);
        Assert.Equal(ImageSourceQuality.Usable, ImageSource.Quality(image));
    }

    [Fact]
    public void Image_NormalizesSpotifyImageToken_OnWithUpdate()
    {
        var image = new Image("https://i.scdn.co/image/old") with { Url = "spotify:image:new" };

        Assert.Equal("https://i.scdn.co/image/new", image.Url);
    }

    [Fact]
    public void Image_NormalizesLargestSpotifyImageToken()
    {
        var image = new Image("https://i.scdn.co/image/card", LargestUrl: " spotify:image:largest ");

        Assert.Equal("https://i.scdn.co/image/largest", image.LargestUrl);
        Assert.Equal("https://i.scdn.co/image/card", ImageSource.UrlFor(image, preferLargest: false));
        Assert.Equal("https://i.scdn.co/image/largest", ImageSource.UrlFor(image, preferLargest: true));
    }

    [Fact]
    public void Image_NormalizesMosaicTiles()
    {
        var image = new Image("", MosaicTiles: new List<string>
        {
            "spotify:image:first",
            " https://i.scdn.co/image/second ",
        });

        Assert.NotNull(image.MosaicTiles);
        Assert.Equal("https://i.scdn.co/image/first", image.MosaicTiles![0]);
        Assert.Equal("https://i.scdn.co/image/second", image.MosaicTiles[1]);
        Assert.Equal(ImageSourceQuality.Usable, ImageSource.Quality(image));
    }

    [Fact]
    public void ImageSource_Quality_ClassifiesRawSources()
    {
        Assert.Equal(ImageSourceQuality.None, ImageSource.Quality(""));
        Assert.Equal(ImageSourceQuality.None, ImageSource.Quality("   "));
        Assert.Equal(ImageSourceQuality.Unresolved, ImageSource.Quality("spotify:image:abc"));
        Assert.Equal(ImageSourceQuality.Usable, ImageSource.Quality("https://i.scdn.co/image/abc"));
    }

    [Fact]
    public void SameSource_MatchesNormalizedUrls_CaseInsensitive()
    {
        var a = new Image("https://i.scdn.co/image/AbC");
        var b = new Image("spotify:image:abc");
        Assert.True(ImageSource.SameSource(a, b));
        Assert.False(ImageSource.SameSource(a, new Image("https://i.scdn.co/image/other")));
    }

    // Real id shapes: 40 hex = 16-char size/kind marker + 24-char art identity (CoverColorPlane keys on the same tail).
    const string Art = "a149cc5f2c8074884fc06a80";
    const string Card300 = "https://i.scdn.co/image/ab67616d00001e02" + Art;
    const string Hero640 = "https://i.scdn.co/image/ab67616d0000b273" + Art;
    const string OtherArt = "https://i.scdn.co/image/ab67616d0000b27392144c5952844a7c0086b141";

    [Fact]
    public void ArtIdentity_IsTheSizeIndependentTail_OfASpotifyImageId()
    {
        Assert.Equal(Art, ImageSource.ArtIdentity(Card300).ToString());
        Assert.Equal(Art, ImageSource.ArtIdentity(Hero640).ToString());
        Assert.Equal(Art, ImageSource.ArtIdentity("spotify:image:ab67616d00004851" + Art).ToString());
        // Anything that is not the 40-char provider form keys on itself (the whole last segment).
        Assert.Equal("cover.png", ImageSource.ArtIdentity("https://example.com/art/cover.png").ToString());
        Assert.True(ImageSource.ArtIdentity(null).IsEmpty);
        Assert.True(ImageSource.ArtIdentity("").IsEmpty);
    }

    [Fact]
    public void SameArt_MatchesAcrossSizes_ButNotAcrossCovers()
    {
        var card = new Image(Card300, 300, 300);
        var hero = new Image(Hero640, 640, 640);
        Assert.True(ImageSource.SameArt(card, hero));
        Assert.True(ImageSource.SameArt(hero, card));
        Assert.True(ImageSource.SameArt(card, new Image("spotify:image:ab67616d00004851" + Art)));   // token form
        Assert.False(ImageSource.SameArt(card, new Image(OtherArt)));
        Assert.False(ImageSource.SameArt(card, null));
        Assert.False(ImageSource.SameArt(null, hero));
        // Non-Spotify urls: identity is the exact url (SameSource), never a 24-char slice.
        Assert.True(ImageSource.SameArt(new Image("https://example.com/a/cover.png"), new Image("https://example.com/a/cover.png")));
        Assert.False(ImageSource.SameArt(new Image("https://example.com/a/cover.png"), new Image("https://example.com/b/cover.png")));
    }

    [Fact]
    public void PreferVisible_KeepsAlreadyShownCover_WhenLoadedUrlIsTheSameArtAtAnotherSize()
    {
        // Nav preview (card CDN size) vs detail payload (largest CDN size) — SAME art, different size hash. Keeping the
        // visible rendition is what stops the hero re-decoding and re-fading the picture it already shows.
        var visible = new Image(Card300, 300, 300);
        var incoming = new Image(Hero640, 640, 640);

        Image? chosen = ImageSource.PreferVisible(incoming, visible);

        Assert.NotNull(chosen);
        Assert.Equal(visible.Url, chosen!.Url);
        Assert.Equal(incoming.Url, chosen.LargestUrl);
        Assert.Equal(visible.Url, ImageSource.UrlFor(chosen, preferLargest: false));
        Assert.Equal(incoming.Url, ImageSource.UrlFor(chosen, preferLargest: true));
    }

    [Fact]
    public void PreferVisible_TakesIncoming_WhenItIsDifferentArt()
    {
        // A playlist whose cover was edited / a daylist that rolled over: the NEW cover must show.
        var visible = new Image(Card300, 300, 300);
        var incoming = new Image(OtherArt, 640, 640);
        Assert.Same(incoming, ImageSource.PreferVisible(incoming, visible));
    }

    [Fact]
    public void PreferVisible_IsIdempotent_OnRepeatedPublishes()
    {
        // The latch runs on every publish (initial merge AND live refresh); re-applying it to its own output is a no-op.
        var visible = new Image(Card300, 300, 300);
        var incoming = new Image(Hero640, 640, 640);
        Image? once = ImageSource.PreferVisible(incoming, visible);
        Image? twice = ImageSource.PreferVisible(incoming, once);
        Assert.Equal(once!.Url, twice!.Url);
        Assert.Equal(once.LargestUrl, twice.LargestUrl);
    }

    [Fact]
    public void PreferVisible_UsesIncoming_WhenVisibleMissingOrUnusable()
    {
        var incoming = new Image("https://i.scdn.co/image/fullsize", 640, 640);
        Assert.Same(incoming, ImageSource.PreferVisible(incoming, null));
        // Empty Image is unusable (Url normalizes spotify:image: tokens to CDN urls at construction).
        Assert.Same(incoming, ImageSource.PreferVisible(incoming, new Image("")));
    }

    [Fact]
    public void PreferVisible_KeepsVisible_WhenIncomingMissing()
    {
        var visible = new Image("https://i.scdn.co/image/cardsize");
        Assert.Same(visible, ImageSource.PreferVisible(null, visible));
        Assert.Same(visible, ImageSource.PreferVisible(new Image(""), visible));
    }

    [Fact]
    public void PreferVisible_SameSource_MergesBlurHashFromIncoming()
    {
        var visible = new Image("https://i.scdn.co/image/same", 300, 300);
        var incoming = new Image("https://i.scdn.co/image/same", 640, 640, BlurHash: "LGF5]+Yk^6#M@-5c,1J5@[or[Q6.");

        Image? chosen = ImageSource.PreferVisible(incoming, visible);

        Assert.NotNull(chosen);
        Assert.Equal(visible.Url, chosen!.Url);
        Assert.Equal(incoming.BlurHash, chosen.BlurHash);
        Assert.Equal(300, chosen.Width); // keep the already-decoded size metadata
        Assert.Equal(incoming.Url, chosen.LargestUrl);
    }
}
