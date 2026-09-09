using Xunit;

namespace Wavee.Tests;

// The bug: a library row whose identity facet is still Unknown (SidebarLibraryEntry.Name == "") used to fall
// straight to SidebarPaneText.ShortUri(...) as its title — a raw "5NdDCZh1OCLkpoXG…" dimmed for however long the
// catalog took to answer (a screen recording showed a block of eight such rows for ~5s after launch). These pin the
// ONE pure decision SidebarPaneSlot.EntryRow now shares with every other "no title yet" row: an authored alias
// always wins, a resolved name renders as text, a cached retention title is the next fallback, and otherwise the row
// is honestly LOADING — never the id.
public sealed class SidebarRowTitlePresentationTests
{
    [Fact]
    public void LabelOverride_AlwaysWins_EvenWhenTheEntityHasResolved()
    {
        var p = SidebarRowTitlePresentation.Resolve("My Alias", "Discover Weekly");
        Assert.Equal(SidebarRowTitle.Text, p.Kind);
        Assert.False(p.IsSkeleton);
        Assert.Equal("My Alias", p.Text);
    }

    [Fact]
    public void ResolvedName_RendersAsText_WhenThereIsNoOverride()
    {
        var p = SidebarRowTitlePresentation.Resolve(null, "Discover Weekly");
        Assert.False(p.IsSkeleton);
        Assert.Equal("Discover Weekly", p.Text);
    }

    [Fact]
    public void EmptyOverride_IsTreatedAsNoOverride()
    {
        // "" is the reducer's normalized "no alias" — never a literal empty title (SidebarItemSpec's own contract).
        var p = SidebarRowTitlePresentation.Resolve("", "Discover Weekly");
        Assert.False(p.IsSkeleton);
        Assert.Equal("Discover Weekly", p.Text);
    }

    [Fact]
    public void NoOverride_NoResolvedName_ButACachedFallback_RendersTheFallback()
    {
        // Missing-entity retention (SidebarItemSpec.FallbackTitle): a prior successful resolution outlives the
        // entity's current absence, so this is still real text, not a skeleton.
        var p = SidebarRowTitlePresentation.Resolve(null, "", "Last Known Title");
        Assert.False(p.IsSkeleton);
        Assert.Equal("Last Known Title", p.Text);
    }

    [Fact]
    public void NothingResolved_IsHonestlyLoading_NeverTheId()
    {
        var p = SidebarRowTitlePresentation.Resolve(null, "", null);
        Assert.True(p.IsSkeleton);
        Assert.Equal(SidebarRowTitle.Skeleton, p.Kind);
    }

    [Fact]
    public void NothingResolved_EmptyFallback_IsAlsoLoading()
    {
        var p = SidebarRowTitlePresentation.Resolve(null, "", "");
        Assert.True(p.IsSkeleton);
    }

    [Fact]
    public void ResolveDefaultsFallbackTitleToNull_ForCallersWithNoRetentionConcept()
    {
        // EntryRow's projected entries have no FallbackTitle parameter at all — the 2-arg overload must behave
        // exactly like passing fallbackTitle: null.
        var withoutArg = SidebarRowTitlePresentation.Resolve(null, "");
        var withNullArg = SidebarRowTitlePresentation.Resolve(null, "", null);
        Assert.Equal(withNullArg, withoutArg);
        Assert.True(withoutArg.IsSkeleton);
    }
}
