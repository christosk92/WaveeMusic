using System.Runtime.InteropServices;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Wavee.Core.Connection;
using Wavee.Protocol;

namespace Wavee.Core.Authentication;

/// <summary>
/// Handles Spotify Access Point authentication after handshake completion.
/// </summary>
/// <remarks>
/// Authentication Flow:
/// <list type="number">
/// <item>Build ClientResponseEncrypted packet with credentials + system info</item>
/// <item>Send packet via ApTransport (command: 0xAB = Login)</item>
/// <item>Receive response:
///   <list type="bullet">
///   <item>APWelcome (0xAC) → Success, extract reusable credentials</item>
///   <item>APLoginFailed (0xAD) → Failure, extract error code</item>
///   </list>
/// </item>
/// </list>
///
/// The authenticator is stateless and uses structured logging for diagnostics.
/// </remarks>
internal static class Authenticator
{
    private const byte PacketTypeLogin = 0xAB;
    private const byte PacketTypeApWelcome = 0xAC;
    private const byte PacketTypeAuthFailure = 0xAD;

    /// <summary>
    /// Authenticates with Spotify Access Point using provided credentials.
    /// </summary>
    /// <param name="transport">ApTransport from completed handshake.</param>
    /// <param name="credentials">Credentials (password, token, or blob).</param>
    /// <param name="deviceId">Unique device identifier.</param>
    /// <param name="logger">Optional logger for diagnostic output.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Reusable credentials (blob) for future logins.</returns>
    /// <exception cref="ArgumentNullException">Thrown if transport or credentials is null.</exception>
    /// <exception cref="ArgumentException">Thrown if deviceId is null or whitespace.</exception>
    /// <exception cref="AuthenticationException">Thrown if login fails.</exception>
    public static async Task<Credentials> AuthenticateAsync(
        ApTransport transport,
        Credentials credentials,
        string deviceId,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        logger?.LogDebug("Building authentication packet for {AuthType}", credentials.AuthType);

        // Build ClientResponseEncrypted packet
        var packet = BuildClientResponseEncrypted(credentials, deviceId);
        var packetBytes = packet.ToByteArray();

        logger?.LogInformation("Authenticating as '{Username}' using {AuthType}",
            credentials.Username ?? "<token>",
            credentials.AuthType);

        // Send login packet
        await transport.SendAsync(PacketTypeLogin, packetBytes, cancellationToken);

        // Receive response
        var response = await transport.ReceiveAsync(cancellationToken);
        if (response is null)
        {
            logger?.LogError("Transport closed during authentication");
            throw new AuthenticationException(
                AuthenticationFailureReason.TransportClosed,
                "Transport closed during authentication");
        }

        var (command, payload) = response.Value;

        return command switch
        {
            PacketTypeApWelcome => HandleWelcome(payload, logger),
            PacketTypeAuthFailure => HandleFailure(payload, logger),
            _ => throw new AuthenticationException(
                AuthenticationFailureReason.UnexpectedPacket,
                $"Unexpected packet type during authentication: 0x{command:X2}")
        };
    }

    /// <summary>
    /// Builds the ClientResponseEncrypted packet with credentials and system information.
    /// </summary>
    private static ClientResponseEncrypted BuildClientResponseEncrypted(
        Credentials credentials,
        string deviceId)
    {
        var packet = new ClientResponseEncrypted
        {
            LoginCredentials = new LoginCredentials
            {
                Typ = credentials.AuthType,
                AuthData = ByteString.CopyFrom(credentials.AuthData)
            },
            SystemInfo = new SystemInfo
            {
                CpuFamily = DetectCpuFamily(),
                Os = DetectOperatingSystem(),
                SystemInformationString = GetSystemInformationString(),
                DeviceId = deviceId
            },
            VersionString = GetVersionString()
        };

        // Username is optional (not used for token auth)
        if (credentials.Username is not null)
        {
            packet.LoginCredentials.Username = credentials.Username;
        }

        return packet;
    }

