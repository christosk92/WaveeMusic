using FluentAssertions;
using Wavee.Core.Audio;
using Xunit;

namespace Wavee.Tests.Core.Audio;

/// <summary>
/// Tests for AudioQuality enum and AudioQualityExtensions.
/// Validates quality preferences and format mappings.
/// </summary>
public class AudioQualityTests
{
    [Fact]
    public void GetPreferredFormats_VeryHigh_Returns320First()
    {
        // ============================================================
        // WHY: VeryHigh quality should prefer 320kbps as first choice.
        // ============================================================

        // Act
        var formats = AudioQuality.VeryHigh.GetPreferredFormats();

        // Assert
        formats.Should().HaveCount(3);
        formats[0].Should().Be(AudioFileFormat.OGG_VORBIS_320, "320kbps is first preference");
        formats[1].Should().Be(AudioFileFormat.OGG_VORBIS_160, "160kbps is second preference");
        formats[2].Should().Be(AudioFileFormat.OGG_VORBIS_96, "96kbps is last resort");
    }

    [Fact]
    public void GetPreferredFormats_High_Returns160First()
    {
        // ============================================================
        // WHY: High quality should prefer 160kbps as first choice.
        // ============================================================

        // Act
        var formats = AudioQuality.High.GetPreferredFormats();

        // Assert
        formats.Should().HaveCount(3);
        formats[0].Should().Be(AudioFileFormat.OGG_VORBIS_160, "160kbps is first preference");
        formats[1].Should().Be(AudioFileFormat.OGG_VORBIS_320, "320kbps is second preference");
        formats[2].Should().Be(AudioFileFormat.OGG_VORBIS_96, "96kbps is last resort");
    }

    [Fact]
    public void GetPreferredFormats_Normal_Returns96First()
    {
        // ============================================================
        // WHY: Normal quality should prefer 96kbps as first choice.
        // ============================================================

        // Act
        var formats = AudioQuality.Normal.GetPreferredFormats();

        // Assert
        formats.Should().HaveCount(3);
        formats[0].Should().Be(AudioFileFormat.OGG_VORBIS_96, "96kbps is first preference");
        formats[1].Should().Be(AudioFileFormat.OGG_VORBIS_160, "160kbps is second preference");
        formats[2].Should().Be(AudioFileFormat.OGG_VORBIS_320, "320kbps is last choice");
    }

    [Fact]
    public void GetPreferredFormat_VeryHigh_Returns320()
    {
        // ============================================================
        // WHY: GetPreferredFormat should return the single best format.
        // ============================================================

        // Assert
        AudioQuality.VeryHigh.GetPreferredFormat().Should().Be(AudioFileFormat.OGG_VORBIS_320);
    }

    [Fact]
    public void GetPreferredFormat_High_Returns160()
    {
        // ============================================================
        // WHY: High quality maps to 160kbps.
        // ============================================================

        // Assert
        AudioQuality.High.GetPreferredFormat().Should().Be(AudioFileFormat.OGG_VORBIS_160);
    }

    [Fact]
    public void GetPreferredFormat_Normal_Returns96()
    {
        // ============================================================
        // WHY: Normal quality maps to 96kbps.
        // ============================================================

        // Assert
        AudioQuality.Normal.GetPreferredFormat().Should().Be(AudioFileFormat.OGG_VORBIS_96);
    }

    [Theory]
    [InlineData(AudioFileFormat.OGG_VORBIS_96, 96)]
    [InlineData(AudioFileFormat.OGG_VORBIS_160, 160)]
    [InlineData(AudioFileFormat.OGG_VORBIS_320, 320)]
    [InlineData(AudioFileFormat.MP3_96, 96)]
    [InlineData(AudioFileFormat.MP3_160, 160)]
    [InlineData(AudioFileFormat.MP3_256, 256)]
    [InlineData(AudioFileFormat.MP3_320, 320)]
    public void GetBitrate_ReturnsCorrectKbps(AudioFileFormat format, int expectedBitrate)
    {
        // ============================================================
        // WHY: GetBitrate should return the correct bitrate for each format.
        // ============================================================

        // Act
        var bitrate = format.GetBitrate();

        // Assert
        bitrate.Should().Be(expectedBitrate);
    }

