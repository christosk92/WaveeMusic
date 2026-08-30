using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Google.Protobuf;
using Microsoft.Win32;
using Wavee.Backend.Audio;
using Wavee.Backend.Spotify;
using Wavee.Protocol.ClientToken;

namespace Wavee.SpotifyLive;

// Obtains + caches the Spotify client-token (required on every spclient request — extended-metadata 403s without it).
// Wire shape decoded from desktop 1.2.93.667 capture: CLIENT_DATA carries client_version + client_id +
// ConnectivitySdkData{ platform_specific_data.desktop_windows, device_id = local-machine SID prefix }.
public sealed class ClientTokenClient
{
    const string Endpoint = "https://clienttoken.spotify.com/v1/clienttoken";
    const int MaxChallengeRetries = 3;

    readonly string _clientId;
    readonly string _fallbackDeviceId;
    readonly SemaphoreSlim _lock = new(1, 1);
    string? _cached;
    DateTimeOffset _expiresAt;

    public ClientTokenClient(string clientId, string deviceId)
    {
        _clientId = clientId;
        _fallbackDeviceId = deviceId;
    }

    /// <summary>A valid client-token, fetching/refreshing as needed. Returns null on failure (the caller proceeds without).</summary>
    public async Task<string?> GetAsync(CancellationToken ct = default)
    {
        if (_cached is not null && DateTimeOffset.UtcNow < _expiresAt.AddMinutes(-5)) return _cached;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cached is not null && DateTimeOffset.UtcNow < _expiresAt.AddMinutes(-5)) return _cached;
            return await FetchAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return null;   // best-effort — a missing client-token surfaces downstream as a 403 the caller can report
        }
        finally { _lock.Release(); }
    }

    async Task<string?> FetchAsync(CancellationToken ct)
    {
        var request = BuildInitial();
        for (int attempt = 0; attempt < MaxChallengeRetries; attempt++)
        {
            var response = await SendAsync(request, ct).ConfigureAwait(false);
            switch (response.ResponseType)
            {
                case ClientTokenResponseType.ResponseGrantedTokenResponse:
                    var g = response.GrantedToken;
                    _cached = g.Token;
                    _expiresAt = DateTimeOffset.UtcNow.AddSeconds(g.RefreshAfterSeconds > 0 ? g.RefreshAfterSeconds : 7200);
                    return _cached;
                case ClientTokenResponseType.ResponseChallengesResponse:
                    request = SolveChallenge(response.Challenges);
                    break;
                default:
                    return null;
            }
        }
        return null;
    }

    ClientTokenRequest BuildInitial() => new()
    {
        RequestType = ClientTokenRequestType.RequestClientDataRequest,
        ClientData = new ClientDataRequest
        {
            ClientVersion = SpotifyHeaders.ClientVersion,
            ClientId = _clientId,
            ConnectivitySdkData = BuildConnectivity(),
        },
    };

    ConnectivitySdkData BuildConnectivity()
    {
        // device_id is the local-machine SID prefix on desktop (S-1-5-21-…), NOT the Spotify Connect device id.
        var data = new ConnectivitySdkData
        {
            DeviceId = TryGetWindowsMachineSid() ?? _fallbackDeviceId,
            PlatformSpecificData = new PlatformSpecificData(),
        };
        if (OperatingSystem.IsWindows())
            data.PlatformSpecificData.DesktopWindows = new NativeDesktopWindowsData
            {
                OsVersion = 10,
                OsBuild = Environment.OSVersion.Version.Build,
                PlatformId = 2,
                UnknownValue5 = 12,   // wire-observed on 1.2.93.667 ARM64 desktop
                UnknownValue6 = 12,
                PeMachine = PeMachine(),
                // image_file_machine + unknown_value_10 intentionally omitted — part of genuine-client signature
            };
        else if (OperatingSystem.IsLinux())
            data.PlatformSpecificData.DesktopLinux = new NativeDesktopLinuxData
            {
                SystemName = "Linux",
                SystemRelease = Environment.OSVersion.Version.ToString(),
                SystemVersion = "#1",
                Hardware = "x86_64",
            };
        else if (OperatingSystem.IsMacOS())
            data.PlatformSpecificData.DesktopMacos = new NativeDesktopMacOSData
            {
                SystemVersion = Environment.OSVersion.Version.ToString(),
                HwModel = "MacBookPro",
                CompiledCpuType = "0",
            };
        return data;
    }

    static int PeMachine() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X86 => 332,
        Architecture.X64 => 34404,
        Architecture.Arm => 452,
        Architecture.Arm64 => 43620,
        _ => 34404,
    };

    [SupportedOSPlatformGuard("windows")]
    static string? TryGetWindowsMachineSid()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return ReadLocalMachineSidFromRegistry() ?? GetCurrentUserSid(); }
        catch { return null; }
    }

    [SupportedOSPlatform("windows")]
    static string? ReadLocalMachineSidFromRegistry()
    {
        using var profiles = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
        if (profiles is null) return null;
        foreach (var name in profiles.GetSubKeyNames())
        {
            if (!name.StartsWith("S-1-5-21-", StringComparison.Ordinal)) continue;
            var lastDash = name.LastIndexOf('-');
            if (lastDash <= "S-1-5-21".Length) continue;
            return name[..lastDash];
        }
        return null;
    }

    [SupportedOSPlatform("windows")]
    static string GetCurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? string.Empty;
    }

    static ClientTokenRequest SolveChallenge(ChallengesResponse challenges)
    {
        var request = new ClientTokenRequest
        {
            RequestType = ClientTokenRequestType.RequestChallengeAnswersRequest,
            ChallengeAnswers = new ChallengeAnswersRequest { State = challenges.State },
        };
        foreach (var challenge in challenges.Challenges)
        {
            if (challenge.Type == ChallengeType.ChallengeHashCash && challenge.EvaluateHashcashParameters is { } hp)
            {
                var suffix = HashcashSolver.Solve([], Convert.FromHexString(hp.Prefix), hp.Length);
                request.ChallengeAnswers.Answers.Add(new ChallengeAnswer
                {
                    ChallengeType = ChallengeType.ChallengeHashCash,
                    HashCash = new HashCashAnswer { Suffix = Convert.ToHexString(suffix) },
                });
            }
        }
        return request;
    }

    static async Task<ClientTokenResponse> SendAsync(ClientTokenRequest request, CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new ByteArrayContent(request.ToByteArray()) };
        msg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-protobuf"));
        msg.Headers.TryAddWithoutValidation("User-Agent", SpotifyRuntimeIdentityHost.Current.ClientTokenUserAgent);
        msg.Headers.TryAddWithoutValidation("Origin", "https://clienttoken.spotify.com");
        msg.Headers.TryAddWithoutValidation("Cache-Control", "no-cache, no-store, max-age=0");
        msg.Headers.TryAddWithoutValidation("Pragma", "no-cache");
        using var resp = await SharedHttp.Client.SendAsync(msg, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return ClientTokenResponse.Parser.ParseFrom(bytes);
    }
}
