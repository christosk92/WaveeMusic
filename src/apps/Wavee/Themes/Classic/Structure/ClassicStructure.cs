// ── Themes/Classic/Structure/ClassicStructure.cs — the Classic structure (themes plan 6.1) ─────────────────────────────
//
// Registers the views T-lane PRs have moved here, one family at a time (`Register`): T4a is the frame and the content states
// (`Views\Frame\ClassicFrame.cs`, `ClassicContentState.cs`), T4b the title bar and the search (`Views\TitleBar\ClassicTitleBar.cs`,
// `Views\Search\ClassicSearch.cs`), T4c the frame's small views (`Views\Masthead`, `Material`, `Drawer`, `Palette`, `FrameBits`),
// T5a the navigation pane's rows (`Views\Nav\ClassicNavPane.cs`, `ClassicNavRows.cs`, `ClassicNavRow.cs`), T5b its heads
// (`ClassicNavHead.cs`, `ClassicNavFilter.cs`, `ClassicNavLibraryHead.cs`), T6a the player bar (`Views\Player\ClassicPlayerBar.cs`),
// T6b the right panel and the player's flyouts (`Views\Panel`, `Views\Player`), T7a1 the fullscreen stage's frame and T7a2 its layout layers (`Views\Stage`),
// T8a the track table's rows (`Lists\ClassicTrackTable.cs`: the Modern and Classic bound-row templates, registered as the
// `StyleKeys.TrackTable` style); every other surface is still core code. It
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
        // T5b: the heads. Your Library's head and the empty body; the section header, the "+" and the filter box ride the row slot.
        views.Add(ViewKeys.ShellNavLibraryHead, ClassicNavLibraryHead.Template, "classic.shell.nav-library-head");
        views.Add(ViewKeys.ShellNavEmpty, ClassicNavEmpty.Template, "classic.shell.nav-empty");
        // T5c: the compact rail's flyout panels (a collapsed section's rows, a folder tile's drill-in); the pane opens and dismisses them.
        views.Add(ViewKeys.ShellNavSectionFlyout, ClassicNavFlyouts.SectionTemplate, "classic.shell.nav-section-flyout");
        views.Add(ViewKeys.ShellNavFolderFlyout, ClassicNavFlyouts.FolderTemplate, "classic.shell.nav-folder-flyout");
        // T6a: the player bar (the podcast transport rides with it).
        views.Add(ViewKeys.PlayerBar, ClassicPlayerBar.Template, "classic.player.bar");
        // T8a: the track table's rows (the Modern and Classic bound grids and skins; the header, chrome and placeholder are T8b's).
        views.AddStyle(StyleKeys.TrackTable, ClassicTrackTable.Style);
        // T6b: the right panel (frame, header, arms), its friends body, the now-playing hero, the deck's style flyout, the device
        // picker's menu and the on-media transport.
        views.Add(ViewKeys.ShellPanel, ClassicPanel.Template, "classic.shell.panel");
        views.Add(ViewKeys.PanelFriends, ClassicFriends.Template, "classic.panel.friends");
        views.Add(ViewKeys.NowPlayingHero, ClassicNowPlayingHero.Template, "classic.player.now-playing-hero");
        views.Add(ViewKeys.DeckStyleFlyout, ClassicDeckFlyout.Template, "classic.player.deck-style-flyout");
        views.Add(ViewKeys.DevicePicker, ClassicDevicePicker.Template, "classic.player.device-picker");
        views.Add(ViewKeys.OnMediaTransport, ClassicOnMedia.Template, "classic.player.on-media-transport");
        // T7a1: the fullscreen stage's frame: the root (layer stack, chrome placement, hairline), the top bar, the transport card, the
        // options popover, the visualizer gallery and the lyric caption. The layouts, hero and artist pane (T7a2), lyrics (T7b), video
        // (T7c) and the queue pane (T7d) arrive as builders on the root until they have keys.
        views.Add(ViewKeys.StageRoot, ClassicStageRoot.Template, "classic.stage.root");
        views.Add(ViewKeys.StageTopBar, ClassicStageTopBar.Template, "classic.stage.top-bar");
        views.Add(ViewKeys.StageTransport, ClassicStageTransport.Template, "classic.stage.transport");
        views.Add(ViewKeys.StageOptions, ClassicStageOptions.Template, "classic.stage.options");
        views.Add(ViewKeys.StageGallery, ClassicStageGallery.Template, "classic.stage.gallery");
        views.Add(ViewKeys.StageCaption, ClassicStageCaption.Template, "classic.stage.caption");
        views.Add(ViewKeys.StageBackdrop, ClassicStageBackdrop.Template, "classic.stage.backdrop");
        views.Add(ViewKeys.StageHero, ClassicStageHero.Template, "classic.stage.hero");
        views.Add(ViewKeys.StageArtistName, ClassicStageArtistName.Template, "classic.stage.artist-name");
        views.Add(ViewKeys.StageFace, ClassicStageFace.Template, "classic.stage.face");
        views.Add(ViewKeys.StageFaceScrim, ClassicStageFaceScrim.Template, "classic.stage.face-scrim");
        views.Add(ViewKeys.StageSpectrum, ClassicStageSpectrum.Template, "classic.stage.spectrum");
        views.Add(ViewKeys.StageNowPlayingCard, ClassicStageNowPlayingCard.Template, "classic.stage.now-playing-card");
        views.Add(ViewKeys.StageIdentity, ClassicStageIdentity.Template, "classic.stage.identity");
        views.Add(ViewKeys.StageArtistPane, ClassicStageArtistPane.Template, "classic.stage.artist-pane");
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
