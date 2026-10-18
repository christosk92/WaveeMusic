// ── Theming/Contracts/SelectionDock.cs — the docked selection bar seam (contract 1.47; P5 Zune fidelity round 2) ────────────────────────────
//
// A track table in selection mode draws its selection bar IN FLOW, in the command lane above the header (Classic's picture). A theme whose frame
// docks the bar at the foot of the page (Zune's Z-State-Rows: "3 selected · play · play next · add to playlist · like · remove · select all · done",
// a 60 bar above the player bar) provides a `SelectionDockHost` through `SelectionDockHost.Context` around the page, and draws the bar from the model the
// table publishes. The seam is ADDITIVE and opt-in: where no host is provided (Classic's frame, a view mounted alone) the table keeps its in-flow bar and
// nothing here is ever read.
//
// WHAT EACH SIDE OWNS. The core: when the bar shows (`SelectionBarRules`), which verbs it carries (the same set and the same enabled / checked / labelled
// facts as the in-flow bar: play, play next, add to queue, like, the "..." menu with select all, done), what a verb does and that it exits selection
// after it runs. The theme: where the bar sits, how it is laid out at each tier, its type and its motion. The frame reserves the bar's height at the
// foot of the page while the model is non-null (an animated reservation, so the last rows are never covered).

using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>One verb of the docked selection bar (<see cref="SelectionDockProps.Commands"/>): already resolved against the selection (label, glyph, enabled).</summary>
/// <param name="Key">A stable id (the action's name): the theme keys its button with it.</param>
/// <param name="Label">The verb's words ("Play", "Play 3 next", "Saved"...).</param>
/// <param name="Glyph">The icon glyph; <paramref name="Font"/> its font family (null = the default icon face).</param>
/// <param name="Primary">The verb a theme gives the accent disc (play).</param>
/// <param name="Invoke">Runs the verb and exits selection. Never null; a disabled verb's is a no-op.</param>
public sealed record SelectionDockCommand(string Key, string Label, string Glyph, string? Font, bool Enabled, bool Primary, Action Invoke);

/// <summary>What the table publishes while its selection bar shows (the props of the theme's docked bar).</summary>
public sealed record SelectionDockProps
{
    /// <summary>"3 selected".</summary>
    public string CountLabel { get; init; } = "";

    /// <summary>The selected count.</summary>
    public int Count { get; init; }

    /// <summary>How many rows the list holds ("select all 42"); 0 when unknown.</summary>
    public int Total { get; init; }

    /// <summary>The verbs, in order (the in-flow bar's: play, play next, add to queue, like; an owned playlist's also remove).</summary>
    public IReadOnlyList<SelectionDockCommand> Commands { get; init; } = [];

    /// <summary>The "..." button (the selection's menu: the track menu's rows, then select all). The core's own element: it opens its menu from itself.</summary>
    public Element? More { get; init; }

    /// <summary>"Select all" and its verb.</summary>
    public string SelectAllLabel { get; init; } = "";

    /// <summary>Select every row.</summary>
    public Action SelectAll { get; init; } = static () => { };

    /// <summary>"Done" (the in-flow bar's clear-selection cross).</summary>
    public string DoneLabel { get; init; } = "";

    /// <summary>Clear the selection and leave selection mode.</summary>
    public Action Done { get; init; } = static () => { };
}

/// <summary>The theme frame's side of the docked selection bar: the signal the table writes and the frame reads. A host exists only where a frame
/// provides it through <see cref="Context"/>.</summary>
public sealed class SelectionDockHost
{
    /// <summary>The context a frame provides around its page (null = no dock: the table keeps its in-flow bar).</summary>
    public static readonly Context<SelectionDockHost?> Context = new(null);

    /// <summary>The bar's model while a selection shows it; null otherwise. Written by the table that owns the selection, one at a time.</summary>
    public Signal<SelectionDockProps?> Model { get; } = new(null);
}
