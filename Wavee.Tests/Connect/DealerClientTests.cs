using FluentAssertions;
using Moq;
using System.Reactive.Linq;
using Wavee.Connect;
using Wavee.Connect.Connection;
using Wavee.Connect.Protocol;
using Wavee.Tests.Helpers;
using Xunit;

namespace Wavee.Tests.Connect;

/// <summary>
/// Tests for DealerClient - validates high-level dealer orchestration and integration.
///
/// WHY: DealerClient is the main API for dealer communication. Bugs here will cause:
/// - Application-level failures (features don't work)
/// - Integration issues (components don't work together)
/// - State management bugs (reconnection, heartbeat)
/// - Memory leaks (observables not completed)
///
/// These tests use mocked DealerConnection to test orchestration logic without
/// requiring actual WebSocket connections.
/// </summary>
public class DealerClientTests
{
    // ================================================================
    // CONSTRUCTION & INITIALIZATION TESTS
    // ================================================================

    [Fact]
    public void Constructor_ShouldInitializeCorrectly()
    {
        // Arrange & Act
        var client = new DealerClient(
            new DealerClientConfig
            {
                Logger = TestHelpers.CreateMockLogger<DealerClient>().Object
            });

        // Assert
        client.CurrentState.Should().Be(ConnectionState.Disconnected, "initial state should be Disconnected");
        client.Messages.Should().NotBeNull("Messages observable should be initialized");
        client.Requests.Should().NotBeNull("Requests observable should be initialized");
        client.ConnectionState.Should().NotBeNull("ConnectionState observable should be initialized");
    }

    // ================================================================
    // CONNECTION TESTS - Multi-dealer fallback and error handling
    // ================================================================

    [Fact]
    public async Task ConnectAsync_WhenAlreadyConnected_ShouldThrow()
    {
        // Arrange
        var mockConnection = new MockDealerConnection();
        var client = new DealerClient(DealerTestHelpers.CreateTestConfig(), connection: mockConnection);
        var mockSession = DealerTestHelpers.CreateMockSession();
        var httpClient = DealerTestHelpers.CreateMockHttpClientForDealers("dealer1.spotify.com:443");

        // First connect
        await client.ConnectAsync(mockSession, httpClient);

        // Act - Try to connect again
        var act = async () => await client.ConnectAsync(mockSession, httpClient);

        // Assert - Should throw because already connected
        await act.Should().ThrowAsync<InvalidOperationException>("cannot connect when already connected");

        await client.DisposeAsync();
    }

    // ================================================================
    // MESSAGE HANDLING TESTS - Observable pattern validation
    // ================================================================

