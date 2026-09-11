using Wavee.Core;
using Wavee.Features.Player.Deck.Model;
using Xunit;

namespace Wavee.Tests.Player.Deck;

/// <summary>Pure physics for the Winamp/WMP-style analyser (docs/plans/wavee/npv-player-styles-implementation.md,
/// Part 3 "Other decks"): bands stay in range, silence relaxes to the floor, and peak caps never fall below the band
/// they're capping.</summary>
public class LevelSynthTests
{
    static DeckInput Input(bool playWhenReady, bool advancing)
        => new(0, true, PlaybackPhase.Playing, playWhenReady, advancing, false, false, false,
            DeckBoundary.None, null, null, 90_000, 180_000, false, 33.333f, false);

    [Fact]
    public void Bands_StayWithinZeroToOne_WhilePlaying()
    {
        var model = new LevelModel(19, scope: false, levels: () => (0.6f, 0.9f), seed: 1.234f);
        for (int i = 0; i < 300; i++)
        {
            var f = model.Tick(Input(true, true), 1f / 30f);
            foreach (var b in model.Bands) Assert.InRange(b, 0f, 1f);
            _ = f;
        }
    }

    [Fact]
    public void ScopeBands_StayWithinZeroToOne_WhilePlaying()
    {
        var model = new LevelModel(24, scope: true, levels: () => (0.6f, 0.9f), seed: 4.321f);
        for (int i = 0; i < 300; i++)
        {
            model.Tick(Input(true, true), 1f / 30f);
            foreach (var b in model.Bands) Assert.InRange(b, 0f, 1f);
        }
    }

    [Fact]
    public void Silence_RelaxesToFloor_AfterRelease()
    {
        var model = new LevelModel(19, scope: false, levels: () => (0.6f, 0.9f), seed: 0f);
        for (int i = 0; i < 60; i++) model.Tick(Input(true, true), 1f / 30f);   // get bands moving

        // Now silence, still "playing" per the transport flags but the tap reports nothing driving the signal.
        for (int i = 0; i < 200; i++) model.Tick(Input(false, false), 1f / 30f);   // not playing -> target floor
        foreach (var b in model.Bands) Assert.True(b <= 0.021f, $"band {b} should have relaxed to the floor");
    }

    [Fact]
    public void Peaks_AreNeverBelowTheirBand()
    {
        var model = new LevelModel(19, scope: false, levels: () => (0.6f, 0.9f), seed: 2.5f);
        for (int i = 0; i < 300; i++)
        {
            model.Tick(Input(true, true), 1f / 30f);
            var bands = model.Bands;
            var peaks = model.Peaks;
            for (int b = 0; b < bands.Length; b++) Assert.True(peaks[b] >= bands[b] - 1e-4f, $"peak {peaks[b]} < band {bands[b]} at index {b}");
        }
    }

    [Fact]
    public void NullLevels_StillProducesInRangeBands()
    {
        var model = new LevelModel(19, scope: false, levels: null!, seed: 0f);
        for (int i = 0; i < 120; i++)
        {
            model.Tick(Input(true, true), 1f / 30f);
            foreach (var b in model.Bands) Assert.InRange(b, 0f, 1f);
        }
    }

    [Fact]
    public void IsSettled_OnlyWhenNotPlayingAndAtFloor()
    {
        var model = new LevelModel(19, scope: false, levels: () => (0.6f, 0.9f), seed: 0f);
        model.Tick(Input(true, true), 1f / 30f);
        Assert.False(model.IsSettled);
        for (int i = 0; i < 200; i++) model.Tick(Input(false, false), 1f / 30f);
        Assert.True(model.IsSettled);
    }
}
