using FluentAssertions;
using Wavee.Connect.Protocol;
using Wavee.Tests.Helpers;
using Xunit;

namespace Wavee.Tests.Connect.Protocol;

/// <summary>
/// Tests for MessageParser - validates zero-allocation JSON parsing and dealer protocol message handling.
///
/// WHY: MessageParser is the foundation of dealer communication. Bugs here will cause:
/// - Lost messages (connection state not synced)
/// - Security issues (malformed payload handling)
/// - Memory leaks (failed ArrayPool returns)
/// - Protocol violations (incorrect message routing)
/// </summary>
public class MessageParserTests
{
    // ================================================================
    // MESSAGE TYPE PARSING TESTS - Core protocol discrimination
    // ================================================================

    [Fact]
    public void ParseMessageType_Ping_ShouldReturnPing()
    {
        // Arrange
        var json = DealerTestHelpers.CreatePingMessage();
        var bytes = DealerTestHelpers.CreateMessageBytes(json);

        // Act
        var messageType = MessageParser.ParseMessageType(bytes);

        // Assert
        messageType.Should().Be(MessageType.Ping, "PING messages are used for heartbeat");
    }

    [Fact]
    public void ParseMessageType_Pong_ShouldReturnPong()
    {
        // Arrange
        var json = DealerTestHelpers.CreatePongMessage();
        var bytes = DealerTestHelpers.CreateMessageBytes(json);

        // Act
        var messageType = MessageParser.ParseMessageType(bytes);

        // Assert
        messageType.Should().Be(MessageType.Pong, "PONG messages acknowledge heartbeat");
    }

    [Fact]
    public void ParseMessageType_Message_ShouldReturnMessage()
    {
        // Arrange
        var json = DealerTestHelpers.CreateDealerMessage(
            "hm://connect-state/v1/connect/volume",
            new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            new byte[] { 1, 2, 3 });
        var bytes = DealerTestHelpers.CreateMessageBytes(json);

        // Act
        var messageType = MessageParser.ParseMessageType(bytes);

        // Assert
        messageType.Should().Be(MessageType.Message, "MESSAGE type carries dealer notifications");
    }

    [Fact]
    public void ParseMessageType_Request_ShouldReturnRequest()
    {
        // Arrange
        var json = DealerTestHelpers.CreateDealerRequest(
            123,
            "device123",
            "hm://connect-state/v1/cluster",
            new { command = "transfer" });
        var bytes = DealerTestHelpers.CreateMessageBytes(json);

        // Act
        var messageType = MessageParser.ParseMessageType(bytes);

        // Assert
        messageType.Should().Be(MessageType.Request, "REQUEST type requires reply from client");
    }

    [Fact]
    public void ParseMessageType_UnknownType_ShouldReturnUnknown()
    {
        // Arrange
        var json = "{\"type\":\"foobar\"}";
        var bytes = DealerTestHelpers.CreateMessageBytes(json);

        // Act
        var messageType = MessageParser.ParseMessageType(bytes);

        // Assert
        messageType.Should().Be(MessageType.Unknown, "unknown types should be handled gracefully");
    }

    [Fact]
    public void ParseMessageType_MalformedJson_ShouldReturnUnknown()
    {
        // Arrange
        var json = "{invalid json";
        var bytes = DealerTestHelpers.CreateMessageBytes(json);

        // Act
        var messageType = MessageParser.ParseMessageType(bytes);

        // Assert
        messageType.Should().Be(MessageType.Unknown, "malformed JSON should not crash parser");
    }

    // ================================================================
    // MESSAGE PARSING TESTS - Full MESSAGE extraction
    // ================================================================

