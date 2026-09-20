// ── Wavee.Tests/BrowsePageLayoutTests.cs — shelves or one flattened grid (ch 13 §8) ─────────────────────────────────
//
// Ported verbatim from 0.2.9 `BrowsePageLayoutTests`. The 0.2.9 record graph (`BrowseSection` carrying its cards and
// categories, `BrowsePageModel`) is replaced by the facts the rule actually reads — `BrowseSectionFacts(Uri, Title,
// Kind, CardCount, CategoryCount, Total)` and `BrowsePageFacts(Uri, Title, Sections)` (Entities/Browse.cs) — so the
// builders below count cards instead of minting them. Every assertion is unchanged.

using System.Linq;
using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>BrowsePageLayout decides whether a browse category page reads as named shelves or as one flattened grid.
/// The rule exists for the page that IS a single untitled bag of cards — that renders as a one-row carousel otherwise,
/// which is silly. These tests pin the shape decision (untitled-or-redundant single shelf, two untitled shelves
/// concat-vs-stacked by whether either has more, three-or-more always Shelves), the empty-section drop, that
/// CategoryGrid/Related never participate in the shelf count, and that an identityless page (no Uri) never flattens
/// because there is no endpoint to page a flattened section against.</summary>
public sealed class BrowsePageLayoutTests
{
    static BrowseSectionFacts Shelf(string title, int cardCount = 3, int? total = null, string uri = "spotify:section:s") =>
        new(uri, title, BrowseSectionKind.Shelf, cardCount, 0, total ?? cardCount);

    static BrowseSectionFacts EmptyShelf(string uri = "spotify:section:empty") =>
        new(uri, null, BrowseSectionKind.Shelf, 0, 0, 0);

    static BrowseSectionFacts Related(int categoryCount = 2, string uri = "spotify:section:related") =>
        new(uri, "Related", BrowseSectionKind.Related, 0, categoryCount, categoryCount);

    static BrowseSectionFacts EmptyRelated(string uri = "spotify:section:related-empty") =>
        new(uri, "Related", BrowseSectionKind.Related, 0, 0, 0);

    static BrowseSectionFacts CategoryGrid(int categoryCount = 2, string uri = "spotify:section:grid") =>
        new(uri, "Grid", BrowseSectionKind.CategoryGrid, 0, categoryCount, categoryCount);

    static BrowsePageFacts Page(string? title, params BrowseSectionFacts[] sections) =>
        new("spotify:page:test", title, sections);

    // ── FlattenOne ────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void OneUntitledShelf_Flattens()
    {
        var page = Page("Jazz", Shelf(title: null!));
        Assert.Equal(BrowsePageLayout.Mode.FlattenOne, BrowsePageLayout.Of(page).Mode);
    }

    [Fact]
    public void OneShelf_TitledSameAsPage_OrdinalIgnoreCase_Flattens()
    {
        var page = Page("Jazz", Shelf(title: "jazz"));
        Assert.Equal(BrowsePageLayout.Mode.FlattenOne, BrowsePageLayout.Of(page).Mode);
    }

    [Fact]
    public void OneShelf_WithWhitespaceOnlyTitle_CountsAsUntitled_Flattens()
    {
        var page = Page("Jazz", Shelf(title: "   "));
        Assert.Equal(BrowsePageLayout.Mode.FlattenOne, BrowsePageLayout.Of(page).Mode);
    }

    [Fact]
    public void OneShelf_TitledDifferentlyFromPage_StaysShelves()
    {
        var page = Page("Charts", Shelf(title: "Weekly"));
        Assert.Equal(BrowsePageLayout.Mode.Shelves, BrowsePageLayout.Of(page).Mode);
    }

    // ── FlattenTwoConcat / FlattenTwoStacked ─────────────────────────────────────────────────────────────────────
    [Fact]
    public void TwoUntitledShelves_NeitherHasMore_FlattenTwoConcat()
    {
        var page = Page("Jazz",
            Shelf(title: null!, uri: "spotify:section:a"),
            Shelf(title: null!, uri: "spotify:section:b"));
        Assert.Equal(BrowsePageLayout.Mode.FlattenTwoConcat, BrowsePageLayout.Of(page).Mode);
    }

    [Fact]
    public void TwoUntitledShelves_OneHasMore_FlattenTwoStacked()
    {
        var page = Page("Jazz",
            Shelf(title: null!, uri: "spotify:section:a"),
            Shelf(title: null!, uri: "spotify:section:b", cardCount: 3, total: 30));
        Assert.Equal(BrowsePageLayout.Mode.FlattenTwoStacked, BrowsePageLayout.Of(page).Mode);
    }

    [Fact]
    public void TwoShelves_OneWithARealDistinctTitle_StaysShelves()
    {
        var page = Page("Jazz",
            Shelf(title: null!, uri: "spotify:section:a"),
            Shelf(title: "Smooth Jazz", uri: "spotify:section:b"));
        Assert.Equal(BrowsePageLayout.Mode.Shelves, BrowsePageLayout.Of(page).Mode);
    }

