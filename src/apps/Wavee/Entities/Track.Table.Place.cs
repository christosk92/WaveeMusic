// ── Entities/Track.Table.Place.cs ─────────────────────────────────────────────────────────────────────────────────
// The detail table's PLACE across an arm swap (themes plan 3.3, owner rule "a crossing must not cost scroll position or
// selection"). A new named partial of `Track`, beside `TableHost` (Track.Table.cs).
//
// Role: UI
//
// WHY A HAND-OVER, NOT A RE-PARENT. The detail frame's arms (`detail:arm:vertical` ↔ `detail:arm:two-column` / `head`) are
// different TABLES on purpose: the vertical arm's list carries the hero and the chrome as its persistent prefix items, the
// two-column arm's list starts at its first row with the chrome above its viewport, and an album's list rides one outer
// scroller with a hero (vertical) or without one (two-column). The table is keyed by the arm (`tracks:vertical:` /
// `tracks:standard:`) so a hero-system list never reconciles against a standard one. Re-parenting the old table into the new
// arm (`Flow.Persist`) would keep a list whose geometry is wrong for the new arm, take the list out of the leaving arm (the
// rows would vanish from the fade-out and fade in from zero: a flash), park every closed page's table for 10 s, and carry
// find text and selection across a theme switch (owner Q26: nothing is carried). A pixel offset (the ScrollKey memory, which
// both arms share) maps to a DIFFERENT row, because the hero sits above the rows in one arm and not in the other.
//
// So the frame host (`Detail.FrameHost`, which outlives its arms) owns one `ListPlace` per page and, on the render that
// flips the arm, asks the LIVE table for its place: the first visible row (under the vertical arm's pinned band, or the
// viewport top), the selection and its anchor (as DISPLAY rows, so the vertical arm's two prefix items never shift it),
// the keyboard-current row when focus is inside the list, and the find text, filters, multi-select and the open find box.
// The arriving table takes it at mount (the same route and context only), opens with that view state in its first render
// (so the list key it mounts under is already the final one), skips the warm reveal ramp (the rows are not new), and, in a
// layout effect once its list has laid out, brings the same row to the top of its visible band, focuses the same row and
// re-selects the same rows. The two arms then cross-fade with the same rows in the same order at the top of both.

using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;

namespace Wavee;

