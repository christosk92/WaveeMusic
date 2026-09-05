using System;

namespace Wavee;

/// <summary>
/// W1 — the library search host's pure decisions: the Escape ladder, the blur-close rule and the open width
/// arithmetic. Extracted (the "engine-free decision" pattern every sidebar seam follows) so the morph's three most
/// fiddly rules — when Escape clears vs closes, when a blur closes, how wide the open host gets — are reviewable and
/// testable without a component, a signal or a frame.
///
/// <para>Search used to share its row with only the sort/view pill (a single 28-DIP icon-only box) — it now shares
/// the HEADER row instead, alongside the title and the create/overflow/[collapse] button cluster, since the
/// standalone toolbar band was folded away. <see cref="TrailingControlsWidth"/>/<see cref="TitleReserve"/> replace
/// the old sort-pill reservation; both are conservative estimates flagged for a live-visual tuning pass at pane
/// widths 220/280/300/360/480 (docked and drawer) rather than a value measured off a running build.</para>
/// </summary>
static class LibraryV3SearchRules
{
    /// <summary>The closed host's width — the same box the magnifier button always was, so the morph's start/end
    /// frame never jumps on open/close.</summary>
    public const float ClosedWidth = 32f;

    /// <summary>Reserved width for the header's trailing button cluster (create 28 + overflow 28 + collapse 28 +
    /// their 2 internal 4-DIP gaps + the one gap between the search host and the first button) — what
    /// <see cref="OpenWidth"/> must leave clear so the open field's trailing edge never overlaps them. A drawer has
    /// no collapse chevron, so this slightly over-reserves there — a safe direction to be wrong in.</summary>
    public const float TrailingControlsWidth = 96f;

    /// <summary>Approximate natural width of the short, fixed "Your Library" title on the LEADING side of the
    /// search host — the title no longer grows to fill the row (that's the search host's job when Inline), so
    /// <see cref="OpenWidth"/> has to leave room for it explicitly instead of the title yielding on its own.</summary>
    public const float TitleReserve = 96f;

    /// <summary>The header row's own <c>Gap</c> between adjacent children.</summary>
    public const float Gap = 4f;

    /// <summary>At or above this pane width the field is INLINE — always expanded, transparent, sharing the header
    /// row with the title and the full button cluster, because there is room for all of it. Below it the field
    /// collapses to the 32-DIP magnifier and a click morphs it open. Raised from the old toolbar-only threshold
    /// (300) because the row now also carries the title and three header buttons instead of one icon-only pill.</summary>
    public const float InlineWidth = 420f;

    /// <summary>The header row's shape for one (pane width, user opened it, has text) triple.</summary>
    /// <param name="Inline">The field is permanently expanded (wide pane) — no button, no tooltip, no morph.</param>
    /// <param name="Expanded">The field is showing (inline, or opened/holding text on a narrow pane).</param>
    public readonly record struct Layout(bool Inline, bool Expanded);

    /// <summary>Resolve the row's shape. Narrow + text keeps the field open even if the user never "opened" it (a
    /// query typed while wide must survive a seam drag past the threshold); narrow + empty + not opened is the
    /// button.</summary>
    public static Layout Resolve(float paneWidth, bool openedByUser, bool hasText)
    {
        bool inline = paneWidth >= InlineWidth;
        if (inline) return new Layout(true, true);
        bool expanded = openedByUser || hasText;
        return new Layout(false, expanded);
    }

    public enum EscapeAction : byte { None, Clear, Close }

    /// <summary>One Escape = clear the query (the filter is what you want gone first, mirroring the WinUI TextBox
    /// DeleteButton); a SECOND Escape (on an already-empty field) closes it. Never <see cref="EscapeAction.None"/> —
    /// the host is only reachable while open, and an open host always has something to do with Escape.</summary>
    public static EscapeAction OnEscape(string text) => text.Length > 0 ? EscapeAction.Clear : EscapeAction.Close;

    /// <summary>Focus left the editor: an EMPTY field closes (nothing left to keep visible); a field carrying a query
    /// stays open — Spotify keeps an active filter on screen even after the pointer moves to a row.</summary>
    public static bool ClosesOnBlur(string text) => text.Length == 0;

    /// <summary>The open host's width: the header's own content lane (the pane width less its horizontal padding —
    /// W7's <c>LeadInset</c> on the left, <c>ContentLaneEnd</c> on the right) minus the title's reserved space, the
    /// trailing button cluster, and the two gaps between them, so the field's trailing edge lands exactly where the
    /// button cluster's leading edge would otherwise sit. Floored at <see cref="ClosedWidth"/> so a pane narrower
    /// than everything else it shares the row with still yields a host, not a negative width.</summary>
    public static float OpenWidth(float paneWidth, float toolbarPadH)
        => MathF.Max(ClosedWidth, paneWidth - toolbarPadH - TitleReserve - TrailingControlsWidth - Gap * 2f);
}
