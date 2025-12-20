namespace Wavee.Core.Storage;

/// <summary>
/// Type of Spotify entity stored in the metadata database.
/// </summary>
public enum EntityType
{
    /// <summary>Unknown or unrecognized entity type.</summary>
    Unknown = 0,

    /// <summary>Music track.</summary>
    Track = 1,

    /// <summary>Music album.</summary>
    Album = 2,

    /// <summary>Artist.</summary>
    Artist = 3,

    /// <summary>Podcast episode.</summary>
    Episode = 4,

    /// <summary>Podcast show.</summary>
    Show = 5,

    /// <summary>Playlist.</summary>
    Playlist = 6,

    /// <summary>User profile.</summary>
    User = 7
}

/// <summary>
/// Extension methods for EntityType.
/// </summary>
public static class EntityTypeExtensions
{
    /// <summary>
    /// Parses entity type from a Spotify URI.
    /// </summary>
    /// <param name="uri">Spotify URI (e.g., "spotify:track:xxx").</param>
    /// <returns>The entity type, or Unknown if not recognized.</returns>
    public static EntityType ParseFromUri(string uri)
    {
        if (string.IsNullOrEmpty(uri))
            return EntityType.Unknown;

        // URI format: spotify:{type}:{id}
        var parts = uri.Split(':');
        if (parts.Length < 2)
            return EntityType.Unknown;

        return parts[1].ToLowerInvariant() switch
        {
            "track" => EntityType.Track,
            "album" => EntityType.Album,
            "artist" => EntityType.Artist,
            "episode" => EntityType.Episode,
            "show" => EntityType.Show,
            "playlist" => EntityType.Playlist,
            "user" => EntityType.User,
            _ => EntityType.Unknown
        };
    }

    /// <summary>
    /// Gets the URI type string for this entity type.
    /// </summary>
    public static string ToUriType(this EntityType type)
    {
        return type switch
        {
            EntityType.Track => "track",
            EntityType.Album => "album",
            EntityType.Artist => "artist",
            EntityType.Episode => "episode",
            EntityType.Show => "show",
            EntityType.Playlist => "playlist",
            EntityType.User => "user",
            _ => "unknown"
        };
    }
}
