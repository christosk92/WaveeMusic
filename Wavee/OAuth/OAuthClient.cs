using Microsoft.Extensions.Logging;

namespace Wavee.OAuth;

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
        ILogger? logger)
    {
        _clientId = clientId;
        _scopes = scopes;
        _flow = flow;
        _redirectUri = redirectUri ?? DefaultRedirectUri;
        _openBrowser = openBrowser;
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
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <returns>Configured OAuth client.</returns>
    public static OAuthClient CreateCustom(
        string clientId,
        string[] scopes,
        OAuthFlow flow = OAuthFlow.AuthorizationCode,
        string? redirectUri = null,
        bool openBrowser = true,
        ILogger? logger = null)
    {
        return new OAuthClient(
            clientId,
            scopes,
            flow,
            redirectUri,
            openBrowser,
            logger);
    }

    /// <summary>
    /// Obtains a new access token using the configured OAuth flow.
    /// </summary>
    /// <remarks>
    /// This method always executes the full OAuth flow and does not cache tokens.
    /// The returned token should be used immediately to create a Spotify session,
    /// which will return stored credentials that can be cached for future use.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>OAuth token with access and refresh tokens.</returns>
    /// <exception cref="OAuthException">Thrown if authorization fails.</exception>
    public async Task<OAuthToken> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        // Execute OAuth flow
        var token = _flow switch
        {
            OAuthFlow.AuthorizationCode => await ExecuteAuthorizationCodeFlowAsync(cancellationToken),
            OAuthFlow.DeviceCode => await ExecuteDeviceCodeFlowAsync(cancellationToken),
            _ => throw new OAuthException(OAuthFailureReason.Unknown, "Invalid OAuth flow")
        };

        return token;
    }

    /// <summary>
    /// Refreshes an existing access token using a refresh token.
    /// </summary>
    /// <remarks>
    /// This method is rarely needed since OAuth tokens are not cached.
    /// Stored credentials from the session should be used instead of refreshing OAuth tokens.
    /// </remarks>
    /// <param name="refreshToken">Refresh token from a previous authorization.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>New OAuth token with refreshed access token.</returns>
    /// <exception cref="OAuthException">Thrown if refresh fails.</exception>
    public async Task<OAuthToken> RefreshTokenAsync(
        string refreshToken,
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
