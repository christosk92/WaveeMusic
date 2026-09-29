// ── Wavee.Tests/HomeUi/WashPickTests.cs — Home/Reveal.cs's shell-wash accent source order ────────────────────────────
//
// Wave 1, owner A5. Payload accent first, then the graded cover, else the fallback — no tier 3.

using Wavee.HomeUi;
using Xunit;

namespace Wavee.Tests.HomeUi;

public sealed class WashPickTests
{
    [Fact]
    public void DaylistAccent_WinsWhenPresent()
        => Assert.Equal(0xFFAABBCCu, WashPick.Pick(0xFFAABBCCu, 0xFF112233u, 0xFF000000u));

    [Fact]
    public void FirstCoverAccent_IsTheSecondTier()
        => Assert.Equal(0xFF112233u, WashPick.Pick(0u, 0xFF112233u, 0xFF000000u));

    [Fact]
    public void Fallback_IsUsedWhenNeitherAccentIsSet()
        => Assert.Equal(0xFF000000u, WashPick.Pick(0u, 0u, 0xFF000000u));

    [Fact]
    public void Fallback_CanItselfBeZero_ThereIsNoTierThree()
        => Assert.Equal(0u, WashPick.Pick(0u, 0u, 0u));
}
