using FluentAssertions;
using Wavee.UI.Helpers.Artist;

namespace Wavee.UI.Tests.Helpers.Artist;

public sealed class ArtistHeroImageSelectorTests
{
    [Fact]
    public void Select_UsesHeaderBeforeGalleryAndAvatar()
    {
        var selection = ArtistHeroImageSelector.Select(
            " header ",
            "gallery",
            "avatar");

        selection.BackdropImageUrl.Should().Be("header");
        selection.FallbackImageUrl.Should().BeNull();
    }

    [Fact]
    public void Select_UsesGalleryBeforeAvatar_WhenHeaderMissing()
    {
        var selection = ArtistHeroImageSelector.Select(
            null,
            " gallery ",
            "avatar");

        selection.BackdropImageUrl.Should().Be("gallery");
        selection.FallbackImageUrl.Should().BeNull();
    }

    [Fact]
    public void Select_UsesAvatarAsFallback_WhenHeaderAndGalleryMissing()
    {
        var selection = ArtistHeroImageSelector.Select(
            null,
            "   ",
            " avatar ");

        selection.BackdropImageUrl.Should().BeNull();
        selection.FallbackImageUrl.Should().Be("avatar");
    }

    [Fact]
    public void Select_IgnoresWhitespaceOnlyValues()
    {
        var selection = ArtistHeroImageSelector.Select(
            " ",
            "\t",
            "\r\n");

        selection.BackdropImageUrl.Should().BeNull();
        selection.FallbackImageUrl.Should().BeNull();
    }
}
