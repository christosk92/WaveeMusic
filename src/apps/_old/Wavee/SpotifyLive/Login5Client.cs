using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Wavee.Backend.Spotify;
using Wavee.Protocol.Login;

namespace Wavee.SpotifyLive;

// Mints a short-lived spclient access token from a stored reusable credential (login5.spotify.com/v3/login). The reusable
// blob comes from the AP APWelcome; login5 exchanges it for the bearer spclient accepts. Solves the login5 hashcash if
// challenged — note this variant hashes context = login_context with a RAW-byte suffix (the client-token variant uses an
// empty context + hex suffix). Lifted faithfully from the desktop wire.
public sealed class Login5Client
{
    const string Endpoint = "https://login5.spotify.com/v3/login";
    const int MaxRetries = 4;

    readonly string _clientId;
    readonly string _deviceId;

    public Login5Client(string clientId, string deviceId)
    {
        _clientId = clientId;
        _deviceId = deviceId;
    }

    public sealed record AccessToken(string Token, DateTimeOffset ExpiresAt);

    public async Task<AccessToken> GetAccessTokenAsync(string username, byte[] storedCredential, string? clientToken, CancellationToken ct = default)
    {
        if (storedCredential.Length == 0) throw new InvalidOperationException("login5: empty stored credential");

        var request = new LoginRequest
        {
            ClientInfo = new ClientInfo { ClientId = _clientId, DeviceId = _deviceId },
            StoredCredential = new StoredCredential { Username = username, Data = ByteString.CopyFrom(storedCredential) },
        };

        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            var response = await SendAsync(request, clientToken, ct).ConfigureAwait(false);
            if (response.Ok is { } ok)
                return new AccessToken(ok.AccessToken, DateTimeOffset.UtcNow.AddSeconds(ok.AccessTokenExpiresIn > 0 ? ok.AccessTokenExpiresIn : 3600));

            if (response.Challenges is { } ch && ch.Challenges_.Count > 0)
            {
                SolveChallenges(request, response);   // solve + re-submit with the same login_context
                continue;
            }
            if (response.Error != LoginError.UnknownError)
                throw new InvalidOperationException($"login5 error: {response.Error}");
        }
        throw new InvalidOperationException("login5: no access token after retries");
    }

    static void SolveChallenges(LoginRequest request, LoginResponse response)
    {
        var solutions = new ChallengeSolutions();
        foreach (var challenge in response.Challenges.Challenges_)
        {
            if (challenge.Hashcash is { } hc)
            {
                var sw = Stopwatch.StartNew();
                var suffix = HashcashSolver.Solve(response.LoginContext.ToByteArray(), hc.Prefix.ToByteArray(), hc.Length);
                sw.Stop();
                solutions.Solutions.Add(new ChallengeSolution
                {
                    Hashcash = new HashcashSolution { Suffix = ByteString.CopyFrom(suffix), Duration = Duration.FromTimeSpan(sw.Elapsed) },
                });
            }
        }
        request.ChallengeSolutions = solutions;
        request.LoginContext = response.LoginContext;
    }

    static async Task<LoginResponse> SendAsync(LoginRequest request, string? clientToken, CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new ByteArrayContent(request.ToByteArray()) };
        msg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-protobuf"));
        if (!string.IsNullOrEmpty(clientToken)) msg.Headers.TryAddWithoutValidation("client-token", clientToken);
        using var resp = await SharedHttp.Client.SendAsync(msg, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return LoginResponse.Parser.ParseFrom(bytes);
    }
}
