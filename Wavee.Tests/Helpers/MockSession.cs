using Wavee.Core.Http;
using Wavee.Core.Session;

namespace Wavee.Tests.Helpers;

/// <summary>
/// Mock implementation of ISession for testing.
/// </summary>
internal class MockSession : ISession
{
    private readonly AccessToken _accessToken;

    /// <summary>
    /// Initializes a new instance of the <see cref="MockSession"/> class.
    /// </summary>
    /// <param name="accessToken">The access token to return.</param>
    /// <param name="config">Optional session configuration. If null, creates a default test config.</param>
    public MockSession(AccessToken? accessToken = null, SessionConfig? config = null)
    {
        _accessToken = accessToken ?? new AccessToken
        {
            Token = "test_access_token",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };

        Config = config ?? new SessionConfig
        {
            DeviceId = "test_device_id",
            DeviceName = "Test Device"
        };
    }

    /// <summary>
    /// Gets the session configuration.
    /// </summary>
    public SessionConfig Config { get; }

    /// <summary>
    /// Gets the configured access token.
    /// </summary>
    public Task<AccessToken> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_accessToken);
    }

    /// <summary>
    /// Gets the current preferred locale override.
    /// </summary>
    public string? GetPreferredLocale() => null;

    /// <summary>
    /// Gets current user data.
    /// </summary>
    public UserData? GetUserData() => null;
}
