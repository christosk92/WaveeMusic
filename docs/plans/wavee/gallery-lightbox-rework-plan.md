# Artist Gallery Lightbox Rework — Implementation Plan

Concept prototype: https://claude.ai/code/artifact/c8b7d062-3ded-4298-bdf4-537a111874c0
(near-fullscreen photo, auto-hiding floating chrome, filmstrip, pan/zoom, touchpad prev/next)

## Goal

Replace the `ContentDialog` gallery (548px card, title + OK/Close footer, 500×500 FlipView) with a
full-window lightbox pushed straight onto the modal overlay: the photo fills the viewport
(`ImageFit.Contain`), all chrome floats over it and fades after ~2.6s of pointer idle, and the
input model grows proper pan/zoom plus touchpad two-finger prev/next.

## What the research established (verified seams — do not re-derive)

| Seam | Where | Shape |
|---|---|---|
| Modal overlay | `src/FluentGpu.Controls/OverlayHost.cs:98-118` | `IOverlayService.Open(Func<NodeHandle> anchor, Func<Element> content, FlyoutPlacement, PopupOptions)` → `OverlayHandle` (`.Close()`, public `ClosedAction`). `PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.Modal, Chrome: PopupChrome.Modal)` gives the full-window centered host (`PositionedModal`, `OverlayHost.cs:1065`), smoke scrim, focus trap, and **Escape-to-close for free** (`PreviewKey`, `OverlayHost.cs:585`). A `Grow = 1` root stretches to the window. |
| Window size | `src/FluentGpu.Engine/Hooks/Context.cs:20` | `UseContext(Viewport.Size)` → `Size2` (re-renders on resize — wanted here). |
| Transforms | `src/FluentGpu.Engine/Dsl/Element.cs:272-299` | **`BoxEl` only**: `OffsetX/Y`, `ScaleX/Y`, `TransformOriginX/Y` (0..1), composited — never layout/hit-test. `ImageEl` has none → transform a wrapper box. |
| Node capture | `Element.cs:332` | `BoxEl.OnRealized = nh => …` (mount-only). FlipView's own idiom: `UseRef<NodeHandle>` + drive channels. |
| Anim channels | `src/FluentGpu.Engine/Animation/AnimScheduler.cs:209/282`, `AnimScheduler.Timeline.cs:29` | `Context.Anim.Animate(node, ch, from, to, durMs, easing)`, `.Spring(node, ch, to, in SpringParams)`, `.Keyframes(node, ch, keys, durMs)`, `.TryGetTrackValue(node, ch, out float)`. Channels: `AnimChannel.ScaleX/ScaleY/TranslateX/TranslateY`. Zero-alloc per-frame follow = reused 2-key `Keyframe[]` (FlipView `PanMove`, `FlipView.cs:261-274`). |
| Clipping | `src/FluentGpu.Engine/Render/SceneRecorder.cs:799-838` | `ClipToBounds` scissors scaled/translated children to the parent's device AABB — exactly what the pan/zoom frame needs. Rounded clip does NOT round images; irrelevant here (square frame). |
| Wheel events | `Element.cs:190`, `Foundation/Events.cs:131` | `OnPointerWheel = Action<WheelEventArgs>`; args carry `Local`, `Delta` (vertical), **`DeltaX` (horizontal)**, `Mods`, `Handled`. Dispatch bubbles leaf→root, stops on `Handled` (`InputDispatcher.cs:2802-2815`). |
| Drag | `Element.cs:140-141` | `OnPointerDown`/`OnDrag = Action<Point2>` (local), implicit capture; commit edge = `OnClick`. |
| Keys | `InputDispatcher.cs:4000` | `OnKeyDown(KeyEventArgs{KeyCode,Mods,Handled})` bubbles from focused node. `Keys.Left/Right`. Escape is consumed earlier by the overlay `PreviewKey`. |
| Touchpad reality | `src/FluentGpu.Windows/Pal/Win32Platform.cs:1156-1314`, `Win32DirectManipulation.cs` | `EnableMouseInPointer` retires `WM_MOUSEHWHEEL`; horizontal arrives via `WM_POINTERHWHEEL` or DManip. Precision-touchpad gestures become **scroll phase events** (`ScrollBegin/Update/End`, per-pixel `ScrollDeltaX`/`ScrollDelta`) that **bypass element `OnPointerWheel` entirely** — they only feed the scroll integrator, and are **dropped** when no scrollable matches the axis (`InputDispatcher.AccumulateContactDelta`, `InputDispatcher.cs:3031-3034`). Only detented mouse wheels produce `InputKind.Wheel` → `OnPointerWheel`. Touchpad pinch is expected as Ctrl+wheel packets (hi-res, ctrl mod). |
| FlipView | `src/FluentGpu.Controls/FlipView.cs` | `Create(IReadOnlyList<Element> items, float width, float height, int selectedIndex, Action<int>? onSelectionChanged, …)`. Owns keyboard/wheel/touch-drag. `OnWheel` (`:197-222`) reads **only `e.Delta`** and early-returns on Ctrl. Effect deps include `w,h` → window resize re-seeds the strip. |
| Idle timer | no hook exists | Pattern: CTS + `Task.Delay` + `UsePost()` (`ConcertAppendPreloader.cs:66-84`), cleanup via `Reactive.OnCleanup`. |
| App vocab | — | `Surfaces.Artwork(Image?, seed, w, h, corners, morphKey, decodePx)` (`Design/Surfaces.cs:59`); `Button.Accent/Standard(label, onClick, style?, isEnabled, parts?)` (`Button.cs:137/140`); `Image { Url, int? Width, int? Height, BlurHash }` (`Wavee.Core/Domain/Models.cs:11`); `BoxEl.Gradient = GradientSpec?` (`Element.cs:114`, angle 90 = vertical per hero-seam finding). |

