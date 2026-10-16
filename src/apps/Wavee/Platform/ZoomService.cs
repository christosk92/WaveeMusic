// ── Platform/ZoomService.cs — the ONE owner of the app zoom's rules and verbs ───────────────────────────────────────
//
// Role: CORE (themes plan §3.2, PR C1; extraction X18). The zoom logic used to exist twice: the shell frame's
// large-display control loop plus its debounced persistence (`Shell.UI.cs`) and the Settings > Appearance > Zoom row's
// picks and "Auto (N%)" label (Classic's Settings Appearance tab, `ClassicSettings.Appearance.cs`). The rules are here now; both call `ZoomService`.
//
// THE POLICY (`ZoomAutoPolicy`, `Platform.cs`) stays where it is: it maps a window's BASE extent (client DIP at zoom 1)
// to a plateau. This file is the control loop around it and the verbs that move the zoom.
//
// NO STARTUP RE-ZOOM. The window opens at a fixed client size whatever the persisted zoom is, and the 500 ms loop used
// to move a zoom persisted on another window/monitor to this window's Auto zoom half a second after the first frame.
// `InitialZoom` computes that settled value BEFORE the window exists (from the requested client size, bounded by the
// primary monitor's work area), so the first frame is already final and the loop's first tick finds nothing to do.
// The debounce for later resizes is narrowed to size streams by C1b (below).
//
// THE LARGE-DISPLAY CLAMP AND THE SETTLED ZOOM (themes plan 3.2, PR C1b; owner NQ1 revised).
//   · `ZoomTierReference` is the ONE reference edge: Auto zoom keeps `ZoomAutoPolicy`'s curve, except it never pushes a window
//     whose unzoomed width is Huge (>= 1920) back down into Standard. Theme-independent by construction (no theme is named or
//     read here), so a theme switch never changes the zoom and no theme transaction ever rescales. `AutoZoom`, `InitialZoom`
//     and `AutoSuggestion` all go through it, so the first frame, the loop and the Zoom row's "Auto (N%)" agree.
//   · A SETTLED size (maximize, restore, Snap, a drag release, a DPI or monitor change) is classified by `AutoLoop.Arrive` and
//     applied by the frame in the same layout phase, so there is no 500 ms of the old zoom; only a STREAM of sizes (a second
//     size inside `DebounceMs` of the previous one) waits for the debounce. Nothing is rescaled inside the modal size-move loop.
//   · Manual zoom is bounded below by the Compact floor (`ZoomTierReference.CompactFloor`, 300 DIP): a step whose layout
//     width would fall under it is refused (`ManualStep`, `ClampManual`, `FloorManual`).

using Wavee.Theming;

namespace Wavee;

/// <summary>The monitor the first window opens on, in raw DIP (physical px / the monitor's DPI scale, before any app
/// zoom). Zero = unknown (the query failed, or a test host): nothing bounds the window then.</summary>
public readonly record struct ZoomMonitor(float WorkWidthDip, float WorkHeightDip)
{
    /// <summary>The work area (desktop minus taskbar) is known and non-degenerate.</summary>
    public bool IsKnown => WorkWidthDip > 0f && WorkHeightDip > 0f;
}

/// <summary>The core reference edge the Auto zoom is clamped against (plan 3.2, C1b), and the Manual floor. Both are CORE
/// numbers: not any structure's ladder, so the zoom never depends on the active theme. Pure (BCL plus
/// <see cref="ZoomAutoPolicy"/> and <see cref="FluentGpu.Foundation.ZoomLadder"/>), driven directly by the tests.</summary>
public static class ZoomTierReference
{
    /// <summary>Where Huge begins: the Huge edge of every shipped width ladder (Classic, WinUI, Zune, Panorama). Huge and
    /// Huge 4K are one band for zoom.</summary>
    public const float HugeFrom = 1920f;

    /// <summary>The smallest layout width the app supports: the window minimum, and every structure's
    /// <see cref="TierPolicy.WindowFloor"/>. Manual zoom stops before it.</summary>
    public const float CompactFloor = TierPolicy.WindowFloor;

    // Layout widths are compared with the same noise tolerance as a ladder step.
    const float LayoutEpsilon = 0.01f;
    const float ZoomEpsilon = 0.001f;

