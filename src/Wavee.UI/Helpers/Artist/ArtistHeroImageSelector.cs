namespace Wavee.UI.Helpers.Artist;

public sealed record ArtistHeroImageSelection(
    string? BackdropImageUrl,
    string? FallbackImageUrl);

public static class ArtistHeroImageSelector
{
    public static ArtistHeroImageSelection Select(
        string? headerImageUrl,
        string? galleryHeroUrl,
        string? avatarImageUrl)
    {
        var header = Normalize(headerImageUrl);
        if (header is not null)
            return new ArtistHeroImageSelection(header, null);

        var gallery = Normalize(galleryHeroUrl);
        if (gallery is not null)
            return new ArtistHeroImageSelection(gallery, null);

        return new ArtistHeroImageSelection(null, Normalize(avatarImageUrl));
    }

    private static string? Normalize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        return url.Trim();
    }
}