## Architecture decisions

1. **Zoom mode swaps the subtree.** Unzoomed → `FlipView` owns drag/wheel/keys. Zoomed → the
   FlipView unmounts and a dedicated `ZoomSurface` takes over (drag pans, wheel pans, Ctrl+wheel
   zooms, click exits). This kills every input-arbitration conflict (FlipView's root `OnDrag`/
   `OnClick` commit edge never competes with pan). The same photo URL means the image cache makes
   the swap visually seamless.
2. **Touchpad prev/next = one small engine seam + FlipView wheel rework.** Phase-scroll gestures
   that latch **no scroller on either axis** get routed to the element wheel dispatch
   (`DispatchWheel`) instead of being dropped. FlipView then learns to read `DeltaX` and to
   accumulate hi-res packets. No env flags — constants with good defaults.
3. **Esc order: zoomed → unzoom; else close.** Needs `OverlayHandle.ClosingAction` promoted from
   internal to public (ContentDialog already uses it at `ContentDialog.cs:185`).
4. **Chrome auto-hide** is one `Signal<bool>` + a lastActivity-tick idle loop; chrome strips fade
   via `Opacity` + `Transition = MotionTok.ControlNormal`. Handlers stay live while hidden —
   reaching a button requires pointer movement, which re-shows chrome first.
5. **Scrim**: the lightbox root paints `rgba(0,0,0,0.88)` over the standard modal smoke.
   Accent stays small-emphasis only (active filmstrip ring), per the app's design language.

## Files touched

| File | Change |
|---|---|
| `src/FluentGpu.Engine/Input/InputDispatcher.cs` | phase-gesture wheel fallback (~20 lines) |
| `src/FluentGpu.Controls/FlipView.cs` | horizontal + hi-res wheel accumulation (~35 lines) |
| `src/FluentGpu.Controls/OverlayHost.cs` | `OverlayHandle.ClosingAction` internal → public (1 line) |
| `src/apps/Wavee/Features/Detail/ArtistGalleryLightbox.cs` | **new** — the lightbox component (chrome, filmstrip, idle, zoom surface, export moved here) |
| `src/apps/Wavee/Features/Detail/ArtistPage.Shelves.cs` | `OpenGallery` rewrite; delete `ArtistGalleryViewer` (lines 209-311) |

---

## A. Engine: phase-scroll → element-wheel fallback (`InputDispatcher.cs`)

When a precision-touchpad gesture crosses slop and neither axis finds a scrollable under the
contact, the deltas are currently dropped (`AccumulateContactDelta`, `:3031-3034`). Fall back to
the element wheel dispatch so wheel-consuming controls (FlipView paging, EditableText horizontal
scroll) see the gesture. Scoped: only fires where the gesture previously did nothing.

