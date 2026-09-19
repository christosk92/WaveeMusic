using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// SetupCommands.Project: the fold from the login takeover's ten LoginPhase values down to the setup wizard's six
// SetupSignInPhase facets. Pins every phase (× the AuthStatus/LoginStep values that could plausibly accompany it) to
// the intended facet, per LoginView.cs's own render switch.
public class SetupSignInProjectionTests
{
    [Theory]
    [InlineData(LoginPhase.LoggedOut, SetupSignInPhase.Idle)]
    [InlineData(LoginPhase.SilentResume, SetupSignInPhase.Busy)]
    [InlineData(LoginPhase.RequestingCode, SetupSignInPhase.Busy)]
    [InlineData(LoginPhase.AwaitingBrowser, SetupSignInPhase.Busy)]
    [InlineData(LoginPhase.AwaitingApproval, SetupSignInPhase.Idle)]
    [InlineData(LoginPhase.ChallengeExpired, SetupSignInPhase.Expired)]
    [InlineData(LoginPhase.Finalizing, SetupSignInPhase.Busy)]
    [InlineData(LoginPhase.Authenticated, SetupSignInPhase.Done)]
    [InlineData(LoginPhase.Failed, SetupSignInPhase.Failed)]
    [InlineData(LoginPhase.PremiumRequired, SetupSignInPhase.Premium)]
    public void Project_FoldsEveryPhase_IndependentlyOfStepAndAuth(LoginPhase phase, SetupSignInPhase expected)
    {
        // The fold is a pure function of Phase alone (see SetupCommands.Project's remarks) — pin that Step/AuthStatus
        // never move the result, across every value each can plausibly carry.
        foreach (LoginStep step in System.Enum.GetValues<LoginStep>())
        foreach (AuthStatus auth in System.Enum.GetValues<AuthStatus>())
            Assert.Equal(expected, SetupCommands.Project(phase, step, auth));
    }

    [Fact]
    public void EveryLoginPhase_IsCovered()
    {
        // Exhaustiveness: every LoginPhase value maps to SOME facet (the fold never needs a caller-supplied fallback).
        foreach (LoginPhase phase in System.Enum.GetValues<LoginPhase>())
        {
            var facet = SetupCommands.Project(phase, LoginStep.Connecting, AuthStatus.LoggedOut);
            Assert.True(System.Enum.IsDefined(facet));
        }
    }
}
