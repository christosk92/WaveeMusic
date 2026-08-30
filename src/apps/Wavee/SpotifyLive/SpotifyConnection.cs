using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Wavee.Backend.Spotify;
using Wavee.Protocol;

namespace Wavee.SpotifyLive;

public sealed record SpotifyWelcome(string Username, string AccountType, byte[] ReusableCredentials, string ReusableType,
    ProductInfo? Product = null, string? Country = null);

/// <summary>The AP rejected the credential (AuthFailure) — distinct from transient/network errors, so a caller can drop
/// stored credentials and re-authorize WITHOUT wiping a valid credential on a mere connectivity blip.</summary>
public sealed class SpotifyAuthRejectedException(string message) : Exception(message);

/// <summary>The AP asked the client to reconnect elsewhere (login_failed = TryAnotherAP). The caller should retry the
/// handshake against the NEXT resolved access point rather than failing.</summary>
public sealed class SpotifyTryAnotherApException(string message) : Exception(message);

// The real AP handshake + login over a live socket, composing the unit-tested backend crypto (DiffieHellman,
// ApKeyDerivation, ApCodec) with the vendored protobuf wire messages (ClientHello/APResponse/ClientResponseEncrypted/
// APWelcome). The OAuth device-code access token is presented as AUTHENTICATION_SPOTIFY_TOKEN.
public static class SpotifyConnection
{
    const byte CmdLogin = 0xAB, CmdApWelcome = 0xAC, CmdAuthFailure = 0xAD, CmdCountryCode = 0x1b, CmdProductInfo = 0x50;

    /// <summary>The original login (returns just the welcome; the caller owns + disposes the socket).</summary>
    public static async Task<SpotifyWelcome> HandshakeAndLoginAsync(IDuplexStream stream, Credential cred, string deviceId, CancellationToken ct)
        => (await HandshakeRetainAsync(stream, cred, deviceId, ct).ConfigureAwait(false)).Welcome;

