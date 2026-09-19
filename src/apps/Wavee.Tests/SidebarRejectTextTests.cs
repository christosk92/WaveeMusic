using Wavee;
using Xunit;

namespace Wavee.Tests;

// Every rejection the reducer can return says something (ch 26 §0.3), on the page AND in the pane's options popover —
// both hosts read SidebarRejectText, so the two cannot drift into two vocabularies.
public class SidebarRejectTextTests
{
    [Fact]
    public void EveryReasonButNoneHasASentence()
    {
        foreach (SidebarRejectReason reason in Enum.GetValues<SidebarRejectReason>())
        {
            if (reason == SidebarRejectReason.None) continue;
            Assert.False(string.IsNullOrEmpty(SidebarRejectText.LocKey(reason)), "silent rejection: " + reason);
            Assert.False(string.IsNullOrEmpty(SidebarRejectText.LocKey(reason, topBar: true)), "silent band rejection: " + reason);
        }
    }

    [Fact]
    public void NoneAndAFutureReasonStayQuiet()
    {
        Assert.Null(SidebarRejectText.LocKey(SidebarRejectReason.None));
        Assert.Null(SidebarRejectText.LocKey((SidebarRejectReason)200));
    }

    [Theory]
    [InlineData(SidebarRejectReason.SectionCapReached, "sidebar.topbar.capReached")]
    [InlineData(SidebarRejectReason.DuplicateItem, "sidebar.customizer.topBarDuplicate")]
    [InlineData(SidebarRejectReason.InvalidIcon, "sidebar.customizer.topBarInvalidIcon")]
    [InlineData(SidebarRejectReason.UnknownItem, "sidebar.customizer.topBarUnknownItem")]
    [InlineData(SidebarRejectReason.NoChange, "sidebar.customizer.topBarNoChange")]
    public void TheShortcutBandHasItsOwnWordsForFiveReasons(SidebarRejectReason reason, string key)
    {
        Assert.Equal(key, SidebarRejectText.LocKey(reason, topBar: true));
        Assert.NotEqual(key, SidebarRejectText.LocKey(reason, topBar: false));
    }

    [Fact]
    public void TheBandFallsThroughToTheGeneralVocabularyForEverythingElse()
    {
        Assert.Equal(SidebarRejectText.LocKey(SidebarRejectReason.KindNotDuplicable),
                     SidebarRejectText.LocKey(SidebarRejectReason.KindNotDuplicable, topBar: true));
        Assert.Equal("sidebar.customizer.rejectNesting", SidebarRejectText.LocKey(SidebarRejectReason.NestingTooDeep));
        Assert.Equal("sidebar.customizer.rejectNesting", SidebarRejectText.LocKey(SidebarRejectReason.KindNotNestable));
        Assert.Equal("sidebar.customizer.rejectSectionCap", SidebarRejectText.LocKey(SidebarRejectReason.SectionCapReached));
    }
}
