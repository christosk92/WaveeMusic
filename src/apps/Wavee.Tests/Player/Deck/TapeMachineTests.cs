using System;
using Wavee.Core;
using Wavee.Features.Player.Deck.Model;
using Xunit;

namespace Wavee.Tests.Player.Deck;

/// <summary>Pure physics for the Cassette/Reel-to-reel decks (docs/plans/wavee/npv-player-styles-implementation.md,
/// Part 3 "Other decks"): pack radii geometry, the constant-linear-speed → 1/r angular speed relationship, the
/// fast-wind window after a seek, and the new-album eject sequence bumping CoverGen exactly once.</summary>
public class TapeMachineTests
{
    static DeckInput Input(
        long nowMs = 0, bool hasTrack = true, bool playWhenReady = true, bool advancing = true, bool buffering = false,
        DeckBoundary boundary = DeckBoundary.None, long? seekTargetMs = null, long positionMs = 0, long durationMs = 180_000)
        => new(nowMs, hasTrack, PlaybackPhase.Playing, playWhenReady, advancing, buffering, false, false,
            boundary, seekTargetMs, null, positionMs, durationMs, false, 33.333f, false);

    [Theory]
    [InlineData(0f, 26f, 13f)]
    [InlineData(0.5f, 19.5f, 19.5f)]
    [InlineData(1f, 13f, 26f)]
    public void CassettePackRadii_AtEachFraction(float p, float rL, float rR)
    {
        var (l, r) = TapeModel.PackRadii(TapeKind.Cassette, p);
        Assert.Equal(rL, l, 3);
        Assert.Equal(rR, r, 3);
    }

    [Theory]
    [InlineData(0f, 92f, 44f)]
    [InlineData(0.5f, 68f, 68f)]
    [InlineData(1f, 44f, 92f)]
    public void ReelPackRadii_AtEachFraction(float p, float rL, float rR)
    {
        var (l, r) = TapeModel.PackRadii(TapeKind.Reel, p);
        Assert.Equal(rL, l, 3);
        Assert.Equal(rR, r, 3);
    }

    [Theory]
    [InlineData(TapeKind.Cassette)]
    [InlineData(TapeKind.Reel)]
    public void Omega_IsInverselyProportionalToRadius(TapeKind kind)
    {
        float small = MathF.Abs(TapeModel.Omega(kind, 10f, 1f, true));
        float big = MathF.Abs(TapeModel.Omega(kind, 20f, 1f, true));
        Assert.Equal(small / 2f, big, 4);
    }

    [Fact]
    public void Omega_IsZero_WhenNotPlaying()
    {
        Assert.Equal(0f, TapeModel.Omega(TapeKind.Cassette, 20f, 4f, false));
    }

    [Fact]
    public void CommittedSeek_FastWindsFor900Ms_ThenSettlesToNormalSpeed()
    {
        var model = new TapeModel(TapeKind.Cassette);
        // Prime the model with one steady tick (no seek) so the seek transition below is "newly non-null".
        model.Tick(Input(nowMs: 0, seekTargetMs: null, positionMs: 0), 1f / 30f);

        // A committed seek lands: SeekTargetMs flips null -> non-null.
        var duringWind = model.Tick(Input(nowMs: 33, seekTargetMs: 60_000, positionMs: 60_000), 1f / 30f);
        // Within the 900 ms window the reels should still be accelerating toward the fast-wind target.
        var stillWinding = model.Tick(Input(nowMs: 500, seekTargetMs: 60_000, positionMs: 60_000), 1f / 30f);
        Assert.True(duringWind.Phase == DeckPhaseName.Winding || stillWinding.Phase == DeckPhaseName.Winding);

        // Past the 900 ms window (seek target held, no further jump) winding must have ended.
        var afterWindow = model.Tick(Input(nowMs: 1_500, seekTargetMs: 60_000, positionMs: 60_000), 1f / 30f);
        Assert.Equal(DeckPhaseName.Playing, afterWindow.Phase);
    }

    [Fact]
    public void NewAlbumEject_BumpsCoverGenExactlyOnce()
    {
        var model = new TapeModel(TapeKind.Reel);
        model.Tick(Input(nowMs: 0, boundary: DeckBoundary.None), 1f / 30f);
        var start = model.Tick(Input(nowMs: 33, boundary: DeckBoundary.NaturalNewAlbum), 1f / 30f);
        int genAtStart = start.CoverGen;

        int lastGen = genAtStart;
        int bumps = 0;
        for (int i = 0; i < 120; i++)
        {
            var f = model.Tick(Input(nowMs: 66 + i * 33, boundary: DeckBoundary.None), 1f / 30f);
            if (f.CoverGen != lastGen) { bumps++; lastGen = f.CoverGen; }
        }
        Assert.Equal(1, bumps);
        Assert.Equal(1f, model.Tick(Input(nowMs: 66 + 120 * 33 + 2000), 0f).Slide, 2);
    }
}
