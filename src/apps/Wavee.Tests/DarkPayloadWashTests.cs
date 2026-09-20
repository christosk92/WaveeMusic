using FluentGpu.Foundation;
using Xunit;

namespace Wavee.Tests;

public class DarkPayloadWashTests
{
    [Fact]
    public void No_payload_is_the_neutral_dark_base()
        => Assert.Equal(Design.Palette.BackgroundDark(Design.Palette.Neutral), Design.Palette.DarkFromPayload(0));

    [Fact]
    public void A_payload_keeps_its_hue_at_the_neutral_dark_brightness()
    {
        ColorF ground = Design.Palette.BackgroundDark(Design.Palette.Neutral);
        ColorF sunk = Design.Palette.DarkFromPayload(0xFFE0A030);          // a warm yellow
        float groundMax = MathF.Max(ground.R, MathF.Max(ground.G, ground.B));
        float sunkMax = MathF.Max(sunk.R, MathF.Max(sunk.G, sunk.B));
        Assert.InRange(sunkMax, groundMax - 0.002f, groundMax + 0.002f);
        Assert.True(sunk.R > sunk.G && sunk.G > sunk.B);                  // yellow-orange order of channels survives
    }
}
