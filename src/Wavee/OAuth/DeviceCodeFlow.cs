using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Wavee.OAuth;

/// <summary>
/// Implements OAuth 2.0 Device Code Flow (RFC 8628).
/// User authorizes on a separate device by entering a code.
/// Perfect for console/CLI applications and headless scenarios.
/// </summary>
internal sealed class DeviceCodeFlow : IDisposable
{
    private const string DeviceAuthorizationEndpoint = "https://accounts.spotify.com/oauth2/device/authorize";
    private const string TokenEndpoint = "https://accounts.spotify.com/api/token";

    private readonly string _clientId;
    private readonly string[] _scopes;
    private readonly OAuthHttpClient _httpClient;
    private readonly ILogger? _logger;

    /// <summary>
    /// Event raised when device code is received and should be displayed to user.
    /// </summary>
    public event EventHandler<DeviceCodeReceivedEventArgs>? DeviceCodeReceived;

    /// <summary>
    /// Creates a new Device Code Flow handler.
    /// </summary>
    /// <param name="clientId">Spotify application client ID.</param>
    /// <param name="scopes">OAuth scopes to request.</param>
    /// <param name="logger">Optional logger.</param>
    public DeviceCodeFlow(
        string clientId,
        string[] scopes,
        ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(scopes);

        _clientId = clientId;
        _scopes = scopes;
        _logger = logger;
        _httpClient = new OAuthHttpClient(logger: logger);
    }

    /// <summary>
    /// Executes the full device code flow to obtain an access token.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>OAuth token with access and refresh tokens.</returns>
    /// <exception cref="OAuthException">Thrown if authorization fails.</exception>
    public async Task<OAuthToken> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Starting Device Code Flow");

        // Step 1: Request device code
        var deviceCode = await RequestDeviceCodeAsync(cancellationToken);

        // Step 2: Display code to user
        DisplayDeviceCode(deviceCode);

        // Step 3: Poll for token
        var token = await PollForTokenAsync(deviceCode, cancellationToken);

