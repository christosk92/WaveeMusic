namespace Wavee.Features.Detail;

/// <summary>Shared detail-page width breakpoints with resize hysteresis (butter-smooth resize v2 §5). Pure static —
/// source-included by Wavee.Tests.</summary>
public static class DetailLayoutBreakpoints
{
    public const float TierHysteresisDip = 24f;
    public const float ModeHysteresisDip = 24f;

    public static int NominalTierFor(float w) =>
        w <= 0f ? 0 : w >= 860f ? 0 : w >= 720f ? 1 : w >= 560f ? 2 : w >= 440f ? 3 : w >= 340f ? 4 : w >= 300f ? 5 : 6;

    /// <summary>Safe pre-measure seed from the window viewport, so a 360-DIP launch never composes the wide table for
    /// its first frame.</summary>
    public static int InitialTierForViewport(float viewportWidth) => NominalTierFor(viewportWidth);

    // ── the WINDOW-viewport-vs-PAGE-width gap ───────────────────────────────────────────────────────────────────────
    // The detail page's own content column sits inside the shell's nav pane, so the WINDOW viewport a pre-measure seed
    // reads (Ctx Viewport.Size) overstates the page's actual width by roughly a sidebar. Left uncorrected, a window a
    // little above one of this file's breakpoints can seed the WIDE arm (rail / two-column) for its first composed
    // frame while the real page — narrower by the sidebar — can only hold the narrower one; the very next Measure then
    // flips it, which is exactly the hero-remount flicker this estimate exists to prevent.
    //
    // ShellResponsiveLayout.NavPaneNarrowW (240) is the sidebar's own default/minimum footprint (SidebarPreferences
    // seeds the pane at it pre-measure, and it is also the width the shell's nav-pane ladder actually holds across the
    // window-width band where this file's OWN breakpoints (560/660/820) fall — the ladder only steps wider at 1400+).
    // Deliberately the sidebar's SMALLEST plausible width, not its live (possibly wider, user-resized) one: a too-SMALL
    // allowance can make this estimate OVERSHOOT the real page width and seed a too-wide arm (the failure mode above);
    // a too-LARGE allowance only makes it undershoot, which just seeds a narrower arm than strictly needed and self-
    // corrects at the very next Measure (this file's <see cref="ModeFor"/> / <see cref="TierFor"/> hysteresis only
    // ever widens on a genuine subsequent measurement, never on a stale seed).
    public const float ShellChromeAllowanceDip = ShellResponsiveLayout.NavPaneNarrowW;

    /// <summary>A pre-measure PAGE width from the WINDOW viewport (see <see cref="ShellChromeAllowanceDip"/>) — what
    /// <see cref="InitialModeForViewport"/> / <see cref="InitialTierForViewport"/> and the vertical hero's pre-measure
    /// geometry should seed from instead of the raw viewport width.</summary>
    public static float EstimatePageWidthFromViewport(float viewportWidth)
        => MathF.Max(0f, viewportWidth - ShellChromeAllowanceDip);

    /// <summary>Narrow (drop a column) immediately; re-admit a column only once the width clears the threshold by
    /// <see cref="TierHysteresisDip"/> — the safe asymmetry, since the cost of the wrong guess in the widening
    /// direction is a column set the pane cannot hold.
    ///
    /// <paramref name="initialized"/> false ⇒ the caller has not measured yet, so <paramref name="prev"/> is a
    /// construction default / a pre-measure viewport seed rather than a tier the user has actually seen: take the
    /// nominal tier outright and let hysteresis start from there. (Mirrors <see cref="ModeFor"/>'s first-measure rule.
    /// Without it, whether the first real measure is honoured depends on which side of the seed it lands on.)</summary>
    public static int TierFor(float w, int prev, bool initialized = true)
    {
        if (w <= 0f) return prev;
        if (!initialized) return NominalTierFor(w);
        int nominal = NominalTierFor(w);
        if (nominal >= prev) return nominal;
        int dipped = NominalTierFor(w - TierHysteresisDip);
        return dipped < prev ? dipped : prev;
    }

    public const int VerticalMode = 3;
    public const float VerticalEnterW = 540f;
    public const float VerticalExitW = 580f;
    public const float TwoColumnContentMinW = 300f;

    /// <summary>The ultra-narrow vertical page may use the track table's tier-6 layout all the way down. Retain the
    /// 300-DIP guard only for transient/two-column frames whose active column set still needs it.</summary>
    public static float ContentMinWidthForMode(int mode)
        => mode == VerticalMode ? 0f : TwoColumnContentMinW;

    public static int NominalModeFor(float w) =>
        w <= 0f ? 0 : w >= 820f ? 0 : w >= 660f ? 1 : w >= 560f ? 2 : VerticalMode;

    /// <summary>Pre-measure page-system seed. Ultra-narrow launches choose Vertical before the first bounds callback.</summary>
    public static int InitialModeForViewport(float viewportWidth) => NominalModeFor(viewportWidth);

    /// <summary>820/660 crossings use <see cref="ModeHysteresisDip"/>; the 540/580 vertical band is unchanged.</summary>
    public static int ModeFor(float w, int currentMode, bool initialized)
    {
        if (w <= 0f) return currentMode;
        if (!initialized) return NominalModeFor(w);
        if (currentMode == VerticalMode) return w >= VerticalExitW ? NominalModeFor(w) : VerticalMode;
        if (w < VerticalEnterW) return VerticalMode;
        int nominal = NominalModeFor(w);
        if (nominal == VerticalMode) return 2;
        if (nominal >= currentMode) return nominal;
        int dipped = NominalModeFor(w - ModeHysteresisDip);
        if (dipped == VerticalMode) dipped = 2;
        return dipped < currentMode ? dipped : currentMode;
    }
}
