namespace Wavee;

/// <summary>Which panel the right rail is showing. <see cref="Video"/> is the video-first rail body: the docked
/// card's takeover face, track meta, and Up next — entered only when the user docks a video into a closed rail
/// (never by a track change alone). See <see cref="RailVideoCoupling"/>. Appended last — nothing persists these
/// values today, but keep the habit. Declared here (rather than on <c>ShellUi</c>, which is Signal-heavy) because it
/// is System-only and travels with the pure coupling rules below into the engine-free test project.</summary>
public enum RailMode { Lyrics, Queue, Details, Friends, Video }

/// <summary>
/// The PURE rail ↔ docked-video coupling rules. Like its siblings <see cref="PlacementCore"/>,
/// <see cref="VideoUpgradeGate"/> and <see cref="LyricsSyncGate"/> it takes plain values read at the call site and
/// returns a decision — no <c>Signal&lt;T&gt;</c>, no FluentGpu type — so it is verifiable without a GPU or a window.
///
/// <para>WHY THIS EXISTS. Docking is a two-way coupling between two independent pieces of state — the rail's own
/// open/closed + mode (<see cref="ShellUi"/>) and the video surface's placement (<c>PlaybackBridge.VideoSurface</c>)
/// — and every rule connecting them is an edge case (the docked-video design's B1, B6-B8, B10-B14) that is easy to
/// get half-right if it is inlined separately at each of the four call sites. Collecting the four rules here means
/// the edge cases are asserted once, in one place, over plain values, instead of re-derived (and re-broken) at each
/// call site.</para>
/// </summary>
public static class RailVideoCoupling
{
    /// <summary>Docking was just requested (the user picked "Dock in rail", or a fresh profile's default resolved to
    /// Docked). Which rail mode should be showing? <c>null</c> means leave the rail exactly as it is — this only
    /// fires when the rail was CLOSED, so opening it for video does not clobber a mode the user was already looking
    /// at.</summary>
    /// <param name="host">Which docked host would own the surface (<c>DockedVideoHosting.HostFor</c>). Rail-only: when
    /// the module watch page's in-page stage is what will host the video, opening the rail into the video-first body
    /// would open a rail whose card is mounted somewhere else entirely — an empty takeover body next to the real
    /// picture.</param>
    public static RailMode? ModeOnDock(bool railOpen, RailMode current, DockedVideoHost host)
        => host != DockedVideoHost.Rail || railOpen ? null : RailMode.Video;

    /// <summary>The user closed the rail. What should happen to a docked video? Returns the DEMOTE target, or
    /// <see cref="SurfacePlacement.None"/> for "nothing to do" (video was not docked, so closing the rail is inert to
    /// it). Only Docked is ever demoted here — the rail closing has no opinion about a floating or detached
    /// video.
    ///
    /// <para>INERT while the page stage is hosting (<paramref name="host"/> is
    /// <see cref="DockedVideoHost.PageStage"/>): the rail is not where that video lives, so closing the rail has no
    /// more to say about it than closing it says about a detached window. Without this term the rule would demote a
    /// watch page's in-page stage to the mini player and yank the video straight off the page the user is reading.</para></summary>
    public static SurfacePlacement OnRailClosed(in PlacementState s, DockedVideoHost host)
        => host == DockedVideoHost.Rail && PlacementCore.Resolve(s) == SurfacePlacement.Docked
            ? SurfacePlacement.Floating : SurfacePlacement.None;

    /// <summary>The rail was opened again. Should video return to the dock? Keyed on
    /// <see cref="PlacementState.Preferred"/> being Docked — which is exactly what distinguishes "the rail took my
    /// video away" (re-dock it) from "I deliberately chose the mini player from the menu" (leave it floating, since
    /// THAT write set <c>Preferred = Floating</c>). Excludes Fullscreen: entering fullscreen from the dock must not
    /// re-fire this the moment the rail happens to still be open underneath it.</summary>
    public static bool ReDockOnRailOpen(in PlacementState s)
        => s.Preferred == SurfacePlacement.Docked
        && s.Requested != SurfacePlacement.None
        && s.Requested != SurfacePlacement.Docked
        && s.Requested != SurfacePlacement.Fullscreen;

    /// <summary>Video left the dock — turned off, its content lost availability, or it moved to another placement
    /// entirely — while the rail was showing the video-first body. Should the rail close? Yes, because
    /// <see cref="RailMode.Video"/> has nothing left to show once the card it hosts is gone.
    ///
    /// <para><paramref name="hostBefore"/>, not the host now: once the video has LEFT the dock the derivation always
    /// reads <see cref="DockedVideoHost.Rail"/> (nothing is docked, so the rail is the resting owner), which would make
    /// a current-host term unconditionally true and close the rail on behalf of a card the page stage was holding. The
    /// question this rule asks is about the body that WAS on screen, so it takes the host that WAS.</para></summary>
    public static bool CloseRailOnVideoLeft(RailMode mode, bool videoTurnedOff, DockedVideoHost hostBefore)
        => hostBefore == DockedVideoHost.Rail && mode == RailMode.Video && videoTurnedOff;

    /// <summary>What the rail's BODY renders, given the user's chosen mode and whether the page stage owns the one
    /// video surface. Video-first and Details are the two bodies that HOST the docked card (the Cap face and the
    /// Art-tile hero respectively); when the stage owns the surface neither has anything left to host — a takeover body
    /// with no takeover, and a pinned hero square that would render an empty tile above the credits — so both fall back
    /// to Queue, which is the one body that is always meaningful while something is playing.
    ///
    /// <para>A substitution at RENDER time, never a write to <c>ShellUi.Mode</c>. The user's chosen mode is untouched,
    /// so the moment the stage yields (a navigation away, a track change, availability dropping) the rail is back
    /// exactly where it was — there is no displaced-mode memory to restore, and therefore no ordering between "the
    /// stage released it" and "the rail restored itself" to get wrong. This is the same reason the host itself is
    /// derived rather than claimed; see <c>DockedVideoHosting</c>.</para></summary>
    public static RailMode BodyModeFor(RailMode mode, bool stageHostsVideo)
        => stageHostsVideo && mode is RailMode.Video or RailMode.Details ? RailMode.Queue : mode;
}
