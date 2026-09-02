using Xunit;

namespace Wavee.Tests;

// App/EqualizerSettings.cs: the pure parse/clamp/serialize for the persisted 10-band equalizer gain vector, shared
// by the live host's seed (SpotifyLive/Audio/AudioPlaybackStack.cs), the pre-login local host's seed
// (App/Services.cs BuildPreLoginMedia), the Settings tab and PlaybackDsp's live push — so all four can never
// disagree about how a gain string is parsed or clamped.
public class EqualizerSettingsTests
{
    [Fact]
    public void ReadGains_DefaultSettings_AllZero()
    {
        var settings = new MemoryAppSettings();
        var gains = EqualizerSettings.ReadGains(settings);
        Assert.Equal(10, gains.Length);
        Assert.All(gains, g => Assert.Equal(0f, g));
    }

    [Fact]
    public void ReadGains_NullSettings_FallsBackToKeyDefault()
    {
        var gains = EqualizerSettings.ReadGains(null);
        Assert.Equal(10, gains.Length);
        Assert.All(gains, g => Assert.Equal(0f, g));
    }

    [Fact]
    public void ReadGains_ParsesPersistedValues()
    {
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.EqualizerGains, "1,-2,3.5,0,0,0,0,0,0,6");
        var gains = EqualizerSettings.ReadGains(settings);
        Assert.Equal(new[] { 1f, -2f, 3.5f, 0f, 0f, 0f, 0f, 0f, 0f, 6f }, gains);
    }

    [Fact]
    public void ReadGains_ClampsBeyond12Db()
    {
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.EqualizerGains, "20,-20,0,0,0,0,0,0,0,0");
        var gains = EqualizerSettings.ReadGains(settings);
        Assert.Equal(12f, gains[0]);
        Assert.Equal(-12f, gains[1]);
    }

    [Fact]
    public void ReadGains_TooFewEntries_PadsRestWithZero()
    {
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.EqualizerGains, "4,5");
        var gains = EqualizerSettings.ReadGains(settings);
        Assert.Equal(4f, gains[0]);
        Assert.Equal(5f, gains[1]);
        for (int i = 2; i < gains.Length; i++) Assert.Equal(0f, gains[i]);
    }

    [Fact]
    public void ReadGains_GarbageEntry_DefaultsThatBandToZero_NeverThrows()
    {
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.EqualizerGains, "not-a-number,3,,,,,,,,");
        var gains = EqualizerSettings.ReadGains(settings);
        Assert.Equal(0f, gains[0]);   // garbage → flat, not a crash
        Assert.Equal(3f, gains[1]);
    }

    [Fact]
    public void SerializeGains_RoundTripsThroughReadGains()
    {
        var settings = new MemoryAppSettings();
        var written = new float[] { 12f, -12f, 0f, 1.5f, -1.5f, 0f, 0f, 0f, 0f, 0f };
        settings.Set(WaveeSettings.EqualizerGains, EqualizerSettings.SerializeGains(written));

        var read = EqualizerSettings.ReadGains(settings);
        Assert.Equal(written, read);
    }

    [Fact]
    public void SerializeGains_ClampsAndPadsShortVector()
    {
        var serialized = EqualizerSettings.SerializeGains(new float[] { 99f, -99f });
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.EqualizerGains, serialized);
        var gains = EqualizerSettings.ReadGains(settings);
        Assert.Equal(12f, gains[0]);
        Assert.Equal(-12f, gains[1]);
        for (int i = 2; i < gains.Length; i++) Assert.Equal(0f, gains[i]);
    }
}
