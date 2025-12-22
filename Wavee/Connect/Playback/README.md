# Wavee Audio Pipeline

## Status: Foundation Complete ✅

The complete audio pipeline architecture is implemented with all abstractions and infrastructure in place. The project **builds successfully** with zero errors.

## What's Ready to Use

### ✅ Complete Abstractions (All Interfaces Defined)

**Layer 1 - Track Sources**:
- `ITrackSource` - Interface for loading tracks from any source
- `ITrackStream` - Unified track stream with metadata
- `TrackSourceRegistry` - URI routing to appropriate source
- `StubTrackSource` - Testing implementation

**Layer 2 - Audio Decoders**:
- `IAudioDecoder` - Interface for audio decoders
- `AudioDecoderRegistry` - Format detection and routing
- `StubDecoder` - Testing implementation (generates silence)

**Layer 3 - Audio Processing**:
- `IAudioProcessor` - Interface for audio effects
- `AudioProcessingChain` - Chainable processor pipeline

**Layer 4 - Audio Output**:
- `IAudioSink` - Interface for platform audio output
- `StubAudioSink` - Testing implementation (discards audio)

**Core Data Types**:
- `AudioFormat` - PCM audio format with helper methods
- `AudioBuffer` - Audio data with timing
- `TrackMetadata` - Rich metadata for any source
- `AudioSinkStatus` - Playback status information

## Architecture Overview

```
Track URI (spotify:, file://, http://)
    ↓
TrackSourceRegistry → ITrackSource → ITrackStream
    ↓
AudioDecoderRegistry → IAudioDecoder → AudioBuffer stream
    ↓
AudioProcessingChain → IAudioProcessor[] → Processed AudioBuffer
    ↓
IAudioSink → Platform Audio Output
```

## Quick Start (Testing)

```csharp
using Wavee.Connect.Playback.Abstractions;
using Wavee.Connect.Playback.Sources;
using Wavee.Connect.Playback.Decoders;
using Wavee.Connect.Playback.Sinks;

// Create registries
var sourceRegistry = new TrackSourceRegistry();
var decoderRegistry = new AudioDecoderRegistry();

// Register stub implementations (for testing)
sourceRegistry.Register(new StubTrackSource());
decoderRegistry.Register(new StubDecoder());

// Create audio sink
var audioSink = new StubAudioSink();

// Load a stub track
var trackStream = await sourceRegistry.LoadAsync("stub:test", CancellationToken.None);

// Detect format and get decoder
var (decoder, format) = await decoderRegistry.DetectFormatAsync(
    trackStream.AudioStream,
    CancellationToken.None);

// Initialize sink
await audioSink.InitializeAsync(format);

// Decode and play
await foreach (var buffer in decoder.DecodeAsync(trackStream.AudioStream))
{
    await audioSink.WriteAsync(buffer.Data);
    Console.WriteLine($"Playing at {buffer.PositionMs}ms");
}
```

## What's Complete

### ✅ All Audio Processors
All audio processing components are fully implemented and ready to use:
- **VolumeProcessor**: Linear and dB-based volume control
- **EqualizerProcessor**: Multi-band parametric EQ with biquad filters
- **CrossfadeProcessor**: Equal-power crossfading with multiple curves
- **NormalizationProcessor**: ReplayGain/LUFS support with soft clipping

## What's Implemented

### ✅ Real Decoders

**BassDecoder** ✅:
- Decodes MP3, FLAC, WAV, AIFF using the BASS audio library
- Supports seeking to arbitrary positions
- Outputs 16-bit PCM audio
- See [BASS_SETUP.md](BASS_SETUP.md) for native library installation

**VorbisDecoder** ✅:
- Decodes OGG Vorbis streams (used for Spotify audio)
- Uses NVorbis library

## What's Left to Implement

### Priority 1: Real Audio Output

**WASAPI Audio Sink** (Windows):
```csharp
// File: Sinks/WasapiAudioSink.cs
public class WasapiAudioSink : IAudioSink
{
    // YOUR IMPLEMENTATION
    // Use Windows WASAPI for audio output
}
```

### Priority 2: Spotify Integration

**Spotify Track Source**:
```csharp
// File: Sources/SpotifyTrackSource.cs
public class SpotifyTrackSource : ITrackSource
{
    // YOUR IMPLEMENTATION
    // Load from Spotify CDN
    // Use existing AudioDecryptStream for decryption
}
```

### ✅ Audio Processing (Complete!)

**Volume Processor** ✅:
- Linear and logarithmic (dB) volume control
- Supports 16/24/32-bit audio
- Overflow prevention with clamping

**Equalizer Processor** ✅:
- Multi-band parametric EQ using biquad IIR filters
- Supports peaking, low shelf, high shelf, lowpass, highpass
- Built-in 10-band graphic EQ preset
- Per-channel filtering for stereo/multi-channel audio

**Crossfade Processor** ✅:
- Equal-power crossfading for smooth track transitions
- Multiple curve types: Linear, Equal-Power, Logarithmic, S-Curve
- Configurable duration (0-30 seconds)
- Queue-based mixing for seamless transitions

**Normalization Processor** ✅:
- ReplayGain support (track and album modes)
- Configurable pre-amplification (-24dB to +24dB)
- Soft clipping prevention using tanh curve
- Automatic volume leveling based on metadata

### ✅ Pipeline Orchestration (AudioPipeline Complete!)

**AudioPipeline** ✅ (Implements IPlaybackEngine):
- Full playback loop: source → decoder → processors → sink
- All IPlaybackEngine methods implemented (Play, Pause, Resume, Seek, Skip)
- LocalPlaybackState publishing for bidirectional sync
- Auto-subscribes to ConnectCommandHandler observables
- Supports repeat track mode
- Thread-safe with proper locking
- ~550 lines of production-ready code

