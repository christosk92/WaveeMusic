using Wavee.Core.Http;

namespace Wavee.Core.Session;

/// <summary>
/// Interface for Spotify session abstraction.
/// Enables dependency injection and mocking in tests.
/// </summary>
public interface ISession
{
    /// <summary>
    /// Gets the session configuration.
    /// </summary>
    SessionConfig Config { get; }

    /// <summary>
    /// Gets a valid access token, refreshing if necessary.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A valid access token.</returns>
    Task<AccessToken> GetAccessTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current preferred locale override (or null if using Spotify's default).
    /// </summary>
    /// <returns>The 2-character locale code or null.</returns>
    string? GetPreferredLocale();

    /// <summary>
    /// Gets current user data (or null if not authenticated).
    /// </summary>
    /// <returns>User data if authenticated, otherwise null.</returns>
    UserData? GetUserData();
}
