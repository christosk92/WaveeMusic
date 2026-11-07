// ── Theming/Contracts/BandProps.cs — the data a band-style navigation pane draws from (themes plan 6.3, P5 ZK) ─────────────────
//
// A structure whose `Shell.NavPane` is a BAND (Zune: two rows over the page column, pivots and pins in row 1, the page's own words in
// row 2) does not mount the core sidebar pane; it draws the band from this record, which the core hands it on `NavPaneProps.Band`.
// Everything here is core state the band must not reach for itself (WVTH002: a template never names Shell or the nav model):
//   · ROW 1: the destinations (home, browse, library, recents) and which one is lit for the route on screen;
//   · PINS: the user's pins, capped by the active structure's `ShellCapabilities.MaxBandPins`, each with its open and play verbs and
//     its playing fact and its context menu (1.88), and the drag-to-pin target the band puts on them (`PinDrop`, 1.88);
//   · ROW 2: the current page's `PageChrome` (its views, actions and backdrop), as a signal;
//   · COLLAPSE: the current page's scroll (offset and direction), so a band can collapse to row 1 while the page scrolls down;
//   · THE HUB LIST: the destinations a narrow band's lead word opens as a list (row 1 plus profile and settings).
// Whether the pins show at a window tier, how the band collapses and how its words overflow are the structure's decisions.
//
// The record is one static instance (equal across re-pushes); every live value is a signal or a Func read by the template.

using FluentGpu.Controls;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>One destination of the band's row 1 or of the hub list.</summary>
/// <param name="Id">A stable id ("home", "browse", "library", "recents", "profile", "settings"): the node key, never the label.</param>
/// <param name="Label">The destination's word, as the core spells it (the theme's role decides the casing).</param>
/// <param name="Glyph">The destination's glyph (a theme that draws words only ignores it).</param>
/// <param name="Go">Navigates to the destination.</param>
public sealed record BandDestination(string Id, string Label, string Glyph, Action Go);

/// <summary>One pin of the band.</summary>
/// <param name="Id">The pin's id (stable across renders and sessions).</param>
/// <param name="Label">The pin's name (the tooltip; a band draws the art).</param>
/// <param name="Image">The pin's art (an image uri), or null while none is known.</param>
/// <param name="Circular">The art is a person's (drawn round where the style rounds avatars).</param>
/// <param name="Go">Opens the pinned destination.</param>
/// <param name="Play">Plays it as a context; null when it is not playable (a route pin).</param>
/// <param name="Playing">Is the playing context this pin (or inside it)? Read tracked.</param>
public sealed record BandPin(string Id, string Label, string? Image, bool Circular, Action Go, Action? Play, Func<bool> Playing)
{
    /// <summary>The pin's context menu (contract 1.88), built when it opens: the entity's own menu (play, open, the queue pair, save, share) with
    /// Unpin and the band's Move left / Move right; a route pin's Open, Unpin and the moves. Null: the pin has no menu.</summary>
    public Func<ContextMenuModel?>? Menu { get; init; }
}

/// <summary>Dropping onto a band's pins (contract 1.88): an album, a playlist, an artist, a show or a route dragged there is pinned where the band
/// shows it (after the last pin it shows; in the last place it shows when the band is full), an already pinned one moves there. A track drag is not
/// this target's business (transparent), so it passes through.</summary>
/// <param name="Target">The drop target the band puts on its pins (and on the drop slot it shows while <paramref name="Armed"/>).</param>
/// <param name="Over">A pinnable drag is over <paramref name="Target"/> right now (the band lights its drop slot). A signal: bind it.</param>
/// <param name="Armed">A pinnable drag is live (read on the drag's begin and end edges, e.g. from a <c>UseDragState</c> watcher): while it holds the
/// band shows where a drop lands, even with no pins yet.</param>
public sealed record BandPinDrop(DropTargetSpec Target, IReadSignal<bool> Over, Func<bool> Armed);

/// <summary>Which way the current page last scrolled.</summary>
public enum PageScrollDirection : byte
{
    /// <summary>At rest at the top, or no scroller is known.</summary>
    None,

    /// <summary>Towards the end of the page (content moving up).</summary>
    Down,

    /// <summary>Back towards the top.</summary>
    Up,
}

/// <summary>The current page's scroll, as a band reads it.</summary>
/// <param name="Offset">The page's main scroller's offset in DIP (0 at the top, or when the page reports no scroller).</param>
/// <param name="Direction">The last direction travelled past the core's dead zone.</param>
public readonly record struct PageScroll(float Offset, PageScrollDirection Direction)
{
    /// <summary>At the top, no direction.</summary>
    public static PageScroll Top => default;
}

/// <summary>The data of a band-style navigation pane (<see cref="NavPaneProps.Band"/>, contract 1.26).</summary>
public sealed record BandProps
{
    /// <summary>Row 1's destinations, in order: home, browse, library, recents. A read hands back the same instance until the language
    /// changes (the labels are localised).</summary>
    public required Func<IReadOnlyList<BandDestination>> Destinations { get; init; }

    /// <summary>The index in <see cref="Destinations"/> lit for the route on screen, -1 when none is (a detail page reached from
    /// elsewhere, settings). Read tracked: it follows every navigation.</summary>
    public required Func<int> Current { get; init; }

    /// <summary>The index in <see cref="Destinations"/> the page on screen lives under (contract 1.62): <see cref="Current"/> where that lights one;
    /// on a detail page the destination its trail starts from (the journey's origin, else the information architecture's root: library for an
    /// album, playlist, artist, show or episode; browse for a search); -1 when none (settings, profile). A band that lights its section word on a
    /// detail page reads this; <see cref="Current"/> still says whether the page is a destination. Read tracked. Null: the band lights
    /// <see cref="Current"/> alone.</summary>
    public Func<int>? Section { get; init; }

    /// <summary>Whether the page whose <see cref="Chrome"/> the band shows is a detail page (no destination of its own), contract 1.83 (audit FU2d).
    /// It follows the page swap's LANDING, as <see cref="Chrome"/> does, while <see cref="Current"/> lights a clicked destination at once: a band that
    /// leads row 2 with the page's title reads this, so the leaving detail page's row keeps its title until the swap lands and its words never jump
    /// to the lead under a destination's lit word. Read tracked. Null: <see cref="Current"/> &lt; 0.</summary>
    public Func<bool>? ChromeIsDetail { get; init; }

    /// <summary>The pins, in the user's order, at most the active structure's <c>MaxBandPins</c>. Read tracked: it follows a pin, an
    /// unpin, a move and a capability change.</summary>
    public required Func<IReadOnlyList<BandPin>> Pins { get; init; }

    /// <summary>Drag to pin (contract 1.88): the drop target a band puts on its pins. Null: the band takes no drop.</summary>
    public BandPinDrop? PinDrop { get; init; }

    /// <summary>The current page's chrome (title, trail, row 2), or null before any page was shown.</summary>
    public required IReadSignal<PageChrome?> Chrome { get; init; }

    /// <summary>The current page's scroll: what a band collapses on.</summary>
    public required IReadSignal<PageScroll> Scroll { get; init; }

    /// <summary>The hub list: the destinations a narrow band's lead word opens (row 1, then profile when signed in, then settings).
    /// Read when the list opens.</summary>
    public required Func<IReadOnlyList<BandDestination>> Hub { get; init; }
}
