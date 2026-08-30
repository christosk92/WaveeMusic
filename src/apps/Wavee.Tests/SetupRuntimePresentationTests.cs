using System;
using Xunit;

namespace Wavee.Tests;

public class SetupRuntimePresentationTests
{
    [Theory]
    [InlineData(-1, 100, 0f)]
    [InlineData(0, 100, 0f)]
    [InlineData(50, 100, 0.5f)]
    [InlineData(100, 100, 1f)]
    [InlineData(150, 100, 1f)]
    [InlineData(50, 0, 0f)]
    public void ProgressFraction_ClampsLiveByteCounts(long received, long total, float expected)
        => Assert.Equal(expected, SetupRuntimePresentation.ProgressFraction(received, total));

    [Theory]
    [InlineData("9f31d02ac4a7", "9f31…c4a7")]
    [InlineData("12345678", "12345678")]
    [InlineData("", "")]
    public void ShortHash_PreservesUsefulEnds(string hash, string expected)
        => Assert.Equal(expected, SetupRuntimePresentation.ShortHash(hash));

    // ── Work package D additions: theories over all 8 PlaybackRuntimeSetupModel.Phase values ────────────────────────

    [Theory]
    [InlineData(PlaybackRuntimeSetupModel.Phase.Offer, SetupStepState.Pending, SetupStepState.Pending, SetupStepState.Pending)]
    [InlineData(PlaybackRuntimeSetupModel.Phase.FetchingCatalog, SetupStepState.Current, SetupStepState.Pending, SetupStepState.Pending)]
    [InlineData(PlaybackRuntimeSetupModel.Phase.Downloading, SetupStepState.Current, SetupStepState.Pending, SetupStepState.Pending)]
    [InlineData(PlaybackRuntimeSetupModel.Phase.Verifying, SetupStepState.Done, SetupStepState.Current, SetupStepState.Pending)]
    [InlineData(PlaybackRuntimeSetupModel.Phase.Untrusted, SetupStepState.Done, SetupStepState.Attention, SetupStepState.Pending)]
    [InlineData(PlaybackRuntimeSetupModel.Phase.Ready, SetupStepState.Done, SetupStepState.Done, SetupStepState.Done)]
    [InlineData(PlaybackRuntimeSetupModel.Phase.Failed, SetupStepState.Failed, SetupStepState.Pending, SetupStepState.Pending)]
    [InlineData(PlaybackRuntimeSetupModel.Phase.Advanced, SetupStepState.Pending, SetupStepState.Pending, SetupStepState.Pending)]
    public void StepStates_MatchesTheDownloadVerifyReadyLadder(PlaybackRuntimeSetupModel.Phase phase,
        SetupStepState download, SetupStepState verify, SetupStepState ready)
    {
        var s = SetupRuntimePresentation.StepStates(phase);
        Assert.Equal(download, s.Download);
        Assert.Equal(verify, s.Verify);
        Assert.Equal(ready, s.Ready);
    }

    [Theory]
    [InlineData(PlaybackRuntimeSetupModel.Phase.Offer, true)]
    [InlineData(PlaybackRuntimeSetupModel.Phase.FetchingCatalog, true)]
    [InlineData(PlaybackRuntimeSetupModel.Phase.Downloading, true)]
    [InlineData(PlaybackRuntimeSetupModel.Phase.Verifying, true)]
    [InlineData(PlaybackRuntimeSetupModel.Phase.Untrusted, true)]
    [InlineData(PlaybackRuntimeSetupModel.Phase.Ready, true)]
    [InlineData(PlaybackRuntimeSetupModel.Phase.Failed, false)]
    [InlineData(PlaybackRuntimeSetupModel.Phase.Advanced, false)]
    public void ShowsStepCards_HidesForFailedAndAdvancedOnly(PlaybackRuntimeSetupModel.Phase phase, bool expected)
        => Assert.Equal(expected, SetupRuntimePresentation.ShowsStepCards(phase));

