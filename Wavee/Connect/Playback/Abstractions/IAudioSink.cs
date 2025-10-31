namespace Wavee.Connect.Playback.Abstractions;

/// <summary>
/// Interface for platform audio output (WASAPI, ALSA, CoreAudio, etc.).
/// </summary>
public interface IAudioSink : IAsyncDisposable
{
    /// <summary>
    /// Gets the sink name (e.g., "WASAPI", "ALSA", "Stub").
    /// </summary>
    string SinkName { get; }

    /// <summary>
    /// Initializes the audio sink with the given format.
    /// </summary>
    /// <param name="format">PCM audio format.</param>
    /// <param name="bufferSizeMs">Desired buffer size in milliseconds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task InitializeAsync(AudioFormat format, int bufferSizeMs = 100, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes PCM audio data to the output device.
    /// </summary>
    /// <param name="audioData">PCM audio data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task WriteAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current playback status.
    /// </summary>
    /// <returns>Status including position and buffer info.</returns>
    Task<AudioSinkStatus> GetStatusAsync();

    /// <summary>
    /// Pauses audio output.
    /// </summary>
    Task PauseAsync();

    /// <summary>
    /// Resumes audio output.
    /// </summary>
    Task ResumeAsync();

    /// <summary>
    /// Flushes any buffered audio data.
    /// </summary>
    Task FlushAsync();
}

/// <summary>
/// Audio sink status information.
/// </summary>
/// <param name="PositionMs">Current playback position in milliseconds.</param>
/// <param name="BufferedMs">Amount of audio buffered in milliseconds.</param>
/// <param name="IsPlaying">Whether audio is currently playing.</param>
public sealed record AudioSinkStatus(long PositionMs, int BufferedMs, bool IsPlaying);