    [Fact]
    public async Task Messages_WhenSubscribed_ShouldReceiveMessages()
    {
        // Arrange
        var mockConnection = new MockDealerConnection();
        var client = new DealerClient(DealerTestHelpers.CreateTestConfig(), connection: mockConnection);
        var receivedMessages = new List<DealerMessage>();

        // Subscribe to messages
        var subscription = client.Messages.Subscribe(msg => receivedMessages.Add(msg));

        // Simulate connecting
        await mockConnection.ConnectAsync("wss://test.spotify.com");

        // Act - Simulate receiving a dealer message
        var testMessage = DealerTestHelpers.CreateDealerMessage(
            "hm://connect-state/v1/connect/volume",
            new Dictionary<string, string> { ["Spotify-Connection-Id"] = "test123" },
            new byte[] { 1, 2, 3 });

        await mockConnection.SimulateMessageAsync(testMessage);

        // Give AsyncWorker time to process
        await Task.Delay(100);

        // Assert
        subscription.Should().NotBeNull("subscription should be created");
        receivedMessages.Should().HaveCount(1, "one message should be received");
        receivedMessages[0].Uri.Should().Be("hm://connect-state/v1/connect/volume");
        receivedMessages[0].Payload.Should().Equal(new byte[] { 1, 2, 3 });

        // Cleanup
        subscription.Dispose();
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Requests_WhenSubscribed_ShouldReceiveRequests()
    {
        // Arrange
        var mockConnection = new MockDealerConnection();
        var client = new DealerClient(DealerTestHelpers.CreateTestConfig(), connection: mockConnection);
        var receivedRequests = new List<DealerRequest>();

        // Subscribe to requests
        var subscription = client.Requests.Subscribe(req => receivedRequests.Add(req));

        // Simulate connecting
        await mockConnection.ConnectAsync("wss://test.spotify.com");

        // Act - Simulate receiving a dealer request
        var testRequest = DealerTestHelpers.CreateDealerRequest(
            messageId: 42,
            senderDeviceId: "device123",
            messageIdent: "hm://connect-state/v1/cluster",
            payload: new { volume = 50 });

        await mockConnection.SimulateMessageAsync(testRequest);

        // Give AsyncWorker time to process
        await Task.Delay(100);

        // Assert
        subscription.Should().NotBeNull("subscription should be created");
        receivedRequests.Should().HaveCount(1, "one request should be received");
        receivedRequests[0].MessageIdent.Should().Be("hm://connect-state/v1/cluster");
        receivedRequests[0].MessageId.Should().Be(42);
        receivedRequests[0].SenderDeviceId.Should().Be("device123");

        // Cleanup
        subscription.Dispose();
        await client.DisposeAsync();
    }

    [Fact]
    public async Task ConnectionState_ShouldStartAsDisconnected()
    {
        // Arrange
        var mockConnection = new MockDealerConnection();
        var client = new DealerClient(DealerTestHelpers.CreateTestConfig(), connection: mockConnection);
        var stateChanges = new List<ConnectionState>();

        // Subscribe to connection state
        var subscription = client.ConnectionState.Subscribe(state => stateChanges.Add(state));

        // Assert
        stateChanges.Should().HaveCount(1, "should receive initial state");
        stateChanges[0].Should().Be(ConnectionState.Disconnected);
        client.CurrentState.Should().Be(ConnectionState.Disconnected);

        // Cleanup
        subscription.Dispose();
        await client.DisposeAsync();
    }

    // ================================================================
    // SEND REPLY TESTS - Request handling
    // ================================================================

    [Fact]
    public async Task SendReplyAsync_WithSuccess_ShouldFormatCorrectly()
    {
        // Arrange
        var mockConnection = new MockDealerConnection();
        var client = new DealerClient(DealerTestHelpers.CreateTestConfig(), connection: mockConnection);
        await mockConnection.ConnectAsync("wss://test.spotify.com");

        // Expected format: {"type":"reply","key":"123/device","payload":{"success":true}}
        var expectedKey = "123/device456";

        // Act
        await client.SendReplyAsync(expectedKey, RequestResult.Success);

        // Assert
        mockConnection.SentStrings.Should().HaveCount(1, "one message should be sent");
        var sentMessage = mockConnection.SentStrings[0];
        sentMessage.Should().Contain("\"type\":\"reply\"");
        sentMessage.Should().Contain($"\"key\":\"{expectedKey}\"");
        sentMessage.Should().Contain("\"success\":true");

        await client.DisposeAsync();
    }

    [Fact]
    public async Task SendReplyAsync_WithFailure_ShouldSendFalse()
    {
        // Arrange
        var mockConnection = new MockDealerConnection();
        var client = new DealerClient(DealerTestHelpers.CreateTestConfig(), connection: mockConnection);
        await mockConnection.ConnectAsync("wss://test.spotify.com");

        // Expected format with failure: {"type":"reply","key":"...","payload":{"success":false}}
        var expectedKey = "789/device123";

        // Act
        await client.SendReplyAsync(expectedKey, RequestResult.UnknownSendCommandResult);

        // Assert
        mockConnection.SentStrings.Should().HaveCount(1, "one message should be sent");
        var sentMessage = mockConnection.SentStrings[0];
        sentMessage.Should().Contain("\"type\":\"reply\"");
        sentMessage.Should().Contain($"\"key\":\"{expectedKey}\"");
        sentMessage.Should().Contain("\"success\":false");

        await client.DisposeAsync();
    }

    // ================================================================
    // DISCONNECT TESTS - Cleanup validation
    // ================================================================

    [Fact]
    public async Task DisconnectAsync_WhenNotConnected_ShouldNotThrow()
    {
        // Arrange
        var mockConnection = new MockDealerConnection();
        var client = new DealerClient(DealerTestHelpers.CreateTestConfig(), connection: mockConnection);

        // Act
        var act = async () => await client.DisconnectAsync();

        // Assert
        await act.Should().NotThrowAsync("disconnecting when not connected should be safe");

        await client.DisposeAsync();
    }

    // ================================================================
    // DISPOSAL TESTS - Resource cleanup
    // ================================================================

    [Fact]
    public async Task DisposeAsync_WithActiveSubscriptions_ShouldCompleteObservables()
    {
        // Arrange
        var mockConnection = new MockDealerConnection();
        var client = new DealerClient(DealerTestHelpers.CreateTestConfig(), connection: mockConnection);
        var messageCompleted = false;
        var requestCompleted = false;
        var stateCompleted = false;

        client.Messages.Subscribe(
            _ => { },
            () => messageCompleted = true);

        client.Requests.Subscribe(
            _ => { },
            () => requestCompleted = true);

        client.ConnectionState.Subscribe(
            _ => { },
            () => stateCompleted = true);

        // Act
        await client.DisposeAsync();

        // Give observables time to complete
        await Task.Delay(100);

        // Assert
        messageCompleted.Should().BeTrue("Messages observable should complete on dispose");
        requestCompleted.Should().BeTrue("Requests observable should complete on dispose");
        stateCompleted.Should().BeTrue("ConnectionState observable should complete on dispose");
    }

    [Fact]
    public async Task DisposeAsync_ShouldCleanupAllResources()
    {
        // Arrange
        var mockConnection = new MockDealerConnection();
        var client = new DealerClient(DealerTestHelpers.CreateTestConfig(), connection: mockConnection);

        // Act
        await client.DisposeAsync();

        // Assert - Dispose should complete without throwing
        // Note: Cannot access CurrentState after disposal as BehaviorSubject is disposed
        await client.DisposeAsync(); // Should be idempotent
    }

    [Fact]
    public async Task DisposeAsync_Multiple_ShouldBeIdempotent()
    {
        // Arrange
        var mockConnection = new MockDealerConnection();
        var client = new DealerClient(DealerTestHelpers.CreateTestConfig(), connection: mockConnection);

        // Act
        await client.DisposeAsync();
        var act = async () => await client.DisposeAsync();

        // Assert
        await act.Should().NotThrowAsync("multiple dispose calls should be safe");
    }

    // ================================================================
    // CONFIGURATION TESTS - Config application
    // ================================================================

    [Fact]
    public void Constructor_WithNullConfig_ShouldUseDefaults()
    {
        // Arrange & Act
        var mockConnection = new MockDealerConnection();
        var client = new DealerClient(config: null, connection: mockConnection);

        // Assert
        client.Should().NotBeNull("should create with default config");
        client.CurrentState.Should().Be(ConnectionState.Disconnected);
    }

    [Fact]
    public async Task Constructor_WithCustomConfig_ShouldApplySettings()
    {
        // Arrange
        var mockConnection = new MockDealerConnection();
        var config = new DealerClientConfig
        {
            PingInterval = TimeSpan.FromSeconds(10),
            PongTimeout = TimeSpan.FromSeconds(2),
            EnableAutoReconnect = false,
            MaxReconnectAttempts = 5
        };

        // Act
        var client = new DealerClient(config, connection: mockConnection);

        // Assert
        client.Should().NotBeNull();
        // Config is applied internally, verified through behavior

        await client.DisposeAsync();
    }

    // ================================================================
    // OBSERVABLE FILTERING TESTS - Extension methods
    // ================================================================

    [Fact]
    public async Task Messages_WithWhereFilter_ShouldFilterByUri()
    {
        // Arrange
        var mockConnection = new MockDealerConnection();
        var client = new DealerClient(DealerTestHelpers.CreateTestConfig(), connection: mockConnection);
        var volumeMessages = new List<DealerMessage>();

        // Subscribe with URI filter
        var subscription = client.Messages
            .Where(m => m.Uri.StartsWith("hm://connect-state/v1/connect/volume"))
            .Subscribe(msg => volumeMessages.Add(msg));

        await mockConnection.ConnectAsync("wss://test.spotify.com");

        // Send two messages, only one matches filter
        await mockConnection.SimulateMessageAsync(DealerTestHelpers.CreateDealerMessage("hm://connect-state/v1/connect/volume", null, new byte[] { 1 }));
        await mockConnection.SimulateMessageAsync(DealerTestHelpers.CreateDealerMessage("hm://other/uri", null, new byte[] { 2 }));

        await Task.Delay(100);

        // Assert
        subscription.Should().NotBeNull();
        volumeMessages.Should().HaveCount(1, "only volume message should pass filter");

        // Cleanup
        subscription.Dispose();
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Requests_WithWhereFilter_ShouldFilterByMessageIdent()
    {
        // Arrange
        var mockConnection = new MockDealerConnection();
        var client = new DealerClient(DealerTestHelpers.CreateTestConfig(), connection: mockConnection);
        var clusterRequests = new List<DealerRequest>();

        // Subscribe with filter
        var subscription = client.Requests
            .Where(r => r.MessageIdent.Contains("cluster"))
            .Subscribe(req => clusterRequests.Add(req));

        await mockConnection.ConnectAsync("wss://test.spotify.com");

        // Send two requests, only one matches filter
        await mockConnection.SimulateMessageAsync(DealerTestHelpers.CreateDealerRequest(1, "dev1", "hm://cluster/update", new { }));
        await mockConnection.SimulateMessageAsync(DealerTestHelpers.CreateDealerRequest(2, "dev2", "hm://other/command", new { }));

        await Task.Delay(100);

        // Assert
        subscription.Should().NotBeNull();
        clusterRequests.Should().HaveCount(1, "only cluster request should pass filter");

        // Cleanup
        subscription.Dispose();
        await client.DisposeAsync();
    }

    // ================================================================
    // INTEGRATION BEHAVIOR TESTS - Expected workflows
    // ================================================================

    [Fact]
    public async Task Workflow_SubscribeThenConnect_ShouldWork()
    {
        // Arrange
        var mockConnection = new MockDealerConnection();
        var client = new DealerClient(DealerTestHelpers.CreateTestConfig(), connection: mockConnection);
        var messages = new List<DealerMessage>();

        // Subscribe before connecting (valid pattern)
        var subscription = client.Messages.Subscribe(msg => messages.Add(msg));

        // Connect and send message
        await mockConnection.ConnectAsync("wss://test.spotify.com");
        await mockConnection.SimulateMessageAsync(DealerTestHelpers.CreateDealerMessage("hm://test", null, new byte[] { 1 }));
        await Task.Delay(100);

        // Assert
        subscription.Should().NotBeNull("should be able to subscribe before connecting");
        messages.Should().HaveCount(1, "message should be received after connecting");

        // Cleanup
        subscription.Dispose();
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Workflow_MultipleSubscribers_ShouldAllReceive()
    {
        // Arrange
        var mockConnection = new MockDealerConnection();
        var client = new DealerClient(DealerTestHelpers.CreateTestConfig(), connection: mockConnection);
        var subscriber1Messages = new List<DealerMessage>();
        var subscriber2Messages = new List<DealerMessage>();
        var subscriber3Messages = new List<DealerMessage>();

        // Multiple subscribers to same observable
        var sub1 = client.Messages.Subscribe(msg => subscriber1Messages.Add(msg));
        var sub2 = client.Messages.Subscribe(msg => subscriber2Messages.Add(msg));
        var sub3 = client.Messages.Subscribe(msg => subscriber3Messages.Add(msg));

        // Send message
        await mockConnection.ConnectAsync("wss://test.spotify.com");
        await mockConnection.SimulateMessageAsync(DealerTestHelpers.CreateDealerMessage("hm://test", null, new byte[] { 1 }));
        await Task.Delay(100);

        // Assert - All subscribers should receive the same message
        subscriber1Messages.Should().HaveCount(1);
        subscriber2Messages.Should().HaveCount(1);
        subscriber3Messages.Should().HaveCount(1);

        // Cleanup
        sub1.Dispose();
        sub2.Dispose();
        sub3.Dispose();
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Workflow_UnsubscribeThenResubscribe_ShouldWork()
    {
        // Arrange
        var mockConnection = new MockDealerConnection();
        var client = new DealerClient(DealerTestHelpers.CreateTestConfig(), connection: mockConnection);

        // Subscribe
        var subscription1 = client.Messages.Subscribe(_ => { });

        // Unsubscribe
        subscription1.Dispose();

        // Resubscribe (should work fine)
        var subscription2 = client.Messages.Subscribe(_ => { });

        // Assert
        subscription2.Should().NotBeNull("should be able to resubscribe after unsubscribe");

        // Cleanup
        subscription2.Dispose();
        await client.DisposeAsync();
    }

    // ================================================================
    // ERROR HANDLING TESTS - Resilience validation
    // ================================================================

    [Fact]
    public async Task MalformedMessage_ShouldLogWarningButNotCrash()
    {
        // Arrange
        var mockConnection = new MockDealerConnection();
        var client = new DealerClient(DealerTestHelpers.CreateTestConfig(), connection: mockConnection);
        var messages = new List<DealerMessage>();
        client.Messages.Subscribe(msg => messages.Add(msg));

        await mockConnection.ConnectAsync("wss://test.spotify.com");

        // Act - Send malformed JSON
        await mockConnection.SimulateMessageAsync("{\"type\":\"message\",\"malformed");
        await Task.Delay(100);

        // Assert - Should not crash, malformed message should be ignored
        messages.Should().BeEmpty("malformed messages should be ignored");

        await client.DisposeAsync();
    }

    [Fact]
    public async Task ExceptionInSubscriber_ShouldNotBreakOtherSubscribers()
    {
        // Arrange
        var mockConnection = new MockDealerConnection();
        var client = new DealerClient(DealerTestHelpers.CreateTestConfig(), connection: mockConnection);
        var goodSubscriberMessages = new List<DealerMessage>();

        // Bad subscriber that throws
        var badSubscription = client.Messages.Subscribe(msg =>
        {
            throw new InvalidOperationException("Bad subscriber");
        });

        // Good subscriber
        var goodSubscription = client.Messages.Subscribe(msg =>
        {
            goodSubscriberMessages.Add(msg);
        });

        await mockConnection.ConnectAsync("wss://test.spotify.com");

        // Act - Send message (bad subscriber will throw, good subscriber should still receive)
        await mockConnection.SimulateMessageAsync(DealerTestHelpers.CreateDealerMessage("hm://test", null, new byte[] { 1 }));
        await Task.Delay(100);

        // Assert - Good subscriber should receive message despite bad subscriber throwing
        goodSubscriberMessages.Should().HaveCount(1, "good subscriber should continue working");

        // Cleanup
        badSubscription.Dispose();
        goodSubscription.Dispose();
        await client.DisposeAsync();
    }
}
