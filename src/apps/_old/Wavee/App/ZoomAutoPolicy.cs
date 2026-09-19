using System;

namespace Wavee;

/// <summary>The zoom picker's three states (docs/plans/wavee/large-display-scaling.md §3.2). Stored as an int (the
/// ThemeMode/RowDensity/TrackRowStyle convention — <c>AppDataSettings</c> has no enum arm), so a value this build
/// doesn't define clamps rather than throws. <see cref="Auto"/>/<see cref="Dense"/> re-derive
/// <c>appearance.zoom</c> from the display (<see cref="ZoomAutoPolicy.Suggest"/>); <see cref="Manual"/> is the
/// browser-style Ctrl+± ladder alone, unmanaged by this policy.</summary>
public enum ZoomAutoMode { Auto = 0, Manual = 1, Dense = 2 }

/// <summary>§3.2 — derives the app zoom from the window's DIP extent against the app's own design box, so a single
/// "one number for every display" zoom stops being wrong on a 2×-DIP-wider monitor. Engine-free by construction
/// (BCL only) — the <c>SetupGating</c>/<c>SidebarBootstrap</c> pattern — so it is source-included into
/// <c>Wavee.Tests.csproj</c> and <c>ZoomAutoPolicyTests</c> drives the REAL decision, not a copy of it.
///
/// <para>THE DESIGN BOX IS NOT A FRESH NUMBER. <see cref="DesignW"/> is
/// <c>Wavee.Design.WaveeTokens.WaveeSize.PageMaxW</c> — the page-measure cap every page already centres at,
/// pinned by <c>DesignTokenConvergenceTests.PageMeasure_IsOneNumber</c> — kept here as a literal (not a direct
/// reference) only because this file must stay BCL-only to source-include cleanly; <c>ZoomAutoPolicyTests</c>
/// cross-checks the two constants stay equal. <see cref="DesignH"/> is <c>DetailShell</c>'s own tall-hero gate
/// (<c>winH >= 900f ? TitleLarge : Title</c>, <c>DetailShell.cs</c>) — the app's OTHER stated design number, with
/// no <c>WaveeSize</c> twin to cross-check against, so this literal IS its source of truth.</para>
///
/// <para>BOTH AXES BIND. Width alone would pick a zoom that DEMOTES structural decisions the app already made on
/// height (the tall-hero rung, the nav-pane wide tier) purely because a monitor happens to be short — the failure
/// mode of every naive "match my display" auto-zoom. Taking <c>min()</c> over the two axis ratios makes the
/// policy incapable of buying width by giving up height, or vice versa.</para>
///
/// <para><see cref="Suggest"/> takes BASE dips — client px ÷ the OS DPI scale ALONE, at zoom 1 — never the live,
/// already-zoomed viewport. Feeding the live viewport back in would close a control loop that converges on the
/// design box regardless of what the display actually is, which is exactly wrong: two very different panels that
/// happen to present the same base box must get the SAME answer (DPI independence), and a monitor with twice the
/// DIPs must never suggest a SMALLER zoom (monotonicity). Callers recover the base extent without a new engine
/// seam: <c>baseDip = viewportDip * zoom</c>, because <c>viewportDip = clientPx / (osDpiScale * zoom)</c> — both
/// <c>Viewport.Size</c> (the live DIP viewport) and <c>Viewport.Zoom</c> are engine Contexts already, and reading
/// <c>Viewport.Zoom</c> here is the engine's own sanctioned "display-only" use (a POLICY INPUT, never a
/// coordinate conversion).</para></summary>
public static class ZoomAutoPolicy
{
    /// <summary>= <c>WaveeSize.PageMaxW</c> — see the type doc for why this is a literal, not a reference.</summary>
    public const float DesignW = 1600f;

    /// <summary>= <c>DetailShell</c>'s tall-hero gate (<c>winH >= 900f</c>) — see the type doc.</summary>
    public const float DesignH = 900f;

    /// <summary>Never suggest more than 200%: past this, on any real panel measured for this document, the DIP
    /// viewport drops below the design box on at least one axis (the §3.2 worked table's last row).</summary>
    public const float Ceiling = 2f;

    /// <summary>The floor <see cref="ZoomAutoMode.Dense"/> may suggest below 100% — Slack's 80% large-monitor
    /// case, snapped to this policy's own plateau set. <see cref="ZoomAutoMode.Auto"/> never goes below 100%.</summary>
    public const float DenseFloor = 0.75f;

