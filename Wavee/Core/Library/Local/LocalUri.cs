using System.Buffers;

namespace Wavee.Core.Library.Local;

public enum LocalUriKind
{
    None = 0,
    Track,
    Album,
    Artist,
}

/// <summary>
/// Helpers for building and parsing the <c>wavee:local:{kind}:{40-hex}</c> URI scheme.
/// All hashes are 40-character lowercase SHA-1 hex.
/// </summary>
public static class LocalUri
{
    public const string TrackPrefix = "wavee:local:track:";
    public const string AlbumPrefix = "wavee:local:album:";
    public const string ArtistPrefix = "wavee:local:artist:";
    public const int HashLength = 40;

    public static string BuildTrack(string hash) => Validate(TrackPrefix, hash);
    public static string BuildAlbum(string hash) => Validate(AlbumPrefix, hash);
    public static string BuildArtist(string hash) => Validate(ArtistPrefix, hash);

    public static bool TryParse(string? uri, out LocalUriKind kind, out string hash)
    {
        kind = LocalUriKind.None;
        hash = string.Empty;
        if (string.IsNullOrEmpty(uri)) return false;

        string? prefix = null;
        if (uri.StartsWith(TrackPrefix, StringComparison.Ordinal)) { prefix = TrackPrefix; kind = LocalUriKind.Track; }
        else if (uri.StartsWith(AlbumPrefix, StringComparison.Ordinal)) { prefix = AlbumPrefix; kind = LocalUriKind.Album; }
        else if (uri.StartsWith(ArtistPrefix, StringComparison.Ordinal)) { prefix = ArtistPrefix; kind = LocalUriKind.Artist; }

        if (prefix is null) return false;
        if (uri.Length != prefix.Length + HashLength) { kind = LocalUriKind.None; return false; }

        for (int i = prefix.Length; i < uri.Length; i++)
        {
            char c = uri[i];
            bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!ok) { kind = LocalUriKind.None; return false; }
        }

        hash = uri.Substring(prefix.Length);
        return true;
    }

    public static bool IsTrack(string? uri) => uri is not null && uri.StartsWith(TrackPrefix, StringComparison.Ordinal);
    public static bool IsAlbum(string? uri) => uri is not null && uri.StartsWith(AlbumPrefix, StringComparison.Ordinal);
    public static bool IsArtist(string? uri) => uri is not null && uri.StartsWith(ArtistPrefix, StringComparison.Ordinal);
    public static bool IsAny(string? uri) => IsTrack(uri) || IsAlbum(uri) || IsArtist(uri);

    private static string Validate(string prefix, string hash)
    {
        if (hash is null) throw new ArgumentNullException(nameof(hash));
        if (hash.Length != HashLength)
            throw new ArgumentException($"Hash must be {HashLength} chars; got {hash.Length}.", nameof(hash));
        for (int i = 0; i < hash.Length; i++)
        {
            char c = hash[i];
            bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!ok)
                throw new ArgumentException("Hash must be lowercase hex.", nameof(hash));
        }
        return string.Concat(prefix, hash);
    }
}
