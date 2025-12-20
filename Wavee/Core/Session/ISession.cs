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

    /// <summary>
    /// Checks if the session is connected and authenticated.
    /// </summary>
    /// <returns>True if connected and authenticated, otherwise false.</returns>
    bool IsConnected();

    /// <summary>
    /// Sends a packet to the Spotify server.
    /// </summary>
    /// <param name="packetType">The packet type.</param>
    /// <param name="payload">The payload data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask SendAsync(PacketType packetType, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the user's country code (ISO 3166-1 alpha-2).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The country code.</returns>
    Task<string> GetCountryCodeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the user's account type.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The account type.</returns>
    Task<AccountType> GetAccountTypeAsync(CancellationToken cancellationToken = default);
}
