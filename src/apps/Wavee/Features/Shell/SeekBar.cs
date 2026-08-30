using System;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Backend.Playback;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>What the centre rail IS right now. Three shapes, decided by the source's own timeline rather than by
/// whether a duration happened to be reported — the defect this enum exists to end is a sliding DVR window drawn as a
/// 3-minute track because Media Foundation answered <c>GetDuration</c> with the window's width.</summary>
enum SeekRailMode : byte
{
    /// <summary>An ordinary track: 0 → duration.</summary>
    Track,
    /// <summary>A live broadcast with a rewindable window: the rail maps the WINDOW, its right end IS the live edge.</summary>
    Dvr,
    /// <summary>A live broadcast with nothing to rewind (radio): there is no position to draw, so the rail stops
    /// pretending to be one and becomes a breathing line.</summary>
    Line,
}

// The center scrub bar — a bespoke media-seek control that REPLACES the old signal-bound Slider seek in the player bar. Three
// reasons it isn't a Slider:
//   1. THE SCRUB GATE (the bug fix). While the user is dragging, the displayed fraction must IGNORE PositionFrac so the
//      1 Hz position tick can't yank the thumb back under the finger. Slider.Bind reads one signal; we need a derived
//      fraction (scrubbing ? scrubFrac : playing ? interpolated : positionFrac).
//   2. SMOOTH PLAYHEAD. The transport only reports position ~1 Hz, so a raw bind steps once a second (jerky). While
//      playing we interpolate between ticks on a pixel-due UseInterval, anchored to the wall-clock at the last tick.
//   3. CLICK-ANYWHERE SCRUB modeled on ScrollBar's thumb-drag (grab on down, normalized 0..1 on drag), committing on
//      the drag-end (OnClick) so we issue ONE SeekAsync, not one per move.
//
// Cost discipline (signals-first): this is a leaf sub-component. The fill/thumb position is a compositor Transform BIND
// reading ONE signal (_displayFrac) — it NEVER re-renders this component. While playing, a mounted SeekTicker advances
// _displayFrac on a pixel-due interval (not FrameClock — that pins the host via FrameClockPoller); when paused/stopped
// the ticker is unmounted and the frame loop idles. The component re-renders only on the LOW-frequency state it reads
// in Render (playing/enabled), never per move or per frame.
sealed class SeekBar : Component
{
    internal const float HitHeight = 32f;     // WinUI SliderHorizontalHeight
    internal const float LiveLineHeight = 2f; // the radio line's stroke — a rule, not a rail
    static readonly bool DiagEnabled = Diag.EnvFlag("WAVEE_PLAYERBAR_DIAG");
    static int s_renderCount;
    static int s_boundsCount;

    readonly PlaybackBridge _b;

    // While scrubbing the fill follows _scrubFrac and ignores PositionFrac (no 1 Hz snap-back).
    readonly Signal<bool> _scrubbing = new(false);
    readonly FloatSignal _scrubFrac = new(0f);
    // The single value the fill/thumb compositor binds read. Advanced per frame by SeekTicker while playing; set
    // directly from PositionFrac when paused; set to the finger position while scrubbing.
    readonly FloatSignal _displayFrac = new(0f);

    NodeHandle _self;
    NodeHandle _thumb;
    float _width;            // live track width (px), refreshed from arranged bounds and each pointer-down
    long _tickWallMs;        // Environment.TickCount64 at the last position tick — the interpolation anchor
    long _tickPosMs;         // PositionMs at the last tick

    public SeekBar(PlaybackBridge b)
    {
        _b = b;
    }

