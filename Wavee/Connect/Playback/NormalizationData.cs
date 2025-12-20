using System.Buffers.Binary;

namespace Wavee.Connect.Playback;

/// <summary>
/// Normalization data embedded in Spotify audio files.
/// </summary>
/// <remarks>
/// Located at offset 144 in the encrypted audio file, 16 bytes total.
/// Used for ReplayGain-style volume normalization.
/// </remarks>
/// <param name="TrackGainDb">Track gain in decibels.</param>
/// <param name="TrackPeak">Track peak value (0.0-1.0).</param>
/// <param name="AlbumGainDb">Album gain in decibels.</param>
/// <param name="AlbumPeak">Album peak value (0.0-1.0).</param>
public readonly record struct NormalizationData(
    float TrackGainDb,
    float TrackPeak,
    float AlbumGainDb,
    float AlbumPeak)
{
    /// <summary>
    /// Offset in the audio file where normalization data is located.
    /// </summary>
    public const int FileOffset = 144;

    /// <summary>
    /// Size of normalization data in bytes.
    /// </summary>
    public const int Size = 16;

    /// <summary>
    /// Default normalization data (no gain adjustment).
    /// </summary>
    public static NormalizationData Default => new(0f, 1f, 0f, 1f);

    /// <summary>
    /// Parses normalization data from raw bytes.
    /// </summary>
    /// <param name="data">16 bytes of normalization data.</param>
    /// <returns>Parsed normalization data.</returns>
    public static NormalizationData Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < Size)
            return Default;

        // Data is stored as 4 little-endian floats (per librespot-java)
        var trackGain = BinaryPrimitives.ReadSingleLittleEndian(data);
        var trackPeak = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(4));
        var albumGain = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(8));
        var albumPeak = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(12));

        return new NormalizationData(trackGain, trackPeak, albumGain, albumPeak);
    }

    /// <summary>
    /// Calculates the linear gain factor for track-based normalization.
    /// </summary>
    /// <param name="targetDb">Target loudness in dB (typically -14 to -23 LUFS).</param>
    /// <param name="preventClipping">Whether to prevent clipping by limiting gain.</param>
    /// <returns>Linear gain factor to multiply samples by.</returns>
    public float GetTrackGainFactor(float targetDb = -14f, bool preventClipping = true)
    {
        var gainDb = targetDb - TrackGainDb;
        var gainFactor = MathF.Pow(10f, gainDb / 20f);

        if (preventClipping && TrackPeak > 0)
        {
            var maxGain = 1f / TrackPeak;
            gainFactor = MathF.Min(gainFactor, maxGain);
        }

        return gainFactor;
    }

    /// <summary>
    /// Calculates the linear gain factor for album-based normalization.
    /// </summary>
    /// <param name="targetDb">Target loudness in dB.</param>
    /// <param name="preventClipping">Whether to prevent clipping by limiting gain.</param>
    /// <returns>Linear gain factor to multiply samples by.</returns>
    public float GetAlbumGainFactor(float targetDb = -14f, bool preventClipping = true)
    {
        var gainDb = targetDb - AlbumGainDb;
        var gainFactor = MathF.Pow(10f, gainDb / 20f);

        if (preventClipping && AlbumPeak > 0)
        {
            var maxGain = 1f / AlbumPeak;
            gainFactor = MathF.Min(gainFactor, maxGain);
        }

        return gainFactor;
    }

    public override string ToString() =>
        $"Track: {TrackGainDb:F2}dB (peak={TrackPeak:F4}), Album: {AlbumGainDb:F2}dB (peak={AlbumPeak:F4})";
}
