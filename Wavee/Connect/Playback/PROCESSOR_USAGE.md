# Audio Processor Usage Guide

This guide demonstrates how to use the audio processors with the Wavee audio pipeline.

## Basic Setup

```csharp
using Wavee.Connect.Playback.Abstractions;
using Wavee.Connect.Playback.Processors;

// Create the processing chain
var processingChain = new AudioProcessingChain();

// Initialize with audio format (e.g., CD quality)
await processingChain.InitializeAsync(AudioFormat.CdQuality);
```

## Volume Control

```csharp
// Create volume processor
var volumeProcessor = new VolumeProcessor();

// Set volume using linear scale (0.0 to 2.0+)
volumeProcessor.Volume = 0.8f; // 80% volume

// Or set volume using decibels
volumeProcessor.VolumeDb = -3.0f; // -3dB (approximately 70% volume)

// Add to processing chain
processingChain.AddProcessor(volumeProcessor);

// Enable/disable at runtime
volumeProcessor.IsEnabled = true;
```

## Normalization (ReplayGain)

```csharp
// Create normalization processor
var normalizationProcessor = new NormalizationProcessor
{
    Mode = NormalizationMode.Track,  // or NormalizationMode.Album
    PreAmpDb = -6.0f,                 // Pre-amplification
    PreventClipping = true            // Enable soft clipping
};

processingChain.AddProcessor(normalizationProcessor);

// When a track starts, set its gain from metadata
var metadata = new TrackMetadata
{
    Uri = "spotify:track:...",
    ReplayGainTrackGain = -5.2,  // dB (from track metadata)
    ReplayGainAlbumGain = -4.8   // dB (from album metadata)
};

normalizationProcessor.SetTrackGain(metadata);
```

## Equalizer

### 10-Band Graphic EQ

```csharp
// Create equalizer with standard 10-band graphic EQ
var equalizerProcessor = new EqualizerProcessor();
equalizerProcessor.CreateGraphicEq10Band();

// Adjust individual bands
var bands = equalizerProcessor.Bands;
bands[0].GainDb = +3.0;  // 31 Hz: +3dB (boost bass)
bands[1].GainDb = +2.0;  // 62 Hz: +2dB
bands[4].GainDb = -2.0;  // 500 Hz: -2dB (cut mids)
bands[9].GainDb = +4.0;  // 16 kHz: +4dB (boost treble)

processingChain.AddProcessor(equalizerProcessor);
```

### Custom Parametric EQ

```csharp
var equalizerProcessor = new EqualizerProcessor();

// Add custom bands
equalizerProcessor.AddBand(new EqualizerBand(
    frequencyHz: 80.0,       // Center frequency
    gainDb: +6.0,            // Boost by 6dB
    q: 1.2,                  // Q factor (bandwidth)
    type: BandType.LowShelf  // Low shelf filter
));

equalizerProcessor.AddBand(new EqualizerBand(
    frequencyHz: 2500.0,
    gainDb: -3.0,            // Cut by 3dB
    q: 2.0,                  // Narrow band
    type: BandType.Peaking   // Peaking filter
));

equalizerProcessor.AddBand(new EqualizerBand(
    frequencyHz: 10000.0,
    gainDb: +4.0,
    q: 0.7,
    type: BandType.HighShelf // High shelf filter
));

processingChain.AddProcessor(equalizerProcessor);
```

### Filter Types

- **Peaking**: Bell curve boost/cut (parametric EQ)
- **LowShelf**: Boost/cut all frequencies below center frequency
- **HighShelf**: Boost/cut all frequencies above center frequency
- **LowPass**: Cut frequencies above center frequency
- **HighPass**: Cut frequencies below center frequency

## Crossfade

```csharp
// Create crossfade processor
var crossfadeProcessor = new CrossfadeProcessor
{
    CrossfadeDurationMs = 5000,              // 5-second crossfade
    Curve = CrossfadeCurve.EqualPower,       // Equal-power curve
    CrossfadeEnabled = true
};

processingChain.AddProcessor(crossfadeProcessor);

// When nearing end of track, start crossfade
var trackDurationMs = 240000; // 4 minutes
var currentPositionMs = 235000; // 3:55

if (trackDurationMs - currentPositionMs <= crossfadeProcessor.CrossfadeDurationMs)
{
    crossfadeProcessor.StartCrossfade(currentPositionMs);

    // Start decoding next track and queue buffers
    await foreach (var nextBuffer in DecodeNextTrack())
    {
        crossfadeProcessor.QueueNextTrackBuffer(nextBuffer);
    }
}
```

### Crossfade Curves

