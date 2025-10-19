using Microsoft.Extensions.Logging;

namespace Wavee.Core.OAuth;

/// <summary>
/// Main OAuth 2.0 client for obtaining Spotify access tokens.
/// Supports both Authorization Code Flow with PKCE and Device Code Flow.
/// </summary>
public sealed class OAuthClient : IDisposable
{
    private const string DefaultRedirectUri = "http://127.0.0.1:8898/login";
    private const string TokenEndpoint = "https://accounts.spotify.com/api/token";

    private readonly string _clientId;
    private readonly string[] _scopes;
    private readonly OAuthFlow _flow;
    private readonly string? _redirectUri;
    private readonly bool _openBrowser;
    private readonly ITokenCache? _tokenCache;
    private readonly ILogger? _logger;
    private readonly OAuthHttpClient _httpClient;

    /// <summary>
    /// Event raised when device code is received (Device Code Flow only).
    /// Subscribe to this to display the code in a custom UI.
    /// </summary>
    public event EventHandler<DeviceCodeReceivedEventArgs>? DeviceCodeReceived;

    private OAuthClient(
        string clientId,
        string[] scopes,
        OAuthFlow flow,
        string? redirectUri,
        bool openBrowser,
        ITokenCache? tokenCache,
        ILogger? logger)
    {
        _clientId = clientId;
        _scopes = scopes;
        _flow = flow;
        _redirectUri = redirectUri ?? DefaultRedirectUri;
        _openBrowser = openBrowser;
        _tokenCache = tokenCache;
        _logger = logger;
        _httpClient = new OAuthHttpClient(logger: logger);
    }

    /// <summary>
    /// Creates a new OAuth client with default settings (Authorization Code Flow).
    /// </summary>
    /// <param name="clientId">Spotify application client ID.</param>
    /// <param name="scopes">OAuth scopes to request (e.g., "streaming", "user-read-playback-state").</param>
    /// <param name="openBrowser">Whether to automatically open browser for authorization.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <returns>Configured OAuth client.</returns>
    public static OAuthClient Create(
        string clientId,
        string[] scopes,
        bool openBrowser = true,
        ILogger? logger = null)
    {
        return new OAuthClient(
            clientId,
            scopes,
            OAuthFlow.AuthorizationCode,
            redirectUri: null,
            openBrowser,
            tokenCache: new TokenCache(logger: logger),
            logger);
    }

    /// <summary>
    /// Creates a new OAuth client with custom configuration.
    /// </summary>
    /// <param name="clientId">Spotify application client ID.</param>
    /// <param name="scopes">OAuth scopes to request.</param>
    /// <param name="flow">OAuth flow to use (Authorization Code or Device Code).</param>
    /// <param name="redirectUri">Custom redirect URI (Authorization Code Flow only).</param>
    /// <param name="openBrowser">Whether to automatically open browser (Authorization Code Flow only).</param>
    /// <param name="tokenCache">Custom token cache implementation (null to disable caching).</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <returns>Configured OAuth client.</returns>
    public static OAuthClient CreateCustom(
        string clientId,
        string[] scopes,
        OAuthFlow flow = OAuthFlow.AuthorizationCode,
        string? redirectUri = null,
        bool openBrowser = true,
        ITokenCache? tokenCache = null,
        ILogger? logger = null)
    {
        return new OAuthClient(
            clientId,
            scopes,
            flow,
            redirectUri,
            openBrowser,
            tokenCache,
            logger);
    }

    /// <summary>
    /// Obtains a new access token using the configured OAuth flow.
    /// If a cached valid token exists, returns it immediately.
    /// </summary>
    /// <param name="username">Optional username for token caching (null uses default cache).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>OAuth token with access and refresh tokens.</returns>
    /// <exception cref="OAuthException">Thrown if authorization fails.</exception>
    public async Task<OAuthToken> GetAccessTokenAsync(
        string? username = null,
        CancellationToken cancellationToken = default)
    {
        // Try to load from cache first
        if (_tokenCache != null)
        {
            var cachedToken = await _tokenCache.LoadTokenAsync(username, cancellationToken);
            if (cachedToken != null && !cachedToken.IsExpired())
            {
                _logger?.LogInformation("Using cached token (expires {ExpiresAt})", cachedToken.ExpiresAt);
                return cachedToken;
            }

            if (cachedToken != null)
            {
                _logger?.LogInformation("Cached token expired, will obtain new token");
            }
        }

        // Execute OAuth flow
        var token = _flow switch
        {
            OAuthFlow.AuthorizationCode => await ExecuteAuthorizationCodeFlowAsync(cancellationToken),
            OAuthFlow.DeviceCode => await ExecuteDeviceCodeFlowAsync(cancellationToken),
            _ => throw new OAuthException(OAuthFailureReason.Unknown, "Invalid OAuth flow")
        };

        // Cache the token
        if (_tokenCache != null)
        {
            await _tokenCache.SaveTokenAsync(token, username, cancellationToken);
        }

        return token;
    }

