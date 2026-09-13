using System;

namespace Wavee;

/// <summary>
/// W1 — the library search host's pure decisions: the Escape ladder, the blur-close rule and the open width
/// arithmetic. Extracted (the "engine-free decision" pattern every sidebar seam follows) so the morph's three most
/// fiddly rules — when Escape clears vs closes, when a blur closes, how wide the open host gets — are reviewable and
/// testable without a component, a signal or a frame.
/// </summary>
static class LibraryV3SearchRules
{
    /// <summary>The closed host's width (28) — the same box the magnifier button always was, so the morph's start/end
    /// frame never jumps on open/close.</summary>
    public const float ClosedWidth = 32f;

    /// <summary>The sort/view trigger's icon-only box (28) — what <see cref="OpenWidth"/> must leave room for so the
    /// open field never overlaps the pill it shares the toolbar row with.</summary>
    public const float SortIconOnlyWidth = 28f;

    /// <summary>The toolbar's own <c>Gap</c> between the search host and the sort/view trigger.</summary>
    public const float Gap = 4f;

    /// <summary>At or above this pane width the field is INLINE — always expanded, transparent, sharing the toolbar row
    /// with the full sort/view pill — because there is room for both (a 300-DIP pane leaves ~120 DIP of field beside a
    /// labelled pill). Below it the field collapses to the 32-DIP magnifier and a click morphs it open while the sort
    /// pill drops to icon-only. 300 sits above <c>LibraryV3Metrics.SortIconOnlyWidth</c> (280) on purpose: an inline
    /// field never coexists with an icon-only pill, so the row has exactly two shapes, not three.</summary>
    public const float InlineWidth = 300f;

    /// <summary>The toolbar row's shape for one (pane width, user opened it, has text) triple.</summary>
    /// <param name="Inline">The field is permanently expanded (wide pane) — no button, no tooltip, no morph.</param>
    /// <param name="Expanded">The field is showing (inline, or opened/holding text on a narrow pane).</param>
    /// <param name="SortIconOnly">The sort/view pill shows only its glyph.</param>
    public readonly record struct Layout(bool Inline, bool Expanded, bool SortIconOnly);

    /// <summary>Resolve the row's shape. Narrow + text keeps the field open even if the user never "opened" it (a query
    /// typed while wide must survive a seam drag past the threshold); narrow + empty + not opened is the button.</summary>
    public static Layout Resolve(float paneWidth, bool openedByUser, bool hasText)
    {
        bool inline = paneWidth >= InlineWidth;
        if (inline) return new Layout(true, true, SortIconOnly: false);
        bool expanded = openedByUser || hasText;
        return new Layout(false, expanded, SortIconOnly: expanded || paneWidth < 280f);
    }

    public enum EscapeAction : byte { None, Clear, Close }

    /// <summary>One Escape = clear the query (the filter is what you want gone first, mirroring the WinUI TextBox
    /// DeleteButton); a SECOND Escape (on an already-empty field) closes it. Never <see cref="EscapeAction.None"/> —
    /// the host is only reachable while open, and an open host always has something to do with Escape.</summary>
    public static EscapeAction OnEscape(string text) => text.Length > 0 ? EscapeAction.Clear : EscapeAction.Close;

    /// <summary>Focus left the editor: an EMPTY field closes (nothing left to keep visible); a field carrying a query
    /// stays open — Spotify keeps an active filter on screen even after the pointer moves to a row.</summary>
    public static bool ClosesOnBlur(string text) => text.Length == 0;

    /// <summary>The open host's width: the toolbar's own content lane (the pane width less its horizontal padding —
    /// W7's <c>LeadInset</c> on the left, <c>ContentLaneEnd</c> on the right) minus the icon-only sort pill and the one
    /// gap between them, so the field's trailing edge lands exactly where the pill's leading edge would otherwise sit.
    /// Floored at <see cref="ClosedWidth"/> so a pane narrower than the pill+gap still yields a host, not a negative
    /// width.</summary>
    public static float OpenWidth(float paneWidth, float toolbarPadH)
        => MathF.Max(ClosedWidth, paneWidth - toolbarPadH - SortIconOnlyWidth - Gap);
}
