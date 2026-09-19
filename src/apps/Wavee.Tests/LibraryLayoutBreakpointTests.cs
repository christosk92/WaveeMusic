// ── Wavee.Tests/LibraryLayoutBreakpointTests.cs — the library master-detail collapse rule (Entities/User.cs §8) ────────
//
// A VERBATIM port of 0.2.9's LibraryLayoutBreakpointTests: 640 DIP enters the single-column drill-in, 664 leaves it,
// and an unmeasured width keeps the previous answer.

using Xunit;

namespace Wavee.Tests;

public class LibraryLayoutBreakpointTests
{
    [Fact]
    public void Collapsed_EntersBelowThreshold()
    {
        Assert.False(LibraryLayoutBreakpoints.Collapsed(700f, false));
        Assert.True(LibraryLayoutBreakpoints.Collapsed(600f, false));
        Assert.True(LibraryLayoutBreakpoints.Collapsed(639f, false));
        Assert.False(LibraryLayoutBreakpoints.Collapsed(640f, false));
    }

    [Fact]
    public void Collapsed_HoldsUntilComfortablyWide()
    {
        // Once collapsed, stay collapsed through the hysteresis band (640–664), exit only at ≥ 664.
        Assert.True(LibraryLayoutBreakpoints.Collapsed(650f, true));
        Assert.True(LibraryLayoutBreakpoints.Collapsed(663f, true));
        Assert.False(LibraryLayoutBreakpoints.Collapsed(664f, true));
    }

    [Fact]
    public void Collapsed_UnmeasuredKeepsPrevious()
    {
        Assert.True(LibraryLayoutBreakpoints.Collapsed(0f, true));
        Assert.False(LibraryLayoutBreakpoints.Collapsed(0f, false));
    }
}
