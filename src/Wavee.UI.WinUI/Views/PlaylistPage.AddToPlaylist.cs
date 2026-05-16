using System;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Wavee.UI.Services.AddToPlaylist;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Views.Home;

namespace Wavee.UI.WinUI.Views;

/// <summary>
/// Partial-class extension for <see cref="PlaylistPage"/> that wires the
/// empty-state entry points into the app-wide
/// <see cref="IAddToPlaylistSession"/>. The "Find songs to add" CTA starts a
/// session and navigates to Search; any genre chip in the empty-state grid
/// starts the session too (per the chosen "Both" entry-trigger model) and
/// lets its own navigation continue normally.
/// </summary>
public sealed partial class PlaylistPage
{
    private void EmptyStateFindSongsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginAddSession()) return;
        // Universal "find songs" entry — Search is the cleanest landing
        // because the user can type, filter, or pivot to artist/album from
        // there with the same + affordance available on every track row.
        NavigationHelpers.OpenSearch();
    }

    private void FindMoreSongsChip_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginAddSession()) return;
        NavigationHelpers.OpenSearch();
    }

    private void EmptyStateBrowseGrid_Tapped(object sender, TappedRoutedEventArgs e)
    {
        // BrowseChip.OnTapped runs first (it's on the leaf), navigates, then
        // the tap bubbles up to this handler — so by the time we begin the
        // session the user is already on the genre page. Both events are
        // dispatched on the UI thread before any layout pass, so the
        // floating bar appears in the same frame the new page renders.
        if (!IsTapOnBrowseChip(e.OriginalSource as DependencyObject)) return;
        TryBeginAddSession();
    }

    private bool TryBeginAddSession()
    {
        var session = Ioc.Default.GetService<IAddToPlaylistSession>();
        var vm = ViewModel;
        if (session is null || vm is null) return false;
        if (string.IsNullOrEmpty(vm.PlaylistId)) return false;
        // Owners only — Spotify rejects edits to playlists the user doesn't
        // own. Without this guard a viewer could enter add-mode against
        // someone else's empty playlist and only discover the failure on
        // Submit. The empty-state CTA is also Visibility-gated on the same
        // flag in XAML; this guard covers the genre-chip intercept path.
        if (!vm.CanEditItems) return false;

        session.Begin(
            playlistId: vm.PlaylistId!,
            playlistName: vm.PlaylistName ?? string.Empty,
            playlistImageUrl: vm.PlaylistImageUrl);
        return true;
    }

    /// <summary>Walk the visual-tree parents of the tapped element looking
    /// for a <see cref="BrowseChip"/>. Returns true when one is found —
    /// other taps on the empty-state surface (gaps between chips, the icon
    /// header) are ignored so we don't enter add-mode on accidental clicks.</summary>
    private static bool IsTapOnBrowseChip(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is BrowseChip) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }
}
