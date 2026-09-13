using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee;

// RailMode lives in RailVideoCoupling.cs — it is System-only (no Signal<T>/FluentGpu types) so it can be
// source-included into the engine-free Wavee.Tests project alongside the pure rail<->docked-video coupling rules
// that are keyed on it. Same `Wavee` namespace, so no `using` is needed here.

/// <summary>
/// UI-only chrome state for the WaveeMusic-style right rail. Kept off <see cref="PlaybackBridge"/> so the bridge stays
/// about playback, not chrome. Created and provided by <c>WaveeShell</c> via <see cref="Slot"/>; the player-bar toggle
/// writes it, while <c>RightRail</c> and the panel views read it.
/// </summary>
public sealed class ShellUi
{
    public static readonly Context<ShellUi?> Slot = new(null);

    /// <summary>The rail is open. When false the rail slot animates its width to 0.</summary>
    public Signal<bool> RailOpen { get; } = new(false);

    /// <summary>Which panel the rail shows when open.</summary>
    public Signal<RailMode> Mode { get; } = new(RailMode.Lyrics);

    /// <summary>The rail's expanded width in DIP.</summary>
    public FloatSignal RailWidth { get; } = new(ShellResponsiveLayout.RailDefaultW);

    /// <summary>Docked music-video cap height in DIP (Lyrics/Queue/Friends/Video). Floor is 16:9 of
    /// <see cref="RailWidth"/>; the vertical splitter only grows from there.</summary>
    public FloatSignal DockedVideoHeight { get; } = new(ShellResponsiveLayout.DockedVideoNaturalH(ShellResponsiveLayout.RailDefaultW));

    /// <summary>Has the user DELIBERATELY sized the docked cap for what is playing right now? False (the default) means
    /// <see cref="DockedVideoHeight"/> follows the content's own aspect — <c>ShellResponsiveLayout.FitDockedVideoHeight</c>
    /// — so a 16:9 stream fills the card edge to edge instead of sitting in letterbox bars. The vertical splitter sets it
    /// on a committed drag; the docked surface clears it when the SOURCE changes.</summary>
    public Signal<bool> DockedVideoHeightPinned { get; } = new(false);

    /// <summary>The PLAYABLE uri the attached page's own stage would host — a module watch page's video — or
    /// <c>""</c> when no attached page stages anything. NAVIGATION state, not rail state, which is why it lives here
    /// next to the chrome the arbitration reads rather than on <c>PlaybackBridge</c>; and the empty string (not null)
    /// is the resting value, so <c>DockedVideoHosting.PageStageHosts</c> can treat empty as "nothing staged" and never
    /// accidentally match an empty playing uri.
    ///
    /// <para><b>A PLAYABLE uri, and that is the whole point.</b> This was once the page's OWN entity uri, which looked
    /// equivalent and is not: a module's entity id space and its playable id space are deliberately different —
    /// YouTube's video page is entity <c>video:tRsQsTMvPNg</c> while the thing that plays is playable
    /// <c>tRsQsTMvPNg</c>. Compared against <c>PlaybackBridge.CurrentTrack.Uri</c> (always a PLAYABLE uri) the two
    /// could never be equal, so the page stage never mounted and the rail never yielded, on the one module the feature
    /// exists for. Both terms of that comparison now live in ONE id space: the playable uri.</para>
    ///
    /// <para><b>Two writers, disjoint by route kind, and no reader ever compares uris by hand.</b> The attached
    /// <c>ModulePage</c> writes the playable its document's play action names (it is the only thing that HAS the
    /// document, and therefore the only thing that can map a page to a playable), from an effect gated on
    /// <c>UseIsActive</c> so a keep-alive-parked page never writes. <c>ContentHost</c> writes <c>""</c> on navigation
    /// to any route that is NOT a module page, which is what stops a stale claim from surviving a trip to Home. Every
    /// reader — the rail's yield, the docked capability bit, the page stage's own mount gate — goes through
    /// <c>DockedVideoHosting</c>.</para></summary>
    public Signal<string> ActiveStagePlayable { get; } = new("");

    /// <summary>
    /// Whether the rail can currently reserve inline layout width alongside the sidebar and content region. When false,
    /// the shell floats the rail over content instead of allocating row width for it.
    /// </summary>
    public Signal<bool> RailFits { get; } = new(true);

    /// <summary>The immersive fullscreen lyrics surface is open. <c>WaveeShell</c> mounts
    /// <c>ImmersiveLyricsSurface</c> as a full-bleed overlay while this is true; the rail's lyrics header expand button
    /// sets it, and the surface's own close button / Escape clears it. Deliberately NOT a <see cref="RailMode"/>: the
    /// surface covers the whole shell rather than replacing the rail's panel, so the rail's own state is untouched.</summary>
    public Signal<bool> ImmersiveLyrics { get; } = new(false);

    // NOTE: there is deliberately no layout-defer lock here any more. It existed to stop the OLD reflow-spring rail
    // (which animated REAL width) from churning a breakpoint remount on every intermediate width. The shipping path
    // commits the reserved width in ONE frame (the spacer snaps; the content card eases only a clip window), so there
    // are no intermediate widths to debounce — and deferring the breakpoint reaction instead GUARANTEED a ~300ms window
    // where the wide column set was rendered into the already-narrow pane, which the grid's overflow guard crushed into
    // overlapping glyphs. Breakpoints now react on the same frame the width commits.

    /// <summary>Clicking the already-showing mode closes the rail; otherwise switch to that mode and open.</summary>
    public void Toggle(RailMode mode)
    {
        if (RailOpen.Peek() && Mode.Peek() == mode) { RailOpen.Value = false; return; }
        Mode.Value = mode;
        RailOpen.Value = true;
    }

    /// <summary>Viewport-fit test for sidebar + rail + a minimum usable content region.</summary>
    public static bool CanFitRail(float viewportW, float sidebarW,
        float railW = ShellResponsiveLayout.RailDefaultW, float minContentW = 480f)
        => ShellResponsiveLayout.CanFitRail(viewportW, sidebarW, railW, minContentW);
}
