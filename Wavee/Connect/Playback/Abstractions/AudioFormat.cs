namespace Wavee.Connect.Playback.Abstractions;

/// <summary>
/// Represents the format of PCM audio data.
/// </summary>
/// <param name="SampleRate">Sample rate in Hz (e.g., 44100, 48000).</param>
/// <param name="Channels">Number of audio channels (1 = mono, 2 = stereo).</param>
/// <param name="BitsPerSample">Bits per sample (typically 16 or 24).</param>
public sealed record AudioFormat(int SampleRate, int Channels, int BitsPerSample)
{
    /// <summary>
    /// Gets the number of bytes per sample across all channels.
    /// </summary>
    public int BytesPerFrame => Channels * (BitsPerSample / 8);

    /// <summary>
    /// Gets the number of bytes per second.
    /// </summary>
    public int BytesPerSecond => SampleRate * BytesPerFrame;

    /// <summary>
    /// Calculates the duration in milliseconds for a given number of bytes.
    /// </summary>
    public long BytesToMilliseconds(long bytes)
    {
        return (bytes * 1000) / BytesPerSecond;
    }

    /// <summary>
    /// Calculates the number of bytes for a given duration in milliseconds.
    /// </summary>
    public long MillisecondsToBytes(long milliseconds)
    {
        return (milliseconds * BytesPerSecond) / 1000;
    }

    /// <summary>
    /// Standard CD quality audio format (44.1 kHz, 16-bit stereo).
    /// </summary>
    public static AudioFormat CdQuality => new(44100, 2, 16);

    /// <summary>
    /// High quality audio format (48 kHz, 16-bit stereo).
    /// </summary>
    public static AudioFormat HighQuality => new(48000, 2, 16);

    /// <summary>
    /// Studio quality audio format (96 kHz, 24-bit stereo).
    /// </summary>
    public static AudioFormat StudioQuality => new(96000, 2, 24);
}
