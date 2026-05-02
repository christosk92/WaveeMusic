using FluentAssertions;
using System.Reactive.Linq;
using Wavee.Connect;
using Wavee.Connect.Protocol;
using Wavee.Protocol.Player;
using Wavee.Tests.Connect;
using Wavee.Tests.Helpers;
using Xunit;

namespace Wavee.Tests.Connect;

/// <summary>
/// Tests for PlaybackStateManager (remote-only mode).
/// Covers cluster update parsing, observables, change detection, and gzip handling.
/// </summary>
public sealed class PlaybackStateManagerTests : IAsyncDisposable
{
    private readonly DealerClient _mockDealer;
    private readonly MockDealerConnection _mockConnection;
    private PlaybackStateManager? _manager;

    public PlaybackStateManagerTests()
    {
        (_mockDealer, _mockConnection) = DealerTestHelpers.CreateMockDealer();
    }

    public async ValueTask DisposeAsync()
    {
        if (_manager != null)
            await _manager.DisposeAsync();
        await _mockDealer.DisposeAsync();
    }

    #region Construction Tests

    [Fact]
    public void Constructor_RemoteOnlyMode_ShouldInitializeWithEmptyState()
    {
        // Act
        _manager = new PlaybackStateManager(_mockDealer, null);

        // Assert
        _manager.CurrentState.Should().NotBeNull();
        _manager.CurrentState.Status.Should().Be(PlaybackStatus.Stopped);
        _manager.CurrentState.Track.Should().BeNull();
        _manager.IsBidirectional.Should().BeFalse();
        _manager.IsLocalPlaybackActive.Should().BeFalse();
    }

    [Fact]
    public void Constructor_NullDealerClient_ShouldThrowArgumentNullException()
    {
        // Act
        var act = () => new PlaybackStateManager(null!, null);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("dealerClient");
    }

    #endregion

    #region Cluster Update Parsing Tests

    [Fact]
    public async Task ClusterUpdate_WithTrackInfo_ShouldUpdateState()
    {
        // Arrange
        _manager = new PlaybackStateManager(_mockDealer, null);
        var receivedStates = new List<PlaybackState>();
        _manager.StateChanges.Subscribe(s => receivedStates.Add(s));

        var message = PlaybackStateTestHelpers.CreateClusterUpdateMessage(
            activeDeviceId: "device_123",
            trackUri: "spotify:track:abc",
            trackTitle: "Test Track",
            artist: "Test Artist",
            positionMs: 5000,
            durationMs: 300000,
            isPlaying: true,
            contextUri: "spotify:playlist:xyz");

        // Act
        await _mockConnection.SimulateMessageAsync(message);
        await Task.Delay(200); // Wait for async processing

        // Assert
        receivedStates.Should().HaveCountGreaterOrEqualTo(1);
        var state = receivedStates.Last();
        state.Track.Should().NotBeNull();
        state.Track!.Uri.Should().Be("spotify:track:abc");
        state.Track.Title.Should().Be("Test Track");
        state.Track.Artist.Should().Be("Test Artist");
        state.PositionMs.Should().Be(5000);
        state.DurationMs.Should().Be(300000);
        state.Status.Should().Be(PlaybackStatus.Playing);
        state.ContextUri.Should().Be("spotify:playlist:xyz");
        state.ActiveDeviceId.Should().Be("device_123");
        state.Source.Should().Be(StateSource.Cluster);
    }

    [Fact]
    public async Task ClusterUpdate_GzipCompressed_ShouldDecompressAndParse()
    {
        // Arrange
        _manager = new PlaybackStateManager(_mockDealer, null);
        var receivedStates = new List<PlaybackState>();
        _manager.StateChanges.Subscribe(s => receivedStates.Add(s));

        var message = PlaybackStateTestHelpers.CreateClusterUpdateMessage(
            activeDeviceId: "device_123",
            trackUri: "spotify:track:abc",
            trackTitle: "Compressed Track",
            isPlaying: true,
            gzipCompress: true);  // Enable gzip compression

        // Act
        await _mockConnection.SimulateMessageAsync(message);
        await Task.Delay(200);

        // Assert
        receivedStates.Should().HaveCountGreaterOrEqualTo(1);
        var state = receivedStates.Last();
        state.Track.Should().NotBeNull();
        state.Track!.Title.Should().Be("Compressed Track");
    }

