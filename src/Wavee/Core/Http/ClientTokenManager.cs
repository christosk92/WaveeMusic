using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
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
    // Mirror what current desktop sends on the wire. Decoded from a live
    // /v1/clienttoken POST capture (sazct/03_c.txt). The git-hash suffix is
    // significant — Spotify's server allowlists "real-looking" version strings
    // and tags requests with stale/no-hash versions as untrusted, which then
    // causes downstream gabo events to be rejected with reason=3.
    private const string ClientVersion = "1.2.88.483.g8aa8628e";
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
            // The proto field is named `device_id` but desktop puts the
            // current Windows user's SID here, NOT the Spotify device id.
            // Wire decode of sazct/03_c.txt confirms: this carries
            // "S-1-5-21-...". Falls back to Spotify device id on non-Windows
            // or if SID lookup fails — keeps shape non-empty.
            DeviceId = TryGetWindowsUserSid() ?? _config.DeviceId,
            PlatformSpecificData = new PlatformSpecificData()
        };

        if (OperatingSystem.IsWindows())
        {
            // Wire-observed shape (desktop 1.2.88.483 on Win 11 26200 ARM64):
            //   #1 os_version=10  #3 os_build=26200  #4 platform_id=2
            //   #5=9  #6=9  #8 pe_machine=43620 (ARM64)
            // Desktop does NOT set #7 image_file_machine or #10 unknown_value_10
            // — omitting them is part of the genuine-client signature.
            data.PlatformSpecificData.DesktopWindows = new NativeDesktopWindowsData
            {
                OsVersion = 10,
                OsBuild = Environment.OSVersion.Version.Build,  // real build (e.g. 26200)
                PlatformId = 2,
                UnknownValue5 = 9,
                UnknownValue6 = 9,
                PeMachine = GetPeMachineForCurrentArch(),
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

    /// <summary>
    /// PE machine constants for the current process architecture. Maps to
    /// <see cref="System.Reflection.PortableExecutable.Machine"/> values.
    /// Wire-observed: desktop on ARM64 sends 43620 (0xAA64).
    /// </summary>
    private static int GetPeMachineForCurrentArch() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X86 => 332,        // 0x014C
        Architecture.X64 => 34404,      // 0x8664
        Architecture.Arm => 452,        // 0x01C4 (ARM Thumb-2)
        Architecture.Arm64 => 43620,    // 0xAA64
        _ => 34404,                     // sensible fallback
    };

    /// <summary>
    /// Returns the current Windows user's SID (e.g. "S-1-5-21-..."), or null
    /// if not running on Windows or SID lookup fails. This is part of the
    /// genuine-client attestation chain — desktop puts it in
    /// <c>ConnectivitySdkData.device_id</c>.
    /// </summary>
    [SupportedOSPlatformGuard("windows")]
    private string? TryGetWindowsUserSid()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return GetWindowsUserSidCore();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Windows SID lookup failed; falling back to device id");
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string GetWindowsUserSidCore()
    {
        // Wire-observed: desktop puts the LOCAL MACHINE SID here, not the
        // current user SID. Format: "S-1-5-21-X-Y-Z" (no trailing user RID).
        // For Microsoft-Account-signed-in users, WindowsIdentity.User returns
        // an AAD SID ("S-1-12-1-...") which Spotify's allowlist doesn't
        // recognize. Walk the local profile list in HKLM and return the first
        // local-account SID prefix.
        var local = TryReadLocalMachineSidFromRegistry();
        if (!string.IsNullOrEmpty(local)) return local!;

        // Final fallback: AAD or whatever WindowsIdentity gives us. Better
        // than nothing — at least the field is non-empty.
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? string.Empty;
    }

    /// <summary>
    /// Reads the local machine SID by enumerating
    /// <c>HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList</c>
    /// and returning the common <c>S-1-5-21-X-Y-Z</c> prefix shared among
    /// real local profiles. Returns null on failure.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string? TryReadLocalMachineSidFromRegistry()
    {
        try
        {
            using var profiles = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (profiles == null) return null;

            foreach (var name in profiles.GetSubKeyNames())
            {
                // Local-account SIDs are S-1-5-21-{4 numeric components}.
                // Strip the trailing "-RID" to get the machine SID prefix.
                if (!name.StartsWith("S-1-5-21-", StringComparison.Ordinal)) continue;
                var lastDash = name.LastIndexOf('-');
                if (lastDash <= "S-1-5-21".Length) continue;
                return name[..lastDash];
            }
        }
        catch
        {
            // Registry access can fail (permissions, etc.) — return null and
            // let caller fall back.
        }
        return null;
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
