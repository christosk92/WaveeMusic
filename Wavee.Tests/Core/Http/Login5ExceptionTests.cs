using FluentAssertions;
using Wavee.Core.Http;
using Wavee.Protocol.Login;
using Xunit;

namespace Wavee.Tests.Core.Http;

/// <summary>
/// Tests for Login5Exception.
/// Validates exception handling for login5 authentication failures.
/// </summary>
public class Login5ExceptionTests
{
    [Theory]
    [InlineData(LoginError.InvalidCredentials, Login5FailureReason.InvalidCredentials)]
    [InlineData(LoginError.BadRequest, Login5FailureReason.BadRequest)]
    [InlineData(LoginError.UnsupportedLoginProtocol, Login5FailureReason.UnsupportedProtocol)]
    [InlineData(LoginError.Timeout, Login5FailureReason.Timeout)]
    [InlineData(LoginError.UnknownIdentifier, Login5FailureReason.UnknownIdentifier)]
    [InlineData(LoginError.TooManyAttempts, Login5FailureReason.TooManyAttempts)]
    [InlineData(LoginError.InvalidPhonenumber, Login5FailureReason.InvalidPhoneNumber)]
    [InlineData(LoginError.TryAgainLater, Login5FailureReason.TryAgainLater)]
    [InlineData(LoginError.UnknownError, Login5FailureReason.Unknown)]
    public void FromLoginError_ShouldMapErrorToReason(
        LoginError error,
        Login5FailureReason expectedReason)
    {
        // ============================================================
        // WHY: Login5Exception must correctly map protobuf LoginError
        //      enum values to Login5FailureReason for error handling.
        // ============================================================

        // Act
        var exception = Login5Exception.FromLoginError(error);

        // Assert
        exception.Should().NotBeNull();
        exception.Reason.Should().Be(expectedReason);
        exception.Message.Should().Contain(error.ToString());
    }

    [Fact]
    public void Constructor_WithAllParameters_ShouldSetProperties()
    {
        // ============================================================
        // WHY: Exception constructor must correctly initialize all
        //      properties including inner exception.
        // ============================================================

        // Arrange
        var innerException = new InvalidOperationException("Inner error");
        var message = "Login5 failed";
        var reason = Login5FailureReason.InvalidCredentials;

        // Act
        var exception = new Login5Exception(reason, message, innerException);

        // Assert
        exception.Reason.Should().Be(reason);
        exception.Message.Should().Be(message);
        exception.InnerException.Should().BeSameAs(innerException);
    }

    [Fact]
    public void Constructor_WithoutInnerException_ShouldHaveNullInnerException()
    {
        // ============================================================
        // WHY: Inner exception parameter is optional and should
        //      default to null when not provided.
        // ============================================================

        // Act
        var exception = new Login5Exception(
            Login5FailureReason.Timeout,
            "Request timed out");

        // Assert
        exception.InnerException.Should().BeNull();
    }

    [Fact]
    public void Exception_ShouldBeThrowableAndCatchable()
    {
        // ============================================================
        // WHY: Exception must be properly throwable and catchable
        //      in normal exception handling patterns.
        // ============================================================

        // Act
        Action act = () => throw new Login5Exception(
            Login5FailureReason.TooManyAttempts,
            "Too many login attempts");

        // Assert
        act.Should().Throw<Login5Exception>()
            .WithMessage("Too many login attempts")
            .Where(ex => ex.Reason == Login5FailureReason.TooManyAttempts);
    }
}
