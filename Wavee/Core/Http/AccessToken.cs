namespace Wavee.Core.Http;

/// <summary>
/// Access token for SpClient HTTP API.
/// </summary>
/// <remarks>
/// Obtained from login5 by exchanging stored credentials.
/// Typically expires after 1 hour and must be refreshed.
/// </remarks>
public sealed record AccessToken
{
    /// <summary>
    /// Bearer token string.
    /// </summary>
    public required string Token { get; init; }

    /// <summary>
    /// Token type (always "Bearer" for Spotify).
    /// </summary>
    public string TokenType { get; init; } = "Bearer";

    /// <summary>
    /// Absolute expiration time (UTC).
    /// </summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// Checks if the token is expired.
    /// </summary>
    /// <returns>True if the token has expired, false otherwise.</returns>
    public bool IsExpired() => DateTimeOffset.UtcNow >= ExpiresAt;

    /// <summary>
    /// Checks if the token should be refreshed (with buffer time before expiry).
    /// </summary>
    /// <param name="threshold">Time before expiry to trigger refresh. Defaults to 5 minutes.</param>
    /// <returns>True if the token should be refreshed, false otherwise.</returns>
    public bool ShouldRefresh(TimeSpan? threshold = null)
    {
        var refreshThreshold = threshold ?? TimeSpan.FromMinutes(5);
        return DateTimeOffset.UtcNow >= ExpiresAt - refreshThreshold;
    }

    /// <summary>
    /// Creates an AccessToken from login5 response.
    /// </summary>
    /// <param name="token">The access token string.</param>
    /// <param name="expiresInSeconds">Token lifetime in seconds.</param>
    /// <returns>A new AccessToken instance.</returns>
    internal static AccessToken FromLogin5Response(string token, int expiresInSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        return new AccessToken
        {
            Token = token,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds)
        };
    }

    /// <summary>
    /// Returns a string representation of the token without revealing the full token value.
    /// </summary>
    /// <returns>Safe string representation for logging.</returns>
    public override string ToString()
    {
        var tokenPreview = Token.Length > 10 ? $"{Token[..10]}..." : Token;
        return $"AccessToken {{ Token = {tokenPreview}, ExpiresAt = {ExpiresAt}, Expired = {IsExpired()} }}";
    }
}
