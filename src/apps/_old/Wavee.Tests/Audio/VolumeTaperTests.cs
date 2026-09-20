using Wavee.Backend.Audio;
using Xunit;

namespace Wavee.Tests.Audio;

public class VolumeTaperTests
{
    [Fact]
    public void Amplitude_Endpoints_AreExact()
    {
        Assert.Equal(0f, VolumeTaper.Amplitude(0f));
        Assert.Equal(1f, VolumeTaper.Amplitude(1f));
    }

    [Fact]
    public void Amplitude_IsStrictlyMonotonic()
    {
        float prev = VolumeTaper.Amplitude(0f);
        for (int i = 1; i <= 100; i++)
        {
            float v = i / 100f;
            float amp = VolumeTaper.Amplitude(v);
            Assert.True(amp > prev, $"volume {v} should increase amplitude");
            prev = amp;
        }
    }

    [Fact]
    public void Amplitude_HalfSlider_IsMinus18Db()
    {
        Assert.Equal(0.125f, VolumeTaper.Amplitude(0.5f), precision: 6);
    }

    [Fact]
    public void Amplitude_ClampsOutOfRange()
    {
        Assert.Equal(0f, VolumeTaper.Amplitude(-0.5f));
        Assert.Equal(1f, VolumeTaper.Amplitude(1.5f));
    }

    [Fact]
    public void OsMaster_AppSession_AndTransitionGain_AreIndependentMultipliers()
    {
        // Keyboard/endpoint volume at 50% does not rewrite Wavee's 100% per-app slider.
        Assert.Equal(0.5f, OutputGain.EffectiveAmplitude(0.5f, 1f, 1f), precision: 6);
        // A transition stream at 50% is an additional private scalar; neither visible slider changes.
        Assert.Equal(0.25f, OutputGain.EffectiveAmplitude(0.5f, 1f, 0.5f), precision: 6);
        // Wavee's slider retains its cubic taper independently of the OS master.
        Assert.Equal(0.0625f, OutputGain.EffectiveAmplitude(0.5f, 0.5f, 1f), precision: 6);
    }
}
