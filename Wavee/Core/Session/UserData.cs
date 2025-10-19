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
