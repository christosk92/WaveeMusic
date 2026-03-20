using System.Net.Http;
using System.Net.Http.Headers;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Wavee.Core.Session;
using Wavee.Protocol.ClientToken;

namespace Wavee.Core.Http;

/// <summary>
/// Manages Spotify client-token lifecycle: fetch, cache, auto-refresh, and hashcash challenge solving.
/// The client-token is required as a header on all spclient requests.
/// </summary>
internal sealed class ClientTokenManager
{
    private const string TokenEndpoint = "https://clienttoken.spotify.com/v1/clienttoken";
    private const string ClientVersion = "1.2.52.442";
    private const int MaxChallengeRetries = 3;

    private readonly HttpClient _httpClient;
    private readonly SessionConfig _config;
    private readonly ILogger? _logger;

    private string? _cachedToken;
    private DateTimeOffset _expiresAt;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ClientTokenManager(HttpClient httpClient, SessionConfig config, ILogger? logger = null)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Gets a valid client token, fetching or refreshing as needed.
    /// </summary>
    public async Task<string?> GetClientTokenAsync(CancellationToken ct = default)
    {
        // Fast path: cached and not about to expire
        if (_cachedToken != null && DateTimeOffset.UtcNow < _expiresAt.AddMinutes(-5))
            return _cachedToken;

        await _lock.WaitAsync(ct);
        try
        {
            // Double-check after lock
            if (_cachedToken != null && DateTimeOffset.UtcNow < _expiresAt.AddMinutes(-5))
                return _cachedToken;

            return await FetchTokenAsync(ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to obtain client token, continuing without");
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<string?> FetchTokenAsync(CancellationToken ct)
    {
        var request = BuildInitialRequest();

        for (int attempt = 0; attempt < MaxChallengeRetries; attempt++)
        {
            var response = await SendTokenRequestAsync(request, ct);

            switch (response.ResponseType)
            {
                case ClientTokenResponseType.ResponseGrantedTokenResponse:
                    var granted = response.GrantedToken;
                    _cachedToken = granted.Token;
                    _expiresAt = DateTimeOffset.UtcNow.AddSeconds(
                        granted.RefreshAfterSeconds > 0 ? granted.RefreshAfterSeconds : 7200);
                    _logger?.LogInformation("Client token obtained (expires in {Seconds}s)",
                        granted.RefreshAfterSeconds);
                    return _cachedToken;

                case ClientTokenResponseType.ResponseChallengesResponse:
                    _logger?.LogDebug("Client token challenge received (attempt {Attempt})", attempt + 1);
                    request = SolveChallenge(response.Challenges);
                    break;

                default:
                    _logger?.LogWarning("Unexpected client token response: {Type}", response.ResponseType);
                    return null;
            }
        }

        _logger?.LogWarning("Failed to solve client token challenge after {Max} attempts", MaxChallengeRetries);
        return null;
    }

    private ClientTokenRequest BuildInitialRequest()
    {
        return new ClientTokenRequest
        {
            RequestType = ClientTokenRequestType.RequestClientDataRequest,
            ClientData = new ClientDataRequest
            {
                ClientVersion = ClientVersion,
                ClientId = _config.GetClientId(),
                ConnectivitySdkData = BuildConnectivityData()
            }
        };
    }

    private ConnectivitySdkData BuildConnectivityData()
    {
        var data = new ConnectivitySdkData
        {
            DeviceId = _config.DeviceId,
            PlatformSpecificData = new PlatformSpecificData()
        };

        if (OperatingSystem.IsWindows())
        {
            data.PlatformSpecificData.DesktopWindows = new NativeDesktopWindowsData
            {
                OsVersion = 10,
                OsBuild = 22621,
                PlatformId = 2,
                UnknownValue6 = 9,
                ImageFileMachine = 34404, // x86_64
                PeMachine = 34404,
                UnknownValue10 = true
            };
        }
        else if (OperatingSystem.IsLinux())
        {
            data.PlatformSpecificData.DesktopLinux = new NativeDesktopLinuxData
            {
                SystemName = "Linux",
                SystemRelease = Environment.OSVersion.Version.ToString(),
                SystemVersion = "#1",
                Hardware = "x86_64"
            };
        }
        else if (OperatingSystem.IsMacOS())
        {
            data.PlatformSpecificData.DesktopMacos = new NativeDesktopMacOSData
            {
                SystemVersion = Environment.OSVersion.Version.ToString(),
                HwModel = "MacBookPro",
                CompiledCpuType = "0"
            };
        }

        return data;
    }

    private ClientTokenRequest SolveChallenge(ChallengesResponse challenges)
    {
        var request = new ClientTokenRequest
        {
            RequestType = ClientTokenRequestType.RequestChallengeAnswersRequest,
            ChallengeAnswers = new ChallengeAnswersRequest
            {
                State = challenges.State
            }
        };

        foreach (var challenge in challenges.Challenges)
        {
            if (challenge.Type == ChallengeType.ChallengeHashCash &&
                challenge.EvaluateHashcashParameters != null)
            {
                var hashParams = challenge.EvaluateHashcashParameters;
                var prefix = Convert.FromHexString(hashParams.Prefix);

                // Use the existing HashcashSolver
                var (suffix, duration) = HashcashSolver.Solve(
                    context: [],
                    prefix: prefix,
                    targetLength: hashParams.Length);

                _logger?.LogDebug("Hashcash challenge solved in {Duration}ms", duration.TotalMilliseconds);

                var answer = new ChallengeAnswer
                {
                    ChallengeType = ChallengeType.ChallengeHashCash,
                    HashCash = new HashCashAnswer
                    {
                        Suffix = Convert.ToHexString(suffix)
                    }
                };

                request.ChallengeAnswers.Answers.Add(answer);
            }
        }

        return request;
    }

    private async Task<ClientTokenResponse> SendTokenRequestAsync(
        ClientTokenRequest request, CancellationToken ct)
    {
        var requestBytes = request.ToByteArray();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint);
        httpRequest.Content = new ByteArrayContent(requestBytes);
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-protobuf"));

        var response = await _httpClient.SendAsync(httpRequest, ct);
        response.EnsureSuccessStatusCode();

        var responseBytes = await response.Content.ReadAsByteArrayAsync(ct);
        return ClientTokenResponse.Parser.ParseFrom(responseBytes);
    }
}
