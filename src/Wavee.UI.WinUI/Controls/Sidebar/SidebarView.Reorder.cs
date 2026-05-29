using System.Collections.Generic;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Wavee.UI.Services.DragDrop;
using Wavee.UI.WinUI.Controls.Reorder;

namespace Wavee.UI.WinUI.Controls.Sidebar;

/// <summary>
/// react-beautiful-dnd-style reorder gap for the sidebar, plus the container-level
/// drop authority that makes the gap droppable.
///
/// <para>The sidebar drag is OLE (no pointer capture — it must hit-test across
/// rows for nest / copy / pin / move-to-root). Opening the gap with composition
/// <c>Translation</c> shifts the displaced rows' hit-test regions, so the visual
/// gap becomes a hit-test hole: a row's <c>DragOver</c> stops firing there, the
/// operation falls to <c>None</c> (Ø cursor) and the drop lands on nothing. To fix
/// that, <see cref="MenuItemHostScrollViewer"/> — which never moves — owns a
/// fallback <c>DragOver</c>/<c>Drop</c> (registered <c>handledEventsToo</c>): rows
/// mark their own events handled when the pointer is over them; when the pointer is
/// in the gap, no row handles it, so the scroll-viewer fallback re-affirms the
/// accept and, on drop, resolves the target row + position from the pointer in
/// resting (layout) space and raises the same drop the row would have.</para>
///
/// <para>The tree is hierarchical: <c>MenuItemsHost</c> holds only section rows;
/// playlists live in the Playlists section's nested <c>ChildrenPresenter</c>
/// repeater (folders in their own). So the gap displaces siblings in the target
/// row's <em>own parent repeater</em>, never <c>MenuItemsHost</c> (which would
/// shove whole sections — the "everything moves down" bug).</para>
/// </summary>
public sealed partial class SidebarView
{
    private readonly ReorderDisplacement _reorderGap = new();
    private ItemsRepeater? _gapRepeater;
    private int _reorderGapInsertion = -1;
    private bool _reorderSurfaceAttached;

    /// <summary>Wire the scroll viewer as the gap's drop authority (once, on load).</summary>
    private void AttachReorderSurface()
    {
        if (_reorderSurfaceAttached || MenuItemHostScrollViewer is not { } sv) return;
        _reorderSurfaceAttached = true;
        sv.AllowDrop = true;
        sv.AddHandler(UIElement.DragOverEvent, new DragEventHandler(OnSurfaceDragOver), handledEventsToo: true);
        sv.AddHandler(UIElement.DropEvent, new DragEventHandler(OnSurfaceDrop), handledEventsToo: true);
        sv.DragLeave += (_, _) => ClearReorderGap(animate: true);
    }

    // ── Row-driven path (pointer is over a row) ───────────────────────────

    /// <summary>
    /// Open / move the gap based on the live pointer. <paramref name="targetRow"/>
    /// is the row whose <c>DragOver</c> fired; its parent repeater is the list the
    /// gap opens within. <paramref name="edgeOnly"/> is true for edge-reorder
    /// targets (center/nest targets pass false → gap closes, outline shows instead).
    /// </summary>
    internal void UpdateReorderGap(SidebarItem targetRow, DragEventArgs dragArgs, bool edgeOnly)
    {
        if (!edgeOnly) { ClearReorderGap(animate: true); return; }
        var repeater = FindParentRepeater(targetRow);
        System.Diagnostics.Debug.WriteLine(
            $"[sbreorder] UpdateReorderGap edgeOnly={edgeOnly} parentRepeaterNull={repeater is null}");
        if (repeater is null) { ClearReorderGap(animate: true); return; }
        double pointerY;
        try { pointerY = dragArgs.GetPosition(repeater).Y; }
        catch { return; }
        OpenGap(repeater, pointerY);
    }

    // ── Container-driven fallback (pointer is in the gap dead-zone) ────────

    private void OnSurfaceDragOver(object sender, DragEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[sbreorder] SurfaceDragOver handled={e.Handled} reorderable={IsReorderablePayload()} gapRepeaterNull={_gapRepeater is null}");
        // A row under the pointer already handled it (over-row case) → nothing to do.
        if (e.Handled) return;
        if (!IsReorderablePayload() || _gapRepeater is null) return;

        double pointerY;
        try { pointerY = e.GetPosition(_gapRepeater).Y; }
        catch { return; }

        // Keep the gap alive + the operation accepted while the pointer hovers it.
        OpenGap(_gapRepeater, pointerY);
        e.AcceptedOperation = DataPackageOperation.Copy;
        UpdateReorderAutoScroll(e);
        e.Handled = true;
    }

    private void OnSurfaceDrop(object sender, DragEventArgs e)
    {
        if (e.Handled) return; // a row committed the drop
        StopReorderAutoScroll();
        if (TryResolveSurfaceDropTarget(e, out var targetRow, out var pos))
        {
            ClearReorderGap(animate: false);
            RaiseItemDropped(targetRow!, pos, e);
            e.Handled = true;
        }
        else
        {
            ClearReorderGap(animate: true);
        }
    }

