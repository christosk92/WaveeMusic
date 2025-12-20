using System.Buffers.Binary;
using FluentAssertions;
using Wavee.Connect.Playback;
using Xunit;

namespace Wavee.Tests.Connect.Playback;

/// <summary>
/// Tests for NormalizationData struct.
/// Validates ReplayGain data parsing and gain factor calculations.
/// </summary>
public class NormalizationDataTests
{
    [Fact]
    public void Parse_ValidBytes_ReturnsCorrectValues()
    {
        // ============================================================
        // WHY: Parsing valid normalization data should extract correct values.
        // ============================================================

        // Arrange - create 16 bytes with known float values (big-endian)
        var data = new byte[16];
        var trackGain = -5.5f;
        var trackPeak = 0.95f;
        var albumGain = -4.2f;
        var albumPeak = 0.98f;

        WriteFloatBigEndian(data, 0, trackGain);
        WriteFloatBigEndian(data, 4, trackPeak);
        WriteFloatBigEndian(data, 8, albumGain);
        WriteFloatBigEndian(data, 12, albumPeak);

        // Act
        var normData = NormalizationData.Parse(data);

        // Assert
        normData.TrackGainDb.Should().BeApproximately(trackGain, 0.001f);
        normData.TrackPeak.Should().BeApproximately(trackPeak, 0.001f);
        normData.AlbumGainDb.Should().BeApproximately(albumGain, 0.001f);
        normData.AlbumPeak.Should().BeApproximately(albumPeak, 0.001f);
    }

    [Fact]
    public void Parse_AllZeros_ReturnsZeroValues()
    {
        // ============================================================
        // WHY: All-zero bytes should parse as zeros.
        // ============================================================

        // Arrange
        var data = new byte[16];

        // Act
        var normData = NormalizationData.Parse(data);

        // Assert
        normData.TrackGainDb.Should().Be(0f);
        normData.TrackPeak.Should().Be(0f);
        normData.AlbumGainDb.Should().Be(0f);
        normData.AlbumPeak.Should().Be(0f);
    }

    [Fact]
    public void Parse_TooShortData_ReturnsDefault()
    {
        // ============================================================
        // WHY: Data shorter than 16 bytes should return default values.
        // ============================================================

        // Arrange
        var shortData = new byte[10];

        // Act
        var normData = NormalizationData.Parse(shortData);

        // Assert
        normData.Should().Be(NormalizationData.Default);
    }

    [Fact]
    public void Parse_EmptyData_ReturnsDefault()
    {
        // ============================================================
        // WHY: Empty data should return default values.
        // ============================================================

        // Act
        var normData = NormalizationData.Parse(Array.Empty<byte>());

        // Assert
        normData.Should().Be(NormalizationData.Default);
    }

    [Fact]
    public void Default_HasZeroGain()
    {
        // ============================================================
        // WHY: Default normalization should have 0 dB gain and peak = 1.
        // ============================================================

        // Assert
        NormalizationData.Default.TrackGainDb.Should().Be(0f);
        NormalizationData.Default.AlbumGainDb.Should().Be(0f);
        NormalizationData.Default.TrackPeak.Should().Be(1f);
        NormalizationData.Default.AlbumPeak.Should().Be(1f);
    }

    [Fact]
    public void FileOffset_IsCorrectValue()
    {
        // ============================================================
        // WHY: Normalization data is located at offset 144 in audio files.
        // ============================================================

        // Assert
        NormalizationData.FileOffset.Should().Be(144);
    }

    [Fact]
    public void Size_Is16Bytes()
    {
        // ============================================================
        // WHY: Normalization data is exactly 16 bytes.
        // ============================================================

        // Assert
        NormalizationData.Size.Should().Be(16);
    }

    [Fact]
    public void GetTrackGainFactor_ZeroGain_ReturnsTargetLevel()
    {
        // ============================================================
        // WHY: Zero dB gain with target -14 should produce specific factor.
        // ============================================================

        // Arrange
        var normData = new NormalizationData(0f, 1f, 0f, 1f);

        // Act
        var factor = normData.GetTrackGainFactor(targetDb: -14f);

        // Assert
        // Factor = 10^((-14 - 0) / 20) = 10^(-0.7) ~ 0.1995
        factor.Should().BeApproximately(0.1995f, 0.001f);
    }

    [Fact]
    public void GetTrackGainFactor_NegativeGain_IncreasesLevel()
    {
        // ============================================================
        // WHY: Negative track gain (quiet track) should result in higher factor.
        // ============================================================

        // Arrange - track is 6dB quieter than reference
        var normData = new NormalizationData(-6f, 1f, 0f, 1f);

        // Act
        var factorDefault = NormalizationData.Default.GetTrackGainFactor(-14f);
        var factorQuiet = normData.GetTrackGainFactor(-14f);

        // Assert
        factorQuiet.Should().BeGreaterThan(factorDefault,
            "Quieter track needs more amplification");
    }

