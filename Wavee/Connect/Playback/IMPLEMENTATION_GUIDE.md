# Audio Pipeline Implementation Guide

## Quick Start

This guide shows you how to complete the audio pipeline implementation. All interfaces are defined, you just need to fill in the implementations.

## What's Ready

✅ **Complete Abstractions**:
- `AudioFormat`, `AudioBuffer`, `TrackMetadata` - Data structures
- `ITrackSource`, `ITrackStream` - Track loading interfaces
- `IAudioDecoder` - Decoder interface
- `IAudioProcessor` - Processor interface
- `IAudioSink` - Audio output interface

## Implementation Order (Recommended)

### 1. Start Simple: WAV Decoder + Stub Sink

**Goal**: Get basic playback working with minimal dependencies

```csharp
// File: Wavee/Connect/Playback/Decoders/WaveDecoder.cs
public class WaveDecoder : IAudioDecoder
{
    public string FormatName => "WAV";

    public bool CanDecode(Stream stream)
    {
        // Check for "RIFF" header
        var header = new byte[4];
        stream.Read(header, 0, 4);
        stream.Position = 0;
        return Encoding.ASCII.GetString(header) == "RIFF";
    }

    public async Task<AudioFormat> GetFormatAsync(Stream stream, CancellationToken ct)
    {
        // Parse WAV header (44 bytes standard)
        // Return AudioFormat with sample rate, channels, bits
    }

    public async IAsyncEnumerable<AudioBuffer> DecodeAsync(Stream stream, CancellationToken ct)
    {
        // Skip 44-byte header
        // Yield chunks of PCM data
        var buffer = new byte[4096];
        long position = 0;
        while (true)
        {
            int read = await stream.ReadAsync(buffer, ct);
            if (read == 0) break;
            yield return new AudioBuffer(buffer[..read], position);
            position += /* calculate based on format */;
        }
    }
}
```

### 2. Stub Audio Sink (Testing Without Real Audio)

```csharp
// File: Wavee/Connect/Playback/Sinks/StubAudioSink.cs
public class StubAudioSink : IAudioSink
{
    private AudioFormat? _format;
    private long _position;
    private bool _isPlaying;

    public string SinkName => "Stub";

    public Task InitializeAsync(AudioFormat format, int bufferSizeMs, CancellationToken ct)
    {
        _format = format;
        return Task.CompletedTask;
    }

    public Task WriteAsync(ReadOnlyMemory<byte> audioData, CancellationToken ct)
    {
        // Simulate writing by advancing position
        _position += _format!.BytesToMilliseconds(audioData.Length);
        // Optional: Add small delay to simulate real-time playback
        // await Task.Delay((int)_format.BytesToMilliseconds(audioData.Length), ct);
        return Task.CompletedTask;
    }

    public Task<AudioSinkStatus> GetStatusAsync()
    {
        return Task.FromResult(new AudioSinkStatus(_position, 0, _isPlaying));
    }

    public Task PauseAsync() { _isPlaying = false; return Task.CompletedTask; }
    public Task ResumeAsync() { _isPlaying = true; return Task.CompletedTask; }
    public Task FlushAsync() { _position = 0; return Task.CompletedTask; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```

### 3. Local File Track Source