    private bool IsReorderablePayload()
    {
        var kind = _dragStateService?.CurrentPayload?.Kind;
        return kind is DragPayloadKind.Playlist or DragPayloadKind.SidebarItem;
    }

    /// <summary>
    /// Map the in-gap pointer to the row + position the drop should target: insert
    /// before row <c>i</c> → (rows[i], Top); past the last row → (lastRow, Bottom).
    /// </summary>
    private bool TryResolveSurfaceDropTarget(DragEventArgs e, out SidebarItem? targetRow, out SidebarItemDropPosition pos)
    {
        targetRow = null;
        pos = SidebarItemDropPosition.Top;
        if (!IsReorderablePayload() || _gapRepeater is null) return false;

        var rows = RealizedRows(_gapRepeater);
        if (rows.Count == 0) return false;
        double pointerY;
        try { pointerY = e.GetPosition(_gapRepeater).Y; }
        catch { return false; }

        var insertion = ResolveInsertionIndex(pointerY, rows);
        // insertion is a LIST index (RootRow.Index), NOT a position into `rows` —
        // they diverge the moment the repeater virtualizes and the realized window
        // starts past list-index 0 (many playlists + scrolled down). Map the list
        // index back to the realized row before indexing.
        var at = rows.FindIndex(r => r.Index == insertion);
        if (at < 0)
        {
            // Past the last realized row → append after it.
            targetRow = rows[^1].Element as SidebarItem;
            pos = SidebarItemDropPosition.Bottom;
        }
        else
        {
            targetRow = rows[at].Element as SidebarItem;
            pos = SidebarItemDropPosition.Top;
        }
        return targetRow is not null;
    }

    // ── Shared gap mechanics ──────────────────────────────────────────────

    private void OpenGap(ItemsRepeater repeater, double pointerY)
    {
        // Switched lists mid-drag (e.g. into a folder's children) → snap prior shut.
        if (!ReferenceEquals(repeater, _gapRepeater))
        {
            _reorderGap.ClearAllInstant();
            _gapRepeater = repeater;
            _reorderGapInsertion = -1;
        }

        var rows = RealizedRows(repeater);
        if (rows.Count == 0) return;

        var insertion = ResolveInsertionIndex(pointerY, rows);
        System.Diagnostics.Debug.WriteLine(
            $"[sbreorder] OpenGap rows={rows.Count} insertion={insertion} prev={_reorderGapInsertion}");
        if (insertion == _reorderGapInsertion) return; // idempotent — no re-spring per tick
        _reorderGapInsertion = insertion;

        var gap = rows[0].Height > 0 ? rows[0].Height : 44;
        foreach (var r in rows)
            _reorderGap.ApplyOffset(r.Element, r.Index >= insertion ? gap : 0);
    }

    /// <summary>Close the gap. animate=true springs shut (mid-drag); false zeroes instantly (drop / drag-end).</summary>
    internal void ClearReorderGap(bool animate)
    {
        _reorderGapInsertion = -1;
        _gapRepeater = null;
        if (animate) _reorderGap.ResetAnimated();
        else _reorderGap.ClearAllInstant();
    }

    private readonly record struct RootRow(UIElement Element, int Index, double Top, double Height);

    /// <summary>The nearest ancestor <see cref="ItemsRepeater"/> that directly realizes
    /// <paramref name="row"/> — its sibling list is where the gap opens.</summary>
    private static ItemsRepeater? FindParentRepeater(DependencyObject row)
    {
        var node = VisualTreeHelper.GetParent(row);
        while (node is not null)
        {
            if (node is ItemsRepeater r) return r;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    /// <summary>Realized rows of <paramref name="repeater"/> with resting (layout) bounds in its own space.</summary>
    private static List<RootRow> RealizedRows(ItemsRepeater repeater)
    {
        var rows = new List<RootRow>();
        var count = repeater.ItemsSourceView?.Count ?? 0;
        for (var i = 0; i < count; i++)
        {
            if (repeater.TryGetElement(i) is not FrameworkElement el) continue;
            double top;
            try { top = el.TransformToVisual(repeater).TransformPoint(new Point(0, 0)).Y; }
            catch { continue; }
            var h = el.ActualHeight;
            if (h <= 0) continue;
            rows.Add(new RootRow(el, i, top, h));
        }
        rows.Sort(static (a, b) => a.Index.CompareTo(b.Index));
        return rows;
    }

    /// <summary>
    /// Gap index (insert-before-this-row) from the pointer's resting-space Y:
    /// the first row whose vertical midpoint sits below the pointer. Past the last
    /// row → append after it.
    /// </summary>
    private static int ResolveInsertionIndex(double pointerY, List<RootRow> rows)
    {
        foreach (var r in rows)
            if (pointerY < r.Top + r.Height / 2)
                return r.Index;
        return rows[^1].Index + 1;
    }
}
