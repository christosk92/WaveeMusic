// ── Themes/WinUi/Structure/Views/Overlays/WinUiSheetDrag.cs — the ONE drag every Classic bottom sheet shares ─────────────────────
//
// A bottom sheet (the Compact panel sheet, TC2; the context sheet, TC9) is dragged by its head (the grab strip and the row under it) the way a
// phone's sheet is:
//
//   press        the sheet stops where it is drawn (a press mid-slide catches it) and the cursor turns to the vertical-resize arrows for the whole
//                gesture, wherever the pointer goes
//   move         the sheet follows the pointer 1:1 (the next frame, no easing); above its rest it rubber-bands (`WinUiSheetRules.Rubber`, up to
//                the sheet's `Reach`); the scrim's dim follows the share of the sheet still on screen
//   release      past the travel or the speed threshold (`WinUiSheetRules.Dismisses`) the owner's dismiss runs and the sheet slides out from
//                where it is; short of it, it springs back to rest carrying the release speed, the scrim with it on the same spring
//   cancel       (capture lost) springs back
//
// A tap on the head moves nothing (the release finds no travel and settles at rest), and nothing here closes on its own: the owner's dismiss
// does (the panel's close, the sheet overlay's close). Mouse, pen and touch drive the same `OnDrag` capture (the engine routes a touch press on an
// `OnDrag` node to it), so the three behave alike.
//
// TWO NODES. The drag moves `SheetNode`. An owner that runs its own open/close slide on ANOTHER node (`SlideNode`: the panel's arm, under the host
// the drag moves, which is what lets the sheet rise past the host's clip) hands the pose over at both ends: a press folds the slide's pose into the
// drag's (the slide stops where it is drawn), and a dismiss hands the drag's pose back to the slide node, so the owner's slide-out starts there.
//
// THE COORDINATES. `OnDrag` hands the head's local point UNCLAMPED, through the head's live transforms, and the head moves with the sheet, so the
// window-space pointer is that point plus the head's absolute origin (both read the same transforms, so a stale pose cancels out). The velocity is
// sampled on the frame clock (`FrameClock.FrameQpc`), one sample per frame, so a headless run is deterministic.

using System.Diagnostics;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;

namespace Wavee.Themes.WinUi;

/// <summary>The shared bottom-sheet drag (see the file header).</summary>
internal sealed class WinUiSheetDrag
{
    SceneStore? _scene;
    AnimEngine? _anim;
    InputHooks? _hooks;
    NodeHandle _head;
    bool _dragging;
    float _pointer0, _offset0, _offset;

    // One sample per frame: (frame seconds, presented offset).
    readonly double[] _t = new double[8];
    readonly float[] _y = new float[8];
    int _count, _last = -1;

    public WinUiSheetDrag()
    {
        HeadRealized = h => _head = h;
        Down = OnDown;
        Move = OnMove;
        Release = OnRelease;
        Cancel = OnCancel;
    }

    /// <summary>The node whose vertical translation the drag moves.</summary>
    public NodeHandle SheetNode { get; set; }

    /// <summary>The node the owner runs its own open/close slide on, when that is not <see cref="SheetNode"/> (see the file header); none
    /// otherwise.</summary>
    public NodeHandle SlideNode { get; set; }

    /// <summary>The node whose opacity follows the share of the sheet still on screen (a scrim's dim), or none.</summary>
    public NodeHandle DimNode { get; set; }

    /// <summary>How far the sheet may rise above its rest (the room its clip leaves it).</summary>
    public float Reach { get; set; } = WinUiSheetRules.RubberReach;

    /// <summary>How tall the sheet is (its travel to off-screen); the sheet node's laid-out height when null.</summary>
    public Func<float>? Height { get; set; }

    /// <summary>What a release past the threshold does: the owner closes the sheet, which slides out from where it is.</summary>
    public Action? Dismiss { get; set; }

    /// <summary>The head's handlers, built once (stable delegates: a re-render rebinds nothing).</summary>
    public Action<NodeHandle> HeadRealized { get; }
    public Action<Point2> Down { get; }
    public Action<Point2> Move { get; }
    public Action Release { get; }
    public Action Cancel { get; }

    /// <summary>Is a drag in progress.</summary>
    public bool Dragging => _dragging;

    /// <summary>Bind the live scene, the animation engine and the input hooks (call it in the owner's render).</summary>
    public void Bind(RenderContext ctx, InputHooks hooks)
    {
        _scene = ctx.Scene;
        _anim = ctx.Anim;
        _hooks = hooks;
    }

    /// <summary>Put the dim back at full strength (the owner calls it as the sheet opens, while the scrim is still invisible).</summary>
    public void ResetDim()
    {
        if (_anim is { } anim && Live(DimNode)) anim.Animate(DimNode, AnimChannel.Opacity, 1f, 1f, 1f, Easing.Linear);
    }

    /// <summary>Wire <paramref name="head"/> as the sheet's drag handle: the press, the drag, the release (an <c>OnDrag</c> node's click is its
    /// release edge), the cancel and the vertical-resize cursor. Its interactive children keep their own presses.</summary>
    public BoxEl Head(BoxEl head) => head with
    {
        OnRealized = HeadRealized,
        OnPointerDown = Down,
        OnDrag = Move,
        OnClick = Release,
        OnDragCanceled = Cancel,
        Cursor = CursorId.SizeNS,
        TabStop = false,                                  // a handle, not a tab stop (Escape and the doors close the sheet from the keyboard)
    };

    bool Live(NodeHandle n) => !n.IsNull && _scene is { } scene && scene.IsLive(n);

