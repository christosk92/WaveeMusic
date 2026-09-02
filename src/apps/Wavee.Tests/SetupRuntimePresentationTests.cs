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

    // ── PlaybackRuntimeSetupModel.ShowsReadyToast (Features/Shell/PlaybackRuntimeSetupModel.Phase.cs) ────────────────
    // Onboarding redesign: the setup wizard's own LocalPlayback page shows the Ready state in place, so the
    // standalone "Local playback is ready" toast must stay silent while wizard-hosted.

    [Theory]
    [InlineData(false, true)]   // standalone dialog (Settings/banner) — toast fires
    [InlineData(true, false)]   // wizard-hosted (OnWizardExit set) — no toast, the page already shows Ready
    public void ShowsReadyToast_SkipsOnlyWhenWizardHosted(bool wizardHosted, bool expected)
        => Assert.Equal(expected, PlaybackRuntimeSetupModel.ShowsReadyToast(wizardHosted));
}
