using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>The lyrics blur-strength decision: -1 (auto) resolves against the GPU tier, an explicit 0..100 always
/// wins regardless of tier, and 0 is a real "off" — pinned here so LyricsView's scaling and LyricsBlurPolicy stay
/// interchangeable with a settings row that only ever stores an int.</summary>
public class LyricsBlurPolicyTests
{
    [Fact]
    public void Resolve_Auto_OnAStrongGpu_Is100()
        => Assert.Equal(100, LyricsBlurPolicy.Resolve(LyricsBlurPolicy.Auto, weakGpu: false));

    [Fact]
    public void Resolve_Auto_OnAWeakGpu_Is40()
        => Assert.Equal(40, LyricsBlurPolicy.Resolve(LyricsBlurPolicy.Auto, weakGpu: true));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(40)]
    [InlineData(100)]
    public void Resolve_AnExplicitValue_OverridesAuto_OnEitherTier(int explicitValue)
    {
        Assert.Equal(explicitValue, LyricsBlurPolicy.Resolve(explicitValue, weakGpu: false));
        Assert.Equal(explicitValue, LyricsBlurPolicy.Resolve(explicitValue, weakGpu: true));
    }

    [Theory]
    [InlineData(101, 100)]
    [InlineData(1000, 100)]
    [InlineData(-2, 0)]     // anything below -1 is not the auto sentinel, just an out-of-range stored value
    public void Resolve_ClampsAnOutOfRangeStoredValue(int stored, int expected)
        => Assert.Equal(expected, LyricsBlurPolicy.Resolve(stored, weakGpu: false));

    [Theory]
    [InlineData(0, 0f)]
    [InlineData(40, 0.4f)]
    [InlineData(100, 1f)]
    public void Scale_IsTheResolvedStrengthOverAHundred(int strength, float expected)
        => Assert.Equal(expected, LyricsBlurPolicy.Scale(strength), 3);

    [Theory]
    [InlineData(-5, 0f)]
    [InlineData(150, 1f)]
    public void Scale_ClampsAnOutOfRangeStrength(int strength, float expected)
        => Assert.Equal(expected, LyricsBlurPolicy.Scale(strength), 3);

    [Fact]
    public void Enabled_IsFalseOnlyAtZero()
    {
        Assert.False(LyricsBlurPolicy.Enabled(0));
        Assert.True(LyricsBlurPolicy.Enabled(1));
        Assert.True(LyricsBlurPolicy.Enabled(40));
        Assert.True(LyricsBlurPolicy.Enabled(100));
    }
}