    float PointerY(Point2 local) => local.Y + (_scene is { } scene && Live(_head) ? scene.AbsoluteRect(_head).Y : 0f);

    /// <summary>Where <paramref name="node"/> is drawn on Y: its running track first, else its painted pose.</summary>
    float Presented(NodeHandle node)
    {
        if (!Live(node)) return 0f;
        if (_anim is { } anim && anim.TryGetTrackValue(node, AnimChannel.TranslateY, out float v)) return v;
        return _scene!.Paint(node).LocalTransform.Dy;
    }

    float SheetHeight()
    {
        float h = Height?.Invoke() ?? (Live(SheetNode) ? _scene!.Bounds(SheetNode).H : 0f);
        return MathF.Max(1f, h);
    }

    static double Now() => FrameClock.FrameQpc / (double)Stopwatch.Frequency;

    void OnDown(Point2 local)
    {
        if (_anim is not { } anim || !Live(SheetNode) || !Live(_head)) return;
        _dragging = true;
        _pointer0 = PointerY(local);
        _offset0 = Presented(SheetNode);
        if (Live(SlideNode))
        {
            // Catch the owner's slide where it is drawn: its pose becomes the drag's.
            _offset0 += Presented(SlideNode);
            anim.Animate(SlideNode, AnimChannel.TranslateY, 0f, 0f, 1f, Easing.Linear);
        }
        _offset = _offset0;
        _count = 0; _last = -1;
        Note(_offset);
        Apply(_offset);
        _hooks?.SetCursorOverride?.Invoke(this, CursorId.SizeNS);
    }

    void OnMove(Point2 local)
    {
        if (!_dragging) return;
        _offset = WinUiSheetRules.Rubber(_offset0 + (PointerY(local) - _pointer0), Reach);
        Note(_offset);
        Apply(_offset);
    }

    void OnRelease()
    {
        if (!_dragging) return;
        _dragging = false;
        _hooks?.SetCursorOverride?.Invoke(this, null);
        float velocity = Velocity();
        float height = SheetHeight();
        if (WinUiSheetRules.Dismisses(_offset, velocity, height) && Dismiss is { } dismiss)
        {
            if (_anim is { } anim && Live(SlideNode))
            {
                // Hand the pose back to the owner's slide node: its slide-out starts where the drag left the sheet.
                anim.Animate(SlideNode, AnimChannel.TranslateY, _offset, _offset, 1f, Easing.Linear);
                anim.Animate(SheetNode, AnimChannel.TranslateY, 0f, 0f, 1f, Easing.Linear);
            }
            dismiss();
            return;
        }
        Settle(velocity, height);
    }

    void OnCancel()
    {
        if (!_dragging) return;
        _dragging = false;
        _hooks?.SetCursorOverride?.Invoke(this, null);
        Settle(0f, SheetHeight());
    }

    /// <summary>Spring the sheet back to rest from where it is, the release speed carried in, and the dim with it on the same spring
    /// (a linear spring: the dim stays the share of the sheet on screen all the way).</summary>
    void Settle(float velocity, float height)
    {
        if (_anim is not { } anim || !Live(SheetNode)) return;
        var spring = SpringParams.FromResponse(WinUiSheetRules.SettleResponseS, WinUiSheetRules.SettleDamping);
        anim.Spring(SheetNode, AnimChannel.TranslateY, 0f, spring, initial: _offset, initialVelocity: velocity);
        if (Live(DimNode))
            anim.Spring(DimNode, AnimChannel.Opacity, 1f, spring, initial: WinUiSheetRules.ScrimShare(_offset, height), initialVelocity: -velocity / height);
        _offset = 0f;
    }

    /// <summary>Slide the sheet the rest of the way out over <paramref name="ms"/> (a sheet whose owner does not slide it itself).</summary>
    public void SlideOut(float ms)
    {
        if (_anim is not { } anim || !Live(SheetNode)) return;
        float height = SheetHeight();
        anim.Animate(SheetNode, AnimChannel.TranslateY, Presented(SheetNode), height, ms, Easing.FluentAccelerate);
        if (Live(DimNode)) anim.Animate(DimNode, AnimChannel.Opacity, WinUiSheetRules.ScrimShare(_offset, height), 0f, ms, Easing.Linear);
    }

    void Apply(float offset)
    {
        if (_anim is not { } anim) return;
        anim.Animate(SheetNode, AnimChannel.TranslateY, offset, offset, 1f, Easing.Linear);
        if (Live(DimNode))
        {
            float share = WinUiSheetRules.ScrimShare(offset, SheetHeight());
            anim.Animate(DimNode, AnimChannel.Opacity, share, share, 1f, Easing.Linear);
        }
    }

    void Note(float offset)
    {
        double t = Now();
        if (_last >= 0 && _t[_last] >= t) { _y[_last] = offset; return; }   // one sample per frame: the latest position wins
        _last = (_last + 1) % _t.Length;
        _t[_last] = t; _y[_last] = offset;
        if (_count < _t.Length) _count++;
    }

    /// <summary>The release speed (DIP per second, positive downward): the travel over the last window, or none for a pointer held still.</summary>
    float Velocity()
    {
        if (_count < 2) return 0f;
        if (Now() - _t[_last] > WinUiSheetRules.StillAfterS) return 0f;
        int oldest = _last;
        for (int i = 1; i < _count; i++)
        {
            int j = (_last - i + _t.Length) % _t.Length;
            if (_t[_last] - _t[j] > WinUiSheetRules.VelocityWindowS) break;
            oldest = j;
        }
        double dt = _t[_last] - _t[oldest];
        return dt <= 0.0 ? 0f : (float)((_y[_last] - _y[oldest]) / dt);
    }
}
