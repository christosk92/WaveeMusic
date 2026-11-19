// ── Theming/Contracts/HintProps.cs — the props of the hint, the explanation badge and the in-context dials (contract 1.115, 2.2) ────
//
// Settings rework phase 3 (handoff/settings-2026-11-18/DESIGN.md 5.2, 6.1-6.5). The STATE stays core: which hint qualifies (the
// table `Hints`, `Shell/Hints.cs`), the budget (one on screen, one new per session, the show count and days, the 30-day snooze, the
// third dismissal, the global switch, the never-when list), retire-on-use, the clip lookup of a badge (`SettingsExplain`) and every
// writer (`SettingsDial.Writer`). A theme draws what these carry, in its own pattern (a TeachingTip, a line, an InfoBar, a dot, an
// accent word), and calls the verbs handed to it; it never keeps a value of its own and never reaches the store (WVTH002).

using System.Collections.Immutable;
using FluentGpu.Foundation;

namespace Wavee.Theming.Contracts;

/// <summary>What a hint's one action does (design 6.1).</summary>
public enum HintAction : byte
{
    /// <summary>A button that writes the dial's own writer (a single, reversible, free switch); after the write the hint turns into
    /// its confirmation (<see cref="HintProps.Done"/>) with "Change".</summary>
    Inline,
    /// <summary>A button that deep-links to the dial (or opens the pointer's place): the change has a cost or a choice.</summary>
    Link,
    /// <summary>Points at a control the surface already draws (<see cref="HintProps.Anchor"/>); draws no control of its own.</summary>
    Point,
    /// <summary>A one-time dot on a menu item (no words); the item itself opens the place.</summary>
    Badge,
    /// <summary>State at rest, shown every time, never counted against the budget (a quality row's "lowered on a metered connection").</summary>
    State,
}

/// <summary>The surfaces a hint can sit on (design 6.3): the surface argument of <see cref="ViewKeys.Hint"/>.</summary>
public static class HintSurfaces
{
    /// <summary>The lyrics pane's footer strip (where the AI disclosure line lives), in every lyrics surface but the mini player's face.</summary>
    public const string LyricsFooter = "lyrics.footer";
    /// <summary>The devices flyout's quality row (a second line) and its last row.</summary>
    public const string DevicePicker = "devicePicker";
    /// <summary>The queue (Up next) panel, by its Crossfade switch.</summary>
    public const string QueuePanel = "queue.panel";
    /// <summary>By the player bar's volume slider.</summary>
    public const string PlayerVolume = "playerBar.volume";
    /// <summary>By the player bar's play button.</summary>
    public const string PlayButton = "playerBar.play";
    /// <summary>The player bar (a line while an audiobook plays).</summary>
    public const string PlayerBar = "playerBar";
    /// <summary>The notification bell flyout's header.</summary>
    public const string BellHeader = "bell.header";
    /// <summary>An InfoBar line at the top of Home (floats over the page; never reflows it).</summary>
    public const string HomeBanner = "home.banner";
    /// <summary>The back button (WinUI) or a line under the page head (Classic).</summary>
    public const string PageHead = "page.head";
    /// <summary>The title bar (WinUI).</summary>
    public const string TitleBar = "titleBar";
    /// <summary>The daylist tile's "..." menu (a dot on its options item).</summary>
    public const string DaylistMenu = "home.daylist.menu";
    /// <summary>The Settings home's Suggestions (never in the app).</summary>
    public const string SettingsHome = "settings.home";
}

/// <summary>The props of <see cref="ViewKeys.Hint"/>: one hint on one surface, worded and wired by the core.</summary>
public sealed record HintProps
{
    /// <summary>The surface it sits on (<see cref="HintSurfaces"/>): the one key serves every surface, the template draws each in its place.</summary>
    public required string Surface { get; init; }

    /// <summary>The hint's id (<c>ai-lyrics</c>), stable: a template keys its node <c>hint:&lt;id&gt;</c>.</summary>
    public required string Id { get; init; }

    public required HintAction Action { get; init; }

    /// <summary>The line (resolved text; a theme applies its own case role).</summary>
    public required string Text { get; init; }

    /// <summary>The button's word ("Set up", "Turn on"), or null for a state line, a pointer or a badge.</summary>
    public string? ActionLabel { get; init; }

    /// <summary>The button: an inline write through the dial's writer, a deep link, or opening the pointer's place. Null with no button.</summary>
    public Action? Act { get; init; }

    /// <summary>"×" / "Not now": not again for the hint's snooze, the third retires it. Null for a state line.</summary>
    public Action? Dismiss { get; init; }

