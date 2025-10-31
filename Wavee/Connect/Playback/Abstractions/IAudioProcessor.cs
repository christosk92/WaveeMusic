namespace Wavee.Connect.Playback.Abstractions;

/// <summary>
/// Interface for audio processors (EQ, crossfade, normalization, etc.).
/// </summary>
public interface IAudioProcessor
{
    /// <summary>
    /// Gets the processor name (e.g., "Equalizer", "Crossfade", "Normalization").
    /// </summary>
    string ProcessorName { get; }

    /// <summary>
    /// Gets or sets whether this processor is enabled.
    /// </summary>
    bool IsEnabled { get; set; }

    /// <summary>
    /// Initializes the processor with the audio format.
    /// Called once before processing begins.
    /// </summary>
    /// <param name="format">Audio format that will be processed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task InitializeAsync(AudioFormat format, CancellationToken cancellationToken = default);

    /// <summary>
    /// Processes an audio buffer.
    /// </summary>
    /// <param name="input">Input audio buffer.</param>
    /// <returns>Processed audio buffer (may be same instance if no processing done).</returns>
    AudioBuffer Process(AudioBuffer input);

    /// <summary>
    /// Resets the processor state (e.g., when seeking or changing tracks).
    /// </summary>
    void Reset();
}
