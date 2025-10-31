using FluentAssertions;
using Wavee.Connect;
using Wavee.Core.Session;
using Wavee.Tests.Helpers;
using Xunit;

namespace Wavee.Tests.Connect;

/// <summary>
/// Tests for ConnectStateHelpers - helper methods for building Spotify Connect State protobuf messages.
/// </summary>
/// <remarks>
/// WHY: ConnectStateHelpers builds the protobuf messages that announce device presence to Spotify.
/// Testing ensures correct message structure, capabilities, and value handling.
/// </remarks>
public sealed class ConnectStateHelpersTests
{
    #region DeviceInfo Creation Tests

    [Fact]
    public void CreateDeviceInfo_WithDefaultValues_ShouldSetAllRequiredFields()
    {
        // WHY: DeviceInfo must contain all required fields for Spotify to recognize the device

        // Arrange
        var session = DealerTestHelpers.CreateMockSession();

        // Act
        var deviceInfo = ConnectStateHelpers.CreateDeviceInfo(session.Config);

        // Assert
        deviceInfo.Should().NotBeNull();
        deviceInfo.DeviceId.Should().Be(session.Config.DeviceId, "device ID from config");
        deviceInfo.Name.Should().Be(session.Config.DeviceName, "device name from config");
        deviceInfo.CanPlay.Should().BeTrue("device can play audio");
        deviceInfo.Volume.Should().Be((uint)(ConnectStateHelpers.MaxVolume / 2), "default volume is mid-range");
        deviceInfo.Capabilities.Should().NotBeNull("capabilities must be set");
        deviceInfo.SpircVersion.Should().NotBeNullOrEmpty("SpIRC version must be set");
        deviceInfo.DeviceSoftwareVersion.Should().Contain("Wavee", "software version includes Wavee");
    }

    [Fact]
    public void CreateDeviceInfo_WithCustomVolume_ShouldUseProvidedVolume()
    {
        // WHY: Device should start with configured initial volume

        // Arrange
        var session = DealerTestHelpers.CreateMockSession();
        const int customVolume = 45000;

        // Act
        var deviceInfo = ConnectStateHelpers.CreateDeviceInfo(session.Config, customVolume);

        // Assert
        deviceInfo.Volume.Should().Be((uint)customVolume, "custom volume applied");
    }

    [Fact]
    public void CreateDeviceInfo_ShouldClampVolumeToValidRange()
    {
        // WHY: Volume must always be within 0-65535 range

        // Arrange
        var session = DealerTestHelpers.CreateMockSession();

        // Act & Assert
        var tooLow = ConnectStateHelpers.CreateDeviceInfo(session.Config, -1000);
        tooLow.Volume.Should().Be(0, "negative volume clamped to 0");

        var tooHigh = ConnectStateHelpers.CreateDeviceInfo(session.Config, 100000);
        tooHigh.Volume.Should().Be((uint)ConnectStateHelpers.MaxVolume, "excessive volume clamped to max");
    }

    #endregion

    #region Capabilities Tests

    [Fact]
    public void CreateDefaultCapabilities_ShouldEnableAllConnectFeatures()
    {
        // WHY: Device must advertise all supported Spotify Connect capabilities

        // Act
        var capabilities = ConnectStateHelpers.CreateDefaultCapabilities();

        // Assert
        capabilities.Should().NotBeNull();
        capabilities.CanBePlayer.Should().BeTrue("device can play audio");
        capabilities.IsControllable.Should().BeTrue("device can be controlled remotely");
        capabilities.IsObservable.Should().BeTrue("device state can be observed");
        capabilities.SupportsTransferCommand.Should().BeTrue("supports playback transfer");
        capabilities.SupportsCommandRequest.Should().BeTrue("supports command requests");
        capabilities.SupportsPlaylistV2.Should().BeTrue("supports modern playlist format");
        capabilities.CommandAcks.Should().BeTrue("acknowledges commands");
        capabilities.SupportsGzipPushes.Should().BeTrue("supports compressed messages");
    }

    [Fact]
    public void CreateDefaultCapabilities_ShouldSetVolumeSteps()
    {
        // WHY: Spotify needs to know volume granularity for UI controls

        // Act
        var capabilities = ConnectStateHelpers.CreateDefaultCapabilities();

        // Assert
        capabilities.VolumeSteps.Should().Be(ConnectStateHelpers.DefaultVolumeSteps,
            "volume steps set to default");
    }

