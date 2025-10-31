using FluentAssertions;
using Wavee.Connect;
using Wavee.Connect.Commands;
using Wavee.Connect.Playback;
using Wavee.Connect.Playback.Abstractions;
using Wavee.Connect.Playback.Decoders;
using Wavee.Connect.Playback.Processors;
using Wavee.Connect.Playback.Sinks;
using Wavee.Connect.Playback.Sources;
using Wavee.Tests.Helpers;
using Xunit;

namespace Wavee.Tests.Connect.Playback;

/// <summary>
/// Tests for AudioPipeline - validates playback orchestration, command handling, and state management.
///
/// WHY: AudioPipeline is the core playback orchestrator. Bugs here will cause:
/// - Commands not executing (Spotify controls don't work)
/// - State not publishing (bidirectional mode broken)
/// - Playback loop failures (tracks don't play)
/// - Threading issues (race conditions, deadlocks)
/// - Memory leaks (resources not cleaned up)
/// </summary>
public sealed class AudioPipelineTests : IAsyncDisposable
{
    private readonly TrackSourceRegistry _sourceRegistry;
    private readonly AudioDecoderRegistry _decoderRegistry;
    private readonly StubAudioSink _audioSink;
    private readonly AudioProcessingChain _processingChain;
    private AudioPipeline? _pipeline;

    public AudioPipelineTests()
    {
        // Setup test dependencies
        _sourceRegistry = new TrackSourceRegistry();
        _sourceRegistry.Register(new StubTrackSource());

        _decoderRegistry = new AudioDecoderRegistry();
        _decoderRegistry.Register(new StubDecoder());

        _audioSink = new StubAudioSink();
        _processingChain = new AudioProcessingChain();
    }

    public async ValueTask DisposeAsync()
    {
        if (_pipeline != null)
            await _pipeline.DisposeAsync();

        await _audioSink.DisposeAsync();
    }

    // ================================================================
    // HELPER METHODS
    // ================================================================

    /// <summary>
    /// Creates a valid PlayCommand for testing with all required properties.
    /// </summary>
    private static PlayCommand CreateTestPlayCommand(
        string? trackUri = null,
        string? contextUri = null,
        long? positionMs = null,
        PlayerOptions? options = null)
    {
        return new PlayCommand
        {
            // Required base properties from ConnectCommand
            Endpoint = "play",
            MessageIdent = "hm://connect-state/v1/play",
            MessageId = 1,
            SenderDeviceId = "test-device",
            Key = "1/test-device",

            // PlayCommand specific properties
            TrackUri = trackUri,
            ContextUri = contextUri,
            PositionMs = positionMs,
            Options = options
        };
    }

    // ================================================================
    // SECTION 1: CONSTRUCTION & INITIALIZATION TESTS
    // ================================================================

    [Fact]
    public void Constructor_WithValidDependencies_ShouldInitialize()
    {
        // WHY: Verify AudioPipeline initializes correctly without command handler

        // Act
        _pipeline = new AudioPipeline(
            _sourceRegistry,
            _decoderRegistry,
            _audioSink,
            _processingChain);

        // Assert
        _pipeline.Should().NotBeNull();
        _pipeline.StateChanges.Should().NotBeNull("StateChanges observable should be initialized");
        _pipeline.CurrentState.Should().NotBeNull("CurrentState should be initialized");
        _pipeline.CurrentState.IsPlaying.Should().BeFalse("Should start in stopped state");
        _pipeline.CurrentState.IsPaused.Should().BeFalse("Should start in stopped state");
    }