    /// <summary>
    /// Refreshes an existing access token using a refresh token.
    /// </summary>
    /// <param name="refreshToken">Refresh token from a previous authorization.</param>
    /// <param name="username">Optional username for token caching.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>New OAuth token with refreshed access token.</returns>
    /// <exception cref="OAuthException">Thrown if refresh fails.</exception>
    public async Task<OAuthToken> RefreshTokenAsync(
        string refreshToken,
        string? username = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        _logger?.LogInformation("Refreshing access token");

        try
        {
            var parameters = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = _clientId
            };

            using var response = await _httpClient.PostFormAsync(
                TokenEndpoint,
                parameters,
                cancellationToken: cancellationToken);

            var token = ParseTokenResponse(response, refreshToken);

            // Cache the refreshed token
            if (_tokenCache != null)
            {
                await _tokenCache.SaveTokenAsync(token, username, cancellationToken);
            }

            _logger?.LogInformation("Token refreshed successfully");
            return token;
        }
        catch (OAuthException ex) when (ex.Reason == OAuthFailureReason.InvalidGrant)
        {
            _logger?.LogWarning("Refresh token invalid or revoked");
            throw new OAuthException(
                OAuthFailureReason.InvalidRefreshToken,
                "Refresh token is invalid or has been revoked. Please authorize again.",
                ex);
        }
    }

    /// <summary>
    /// Explicitly clears the cached token.
    /// </summary>
    /// <param name="username">Optional username for token caching.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ClearCachedTokenAsync(
        string? username = null,
        CancellationToken cancellationToken = default)
    {
        if (_tokenCache != null)
        {
            await _tokenCache.ClearTokenAsync(username, cancellationToken);
            _logger?.LogInformation("Cleared cached token");
        }
    }

    /// <summary>
    /// Executes Authorization Code Flow with PKCE.
    /// </summary>
    private async Task<OAuthToken> ExecuteAuthorizationCodeFlowAsync(CancellationToken cancellationToken)
    {
        using var flow = new AuthorizationCodeFlow(
            _clientId,
            _redirectUri!,
            _scopes,
            _openBrowser,
            _logger);

        return await flow.GetAccessTokenAsync(cancellationToken);
    }

    /// <summary>
    /// Executes Device Code Flow.
    /// </summary>
    private async Task<OAuthToken> ExecuteDeviceCodeFlowAsync(CancellationToken cancellationToken)
    {
        using var flow = new DeviceCodeFlow(
            _clientId,
            _scopes,
            _logger);

        // Forward device code event
        flow.DeviceCodeReceived += (sender, e) =>
        {
            DeviceCodeReceived?.Invoke(this, e);
        };

        return await flow.GetAccessTokenAsync(cancellationToken);
    }

    /// <summary>
    /// Parses token response from Spotify.
    /// </summary>
    private OAuthToken ParseTokenResponse(
        System.Text.Json.JsonDocument response,
        string existingRefreshToken)
    {
        var root = response.RootElement;

        var accessToken = root.GetProperty("access_token").GetString()
            ?? throw new OAuthException(OAuthFailureReason.Unknown, "Missing access_token in response");

        // Spotify doesn't always return a new refresh token during refresh
        var refreshToken = root.TryGetProperty("refresh_token", out var refreshProp)
            ? refreshProp.GetString() ?? existingRefreshToken
            : existingRefreshToken;

        var expiresIn = root.GetProperty("expires_in").GetInt32();

        var tokenType = root.GetProperty("token_type").GetString() ?? "Bearer";

        var scopes = root.TryGetProperty("scope", out var scopeProp)
            ? scopeProp.GetString() ?? string.Join(" ", _scopes)
            : string.Join(" ", _scopes);

        return OAuthToken.FromResponse(accessToken, refreshToken, expiresIn, tokenType, scopes);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

/// <summary>
/// OAuth 2.0 flow type.
/// </summary>
public enum OAuthFlow
{
    /// <summary>
    /// Authorization Code Flow with PKCE.
    /// Opens browser, runs local HTTP server for callback.
    /// Best for desktop applications with browser access.
    /// </summary>
    AuthorizationCode,

    /// <summary>
    /// Device Code Flow.
    /// User enters code on separate device.
    /// Best for CLI, console apps, and headless scenarios.
    /// </summary>
    DeviceCode
}