**Two Constructor Modes**:
```csharp
// Manual control (no command integration)
var pipeline = new AudioPipeline(sourceRegistry, decoderRegistry, sink, processingChain);

// Auto command integration (subscribes to Spotify commands)
var pipeline = new AudioPipeline(sourceRegistry, decoderRegistry, sink, processingChain, commandHandler);
```

**PlaybackQueue** (Future Enhancement):
- Not required for single-track playback
- Will add multi-track queue, shuffle, auto-advance
- AudioPipeline works without it

**PlaybackCommandRouter** ❌ (NOT NEEDED):
- ConnectCommandHandler already provides typed observables
- AudioPipeline subscribes directly (cleaner architecture)

## File Structure

```
Wavee/Connect/Playback/
├── README.md                          (this file)
├── BASS_SETUP.md                      (native BASS library setup)
├── IMPLEMENTATION_STATUS.md           (detailed status)
├── IMPLEMENTATION_GUIDE.md            (how-to guide)
├── Abstractions/
│   ├── AudioFormat.cs                 ✅ Complete
│   ├── AudioBuffer.cs                 ✅ Complete
│   ├── TrackMetadata.cs               ✅ Complete
│   ├── ITrackSource.cs                ✅ Complete
│   ├── ITrackStream.cs                ✅ Complete
│   ├── IAudioDecoder.cs               ✅ Complete
│   ├── IAudioProcessor.cs             ✅ Complete
│   └── IAudioSink.cs                  ✅ Complete
├── Sources/
│   ├── TrackSourceRegistry.cs         ✅ Complete
│   ├── StubTrackSource.cs             ✅ Complete
│   ├── LocalFileTrackSource.cs        ✅ Complete (local audio files)
│   ├── SpotifyTrackSource.cs          ⚠️ TODO (you implement)
│   └── HttpTrackSource.cs             ⚠️ TODO (future)
├── Decoders/
│   ├── AudioDecoderRegistry.cs        ✅ Complete
│   ├── StubDecoder.cs                 ✅ Complete
│   ├── VorbisDecoder.cs               ✅ Complete (OGG Vorbis via NVorbis)
│   └── BassDecoder.cs                 ✅ Complete (MP3/FLAC/WAV/AIFF via BASS)
├── Processors/
│   ├── AudioProcessingChain.cs        ✅ Complete
│   ├── VolumeProcessor.cs             ✅ Complete
│   ├── EqualizerProcessor.cs          ✅ Complete
│   ├── NormalizationProcessor.cs      ✅ Complete
│   └── CrossfadeProcessor.cs          ✅ Complete
├── Sinks/
│   ├── StubAudioSink.cs               ✅ Complete
│   ├── WasapiAudioSink.cs             ⚠️ TODO (you implement)
│   └── AlsaAudioSink.cs               ⚠️ TODO (future)
├── AudioPipeline.cs                   ✅ Complete (orchestrator)
├── PlaybackQueue.cs                   ⚠️ TODO (future - for full queue support)
└── PlaybackCommandRouter.cs           ❌ NOT NEEDED (commands wire directly)
```

## Integration with PlaybackStateManager

Once you implement `AudioPipeline` (which implements `IPlaybackEngine`), you can enable bidirectional mode:

```csharp
// Create pipeline with your implementations
var pipeline = new AudioPipeline(
    sourceRegistry,
    decoderRegistry,
    audioSink,
    processingChain);

// Enable bidirectional PlaybackStateManager
var stateManager = new PlaybackStateManager(
    dealerClient,
    pipeline,        // IPlaybackEngine
    spClient,
    session);

// Now:
// - Remote commands → pipeline (Spotify app controls your playback)
// - Local state → Spotify (your playback state published to cloud)
```

## Build Status

✅ **Project builds successfully with zero errors**
✅ **All abstractions complete**
✅ **All audio processors complete** (Volume, EQ, Normalization, Crossfade)
✅ **AudioPipeline complete** (Full IPlaybackEngine implementation)
✅ **Stub implementations working**
✅ **Ready for bidirectional mode**
✅ **Ready for real decoders/sinks**

## Next Steps

### Ready to Use NOW:
1. **Test with stubs**: AudioPipeline + StubTrackSource + StubDecoder + StubAudioSink
2. **Enable bidirectional mode**: Wire AudioPipeline into PlaybackStateManager
3. **Test Spotify commands**: Control playback from Spotify app

### Implement for Real Audio:
1. ✅ **BassDecoder**: Decodes MP3/FLAC/WAV/AIFF (see [BASS_SETUP.md](BASS_SETUP.md))
2. ✅ **LocalFileTrackSource**: Loads local audio files from filesystem
3. ✅ **VorbisDecoder**: Decodes Spotify's OGG Vorbis streams
4. **SpotifyTrackSource**: Load encrypted audio from Spotify CDN (use existing AudioDecryptStream)
5. **WASAPI sink**: Hear actual audio on Windows!

### Future Enhancements:
1. **PlaybackQueue**: Multi-track queue with auto-advance
2. **Crossfade integration**: Wire CrossfadeProcessor with queue transitions
3. **BASS plugins**: AAC, WMA, Opus support (see [BASS_SETUP.md](BASS_SETUP.md))

See **IMPLEMENTATION_GUIDE.md** for detailed implementation examples.

---

**Created**: 2025-01-31
**Status**: Foundation Complete, Ready for Implementation
**Build**: ✅ Success (0 errors, 0 warnings)