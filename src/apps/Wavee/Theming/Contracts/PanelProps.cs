// ── Theming/Contracts/PanelProps.cs — the props of `Shell.Panel` (themes plan 6.0, T6b) ───────────────────────────────────────
//
// THE PANEL IS A CORE STATE MACHINE AND A THEME VIEW. The core keeps what decides: `Shell.PanelHost` (open, mode, committed
// width, docked, floating or a sheet, the docked video's height and pin, the queue beside it), the width and placement rules
// (`PanelPolicy` per window tier, `Shell.ClampRailWidth`, `PanelHost.FitWidthTo` / `FitWidthToFloor`), the rail <-> docked-video
// coupling, the seam's splitter (the frame places it) and the open/close bookkeeping. This record is what the template draws from: ONE
// decided `PanelView` per read, plus the verbs a click asks for and the builders of the bodies the core hosts.
//
// TWO PANES (TC2, D3). Where the window tier docks two panels, the queue opens in a SECOND pane beside the primary one. The frame mounts
// `Shell.Panel` a second time with the second pane's own props (`ShellFrameProps.SecondPanel`): its view is always the queue, docked, with
// no docked video card.
//
// WHAT THE VIEW SAYS. `Read` is called inside the template's render (the panel frame is a component of the theme), so the
// subscriptions land on it: the panel's cells, the video surface's placement, the stage that may host the docked video, and the
// playing id. It answers the same facts the core's rail frame read before T6b: whether the panel is open, its committed width,
// whether it floats over the page, which body to show (`BodyModeFor`: a stage that hosts the video swaps the Video and
// NowPlaying bodies for the queue), whether the docked card is up, and what occupies the now-playing arm's pinned slot.
//
// THE BODIES. The now-playing body is this PR's (`NowPlaying`); the friends body is the `Panel.Friends` key; the lyrics header is the
// `Panel.LyricsHeader` key (`LyricsHeader`, P3). The lyrics body and the queue body are `Func<Element>` builders of core hosts that mount
// `Lyrics.View` (T7b) and `Queue.Panel` (T7d); the docked video card is a core host that mounts `Video.Docked` (`Video.DockedCap`). The video body (T7c) is
// the template's own: it reads the playing track and the "Next up" rows through `NowPlaying` and subscribes through `SubscribeVideoBody`.
//
// WVTH006: a sealed record with init-only members and no writable signal. The record is built once (`RailWiring.Panel`), so an
// equal re-push skips the template.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>Which body the panel shows. Same members, values and order as the core's rail mode (which the player bar's toggles write
/// and the session document persists).</summary>
public enum PanelMode : byte { Lyrics, Queue, NowPlaying, Friends, Video }

/// <summary>Where the right panel is laid out in a window tier (the structure's <c>PanelPolicy</c>; contract 1.15).</summary>
public enum PanelPlacement : byte
{
    /// <summary>Docked beside the page: the content row reserves its width.</summary>
    Dock,

    /// <summary>Floating over the page: the page keeps its width.</summary>
    Float,

    /// <summary>A bottom sheet over the page and the player bar.</summary>
    Sheet,
}

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

    /// <summary>The window tier does not dock the panel: it overlays the page (<see cref="Placement"/> says how).</summary>
    public bool Floats { get; init; }

    /// <summary>Where the window tier lays the panel out: docked, floating or a bottom sheet (contract 1.15).</summary>
    public PanelPlacement Placement { get; init; }

    /// <summary>The body unmounts once a close has slid out (the second docked pane, whose queue must not stay mounted beside the
    /// primary panel's own); false keeps a closed panel's body mounted, as the primary panel always has (contract 1.15).</summary>
    public bool UnmountWhenClosed { get; init; }

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

    /// <summary>Show another body in this panel (the Compact now-playing sheet's Lyrics and Queue doors); null where the panel offers none
    /// (contract 1.15).</summary>
    public Action<PanelMode>? ShowMode { get; init; }

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

    /// <summary>The lyrics mode's header (title, second-line toggle, AI sparkle, expand, inspector, close): the props the lyrics arm mounts
    /// <see cref="ViewKeys.PanelLyricsHeader"/> with.</summary>
    public required PanelLyricsHeaderProps LyricsHeader { get; init; }

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

    /// <summary>The transport a panel shown as a bottom sheet carries (the sheet covers the player bar; C-NowPlaying-Compact): the facts,
    /// the verbs and the bar's own seek rail and time labels. Null where the panel offers none (contract 1.20).</summary>
    public PanelTransportProps? Transport { get; init; }

    /// <summary>The video body's title-row actions (C-Video-Huge: the heart and the "..." beside the playing track's title). Null where the
    /// panel offers none (contract 1.22).</summary>
    public PanelVideoTitleProps? VideoTitle { get; init; }
}