```csharp
// File: Wavee/Connect/Playback/Sources/LocalFileTrackSource.cs
public class LocalFileTrackSource : ITrackSource
{
    public string SourceName => "LocalFile";

    public bool CanHandle(string uri)
    {
        return uri.StartsWith("file:///") ||
               Path.IsPathRooted(uri) && File.Exists(uri);
    }

    public async Task<ITrackStream> LoadAsync(string uri, CancellationToken ct)
    {
        var filePath = uri.StartsWith("file:///")
            ? new Uri(uri).LocalPath
            : uri;

        if (!File.Exists(filePath))
            throw new FileNotFoundException("Track file not found", filePath);

        var stream = File.OpenRead(filePath);
        var metadata = await ReadMetadataAsync(filePath, ct);

        return new FileTrackStream(stream, metadata);
    }

    private async Task<TrackMetadata> ReadMetadataAsync(string filePath, CancellationToken ct)
    {
        // TODO: Read ID3 tags, Vorbis comments, etc.
        // For now, just return basic metadata
        return new TrackMetadata
        {
            Uri = filePath,
            Title = Path.GetFileNameWithoutExtension(filePath)
        };
    }
}

internal class FileTrackStream : ITrackStream
{
    public FileTrackStream(Stream audioStream, TrackMetadata metadata)
    {
        AudioStream = audioStream;
        Metadata = metadata;
    }

    public Stream AudioStream { get; }
    public TrackMetadata Metadata { get; }
    public AudioFormat? KnownFormat => null;  // Decoder will detect
    public bool CanSeek => AudioStream.CanSeek;

    public ValueTask DisposeAsync() => AudioStream.DisposeAsync();
}
```

### 4. Basic Audio Pipeline (No Processing Yet)

```csharp
// File: Wavee/Connect/Playback/AudioPipeline.cs
public class AudioPipeline : IPlaybackEngine
{
    private readonly IAudioDecoder _decoder;
    private readonly IAudioSink _sink;
    private readonly ITrackSource _trackSource;
    private readonly Subject<LocalPlaybackState> _stateChanges = new();
    private LocalPlaybackState _currentState = LocalPlaybackState.Empty;
    private CancellationTokenSource? _playbackCts;

    public IObservable<LocalPlaybackState> StateChanges => _stateChanges;
    public LocalPlaybackState CurrentState => _currentState;

    public async Task PlayAsync(PlayCommand command, CancellationToken ct)
    {
        // 1. Stop current playback
        _playbackCts?.Cancel();

        // 2. Load track
        var trackStream = await _trackSource.LoadAsync(command.TrackUri!, ct);

        // 3. Decode format
        var format = await _decoder.GetFormatAsync(trackStream.AudioStream, ct);

        // 4. Initialize sink
        await _sink.InitializeAsync(format, cancellationToken: ct);

        // 5. Start playback loop
        _playbackCts = new CancellationTokenSource();
        _ = Task.Run(() => PlaybackLoopAsync(trackStream, format, _playbackCts.Token));

        // 6. Update state
        UpdateState(new LocalPlaybackState
        {
            TrackUri = command.TrackUri,
            IsPlaying = true,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
    }

    private async Task PlaybackLoopAsync(ITrackStream trackStream, AudioFormat format, CancellationToken ct)
    {
        try
        {
            await foreach (var buffer in _decoder.DecodeAsync(trackStream.AudioStream, ct))
            {
                await _sink.WriteAsync(buffer.Data, ct);

                // Periodically update position
                if (buffer.PositionMs % 1000 < 100)  // Every ~1 second
                {
                    UpdateState(_currentState with { PositionMs = buffer.PositionMs });
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal stop
        }
        finally
        {
            await trackStream.DisposeAsync();
        }
    }

    public async Task PauseAsync(CancellationToken ct)
    {
        await _sink.PauseAsync();
        UpdateState(_currentState with { IsPlaying = false, IsPaused = true });
    }

    public async Task ResumeAsync(CancellationToken ct)
    {
        await _sink.ResumeAsync();
        UpdateState(_currentState with { IsPlaying = true, IsPaused = false });
    }

    // TODO: Implement other IPlaybackEngine methods (SeekAsync, SkipNextAsync, etc.)

    private void UpdateState(LocalPlaybackState newState)
    {
        _currentState = newState;
        _stateChanges.OnNext(newState);
    }
}
```

## Your Custom Implementations

### Vorbis Decoder (YOU IMPLEMENT)

