using Xunit;

namespace Wavee.Tests;

// W1 — LibraryV3HeaderRules is THE one rule for the header row's shape (Priority+: the title is the last thing to
// yield). Pinned here, engine-free, across the widths the eyeball pass exercises (180/200/239/240/319/320/460) and
// the searchOpen/hasText axis, so a future header change cannot silently move a threshold without a red test.
public sealed class LibraryV3HeaderRulesTests
{
    [Theory]
    [InlineData(180f, false, false)]
    [InlineData(200f, false, false)]
    [InlineData(239f, false, false)]
    [InlineData(240f, false, false)]
    [InlineData(319f, false, false)]
    [InlineData(180f, true, false)]
    [InlineData(180f, false, true)]
    [InlineData(319f, true, true)]
    public void Resolve_BelowInlineThreshold_SearchIsNotInline(float width, bool searchOpen, bool hasText)
    {
        var shape = LibraryV3HeaderRules.Resolve(width, searchOpen, hasText);
        Assert.False(shape.InlineSearch);
    }

    [Theory]
    [InlineData(320f)]
    [InlineData(460f)]
    public void Resolve_AtOrAboveInlineThreshold_SearchIsInline(float width)
    {
        var shape = LibraryV3HeaderRules.Resolve(width, searchOpen: false, hasText: false);
        Assert.True(shape.InlineSearch);
        // Inline never "takes the row" over the title — it shares the row WITH the title.
        Assert.False(shape.SearchTakesRow);
    }

    [Theory]
    [InlineData(320f)]
    [InlineData(460f)]
    public void Resolve_Inline_NeverTakesTheRow_EvenWhenOpenedOrTyped(float width)
    {
        Assert.False(LibraryV3HeaderRules.Resolve(width, searchOpen: true, hasText: false).SearchTakesRow);
        Assert.False(LibraryV3HeaderRules.Resolve(width, searchOpen: false, hasText: true).SearchTakesRow);
        Assert.False(LibraryV3HeaderRules.Resolve(width, searchOpen: true, hasText: true).SearchTakesRow);
    }

    [Theory]
    [InlineData(180f)]
    [InlineData(200f)]
    [InlineData(239f)]
    [InlineData(240f)]
    [InlineData(319f)]
    public void Resolve_BelowInline_NotOpenedNotTyped_SearchDoesNotTakeTheRow(float width)
    {
        var shape = LibraryV3HeaderRules.Resolve(width, searchOpen: false, hasText: false);
        Assert.False(shape.SearchTakesRow);
    }

    [Theory]
    [InlineData(180f)]
    [InlineData(200f)]
    [InlineData(239f)]
    [InlineData(240f)]
    [InlineData(319f)]
    public void Resolve_BelowInline_OpenedByUser_SearchTakesTheRow(float width)
    {
        var shape = LibraryV3HeaderRules.Resolve(width, searchOpen: true, hasText: false);
        Assert.True(shape.SearchTakesRow);
    }

    [Theory]
    [InlineData(180f)]
    [InlineData(200f)]
    [InlineData(239f)]
    [InlineData(240f)]
    [InlineData(319f)]
    public void Resolve_BelowInline_HasText_SearchTakesTheRow_EvenIfNeverOpened(float width)
    {
        // A query typed while wide must survive a seam drag past the threshold.
        var shape = LibraryV3HeaderRules.Resolve(width, searchOpen: false, hasText: true);
        Assert.True(shape.SearchTakesRow);
    }

    [Theory]
    [InlineData(180f, false, false)]
    [InlineData(200f, false, false)]
    [InlineData(239f, true, false)]
    [InlineData(239f, false, true)]
    public void Resolve_BelowCreateFoldThreshold_CreateIsHidden(float width, bool searchOpen, bool hasText)
    {
        Assert.False(LibraryV3HeaderRules.Resolve(width, searchOpen, hasText).ShowsCreate);
    }

    [Theory]
    [InlineData(240f)]
    [InlineData(319f)]
    [InlineData(320f)]
    [InlineData(460f)]
    public void Resolve_AtOrAboveCreateFoldThreshold_CreateShows(float width)
    {
        Assert.True(LibraryV3HeaderRules.Resolve(width, searchOpen: false, hasText: false).ShowsCreate);
    }

    [Fact]
    public void Resolve_At180_TheTitleNeverYields_NoInlineSearchAndNoCreate()
    {
        // The floor case from the width-budget note: at 180 there is no room for inline search or a visible "+",
        // but the title is not a parameter of this decision at all — it is drawn Shrink=0 regardless of this shape.
        var shape = LibraryV3HeaderRules.Resolve(180f, searchOpen: false, hasText: false);
        Assert.False(shape.InlineSearch);
        Assert.False(shape.ShowsCreate);
    }

    [Fact]
    public void InlineSearchWidth_Is320()
    {
        Assert.Equal(320f, LibraryV3HeaderRules.InlineSearchWidth);
    }

    [Fact]
    public void CreateFoldWidth_Is240()
    {
        Assert.Equal(240f, LibraryV3HeaderRules.CreateFoldWidth);
    }
}