    /// <summary>The Auto (or Dense) zoom for a window whose extent AT ZOOM 1 is <paramref name="baseWidth"/> ×
    /// <paramref name="baseHeight"/>: <c>W &lt; 1920 ? Suggest : min(Suggest, plateauDown(W / 1920))</c>. Both arms are
    /// non-decreasing in W and meet at 1920 (both 1.0), so widening a window never lowers the zoom (the contract of
    /// <see cref="ZoomAutoPolicy"/>), and the layout width <c>W / zoom</c> is at least 1920 for every W of 1920 or more.
    /// A degenerate or non-finite extent is the policy's neutral 1.0.</summary>
    public static float Suggest(float baseWidth, float baseHeight, ZoomAutoMode mode)
    {
        float suggested = ZoomAutoPolicy.Suggest(baseWidth, baseHeight, mode);
        if (!float.IsFinite(baseWidth) || !float.IsFinite(baseHeight) || baseWidth < HugeFrom) return suggested;
        return MathF.Min(suggested, ZoomAutoPolicy.PlateauAtOrBelow(baseWidth / HugeFrom));
    }

    /// <summary>A Manual <paramref name="zoom"/> brought back inside the floor: unchanged when the layout would stay at or above
    /// <see cref="CompactFloor"/> (and always at 100 % or below, which never narrows the layout), else the largest ladder step
    /// that fits, never below 100 %. An unknown width bounds nothing.</summary>
    public static float FloorManual(float zoom, float baseWidth)
    {
        zoom = FluentGpu.Foundation.ZoomLadder.Clamp(zoom);
        if (zoom <= FluentGpu.Foundation.ZoomLadder.Default + ZoomEpsilon) return zoom;
        if (!(baseWidth > 0f) || !float.IsFinite(baseWidth)) return zoom;
        if (baseWidth / zoom >= CompactFloor - LayoutEpsilon) return zoom;
        float limit = baseWidth / CompactFloor;
        float best = FluentGpu.Foundation.ZoomLadder.Default;
        foreach (float step in FluentGpu.Foundation.ZoomLadder.Steps)
            if (step <= limit + ZoomEpsilon && step <= zoom + ZoomEpsilon && step > best) best = step;
        return best;
    }

    /// <summary>The zoom a Manual change from <paramref name="liveZoom"/> to <paramref name="target"/> lands on. A change that
    /// does not raise the zoom is never refused; a raise stops at the floor (<see cref="FloorManual"/>) and never ends below
    /// where the window already is, so a refused step stays put.</summary>
    public static float ClampManual(float target, float liveZoom, float baseWidth)
    {
        target = FluentGpu.Foundation.ZoomLadder.Clamp(target);
        liveZoom = FluentGpu.Foundation.ZoomLadder.Clamp(liveZoom);
        if (target <= liveZoom + ZoomEpsilon) return target;
        return MathF.Max(FloorManual(target, baseWidth), liveZoom);
    }
}

/// <summary>The app zoom's control loop and verbs. The pure members (<see cref="AutoZoom"/>, <see cref="InitialZoom"/>,
/// <see cref="AutoSuggestion"/>, <see cref="ManualStep"/>) touch no engine or settings state, so the tests drive them directly.</summary>
public static partial class ZoomService
{
    /// <summary>A STREAM of window sizes (each within this of the previous one) waits for this much quiet before the zoom
    /// re-resolves (trailing edge); an isolated settled size does not wait (<see cref="AutoLoop.Arrive"/>). The DIP re-layout
    /// itself is never debounced — only the zoom half is. Also what the persistence debounce waits.</summary>
    public const float DebounceMs = 500f;

    /// <summary>Zooms within this of each other are the same zoom (the no-op guard that actually stops re-entry).</summary>
    public const float Epsilon = 0.004f;

    public enum ZoomStepKind : byte
    {
        /// <summary>Touch nothing.</summary>
        None,
        /// <summary>Apply <see cref="ZoomDecision.Zoom"/>.</summary>
        Apply,
        /// <summary>Something ELSE moved the zoom while in Auto (a chord, the wheel, the palette): flip the stored mode
        /// to Manual instead of clobbering the user's pick next tick.</summary>
        PinManual,
    }

    /// <summary>What a window extent is to the Auto loop (<see cref="AutoLoop.Arrive"/>).</summary>
    public enum SizeArrival : byte
    {
        /// <summary>The same size as before, or a size that cannot be used: do nothing.</summary>
        Unchanged,
        /// <summary>An isolated size, or the first one after the modal size-move loop: step now.</summary>
        Settled,
        /// <summary>One of a stream of sizes: the trailing debounce steps when the stream ends.</summary>
        Stream,
        /// <summary>Inside the OS modal size-move loop: nothing is rescaled.</summary>
        Held,
    }

