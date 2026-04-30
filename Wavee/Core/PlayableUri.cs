namespace Wavee.Core;

public enum PlayableUriKind
{
    Unknown = 0,
    SpotifyTrack,
    SpotifyAlbum,
    SpotifyArtist,
    SpotifyPlaylist,
    SpotifyShow,
    SpotifyEpisode,
    SpotifyUser,
    SpotifyImage,
    LocalTrack,
    LocalAlbum,
    LocalArtist,
    File,
    HttpStream,
    Podcast,
}

public static class PlayableUri
{
    public const string SpotifyPrefix = "spotify:";
    public const string LocalPrefix = "wavee:local:";
    public const string LocalTrackPrefix = "wavee:local:track:";
    public const string LocalAlbumPrefix = "wavee:local:album:";
    public const string LocalArtistPrefix = "wavee:local:artist:";

    public static PlayableUriKind Classify(string? uri)
    {
        if (string.IsNullOrEmpty(uri))
            return PlayableUriKind.Unknown;

        if (uri.StartsWith(LocalTrackPrefix, StringComparison.Ordinal))
            return PlayableUriKind.LocalTrack;
        if (uri.StartsWith(LocalAlbumPrefix, StringComparison.Ordinal))
            return PlayableUriKind.LocalAlbum;
        if (uri.StartsWith(LocalArtistPrefix, StringComparison.Ordinal))
            return PlayableUriKind.LocalArtist;

        if (uri.StartsWith(SpotifyPrefix, StringComparison.Ordinal))
        {
            // spotify:{type}:{id} — only the type matters here.
            int firstColon = uri.IndexOf(':', SpotifyPrefix.Length);
            if (firstColon <= SpotifyPrefix.Length)
                return PlayableUriKind.Unknown;
            ReadOnlySpan<char> type = uri.AsSpan(SpotifyPrefix.Length, firstColon - SpotifyPrefix.Length);
            return type switch
            {
                "track" => PlayableUriKind.SpotifyTrack,
                "album" => PlayableUriKind.SpotifyAlbum,
                "artist" => PlayableUriKind.SpotifyArtist,
                "playlist" => PlayableUriKind.SpotifyPlaylist,
                "show" => PlayableUriKind.SpotifyShow,
                "episode" => PlayableUriKind.SpotifyEpisode,
                "user" => PlayableUriKind.SpotifyUser,
                "image" => PlayableUriKind.SpotifyImage,
                _ => PlayableUriKind.Unknown,
            };
        }

        if (uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return PlayableUriKind.File;
        if (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return PlayableUriKind.HttpStream;
        if (uri.StartsWith("podcast:", StringComparison.OrdinalIgnoreCase))
            return PlayableUriKind.Podcast;

        return PlayableUriKind.Unknown;
    }

    public static bool IsSpotify(string? uri) => Classify(uri) is
        PlayableUriKind.SpotifyTrack or PlayableUriKind.SpotifyAlbum or
        PlayableUriKind.SpotifyArtist or PlayableUriKind.SpotifyPlaylist or
        PlayableUriKind.SpotifyShow or PlayableUriKind.SpotifyEpisode or
        PlayableUriKind.SpotifyUser or PlayableUriKind.SpotifyImage;

    public static bool IsLocal(string? uri) => Classify(uri) is
        PlayableUriKind.LocalTrack or PlayableUriKind.LocalAlbum or PlayableUriKind.LocalArtist;

    public static bool IsSpotifyTrack(string? uri) => Classify(uri) == PlayableUriKind.SpotifyTrack;
    public static bool IsLocalTrack(string? uri) => Classify(uri) == PlayableUriKind.LocalTrack;
    public static bool IsLocalAlbum(string? uri) => Classify(uri) == PlayableUriKind.LocalAlbum;
    public static bool IsLocalArtist(string? uri) => Classify(uri) == PlayableUriKind.LocalArtist;

    /// <summary>
    /// Extracts the trailing identifier from a typed URI. Returns the bare ID for both
    /// spotify:* and wavee:local:* shapes. Empty span if the URI is not recognised.
    /// </summary>
    public static ReadOnlySpan<char> ExtractId(string uri)
    {
        if (string.IsNullOrEmpty(uri)) return ReadOnlySpan<char>.Empty;
        int last = uri.LastIndexOf(':');
        if (last < 0 || last == uri.Length - 1) return ReadOnlySpan<char>.Empty;
        return uri.AsSpan(last + 1);
    }
}