    /// <summary>The plateau-clean subset of the engine's <c>ZoomLadder.Steps</c> — Microsoft's 4-epx rule lands on
    /// whole pixels only at the 100/125/150/175/200% plateaus; the ladder's 0.67/0.8/0.9/1.1 rungs put a 4-DIP
    /// metric on a fractional pixel and stay available to a MANUAL pick only. <c>ZoomAutoPolicyTests</c>
    /// cross-checks every member against the live ladder.</summary>
    static readonly float[] Plateaus = [0.75f, 1f, 1.25f, 1.5f, 1.75f, 2f];

    /// <summary>The neutral zoom returned for a degenerate input (a headless/pre-publish read, or a corrupt
    /// base extent) — the engine's own <c>ZoomLadder.Default</c>, kept as a literal for the same BCL reason as
    /// <see cref="DesignW"/>.</summary>
    const float NeutralZoom = 1f;

    // Plateau-membership comparisons tolerate float noise exactly like ZoomLadder.Snap's own Epsilon.
    const float Epsilon = 1e-4f;

    /// <summary>The zoom that makes THIS window's DIP viewport match the design box — see the type doc for the
    /// full contract (base, not live, dips; both axes bind).</summary>
    public static float Suggest(float baseW, float baseH, ZoomAutoMode mode)
    {
        if (!float.IsFinite(baseW) || !float.IsFinite(baseH) || baseW <= 0f || baseH <= 0f) return NeutralZoom;
        float ratio = MathF.Min(baseW / DesignW, baseH / DesignH);
        float lo = mode == ZoomAutoMode.Dense ? DenseFloor : NeutralZoom;
        return SnapPlateauDown(Math.Clamp(ratio, lo, Ceiling));
    }

    /// <summary>Snap DOWN to the nearest plateau at or below <paramref name="ratio"/> — §3.2's table is explicit
    /// that 1.20 → 100% and 1.55 → 150%, never the nearer-by-distance rung. <paramref name="ratio"/> is already
    /// clamped to <c>[lo, Ceiling]</c> by the caller, both of which are themselves plateau members, so this always
    /// finds a member at or below it.</summary>
    static float SnapPlateauDown(float ratio)
    {
        float best = Plateaus[0];
        for (int i = 0; i < Plateaus.Length; i++)
            if (Plateaus[i] <= ratio + Epsilon) best = Plateaus[i];
        return best;
    }

    /// <summary>Monotonic "has the zoom-mode migration run" guard — the SidebarBootstrapVersion/SetupBootstrapVersion
    /// precedent (IAppSettings has no key-exists probe, so this is the only way to tell "never written" from
    /// "written as the default", which is what lets a factory-reset settings store re-arm the migration cleanly).
    /// Bump this AND extend <see cref="MigrateMode"/> when a future release needs another one-time zoom-mode step.</summary>
    public const int MigrationTargetVersion = 1;

    /// <summary>One-shot, run at settings load (<c>Program.cs</c>, beside <c>SidebarBootstrap.Run</c>/
    /// <c>SetupBootstrap.Run</c> — same ordering rule, before anything reads <c>appearance.zoom.mode</c>): a FRESH
    /// install leaves <c>appearance.zoom.mode</c> at its own default (<see cref="ZoomAutoMode.Auto"/>) — nothing to
    /// write, since <c>appearance.zoom</c> is also still at its default 1.0. An UPGRADE that already has a
    /// non-1.0 <c>appearance.zoom</c> stored is pinned to <see cref="ZoomAutoMode.Manual"/> so this policy can
    /// never silently override a zoom the user already picked — a user who already chose 125% must not wake up
    /// to 150%. An upgrade sitting exactly at 1.0 (never touched the picker, or deliberately reset to 100%) is
    /// indistinguishable from a fresh install and is left on Auto, which is the better default for that install
    /// too.</summary>
    public static void MigrateMode(IAppSettings settings)
    {
        if (settings.Get(WaveeSettings.ZoomModeBootstrapVersion) >= MigrationTargetVersion) return;
        float stored = settings.Get(WaveeSettings.ZoomLevel);
        if (MathF.Abs(stored - NeutralZoom) > 0.004f)
            settings.Set(WaveeSettings.ZoomMode, (int)ZoomAutoMode.Manual);
        settings.Set(WaveeSettings.ZoomModeBootstrapVersion, MigrationTargetVersion);
    }
}
