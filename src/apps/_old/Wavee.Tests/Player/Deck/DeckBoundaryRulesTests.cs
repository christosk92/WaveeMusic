using Wavee.Core;
using Wavee.Features.Player.Deck.Model;
using Xunit;

namespace Wavee.Tests.Player.Deck;

/// <summary>
/// WHY the medium changed track (docs/plans/wavee/npv-player-styles-implementation.md, Part 3 "Model contracts").
/// None of these four cases is visible in a screenshot — they are the difference between the arm re-cueing the same
/// disc, swapping the whole record, or doing neither — so the decision lives in a pure function and is pinned here.
/// </summary>
public class DeckBoundaryRulesTests
{
    const string TrackA = "spotify:track:aaa", TrackB = "spotify:track:bbb";
    const string AlbumX = "spotify:album:xxx", AlbumY = "spotify:album:yyy";
    const long Dur = 200_000;

    static DeckBoundary Classify(
        string? prevUri, string? prevAlbum, long prevPos, string? uri, string? album, long pos,
        long prevDur = Dur, PlaybackPhase prevPhase = PlaybackPhase.Playing, bool repeatOne = false)
        => DeckBoundaryRules.Classify(prevUri, prevAlbum, prevPos, prevDur, prevPhase, uri, album, pos, repeatOne);

    [Fact]
    public void Constants_PinTheWindows()
    {
        Assert.Equal(1_500L, DeckBoundaryRules.NaturalEndWindowMs);
        Assert.Equal(2_000L, DeckBoundaryRules.RepeatRewindMs);
    }

    // ── the same uri is NOT a track change (except repeat-one) ──────────────────────────────────────────────────

    [Fact]
    public void SameTrack_TickingAlong_IsNone()
        => Assert.Equal(DeckBoundary.None, Classify(TrackA, AlbumX, 30_000, TrackA, AlbumX, 30_033));

    [Fact]
    public void SameTrack_RepeatOne_RewoundFromTheEnd_IsRepeatOne()
        => Assert.Equal(DeckBoundary.RepeatOne,
            Classify(TrackA, AlbumX, Dur - 1_000, TrackA, AlbumX, 500, repeatOne: true));

    [Fact]
    public void SameTrack_RepeatOne_ExactlyAtTheWindowEdge_IsRepeatOne()
        => Assert.Equal(DeckBoundary.RepeatOne,
            Classify(TrackA, AlbumX, Dur - DeckBoundaryRules.NaturalEndWindowMs, TrackA, AlbumX, 0, repeatOne: true));

    [Fact]
    public void SameTrack_RepeatOne_ButRewoundFromMidTrack_IsNone()
        => Assert.Equal(DeckBoundary.None,
            Classify(TrackA, AlbumX, 50_000, TrackA, AlbumX, 500, repeatOne: true));

    [Fact]
    public void SameTrack_RepeatOne_ButLandedPastTheRewindWindow_IsNone()
        => Assert.Equal(DeckBoundary.None,
            Classify(TrackA, AlbumX, Dur - 1_000, TrackA, AlbumX, DeckBoundaryRules.RepeatRewindMs, repeatOne: true));

    [Fact]
    public void SameTrack_RepeatOff_LoopingBackIsStillNone()
        => Assert.Equal(DeckBoundary.None, Classify(TrackA, AlbumX, Dur - 1_000, TrackA, AlbumX, 0));

    [Fact]
    public void SameTrack_RepeatOne_UnknownDuration_IsNone()
        => Assert.Equal(DeckBoundary.None,
            Classify(TrackA, AlbumX, 0, TrackA, AlbumX, 0, prevDur: 0, repeatOne: true));

    // ── a null on either side is "nothing to compare", never a synthesized skip ─────────────────────────────────

    [Fact]
    public void FirstFold_NothingPlayedBefore_IsNone()
        => Assert.Equal(DeckBoundary.None, Classify(null, null, 0, TrackA, AlbumX, 0));

    [Fact]
    public void TrackRemoved_IsNone()
        => Assert.Equal(DeckBoundary.None, Classify(TrackA, AlbumX, 10_000, null, null, 0));

    // ── natural (ran out) vs skip (the user jumped) x same album vs new ─────────────────────────────────────────

    [Fact]
    public void RanOut_SameAlbum_IsNaturalSameAlbum()
        => Assert.Equal(DeckBoundary.NaturalSameAlbum, Classify(TrackA, AlbumX, Dur - 200, TrackB, AlbumX, 0));

    [Fact]
    public void RanOut_ExactlyAtTheWindowEdge_IsNatural()
        => Assert.Equal(DeckBoundary.NaturalSameAlbum,
            Classify(TrackA, AlbumX, Dur - DeckBoundaryRules.NaturalEndWindowMs, TrackB, AlbumX, 0));

    [Fact]
    public void RanOut_NewAlbum_IsNaturalNewAlbum()
        => Assert.Equal(DeckBoundary.NaturalNewAlbum, Classify(TrackA, AlbumX, Dur - 200, TrackB, AlbumY, 0));

    [Fact]
    public void Transitioning_CountsAsNatural_EvenFromMidTrack()
        => Assert.Equal(DeckBoundary.NaturalSameAlbum,
            Classify(TrackA, AlbumX, 10_000, TrackB, AlbumX, 0, prevPhase: PlaybackPhase.Transitioning));

    [Fact]
    public void JumpedMidTrack_SameAlbum_IsSkipSameAlbum()
        => Assert.Equal(DeckBoundary.SkipSameAlbum, Classify(TrackA, AlbumX, 10_000, TrackB, AlbumX, 0));

    [Fact]
    public void JumpedMidTrack_NewAlbum_IsSkipNewAlbum()
        => Assert.Equal(DeckBoundary.SkipNewAlbum, Classify(TrackA, AlbumX, 10_000, TrackB, AlbumY, 0));

    [Fact]
    public void JumpedJustInsideTheWindow_IsStillASkip()
        => Assert.Equal(DeckBoundary.SkipSameAlbum,
            Classify(TrackA, AlbumX, Dur - DeckBoundaryRules.NaturalEndWindowMs - 1, TrackB, AlbumX, 0));

    [Fact]
    public void UnknownDuration_CannotBeNatural_SoItIsASkip()
        => Assert.Equal(DeckBoundary.SkipSameAlbum, Classify(TrackA, AlbumX, 10_000, TrackB, AlbumX, 0, prevDur: 0));

    // ── "same album" needs a real album uri; an unknown one must not glue two records together ──────────────────

    [Fact]
    public void EmptyAlbumUri_IsNeverSameAlbum()
        => Assert.Equal(DeckBoundary.SkipNewAlbum, Classify(TrackA, "", 10_000, TrackB, "", 0));

    [Fact]
    public void NullAlbumUri_IsNeverSameAlbum()
        => Assert.Equal(DeckBoundary.SkipNewAlbum, Classify(TrackA, null, 10_000, TrackB, null, 0));
}
