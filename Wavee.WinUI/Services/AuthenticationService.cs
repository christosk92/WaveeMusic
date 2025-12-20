using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Wavee.Core.Authentication;
using Wavee.Core.Session;
using Wavee.OAuth;

namespace Wavee.WinUI.Services;

/// <summary>
/// Service for managing authentication state and session lifecycle in the application.
/// </summary>
/// <remarks>
/// This service manages the entire authentication lifecycle:
/// - Auto-login from cached credentials on startup
/// - OAuth login flow with browser
/// - Session management (singleton instance)
/// - Credential caching with DPAPI encryption
/// - Logout and credential clearing
///
/// Register as singleton in DI container.
/// </remarks>
public sealed class AuthenticationService : ObservableObject, IAuthenticationService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AuthenticationService> _logger;
    private readonly ICredentialsCache _credentialsCache;
    private readonly SessionConfig _sessionConfig;
    private readonly OAuthClient _oauthClient;

    private Session? _session;
    private UserData? _currentUser;
    private bool _isAuthenticated;
    private OAuthClient? _deviceCodeClient;
    private CancellationTokenSource? _deviceCodeCts;

    /// <inheritdoc/>
    public bool IsAuthenticated
    {
        get => _isAuthenticated;
        private set => SetProperty(ref _isAuthenticated, value);
    }

    /// <inheritdoc/>
    public UserData? CurrentUser
    {
        get => _currentUser;
        private set => SetProperty(ref _currentUser, value);
    }

    /// <inheritdoc/>
    public ISession? Session => _session;

    /// <inheritdoc/>
    public event EventHandler<AuthenticationStateChangedEventArgs>? AuthenticationStateChanged;

    /// <summary>
    /// Creates a new authentication service instance.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory for creating HttpClient instances.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public AuthenticationService(
        IHttpClientFactory httpClientFactory,
        ILogger<AuthenticationService> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Initialize credentials cache (default location: %APPDATA%/Wavee/credentials)
        _credentialsCache = new CredentialsCache(logger: logger);

        // Create session configuration
        // Generate a stable device ID based on machine name + user
        var deviceId = GenerateDeviceId();
        _sessionConfig = new SessionConfig
        {
            DeviceId = deviceId,
            DeviceName = GetDeviceName(),
            DeviceType = DeviceType.Computer,
            EnableConnect = true,
            InitialVolume = 32767 // 50%
        };

        _logger.LogInformation("Session config: DeviceId={DeviceId}, DeviceName={DeviceName}",
            _sessionConfig.DeviceId, _sessionConfig.DeviceName);

        // Create OAuth client for login flow
        // Using Spotify's official scopes for full functionality
        var scopes = new[]
        {
            "streaming",
            "user-read-playback-state",
            "user-modify-playback-state",
            "user-read-currently-playing",
            "user-read-private",
            "user-read-email",
            "user-library-read",
            "user-library-modify",
            "playlist-read-private",
            "playlist-read-collaborative",
            "playlist-modify-public",
            "playlist-modify-private",
            "user-follow-read",
            "user-follow-modify",
            "user-top-read",
            "user-read-recently-played"
        };

        _oauthClient = OAuthClient.Create(
            clientId: _sessionConfig.GetClientId(),
            scopes: scopes,
            openBrowser: true,
            logger: logger);
    }

    /// <inheritdoc/>
    public async Task<bool> InitializeAsync()
    {
        _logger.LogInformation("Initializing authentication service");

        try
        {
            // Try to load cached credentials
            var cachedCredentials = await _credentialsCache.LoadCredentialsAsync();

            if (cachedCredentials == null)
            {
                _logger.LogInformation("No cached credentials found, user needs to login");
                return false;
            }

            _logger.LogInformation("Found cached credentials for user: {Username}", cachedCredentials.Username ?? "<unknown>");

            // Create session
            _session = Wavee.Core.Session.Session.Create(_sessionConfig, _httpClientFactory, _logger);

            // Try to connect with cached credentials
            await _session.ConnectAsync(cachedCredentials, _credentialsCache, CancellationToken.None);

            // Get user data
            _currentUser = _session.GetUserData();

            // Update state
            IsAuthenticated = true;
            CurrentUser = _currentUser;

            _logger.LogInformation("Auto-login succeeded for user: {Username}", _currentUser?.Username);

            // Raise authentication state changed event
            RaiseAuthenticationStateChanged(true, _currentUser);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-login failed, clearing invalid credentials");

            // Clear invalid credentials
            await _credentialsCache.ClearCredentialsAsync();

            // Cleanup session if it was created
            if (_session != null)
            {
                await _session.DisposeAsync();
                _session = null;
            }

            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> LoginAsync()
    {
        _logger.LogInformation("Starting OAuth login flow");

        try
        {
            // Cancel any ongoing device code flow
            await CancelDeviceCodeFlowAsync();

            // Execute OAuth flow (opens browser)
            var oauthToken = await _oauthClient.GetAccessTokenAsync();

            _logger.LogInformation("OAuth token obtained, creating session");

            // Create credentials from OAuth token
            var credentials = Credentials.WithAccessToken(oauthToken.AccessToken);

            // Dispose existing session if any
            if (_session != null)
            {
                await _session.DisposeAsync();
                _session = null;
            }

            // Create new session
            _session = Wavee.Core.Session.Session.Create(_sessionConfig, _httpClientFactory, _logger);

            // Connect with OAuth credentials (this will save reusable credentials to cache)
            await _session.ConnectAsync(credentials, _credentialsCache, CancellationToken.None);

            // Get user data
            _currentUser = _session.GetUserData();

            // Update state
            IsAuthenticated = true;
            CurrentUser = _currentUser;

            _logger.LogInformation("Login succeeded for user: {Username}", _currentUser?.Username);

            // Raise authentication state changed event
            RaiseAuthenticationStateChanged(true, _currentUser);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login failed");

            // Cleanup session if it was created
            if (_session != null)
            {
                await _session.DisposeAsync();
                _session = null;
            }

            // Reset state
            IsAuthenticated = false;
            CurrentUser = null;

            return false;
        }
    }

    /// <inheritdoc/>
    public async Task LogoutAsync()
    {
        _logger.LogInformation("Logging out user: {Username}", _currentUser?.Username);

        try
        {
            // Dispose session
            if (_session != null)
            {
                await _session.DisposeAsync();
                _session = null;
            }

            // Clear cached credentials
            await _credentialsCache.ClearCredentialsAsync();

            // Reset state
            IsAuthenticated = false;
            CurrentUser = null;

            _logger.LogInformation("Logout completed");

            // Raise authentication state changed event
            RaiseAuthenticationStateChanged(false, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during logout");
            throw;
        }
    }

    /// <inheritdoc/>
    public Task<ISession> GetSessionAsync()
    {
        if (!IsAuthenticated || _session == null)
        {
            throw new InvalidOperationException("Not authenticated. Call LoginAsync() first.");
        }

        return Task.FromResult<ISession>(_session);
    }

    /// <summary>
    /// Starts the device code flow and returns the user code and verification URL for display.
    /// </summary>
    /// <returns>Tuple containing (userCode, verificationUrl) for QR code generation.</returns>
    public async Task<(string userCode, string verificationUrl)> StartDeviceCodeFlowAsync()
    {
        _logger.LogInformation("Starting device code flow");

        try
        {
            // Cancel any existing device code flow
            await CancelDeviceCodeFlowAsync();

            // Create cancellation token source for this flow
            _deviceCodeCts = new CancellationTokenSource();

            // Create device code OAuth client
            var scopes = new[]
            {
                "streaming",
                "user-read-playback-state",
                "user-modify-playback-state",
                "user-read-currently-playing",
                "user-read-private",
                "user-read-email",
                "user-library-read",
                "user-library-modify",
                "playlist-read-private",
                "playlist-read-collaborative",
                "playlist-modify-public",
                "playlist-modify-private",
                "user-follow-read",
                "user-follow-modify",
                "user-top-read",
                "user-read-recently-played"
            };

            _deviceCodeClient = OAuthClient.CreateCustom(
                clientId: _sessionConfig.GetClientId(),
                scopes: scopes,
                flow: OAuthFlow.DeviceCode,
                logger: _logger);

            // Capture device code info
            string? userCode = null;
            string? verificationUrl = null;
            var deviceCodeReceived = new TaskCompletionSource<bool>();

            _deviceCodeClient.DeviceCodeReceived += (sender, e) =>
            {
                userCode = e.UserCode;
                verificationUrl = e.VerificationUri;
                deviceCodeReceived.TrySetResult(true);
                _logger.LogInformation("Device code received: {UserCode}, URL: {VerificationUrl}", userCode, verificationUrl);
            };

            // Start the device code flow in the background
            _ = Task.Run(async () =>
            {
                try
                {
                    _logger.LogInformation("Starting device code authentication");
                    var token = await _deviceCodeClient.GetAccessTokenAsync(_deviceCodeCts.Token);

                    _logger.LogInformation("Device code authentication succeeded, creating session");

                    // Create credentials from OAuth token
                    var credentials = Credentials.WithAccessToken(token.AccessToken);

                    // Dispose existing session if any
                    if (_session != null)
                    {
                        await _session.DisposeAsync();
                        _session = null;
                    }

                    // Create new session
                    _session = Wavee.Core.Session.Session.Create(_sessionConfig, _httpClientFactory, _logger);

                    // Connect with OAuth credentials
                    await _session.ConnectAsync(credentials, _credentialsCache, CancellationToken.None);

                    // Get user data
                    _currentUser = _session.GetUserData();

                    // Update state
                    IsAuthenticated = true;
                    CurrentUser = _currentUser;

                    _logger.LogInformation("Device code login succeeded for user: {Username}", _currentUser?.Username);

                    // Raise authentication state changed event
                    RaiseAuthenticationStateChanged(true, _currentUser);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Device code flow was cancelled");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Device code authentication failed");
                }
            }, _deviceCodeCts.Token);

            // Wait for device code to be received
            await deviceCodeReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));

            if (userCode == null || verificationUrl == null)
            {
                throw new InvalidOperationException("Failed to receive device code from OAuth server");
            }

            return (userCode, verificationUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start device code flow");
            throw;
        }
    }

    /// <summary>
    /// Cancels any ongoing device code flow.
    /// </summary>
    public async Task CancelDeviceCodeFlowAsync()
    {
        if (_deviceCodeCts != null)
        {
            _logger.LogInformation("Cancelling device code flow");
            _deviceCodeCts.Cancel();
            _deviceCodeCts.Dispose();
            _deviceCodeCts = null;
        }

        if (_deviceCodeClient != null)
        {
            _deviceCodeClient.Dispose();
            _deviceCodeClient = null;
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Raises the AuthenticationStateChanged event.
    /// </summary>
    private void RaiseAuthenticationStateChanged(bool isAuthenticated, UserData? user)
    {
        AuthenticationStateChanged?.Invoke(this, new AuthenticationStateChangedEventArgs
        {
            IsAuthenticated = isAuthenticated,
            User = user
        });
    }

    /// <summary>
    /// Generates a stable device ID for this machine/user combination.
    /// </summary>
    private static string GenerateDeviceId()
    {
        // Use machine name + username to generate a stable device ID
        var machineId = Environment.MachineName;
        var userName = Environment.UserName;
        var combined = $"{machineId}-{userName}-Wavee";

        // Generate deterministic GUID from string
        using var md5 = System.Security.Cryptography.MD5.Create();
        var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(combined));
        var guid = new Guid(hash);

        return guid.ToString();
    }

    /// <summary>
    /// Gets a friendly device name for this machine.
    /// </summary>
    private static string GetDeviceName()
    {
        var machineName = Environment.MachineName;
        return $"Wavee on {machineName}";
    }
}
