using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Wavee.Audio.Queue;
using Wavee.UI.Services.DragDrop;
using Wavee.UI.Services.DragDrop.Payloads;
using Wavee.UI.WinUI.Controls.Reorder;
using Windows.Foundation;

namespace Wavee.UI.WinUI.Controls.Queue;

/// <summary>
/// react-beautiful-dnd reorder wiring for the queue. Each reorderable section
/// (<c>UserQueue</c>, <c>ContextUpcoming</c> shown as Next-Up + Autoplay, and
/// <c>PostContext</c>) is a separate <see cref="ListView"/>, so each gets its own
/// <see cref="ReorderController"/>; a drag stays within its section (matching the
/// prior queue UX). The shared composition lift/displacement engine replaces the
/// old 2-px insertion line.
/// </summary>
public sealed partial class QueueControl
{
    private readonly Dictionary<ListView, ReorderController> _sectionReorder = new();

    /// <summary>
    /// Attach the section's reorder engine to a freshly-realized row. <paramref name="rowRoot"/>
    /// is the template content (TrackRoot); the engine operates on the row's
    /// <see cref="ListViewItem"/> container so its lift/displacement targets line up
    /// with <c>GetRealizedRows</c> (which enumerates <c>ContainerFromIndex</c>).
    /// </summary>
    private void AttachReorderToRow(FrameworkElement rowRoot)
    {
        ListViewItem? container = null;
        ListView? list = null;
        DependencyObject? node = rowRoot;
        while (node is not null)
        {
            node = VisualTreeHelper.GetParent(node);
            if (container is null && node is ListViewItem lvi) container = lvi;
            if (node is ListView lv) { list = lv; break; }
        }
        if (container is null || list is null || SectionTargetFor(list) is not { } target) return;

        if (!_sectionReorder.TryGetValue(list, out var controller))
        {
            controller = new ReorderController(new SectionReorderHost(this, list, target));
            _sectionReorder[list] = controller;
        }
        controller.AttachRow(container);
    }

    /// <summary>Queue reorder is local-playback only — a remote active device owns the queue.</summary>
    private bool QueueReorderEnabled => !(_playbackCommandService?.IsPlayingRemotely ?? false);

    /// <summary>
    /// Map the engine's <c>(modelIndex, gapSlot)</c> to the backend
    /// <c>(oldIndex, newIndex)</c> the bucket expects, then dispatch. Context rows
    /// translate through <see cref="QueueDisplayItem.ContextTailIndex"/> (Next-Up and
    /// Autoplay share one absolute context bucket); other buckets are list-local.
    /// </summary>
    private bool CommitSectionReorder(ListView list, QueueReorderTarget target, int fromModelIndex, int toGapSlot)
    {
        var count = list.Items.Count;
        if (count == 0 || fromModelIndex < 0 || fromModelIndex >= count) return false;

        int from, to;
        if (target == QueueReorderTarget.ContextUpcoming)
        {
            if (list.Items[fromModelIndex] is not QueueDisplayItem srcItem) return false;
            from = srcItem.ContextTailIndex;
            int gapAbs;
            if (toGapSlot < count && list.Items[toGapSlot] is QueueDisplayItem atSlot)
                gapAbs = atSlot.ContextTailIndex;
            else if (list.Items[count - 1] is QueueDisplayItem last)
                gapAbs = last.ContextTailIndex + 1;
            else return false;
            // Plain remove+insert: a gap past the source shifts down by one.
            to = gapAbs > from ? gapAbs - 1 : gapAbs;
        }
        else
        {
            from = fromModelIndex;
            to = toGapSlot > from ? toGapSlot - 1 : toGapSlot;
            to = Math.Clamp(to, 0, count - 1);
        }

        if (from < 0 || to < 0 || from == to) return false;
        _ = ReorderAsync(target, from, to);
        return true;
    }

    /// <summary>Per-section <see cref="IReorderHost"/> adapter over a queue ListView.</summary>
    private sealed class SectionReorderHost : IReorderHost
    {
        private readonly QueueControl _owner;
        private readonly ListView _list;
        private readonly QueueReorderTarget _target;

        public SectionReorderHost(QueueControl owner, ListView list, QueueReorderTarget target)
        {
            _owner = owner;
            _list = list;
            _target = target;
        }

        public FrameworkElement ReorderCoordinateRoot =>
            (FrameworkElement?)_list.ItemsPanelRoot ?? _list;

        public FrameworkElement ViewportElement =>
            (FrameworkElement?)_owner.QueueScroll ?? _list;

        public int ItemCount => _list.Items.Count;

        public IReadOnlyList<ReorderRow> GetRealizedRows()
        {
            var root = ReorderCoordinateRoot;
            var rows = new List<ReorderRow>(_list.Items.Count);
            for (int i = 0; i < _list.Items.Count; i++)
            {
                if (_list.ContainerFromIndex(i) is not FrameworkElement c) continue;
                double top;
                try { top = c.TransformToVisual(root).TransformPoint(new Point(0, 0)).Y; }
                catch { continue; }
                rows.Add(new ReorderRow(c, i, top, c.ActualHeight));
            }
            return rows;
        }

        public bool CanReorder => _owner.QueueReorderEnabled;

        // Queue rows always drag singly.
        public (int From, int Length) GetReorderSpan(int pressedIndex) => (pressedIndex, 1);

        public string GetItemLabel(int index) =>
            index >= 0 && index < _list.Items.Count && _list.Items[index] is QueueDisplayItem q
                ? q.Title : string.Empty;

        public void ScrollBy(double deltaPixels) =>
            _owner.QueueScroll?.ScrollBy(0, deltaPixels, InstantScroll);

        // Per-frame auto-scroll must be instant: the default animated ScrollBy
        // retargets a running animation every frame, which lags and compounds.
        private static readonly Microsoft.UI.Xaml.Controls.ScrollingScrollOptions InstantScroll =
            new(Microsoft.UI.Xaml.Controls.ScrollingAnimationMode.Disabled,
                Microsoft.UI.Xaml.Controls.ScrollingSnapPointsMode.Ignore);

        public IDragPayload? BuildPayload(int from, int length)
        {
            if (from < 0 || from >= _list.Items.Count
                || _list.Items[from] is not QueueDisplayItem q
                || string.IsNullOrEmpty(q.TrackUri))
                return null;
            // Queue isn't a playlist context, so a drop on a playlist routes to
            // "add tracks", never an intra-list reorder.
            return new TrackDragPayload(new[] { q.TrackUri }, sourceContextUri: null, sourceStartIndex: null);
        }

        public bool CommitMove(int fromIndex, int length, int toGapSlot) =>
            _owner.CommitSectionReorder(_list, _target, fromIndex, toGapSlot);
    }
}
