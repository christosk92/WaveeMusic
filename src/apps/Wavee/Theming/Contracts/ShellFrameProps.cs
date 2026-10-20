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
//   · Element builders of what is behaviour or a core host, not a picture: each is a `Func<Element>` over the core builder (the
//     content host, the overlay layer, the two seams' splitters, the narrow drawer's host and the video layers, which are core hosts
//     that mount their own keys).
//     Every surface with a picture is its own key: those members carry the child view's PROPS (the title bar, the player bar, the
//     material, the nav pane, the right panel, the stage, the file-drop cue, the cover scrim, the drag chip, the frame-rate overlay...)
//     and the frame mounts `ctx.View(key, props)`.
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

    /// <summary>The right rail's LIVE width: the seam drag's width while a drag is in flight, the committed width at rest; while a drag
    /// closed the panel it holds the width the panel closed at (until it opens again). A frame that lets a drag move the panel and the page
    /// every frame binds this; one that leaves the drag a guide line binds <see cref="RailWidth"/>. Null: the committed width.</summary>
    public IReadSignal<float>? RailLiveWidth { get; init; }

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

    // ── the window tier's panel placement and the second docked pane (contract 1.15, TC2) ──────────────────────────────

    /// <summary>Where the window tier lays the panel out (dock, float or sheet); null where the core publishes none (Dock).</summary>
    public Func<PanelPlacement>? PanelPlacement { get; init; }

    /// <summary>Is the second docked pane (the queue beside the primary panel) on screen? Null: never.</summary>
    public Func<bool>? SecondPanelOpen { get; init; }

    /// <summary>The second pane's width.</summary>
    public Func<float>? SecondPanelWidth { get; init; }

    /// <summary>The second pane's breathing gap (8 while open, else 0).</summary>
    public Func<float>? SecondGapWidth { get; init; }

    /// <summary>The second pane's row reservation (its width while open, else 0).</summary>
    public Func<float>? SecondReservedWidth { get; init; }

    /// <summary>The second pane's props: the frame mounts <see cref="ViewKeys.ShellPanel"/> with them beside the primary panel.</summary>
    public PanelProps? SecondPanel { get; init; }

    // ── every docked pane beside the primary (contract 1.29, ZT2) ────────────────────────────────────────────────────────

    /// <summary>Every pane the structure's arrangement can dock beside the primary panel, in pane order (the second pane, then the third):
    /// each one's open state, width, reservation, side and props. The members above are the first of them, as a frame that docks one pane
    /// beside the primary reads it (Classic). Null where the core hands none.</summary>
    public IReadOnlyList<DockedPaneProps>? DockedPanes { get; init; }

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

    // ── the frame's other surfaces: a key's props, or a builder over a core host or a behaviour control ──────────────

    /// <summary>The player bar's props: the frame mounts <see cref="ViewKeys.PlayerBar"/> with them (T6a). The bar's hooks and
    /// decisions are the core half the template runs first (<see cref="IPlayerBarHost"/>).</summary>
    public required PlayerBarProps PlayerBar { get; init; }

    /// <summary>The shell material under the column: the props of <see cref="ViewKeys.ShellMaterialLayer"/> (T4c).</summary>
    public required MaterialProps Material { get; init; }

    /// <summary>The page-swap host: the keep-alive boundary, the masthead and the offline lane. Draws its states through
    /// <see cref="ViewKeys.ShellContentState"/>.</summary>
    public required Func<Element> ContentHost { get; init; }

    /// <summary>The docked sidebar pane's props: the frame mounts it as <see cref="ViewKeys.ShellNavPane"/> (T5a), so the pane's list is
    /// the structure's while the core pane keeps its state machines.</summary>
    public required NavPaneProps NavPane { get; init; }

    /// <summary>The sidebar seam: the splitter wired to the sidebar width.</summary>
    public required Func<Element> SidebarSeam { get; init; }

    /// <summary>The right panel's props: the frame mounts <see cref="ViewKeys.ShellPanel"/> with them (T6b). The panel's state machine
    /// (open, mode, width, floats, the docked video) stays the core's; the template draws the decided <see cref="PanelView"/>.</summary>
    public required PanelProps Panel { get; init; }

    /// <summary>The rail seam: the splitter wired to the rail width.</summary>
    public required Func<Element> RailSeam { get; init; }

    /// <summary>The narrow-window drawer's core host (T4c): it keeps the key-preview chain, the leaf-navigation close and the mount
    /// decision, and mounts <see cref="ViewKeys.ShellNarrowDrawer"/> itself (its props change with the band and the open flag).</summary>
    public required Func<Element> NarrowDrawer { get; init; }

    /// <summary>The fullscreen stage: the props of <see cref="ViewKeys.StageRoot"/> (T7a1). The frame mounts the key inside the immersive
    /// layer's Show; the core surface keeps the stage's hooks, machines and key map and the structure draws the layer stack
    /// (<see cref="StageRootProps.Mount"/>).</summary>
    public required StageRootProps StageRoot { get; init; }

    /// <summary>The video layer: the core hosts of the stay-mounted presenter (which mounts <see cref="ViewKeys.VideoPip"/>), the pop-out's
    /// owner (<see cref="ViewKeys.VideoPopOut"/>), the host observer and the join fold. A builder for good (T7c): these are lifecycle, not picture.</summary>
    public required Func<Element> VideoPipLayer { get; init; }

    /// <summary>The fullscreen video layer: the core host of the fullscreen mode (the OS window, the focus scope, Escape / F), which mounts
    /// <see cref="ViewKeys.VideoFullscreen"/> for its chrome. A builder for good (T7c).</summary>
    public required Func<Element> VideoFullscreenLayer { get; init; }

    /// <summary>The overlay layer (banner, toast cues) (T11a).</summary>
    public required Func<Element> Overlays { get; init; }

    /// <summary>The file-drop cue: the props of <see cref="ViewKeys.ShellFileDrop"/> (T4c).</summary>
    public required FileDropProps FileDropCue { get; init; }

    /// <summary>The command palette: the props of <see cref="ViewKeys.ShellCommandPalette"/> (T4c).</summary>
    public required PaletteProps CommandPalette { get; init; }

    /// <summary>The setup wizard's cover scrim: the props of <see cref="ViewKeys.ShellCoverScrim"/> (T4c).</summary>
    public required CoverScrimProps CoverScrim { get; init; }

    /// <summary>The drag chip layer: the props of <see cref="ViewKeys.ShellDragChip"/> (T4c).</summary>
    public required DragChipProps DragChip { get; init; }

    /// <summary>The frame-rate overlay: the props of <see cref="ViewKeys.ShellFps"/> (T4c).</summary>
    public required FpsProps Fps { get; init; }
}

/// <summary>One docked pane beside the primary panel (contract 1.29): what the frame binds to place it and the props it mounts
/// <see cref="ViewKeys.ShellPanel"/> with. Built once, every member a bound read.</summary>
public sealed record DockedPaneProps
{
    /// <summary>The pane's place in the arrangement: 0 the second pane, 1 the third. Stable, so the frame keys the pane's host by it and a
    /// crossing that adds or drops another pane keeps this one's node.</summary>
    public required int Index { get; init; }

    /// <summary>Is the pane on screen (the tier docks it and it is open)?</summary>
    public required Func<bool> Open { get; init; }

    /// <summary>The pane's width (also while it is closed: its host keeps it, the pane slides out of it).</summary>
    public required Func<float> Width { get; init; }

    /// <summary>The pane's row reservation: its width while it is open, else 0.</summary>
    public required Func<float> ReservedWidth { get; init; }

    /// <summary>The side of the primary panel it sits on in the window tier (the widest tier's side where the tier docks it not).</summary>
    public required Func<PaneSide> Side { get; init; }

    /// <summary>The pane's props: its one body, docked, with no docked video card.</summary>
    public required PanelProps Panel { get; init; }
}
