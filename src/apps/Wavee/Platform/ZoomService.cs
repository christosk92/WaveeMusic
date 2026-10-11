// ── Platform/ZoomService.cs — the ONE owner of the app zoom's rules and verbs ───────────────────────────────────────
//
// Role: CORE (themes plan §3.2, PR C1; extraction X18). The zoom logic used to exist twice: the shell frame's
// large-display control loop plus its debounced persistence (`Shell.UI.cs`) and the Settings > Appearance > Zoom row's
// picks and "Auto (N%)" label (`Settings.UI.Appearance.cs`). The rules are here now; both call `ZoomService`.
//
// THE POLICY (`ZoomAutoPolicy`, `Platform.cs`) stays where it is: it maps a window's BASE extent (client DIP at zoom 1)
// to a plateau. This file is the control loop around it and the verbs that move the zoom.
//
// NO STARTUP RE-ZOOM. The window opens at a fixed client size whatever the persisted zoom is, and the 500 ms loop used
// to move a zoom persisted on another window/monitor to this window's Auto zoom half a second after the first frame.
// `InitialZoom` computes that settled value BEFORE the window exists (from the requested client size, bounded by the
// primary monitor's work area), so the first frame is already final and the loop's first tick finds nothing to do.
// The debounce for later resizes is unchanged (C1b narrows it to size streams).

namespace Wavee;

/// <summary>The monitor the first window opens on, in raw DIP (physical px / the monitor's DPI scale, before any app
/// zoom). Zero = unknown (the query failed, or a test host): nothing bounds the window then.</summary>
public readonly record struct ZoomMonitor(float WorkWidthDip, float WorkHeightDip)
{
    /// <summary>The work area (desktop minus taskbar) is known and non-degenerate.</summary>
    public bool IsKnown => WorkWidthDip > 0f && WorkHeightDip > 0f;
}

/// <summary>The app zoom's control loop and verbs. The pure members (<see cref="AutoZoom"/>, <see cref="InitialZoom"/>,
/// <see cref="AutoSuggestion"/>) touch no engine or settings state, so the tests drive them directly.</summary>
public static partial class ZoomService
{
    /// <summary>The zoom re-resolve waits for this much resize quiet (trailing edge). The DIP re-layout itself is
    /// never debounced — only the zoom half is.</summary>
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
        if (mode == ZoomAutoMode.Manual || baseWidth <= 0f || baseHeight <= 0f)
            return new ZoomDecision(ZoomStepKind.None, liveZoom, lastAuto, seeded);
        float suggested = ZoomAutoPolicy.Suggest(baseWidth, baseHeight, mode);
        if (MathF.Abs(suggested - liveZoom) <= Epsilon)
            return new ZoomDecision(ZoomStepKind.None, liveZoom, liveZoom, true);
        if (seeded && MathF.Abs(liveZoom - lastAuto) > Epsilon)
            return new ZoomDecision(ZoomStepKind.PinManual, liveZoom, lastAuto, seeded);
        return new ZoomDecision(ZoomStepKind.Apply, suggested, suggested, true);
    }

    /// <summary>The zoom the first frame opens at: the value the 500 ms loop WOULD settle on for the first window, computed
    /// before the window exists. It is the loop's own first tick (<see cref="AutoZoom"/>, unseeded, live = the persisted
    /// zoom after the engine's clamp) over the window's base extent: the requested client size (raw DIP — the window is
    /// created at that size whatever the zoom is), bounded by the monitor's work area. Manual mode keeps the persisted zoom.</summary>
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
        var settled = AutoZoom(mode, width, height, live, lastAuto: 0f, seeded: false);
        return settled.Kind == ZoomStepKind.Apply ? settled.Zoom : live;
    }

    /// <summary>What Auto would choose for a window with this LIVE (already zoomed) viewport — the Zoom row's "Auto (N%)"
    /// label. The policy takes BASE dips, so the live viewport is multiplied back by the live zoom.</summary>
    public static float AutoSuggestion(float viewportWidth, float viewportHeight, float liveZoom)
        => ZoomAutoPolicy.Suggest(viewportWidth * liveZoom, viewportHeight * liveZoom, ZoomAutoMode.Auto);

    // ══ 2. THE STORED STATE ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The persisted picker mode, clamped like every reader (a value this build does not define is not a crash).</summary>
    public static ZoomAutoMode Mode => (ZoomAutoMode)Math.Clamp(Platform.Settings.Get(Platform.Keys.ZoomMode), 0, 2);

    /// <summary>The persisted zoom level.</summary>
    public static float PersistedLevel => Platform.Settings.Get(Platform.Keys.ZoomLevel);

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

    /// <summary>The Zoom row's rung: mode = Manual, apply <paramref name="zoom"/> at once and persist it.</summary>
    public static void PickManual(float zoom) => Pick(ZoomAutoMode.Manual, zoom);

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
        float _lastAuto;
        bool _seeded;

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