    [Theory]
    [InlineData(AudioFileFormat.OGG_VORBIS_96, true)]
    [InlineData(AudioFileFormat.OGG_VORBIS_160, true)]
    [InlineData(AudioFileFormat.OGG_VORBIS_320, true)]
    [InlineData(AudioFileFormat.MP3_320, false)]
    [InlineData(AudioFileFormat.FLAC_FLAC, false)]
    public void IsOggVorbis_ReturnsCorrectValue(AudioFileFormat format, bool expected)
    {
        // ============================================================
        // WHY: IsOggVorbis should correctly identify Vorbis formats.
        // ============================================================

        // Assert
        format.IsOggVorbis().Should().Be(expected);
    }

    [Theory]
    [InlineData(AudioFileFormat.MP3_96, true)]
    [InlineData(AudioFileFormat.MP3_160, true)]
    [InlineData(AudioFileFormat.MP3_256, true)]
    [InlineData(AudioFileFormat.MP3_320, true)]
    [InlineData(AudioFileFormat.MP3_160_ENC, true)]
    [InlineData(AudioFileFormat.OGG_VORBIS_320, false)]
    [InlineData(AudioFileFormat.FLAC_FLAC, false)]
    public void IsMp3_ReturnsCorrectValue(AudioFileFormat format, bool expected)
    {
        // ============================================================
        // WHY: IsMp3 should correctly identify MP3 formats.
        // ============================================================

        // Assert
        format.IsMp3().Should().Be(expected);
    }

    [Theory]
    [InlineData(AudioFileFormat.FLAC_FLAC, true)]
    [InlineData(AudioFileFormat.FLAC_FLAC_24BIT, true)]
    [InlineData(AudioFileFormat.OGG_VORBIS_320, false)]
    [InlineData(AudioFileFormat.MP3_320, false)]
    public void IsFlac_ReturnsCorrectValue(AudioFileFormat format, bool expected)
    {
        // ============================================================
        // WHY: IsFlac should correctly identify FLAC formats.
        // ============================================================

        // Assert
        format.IsFlac().Should().Be(expected);
    }

    [Fact]
    public void AudioQuality_EnumValues_MatchBitrate()
    {
        // ============================================================
        // WHY: AudioQuality enum values should match their bitrate in kbps.
        // ============================================================

        // Assert
        ((int)AudioQuality.Normal).Should().Be(96);
        ((int)AudioQuality.High).Should().Be(160);
        ((int)AudioQuality.VeryHigh).Should().Be(320);
    }

    [Fact]
    public void GetBitrate_FlacFormats_ReturnsHighBitrate()
    {
        // ============================================================
        // WHY: FLAC formats should return high bitrate values (lossless).
        // ============================================================

        // Assert
        AudioFileFormat.FLAC_FLAC.GetBitrate().Should().BeGreaterThan(1000);
        AudioFileFormat.FLAC_FLAC_24BIT.GetBitrate().Should().BeGreaterThan(1000);
    }

    [Theory]
    [InlineData(AudioFileFormat.AAC_24, 24)]
    [InlineData(AudioFileFormat.AAC_48, 48)]
    [InlineData(AudioFileFormat.XHE_AAC_24, 24)]
    [InlineData(AudioFileFormat.XHE_AAC_16, 16)]
    [InlineData(AudioFileFormat.XHE_AAC_12, 12)]
    public void GetBitrate_AacFormats_ReturnsCorrectBitrate(AudioFileFormat format, int expectedBitrate)
    {
        // ============================================================
        // WHY: AAC formats should return their correct bitrates.
        // ============================================================

        // Assert
        format.GetBitrate().Should().Be(expectedBitrate);
    }
}
