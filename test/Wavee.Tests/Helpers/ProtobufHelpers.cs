using Google.Protobuf;
using Wavee.Core.Authentication;
using Wavee.Protocol;

namespace Wavee.Tests.Helpers;

/// <summary>
/// Helper methods for creating test protobuf messages.
/// </summary>
internal static class ProtobufHelpers
{
    /// <summary>
    /// Creates a ClientResponseEncrypted packet with test data.
    /// </summary>
    public static ClientResponseEncrypted CreateClientResponseEncrypted(
        string username,
        AuthenticationType authType,
        byte[] authData,
        string deviceId = "test-device")
    {
        return new ClientResponseEncrypted
        {
            LoginCredentials = new LoginCredentials
            {
                Username = username,
                Typ = authType,
                AuthData = ByteString.CopyFrom(authData)
            },
            SystemInfo = new SystemInfo
            {
                CpuFamily = CpuFamily.CpuX8664,
                Os = Os.Windows,
                SystemInformationString = "Wavee-Test-1.0",
                DeviceId = deviceId
            },
            VersionString = "Wavee 1.0"
        };
    }

    /// <summary>
    /// Creates an APWelcome packet (successful authentication response).
    /// </summary>
    public static byte[] CreateAPWelcome(
        string canonicalUsername,
        byte[] reusableCredentials,
        AuthenticationType credentialsType = AuthenticationType.AuthenticationStoredSpotifyCredentials)
    {
        var welcome = new APWelcome
        {
            CanonicalUsername = canonicalUsername,
            ReusableAuthCredentialsType = credentialsType,
            ReusableAuthCredentials = ByteString.CopyFrom(reusableCredentials)
        };

        return welcome.ToByteArray();
    }

    /// <summary>
    /// Creates an APLoginFailed packet with specified error code.
    /// </summary>
    public static byte[] CreateAPLoginFailed(ErrorCode errorCode)
    {
        var failure = new APLoginFailed
        {
            ErrorCode = errorCode
        };

        return failure.ToByteArray();
    }

    /// <summary>
    /// Creates valid test credentials.
    /// </summary>
    public static Credentials CreateValidCredentials(
        string? username = null,
        AuthenticationType authType = AuthenticationType.AuthenticationUserPass)
    {
        return new Credentials
        {
            Username = username ?? TestHelpers.CreateUsername(),
            AuthType = authType,
            AuthData = TestHelpers.GenerateRandomBytes(32)
        };
    }
}
