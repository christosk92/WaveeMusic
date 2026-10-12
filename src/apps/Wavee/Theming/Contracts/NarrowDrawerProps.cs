// ── Theming/Contracts/NarrowDrawerProps.cs — the props of `Shell.NarrowDrawer` (themes plan 6.0, T4c) ────────────────────────────
//
// THE DRAWER IS A CORE HOST AND A THEME VIEW. In the Narrow and Tiny bands the sidebar is an overlay over the page. The core host
// (`Shell.NarrowDrawer`, a [CoreHost]) keeps everything that decides: the band's overlay and pinned answers, the session's open flag,
// the single `KeyPreview` slot it chains while open (bare Escape closes the overlay, and while editing Escape belongs to the pane
// first), the leaf-navigation close, and the mount decision (`NarrowDrawerMount.ShouldMount`: the band ALONE, never the open flag, so
// the pane stays mounted for the whole time the shell is narrow and its first open has something to slide from). It builds the props
// and mounts `Shell.NarrowDrawer`; the theme draws the scrim and the pane and runs their open/close motion.
//
// The record has two halves. `Mounted` and `Open` are VALUES the host re-evaluates on every render (the band and the open flag); a
// change re-runs the template, exactly as the host's own re-render did. `Parts` is the STABLE half (cached statics of the core):
// the live reads the scrim and the pane component bind to, so a drag-resize inside the band never re-runs the template.

using FluentGpu.Dsl;

namespace Wavee.Theming.Contracts;

/// <summary>The props of <see cref="ViewKeys.ShellNarrowDrawer"/>.</summary>
public sealed record NarrowDrawerProps
{
    /// <summary>Is the overlay reachable (a Narrow or Tiny band)? False mounts nothing but a zero-size placeholder.</summary>
    public bool Mounted { get; init; }

    /// <summary>Is the overlay open (the session flag, or pinned while editing in a forced band)? Gates hit-testing.</summary>
    public bool Open { get; init; }

    /// <summary>The live reads and verbs the scrim and the pane bind to.</summary>
    public required NarrowDrawerParts Parts { get; init; }
}

/// <summary>The stable half of <see cref="NarrowDrawerProps"/>: delegates of the core, cached statics, equal across renders.</summary>
public sealed record NarrowDrawerParts
{
    /// <summary>Is the overlay open: the session flag, or pinned. Tracked: a component that calls it re-renders on a change.</summary>
    public required Func<bool> IsOpen { get; init; }

    /// <summary>Is the overlay pinned (editing in a forced band)? A pinned drawer stays open behind a non-dismissing scrim and the
    /// page behind it stays usable.</summary>
    public required Func<bool> IsPinned { get; init; }

    /// <summary>The pane's width for a viewport width (the preferred width, clamped to the window). Tracked on the preferred width.</summary>
    public required Func<float, float> Width { get; init; }

    /// <summary>The same answer, read without subscribing (the width a transition seeds from at render).</summary>
    public required Func<float, float> WidthNow { get; init; }

    /// <summary>The scrim's resting opacity for an open state.</summary>
    public required Func<bool, float> RestingOpacity { get; init; }

    /// <summary>The pane's resting x translation for an open state and a width (closed: fully off the left edge).</summary>
    public required Func<bool, float, float> RestingTranslateX { get; init; }

    /// <summary>Close the overlay (a click on the scrim).</summary>
    public required Action Close { get; init; }

    /// <summary>The pane's body: the sidebar's drawer pane.</summary>
    public required Func<Element> PaneBody { get; init; }
}