    [Fact]
    public void CreateDefaultCapabilities_WithCustomVolumeSteps_ShouldUseProvidedValue()
    {
        // WHY: Some devices may support different volume granularity

        // Act
        var capabilities = ConnectStateHelpers.CreateDefaultCapabilities(volumeSteps: 100);

        // Assert
        capabilities.VolumeSteps.Should().Be(100, "custom volume steps applied");
    }

    [Fact]
    public void CreateDefaultCapabilities_ShouldIncludeSupportedContentTypes()
    {
        // WHY: Spotify needs to know what media types the device can play

        // Act
        var capabilities = ConnectStateHelpers.CreateDefaultCapabilities();

        // Assert
        capabilities.SupportedTypes.Should().Contain("audio/track", "supports track playback");
        capabilities.SupportedTypes.Should().Contain("audio/episode", "supports podcast playback");
    }

    #endregion

    #region PlayerState Tests

    [Fact]
    public void CreateEmptyPlayerState_ShouldSetTimestamp()
    {
        // WHY: PlayerState must have a valid timestamp for synchronization

        // Arrange
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Act
        var playerState = ConnectStateHelpers.CreateEmptyPlayerState();

        // Assert
        var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        playerState.Should().NotBeNull();
        playerState.Timestamp.Should().BeGreaterOrEqualTo(before, "timestamp is recent");
        playerState.Timestamp.Should().BeLessOrEqualTo(after, "timestamp is current");
    }

    #endregion

    #region Volume Conversion Tests

    [Fact]
    public void VolumeFromPercentage_ShouldConvertCorrectly()
    {
        // WHY: UI typically uses 0-100% range, Spotify uses 0-65535

        // Act & Assert
        ConnectStateHelpers.VolumeFromPercentage(0).Should().Be(0, "0% = 0");
        ConnectStateHelpers.VolumeFromPercentage(100).Should().Be(65535, "100% = 65535");
        ConnectStateHelpers.VolumeFromPercentage(50).Should().BeInRange(32667, 32867, "50% ≈ mid-range");
        ConnectStateHelpers.VolumeFromPercentage(25).Should().BeInRange(16283, 16483, "25% ≈ quarter-range");
    }

    [Fact]
    public void VolumeToPercentage_ShouldConvertCorrectly()
    {
        // WHY: Convert Spotify's 0-65535 range back to 0-100%

        // Act & Assert
        ConnectStateHelpers.VolumeToPercentage(0).Should().Be(0, "0 = 0%");
        ConnectStateHelpers.VolumeToPercentage(65535).Should().Be(100, "65535 = 100%");
        ConnectStateHelpers.VolumeToPercentage(32767).Should().BeInRange(49, 51, "mid-range ≈ 50%");
        ConnectStateHelpers.VolumeToPercentage(16383).Should().BeInRange(24, 26, "quarter ≈ 25%");
    }

    [Fact]
    public void VolumeConversion_ShouldBeRoundTripAccurate()
    {
        // WHY: Converting percentage → Spotify → percentage should preserve value

        // Arrange
        var percentages = new[] { 0, 25, 50, 75, 100 };

        // Act & Assert
        foreach (var originalPercent in percentages)
        {
            var spotifyVolume = ConnectStateHelpers.VolumeFromPercentage(originalPercent);
            var roundTripPercent = ConnectStateHelpers.VolumeToPercentage(spotifyVolume);

            roundTripPercent.Should().Be(originalPercent,
                $"round-trip conversion for {originalPercent}% should be accurate");
        }
    }

    #endregion

    #region Constants Tests

    [Fact]
    public void MaxVolume_ShouldBe65535()
    {
        // WHY: Spotify's volume range is 0-65535 (16-bit unsigned int)

        ConnectStateHelpers.MaxVolume.Should().Be(65535, "Spotify uses 16-bit volume range");
    }

    [Fact]
    public void DefaultVolumeSteps_ShouldBe64()
    {
        // WHY: 64 steps provides good granularity for volume control

        ConnectStateHelpers.DefaultVolumeSteps.Should().Be(64, "default volume steps");
    }

    #endregion
}
