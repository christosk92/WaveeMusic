// ── Theming/Contracts/ViewKeys.FrameBits.cs — the frame's small views (themes plan 6.0, T4c) ──────────────────────────────
//
// T4c moves the surfaces around the frame into the structure: the masthead and the page head, the material and its four cover
// leaves, the narrow drawer, the command palette, the history menu, the file-drop cue, the drag chip layer, the setup wizard's
// cover scrim and the frame-rate overlay. The title bar and the search arrive with T4b, the sidebar with T5.

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>The masthead band ("Browse > Category") the content host overlays on its page-swap boundary. Mounted by the core
    /// masthead host. Props: <see cref="MastheadProps"/>.</summary>
    public static readonly ViewKey<MastheadProps> ShellMasthead = new("Shell.Masthead");

    /// <summary>A page's head: the trail-as-title row. Placed by the masthead band (Classic); a page that draws its own head mounts it.
    /// Props: <see cref="PageHeadProps"/>.</summary>
    public static readonly ViewKey<PageHeadProps> PageHead = new("Page.Head");

    /// <summary>The material layer between live Mica and the chrome column. Mounted by the frame. Props: <see cref="MaterialProps"/>.</summary>
    public static readonly ViewKey<MaterialProps> ShellMaterialLayer = new("Shell.Material");

    /// <summary>The detail page's flat art-derived ground (a page-root sibling). Mounted by <c>Palette.PageTonePlane</c>.
    /// Props: <see cref="CoverToneProps"/>.</summary>
    public static readonly ViewKey<CoverToneProps> ShellCoverTone = new("Shell.Material.PageTone");

    /// <summary>The artist page's blend wash. Mounted by <c>Palette.ArtistBlendWash</c>. Props: <see cref="CoverBlendProps"/>.</summary>
    public static readonly ViewKey<CoverBlendProps> ShellCoverBlend = new("Shell.Material.ArtistBlend");

    /// <summary>The artist hero's photography veil (also the Home daylist card's). Mounted by <c>Palette.ArtistHeroVeil</c>.
    /// Props: <see cref="CoverVeilProps"/>.</summary>
    public static readonly ViewKey<CoverVeilProps> ShellCoverVeil = new("Shell.Material.ArtistVeil");

    /// <summary>The leaf that watches one cover and publishes the shell tint. Mounted by <c>Palette.ShellTint</c>.
    /// Props: <see cref="CoverTintProps"/>.</summary>
    public static readonly ViewKey<CoverTintProps> ShellCoverTint = new("Shell.Material.TintBinder");

    /// <summary>The narrow-window drawer: the sidebar as an overlay in the Narrow and Tiny bands. Mounted by the core drawer host.
    /// Props: <see cref="NarrowDrawerProps"/>.</summary>
    public static readonly ViewKey<NarrowDrawerProps> ShellNarrowDrawer = new("Shell.NarrowDrawer");

    /// <summary>The command palette (Ctrl+K): the lane, the popup's anchor and the card. Mounted by the frame.
    /// Props: <see cref="PaletteProps"/>.</summary>
    public static readonly ViewKey<PaletteProps> ShellCommandPalette = new("Shell.CommandPalette");

    /// <summary>The Back and Forward buttons' history menu. Mounted in the flyout the buttons open. Props: <see cref="HistoryMenuProps"/>.</summary>
    public static readonly ViewKey<HistoryMenuProps> ShellHistoryMenu = new("Shell.HistoryMenu");

    /// <summary>The cue shown while a file is dragged over the window. Mounted by the frame. Props: <see cref="FileDropProps"/>.</summary>
    public static readonly ViewKey<FileDropProps> ShellFileDrop = new("Shell.FileDrop");

    /// <summary>The layer that draws the chip following the pointer during a drag. Mounted by the frame. Props: <see cref="DragChipProps"/>.</summary>
    public static readonly ViewKey<DragChipProps> ShellDragChip = new("Shell.DragChip");

    /// <summary>The setup wizard's dim over the shell. Mounted by the frame. Props: <see cref="CoverScrimProps"/>.</summary>
    public static readonly ViewKey<CoverScrimProps> ShellCoverScrim = new("Shell.CoverScrim");

    /// <summary>The frame-rate overlay (developer mode). Mounted by the frame. Props: <see cref="FpsProps"/>.</summary>
    public static readonly ViewKey<FpsProps> ShellFps = new("Shell.Fps");
}