    // Re-derive _displayFrac from the current model — called by the ticker every frame while playing, and once from a
    // mount effect so a paused/stopped bar still shows the right resting position. Zero alloc.
    internal void Recompute()
    {
        if (_scrubbing.Peek()) { _displayFrac.Value = _scrubFrac.Peek(); return; }   // scrub gate: ignore PositionFrac
        // A live broadcast's rail is anchored to its WINDOW, not to zero-and-a-duration — see LiveRail for why the
        // ordinary formula is meaningless there. Peeked (never .Value): this runs from the ticker and from effects,
        // never from Render, so it must not subscribe anything.
        LiveWindow live = _b.Live.Peek();
        bool dvr = live.IsLive && live.HasWindow;
        bool advancing = _b.IsPlaying.Peek() && !_b.IsBuffering.Peek();
        // The smooth playhead: the transport reports position ~1 Hz, so between ticks we extrapolate from the wall-clock
        // anchor. ONE playhead for both rails — a DVR window's fraction is computed from the same interpolated number a
        // track's is, so the fill advances at the playhead's rate and not at the window's republish rate.
        long est = advancing ? _tickPosMs + (Environment.TickCount64 - _tickWallMs) : live.PositionMs;
        if (dvr)
        {
            // AT EDGE the rail SNAPS FULL (LiveRail.DisplayFrac): the honest measurement against two ends that both keep
            // moving is a fill that never fills and slides a little on every window report, which reads as easing under a
            // playhead that is not moving. The edge state is the bridge's ONE hysteresis fact — never a threshold here.
            bool behind = _b.IsBehindLive.Peek();
            Publish((float)LiveRail.DisplayFrac(live.SeekableStartMs, live.SeekableEndMs, est, behind));
            return;
        }
        if (advancing)
        {
            long dur = _b.DurationMs.Peek();
            if (dur <= 0L) { _displayFrac.Value = 0f; return; }
            Publish(Math.Clamp(est / (float)dur, 0f, 1f));
            return;
        }
        // Paused/stopped on an ordinary track: static, the reported fraction.
        _displayFrac.Value = _b.PositionFrac.Peek();
    }

    /// <summary>Write the ONE value the fill/thumb binds read — a plain value SET, never a tween. The fill is a
    /// compositor <c>Transform</c> bind with no <c>Animate</c>, no <c>BrushTransition</c> and no motion tier on it, so
    /// what is written here is what is drawn on the next frame.
    /// <para>Quantized to the live track's whole-pixel granularity: a multi-minute track's playhead moves a few px/s, so
    /// most ticker frames land on the SAME pixel. Snapping to that pixel makes those frames write no transform → a
    /// byte-identical DrawList → the host's skip-submit gate elides the redundant GPU submit+present (the dominant
    /// at-rest cost), while a real pixel step still advances smoothly. It is also what keeps a DVR window's sub-pixel
    /// republish jitter from moving the thumb at all. Raw fraction when the width is not known yet.</para></summary>
    void Publish(float frac)
    {
        float w = _width;
        float q = w > 1f ? MathF.Round(frac * w) / w : frac;
        if (q != _displayFrac.Peek()) _displayFrac.Value = q;   // value-gate: an unmoved pixel is a true no-op (no bind re-run)
    }

