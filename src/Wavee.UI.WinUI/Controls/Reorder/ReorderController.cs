using System;
using System.Collections.Generic;
using System.Numerics;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Wavee.UI.WinUI.DragDrop;

namespace Wavee.UI.WinUI.Controls.Reorder;

/// <summary>
/// Capture-driven, composition-based reorder engine that reproduces the
/// react-beautiful-dnd feel: press-and-drag lifts the row, neighbours glide out of
/// the way (<see cref="ReorderDisplacement"/>) to open a real gap, edge auto-scroll
/// (<see cref="ReorderAutoScroller"/>), a distance-scaled spring drop, plus keyboard
/// reorder + screen-reader announcements.
///
/// <para>One controller per list. The host calls <see cref="AttachRow"/> on each
/// realized container (the same element it returns from
/// <see cref="IReorderHost.GetRealizedRows"/>). When the pointer leaves the list
/// horizontally the gesture is handed off to the OS OLE drag so existing
/// cross-surface drop targets (sidebar playlist, enqueue, …) keep working.</para>
/// </summary>
public sealed class ReorderController
{
    // ── rbd tuning ──
    private const double ThresholdSq = 6 * 6;          // promote hold→drag
    private const double HandoffMargin = 24;           // px outside viewport X → OLE handoff
    private static readonly Vector2 DropEaseA = new(0.2f, 1f);   // cubic-bezier(.2,1,.1,1)
    private static readonly Vector2 DropEaseB = new(0.1f, 1f);
    private const double MinDropMs = 330, MaxDropMs = 550, MaxDropDist = 1500;
    private const float LiftScale = 1.02f, LiftZ = 32f, LiftOpacity = 0.95f;

    private readonly IReorderHost _host;
    private readonly ReorderDisplacement _displacement = new();
    private readonly ReorderAutoScroller _autoScroll;
    private readonly HashSet<FrameworkElement> _rows = new();

    // pointer gesture state
    private FrameworkElement? _pressRow;
    private uint _pointerId;
    private Point _pressOriginPanel;       // press point in coordinate-root space
    private bool _dragging;
    private double _lastPointerPanelY;     // pointer Y in coordinate-root (content) space
    private double _lastPointerViewportY;  // pointer Y in viewport space (stable while finger still)

    // active lift
    private readonly List<FrameworkElement> _spanContainers = new();
    private int _from, _length, _slot;
    private double _liftStartTop;          // dragged block top (panel space) at lift
    private double _spanHeight;
    private bool _committing;

    // keyboard lift
    private bool _keyboardLift;
    private FrameworkElement? _keyboardRow;

    public ReorderController(IReorderHost host)
    {
        _host = host;
        _autoScroll = new ReorderAutoScroller(host.ScrollBy, OnAutoScrolled);
    }

