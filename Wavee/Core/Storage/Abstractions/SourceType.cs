namespace Wavee.Core.Storage.Abstractions;

/// <summary>
/// Identifies the source/origin of a library item.
/// </summary>
public enum SourceType
{
    /// <summary>
    /// Track from Spotify (spotify:track:xxx).
    /// </summary>
    Spotify = 0,

    /// <summary>
    /// Local file on disk (file:///path/to/file.mp3).
    /// </summary>
    LocalFile = 1,

    /// <summary>
    /// HTTP/HTTPS stream (radio, direct URL).
    /// </summary>
    HttpStream = 2,

    /// <summary>
    /// Podcast episode from RSS feed.
    /// </summary>
    Podcast = 3
}

/// <summary>
/// Extension methods for SourceType.
/// </summary>
public static class SourceTypeExtensions
{
    /// <summary>
    /// Detects the source type from a URI string.
    /// </summary>
    public static SourceType FromUri(string uri)
    {
        if (string.IsNullOrEmpty(uri))
            return SourceType.Spotify; // Default

        if (uri.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase))
            return SourceType.Spotify;

        if (uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return SourceType.LocalFile;

        if (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return SourceType.HttpStream;

        if (uri.StartsWith("podcast:", StringComparison.OrdinalIgnoreCase))
            return SourceType.Podcast;

        // Check if it looks like a local file path
        if (uri.Contains(":\\") || uri.StartsWith("/"))
            return SourceType.LocalFile;

        return SourceType.Spotify; // Default fallback
    }

    /// <summary>
    /// Gets a display name for the source type.
    /// </summary>
    public static string GetDisplayName(this SourceType sourceType) => sourceType switch
    {
        SourceType.Spotify => "Spotify",
        SourceType.LocalFile => "Local File",
        SourceType.HttpStream => "Stream",
        SourceType.Podcast => "Podcast",
        _ => "Unknown"
    };
}
