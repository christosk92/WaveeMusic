// ── Wavee.Tests/LibraryLayoutBreakpointTests.cs — the library master-detail collapse rule (Classic's `Rules\Tiers.Library.cs`, T10c1) ────────
//
// A VERBATIM port of 0.2.9's LibraryLayoutBreakpointTests: 640 DIP enters the single-column drill-in, 664 leaves it,
// and an unmeasured width keeps the previous answer.

using Wavee.Themes.Classic;
using Xunit;

namespace Wavee.Tests;

public class LibraryLayoutBreakpointTests
{
    [Fact]
    public void Collapsed_EntersBelowThreshold()
    {
        Assert.False(ClassicLibraryTiers.Collapsed(700f, false));
        Assert.True(ClassicLibraryTiers.Collapsed(600f, false));
        Assert.True(ClassicLibraryTiers.Collapsed(639f, false));
        Assert.False(ClassicLibraryTiers.Collapsed(640f, false));
    }

    [Fact]
    public void Collapsed_HoldsUntilComfortablyWide()
    {
        // Once collapsed, stay collapsed through the hysteresis band (640–664), exit only at ≥ 664.
        Assert.True(ClassicLibraryTiers.Collapsed(650f, true));
        Assert.True(ClassicLibraryTiers.Collapsed(663f, true));
        Assert.False(ClassicLibraryTiers.Collapsed(664f, true));
    }

    [Fact]
    public void Collapsed_UnmeasuredKeepsPrevious()
    {
        Assert.True(ClassicLibraryTiers.Collapsed(0f, true));
        Assert.False(ClassicLibraryTiers.Collapsed(0f, false));
    }
}
