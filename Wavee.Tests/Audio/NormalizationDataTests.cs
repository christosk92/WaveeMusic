using System.Buffers.Binary;
using FluentAssertions;
using Wavee.Audio;
using Xunit;

namespace Wavee.Tests.Audio;

/// <summary>
/// Tests for NormalizationData struct.
/// Validates ReplayGain data parsing and gain factor calculations.
/// </summary>
public class NormalizationDataTests
{
    [Fact]
    public void Parse_ValidBytes_ReturnsCorrectValues()
    {
        var data = new byte[16];
        var trackGain = -5.5f;
        var trackPeak = 0.95f;
        var albumGain = -4.2f;
        var albumPeak = 0.98f;

        WriteFloatLittleEndian(data, 0, trackGain);
        WriteFloatLittleEndian(data, 4, trackPeak);
        WriteFloatLittleEndian(data, 8, albumGain);
        WriteFloatLittleEndian(data, 12, albumPeak);

        var normData = NormalizationData.Parse(data);

        normData.TrackGainDb.Should().BeApproximately(trackGain, 0.001f);
        normData.TrackPeak.Should().BeApproximately(trackPeak, 0.001f);
        normData.AlbumGainDb.Should().BeApproximately(albumGain, 0.001f);
        normData.AlbumPeak.Should().BeApproximately(albumPeak, 0.001f);
    }

    [Fact]
    public void Parse_TooShortData_ReturnsDefault()
    {
        var normData = NormalizationData.Parse(new byte[10]);
        normData.Should().Be(NormalizationData.Default);
    }

    [Fact]
    public void Default_HasZeroGain()
    {
        NormalizationData.Default.TrackGainDb.Should().Be(0f);
        NormalizationData.Default.AlbumGainDb.Should().Be(0f);
        NormalizationData.Default.TrackPeak.Should().Be(1f);
        NormalizationData.Default.AlbumPeak.Should().Be(1f);
    }

    [Fact]
    public void FileOffset_IsCorrectValue()
    {
        NormalizationData.FileOffset.Should().Be(144);
    }

    [Fact]
    public void Size_Is16Bytes()
    {
        NormalizationData.Size.Should().Be(16);
    }

    [Fact]
    public void GetTrackGainFactor_NegativeGain_IncreasesLevel()
    {
        var normData = new NormalizationData(-6f, 1f, 0f, 1f);
        var factorDefault = NormalizationData.Default.GetTrackGainFactor(-14f);
        var factorQuiet = normData.GetTrackGainFactor(-14f);
        factorQuiet.Should().BeGreaterThan(factorDefault);
    }

    [Fact]
    public void GetTrackGainFactor_PreventClipping_LimitsGain()
    {
        var normData = new NormalizationData(-20f, 0.9f, 0f, 1f);
        var factor = normData.GetTrackGainFactor(-14f, preventClipping: true);
        factor.Should().BeLessThanOrEqualTo(1f / 0.9f + 0.001f);
    }

    [Fact]
    public void GetTrackGainFactor_ZeroPeak_DoesNotDivideByZero()
    {
        var normData = new NormalizationData(-6f, 0f, 0f, 0f);
        var factor = normData.GetTrackGainFactor(-14f, preventClipping: true);
        factor.Should().BePositive();
    }

    private static void WriteFloatLittleEndian(Span<byte> destination, int offset, float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(destination.Slice(offset), value);
    }
}
