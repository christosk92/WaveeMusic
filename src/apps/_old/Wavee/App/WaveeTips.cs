using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Foundation;   // NodeHandle
using FluentGpu.Localization;
using FluentGpu.Scene;

namespace Wavee;

/// <summary>The app-wide teaching-tip service: one place that decides whether a first-run callout may appear, composes it
/// as a WinUI <c>TeachingTip</c>, anchors it to any element, and remembers that the user acknowledged it. Every feature's
/// tip goes through <see cref="TryShow"/> — no feature owns its own marker key, its own card, or its own scheduling.
///
/// <para><b>Call shape</b> (from the component that owns the anchor):</para>
/// <code>
/// UseEffect(() =>
/// {
///     if (eligible)
///         WaveeTips.TryShow(overlay, services?.Settings, post, WaveeTipIds.DetailTuning,
///             () => anchor.Value, () => Context.Scene, "detail.tuning.tipTitle", "detail.tuning.tipBody");
///     return (Action?)(() => WaveeTips.Close(WaveeTipIds.DetailTuning));   // navigation away: close, don't acknowledge
/// }, eligible);
/// </code>
/// <para>…plus <see cref="Acknowledge"/> wherever using the taught affordance counts as "got it" (e.g. clicking the very
/// button the tip points at).</para>
///
/// <para><b>Rules the service owns</b> (so no consumer can get them wrong):
/// ONE tip at a time process-wide; at most one appearance per tip per launch; never shown again once acknowledged
/// (<c>WaveeSettings.TipsSeen</c>, a single set-valued key for all tips, ever); never steals focus and never scrims
/// (<c>FocusTrap: false</c> + <see cref="DismissBehavior.None"/>, so the page underneath stays fully clickable);
/// scheduled with the double-post so it rises over a page that has already PAINTED; and closed automatically when its
/// anchor leaves the scene (the overlay host's orphaned-owner prune, OverlayHost.cs:571-585) as well as from the
/// consumer's effect cleanup.</para>
///
/// <para><b>Acknowledgement vs close</b> is the load-bearing distinction: <see cref="Acknowledge"/> burns the id
/// (the tip's ✕, or invoking the affordance) and never shows it again; <see cref="Close"/> just takes it down (navigating
/// away, the anchor being evicted from a command bar) and leaves the id unburned, so an unread tip gets one more chance
/// on the next launch. The per-launch latch is what stops it re-opening on the next page in the meantime.</para>
///
/// <para><b>The visual is the ENGINE's control</b>, not app chrome: <see cref="FluentGpu.Controls.TeachingTip"/> in its
/// TARGETED form (<c>TeachingTip.Show(overlay, target, configure)</c>) — the WinUI-parity card (solid tertiary surface,
/// surface stroke, OverlayCornerRadius 8, the 1px top highlight, title 16 SemiBold + subtitle 14, the 40×40 alternate ✕)
/// with the tail pointing at the anchor, opened with <see cref="PopupChrome.TeachingTip"/> so the overlay host supplies
/// the real muxc motion (expand scale Min(0.01, 20/W) → 1 over 300ms cubic-bezier(0.1,0.9,0.2,1); contract 1 → 20/W over
/// 200ms cubic-bezier(0.7,0,1,0.5)). This service owns WHEN a tip appears and WHETHER it ever appears again; the control
/// owns what it looks like.</para>
///
/// <para>Thread confinement: all state below is UI-thread-only (every entry point is called from render, an effect, a
/// posted callback, or a click handler — all on the engine's single UI thread), so no locking.</para></summary>
static class WaveeTips
{
    // ── process-wide state ────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Tips armed since launch (shown, or scheduled and then taken down) — the per-launch half of the gate.</summary>
    static readonly HashSet<string> _armed = new(StringComparer.Ordinal);
    /// <summary>The single tip slot: the id that is scheduled-or-visible, and its handle once it is actually up. Reserved
    /// at ARM time (not at open time) because the double-post spans two frames — without the reservation a second tip
    /// mounting in between would slip through the one-at-a-time rule.</summary>
    static string? _activeId;
    static OverlayHandle? _activeHandle;

    /// <summary>The id currently scheduled or on screen (null = none). Consumers rarely need this; it exists so an
    /// unrelated surface can politely defer its own transient UI while a tip is up.</summary>
    public static string? ActiveId => _activeId;

    /// <summary>True when <paramref name="tipId"/> is scheduled or on screen right now.</summary>
    public static bool IsActive(string tipId) => string.Equals(_activeId, tipId, StringComparison.Ordinal);

    /// <summary>Has the user already acknowledged this tip (any launch)? A null settings seam answers TRUE — see
    /// <see cref="WaveeTipsCore.ShouldShow"/> for why an unpersistable tip is never shown.</summary>
    public static bool IsSeen(IAppSettings? settings, string tipId)
        => settings is null || WaveeTipsCore.Contains(settings.Get(WaveeSettings.TipsSeen), tipId);

    /// <summary>Record the acknowledgement durably (idempotent). Public so a flow can pre-burn a tip it has effectively
    /// taught by other means.</summary>
    public static void MarkSeen(IAppSettings? settings, string tipId)
    {
        if (settings is null || string.IsNullOrEmpty(tipId)) return;
        var current = settings.Get(WaveeSettings.TipsSeen);
        var next = WaveeTipsCore.Add(current, tipId);
        if (!string.Equals(next, current, StringComparison.Ordinal)) settings.Set(WaveeSettings.TipsSeen, next);
    }

