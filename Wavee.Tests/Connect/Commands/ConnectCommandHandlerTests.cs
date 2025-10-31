using FluentAssertions;
using Google.Protobuf;
using System.Reactive.Linq;
using Wavee.Connect;
using Wavee.Connect.Commands;
using Wavee.Connect.Protocol;
using Wavee.Protocol.Transfer;
using Wavee.Tests.Connect;
using Wavee.Tests.Helpers;
using Xunit;

namespace Wavee.Tests.Connect.Commands;

/// <summary>
/// Tests for ConnectCommandHandler - validates command parsing, dispatch, and reply handling.
///
/// WHY: ConnectCommandHandler is the bridge between dealer messages and command handlers. Bugs here will cause:
/// - Lost commands (playback doesn't respond)
/// - Memory leaks (observables not completed)
/// - Race conditions (concurrent command handling)
/// - Reply tracking failures (Spotify doesn't know command status)
/// </summary>
public class ConnectCommandHandlerTests
{
    // ================================================================
    // SECTION 1: CONSTRUCTION & INITIALIZATION TESTS
    // ================================================================

    [Fact]
    public async Task Constructor_WithValidCommandSource_ShouldInitialize()
    {
        // WHY: Verify handler initializes correctly and creates all observables

        // Arrange & Act
        var (handler, _) = ConnectCommandTestHelpers.CreateTestCommandHandler();

        // Assert
        handler.Should().NotBeNull();
        handler.PlayCommands.Should().NotBeNull("PlayCommands observable should be initialized");
        handler.PauseCommands.Should().NotBeNull("PauseCommands observable should be initialized");
        handler.ResumeCommands.Should().NotBeNull("ResumeCommands observable should be initialized");
        handler.SeekCommands.Should().NotBeNull("SeekCommands observable should be initialized");
        handler.SkipNextCommands.Should().NotBeNull("SkipNextCommands observable should be initialized");
        handler.SkipPrevCommands.Should().NotBeNull("SkipPrevCommands observable should be initialized");
        handler.ShuffleCommands.Should().NotBeNull("ShuffleCommands observable should be initialized");
        handler.RepeatContextCommands.Should().NotBeNull("RepeatContextCommands observable should be initialized");
        handler.RepeatTrackCommands.Should().NotBeNull("RepeatTrackCommands observable should be initialized");
        handler.SetQueueCommands.Should().NotBeNull("SetQueueCommands observable should be initialized");
        handler.AddToQueueCommands.Should().NotBeNull("AddToQueueCommands observable should be initialized");
        handler.TransferCommands.Should().NotBeNull("TransferCommands observable should be initialized");

        await handler.DisposeAsync();
    }

