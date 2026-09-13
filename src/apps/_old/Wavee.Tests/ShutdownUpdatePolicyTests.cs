using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// "Install a waiting update when I quit Wavee" — the only update-TIMING choice the app actually implements, and the
// one thing standing between a user's setting and a ten-minute blocking download at shutdown. Pure, so the whole
// decision is pinned here rather than inferred from a process that has already exited.
public class ShutdownUpdatePolicyTests
{
    [Theory]
    [InlineData(AppUpdateState.Available)]
    [InlineData(AppUpdateState.Snoozed)]
    public void On_WithAWaitingTarget_Applies(AppUpdateState state)
        => Assert.True(ShutdownUpdatePolicy.ShouldApply(installOnQuit: true, state));

    [Theory]
    [InlineData(AppUpdateState.None)]
    [InlineData(AppUpdateState.Checking)]
    [InlineData(AppUpdateState.Downloading)]
    [InlineData(AppUpdateState.Installing)]
    [InlineData(AppUpdateState.Completed)]
    public void On_WithNothingWaiting_DoesNothing(AppUpdateState state)
        => Assert.False(ShutdownUpdatePolicy.ShouldApply(installOnQuit: true, state));

    [Fact]
    public void AFailedAttempt_IsNeverRetriedSilentlyAtQuit()
    {
        // The user pressed something, was told it failed, and closed the app. A quiet retry that fails again is
        // invisible — and one that succeeds silently reverses a decision the user just watched go wrong.
        Assert.False(ShutdownUpdatePolicy.ShouldApply(installOnQuit: true, AppUpdateState.Failed));
    }

    [Theory]
    [InlineData(AppUpdateState.Available)]
    [InlineData(AppUpdateState.Snoozed)]
    [InlineData(AppUpdateState.Failed)]
    [InlineData(AppUpdateState.None)]
    public void Off_NeverApplies(AppUpdateState state)
        => Assert.False(ShutdownUpdatePolicy.ShouldApply(installOnQuit: false, state));

    [Theory]
    [InlineData(AppUpdateState.Installing, true)]
    [InlineData(AppUpdateState.Failed, true)]
    [InlineData(AppUpdateState.Available, false)]     // the bounded wait gave up; still offered on the next launch
    [InlineData(AppUpdateState.Downloading, false)]
    [InlineData(AppUpdateState.None, false)]
    public void IsSettled_OnlyForTheTwoEndings(AppUpdateState state, bool settled)
        => Assert.Equal(settled, ShutdownUpdatePolicy.IsSettled(state));
}