    [Fact]
    public void ThreeUntitledShelves_StaysShelves()
    {
        var page = Page("Jazz",
            Shelf(title: null!, uri: "spotify:section:a"),
            Shelf(title: null!, uri: "spotify:section:b"),
            Shelf(title: null!, uri: "spotify:section:c"));
        Assert.Equal(BrowsePageLayout.Mode.Shelves, BrowsePageLayout.Of(page).Mode);
    }

    // ── empty-section dropping ───────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void EmptyShelf_IsDropped_LeavingTheSoleUntitledShelfToFlatten()
    {
        var page = Page("Jazz", EmptyShelf(), Shelf(title: null!, uri: "spotify:section:real"));
        var result = BrowsePageLayout.Of(page);
        Assert.Equal(BrowsePageLayout.Mode.FlattenOne, result.Mode);
        Assert.Single(result.Sections);
        Assert.Equal("spotify:section:real", result.Sections[0].Uri);
    }

    [Fact]
    public void EmptyRelated_IsDropped()
    {
        var page = Page("Jazz", EmptyRelated(), Shelf(title: null!, uri: "spotify:section:real"));
        var result = BrowsePageLayout.Of(page);
        Assert.Equal(BrowsePageLayout.Mode.FlattenOne, result.Mode);
        Assert.Single(result.Sections);
    }

    // ── CategoryGrid/Related pass through untouched by the shelf count ───────────────────────────────────────────
    [Fact]
    public void CategoryGridAndRelated_NeverAffectShelfCount_AndPassThroughInOrder()
    {
        var relatedSection = Related();
        var shelfSection = Shelf(title: null!, uri: "spotify:section:solo");
        var page = Page("Jazz", shelfSection, relatedSection);

        var result = BrowsePageLayout.Of(page);

        Assert.Equal(BrowsePageLayout.Mode.FlattenOne, result.Mode);
        Assert.Equal([shelfSection.Uri, relatedSection.Uri], result.Sections.Select(s => s.Uri));
    }

    [Fact]
    public void CategoryGrid_DoesNotCountAsAShelf_EvenAlongsideTwoUntitledShelves()
    {
        var page = Page("Jazz",
            Shelf(title: null!, uri: "spotify:section:a"),
            Shelf(title: null!, uri: "spotify:section:b"),
            CategoryGrid());
        Assert.Equal(BrowsePageLayout.Mode.FlattenTwoConcat, BrowsePageLayout.Of(page).Mode);
    }

    // ── identityless page never flattens ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public void IdentitylessPage_NeverFlattens_EvenWithASingleUntitledShelf()
    {
        // Shaped like the page's own skeleton seed: Uri "" (the field it actually leaves blank while loading; Title is
        // left as " " there, which is itself blank under TitleIsRedundant, but Uri is the gate here).
        var page = new BrowsePageFacts(
            "",
            " ",
            [Shelf(title: " ", uri: "spotify:section:skeleton")]);

        Assert.Equal(BrowsePageLayout.Mode.Shelves, BrowsePageLayout.Of(page).Mode);
    }

    // ── HasMore / TitleIsRedundant direct pins ───────────────────────────────────────────────────────────────────
    [Fact]
    public void HasMore_IsTrue_WhenTotalExceedsCardsCount()
    {
        Assert.True(BrowsePageLayout.HasMore(Shelf(title: null!, cardCount: 3, total: 30)));
        Assert.False(BrowsePageLayout.HasMore(Shelf(title: null!, cardCount: 3, total: 3)));
    }

    [Fact]
    public void TitleIsRedundant_BlankOrEqualIgnoringCaseAndWhitespace_IsTrue()
    {
        Assert.True(BrowsePageLayout.TitleIsRedundant(null, "Jazz"));
        Assert.True(BrowsePageLayout.TitleIsRedundant("", "Jazz"));
        Assert.True(BrowsePageLayout.TitleIsRedundant("   ", "Jazz"));
        Assert.True(BrowsePageLayout.TitleIsRedundant("jazz", "Jazz"));
        Assert.True(BrowsePageLayout.TitleIsRedundant(" Jazz ", "Jazz"));
        Assert.False(BrowsePageLayout.TitleIsRedundant("Weekly", "Charts"));
    }

    // ── 0.3 additions: the section form → rule kind map, and the empty page ──────────────────────────────────────
    [Theory]
    [InlineData(SectionKind.BrowseShelf, BrowseSectionKind.Shelf)]
    [InlineData(SectionKind.BrowseCategoryGrid, BrowseSectionKind.CategoryGrid)]
    [InlineData(SectionKind.BrowseRelated, BrowseSectionKind.Related)]
    [InlineData(SectionKind.Unknown, BrowseSectionKind.Shelf)]        // an unknown form is a shelf, as 0.2.9's mapper decided
    public void KindOf_MapsTheSectionRowsForm(SectionKind form, BrowseSectionKind expected)
        => Assert.Equal(expected, BrowsePageLayout.KindOf(form));

    [Fact]
    public void APageWithNoSectionsAndNoTitle_IsEmpty()
    {
        Assert.True(new BrowsePageFacts("spotify:page:x", null, []).IsEmpty);
        Assert.False(new BrowsePageFacts("spotify:page:x", "Jazz", []).IsEmpty);
        Assert.False(Page(null, Shelf(title: null!)).IsEmpty);
    }
}
