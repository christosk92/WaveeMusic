using Wavee.Core.Authentication;
using Wavee.Core.Connection;
using Wavee.Core.Http;

namespace Wavee.Core.Session;

/// <summary>
/// Thread-safe mutable state for an active Spotify session.
/// </summary>
/// <remarks>
/// This class uses locking to ensure thread-safe access from:
/// - Packet dispatcher thread (updating counters, timestamps)
/// - User code (reading state, calling methods)
///
/// Use ReaderWriterLockSlim for read-heavy workloads.
/// </remarks>
internal sealed class SessionData : IDisposable
{
    private readonly ReaderWriterLockSlim _lock = new();

    // Connection state
    private ApTransport? _transport;
    private string? _apUrl;
    private DateTime _connectedAt;

    // Keep-alive state
    private DateTime _lastPingSent;
    private DateTime _lastPongReceived;
    private int _missedPongs;

    // User data
    private UserData? _userData;

    // Authentication data
    private Credentials? _storedCredentials;
    private AccessToken? _accessToken;

    // Awaitable data from packets
    private readonly TaskCompletionSource<string> _countryCodeTcs = new();
    private readonly TaskCompletionSource<AccountType> _accountTypeTcs = new();

    // Lazy managers (initialized on first access)
    private readonly Lazy<object> _mercury;      // TODO: Replace with MercuryManager
    private readonly Lazy<object> _channel;      // TODO: Replace with ChannelManager
    private readonly Lazy<object> _audioKey;     // TODO: Replace with AudioKeyManager
    private readonly Lazy<Login5Client> _login5Client;

    private readonly SessionConfig _config;

    public SessionData(SessionConfig config, HttpClient httpClient, Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(httpClient);

        _config = config;

        // Lazy initialization for managers (thread-safe by default)
        _mercury = new Lazy<object>(() => CreateMercuryManager());
        _channel = new Lazy<object>(() => CreateChannelManager());
        _audioKey = new Lazy<object>(() => CreateAudioKeyManager());
        _login5Client = new Lazy<Login5Client>(() =>
            new Login5Client(httpClient, config.GetClientId(), config.DeviceId, logger));
    }

    /// <summary>
    /// Sets the active transport after successful connection.
    /// </summary>
    public void SetTransport(ApTransport? transport, string? apUrl)
    {
        _lock.EnterWriteLock();
        try
        {
            _transport?.DisposeAsync().AsTask().Wait();
            _transport = transport;
            _apUrl = apUrl;
            _connectedAt = DateTime.UtcNow;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Gets the active transport (or null if disconnected).
    /// </summary>
    public ApTransport? GetTransport()
    {
        _lock.EnterReadLock();
        try
        {
            return _transport;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Sets user data after successful authentication.
    /// </summary>
    public void SetUserData(UserData userData)
    {
        ArgumentNullException.ThrowIfNull(userData);

        _lock.EnterWriteLock();
        try
        {
            _userData = userData;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Gets current user data (or null if not authenticated).
    /// </summary>
    public UserData? GetUserData()
    {
        _lock.EnterReadLock();
        try
        {
            return _userData;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Updates keep-alive ping timestamp.
    /// </summary>
    public void RecordPingSent()
    {
        _lock.EnterWriteLock();
        try
        {
            _lastPingSent = DateTime.UtcNow;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Updates keep-alive pong timestamp and resets missed counter.
    /// </summary>
    public void RecordPongReceived()
    {
        _lock.EnterWriteLock();
        try
        {
            _lastPongReceived = DateTime.UtcNow;
            _missedPongs = 0;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Increments missed pong counter.
    /// </summary>
    public void RecordMissedPong()
    {
        _lock.EnterWriteLock();
        try
        {
            _missedPongs++;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Gets current keep-alive state.
    /// </summary>
    public (DateTime lastPingSent, DateTime lastPongReceived, int missedPongs) GetKeepAliveState()
    {
        _lock.EnterReadLock();
        try
        {
            return (_lastPingSent, _lastPongReceived, _missedPongs);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Checks if the session is connected.
    /// </summary>
    public bool IsConnected()
    {
        _lock.EnterReadLock();
        try
        {
            return _transport != null && _userData != null;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Sets the country code when received from the server (packet 0x1b).
    /// </summary>
    public void SetCountryCode(string countryCode)
    {
        _countryCodeTcs.TrySetResult(countryCode);
    }

    /// <summary>
    /// Sets the account type when received from the server (packet 0x50).
    /// </summary>
    public void SetAccountType(AccountType accountType)
    {
        _accountTypeTcs.TrySetResult(accountType);
    }

    /// <summary>
    /// Waits for the country code to be received from the server.
    /// </summary>
    /// <returns>A task that completes when the country code packet (0x1b) is received.</returns>
    public Task<string> GetCountryCodeAsync() => _countryCodeTcs.Task;

    /// <summary>
    /// Waits for the account type to be received from the server.
    /// </summary>
    /// <returns>A task that completes when the product info packet (0x50) is received.</returns>
    public Task<AccountType> GetAccountTypeAsync() => _accountTypeTcs.Task;

    /// <summary>
    /// Sets the stored credentials after successful authentication.
    /// </summary>
    public void SetStoredCredentials(Credentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        _lock.EnterWriteLock();
        try
        {
            _storedCredentials = credentials;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Gets the stored credentials (or null if not authenticated).
    /// </summary>
    public Credentials? GetStoredCredentials()
    {
        _lock.EnterReadLock();
        try
        {
            return _storedCredentials;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Sets the current access token.
    /// </summary>
    public void SetAccessToken(AccessToken token)
    {
        ArgumentNullException.ThrowIfNull(token);

        _lock.EnterWriteLock();
        try
        {
            _accessToken = token;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Gets the current access token (or null if not obtained).
    /// </summary>
    public AccessToken? GetAccessToken()
    {
        _lock.EnterReadLock();
        try
        {
            return _accessToken;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Gets the Login5Client for obtaining access tokens.
    /// </summary>
    public Login5Client GetLogin5Client()
    {
        return _login5Client.Value;
    }

    private object CreateMercuryManager()
    {
        // TODO: Create MercuryManager when implemented
        throw new NotImplementedException("Mercury manager not yet implemented");
    }

    private object CreateChannelManager()
    {
        // TODO: Create ChannelManager when implemented
        throw new NotImplementedException("Channel manager not yet implemented");
    }

    private object CreateAudioKeyManager()
    {
        // TODO: Create AudioKeyManager when implemented
        throw new NotImplementedException("Audio key manager not yet implemented");
    }

    public void Dispose()
    {
        _lock.EnterWriteLock();
        try
        {
            _transport?.DisposeAsync().AsTask().Wait();
            _transport = null;
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        _lock.Dispose();
    }
}
