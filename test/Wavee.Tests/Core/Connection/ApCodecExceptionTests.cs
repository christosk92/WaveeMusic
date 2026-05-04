using FluentAssertions;
using Wavee.Core.Connection;
using Xunit;

namespace Wavee.Tests.Core.Connection;

/// <summary>
/// Tests for ApCodecException.
/// </summary>
public class ApCodecExceptionTests
{
    [Fact]
    public void Constructor_WithMessage_ShouldSetMessage()
    {
        // Arrange
        var expectedMessage = "Test error message";

        // Act
        var exception = new ApCodecException(expectedMessage);

        // Assert
        exception.Message.Should().Be(expectedMessage);
    }

    [Fact]
    public void Constructor_WithMessageAndInnerException_ShouldSetBoth()
    {
        // Arrange
        var expectedMessage = "Test error message";
        var innerException = new InvalidOperationException("Inner error");

        // Act
        var exception = new ApCodecException(expectedMessage, innerException);

        // Assert
        exception.Message.Should().Be(expectedMessage);
        exception.InnerException.Should().BeSameAs(innerException);
    }

    [Fact]
    public void Exception_ShouldBeThrowable()
    {
        // Arrange & Act
        Action act = () => throw new ApCodecException("Test exception");

        // Assert
        act.Should().Throw<ApCodecException>()
            .WithMessage("Test exception");
    }
}
