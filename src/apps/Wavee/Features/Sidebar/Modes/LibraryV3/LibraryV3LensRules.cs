namespace Wavee;

/// <summary>
/// W2 — what the row under the chips says and which controls it carries. Engine-free (System plus the two
/// persisted-code enums in <c>SidebarDesign.cs</c> and <see cref="LibraryV3ChipStrip.RouteFor"/>, itself
/// engine-free) so the row's shape is reviewable and unit-tested without a component, a signal or a frame — the
/// same "pure decision" seam as <see cref="LibraryV3HeaderRules"/> and <see cref="LibraryV3ChipStrip"/>.
///
/// <para>The lens row replaces the old destination word rail's "which page am I on" job AND carries Sort/View,
/// which used to live only in the header's "…" overflow (§W2 of the V3.1 plan). It is hidden while drilled into a
/// folder — the breadcrumb takes its place in the chrome stack for exactly that state.</para>
/// </summary>
static class LibraryV3LensRules
{
    /// <summary>Below this pane width the view-density glyph is dropped; the sort text button always stays (it is
    /// the more load-bearing of the two — Sort by Recents/Alphabetical/etc. changes what you see, View only how it
    /// is laid out).</summary>
    public const float ViewToggleWidth = 200f;

    /// <summary>One rendered position of the lens row, as a pure function of the active filter/qualifier/search/
    /// drill state and the pane's live width.</summary>
    /// <param name="Visible">False while drilled into a folder — the breadcrumb band takes this row's place.</param>
    /// <param name="Filter">The active <c>SidebarV3Filter</c> code, carried through unchanged for the label.</param>
    /// <param name="Qualifier">The active <c>SidebarV3Qualifier</c> code, carried through unchanged for the label.</param>
    /// <param name="Searching">Whether the library-only search box holds a query — the count reads as "N matches"
    /// instead of a bare number, and the label never links to a page while searching (a query is not a lens).</param>
    /// <param name="PageRoute">The facet's library page (<c>albums</c>/<c>artists</c>/<c>podcasts</c>), or null for
    /// All/Playlists (no such page) and for any facet while searching.</param>
    /// <param name="ShowsView">Whether the view-density control has room to show beside the sort control.</param>
    public readonly record struct Shape(bool Visible, int Filter, int Qualifier, bool Searching, string? PageRoute, bool ShowsView);

    public static Shape Resolve(int filter, int qualifier, bool searching, bool drilled, float paneWidth) =>
        new(Visible: !drilled, filter, qualifier, searching,
            // Only Albums/Artists/Podcasts have an actual library page (LibraryV3ChipStrip.RouteFor); a search
            // flattens the lens into a relevance list, so linking to "the page" while one is live would be a lie.
            PageRoute: searching ? null : LibraryV3ChipStrip.RouteFor(filter),
            ShowsView: paneWidth >= ViewToggleWidth);
}
