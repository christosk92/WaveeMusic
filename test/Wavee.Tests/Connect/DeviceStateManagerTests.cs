using FluentAssertions;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Wavee.Connect;
using Wavee.Connect.Protocol;
using Wavee.Core.Http;
using Wavee.Protocol.Player;
using Wavee.Tests.Helpers;
using Xunit;

namespace Wavee.Tests.Connect;

/// <summary>
/// Tests for DeviceStateManager - manages Spotify Connect device state and coordinates state updates.
/// </summary>
/// <remarks>
/// WHY: DeviceStateManager is critical for Spotify Connect functionality - it orchestrates
/// all device state synchronization with Spotify's cloud API. Testing ensures reliable device
/// presence announcement, volume control, and state updates.
/// </remarks>
public sealed class DeviceStateManagerTests
{
    #region Initialization Tests

    [Fact]
    public async Task Constructor_WithValidDependencies_ShouldInitialize()
    {
        // WHY: Verify DeviceStateManager initializes correctly with valid dependencies
        // and sets up subscriptions

        // Arrange & Act
        await using var stateManager = DeviceStateTestHelpers.CreateDeviceStateManager();

        // Assert
        stateManager.Should().NotBeNull();
        stateManager.IsActive.Should().BeFalse("device starts inactive");
        stateManager.CurrentVolume.Should().Be(ConnectStateHelpers.MaxVolume / 2, "default volume is mid-range");
    }

    [Fact]
    public async Task Constructor_WithCustomInitialVolume_ShouldClampToValidRange()
    {
        // WHY: Volume must always be within valid range (0-65535)

        // Arrange & Act - Test values outside valid range
        await using var tooLow = DeviceStateTestHelpers.CreateDeviceStateManager(initialVolume: -1000);
        await using var tooHigh = DeviceStateTestHelpers.CreateDeviceStateManager(initialVolume: 100000);

        // Assert
        tooLow.CurrentVolume.Should().Be(0, "negative volume clamped to 0");
        tooHigh.CurrentVolume.Should().Be(ConnectStateHelpers.MaxVolume, "excessive volume clamped to max");
    }

