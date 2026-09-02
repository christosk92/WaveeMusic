using Xunit;

namespace Wavee.Tests;

// The Rise Media Player reference metrics, pinned as pure arithmetic: the 762×490 plate clamp, the single 770-DIP
// icon-column breakpoint (no tier ladder any more), the footer's button-width split, and the shell-cover mapping.
public class SetupLayoutTests
{
    [Theory]
    [InlineData(0f, 320f)]      // no viewport known yet → clamps to the floor
    [InlineData(1200f, 762f)]   // plenty of room → the reference width
    [InlineData(900f, 762f)]    // still plenty of room
    [InlineData(800f, 736f)]    // viewport pressure below 826 (762 + 2*32) starts shrinking the plate
    [InlineData(300f, 320f)]    // tiny viewport → the floor
    public void Width_ClampsToTheRisePlate(float viewport, float expected)
        => Assert.Equal(expected, SetupLayout.Width(viewport));

    [Theory]
    [InlineData(0f, 184f)]
    [InlineData(1000f, 490f)]
    [InlineData(500f, 436f)]
    [InlineData(200f, 184f)]
    public void Height_ClampsToTheRisePlate(float viewport, float expected)
        => Assert.Equal(expected, SetupLayout.Height(viewport));

    [Theory]
    [InlineData(769f, false)]
    [InlineData(770f, true)]    // Rise's own AdaptiveTrigger MinWindowWidth
    [InlineData(1200f, true)]
    public void ShowsIcon_AtTheRiseBreakpoint(float viewport, bool expected)
        => Assert.Equal(expected, SetupLayout.ShowsIcon(viewport));

    [Theory]
    [InlineData(true, 210f)]
    [InlineData(false, 0f)]
    public void ProgressColumnFor_CollapsesWithTheIcon(bool large, float expected)
        => Assert.Equal(expected, SetupLayout.ProgressColumnFor(large));

    [Theory]
    [InlineData(762f, true, 246f)]    // (762 - 48 padding - 210 progress - 12 gaps) / 2
    [InlineData(762f, false, 351f)]   // (762 - 48 padding - 0 progress - 12 gaps) / 2
    public void FooterButtonWidth_SplitsTheRemainderEqually(float plateW, bool large, float expected)
        => Assert.Equal(expected, SetupLayout.FooterButtonWidth(plateW, large));

    [Theory]
    [InlineData(true, 1)]    // Dim
    [InlineData(false, 0)]   // None
    public void CoverFor_DimWheneverAShellIsBehind_NoneOtherwise(bool shellBehind, int expected)
        => Assert.Equal((SetupCover)expected, SetupLayout.CoverFor(shellBehind));

    // 490 − 80 footer − 1 separator − 48 padding − 36 title = 325
    [Fact]
    public void BodyLaneHeight_AtTheReferencePlate()
        => Assert.Equal(325f, SetupLayout.BodyLaneHeight(SetupLayout.PlateHeight));

    // The sign-in page must FIT the reference plate with a two-line lead (the 14-px lead wraps once beside the icon
    // column): 40 + 20 + 68 + 20 + 112 + 20 + 32 = 312 ≤ 325. The old page (96-DIP QR, a two-line browser
    // description, the Premium note and the sign-up link stacked as two rows) summed to 388 and was cut off with
    // no scroll affordance. (#53)
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void SignInIdleBody_FitsTheReferenceLane(int leadLines)
        => Assert.True(SetupLayout.SignInIdleBodyHeight(leadLines) <= SetupLayout.BodyLaneHeight(SetupLayout.PlateHeight),
            $"{SetupLayout.SignInIdleBodyHeight(leadLines)} > {SetupLayout.BodyLaneHeight(SetupLayout.PlateHeight)}");

    [Fact]
    public void SignInIdleBody_ThreeLineLeadOverflows_SoTheScrollbarMustShow()
        => Assert.True(SetupLayout.SignInIdleBodyHeight(3) > SetupLayout.BodyLaneHeight(SetupLayout.PlateHeight) - SetupLayout.BodySpacing);
}