    /// <summary>
    /// Handles successful authentication (APWelcome packet).
    /// </summary>
    private static Credentials HandleWelcome(byte[] payload, ILogger? logger)
    {
        var welcome = APWelcome.Parser.ParseFrom(payload);

        logger?.LogInformation("Authenticated as '{Username}'", welcome.CanonicalUsername);

        return new Credentials
        {
            Username = welcome.CanonicalUsername,
            AuthType = welcome.ReusableAuthCredentialsType,
            AuthData = welcome.ReusableAuthCredentials.ToByteArray()
        };
    }

    /// <summary>
    /// Handles authentication failure (APLoginFailed packet).
    /// </summary>
    private static Credentials HandleFailure(byte[] payload, ILogger? logger)
    {
        var failure = APLoginFailed.Parser.ParseFrom(payload);
        var errorCode = failure.ErrorCode;

        var errorMessage = GetErrorMessage(errorCode);
        logger?.LogError("Authentication failed: {ErrorCode} - {ErrorMessage}",
            errorCode,
            errorMessage);

        throw new AuthenticationException(
            MapErrorCode(errorCode),
            $"Authentication failed: {errorMessage}");
    }

    /// <summary>
    /// Detects the CPU architecture.
    /// </summary>
    private static CpuFamily DetectCpuFamily()
    {
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => CpuFamily.CpuX86,
            Architecture.X64 => CpuFamily.CpuX8664,
            Architecture.Arm => CpuFamily.CpuArm,
            Architecture.Arm64 => CpuFamily.CpuArm,  // Protocol doesn't have ARM64, use ARM
            _ => CpuFamily.CpuUnknown
        };
    }

    /// <summary>
    /// Detects the operating system.
    /// </summary>
    private static Os DetectOperatingSystem()
    {
        if (OperatingSystem.IsWindows()) return Os.Windows;
        if (OperatingSystem.IsMacOS()) return Os.Osx;
        if (OperatingSystem.IsLinux()) return Os.Linux;
        if (OperatingSystem.IsAndroid()) return Os.Android;
        if (OperatingSystem.IsIOS()) return Os.Iphone;
        if (OperatingSystem.IsFreeBSD()) return Os.Freebsd;
        return Os.Unknown;
    }

    /// <summary>
    /// Gets system information string for telemetry.
    /// </summary>
    private static string GetSystemInformationString()
    {
        var osDescription = RuntimeInformation.OSDescription;
        var framework = RuntimeInformation.FrameworkDescription;
        return $"Wavee-{osDescription}-{framework}";
    }

    /// <summary>
    /// Gets the Wavee version string.
    /// </summary>
    private static string GetVersionString()
    {
        var version = typeof(Authenticator).Assembly.GetName().Version;
        return $"Wavee {version?.Major ?? 0}.{version?.Minor ?? 0}";
    }

    /// <summary>
    /// Maps Spotify error codes to authentication failure reasons.
    /// </summary>
    private static AuthenticationFailureReason MapErrorCode(ErrorCode code) => code switch
    {
        ErrorCode.BadCredentials => AuthenticationFailureReason.BadCredentials,
        ErrorCode.CouldNotValidateCredentials => AuthenticationFailureReason.BadCredentials,
        ErrorCode.PremiumAccountRequired => AuthenticationFailureReason.PremiumRequired,
        ErrorCode.TryAnotherAp => AuthenticationFailureReason.TryAnotherAp,
        ErrorCode.ProtocolError => AuthenticationFailureReason.ProtocolError,
        _ => AuthenticationFailureReason.LoginFailed
    };

    /// <summary>
    /// Gets a human-readable error message for an error code.
    /// </summary>
    private static string GetErrorMessage(ErrorCode code) => code switch
    {
        ErrorCode.ProtocolError => "Protocol error",
        ErrorCode.TryAnotherAp => "Try another access point",
        ErrorCode.BadConnectionId => "Bad connection ID",
        ErrorCode.TravelRestriction => "Travel restriction",
        ErrorCode.PremiumAccountRequired => "Premium account required",
        ErrorCode.BadCredentials => "Bad credentials",
        ErrorCode.CouldNotValidateCredentials => "Could not validate credentials",
        ErrorCode.AccountExists => "Account exists",
        ErrorCode.ExtraVerificationRequired => "Extra verification required",
        ErrorCode.InvalidAppKey => "Invalid app key",
        ErrorCode.ApplicationBanned => "Application banned",
        _ => $"Unknown error: {code}"
    };
}
