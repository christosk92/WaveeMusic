using FluentGpu.WindowsApi.Network;
using Wavee;
using Xunit;

// The "On metered connections" status line (App/MeteredStatusLine.cs). The rule worth pinning: a failed cost probe is
// SAID ("couldn't read"), never silently rendered as "not metered" — that silence is what hid a broken detector.
public class MeteredStatusLineTests
{
    static NetworkCost Cost(NetworkCostKind kind, bool overLimit = false, bool approaching = false, bool roaming = false)
        => new(kind, overLimit, approaching, roaming);

    // Identity loc: the rendered line is then the joined KEYS, which makes composition assertions exact.
    static string Keys(MeteredStatusLine line) => line.Render(static k => k);

    [Fact]
    public void UnknownCost_SaysItCouldNotRead_NotNotMetered()
    {
        var line = MeteredStatusLine.For(NetworkCost.Unknown, capInEffect: true);
        Assert.Equal(MeteredStatusKind.Unknown, line.Kind);
        Assert.Equal(Strings.Settings.Playback.MeteredStatus.Unknown, Keys(line));
        Assert.NotEqual(Strings.Settings.Playback.MeteredStatus.NotMetered, Keys(line));
    }

    [Fact]
    public void Unrestricted_IsNotMetered_RegardlessOfCap()
    {
        Assert.Equal(MeteredStatusKind.NotMetered, MeteredStatusLine.For(Cost(NetworkCostKind.Unrestricted), capInEffect: true).Kind);
        Assert.Equal(MeteredStatusKind.NotMetered, MeteredStatusLine.For(Cost(NetworkCostKind.Unrestricted), capInEffect: false).Kind);
        Assert.Equal(Strings.Settings.Playback.MeteredStatus.NotMetered,
            Keys(MeteredStatusLine.For(Cost(NetworkCostKind.Unrestricted), capInEffect: true)));
    }

    [Theory]
    [InlineData(NetworkCostKind.Fixed)]
    [InlineData(NetworkCostKind.Variable)]
    public void MeteredKinds_WithCapBiting_SayCapInEffect(NetworkCostKind kind)
    {
        var line = MeteredStatusLine.For(Cost(kind), capInEffect: true);
        Assert.Equal(MeteredStatusKind.Metered, line.Kind);
        Assert.Equal(Strings.Settings.Playback.MeteredStatus.Metered, Keys(line));
    }

    [Theory]
    [InlineData(NetworkCostKind.Fixed)]
    [InlineData(NetworkCostKind.Variable)]
    public void MeteredKinds_WithQualityAlreadyUnderCap_DoNotClaimTheCapBites(NetworkCostKind kind)
    {
        var line = MeteredStatusLine.For(Cost(kind), capInEffect: false);
        Assert.Equal(MeteredStatusKind.MeteredWithinCap, line.Kind);
        Assert.Equal(Strings.Settings.Playback.MeteredStatus.MeteredWithinCap, Keys(line));
    }

    [Fact]
    public void OverLimit_ThenRoaming_AreSuffixedInThatOrder()
    {
        var line = MeteredStatusLine.For(Cost(NetworkCostKind.Variable, overLimit: true, roaming: true), capInEffect: true);
        Assert.True(line.OverDataLimit);
        Assert.True(line.Roaming);
        Assert.Equal(
            Strings.Settings.Playback.MeteredStatus.Metered + MeteredStatusLine.Separator +
            Strings.Settings.Playback.MeteredStatus.OverLimit + MeteredStatusLine.Separator +
            Strings.Settings.Playback.MeteredStatus.Roaming,
            Keys(line));
    }

    [Fact]
    public void SingleSuffixes_RenderAlone()
    {
        Assert.Equal(
            Strings.Settings.Playback.MeteredStatus.Metered + MeteredStatusLine.Separator + Strings.Settings.Playback.MeteredStatus.OverLimit,
            Keys(MeteredStatusLine.For(Cost(NetworkCostKind.Fixed, overLimit: true), capInEffect: true)));
        Assert.Equal(
            Strings.Settings.Playback.MeteredStatus.Metered + MeteredStatusLine.Separator + Strings.Settings.Playback.MeteredStatus.Roaming,
            Keys(MeteredStatusLine.For(Cost(NetworkCostKind.Fixed, roaming: true), capInEffect: true)));
    }

    [Fact]
    public void ApproachingDataLimit_IsNotSurfaced()
    {
        // NLM's "approaching" bit is a hint, not a status the card promises; the line stays the plain headline.
        var line = MeteredStatusLine.For(Cost(NetworkCostKind.Fixed, approaching: true), capInEffect: true);
        Assert.Equal(Strings.Settings.Playback.MeteredStatus.Metered, Keys(line));
    }

    [Fact]
    public void Render_UsesTheLocSeam()
    {
        var line = MeteredStatusLine.For(Cost(NetworkCostKind.Unrestricted), capInEffect: false);
        Assert.Equal("Not metered", line.Render(k => k == Strings.Settings.Playback.MeteredStatus.NotMetered ? "Not metered" : "?"));
    }
}
