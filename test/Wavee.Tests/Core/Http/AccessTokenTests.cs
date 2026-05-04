using FluentAssertions;
using Wavee.Core.Http;
using Xunit;

namespace Wavee.Tests.Core.Http;

/// <summary>
/// Tests for Access Token.
/// Validates token expiry checking and refresh logic.
/// </summary>
public class AccessTokenTests
{
    [Fact]
    public void FromLogin5Response_WithValidData_ShouldCreateToken()
    {
        // ============================================================
        // WHY: Factory method must correctly create AccessToken from
        //      login5 response data.
        // ============================================================

        // Arrange
        var tokenString = "test_token_abc123";
        var expiresInSeconds = 3600;
        var beforeCreation = DateTimeOffset.UtcNow;

        // Act
        var token = AccessToken.FromLogin5Response(tokenString, expiresInSeconds);

        // Assert
        token.Should().NotBeNull();
        token.Token.Should().Be(tokenString);
        token.TokenType.Should().Be("Bearer");
        token.ExpiresAt.Should().BeCloseTo(beforeCreation.AddSeconds(expiresInSeconds), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void IsExpired_WithExpiredToken_ShouldReturnTrue()
    {
        // ============================================================
        // WHY: Expired tokens must be detected to trigger refresh.
        // ============================================================

        // Arrange
        var token = new AccessToken
        {
            Token = "expired_token",
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-10) // 10 seconds ago
        };

        // Act
        var isExpired = token.IsExpired();

        // Assert
        isExpired.Should().BeTrue("Token expired 10 seconds ago");
    }

    [Fact]
    public void IsExpired_WithValidToken_ShouldReturnFalse()
    {
        // ============================================================
        // WHY: Valid tokens should not be marked as expired.
        // ============================================================

        // Arrange
        var token = new AccessToken
        {
            Token = "valid_token",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) // 1 hour from now
        };

        // Act
        var isExpired = token.IsExpired();

        // Assert
        isExpired.Should().BeFalse("Token expires in 1 hour");
    }

    [Fact]
    public void ShouldRefresh_WithNearExpiryToken_ShouldReturnTrue()
    {
        // ============================================================
        // WHY: Tokens near expiry (within 5-min buffer) should trigger
        //      refresh to avoid using expired tokens.
        // ============================================================

        // Arrange
        var token = new AccessToken
        {
            Token = "soon_to_expire_token",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(3) // 3 minutes from now
        };

        // Act
        var shouldRefresh = token.ShouldRefresh(); // Default threshold is 5 minutes

        // Assert
        shouldRefresh.Should().BeTrue("Token expires within 5-minute threshold");
    }

    [Fact]
    public void ShouldRefresh_WithValidToken_ShouldReturnFalse()
    {
        // ============================================================
        // WHY: Tokens with plenty of lifetime remaining should not
        //      trigger unnecessary refresh.
        // ============================================================

        // Arrange
        var token = new AccessToken
        {
            Token = "valid_token",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) // 1 hour from now
        };

        // Act
        var shouldRefresh = token.ShouldRefresh(); // Default threshold is 5 minutes

        // Assert
        shouldRefresh.Should().BeFalse("Token has 1 hour remaining");
    }

    [Fact]
    public void ShouldRefresh_WithCustomThreshold_ShouldUseCustomValue()
    {
        // ============================================================
        // WHY: Custom refresh thresholds allow fine-tuning refresh
        //      timing for specific use cases.
        // ============================================================

        // Arrange
        var token = new AccessToken
        {
            Token = "token",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(8) // 8 minutes from now
        };

        // Act
        var shouldRefreshDefault = token.ShouldRefresh(); // 5-minute threshold
        var shouldRefreshCustom = token.ShouldRefresh(TimeSpan.FromMinutes(10)); // 10-minute threshold

        // Assert
        shouldRefreshDefault.Should().BeFalse("Token has 8 minutes, threshold is 5 minutes");
        shouldRefreshCustom.Should().BeTrue("Token has 8 minutes, custom threshold is 10 minutes");
    }

    [Fact]
    public void ToString_ShouldNotRevealFullToken()
    {
        // ============================================================
        // WHY: ToString() must not expose full token value in logs
        //      to prevent accidental credential leakage.
        // ============================================================

        // Arrange
        var fullToken = "very_long_secret_token_abc123xyz789";
        var token = new AccessToken
        {
            Token = fullToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };

        // Act
        var stringRepresentation = token.ToString();

        // Assert
        stringRepresentation.Should().NotContain(fullToken, "Full token should not be in string representation");
        stringRepresentation.Should().Contain("very_long_", "Should show first 10 characters");
        stringRepresentation.Should().Contain("...", "Should indicate truncation");
        stringRepresentation.Should().Contain("ExpiresAt", "Should include expiry info");
    }

    [Fact]
    public void FromLogin5Response_WithNullToken_ShouldThrow()
    {
        // ============================================================
        // WHY: Null tokens are invalid and must be rejected.
        // ============================================================

        // Act
        Action act = () => AccessToken.FromLogin5Response(null!, 3600);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void FromLogin5Response_WithEmptyToken_ShouldThrow()
    {
        // ============================================================
        // WHY: Empty tokens are invalid and must be rejected.
        // ============================================================

        // Act
        Action act = () => AccessToken.FromLogin5Response("", 3600);

        // Assert
        act.Should().Throw<ArgumentException>();
    }
}
