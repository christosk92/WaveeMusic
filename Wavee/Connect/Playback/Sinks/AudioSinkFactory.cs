using Microsoft.Extensions.Logging;
using Wavee.Connect.Playback.Abstractions;

namespace Wavee.Connect.Playback.Sinks;

/// <summary>
/// Factory for creating platform-specific audio sinks.
/// </summary>
public static class AudioSinkFactory
{
    /// <summary>
    /// Creates the default audio sink for the current platform.
    /// Uses miniaudio which supports Windows, macOS, and Linux natively.
    /// </summary>
    /// <param name="logger">Optional logger.</param>
    /// <returns>Platform-appropriate audio sink.</returns>
    public static IAudioSink CreateDefault(ILogger? logger = null)
    {
        // PortAudio is cross-platform and AOT-compatible
        // It automatically uses the best backend:
        // - Windows: WASAPI/DirectSound
        // - macOS: CoreAudio
        // - Linux: ALSA/PulseAudio
        return new PortAudioSink(logger);
    }

    /// <summary>
    /// Creates a specific type of audio sink.
    /// </summary>
    /// <param name="sinkType">The type of sink to create.</param>
    /// <param name="logger">Optional logger.</param>
    /// <returns>The requested audio sink.</returns>
    public static IAudioSink Create(AudioSinkType sinkType, ILogger? logger = null)
    {
        return sinkType switch
        {
            AudioSinkType.PortAudio => new PortAudioSink(logger),
            AudioSinkType.Stub => new StubAudioSink(logger),
            _ => new PortAudioSink(logger) // Default to PortAudio
        };
    }
}

/// <summary>
/// Available audio sink types.
/// </summary>
public enum AudioSinkType
{
    /// <summary>
    /// Cross-platform PortAudio backend (WASAPI/CoreAudio/ALSA).
    /// </summary>
    PortAudio,

    /// <summary>
    /// Stub sink that discards audio (for testing).
    /// </summary>
    Stub
}