```csharp
// File: Wavee/Connect/Playback/Decoders/VorbisDecoder.cs
public class VorbisDecoder : IAudioDecoder
{
    public string FormatName => "Vorbis";

    public bool CanDecode(Stream stream)
    {
        // Check for OGG container + Vorbis codec
        // YOUR IMPLEMENTATION
    }

    public async Task<AudioFormat> GetFormatAsync(Stream stream, CancellationToken ct)
    {
        // Parse Vorbis headers
        // YOUR IMPLEMENTATION
    }

    public async IAsyncEnumerable<AudioBuffer> DecodeAsync(Stream stream, CancellationToken ct)
    {
        // YOUR CUSTOM VORBIS DECODER
        // Yield PCM audio buffers
    }
}
```

### WASAPI Audio Sink (YOU IMPLEMENT)

```csharp
// File: Wavee/Connect/Playback/Sinks/WasapiAudioSink.cs
public class WasapiAudioSink : IAudioSink
{
    public string SinkName => "WASAPI";

    public async Task InitializeAsync(AudioFormat format, int bufferSizeMs, CancellationToken ct)
    {
        // Initialize WASAPI
        // YOUR IMPLEMENTATION
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> audioData, CancellationToken ct)
    {
        // Write to WASAPI buffer
        // YOUR IMPLEMENTATION
    }

    // ... other methods
}
```

### Spotify Track Source (YOU IMPLEMENT)

```csharp
// File: Wavee/Connect/Playback/Sources/SpotifyTrackSource.cs
public class SpotifyTrackSource : ITrackSource
{
    public string SourceName => "Spotify";

    public bool CanHandle(string uri) => uri.StartsWith("spotify:track:");

    public async Task<ITrackStream> LoadAsync(string uri, CancellationToken ct)
    {
        // 1. Parse track ID from URI
        // 2. Get CDN URL from Spotify API
        // 3. Download encrypted audio stream
        // 4. Return stream wrapped with AudioDecryptStream
        // YOUR IMPLEMENTATION
    }
}
```

## Audio Processors

### Volume Processor (Simple Example)

```csharp
public class VolumeProcessor : IAudioProcessor
{
    private float _volumeMultiplier = 1.0f;  // 0.0 to 1.0

    public void SetVolume(float volume) => _volumeMultiplier = Math.Clamp(volume, 0f, 1f);

    public AudioBuffer Process(AudioBuffer input)
    {
        if (!IsEnabled || Math.Abs(_volumeMultiplier - 1.0f) < 0.001f)
            return input;  // Bypass

        var output = new byte[input.Data.Length];
        var span = input.Data.Span;

        // Assuming 16-bit PCM
        for (int i = 0; i < span.Length; i += 2)
        {
            short sample = (short)(span[i] | (span[i + 1] << 8));
            sample = (short)(sample * _volumeMultiplier);
            output[i] = (byte)sample;
            output[i + 1] = (byte)(sample >> 8);
        }

        return new AudioBuffer(output, input.PositionMs);
    }
}
```

## Integration Example

```csharp
// In your app initialization:
var waveDecoder = new WaveDecoder();
var stubSink = new StubAudioSink();
var localFileSource = new LocalFileTrackSource();

var pipeline = new AudioPipeline(waveDecoder, stubSink, localFileSource);

// Integrate with PlaybackStateManager
var stateManager = new PlaybackStateManager(
    dealerClient,
    pipeline,         // IPlaybackEngine
    spClient,
    session);

// Now you have bidirectional mode!
// Commands from Spotify → pipeline
// Local state → published to Spotify

// Test with local WAV file
await pipeline.PlayAsync(new PlayCommand {
    TrackUri = "C:\\Music\\test.wav"
});
```

## Testing Strategy

1. **Test with WAV files** - Simple format, easy to verify
2. **Test with StubAudioSink** - No audio dependencies needed
3. **Add your Vorbis decoder** - Test with Spotify tracks
4. **Add WASAPI sink** - Hear actual audio!
5. **Add processors** - EQ, crossfade, etc.

## Next Phase

Once basic playback works:
1. Add PlaybackQueue for shuffle/repeat
2. Add audio processors for EQ/normalization
3. Implement crossfading
4. Add full command routing

You now have a complete architecture to build upon!
