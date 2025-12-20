# AudioPipeline Usage Guide

Complete guide to using the AudioPipeline orchestrator for Spotify Connect playback.

## Quick Start

```csharp
using Wavee.Connect;
using Wavee.Connect.Commands;
using Wavee.Connect.Playback;
using Wavee.Connect.Playback.Sources;
using Wavee.Connect.Playback.Decoders;
using Wavee.Connect.Playback.Processors;
using Wavee.Connect.Playback.Sinks;

// 1. Create registries
var sourceRegistry = new TrackSourceRegistry();
var decoderRegistry = new AudioDecoderRegistry();

// 2. Register implementations (stubs for now)
sourceRegistry.Register(new StubTrackSource());
decoderRegistry.Register(new StubDecoder());

// 3. Create sink and processing chain
var audioSink = new StubAudioSink();
var processingChain = new AudioProcessingChain();

// 4. Create AudioPipeline with command handler integration
var pipeline = new AudioPipeline(
    sourceRegistry,
    decoderRegistry,
    audioSink,
    processingChain,
    commandHandler);  // Auto-subscribes to Spotify commands

// 5. Subscribe to state changes
pipeline.StateChanges.Subscribe(state =>
{
    Console.WriteLine($"Now playing: {state.TrackUri}");
    Console.WriteLine($"Position: {state.PositionMs}ms / {state.DurationMs}ms");
    Console.WriteLine($"Status: Playing={state.IsPlaying}, Paused={state.IsPaused}");
});

// 6. Commands from Spotify app automatically trigger playback!
// User presses play in Spotify app → command flows through pipeline
```

## Integration with PlaybackStateManager (Bidirectional Mode)

```csharp
// Enable full bidirectional sync with Spotify
var stateManager = new PlaybackStateManager(
    dealerClient,
    pipeline,        // IPlaybackEngine - publishes local state
    spClient,
    session);

// Now:
// - Spotify app → commands → AudioPipeline → playback
// - AudioPipeline → state changes → PlaybackStateManager → Spotify API
// - Your device appears active in Spotify app!
```

## Manual Playback Control

```csharp
// Create pipeline without command handler (manual control only)
var pipeline = new AudioPipeline(
    sourceRegistry,
    decoderRegistry,
    audioSink,
    processingChain);

// Play a track
var playCommand = new PlayCommand
{
    TrackUri = "stub:test-track",
    PositionMs = 0,
    Options = new PlayerOptions
    {
        ShufflingContext = false,
        RepeatingTrack = false,
        RepeatingContext = false
    }
};

await pipeline.PlayAsync(playCommand);

// Pause
await pipeline.PauseAsync();

// Resume
await pipeline.ResumeAsync();

// Seek to 30 seconds
await pipeline.SeekAsync(30000);

// Skip to next (requires queue implementation)
await pipeline.SkipNextAsync();

// Set shuffle
await pipeline.SetShuffleAsync(true);

// Set repeat track
await pipeline.SetRepeatTrackAsync(true);
```

## State Monitoring

```csharp
// Subscribe to all state changes
pipeline.StateChanges.Subscribe(state =>
{
    Console.WriteLine($"State Update:");
    Console.WriteLine($"  Track: {state.TrackUri}");
    Console.WriteLine($"  Position: {state.PositionMs}ms / {state.DurationMs}ms");
    Console.WriteLine($"  Playing: {state.IsPlaying}");
    Console.WriteLine($"  Paused: {state.IsPaused}");
    Console.WriteLine($"  Buffering: {state.IsBuffering}");
    Console.WriteLine($"  Shuffle: {state.Shuffling}");
    Console.WriteLine($"  Repeat Track: {state.RepeatingTrack}");
    Console.WriteLine($"  Repeat Context: {state.RepeatingContext}");
});

// Get current state (synchronous)
var currentState = pipeline.CurrentState;
Console.WriteLine($"Current position: {currentState.PositionMs}ms");
```

## Adding Audio Processing

