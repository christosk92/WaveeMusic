// ── Themes/Classic/Structure/ClassicStructure.cs — the Classic structure (themes plan 6.1) ─────────────────────────────
//
// Registers the views T-lane PRs have moved here, one family at a time (`Register`): T4a is the frame and the content states
// (`Views\Frame\ClassicFrame.cs`, `ClassicContentState.cs`), T4b the title bar and the search (`Views\TitleBar\ClassicTitleBar.cs`,
// `Views\Search\ClassicSearch.cs`), T4c the frame's small views (`Views\Masthead`, `Material`, `Drawer`, `Palette`, `FrameBits`),
// T5a the navigation pane's rows (`Views\Nav\ClassicNavPane.cs`, `ClassicNavRows.cs`, `ClassicNavRow.cs`); every other surface is
// still core code. It
// decides the shell's policies (`Rules\ClassicPolicies.cs`: nav pane, panel and mini player, player bar, chrome row, stage)
// and offers every capability (T3). Its window tiers (T3b) are `Rules\Tiers.cs`.
//
// T1b: the options Classic will own (plan 6.1) are still TODAY's switches, bound to their legacy keys through `SettingsDoc`
// rows. `LegacyRows` lists them in their FINAL grouping so Settings › Appearance shows Classic's section once, in its
// final shape; each M-PR deletes its rows from the list and declares the option in `Options` (the generated row takes the
// same place in the same group).

using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>The structure of Wavee Classic.</summary>
public sealed class ClassicStructure : IStructureLayer
{
    /// <summary>The structure id.</summary>
    public const string LayerId = "Classic.Structure";

    /// <inheritdoc />
    public string Id => LayerId;

    /// <inheritdoc />
    public void Register(ViewRegistryBuilder views)
    {
        // T4a: the window frame and the content region's states (offline lane, empty body, not-found, page).
        views.Add(ViewKeys.ShellFrame, ClassicFrame.Template, "classic.shell.frame");
        views.Add(ViewKeys.ShellContentState, ClassicContentState.Template, "classic.shell.content-state");
        // T4b: the merged title bar row, the search (the field or the magnifier and its flyout) and the suggestions popup body.
        views.Add(ViewKeys.ShellTitleBar, ClassicTitleBar.Template, "classic.shell.title-bar");
        views.Add(ViewKeys.ShellSearch, ClassicSearch.Template, "classic.shell.search");
        views.Add(ViewKeys.ShellSearchSuggestions, ClassicSearch.SuggestionsTemplate, "classic.shell.search-suggestions");
        // T4c: the surfaces around the frame. The masthead and the page head, the material and its four cover leaves, the narrow drawer, the
        // command palette, and the small ones (history menu, file-drop cue, drag chip layer, setup wizard's cover scrim, frame-rate overlay).
        views.Add(ViewKeys.ShellMasthead, ClassicMasthead.Template, "classic.shell.masthead");
        views.Add(ViewKeys.PageHead, ClassicPageHead.Template, "classic.page.head");
        views.Add(ViewKeys.ShellMaterialLayer, ClassicMaterial.Template, "classic.shell.material");
        views.Add(ViewKeys.ShellCoverTone, ClassicMaterial.CoverTone, "classic.shell.material.page-tone");
        views.Add(ViewKeys.ShellCoverBlend, ClassicMaterial.CoverBlend, "classic.shell.material.artist-blend");
        views.Add(ViewKeys.ShellCoverVeil, ClassicMaterial.CoverVeil, "classic.shell.material.artist-veil");
        views.Add(ViewKeys.ShellCoverTint, ClassicMaterial.CoverTint, "classic.shell.material.tint-binder");
        views.Add(ViewKeys.ShellNarrowDrawer, ClassicDrawer.Template, "classic.shell.narrow-drawer");
        views.Add(ViewKeys.ShellCommandPalette, ClassicPalette.Template, "classic.shell.command-palette");
        views.Add(ViewKeys.ShellHistoryMenu, ClassicFrameBits.HistoryMenu, "classic.shell.history-menu");
        views.Add(ViewKeys.ShellFileDrop, ClassicFrameBits.FileDrop, "classic.shell.file-drop");
        views.Add(ViewKeys.ShellDragChip, ClassicFrameBits.DragChip, "classic.shell.drag-chip");
        views.Add(ViewKeys.ShellCoverScrim, ClassicFrameBits.CoverScrim, "classic.shell.cover-scrim");
        views.Add(ViewKeys.ShellFps, ClassicFrameBits.Fps, "classic.shell.fps");
        // T5a: the navigation pane (the rows: planner, diff, extents, freeze and the row slot; the rest of the pane is T5b-f's).
        views.Add(ViewKeys.ShellNavPane, ClassicNavPane.Template, "classic.shell.nav-pane");
    }

    /// <inheritdoc />
    public ThemeOptionSchema Options => ThemeOptionSchema.Empty;

    /// <inheritdoc />
    public ThemePolicies Policies(OptionValues structure, in StyleMetrics metrics) => ClassicPolicies.Bundle;

    /// <inheritdoc />
    public ShellCapabilities Capabilities(OptionValues structure) => ShellCapabilities.Unrestricted;

    /// <inheritdoc />
    public RoleSet RequiredRoles => RoleSet.None;

    /// <summary>Each entry leaves with the M-PR that migrates its switch: sidebar M7a, row style M2, row density M3, hide art M4,
    /// washes M6, page layout and rails M8, page motion M1 (deleted).</summary>
    public IReadOnlyList<LegacyOptionRow> LegacyRows { get; } =
    [
        new("sidebarDesign", Strings.Settings.ThemeOptions.Group.Sidebar),
        new("sidebarDensity", Strings.Settings.ThemeOptions.Group.Sidebar),
        new("sidebarReset", Strings.Settings.ThemeOptions.Group.Sidebar),
        new("sidebarCustomize", Strings.Settings.ThemeOptions.Group.Sidebar),
        new("sidebarResetEverything", Strings.Settings.ThemeOptions.Group.Sidebar),
        new("trackListStyle", Strings.Settings.ThemeOptions.Group.TrackLists),
        new("rowDensity", Strings.Settings.ThemeOptions.Group.TrackLists),
        new("hideTrackArtwork", Strings.Settings.ThemeOptions.Group.TrackLists),
        new("pageLayout", Strings.Settings.ThemeOptions.Group.TrackPages),
        new("railUniform", Strings.Settings.ThemeOptions.Group.TrackPages),
        new("railReset", Strings.Settings.ThemeOptions.Group.TrackPages),
        new("colorWashes", Strings.Settings.ThemeOptions.Group.Surfaces),
        new("pageMotion", Strings.Settings.ThemeOptions.Group.Surfaces),
    ];
}
