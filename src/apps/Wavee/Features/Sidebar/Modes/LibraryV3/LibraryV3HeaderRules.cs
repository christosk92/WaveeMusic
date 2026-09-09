namespace Wavee;

/// <summary>
/// W1 — THE one rule for the header row's shape. Priority+: the title is the LAST thing to yield. Everything else
/// in the row (the inline search field, the create "+") folds away first as the pane narrows; the title itself is
/// never squeezed, never ellipsized, and never removed — <see cref="LibraryV3Header"/> gives it <c>Shrink = 0</c>
/// and no <c>Trim</c> on the strength of this file alone.
///
/// <para>Engine-free by design (System only — no <c>Loc</c>/<c>Icons</c>/<c>Tok</c>/<c>Element</c>): the ladder is a
/// pure function of (pane width, search-open, has-text), so it is unit-testable without a component, a signal or a
/// frame, and is the one thing <see cref="LibraryV3Header"/> and <see cref="LibraryV3SearchRules"/> both defer to —
/// neither may recompute its own opinion of "is the field inline" or "does '+' fit".</para>
/// </summary>
static class LibraryV3HeaderRules
{
    /// <summary>At or above this pane width the search field sits INLINE in the header row (transparent, grows
    /// beside the title) — below it, the field is a 32-DIP button that must be opened (or hold text) to expand.</summary>
    public const float InlineSearchWidth = 320f;

    /// <summary>At or above this pane width the "+" create button shows in the header row itself — below it, "+"
    /// folds into the "…" overflow as "New playlist"/"New folder" so the header never runs out of room for the
    /// title.</summary>
    public const float CreateFoldWidth = 240f;

    /// <summary>The header row's shape for one (pane width, search-open, has-text) triple.</summary>
    /// <param name="InlineSearch">The search field is permanently expanded, inline, beside the title.</param>
    /// <param name="SearchTakesRow">The field is NOT inline but is showing anyway (opened, or holding a query) —
    /// on a narrow pane there is no room for both it and the title, so the title hides while this is true.</param>
    /// <param name="ShowsCreate">The "+" create button renders in the row (folds into the overflow otherwise).</param>
    public readonly record struct Shape(bool InlineSearch, bool SearchTakesRow, bool ShowsCreate);

    /// <summary>Resolve the row's shape. The title never yields: it is not a parameter of this function's narrowing
    /// logic at all — only the search field and the create button react to width.</summary>
    public static Shape Resolve(float paneWidth, bool searchOpen, bool hasText)
    {
        bool inline = paneWidth >= InlineSearchWidth;
        // Narrow + (opened or holding text) covers the field over the title; Escape/blur restores the title, and a
        // query typed while wide survives a seam drag past the threshold (it keeps SearchTakesRow true).
        bool searchTakesRow = !inline && (searchOpen || hasText);
        return new Shape(inline, searchTakesRow, paneWidth >= CreateFoldWidth);
    }
}