    public override Element Render()
    {
        var b = _b;

        // Derive `enabled` REACTIVELY from the bridge signals (NOT a ctor-frozen field). The reconciler reuses a mounted
        // ComponentEl across re-renders without re-invoking the factory (Reconciler.Update: same ComponentType → early
        // return), so a ctor-frozen flag would stick at its first-mount value (false, before the track resolves) forever.
        // Reading the signals here re-renders the bar on the enabling transition, which re-installs the interaction
        // handlers (OnClick/OnPointerDown/OnDrag run on every reconcile). Mirrors PlayerBar's `active`.
        bool enabled = b.CurrentTrack.Value != null && b.Error.Value == null && !b.IsLoading.Value && b.CanSeek.Value;

        // WHAT the rail is. Derived through a MEMO, not read straight off `Live`: the host republishes the window
        // several times a second (the positions inside it move), and this component only cares about the three-way
        // SHAPE. A memo's equality cut-off means a moving window re-renders nothing — the fill is a compositor bind and
        // the Dvr↔Line↔Track flip is the only thing that changes structure.
        var railMode = UseComputed(() =>
        {
            LiveWindow live = b.Live.Value;
            if (!live.IsLive && !b.IsLive.Value) return SeekRailMode.Track;
            return live.HasWindow ? SeekRailMode.Dvr : SeekRailMode.Line;
        }).Value;
        bool dvr = railMode == SeekRailMode.Dvr;

        // Subscribe to the LOW-frequency signals that change the bar's STRUCTURE (mount/unmount the ticker) only.
        bool playing = b.IsPlaying.Value;
        bool buffering = b.IsBuffering.Value;
        long posTick = b.PositionMs.Value;   // subscribe → re-anchor the interpolation each ~1 Hz tick
        if (DiagEnabled)
            WaveeLog.Instance.Event(WaveeLogLevel.Debug, "ui", "seekbar.render", "Seek bar rendered",
                fields:
                [
                    WaveeLogField.Of("render", ++s_renderCount),
                    WaveeLogField.Of("enabled", enabled),
                    WaveeLogField.Of("playing", playing),
                ]);

        // Anchor the smooth-playhead interpolation: snapshot wall + position whenever PositionMs changes, then refresh
        // the resting display (covers the paused/seek-while-paused case — the ticker isn't mounted then).
        UseEffect(() =>
        {
            _tickWallMs = Environment.TickCount64;
            _tickPosMs = b.PositionMs.Peek();
            Recompute();
        }, posTick);

        // RE-ANCHOR on a play/pause flip. Position doesn't tick while paused, so on RESUME the interpolation would
        // extrapolate `_tickPosMs + (now - _tickWallMs)` across the whole paused gap for one frame (the timestamp jumps
        // ahead, then snaps back when the next tick re-anchors). Resetting the wall/pos anchor on the transition kills it.
        UseEffect(() =>
        {
            _tickWallMs = Environment.TickCount64;
            _tickPosMs = b.PositionMs.Peek();
            Recompute();
        }, playing);

        // Fill: a full-width accent bar scaled from the LEFT edge by _displayFrac. TransformOriginX=0 makes the bound
        // Scale pivot on the left (SceneRecorder: world ∘ T(ox,oy) ∘ Local ∘ T(-ox,-oy), ox = W·OriginX = 0), so the
        // fill grows rightward from 0. No layout, no re-render — a pure compositor transform reading ONE signal.
        var s = Slider.DefaultStyle;
        float ringD = s.ThumbRingDiameter;
        ColorF railFill = enabled ? s.RailFill : s.RailFillDisabled;
        ColorF valueFill = enabled ? s.ValueFill : s.ValueFillDisabled;
        ColorF valueHover = enabled ? s.ValueFillPointerOver : s.ValueFillDisabled;
        ColorF valuePress = enabled ? s.ValueFillPressed : s.ValueFillDisabled;
        ColorF dot = enabled ? s.ThumbFill : s.ThumbFillDisabled;
        ColorF dotHover = enabled ? s.ThumbFillPointerOver : s.ThumbFillDisabled;
        ColorF dotPress = enabled ? s.ThumbFillPressed : s.ThumbFillDisabled;
        float rest = enabled ? s.InnerRestScale : s.InnerDisabledScale;
        float hoverScale = enabled ? s.InnerHoverScale / s.InnerRestScale : 1f;
        float pressScale = enabled ? s.InnerPressScale / s.InnerRestScale : 1f;

        Func<Affine2D> fillBind = () => Affine2D.Scale(MathF.Max(Math.Clamp(_displayFrac.Value, 0f, 1f), 1e-4f), 1f);
        Func<Affine2D> thumbBind = () => ThumbTransform(_width, _displayFrac.Value, ringD);
        // Rail thickens while scrubbing (a compositor scale on the cross axis is overkill; a bound Height would relayout,
        // so we keep a static thicker rail when enabled — the visible cue is the thumb fade-in on hover).

        var fill = new BoxEl
        {
            Grow = 1f, Height = s.TrackHeight, AlignSelf = FlexAlign.Center,
            Fill = valueFill, HoverFill = valueHover, PressedFill = valuePress,
            // Keep the rail WinUI-rounded, but make the transformed value segment square.
            // Scaling a rounded rect exposes tiny vertical cap slivers at the value edge.
            Corners = CornerRadius4.All(0f),
            HitTestVisible = false,
            TransformOriginX = 0f,
            // Always bound: _displayFrac is 0 when disabled (no track) so the fill scales to empty — a disabled rail reads
            // as a flat EMPTY track, not a full-width grey bar (which an identity transform would have shown).
            Transform = fillBind,
        };

        // The DVR rail's right end IS the live edge, so it is marked. Without the tick the rail reads as a track whose
        // end is "the end", and a viewer riding the edge sees a full bar with no way to tell that full MEANS now.
        Element[] railKids;
        if (dvr)
        {
            var edgeTick = new BoxEl
            {
                Grow = 1f, Height = s.TrackHeight, Direction = 0, Justify = FlexJustify.End,
                HitTestVisible = false,
                Children =
                [
                    new BoxEl
                    {
                        Width = LiveLineHeight, Height = s.TrackHeight, Shrink = 0f,
                        Fill = Tok.AccentDefault,
                        HitTestVisible = false,
                    },
                ],
            };
            railKids = [fill, edgeTick];
        }
        else railKids = [fill];

        var rail = new BoxEl
        {
            Height = s.TrackHeight, Grow = 1f, AlignSelf = FlexAlign.Center,
            // A subtle track at low alpha (WinUI ControlStrong rail, dimmed for a media line).
            Fill = railFill, HoverFill = railFill, PressedFill = railFill,
            Corners = CornerRadius4.All(s.TrackCornerRadius),
            ClipToBounds = true,
            ZStack = true,
            HitTestVisible = false,
            Children = railKids,
        };

        var inner = new BoxEl
        {
            Width = s.InnerThumbDiameter, Height = s.InnerThumbDiameter,
            Corners = CornerRadius4.All(s.InnerThumbDiameter * 0.5f),
            Fill = dot, HoverFill = dotHover, PressedFill = dotPress,
            ScaleX = rest, ScaleY = rest,
            HoverScale = hoverScale, PressScale = pressScale,
            HoverDurationMs = 250f, PressDurationMs = 250f,
            HitTestVisible = false,
        };

        var thumb = new BoxEl
        {
            Width = ringD, Height = ringD,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(s.ThumbCornerRadius),
            Fill = s.ThumbRing, HoverFill = s.ThumbRing, PressedFill = s.ThumbRing,
            BorderBrush = s.ThumbBorder, BorderWidth = s.ThumbBorderWidth,
            Opacity = 0f, HoverOpacity = enabled ? 1f : 0f, PressedOpacity = enabled ? 1f : 0f,
            HitTestVisible = false,
            Transform = thumbBind,
            OnRealized = OnThumbRealized,
            Children = [inner],
        };

        var stack = new BoxEl
        {
            ZStack = true, Grow = 1f, Height = HitHeight, AlignItems = FlexAlign.Center,
            HitTestVisible = false,
            Children = [rail, thumb],
        };

        // NOTHING TO REWIND (radio, and any broadcast whose window is under LiveWindow.MinWindowMs): there is no
        // position to draw, so the rail stops pretending to be one. Returned AFTER every hook above, so the hook order
        // is identical in all three modes — the mode is a value the render branches on, never a hook branch.
        if (railMode == SeekRailMode.Line) return Embed.Comp(() => new LiveLine(b));

        // While playing, mount the pixel-due ticker (UseInterval — NOT FrameClock.Tick, which would pin the host at
        // panel rate via FrameClockPoller). Unmounts when paused/stopped so the frame loop idles. NEVER re-renders us.
        bool canAdvance = b.CurrentTrack.Peek() is not null && b.Error.Peek() is null && !b.IsLoading.Peek()
            && playing && !buffering;
        Element? ticker = canAdvance ? Embed.Comp(() => new SeekTicker { Owner = this }) : null;

        // The interactive row. Click-anywhere + drag scrub; OnClick is the drag-END commit edge (single SeekAsync).
        return new BoxEl
        {
            Grow = 1f, Height = HitHeight, Direction = 0, AlignItems = FlexAlign.Center,
            Role = AutomationRole.Slider,
            Cursor = enabled ? CursorId.Hand : (CursorId?)null,
            IsEnabled = enabled,
            // OnRealized is MOUNT-ONLY (BindNode wires it once and ignores it on re-render). Set it UNCONDITIONALLY so
            // `_self` is captured at mount regardless of the (then-unknown) enabled state — capturing a node handle while
            // disabled is harmless, and it MUST exist once the bar becomes enabled or every RefreshWidth early-returns.
            OnRealized = OnRealizedCb,
            OnBoundsChanged = OnArrangedBoundsChanged,
            OnPointerDown = enabled ? OnDown : null,
            OnDrag = enabled ? OnDrag : null,
            OnClick = enabled ? OnCommit : null,            // drag-end → commit
            OnDragCanceled = enabled ? OnCanceled : null,
            Children = ticker is null ? [stack] : [stack, ticker],
        };
    }

