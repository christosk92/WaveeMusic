// ── Theming/Contracts/PlayerTransportProps.cs — the transport pieces the bar, the stage and the on-media strip share (leftovers P3) ──
//
// THE SEEK RAIL, THE TIME LABELS AND THEIR POPUPS. Three surfaces place the same rail and the same two labels (the player bar,
// `Player.Bar`; the fullscreen stage's transport card, `Stage.Transport`; the on-media strip, `Player.OnMediaTransport`), each over
// its own core host (`Shell.SeekBar` / `Shell.TimeText`, `[CoreHost]`, `Shell\Shell.PlayerBar.UI.cs`), which the surface's props
// carry as a builder because the per-surface arguments (the chrome feed, the stage's scrub edge, the strip's reveal) are core objects.
// The hosts keep everything that decides: the scrub controller (the gesture, the pixel dwell, the seek-generation hold, the keyboard
// ladder), every hook, interval and effect, the readout and chapter-tip overlays (when they open, when they close, the unmount abort),
// the chapter lease and the markers' arithmetic, the time formatting and the live-slot decision. What a theme draws arrives here:
//
//   `Player.SeekRail`     the rail (track and fill, the DVR live edge, the thumb ring and its inner disc, the hit box with its role),
//                         or, with NOTHING TO REWIND, the breathing live line; the props say which, the host binds and handles
//   `Player.SeekChapters` an episode's chapter ticks and the invisible node the chapter tip is anchored to
//   `Player.ChapterTip`   the chapter tip's bubble (mounted by the host inside its overlay)
//   `Player.SeekReadout`  the scrub readout's bubble (mounted by the host inside its overlay)
//   `Player.TimeLabel`    one time label: the digits' slot (a bound text channel), or the live slot (LIVE or GO LIVE)
//
// The player bar's credits line is not a key: only the bar draws it, so `PlayerBarProps` carries its facts (`PlayerCredits`).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>What a seek rail's picture binds and calls. The core host implements it once per rail: every member is a stable
/// delegate or bind built once, so a re-render hands the reconciler the SAME thunks and rebinds nothing.</summary>
public interface ISeekRailHost
{
    /// <summary>The value fill's transform: an x-scale by the displayed fraction (compositor-bound; the rail never re-renders for it).</summary>
    Prop<Affine2D> FillTransform { get; }

    /// <summary>The thumb ring's transform for a ring of <paramref name="ringDiameter"/>: centred on the displayed fraction, never past
    /// either end of the rail. Built once per diameter.</summary>
    Prop<Affine2D> ThumbTransform(float ringDiameter);

    /// <summary>The press: jump to the press point and open the readout.</summary>
    Action<Point2> PointerDown { get; }
    /// <summary>The drag: follow the pointer.</summary>
    Action<Point2> DragMove { get; }
    /// <summary>The drag-END edge (the box's click): commit ONE seek and close the readout.</summary>
    Action Commit { get; }
    /// <summary>A cancelled drag.</summary>
    Action Cancel { get; }
    /// <summary>The hover over a chapter rail (the tip follows it).</summary>
    Action<Point2> HoverMove { get; }
    /// <summary>The pointer leaving a chapter rail.</summary>
    Action PointerExit { get; }
    /// <summary>The hit box's bounds: the rail's width in pixels.</summary>
    Action<RectF> BoundsChanged { get; }
    /// <summary>The thumb ring's node: the readout bubble's anchor.</summary>
    Action<NodeHandle> ThumbRealized { get; }

    /// <summary>Is the deck playing (the live line breathes only while it is).</summary>
    IReadSignal<bool> Playing { get; }

    /// <summary>The keyboard ladder's commit clock: zero-size machinery every rail places, so the Left/Right steps commit wherever a rail is.</summary>
    Element KeyLadderTick();
}

/// <summary>The props of <see cref="ViewKeys.SeekRail"/>: one render of the rail's core host.</summary>
public sealed record SeekRailProps
{
    /// <summary>The binds and handlers.</summary>
    public required ISeekRailHost Host { get; init; }

    /// <summary>The scrub gate: a rail that can seek (enabled fills, hover and press, the handlers wired).</summary>
    public bool Enabled { get; init; }

    /// <summary>A sliding DVR window: the rail's right end is the live edge, so it is marked.</summary>
    public bool Dvr { get; init; }

    /// <summary>A broadcast with NOTHING TO REWIND: no rail at all, the live line instead (no hit test, no role).</summary>
    public bool Line { get; init; }

    /// <summary>An episode's chapter rail: the hover handlers are wired (when enabled) and the pointer-exit always.</summary>
    public bool ChapterRail { get; init; }