    [Fact]
    public void Constructor_WithNullCommandSource_ShouldThrow()
    {
        // WHY: Null dependencies should fail fast

        // Act & Assert
        var act = () => new ConnectCommandHandler((ICommandSource)null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("commandSource");
    }

    [Fact]
    public async Task Constructor_ShouldSubscribeToCommandSource()
    {
        // WHY: Handler must listen to command source request stream on construction

        // Arrange
        var (handler, mockSource) = ConnectCommandTestHelpers.CreateTestCommandHandler();

        var receivedCommands = new List<PauseCommand>();
        handler.PauseCommands.Subscribe(cmd => receivedCommands.Add(cmd));

        // Act - Send a pause command through mock source
        var request = ConnectCommandTestHelpers.CreatePauseCommandRequest(1, "device_test");
        mockSource.SimulateRequest(request);
        await ConnectCommandTestHelpers.WaitForProcessingAsync();

        // Assert
        receivedCommands.Should().HaveCount(1, "handler should have subscribed to command source");

        await handler.DisposeAsync();
    }

    // ================================================================
    // SECTION 2: COMMAND PARSING & DISPATCH TESTS
    // ================================================================

    [Fact]
    public async Task PlayCommand_WhenReceived_ShouldDispatchToPlayObservable()
    {
        // WHY: Play commands must be parsed and dispatched with all properties

        // Arrange
        var (handler, mockSource) = ConnectCommandTestHelpers.CreateTestCommandHandler();
        var receivedCommands = new List<PlayCommand>();
        handler.PlayCommands.Subscribe(cmd => receivedCommands.Add(cmd));

        // Act
        var request = ConnectCommandTestHelpers.CreatePlayCommandRequest(
            messageId: 42,
            deviceId: "device_test_123",
            contextUri: "spotify:playlist:37i9dQZF1DXcBWIGoYBM5M",
            trackUri: "spotify:track:3n3Ppam7vgaVa1iaRUc9Lp",
            seekTo: 5000,
            shuffling: true);

        mockSource.SimulateRequest(request);
        await ConnectCommandTestHelpers.WaitForProcessingAsync();

        // Assert
        receivedCommands.Should().HaveCount(1);
        var cmd = receivedCommands[0];
        cmd.Endpoint.Should().Be("play");
        cmd.MessageId.Should().Be(42);
        cmd.SenderDeviceId.Should().Be("device_test_123");
        cmd.Key.Should().Be("42/device_test_123");
        cmd.ContextUri.Should().Be("spotify:playlist:37i9dQZF1DXcBWIGoYBM5M");
        cmd.TrackUri.Should().Be("spotify:track:3n3Ppam7vgaVa1iaRUc9Lp");
        cmd.PositionMs.Should().Be(5000);
        cmd.Options.Should().NotBeNull();
        cmd.Options!.ShufflingContext.Should().BeTrue();

        await handler.DisposeAsync();
    }

    [Fact]
    public async Task PauseCommand_WhenReceived_ShouldDispatchToPauseObservable()
    {
        // WHY: Pause commands must be parsed and dispatched correctly

        // Arrange
        var (handler, mockSource) = ConnectCommandTestHelpers.CreateTestCommandHandler();
        var receivedCommands = new List<PauseCommand>();
        handler.PauseCommands.Subscribe(cmd => receivedCommands.Add(cmd));

        // Act
        var request = ConnectCommandTestHelpers.CreatePauseCommandRequest(100, "device_pause");
        mockSource.SimulateRequest(request);
        await ConnectCommandTestHelpers.WaitForProcessingAsync();

        // Assert
        receivedCommands.Should().HaveCount(1);
        receivedCommands[0].Endpoint.Should().Be("pause");
        receivedCommands[0].MessageId.Should().Be(100);
        receivedCommands[0].Key.Should().Be("100/device_pause");

        await handler.DisposeAsync();
    }

    [Fact]
    public async Task ResumeCommand_WhenReceived_ShouldDispatchToResumeObservable()
    {
        // WHY: Resume commands must be parsed and dispatched correctly

        // Arrange
        var (handler, mockSource) = ConnectCommandTestHelpers.CreateTestCommandHandler();
        var receivedCommands = new List<ResumeCommand>();
        handler.ResumeCommands.Subscribe(cmd => receivedCommands.Add(cmd));

        // Act
        var request = ConnectCommandTestHelpers.CreateResumeCommandRequest(101, "device_resume");
        mockSource.SimulateRequest(request);
        await ConnectCommandTestHelpers.WaitForProcessingAsync();

        // Assert
        receivedCommands.Should().HaveCount(1);
        receivedCommands[0].Endpoint.Should().Be("resume");

        await handler.DisposeAsync();
    }

    [Fact]
    public async Task SeekCommand_WhenReceived_ShouldDispatchToSeekObservable()
    {
        // WHY: Seek commands must include position parameter

        // Arrange
        var (handler, mockSource) = ConnectCommandTestHelpers.CreateTestCommandHandler();
        var receivedCommands = new List<SeekCommand>();
        handler.SeekCommands.Subscribe(cmd => receivedCommands.Add(cmd));

        // Act
        var request = ConnectCommandTestHelpers.CreateSeekCommandRequest(102, "device_seek", 12345);
        mockSource.SimulateRequest(request);
        await ConnectCommandTestHelpers.WaitForProcessingAsync();

        // Assert
        receivedCommands.Should().HaveCount(1);
        receivedCommands[0].PositionMs.Should().Be(12345);

        await handler.DisposeAsync();
    }

    [Fact]
    public async Task SkipNextCommand_WhenReceived_ShouldDispatchToSkipNextObservable()
    {
        // WHY: Skip next commands must be handled

        // Arrange
        var (handler, mockSource) = ConnectCommandTestHelpers.CreateTestCommandHandler();
        var receivedCommands = new List<SkipNextCommand>();
        handler.SkipNextCommands.Subscribe(cmd => receivedCommands.Add(cmd));

        // Act
        var request = ConnectCommandTestHelpers.CreateSkipNextCommandRequest(103, "device_skip");
        mockSource.SimulateRequest(request);
        await ConnectCommandTestHelpers.WaitForProcessingAsync();

        // Assert
        receivedCommands.Should().HaveCount(1);

        await handler.DisposeAsync();
    }

    [Fact]
    public async Task SkipPrevCommand_WhenReceived_ShouldDispatchToSkipPrevObservable()
    {
        // WHY: Skip previous commands must be handled

        // Arrange
        var (handler, mockSource) = ConnectCommandTestHelpers.CreateTestCommandHandler();
        var receivedCommands = new List<SkipPrevCommand>();
        handler.SkipPrevCommands.Subscribe(cmd => receivedCommands.Add(cmd));

        // Act
        var request = ConnectCommandTestHelpers.CreateSkipPrevCommandRequest(104, "device_skip");
        mockSource.SimulateRequest(request);
        await ConnectCommandTestHelpers.WaitForProcessingAsync();

        // Assert
        receivedCommands.Should().HaveCount(1);

        await handler.DisposeAsync();
    }

    [Fact]
    public async Task ShuffleCommand_WhenReceived_ShouldDispatchToShuffleObservable()
    {
        // WHY: Shuffle toggle commands must include value parameter

        // Arrange
        var (handler, mockSource) = ConnectCommandTestHelpers.CreateTestCommandHandler();
        var receivedCommands = new List<ShuffleCommand>();
        handler.ShuffleCommands.Subscribe(cmd => receivedCommands.Add(cmd));

        // Act
        var request = ConnectCommandTestHelpers.CreateShuffleCommandRequest(105, "device_shuffle", true);
        mockSource.SimulateRequest(request);
        await ConnectCommandTestHelpers.WaitForProcessingAsync();

        // Assert
        receivedCommands.Should().HaveCount(1);
        receivedCommands[0].Enabled.Should().BeTrue();

        await handler.DisposeAsync();
    }

    [Fact]
    public async Task RepeatContextCommand_WhenReceived_ShouldDispatchToRepeatContextObservable()
    {
        // WHY: Repeat context commands must include value parameter

        // Arrange
        var (handler, mockSource) = ConnectCommandTestHelpers.CreateTestCommandHandler();
        var receivedCommands = new List<RepeatContextCommand>();
        handler.RepeatContextCommands.Subscribe(cmd => receivedCommands.Add(cmd));

        // Act
        var request = ConnectCommandTestHelpers.CreateRepeatContextCommandRequest(106, "device_repeat", false);
        mockSource.SimulateRequest(request);
        await ConnectCommandTestHelpers.WaitForProcessingAsync();

        // Assert
        receivedCommands.Should().HaveCount(1);
        receivedCommands[0].Enabled.Should().BeFalse();

        await handler.DisposeAsync();
    }

    [Fact]
    public async Task RepeatTrackCommand_WhenReceived_ShouldDispatchToRepeatTrackObservable()
    {
        // WHY: Repeat track commands must include value parameter

        // Arrange
        var (handler, mockSource) = ConnectCommandTestHelpers.CreateTestCommandHandler();
        var receivedCommands = new List<RepeatTrackCommand>();
        handler.RepeatTrackCommands.Subscribe(cmd => receivedCommands.Add(cmd));

        // Act
        var request = ConnectCommandTestHelpers.CreateRepeatTrackCommandRequest(107, "device_repeat", true);
        mockSource.SimulateRequest(request);
        await ConnectCommandTestHelpers.WaitForProcessingAsync();

        // Assert
        receivedCommands.Should().HaveCount(1);
        receivedCommands[0].Enabled.Should().BeTrue();

        await handler.DisposeAsync();
    }

    [Fact]
    public async Task SetQueueCommand_WhenReceived_ShouldDispatchToSetQueueObservable()
    {
        // WHY: Queue replacement commands must include track list

        // Arrange
        var (handler, mockSource) = ConnectCommandTestHelpers.CreateTestCommandHandler();
        var receivedCommands = new List<SetQueueCommand>();
        handler.SetQueueCommands.Subscribe(cmd => receivedCommands.Add(cmd));

        // Act
        var trackUris = new[] { "spotify:track:1", "spotify:track:2", "spotify:track:3" };
        var request = ConnectCommandTestHelpers.CreateSetQueueCommandRequest(108, "device_queue", trackUris);
        mockSource.SimulateRequest(request);
        await ConnectCommandTestHelpers.WaitForProcessingAsync();

        // Assert
        receivedCommands.Should().HaveCount(1);
        receivedCommands[0].TrackUris.Should().BeEquivalentTo(trackUris);

        await handler.DisposeAsync();
    }

    [Fact]
    public async Task AddToQueueCommand_WhenReceived_ShouldDispatchToAddToQueueObservable()
    {
        // WHY: Add to queue commands must include track URI

        // Arrange
        var (handler, mockSource) = ConnectCommandTestHelpers.CreateTestCommandHandler();
        var receivedCommands = new List<AddToQueueCommand>();
        handler.AddToQueueCommands.Subscribe(cmd => receivedCommands.Add(cmd));

        // Act
        var request = ConnectCommandTestHelpers.CreateAddToQueueCommandRequest(109, "device_queue", "spotify:track:xyz");
        mockSource.SimulateRequest(request);
        await ConnectCommandTestHelpers.WaitForProcessingAsync();

        // Assert
        receivedCommands.Should().HaveCount(1);
        receivedCommands[0].TrackUri.Should().Be("spotify:track:xyz");

        await handler.DisposeAsync();
    }

    [Fact]
    public async Task TransferCommand_WhenReceived_ShouldDispatchToTransferObservable()
    {
        // WHY: Transfer commands indicate playback moved from another device

        // Arrange
        var (handler, mockSource) = ConnectCommandTestHelpers.CreateTestCommandHandler();
        var receivedCommands = new List<TransferCommand>();
        handler.TransferCommands.Subscribe(cmd => receivedCommands.Add(cmd));

        // Act - Create a minimal TransferState protobuf
        var transferState = new TransferState { CurrentSession = new Session() };
        var transferStateBytes = transferState.ToByteArray();
        var request = ConnectCommandTestHelpers.CreateTransferCommandRequest(110, "device_transfer", transferStateBytes);
        mockSource.SimulateRequest(request);
        await ConnectCommandTestHelpers.WaitForProcessingAsync();

        // Assert
        receivedCommands.Should().HaveCount(1);
        receivedCommands[0].TransferState.Should().NotBeNull();

        await handler.DisposeAsync();
    }

    // ================================================================
    // SECTION 3: OBSERVABLE SUBSCRIPTION BEHAVIOR
    // ================================================================

    [Fact]
    public async Task CommandObservables_ShouldEmitToMultipleSubscribers()
    {
        // WHY: SafeSubject must support multiple subscribers

        // Arrange
        var (handler, mockSource) = ConnectCommandTestHelpers.CreateTestCommandHandler();
        var subscriber1 = new List<PauseCommand>();
        var subscriber2 = new List<PauseCommand>();
        var subscriber3 = new List<PauseCommand>();

        handler.PauseCommands.Subscribe(cmd => subscriber1.Add(cmd));
        handler.PauseCommands.Subscribe(cmd => subscriber2.Add(cmd));
        handler.PauseCommands.Subscribe(cmd => subscriber3.Add(cmd));

        // Act
        var request = ConnectCommandTestHelpers.CreatePauseCommandRequest(200, "device_multi");
        mockSource.SimulateRequest(request);
        await ConnectCommandTestHelpers.WaitForProcessingAsync();

        // Assert
        subscriber1.Should().HaveCount(1, "subscriber 1 should receive command");
        subscriber2.Should().HaveCount(1, "subscriber 2 should receive command");
        subscriber3.Should().HaveCount(1, "subscriber 3 should receive command");

        await handler.DisposeAsync();
    }

    [Fact]
    public async Task CommandObservables_SubscriberException_ShouldNotAffectOthers()
    {
        // WHY: SafeSubject must isolate exceptions between subscribers

        // Arrange
        var (handler, mockSource) = ConnectCommandTestHelpers.CreateTestCommandHandler();
        var goodSubscriber = new List<PauseCommand>();

        // Throwing subscriber
        handler.PauseCommands.Subscribe(cmd => throw new InvalidOperationException("Test exception"));

        // Good subscriber
        handler.PauseCommands.Subscribe(cmd => goodSubscriber.Add(cmd));

        // Act
        var request = ConnectCommandTestHelpers.CreatePauseCommandRequest(201, "device_exception");
        mockSource.SimulateRequest(request);
        await ConnectCommandTestHelpers.WaitForProcessingAsync();

        // Assert
        goodSubscriber.Should().HaveCount(1, "good subscriber should still receive command despite other subscriber throwing");

        await handler.DisposeAsync();
    }

    [Fact]
    public async Task CommandObservables_FilteredSubscription_ShouldOnlyReceiveMatchingCommands()
    {
        // WHY: LINQ operators should work on command observables

        // Arrange
        var (handler, mockSource) = ConnectCommandTestHelpers.CreateTestCommandHandler();
        var filtered = new List<PlayCommand>();

        // Subscribe with filter for specific context URI
        handler.PlayCommands
            .Where(cmd => cmd.ContextUri == "spotify:playlist:match")
            .Subscribe(cmd => filtered.Add(cmd));

        // Act - Send matching and non-matching commands
        var matchingRequest = ConnectCommandTestHelpers.CreatePlayCommandRequest(
            202, "device_filter", contextUri: "spotify:playlist:match");
        var nonMatchingRequest = ConnectCommandTestHelpers.CreatePlayCommandRequest(
            203, "device_filter", contextUri: "spotify:playlist:nomatch");

        mockSource.SimulateRequest(matchingRequest);
        mockSource.SimulateRequest(nonMatchingRequest);
        await ConnectCommandTestHelpers.WaitForProcessingAsync();

        // Assert
        filtered.Should().HaveCount(1, "only matching command should be received");
        filtered[0].ContextUri.Should().Be("spotify:playlist:match");

        await handler.DisposeAsync();
    }

    [Fact]
    public async Task CommandObservables_ShouldSupportEarlySubscription()
    {
        // WHY: Subscribers can subscribe before commands arrive

        // Arrange
        var (handler, mockSource) = ConnectCommandTestHelpers.CreateTestCommandHandler();
        var received = new List<PauseCommand>();

        // Subscribe before sending command
        handler.PauseCommands.Subscribe(cmd => received.Add(cmd));

        // Act - Send command after subscription
        var request = ConnectCommandTestHelpers.CreatePauseCommandRequest(204, "device_early");
        mockSource.SimulateRequest(request);
        await ConnectCommandTestHelpers.WaitForProcessingAsync();

        // Assert
        received.Should().HaveCount(1);

        await handler.DisposeAsync();
    }

    [Fact]
    public async Task CommandObservables_UnsubscribeThenResubscribe_ShouldWork()
    {
        // WHY: Observable should support resubscription

        // Arrange
        var (handler, mockSource) = ConnectCommandTestHelpers.CreateTestCommandHandler();
        var firstSubscription = new List<PauseCommand>();
        var secondSubscription = new List<PauseCommand>();

        // First subscription
        var disposable = handler.PauseCommands.Subscribe(cmd => firstSubscription.Add(cmd));

        // Act - Unsubscribe
        disposable.Dispose();

        // Send command (should not be received by first subscription)
        var request1 = ConnectCommandTestHelpers.CreatePauseCommandRequest(205, "device_resub");
        mockSource.SimulateRequest(request1);
        await ConnectCommandTestHelpers.WaitForProcessingAsync();

        // Resubscribe
        handler.PauseCommands.Subscribe(cmd => secondSubscription.Add(cmd));

        // Send another command
        var request2 = ConnectCommandTestHelpers.CreatePauseCommandRequest(206, "device_resub");
        mockSource.SimulateRequest(request2);
        await ConnectCommandTestHelpers.WaitForProcessingAsync();

        // Assert
        firstSubscription.Should().BeEmpty("first subscription was disposed");
        secondSubscription.Should().HaveCount(1, "second subscription should receive command");

        await handler.DisposeAsync();
    }

    // ================================================================
    // SECTION 4: REPLY TRACKING & TIMEOUT HANDLING
    // ================================================================

    [Fact]
    public async Task SendReplyAsync_WithValidKey_ShouldSendToDealerClient()
    {
        // WHY: Replies must be forwarded to dealer client

        // Arrange
        var (handler, mockSource) = ConnectCommandTestHelpers.CreateTestCommandHandler();
        var testKey = "300/device_reply";

        // Act
        await handler.SendReplyAsync(testKey, RequestResult.Success);

        // Assert
        mockSource.SentReplies.Should().Contain(r => r.key == testKey && r.result == RequestResult.Success,
            "reply should be sent to command source");

        await handler.DisposeAsync();
    }

    [Fact]
    public async Task WaitForReplyAsync_BeforeSend_ShouldWaitForReply()
    {
        // WHY: WaitForReply creates pending TaskCompletionSource

        // Arrange
        var (handler, _) = ConnectCommandTestHelpers.CreateTestCommandHandler();
        var testKey = "301/device_wait";

        // Act - Start waiting
        var waitTask = handler.WaitForReplyAsync(testKey, timeout: TimeSpan.FromSeconds(5));

        // Send reply after a short delay
        _ = Task.Run(async () =>
        {
            await Task.Delay(50);
            await handler.SendReplyAsync(testKey, RequestResult.Success);
        });

        var result = await waitTask;

        // Assert
        result.Should().Be(RequestResult.Success);

        await handler.DisposeAsync();
    }

    [Fact]
    public async Task WaitForReplyAsync_WithTimeout_ShouldReturnUpstreamError()
    {
        // WHY: Timeouts must not hang forever

        // Arrange
        var (handler, _) = ConnectCommandTestHelpers.CreateTestCommandHandler();
        var testKey = "302/device_timeout";

        // Act - Wait for reply that never comes
        var result = await handler.WaitForReplyAsync(testKey, timeout: TimeSpan.FromMilliseconds(100));

        // Assert
        result.Should().Be(RequestResult.UpstreamError, "timeout should return UpstreamError");

        await handler.DisposeAsync();
    }

    [Fact]
    public async Task WaitForReplyAsync_MultipleWaiters_ShouldAllReceiveResult()
    {
        // WHY: Multiple waiters on same key should all complete

        // Arrange
        var (handler, _) = ConnectCommandTestHelpers.CreateTestCommandHandler();
        var testKey = "303/device_multi_wait";

        // Act - Start 3 waiters
        var wait1 = handler.WaitForReplyAsync(testKey, timeout: TimeSpan.FromSeconds(5));
        var wait2 = handler.WaitForReplyAsync(testKey, timeout: TimeSpan.FromSeconds(5));
        var wait3 = handler.WaitForReplyAsync(testKey, timeout: TimeSpan.FromSeconds(5));

        // Send one reply
        await Task.Delay(50);
        await handler.SendReplyAsync(testKey, RequestResult.Success);

        var results = await Task.WhenAll(wait1, wait2, wait3);

        // Assert
        results.Should().OnlyContain(r => r == RequestResult.Success, "all waiters should receive the same result");

        await handler.DisposeAsync();
    }

    [Fact]
    public async Task WaitForReplyAsync_ConcurrentKeys_ShouldTrackIndependently()
    {
        // WHY: Different keys must be tracked separately

        // Arrange
        var (handler, _) = ConnectCommandTestHelpers.CreateTestCommandHandler();
        var key1 = "304/device1";
        var key2 = "305/device2";

        // Act - Start waiting on both keys
        var wait1 = handler.WaitForReplyAsync(key1, timeout: TimeSpan.FromSeconds(5));
        var wait2 = handler.WaitForReplyAsync(key2, timeout: TimeSpan.FromMilliseconds(100));

        // Send reply for key1 only
        await Task.Delay(50);
        await handler.SendReplyAsync(key1, RequestResult.Success);

        var result1 = await wait1;
        var result2 = await wait2;

        // Assert
        result1.Should().Be(RequestResult.Success, "key1 should receive success");
        result2.Should().Be(RequestResult.UpstreamError, "key2 should timeout");

        await handler.DisposeAsync();
    }

    // ================================================================
    // SECTION 5: ERROR SCENARIOS
    // ================================================================

    [Fact]
    public async Task UnknownEndpoint_ShouldSendErrorReply()
    {
        // WHY: Unsupported commands should return DeviceDoesNotSupportCommand

        // Arrange
        var (handler, mockSource) = ConnectCommandTestHelpers.CreateTestCommandHandler();
        // Act - Send unsupported command
        var request = ConnectCommandTestHelpers.CreateUnknownCommandRequest(400, "device_unknown", "unsupported_endpoint");
        mockSource.SimulateRequest(request);
        await ConnectCommandTestHelpers.WaitForProcessingAsync();

        // Assert - Verify error reply was sent
        mockSource.SentReplies.Should().Contain(r => r.key == "400/device_unknown" && r.result == RequestResult.DeviceDoesNotSupportCommand,
            "unsupported command should return error reply");

        await handler.DisposeAsync();
    }

    [Fact]
    public async Task MalformedRequest_InvalidMessageIdent_ShouldSendErrorReply()
    {
        // WHY: Malformed requests should not crash handler

        // Arrange
        var (handler, mockSource) = ConnectCommandTestHelpers.CreateTestCommandHandler();
        // Act - Send request with invalid messageIdent format (passes filter but fails parsing)
        var request = MockCommandSource.CreateRequestFromJson(
            messageId: 401,
            deviceId: "device_malformed",
            messageIdent: "hm://connect-state/v1/",  // Passes filter but no endpoint
            jsonPayload: "{}");

        mockSource.SimulateRequest(request);
        await ConnectCommandTestHelpers.WaitForProcessingAsync();

        // Assert - Should have sent error reply
        mockSource.SentReplies.Should().Contain(r => r.key == "401/device_malformed" && r.result == RequestResult.DeviceDoesNotSupportCommand);

        await handler.DisposeAsync();
    }

    // ================================================================
    // SECTION 6: CONCURRENT COMMAND PROCESSING
    // ================================================================

    [Fact]
    public async Task ConcurrentCommands_DifferentTypes_ShouldAllProcess()
    {
        // WHY: Multiple commands of different types should be processed concurrently

        // Arrange
        var (handler, mockSource) = ConnectCommandTestHelpers.CreateTestCommandHandler();
        var playCommands = new List<PlayCommand>();
        var pauseCommands = new List<PauseCommand>();
        var seekCommands = new List<SeekCommand>();

        handler.PlayCommands.Subscribe(cmd => playCommands.Add(cmd));
        handler.PauseCommands.Subscribe(cmd => pauseCommands.Add(cmd));
        handler.SeekCommands.Subscribe(cmd => seekCommands.Add(cmd));

        // Act - Send 3 different command types
        var requests = new[]
        {
            ConnectCommandTestHelpers.CreatePlayCommandRequest(500, "device_concurrent", contextUri: "spotify:playlist:1"),
            ConnectCommandTestHelpers.CreatePauseCommandRequest(501, "device_concurrent"),
            ConnectCommandTestHelpers.CreateSeekCommandRequest(502, "device_concurrent", 1000)
        };

        foreach (var request in requests)
        {
            mockSource.SimulateRequest(request);
        }

        await ConnectCommandTestHelpers.WaitForProcessingAsync(150);

        // Assert
        playCommands.Should().HaveCount(1);
        pauseCommands.Should().HaveCount(1);
        seekCommands.Should().HaveCount(1);

        await handler.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentCommands_SameType_ShouldAllProcess()
    {
        // WHY: Multiple commands of same type should not interfere

        // Arrange
        var (handler, mockSource) = ConnectCommandTestHelpers.CreateTestCommandHandler();
        var playCommands = new List<PlayCommand>();
        handler.PlayCommands.Subscribe(cmd => playCommands.Add(cmd));

        // Act - Send 5 play commands
        for (int i = 0; i < 5; i++)
        {
            var request = ConnectCommandTestHelpers.CreatePlayCommandRequest(
                510 + i,
                "device_same_type",
                contextUri: $"spotify:playlist:{i}");
            mockSource.SimulateRequest(request);
        }

        await ConnectCommandTestHelpers.WaitForProcessingAsync(150);

        // Assert
        playCommands.Should().HaveCount(5);
        playCommands.Select(c => c.ContextUri).Should().BeEquivalentTo(
            new[] { "spotify:playlist:0", "spotify:playlist:1", "spotify:playlist:2", "spotify:playlist:3", "spotify:playlist:4" });

        await handler.DisposeAsync();
    }

    // ================================================================
    // SECTION 7: DISPOSAL & CLEANUP
    // ================================================================

    [Fact]
    public async Task DisposeAsync_ShouldCompleteAllObservables()
    {
        // WHY: Subscribers must be notified when handler is disposed

        // Arrange
        var (handler, _) = ConnectCommandTestHelpers.CreateTestCommandHandler();

        var playCompleted = false;
        var pauseCompleted = false;
        var seekCompleted = false;

        handler.PlayCommands.Subscribe(_ => { }, () => playCompleted = true);
        handler.PauseCommands.Subscribe(_ => { }, () => pauseCompleted = true);
        handler.SeekCommands.Subscribe(_ => { }, () => seekCompleted = true);

        // Act
        await handler.DisposeAsync();
        await Task.Delay(50);

        // Assert
        playCompleted.Should().BeTrue("PlayCommands should complete on dispose");
        pauseCompleted.Should().BeTrue("PauseCommands should complete on dispose");
        seekCompleted.Should().BeTrue("SeekCommands should complete on dispose");
    }

    [Fact]
    public async Task DisposeAsync_MultipleDispose_ShouldBeIdempotent()
    {
        // WHY: Multiple dispose calls should not throw

        // Arrange
        var (handler, _) = ConnectCommandTestHelpers.CreateTestCommandHandler();

        // Act & Assert
        await handler.DisposeAsync();
        var act = async () => await handler.DisposeAsync();

        await act.Should().NotThrowAsync("multiple dispose should be idempotent");
    }

    // ================================================================
    // SECTION 8: INTEGRATION TESTS
    // ================================================================

    [Fact]
    public async Task EndToEndWorkflow_PlayCommand_WithReply()
    {
        // WHY: Verify complete command flow from dealer to reply

        // Arrange
        var (handler, mockSource) = ConnectCommandTestHelpers.CreateTestCommandHandler();
        var receivedCommand = false;
        handler.PlayCommands.Subscribe(async cmd =>
        {
            receivedCommand = true;
            // Simulate handler processing and sending reply
            await handler.SendReplyAsync(cmd.Key, RequestResult.Success);
        });

        // Act - Send command and wait for reply
        var request = ConnectCommandTestHelpers.CreatePlayCommandRequest(
            600,
            "device_e2e",
            contextUri: "spotify:playlist:test");

        mockSource.SimulateRequest(request);
        await ConnectCommandTestHelpers.WaitForProcessingAsync(150);

        // Assert
        receivedCommand.Should().BeTrue("command should be received");
        mockSource.SentReplies.Should().Contain(r => r.key == "600/device_e2e" && r.result == RequestResult.Success,
            "reply should be sent");

        await handler.DisposeAsync();
    }

    [Fact]
    public async Task EndToEndWorkflow_MultipleCommandTypes_InSequence()
    {
        // WHY: Verify handler processes different command types in sequence

        // Arrange
        var (handler, mockSource) = ConnectCommandTestHelpers.CreateTestCommandHandler();
        var commandSequence = new List<string>();

        handler.PlayCommands.Subscribe(cmd => commandSequence.Add("play"));
        handler.PauseCommands.Subscribe(cmd => commandSequence.Add("pause"));
        handler.ResumeCommands.Subscribe(cmd => commandSequence.Add("resume"));
        handler.SkipNextCommands.Subscribe(cmd => commandSequence.Add("skip_next"));

        // Act - Send commands in sequence
        mockSource.SimulateRequest(
            ConnectCommandTestHelpers.CreatePlayCommandRequest(610, "device_seq", contextUri: "spotify:playlist:1"));
        mockSource.SimulateRequest(
            ConnectCommandTestHelpers.CreatePauseCommandRequest(611, "device_seq"));
        mockSource.SimulateRequest(
            ConnectCommandTestHelpers.CreateResumeCommandRequest(612, "device_seq"));
        mockSource.SimulateRequest(
            ConnectCommandTestHelpers.CreateSkipNextCommandRequest(613, "device_seq"));

        await ConnectCommandTestHelpers.WaitForProcessingAsync(150);

        // Assert
        commandSequence.Should().Equal("play", "pause", "resume", "skip_next");

        await handler.DisposeAsync();
    }

    [Fact]
    public async Task EndToEndWorkflow_WithReplyTracking_CompletesCorrectly()
    {
        // WHY: Verify reply tracking works end-to-end

        // Arrange
        var (handler, mockSource) = ConnectCommandTestHelpers.CreateTestCommandHandler();
        var commandKey = string.Empty;
        var replyTcs = new TaskCompletionSource<bool>();

        handler.PlayCommands.Subscribe(async cmd =>
        {
            commandKey = cmd.Key;
            // Simulate processing and reply
            await Task.Delay(50);
            await handler.SendReplyAsync(cmd.Key, RequestResult.Success);
            replyTcs.TrySetResult(true);  // Signal that reply was sent
        });

        // Act - Send command
        var request = ConnectCommandTestHelpers.CreatePlayCommandRequest(
            620,
            "device_tracking",
            contextUri: "spotify:playlist:test");

        mockSource.SimulateRequest(request);

        // Wait for reply to be sent (with timeout for safety)
        var replySent = await Task.WhenAny(
            replyTcs.Task,
            Task.Delay(TimeSpan.FromSeconds(1))
        );

        if (replySent != replyTcs.Task)
        {
            throw new TimeoutException("Subscriber did not send reply within expected time");
        }

        // Wait for reply
        var result = await handler.WaitForReplyAsync(commandKey, timeout: TimeSpan.FromSeconds(2));

        // Assert
        result.Should().Be(RequestResult.Success);

        await handler.DisposeAsync();
    }
}