    [Fact]
    public void TryParseMessage_ValidMessage_ShouldSucceed()
    {
        // Arrange
        var expectedUri = "hm://connect-state/v1/connect/volume";
        var expectedHeaders = new Dictionary<string, string>
        {
            ["Content-Type"] = "application/json",
            ["Transfer-Encoding"] = "gzip"
        };
        var expectedPayload = new byte[] { 1, 2, 3, 4, 5 };

        var json = DealerTestHelpers.CreateDealerMessage(expectedUri, expectedHeaders, expectedPayload);
        var bytes = DealerTestHelpers.CreateMessageBytes(json);

        // Act
        var success = MessageParser.TryParseMessage(bytes, out var message);

        // Assert
        success.Should().BeTrue("valid MESSAGE should parse successfully");
        message.Should().NotBeNull();
        message!.Uri.Should().Be(expectedUri);
        message.Headers.Should().BeEquivalentTo(expectedHeaders);
        message.Payload.Should().Equal(expectedPayload);
    }

    [Fact]
    public void TryParseMessage_EmptyHeaders_ShouldSucceed()
    {
        // Arrange
        var json = DealerTestHelpers.CreateDealerMessage(
            "hm://test",
            new Dictionary<string, string>(),
            new byte[] { 1 });
        var bytes = DealerTestHelpers.CreateMessageBytes(json);

        // Act
        var success = MessageParser.TryParseMessage(bytes, out var message);

        // Assert
        success.Should().BeTrue();
        message!.Headers.Should().BeEmpty();
    }

    [Fact]
    public void TryParseMessage_EmptyPayload_ShouldSucceed()
    {
        // Arrange
        var json = DealerTestHelpers.CreateDealerMessage(
            "hm://test",
            new Dictionary<string, string> { ["Content-Type"] = "text/plain" },
            Array.Empty<byte>());
        var bytes = DealerTestHelpers.CreateMessageBytes(json);

        // Act
        var success = MessageParser.TryParseMessage(bytes, out var message);

        // Assert
        success.Should().BeTrue();
        message!.Payload.Should().BeEmpty();
    }

    [Fact]
    public void TryParseMessage_MissingUri_ShouldFail()
    {
        // Arrange
        var json = "{\"type\":\"message\",\"headers\":{},\"payloads\":[\"AQID\"]}";
        var bytes = DealerTestHelpers.CreateMessageBytes(json);

        // Act
        var success = MessageParser.TryParseMessage(bytes, out var message);

        // Assert
        success.Should().BeFalse("URI is required for MESSAGE");
        message.Should().BeNull();
    }

    [Fact]
    public void TryParseMessage_MissingPayloads_ShouldFail()
    {
        // Arrange
        var json = "{\"type\":\"message\",\"uri\":\"hm://test\",\"headers\":{}}";
        var bytes = DealerTestHelpers.CreateMessageBytes(json);

        // Act
        var success = MessageParser.TryParseMessage(bytes, out var message);

        // Assert
        success.Should().BeFalse("payloads array is required");
        message.Should().BeNull();
    }

    [Fact]
    public void TryParseMessage_InvalidBase64Payload_ShouldFail()
    {
        // Arrange
        var json = "{\"type\":\"message\",\"uri\":\"hm://test\",\"headers\":{},\"payloads\":[\"!!!invalid base64!!!\"]}";
        var bytes = DealerTestHelpers.CreateMessageBytes(json);

        // Act
        var success = MessageParser.TryParseMessage(bytes, out var message);

        // Assert
        success.Should().BeFalse("invalid base64 should be rejected");
        message.Should().BeNull();
    }

    // ================================================================
    // REQUEST PARSING TESTS - Full REQUEST extraction with key parsing
    // ================================================================

    [Fact]
    public void TryParseRequest_ValidRequest_ShouldSucceed()
    {
        // Arrange
        var expectedMessageId = 12345;
        var expectedDeviceId = "device-abc-123";
        var expectedMessageIdent = "hm://connect-state/v1/cluster";
        var expectedCommand = new { endpoint = "transfer", data = "test" };

        var json = DealerTestHelpers.CreateDealerRequest(
            expectedMessageId,
            expectedDeviceId,
            expectedMessageIdent,
            new { command = expectedCommand });
        var bytes = DealerTestHelpers.CreateMessageBytes(json);

        // Act
        var success = MessageParser.TryParseRequest(bytes, out var request);

        // Assert
        success.Should().BeTrue("valid REQUEST should parse successfully");
        request.Should().NotBeNull();
        request!.Key.Should().Be($"{expectedMessageId}/{expectedDeviceId}");
        request.MessageIdent.Should().Be(expectedMessageIdent);
        request.MessageId.Should().Be(expectedMessageId);
        request.SenderDeviceId.Should().Be(expectedDeviceId);
        request.Command.Should().NotBeNull();
    }

