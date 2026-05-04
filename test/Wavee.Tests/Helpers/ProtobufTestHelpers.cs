using Google.Protobuf;
using Wavee.Protocol.Login;

namespace Wavee.Tests.Helpers;

/// <summary>
/// Helper methods for creating test protobuf messages.
/// </summary>
internal static class ProtobufTestHelpers
{
    /// <summary>
    /// Creates a Login OK response with access token.
    /// </summary>
    public static LoginResponse CreateLoginOkResponse(
        string username = "testuser",
        string accessToken = "test_access_token_abc123",
        int expiresInSeconds = 3600,
        byte[]? storedCredential = null)
    {
        return new LoginResponse
        {
            Ok = new LoginOk
            {
                Username = username,
                AccessToken = accessToken,
                AccessTokenExpiresIn = expiresInSeconds,
                StoredCredential = ByteString.CopyFrom(storedCredential ?? new byte[] { 0x01, 0x02, 0x03 })
            }
        };
    }

    /// <summary>
    /// Creates a Login Error response.
    /// </summary>
    public static LoginResponse CreateLoginErrorResponse(LoginError error)
    {
        return new LoginResponse
        {
            Error = error
        };
    }

    /// <summary>
    /// Creates a Login response with hashcash challenge.
    /// </summary>
    public static LoginResponse CreateLoginResponseWithHashcashChallenge(
        byte[]? prefix = null,
        int length = 10,
        byte[]? loginContext = null)
    {
        var hashcashChallenge = new Wavee.Protocol.Login.HashcashChallenge
        {
            Prefix = ByteString.CopyFrom(prefix ?? new byte[] { 0x01, 0x02, 0x03, 0x04 }),
            Length = length
        };

        return new LoginResponse
        {
            Challenges = new Challenges
            {
                Challenges_ =
                {
                    new Challenge
                    {
                        Hashcash = hashcashChallenge
                    }
                }
            },
            LoginContext = ByteString.CopyFrom(loginContext ?? new byte[] { 0xAA, 0xBB, 0xCC })
        };
    }

    /// <summary>
    /// Creates a Login response with code challenge (not supported).
    /// </summary>
    public static LoginResponse CreateLoginResponseWithCodeChallenge()
    {
        var codeChallenge = new Wavee.Protocol.Login.CodeChallenge
        {
            Method = Wavee.Protocol.Login.CodeChallenge.Types.Method.Sms,
            CodeLength = 8
        };

        return new LoginResponse
        {
            Challenges = new Challenges
            {
                Challenges_ =
                {
                    new Challenge
                    {
                        Code = codeChallenge
                    }
                }
            }
        };
    }

    /// <summary>
    /// Creates a ClientInfo for Login5 requests.
    /// </summary>
    public static ClientInfo CreateClientInfo(
        string clientId = "test-client-id",
        string deviceId = "test-device-id")
    {
        return new ClientInfo
        {
            ClientId = clientId,
            DeviceId = deviceId
        };
    }

    /// <summary>
    /// Creates a StoredCredential for Login5 requests.
    /// </summary>
    public static Wavee.Protocol.Login.StoredCredential CreateStoredCredential(
        string username = "testuser",
        byte[]? data = null)
    {
        return new Wavee.Protocol.Login.StoredCredential
        {
            Username = username,
            Data = ByteString.CopyFrom(data ?? new byte[] { 0x01, 0x02, 0x03, 0x04 })
        };
    }
}
