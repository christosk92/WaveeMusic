using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Wavee.UI.Services.DragDrop;

namespace Wavee.UI.WinUI.Controls.Reorder;

/// <summary>
/// One realized, reorderable row: its container element, model index, and bounds
/// in <see cref="IReorderHost.ReorderCoordinateRoot"/> space. Headers/footers are
/// never reported here.
/// </summary>
public readonly record struct ReorderRow(FrameworkElement Container, int ModelIndex, double Top, double Height);

/// <summary>
/// Surface adapter for <see cref="ReorderController"/>. A host (queue section,
/// <c>TrackDataGrid</c>, <c>TrackListView</c>) implements this so the engine stays
/// control-agnostic — the host knows whether it wraps a <c>ListView</c>,
/// <c>ItemsRepeater</c> or <c>ItemsView</c>.
///
/// <para>Coordinate model: <see cref="ReorderCoordinateRoot"/> is the scrolling
/// <em>content</em> panel, so a row's <see cref="ReorderRow.Top"/> is stable under
/// scroll and a stationary screen pointer's Y in this space grows as content
/// auto-scrolls — which is exactly what drives target advance during edge-scroll.
/// <see cref="ViewportElement"/> is the (non-scrolling) viewport, used for
/// edge-band sizing and the OLE-handoff hit-test.</para>
/// </summary>
public interface IReorderHost
{
    /// <summary>Scrolling content panel; pointer + row geometry are measured against this.</summary>
    FrameworkElement ReorderCoordinateRoot { get; }

    /// <summary>Non-scrolling viewport (the ScrollViewer/ScrollView), for edge bands + handoff hit-test.</summary>
    FrameworkElement ViewportElement { get; }

    /// <summary>Total item count in the reorderable collection (including virtualized).</summary>
    int ItemCount { get; }

    /// <summary>Realized reorderable rows with bounds in <see cref="ReorderCoordinateRoot"/> space.</summary>
    IReadOnlyList<ReorderRow> GetRealizedRows();

    /// <summary>Gate: true when a reorder may start at all (queue local-only, playlist Custom-sort+owner, …).</summary>
    bool CanReorder { get; }

    /// <summary>Resolve the contiguous block to drag when the user grabs <paramref name="pressedModelIndex"/> (honours multi-select).</summary>
    (int From, int Length) GetReorderSpan(int pressedModelIndex);

    /// <summary>Human label for screen-reader announcements (e.g. track title). May be empty.</summary>
    string GetItemLabel(int index);

    /// <summary>Scroll the list by <paramref name="deltaPixels"/> (positive = toward end). No-op if not scrollable.</summary>
    void ScrollBy(double deltaPixels);

    /// <summary>Cross-surface payload for an OLE handoff when the pointer leaves the list, or null to suppress handoff.</summary>
    IDragPayload? BuildPayload(int fromIndex, int length);

    /// <summary>
    /// Commit a contiguous block move. <paramref name="toGapSlot"/> is the gap
    /// index in <c>[0, ItemCount]</c> (0 = before first row), matching the legacy
    /// <c>TracksReorderRequested</c> / queue contract — the host applies any
    /// removal-shift / context-index mapping itself. Should perform an optimistic
    /// local move synchronously, then dispatch the backend write. Returns false
    /// if rejected (engine then reverts visuals).
    /// </summary>
    bool CommitMove(int fromIndex, int length, int toGapSlot);
}
