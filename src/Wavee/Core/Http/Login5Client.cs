using System.Net.Http.Headers;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Wavee.Protocol.Login;

namespace Wavee.Core.Http;

/// <summary>
/// Client for Spotify's login5 authentication service.
/// </summary>
/// <remarks>
/// Exchanges stored credentials (from APWelcome) for short-lived access tokens.
/// Handles hashcash challenges automatically.
///
/// Endpoint: https://login5.spotify.com/v3/login
/// Protocol: Protobuf request/response
/// </remarks>
internal sealed class Login5Client
{
    private const string Login5Url = "https://login5.spotify.com/v3/login";
    private const int MaxRetries = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    private readonly HttpClient _httpClient;
    private readonly string _clientId;
    private readonly string _deviceId;
    private readonly ILogger? _logger;

    /// <summary>
    /// Creates a new Login5Client.
    /// </summary>
    /// <param name="httpClient">HTTP client for making requests.</param>
    /// <param name="clientId">Spotify client ID.</param>
    /// <param name="deviceId">Unique device identifier.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public Login5Client(
        HttpClient httpClient,
        string clientId,
        string deviceId,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        _httpClient = httpClient;
        _clientId = clientId;
        _deviceId = deviceId;
        _logger = logger;
    }

    /// <summary>
    /// Obtains an access token using stored credentials.
    /// </summary>
    /// <param name="username">Spotify username.</param>
    /// <param name="authData">Authentication data from stored credentials.</param>
    /// <param name="clientToken">Optional client token for additional security.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Access token with expiry information.</returns>
    /// <exception cref="Login5Exception">Thrown if authentication fails.</exception>
    public async Task<AccessToken> GetAccessTokenAsync(
        string username,
        byte[] authData,
        string? clientToken = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentNullException.ThrowIfNull(authData);

        if (authData.Length == 0)
        {
            throw new Login5Exception(
                Login5FailureReason.NoStoredCredentials,
                "Authentication data is empty");
        }

        _logger?.LogDebug("Requesting access token for user: {Username}", username);

        // Build LoginRequest with StoredCredential
        var loginRequest = new LoginRequest
        {
            ClientInfo = new ClientInfo
            {
                ClientId = _clientId,
                DeviceId = _deviceId
            },
            StoredCredential = new StoredCredential
            {
                Username = username,
                Data = ByteString.CopyFrom(authData)
            }
        };

        // Send request and handle challenges/retries
        var response = await SendLoginRequestAsync(loginRequest, clientToken, cancellationToken);

        // Extract access token from response
        if (response.Ok == null)
        {
            throw new Login5Exception(
                Login5FailureReason.NoOkResponse,
                "Login5 response missing Ok field");
        }

        // Log raw expiry duration from Spotify for diagnostics
        _logger?.LogDebug("Token expires in {Seconds} seconds (raw AccessTokenExpiresIn: {RawValue})",
            response.Ok.AccessTokenExpiresIn, response.Ok.AccessTokenExpiresIn);

        var accessToken = AccessToken.FromLogin5Response(
            response.Ok.AccessToken,
            response.Ok.AccessTokenExpiresIn);

        _logger?.LogInformation("Access token obtained (expires {ExpiresAt})", accessToken.ExpiresAt);

        return accessToken;
    }

    /// <summary>
    /// Sends a login request and handles challenges/retries.
    /// </summary>
    private async Task<LoginResponse> SendLoginRequestAsync(
        LoginRequest request,
        string? clientToken,
        CancellationToken cancellationToken)
    {
        LoginResponse? response = null;
        int attempts = 0;

        while (attempts < MaxRetries)
        {
            attempts++;

            _logger?.LogTrace("Login5 request attempt {Attempt}/{MaxRetries}", attempts, MaxRetries);

            // Serialize request
            var body = request.ToByteArray();

            // Build HTTP request
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, Login5Url)
            {
                Content = new ByteArrayContent(body)
            };

            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-protobuf"));

            if (!string.IsNullOrWhiteSpace(clientToken))
            {
                httpRequest.Headers.Add("client-token", clientToken);
            }

            // Send request
            HttpResponseMessage httpResponse;
            try
            {
                httpResponse = await _httpClient.SendAsync(httpRequest, cancellationToken);
                httpResponse.EnsureSuccessStatusCode();
            }
            catch (HttpRequestException ex)
            {
                _logger?.LogError(ex, "Login5 HTTP request failed");
                throw new Login5Exception(
                    Login5FailureReason.Unknown,
                    "Failed to send login5 request",
                    ex);
            }

            var responseBytes = await httpResponse.Content.ReadAsByteArrayAsync(cancellationToken);
            response = LoginResponse.Parser.ParseFrom(responseBytes);

            // Check for successful response
            if (response.Ok != null)
            {
                _logger?.LogDebug("Login5 authentication successful");
                return response;
            }

            // Check for errors
            if (response.Error != LoginError.UnknownError)
            {
                _logger?.LogWarning("Login5 error: {Error}", response.Error);

                // Handle retryable errors
                if (response.Error == LoginError.Timeout || response.Error == LoginError.TooManyAttempts)
                {
                    if (attempts < MaxRetries)
                    {
                        _logger?.LogInformation("Retrying login5 request in {Delay}s", RetryDelay.TotalSeconds);
                        await Task.Delay(RetryDelay, cancellationToken);
                        continue;
                    }
                }

                throw Login5Exception.FromLoginError(response.Error);
            }

            // Handle challenges
            if (response.Challenges != null && response.Challenges.Challenges_.Count > 0)
            {
                _logger?.LogDebug("Received {Count} challenges, solving...", response.Challenges.Challenges_.Count);
                HandleChallenges(request, response);
                continue; // Retry with solved challenges
            }

            // Unknown state - no ok, no error, no challenges
            if (attempts < MaxRetries)
            {
                _logger?.LogWarning("Login5 returned unexpected response, retrying...");
                await Task.Delay(RetryDelay, cancellationToken);
                continue;
            }

            throw new Login5Exception(
                Login5FailureReason.Unknown,
                "Login5 returned unexpected response");
        }

        throw new Login5Exception(
            Login5FailureReason.MaxRetriesExceeded,
            $"Failed to authenticate after {MaxRetries} attempts");
    }

    /// <summary>
    /// Handles challenges from the login5 response.
    /// </summary>
    private void HandleChallenges(LoginRequest request, LoginResponse response)
    {
        var solutions = new ChallengeSolutions();

        foreach (var challenge in response.Challenges.Challenges_)
        {
            // Handle code challenges (not supported)
            if (challenge.Code != null)
            {
                _logger?.LogError("Code challenge received but not supported");
                throw new Login5Exception(
                    Login5FailureReason.CodeChallengeNotSupported,
                    "Code challenges are not supported");
            }

            // Handle hashcash challenges
            if (challenge.Hashcash != null)
            {
                _logger?.LogDebug(
                    "Solving hashcash challenge (prefix: {PrefixLength} bytes, length: {Length} bits)",
                    challenge.Hashcash.Prefix.Length,
                    challenge.Hashcash.Length);

                var solution = SolveHashcash(challenge.Hashcash, response.LoginContext.ToByteArray());
                solutions.Solutions.Add(solution);

                _logger?.LogDebug("Hashcash solved");
            }
        }

        // Update request with solutions and login context
        request.ChallengeSolutions = solutions;
        request.LoginContext = response.LoginContext;
    }

    /// <summary>
    /// Solves a hashcash challenge.
    /// </summary>
    private ChallengeSolution SolveHashcash(HashcashChallenge challenge, byte[] loginContext)
    {
        var (suffix, duration) = HashcashSolver.Solve(
            loginContext,
            challenge.Prefix.ToByteArray(),
            challenge.Length);

        _logger?.LogTrace("Hashcash solved in {Duration:F3}s", duration.TotalSeconds);

        return new ChallengeSolution
        {
            Hashcash = new HashcashSolution
            {
                Suffix = ByteString.CopyFrom(suffix),
                Duration = Google.Protobuf.WellKnownTypes.Duration.FromTimeSpan(duration)
            }
        };
    }
}