public readonly partial struct Track
{
    /// <summary>The hand-over of a detail table's place across an arm swap. Owned by the frame host (one per page, outliving
    /// its arms); the live table registers itself, the host asks for the place on the render that flips the arm, and the
    /// arriving table takes it at mount.</summary>
    public sealed class ListPlace
    {
        Func<PlaceState?>? _capture;
        PlaceState? _armed;

        /// <summary>The frame host, on the render that swaps the table for the other arm's: record where the live table is.
        /// A plain field write (no signal), so it is safe in a render.</summary>
        public void Capture() => _armed = _capture?.Invoke();

        /// <summary>True while a captured place waits for the arriving table (tests).</summary>
        public bool Armed => _armed is not null;

        internal void Own(Func<PlaceState?> capture) => _capture = capture;

        internal void Release(Func<PlaceState?> capture)
        {
            if (ReferenceEquals(_capture, capture)) _capture = null;
        }

        /// <summary>The arriving table, at mount: the captured place if it is this route's and this context's, once.</summary>
        internal PlaceState? Take(string scrollKey, EntityId context)
        {
            var s = _armed;
            _armed = null;
            return s is not null && string.Equals(s.ScrollKey, scrollKey, StringComparison.Ordinal) && s.Context.Equals(context) ? s : null;
        }
    }

    /// <summary>One captured place, in DISPLAY rows (the arm-independent index space: the vertical arm's prefix items excluded).</summary>
    internal sealed class PlaceState
    {
        public string ScrollKey = "";
        public EntityId Context;
        /// <summary>The first visible row (−1: the list rested at its top, or no row was under the band).</summary>
        public int Row = -1;
        /// <summary>The keyboard-current row while focus was inside the list (−1: focus was elsewhere).</summary>
        public int Focused = -1;
        public int Anchor = -1;
        public readonly List<(int Start, int End)> Ranges = new();
        public string Query = "";
        public FilterState Filters;
        public bool Multi, SearchOpen;
    }

    sealed partial class TableHost
    {
        /// <summary>The place this table took at mount, applied once its list has laid out (<see cref="ApplyPlace"/>).</summary>
        PlaceState? _pendingPlace;
        /// <summary><see cref="LoadView"/> has run for the mounted context (the place's selection lands after it, never under its reset).</summary>
        bool _viewLoaded;
        /// <summary>The band the rows scroll under in this arm: the vertical arm's sticky inset, 0 where the chrome sits above the viewport.</summary>
        float _placeBand;
        Func<PlaceState?>? _capturePlace;

        /// <summary>ApplyProps, at mount only: take the place the leaving arm's table left, and open with its view state, so the first render
        /// already builds the list under its final key (the find text and the filters are in the two-column list's key).</summary>
        void TakePlace(TableArgs args)
        {
            if (args.Place is not { } place) return;
            _pendingPlace = place.Take(args.ScrollKey, args.Source.Context.Id);
            if (_pendingPlace is not { } s) return;
            // No subscriber exists yet (the first render has not run), so these writes notify nothing.
            _query.Value = s.Query;
            _filters.Value = s.Filters;
            _multi.Value = s.Multi;
            _searchExpanded.Value = s.SearchOpen;
        }

        /// <summary>Register with the page's place (an effect; the cleanup releases it only if no successor took it since, and the leaving table's
        /// cleanup runs after the frame host has already captured from it).</summary>
        Action? OwnPlace()
        {
            if (_latest.Place is not { } place) return null;
            var capture = _capturePlace ??= CapturePlace;
            place.Own(capture);
            return () => place.Release(capture);
        }

        /// <summary>The frame host's question, asked on the render that swaps this table for the other arm's: where is the user? Reads the live
        /// scene (this table's list is still mounted and laid out at the old arm); writes nothing.</summary>
        PlaceState? CapturePlace()
        {
            if (_snapshot is null || _rowItems is null) return null;
            var scene = Context.Scene;
            int start = TrackStart;
            int count = _rowItems.Count.Peek();
            var s = new PlaceState
            {
                ScrollKey = _latest.ScrollKey, Context = _contextSeen,
                Query = _query.Peek(), Filters = _filters.Peek(), Multi = _multi.Peek(), SearchOpen = _searchExpanded.Peek(),
            };
            if (scene is null || count <= 0) return s;
            s.Row = Cfg.HasTrailing ? TrailFirstRow(scene, count) : ListFirstRow(scene, start, count);
            int last = start + count - 1;
            for (int r = 0; r < _selection.RangeCount; r++)
            {
                var (a, b) = _selection.GetRange(r);
                a = Math.Max(a, start);
                b = Math.Min(b, last);
                if (b >= a) s.Ranges.Add((a - start, b - start));
            }
            int anchor = _selection.AnchorIndex - start;
            s.Anchor = anchor >= 0 && anchor < count ? anchor : -1;
            int current = _listCtl.CurrentItemIndex - start;
            if (current >= 0 && current < count && FocusInList(scene)) s.Focused = current;
            return s;
        }

        /// <summary>The list's own viewport scrolls (a playlist, Liked): the row under the top of the visible band (below the pinned hero band in
        /// the vertical arm, whose `ItemClipTopInset` the list carries; the viewport top in the two-column arm).</summary>
        int ListFirstRow(SceneStore scene, int start, int count)
        {
            var vp = _listCtl.Viewport;
            if (vp.IsNull || !scene.IsLive(vp) || !scene.TryGetScroll(vp, out var sc)) return -1;
            if (sc.OffsetY <= 0.5f || sc.ViewportH <= 1f) return -1;   // resting at the top: the new arm opens at its top
            float inset = float.IsFinite(sc.ItemClipTopInset) ? MathF.Max(0f, sc.ItemClipTopInset) : 0f;
            // A probe a little under the band's edge, stepped down past a row gap.
            for (float dy = 1f; dy <= 33f; dy += 8f)
            {
                float ratio = MathF.Min(1f, (inset + dy) / sc.ViewportH);
                if (!_listCtl.TryGetItemIndex(0.5f, ratio, out int item)) continue;
                int row = item - start;
                return row >= 0 && row < count ? row : -1;   // the hero / chrome prefix: the user is at the top
            }
            return -1;
        }

        /// <summary>An album's rows ride ONE outer scroller (the trailing body) with the hero (vertical) or nothing (two-column) above them: the row
        /// at the outer viewport's band edge, from the list's laid-out top inside the scroller's content and the fixed row height.</summary>
        int TrailFirstRow(SceneStore scene, int count)
        {
            if (!TrailGeometry(scene, out var outer, out float listTop)) return -1;
            if (!scene.TryGetScroll(outer, out var sc) || sc.OffsetY <= 0.5f) return -1;
            float rowH = _shape?.Peek().RowH ?? 0f;
            if (rowH <= 0f) return -1;
            float y = sc.OffsetY + _placeBand - listTop;
            if (y < 0f) return -1;
            return Math.Min(count - 1, (int)(y / rowH));
        }

        /// <summary>The trailing body's outer viewport (the nearest scroller above the list's own natural-height viewport) and the list's top in
        /// that scroller's content coordinates (layout rects: the scroll offset is a transform, not a bound).</summary>
        bool TrailGeometry(SceneStore scene, out NodeHandle outer, out float listTop)
        {
            outer = NodeHandle.Null;
            listTop = 0f;
            var inner = _listCtl.Viewport;
            if (inner.IsNull || !scene.IsLive(inner)) return false;
            for (var n = scene.Parent(inner); !n.IsNull; n = scene.Parent(n))
                if (scene.HasScroll(n)) { outer = n; break; }
            if (outer.IsNull) return false;
            listTop = scene.AbsoluteLayoutRect(inner).Y - scene.AbsoluteLayoutRect(outer).Y;
            return true;
        }

        /// <summary>Is keyboard focus inside this table's list (the focused node under the list's viewport)?</summary>
        bool FocusInList(SceneStore scene)
        {
            var vp = _listCtl.Viewport;
            var focus = _hooks?.GetFocus?.Invoke() ?? NodeHandle.Null;
            if (vp.IsNull || focus.IsNull || !scene.IsLive(focus)) return false;
            for (var n = focus; !n.IsNull; n = scene.Parent(n))
                if (n == vp) return true;
            return false;
        }

        /// <summary>Auto-tracked layout effect (after layout, before paint): once the arriving list has laid out with its rows, put the user back
        /// where they were. Re-runs as the column's measured width or the row count moves, until it lands once.</summary>
        void ApplyPlace()
        {
            _ = _colW.Value;
            _ = _visible.Value;
            if (_pendingPlace is not { } s || !_viewLoaded) return;
            var scene = Context.Scene;
            if (scene is null) { _pendingPlace = null; return; }
            var vp = _listCtl.Viewport;
            int count = _rowItems?.Count.Peek() ?? 0;
            if (vp.IsNull || !scene.IsLive(vp) || count <= 0 || _selection.ItemCount <= 0) return;   // the list has not laid out its rows yet
            _pendingPlace = null;
            int start = TrackStart;

            // 1. The same first visible row, at the top of this arm's visible band; a list that rested at its top (or above its first row: the
            //    hero) opens at its top. Either way this replaces the pixel offset the shared ScrollKey restored, which means another row here.
            if (!Cfg.HasTrailing)
            {
                if (s.Row >= 0) _listCtl.StartBringItemIntoView(Math.Min(s.Row, count - 1) + start, 0f);
                else scene.ScrollHandleFor(vp)?.ScrollTo(0.0, ScrollMove.Immediate);
            }
            else if (TrailGeometry(scene, out var outer, out float listTop))
            {
                float rowH = _shape?.Peek().RowH ?? 0f;
                float target = s.Row >= 0 ? MathF.Max(0f, listTop + Math.Min(s.Row, count - 1) * rowH - _placeBand) : 0f;
                scene.ScrollHandleFor(outer)?.ScrollTo(target, ScrollMove.Immediate);
            }
            // 2. The keyboard-current row, focused again (only when focus was in the list). Its arrow-key selection rule runs, so the
            //    selection is put back after it.
            if (s.Focused >= 0 && s.Focused < count) _listCtl.FocusItem(s.Focused + start);
            // 3. The same rows selected, the same anchor for a shift-click.
            _selection.ClearSelection();
            foreach (var (a, b) in s.Ranges)
            {
                if (a >= count) continue;
                _selection.SelectRange(a + start, Math.Min(b, count - 1) + start);
            }
            _selection.AnchorIndex = s.Anchor >= 0 && s.Anchor < count ? s.Anchor + start : -1;
        }
    }
}
