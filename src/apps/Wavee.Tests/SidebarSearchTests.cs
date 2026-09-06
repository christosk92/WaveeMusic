using Xunit;

namespace Wavee.Tests;

// W8 — SidebarSearch.Find, the HIGHLIGHT range (never membership — Matches above already owns that, folded for
// diacritics). Find is a plain ordinal case-insensitive IndexOf: no fixture beyond bare strings is needed, so this
// file carries no entry builder of its own (contrast SidebarSubtitleRulesTests, which does).
public sealed class SidebarSearchTests
{
    [Fact]
    public void Find_Found_ReturnsTheMatchedRange()
    {
        var (start, length) = SidebarSearch.Find("Savage Garden", "sav");
        Assert.Equal(0, start);
        Assert.Equal(3, length);
    }

    [Fact]
    public void Find_NotFound_ReturnsTheSentinel()
    {
        var (start, length) = SidebarSearch.Find("Savage Garden", "xyz");
        Assert.Equal(-1, start);
        Assert.Equal(0, length);
    }

    [Fact]
    public void Find_EmptyQuery_ReturnsTheSentinel()
    {
        // An empty query matches every row (SidebarSearch.Matches), but there is nothing to PAINT — a zero-length
        // highlight box would be a visible glitch, not a no-op.
        var (start, length) = SidebarSearch.Find("Savage Garden", "");
        Assert.Equal(-1, start);
        Assert.Equal(0, length);
    }

    [Fact]
    public void Find_CaseInsensitive()
    {
        var (start, length) = SidebarSearch.Find("Savage Garden", "GARDEN");
        Assert.Equal(7, start);
        Assert.Equal(6, length);
    }

    [Fact]
    public void Find_ReturnsTheFirstOccurrence()
    {
        var (start, length) = SidebarSearch.Find("banana", "an");
        Assert.Equal(1, start);
        Assert.Equal(2, length);
    }

    [Fact]
    public void Find_NullOrEmptyName_ReturnsTheSentinel()
    {
        Assert.Equal((-1, 0), SidebarSearch.Find(null, "sav"));
        Assert.Equal((-1, 0), SidebarSearch.Find("", "sav"));
    }
}