    /// <summary>One tick's answer plus the loop's carried state (the zoom THIS policy last applied).</summary>
    public readonly record struct ZoomDecision(ZoomStepKind Kind, float Zoom, float LastAuto, bool Seeded);

    // ══ 1. THE RULES (pure) ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The auto-zoom control loop, as a pure step. <paramref name="baseWidth"/>/<paramref name="baseHeight"/>
    /// are the window's DIP extent AT ZOOM 1 (<c>viewportDip x Viewport.Zoom</c> — zoom-invariant by construction), so
    /// a re-entrant tick from our own apply computes the identical suggestion and the no-op guard returns.
    /// <para>"Someone else moved the zoom" is detected by comparing the LIVE zoom against this policy's OWN last pick,
    /// not against the suggestion — the only way a static zoom verb with no settings reference can opt a user out of
    /// Auto.</para></summary>
    public static ZoomDecision AutoZoom(ZoomAutoMode mode, float baseWidth, float baseHeight, float liveZoom,
        float lastAuto, bool seeded)
    {
        // A minimised window reports 0 and a corrupt extent may be NaN or infinite: neither is a size to zoom for.
        if (mode == ZoomAutoMode.Manual || !(baseWidth > 0f) || !(baseHeight > 0f)
            || !float.IsFinite(baseWidth) || !float.IsFinite(baseHeight))
            return new ZoomDecision(ZoomStepKind.None, liveZoom, lastAuto, seeded);
        float suggested = ZoomTierReference.Suggest(baseWidth, baseHeight, mode);
        if (MathF.Abs(suggested - liveZoom) <= Epsilon)
            return new ZoomDecision(ZoomStepKind.None, liveZoom, liveZoom, true);
        if (seeded && MathF.Abs(liveZoom - lastAuto) > Epsilon)
            return new ZoomDecision(ZoomStepKind.PinManual, liveZoom, lastAuto, seeded);
        return new ZoomDecision(ZoomStepKind.Apply, suggested, suggested, true);
    }

    /// <summary>The zoom the first frame opens at: the value the loop WOULD settle on for the first window, computed
    /// before the window exists. It is the loop's own first tick (<see cref="AutoZoom"/>, unseeded, live = the persisted
    /// zoom after the engine's clamp) over the window's base extent: the requested client size (raw DIP — the window is
    /// created at that size whatever the zoom is), bounded by the monitor's work area. So it follows the same large-display
    /// clamp as every later resize (<see cref="ZoomTierReference"/>). Manual mode keeps the persisted zoom, held inside the
    /// Compact floor (<see cref="ZoomTierReference.FloorManual"/>).</summary>
    public static float InitialZoom(ZoomAutoMode mode, float persistedZoom, float windowWidthDip, float windowHeightDip,
        in ZoomMonitor monitor)
    {
        float live = FluentGpu.Foundation.ZoomLadder.Clamp(persistedZoom);
        float width = windowWidthDip, height = windowHeightDip;
        if (monitor.IsKnown)
        {
            width = MathF.Min(width, monitor.WorkWidthDip);
            height = MathF.Min(height, monitor.WorkHeightDip);
        }
        if (mode == ZoomAutoMode.Manual) return ZoomTierReference.FloorManual(live, width);
        var settled = AutoZoom(mode, width, height, live, lastAuto: 0f, seeded: false);
        return settled.Kind == ZoomStepKind.Apply ? settled.Zoom : live;
    }

    /// <summary>What Auto would choose for a window with this LIVE (already zoomed) viewport — the Zoom row's "Auto (N%)"
    /// label. The policy takes BASE dips, so the live viewport is multiplied back by the live zoom.</summary>
    public static float AutoSuggestion(float viewportWidth, float viewportHeight, float liveZoom)
        => ZoomTierReference.Suggest(viewportWidth * liveZoom, viewportHeight * liveZoom, ZoomAutoMode.Auto);

    /// <summary>The zoom a Manual chord, wheel notch or palette row lands on: one ladder rung from <paramref name="liveZoom"/>
    /// (<paramref name="direction"/> 0 resets to 100 %), refused when it would take the layout under the Compact floor on a
    /// window <paramref name="baseWidth"/> wide at zoom 1 (<see cref="ZoomTierReference.ClampManual"/>).</summary>
    public static float ManualStep(float liveZoom, int direction, float baseWidth)
    {
        float target = direction == 0 ? FluentGpu.Foundation.ZoomLadder.Default
            : direction > 0 ? FluentGpu.Foundation.ZoomLadder.In(liveZoom)
            : FluentGpu.Foundation.ZoomLadder.Out(liveZoom);
        return ZoomTierReference.ClampManual(target, liveZoom, baseWidth);
    }