```csharp
// new field beside the _sg* gesture state:
bool _sgWheelFallback;   // unlatched gesture with no scroller on either axis → element wheel dispatch

// OnScrollPhase — case InputKind.ScrollBegin: add to the reset block (after _sgAccumX/Y = 0):
_sgWheelFallback = false;

// OnScrollPhase — case InputKind.ScrollUpdate: FIRST line of the case:
if (_sgWheelFallback) { DispatchWheel(in e); break; }

// OnScrollPhase — case InputKind.MomentumUpdate: FIRST line of the case (inertia keeps flipping;
// FlipView's cooldown bounds the rate):
if (_sgWheelFallback) { DispatchWheel(in e); break; }

// AccumulateContactDelta — replace the no-scroller bail (currently `if (vp.IsNull) return;` after
// the cross-axis retry at :3033-3034):
NodeHandle vp = ScrollableUnderForAxis(e.PositionPx, horiz);
if (vp.IsNull)
{
    vp = ScrollableUnderForAxis(e.PositionPx, !horiz);   // cross-axis fallback (unchanged)
    if (vp.IsNull)
    {
        // No scroller on either axis under the contact: route the REST of this gesture's packets
        // to element wheel handlers (leaf→root, Handled-stopping) — precision-touchpad gestures
        // otherwise exist only as phase events and die here. Reset by ScrollBegin.
        _sgWheelFallback = true;
        DispatchWheel(in e);   // deliver the slop-crossing packet too
        return;
    }
    horiz = !horiz;
}
```

Also reset `_sgWheelFallback = false` in `EndScrollGesture` alongside the other `_sg*` resets.

**Pre-implementation checks (do these before writing the code):**
- `DispatchWheel`'s exact signature (`InputDispatcher.cs:~2802`) — it already builds
  `WheelEventArgs` from `e.ScrollDelta`/`e.ScrollDeltaX`/`e.Mods`/`e.PositionPx`, so a phase
  `InputEvent` should slot straight in; confirm no wheel-only fields are read.
