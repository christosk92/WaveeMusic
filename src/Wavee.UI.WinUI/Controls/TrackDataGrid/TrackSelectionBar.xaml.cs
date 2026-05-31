using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Wavee.UI.Contracts;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Styles;
using Wavee.UI.WinUI.Controls.ContextMenu;
using Wavee.UI.WinUI.Controls.ContextMenu.Builders;
using Windows.System;

namespace Wavee.UI.WinUI.Controls.TrackDataGrid;

/// <summary>
/// Floating command bar for the multi-select experience. Hosted once per page
/// as a viewport-pinned sibling of the page scroll view and bound to that
/// page's <see cref="TrackDataGrid"/> via <see cref="Attach"/>.
///
/// The bar is purely a view over the grid's selection: it observes
/// <see cref="TrackDataGrid.SelectionModeStateChanged"/>, shows itself only while
/// the grid is in explicit selection mode, and routes every action
/// through the grid's shared <c>Invoke*Selection</c> helpers. It owns no
/// selection state of its own.
/// </summary>
public sealed partial class TrackSelectionBar : UserControl
{
    private TrackDataGrid? _grid;

    public TrackSelectionBar()
    {
        InitializeComponent();

        // Labelled icon+text buttons. Add to queue uses the list glyph and Add
        // to playlist the "+" glyph — matching the album / playlist pages and
        // the track context menu.
        SetButton(PlayButton, FluentGlyphs.Play, "Play");
        SetButton(PlayNextButton, FluentGlyphs.PlayNext, "Play next");
        SetButton(AddToQueueButton, FluentGlyphs.Queue, "Add to queue");
        SetButton(AddToPlaylistButton, FluentGlyphs.Add, "Add to playlist");
        SetButton(SaveButton, FluentGlyphs.HeartOutline, "Save");
        SetButton(RemoveButton, FluentGlyphs.Remove, "Remove");
        SetButton(SelectAllButton, FluentGlyphs.SelectAll, "Select all");
        SetButton(CloseButton, FluentGlyphs.Cancel, "Done");

        KeyDown += OnBarKeyDown;
    }

    private static void SetButton(Button button, string glyph, string label)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
        content.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        });
        button.Content = content;
    }

    /// <summary>Bind the bar to a grid. Idempotent; re-attaching swaps the target.</summary>
    public void Attach(TrackDataGrid grid)
    {
        if (ReferenceEquals(_grid, grid)) return;
        Detach();
        _grid = grid;
        _grid.SelectionModeStateChanged += OnGridSelectionStateChanged;
        UpdateFromGrid();
    }

    /// <summary>Unbind from the current grid. Call on page Dispose.</summary>
    public void Detach()
    {
        if (_grid is null) return;
        _grid.SelectionModeStateChanged -= OnGridSelectionStateChanged;
        _grid = null;
        Visibility = Visibility.Collapsed;
    }

    private void OnGridSelectionStateChanged(object? sender, EventArgs e) => UpdateFromGrid();

    private void UpdateFromGrid()
    {
        if (_grid is null)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        var selection = _grid.GetSelectedTracks();
        var count = selection.Count;

        // Native ItemsView selection is also used for row focus / keyboard
        // navigation. The bulk-action bar belongs only to the explicit
        // checkbox-selection mode (toolbar Select, row menu Select, Ctrl-click).
        var show = _grid.IsSelectionMode;
        Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        CountText.Text = count switch
        {
            0 => "Select tracks",
            1 => "1 selected",
            _ => $"{count} selected"
        };

        var hasSelection = count > 0;
        PlayButton.IsEnabled = hasSelection;
        PlayNextButton.IsEnabled = hasSelection;
        AddToQueueButton.IsEnabled = hasSelection;
        AddToPlaylistButton.IsEnabled = hasSelection;
        SaveButton.IsEnabled = hasSelection;
        RemoveButton.IsEnabled = hasSelection;

        // Save toggles to "Saved" only when every selected track is already liked.
        var allLiked = hasSelection && selection.All(t => t.IsLiked);
        SetButton(SaveButton,
            allLiked ? FluentGlyphs.HeartFilled : FluentGlyphs.HeartOutline,
            allLiked ? "Saved" : "Save");

        // Remove only exists where the host wired a remove command (playlist
        // owner / Liked Songs). Albums never wire it.
        RemoveButton.Visibility = _grid.CanRemoveSelection ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPlayClick(object sender, RoutedEventArgs e)
        => _grid?.InvokePlaySelection(_grid.GetSelectedTracks());

    private void OnPlayNextClick(object sender, RoutedEventArgs e)
        => _grid?.InvokePlayNextSelection(_grid.GetSelectedTracks());

    private void OnAddToQueueClick(object sender, RoutedEventArgs e)
        => _grid?.InvokeAddToQueueSelection(_grid.GetSelectedTracks());

    private void OnSaveClick(object sender, RoutedEventArgs e)
        => _grid?.InvokeToggleLikeSelection(_grid.GetSelectedTracks());

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (_grid is null) return;
        _grid.InvokeRemoveSelection(_grid.GetSelectedTracks());
        _grid.ExitSelectionMode();
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
        => _grid?.SelectAllRows();

    private void OnCloseClick(object sender, RoutedEventArgs e)
        => _grid?.ExitSelectionMode();

    private void OnBarKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && _grid is not null)
        {
            _grid.ExitSelectionMode();
            e.Handled = true;
        }
    }

    // ── Add to playlist ────────────────────────────────────────────────────

    private async void OnAddToPlaylistClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || _grid is null) return;

        // Snapshot the selected tracks' URIs now — same folder-aware menu the
        // row / card / hero menus use: nests folders, lists only owned playlists,
        // and offers "Create new playlist". Add + success toast live in the builder.
        var uris = _grid.GetSelectedTracks()
            .Select(t => t.Uri)
            .Where(u => !string.IsNullOrEmpty(u))
            .ToList();
        if (uris.Count == 0) return;

        var loader = AddToPlaylistSubmenuBuilder.Loader(
            sourceLabel: uris.Count == 1 ? "1 track" : $"{uris.Count} tracks",
            trackUrisLoader: _ => Task.FromResult<IReadOnlyList<string>>(uris));
        var items = await loader();
        ContextMenuHost.Show(fe, items);
    }
}