- **Linear**: Simple linear fade (can cause volume dip)
- **EqualPower**: Constant perceived loudness (recommended)
- **Logarithmic**: Slower start, faster end
- **SCurve**: Smooth acceleration/deceleration

## Complete Example: Full Processing Chain

```csharp
using Wavee.Connect.Playback.Abstractions;
using Wavee.Connect.Playback.Processors;
using Wavee.Connect.Playback.Sources;
using Wavee.Connect.Playback.Decoders;
using Wavee.Connect.Playback.Sinks;

// Create processing chain
var processingChain = new AudioProcessingChain();

// 1. Add normalization (first in chain)
var normalization = new NormalizationProcessor
{
    Mode = NormalizationMode.Album,
    PreAmpDb = -6.0f,
    PreventClipping = true
};
processingChain.AddProcessor(normalization);

// 2. Add equalizer
var equalizer = new EqualizerProcessor();
equalizer.CreateGraphicEq10Band();
equalizer.Bands[0].GainDb = +3.0;  // Bass boost
equalizer.Bands[9].GainDb = +2.0;  // Treble boost
processingChain.AddProcessor(equalizer);

// 3. Add volume control
var volume = new VolumeProcessor { VolumeDb = -3.0f };
processingChain.AddProcessor(volume);

// 4. Add crossfade (last in chain)
var crossfade = new CrossfadeProcessor
{
    CrossfadeDurationMs = 3000,
    Curve = CrossfadeCurve.EqualPower
};
processingChain.AddProcessor(crossfade);

// Initialize chain with audio format
var format = AudioFormat.CdQuality;
await processingChain.InitializeAsync(format);

// Process audio buffers
var sourceRegistry = new TrackSourceRegistry();
var decoderRegistry = new AudioDecoderRegistry();
var audioSink = new StubAudioSink();

sourceRegistry.Register(new StubTrackSource());
decoderRegistry.Register(new StubDecoder());

var trackStream = await sourceRegistry.LoadAsync("stub:test", CancellationToken.None);
var (decoder, detectedFormat) = await decoderRegistry.DetectFormatAsync(
    trackStream.AudioStream,
    CancellationToken.None);

await audioSink.InitializeAsync(detectedFormat);

// Set track gain from metadata
normalization.SetTrackGain(trackStream.Metadata);

// Decode and process
await foreach (var buffer in decoder.DecodeAsync(trackStream.AudioStream))
{
    // Apply processing chain
    var processed = processingChain.Process(buffer);

    // Send to audio output
    await audioSink.WriteAsync(processed.Data);

    Console.WriteLine($"Position: {processed.PositionMs}ms");
}
```

## Processing Order Recommendations

For best results, arrange processors in this order:

1. **NormalizationProcessor** - Normalize input levels first
2. **EqualizerProcessor** - EQ after normalization
3. **VolumeProcessor** - Master volume control
4. **CrossfadeProcessor** - Crossfade should be last

This order ensures:
- Normalization works on original audio
- EQ operates on normalized levels
- Volume affects all processed audio
- Crossfade mixes final processed audio

## Dynamic Control

```csharp
// Enable/disable processors at runtime
volumeProcessor.IsEnabled = false;        // Bypass volume
equalizerProcessor.IsEnabled = true;      // Enable EQ
normalizationProcessor.IsEnabled = true;  // Enable normalization

// Adjust settings during playback
volumeProcessor.VolumeDb = -6.0f;         // Fade out
equalizerProcessor.Bands[0].GainDb += 2.0; // Boost bass more

// Reset all processors
processingChain.Reset();
```

## Performance Tips

1. **Disable unused processors**: Set `IsEnabled = false` to bypass processing
2. **Limit EQ bands**: More bands = more CPU usage
3. **Use appropriate Q values**: Higher Q requires more precision
4. **Crossfade duration**: Longer crossfades use more memory for buffering
5. **Bit depth**: 16-bit processing is faster than 24-bit or 32-bit

## Thread Safety

All processors are **NOT thread-safe**. Ensure:
- Initialization happens before processing
- Settings are changed from the same thread that processes audio
- Or use proper synchronization (locks, etc.) when changing settings

## Zero Dependencies

All processors are implemented in pure C# with:
- No external audio libraries
- No unsafe code
- No P/Invoke (except for audio sink implementations)
- Cross-platform compatible (Windows, Linux, macOS)

The processors support:
- 16-bit PCM (most common)
- 24-bit PCM (high quality)
- 32-bit PCM (studio quality)
- Any sample rate (44.1kHz, 48kHz, 96kHz, etc.)
- Any channel count (mono, stereo, 5.1, etc.)
