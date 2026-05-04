namespace Wavee.Core.Audio;

/// <summary>
/// Preferred audio quality for streaming.
/// </summary>
/// <remarks>
/// Maps to Spotify audio file formats:
/// - Normal: OGG_VORBIS_96 (96 kbps)
/// - High: OGG_VORBIS_160 (160 kbps)
/// - VeryHigh: OGG_VORBIS_320 (320 kbps) - Requires Premium
/// </remarks>
public enum AudioQuality
{
    /// <summary>
    /// Normal quality (96 kbps Vorbis).
    /// Available on all account types.
    /// </summary>
    Normal = 96,

    /// <summary>
    /// High quality (160 kbps Vorbis).
    /// Available on all account types.
    /// </summary>
    High = 160,

    /// <summary>
    /// Very high quality (320 kbps Vorbis).
    /// Requires Premium subscription.
    /// </summary>
    VeryHigh = 320
}

/// <summary>
/// Spotify audio file format identifiers.
/// </summary>
public enum AudioFileFormat
{
    // Vorbis formats
    OGG_VORBIS_96 = 0,
    OGG_VORBIS_160 = 1,
    OGG_VORBIS_320 = 2,

    // MP3 formats
    MP3_256 = 3,
    MP3_320 = 4,
    MP3_160 = 5,
    MP3_96 = 6,
    MP3_160_ENC = 7,

    // AAC formats
    AAC_24 = 8,
    AAC_48 = 9,
    AAC_160 = 10,
    AAC_320 = 11,

    // MP4
    MP4_128 = 12,

    // Other
    OTHER5 = 13,

    // FLAC
    FLAC_FLAC = 14,
    FLAC_FLAC_24BIT = 15,

    // Extended HE-AAC
    XHE_AAC_24 = 16,
    XHE_AAC_16 = 17,
    XHE_AAC_12 = 18
}

/// <summary>
/// Extension methods for audio quality and format.
/// </summary>
public static class AudioQualityExtensions
{
    /// <summary>
    /// Gets the preferred file format for a quality setting.
    /// </summary>
    public static AudioFileFormat GetPreferredFormat(this AudioQuality quality)
    {
        return quality switch
        {
            AudioQuality.VeryHigh => AudioFileFormat.OGG_VORBIS_320,
            AudioQuality.High => AudioFileFormat.OGG_VORBIS_160,
            AudioQuality.Normal => AudioFileFormat.OGG_VORBIS_96,
            _ => AudioFileFormat.OGG_VORBIS_160
        };
    }

    /// <summary>
    /// Gets an ordered list of preferred formats for fallback selection.
    /// </summary>
    public static AudioFileFormat[] GetPreferredFormats(this AudioQuality quality)
    {
        return quality switch
        {
            AudioQuality.VeryHigh => new[]
            {
                AudioFileFormat.OGG_VORBIS_320,
                AudioFileFormat.OGG_VORBIS_160,
                AudioFileFormat.OGG_VORBIS_96
            },
            AudioQuality.High => new[]
            {
                AudioFileFormat.OGG_VORBIS_160,
                AudioFileFormat.OGG_VORBIS_320,
                AudioFileFormat.OGG_VORBIS_96
            },
            AudioQuality.Normal => new[]
            {
                AudioFileFormat.OGG_VORBIS_96,
                AudioFileFormat.OGG_VORBIS_160,
                AudioFileFormat.OGG_VORBIS_320
            },
            _ => new[]
            {
                AudioFileFormat.OGG_VORBIS_160,
                AudioFileFormat.OGG_VORBIS_320,
                AudioFileFormat.OGG_VORBIS_96
            }
        };
    }

    /// <summary>
    /// Checks if the format is OGG Vorbis.
    /// </summary>
    public static bool IsOggVorbis(this AudioFileFormat format)
    {
        return format is AudioFileFormat.OGG_VORBIS_96
            or AudioFileFormat.OGG_VORBIS_160
            or AudioFileFormat.OGG_VORBIS_320;
    }

    /// <summary>
    /// Checks if the format is MP3.
    /// </summary>
    public static bool IsMp3(this AudioFileFormat format)
    {
        return format is AudioFileFormat.MP3_96
            or AudioFileFormat.MP3_160
            or AudioFileFormat.MP3_256
            or AudioFileFormat.MP3_320
            or AudioFileFormat.MP3_160_ENC;
    }

    /// <summary>
    /// Checks if the format is FLAC.
    /// </summary>
    public static bool IsFlac(this AudioFileFormat format)
    {
        return format is AudioFileFormat.FLAC_FLAC or AudioFileFormat.FLAC_FLAC_24BIT;
    }

    /// <summary>
    /// Gets the approximate bitrate in kbps.
    /// </summary>
    public static int GetBitrate(this AudioFileFormat format)
    {
        return format switch
        {
            AudioFileFormat.OGG_VORBIS_96 => 96,
            AudioFileFormat.OGG_VORBIS_160 => 160,
            AudioFileFormat.OGG_VORBIS_320 => 320,
            AudioFileFormat.MP3_96 => 96,
            AudioFileFormat.MP3_160 => 160,
            AudioFileFormat.MP3_160_ENC => 160,
            AudioFileFormat.MP3_256 => 256,
            AudioFileFormat.MP3_320 => 320,
            AudioFileFormat.AAC_24 => 24,
            AudioFileFormat.AAC_48 => 48,
            AudioFileFormat.AAC_160 => 160,
            AudioFileFormat.AAC_320 => 320,
            AudioFileFormat.MP4_128 => 128,
            AudioFileFormat.FLAC_FLAC => 1411, // Approximate CD quality
            AudioFileFormat.FLAC_FLAC_24BIT => 2116, // Approximate 24-bit
            AudioFileFormat.XHE_AAC_24 => 24,
            AudioFileFormat.XHE_AAC_16 => 16,
            AudioFileFormat.XHE_AAC_12 => 12,
            _ => 160 // Default fallback
        };
    }
}
