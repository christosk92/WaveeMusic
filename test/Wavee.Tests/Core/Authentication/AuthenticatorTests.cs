using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Moq;
using Wavee.Core.Authentication;
using Wavee.Core.Connection;
using Wavee.Protocol;
using Wavee.Tests.Helpers;
using Xunit;

namespace Wavee.Tests.Core.Authentication;

/// <summary>
/// Tests for Authenticator static class.
/// Validates the CRITICAL authentication flow that gates access to Spotify services.
/// </summary>
public class AuthenticatorTests
{
    private readonly Mock<IApTransport> _mockTransport;
    private readonly Mock<ILogger> _mockLogger;
    private readonly string _testDeviceId;

    public AuthenticatorTests()
    {
        _mockTransport = MockTransportHelpers.CreateMockApTransport();
        _mockLogger = TestHelpers.CreateMockLogger();
        _testDeviceId = TestHelpers.CreateDeviceId();
    }

    // ================================================================
    // HAPPY PATH TESTS - Core authentication flows that MUST work
    // ================================================================

    [Fact]
    public async Task AuthenticateAsync_WithValidPasswordCredentials_ShouldReturnStoredCredentials()
    {
        // ============================================================
        // WHY: Validates the most common authentication path - user
        //      logging in with username and password. This must work
        //      reliably or users cannot access the service.
        // ============================================================

        // Arrange - Create password-based credentials
        var username = TestHelpers.CreateUsername();
        var password = "SecurePassword123!";
        var credentials = new Credentials
        {
            Username = username,
            AuthType = AuthenticationType.AuthenticationUserPass,
            AuthData = System.Text.Encoding.UTF8.GetBytes(password)
        };

        // Setup mock to return successful APWelcome packet
        var reusableAuthData = TestHelpers.GenerateRandomBytes(64);
        MockTransportHelpers.SetupReceiveAPWelcome(_mockTransport, username, reusableAuthData);

        // Act - Perform authentication
        var result = await Authenticator.AuthenticateAsync(
            _mockTransport.Object,
            credentials,
            _testDeviceId,
            _mockLogger.Object,
            CancellationToken.None);

        // Assert - Verify returned credentials are correct
        result.Should().NotBeNull("authentication should succeed");
        result.Username.Should().Be(username, "should return canonical username from server");
        result.AuthData.Should().BeEquivalentTo(reusableAuthData,
            "should return reusable credentials for future logins");
        result.AuthType.Should().Be(AuthenticationType.AuthenticationStoredSpotifyCredentials,
            "should convert to stored credential type for reuse");

        // Verify Login packet (0xAB) was sent exactly once
        MockTransportHelpers.VerifySendPacket(_mockTransport, 0xAB, Times.Once());
    }

    [Fact]
    public async Task AuthenticateAsync_WithValidStoredCredentials_ShouldReturnStoredCredentials()
    {
        // ============================================================
        // WHY: Validates reusing stored credentials from a previous
        //      login. This is the FAST PATH for returning users and
        //      must work to avoid forcing re-authentication.
        // ============================================================

        // Arrange - Create stored credential (from previous login)
        var username = TestHelpers.CreateUsername();
        var storedAuthData = TestHelpers.GenerateRandomBytes(64);
        var credentials = new Credentials
        {
            Username = username,
            AuthType = AuthenticationType.AuthenticationStoredSpotifyCredentials,
            AuthData = storedAuthData
        };

        // Setup mock to return APWelcome
        var newReusableAuthData = TestHelpers.GenerateRandomBytes(64);
        MockTransportHelpers.SetupReceiveAPWelcome(_mockTransport, username, newReusableAuthData);

        // Act
        var result = await Authenticator.AuthenticateAsync(
            _mockTransport.Object,
            credentials,
            _testDeviceId,
            _mockLogger.Object);

        // Assert
        result.Should().NotBeNull();
        result.Username.Should().Be(username);
        result.AuthData.Should().NotBeEmpty("should return new reusable credentials");
    }

    // ================================================================
    // ERROR HANDLING TESTS - All error paths must be validated
    // ================================================================

