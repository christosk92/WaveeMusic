using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Wavee.UI.Contracts;

namespace Wavee.UI.WinUI.Controls.TrackDataGrid;

/// <summary>
/// Selection-mode partial for <see cref="TrackDataGrid"/>.
///
/// "Selection mode" is the keyboard-free multi-select state: every row shows a
/// persistent checkbox, a plain tap toggles selection (tap-to-play suppressed),
/// and the floating <c>TrackSelectionBar</c> is visible. The state is grid-level
/// — it does NOT change <c>RowsItemsView.SelectionMode</c> (kept Extended for the
/// control's lifetime so the existing selection / restore / drag plumbing keeps
/// working); rows are driven into the mode via <see cref="Track.TrackItem.IsSelectionMode"/>.
///
/// The action helpers (<c>Invoke*Selection</c>) are the single dispatch path for
/// both the floating bar and the multi-selection right-click menu
/// (<see cref="BuildSelectionMenuItems"/>): run the consumer-supplied
/// <c>MultiSelect*Command</c> when bound, else the <c>Default*Selection</c>
/// fallback in <c>TrackDataGrid.RowContextMenu.cs</c>.
/// </summary>
public sealed partial class TrackDataGrid
{
    private bool _isSelectionMode;

    /// <summary>Whether the grid is currently in keyboard-free multi-select mode.</summary>
    public bool IsSelectionMode => _isSelectionMode;

    /// <summary>
    /// Raised whenever selection mode toggles OR the selected-row set changes.
    /// The floating <c>TrackSelectionBar</c> listens to recompute its count and
    /// show / hide itself.
    /// </summary>
    public event EventHandler? SelectionModeStateChanged;

    /// <summary>Snapshot of the selected track rows in selection order.</summary>
    public IReadOnlyList<ITrackItem> GetSelectedTracks()
        => RowsItemsView.SelectedItems.OfType<ITrackItem>().ToArray();

    /// <summary>Enter selection mode. Idempotent.</summary>
    public void EnterSelectionMode()
    {
        if (_isSelectionMode) return;
        _isSelectionMode = true;
        SyncSelectionModeToggle();
        PushSelectionModeToRows();
        RaiseSelectionModeStateChanged();
    }

    /// <summary>Exit selection mode and clear the selection. Idempotent.</summary>
    public void ExitSelectionMode()
    {
        if (!_isSelectionMode)
        {
            // Not in mode but a stray selection lingers — clear it anyway.
            if (RowsItemsView.SelectedItems.Count > 0)
                ClearSelection();
            return;
        }
        _isSelectionMode = false;
        SyncSelectionModeToggle();
        ClearSelection();
        PushSelectionModeToRows();
        RaiseSelectionModeStateChanged();
    }

    // The toolbar toggle is the primary entry point — keep its checked state in
    // sync whenever selection mode is entered / exited by any other path
    // (context menu "Select", the bar's Done button, Esc).
    private bool _suppressSelectionToggleEvent;

    private void SyncSelectionModeToggle()
    {
        _suppressSelectionToggleEvent = true;
        SelectionModeToggle.IsChecked = _isSelectionMode;
        _suppressSelectionToggleEvent = false;
    }

    private void SelectionModeToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressSelectionToggleEvent) return;
        EnterSelectionMode();
    }

    private void SelectionModeToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_suppressSelectionToggleEvent) return;
        ExitSelectionMode();
    }

    // Ctrl+A — wired from the KeyboardAccelerator on the Root grid so it fires
    // regardless of which sub-element holds focus (the old KeyDown-on-ItemsView
    // route only worked when a row was focused, which broke on the album page).
    private void OnSelectAllAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        var selectableCount = _visibleRows.Count(r => r is ITrackItem);
        var selectedCount = RowsItemsView.SelectedItems.OfType<ITrackItem>().Count();

        // Toggle: everything already selected → Ctrl+A exits; otherwise enter
        // selection mode and select every track.
        if (selectableCount > 0 && selectedCount >= selectableCount)
        {
            ExitSelectionMode();
        }
        else
        {
            EnterSelectionMode();
            SelectAllRows();
        }
        args.Handled = true;
    }

    // Push the current mode flag onto every realized row. Rows realized later
    // pick it up in RowsItemsViewTrackItem_Loaded.
    private void PushSelectionModeToRows()
    {
        foreach (var row in _itemsViewRows.ToArray())
            row.IsSelectionMode = _isSelectionMode;
    }

    private void RaiseSelectionModeStateChanged()
        => SelectionModeStateChanged?.Invoke(this, EventArgs.Empty);

    // ── Row → grid plumbing ────────────────────────────────────────────────

    private void OnRowSelectionToggleRequested(object? sender, bool desiredSelected)
    {
        if (sender is not Track.TrackItem row || row.Track is not { } track)
            return;
        var index = _visibleRows.IndexOf(track);
        if (index < 0) return;
        if (desiredSelected)
            RowsItemsView.Select(index);
        else
            RowsItemsView.Deselect(index);
    }

    private void OnRowEnterSelectionRequested(object? sender, EventArgs e)
    {
        EnterSelectionMode();
        if (sender is Track.TrackItem row && row.Track is { } track)
        {
            var index = _visibleRows.IndexOf(track);
            if (index >= 0)
                RowsItemsView.Select(index);
        }
    }

    // ── Shared selection-action dispatch (bar + context menu) ──────────────

    /// <summary>True when a Remove capability is wired (playlist owner / Liked
    /// Songs). Albums never wire it, so the bar hides Remove there.</summary>
    internal bool CanRemoveSelection => MultiSelectRemoveCommand is not null;

    internal void InvokePlaySelection(IReadOnlyList<ITrackItem> selection)
    {
        if (selection.Count == 0) return;
        var first = selection[0];
        if (PlayCommand?.CanExecute(first) == true)
            PlayCommand.Execute(first);
    }

    internal void InvokePlayNextSelection(IReadOnlyList<ITrackItem> selection)
    {
        if (selection.Count == 0) return;
        if (MultiSelectPlayNextCommand is { } cmd && cmd.CanExecute(selection))
            cmd.Execute(selection);
        else
            DefaultPlayNextSelection(selection);
    }

    internal void InvokeAddToQueueSelection(IReadOnlyList<ITrackItem> selection)
    {
        if (selection.Count == 0) return;
        if (MultiSelectAddToQueueCommand is { } cmd && cmd.CanExecute(selection))
            cmd.Execute(selection);
        else
            DefaultAddToQueueSelection(selection);
    }

    internal void InvokeToggleLikeSelection(IReadOnlyList<ITrackItem> selection)
    {
        if (selection.Count == 0) return;
        if (MultiSelectToggleLikeCommand is { } cmd && cmd.CanExecute(selection))
            cmd.Execute(selection);
        else
            DefaultToggleLikeSelection(selection, selection.Any(t => t.IsLiked));
    }

    internal void InvokeRemoveSelection(IReadOnlyList<ITrackItem> selection)
    {
        if (selection.Count == 0) return;
        if (MultiSelectRemoveCommand is { } cmd && cmd.CanExecute(selection))
            cmd.Execute(selection);
    }
}
