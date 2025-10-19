namespace Wavee.Core.Session;

/// <summary>
/// Authenticated user data from Spotify Access Point.
/// </summary>
/// <remarks>
/// This data is returned from successful authentication and remains
/// valid for the lifetime of the session.
/// </remarks>
public sealed record UserData
{
    /// <summary>
    /// Canonical Spotify username.
    /// </summary>
    public required string Username { get; init; }

    /// <summary>
    /// ISO 3166-1 alpha-2 country code (e.g., "US", "GB", "SE").
    /// Will be null until the CountryCode packet (0x1b) is received.
    /// </summary>
    public string? CountryCode { get; init; }

    /// <summary>
    /// User's subscription tier.
    /// Will be null until the ProductInfo packet (0x50) is received.
    /// </summary>
    public AccountType? AccountType { get; init; }

    /// <summary>
    /// URL template for head files (e.g., "https://heads-fa-tls13.spotifycdn.com/head/{file_id}").
    /// Will be null until the ProductInfo packet (0x50) is received.
    /// </summary>
    public string? HeadFilesUrl { get; init; }

    /// <summary>
    /// URL template for images (e.g., "https://i.scdn.co/image/{file_id}").
    /// Will be null until the ProductInfo packet (0x50) is received.
    /// </summary>
    public string? ImageUrl { get; init; }

    /// <summary>
    /// Whether explicit content filtering is enabled.
    /// Will be false until the ProductInfo packet (0x50) is received.
    /// </summary>
    public bool FilterExplicitContent { get; init; }

    /// <summary>
    /// User's preferred locale (e.g., "en", "es", "fr").
    /// Will be null until the ProductInfo packet (0x50) is received.
    /// </summary>
    public string? PreferredLocale { get; init; }

    /// <summary>
    /// URL template for video keyframes (e.g., "http://keyframes-fa.cdn.spotify.com/keyframes/v1/sources/{source_id}/keyframe/heights/{height}/timestamps/{timestamp_ms}.jpg").
    /// Will be null until the ProductInfo packet (0x50) is received.
    /// </summary>
    public string? VideoKeyframeUrl { get; init; }
}

/// <summary>
/// Spotify account subscription tiers.
/// </summary>
public enum AccountType
{
    /// <summary>
    /// Free tier (ads, limited features).
    /// </summary>
    Free,

    /// <summary>
    /// Premium subscription (full features).
    /// </summary>
    Premium,

    /// <summary>
    /// Spotify for Artists.
    /// </summary>
    Artist,

    /// <summary>
    /// Premium Family plan.
    /// </summary>
    Family,

    /// <summary>
    /// Unknown or unrecognized account type.
    /// </summary>
    Unknown
}
