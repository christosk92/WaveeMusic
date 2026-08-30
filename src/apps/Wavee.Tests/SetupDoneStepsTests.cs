using Wavee.Backend.Audio;
using Xunit;

namespace Wavee.Tests;

public class SetupDoneStepsTests
{
    [Fact]
    public void Compute_ReturnsFourStates()
        => Assert.Equal(4, SetupDoneSteps.Compute(true, true, false, ProvisioningOutcome.Ready).Length);

    [Fact]
    public void Compute_SettingsAndSidebarAreAlwaysDone()
    {
        var steps = SetupDoneSteps.Compute(true, false, false, ProvisioningOutcome.NeverAttempted);
        Assert.Equal(SetupStepState.Done, steps[0]);
        Assert.Equal(SetupStepState.Done, steps[1]);
    }

    [Theory]
    [InlineData(false, false, SetupStepState.Done)]   // no real sync at all -> nothing to wait for
    [InlineData(true, false, SetupStepState.Current)] // real sync, still running
    [InlineData(true, true, SetupStepState.Done)]     // real sync, caught up
    public void Compute_LibraryReflectsSyncIdleness(bool hasRealSync, bool libraryIdle, SetupStepState expected)
    {
        var steps = SetupDoneSteps.Compute(hasRealSync, libraryIdle, runtimeDeclined: false, ProvisioningOutcome.Ready);
        Assert.Equal(expected, steps[2]);
    }

    [Theory]
    [InlineData(ProvisioningOutcome.Ready, SetupStepState.Done)]
    [InlineData(ProvisioningOutcome.NeverAttempted, SetupStepState.Current)]
    [InlineData(ProvisioningOutcome.RuntimeUnavailable, SetupStepState.Failed)]
    [InlineData(ProvisioningOutcome.NoSupportedPack, SetupStepState.Failed)]
    [InlineData(ProvisioningOutcome.PackDownloadFailed, SetupStepState.Failed)]
    [InlineData(ProvisioningOutcome.HashMismatch, SetupStepState.Failed)]
    [InlineData(ProvisioningOutcome.SignatureInvalid, SetupStepState.Failed)]
    [InlineData(ProvisioningOutcome.ArchUnsupported, SetupStepState.Failed)]
    public void Compute_RuntimeReflectsProvisioningOutcome(ProvisioningOutcome outcome, SetupStepState expected)
    {
        var steps = SetupDoneSteps.Compute(hasRealSync: true, libraryIdle: true, runtimeDeclined: false, outcome);
        Assert.Equal(expected, steps[3]);
    }

    [Theory]
    [InlineData(ProvisioningOutcome.Ready)]
    [InlineData(ProvisioningOutcome.NeverAttempted)]
    [InlineData(ProvisioningOutcome.RuntimeUnavailable)]
    [InlineData(ProvisioningOutcome.NoSupportedPack)]
    [InlineData(ProvisioningOutcome.PackDownloadFailed)]
    [InlineData(ProvisioningOutcome.HashMismatch)]
    [InlineData(ProvisioningOutcome.SignatureInvalid)]
    [InlineData(ProvisioningOutcome.ArchUnsupported)]
    public void Compute_DeclinedRuntimeOverridesEveryOutcome(ProvisioningOutcome outcome)
    {
        var steps = SetupDoneSteps.Compute(hasRealSync: true, libraryIdle: true, runtimeDeclined: true, outcome);
        Assert.Equal(SetupStepState.Done, steps[3]);
    }
}