        _logger?.LogInformation("Device Code Flow completed successfully");
        return token;
    }

    /// <summary>
    /// Requests a device code from Spotify.
    /// </summary>
    private async Task<DeviceCodeResponse> RequestDeviceCodeAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation("Requesting device code from Spotify");

        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["scope"] = string.Join(" ", _scopes)
        };

        using var response = await _httpClient.PostFormAsync(
            DeviceAuthorizationEndpoint,
            parameters,
            cancellationToken: cancellationToken);

        return ParseDeviceCodeResponse(response);
    }

    /// <summary>
    /// Displays the device code to the user (console + event).
    /// </summary>
    private void DisplayDeviceCode(DeviceCodeResponse deviceCode)
    {
        _logger?.LogInformation("Device code received: {UserCode}", deviceCode.UserCode);

        // Raise event for UI applications
        DeviceCodeReceived?.Invoke(this, new DeviceCodeReceivedEventArgs(deviceCode));

        // Display to console
        Console.WriteLine();
        Console.WriteLine("┌─────────────────────────────────────┐");
        Console.WriteLine("│  Spotify Authorization Required     │");
        Console.WriteLine("├─────────────────────────────────────┤");
        Console.WriteLine($"│  Visit: {deviceCode.VerificationUri,-25}│");
        Console.WriteLine($"│  Enter code: {deviceCode.UserCode,-22}│");
        Console.WriteLine("│                                     │");
        Console.WriteLine($"│  Code expires in {deviceCode.ExpiresIn / 60} minutes{new string(' ', 13)}│");
        Console.WriteLine("└─────────────────────────────────────┘");
        Console.WriteLine();
    }

    /// <summary>
    /// Polls Spotify for token until user authorizes or code expires.
    /// </summary>
    private async Task<OAuthToken> PollForTokenAsync(
        DeviceCodeResponse deviceCode,
        CancellationToken cancellationToken)
    {
        _logger?.LogInformation("Polling for token (interval: {Interval}s)", deviceCode.Interval);

        var interval = TimeSpan.FromSeconds(deviceCode.Interval);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(deviceCode.ExpiresIn);

        // Wait before first poll
        await Task.Delay(interval, cancellationToken);

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                _logger?.LogDebug("Polling for token...");

                var parameters = new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["device_code"] = deviceCode.DeviceCode,
                    ["client_id"] = _clientId
                };

                using var response = await _httpClient.PostFormAsync(
                    TokenEndpoint,
                    parameters,
                    cancellationToken: cancellationToken);

                // Success!
                _logger?.LogInformation("User authorized successfully");
                return ParseTokenResponse(response);
            }
            catch (OAuthException ex) when (ex.Reason == OAuthFailureReason.AuthorizationPending)
            {
                // User hasn't authorized yet - continue polling
                _logger?.LogDebug("Authorization pending, will retry in {Interval}", interval);
                await Task.Delay(interval, cancellationToken);
            }
            catch (OAuthException ex) when (ex.Reason == OAuthFailureReason.SlowDown)
            {
                // Polling too fast - increase interval
                interval = interval.Add(TimeSpan.FromSeconds(5));
                _logger?.LogWarning("Polling too fast, increasing interval to {Interval}", interval);
                await Task.Delay(interval, cancellationToken);
            }
            catch (OAuthException ex) when (ex.Reason == OAuthFailureReason.ExpiredDeviceCode)
            {
                // Code expired
                _logger?.LogError("Device code expired before user authorized");
                throw new OAuthException(
                    OAuthFailureReason.ExpiredDeviceCode,
                    "Device code expired. Please start authorization again.");
            }
            catch (OAuthException ex) when (ex.Reason == OAuthFailureReason.AccessDenied)
            {
                // User explicitly denied
                _logger?.LogWarning("User denied authorization");
                throw new OAuthException(
                    OAuthFailureReason.AccessDenied,
                    "User denied authorization request");
            }
        }

        // Timeout
        _logger?.LogError("Timeout waiting for user authorization");
        throw new OAuthException(
            OAuthFailureReason.Timeout,
            $"User did not authorize within {deviceCode.ExpiresIn / 60} minutes");
    }

    /// <summary>
    /// Parses the device code response from Spotify.
    /// </summary>
    private DeviceCodeResponse ParseDeviceCodeResponse(JsonDocument response)
    {
        var root = response.RootElement;

        var deviceCode = root.GetProperty("device_code").GetString()
            ?? throw new OAuthException(OAuthFailureReason.Unknown, "Missing device_code in response");

        var userCode = root.GetProperty("user_code").GetString()
            ?? throw new OAuthException(OAuthFailureReason.Unknown, "Missing user_code in response");

        var verificationUri = root.GetProperty("verification_uri").GetString()
            ?? throw new OAuthException(OAuthFailureReason.Unknown, "Missing verification_uri in response");

        var verificationUriComplete = root.TryGetProperty("verification_uri_complete", out var uriCompleteProp)
            ? uriCompleteProp.GetString()
            : null;

        var expiresIn = root.GetProperty("expires_in").GetInt32();

        var interval = root.GetProperty("interval").GetInt32();

        return new DeviceCodeResponse
        {
            DeviceCode = deviceCode,
            UserCode = userCode,
            VerificationUri = verificationUri,
            VerificationUriComplete = verificationUriComplete,
            ExpiresIn = expiresIn,
            Interval = interval
        };
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

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

/// <summary>
/// Response from device authorization endpoint.
/// </summary>
internal sealed record DeviceCodeResponse
{
    public required string DeviceCode { get; init; }
    public required string UserCode { get; init; }
    public required string VerificationUri { get; init; }
    public string? VerificationUriComplete { get; init; }
    public required int ExpiresIn { get; init; }
    public required int Interval { get; init; }
}

/// <summary>
/// Event args for when device code is received.
/// </summary>
public sealed class DeviceCodeReceivedEventArgs : EventArgs
{
    /// <summary>
    /// User code to display to the user.
    /// </summary>
    public string UserCode { get; }

    /// <summary>
    /// Verification URI where user should go.
    /// </summary>
    public string VerificationUri { get; }

    /// <summary>
    /// Complete verification URI with code pre-filled (for QR codes).
    /// </summary>
    public string? VerificationUriComplete { get; }

    /// <summary>
    /// Seconds until code expires.
    /// </summary>
    public int ExpiresIn { get; }

    internal DeviceCodeReceivedEventArgs(DeviceCodeResponse deviceCode)
    {
        UserCode = deviceCode.UserCode;
        VerificationUri = deviceCode.VerificationUri;
        VerificationUriComplete = deviceCode.VerificationUriComplete;
        ExpiresIn = deviceCode.ExpiresIn;
    }
}
