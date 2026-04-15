using System.Reactive.Linq;
using Wavee.Core.Http;
using Wavee.Core.Session;
using Wavee.Core.Time;

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

    /// <summary>
    /// Checks if the session is connected.
    /// </summary>
    public bool IsConnected() => true;

    /// <summary>
    /// Sends a packet (no-op for mock).
    /// </summary>
    public ValueTask SendAsync(PacketType packetType, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Gets the user's country code.
    /// </summary>
    public Task<string> GetCountryCodeAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult("US");
    }

    /// <summary>
    /// Gets the user's account type.
    /// </summary>
    public Task<AccountType> GetAccountTypeAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(AccountType.Premium);
    }

    /// <summary>
    /// Reconnects to Spotify AP (no-op for mock).
    /// </summary>
    public Task ReconnectApAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public IPathfinderClient Pathfinder => throw new NotImplementedException("Mock does not support Pathfinder");

    public ISpClient SpClient => throw new NotImplementedException("Mock does not support SpClient");

    // A real SpotifyClockService instance (not Started, so it just reads local time + an
    // offset of 0). Tests that call Clock.NowMs get the local Unix-ms timestamp, which is
    // what PlaybackStateManager needs for PutState timestamps.
    private SpotifyClockService? _clock;
    public SpotifyClockService Clock => _clock ??= new SpotifyClockService(spClient: null!);

    public IObservable<SessionConnectionState> ConnectionState =>
        Observable.Return(SessionConnectionState.Connected);
}