    /// <summary>Handshake + login that ALSO returns the negotiated Shannon <see cref="ApCodec"/> so the caller can keep the
    /// socket alive as a persistent AP channel (Mercury / audio-key / packets) — Stage F's key path. The caller owns the
    /// socket lifetime (it is NOT disposed here).</summary>
    public static async Task<(SpotifyWelcome Welcome, ApCodec Codec)> HandshakeRetainAsync(IDuplexStream stream, Credential cred, string deviceId, CancellationToken ct)
    {
        var dh = DiffieHellman.Generate();
        var acc = new List<byte>();

        // 1. ClientHello: [0x00,0x04][size:4 BE][protobuf]
        var nonce = new byte[16];
        RandomNumberGenerator.Fill(nonce);
        var hello = new ClientHello
        {
            BuildInfo = new BuildInfo { Product = Product.Client, Platform = Platform.Win32X86, Version = 124200447 },
            CryptosuitesSupported = { Cryptosuite.Shannon },
            LoginCryptoHello = new LoginCryptoHelloUnion
            {
                DiffieHellman = new LoginCryptoDiffieHellmanHello { Gc = ByteString.CopyFrom(dh.PublicKey), ServerKeysKnown = 1 },
            },
            ClientNonce = ByteString.CopyFrom(nonce),
            Padding = ByteString.CopyFrom(new byte[] { 0x1e }),
        };
        var helloMsg = hello.ToByteArray();
        var helloFrame = new byte[2 + 4 + helloMsg.Length];
        helloFrame[0] = 0x00; helloFrame[1] = 0x04;
        BinaryPrimitives.WriteUInt32BigEndian(helloFrame.AsSpan(2), (uint)helloFrame.Length);
        helloMsg.CopyTo(helloFrame.AsSpan(6));
        acc.AddRange(helloFrame);
        await stream.WriteAsync(helloFrame, ct).ConfigureAwait(false);

        // 2. APResponse: [size:4 BE][protobuf]
        var sizeBuf = new byte[4];
        await stream.ReadExactAsync(sizeBuf, ct).ConfigureAwait(false);
        acc.AddRange(sizeBuf);
        int size = (int)BinaryPrimitives.ReadUInt32BigEndian(sizeBuf);
        if (size < 4 || size > (1 << 20)) throw new InvalidDataException($"APResponse frame size out of range: {size}");   // untrusted, pre-auth
        var respMsg = new byte[size - 4];
        await stream.ReadExactAsync(respMsg, ct).ConfigureAwait(false);
        acc.AddRange(respMsg);
        var apResponse = APResponseMessage.Parser.ParseFrom(respMsg);
        if (apResponse.Challenge is null)
        {
            if (apResponse.LoginFailed?.ErrorCode == ErrorCode.TryAnotherAp)
                throw new SpotifyTryAnotherApException("AP asked to try another access point");
            throw new InvalidOperationException($"AP sent no challenge (upgrade={apResponse.Upgrade != null}, loginFailed={apResponse.LoginFailed?.ErrorCode})");
        }
        var dhChallenge = apResponse.Challenge.LoginCryptoChallenge.DiffieHellman;
        var gs = dhChallenge.Gs.ToByteArray();
        // SECURITY: the AP socket is raw TCP — verify the server's RSA signature over gs BEFORE deriving keys, otherwise an
        // active MITM could negotiate the channel and read the login (token) packet.
        if (!ApSignature.Verify(gs, dhChallenge.GsSignature.ToByteArray()))
            throw new InvalidOperationException("AP server signature verification failed - possible MITM; aborting handshake.");

        // 3. derive keys + ClientResponsePlaintext (the DH challenge HMAC)
        var keys = ApKeyDerivation.Derive(dh.SharedSecret(gs), acc.ToArray());
        var clientResp = new ClientResponsePlaintext
        {
            LoginCryptoResponse = new LoginCryptoResponseUnion { DiffieHellman = new LoginCryptoDiffieHellmanResponse { Hmac = ByteString.CopyFrom(keys.Challenge) } },
            PowResponse = new PoWResponseUnion(),
            CryptoResponse = new CryptoResponseUnion(),
        };
        var crBytes = clientResp.ToByteArray();
        var crFrame = new byte[4 + crBytes.Length];
        BinaryPrimitives.WriteUInt32BigEndian(crFrame.AsSpan(0), (uint)crFrame.Length);
        crBytes.CopyTo(crFrame.AsSpan(4));
        await stream.WriteAsync(crFrame, ct).ConfigureAwait(false);

        // 4. encrypted channel — send Login (the OAuth access token as AUTHENTICATION_SPOTIFY_TOKEN)
        var codec = new ApCodec(keys.SendKey, keys.ReceiveKey);
        var creds = new LoginCredentials
        {
            Typ = cred.Kind == CredentialKind.ReusableBlob
                ? AuthenticationType.AuthenticationStoredSpotifyCredentials   // stored reusable blob → no re-auth
                : AuthenticationType.AuthenticationSpotifyToken,              // fresh OAuth access token
            AuthData = ByteString.CopyFrom(cred.Kind == CredentialKind.ReusableBlob
                ? Convert.FromBase64String(cred.Secret)
                : Encoding.UTF8.GetBytes(cred.Secret)),
        };
        if (cred.Kind == CredentialKind.ReusableBlob && !string.IsNullOrEmpty(cred.Username))
            creds.Username = cred.Username;
        var login = new ClientResponseEncrypted
        {
            LoginCredentials = creds,
            SystemInfo = new SystemInfo
            {
                CpuFamily = CpuFamily.CpuX8664,
                Os = Os.Windows,
                SystemInformationString = "wavee-fluentgpu",
                DeviceId = deviceId,   // persisted + reused across connects (a fresh id each time churns the device list)
            },
        };
        await stream.WriteAsync(codec.Encode(CmdLogin, login.ToByteArray()), ct).ConfigureAwait(false);

        // 5. read the login result + the CountryCode (0x1b) / ProductInfo (0x50) the AP pushes on success. Bounded by a
        //    timeout so a missing trailing packet can't hang login: 15s for the result, then a brief 3s window for the trailers.
        using var loginCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        loginCts.CancelAfter(TimeSpan.FromSeconds(15));
        APWelcome? welcome = null;
        ProductInfo? product = null;
        string? country = null;
        try
        {
            while (welcome is null || product is null)
            {
                var (cmd, payload) = await ReceivePacketAsync(stream, codec, loginCts.Token).ConfigureAwait(false);
                switch (cmd)
                {
                    case CmdApWelcome:
                        welcome = APWelcome.Parser.ParseFrom(payload);
                        loginCts.CancelAfter(TimeSpan.FromSeconds(3));   // brief window to catch the trailing product/country
                        break;
                    case CmdAuthFailure:
                        string detail;
                        try { var f = APLoginFailed.Parser.ParseFrom(payload); detail = $"{f.ErrorCode} {f.ErrorDescription}"; }
                        catch { detail = $"{payload.Length} bytes"; }
                        throw new SpotifyAuthRejectedException($"Spotify login failed: {detail}");
                    case CmdCountryCode:
                        country = Encoding.UTF8.GetString(payload);
                        break;
                    case CmdProductInfo:
                        product = ProductInfo.Parse(Encoding.UTF8.GetString(payload));
                        break;
                    // other packets (ping 0x04, legacy) are ignored
                }
            }
        }
        catch (OperationCanceledException) when (loginCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Timed out waiting for the trailing packets — proceed with whatever arrived (product/country may be null).
        }
        if (welcome is null) throw new InvalidOperationException("no APWelcome/AuthFailure after login");
        var result = new SpotifyWelcome(welcome.CanonicalUsername, welcome.AccountTypeLoggedIn.ToString(),
            welcome.ReusableAuthCredentials.ToByteArray(), welcome.ReusableAuthCredentialsType.ToString(), product, country);
        return (result, codec);
    }

    static async Task<(byte Cmd, byte[] Payload)> ReceivePacketAsync(IDuplexStream stream, ApCodec codec, CancellationToken ct)
    {
        var header = new byte[3];
        await stream.ReadExactAsync(header, ct).ConfigureAwait(false);
        var (cmd, len) = codec.BeginDecode(header);
        var rest = new byte[len + 4];
        await stream.ReadExactAsync(rest, ct).ConfigureAwait(false);
        var payload = codec.EndDecode(rest.AsSpan(0, len), rest.AsSpan(len, 4));
        return (cmd, payload);
    }
}
