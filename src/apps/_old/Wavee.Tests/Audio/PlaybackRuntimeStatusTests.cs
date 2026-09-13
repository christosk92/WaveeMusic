using Wavee.Backend.Audio;
using Xunit;

namespace Wavee.Tests.Audio;

public class PlaybackRuntimeStatusTests
{
    [Fact]
    public void Outcome_MapsToUserMessage()
    {
        Assert.Contains("device", AudioFailureText.ToUserMessage(ProvisioningOutcome.ArchUnsupported));
        Assert.Contains("verification", AudioFailureText.ToUserMessage(ProvisioningOutcome.HashMismatch));
    }

    [Fact]
    public void ShowBanner_OnlyWhenActionable()
    {
        var ready = new PlaybackRuntimeStatus(ProvisioningOutcome.Ready);
        var missing = new PlaybackRuntimeStatus(ProvisioningOutcome.RuntimeUnavailable);
        Assert.False(ready.ShowBanner);
        Assert.True(missing.ShowBanner);
    }
}
