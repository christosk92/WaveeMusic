using Wavee.Core;
using Wavee.Features.Player.Deck.Model;
using Xunit;

namespace Wavee.Tests.Player.Deck;

/// <summary>Pure physics for the CD/MiniDisc deck (docs/plans/wavee/npv-player-styles-implementation.md, Part 3
/// "Other decks"): the constant-linear-velocity spindle target and the sled travel fraction.</summary>
public class DiscMachineTests
{
    static DeckInput Input(long nowMs = 0, bool playWhenReady = true, bool advancing = true, DeckBoundary boundary = DeckBoundary.None, long positionMs = 0)
        => new(nowMs, true, PlaybackPhase.Playing, playWhenReady, advancing, false, false, false,
            boundary, null, null, positionMs, 180_000, false, 33.333f, false);

    [Fact]
    public void ClvTarget_Is3000AtInnerEdge()
    {
        Assert.Equal(3000f, DiscModel.ClvTargetDegPerSec(0f, true));
    }

    [Fact]
    public void ClvTarget_Is1200AtOuterRim()
    {
        Assert.Equal(1200f, DiscModel.ClvTargetDegPerSec(1f, true));
    }

    [Fact]
    public void ClvTarget_IsZero_WhenPaused()
    {
        Assert.Equal(0f, DiscModel.ClvTargetDegPerSec(0.5f, false));
    }

    [Theory]
    [InlineData(0f, 0.20f)]
    [InlineData(0.5f, 0.33f)]
    [InlineData(1f, 0.46f)]
    public void SledFraction_TracksPlayFraction(float p, float expected)
    {
        var model = new DiscModel();
        var frame = model.Tick(Input(positionMs: (long)(p * 180_000)), 1f / 30f);
        Assert.Equal(p, frame.Frac, 3);
        Assert.Equal(expected, frame.Aux0, 2);
    }

    [Fact]
    public void NewAlbumEject_BumpsCoverGenExactlyOnce()
    {
        var model = new DiscModel();
        model.Tick(Input(nowMs: 0), 1f / 30f);
        var start = model.Tick(Input(nowMs: 33, boundary: DeckBoundary.NaturalNewAlbum), 1f / 30f);
        int lastGen = start.CoverGen;
        int bumps = 0;
        for (int i = 0; i < 120; i++)
        {
            var f = model.Tick(Input(nowMs: 66 + i * 33), 1f / 30f);
            if (f.CoverGen != lastGen) { bumps++; lastGen = f.CoverGen; }
        }
        Assert.Equal(1, bumps);
    }
}
