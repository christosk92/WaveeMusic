using System;
using System.Threading;
using Moq;
using Wavee.Core.Connection;
using Wavee.Protocol;

namespace Wavee.Tests.Helpers;

/// <summary>
/// Helper methods for mocking IApTransport.
/// </summary>
internal static class MockTransportHelpers
{
    /// <summary>
    /// Creates a mock IApTransport with basic setup.
    /// </summary>
    public static Mock<IApTransport> CreateMockApTransport()
    {
        return new Mock<IApTransport>();
    }

    /// <summary>
    /// Setups mock to return APWelcome packet.
    /// </summary>
    public static void SetupReceiveAPWelcome(
        Mock<IApTransport> mockTransport,
        string username,
        byte[] reusableCredentials)
    {
        var payload = ProtobufHelpers.CreateAPWelcome(username, reusableCredentials);
        mockTransport
            .Setup(t => t.ReceiveAsync(It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<(byte command, byte[] payload)?>(((byte)0xAC, payload))); // 0xAC = APWelcome
    }

    /// <summary>
    /// Setups mock to return APLoginFailed packet.
    /// </summary>
    public static void SetupReceiveAPLoginFailed(
        Mock<IApTransport> mockTransport,
        ErrorCode errorCode)
    {
        var payload = ProtobufHelpers.CreateAPLoginFailed(errorCode);
        mockTransport
            .Setup(t => t.ReceiveAsync(It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<(byte command, byte[] payload)?>(((byte)0xAD, payload))); // 0xAD = APLoginFailed
    }

    /// <summary>
    /// Setups mock to return null (connection closed).
    /// </summary>
    public static void SetupReceiveNull(Mock<IApTransport> mockTransport)
    {
        mockTransport
            .Setup(t => t.ReceiveAsync(It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<(byte command, byte[] payload)?>(default((byte, byte[])?)));
    }

    /// <summary>
    /// Setups mock to return unexpected packet type.
    /// </summary>
    public static void SetupReceiveUnexpectedPacket(
        Mock<IApTransport> mockTransport,
        byte packetType)
    {
        mockTransport
            .Setup(t => t.ReceiveAsync(It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<(byte command, byte[] payload)?>((packetType, new byte[] { 1, 2, 3 })));
    }

    /// <summary>
    /// Verifies that SendAsync was called with expected command.
    /// </summary>
    public static void VerifySendPacket(
        Mock<IApTransport> mockTransport,
        byte expectedCommand,
        Times times)
    {
        mockTransport.Verify(
            t => t.SendAsync(
                expectedCommand,
                It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()),
            times);
    }
}