- The `InputKind.Wheel` case (`:1146-1164`) also runs a viewport-scroll step after
  `DispatchWheel`; the fallback deliberately calls **only** `DispatchWheel` (there is no viewport
  to scroll — that's the precondition).

## B. FlipView: horizontal + hi-res wheel (`FlipView.cs`)

Rework `OnWheel` (`:197-222`). Keep: Ctrl early-return (zoom chord belongs to content handlers),
the 200ms detent throttle, not-handled-at-ends scroll chaining. Add: dominant-axis selection so
`DeltaX` counts, and accumulation for sub-detent hi-res packets (which now arrive via seam A).

```csharp
const float SwipeFlipDip = 80f;      // accumulated hi-res travel per flip
const float SwipeNotchDip = 48f;     // |axis| at/above this = a discrete detent (mouse notch ≈ 60 DIP)
const float SwipeCooldownMs = 350f;  // post-flip refractory for the packet stream

// hooks state beside lastWheelTime/lastWheelDelta:
var swipeAccum = UseRef(0f);
var swipeLastMs = UseRef(0L);
var swipeCooldownUntil = UseRef(0L);

void OnWheel(WheelEventArgs e)
{
    if (e.Handled || count == 0) return;
    if ((e.Mods & KeyModifiers.Ctrl) != 0) return;

    bool horizPacket = MathF.Abs(e.DeltaX) > MathF.Abs(e.Delta);
    float axis = horizPacket ? e.DeltaX : e.Delta;
    if (axis == 0f) return;
    long now = Environment.TickCount64;

    if (MathF.Abs(axis) >= SwipeNotchDip)
    {
        // detented wheel: existing behavior, generalized to the dominant axis
        bool directionChange = (axis < 0f && lastWheelDelta.Value >= 0f) ||
                               (axis > 0f && lastWheelDelta.Value <= 0f);
        bool canFlip = directionChange || now - lastWheelTime.Value > (long)WheelDelayMs;
        lastWheelTime.Value = now;
        if (canFlip)
        {
            bool moved = axis < 0f ? MoveNext() : MovePrevious();
            if (moved) { lastWheelDelta.Value = axis; e.Handled = true; }
        }
        else e.Handled = true;
        return;
    }

    // hi-res packet stream (touchpad, via the dispatcher's no-scroller wheel fallback)
    if (now - swipeLastMs.Value > (long)WheelDelayMs) swipeAccum.Value = 0f;   // new segment
    swipeLastMs.Value = now;
    if (now < swipeCooldownUntil.Value) { e.Handled = true; return; }
    swipeAccum.Value += axis;
    if (MathF.Abs(swipeAccum.Value) >= SwipeFlipDip)
    {
        bool moved = swipeAccum.Value < 0f ? MoveNext() : MovePrevious();
        swipeAccum.Value = 0f;
        swipeCooldownUntil.Value = now + (long)SwipeCooldownMs;
        if (moved) e.Handled = true;
        // at the ends: leave unhandled so the gesture can chain outward, matching the notch path
    }
    else e.Handled = true;   // building toward a flip — swallow partials
}
```

**Sign check on hardware** (memory: verify before styling/behavior theories): PAL horizontal
convention is `+delta = right` (`Win32Platform.cs:1254`); `axis < 0 → MoveNext` matches the
existing vertical convention. If a rightward two-finger swipe moves the wrong way, flip the
comparison for the `horizPacket` case only — one constant, no env flag.

## C. Controls: expose the close veto (`OverlayHost.cs`)

`OverlayHandle.ClosingAction` already exists (ContentDialog assigns it, `ContentDialog.cs:185`)
but is internal. Promote the member to `public`, keeping its existing name and delegate type
(mirror `ContentDialog.VetoClosing` at `ContentDialog.cs:192-198` for the exact shape — it
receives the close cause and can cancel). No behavioral change.

## D. App: `ArtistGalleryLightbox.cs` (new file)

Move `ExportImage`/`ExtensionOf` verbatim from `ArtistGalleryViewer`; delete that class.

```csharp
using FluentGpu; // engine + controls usings per file conventions in Features/Detail

namespace Wavee.Features.Detail;

/// <summary>Full-window artist-photo lightbox: FlipView paging with auto-hiding floating chrome
/// (counter, export, close, filmstrip) and a pan/zoom surface. Zoom mode REPLACES the FlipView
/// subtree so drag/wheel ownership is never ambiguous. Opened by ArtistPage.OpenGallery through
/// the modal overlay — no ContentDialog card.</summary>
sealed class ArtistGalleryLightbox : Component
{
    const float IdleHideMs = 2600f;
    const float ZoomMax = 4f, ZoomClick = 2.2f, ZoomWheelK = 0.0022f;

    readonly IReadOnlyList<Image> _photos;
    readonly int _initialIndex;
    readonly Func<OverlayHandle?> _handle;

    readonly Signal<bool> _saving = new(false);
    readonly Signal<bool> _chrome = new(true);
    readonly Signal<bool> _zoomMode = new(false);

    // pan/zoom live state — screen px, driven onto the zoom node's anim channels
    NodeHandle _zoomNode;
    float _s = 1f, _tx, _ty;
    Point2 _pendingAnchor; bool _hasPendingAnchor;
    readonly Keyframe[] _seedKeys = new Keyframe[2];   // reused: FlipView PanMove 0-alloc idiom
    Point2 _dragLast; bool _dragMoved;

    long _lastActivityMs;
    CancellationTokenSource? _idleCts;

    public ArtistGalleryLightbox(IReadOnlyList<Image> photos, int initialIndex, Func<OverlayHandle?> handle)
    {
        _photos = photos;
        _initialIndex = Math.Clamp(initialIndex, 0, Math.Max(0, photos.Count - 1));
        _handle = handle;
    }

    /// <summary>Overlay Escape veto: zoomed → unzoom and cancel the close; else allow.</summary>
    public bool TryConsumeEscape()
    {
        if (!_zoomMode.Peek()) return false;
        SpringZoomHome();
        return true;
    }

    public override Element Render()
    {
        var vp = UseContext(Viewport.Size);
        var (selected, setSelected) = UseState(_initialIndex);
        var post = UsePost();
        int current = Math.Clamp(selected, 0, Math.Max(0, _photos.Count - 1));
        var photo = _photos[current];
        bool zoomed = _zoomMode.Value;

        UseEffect(() =>
        {
            Poke(post);
            Reactive.OnCleanup(() => { _idleCts?.Cancel(); _idleCts?.Dispose(); _idleCts = null; });
        });

        return new BoxEl
        {
            Grow = 1f, ZStack = true,
            Fill = ColorF.FromRgba(0, 0, 0, 224),          // ~88% scrim over the modal smoke
            Focusable = true,
            OnKeyDown = e => OnKeys(e, current, setSelected),
            OnHoverMove = _ => Poke(post),
            Children =
            [
                zoomed ? ZoomSurface(photo, vp)
                       : Pager(vp, current, i => { setSelected(i); ResetZoomState(); }),
                TopChrome(vp, current, post),
                zoomed ? new BoxEl() : Filmstrip(vp, current, setSelected),
            ],
        };
    }

    // ── unzoomed: FlipView pager ─────────────────────────────────────────────────────────────

    Element Pager(Size2 vp, int current, Action<int> onSelect)
    {
        var pages = new Element[_photos.Count];
        for (int i = 0; i < pages.Length; i++)
        {
            var photo = _photos[i];
            pages[i] = new BoxEl
            {
                Width = vp.Width, Height = vp.Height,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                // Ctrl+wheel / touchpad pinch enters zoom; everything else bubbles to FlipView.
                OnPointerWheel = e =>
                {
                    if ((e.Mods & KeyModifiers.Ctrl) == 0) return;
                    e.Handled = true;
                    EnterZoom(e.Local);
                },
                Children = [ HeroImage(photo, vp) ],
            };
        }
        return FlipView.Create(pages, vp.Width, vp.Height, current, onSelect);
    }

    Element HeroImage(Image photo, Size2 vp) => new ImageEl
    {
        Key = "gallery-image:" + photo.Url,
        Source = photo.Url, Width = vp.Width, Height = vp.Height,
        Fit = ImageFit.Contain,
        DecodePx = MathF.Min(MathF.Max(vp.Width, vp.Height), 2048f),
        Placeholder = ColorF.FromRgba(0, 0, 0, 0),
        BlurHash = photo.BlurHash,
        Transition = ImageTransition.Fade(140f),
    };

    // ── zoomed: pan/zoom surface (FlipView unmounted — no input arbitration) ────────────────

    Element ZoomSurface(Image photo, Size2 vp) => new BoxEl
    {
        Width = vp.Width, Height = vp.Height, ClipToBounds = true,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Cursor = CursorId.Hand,
        OnPointerDown = lp => { _dragLast = lp; _dragMoved = false; },
        OnDrag = lp =>
        {
            float dx = lp.X - _dragLast.X, dy = lp.Y - _dragLast.Y;
            if (MathF.Abs(dx) + MathF.Abs(dy) > 0f) _dragMoved = true;
            _dragLast = lp;
            _tx += dx; _ty += dy;
            ClampPan(photo, vp);
            WriteTransform();
        },
        OnClick = () => { if (!_dragMoved) SpringZoomHome(); },
        OnPointerWheel = e =>
        {
            e.Handled = true;
            if ((e.Mods & KeyModifiers.Ctrl) != 0)
            {
                float s1 = _s * MathF.Exp(-e.Delta * ZoomWheelK);
                if (s1 <= 1.02f) { SpringZoomHome(); return; }
                ZoomAt(e.Local, s1, photo, vp);
                WriteTransform();
            }
            else
            {
                _tx -= e.DeltaX; _ty -= e.Delta;   // two-finger pan; sign-check on hardware
                ClampPan(photo, vp);
                WriteTransform();
            }
        },
        Children =
        [
            new BoxEl   // the transformed carrier — handlers live on the STATIC outer frame above
            {
                Width = vp.Width, Height = vp.Height,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                TransformOriginX = 0.5f, TransformOriginY = 0.5f,
                OnRealized = nh => { _zoomNode = nh; SeedEnterZoom(vp); },
                Children = [ HeroImage(photo, vp) ],
            },
        ],
    };

    void EnterZoom(Point2 anchor)
    {
        _pendingAnchor = anchor; _hasPendingAnchor = true;
        _s = 1f; _tx = 0f; _ty = 0f;
        _zoomMode.Value = true;    // remounts subtree; SeedEnterZoom fires on OnRealized
    }

    void SeedEnterZoom(Size2 vp)
    {
        WriteTransform();          // seed channels at identity
        var photo = _photos[Math.Clamp(/* current — pass through a field set in Render */ _renderCurrent, 0, _photos.Count - 1)];
        Point2 a = _hasPendingAnchor ? _pendingAnchor : new Point2(vp.Width * 0.5f, vp.Height * 0.5f);
        _hasPendingAnchor = false;
        ZoomAt(a, ZoomClick, photo, vp);
        SpringChannels();          // springs from the seeded identity to the anchored target
    }

    void ZoomAt(Point2 local, float s1, Image photo, Size2 vp)
    {
        s1 = Math.Clamp(s1, 1f, ZoomMax);
        float dx = local.X - vp.Width * 0.5f, dy = local.Y - vp.Height * 0.5f;
        float k = s1 / _s;
        _tx = dx - (dx - _tx) * k;
        _ty = dy - (dy - _ty) * k;
        _s = s1;
        ClampPan(photo, vp);
    }

    void ClampPan(Image photo, Size2 vp)
    {
        (float cw, float ch) = ContainSize(photo, vp);
        float maxX = MathF.Max(0f, (cw * _s - vp.Width) * 0.5f);
        float maxY = MathF.Max(0f, (ch * _s - vp.Height) * 0.5f);
        _tx = Math.Clamp(_tx, -maxX, maxX);
        _ty = Math.Clamp(_ty, -maxY, maxY);
    }

    static (float W, float H) ContainSize(Image photo, Size2 vp)
    {
        float aspect = photo.Width is int w && photo.Height is int h && h > 0 ? (float)w / h : 1f;
        float cw = vp.Width, chh = cw / aspect;
        if (chh > vp.Height) { chh = vp.Height; cw = chh * aspect; }
        return (cw, chh);
    }

    /// <summary>Per-frame direct write — reused 2-key seed (FlipView PanMove idiom, 0-alloc).</summary>
    void WriteTransform()
    {
        var anim = Context.Anim;
        if (anim is null || _zoomNode.IsNull) return;
        Seed(anim, AnimChannel.ScaleX, _s);
        Seed(anim, AnimChannel.ScaleY, _s);
        Seed(anim, AnimChannel.TranslateX, _tx);
        Seed(anim, AnimChannel.TranslateY, _ty);
    }

    void Seed(AnimScheduler anim, AnimChannel ch, float v)
    {
        _seedKeys[0] = new Keyframe(0f, v, Easing.Linear);
        _seedKeys[1] = new Keyframe(1f, v, Easing.Linear);
        anim.Keyframes(_zoomNode, ch, _seedKeys, 1f);
    }

    void SpringChannels()
    {
        var anim = Context.Anim;
        if (anim is null || _zoomNode.IsNull) return;
        var sp = SpringParams.FromResponse(0.32f, 0.9f);
        anim.Spring(_zoomNode, AnimChannel.ScaleX, _s, sp);
        anim.Spring(_zoomNode, AnimChannel.ScaleY, _s, sp);
        anim.Spring(_zoomNode, AnimChannel.TranslateX, _tx, sp);
        anim.Spring(_zoomNode, AnimChannel.TranslateY, _ty, sp);
    }

    void SpringZoomHome()
    {
        _s = 1f; _tx = 0f; _ty = 0f;
        SpringChannels();
        // let the spring visibly settle, then swap back to the FlipView
        var post = /* captured UsePost from Render via a field */ _post;
        _ = Task.Delay(260).ContinueWith(_ => post(() => _zoomMode.Value = false));
    }

    void ResetZoomState() { _s = 1f; _tx = 0f; _ty = 0f; _zoomMode.Value = false; }

    // ── keys ─────────────────────────────────────────────────────────────────────────────────

    void OnKeys(KeyEventArgs e, int current, Action<int> setSelected)
    {
        if (e.Handled) return;
        // Unzoomed arrows are handled by the focused FlipView; this catches root-focus + zoom mode.
        if (e.KeyCode is Keys.Left or Keys.Right)
        {
            int next = Math.Clamp(current + (e.KeyCode == Keys.Right ? 1 : -1), 0, _photos.Count - 1);
            if (next != current) { ResetZoomState(); setSelected(next); }
            e.Handled = true;
        }
    }

    // ── chrome ───────────────────────────────────────────────────────────────────────────────

    Element TopChrome(Size2 vp, int current, Action<Action> post) => new BoxEl
    {
        Width = vp.Width,
        Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M,
        Padding = new Edges4(WaveeSpace.L, WaveeSpace.M, WaveeSpace.L, WaveeSpace.L),
        Gradient = /* vertical top scrim: rgba(0,0,0,0.62) → transparent, angle 90 */ TopScrimGradient,
        Opacity = _chrome.Value ? 1f : 0f,
        Transition = MotionTok.ControlNormal,
        Children =
        [
            new BoxEl
            {
                Direction = 1, Gap = 2f,
                Children =
                [
                    new TextEl(Loc.Get(Strings.Artist.Gallery)) { Size = 14f, Weight = 600, Color = Tok.TextPrimary },
                    new TextEl($"{current + 1} / {_photos.Count}") { Size = 12f, Color = Tok.TextSecondary },
                ],
            },
            new BoxEl { Grow = 1f },
            Button.Accent(_saving.Value ? "Exporting…" : "Export image",
                () => ExportImage(_photos[current], current, post), isEnabled: !_saving.Value),
            Button.Standard(Loc.Get(Strings.Auth.Close), () => _handle()?.Close()),
        ],
    };

    Element Filmstrip(Size2 vp, int current, Action<int> setSelected)
    {
        const float Thumb = 56f, Pad = 14f;
        var thumbs = new Element[_photos.Count];
        for (int i = 0; i < thumbs.Length; i++)
        {
            int idx = i;
            bool active = i == current;
            thumbs[i] = new BoxEl
            {
                Width = Thumb, Height = Thumb,
                Corners = CornerRadius4.All(WaveeRadius.Control), ClipToBounds = true,
                BorderWidth = 2f,
                BorderColor = active ? Tok.AccentTextPrimary : ColorF.FromRgba(0, 0, 0, 0),
                Opacity = active ? 1f : 0.55f,
                Transition = MotionTok.ControlFast,
                OnClick = () => setSelected(idx),
                Cursor = CursorId.Hand, HoverScale = 1.05f,
                Children = [ Surfaces.Artwork(_photos[idx], idx, Thumb, Thumb, WaveeRadius.Control, decodePx: 128) ],
            };
        }
        return new BoxEl
        {
            // anchored to the window bottom via layout margin — hit area is ONLY the strip row
            Width = vp.Width,
            Margin = new Edges4(0f, vp.Height - (Thumb + 2 * Pad), 0f, 0f),
            Direction = 0, Justify = FlexJustify.Center, AlignItems = FlexAlign.Center, Gap = WaveeSpace.S,
            Padding = Edges4.All(Pad),
            Opacity = _chrome.Value ? 1f : 0f,
            Transition = MotionTok.ControlNormal,
            Children = thumbs,
        };
    }

    // ── idle-hide loop (no timer hook exists; CTS + Task.Delay + UsePost pattern) ────────────

    void Poke(Action<Action> post)
    {
        _lastActivityMs = Environment.TickCount64;
        if (!_chrome.Peek()) _chrome.Value = true;
        if (_idleCts is not null) return;              // one loop; it re-reads _lastActivityMs
        var cts = _idleCts = new CancellationTokenSource();
        _ = IdleLoop(cts, post);
    }

    async Task IdleLoop(CancellationTokenSource cts, Action<Action> post)
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                long idle = Environment.TickCount64 - _lastActivityMs;
                if (idle >= (long)IdleHideMs)
                {
                    post(() => { if (!cts.IsCancellationRequested) _chrome.Value = false; });
                    break;
                }
                await Task.Delay((int)(IdleHideMs - idle) + 16, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(_idleCts, cts)) _idleCts = null; }
    }

    // ── export: moved verbatim from ArtistGalleryViewer (ArtistPage.Shelves.cs:270-311) ──────
    // async void ExportImage(Image photo, int index, Action<Action> post) { … }
    // static string ExtensionOf(string url) { … }
}
```

Note on the two field hand-offs the sketch glosses (`_renderCurrent`, `_post`): set both at the
top of `Render()` (`_renderCurrent = current; _post = post;`) — mount-frozen ctor args can't carry
them, and both are only read after at least one render.

## E. App: `OpenGallery` rewrite (`ArtistPage.Shelves.cs:166-177`)

```csharp
void OpenGallery(IReadOnlyList<Image> photos, int initialIndex)
{
    if (photos.Count == 0 || _menuOverlay is null) return;
    OverlayHandle? handle = null;
    ArtistGalleryLightbox? viewer = null;
    handle = _menuOverlay.Open(
        static () => NodeHandle.Null,
        () => Embed.Comp(() => viewer = new ArtistGalleryLightbox(photos, initialIndex, () => handle)),
        FlyoutPlacement.BottomCenter,
        new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.Modal, Chrome: PopupChrome.Modal));
    // Esc while zoomed unzooms instead of closing (needs change C; mirror ContentDialog.VetoClosing's shape):
    handle.ClosingAction = /* cause => cancel when */ (viewer?.TryConsumeEscape() == true);
}
```

Delete `ArtistGalleryViewer` (lines 209-311) after moving export code into the new file.

## Pre-implementation verification list — RESOLVED (2026-07-16)

1. **ZStack child anchoring** — `FlexLayout.ArrangeZStack` (`FlexLayout.cs:869-886`): a NaN-sized
   child FILLS the stack (minus margins); explicit sizes are kept; `AlignSelf` is the child's
   VERTICAL placement (`End` = bottom). ⇒ chrome strips get explicit heights + the filmstrip uses
   `AlignSelf = FlexAlign.End` — NOT the Margin hack sketched in §D.
2. **`DispatchWheel`** — `private bool DispatchWheel(in InputEvent e)` (`InputDispatcher.cs:2802`)
   reads only `PositionPx/ScrollDelta/ScrollDeltaX/Mods`; phase events carry all. Safe as planned.
3. **`OverlayHandle.ClosingAction`** — `Func<OverlayCloseCause, bool>` (`OverlayHost.cs:17`);
   return true = allow close, false = veto (per `ContentDialog.VetoClosing`).
4. **`Reactive.OnCleanup(Action)`** exists (`ReactiveCore.cs:37`); fallback if effect-scope
   doesn't apply: cleanup via public `handle.ClosedAction` in `OpenGallery`.
5. **Channel composition order** — anim slab `Compose` (`AnimScheduler.cs:165-171`) =
   `Translation(Tx,Ty) * Rotation * Scale(Sx,Sy)`, same as the reconciler static path. The zoom
   math holds as written (translate in screen px). Note: `Context.Anim` is `AnimEngine?`.
6. **Horizontal swipe sign** — still a HARDWARE check (one comparison flip if wrong; no env flag).
7. **`ColorF.FromRgba(byte r, byte g, byte b, byte a = 255)`** exists (`Geometry.cs:64`).

## Verification (after implementation)

- `dotnet build src/FluentGpu.slnx` clean; `dotnet run --project src/FluentGpu.VerticalSlice` →
  "ALL CHECKS PASSED" (engine input + controls touched: alloc tripwire and input gates must stay
  green; the wheel-fallback packets must allocate nothing — `DispatchWheel` reuses its args).
- Manual (Chris builds and runs — no agent builds):
  - Open gallery → photo near-fullscreen over dark scrim; chrome fades after ~2.6s idle, returns
    on pointer move.
  - Mouse wheel + ←/→ + touch drag flip photos (existing FlipView paths intact elsewhere in app).
  - **Touchpad**: two-finger horizontal swipe flips one photo per swipe; vertical two-finger also
    flips; pinch (or Ctrl+wheel) enters zoom anchored at the cursor.
  - **Zoom**: drag pans with clamped edges; two-finger scroll pans; Ctrl+wheel zooms 1–4×;
    click (no drag) or zooming out fully springs home and returns to the pager; Esc unzooms
    first, second Esc closes; ←/→ while zoomed unzoom + navigate.
  - Filmstrip click jumps (adjacent glides, distant jumps — FlipView semantics); active thumb ring.
  - Export image works with the Save-As picker + toast; Close button and scrim-Esc close.
  - Resize the window while open — image and strips track the new viewport.
- Regression: `EditableText` horizontal wheel still works; app scroll surfaces (home shelves,
  detail pages) unaffected by the dispatcher fallback (it only fires where NO scroller matched).
