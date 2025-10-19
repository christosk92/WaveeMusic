using System.Text.Json.Serialization;

namespace Wavee.Core.OAuth;

/// <summary>
/// Represents an OAuth 2.0 access token for Spotify API authentication.
/// </summary>
/// <remarks>
/// Contains both access token (short-lived, 1 hour) and refresh token (long-lived).
/// Access tokens are used as Bearer tokens for API requests.
/// Refresh tokens can be used to obtain new access tokens without re-authorization.
/// </remarks>
public sealed record OAuthToken
{
    /// <summary>
    /// Bearer token for authenticated Spotify API requests.
    /// Valid for the duration specified in ExpiresIn (typically 3600 seconds).
    /// </summary>
    [JsonPropertyName("access_token")]
    public required string AccessToken { get; init; }

    /// <summary>
    /// Long-lived token used to obtain new access tokens without user interaction.
    /// Should be stored securely and encrypted on disk.
    /// </summary>
    [JsonPropertyName("refresh_token")]
    public required string RefreshToken { get; init; }

    /// <summary>
    /// UTC timestamp when the access token expires.
    /// </summary>
    [JsonPropertyName("expires_at")]
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// Type of token (always "Bearer" for Spotify).
    /// </summary>
    [JsonPropertyName("token_type")]
    public required string TokenType { get; init; }

    /// <summary>
    /// Space-separated list of granted OAuth scopes.
    /// </summary>
    [JsonPropertyName("scopes")]
    public required string Scopes { get; init; }

    /// <summary>
    /// Checks if the access token has expired.
    /// </summary>
    /// <returns>True if the token has passed its expiration time.</returns>
    public bool IsExpired() => DateTimeOffset.UtcNow >= ExpiresAt;

    /// <summary>
    /// Gets the time remaining until the access token expires.
    /// </summary>
    /// <returns>
    /// TimeSpan representing remaining validity, or TimeSpan.Zero if already expired.
    /// </returns>
    public TimeSpan ExpiresIn()
    {
        var remaining = ExpiresAt - DateTimeOffset.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>
    /// Checks if the access token should be refreshed proactively.
    /// </summary>
    /// <param name="threshold">
    /// Minimum remaining time before considering refresh.
    /// Defaults to 10 minutes to avoid expiration during API calls.
    /// </param>
    /// <returns>True if token expires within the threshold period.</returns>
    public bool ShouldRefresh(TimeSpan? threshold = null)
    {
        threshold ??= TimeSpan.FromMinutes(10);
        return ExpiresIn() <= threshold;
    }

    /// <summary>
    /// Creates an OAuthToken from a Spotify token response.
    /// </summary>
    /// <param name="accessToken">Access token from Spotify.</param>
    /// <param name="refreshToken">Refresh token from Spotify.</param>
    /// <param name="expiresInSeconds">Token lifetime in seconds.</param>
    /// <param name="tokenType">Token type (typically "Bearer").</param>
    /// <param name="scopes">Space-separated scope list.</param>
    /// <returns>New OAuthToken instance.</returns>
    internal static OAuthToken FromResponse(
        string accessToken,
        string refreshToken,
        int expiresInSeconds,
        string tokenType,
        string scopes)
    {
        return new OAuthToken
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds),
            TokenType = tokenType,
            Scopes = scopes
        };
    }

    /// <summary>
    /// Returns a safe string representation without exposing token values.
    /// </summary>
    public override string ToString() =>
        $"OAuthToken {{ TokenType = {TokenType}, Scopes = {Scopes}, ExpiresIn = {ExpiresIn():hh\\:mm\\:ss} }}";
}
