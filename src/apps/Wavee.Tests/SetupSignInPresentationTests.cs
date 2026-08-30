using Xunit;

namespace Wavee.Tests;

// SetupSignInPresentation: the pure stage/decision-column facts the setup wizard's sign-in page (page 2, Work
// package C) reads off SetupSignInPhase instead of re-deriving the split inline. Pinned per phase so a future edit
// to the page's Wide-tier composition can't silently change which phase shows what without a red test.
public class SetupSignInPresentationTests
{
    [Theory]
    [InlineData(SetupSignInPhase.Idle, 1f)]
    [InlineData(SetupSignInPhase.Busy, 0.22f)]
    [InlineData(SetupSignInPhase.Done, 0f)]
    [InlineData(SetupSignInPhase.Failed, 0f)]
    [InlineData(SetupSignInPhase.Expired, 0f)]
    [InlineData(SetupSignInPhase.Premium, 0f)]
    public void PaneOpacity_MatchesPhase(SetupSignInPhase phase, float expected) =>
        Assert.Equal(expected, SetupSignInPresentation.PaneOpacity(phase));

    [Theory]
    [InlineData(SetupSignInPhase.Idle, true)]
    [InlineData(SetupSignInPhase.Busy, false)]
    [InlineData(SetupSignInPhase.Done, false)]
    [InlineData(SetupSignInPhase.Failed, false)]
    [InlineData(SetupSignInPhase.Expired, false)]
    [InlineData(SetupSignInPhase.Premium, false)]
    public void PaneInteractive_IdleOnly(SetupSignInPhase phase, bool expected) =>
        Assert.Equal(expected, SetupSignInPresentation.PaneInteractive(phase));

    [Theory]
    [InlineData(SetupSignInPhase.Idle, true)]
    [InlineData(SetupSignInPhase.Busy, false)]
    [InlineData(SetupSignInPhase.Done, false)]
    [InlineData(SetupSignInPhase.Failed, false)]
    [InlineData(SetupSignInPhase.Expired, false)]
    [InlineData(SetupSignInPhase.Premium, false)]
    public void ShowsOptionCards_IdleOnly(SetupSignInPhase phase, bool expected) =>
        Assert.Equal(expected, SetupSignInPresentation.ShowsOptionCards(phase));

    [Theory]
    [InlineData(SetupSignInPhase.Idle, true)]
    [InlineData(SetupSignInPhase.Busy, true)]
    [InlineData(SetupSignInPhase.Done, false)]
    [InlineData(SetupSignInPhase.Failed, false)]
    [InlineData(SetupSignInPhase.Expired, false)]
    [InlineData(SetupSignInPhase.Premium, false)]
    public void ShowsApproveCard_IdleAndBusyOnly(SetupSignInPhase phase, bool expected) =>
        Assert.Equal(expected, SetupSignInPresentation.ShowsApproveCard(phase));

    [Theory]
    [InlineData(SetupSignInPhase.Idle, SignInStageKind.Pairing)]
    [InlineData(SetupSignInPhase.Busy, SignInStageKind.Pairing)]
    [InlineData(SetupSignInPhase.Done, SignInStageKind.Terminal)]
    [InlineData(SetupSignInPhase.Failed, SignInStageKind.Terminal)]
    [InlineData(SetupSignInPhase.Expired, SignInStageKind.Terminal)]
    [InlineData(SetupSignInPhase.Premium, SignInStageKind.Terminal)]
    public void StageKind_PairingForIdleAndBusy_TerminalOtherwise(SetupSignInPhase phase, SignInStageKind expected) =>
        Assert.Equal(expected, SetupSignInPresentation.StageKind(phase));

    [Fact]
    public void EveryPhase_IsCoveredByEveryFunction()
    {
        // Exhaustiveness: no SetupSignInPhase value falls through to a caller-supplied fallback in any of the six
        // functions — a new phase added to the enum without updating this class would still return SOME defined
        // value here, but a reviewer adding InlineData rows above is what actually catches the missing case.
        foreach (SetupSignInPhase phase in System.Enum.GetValues<SetupSignInPhase>())
        {
            _ = SetupSignInPresentation.PaneOpacity(phase);
            _ = SetupSignInPresentation.PaneInteractive(phase);
            _ = SetupSignInPresentation.ShowsOptionCards(phase);
            _ = SetupSignInPresentation.ShowsApproveCard(phase);
            Assert.True(System.Enum.IsDefined(SetupSignInPresentation.StageKind(phase)));
        }
    }
}
