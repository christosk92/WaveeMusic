using System;
using Microsoft.UI.Xaml;

namespace Wavee.UI.WinUI.Controls.Track;

/// <summary>
/// Partial-class extension on <see cref="TrackItem"/> implementing the
/// multi-select affordance: the per-row checkbox that lives in the index
/// gutter. It fades in on hover (click → asks the host to enter selection
/// mode) and stays visible for every row while selection mode is active.
///
/// The host (<c>TrackDataGrid</c>) owns the actual selection — this row only
/// raises <see cref="TrackItem.SelectionToggleRequested"/> /
/// <see cref="TrackItem.EnterSelectionRequested"/> and reflects
/// <see cref="TrackItem.IsSelected"/> back into the checkbox.
/// </summary>
public sealed partial class TrackItem
{
    // Guards the programmatic IsChecked write in UpdateSelectionAffordance from
    // re-entering RowSelectCheckBox_Toggled and raising a spurious request.
    private bool _suppressSelectionCheckEvent;

    /// <summary>
    /// Repaint the checkbox from the current selection / hover / mode state.
    /// Safe to call any time after the Row template realizes — no-ops in
    /// Compact mode (the checkbox lives only in the Row subtree).
    /// </summary>
    private void UpdateSelectionAffordance()
    {
        if (RowSelectCheckBox is null) return;

        // The checkbox shows only in selection mode (entered from the toolbar
        // toggle or the "Select" context-menu item) — never on plain hover.
        // Gated on SupportsSelectionMode so it's confined to grid-hosted rows.
        var show = IsRowMode
                   && SupportsSelectionMode
                   && Track is not null
                   && IsSelectionMode;

        // Mirror the host's selection state into the checkbox without letting
        // the programmatic write echo back as a toggle request.
        _suppressSelectionCheckEvent = true;
        RowSelectCheckBox.IsChecked = IsSelected;
        _suppressSelectionCheckEvent = false;

        // Keep the element Visible once it has ever shown so the
        // OpacityTransition can animate both directions; Opacity +
        // IsHitTestVisible do the real show/hide.
        RowSelectCheckBox.Visibility = Visibility.Visible;
        RowSelectCheckBox.Opacity = show ? 1d : 0d;
        RowSelectCheckBox.IsHitTestVisible = show;

        // The checkbox shares the left gutter with the decorative popularity
        // badge — hide the badge whenever the checkbox occupies the slot.
        if (RowPopularityBadge is not null)
        {
            RowPopularityBadge.Visibility = !show && ShowPopularityBadge
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void RowSelectCheckBox_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSelectionCheckEvent) return;

        if (!IsSelectionMode)
        {
            // First checkbox click on a non-mode row: enter selection mode.
            // The host enters mode and selects this row, which syncs IsChecked.
            EnterSelectionRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        SelectionToggleRequested?.Invoke(this, RowSelectCheckBox?.IsChecked == true);
    }

    private void RowSelectCheckBox_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        // Stop the tap before it reaches the ItemContainer — see WireRowHandlers.
        e.Handled = true;
    }
}
