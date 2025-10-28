using System;
using System.Text.Json;
using FluentAssertions;
using Wavee.Core.Authentication;
using Wavee.Protocol;
using Wavee.Tests.Helpers;
using Xunit;

namespace Wavee.Tests.Core.Authentication;

/// <summary>
/// Tests for AuthenticationJsonSerializerContext class.
/// Validates source-generated JSON serialization for Native AOT compatibility.
/// </summary>
public class AuthenticationJsonSerializerContextTests
{
    [Fact]
    public void SerializeCredentials_ShouldProduceValidJson()
    {
        // ============================================================
        // WHY: Serialization must produce valid, parseable JSON
        //      that matches expected schema.
        // ============================================================

        // Arrange
        var credentials = new Credentials
        {
            Username = "testuser",
            AuthType = AuthenticationType.AuthenticationUserPass,
            AuthData = new byte[] { 1, 2, 3, 4, 5 }
        };

        // Act
        var json = JsonSerializer.Serialize(
            credentials,
            AuthenticationJsonSerializerContext.Default.Credentials);

        // Assert
        json.Should().NotBeNullOrEmpty();
        json.Should().Contain("\"username\"", "should use camelCase naming");
        json.Should().Contain("testuser");
        json.Should().Contain("\"authType\"", "should use camelCase naming");
        json.Should().NotContain("AuthData", "should use camelCase, not PascalCase");
    }

    [Fact]
    public void DeserializeCredentials_WithValidJson_ShouldReturnCredentials()
    {
        // ============================================================
        // WHY: Deserialization must correctly parse JSON back
        //      into Credentials objects.
        // ============================================================

        // Arrange
        var json = """
        {
            "username": "alice",
            "authType": "AuthenticationUserPass",
            "authData": "AQIDBAU="
        }
        """;

        // Act
        var credentials = JsonSerializer.Deserialize(
            json,
            AuthenticationJsonSerializerContext.Default.Credentials);

        // Assert
        credentials.Should().NotBeNull();
        credentials!.Username.Should().Be("alice");
        credentials.AuthType.Should().Be(AuthenticationType.AuthenticationUserPass);
        credentials.AuthData.Should().BeEquivalentTo(new byte[] { 1, 2, 3, 4, 5 },
            "Base64 'AQIDBAU=' should decode to [1,2,3,4,5]");
    }

    [Fact]
    public void SerializeDeserializeRoundtrip_ShouldPreserveData()
    {
        // ============================================================
        // WHY: CRITICAL - Full roundtrip must preserve all data
        //      without loss, especially binary AuthData.
        // ============================================================

        // Arrange
        var original = new Credentials
        {
            Username = "bob@spotify.com",
            AuthType = AuthenticationType.AuthenticationStoredSpotifyCredentials,
            AuthData = TestHelpers.GenerateRandomBytes(64)
        };

        // Act
        var json = JsonSerializer.Serialize(
            original,
            AuthenticationJsonSerializerContext.Default.Credentials);
        var deserialized = JsonSerializer.Deserialize(
            json,
            AuthenticationJsonSerializerContext.Default.Credentials);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized!.Username.Should().Be(original.Username);
        deserialized.AuthType.Should().Be(original.AuthType);
        deserialized.AuthData.Should().BeEquivalentTo(original.AuthData,
            "binary data must survive JSON roundtrip via Base64");
    }

    [Fact]
    public void SerializeCredentials_WithNullUsername_ShouldHandleGracefully()
    {
        // ============================================================
        // WHY: Token-based auth doesn't require username.
        //      Must handle null username without throwing.
        // ============================================================

        // Arrange
        var credentials = new Credentials
        {
            Username = null,
            AuthType = AuthenticationType.AuthenticationSpotifyToken,
            AuthData = new byte[] { 0x01, 0x02, 0x03 }
        };

        // Act
        var json = JsonSerializer.Serialize(
            credentials,
            AuthenticationJsonSerializerContext.Default.Credentials);
        var deserialized = JsonSerializer.Deserialize(
            json,
            AuthenticationJsonSerializerContext.Default.Credentials);

        // Assert
        json.Should().Contain("\"username\":null", "null should be serialized explicitly");
        deserialized.Should().NotBeNull();
        deserialized!.Username.Should().BeNull("null username should roundtrip correctly");
        deserialized.AuthType.Should().Be(AuthenticationType.AuthenticationSpotifyToken);
    }
}
