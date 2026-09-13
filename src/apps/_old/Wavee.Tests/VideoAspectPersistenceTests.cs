using Xunit;

namespace Wavee.Tests;

public sealed class VideoAspectPersistenceTests
{
    [Theory]
    [InlineData(VideoAspectPreference.Fit, "fit")]
    [InlineData(VideoAspectPreference.Crop, "crop")]
    [InlineData(VideoAspectPreference.Stretch, "stretch")]
    [InlineData(VideoAspectPreference.Native, "native")]
    [InlineData(VideoAspectPreference.Custom, "custom")]
    public void ModeTokens_RoundTrip(VideoAspectPreference mode, string token)
    {
        Assert.Equal(token, VideoAspectPersistence.SaveMode(mode));
        Assert.Equal(mode, VideoAspectPersistence.LoadMode(token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("FIT")]
    [InlineData("future-mode")]
    public void MissingOrCorruptMode_FallsBackToFit(string? raw)
        => Assert.Equal(VideoAspectPreference.Fit, VideoAspectPersistence.LoadMode(raw));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(100.0)]
    public void InvalidRatio_FallsBackToSixteenByNine(double raw)
        => Assert.Equal(VideoAspectPersistence.DefaultCustomRatio, VideoAspectPersistence.LoadRatio(raw));

    [Fact]
    public void SettingsStore_RestoresModeAndRatioAcrossInstances()
    {
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.VideoAspectMode, VideoAspectPersistence.SaveMode(VideoAspectPreference.Custom));
        settings.Set(WaveeSettings.VideoCustomAspectRatio, 2.39);

        // A new preference owner after restart sees only the persisted scalars.
        var restoredMode = VideoAspectPersistence.LoadMode(settings.Get(WaveeSettings.VideoAspectMode));
        var restoredRatio = VideoAspectPersistence.LoadRatio(settings.Get(WaveeSettings.VideoCustomAspectRatio));

        Assert.Equal(VideoAspectPreference.Custom, restoredMode);
        Assert.Equal(2.39, restoredRatio);
    }
}
