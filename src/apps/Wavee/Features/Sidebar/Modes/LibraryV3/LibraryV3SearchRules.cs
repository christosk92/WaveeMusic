namespace Wavee;

/// <summary>
/// W1 — the library search host's pure decisions: the Escape ladder and the blur-close rule. The field's SHAPE
/// (inline vs. a button vs. taking the whole row) is no longer this file's own opinion — it defers entirely to
/// <see cref="LibraryV3HeaderRules"/>, the one rule for the header row (title never yields; everything else folds).
/// Extracted (the "engine-free decision" pattern every sidebar seam follows) so these rules are reviewable and
/// testable without a component, a signal or a frame.
///
/// <para>Search used to reserve width for a title it had to squeeze and a trailing button cluster it had to clear
/// (<c>OpenWidth</c>/<c>TitleReserve</c>/<c>TrailingControlsWidth</c>) — that arithmetic is gone. The open, narrow
/// field now takes the WHOLE row (the title hides via <see cref="LibraryV3HeaderRules.Shape.SearchTakesRow"/>
/// instead of shrinking), so there is nothing left to reserve around.</para>
/// </summary>
static class LibraryV3SearchRules
{
    /// <summary>The closed host's width — the same box the magnifier button always was, so the morph's start/end
    /// frame never jumps on open/close.</summary>
    public const float ClosedWidth = 32f;

    /// <summary>At or above this pane width the field is INLINE — always expanded, transparent, sharing the header
    /// row with the title, because there is room for both. Below it the field collapses to the 32-DIP magnifier
    /// and a click morphs it open, taking the whole row (the title hides). Delegates to
    /// <see cref="LibraryV3HeaderRules.InlineSearchWidth"/> — the header ladder owns this threshold now.</summary>
    public const float InlineWidth = LibraryV3HeaderRules.InlineSearchWidth;

    /// <summary>The header row's shape for one (pane width, user opened it, has text) triple.</summary>
    /// <param name="Inline">The field is permanently expanded (wide pane) — no button, no tooltip, no morph.</param>
    /// <param name="Expanded">The field is showing (inline, or opened/holding text on a narrow pane, where it now
    /// takes the whole row instead of squeezing the title).</param>
    public readonly record struct Layout(bool Inline, bool Expanded);

    /// <summary>Resolve the row's shape from <see cref="LibraryV3HeaderRules.Resolve"/>: <see cref="Layout.Inline"/>
    /// is <c>Shape.InlineSearch</c>; <see cref="Layout.Expanded"/> is inline OR the field taking the row
    /// (<c>Shape.SearchTakesRow</c>) — narrow + text keeps the field open even if the user never "opened" it (a
    /// query typed while wide must survive a seam drag past the threshold); narrow + empty + not opened is the
    /// button.</summary>
    public static Layout Resolve(float paneWidth, bool openedByUser, bool hasText)
    {
        var shape = LibraryV3HeaderRules.Resolve(paneWidth, openedByUser, hasText);
        return new Layout(shape.InlineSearch, shape.InlineSearch || shape.SearchTakesRow);
    }

    public enum EscapeAction : byte { None, Clear, Close }

    /// <summary>One Escape = clear the query (the filter is what you want gone first, mirroring the WinUI TextBox
    /// DeleteButton); a SECOND Escape (on an already-empty field) closes it. Never <see cref="EscapeAction.None"/> —
    /// the host is only reachable while open, and an open host always has something to do with Escape.</summary>
    public static EscapeAction OnEscape(string text) => text.Length > 0 ? EscapeAction.Clear : EscapeAction.Close;

    /// <summary>Focus left the editor: an EMPTY field closes (nothing left to keep visible); a field carrying a query
    /// stays open — Spotify keeps an active filter on screen even after the pointer moves to a row.</summary>
    public static bool ClosesOnBlur(string text) => text.Length == 0;
}
