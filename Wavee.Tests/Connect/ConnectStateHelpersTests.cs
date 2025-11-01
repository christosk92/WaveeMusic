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

    [Fact]
    public void CreateDeviceInfo_WithCustomVolumeSteps_ShouldPassToCapabilities()
    {
        // WHY: Custom volume steps should be propagated to device capabilities

        // Arrange
        var session = DealerTestHelpers.CreateMockSession();
        const int customVolumeSteps = 100;

        // Act
        var deviceInfo = ConnectStateHelpers.CreateDeviceInfo(session.Config, volumeSteps: customVolumeSteps);

        // Assert
        deviceInfo.Capabilities.VolumeSteps.Should().Be(customVolumeSteps, "custom volume steps applied");
    }

    [Theory]
    [InlineData(DeviceType.Computer)]
    [InlineData(DeviceType.Tablet)]
    [InlineData(DeviceType.Smartphone)]
    [InlineData(DeviceType.Speaker)]
    [InlineData(DeviceType.TV)]
    [InlineData(DeviceType.AVR)]
    [InlineData(DeviceType.STB)]
    [InlineData(DeviceType.AudioDongle)]
    [InlineData(DeviceType.GameConsole)]
    [InlineData(DeviceType.Smartwatch)]
    [InlineData(DeviceType.Chromebook)]
    [InlineData(DeviceType.CarThing)]
    public void CreateDeviceInfo_WithDifferentDeviceTypes_ShouldMapCorrectly(DeviceType deviceType)
    {
        // WHY: All device types should map correctly to protocol device types

        // Arrange
        var config = new SessionConfig
        {
            DeviceId = "test_device",
            DeviceName = "Test Device",
            DeviceType = deviceType
        };

        // Act
        var deviceInfo = ConnectStateHelpers.CreateDeviceInfo(config);

        // Assert
        deviceInfo.DeviceType.Should().Be((global::Wavee.Protocol.Player.DeviceType)(int)deviceType,
            $"device type {deviceType} should map correctly");
    }

    [Fact]
    public void CreateDeviceInfo_WithCustomClientId_ShouldUseProvidedClientId()
    {
        // WHY: Custom client ID should be used when provided in config

        // Arrange
        const string customClientId = "custom-client-id-12345";
        var config = new SessionConfig
        {
            DeviceId = "test_device",
            DeviceName = "Test Device",
            ClientId = customClientId
        };

        // Act
        var deviceInfo = ConnectStateHelpers.CreateDeviceInfo(config);

        // Assert
        deviceInfo.ClientId.Should().Be(customClientId, "custom client ID should be used");
    }

    [Fact]
    public void CreateDeviceInfo_WithNullClientId_ShouldUseDefaultKeymaster()
    {
        // WHY: When no client ID is provided, should use default Keymaster client ID

        // Arrange
        var config = new SessionConfig
        {
            DeviceId = "test_device",
            DeviceName = "Test Device",
            ClientId = null
        };

        // Act
        var deviceInfo = ConnectStateHelpers.CreateDeviceInfo(config);

        // Assert
        deviceInfo.ClientId.Should().NotBeNullOrEmpty("default client ID should be set");
        deviceInfo.ClientId.Should().Be("65b708073fc0480ea92a077233ca87bd", "should use Keymaster client ID");
    }

    [Fact]
    public void CreateDeviceInfo_ShouldFormatDeviceSoftwareVersionCorrectly()
    {
        // WHY: Device software version must be properly formatted for Spotify

        // Arrange
        var session = DealerTestHelpers.CreateMockSession();

        // Act
        var deviceInfo = ConnectStateHelpers.CreateDeviceInfo(session.Config);

        // Assert
        deviceInfo.DeviceSoftwareVersion.Should().StartWith("Wavee/", "version should start with Wavee/");
        deviceInfo.DeviceSoftwareVersion.Should().MatchRegex(@"^Wavee/\d+\.\d+", "version should contain major.minor numbers");
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

    [Fact]
    public void CreateDefaultCapabilities_ShouldDisableUnsupportedFeatures()
    {
        // WHY: Features not implemented should be explicitly disabled

        // Act
        var capabilities = ConnectStateHelpers.CreateDefaultCapabilities();

        // Assert
        capabilities.SupportsRename.Should().BeFalse("rename not currently supported");
        capabilities.NeedsFullPlayerState.Should().BeFalse("does not need full player state");
    }

    [Fact]
    public void CreateDefaultCapabilities_ShouldSetAllRequiredCapabilities()
    {
        // WHY: Comprehensive check that all critical capabilities are properly configured

        // Act
        var capabilities = ConnectStateHelpers.CreateDefaultCapabilities();

        // Assert - Player capabilities
        capabilities.CanBePlayer.Should().BeTrue("must be able to play");
        capabilities.IsControllable.Should().BeTrue("must be controllable");
        capabilities.IsObservable.Should().BeTrue("must be observable");

        // Assert - Connect features
        capabilities.SupportsTransferCommand.Should().BeTrue("must support transfers");
        capabilities.SupportsCommandRequest.Should().BeTrue("must support command requests");
        capabilities.CommandAcks.Should().BeTrue("must acknowledge commands");

        // Assert - Modern features
        capabilities.SupportsPlaylistV2.Should().BeTrue("must support modern playlists");
        capabilities.SupportsGzipPushes.Should().BeTrue("must support compressed messages");
        capabilities.GaiaEqConnectId.Should().BeTrue("must support GAIA EQ connect ID");
        capabilities.SupportsLogout.Should().BeTrue("must support logout");

        // Assert - Volume
        capabilities.VolumeSteps.Should().BeGreaterThan(0, "must have volume steps");

        // Assert - Content types
        capabilities.SupportedTypes.Should().NotBeEmpty("must support at least one content type");
        capabilities.SupportedTypes.Should().HaveCountGreaterOrEqualTo(2, "should support tracks and episodes");
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

    [Fact]
    public void VolumeFromPercentage_WithNegativeValue_ShouldCalculateNegativeVolume()
    {
        // WHY: Document that function doesn't validate input - caller must ensure valid range

        // Act
        var result = ConnectStateHelpers.VolumeFromPercentage(-50);

        // Assert
        result.Should().BeLessThan(0, "negative percentage produces negative volume (no validation)");
    }

    [Fact]
    public void VolumeFromPercentage_AboveOneHundred_ShouldCalculateAboveMaxVolume()
    {
        // WHY: Document that function doesn't clamp to max - caller must ensure valid range

        // Act
        var result = ConnectStateHelpers.VolumeFromPercentage(150);

        // Assert
        result.Should().BeGreaterThan(ConnectStateHelpers.MaxVolume, "percentage above 100 produces value > max (no clamping)");
    }

    [Fact]
    public void VolumeToPercentage_WithNegativeVolume_ShouldCalculateNegativePercentage()
    {
        // WHY: Document that function doesn't validate input - caller must ensure valid range

        // Act
        var result = ConnectStateHelpers.VolumeToPercentage(-1000);

        // Assert
        result.Should().BeLessThan(0, "negative volume produces negative percentage (no validation)");
    }

    [Fact]
    public void VolumeToPercentage_AboveMaxVolume_ShouldCalculateAboveOneHundredPercent()
    {
        // WHY: Document that function doesn't clamp to 100% - caller must ensure valid range

        // Act
        var result = ConnectStateHelpers.VolumeToPercentage(ConnectStateHelpers.MaxVolume + 10000);

        // Assert
        result.Should().BeGreaterThan(100, "volume above max produces percentage > 100% (no clamping)");
    }

    [Fact]
    public void VolumeConversion_AllPercentages_ShouldBeAccurate()
    {
        // WHY: Comprehensive round-trip test for all integer percentages

        // Act & Assert
        for (int percent = 0; percent <= 100; percent++)
        {
            var spotifyVolume = ConnectStateHelpers.VolumeFromPercentage(percent);
            var roundTrip = ConnectStateHelpers.VolumeToPercentage(spotifyVolume);

            // Allow ±1% tolerance due to rounding
            roundTrip.Should().BeInRange(percent - 1, percent + 1,
                $"round-trip for {percent}% should be accurate within ±1%");
        }
    }

    [Theory]
    [InlineData(1, 655)]    // 1% ≈ 655
    [InlineData(10, 6554)]  // 10% ≈ 6554
    [InlineData(33, 21627)] // 33% ≈ 21627
    [InlineData(66, 43253)] // 66% ≈ 43253
    [InlineData(99, 64880)] // 99% ≈ 64880
    public void VolumeFromPercentage_SpecificValues_ShouldCalculateCorrectly(int percentage, int expectedVolume)
    {
        // WHY: Verify specific percentage calculations with known values

        // Act
        var result = ConnectStateHelpers.VolumeFromPercentage(percentage);

        // Assert
        result.Should().BeInRange(expectedVolume - 10, expectedVolume + 10,
            $"{percentage}% should convert to approximately {expectedVolume}");
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

    [Fact]
    public void SpircVersion_ShouldBeSet()
    {
        // WHY: SpIRC version must be included in device info for protocol compatibility

        // Arrange
        var session = DealerTestHelpers.CreateMockSession();

        // Act
        var deviceInfo = ConnectStateHelpers.CreateDeviceInfo(session.Config);

        // Assert
        deviceInfo.SpircVersion.Should().NotBeNullOrEmpty("SpIRC version must be set");
        deviceInfo.SpircVersion.Should().MatchRegex(@"^\d+\.\d+", "version should be in major.minor format");
    }

    [Fact]
    public void KeymasterClientId_ShouldBeValidFormat()
    {
        // WHY: Default Keymaster client ID must be a valid hex string format

        // Arrange
        var config = new SessionConfig
        {
            DeviceId = "test_device",
            DeviceName = "Test Device",
            ClientId = null
        };

        // Act
        var deviceInfo = ConnectStateHelpers.CreateDeviceInfo(config);

        // Assert
        deviceInfo.ClientId.Should().NotBeNullOrEmpty("client ID must be set");
        deviceInfo.ClientId.Should().HaveLength(32, "Keymaster client ID should be 32 characters");
        deviceInfo.ClientId.Should().MatchRegex("^[a-f0-9]{32}$", "should be lowercase hex string");
    }

    #endregion
}
