using Wavee;
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
