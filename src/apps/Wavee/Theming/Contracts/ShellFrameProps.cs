// ── Theming/Contracts/ShellFrameProps.cs — the props of `Shell.Frame` (themes plan 6.0, T4a) ───────────────────────────────
//
// THE FRAME IS A CORE HOST AND A THEME VIEW. The core frame root (`Shell.FrameRoot`, [CoreHost]) owns every hook, effect and
// state machine of the window: the sidebar band and pane presentation, the tiers, auto zoom, the rail <-> docked-video
// coupling, the chrome allocation, the material mirror. What it does NOT own is the picture: it builds this record once and
// mounts `Slots.View(ViewKeys.ShellFrame, props)`; the structure's template lays the window out.
//
// WHAT THE RECORD CARRIES. Three things, none of them presentation:
//   · Live values as READ-ONLY signals or `Func`s (the viewport, the sidebar's fade, a column width, "does the rail float"):
//     the template binds them with `Prop.Of(...)`, so a resize, a navigation or a theme flip never re-runs the template.
//   · Behaviour the host wires (the shell chords, the key handler, the file-drop target, the realized-node captures the
//     probes and the scrim clip read): the template only PLACES them.
//   · Element builders of the surfaces that are not this PR's: each is a `Func<Element>` over the core builder that exists
//     today (the material, the player bar, the sidebar pane, the rail, the stage, the overlay layers). A later
//     T-PR gives its surface its own view key and replaces the builder with `ctx.View(...)`; nothing else here changes.
//     T4a draws the FRAME: the column, the window layers, the content region and its seams.
//
// Members are init-only and every delegate is a cached static or a closure built once at mount (the frame root renders
// once for the process), so the props are equal across re-pushes and an equal record skips the template.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>The props of <see cref="ViewKeys.ShellFrame"/>.</summary>
public sealed record ShellFrameProps
{
    // ── live values (read-only) ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The layout viewport in DIP (after app zoom).</summary>
    public required IReadSignal<Size2> Viewport { get; init; }

    /// <summary>The sidebar content's opacity while a seam drag collapses it (1 at rest).</summary>
    public required IReadSignal<float> SidebarFade { get; init; }

    /// <summary>The right rail's content opacity while a rail drag collapses it (1 at rest).</summary>
    public required IReadSignal<float> RailFade { get; init; }

    /// <summary>The presented sidebar width: where the sidebar seam sits.</summary>
    public required IReadSignal<float> SidebarPresentedWidth { get; init; }

    /// <summary>The right rail's committed width.</summary>
    public required IReadSignal<float> RailWidth { get; init; }

    /// <summary>Is the chrome (title row, content region, player bar) mounted? False under fullscreen video or the immersive stage.</summary>
    public required Func<bool> ChromeMounted { get; init; }

    /// <summary>Is the fullscreen (immersive) stage up?</summary>
    public required Func<bool> ImmersiveOpen { get; init; }

    /// <summary>Is the docked sidebar pane mounted (every mode but Minimal)?</summary>
    public required Func<bool> SidebarPaneDocked { get; init; }

    /// <summary>Does the right rail float over the content (open and not fitting inline)?</summary>
    public required Func<bool> RailFloats { get; init; }

    /// <summary>The sidebar column's width: the presented width, or the expanded width while a drag peeks.</summary>
    public required Func<float> SidebarColumnWidth { get; init; }

    /// <summary>The width of the sidebar seam's strip (0 where the band has no seam).</summary>
    public required Func<float> SidebarSeamWidth { get; init; }

    /// <summary>The rail's breathing gap: present only while the rail is inline.</summary>
    public required Func<float> RailGapWidth { get; init; }

    /// <summary>The rail reservation beside the content card (0 unless the rail is open and inline).</summary>
    public required Func<float> RailReservedWidth { get; init; }

    /// <summary>The width of the rail seam's strip (0 while the rail is closed).</summary>
    public required Func<float> RailSeamWidth { get; init; }

    /// <summary>The rail seam's x translation.</summary>
    public required Func<float> RailSeamX { get; init; }

    // ── behaviour the host wires ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The shell chords: zero-size accelerator boxes the column mounts first.</summary>
    public required IReadOnlyList<Element> Chords { get; init; }

    /// <summary>The key handler bare Escape and Space bubble to.</summary>
    public required Action<KeyEventArgs> OnKeyDown { get; init; }

    /// <summary>The shell-wide "drop a file to play it" target.</summary>
    public required DropTargetSpec FileDrop { get; init; }

    /// <summary>The sidebar column was realized (the pane-invariant probe reads its width).</summary>
    public required Action<NodeHandle> OnSidebarColumnRealized { get; init; }

    /// <summary>The content region was realized (the drag spotlight scrim is scoped to it).</summary>
    public required Action<NodeHandle> OnContentRegionRealized { get; init; }

    /// <summary>The content region's bounds changed (the scrim clip follows).</summary>
    public required Action<RectF> OnContentRegionBounds { get; init; }

    // ── the merged title bar row (T4b): its own view key, `Shell.TitleBar` ──────────────────────────────────────────

    /// <summary>The props of the merged title bar row. The frame mounts <see cref="ViewKeys.ShellTitleBar"/> with them.</summary>
    public required ShellTitleBarProps TitleBar { get; init; }

    // ── surfaces that are not T4a's: element builders until each has its own key ────────────────────────────────────

    /// <summary>The player bar dock (T6a).</summary>
    public required Func<Element> PlayerBarDock { get; init; }

    /// <summary>The shell material under the column (T4c).</summary>
    public required Func<Element> MaterialLayer { get; init; }

    /// <summary>The page-swap host: the keep-alive boundary, the masthead and the offline lane. Draws its states through
    /// <see cref="ViewKeys.ShellContentState"/>.</summary>
    public required Func<Element> ContentHost { get; init; }

    /// <summary>The docked sidebar pane (T5).</summary>
    public required Func<Element> SidebarPane { get; init; }

    /// <summary>The sidebar seam: the splitter wired to the sidebar width.</summary>
    public required Func<Element> SidebarSeam { get; init; }

    /// <summary>The right rail's body (T6b).</summary>
    public required Func<Element> RailBody { get; init; }

    /// <summary>The rail seam: the splitter wired to the rail width.</summary>
    public required Func<Element> RailSeam { get; init; }

    /// <summary>The narrow-window drawer (T4c).</summary>
    public required Func<Element> NarrowDrawer { get; init; }

    /// <summary>The fullscreen stage (T7a).</summary>
    public required Func<Element> StageView { get; init; }

    /// <summary>The floating video layer.</summary>
    public required Func<Element> VideoPipLayer { get; init; }

    /// <summary>The fullscreen video layer.</summary>
    public required Func<Element> VideoFullscreenLayer { get; init; }

    /// <summary>The overlay layer (banner, toast cues) (T11a).</summary>
    public required Func<Element> Overlays { get; init; }

    /// <summary>The file-drop cue (T4c).</summary>
    public required Func<Element> FileDropLayer { get; init; }

    /// <summary>The command palette (T4c).</summary>
    public required Func<Element> CommandPalette { get; init; }

    /// <summary>The setup wizard's cover scrim (T4c).</summary>
    public required Func<Element> CoverScrim { get; init; }

    /// <summary>The drag chip (T4c).</summary>
    public required Func<Element> DragPreview { get; init; }

    /// <summary>The frame-rate overlay (T4c).</summary>
    public required Func<Element> FpsOverlay { get; init; }
}