    /// <summary>The window's width at zoom 1 (client DIP x live zoom) as the shell frame last saw it; 0 until the first usable
    /// size. UI thread only. The Manual floor reads it, because a verb has no viewport of its own. A minimised or degenerate size
    /// leaves the last usable one.</summary>
    public static float WindowBaseWidth { get; private set; }

    /// <summary>Record the window's width at zoom 1 (<see cref="WindowBaseWidth"/>); a non-positive or non-finite width is ignored.</summary>
    public static void NoteWindow(float baseWidth)
    {
        if (float.IsFinite(baseWidth) && baseWidth > 0f) WindowBaseWidth = baseWidth;
    }

    // ══ 2. THE STORED STATE ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A review capture's Manual zoom (`--screenshot --zoom`, or `--offscreen`'s implied 1: <c>Diagnostics.CaptureOptions</c>);
    /// 0 for none. Set once by <c>Shell.Run</c> before the window exists. While set, <see cref="Mode"/> answers Manual and
    /// <see cref="PersistedLevel"/> answers it, so the first frame lays out at it and the Auto loop stands down; the profile's
    /// stored zoom is never rewritten for it.</summary>
    public static float RunZoom { get; set; }

    /// <summary>The persisted picker mode, clamped like every reader (a value this build does not define is not a crash).</summary>
    public static ZoomAutoMode Mode => RunZoom > 0f ? ZoomAutoMode.Manual
        : (ZoomAutoMode)Math.Clamp(Platform.Settings.Get(Platform.Keys.ZoomMode), 0, 2);

    /// <summary>The persisted zoom level.</summary>
    public static float PersistedLevel => RunZoom > 0f ? RunZoom : Platform.Settings.Get(Platform.Keys.ZoomLevel);

    // ══ 3. THE VERBS ═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Set the LIVE zoom (the engine clamps it to its ladder range and folds it into the window scale). Persistence
    /// is separate: <see cref="PersistSettled"/> for a held chord or a wheel spin, <see cref="PickAuto"/>/<see cref="PickManual"/>
    /// for a deliberate pick.</summary>
    public static void Apply(float zoom) => FluentGpu.FluentApp.SetZoom(zoom);

    /// <summary><see cref="Apply"/> as a callback for the UI poster.</summary>
    public static Action Applier(float zoom) => () => Apply(zoom);

    /// <summary>Someone else moved the zoom while Auto owned it: keep their pick (mode = Manual) instead of undoing it.</summary>
    public static void PinManual() => Platform.Settings.Set(Platform.Keys.ZoomMode, (int)ZoomAutoMode.Manual);

    /// <summary>Persist a SETTLED zoom (the host debounces so a held chord writes once); a no-op within <see cref="Epsilon"/>.</summary>
    public static void PersistSettled(float zoom)
    {
        if (MathF.Abs(Platform.Settings.Get(Platform.Keys.ZoomLevel) - zoom) > Epsilon)
            Platform.Settings.Set(Platform.Keys.ZoomLevel, zoom);
    }

    /// <summary>The Zoom row's head item: mode = Auto (never Dense), apply <paramref name="suggestion"/> at once and persist it.</summary>
    public static void PickAuto(float suggestion) => Pick(ZoomAutoMode.Auto, suggestion);

    /// <summary>The Zoom row's rung: mode = Manual, apply <paramref name="zoom"/> at once and persist it. A rung that would take
    /// the layout under the Compact floor on this window applies (and persists) the largest rung that fits instead, never
    /// lowering the zoom the window already has.</summary>
    public static void PickManual(float zoom)
        => Pick(ZoomAutoMode.Manual, ZoomTierReference.ClampManual(zoom, FluentGpu.FluentApp.Zoom, WindowBaseWidth));

    static void Pick(ZoomAutoMode mode, float zoom)
    {
        Platform.Settings.Set(Platform.Keys.ZoomMode, (int)mode);
        Apply(zoom);   // live, instant
        Platform.Settings.Set(Platform.Keys.ZoomLevel, zoom);
    }