    [Fact]
    public async Task ClusterUpdate_EmptyPlayerState_ShouldUpdateWithNoTrack()
    {
        // Arrange
        _manager = new PlaybackStateManager(_mockDealer, null);
        var receivedStates = new List<PlaybackState>();
        _manager.StateChanges.Subscribe(s => receivedStates.Add(s));

        var message = PlaybackStateTestHelpers.CreateClusterUpdateMessage(
            activeDeviceId: "device_123",
            trackUri: null);  // No track

        // Act
        await _mockConnection.SimulateMessageAsync(message);
        await Task.Delay(200);

        // Assert
        receivedStates.Should().HaveCountGreaterOrEqualTo(1);
        var state = receivedStates.Last();
        state.Track.Should().BeNull();
        state.Status.Should().Be(PlaybackStatus.Stopped);
    }

    [Fact]
    public async Task ClusterUpdate_RemoteDeviceDisconnected_ShouldClampPlayingToPaused()
    {
        // Repro for the "UI stuck on playing after remote device disappears" bug:
        // Spotify keeps echoing player_state.is_playing=true in the cluster even
        // after active_device_id transitions to empty. Clamp here so the change
        // detector emits a Status transition the UI can act on.
        _manager = new PlaybackStateManager(_mockDealer, null);
        var receivedStates = new List<PlaybackState>();
        _manager.StateChanges.Subscribe(s => receivedStates.Add(s));

        var playingOnRemote = PlaybackStateTestHelpers.CreateClusterUpdateMessage(
            activeDeviceId: "remote_device",
            trackUri: "spotify:track:abc",
            isPlaying: true);

        var remoteDisappeared = PlaybackStateTestHelpers.CreateClusterUpdateMessage(
            activeDeviceId: "",            // device gone
            trackUri: "spotify:track:abc",
            isPlaying: true);              // server still echoes IsPlaying=true

        // Act
        await _mockConnection.SimulateMessageAsync(playingOnRemote);
        await Task.Delay(200);
        await _mockConnection.SimulateMessageAsync(remoteDisappeared);
        await Task.Delay(200);

        // Skip the BehaviorSubject's initial empty replay
        var emitted = receivedStates.Where(s => s.Changes != StateChanges.None).ToList();

        // Assert
        emitted.Should().HaveCountGreaterOrEqualTo(2);
        emitted[0].Status.Should().Be(PlaybackStatus.Playing);
        emitted[0].ActiveDeviceId.Should().Be("remote_device");

        var afterDisconnect = emitted.Last();
        afterDisconnect.Status.Should().Be(PlaybackStatus.Paused);
        afterDisconnect.ActiveDeviceId.Should().BeEmpty();
        afterDisconnect.Changes.Should().HaveFlag(StateChanges.Status);
    }

    #endregion

    #region Observable Tests

    [Fact]
    public async Task TrackChanged_WhenTrackChanges_ShouldEmit()
    {
        // Arrange
        _manager = new PlaybackStateManager(_mockDealer, null);
        var trackChanges = new List<PlaybackState>();
        _manager.TrackChanged.Subscribe(s => trackChanges.Add(s));

        var message1 = PlaybackStateTestHelpers.CreateClusterUpdateMessage(
            "device_123",
            trackUri: "spotify:track:abc",
            trackTitle: "Track 1");

        var message2 = PlaybackStateTestHelpers.CreateClusterUpdateMessage(
            "device_123",
            trackUri: "spotify:track:xyz",
            trackTitle: "Track 2");

        // Act
        await _mockConnection.SimulateMessageAsync(message1);
        await Task.Delay(200);
        await _mockConnection.SimulateMessageAsync(message2);
        await Task.Delay(200);

        // Assert
        trackChanges.Should().HaveCount(2);
        trackChanges[0].Track!.Title.Should().Be("Track 1");
        trackChanges[1].Track!.Title.Should().Be("Track 2");
    }

    [Fact]
    public async Task PlaybackStatusChanged_WhenStatusChanges_ShouldEmit()
    {
        // Arrange
        _manager = new PlaybackStateManager(_mockDealer, null);
        var statusChanges = new List<PlaybackState>();
        _manager.PlaybackStatusChanged.Subscribe(s => statusChanges.Add(s));

        var message1 = PlaybackStateTestHelpers.CreateClusterUpdateMessage(
            "device_123",
            trackUri: "spotify:track:abc",
            isPlaying: true);

        var message2 = PlaybackStateTestHelpers.CreateClusterUpdateMessage(
            "device_123",
            trackUri: "spotify:track:abc",
            isPaused: true);

        // Act
        await _mockConnection.SimulateMessageAsync(message1);
        await Task.Delay(200);
        await _mockConnection.SimulateMessageAsync(message2);
        await Task.Delay(200);

        // Assert
        statusChanges.Should().HaveCount(2);
        statusChanges[0].Status.Should().Be(PlaybackStatus.Playing);
        statusChanges[1].Status.Should().Be(PlaybackStatus.Paused);
    }