    /// <summary>Forget every acknowledgement — the seam a future Settings "Show tips again" row calls (that row is NOT
    /// built here). Clears the per-launch latches too, so tips can appear again without a restart.</summary>
    public static void ResetAll(IAppSettings? settings)
    {
        settings?.Set(WaveeSettings.TipsSeen, "");
        _armed.Clear();
    }

    /// <summary>The acknowledged ids (diagnostics / tests).</summary>
    public static List<string> Seen(IAppSettings? settings)
        => WaveeTipsCore.Parse(settings?.Get(WaveeSettings.TipsSeen));

    /// <summary>Arm the tip <paramref name="tipId"/> against <paramref name="anchor"/>, scheduling it for after the next
    /// PAINTED frame. Returns true when it was armed (the gate passed) — false, and nothing happens, when the tip was
    /// already acknowledged, has already been armed this launch, another tip is scheduled/visible, or the overlay /
    /// settings seams are missing. Safe to call on every render: the latch it takes makes the extra calls no-ops.
    ///
    /// <para><paramref name="anchor"/> is the node thunk the popup follows (capture it with <c>BoxEl.OnRealized</c>),
    /// <paramref name="scene"/> proves that node is still live at open time (pass <c>() =&gt; Context.Scene</c>), and
    /// <paramref name="post"/> is the component's <c>UsePost()</c>. <paramref name="titleKey"/>/<paramref name="bodyKey"/>
    /// are LOC KEYS, resolved when the card renders so a culture switch re-resolves them.</para></summary>
    public static bool TryShow(
        IOverlayService? overlay,
        IAppSettings? settings,
        Action<Action> post,
        string tipId,
        Func<NodeHandle> anchor,
        Func<SceneStore?> scene,
        string titleKey,
        string bodyKey,
        TeachingTip.PlacementMode placement = TeachingTip.PlacementMode.Bottom)
    {
        bool canPresent = overlay is not null && settings is not null && post is not null;
        if (!WaveeTipsCore.ShouldShow(settings?.Get(WaveeSettings.TipsSeen), tipId,
                _armed.Contains(tipId), _activeId is not null, canPresent))
            return false;
        // canPresent == true here, but the compiler cannot see through ShouldShow -- re-prove it for nullable flow.
        if (overlay is null || settings is null || post is null) return false;

        _armed.Add(tipId);
        _activeId = tipId;           // reserve the single slot for the two frames the double-post spans
        _activeHandle = null;

        // TWO nested posts (the SidebarOnboardingChrome precedent): the first lands after this mount's commit, the second
        // after the frame that PAINTED the host — so the user sees the page, then the tip rises over it, and the anchor's
        // layout (a command bar still measuring/fitting its commands) has settled.
        post(() => post(() =>
        {
            if (!IsActive(tipId) || _activeHandle is not null) return;   // released or already up
            var node = anchor();
            var sc = scene();
            // KeepAlive preserves inactive pages as LIVE but PARKED subtrees. A live-only check lets a delayed tip open
            // after navigation and point at whichever node later occupies the old screen coordinates. Parked is just as
            // ineligible as dead: the taught affordance is not on the active page.
            if (sc is null || node.IsNull || !sc.IsLive(node)
                || (sc.Flags(node) & NodeFlags.Parked) != 0) { Release(tipId); return; }

            // The VISUAL is the engine's WinUI-parity control (FluentGpu.Controls/TeachingTip.cs) in its TARGETED form:
            // no trigger chrome of its own, a tail that points at `anchor`, title + subtitle + the 40×40 ✕, light dismiss
            // off, and the muxc expand/contract scale motion supplied by PopupChrome.TeachingTip.
            var handle = TeachingTip.Show(overlay!, anchor, tip =>
            {
                tip.Title = Loc.Get(titleKey);
                tip.Subtitle = Loc.Get(bodyKey);
                tip.PreferredPlacement = placement;
                tip.IsLightDismissEnabled = false;   // WinUI default: only the ✕ / Escape / the caller dismisses
                // The ✕ IS "don't show again" — the only acknowledgement path the control itself owns.
                tip.CloseButtonClick = () => MarkSeen(settings, tipId);
            });
            // A host-less mount (NullOverlayService, e.g. a probe) hands back an inert handle that will never raise
            // ClosedAction — free the single slot now rather than blocking every future tip for the process lifetime.
            if (!handle.IsOpen) { Release(tipId); return; }
            _activeHandle = handle;
            // Fires for EVERY close path — the ✕, our own Close, and the host's orphaned-anchor prune — so the slot can
            // never leak and block every future tip.
            handle.ClosedAction = () => Release(tipId);
        }));
        return true;
    }

    /// <summary>Acknowledge and take down <paramref name="tipId"/> if it is up: the tip's own ✕ path, and the one to call
    /// when the user invokes the affordance the tip points at (using it IS being taught). No-op otherwise.</summary>
    public static void Acknowledge(IAppSettings? settings, string tipId)
    {
        if (!IsActive(tipId)) return;
        MarkSeen(settings, tipId);
        Close(tipId);
    }

    /// <summary>Take down <paramref name="tipId"/> WITHOUT acknowledging it — navigation away, the anchor being evicted,
    /// a host teardown. Call from the consumer's effect cleanup. No-op when that tip is not active.</summary>
    public static void Close(string tipId)
    {
        if (!IsActive(tipId)) return;
        var handle = _activeHandle;
        Release(tipId);
        handle?.Close();
    }

    static void Release(string tipId)
    {
        if (!IsActive(tipId)) return;
        _activeId = null;
        _activeHandle = null;
    }
}