    /// <summary>Attach gesture + keyboard handlers to a realized container (idempotent).</summary>
    public void AttachRow(FrameworkElement row)
    {
        if (!_rows.Add(row)) return;
        row.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPointerPressed), true);
        row.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnPointerMoved), true);
        row.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnPointerReleased), true);
        row.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnPointerCanceled), true);
        row.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown), true);
        // Capture loss mid-gesture (context menu, focus steal, window deactivate)
        // must tear down an ACTIVE lift — not just the press tracking — or the row
        // is left stuck elevated with a gap open. Skip while committing: the drop
        // animation legitimately holds the lift until its batch completes.
        row.PointerCaptureLost += (_, _) =>
        {
            if (_dragging && !_committing && !_keyboardLift) Cancel();
            ResetPress();
        };
    }

    // ── pointer gesture ──────────────────────────────────────────────────

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement row || !_host.CanReorder) return;
        var pp = e.GetCurrentPoint(row);
        if (pp.Properties.IsRightButtonPressed || pp.Properties.IsMiddleButtonPressed) return;

        _pressRow = row;
        _pointerId = pp.PointerId;
        _pressOriginPanel = e.GetCurrentPoint(_host.ReorderCoordinateRoot).Position;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_committing) return;
        var pt = e.GetCurrentPoint(_host.ReorderCoordinateRoot).Position;

        if (!_dragging)
        {
            if (_pressRow is null) return;
            var dx = pt.X - _pressOriginPanel.X;
            var dy = pt.Y - _pressOriginPanel.Y;
            if (dx * dx + dy * dy < ThresholdSq) return;
            if (!BeginLift(_pressRow, e)) { ResetPress(); return; }
        }

        _lastPointerPanelY = pt.Y;

        // OLE handoff when the pointer leaves the list horizontally.
        var vp = e.GetCurrentPoint(_host.ViewportElement).Position;
        if (vp.X < -HandoffMargin || vp.X > _host.ViewportElement.ActualWidth + HandoffMargin)
        {
            HandoffToOle(e);
            return;
        }

        _lastPointerViewportY = vp.Y;
        UpdateTargets(pt.Y);
        UpdateLiftFollow(pt.Y);
        _autoScroll.Update(vp.Y, _host.ViewportElement.ActualHeight);
        e.Handled = true;
    }

    /// <summary>
    /// Project the last viewport-space pointer Y into current coordinate-root
    /// (content) space using the live viewport→content transform. Reflects the
    /// ACTUAL (clamped) scroll position and any list header offset — so at a scroll
    /// boundary, where the viewport can't move, this stops changing instead of
    /// running away (the old "accumulate requested delta" approach oscillated there).
    /// </summary>
    private double PointerPanelYFromViewport()
    {
        try
        {
            var t = _host.ViewportElement.TransformToVisual(_host.ReorderCoordinateRoot);
            return t.TransformPoint(new Point(0, _lastPointerViewportY)).Y;
        }
        catch { return _lastPointerPanelY; }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging) { Drop(); e.Handled = true; }
        ResetPress();
    }

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging) Cancel();
        ResetPress();
    }

    private bool BeginLift(FrameworkElement row, PointerRoutedEventArgs e)
    {
        var rows = _host.GetRealizedRows();
        int pressedIndex = -1;
        foreach (var r in rows)
            if (ReferenceEquals(r.Container, row)) { pressedIndex = r.ModelIndex; break; }
        if (pressedIndex < 0) return false;

        (_from, _length) = _host.GetReorderSpan(pressedIndex);
        if (_length <= 0) return false;

        _spanContainers.Clear();
        _spanHeight = 0;
        foreach (var r in rows)
        {
            if (r.ModelIndex >= _from && r.ModelIndex < _from + _length)
            {
                _spanContainers.Add(r.Container);
                _spanHeight += r.Height;
                if (r.ModelIndex == _from) _liftStartTop = r.Top;
            }
        }
        if (_spanContainers.Count == 0) return false;

        _dragging = true;
        _slot = _from;
        row.CapturePointer(e.Pointer);

        foreach (var c in _spanContainers) ElevateLift(c);
        ReorderAnnouncer.Announce(row, $"Lifted {Label(_from)}, position {_from + 1} of {_host.ItemCount}");
        return true;
    }

    /// <summary>Apply the lift treatment (z-order, shadow, scale, opacity) to a span container.</summary>
    private static void ElevateLift(FrameworkElement c)
    {
        Canvas.SetZIndex(c, 1000);
        ElementCompositionPreview.SetIsTranslationEnabled(c, true);
        c.Translation = new Vector3(0, c.Translation.Y, LiftZ);
        c.Shadow ??= new ThemeShadow();
        var v = ElementCompositionPreview.GetElementVisual(c);
        v.CenterPoint = new Vector3((float)(c.ActualWidth / 2), (float)(c.ActualHeight / 2), 0);
        var comp = v.Compositor;
        var scale = comp.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(1f, new Vector3(LiftScale, LiftScale, 1f));
        scale.Duration = TimeSpan.FromMilliseconds(160);
        v.StartAnimation("Scale", scale);
        c.Opacity = LiftOpacity;
    }

    private void RestoreLift(FrameworkElement c)
    {
        var v = ElementCompositionPreview.GetElementVisual(c);
        v.StopAnimation("Scale");
        v.Scale = Vector3.One;
        v.StopAnimation("Translation");
        c.Translation = Vector3.Zero;
        c.Shadow = null;
        c.Opacity = 1;
        Canvas.SetZIndex(c, 0);
    }

    /// <summary>Move the lifted block to track the pointer (Y delta from press, in panel space).</summary>
    private void UpdateLiftFollow(double pointerPanelY)
    {
        var dy = (float)(pointerPanelY - _pressOriginPanel.Y);
        foreach (var c in _spanContainers)
            c.Translation = new Vector3(0, dy, LiftZ);
    }

    /// <summary>Recompute the target gap from the pointer and displace neighbours.</summary>
    private void UpdateTargets(double pointerPanelY)
    {
        var rows = _host.GetRealizedRows();

        // react-beautiful-dnd center-of-gravity model. The dragged block's CENTER
        // (in resting/content space — grab-offset-independent) is compared against
        // the RESTING centers of the non-dragged rows. This is self-consistent
        // because it never re-measures displaced geometry: GetRealizedRows() returns
        // layout tops (composition Translation is invisible to TransformToVisual),
        // and the block center is derived from the pointer delta, not the live
        // displaced rows. Resolving against the *displaced* bands (the old
        // ResolveGapSlot) drifted by one row height between source and target — the
        // "jumps in between" bug.
        var blockCenter = _liftStartTop + (pointerPanelY - _pressOriginPanel.Y) + _spanHeight / 2;
        var slot = ResolveSlotByCenter(blockCenter, rows);
        if (slot == _slot) return;
        _slot = slot;

        ApplyDisplacement(rows, slot);
    }

    /// <summary>
    /// Gap slot (in original coordinates) for a dragged-block center, by counting
    /// non-dragged rows whose resting center sits above it. Hysteresis: a ~25%
    /// row-height dead band around the boundary that would flip the slot keeps a
    /// finger hovering on an edge from flip-flopping.
    /// </summary>
    private int ResolveSlotByCenter(double blockCenter, IReadOnlyList<ReorderRow> rows)
    {
        const double HysteresisFraction = 0.25;
        var slot = _from; // default: no move
        foreach (var r in rows)
        {
            // Only non-dragged rows define boundaries.
            if (r.ModelIndex >= _from && r.ModelIndex < _from + _length) continue;
            var rowCenter = r.Top + r.Height / 2;
            var band = r.Height * HysteresisFraction;

            if (r.ModelIndex < _from)
            {
                // Rows above the source: block must rise PAST this row's center
                // (minus the dead band) to insert before it.
                if (blockCenter < rowCenter - band)
                    return r.ModelIndex;
            }
            else
            {
                // Rows below the source: block must fall PAST this row's center
                // (plus the dead band) to insert after it. Gap index = row+1.
                if (blockCenter > rowCenter + band)
                    slot = r.ModelIndex + 1;
            }
        }
        return slot;
    }

    /// <summary>Displace non-dragged rows to open a gap at <paramref name="slot"/>.</summary>
    private void ApplyDisplacement(IReadOnlyList<ReorderRow> rows, int slot)
    {
        foreach (var r in rows)
        {
            // span rows follow the pointer, never displaced
            if (r.ModelIndex >= _from && r.ModelIndex < _from + _length) continue;

            double offset = 0;
            if (slot > _from + _length && r.ModelIndex >= _from + _length && r.ModelIndex < slot)
                offset = -_spanHeight;                 // dragging down: rows above gap rise
            else if (slot < _from && r.ModelIndex >= slot && r.ModelIndex < _from)
                offset = _spanHeight;                  // dragging up: rows below new slot drop
            _displacement.ApplyOffset(r.Container, offset);
        }
    }

    private void OnAutoScrolled(double delta)
    {
        if (!_dragging) return;
        // Re-derive the pointer's content-space Y from the LIVE transform rather
        // than trusting the requested `delta`. If the ScrollView was already
        // clamped at an edge, the transform didn't move and this is a no-op — which
        // is exactly what stops the boundary oscillation.
        _lastPointerPanelY = PointerPanelYFromViewport();
        UpdateTargets(_lastPointerPanelY);
        UpdateLiftFollow(_lastPointerPanelY);
    }

    // ── drop / cancel ────────────────────────────────────────────────────

    private void Drop()
    {
        _autoScroll.Stop();
        if (_slot == _from)
        {
            Cancel();
            return;
        }

        // Glide the lifted block to its resting offset, then commit + clear (the
        // displaced layout equals the post-move layout, so clearing is neutral).
        var restTop = RestingTop();
        var restY = (float)(restTop - _liftStartTop);
        var distance = Math.Abs(restY - (_lastPointerPanelY - _pressOriginPanel.Y));
        var ms = MinDropMs + Math.Min(1, distance / MaxDropDist) * (MaxDropMs - MinDropMs);

        _committing = true;
        var lead = _spanContainers.Count > 0 ? _spanContainers[0] : null;
        if (lead is null) { CommitAndClear(); _committing = false; return; }

        var v = ElementCompositionPreview.GetElementVisual(lead);
        var comp = v.Compositor;
        var ease = comp.CreateCubicBezierEasingFunction(DropEaseA, DropEaseB);
        var anim = comp.CreateVector3KeyFrameAnimation();
        anim.InsertKeyFrame(1f, new Vector3(0, restY, LiftZ), ease);
        anim.Duration = TimeSpan.FromMilliseconds(ms);
        anim.Target = "Translation";

        foreach (var c in _spanContainers)
            if (!ReferenceEquals(c, lead))
                c.Translation = new Vector3(0, restY, LiftZ);

        var batch = comp.CreateScopedBatch(CompositionBatchTypes.Animation);
        v.StartAnimation("Translation", anim);
        batch.End();
        batch.Completed += (_, _) =>
        {
            CommitAndClear();
            _committing = false;
        };
    }

    /// <summary>Panel-space top where the dragged block should settle for the current slot.</summary>
    private double RestingTop()
    {
        var rows = _host.GetRealizedRows();
        if (_slot >= _host.ItemCount)
        {
            double bottom = _liftStartTop;
            foreach (var r in rows) bottom = Math.Max(bottom, r.Top + r.Height);
            return bottom - _spanHeight;
        }
        foreach (var r in rows)
            if (r.ModelIndex == _slot)
                return _slot > _from ? r.Top - _spanHeight : r.Top;
        return _liftStartTop;
    }

    private void CommitAndClear()
    {
        var from = _from; var len = _length; var slot = _slot;
        var span = new List<FrameworkElement>(_spanContainers);

        // Optimistic local move first → relayout to final positions; then zero all
        // transforms synchronously. Displaced offsets == layout deltas, so this is
        // visually neutral (no flicker).
        var ok = _host.CommitMove(from, len, slot);
        _displacement.ClearAllInstant();
        foreach (var c in span) RestoreLift(c);
        EndDrag();

        if (ok)
        {
            var to = slot > from ? slot - len : slot;
            ReorderAnnouncer.Announce(span.Count > 0 ? span[0] : null,
                $"Moved {Label(from)} to position {Math.Clamp(to, 0, _host.ItemCount) + 1} of {_host.ItemCount}");
        }
    }

    private void Cancel()
    {
        _autoScroll.Stop();
        var span = new List<FrameworkElement>(_spanContainers);
        _displacement.ResetAnimated();
        foreach (var c in span)
        {
            // glide span back home (0.6× faster per rbd), then restore styling
            var v = ElementCompositionPreview.GetElementVisual(c);
            var comp = v.Compositor;
            var anim = comp.CreateVector3KeyFrameAnimation();
            anim.InsertKeyFrame(1f, new Vector3(0, 0, LiftZ));
            anim.Duration = TimeSpan.FromMilliseconds(200);
            anim.Target = "Translation";
            var batch = comp.CreateScopedBatch(CompositionBatchTypes.Animation);
            v.StartAnimation("Translation", anim);
            batch.End();
            batch.Completed += (_, _) => RestoreLift(c);
        }
        ReorderAnnouncer.Announce(span.Count > 0 ? span[0] : null,
            $"Cancelled, returned {Label(_from)} to position {_from + 1}");
        EndDrag();
    }

    private void HandoffToOle(PointerRoutedEventArgs e)
    {
        var from = _from; var len = _length;
        var src = _spanContainers.Count > 0 ? _spanContainers[0] : _pressRow;
        // Tear down the in-list lift, then start the OS drag carrying the payload
        // so existing cross-surface drop targets light up exactly as before.
        _autoScroll.Stop();
        _displacement.ClearAllInstant();
        foreach (var c in _spanContainers) RestoreLift(c);
        EndDrag();
        if (src is not null) src.ReleasePointerCaptures();
        ResetPress();

        var payload = _host.BuildPayload(from, len);
        if (payload is null || src is null) return;
        var dragState = Ioc.Default.GetService<DragStateService>();
        dragState?.StartDrag(payload);
        src.DragStarting += OnHandoffDragStarting;
        _ = StartOleAsync(src, e, payload, dragState);
    }

    private async System.Threading.Tasks.Task StartOleAsync(
        FrameworkElement src, PointerRoutedEventArgs e, Wavee.UI.Services.DragDrop.IDragPayload payload, DragStateService? dragState)
    {
        try { await src.StartDragAsync(e.GetCurrentPoint(src)); }
        catch { /* OS may refuse mid-gesture; cross-surface drop just won't start */ }
        finally
        {
            src.DragStarting -= OnHandoffDragStarting;
            dragState?.EndDrag();
        }
    }

    private void OnHandoffDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        var payload = Ioc.Default.GetService<DragStateService>()?.CurrentPayload;
        if (payload is null) { args.Cancel = true; return; }
        DragPackageWriter.Write(args.Data, payload);
        args.Data.RequestedOperation =
            Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy
            | Windows.ApplicationModel.DataTransfer.DataPackageOperation.Link
            | Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
    }

    private void EndDrag()
    {
        _dragging = false;
        _spanContainers.Clear();
        _slot = _from;
        _spanHeight = 0;
    }

    private void ResetPress()
    {
        _pressRow = null;
        _pointerId = 0;
    }

    // ── keyboard reorder ─────────────────────────────────────────────────

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not FrameworkElement row) return;

        if (!_keyboardLift)
        {
            if ((e.Key == VirtualKey.Space || e.Key == VirtualKey.Enter) && _host.CanReorder)
            {
                if (BeginKeyboardLift(row)) e.Handled = true;
            }
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Up:
                MoveKeyboard(-1); e.Handled = true; break;
            case VirtualKey.Down:
                MoveKeyboard(+1); e.Handled = true; break;
            case VirtualKey.Space:
            case VirtualKey.Enter:
                EndKeyboardLift(commit: true); e.Handled = true; break;
            case VirtualKey.Escape:
                EndKeyboardLift(commit: false); e.Handled = true; break;
        }
    }

    private bool BeginKeyboardLift(FrameworkElement row)
    {
        var rows = _host.GetRealizedRows();
        int idx = -1;
        foreach (var r in rows)
            if (ReferenceEquals(r.Container, row)) { idx = r.ModelIndex; break; }
        if (idx < 0) return false;

        (_from, _length) = _host.GetReorderSpan(idx);
        if (_length <= 0) return false;
        _spanContainers.Clear();
        _spanHeight = 0;
        foreach (var r in rows)
            if (r.ModelIndex >= _from && r.ModelIndex < _from + _length)
            {
                _spanContainers.Add(r.Container);
                _spanHeight += r.Height;
                if (r.ModelIndex == _from) _liftStartTop = r.Top;
            }
        if (_spanContainers.Count == 0) return false;

        _keyboardLift = true;
        _keyboardRow = row;
        _dragging = true;
        _slot = _from;
        foreach (var c in _spanContainers) ElevateLift(c);
        ReorderAnnouncer.Announce(row, $"Lifted {Label(_from)}, position {_from + 1} of {_host.ItemCount}. Use arrow keys to move.");
        return true;
    }

    private void MoveKeyboard(int delta)
    {
        // Nudge the gap one row at a time (when sitting at the source slot, a
        // downward nudge must clear the dragged block first).
        var newSlot = Math.Clamp(_slot + delta + (delta > 0 && _slot == _from ? _length : 0), 0, _host.ItemCount);
        // A gap that lands inside the dragged span is a no-op position → snap to source.
        if (newSlot > _from && newSlot <= _from + _length) newSlot = _from;
        if (newSlot == _slot) return;

        var rows = _host.GetRealizedRows();
        _slot = newSlot;
        ApplyDisplacement(rows, _slot);
        // follow: lift block by the net displacement
        var restY = (float)(RestingTop() - _liftStartTop);
        foreach (var c in _spanContainers)
            c.Translation = new Vector3(0, restY, LiftZ);

        var to = _slot > _from ? _slot - _length : _slot;
        ReorderAnnouncer.Announce(_keyboardRow, $"Position {Math.Clamp(to, 0, _host.ItemCount) + 1} of {_host.ItemCount}");
    }

    private void EndKeyboardLift(bool commit)
    {
        var doCommit = commit && _slot != _from;
        if (doCommit) CommitAndClear();
        else
        {
            _displacement.ResetAnimated();
            foreach (var c in new List<FrameworkElement>(_spanContainers)) RestoreLift(c);
            EndDrag();
        }
        _keyboardLift = false;
        _keyboardRow = null;
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private string Label(int index)
    {
        var l = _host.GetItemLabel(index);
        return string.IsNullOrEmpty(l) ? "item" : l;
    }
}
