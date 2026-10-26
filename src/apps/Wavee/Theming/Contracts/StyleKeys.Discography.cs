// ── Theming/Contracts/StyleKeys.Discography.cs — the discography's style key (themes plan 6.0, T10b2) ──────────────────────────────
//
// The discography (the artist page's inline facet sections and the `disco:` page's grid, with the album drawer) is a STYLE, not a view: its core components
// keep their hooks, memos and decisions and call the templates while they render (see `DiscographyStyle`). They read the style through `ListStyleLookup`.

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class StyleKeys
{
    /// <summary>The discography's templates: the grid metrics, the facet section, the card data and cell, the drawer's frame, panel, bar, slot,
    /// "Show all" cell and track cell (<see cref="DiscographyStyle"/>). Read by <c>Artist.FacetHost</c>, <c>Artist.DiscoGridHost</c> and the components
    /// under it.</summary>
    public static readonly StyleKey<DiscographyStyle> Discography = new("Artist.Discography");
}
