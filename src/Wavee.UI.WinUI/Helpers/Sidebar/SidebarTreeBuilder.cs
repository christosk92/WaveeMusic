using System;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Controls.Sidebar;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Styles;

namespace Wavee.UI.WinUI.Helpers.Sidebar;

/// <summary>
/// Builds the static skeleton of the sidebar — Pinned / Your Library / Playlists
/// sections plus the library-shelf rows underneath Your Library. Dynamic
/// playlist / pinned rows are populated separately by <c>ShellViewModel</c>
/// once the library data is loaded; this builder only lays the scaffolding.
///
/// <para>Extracted out of <c>ShellViewModel.InitializeSidebarItems</c>
/// (audit D2: long-method smell) so the VM keeps the wiring + lifecycle and
/// the markup-style construction lives next to the other sidebar helpers.</para>
/// </summary>
internal static class SidebarTreeBuilder
{
    public static ObservableCollection<SidebarItemModel> Build(
        Func<SidebarItemModel> buildPinDropZoneRow,
        Func<FrameworkElement?> createComingSoonBadge,
        Func<FrameworkElement?> createPlaylistsAddButton)
    {
        var collection = new ObservableCollection<SidebarItemModel>
        {
            // Pinned section (collapsible, dynamic items)
            new()
            {
                Text = AppLocalization.GetString("Shell_SidebarPinned"),
                Tag = "Pinned",
                IsExpanded = true,
                IsSectionHeader = true,
                ShowEmptyPlaceholder = true,
                Children = new ObservableCollection<SidebarItemModel>
                {
                    // Always-present pin drop zone. Hidden by SidebarItem when
                    // no drag is in flight (IsDropZoneOnly); reveals + accepts
                    // playlist / album / artist / show payloads during drag,
                    // routing through ShellPage's drop handler to PinAsync.
                    buildPinDropZoneRow(),
                }
            },
            // Your Library section (collapsible, NO playlists)
            new()
            {
                Text = AppLocalization.GetString("Shell_SidebarYourLibrary"),
                Tag = "YourLibrary",
                IsExpanded = true,
                IsSectionHeader = true,
                ShowCompactSeparatorBefore = true,
                Children = BuildYourLibraryChildren(createComingSoonBadge),
            },
            // Playlists section (collapsible). IsLoadingChildren=true on cold boot
            // so the sidebar shows shimmer rows the instant it realizes, before any
            // cache or network read has completed. RefreshPlaylistsAsync flips this
            // back to false as soon as either tier yields a result.
            new()
            {
                Text = AppLocalization.GetString("Shell_SidebarPlaylists"),
                Tag = "Playlists",
                IsExpanded = true,
                IsSectionHeader = true,
                ShowCompactSeparatorBefore = true,
                IsLoadingChildren = true,
                ShowEmptyPlaceholder = true,
                EmptyPlaceholderText = AppLocalization.GetString("Shell_SidebarNoPlaylists"),
                ItemDecorator = createPlaylistsAddButton(),
                Children = new ObservableCollection<SidebarItemModel>
                {
                    // User playlists will be populated dynamically.
                }
            }
        };

        return collection;
    }

    private static ObservableCollection<SidebarItemModel> BuildYourLibraryChildren(
        Func<FrameworkElement?> createComingSoonBadge)
        => new()
        {
            new SidebarItemModel
            {
                Text = AppLocalization.GetString("Shell_SidebarAlbums"),
                IconSource = new FontIconSource { Glyph = FluentGlyphs.Album },
                Tag = "Albums",
                BadgeCount = 0
            },
            new SidebarItemModel
            {
                Text = AppLocalization.GetString("Shell_SidebarArtists"),
                IconSource = new FontIconSource { Glyph = FluentGlyphs.Artist },
                Tag = "Artists",
                BadgeCount = 0
            },
            new SidebarItemModel
            {
                Text = AppLocalization.GetString("Shell_SidebarLikedSongs"),
                IconSource = new FontIconSource { Glyph = FluentGlyphs.HeartFilled },
                Tag = "LikedSongs",
                BadgeCount = 0,
                ShowPinToggleButton = true
            },
            new SidebarItemModel
            {
                Text = AppLocalization.GetString("Shell_SidebarPodcasts"),
                IconSource = new FontIconSource { Glyph = FluentGlyphs.Radio },
                Tag = "Podcasts",
                BadgeCount = 0,
                ShowPinToggleButton = true
            },
            // Local files landing page. The typed shelves stay one click
            // deeper inside LocalLibraryPage instead of occupying four
            // separate sidebar rows. FontIconSource (not SymbolIconSource)
            // for parity with its siblings — SymbolIconSource carries a
            // different default size/margin that visibly stretches the
            // icon column and squeezes the label.
            new SidebarItemModel
            {
                Text = AppLocalization.GetString("Shell_SidebarLocalFiles"),
                IconSource = new FontIconSource { Glyph = FluentGlyphs.Folder },
                Tag = "LocalFiles",
                BadgeCount = null,
                IsEnabled = AppFeatureFlags.LocalFilesEnabled,
                ToolTip = AppFeatureFlags.LocalFilesEnabled
                    ? AppLocalization.GetString("Shell_SidebarLocalFiles")
                    : "Coming soon after the initial beta release",
                ItemDecorator = AppFeatureFlags.LocalFilesEnabled ? null : createComingSoonBadge()
            },
        };
}
