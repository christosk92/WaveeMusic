using System;
using System.Collections.Generic;
using Wavee.Core;

namespace Wavee.Features.Browse;

/// <summary>Decides the SHAPE of a browse category page's body — named shelves, or one uniform grid ("flatten").
/// Pure over BrowsePageModel, no engine types, so the rules are pinned by tests instead of category-hopping.
/// Flatten exists for the page that IS one bag of cards: a lone untitled shelf (or one whose title just repeats
/// the page's) earns a grid, not a carousel. A header earns its keep only when it names a real subset.</summary>
static class BrowsePageLayout
{
    internal enum Mode { Shelves, FlattenOne, FlattenTwoConcat, FlattenTwoStacked }

    /// <summary>Sections with the empties dropped, original order kept — render THIS list, whatever the mode.</summary>
    internal sealed record Result(Mode Mode, IReadOnlyList<BrowseSection> Sections);

    internal static Result Of(BrowsePageModel page)
    {
        var survivors = new List<BrowseSection>(page.Sections.Count);
        foreach (var s in page.Sections)
        {
            bool empty = s.Kind == BrowseSectionKind.Shelf ? s.Cards.Count == 0 : s.Categories.Count == 0;
            if (!empty) survivors.Add(s);
        }

        // Flattening exists to page a SECTION in place — a page with no uri has no endpoint to page against, so it
        // never earns a grid. This also pins the skeleton (BrowsePage.SkeletonPage's Uri is "") to the shelves shape.
        if (string.IsNullOrWhiteSpace(page.Uri))
            return new Result(Mode.Shelves, survivors);

        // CategoryGrid/Related never flatten — they stay link tiles in every mode, so only Shelf sections count here.
        BrowseSection? first = null, second = null;
        int shelfCount = 0;
        foreach (var s in survivors)
        {
            if (s.Kind != BrowseSectionKind.Shelf) continue;
            shelfCount++;
            if (shelfCount == 1) first = s;
            else if (shelfCount == 2) second = s;
        }

        if (shelfCount == 1 && TitleIsRedundant(first!.Title, page.Title))
            return new Result(Mode.FlattenOne, survivors);

        // Two-up flatten is stricter than the one-up rule: it requires both titles genuinely BLANK, not merely
        // redundant-with-the-page — a page titled "Jazz" with two shelves both titled "Jazz" still reads as two
        // distinct groupings worth their headers, even though neither header would surprise anyone.
        if (shelfCount == 2 && IsBlank(first!.Title) && IsBlank(second!.Title))
        {
            var mode = HasMore(first) || HasMore(second) ? Mode.FlattenTwoStacked : Mode.FlattenTwoConcat;
            return new Result(mode, survivors);
        }

        return new Result(Mode.Shelves, survivors);
    }

    internal static bool HasMore(BrowseSection s) => s.Total > s.Cards.Count;

    internal static bool TitleIsRedundant(string? sectionTitle, string? pageTitle)
    {
        var s = sectionTitle?.Trim();
        if (string.IsNullOrEmpty(s)) return true;
        var p = pageTitle?.Trim();
        return string.Equals(s, p, StringComparison.OrdinalIgnoreCase);
    }

    static bool IsBlank(string? title) => string.IsNullOrWhiteSpace(title);
}
