using System.Linq;
using Wavee.Core.ReleaseNotes;
using Xunit;

namespace Wavee.Tests;

// Which highlights an install shows. The one channel-aware kind is "store" — the "Wavee is now on the Microsoft
// Store" announcement — which a Store-installed build must never see (its button opens the listing that build came
// from). Getting the CAP wrong is the subtle failure: the strip takes three cards, and a hidden store card must free
// its slot for the next highlight rather than silently shrinking the strip.
public class HighlightVisibilityTests
{
    static ReleaseHighlight H(string kind, string id = "h")
        => new() { Id = id, Title = "T", Body = "B", Kind = kind };

    [Fact]
    public void AStoreHighlight_IsHiddenOnAStoreInstall()
        => Assert.False(HighlightVisibility.IsVisible(H("store"), isStoreInstall: true));

    [Fact]
    public void AStoreHighlight_IsVisibleOnAFeedInstall()
        => Assert.True(HighlightVisibility.IsVisible(H("store"), isStoreInstall: false));

    [Theory]
    [InlineData("new")]
    [InlineData("improved")]
    [InlineData("rebuilt")]
    [InlineData("fixed")]
    [InlineData("added")]
    [InlineData("")]
    [InlineData("some-future-kind")]
    public void EveryOtherKind_IsVisibleEverywhere(string kind)
    {
        Assert.True(HighlightVisibility.IsVisible(H(kind), isStoreInstall: false));
        Assert.True(HighlightVisibility.IsVisible(H(kind), isStoreInstall: true));
        Assert.False(HighlightVisibility.IsStore(H(kind)));
    }

    [Theory]
    [InlineData("store")]
    [InlineData("Store")]
    [InlineData("STORE")]
    public void TheStoreKind_IsCaseInsensitive(string kind)
    {
        Assert.True(HighlightVisibility.IsStore(H(kind)));
        Assert.False(HighlightVisibility.IsVisible(H(kind), isStoreInstall: true));
    }

    [Fact]
    public void ANullHighlight_IsNeitherStoreNorVisible()
    {
        Assert.False(HighlightVisibility.IsStore(null));
        Assert.False(HighlightVisibility.IsVisible(null, isStoreInstall: false));
        Assert.False(HighlightVisibility.IsVisible(null, isStoreInstall: true));
    }

    // ── the cap counts what renders ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OnAStoreInstall_AHiddenStoreCard_FreesItsSlot()
    {
        var doc = new[] { H("store", "store"), H("added", "a"), H("fixed", "b"), H("new", "c") };
        var got = HighlightVisibility.SelectVisible(doc, isStoreInstall: true, max: 3);
        Assert.Equal(new[] { "a", "b", "c" }, got.Select(h => h.Id));
    }

    [Fact]
    public void OnAFeedInstall_TheStoreCard_TakesASlotLikeAnyOther()
    {
        var doc = new[] { H("store", "store"), H("added", "a"), H("fixed", "b"), H("new", "c") };
        var got = HighlightVisibility.SelectVisible(doc, isStoreInstall: false, max: 3);
        Assert.Equal(new[] { "store", "a", "b" }, got.Select(h => h.Id));
    }

    [Fact]
    public void AStoreOnlyDocument_RendersNoCardsOnAStoreInstall()
        => Assert.Empty(HighlightVisibility.SelectVisible(new[] { H("store") }, isStoreInstall: true, max: 3));

    [Fact]
    public void SelectVisible_DropsNullElements_AndToleratesNullAndEmptyInput()
    {
        Assert.Empty(HighlightVisibility.SelectVisible(null, isStoreInstall: false, max: 3));
        Assert.Empty(HighlightVisibility.SelectVisible(new ReleaseHighlight?[] { null }, isStoreInstall: false, max: 3));
        Assert.Empty(HighlightVisibility.SelectVisible(new[] { H("added") }, isStoreInstall: false, max: 0));
    }
}