    [Fact]
    public async Task AuthenticateAsync_WithBadCredentials_ShouldThrowAuthenticationException_BadCredentials()
    {
        // ============================================================
        // WHY: Invalid credentials must be rejected with the CORRECT
        //      error reason so UI can show appropriate message to user.
        // ============================================================

        // Arrange
        var credentials = ProtobufHelpers.CreateValidCredentials();
        MockTransportHelpers.SetupReceiveAPLoginFailed(_mockTransport, ErrorCode.BadCredentials);

        // Act
        var act = async () => await Authenticator.AuthenticateAsync(
            _mockTransport.Object,
            credentials,
            _testDeviceId);

        // Assert - Verify correct exception type and reason
        await act.Should()
            .ThrowAsync<AuthenticationException>()
            .Where(ex => ex.Reason == AuthenticationFailureReason.BadCredentials,
                "should map BAD_CREDENTIALS error code to BadCredentials reason");
    }

    [Fact]
    public async Task AuthenticateAsync_WithPremiumRequired_ShouldThrowAuthenticationException_PremiumRequired()
    {
        // ============================================================
        // WHY: Free accounts cannot use this API - must reject with
        //      clear error so UI can prompt user to upgrade account.
        // ============================================================

        // Arrange
        var credentials = ProtobufHelpers.CreateValidCredentials();
        MockTransportHelpers.SetupReceiveAPLoginFailed(_mockTransport, ErrorCode.PremiumAccountRequired);

        // Act & Assert
        var act = async () => await Authenticator.AuthenticateAsync(
            _mockTransport.Object,
            credentials,
            _testDeviceId);

        await act.Should()
            .ThrowAsync<AuthenticationException>()
            .Where(ex => ex.Reason == AuthenticationFailureReason.PremiumRequired,
                "should map PREMIUM_REQUIRED to PremiumRequired reason");
    }

    [Fact]
    public async Task AuthenticateAsync_WhenTransportReturnsNull_ShouldThrowAuthenticationException_TransportClosed()
    {
        // ============================================================
        // WHY: Network failure during auth should be DETECTED and
        //      reported clearly so app can implement retry logic.
        // ============================================================

        // Arrange
        var credentials = ProtobufHelpers.CreateValidCredentials();
        MockTransportHelpers.SetupReceiveNull(_mockTransport);

        // Act & Assert
        var act = async () => await Authenticator.AuthenticateAsync(
            _mockTransport.Object,
            credentials,
            _testDeviceId);

        await act.Should()
            .ThrowAsync<AuthenticationException>()
            .Where(ex => ex.Reason == AuthenticationFailureReason.TransportClosed,
                "should detect connection closure and throw appropriate exception");
    }

    [Fact]
    public async Task AuthenticateAsync_WithUnexpectedPacketType_ShouldThrowAuthenticationException_UnexpectedPacket()
    {
        // ============================================================
        // WHY: Protocol violation detection - server must respond with
        //      either APWelcome (0xAC) or APLoginFailed (0xAD).
        //      Anything else indicates protocol error or attack.
        // ============================================================

        // Arrange
        var credentials = ProtobufHelpers.CreateValidCredentials();
        MockTransportHelpers.SetupReceiveUnexpectedPacket(_mockTransport, 0xFF);

        // Act & Assert
        var act = async () => await Authenticator.AuthenticateAsync(
            _mockTransport.Object,
            credentials,
            _testDeviceId);

        await act.Should()
            .ThrowAsync<AuthenticationException>()
            .Where(ex => ex.Reason == AuthenticationFailureReason.UnexpectedPacket,
                "should reject unexpected packet types as protocol violation");
    }

    // ================================================================
    // PACKET VALIDATION TESTS - Verify correct packet construction
    // ================================================================

