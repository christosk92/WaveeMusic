using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Wavee.OAuth;

/// <summary>
/// Implements OAuth 2.0 Authorization Code Flow with PKCE (Proof Key for Code Exchange).
/// Opens browser for user authorization and runs local HTTP server to receive callback.
/// </summary>
internal sealed class AuthorizationCodeFlow : IDisposable
{
    private const string AuthorizationEndpoint = "https://accounts.spotify.com/authorize";
    private const string TokenEndpoint = "https://accounts.spotify.com/api/token";

    private readonly string _clientId;
    private readonly string _redirectUri;
    private readonly string[] _scopes;
    private readonly bool _openBrowser;
    private readonly OAuthHttpClient _httpClient;
    private readonly ILogger? _logger;

    /// <summary>
    /// Creates a new Authorization Code Flow handler.
    /// </summary>
    /// <param name="clientId">Spotify application client ID.</param>
    /// <param name="redirectUri">Redirect URI (must match registered URI, typically http://localhost:8898).</param>
    /// <param name="scopes">OAuth scopes to request.</param>
    /// <param name="openBrowser">Whether to automatically open browser.</param>
    /// <param name="logger">Optional logger.</param>
    public AuthorizationCodeFlow(
        string clientId,
        string redirectUri,
        string[] scopes,
        bool openBrowser,
        ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);
        ArgumentNullException.ThrowIfNull(scopes);

