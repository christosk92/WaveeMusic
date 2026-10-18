// ── Theming/Contracts/VideoProps.cs — the props of the six `Video.*` keys (themes plan 6.0, T7c) ───────────────────────────────
//
// THE VIDEO SURFACES ARE CORE HOSTS AND A THEME PICTURE. ONE stay-mounted presenter (`Video.PipSurface`) carries the engine's
// `MediaPlayerElement` for the docked card, the in-window mini player and fullscreen alike (the pop-out is a second window and a second
// element); its keys, its idle machine and chrome feed, the hollow reservation it follows, the join fold, the host observer, the pop-out's
// lifecycle and caption regions, the fullscreen mode and the PiP gestures stay in the core (`Shell\Video*.cs`, `PipGesture.cs`: the rules
// were moved there by C6). What a theme draws is what sits around and over the picture: the card, the frame, the chrome, the poster, the
// notice, the title band, the exit plate and the placement flyout.
//
// THE HOLE RULE (every theme's, not Classic's): the picture is a hole erased from the swapchain, so NO ancestor of it may carry a layout
// transition, an opacity, an offscreen effect or a scale Enter/Exit. Chrome is a SIBLING of the picture (a hover- or idle-revealed strip),
// never its parent. The placement binds (`VideoPipProps.Width`, `Height`, `Transform`, `Visible`, `FollowRect`) are the engine's own
// geometry for the hole: a template puts them on the box it returns and adds nothing between that box and the picture.
//
// WVTH006: sealed records with init-only members and no writable signal. The hosts build them once per surface or per render; the
// delegates are cached statics or built once, so an equal re-push skips the template.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>The props of <see cref="ViewKeys.VideoJoinPoster"/>: what a surface's video area shows instead of (or over) the picture.</summary>
public sealed record VideoJoinPosterProps
{
    /// <summary>What the join decided (<c>Video.JoinNow</c>, folded once from the host's events): the poster alone, the poster with the ring and
    /// "Loading…", or the poster with the fault line. <see cref="Video.JoinVisual.Video"/> draws the poster alone like <see cref="Video.JoinVisual.Poster"/>.</summary>
    public required Video.JoinVisual Visual { get; init; }

    /// <summary>The poster layer is on show (collapsed, not unmounted, once the live picture is the one thing on screen).</summary>
    public required bool Shown { get; init; }

    /// <summary>The current track's cover url, or null. Call it inside a render of the poster's own component (it subscribes the playing track and
    /// the track table), so a stage whose key survives a source switch never shows the first track's art forever.</summary>
    public required Func<string?> ArtUrl { get; init; }

    /// <summary>The fault's own line (a failed join). Bound reactively: the reducer types the fault and it can land after the notice mounted.</summary>
    public required Func<string> FaultTitle { get; init; }
}

/// <summary>What the watch page hands the in-page stage frame.</summary>
/// <param name="PosterUrl">The idle poster, or null.</param>
/// <param name="OnPlay">The idle play disc's verb, or null for no disc.</param>
/// <param name="Live">The docked card mounts here now (the playing uri is this page's, the page stage hosts it). Predicate for a node-bound
/// <c>Flow.Show</c>: a KeepAlive-parked page cannot re-render, but the predicate still runs.</param>
/// <param name="Card">The live card (the core's docked host, keyed); mounted by the frame while <paramref name="Live"/> holds.</param>
/// <param name="MaxHeight">The tallest the stage may be (the docked cap's ceiling: the core's rule, which the page also enforces).</param>
public sealed record VideoPageFrame(string? PosterUrl, Action? OnPlay, Func<bool> Live, Func<Element> Card, float MaxHeight);

/// <summary>What the docked card needs from the core: its height, the hollow reservation's reports and the Space key.</summary>
/// <param name="Face">Which envelope wraps the card: the rail's cap (its height follows the content, splitter-overridable) or the page's stage.</param>
/// <param name="Height">The cap's height: the SAME signal the rail's splitter writes (a fresh thunk per render would collapse the box).</param>
/// <param name="ReservationRealized">The hollow reservation was realized: the stay-mounted presenter follows this node (the engine's <c>FollowRect</c>).</param>
/// <param name="ReservationBounds">The reservation's laid-out rect changed (the docked slot's fallback and its log).</param>
/// <param name="TogglePlay">Space on the focused card: play or pause.</param>
public sealed record VideoDockedCard(
    Video.DockedFace Face, FloatSignal Height, Action<NodeHandle> ReservationRealized, Action<RectF> ReservationBounds, Action TogglePlay);

/// <summary>The props of <see cref="ViewKeys.VideoDocked"/>: the docked card, or the watch page's stage frame around it. Exactly one of the two is set.</summary>
public sealed record VideoDockedProps
{
    /// <summary>Set: draw the docked card itself (the rail's cap, or the live card inside a page's stage).</summary>
    public VideoDockedCard? Card { get; init; }

    /// <summary>Set: draw the watch page's in-page stage frame (the live card is placed by it).</summary>
    public VideoPageFrame? PageFrame { get; init; }
}

/// <summary>The mini player's gestures, as the core's presenter runs them: the drag and the eight resize zones report realized nodes and
/// pointer positions, the core clamps, snaps and persists with the PiP rules (<c>PipGesture</c>, the active panel policy's bounds).</summary>
public interface IVideoPipGestures
{
    /// <summary>The drag surface's node.</summary>
    void DragRealized(NodeHandle node);

    /// <summary>The drag surface was pressed (position in the node's local space).</summary>
    void DragDown(Point2 local);

    /// <summary>The drag surface moved.</summary>
    void DragMove(Point2 local);