    void OnRealizedCb(NodeHandle h)
    {
        _self = h;
        RefreshWidth();
        Recompute();   // seed the resting position before the first paint
    }

    void OnThumbRealized(NodeHandle h)
    {
        _thumb = h;
        UpdateThumbTransform();
    }

    void OnArrangedBoundsChanged(RectF bounds)
    {
        if (!SetWidth(bounds.W)) return;
        if (DiagEnabled)
            WaveeLog.Instance.Event(WaveeLogLevel.Debug, "ui", "seekbar.bounds", "Seek bar bounds changed",
                fields:
                [
                    WaveeLogField.Of("count", ++s_boundsCount),
                    WaveeLogField.Of("width", bounds.W),
                ]);
        Recompute();
    }

    void RefreshWidth()
    {
        var scene = Context.Scene;
        if (scene is null || _self.IsNull || !scene.IsLive(_self)) return;
        SetWidth(scene.AbsoluteRect(_self).W);
    }

    bool SetWidth(float w)
    {
        if (w <= 0f || MathF.Abs(w - _width) <= 0.5f) return false;
        _width = w;
        UpdateThumbTransform();
        return true;
    }

    void UpdateThumbTransform()
    {
        var scene = Context.Scene;
        if (scene is null || _thumb.IsNull || !scene.IsLive(_thumb) || _width <= 0f) return;
        var next = ThumbTransform(_width, _displayFrac.Peek(), Slider.DefaultStyle.ThumbRingDiameter);
        ref var paint = ref scene.Paint(_thumb);
        if (paint.LocalTransform == next) return;
        paint.LocalTransform = next;
        scene.Mark(_thumb, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
    }

    static Affine2D ThumbTransform(float width, float frac, float ringD)
    {
        float half = ringD * 0.5f;
        float x = Math.Clamp(Math.Clamp(frac, 0f, 1f) * width - half, 0f, MathF.Max(0f, width - ringD));
        return Affine2D.Translation(x, 0f);
    }

    // Defensive re-derive for the pointer handlers (Peek — no render subscription). Handlers are only WIRED when enabled,
    // but this guards a stale fire during the disabling transition. Mirrors the Render derivation.
    bool Enabled() => _b.CurrentTrack.Peek() != null && _b.Error.Peek() == null && !_b.IsLoading.Peek() && _b.CanSeek.Peek();

    void OnDown(Point2 local)
    {
        if (!Enabled()) return;
        RefreshWidth();                                     // grip moves with the layout — re-read each gesture
        _scrubbing.Value = true;
        _scrubFrac.Value = Frac(local.X);                  // click-anywhere: jump to the press point
        _displayFrac.Value = _scrubFrac.Peek();            // paint the jump immediately
    }

    void OnDrag(Point2 local)
    {
        if (!Enabled()) return;
        // OnDrag now delivers UNCLAMPED local coords — clamp the fraction ourselves.
        _scrubFrac.Value = Frac(local.X);
        _displayFrac.Value = _scrubFrac.Peek();
    }

    void OnCommit()
    {
        // Always release the scrub gate — bailing out with _scrubbing still true would freeze
        // _displayFrac at the abandoned finger position until the next successful commit.
        if (!Enabled()) { OnCanceled(); return; }
        LiveWindow live = _b.Live.Peek();
        bool dvr = live.IsLive && live.HasWindow;
        long dur = _b.DurationMs.Peek();
        if (!dvr && dur <= 0) { OnCanceled(); return; }
        float f = _scrubFrac.Peek();
        // A DVR commit is a position INSIDE the window, not a fraction of a duration — LiveRail clamps it into the
        // window at both ends, because a seek even a millisecond past the moving edge is one the source rejects.
        long targetMs = dvr
            ? LiveRail.Seek(live.SeekableStartMs, live.SeekableEndMs, f)
            : Math.Clamp((long)(f * dur), 0, dur);
        _b.CommitSeek(targetMs);                            // arms the latch, optimistically publishes PositionMs, issues the accurate seek
        _b.PositionFrac.Value = f;                         // optimistic: paint the new position immediately (the DVR-rail fraction, not ms/dur)
        _tickWallMs = Environment.TickCount64;
        _tickPosMs = targetMs;
        _scrubbing.Value = false;                          // release the scrub gate (PositionFrac/interp resume)
        Recompute();
    }

    void OnCanceled()
    {
        _scrubbing.Value = false;
        Recompute();
    }

    float Frac(float x)
    {
        float w = _width > 0f ? _width : 1f;
        return Math.Clamp(x / w, 0f, 1f);
    }

    /// <summary>Pixel dwell of the playhead: <c>durationMs / trackWidthPx</c>, clamped so short tracks stay smooth and
    /// long tracks don't oversample (~44× at panel rate for a multi-minute bar).</summary>
    internal float TickIntervalMs()
    {
        // The rail's span, whichever kind of rail it is: a track's duration, or a DVR window's width.
        LiveWindow live = _b.Live.Peek();
        long dur = live.IsLive && live.HasWindow ? live.WindowMs : _b.DurationMs.Peek();
        float w = _width;
        if (dur <= 0L || w <= 1f) return 100f;
        return Math.Clamp(dur / w, 33f, 250f);
    }
}

/// <summary>Pixel-due stepper for <see cref="SeekBar"/>: mounted only while playing, advances <c>_displayFrac</c> on a
/// <see cref="Component.UseInterval"/> at the playhead's pixel dwell (not <c>FrameClock.Tick</c> — that bit is
/// latency-sensitive and would pin the whole host at panel rate). Unmounted on pause/stop. NEVER re-renders the owner.</summary>
sealed class SeekTicker : Component
{
    public required SeekBar Owner;