```csharp
using Wavee.Connect.Playback.Processors;

var processingChain = new AudioProcessingChain();

// 1. Add normalization (ReplayGain)
var normalization = new NormalizationProcessor
{
    Mode = NormalizationMode.Album,
    PreAmpDb = -6.0f,
    PreventClipping = true
};
processingChain.AddProcessor(normalization);

// 2. Add 10-band graphic EQ
var equalizer = new EqualizerProcessor();
equalizer.CreateGraphicEq10Band();
equalizer.Bands[0].GainDb = +3.0;  // Boost bass
equalizer.Bands[9].GainDb = +2.0;  // Boost treble
processingChain.AddProcessor(equalizer);

// 3. Add volume control
var volume = new VolumeProcessor { VolumeDb = -3.0f };
processingChain.AddProcessor(volume);

// 4. Add crossfade (for future queue support)
var crossfade = new CrossfadeProcessor
{
    CrossfadeDurationMs = 3000,
    Curve = CrossfadeCurve.EqualPower
};
processingChain.AddProcessor(crossfade);

// Create pipeline with processors
var pipeline = new AudioPipeline(
    sourceRegistry,
    decoderRegistry,
    audioSink,
    processingChain,
    commandHandler);
```

## Implementing Real Decoders

```csharp
// Example: Add WAV decoder
public class WaveDecoder : IAudioDecoder
{
    public string FormatName => "WAV";

    public bool CanDecode(Stream stream)
    {
        var header = new byte[4];
        stream.Read(header, 0, 4);
        stream.Position = 0;
        return header[0] == 'R' && header[1] == 'I' &&
               header[2] == 'F' && header[3] == 'F';
    }

    public Task<AudioFormat> GetFormatAsync(Stream stream, CancellationToken ct)
    {
        // Read WAV header, return AudioFormat
        // ...
    }

    public async IAsyncEnumerable<AudioBuffer> DecodeAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Decode PCM data and yield AudioBuffers
        // ...
    }
}

// Register it
decoderRegistry.Register(new WaveDecoder());
decoderRegistry.Register(new StubDecoder());  // Fallback
```

## Implementing Real Track Sources

```csharp
// Example: Add local file source
public class LocalFileTrackSource : ITrackSource
{
    public string SourceName => "LocalFile";

    public bool CanHandle(string uri)
    {
        return uri.StartsWith("file://") || File.Exists(uri);
    }

    public async Task<ITrackStream> LoadAsync(string uri, CancellationToken ct)
    {
        var filePath = uri.StartsWith("file://")
            ? new Uri(uri).LocalPath
            : uri;

        var stream = File.OpenRead(filePath);
        var metadata = new TrackMetadata
        {
            Uri = uri,
            Title = Path.GetFileNameWithoutExtension(filePath),
            // Read ID3 tags, etc.
        };

        return new LocalFileTrackStream(stream, metadata);
    }
}

// Register it
sourceRegistry.Register(new LocalFileTrackSource());
sourceRegistry.Register(new StubTrackSource());  // Fallback
```

## Implementing Real Audio Sink (WASAPI)

```csharp
// Example: WASAPI audio sink for Windows
public class WasapiAudioSink : IAudioSink
{
    public string SinkName => "WASAPI";

    public Task InitializeAsync(AudioFormat format, int bufferSizeMs, CancellationToken ct)
    {
        // Initialize Windows Audio Session API
        // Configure format, buffer size, etc.
        // ...
    }

    public Task WriteAsync(ReadOnlyMemory<byte> audioData, CancellationToken ct)
    {
        // Write PCM data to WASAPI buffer
        // ...
    }

    public Task<AudioSinkStatus> GetStatusAsync()
    {
        // Return current playback position and buffer status
        // ...
    }

    // ... other methods
}

// Use it
var audioSink = new WasapiAudioSink();
var pipeline = new AudioPipeline(sourceRegistry, decoderRegistry, audioSink, processingChain);
```

## Complete Integration Example

