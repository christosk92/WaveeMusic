using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Wavee.Core.Library.Local;

/// <summary>
/// Normalisation + hash helpers used to derive album/artist URIs from tag metadata.
/// "The Beatles" and "the beatles  " collapse to the same artist; an album collapses
/// across all files that share <c>{albumArtist, album, year}</c>.
/// </summary>
internal static class LocalNormalize
{
    public static string Artist(string? albumArtist, string? trackArtist)
    {
        var name = !string.IsNullOrWhiteSpace(albumArtist) ? albumArtist!
                 : !string.IsNullOrWhiteSpace(trackArtist) ? trackArtist!
                 : "Unknown Artist";
        return Compact(name);
    }

    public static string Album(string? album)
    {
        return string.IsNullOrWhiteSpace(album) ? "Unknown Album" : Compact(album);
    }

    public static string ArtistUri(string artistName)
    {
        var key = Encoding.UTF8.GetBytes(Compact(artistName));
        return LocalUri.BuildArtist(Sha1Hex(key));
    }

    public static string AlbumUri(string artistName, string albumName, int? year)
    {
        var key = $"{Compact(artistName)}\0{Compact(albumName)}\0{(year ?? 0)}";
        return LocalUri.BuildAlbum(Sha1Hex(Encoding.UTF8.GetBytes(key)));
    }

    /// <summary>NFKC + diacritic strip + lowercase + whitespace collapse.</summary>
    public static string Compact(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        var nfkc = input.Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(nfkc.Length);
        bool lastWasSpace = false;
        foreach (var c in nfkc)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0 && !lastWasSpace) { sb.Append(' '); lastWasSpace = true; }
                continue;
            }
            sb.Append(char.ToLowerInvariant(c));
            lastWasSpace = false;
        }
        return sb.ToString().Trim();
    }

    private static string Sha1Hex(byte[] bytes)
    {
        var hash = SHA1.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
