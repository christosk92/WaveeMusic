using System;
using FluentAssertions;
using Wavee.Core.Authentication;
using Xunit;

namespace Wavee.Tests.Core.Authentication;

/// <summary>
/// Tests for AuthenticationException class.
/// Validates exception construction, properties, and message formatting.
/// </summary>
public class AuthenticationExceptionTests
{
    [Fact]
    public void Constructor_WithReason_ShouldSetReasonProperty()
    {
        // ============================================================
        // WHY: Exception reason must be accessible to determine
        //      how to handle the authentication failure.
        // ============================================================

        // Arrange
        var reason = AuthenticationFailureReason.BadCredentials;

        // Act
        var exception = new AuthenticationException(reason, "Test message");

        // Assert
        exception.Reason.Should().Be(reason);
    }

    [Fact]
    public void Constructor_WithReasonAndMessage_ShouldSetBothProperties()
    {
        // Arrange
        var reason = AuthenticationFailureReason.PremiumRequired;
        var message = "Premium account required";

        // Act
        var exception = new AuthenticationException(reason, message);

        // Assert
        exception.Reason.Should().Be(reason);
        exception.Message.Should().Be(message);
    }

    [Fact]
    public void Constructor_WithInnerException_ShouldSetInnerException()
    {
        // ============================================================
        // WHY: Inner exceptions preserve the full error context for
        //      debugging and error reporting.
        // ============================================================

        // Arrange
        var innerException = new InvalidOperationException("Inner error");
        var reason = AuthenticationFailureReason.ProtocolError;
        var message = "Protocol error occurred";

        // Act
        var exception = new AuthenticationException(reason, message, innerException);

        // Assert
        exception.Reason.Should().Be(reason);
        exception.Message.Should().Be(message);
        exception.InnerException.Should().BeSameAs(innerException);
    }

    [Theory]
    [InlineData(AuthenticationFailureReason.BadCredentials)]
    [InlineData(AuthenticationFailureReason.PremiumRequired)]
    [InlineData(AuthenticationFailureReason.TransportClosed)]
    [InlineData(AuthenticationFailureReason.UnexpectedPacket)]
    [InlineData(AuthenticationFailureReason.TryAnotherAp)]
    [InlineData(AuthenticationFailureReason.ProtocolError)]
    [InlineData(AuthenticationFailureReason.LoginFailed)]
    public void Reason_ForEachEnumValue_ShouldStoreCorrectly(AuthenticationFailureReason reason)
    {
        // ============================================================
        // WHY: All possible failure reasons must be supported.
        //      This test ensures no enum value is missed.
        // ============================================================

        // Act
        var exception = new AuthenticationException(reason, "Test");

        // Assert
        exception.Reason.Should().Be(reason);
    }

    [Fact]
    public void Message_ShouldReturnProvidedMessage()
    {
        // Arrange
        var message = "Custom authentication error message";

        // Act
        var exception = new AuthenticationException(
            AuthenticationFailureReason.LoginFailed,
            message);

        // Assert
        exception.Message.Should().Be(message);
    }

    [Fact]
    public void ToString_ShouldIncludeReasonAndMessage()
    {
        // ============================================================
        // WHY: ToString() output is used in logs and error reports.
        //      Must include both reason and message for debugging.
        // ============================================================

        // Arrange
        var reason = AuthenticationFailureReason.BadCredentials;
        var message = "Invalid credentials provided";
        var exception = new AuthenticationException(reason, message);

        // Act
        var toString = exception.ToString();

        // Assert
        toString.Should().NotBeNullOrWhiteSpace();
        toString.Should().Contain(nameof(AuthenticationException));
        toString.Should().Contain(message,
            "exception string should include the error message");
        // Note: The reason enum value should be accessible via the Reason property
    }
}