    public override Element Render()
    {
        // Parent SeekBar re-renders ~1 Hz on PositionMs — refreshes the interval when duration/width settle.
        UseInterval(() => Owner.Recompute(), Owner.TickIntervalMs());
        return new BoxEl { HitTestVisible = false, Width = 0f, Height = 0f };
    }
}

/// <summary>The rail for a live broadcast with NOTHING TO REWIND — internet radio, and any stream whose seekable window
/// is under <see cref="Wavee.Core.LiveWindow.MinWindowMs"/>. A 2px full-width accent line that breathes.
///
/// <para><b>Why not a rail.</b> There is no position: the source starts where you tuned in and ends when you stop, so
/// every fraction a rail could draw would be a fiction, and every scrub a request the source refuses. The line says
/// "sound is arriving" and nothing else — <c>HitTestVisible = false</c> and <see cref="AutomationRole.None"/> so it is
/// not a slider a pointer or a screen reader can try to drag.</para>
///
/// <para><b>The breathe obeys the no-forever-loop rule.</b> A looping track keeps the frame loop awake for as long as it
/// is seeded, so this one is seeded ONLY while something is audibly live AND someone can see it:</para>
/// <list type="bullet">
/// <item>playing — a paused stream is not arriving, so the line goes flat,</item>
/// <item>the window is ACTIVE (<c>Component.UseIsActive</c> — the engine's <c>Activation.IsActive</c> ambient, false
/// while the app is minimized or power-suspended, AND-folded with this component's own KeepAlive-parked state), so a
/// minimized Wavee is not animating a line nobody is looking at,</item>
/// <item>and motion is not reduced.</item>
/// </list>
/// <para>All three fold into ONE value that picks the keyframe track and the loop flag. Reduced motion is a VALUE here,
/// never a hook branch: <see cref="Component.UseKeyframes"/> is called unconditionally, in the same order, every render
/// — only its keys, duration, loop flag and <see cref="DepKey"/> change. On any of the three flipping, the dep changes
/// and the looping pulse is REPLACED IN PLACE by a finite flat track (opacity → 1), which is what lets the loop-track
/// count fall to zero and the frame loop quiesce. The <c>CoverShimmer</c> pattern, at the slower 3 s cadence a "this is
/// alive" cue wants (a 1 s pulse reads as loading).</para></summary>
sealed class LiveLine : Component
{
    // 0.55 → 1.0 → 0.55: symmetric, so the loop has no seam. Held in statics so a re-seed allocates nothing.
    static readonly Keyframe[] Breathe = [new(0f, 0.55f), new(0.5f, 1f), new(1f, 0.55f)];
    static readonly Keyframe[] Flat = [new(0f, 1f), new(1f, 1f)];
    const float BreatheMs = 3000f;

    readonly PlaybackBridge _b;
    public LiveLine(PlaybackBridge b) { _b = b; }

    public override Element Render()
    {
        bool playing = _b.IsPlaying.Value;          // subscribe → stop breathing the moment the stream is paused
        bool windowActive = UseIsActive().Value;    // subscribe → stop breathing while minimized / suspended / parked
        bool breathe = playing && windowActive && !Motion.ReducedMotion;
        UseKeyframes(AnimChannel.Opacity, breathe ? Breathe : Flat, breathe ? BreatheMs : 1f, breathe,
            DepKey.From(breathe));

        return new BoxEl
        {
            Grow = 1f, Height = SeekBar.HitHeight, Direction = 0, AlignItems = FlexAlign.Center,
            HitTestVisible = false,
            Role = AutomationRole.None,
            Children =
            [
                new BoxEl
                {
                    Grow = 1f, MinWidth = 0f, Height = SeekBar.LiveLineHeight,
                    Corners = CornerRadius4.All(SeekBar.LiveLineHeight * 0.5f),
                    Fill = Tok.AccentDefault,
                    HitTestVisible = false,
                },
            ],
        };
    }
}