        _clientId = clientId;
        _redirectUri = redirectUri;
        _scopes = scopes;
        _openBrowser = openBrowser;
        _logger = logger;
        _httpClient = new OAuthHttpClient(logger: logger);
    }

    /// <summary>
    /// Executes the full authorization code flow to obtain an access token.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>OAuth token with access and refresh tokens.</returns>
    /// <exception cref="OAuthException">Thrown if authorization fails.</exception>
    public async Task<OAuthToken> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Starting Authorization Code Flow with PKCE");

        // Step 1: Generate PKCE challenge
        var (verifier, challenge) = GeneratePkceChallenge();

        // Step 2: Build authorization URL
        var authUrl = BuildAuthorizationUrl(challenge);

        // Step 3: Start local HTTP server
        var port = GetPortFromRedirectUri(_redirectUri);
        using var listener = StartHttpListener(port);

        // Step 4: Open browser (or display URL)
        if (_openBrowser)
        {
            OpenBrowser(authUrl);
        }
        else
        {
            _logger?.LogInformation("Browse to: {AuthUrl}", authUrl);
            Console.WriteLine($"Browse to: {authUrl}");
        }

        // Step 5: Wait for authorization code
        var code = await WaitForAuthorizationCodeAsync(listener, cancellationToken);

        // Step 6: Exchange code for token
        var token = await ExchangeCodeForTokenAsync(code, verifier, cancellationToken);

        _logger?.LogInformation("Authorization Code Flow completed successfully");
        return token;
    }

    /// <summary>
    /// Generates a PKCE code verifier and challenge.
    /// </summary>
    /// <returns>Tuple of (verifier, challenge).</returns>
    private (string verifier, string challenge) GeneratePkceChallenge()
    {
        // Generate random verifier (43-128 chars from [A-Za-z0-9-._~])
        var verifierBytes = RandomNumberGenerator.GetBytes(32);
        var verifier = Base64UrlEncode(verifierBytes);

        // Generate challenge: SHA256(verifier)
        var verifierHash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        var challenge = Base64UrlEncode(verifierHash);

        _logger?.LogDebug("Generated PKCE verifier and challenge");
        return (verifier, challenge);
    }

    /// <summary>
    /// Builds the authorization URL with all required parameters.
    /// </summary>
    private string BuildAuthorizationUrl(string codeChallenge)
    {
        var state = GenerateState();
        var scopeString = string.Join(" ", _scopes);

        var queryParams = new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["response_type"] = "code",
            ["redirect_uri"] = _redirectUri,
            ["code_challenge_method"] = "S256",
            ["code_challenge"] = codeChallenge,
            ["scope"] = scopeString,
            ["state"] = state
        };

        var queryString = string.Join("&",
            queryParams.Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));

        return $"{AuthorizationEndpoint}?{queryString}";
    }

    /// <summary>
    /// Starts an HTTP listener on the specified port.
    /// </summary>
    private HttpListener StartHttpListener(int port)
    {
        var listener = new HttpListener();
        var prefix = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(prefix);

        try
        {
            listener.Start();
            _logger?.LogInformation("OAuth callback server listening on {Prefix}", prefix);
            return listener;
        }
        catch (HttpListenerException ex)
        {
            _logger?.LogError(ex, "Failed to start HTTP listener on port {Port}", port);
            throw new OAuthException(
                OAuthFailureReason.ServerStartFailed,
                $"Failed to start local HTTP server on port {port}. Port may be in use.",
                ex);
        }
    }

    /// <summary>
    /// Waits for the OAuth callback and extracts the authorization code.
    /// </summary>
    private async Task<string> WaitForAuthorizationCodeAsync(
        HttpListener listener,
        CancellationToken cancellationToken)
    {
        _logger?.LogInformation("Waiting for authorization callback...");

        try
        {
            var context = await listener.GetContextAsync().WaitAsync(cancellationToken);
            var request = context.Request;
            var response = context.Response;

            _logger?.LogDebug("Received callback: {Url}", request.Url);

            // Extract code from query string
            var queryParams = ParseQueryString(request.Url?.Query ?? "");
            var code = queryParams.TryGetValue("code", out var codeValue) ? codeValue : null;
            var error = queryParams.TryGetValue("error", out var errorValue) ? errorValue : null;

            // Send response to browser
            await SendBrowserResponseAsync(response, code != null);

            // Check for errors
            if (!string.IsNullOrEmpty(error))
            {
                var errorDescription = queryParams.TryGetValue("error_description", out var desc) ? desc : error;
                _logger?.LogError("Authorization failed: {Error}", errorDescription);

                var reason = error == "access_denied"
                    ? OAuthFailureReason.AccessDenied
                    : OAuthFailureReason.Unknown;

                throw new OAuthException(reason, $"Authorization failed: {errorDescription}");
            }

            // Check for code
            if (string.IsNullOrEmpty(code))
            {
                _logger?.LogError("Authorization code not found in callback");
                throw new OAuthException(
                    OAuthFailureReason.CodeNotFound,
                    "Authorization code not found in callback URL");
            }

            _logger?.LogInformation("Received authorization code");
            return code;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("Authorization wait cancelled");
            throw new OAuthException(
                OAuthFailureReason.Cancelled,
                "Authorization was cancelled");
        }
        catch (Exception ex) when (ex is not OAuthException)
        {
            _logger?.LogError(ex, "Unexpected error waiting for callback");
            throw new OAuthException(
                OAuthFailureReason.Unknown,
                "Unexpected error during authorization",
                ex);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// Exchanges the authorization code for an access token.
    /// </summary>
    private async Task<OAuthToken> ExchangeCodeForTokenAsync(
        string code,
        string verifier,
        CancellationToken cancellationToken)
    {
        _logger?.LogInformation("Exchanging authorization code for access token");

        var parameters = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = _redirectUri,
            ["client_id"] = _clientId,
            ["code_verifier"] = verifier
        };

        using var response = await _httpClient.PostFormAsync(
            TokenEndpoint,
            parameters,
            cancellationToken: cancellationToken);

        return ParseTokenResponse(response);
    }

    /// <summary>
    /// Sends an HTML response to the browser after receiving the callback.
    /// </summary>
    private async Task SendBrowserResponseAsync(HttpListenerResponse response, bool success)
    {
        var html = success
            ? """
              <!DOCTYPE html>
              <html lang="en">
              <head>
                  <meta charset="UTF-8">
                  <meta name="viewport" content="width=device-width, initial-scale=1.0">
                  <title>Authorization Successful - Wavee</title>
                  <style>
                      * {
                          margin: 0;
                          padding: 0;
                          box-sizing: border-box;
                      }
                      body {
                          font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Oxygen, Ubuntu, sans-serif;
                          background: linear-gradient(135deg, #1DB954 0%, #191414 100%);
                          min-height: 100vh;
                          display: flex;
                          align-items: center;
                          justify-content: center;
                          color: #fff;
                      }
                      .container {
                          background: rgba(255, 255, 255, 0.1);
                          backdrop-filter: blur(10px);
                          border-radius: 24px;
                          padding: 48px 64px;
                          text-align: center;
                          box-shadow: 0 8px 32px rgba(0, 0, 0, 0.3);
                          border: 1px solid rgba(255, 255, 255, 0.2);
                          max-width: 500px;
                          animation: slideIn 0.5s ease-out;
                      }
                      @keyframes slideIn {
                          from {
                              opacity: 0;
                              transform: translateY(30px);
                          }
                          to {
                              opacity: 1;
                              transform: translateY(0);
                          }
                      }
                      .icon {
                          width: 80px;
                          height: 80px;
                          background: #1DB954;
                          border-radius: 50%;
                          display: flex;
                          align-items: center;
                          justify-content: center;
                          margin: 0 auto 24px;
                          animation: checkmark 0.6s ease-in-out 0.3s both;
                      }
                      @keyframes checkmark {
                          0% {
                              transform: scale(0);
                              opacity: 0;
                          }
                          50% {
                              transform: scale(1.2);
                          }
                          100% {
                              transform: scale(1);
                              opacity: 1;
                          }
                      }
                      .icon svg {
                          width: 48px;
                          height: 48px;
                          stroke: #fff;
                          stroke-width: 3;
                          stroke-linecap: round;
                          stroke-linejoin: round;
                          fill: none;
                          stroke-dasharray: 50;
                          stroke-dashoffset: 50;
                          animation: draw 0.5s ease-out 0.5s forwards;
                      }
                      @keyframes draw {
                          to {
                              stroke-dashoffset: 0;
                          }
                      }
                      h1 {
                          font-size: 32px;
                          font-weight: 700;
                          margin-bottom: 16px;
                          color: #fff;
                      }
                      p {
                          font-size: 16px;
                          color: rgba(255, 255, 255, 0.85);
                          line-height: 1.6;
                          margin-bottom: 8px;
                      }
                      .app-name {
                          display: inline-flex;
                          align-items: center;
                          gap: 8px;
                          background: rgba(29, 185, 84, 0.2);
                          padding: 4px 12px;
                          border-radius: 12px;
                          font-weight: 600;
                          margin-top: 16px;
                          color: #1DB954;
                      }
                      .close-hint {
                          margin-top: 24px;
                          font-size: 14px;
                          color: rgba(255, 255, 255, 0.6);
                      }
                  </style>
              </head>
              <body>
                  <div class="container">
                      <div class="icon">
                          <svg viewBox="0 0 24 24">
                              <polyline points="20 6 9 17 4 12"></polyline>
                          </svg>
                      </div>
                      <h1>Authorization Successful!</h1>
                      <p>You've successfully connected your Spotify account.</p>
                      <div class="app-name">
                          <span>🎵</span>
                          <span>Wavee</span>
                      </div>
                      <p class="close-hint">You can close this window and return to your application.</p>
                  </div>
              </body>
              </html>
              """
            : """
              <!DOCTYPE html>
              <html lang="en">
              <head>
                  <meta charset="UTF-8">
                  <meta name="viewport" content="width=device-width, initial-scale=1.0">
                  <title>Authorization Failed - Wavee</title>
                  <style>
                      * {
                          margin: 0;
                          padding: 0;
                          box-sizing: border-box;
                      }
                      body {
                          font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Oxygen, Ubuntu, sans-serif;
                          background: linear-gradient(135deg, #E22134 0%, #191414 100%);
                          min-height: 100vh;
                          display: flex;
                          align-items: center;
                          justify-content: center;
                          color: #fff;
                      }
                      .container {
                          background: rgba(255, 255, 255, 0.1);
                          backdrop-filter: blur(10px);
                          border-radius: 24px;
                          padding: 48px 64px;
                          text-align: center;
                          box-shadow: 0 8px 32px rgba(0, 0, 0, 0.3);
                          border: 1px solid rgba(255, 255, 255, 0.2);
                          max-width: 500px;
                          animation: slideIn 0.5s ease-out;
                      }
                      @keyframes slideIn {
                          from {
                              opacity: 0;
                              transform: translateY(30px);
                          }
                          to {
                              opacity: 1;
                              transform: translateY(0);
                          }
                      }
                      .icon {
                          width: 80px;
                          height: 80px;
                          background: #E22134;
                          border-radius: 50%;
                          display: flex;
                          align-items: center;
                          justify-content: center;
                          margin: 0 auto 24px;
                          animation: shake 0.5s ease-in-out;
                      }
                      @keyframes shake {
                          0%, 100% { transform: translateX(0); }
                          25% { transform: translateX(-10px); }
                          75% { transform: translateX(10px); }
                      }
                      .icon svg {
                          width: 48px;
                          height: 48px;
                          stroke: #fff;
                          stroke-width: 3;
                          stroke-linecap: round;
                          stroke-linejoin: round;
                          fill: none;
                      }
                      h1 {
                          font-size: 32px;
                          font-weight: 700;
                          margin-bottom: 16px;
                          color: #fff;
                      }
                      p {
                          font-size: 16px;
                          color: rgba(255, 255, 255, 0.85);
                          line-height: 1.6;
                      }
                      .close-hint {
                          margin-top: 24px;
                          font-size: 14px;
                          color: rgba(255, 255, 255, 0.6);
                      }
                  </style>
              </head>
              <body>
                  <div class="container">
                      <div class="icon">
                          <svg viewBox="0 0 24 24">
                              <line x1="18" y1="6" x2="6" y2="18"></line>
                              <line x1="6" y1="6" x2="18" y2="18"></line>
                          </svg>
                      </div>
                      <h1>Authorization Failed</h1>
                      <p>Something went wrong during authorization.</p>
                      <p class="close-hint">Please check your application for more details.</p>
                  </div>
              </body>
              </html>
              """;

        var buffer = Encoding.UTF8.GetBytes(html);
        response.ContentType = "text/html";
        response.ContentLength64 = buffer.Length;
        response.StatusCode = 200;

        await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
        response.OutputStream.Close();
    }

    /// <summary>
    /// Opens the authorization URL in the default browser.
    /// </summary>
    private void OpenBrowser(string url)
    {
        try
        {
            _logger?.LogInformation("Opening browser to {Url}", url);

            // Cross-platform browser opening
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (OperatingSystem.IsLinux())
            {
                Process.Start("xdg-open", url);
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", url);
            }
            else
            {
                _logger?.LogWarning("Cannot open browser on this platform, displaying URL instead");
                Console.WriteLine($"Browse to: {url}");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to open browser, displaying URL instead");
            Console.WriteLine($"Browse to: {url}");
        }
    }

    /// <summary>
    /// Parses the token response from Spotify.
    /// </summary>
    private OAuthToken ParseTokenResponse(JsonDocument response)
    {
        var root = response.RootElement;

        var accessToken = root.GetProperty("access_token").GetString()
            ?? throw new OAuthException(OAuthFailureReason.Unknown, "Missing access_token in response");

        var refreshToken = root.GetProperty("refresh_token").GetString()
            ?? throw new OAuthException(OAuthFailureReason.Unknown, "Missing refresh_token in response");

        var expiresIn = root.GetProperty("expires_in").GetInt32();

        var tokenType = root.GetProperty("token_type").GetString() ?? "Bearer";

        var scopes = root.TryGetProperty("scope", out var scopeProp)
            ? scopeProp.GetString() ?? string.Join(" ", _scopes)
            : string.Join(" ", _scopes);

        return OAuthToken.FromResponse(accessToken, refreshToken, expiresIn, tokenType, scopes);
    }

    /// <summary>
    /// Extracts the port number from a redirect URI.
    /// </summary>
    private static int GetPortFromRedirectUri(string redirectUri)
    {
        var uri = new Uri(redirectUri);
        return uri.Port > 0 ? uri.Port : 80;
    }

    /// <summary>
    /// Generates a random state parameter for CSRF protection.
    /// </summary>
    private static string GenerateState()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        return Base64UrlEncode(bytes);
    }

    /// <summary>
    /// Base64URL encodes a byte array (URL-safe base64 without padding).
    /// </summary>
    private static string Base64UrlEncode(byte[] data)
    {
        var base64 = Convert.ToBase64String(data);
        return base64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    /// <summary>
    /// Parses a query string into a dictionary.
    /// </summary>
    private static Dictionary<string, string> ParseQueryString(string query)
    {
        var result = new Dictionary<string, string>();

        if (string.IsNullOrEmpty(query))
            return result;

        // Remove leading '?' if present
        query = query.TrimStart('?');

        foreach (var pair in query.Split('&'))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2)
            {
                var key = Uri.UnescapeDataString(parts[0]);
                var value = Uri.UnescapeDataString(parts[1]);
                result[key] = value;
            }
        }

        return result;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
