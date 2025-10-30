using FluentAssertions;
using Wavee.Core.Connection;
using Xunit;

namespace Wavee.Tests.Core.Connection;

/// <summary>
/// Tests for HandshakeException.
/// </summary>
public class HandshakeExceptionTests
{
    [Fact]
    public void Constructor_WithReason_ShouldSetReasonProperty()
    {
        // Arrange
        var expectedReason = HandshakeReason.ServerVerificationFailed;
        var expectedMessage = "Test error";

        // Act
        var exception = new HandshakeException(expectedReason, expectedMessage);

        // Assert
        exception.Reason.Should().Be(expectedReason);
        exception.Message.Should().Be(expectedMessage);
    }

    [Fact]
    public void Constructor_WithReasonAndInnerException_ShouldSetAll()
    {
        // Arrange
        var expectedReason = HandshakeReason.NetworkError;
        var expectedMessage = "Network failure";
        var innerException = new System.IO.IOException("Connection lost");

        // Act
        var exception = new HandshakeException(expectedReason, expectedMessage, innerException);

        // Assert
        exception.Reason.Should().Be(expectedReason);
        exception.Message.Should().Be(expectedMessage);
        exception.InnerException.Should().BeSameAs(innerException);
    }

    [Theory]
    [InlineData(HandshakeReason.InvalidKeyLength)]
    [InlineData(HandshakeReason.ServerVerificationFailed)]
    [InlineData(HandshakeReason.NetworkError)]
    [InlineData(HandshakeReason.ProtocolError)]
    public void HandshakeReason_AllEnumValues_ShouldBeTestable(HandshakeReason reason)
    {
        // ============================================================
        // WHY: Ensures all HandshakeReason enum values can be used
        //      to construct exceptions. This validates enum completeness.
        // ============================================================

        // Act
        var exception = new HandshakeException(reason, "Test");

        // Assert
        exception.Reason.Should().Be(reason);
    }

    [Fact]
    public void Exception_ShouldBeThrowable()
    {
        // Arrange & Act
        Action act = () => throw new HandshakeException(
            HandshakeReason.ProtocolError,
            "Protocol violation");

        // Assert
        act.Should().Throw<HandshakeException>()
            .Where(ex => ex.Reason == HandshakeReason.ProtocolError)
            .WithMessage("Protocol violation");
    }

    [Fact]
    public void Reason_Property_ShouldReturnCorrectValue()
    {
        // Arrange
        var exception = new HandshakeException(
            HandshakeReason.ServerVerificationFailed,
            "Signature invalid");

        // Act
        var reason = exception.Reason;

        // Assert
        reason.Should().Be(HandshakeReason.ServerVerificationFailed,
            "Reason property should return the value set in constructor");
    }
}