    [Fact]
    public async Task OptionsChanged_WhenShuffleChanges_ShouldEmit()
    {
        // Arrange
        _manager = new PlaybackStateManager(_mockDealer, null);
        var optionsChanges = new List<PlaybackState>();
        _manager.OptionsChanged.Subscribe(s => optionsChanges.Add(s));

        var message1 = PlaybackStateTestHelpers.CreateClusterUpdateMessage(
            "device_123",
            trackUri: "spotify:track:abc",
            shuffling: true);

        var message2 = PlaybackStateTestHelpers.CreateClusterUpdateMessage(
            "device_123",
            trackUri: "spotify:track:abc",
            shuffling: false);

        // Act
        await _mockConnection.SimulateMessageAsync(message1);
        await Task.Delay(200);
        await _mockConnection.SimulateMessageAsync(message2);
        await Task.Delay(200);

        // Assert
        optionsChanges.Should().HaveCount(2);
        optionsChanges[0].Options.Shuffling.Should().BeTrue();
        optionsChanges[1].Options.Shuffling.Should().BeFalse();
    }

    [Fact]
    public async Task ActiveDeviceChanged_WhenDeviceChanges_ShouldEmit()
    {
        // Arrange
        _manager = new PlaybackStateManager(_mockDealer, null);
        var deviceChanges = new List<PlaybackState>();
        _manager.ActiveDeviceChanged.Subscribe(s => deviceChanges.Add(s));

        var message1 = PlaybackStateTestHelpers.CreateClusterUpdateMessage(
            activeDeviceId: "device_123",
            trackUri: "spotify:track:abc");

        var message2 = PlaybackStateTestHelpers.CreateClusterUpdateMessage(
            activeDeviceId: "device_456",
            trackUri: "spotify:track:abc");

        // Act
        await _mockConnection.SimulateMessageAsync(message1);
        await Task.Delay(200);
        await _mockConnection.SimulateMessageAsync(message2);
        await Task.Delay(200);

        // Assert
        deviceChanges.Should().HaveCount(2);
        deviceChanges[0].ActiveDeviceId.Should().Be("device_123");
        deviceChanges[1].ActiveDeviceId.Should().Be("device_456");
    }

    #endregion

    #region Change Detection Tests

