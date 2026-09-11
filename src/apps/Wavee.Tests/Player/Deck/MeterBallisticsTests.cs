using System;
using Wavee.Core;
using Wavee.Features.Player.Deck.Model;
using Xunit;

namespace Wavee.Tests.Player.Deck;

/// <summary>Pure physics for the Hi-fi VU deck (docs/plans/wavee/npv-player-styles-implementation.md, Part 3 "Other
/// decks"): the dB→degree mapping and the first-order ballistic lag (PPM fast, VU slow).</summary>
public class MeterBallisticsTests
{
    static DeckInput Input(bool playWhenReady = true, bool advancing = true)
        => new(0, true, PlaybackPhase.Playing, playWhenReady, advancing, false, false, false,
            DeckBoundary.None, null, null, 0, 180_000, false, 33.333f, false);

    [Fact]
    public void RmsToDb_MinusEighteenDbfs_IsZero()
    {
        float rms = MathF.Pow(10f, -18f / 20f);   // linear amplitude equivalent to -18 dBFS
        Assert.Equal(0f, MeterModel.RmsToDb(rms), 2);
    }

    [Fact]
    public void DbToDeg_MapsMinus20To3Db_MonotonicallyIntoRange()
    {
        float prev = float.NegativeInfinity;
        for (float db = -20f; db <= 3f; db += 0.5f)
        {
            float deg = MeterModel.DbToDeg(db);
            Assert.InRange(deg, -48f, 48f);
            Assert.True(deg >= prev, $"deg({db}) = {deg} should be >= previous {prev}");
            prev = deg;
        }
        Assert.True(MeterModel.DbToDeg(3f) > MeterModel.DbToDeg(-20f));
    }

    static DeckFrame RunFor(MeterModel model, float seconds, float dt)
    {
        int steps = (int)Math.Ceiling(seconds / dt);
        DeckFrame f = default;
        for (int i = 0; i < steps; i++) f = model.Tick(Input(), dt);
        return f;
    }

    [Fact]
    public void Ppm_ReachesNinetyFivePercentOfTarget_Within150Ms()
    {
        var model = new MeterModel(ppm: true, levels: () => (1f, 1f));   // loud, constant tap → a fixed non-rest target
        var f = RunFor(model, 0.15f, 1f / 240f);
        float target = MeterModel.DbToDeg(MeterModel.RmsToDb(1f));
        float total = target - (-45f);
        Assert.True(f.Angle0 - (-45f) >= 0.95f * total, $"angle0={f.Angle0} target={target}");
    }

    [Fact]
    public void Vu_ReachesNinetyFivePercentOfTarget_Within1Second()
    {
        var model = new MeterModel(ppm: false, levels: () => (1f, 1f));
        var f = RunFor(model, 1.0f, 1f / 240f);
        float target = MeterModel.DbToDeg(MeterModel.RmsToDb(1f));
        float total = target - (-45f);
        Assert.True(f.Angle0 - (-45f) >= 0.95f * total, $"angle0={f.Angle0} target={target}");
    }

    [Fact]
    public void NotPlaying_SettlesAtRest()
    {
        var model = new MeterModel(ppm: false, levels: () => (1f, 1f));
        DeckFrame f = default;
        for (int i = 0; i < 300; i++) f = model.Tick(Input(playWhenReady: false, advancing: false), 1f / 30f);
        Assert.True(model.IsSettled);
        Assert.Equal(-45f, f.Angle0, 1);
    }
}