    [Fact]
    public async Task AuthenticateAsync_ShouldSendClientResponseEncryptedWithCorrectSystemInfo()
    {
        // ============================================================
        // WHY: Spotify requires system info (OS, CPU) for analytics
        //      and compatibility. Must match actual runtime environment.
        // ============================================================

        // Arrange
        var credentials = ProtobufHelpers.CreateValidCredentials();
        var welcomePayload = ProtobufHelpers.CreateAPWelcome("user", new byte[64]);

        // Capture sent packet for verification
        byte[]? capturedPayload = null;
        _mockTransport
            .Setup(t => t.SendAsync(0xAB, It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Callback<byte, ReadOnlyMemory<byte>, CancellationToken>((cmd, payload, ct) =>
                capturedPayload = payload.ToArray())
            .Returns(ValueTask.CompletedTask);

        _mockTransport
            .Setup(t => t.ReceiveAsync(It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<(byte, byte[])?>(((byte)0xAC, welcomePayload)));

        // Act
        await Authenticator.AuthenticateAsync(
            _mockTransport.Object,
            credentials,
            _testDeviceId);

        // Assert - Parse captured packet and verify contents
        capturedPayload.Should().NotBeNull("packet should have been sent");

        var packet = ClientResponseEncrypted.Parser.ParseFrom(capturedPayload!);
        packet.SystemInfo.Should().NotBeNull("system info is required");
        packet.SystemInfo.DeviceId.Should().Be(_testDeviceId,
            "device ID must match provided value");

        // Verify OS detection matches actual runtime
        if (OperatingSystem.IsWindows())
            packet.SystemInfo.Os.Should().Be(Os.Windows, "OS should be detected as Windows");
        else if (OperatingSystem.IsLinux())
            packet.SystemInfo.Os.Should().Be(Os.Linux, "OS should be detected as Linux");
        else if (OperatingSystem.IsMacOS())
            packet.SystemInfo.Os.Should().Be(Os.Osx, "OS should be detected as macOS");

        // Verify CPU architecture detection
        packet.SystemInfo.CpuFamily.Should().NotBe(CpuFamily.CpuUnknown,
            "should detect CPU architecture from runtime");
    }

    [Fact]
    public async Task AuthenticateAsync_WithUsernameAuth_ShouldIncludeUsernameInPacket()
    {
        // ============================================================
        // WHY: Username-based auth (password, blob) requires username
        //      field, while token-based auth omits it. Must distinguish.
        // ============================================================

        // Arrange
        var username = TestHelpers.CreateUsername();
        var credentials = new Credentials
        {
            Username = username,
            AuthType = AuthenticationType.AuthenticationUserPass,
            AuthData = TestHelpers.GenerateRandomBytes(16)
        };

        byte[]? capturedPayload = null;
        _mockTransport
            .Setup(t => t.SendAsync(0xAB, It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Callback<byte, ReadOnlyMemory<byte>, CancellationToken>((cmd, payload, ct) =>
                capturedPayload = payload.ToArray())
            .Returns(ValueTask.CompletedTask);

        MockTransportHelpers.SetupReceiveAPWelcome(_mockTransport, username, new byte[64]);

        // Act
        await Authenticator.AuthenticateAsync(
            _mockTransport.Object,
            credentials,
            _testDeviceId);

        // Assert
        var packet = ClientResponseEncrypted.Parser.ParseFrom(capturedPayload!);
        packet.LoginCredentials.Username.Should().Be(username,
            "username must be included for password-based authentication");
    }

    // ================================================================
    // NULL/VALIDATION TESTS - Input validation is critical for security
    // ================================================================

    [Fact]
    public async Task AuthenticateAsync_WithNullTransport_ShouldThrowArgumentNullException()
    {
        // Arrange
        var credentials = ProtobufHelpers.CreateValidCredentials();

        // Act & Assert
        var act = async () => await Authenticator.AuthenticateAsync(
            null!,
            credentials,
            _testDeviceId);

        await act.Should()
            .ThrowAsync<ArgumentNullException>()
            .WithParameterName("transport");
    }

    [Fact]
    public async Task AuthenticateAsync_WithNullCredentials_ShouldThrowArgumentNullException()
    {
        // Act & Assert
        var act = async () => await Authenticator.AuthenticateAsync(
            _mockTransport.Object,
            null!,
            _testDeviceId);

        await act.Should()
            .ThrowAsync<ArgumentNullException>()
            .WithParameterName("credentials");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public async Task AuthenticateAsync_WithInvalidDeviceId_ShouldThrowArgumentException(string? deviceId)
    {
        // Arrange
        var credentials = ProtobufHelpers.CreateValidCredentials();

        // Act & Assert
        var act = async () => await Authenticator.AuthenticateAsync(
            _mockTransport.Object,
            credentials,
            deviceId!);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
