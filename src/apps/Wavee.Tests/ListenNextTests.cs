// ── Wavee.Tests/ListenNextTests.cs — the continue hero and up next (podcast plan §5.2, §10) ─────────────────────────
//
// `ListenNext.Pick` is 0d0429a0 `ShowViewModel.UpdateListenNextEpisodes` made order-aware. Inputs are NEWEST FIRST (the
// edge order), so for a serial "forward in the story" is toward index 0 and episode 1 is the last index. The serial
// facts are written in EPISODE NUMBERS (`Serial` / `Numbers` below) because that is how the story reads.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class ListenNextTests
{
    /// <summary>A serial of <paramref name="count"/> episodes, newest first: index i is episode count - i.</summary>
    static float[] Serial(int count, Func<int, float> pctOfNumber)
    {
        var pcts = new float[count];
        for (int i = 0; i < count; i++) pcts[i] = pctOfNumber(count - i);
        return pcts;
    }

    static int[] Numbers(int count, int[] indices) => Array.ConvertAll(indices, i => count - i);

    static (int Resume, int[] UpNext) Pick(float[] pcts, ConsumptionOrder order, int playing = -1, int room = ListenNext.UpNextMax)
    {
        var up = new int[room];
        var (resume, n) = ListenNext.Pick(pcts, order, playing, up);
        return (resume, up[..n]);
    }

    // ── serial ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Serial_ReturningListener_ResumesNine_ThenTenElevenTwelve()
    {
        // The fake seed's show 0 (plan §9): 14 episodes, 1-8 played, 9 at 46 %.
        var pcts = Serial(14, num => num <= 8 ? 1f : num == 9 ? 0.46f : 0f);
        var (resume, up) = Pick(pcts, ConsumptionOrder.Sequential);
        Assert.Equal(9, 14 - resume);
        Assert.Equal(new[] { 10, 11, 12 }, Numbers(14, up));
    }

    [Fact]
    public void Serial_WalksForwardFromTheNewestFinished()
    {
        var pcts = Serial(14, num => num <= 8 ? 1f : 0f);
        var (resume, up) = Pick(pcts, ConsumptionOrder.Sequential);
        Assert.Equal(-1, resume);
        Assert.Equal(new[] { 9, 10, 11 }, Numbers(14, up));
    }

    [Fact]
    public void Serial_NeverListened_SeedsFromEpisodeOne()
    {
        var (resume, up) = Pick(Serial(14, _ => 0f), ConsumptionOrder.Sequential);
        Assert.Equal(-1, resume);
        Assert.Equal(new[] { 1, 2, 3 }, Numbers(14, up));
    }

    [Fact]
    public void Serial_NearCompleteAnchorsTheWalk_LikePlayed()
    {
        var pcts = Serial(5, num => num <= 2 ? 1f : num == 3 ? 0.95f : 0f);
        var (resume, up) = Pick(pcts, ConsumptionOrder.Sequential);
        Assert.Equal(-1, resume);                                    // 0.95 is finished, not a resume
        Assert.Equal(new[] { 4, 5 }, Numbers(5, up));
    }

    [Fact]
    public void Serial_PlayingAnOldEpisode_AfterPeekingAtTheNewest_ContinuesAfterThePlayingOne()
    {
        // Played the finale (14), now playing episode 3: the story continues 4, 5, 6 — the anchor is the OLDER of the
        // resume and the newest finished.
        var pcts = Serial(14, num => num == 14 ? 1f : 0f);
        var (resume, up) = Pick(pcts, ConsumptionOrder.Sequential, playing: 14 - 3);
        Assert.Equal(3, 14 - resume);
        Assert.Equal(new[] { 4, 5, 6 }, Numbers(14, up));
    }

    [Fact]
    public void Serial_SkippedAhead_StillOffersWhatWasSkipped()
    {
        // 1-8 played, 11 in progress: 9 and 10 come before 12, and 11 is the hero, never a card.
        var pcts = Serial(14, num => num <= 8 ? 1f : num == 11 ? 0.3f : 0f);
        var (resume, up) = Pick(pcts, ConsumptionOrder.Sequential);
        Assert.Equal(11, 14 - resume);
        Assert.Equal(new[] { 9, 10, 12 }, Numbers(14, up));
    }

    [Fact]
    public void Serial_NothingAhead_FillsFromTheOldestMissed()
    {
        // Everything played but episode 3: the forward walk from the finale is empty, the fill finds the gap.
        var (resume, up) = Pick(Serial(14, num => num == 3 ? 0f : 1f), ConsumptionOrder.Sequential);
        Assert.Equal(-1, resume);
        Assert.Equal(new[] { 3 }, Numbers(14, up));
    }

    [Fact]
    public void Serial_PlayedTheFinaleFirst_StartsTheStoryFromEpisodeOne()
    {
        var (_, up) = Pick(Serial(14, num => num == 14 ? 1f : 0f), ConsumptionOrder.Sequential);
        Assert.Equal(new[] { 1, 2, 3 }, Numbers(14, up));
    }

    // ── episodic (and every non-serial order) ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Episodic_TakesTheNewestUnplayed_TheResumeExcluded()
    {
        // The prototype's returning episodic show: 0-2 new, 3 at 42 %, 6 at 12 %, the rest played.
        float[] pcts = [0f, 0f, 0f, 0.42f, 1f, 1f, 0.12f, 1f];
        var (resume, up) = Pick(pcts, ConsumptionOrder.Episodic);
        Assert.Equal(3, resume);                                     // the NEWEST in-progress episode
        Assert.Equal(new[] { 0, 1, 2 }, up);
    }

    [Fact]
    public void Episodic_AStartedEpisodeOtherThanTheResumeIsStillUpNext()
    {
        var (resume, up) = Pick([1f, 0.3f, 1f, 0.6f, 0f], ConsumptionOrder.Episodic);
        Assert.Equal(1, resume);
        Assert.Equal(new[] { 3, 4 }, up);
    }

    [Theory]
    [InlineData(ConsumptionOrder.Episodic)]
    [InlineData(ConsumptionOrder.Recent)]
    [InlineData(ConsumptionOrder.Unknown)]
    public void EveryNonSerialOrder_IsNewestFirst(ConsumptionOrder order)
    {
        var (_, up) = Pick([0f, 1f, 0f, 0f, 0f], order);
        Assert.Equal(new[] { 0, 2, 3 }, up);
    }

    // ── the resume ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ConsumptionOrder.Sequential)]
    [InlineData(ConsumptionOrder.Episodic)]
    public void ThePlayingEpisode_WinsTheResume_EvenUnstarted(ConsumptionOrder order)
    {
        var (resume, up) = Pick([0f, 0.5f, 0f, 0f], order, playing: 2);
        Assert.Equal(2, resume);
        Assert.DoesNotContain(2, up);
    }

    [Fact]
    public void ThePlayingEpisode_WinsEvenNearComplete_ButNotOncePlayed()
    {
        Assert.Equal(1, Pick([0f, 0.95f, 0.5f], ConsumptionOrder.Episodic, playing: 1).Resume);
        Assert.Equal(2, Pick([0f, 1f, 0.5f], ConsumptionOrder.Episodic, playing: 1).Resume);    // played: the scan decides
    }

    [Fact]
    public void NinetyPercentOrMore_IsNeverAResume_NorUpNext()
    {
        var (resume, up) = Pick([0f, 0.92f, 0.5f], ConsumptionOrder.Episodic);
        Assert.Equal(2, resume);                                     // the newer 0.92 is skipped
        Assert.Equal(new[] { 0 }, up);
        Assert.True(ListenNext.Finished(ListenNext.NearComplete));
        Assert.False(ListenNext.Finished(0.899f));
    }

    [Fact]
    public void TheResume_IsTheNewestInProgress_NotTheMostProgressed()
    {
        // Unlike Episode.Rules.ResumePick (the most progressed), listen-next resumes where the listener was most recently.
        Assert.Equal(1, Pick([0f, 0.2f, 0.8f], ConsumptionOrder.Episodic).Resume);
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(3)]
    [InlineData(99)]
    public void APlayingIndexOutOfRange_ReadsAsNothingPlaying(int playing)
    {
        var (resume, up) = Pick([0f, 0.4f, 0f], ConsumptionOrder.Episodic, playing);
        Assert.Equal(1, resume);
        Assert.Equal(new[] { 0, 2 }, up);
    }

    // ── edges ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ConsumptionOrder.Sequential)]
    [InlineData(ConsumptionOrder.Episodic)]
    public void Empty_IsNothing(ConsumptionOrder order)
    {
        var (resume, up) = Pick([], order, playing: 0);
        Assert.Equal(-1, resume);
        Assert.Empty(up);
    }

    [Theory]
    [InlineData(ConsumptionOrder.Sequential)]
    [InlineData(ConsumptionOrder.Episodic)]
    public void AllPlayed_IsNothing(ConsumptionOrder order)
    {
        var (resume, up) = Pick([1f, 1f, 1f, 0.99f], order);
        Assert.Equal(-1, resume);
        Assert.Empty(up);
    }

    [Fact]
    public void ASingleEpisode()
    {
        Assert.Equal(new[] { 0 }, Pick([0f], ConsumptionOrder.Sequential).UpNext);
        var inProgress = Pick([0.5f], ConsumptionOrder.Sequential);
        Assert.Equal(0, inProgress.Resume);
        Assert.Empty(inProgress.UpNext);
    }

    [Fact]
    public void UpNext_IsCappedAtThree_AndAtTheCallersSpan()
    {
        Assert.Equal(3, Pick(new float[20], ConsumptionOrder.Episodic, room: 8).UpNext.Length);
        Assert.Equal(new[] { 0 }, Pick(new float[20], ConsumptionOrder.Episodic, room: 1).UpNext);
        Assert.Empty(Pick(new float[20], ConsumptionOrder.Sequential, room: 0).UpNext);
    }
}
