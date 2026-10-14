// ── Theming/Contracts/PanelProps.cs — the props of `Shell.Panel` (themes plan 6.0, T6b) ───────────────────────────────────────
//
// THE PANEL IS A CORE STATE MACHINE AND A THEME VIEW. The core keeps what decides: `Shell.PanelHost` (open, mode, committed
// width, floating or inline, the docked video's height and pin), the width and fit rules (`PanelPolicy`, `Shell.ClampRailWidth`,
// `Shell.CanFitRail`, `PanelHost.FitWidthTo`), the rail <-> docked-video coupling, the seam's splitter (the frame places it) and
// the open/close bookkeeping. This record is what the template draws from: ONE decided `PanelView` per read, plus the verbs a
// click asks for and the builders of the bodies the core hosts.
//
// WHAT THE VIEW SAYS. `Read` is called inside the template's render (the panel frame is a component of the theme), so the
// subscriptions land on it: the panel's cells, the video surface's placement, the stage that may host the docked video, and the
// playing id. It answers the same facts the core's rail frame read before T6b: whether the panel is open, its committed width,
// whether it floats over the page, which body to show (`BodyModeFor`: a stage that hosts the video swaps the Video and
// NowPlaying bodies for the queue), whether the docked card is up, and what occupies the now-playing arm's pinned slot.
//
// THE BODIES. The now-playing body is this PR's (`NowPlaying`); the friends body is the `Panel.Friends` key. The lyrics header and
// body and the queue body are `Func<Element>` builders (the lyrics body and the queue body are core hosts that mount `Lyrics.View` (T7b)
// and `Queue.Panel` (T7d)); the docked video card is a core host that mounts `Video.Docked` (`Video.DockedCap`). The video body (T7c) is
// the template's own: it reads the playing track and the "Next up" rows through `NowPlaying` and subscribes through `SubscribeVideoBody`.
//
// WVTH006: a sealed record with init-only members and no writable signal. The record is built once (`RailWiring.Panel`), so an
// equal re-push skips the template.

using FluentGpu.Dsl;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>Which body the panel shows. Same members, values and order as the core's rail mode (which the player bar's toggles write
/// and the session document persists).</summary>
public enum PanelMode : byte { Lyrics, Queue, NowPlaying, Friends, Video }

/// <summary>What occupies the now-playing arm's pinned slot: ONE slot, two possible occupants, never both.</summary>
public enum PanelPinned : byte
{
    /// <summary>The hero tile alone (the stage hosts the video, or the track has no dockable video).</summary>
    Art,
    /// <summary>The docked video card with its splitter.</summary>
    DockedVideo,
    /// <summary>The hero tile with the Art | Video switch over its corner (the video could be docked here).</summary>
    ArtWithVideoToggle,
}

/// <summary>What one render of the panel draws: every fact decided.</summary>
public sealed record PanelView
{
    /// <summary>The panel is open (a closed panel is laid out off-canvas and unhittable).</summary>
    public bool Open { get; init; }

    /// <summary>The committed width (the layout's; a seam drag previews elsewhere and commits on release).</summary>
    public float Width { get; init; }

    /// <summary>The panel does not fit inline and overlays the page.</summary>
    public bool Floats { get; init; }

    /// <summary>The policy says the panel spans the window's full height instead of the content row. Read by a theme whose panel is
    /// laid out that way; the state machine does not depend on it (Classic's is false).</summary>
    public bool FullHeight { get; init; }

    /// <summary>The body on show, after the page stage's substitution (it hosting the video makes Video and NowPlaying the queue).</summary>
    public PanelMode Body { get; init; }

    /// <summary>The docked video card is up in this panel (the video is docked and the page stage does not host it).</summary>
    public bool DockedHere { get; init; }

    /// <summary>The now-playing arm's pinned slot.</summary>
    public PanelPinned Pinned { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.ShellPanel"/>.</summary>
public sealed record PanelProps
{
    // ── the decided view and the live reads ──────────────────────────────────────────────────────────────────────────

    /// <summary>The decided view; call it inside a render (it subscribes the panel's cells, the video surface and the stage).</summary>
    public required Func<PanelView> Read { get; init; }

    /// <summary>The surface paints nothing while the panel is open and inline (the frame's reservation band is the one coat). Bound.</summary>
    public required Func<bool> InlineAndOpen { get; init; }

    /// <summary>The body's height changed (the docked video's ceiling follows it). The core ignores sub-half-DIP changes.</summary>
    public required Action<float> BodyHeightChanged { get; init; }

    // ── the docked video card's splitter ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The docked card's height cell: the very signal the splitter writes and the card binds. A stable bind, never a thunk.</summary>
    public required FloatSignal DockedVideoHeight { get; init; }

    /// <summary>The card's floor at a panel width (the 16:9 height).</summary>
    public required Func<float, float> DockedVideoMin { get; init; }

    /// <summary>The card's ceiling against the panel body's current height.</summary>
    public required Func<float> DockedVideoMax { get; init; }

    /// <summary>Commit the card's height (clamped, pinned, persisted).</summary>
    public required Action CommitDockedVideo { get; init; }

    // ── verbs ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Close the panel, whatever it shows (a docked video is demoted by the coupling).</summary>
    public required Action Close { get; init; }

    /// <summary>Turn the docked video back into the art ("switch to audio").</summary>
    public required Action ShowArt { get; init; }

    /// <summary>Dock the video in the panel ("switch to video").</summary>
    public required Action ShowVideoDocked { get; init; }

    /// <summary>The video header's mini-player button: float the video.</summary>
    public required Action VideoToMiniPlayer { get; init; }

    /// <summary>The video header's full-screen button.</summary>
    public required Action VideoToFullScreen { get; init; }

    // ── bodies the core builds (core hosts and builders) ─────────────────────────────────────────────────────────────

    /// <summary>The docked video card (the core host that mounts <see cref="ViewKeys.VideoDocked"/>): zero size and no reflow while the video is not docked.</summary>
    public required Func<Element> DockedCard { get; init; }

    /// <summary>The lyrics mode's header (title, second-line toggle, AI sparkle, expand, inspector, close): a core builder.</summary>
    public required Func<Element> LyricsHeader { get; init; }

    /// <summary>The lyrics body: the core host that mounts <see cref="ViewKeys.LyricsView"/>.</summary>
    public required Func<Element> LyricsBody { get; init; }

    /// <summary>The queue body (the core host of the queue panel, which mounts <see cref="ViewKeys.QueuePanel"/>, or its empty grammar).</summary>
    public required Func<Element> QueueBody { get; init; }

    /// <summary>Subscribe what the video mode's body reads of the entity tables (the scope epoch, the queue edge, the album table); call it
    /// inside the body's render, after the playing track. The body itself (the track meta, "Next up") is the template's.</summary>
    public required Action SubscribeVideoBody { get; init; }

    // ── the surfaces this PR gave their own keys ─────────────────────────────────────────────────────────────────────

    /// <summary>The now-playing pane: the hero (<see cref="ViewKeys.NowPlayingHero"/>) and the body under it.</summary>
    public required NowPlayingHeroProps NowPlaying { get; init; }

    /// <summary>The friends body (<see cref="ViewKeys.PanelFriends"/>).</summary>
    public required FriendsProps Friends { get; init; }
}