    /// <summary>The chapter decoration's core host (it mounts <see cref="ViewKeys.SeekChapters"/>) while <see cref="ChapterRail"/>
    /// holds; null otherwise (the picture places an inert box in its layer).</summary>
    public Element? Chapters { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.SeekChapters"/>: the ticks and the tip's anchor. Pure decoration: nothing here takes input.</summary>
public sealed record SeekChaptersProps
{
    /// <summary>Where each chapter starts, as a fraction of the rail (the first chapter's start and markers closer than the rail can
    /// show are already dropped).</summary>
    public required float[] Fractions { get; init; }

    /// <summary>The rail's width in pixels (the ticks' and the anchor's translate scale by it).</summary>
    public required IReadSignal<float> Width { get; init; }

    /// <summary>Where the tip points, as a fraction of the rail: the scrub target while scrubbing, else the hovered point.</summary>
    public required Func<float> TipFraction { get; init; }

    /// <summary>The anchor's node: the stock overlay positioner follows it and clamps the bubble to the window.</summary>
    public required Action<NodeHandle> TipAnchorRealized { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.ChapterTip"/>: the chapter under the pointer, as live text (bound, so the bubble never
/// re-renders per move). Built once per chapter rail.</summary>
public sealed record ChapterTipProps
{
    /// <summary>The time under the pointer.</summary>
    public required Func<string> Time { get; init; }
    /// <summary>The chapter's title (or the generic "Chapters" word for an untitled one).</summary>
    public required Func<string> Title { get; init; }
    /// <summary>The chapter's start – end.</summary>
    public required Func<string> Range { get; init; }
    /// <summary>Is the pointer over a chapter (the title and range lines show only then).</summary>
    public required Func<bool> HasChapter { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.SeekReadout"/>: the time under the pointer while a scrub gesture lasts. Built once per rail.</summary>
public sealed record SeekReadoutProps
{
    /// <summary>The rail's own scrub target, live from the pointer fraction (bound).</summary>
    public required Func<string> Text { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.TimeLabel"/>: one render of a time label's core host. The digits are a BOUND text channel
/// (<see cref="Label"/>), so a position tick never re-renders the label.</summary>
public sealed record TimeLabelProps
{
    /// <summary>The right-hand slot (−remaining ⇄ duration; the click toggles which), else the elapsed one.</summary>
    public bool Remaining { get; init; }

    /// <summary>The surface's theme-invariant on-media ink (the stage, the on-media strip); null is the bar's own caption ink.</summary>
    public ColorF? Ink { get; init; }

    /// <summary>A live broadcast: the digits' slot is a FLOOR (h:mm:ss) rather than a fixed reservation.</summary>
    public bool IsLive { get; init; }

    /// <summary>The right slot while live: the LIVE word-mark or the GO LIVE action instead of digits.</summary>
    public bool LiveSlot { get; init; }

    /// <summary>The label's text, read inside the bind (position, duration, the scrub target, the show-remaining preference).</summary>
    public required Func<string> Label { get; init; }

    /// <summary>Flip the right slot between −remaining and the duration.</summary>
    public required Action ToggleRemaining { get; init; }

    /// <summary>The live slot's DECIDED edge: behind a rewindable window it offers the way back (read through the slot's own memo).</summary>
    public required Func<bool> OffersGoLive { get; init; }

    /// <summary>"GO LIVE −m:ss", read inside the bind.</summary>
    public required Func<string> GoLiveText { get; init; }

    /// <summary>Jump to the live edge.</summary>
    public required Action GoLive { get; init; }
}

/// <summary>One link of the player bar's credits line: a credited artist (a track) or the show (an episode).</summary>
/// <param name="Text">What the link says (an artist with no name is still listed: the compact line shows the first credit as it is).</param>
/// <param name="Navigate">Where a click goes; null is an INERT name (no route), never a styled-but-dead link.</param>
/// <param name="Key">The identity the link is keyed on (<c>artist:&lt;slot&gt;</c>, <c>show:&lt;slot&gt;</c>).</param>
public readonly record struct PlayerCreditLink(string Text, Action? Navigate, string Key);

/// <summary>The player bar's credits line, as the now-playing FACE states it (a held face keeps its credits, so the line never empties
/// on a skip). <see cref="None"/> is the empty row: nothing playing, a partial credit list (the line waits for the whole group), or a
/// playable with no credits.</summary>
public sealed record PlayerCredits
{
    /// <summary>The empty row.</summary>
    public static readonly PlayerCredits None = new();

    /// <summary>An episode's show (the one link), or null for a track.</summary>
    public PlayerCreditLink? Show { get; init; }

    /// <summary>A track's credited artists, in billing order.</summary>
    public PlayerCreditLink[] Artists { get; init; } = [];

    /// <summary>The compact line's "+N" chip identity (keyed by its artist set, so the frozen rows are the right ones).</summary>
    public string OverflowKey { get; init; } = "";

    /// <summary>The "+N" chip's menu: every credited artist, built at OPEN time from live reads.</summary>
    public Func<IReadOnlyList<MenuFlyoutItem>> OverflowRows { get; init; } = static () => [];
}