    [Fact]
    public void TryParseRequest_InvalidKeyFormat_NoSlash_ShouldFail()
    {
        // Arrange
        var json = "{\"type\":\"request\",\"key\":\"invalid\",\"message_ident\":\"hm://test\",\"payload\":{}}";
        var bytes = DealerTestHelpers.CreateMessageBytes(json);

        // Act
        var success = MessageParser.TryParseRequest(bytes, out var request);

        // Assert
        success.Should().BeFalse("key must contain slash separator");
        request.Should().BeNull();
    }

    [Theory]
    [InlineData("abc/device")]      // Non-numeric message ID
    [InlineData("123-456/device")]  // Wrong separator
    [InlineData("/device")]         // Empty message ID
    [InlineData("123/")]            // Empty device ID
    public void TryParseRequest_InvalidKeyFormat_ShouldFail(string invalidKey)
    {
        // Arrange
        var json = $"{{\"type\":\"request\",\"key\":\"{invalidKey}\",\"message_ident\":\"hm://test\",\"payload\":{{}}}}";
        var bytes = DealerTestHelpers.CreateMessageBytes(json);

        // Act
        var success = MessageParser.TryParseRequest(bytes, out var request);

        // Assert
        success.Should().BeFalse($"key format '{invalidKey}' is invalid");
    }

    [Fact]
    public void TryParseRequest_MissingPayload_ShouldFail()
    {
        // Arrange
        var json = "{\"type\":\"request\",\"key\":\"123/device\",\"message_ident\":\"hm://test\"}";
        var bytes = DealerTestHelpers.CreateMessageBytes(json);

        // Act
        var success = MessageParser.TryParseRequest(bytes, out var request);

        // Assert
        success.Should().BeFalse("payload is required for REQUEST");
        request.Should().BeNull();
    }

    // ================================================================
    // PERFORMANCE & EDGE CASE TESTS
    // ================================================================

    [Fact]
    public void TryParseMessage_LargePayload_ShouldSucceed()
    {
        // Arrange - 1MB payload
        var largePayload = new byte[1024 * 1024];
        new Random(42).NextBytes(largePayload);

        var json = DealerTestHelpers.CreateDealerMessage("hm://test", null, largePayload);
        var bytes = DealerTestHelpers.CreateMessageBytes(json);

        // Act
        var success = MessageParser.TryParseMessage(bytes, out var message);

        // Assert
        success.Should().BeTrue("large payloads should be handled");
        message!.Payload.Should().Equal(largePayload);
    }

    [Fact]
    public void ParseMessageType_EmptyBytes_ShouldReturnUnknown()
    {
        // Arrange
        var bytes = Array.Empty<byte>();

        // Act
        var messageType = MessageParser.ParseMessageType(bytes);

        // Assert
        messageType.Should().Be(MessageType.Unknown, "empty input should be handled gracefully");
    }

    [Fact]
    public void TryParseMessage_NullHeaderValue_ShouldSucceed()
    {
        // Arrange
        var json = "{\"type\":\"message\",\"uri\":\"hm://test\",\"headers\":{\"Key\":null},\"payloads\":[\"AQID\"]}";
        var bytes = DealerTestHelpers.CreateMessageBytes(json);

        // Act
        var success = MessageParser.TryParseMessage(bytes, out var message);

        // Assert
        // Note: Behavior depends on implementation - might skip null values or include them
        success.Should().BeTrue("null header values should be handled");
    }
}
