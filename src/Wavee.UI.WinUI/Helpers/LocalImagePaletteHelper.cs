using System;
using System.Globalization;
using System.Threading.Tasks;
using Wavee.Controls.Lyrics.Helper;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Wavee.UI.Helpers;

namespace Wavee.UI.WinUI.Helpers;

/// <summary>
/// Best-effort palette extractor for local-library artwork. Resolves a
/// <c>wavee-artwork://</c> URL to a cached file on disk, decodes it, and
/// returns a <c>#RRGGBB</c> string for the dominant accent. Used by the
/// local TV / movie detail pages to tint the <see cref="Controls.HeroHeader.HeroHeader"/>
/// color-blend layer like Spotify's server-side HeroColorHex does on the
/// online ArtistPage.
/// </summary>
internal static class LocalImagePaletteHelper
{
    public static async Task<string?> TryExtractDominantHexAsync(string? imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
            return null;

        var resolved = SpotifyImageHelper.ToHttpsUrl(imageUrl);
        if (string.IsNullOrEmpty(resolved) || !resolved.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            var path = new Uri(resolved).LocalPath;
            var file = await StorageFile.GetFileFromPathAsync(path);
            using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var result = await PaletteHelper.MedianCutGetAccentColorsFromByteAsync(decoder, 1, isDark: true);
            if (result?.Palette is { Count: > 0 } palette)
            {
                var c = palette[0];
                return string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}",
                    (byte)Math.Clamp(c.X, 0, 255),
                    (byte)Math.Clamp(c.Y, 0, 255),
                    (byte)Math.Clamp(c.Z, 0, 255));
            }
        }
        catch
        {
        }
        return null;
    }
}