    /// <summary>"Don't show tips" (the overflow of every hint): writes the global switch. Null for a state line.</summary>
    public Action? NeverTips { get; init; }

    /// <summary>The inline write happened on this mount: draw <see cref="DoneText"/> and <see cref="ChangeLabel"/> instead of the offer.</summary>
    public bool Done { get; init; }

    /// <summary>The confirmation ("On · Normal"), or null.</summary>
    public string? DoneText { get; init; }

    /// <summary>The confirmation's link word ("Change"), or null.</summary>
    public string? ChangeLabel { get; init; }

    /// <summary>The confirmation's link (a deep link to the dial for the duration, the level, the mode), or null.</summary>
    public Action? Change { get; init; }

    /// <summary>The dial the hint is about (a registry id), or null for a feature pointer (the sleep timer).</summary>
    public string? DialId { get; init; }

    /// <summary>For <see cref="HintAction.Point"/>: the core's name for the control it points at (informational: a template aims at
    /// <see cref="Target"/>, the node the surface captured), or null.</summary>
    public string? Anchor { get; init; }

    /// <summary>Shown by the inspector's <c>hint show</c> (its trigger forced for one mount), not by its trigger.</summary>
    public bool Forced { get; init; }

    /// <summary>The control the hint points at, captured by the surface that mounted the slot (the play button, the volume slider, the
    /// queue's Crossfade switch, the back button, the title bar): a TeachingTip's target, a line's anchor. Null where the hint sits in a
    /// place of its own (a footer, a flyout's foot, Home's top). Contract 2.2.</summary>
    public Func<NodeHandle>? Target { get; init; }

    /// <summary>The surface <see cref="Target"/> lives in (the queue panel, the player bar, the page head's band), captured by the same
    /// surface: a line that floats at the target stays inside it, inset from its edges, never hanging over the page beside it. Null = the
    /// window. Contract 2.2.</summary>
    public Func<NodeHandle>? Bounds { get; init; }

    /// <summary>A click elsewhere closed it (a TeachingTip's light dismiss), or its confirmation is done: it goes for the rest of this
    /// session with no snooze (the budget's show was already counted). Null for a state line. Contract 2.2.</summary>
    public Action? SetAside { get; init; }

    /// <summary>A short heading for a template that titles its hint (a TeachingTip): the dial's own name ("Crossfade", "Audio
    /// normalization"), or null where the hint has no dial name. Contract 2.2.</summary>
    public string? Title { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.Explainer"/> (design 6.5): a dial's explanation badge and the tip it opens. The core
/// mounts it only for a dial whose clip exists for the active theme and app mode (a badge shows only where its clip exists).</summary>
public sealed record ExplainerProps
{
    /// <summary>The dial it explains (a registry id); a template keys its badge <see cref="SettingsKeys.Explain"/> (<c>settings:explain:&lt;dialId&gt;</c>).</summary>
    public required string DialId { get; init; }

    /// <summary>The caption (resolved <c>settings.explain.&lt;dialId&gt;</c>), also the clip's alt text.</summary>
    public required string Caption { get; init; }

    /// <summary>The clip (an animated WebP on disk) for the active theme and app mode, or null when it has none (then nothing draws).</summary>
    public string? ClipPath { get; init; }

    /// <summary>Windows' Animation effects are off: show the poster frame and a play button instead of autoplay (a value, not a branch).</summary>
    public required bool ReducedMotion { get; init; }

    /// <summary>Where the badge sits (a Settings row, a search result, an in-context panel: <see cref="HintSurfaces"/> or a panel's surface).</summary>
    public string Surface { get; init; } = "settings";
}

/// <summary>The props of <see cref="ViewKeys.SettingsDials"/> (design 5.2): a short list of dials a feature's own panel draws in its
/// grammar, bound to the same value and writer as Settings. The template reads each dial from the registry (<c>SettingsSpec.Dial</c>,
/// its <c>Current</c>) and writes through <c>SettingsDial.WriteToggle</c> / <c>WriteChoice</c> / <c>WriteSlider</c> / <c>Press</c>
/// with <see cref="Surface"/>, so Recently changed says where the change came from.</summary>
public sealed record SettingsDialsProps
{
    /// <summary>The panel ("home.daylist.options"): what a write records as its source.</summary>
    public required string Surface { get; init; }

    /// <summary>The dials, in order (registry ids).</summary>
    public required ImmutableArray<string> DialIds { get; init; }

    /// <summary>The small label above them ("For every daylist", resolved), or null.</summary>
    public string? Label { get; init; }

    /// <summary>Bumped when a dial's value may have changed (the page epoch), so a re-pushed record re-reads the values.</summary>
    public int Revision { get; init; }
}