    [Theory]
    [InlineData(PlaybackRuntimeSetupModel.Phase.Offer, true)]
    [InlineData(PlaybackRuntimeSetupModel.Phase.FetchingCatalog, false)]
    [InlineData(PlaybackRuntimeSetupModel.Phase.Downloading, false)]
    [InlineData(PlaybackRuntimeSetupModel.Phase.Verifying, false)]
    [InlineData(PlaybackRuntimeSetupModel.Phase.Untrusted, false)]
    [InlineData(PlaybackRuntimeSetupModel.Phase.Ready, false)]
    [InlineData(PlaybackRuntimeSetupModel.Phase.Failed, true)]
    [InlineData(PlaybackRuntimeSetupModel.Phase.Advanced, false)]
    public void ShowsAdvancedChips_OnlyOfferAndFailed(PlaybackRuntimeSetupModel.Phase phase, bool expected)
        => Assert.Equal(expected, SetupRuntimePresentation.ShowsAdvancedChips(phase));

    [Theory]
    [InlineData(PlaybackRuntimeSetupModel.Phase.Offer, false)]
    [InlineData(PlaybackRuntimeSetupModel.Phase.FetchingCatalog, false)]
    [InlineData(PlaybackRuntimeSetupModel.Phase.Downloading, false)]
    [InlineData(PlaybackRuntimeSetupModel.Phase.Verifying, false)]
    [InlineData(PlaybackRuntimeSetupModel.Phase.Untrusted, false)]
    [InlineData(PlaybackRuntimeSetupModel.Phase.Ready, false)]
    [InlineData(PlaybackRuntimeSetupModel.Phase.Failed, false)]
    [InlineData(PlaybackRuntimeSetupModel.Phase.Advanced, true)]
    public void ShowsLocalSourceChips_OnlyAdvanced(PlaybackRuntimeSetupModel.Phase phase, bool expected)
        => Assert.Equal(expected, SetupRuntimePresentation.ShowsLocalSourceChips(phase));

    [Theory]
    [InlineData(PlaybackRuntimeSetupModel.Phase.Offer, SetupRuntimeStagePanel.Facts)]
    [InlineData(PlaybackRuntimeSetupModel.Phase.FetchingCatalog, SetupRuntimeStagePanel.Progress)]
    [InlineData(PlaybackRuntimeSetupModel.Phase.Downloading, SetupRuntimeStagePanel.Progress)]
    [InlineData(PlaybackRuntimeSetupModel.Phase.Verifying, SetupRuntimeStagePanel.Verify)]
    [InlineData(PlaybackRuntimeSetupModel.Phase.Untrusted, SetupRuntimeStagePanel.Verify)]
    [InlineData(PlaybackRuntimeSetupModel.Phase.Ready, SetupRuntimeStagePanel.Ready)]
    [InlineData(PlaybackRuntimeSetupModel.Phase.Failed, SetupRuntimeStagePanel.Facts)]
    [InlineData(PlaybackRuntimeSetupModel.Phase.Advanced, SetupRuntimeStagePanel.Facts)]
    public void StagePanelFor_MatchesTheStageTable(PlaybackRuntimeSetupModel.Phase phase, SetupRuntimeStagePanel expected)
        => Assert.Equal(expected, SetupRuntimePresentation.StagePanelFor(phase));

    [Fact]
    public void EveryPresentationFunction_IsExhaustiveOverEveryPhase()
    {
        // Total over every real Phase value (not a hand-copied subset) — a phase added later that falls through an
        // unhandled switch arm in production would throw here, not just render wrong in the live app.
        foreach (var phase in Enum.GetValues<PlaybackRuntimeSetupModel.Phase>())
        {
            _ = SetupRuntimePresentation.StepStates(phase);
            _ = SetupRuntimePresentation.ShowsStepCards(phase);
            _ = SetupRuntimePresentation.ShowsAdvancedChips(phase);
            _ = SetupRuntimePresentation.ShowsLocalSourceChips(phase);
            _ = SetupRuntimePresentation.StagePanelFor(phase);
        }
    }
}