    [Fact]
    public void Constructor_ShouldThrowOnNullDependencies()
    {
        // WHY: Null dependencies should fail fast with clear error messages

        // Arrange
        var session = DealerTestHelpers.CreateMockSession();
        var spClient = MockSpClientHelpers.CreateMockSpClient();
        var dealerClient = DeviceStateTestHelpers.CreateMockDealerClient();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new DeviceStateManager(null!, spClient, dealerClient));
        Assert.Throws<ArgumentNullException>(() =>
            new DeviceStateManager(session, null!, dealerClient));
        Assert.Throws<ArgumentNullException>(() =>
            new DeviceStateManager(session, spClient, null!));
    }

    #endregion

    #region Volume Observable Tests

    [Fact]
    public async Task VolumeObservable_ShouldEmitInitialVolume()
    {
        // WHY: New subscribers should immediately receive the current volume value

        // Arrange
        const int initialVolume = 32768;
        await using var stateManager = DeviceStateTestHelpers.CreateDeviceStateManager(initialVolume: initialVolume);

        // Act
        var receivedVolume = await stateManager.Volume.FirstAsync();

        // Assert
        receivedVolume.Should().Be(initialVolume, "observable emits initial volume to new subscribers");
    }

    [Fact]
    public async Task VolumeObservable_ShouldSupportMultipleSubscribers()
    {
        // WHY: Multiple components may need to observe volume changes

        // Arrange
        await using var stateManager = DeviceStateTestHelpers.CreateDeviceStateManager();
        var subscriber1Values = new List<int>();
        var subscriber2Values = new List<int>();

        var sub1 = stateManager.Volume.Subscribe(v => subscriber1Values.Add(v));
        var sub2 = stateManager.Volume.Subscribe(v => subscriber2Values.Add(v));

        // Act
        await stateManager.SetVolumeAsync(40000);
        await Task.Delay(100); // Allow subscriptions to receive

        // Assert
        subscriber1Values.Should().Contain(40000, "first subscriber receives volume change");
        subscriber2Values.Should().Contain(40000, "second subscriber receives volume change");

        sub1.Dispose();
        sub2.Dispose();
    }

    #endregion

    #region SetVolumeAsync Tests

    [Fact]
    public async Task SetVolumeAsync_ShouldUpdateVolumeAndNotifyObservers()
    {
        // WHY: Local volume changes must update internal state and notify subscribers

        // Arrange
        var tracker = new MockSpClientHelpers.PutStateCallTracker();
        var spClient = MockSpClientHelpers.CreateMockSpClient(tracker);
        await using var stateManager = DeviceStateTestHelpers.CreateDeviceStateManager(spClient: spClient);

        var volumeChanges = new List<int>();
        stateManager.Volume.Subscribe(v => volumeChanges.Add(v));

        // Act
        await stateManager.SetVolumeAsync(30000);
        await Task.Delay(50); // Allow async processing

        // Assert
        stateManager.CurrentVolume.Should().Be(30000, "volume updated");
        volumeChanges.Should().Contain(30000, "observable notified");
    }

    [Fact]
    public async Task SetVolumeAsync_ShouldClampToValidRange()
    {
        // WHY: Volume must always be within 0-65535 range

        // Arrange
        await using var stateManager = DeviceStateTestHelpers.CreateDeviceStateManager();

        // Act & Assert
        await stateManager.SetVolumeAsync(-100);
        stateManager.CurrentVolume.Should().Be(0, "negative volume clamped to 0");

        await stateManager.SetVolumeAsync(100000);
        stateManager.CurrentVolume.Should().Be(ConnectStateHelpers.MaxVolume, "excessive volume clamped to max");
    }

    [Fact]
    public async Task SetVolumeAsync_WithSameValue_ShouldBeNoOp()
    {
        // WHY: Optimization - avoid unnecessary state updates for unchanged volume

        // Arrange
        var tracker = new MockSpClientHelpers.PutStateCallTracker();
        var spClient = MockSpClientHelpers.CreateMockSpClient(tracker);
        await using var stateManager = DeviceStateTestHelpers.CreateDeviceStateManager(spClient: spClient, initialVolume: 30000);

        tracker.Calls.Clear(); // Clear any initialization calls

        // Act
        await stateManager.SetVolumeAsync(30000); // Same as current
        await Task.Delay(50);

        // Assert
        tracker.Calls.Should().BeEmpty("no PUT state call when volume unchanged");
    }

    #endregion

    #region SetActiveAsync Tests

    [Fact]
    public async Task SetActiveAsync_ShouldUpdateActiveState()
    {
        // WHY: Device must correctly report active/inactive state

        // Arrange
        await using var stateManager = DeviceStateTestHelpers.CreateDeviceStateManager();
        stateManager.IsActive.Should().BeFalse("starts inactive");

        // Act
        await stateManager.SetActiveAsync(true);

        // Assert
        stateManager.IsActive.Should().BeTrue("device activated");
    }

    [Fact]
    public async Task SetActiveAsync_True_ShouldUseNewDeviceReason()
    {
        // WHY: NEW_DEVICE reason signals device joining the Connect cluster (modern Connect State API)

        // Arrange
        var tracker = new MockSpClientHelpers.PutStateCallTracker();
        var spClient = MockSpClientHelpers.CreateMockSpClient(tracker);
        var (dealerClient, mockConnection) = DeviceStateTestHelpers.CreateMockDealerClientWithConnection();
        await using var stateManager = DeviceStateTestHelpers.CreateDeviceStateManager(
            spClient: spClient,
            dealerClient: dealerClient);

        // Simulate connection ID being received
        var headers = new Dictionary<string, string>
        {
            ["Spotify-Connection-Id"] = "test-connection-id"
        };
        var connectionIdJson = DealerTestHelpers.CreateDealerMessage(
            "hm://pusher/v1/connections/",
            headers,
            Array.Empty<byte>());
        await mockConnection.SimulateMessageAsync(connectionIdJson);
        await Task.Delay(50); // Allow subscription to process

        // Act
        await stateManager.SetActiveAsync(true);
        await Task.Delay(50);

        // Assert
        var activeCall = tracker.Calls.LastOrDefault(c => c.Request.IsActive);
        activeCall.Should().NotBeNull("PUT state called with IsActive=true");
        activeCall!.Request.PutStateReason.Should().Be(PutStateReason.NewDevice,
            "NEW_DEVICE reason used when activating (modern Connect State API)");
    }

    [Fact]
    public async Task SetActiveAsync_WithSameValue_ShouldBeNoOp()
    {
        // WHY: Optimization - avoid unnecessary state updates

        // Arrange
        var tracker = new MockSpClientHelpers.PutStateCallTracker();
        var spClient = MockSpClientHelpers.CreateMockSpClient(tracker);
        await using var stateManager = DeviceStateTestHelpers.CreateDeviceStateManager(spClient: spClient);

        tracker.Calls.Clear();

        // Act - Set to false when already false
        await stateManager.SetActiveAsync(false);
        await Task.Delay(50);

        // Assert
        tracker.Calls.Should().BeEmpty("no PUT state when active state unchanged");
    }

    #endregion

    #region Disposal Tests

    [Fact]
    public async Task DisposeAsync_ShouldBeIdempotent()
    {
        // WHY: Multiple dispose calls should not throw or cause errors

        // Arrange
        var stateManager = DeviceStateTestHelpers.CreateDeviceStateManager();

        // Act & Assert - Should not throw
        await stateManager.DisposeAsync();
        await stateManager.DisposeAsync(); // Second dispose
    }

    [Fact]
    public async Task DisposeAsync_ShouldCompleteVolumeObservable()
    {
        // WHY: Subscribers should be notified when observable completes

        // Arrange
        var stateManager = DeviceStateTestHelpers.CreateDeviceStateManager();
        var completed = false;
        stateManager.Volume.Subscribe(
            onNext: _ => { },
            onCompleted: () => completed = true);

        // Act
        await stateManager.DisposeAsync();
        await Task.Delay(50);

        // Assert
        completed.Should().BeTrue("volume observable completed on dispose");
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public async Task SetVolumeAsync_WhenSpClientFails_ShouldLogButNotThrow()
    {
        // WHY: Network errors shouldn't crash the device state manager

        // Arrange
        var failingSpClient = MockSpClientHelpers.CreateFailingSpClient(
            new SpClientException(SpClientFailureReason.ServerError, "Test error"));
        await using var stateManager = DeviceStateTestHelpers.CreateDeviceStateManager(spClient: failingSpClient);

        // Act & Assert - Should not throw
        await stateManager.SetVolumeAsync(30000);
        // Volume should still update locally even if PUT fails
        stateManager.CurrentVolume.Should().Be(30000, "local volume updated despite PUT failure");
    }

    #endregion

    #region Message ID Sequencing Tests

    [Fact]
    public async Task UpdateStateAsync_ShouldIncrementMessageId()
    {
        // WHY: Message IDs must be unique and monotonically increasing

        // Arrange
        var tracker = new MockSpClientHelpers.PutStateCallTracker();
        var spClient = MockSpClientHelpers.CreateMockSpClient(tracker);
        await using var stateManager = DeviceStateTestHelpers.CreateDeviceStateManager(spClient: spClient);

        // Act - Trigger multiple state updates
        await stateManager.SetVolumeAsync(10000);
        await Task.Delay(50);
        await stateManager.SetVolumeAsync(20000);
        await Task.Delay(50);
        await stateManager.SetVolumeAsync(30000);
        await Task.Delay(50);

        // Assert
        var messageIds = tracker.Calls.Select(c => c.Request.MessageId).ToList();
        messageIds.Should().OnlyHaveUniqueItems("each message has unique ID");
        messageIds.Should().BeInAscendingOrder("message IDs increment");
    }

    #endregion
}