    /// <summary>Zone <paramref name="slot"/> (0..7: the three of the top row, left and right of the middle, the three of the bottom row) was realized.</summary>
    void ZoneRealized(int slot, NodeHandle node);

    /// <summary>Zone <paramref name="slot"/> was pressed.</summary>
    void ZoneDown(int slot, Point2 local);

    /// <summary>Zone <paramref name="slot"/> moved: resize from the <paramref name="edge"/> it grips.</summary>
    void ZoneMove(int slot, Video.PipEdge edge, Point2 local);

    /// <summary>A gesture ended (a drag's click IS its release edge; a cancel): one settings write for a deliberately placed card.</summary>
    void Commit();
}

/// <summary>The props of <see cref="ViewKeys.VideoPip"/>.</summary>
public sealed record VideoPipProps
{
    /// <summary>The resolved placement (docked: the hover strip; floating: the card, the drag strip and the zones; fullscreen: the picture alone).</summary>
    public required Video.SurfacePlacement Placement { get; init; }

    /// <summary>The box's bound visibility: covering or releasing the picture re-renders and remounts nothing.</summary>
    public required Prop<bool> Visible { get; init; }

    /// <summary>Docked: the engine places the box over the reservation's painted rect in the same frame.</summary>
    public required Func<NodeHandle> FollowRect { get; init; }

    /// <summary>The box's bound width per placement.</summary>
    public required Prop<float> Width { get; init; }

    /// <summary>The box's bound height per placement.</summary>
    public required Prop<float> Height { get; init; }

    /// <summary>The box's bound offset per placement.</summary>
    public required Prop<Affine2D> Transform { get; init; }

    /// <summary>The picture (the core's keyed video area: the player stage over the poster). Always the first child.</summary>
    public required Func<Element> Area { get; init; }

    /// <summary>The mini player's gestures.</summary>
    public required IVideoPipGestures Gestures { get; init; }

    /// <summary>The docked strip's "mini player" button: announce and float the video.</summary>
    public required Action ToMiniPlayer { get; init; }

    /// <summary>The docked strip's "full screen" button: announce and enter fullscreen.</summary>
    public required Action ToFullscreen { get; init; }

    /// <summary>The docked strip's "turn off video" button: announce and close the docked host (sticky off).</summary>
    public required Action TurnOffDocked { get; init; }

    /// <summary>The mini player's close button: close the floating host.</summary>
    public required Action ClosePip { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.VideoPopOut"/>. Built per render of the core's pop-out content (the join, the fullscreen mode and
/// the chrome's idle edge change it).</summary>
public sealed record VideoPopOutProps
{
    /// <summary>The detached window's viewport: the content is sized to THIS window, always opaque (anything unpainted shows the desktop).</summary>
    public required IReadSignal<Size2> Size { get; init; }

    /// <summary>A player is bound: the stage mounts over the poster; otherwise the poster alone.</summary>
    public required bool HasPlayer { get; init; }

    /// <summary>The join's verdict (the poster's notice, or nothing over a live picture).</summary>
    public required Video.JoinVisual Join { get; init; }

    /// <summary>The title band shows (the window's chrome rides the same idle machine as the transport; never in fullscreen).</summary>
    public required bool BandShown { get; init; }

    /// <summary>The player binding's generation: the stage's identity (its key follows it), so a new binding re-runs the template even when
    /// nothing else moved.</summary>
    public required long Generation { get; init; }

    /// <summary>The stage (the core's keyed player stage: the engine element and the on-media transport).</summary>
    public required Func<Element> Stage { get; init; }

    /// <summary>The poster for a join verdict (<see cref="ViewKeys.VideoJoinPoster"/>, mounted by the core).</summary>
    public required Func<Video.JoinVisual, Element> Poster { get; init; }

    /// <summary>The current track's title, "" when unknown. Subscribes; bound reactively (the window outlives a track change).</summary>
    public required Func<string> Title { get; init; }

    /// <summary>The band's node (the core fades it with the engine's chrome fade).</summary>
    public required Action<NodeHandle> BandRealized { get; init; }

    /// <summary>The pointer moved over the band (counts as chrome activity; the band is an OS caption region).</summary>
    public required Action<Point2> BandPointerMoved { get; init; }

    /// <summary>The pointer left the band.</summary>
    public required Action BandPointerLeft { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.VideoFullscreen"/>.</summary>
public sealed record VideoFullscreenProps
{
    /// <summary>The current track's title, "" when unknown. Subscribes; bound reactively (the surface outlives a track change).</summary>
    public required Func<string> Title { get; init; }

    /// <summary>The way out: leave fullscreen.</summary>
    public required Action Exit { get; init; }

    /// <summary>The current track's artists as one line, "" when unknown. Subscribes. Contract addition after 1.40.</summary>
    public Func<string> Artists { get; init; } = static () => "";

    /// <summary>"Switch to audio": turn the video off for this track and fall back to the song (the same verb as the placement menu's "Turn off video"). Null: no such word. Contract addition after 1.40.</summary>
    public Action? SwitchToAudio { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.VideoPlacementMenu"/>. Built per open (it carries that open's close verb).</summary>
public sealed record VideoPlacementMenuProps
{
    /// <summary>The rows, decided when the menu opened: the placement ladder radio-checked against the resolved placement, an unavailable rung
    /// disabled with its reason (never hidden), the always-on-top toggle while detached and "Turn off video" while active.</summary>
    public required IReadOnlyList<MenuFlyoutItem> Items { get; init; }

    /// <summary>Close the flyout (an invoked row closes it).</summary>
    public required Action Close { get; init; }
}