```csharp
using Wavee.Connect;
using Wavee.Connect.Commands;
using Wavee.Connect.Playback;
using Wavee.Connect.Playback.Sources;
using Wavee.Connect.Playback.Decoders;
using Wavee.Connect.Playback.Processors;
using Wavee.Connect.Playback.Sinks;
using Microsoft.Extensions.Logging;

// Assume session, dealerClient, spClient are already created

// 1. Create command handler
var commandHandler = new ConnectCommandHandler(dealerClient, logger);

// 2. Setup track sources
var sourceRegistry = new TrackSourceRegistry();
sourceRegistry.Register(new LocalFileTrackSource());
sourceRegistry.Register(new SpotifyTrackSource(session, spClient));
sourceRegistry.Register(new StubTrackSource());  // Fallback

// 3. Setup decoders
var decoderRegistry = new AudioDecoderRegistry();
decoderRegistry.Register(new WaveDecoder());
decoderRegistry.Register(new VorbisDecoder());  // Your custom decoder
decoderRegistry.Register(new StubDecoder());    // Fallback

// 4. Setup audio processing
var processingChain = new AudioProcessingChain();

var normalization = new NormalizationProcessor
{
    Mode = NormalizationMode.Album,
    PreAmpDb = -6.0f
};
processingChain.AddProcessor(normalization);

var equalizer = new EqualizerProcessor();
equalizer.CreateGraphicEq10Band();
processingChain.AddProcessor(equalizer);

var volume = new VolumeProcessor { VolumeDb = 0.0f };
processingChain.AddProcessor(volume);

// 5. Create audio sink
var audioSink = new WasapiAudioSink();  // Or StubAudioSink for testing

// 6. Create AudioPipeline with command integration
var pipeline = new AudioPipeline(
    sourceRegistry,
    decoderRegistry,
    audioSink,
    processingChain,
    commandHandler,
    logger);

// 7. Enable bidirectional state sync
var stateManager = new PlaybackStateManager(
    dealerClient,
    pipeline,
    spClient,
    session,
    logger);

// 8. Subscribe to state changes for logging/UI updates
pipeline.StateChanges.Subscribe(state =>
{
    logger.LogInformation(
        "Playback state: {TrackUri} @ {PositionMs}ms (Playing: {IsPlaying})",
        state.TrackUri,
        state.PositionMs,
        state.IsPlaying);
});

// 9. Everything is now connected!
// - Spotify app controls → DealerClient → ConnectCommandHandler → AudioPipeline
// - AudioPipeline state → PlaybackStateManager → Spotify API
// - Your device is a fully functional Spotify Connect speaker!

Console.WriteLine("Spotify Connect playback ready!");
Console.WriteLine("Control playback from your Spotify app.");

// Keep running
await Task.Delay(Timeout.Infinite);
```

## Thread Safety

AudioPipeline is thread-safe for command execution:

- Commands are protected by `_commandLock` semaphore
- State updates use `_stateLock` for synchronization
- Multiple commands can be queued, executed sequentially
- Safe to call from different threads (e.g., UI thread, command handler thread)

```csharp
// Safe to call from any thread
await pipeline.PlayAsync(playCommand);
await pipeline.PauseAsync();
await pipeline.SeekAsync(60000);
```

## Error Handling

```csharp
// Subscribe to state changes and check for errors
pipeline.StateChanges.Subscribe(state =>
{
    if (!state.IsPlaying && !state.IsPaused && state.TrackUri != null)
    {
        // Playback may have failed
        logger.LogWarning("Playback stopped unexpectedly for {TrackUri}", state.TrackUri);
    }
});

// Commands return Task - can catch exceptions
try
{
    await pipeline.PlayAsync(playCommand);
}
catch (Exception ex)
{
    logger.LogError(ex, "Failed to start playback");
}
```

## Performance Tips

1. **Use appropriate buffer sizes**: Default 100ms is good for most cases
2. **Limit processors**: Each enabled processor adds latency
3. **Disable unused processors**: Set `IsEnabled = false` to bypass
4. **Monitor state updates**: Published every ~500ms during playback
5. **Use stubs for testing**: Verify architecture before implementing real components

## Testing with Stubs

```csharp
// Test AudioPipeline without real audio
var sourceRegistry = new TrackSourceRegistry();
sourceRegistry.Register(new StubTrackSource());

var decoderRegistry = new AudioDecoderRegistry();
decoderRegistry.Register(new StubDecoder());

var audioSink = new StubAudioSink();
var processingChain = new AudioProcessingChain();

var pipeline = new AudioPipeline(
    sourceRegistry,
    decoderRegistry,
    audioSink,
    processingChain);

// Test playback
var playCommand = new PlayCommand { TrackUri = "stub:test" };
await pipeline.PlayAsync(playCommand);

// Verify state
Assert.True(pipeline.CurrentState.IsPlaying);
Assert.Equal("stub:test", pipeline.CurrentState.TrackUri);

// Test pause
await pipeline.PauseAsync();
Assert.True(pipeline.CurrentState.IsPaused);
Assert.False(pipeline.CurrentState.IsPlaying);
```

## What's Next?

After AudioPipeline is working:

1. **Test with stubs** - Verify command handling works
2. **Enable bidirectional mode** - Test with PlaybackStateManager
3. **Implement WaveDecoder** - Test with local WAV files
4. **Implement WASAPI sink** - Hear actual audio!
5. **Implement VorbisDecoder** - Play Spotify tracks
6. **Implement SpotifyTrackSource** - Load from Spotify CDN
7. **Add PlaybackQueue** - Multi-track playback with auto-advance

The foundation is complete - now add the pieces you need!