    [Fact]
    public void GetTrackGainFactor_PositiveGain_DecreasesLevel()
    {
        // ============================================================
        // WHY: Positive track gain (loud track) should result in lower factor.
        // ============================================================

        // Arrange - track is 6dB louder than reference
        var normData = new NormalizationData(6f, 1f, 0f, 1f);

        // Act
        var factorDefault = NormalizationData.Default.GetTrackGainFactor(-14f);
        var factorLoud = normData.GetTrackGainFactor(-14f);

        // Assert
        factorLoud.Should().BeLessThan(factorDefault,
            "Louder track needs less amplification");
    }

    [Fact]
    public void GetTrackGainFactor_PreventClipping_LimitsGain()
    {
        // ============================================================
        // WHY: When peak is high, gain should be limited to prevent clipping.
        // ============================================================

        // Arrange - track needs 12dB boost but peak is at 0.9
        var normData = new NormalizationData(-20f, 0.9f, 0f, 1f);

        // Act
        var factor = normData.GetTrackGainFactor(-14f, preventClipping: true);

        // Assert
        // Max gain without clipping = 1/0.9 ~ 1.111
        factor.Should().BeLessThanOrEqualTo(1f / 0.9f + 0.001f,
            "Gain should be limited by peak to prevent clipping");
    }

    [Fact]
    public void GetTrackGainFactor_NoClippingPrevention_AllowsHighGain()
    {
        // ============================================================
        // WHY: With clipping prevention disabled, calculated gain is used.
        // ============================================================

        // Arrange - track needs large boost
        var normData = new NormalizationData(-20f, 0.9f, 0f, 1f);

        // Act
        var factorWithPrevention = normData.GetTrackGainFactor(-14f, preventClipping: true);
        var factorWithoutPrevention = normData.GetTrackGainFactor(-14f, preventClipping: false);

        // Assert
        factorWithoutPrevention.Should().BeGreaterThan(factorWithPrevention,
            "Without clipping prevention, gain is not limited");
    }

    [Fact]
    public void GetAlbumGainFactor_UsesAlbumValues()
    {
        // ============================================================
        // WHY: Album gain should use album-specific values, not track values.
        // ============================================================

        // Arrange - different track and album gain
        var normData = new NormalizationData(-6f, 0.8f, -3f, 0.9f);

        // Act
        var trackFactor = normData.GetTrackGainFactor(-14f);
        var albumFactor = normData.GetAlbumGainFactor(-14f);

        // Assert
        trackFactor.Should().NotBe(albumFactor,
            "Track and album factors should differ when gains differ");
    }

    [Fact]
    public void GetAlbumGainFactor_PreventClipping_UsesAlbumPeak()
    {
        // ============================================================
        // WHY: Album gain clipping prevention should use album peak.
        // ============================================================

        // Arrange - different peaks for track and album
        var normData = new NormalizationData(-20f, 0.5f, -20f, 0.9f);

        // Act
        var albumFactor = normData.GetAlbumGainFactor(-14f, preventClipping: true);

        // Assert
        // Max gain limited by album peak (0.9) not track peak (0.5)
        albumFactor.Should().BeLessThanOrEqualTo(1f / 0.9f + 0.001f);
    }

    [Fact]
    public void ToString_ReturnsFormattedString()
    {
        // ============================================================
        // WHY: ToString should provide a readable representation.
        // ============================================================

        // Arrange
        var normData = new NormalizationData(-5.5f, 0.95f, -4.2f, 0.98f);

        // Act
        var str = normData.ToString();

        // Assert
        str.Should().Contain("Track:");
        str.Should().Contain("Album:");
        str.Should().Contain("dB");
        str.Should().Contain("peak");
    }

    [Fact]
    public void GetTrackGainFactor_ZeroPeak_DoesNotDivideByZero()
    {
        // ============================================================
        // WHY: Zero peak should not cause division by zero.
        // ============================================================

        // Arrange
        var normData = new NormalizationData(-6f, 0f, 0f, 0f);

        // Act - should not throw
        var factor = normData.GetTrackGainFactor(-14f, preventClipping: true);

        // Assert
        factor.Should().BePositive();
    }

    private static void WriteFloatBigEndian(Span<byte> destination, int offset, float value)
    {
        BinaryPrimitives.WriteSingleBigEndian(destination.Slice(offset), value);
    }
}