    [Fact]
    public void Constructor_WithCommandHandler_ShouldInitialize()
    {
        // WHY: Verify AudioPipeline initializes with command handler integration

        // Arrange
        var (commandHandler, _) = ConnectCommandTestHelpers.CreateTestCommandHandler();

        // Act
        _pipeline = new AudioPipeline(
            _sourceRegistry,
            _decoderRegistry,
            _audioSink,
            _processingChain,
            commandHandler);

        // Assert
        _pipeline.Should().NotBeNull();
        _pipeline.StateChanges.Should().NotBeNull();
        _pipeline.CurrentState.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_WithNullSourceRegistry_ShouldThrow()
    {
        // WHY: Null dependencies should fail fast

        // Act
        var act = () => new AudioPipeline(
            null!,
            _decoderRegistry,
            _audioSink,
            _processingChain);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("sourceRegistry");
    }

    [Fact]
    public void Constructor_WithNullDecoderRegistry_ShouldThrow()
    {
        // WHY: Null dependencies should fail fast

        // Act
        var act = () => new AudioPipeline(
            _sourceRegistry,
            null!,
            _audioSink,
            _processingChain);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("decoderRegistry");
    }

    [Fact]
    public void Constructor_WithNullAudioSink_ShouldThrow()
    {
        // WHY: Null dependencies should fail fast

        // Act
        var act = () => new AudioPipeline(
            _sourceRegistry,
            _decoderRegistry,
            null!,
            _processingChain);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("audioSink");
    }

    [Fact]
    public void Constructor_WithNullProcessingChain_ShouldThrow()
    {
        // WHY: Null dependencies should fail fast

        // Act
        var act = () => new AudioPipeline(
            _sourceRegistry,
            _decoderRegistry,
            _audioSink,
            null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("processingChain");
    }

    // ================================================================
    // SECTION 2: COMMAND INTEGRATION TESTS
    // ================================================================

    [Fact]
    public async Task PlayAsync_WithValidCommand_ShouldStartPlayback()
    {
        // WHY: Verify AudioPipeline responds to play commands and updates state

        // Arrange
        _pipeline = new AudioPipeline(
            _sourceRegistry,
            _decoderRegistry,
            _audioSink,
            _processingChain);

        var stateUpdates = new List<LocalPlaybackState>();
        _pipeline.StateChanges.Subscribe(state => stateUpdates.Add(state));

        var playCommand = CreateTestPlayCommand(
            trackUri: "stub:test-track",
            positionMs: 0);

        // Act
        await _pipeline.PlayAsync(playCommand);
        await Task.Delay(200); // Wait for playback loop to start

        // Assert
        var currentState = _pipeline.CurrentState;
        currentState.TrackUri.Should().Be("stub:test-track");
        currentState.IsPlaying.Should().BeTrue();
        currentState.IsPaused.Should().BeFalse();

        stateUpdates.Should().Contain(s => s.IsPlaying,
            "StateChanges should emit playing state");
    }

    [Fact]
    public async Task PlayAsync_WithPosition_ShouldStartAtPosition()
    {
        // WHY: Verify seek-to-position works on play

        // Arrange
        _pipeline = new AudioPipeline(
            _sourceRegistry,
            _decoderRegistry,
            _audioSink,
            _processingChain);

        var playCommand = CreateTestPlayCommand(
            trackUri: "stub:test-track",
            positionMs: 5000);  // Start at 5 seconds

        // Act
        await _pipeline.PlayAsync(playCommand);
        await Task.Delay(200);

        // Assert
        var currentState = _pipeline.CurrentState;
        currentState.PositionMs.Should().BeGreaterOrEqualTo(5000,
            "Should start at requested position");
    }

    [Fact]
    public async Task PlayAsync_WithOptions_ShouldApplyOptions()
    {
        // WHY: Verify playback options are applied from play command

        // Arrange
        _pipeline = new AudioPipeline(
            _sourceRegistry,
            _decoderRegistry,
            _audioSink,
            _processingChain);

        var playCommand = CreateTestPlayCommand(
            trackUri: "stub:test-track",
            options: new PlayerOptions
            {
                ShufflingContext = true,
                RepeatingTrack = true,
                RepeatingContext = false
            });

        // Act
        await _pipeline.PlayAsync(playCommand);
        await Task.Delay(100);

        // Assert
        var currentState = _pipeline.CurrentState;
        currentState.Shuffling.Should().BeTrue();
        currentState.RepeatingTrack.Should().BeTrue();
        currentState.RepeatingContext.Should().BeFalse();
    }

    [Fact]
    public async Task PauseAsync_WhenPlaying_ShouldPausePlayback()
    {
        // WHY: Verify pause command works and updates state

        // Arrange
        _pipeline = new AudioPipeline(
            _sourceRegistry,
            _decoderRegistry,
            _audioSink,
            _processingChain);

        var playCommand = CreateTestPlayCommand(trackUri: "stub:test-track");
        await _pipeline.PlayAsync(playCommand);
        await Task.Delay(200);

        // Act
        await _pipeline.PauseAsync();

        // Assert
        var currentState = _pipeline.CurrentState;
        currentState.IsPaused.Should().BeTrue();
        currentState.IsPlaying.Should().BeFalse();
    }

    [Fact]
    public async Task ResumeAsync_WhenPaused_ShouldResumePlayback()
    {
        // WHY: Verify resume command works and updates state

        // Arrange
        _pipeline = new AudioPipeline(
            _sourceRegistry,
            _decoderRegistry,
            _audioSink,
            _processingChain);

        var playCommand = CreateTestPlayCommand(trackUri: "stub:test-track");
        await _pipeline.PlayAsync(playCommand);
        await Task.Delay(200);
        await _pipeline.PauseAsync();

        // Act
        await _pipeline.ResumeAsync();

        // Assert
        var currentState = _pipeline.CurrentState;
        currentState.IsPaused.Should().BeFalse();
        currentState.IsPlaying.Should().BeTrue();
    }

    [Fact]
    public async Task SeekAsync_WhenPlaying_ShouldSeekToPosition()
    {
        // WHY: Verify seek command restarts playback at new position

        // Arrange
        _pipeline = new AudioPipeline(
            _sourceRegistry,
            _decoderRegistry,
            _audioSink,
            _processingChain);

        var playCommand = CreateTestPlayCommand(trackUri: "stub:test-track");
        await _pipeline.PlayAsync(playCommand);
        await Task.Delay(200);

        // Act
        await _pipeline.SeekAsync(3000); // Seek to 3 seconds
        await Task.Delay(200);

        // Assert
        var currentState = _pipeline.CurrentState;
        currentState.PositionMs.Should().BeGreaterOrEqualTo(3000,
            "Should seek to requested position");
        currentState.IsPlaying.Should().BeTrue("Should resume playing after seek");
    }

    [Fact]
    public async Task SetShuffleAsync_ShouldUpdateShuffleState()
    {
        // WHY: Verify shuffle mode setter works

        // Arrange
        _pipeline = new AudioPipeline(
            _sourceRegistry,
            _decoderRegistry,
            _audioSink,
            _processingChain);

        // Act
        await _pipeline.SetShuffleAsync(true);

        // Assert
        _pipeline.CurrentState.Shuffling.Should().BeTrue();

        // Act - disable shuffle
        await _pipeline.SetShuffleAsync(false);

        // Assert
        _pipeline.CurrentState.Shuffling.Should().BeFalse();
    }

    [Fact]
    public async Task SetRepeatTrackAsync_ShouldUpdateRepeatState()
    {
        // WHY: Verify repeat track mode setter works

        // Arrange
        _pipeline = new AudioPipeline(
            _sourceRegistry,
            _decoderRegistry,
            _audioSink,
            _processingChain);

        // Act
        await _pipeline.SetRepeatTrackAsync(true);

        // Assert
        _pipeline.CurrentState.RepeatingTrack.Should().BeTrue();

        // Act - disable repeat
        await _pipeline.SetRepeatTrackAsync(false);

        // Assert
        _pipeline.CurrentState.RepeatingTrack.Should().BeFalse();
    }

    [Fact]
    public async Task SetRepeatContextAsync_ShouldUpdateRepeatState()
    {
        // WHY: Verify repeat context mode setter works

        // Arrange
        _pipeline = new AudioPipeline(
            _sourceRegistry,
            _decoderRegistry,
            _audioSink,
            _processingChain);

        // Act
        await _pipeline.SetRepeatContextAsync(true);

        // Assert
        _pipeline.CurrentState.RepeatingContext.Should().BeTrue();
    }

    // ================================================================
    // SECTION 3: STATE PUBLISHING TESTS
    // ================================================================

    [Fact]
    public async Task StateChanges_WhenPlayStarts_ShouldEmitPlayingState()
    {
        // WHY: Verify StateChanges observable emits when playback starts

        // Arrange
        _pipeline = new AudioPipeline(
            _sourceRegistry,
            _decoderRegistry,
            _audioSink,
            _processingChain);

        var stateUpdates = new List<LocalPlaybackState>();
        _pipeline.StateChanges.Subscribe(state => stateUpdates.Add(state));

        var playCommand = CreateTestPlayCommand(trackUri: "stub:test-track");

        // Act
        await _pipeline.PlayAsync(playCommand);
        await Task.Delay(300);

        // Assert
        stateUpdates.Should().Contain(s => s.IsPlaying && s.TrackUri == "stub:test-track",
            "Should emit playing state with track URI");
    }

    [Fact]
    public async Task StateChanges_DuringPlayback_ShouldEmitPositionUpdates()
    {
        // WHY: Verify StateChanges emits position updates during playback

        // Arrange
        _pipeline = new AudioPipeline(
            _sourceRegistry,
            _decoderRegistry,
            _audioSink,
            _processingChain);

        var stateUpdates = new List<LocalPlaybackState>();
        _pipeline.StateChanges.Subscribe(state => stateUpdates.Add(state));

        var playCommand = CreateTestPlayCommand(trackUri: "stub:test-track");

        // Act
        await _pipeline.PlayAsync(playCommand);
        await Task.Delay(1500); // Wait for multiple updates

        // Assert
        var playingStates = stateUpdates.Where(s => s.IsPlaying).ToList();
        playingStates.Should().HaveCountGreaterThan(1,
            "Should emit multiple position updates during playback");

        // Verify position progresses
        var positions = playingStates.Select(s => s.PositionMs).ToList();
        positions.Should().BeInAscendingOrder("Position should progress over time");
    }

    [Fact]
    public async Task CurrentState_ShouldReflectLatestState()
    {
        // WHY: Verify CurrentState property returns latest state synchronously

        // Arrange
        _pipeline = new AudioPipeline(
            _sourceRegistry,
            _decoderRegistry,
            _audioSink,
            _processingChain);

        var playCommand = CreateTestPlayCommand(trackUri: "stub:test-track");

        // Act
        var stateBefore = _pipeline.CurrentState;
        await _pipeline.PlayAsync(playCommand);
        await Task.Delay(200);
        var stateAfter = _pipeline.CurrentState;

        // Assert
        stateBefore.IsPlaying.Should().BeFalse();
        stateAfter.IsPlaying.Should().BeTrue();
        stateAfter.TrackUri.Should().Be("stub:test-track");
    }

    [Fact]
    public async Task StateChanges_OnPause_ShouldEmitPausedState()
    {
        // WHY: Verify state updates when pausing

        // Arrange
        _pipeline = new AudioPipeline(
            _sourceRegistry,
            _decoderRegistry,
            _audioSink,
            _processingChain);

        var stateUpdates = new List<LocalPlaybackState>();
        _pipeline.StateChanges.Subscribe(state => stateUpdates.Add(state));

        var playCommand = CreateTestPlayCommand(trackUri: "stub:test-track");
        await _pipeline.PlayAsync(playCommand);
        await Task.Delay(200);

        // Act
        await _pipeline.PauseAsync();

        // Assert
        stateUpdates.Should().Contain(s => s.IsPaused && !s.IsPlaying,
            "Should emit paused state");
    }

    [Fact]
    public async Task StateChanges_ShouldIncludeMetadata()
    {
        // WHY: Verify state includes track metadata

        // Arrange
        _pipeline = new AudioPipeline(
            _sourceRegistry,
            _decoderRegistry,
            _audioSink,
            _processingChain);

        var stateUpdates = new List<LocalPlaybackState>();
        _pipeline.StateChanges.Subscribe(state => stateUpdates.Add(state));

        var playCommand = CreateTestPlayCommand(trackUri: "stub:test-track");

        // Act
        await _pipeline.PlayAsync(playCommand);
        await Task.Delay(300);

        // Assert
        var playingState = stateUpdates.FirstOrDefault(s => s.IsPlaying);
        playingState.Should().NotBeNull();
        playingState!.DurationMs.Should().BeGreaterThan(0, "Should include track duration");
        playingState.TrackUid.Should().NotBeNullOrEmpty("Should include track UID");
    }

    [Fact]
    public async Task StateChanges_ShouldIncludeTimestamp()
    {
        // WHY: Verify state includes timestamp for sync purposes

        // Arrange
        _pipeline = new AudioPipeline(
            _sourceRegistry,
            _decoderRegistry,
            _audioSink,
            _processingChain);

        var stateUpdates = new List<LocalPlaybackState>();
        _pipeline.StateChanges.Subscribe(state => stateUpdates.Add(state));

        var playCommand = CreateTestPlayCommand(trackUri: "stub:test-track");

        // Act
        await _pipeline.PlayAsync(playCommand);
        await Task.Delay(200);

        // Assert
        var states = stateUpdates.Where(s => s.IsPlaying).ToList();
        states.Should().AllSatisfy(state =>
        {
            state.Timestamp.Should().BeGreaterThan(0, "Should include Unix timestamp");
        });
    }

    // ================================================================
    // SECTION 4: PLAYBACK LOOP TESTS
    // ================================================================

    [Fact]
    public async Task PlaybackLoop_ShouldCompleteSuccessfully()
    {
        // WHY: Verify playback loop runs to completion without errors

        // Arrange
        _pipeline = new AudioPipeline(
            _sourceRegistry,
            _decoderRegistry,
            _audioSink,
            _processingChain);

        var playCommand = CreateTestPlayCommand(trackUri: "stub:test-track");

        // Act
        await _pipeline.PlayAsync(playCommand);

        // StubDecoder generates 10 seconds of audio
        await Task.Delay(11000); // Wait for completion

        // Assert
        var finalState = _pipeline.CurrentState;
        finalState.IsPlaying.Should().BeFalse("Playback should complete");
    }

    [Fact]
    public async Task PlaybackLoop_WithRepeatTrack_ShouldLoop()
    {
        // WHY: Verify repeat track mode loops the track

        // Arrange
        _pipeline = new AudioPipeline(
            _sourceRegistry,
            _decoderRegistry,
            _audioSink,
            _processingChain);

        var playCommand = CreateTestPlayCommand(
            trackUri: "stub:test-track",
            options: new PlayerOptions { RepeatingTrack = true });

        // Act
        await _pipeline.PlayAsync(playCommand);
        await Task.Delay(12000); // Wait past first iteration

        // Assert
        var currentState = _pipeline.CurrentState;
        currentState.IsPlaying.Should().BeTrue("Should still be playing (looping)");
    }

    [Fact]
    public async Task PlaybackLoop_WhenCancelled_ShouldStopCleanly()
    {
        // WHY: Verify playback can be cancelled cleanly

        // Arrange
        _pipeline = new AudioPipeline(
            _sourceRegistry,
            _decoderRegistry,
            _audioSink,
            _processingChain);

        var playCommand = CreateTestPlayCommand(trackUri: "stub:test-track");

        // Act
        await _pipeline.PlayAsync(playCommand);
        await Task.Delay(200);

        // Cancel by starting new playback (stops current)
        var playCommand2 = CreateTestPlayCommand(trackUri: "stub:test-track-2");
        await _pipeline.PlayAsync(playCommand2);
        await Task.Delay(200);

        // Assert
        var currentState = _pipeline.CurrentState;
        currentState.TrackUri.Should().Be("stub:test-track-2");
        currentState.IsPlaying.Should().BeTrue();
    }

    // ================================================================
    // SECTION 5: THREAD SAFETY TESTS
    // ================================================================

    [Fact]
    public async Task ConcurrentCommands_ShouldExecuteSequentially()
    {
        // WHY: Verify command lock prevents race conditions

        // Arrange
        _pipeline = new AudioPipeline(
            _sourceRegistry,
            _decoderRegistry,
            _audioSink,
            _processingChain);

        // Act - Execute commands concurrently
        var tasks = new List<Task>
        {
            _pipeline.PlayAsync(CreateTestPlayCommand(trackUri: "stub:track1")),
            _pipeline.PauseAsync(),
            _pipeline.ResumeAsync(),
            _pipeline.SeekAsync(5000),
            _pipeline.SetShuffleAsync(true)
        };

        // Assert - All should complete without exceptions
        await Task.WhenAll(tasks);

        // Verify final state is consistent
        var finalState = _pipeline.CurrentState;
        finalState.Should().NotBeNull();
    }

    [Fact]
    public async Task ConcurrentPlayCommands_ShouldNotCauseCrash()
    {
        // WHY: Verify multiple play commands don't cause threading issues

        // Arrange
        _pipeline = new AudioPipeline(
            _sourceRegistry,
            _decoderRegistry,
            _audioSink,
            _processingChain);

        // Act - Fire multiple play commands rapidly
        var tasks = Enumerable.Range(1, 5)
            .Select(i => _pipeline.PlayAsync(CreateTestPlayCommand(trackUri: $"stub:track{i}")))
            .ToList();

        // Assert - Should complete without crash
        await Task.WhenAll(tasks);

        var finalState = _pipeline.CurrentState;
        finalState.TrackUri.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task StateReading_WhilePlaybackActive_ShouldNotBlock()
    {
        // WHY: Verify state reading doesn't block or cause deadlocks

        // Arrange
        _pipeline = new AudioPipeline(
            _sourceRegistry,
            _decoderRegistry,
            _audioSink,
            _processingChain);

        await _pipeline.PlayAsync(CreateTestPlayCommand(trackUri: "stub:test-track"));
        await Task.Delay(200);

        // Act - Read state many times concurrently
        var readTasks = Enumerable.Range(1, 100)
            .Select(_ => Task.Run(() => _pipeline.CurrentState))
            .ToList();

        // Assert - Should complete quickly without blocking
        var allReadsTask = Task.WhenAll(readTasks);
        var completedTask = await Task.WhenAny(allReadsTask, Task.Delay(1000));
        completedTask.Should().Be(allReadsTask, "State reads should not block");
    }

    // ================================================================
    // SECTION 6: DISPOSAL TESTS
    // ================================================================

    [Fact]
    public async Task DisposeAsync_ShouldStopPlayback()
    {
        // WHY: Verify disposal stops active playback

        // Arrange
        _pipeline = new AudioPipeline(
            _sourceRegistry,
            _decoderRegistry,
            _audioSink,
            _processingChain);

        await _pipeline.PlayAsync(CreateTestPlayCommand(trackUri: "stub:test-track"));
        await Task.Delay(200);

        // Act
        await _pipeline.DisposeAsync();

        // Assert - Should complete without hanging
        // (Test passes if DisposeAsync completes)
    }

    [Fact]
    public async Task DisposeAsync_ShouldCompleteStateObservable()
    {
        // WHY: Verify disposal completes observables to prevent memory leaks

        // Arrange
        _pipeline = new AudioPipeline(
            _sourceRegistry,
            _decoderRegistry,
            _audioSink,
            _processingChain);

        var completed = false;
        _pipeline.StateChanges.Subscribe(
            _ => { },
            () => completed = true);

        // Can start playback first to make it more realistic
        await _pipeline.PlayAsync(CreateTestPlayCommand(trackUri: "stub:test-track"));
        await Task.Delay(100);

        // Act
        await _pipeline.DisposeAsync();

        // Assert
        completed.Should().BeTrue("StateChanges observable should complete on disposal");
    }

    [Fact]
    public async Task DisposeAsync_Multiple_ShouldBeIdempotent()
    {
        // WHY: Verify multiple dispose calls don't cause errors

        // Arrange
        _pipeline = new AudioPipeline(
            _sourceRegistry,
            _decoderRegistry,
            _audioSink,
            _processingChain);

        // Act & Assert - Should not throw
        await _pipeline.DisposeAsync();
        await _pipeline.DisposeAsync();
        await _pipeline.DisposeAsync();
    }
}
