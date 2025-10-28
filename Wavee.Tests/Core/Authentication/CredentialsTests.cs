using System;
using FluentAssertions;
using Wavee.Core.Authentication;
using Wavee.Protocol;
using Wavee.Tests.Helpers;
using Xunit;

namespace Wavee.Tests.Core.Authentication;

/// <summary>
/// Tests for Credentials value object.
/// Validates credential storage, equality, and security (no data leakage).
/// </summary>
public class CredentialsTests
{
    [Fact]
    public void CreateCredentials_WithAllProperties_ShouldStoreCorrectly()
    {
        // ============================================================
        // WHY: Basic functionality - credentials must store and
        //      retrieve all properties correctly.
        // ============================================================

        // Arrange
        var username = "testuser";
        var authType = AuthenticationType.AuthenticationUserPass;
        var authData = TestHelpers.GenerateRandomBytes(32);

        // Act
        var credentials = new Credentials
        {
            Username = username,
            AuthType = authType,
            AuthData = authData
        };

        // Assert
        credentials.Username.Should().Be(username);
        credentials.AuthType.Should().Be(authType);
        credentials.AuthData.Should().BeEquivalentTo(authData);
    }

    [Fact]
    public void Username_SetAndGet_ShouldReturnCorrectValue()
    {
        // Arrange
        var username = "alice";

        // Act
        var credentials = new Credentials
        {
            Username = username
        };

        // Assert
        credentials.Username.Should().Be(username);
    }

    [Fact]
    public void AuthType_SetAndGet_ShouldReturnCorrectValue()
    {
        // Arrange
        var authType = AuthenticationType.AuthenticationStoredSpotifyCredentials;

        // Act
        var credentials = new Credentials
        {
            AuthType = authType
        };

        // Assert
        credentials.AuthType.Should().Be(authType);
    }

    [Fact]
    public void AuthData_SetAndGet_ShouldReturnCorrectValue()
    {
        // Arrange
        var authData = new byte[] { 1, 2, 3, 4, 5 };

        // Act
        var credentials = new Credentials
        {
            AuthData = authData
        };

        // Assert
        credentials.AuthData.Should().BeEquivalentTo(authData);
    }

    [Fact]
    public void Equals_WithIdenticalCredentials_ShouldReturnTrue()
    {
        // ============================================================
        // WHY: Credentials equality comparison is used for caching
        //      and deduplication. Must work correctly.
        // ============================================================

        // Arrange
        var username = "bob";
        var authType = AuthenticationType.AuthenticationUserPass;
        var authData = new byte[] { 1, 2, 3 };

        var credentials1 = new Credentials
        {
            Username = username,
            AuthType = authType,
            AuthData = authData
        };

        var credentials2 = new Credentials
        {
            Username = username,
            AuthType = authType,
            AuthData = authData
        };

        // Act & Assert
        credentials1.Equals(credentials2).Should().BeTrue(
            "credentials with identical data should be equal");
    }

    [Fact]
    public void Equals_WithDifferentUsername_ShouldReturnFalse()
    {
        // Arrange
        var authData = new byte[] { 1, 2, 3 };

        var credentials1 = new Credentials
        {
            Username = "user1",
            AuthType = AuthenticationType.AuthenticationUserPass,
            AuthData = authData
        };

        var credentials2 = new Credentials
        {
            Username = "user2",
            AuthType = AuthenticationType.AuthenticationUserPass,
            AuthData = authData
        };

        // Act & Assert
        credentials1.Equals(credentials2).Should().BeFalse(
            "credentials with different usernames should not be equal");
    }

    [Fact]
    public void GetHashCode_WithSameData_ShouldReturnSameHash()
    {
        // ============================================================
        // WHY: GetHashCode consistency is required for using
        //      Credentials in dictionaries and hash-based collections.
        // ============================================================

        // Arrange
        var username = "charlie";
        var authType = AuthenticationType.AuthenticationUserPass;
        var authData = new byte[] { 1, 2, 3 };

        var credentials1 = new Credentials
        {
            Username = username,
            AuthType = authType,
            AuthData = authData
        };

        var credentials2 = new Credentials
        {
            Username = username,
            AuthType = authType,
            AuthData = authData
        };

        // Act
        var hash1 = credentials1.GetHashCode();
        var hash2 = credentials2.GetHashCode();

        // Assert
        hash1.Should().Be(hash2,
            "equal credentials must have the same hash code");
    }

    [Fact]
    public void ToString_ShouldNotExposeAuthData()
    {
        // ============================================================
        // WHY: CRITICAL SECURITY - ToString() is often used in logging.
        //      Must NEVER expose sensitive auth data in plain text.
        // ============================================================

        // Arrange
        var credentials = new Credentials
        {
            Username = "secureuser",
            AuthType = AuthenticationType.AuthenticationUserPass,
            AuthData = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }
        };

        // Act
        var toString = credentials.ToString();

        // Assert
        toString.Should().NotBeNull();
        toString.Should().NotContain("DEADBEEF",
            "ToString should not expose auth data in hex format");
        toString.Should().NotContain("222, 173, 190, 239",
            "ToString should not expose auth data in decimal format");

        // Verify username is safe to include (not sensitive)
        // But auth data must be hidden or masked
    }
}