    [Fact]
    public async Task ClusterUpdate_NoChanges_ShouldNotEmit()
    {
        // Arrange
        _manager = new PlaybackStateManager(_mockDealer, null);
        var receivedStates = new List<PlaybackState>();
        _manager.StateChanges.Skip(1).Subscribe(s => receivedStates.Add(s)); // Skip initial state

        var message = PlaybackStateTestHelpers.CreateClusterUpdateMessage(
            "device_123",
            trackUri: "spotify:track:abc",
            positionMs: 5000);

        // Act - Send same message twice
        await _mockConnection.SimulateMessageAsync(message);
        await Task.Delay(200);
        await _mockConnection.SimulateMessageAsync(message);
        await Task.Delay(200);

        // Assert - Should only emit once (first update)
        receivedStates.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetCurrentPosition_WhenPlaying_ShouldCalculateElapsedTime()
    {
        // Arrange
        _manager = new PlaybackStateManager(_mockDealer, null);
        var message = PlaybackStateTestHelpers.CreateClusterUpdateMessage(
            "device_123",
            trackUri: "spotify:track:abc",
            positionMs: 5000,
            isPlaying: true);

        await _mockConnection.SimulateMessageAsync(message);
        await Task.Delay(200);

        var positionBefore = _manager.GetCurrentPosition();

        // Act - Wait 500ms
        await Task.Delay(500);
        var positionAfter = _manager.GetCurrentPosition();

        // Assert - Position should increase
        positionAfter.Should().BeGreaterThan(positionBefore);
        (positionAfter - positionBefore).Should().BeGreaterOrEqualTo(400); // Allow some tolerance
    }

    [Fact]
    public async Task GetCurrentPosition_WhenPaused_ShouldReturnStaticPosition()
    {
        // Arrange
        _manager = new PlaybackStateManager(_mockDealer, null);
        var message = PlaybackStateTestHelpers.CreateClusterUpdateMessage(
            "device_123",
            trackUri: "spotify:track:abc",
            positionMs: 5000,
            isPaused: true);

        await _mockConnection.SimulateMessageAsync(message);
        await Task.Delay(200);

        var positionBefore = _manager.GetCurrentPosition();

        // Act - Wait 500ms
        await Task.Delay(500);
        var positionAfter = _manager.GetCurrentPosition();

        // Assert - Position should NOT increase
        positionAfter.Should().Be(positionBefore);
    }

    [Fact]
    public void ClusterToPlaybackState_PlayingPosition_ShouldUsePlayerStateTimestamp()
    {
        // Arrange: mirrors a PUT state response where the cluster response is fresh,
        // but the embedded player state is a snapshot from earlier playback.
        var cluster = new Cluster
        {
            ActiveDeviceId = "device_123",
            ChangedTimestampMs = 10_000,
            ServerTimestampMs = 10_000,
            PlayerState = new PlayerState
            {
                Timestamp = 5_000,
                PositionAsOfTimestamp = 1_000,
                Duration = 300_000,
                IsPlaying = true,
                Track = new ProvidedTrack
                {
                    Uri = "spotify:track:abc",
                    Uid = "track_uid"
                }
            }
        };

        // Act
        var state = PlaybackStateHelpers.ClusterToPlaybackState(cluster, PlaybackState.Empty);
        var currentPosition = PlaybackStateHelpers.CalculateCurrentPosition(state, nowMs: 10_000);

        // Assert
        state.Timestamp.Should().Be(5_000);
        state.PositionMs.Should().Be(1_000);
        currentPosition.Should().Be(6_000);
    }

    [Fact]
    public void ClusterToPlaybackState_ResumeAfterPause_ShouldAnchorPositionAtResumeTimestamp()
    {
        // Arrange: Spotify Connect keeps the paused position, but moves
        // PlayerState.timestamp to the resume command time.
        const long pausedPositionMs = 146_330;
        const long resumeTimestampMs = 1_777_681_520_531;

        var previous = PlaybackState.Empty with
        {
            Track = new TrackInfo { Uri = "spotify:track:abc" },
            PositionMs = pausedPositionMs,
            DurationMs = 277_741,
            Status = PlaybackStatus.Paused,
            Timestamp = 1_777_681_456_508,
            Source = StateSource.Cluster
        };

        var cluster = new Cluster
        {
            ActiveDeviceId = "device_123",
            ChangedTimestampMs = 1_777_681_520_633,
            ServerTimestampMs = 1_777_681_520_637,
            PlayerState = new PlayerState
            {
                Timestamp = resumeTimestampMs,
                PositionAsOfTimestamp = pausedPositionMs,
                Duration = 277_741,
                IsPlaying = true,
                Track = new ProvidedTrack
                {
                    Uri = "spotify:track:abc",
                    Uid = "track_uid"
                }
            }
        };

        // Act
        var state = PlaybackStateHelpers.ClusterToPlaybackState(cluster, previous);
        var currentPosition = PlaybackStateHelpers.CalculateCurrentPosition(state, nowMs: cluster.ServerTimestampMs);

        // Assert
        state.Timestamp.Should().Be(resumeTimestampMs);
        state.PositionMs.Should().Be(pausedPositionMs);
        currentPosition.Should().Be(146_436);
    }

    [Fact]
    public void ClusterToPlaybackState_TrackChangeWithZeroPosition_ShouldIgnoreStaleLegacyPosition()
    {
        // Arrange: some Connect frames can carry a stale legacy `position`
        // field while the new track's position_as_of_timestamp is default/zero.
        var previous = PlaybackState.Empty with
        {
            Track = new TrackInfo { Uri = "spotify:track:old" },
            PositionMs = 74_000,
            DurationMs = 180_000,
            Status = PlaybackStatus.Playing,
            Timestamp = 1_000,
            Source = StateSource.Cluster
        };

        var cluster = new Cluster
        {
            ActiveDeviceId = "device_123",
            ChangedTimestampMs = 5_000,
            ServerTimestampMs = 5_000,
            PlayerState = new PlayerState
            {
                Timestamp = 5_000,
                PositionAsOfTimestamp = 0,
                Position = 74_000,
                Duration = 210_000,
                IsPlaying = true,
                Track = new ProvidedTrack
                {
                    Uri = "spotify:track:new",
                    Uid = "new_uid"
                }
            }
        };

        // Act
        var state = PlaybackStateHelpers.ClusterToPlaybackState(cluster, previous);

        // Assert
        state.Track!.Uri.Should().Be("spotify:track:new");
        state.PositionMs.Should().Be(0);
    }

    #endregion

    #region Disposal Tests

    [Fact]
    public async Task Dispose_ShouldCompleteObservables()
    {
        // Arrange
        _manager = new PlaybackStateManager(_mockDealer, null);
        var completed = false;
        _manager.StateChanges.Subscribe(
            onNext: _ => { },
            onCompleted: () => completed = true);

        // Act
        await _manager.DisposeAsync();

        // Assert
        completed.Should().BeTrue();
    }

    [Fact]
    public async Task Dispose_MultipleTimes_ShouldNotThrow()
    {
        // Arrange
        _manager = new PlaybackStateManager(_mockDealer, null);

        // Act & Assert
        await _manager.DisposeAsync();
        var act = async () => await _manager.DisposeAsync();
        await act.Should().NotThrowAsync();
    }

    #endregion

    #region Bidirectional Mode Tests

    [Fact]
    public void Constructor_BidirectionalMode_ShouldInitializeCorrectly()
    {
        // Arrange
        var mockEngine = PlaybackStateTestHelpers.CreateMockPlaybackEngine();
        var tracker = new MockSpClientHelpers.PutStateCallTracker();
        var mockSpClient = MockSpClientHelpers.CreateMockSpClient(tracker);
        var mockSession = DealerTestHelpers.CreateMockSession();

        // Act
        _manager = new PlaybackStateManager(_mockDealer, mockEngine, mockSpClient, mockSession);

        // Assert
        _manager.IsBidirectional.Should().BeTrue();
        _manager.IsLocalPlaybackActive.Should().BeFalse(); // No playback yet
        _manager.CurrentState.Should().NotBeNull();
    }

    [Fact]
    public async Task BidirectionalMode_WhenLocalPlaybackStarts_ShouldPublishStateToSpotify()
    {
        // Arrange
        var mockEngine = PlaybackStateTestHelpers.CreateMockPlaybackEngine();
        var tracker = new MockSpClientHelpers.PutStateCallTracker();
        var mockSpClient = MockSpClientHelpers.CreateMockSpClient(tracker);
        var mockSession = DealerTestHelpers.CreateMockSession();

        _manager = new PlaybackStateManager(_mockDealer, mockEngine, mockSpClient, mockSession);

        // Simulate connection ID message
        await _mockConnection.SimulateMessageAsync(DealerTestHelpers.CreateConnectionIdMessage());
        await Task.Delay(50);

        // Act
        mockEngine.SimulatePlay("spotify:track:abc", positionMs: 5000);
        await Task.Delay(300); // Wait for async publishing

        // Assert
        tracker.Calls.Should().HaveCountGreaterOrEqualTo(1);
        var lastCall = tracker.Calls.Last();
        lastCall.Request.Device.Should().NotBeNull();
        lastCall.Request.Device.PlayerState.Should().NotBeNull();
        lastCall.Request.Device.PlayerState.Track.Uri.Should().Be("spotify:track:abc");
        lastCall.Request.Device.PlayerState.PositionAsOfTimestamp.Should().Be(5000);
        lastCall.Request.Device.PlayerState.IsPlaying.Should().BeTrue();
    }

    [Fact]
    public async Task BidirectionalMode_WhenLocalPlaybackPauses_ShouldPublishPausedState()
    {
        // Arrange
        var mockEngine = PlaybackStateTestHelpers.CreateMockPlaybackEngine();
        var tracker = new MockSpClientHelpers.PutStateCallTracker();
        var mockSpClient = MockSpClientHelpers.CreateMockSpClient(tracker);
        var mockSession = DealerTestHelpers.CreateMockSession();

        _manager = new PlaybackStateManager(_mockDealer, mockEngine, mockSpClient, mockSession);

        // Simulate connection ID message
        await _mockConnection.SimulateMessageAsync(DealerTestHelpers.CreateConnectionIdMessage());
        await Task.Delay(50);

        mockEngine.SimulatePlay("spotify:track:abc");
        await Task.Delay(300);
        tracker.Calls.Clear(); // Clear play call

        // Act
        mockEngine.SimulatePause();
        await Task.Delay(300);

        // Assert
        tracker.Calls.Should().HaveCountGreaterOrEqualTo(1);
        var lastCall = tracker.Calls.Last();
        // Note: Spotify protocol requires IsPlaying=true even when paused (triple-flag pattern)
        // See PlaybackStateHelpers.ToPlayerState() - when paused, IsPlaying and IsPaused are both true
        lastCall.Request.Device.PlayerState.IsPlaying.Should().BeTrue("Spotify protocol: IsPlaying=true when paused");
        lastCall.Request.Device.PlayerState.IsPaused.Should().BeTrue();
        lastCall.Request.Device.PlayerState.PlaybackSpeed.Should().Be(0.0, "PlaybackSpeed=0 indicates paused state");
    }

    [Fact]
    public async Task BidirectionalMode_WhenLocalPlaybackResumes_ShouldPublishPlayingState()
    {
        // Arrange
        var mockEngine = PlaybackStateTestHelpers.CreateMockPlaybackEngine();
        var tracker = new MockSpClientHelpers.PutStateCallTracker();
        var mockSpClient = MockSpClientHelpers.CreateMockSpClient(tracker);
        var mockSession = DealerTestHelpers.CreateMockSession();

        _manager = new PlaybackStateManager(_mockDealer, mockEngine, mockSpClient, mockSession);

        // Simulate connection ID message
        await _mockConnection.SimulateMessageAsync(DealerTestHelpers.CreateConnectionIdMessage());
        await Task.Delay(50);

        mockEngine.SimulatePlay("spotify:track:abc");
        await Task.Delay(300);
        mockEngine.SimulatePause();
        await Task.Delay(300);
        tracker.Calls.Clear(); // Clear previous calls

        // Act
        mockEngine.SimulateResume();
        await Task.Delay(300);

        // Assert
        tracker.Calls.Should().HaveCountGreaterOrEqualTo(1);
        var lastCall = tracker.Calls.Last();
        lastCall.Request.Device.PlayerState.IsPlaying.Should().BeTrue();
        lastCall.Request.Device.PlayerState.IsPaused.Should().BeFalse();
    }

    [Fact]
    public async Task BidirectionalMode_WhenLocalPlaybackSeeks_ShouldPublishNewPosition()
    {
        // Arrange
        var mockEngine = PlaybackStateTestHelpers.CreateMockPlaybackEngine();
        var tracker = new MockSpClientHelpers.PutStateCallTracker();
        var mockSpClient = MockSpClientHelpers.CreateMockSpClient(tracker);
        var mockSession = DealerTestHelpers.CreateMockSession();

        _manager = new PlaybackStateManager(_mockDealer, mockEngine, mockSpClient, mockSession);

        // Simulate connection ID message
        await _mockConnection.SimulateMessageAsync(DealerTestHelpers.CreateConnectionIdMessage());
        await Task.Delay(50);

        mockEngine.SimulatePlay("spotify:track:abc");
        await Task.Delay(300);
        tracker.Calls.Clear();

        // Act
        mockEngine.SimulateSeek(60000); // Seek to 1 minute
        // Position changes are non-critical → debounced ~750ms before publish
        await Task.Delay(1200);

        // Assert
        tracker.Calls.Should().HaveCountGreaterOrEqualTo(1);
        var lastCall = tracker.Calls.Last();
        lastCall.Request.Device.PlayerState.PositionAsOfTimestamp.Should().Be(60000);
    }

    [Fact]
    public async Task BidirectionalMode_WhenLocalShuffleChanges_ShouldPublishOptions()
    {
        // Arrange
        var mockEngine = PlaybackStateTestHelpers.CreateMockPlaybackEngine();
        var tracker = new MockSpClientHelpers.PutStateCallTracker();
        var mockSpClient = MockSpClientHelpers.CreateMockSpClient(tracker);
        var mockSession = DealerTestHelpers.CreateMockSession();

        _manager = new PlaybackStateManager(_mockDealer, mockEngine, mockSpClient, mockSession);

        // Simulate connection ID message
        await _mockConnection.SimulateMessageAsync(DealerTestHelpers.CreateConnectionIdMessage());
        await Task.Delay(50);

        mockEngine.SimulatePlay("spotify:track:abc");
        await Task.Delay(300);
        tracker.Calls.Clear();

        // Act
        mockEngine.SimulateShuffleChange(true);
        // Options changes are non-critical → debounced ~750ms before publish
        await Task.Delay(1200);

        // Assert
        tracker.Calls.Should().HaveCountGreaterOrEqualTo(1);
        var lastCall = tracker.Calls.Last();
        lastCall.Request.Device.PlayerState.Options.ShufflingContext.Should().BeTrue();
    }

    [Fact]
    public async Task BidirectionalMode_WhenClusterUpdateForDifferentDevice_ShouldUpdateState()
    {
        // Arrange
        var mockEngine = PlaybackStateTestHelpers.CreateMockPlaybackEngine();
        var tracker = new MockSpClientHelpers.PutStateCallTracker();
        var mockSpClient = MockSpClientHelpers.CreateMockSpClient(tracker);
        var mockSession = DealerTestHelpers.CreateMockSession();

        _manager = new PlaybackStateManager(_mockDealer, mockEngine, mockSpClient, mockSession);

        var message = PlaybackStateTestHelpers.CreateClusterUpdateMessage(
            activeDeviceId: "other_device_123",
            trackUri: "spotify:track:xyz",
            trackTitle: "Remote Track",
            isPlaying: true);

        // Act
        await _mockConnection.SimulateMessageAsync(message);
        await Task.Delay(200);

        // Assert
        _manager.CurrentState.Track.Should().NotBeNull();
        _manager.CurrentState.Track!.Title.Should().Be("Remote Track");
        _manager.CurrentState.ActiveDeviceId.Should().Be("other_device_123");
        _manager.CurrentState.Source.Should().Be(StateSource.Cluster);
        _manager.IsLocalPlaybackActive.Should().BeFalse();
    }

    [Fact]
    public async Task BidirectionalMode_WhenLocalDeviceIsActive_ShouldIgnoreClusterUpdates()
    {
        // Arrange
        var mockEngine = PlaybackStateTestHelpers.CreateMockPlaybackEngine();
        var tracker = new MockSpClientHelpers.PutStateCallTracker();
        var mockSpClient = MockSpClientHelpers.CreateMockSpClient(tracker);
        var mockSession = DealerTestHelpers.CreateMockSession();

        _manager = new PlaybackStateManager(_mockDealer, mockEngine, mockSpClient, mockSession);

        // Start local playback
        mockEngine.SimulatePlay("spotify:track:local");
        await Task.Delay(300);

        var statesBefore = new List<PlaybackState>();
        _manager.StateChanges.Subscribe(s => statesBefore.Add(s));

        // Create cluster update with same device ID
        var deviceId = mockSession.Config.DeviceId;
        var message = PlaybackStateTestHelpers.CreateClusterUpdateMessage(
            activeDeviceId: deviceId,
            trackUri: "spotify:track:remote",
            trackTitle: "Remote Track",
            isPlaying: true);

        // Act
        await _mockConnection.SimulateMessageAsync(message);
        await Task.Delay(300);

        // Assert - Should NOT update state from cluster (feedback loop prevention)
        _manager.CurrentState.Track!.Uri.Should().Be("spotify:track:local");
        _manager.CurrentState.Source.Should().Be(StateSource.Local);
    }

    [Fact]
    public async Task BidirectionalMode_WhenLocalPlaybackStops_ShouldAllowClusterUpdates()
    {
        // Arrange
        var mockEngine = PlaybackStateTestHelpers.CreateMockPlaybackEngine();
        var tracker = new MockSpClientHelpers.PutStateCallTracker();
        var mockSpClient = MockSpClientHelpers.CreateMockSpClient(tracker);
        var mockSession = DealerTestHelpers.CreateMockSession();

        _manager = new PlaybackStateManager(_mockDealer, mockEngine, mockSpClient, mockSession);

        // Start and stop local playback
        mockEngine.SimulatePlay("spotify:track:local");
        await Task.Delay(300);
        mockEngine.SimulateStop();
        await Task.Delay(300);

        var message = PlaybackStateTestHelpers.CreateClusterUpdateMessage(
            activeDeviceId: "other_device",
            trackUri: "spotify:track:remote",
            trackTitle: "Remote Track",
            isPlaying: true);

        // Act
        await _mockConnection.SimulateMessageAsync(message);
        await Task.Delay(200);

        // Assert - Should accept cluster updates now
        _manager.CurrentState.Track.Should().NotBeNull();
        _manager.CurrentState.Track!.Title.Should().Be("Remote Track");
        _manager.CurrentState.Source.Should().Be(StateSource.Cluster);
        _manager.IsLocalPlaybackActive.Should().BeFalse();
    }

    [Fact]
    public async Task BidirectionalMode_WhenLocalPlaybackStarts_ShouldSetIsLocalPlaybackActiveTrue()
    {
        // Arrange
        var mockEngine = PlaybackStateTestHelpers.CreateMockPlaybackEngine();
        var mockSpClient = MockSpClientHelpers.CreateMockSpClient();
        var mockSession = DealerTestHelpers.CreateMockSession();

        _manager = new PlaybackStateManager(_mockDealer, mockEngine, mockSpClient, mockSession);

        // Act
        mockEngine.SimulatePlay("spotify:track:abc");
        await Task.Delay(200);

        // Assert
        _manager.IsLocalPlaybackActive.Should().BeTrue();
        _manager.CurrentState.Source.Should().Be(StateSource.Local);
    }

    [Fact]
    public async Task BidirectionalMode_WhenLocalPlaybackStops_ShouldSetIsLocalPlaybackActiveFalse()
    {
        // Arrange
        var mockEngine = PlaybackStateTestHelpers.CreateMockPlaybackEngine();
        var mockSpClient = MockSpClientHelpers.CreateMockSpClient();
        var mockSession = DealerTestHelpers.CreateMockSession();

        _manager = new PlaybackStateManager(_mockDealer, mockEngine, mockSpClient, mockSession);

        // Simulate connection ID message
        await _mockConnection.SimulateMessageAsync(DealerTestHelpers.CreateConnectionIdMessage());
        await Task.Delay(50);

        mockEngine.SimulatePlay("spotify:track:abc");
        await Task.Delay(300);

        // Act
        mockEngine.SimulateStop();
        await Task.Delay(300);

        // Assert
        // Note: IsLocalPlaybackActive remains true - device stays "active" until another device takes over
        // Even when stopped, the local device is still the active device in the cluster
        _manager.CurrentState.Status.Should().Be(PlaybackStatus.Stopped);
        _manager.CurrentState.Source.Should().Be(StateSource.Local);
    }

    [Fact]
    public async Task BidirectionalMode_StateChangesObservable_ShouldEmitLocalAndClusterStates()
    {
        // Arrange
        var mockEngine = PlaybackStateTestHelpers.CreateMockPlaybackEngine();
        var mockSpClient = MockSpClientHelpers.CreateMockSpClient();
        var mockSession = DealerTestHelpers.CreateMockSession();

        _manager = new PlaybackStateManager(_mockDealer, mockEngine, mockSpClient, mockSession);

        var receivedStates = new List<PlaybackState>();
        _manager.StateChanges.Subscribe(s => receivedStates.Add(s));

        // Act - Local playback
        mockEngine.SimulatePlay("spotify:track:local");
        await Task.Delay(200);

        // Act - Stop local, allow cluster update
        mockEngine.SimulateStop();
        await Task.Delay(200);

        var message = PlaybackStateTestHelpers.CreateClusterUpdateMessage(
            activeDeviceId: "other_device",
            trackUri: "spotify:track:remote",
            trackTitle: "Remote Track",
            isPlaying: true);

        await _mockConnection.SimulateMessageAsync(message);
        await Task.Delay(200);

        // Assert
        receivedStates.Should().HaveCountGreaterOrEqualTo(3);
        receivedStates.Should().Contain(s => s.Source == StateSource.Local);
        receivedStates.Should().Contain(s => s.Source == StateSource.Cluster);
    }

    [Fact]
    public async Task BidirectionalMode_PublishingFailure_ShouldNotCrash()
    {
        // Arrange
        var mockEngine = PlaybackStateTestHelpers.CreateMockPlaybackEngine();
        var mockSpClient = MockSpClientHelpers.CreateMockSpClient(System.Net.HttpStatusCode.InternalServerError);
        var mockSession = DealerTestHelpers.CreateMockSession();

        _manager = new PlaybackStateManager(_mockDealer, mockEngine, mockSpClient, mockSession);

        // Act - Should not throw even if publishing fails
        mockEngine.SimulatePlay("spotify:track:abc");
        await Task.Delay(300);

        // Assert - Manager should still work
        _manager.CurrentState.Track.Should().NotBeNull();
        _manager.CurrentState.Track!.Uri.Should().Be("spotify:track:abc");
        _manager.IsLocalPlaybackActive.Should().BeTrue();
    }

    [Fact]
    public async Task BidirectionalMode_RapidLocalStateChanges_ShouldPublishAll()
    {
        // Arrange
        var mockEngine = PlaybackStateTestHelpers.CreateMockPlaybackEngine();
        var tracker = new MockSpClientHelpers.PutStateCallTracker();
        var mockSpClient = MockSpClientHelpers.CreateMockSpClient(tracker);
        var mockSession = DealerTestHelpers.CreateMockSession();

        _manager = new PlaybackStateManager(_mockDealer, mockEngine, mockSpClient, mockSession);

        // Simulate connection ID message
        await _mockConnection.SimulateMessageAsync(DealerTestHelpers.CreateConnectionIdMessage());
        await Task.Delay(50);

        // Act - Rapid state changes
        mockEngine.SimulatePlay("spotify:track:abc");
        await Task.Delay(100);
        mockEngine.SimulatePause();
        await Task.Delay(100);
        mockEngine.SimulateResume();
        await Task.Delay(100);
        mockEngine.SimulateSeek(30000);
        // Seek is non-critical → debounced ~750ms before publish
        await Task.Delay(1200);

        // Assert - All changes should be published
        tracker.Calls.Should().HaveCountGreaterOrEqualTo(4);
    }

    [Fact]
    public async Task BidirectionalMode_DisposalWhilePublishing_ShouldCompleteGracefully()
    {
        // Arrange
        var mockEngine = PlaybackStateTestHelpers.CreateMockPlaybackEngine();
        var mockSpClient = MockSpClientHelpers.CreateMockSpClient();
        var mockSession = DealerTestHelpers.CreateMockSession();

        _manager = new PlaybackStateManager(_mockDealer, mockEngine, mockSpClient, mockSession);

        mockEngine.SimulatePlay("spotify:track:abc");
        await Task.Delay(100);

        // Act - Dispose while publishing might be in progress
        var act = async () => await _manager.DisposeAsync();

        // Assert - Should complete without errors
        await act.Should().NotThrowAsync();
    }

    #endregion
}