    // ══ 4. THE LOOP ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The shell frame's auto-zoom loop state: the zoom this policy last applied and whether it has been seeded.
    /// One per frame; <see cref="Step"/> runs once per resize-settled base extent.</summary>
    public sealed class AutoLoop
    {
        /// <summary>Two base extents closer than this on both axes are the same size (the zoom-invariant extent of our own
        /// re-entrant tick differs from the last by float noise only).</summary>
        const float SizeEpsilon = 0.05f;

        float _lastAuto;
        bool _seeded;
        float _arrivedWidth, _arrivedHeight;
        long _arrivedAtMs;
        bool _hasArrived;
        bool _heldInModal;
        bool _settleNextChange;

        /// <summary>The newest usable extent <see cref="Arrive"/> recorded (never one seen inside the modal loop); false before
        /// the first. What the trailing step of a stream, and a zoom change noticed between sizes, run against.</summary>
        public bool TryGetLatest(out float baseWidth, out float baseHeight)
        {
            baseWidth = _arrivedWidth;
            baseHeight = _arrivedHeight;
            return _hasArrived;
        }

        /// <summary>Classify the window extent (at zoom 1) the frame sees now, at <paramref name="nowMs"/> (a monotonic
        /// millisecond clock), and remember it. The answer says WHEN <see cref="Step"/> may run:
        /// <list type="bullet">
        /// <item><see cref="SizeArrival.Settled"/>: step now, in this layout phase (an isolated size: a first read, a maximize,
        /// a restore, a Snap, a DPI or monitor change, or the first size after the modal size-move loop ended).</item>
        /// <item><see cref="SizeArrival.Stream"/>: another size inside <see cref="DebounceMs"/> of the previous one: leave it to
        /// the trailing debounce, which waits for the stream to end.</item>
        /// <item><see cref="SizeArrival.Held"/>: inside the OS modal size-move loop: nothing is rescaled there; the loop's end
        /// is a Settled arrival whatever the size did.</item>
        /// <item><see cref="SizeArrival.Unchanged"/>: nothing to do: the same size (our own apply re-fires the frame effect
        /// with the same zoom-invariant extent), or a size that is not positive and finite (a minimised window reports 0, a
        /// headless host has not published yet), which is ignored so a restore to the same size changes nothing.</item>
        /// </list></summary>
        public SizeArrival Arrive(long nowMs, float baseWidth, float baseHeight, bool inModalLoop)
        {
            if (!(baseWidth > 0f) || !(baseHeight > 0f) || !float.IsFinite(baseWidth) || !float.IsFinite(baseHeight))
                return SizeArrival.Unchanged;
            if (inModalLoop)
            {
                _heldInModal = true;
                return SizeArrival.Held;
            }
            bool exitedModal = _heldInModal;
            _heldInModal = false;
            bool sameSize = _hasArrived
                && MathF.Abs(baseWidth - _arrivedWidth) <= SizeEpsilon && MathF.Abs(baseHeight - _arrivedHeight) <= SizeEpsilon;
            if (sameSize)
            {
                if (!exitedModal) return SizeArrival.Unchanged;
                // The loop ended on the size it started on (a move, a monitor hop), or its final size has not landed yet: step
                // once, and let the size that follows count as settled too (this answer is not a size, so it stamps no time).
                _settleNextChange = true;
                return SizeArrival.Settled;
            }
            bool stream = !exitedModal && !_settleNextChange && _hasArrived && nowMs - _arrivedAtMs < (long)DebounceMs;
            _settleNextChange = false;
            _hasArrived = true;
            _arrivedWidth = baseWidth;
            _arrivedHeight = baseHeight;
            _arrivedAtMs = nowMs;
            return stream ? SizeArrival.Stream : SizeArrival.Settled;
        }

        /// <summary>One tick: decide (<see cref="AutoZoom"/>), carry the state, then apply through <paramref name="post"/> or
        /// pin Manual. Returns what it decided.</summary>
        public ZoomStepKind Step(ZoomAutoMode mode, float baseWidth, float baseHeight, float liveZoom, Action<Action> post)
        {
            var d = AutoZoom(mode, baseWidth, baseHeight, liveZoom, _lastAuto, _seeded);
            _lastAuto = d.LastAuto;
            _seeded = d.Seeded;
            if (d.Kind == ZoomStepKind.Apply) post(Applier(d.Zoom));
            else if (d.Kind == ZoomStepKind.PinManual) PinManual();
            return d.Kind;
        }
    }
}