/// <summary>What the video title row's heart draws (contract 1.22).</summary>
/// <param name="Saved">The playing track is in Liked Songs.</param>
/// <param name="Like">The heart's verb: save or remove the playing track (null: nothing to save).</param>
public readonly record struct PanelVideoTitleFacts(bool Saved, Action? Like);

/// <summary>The video body's title-row actions (contract 1.22): the heart's facts and the playing track's "..." menu.</summary>
public sealed record PanelVideoTitleProps
{
    /// <summary>The heart's facts; call it inside a render (it subscribes the playing track and the saved set).</summary>
    public required Func<PanelVideoTitleFacts> Facts { get; init; }

    /// <summary>The playing track's context menu, asked at OPEN time (null: none to show).</summary>
    public required Func<FluentGpu.Controls.ContextMenuModel?> Menu { get; init; }
}

/// <summary>What the sheet's transport draws that playback decides (contract 1.20).</summary>
/// <param name="Playing">Playing (the disc shows pause).</param>
/// <param name="PrimaryEnabled">There is a playable and it is not loading.</param>
/// <param name="CanTransport">Shuffle and repeat may be pressed.</param>
/// <param name="PrevEnabled">Previous may be pressed.</param>
/// <param name="NextEnabled">Next may be pressed.</param>
/// <param name="Shuffle">Shuffle is on.</param>
/// <param name="RepeatOn">Repeat is on (the context or the track).</param>
/// <param name="RepeatOne">Repeat is the single track.</param>
public readonly record struct PanelTransportFacts(bool Playing, bool PrimaryEnabled, bool CanTransport, bool PrevEnabled, bool NextEnabled,
    bool Shuffle, bool RepeatOn, bool RepeatOne);

/// <summary>The sheet's transport (contract 1.20): the same facts, verbs and seek pieces the stage's transport card reads.</summary>
public sealed record PanelTransportProps
{
    /// <summary>The facts; call it inside a render (it subscribes playback).</summary>
    public required Func<PanelTransportFacts> Facts { get; init; }

    /// <summary>TogglePlay, Previous, Next, ToggleShuffle, CycleRepeat.</summary>
    public required IPlayerVerbs Verbs { get; init; }

    /// <summary>The bar's own seek rail (the core host that mounts <see cref="ViewKeys.SeekRail"/>).</summary>
    public required Func<Element> SeekBar { get; init; }

    /// <summary>The bar's elapsed (false) or remaining (true) time label (the core host that mounts <see cref="ViewKeys.TimeLabel"/>).</summary>
    public required Func<bool, Element> TimeText { get; init; }

    /// <summary>Where playback comes from (the sheet's head: "Playing from album" over the context's name, a link to its page); call it inside
    /// a render (it subscribes the context). Null where the sheet names none (contract 1.22).</summary>
    public Func<PanelSourceFacts>? Source { get; init; }

    /// <summary>The device playback is on (the sheet's Devices door: its name, latched while it is another device); call it inside a render
    /// (it subscribes the roster). Null where the sheet offers no Devices door (contract 1.22).</summary>
    public Func<PanelDeviceFacts>? Device { get; init; }

    /// <summary>Open the device picker over an anchor, or close it when it is open: the player bar's own toggle (the core's overlay, placement,
    /// dismissal and picker-opened side effect; the menu is <see cref="ViewKeys.DevicePicker"/>). Null with no Devices door (contract 1.22).</summary>
    public Action<IOverlayService, Ref<NodeHandle>, Ref<OverlayHandle?>>? ToggleDevicePicker { get; init; }

    /// <summary>Bumped when something asks for the device picker (a toast's "Choose device", the tray's Devices): the same signal the player bar's
    /// Devices button opens itself on. The sheet covers the bar, so the sheet answers it: it shows the now-playing body if it shows another, then
    /// opens the Devices door's picker. Null with no Devices door. Contract 1.25.</summary>
    public IReadSignal<int>? DevicePickerRequest { get; init; }
}

/// <summary>Where playback comes from, for the now-playing sheet's head (contract 1.22).</summary>
/// <param name="PlayingFrom">The "Playing from album" line; empty with no source (never a "Playing from" with no name).</param>
/// <param name="Name">The source's name; empty with no source.</param>
/// <param name="Open">Go to the source's page; null where it has none.</param>
public readonly record struct PanelSourceFacts(string PlayingFrom, string Name, Action? Open)
{
    /// <summary>There is a source to name.</summary>
    public bool HasSource => Name is { Length: > 0 };
}

/// <summary>The device playback is on, for the now-playing sheet's Devices door (contract 1.22).</summary>
/// <param name="Name">The device's name ("This computer" when it is this one).</param>
/// <param name="Remote">Playback is on another device (the door latches, as the player bar's Devices button does).</param>
public readonly record struct PanelDeviceFacts(string Name, bool Remote);
