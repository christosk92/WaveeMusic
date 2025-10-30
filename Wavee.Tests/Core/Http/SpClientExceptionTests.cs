using FluentAssertions;
using Wavee.Core.Http;
using Xunit;

namespace Wavee.Tests.Core.Http;

/// <summary>
/// Tests for SpClientException.
/// Validates exception handling for SpClient HTTP API failures.
/// </summary>
public class SpClientExceptionTests
{
    [Fact]
    public void Constructor_WithAllParameters_ShouldSetProperties()
    {
        // ============================================================
        // WHY: Exception constructor must correctly initialize all
        //      properties including inner exception.
        // ============================================================

        // Arrange
        var innerException = new InvalidOperationException("Inner error");
        var message = "SpClient request failed";
        var reason = SpClientFailureReason.Unauthorized;

        // Act
        var exception = new SpClientException(reason, message, innerException);

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
        var exception = new SpClientException(
            SpClientFailureReason.NotFound,
            "Resource not found");

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
        Action act = () => throw new SpClientException(
            SpClientFailureReason.RateLimited,
            "Too many requests");

        // Assert
        act.Should().Throw<SpClientException>()
            .WithMessage("Too many requests")
            .Where(ex => ex.Reason == SpClientFailureReason.RateLimited);
    }

    [Theory]
    [InlineData(SpClientFailureReason.RequestFailed)]
    [InlineData(SpClientFailureReason.Unauthorized)]
    [InlineData(SpClientFailureReason.NotFound)]
    [InlineData(SpClientFailureReason.RateLimited)]
    [InlineData(SpClientFailureReason.ServerError)]
    public void AllFailureReasons_ShouldBeTestable(SpClientFailureReason reason)
    {
        // ============================================================
        // WHY: All failure reason enum values must be usable in
        //      exception creation.
        // ============================================================

        // Act
        var exception = new SpClientException(reason, $"Test: {reason}");

        // Assert
        exception.Reason.Should().Be(reason);
        exception.Message.Should().Contain(reason.ToString());
    }
}
