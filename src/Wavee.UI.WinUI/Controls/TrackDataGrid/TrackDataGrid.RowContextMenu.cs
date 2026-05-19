using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Controls.ContextMenu;
using Wavee.UI.WinUI.Controls.ContextMenu.Builders;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Styles;
using Windows.Foundation;

namespace Wavee.UI.WinUI.Controls.TrackDataGrid;

/// <summary>
/// Right-click row menu partial for <see cref="TrackDataGrid"/>.
///
/// Two distinct surfaces converge here:
/// <list type="number">
///   <item><description>
///     <b>Single-row right-click</b> is owned by <see cref="Track.TrackItem"/> — it builds
///     its own <see cref="TrackContextMenuBuilder"/> menu in <c>OnRightTapped</c> and marks
///     the event handled. The grid does NOT preempt that case (single-row context menus
///     are the common path and TrackItem already wires Play / Save / GoToArtist / etc.).
///   </description></item>
///   <item><description>
///     <b>Multi-selection right-click</b> arrives here: when the right-tapped row is part
///     of a multi-row selection, the bubble-phase handler installed on
///     <see cref="RowsItemsView"/> (with <c>handledEventsToo=true</c>) dismisses
///     TrackItem's just-shown flyout and opens a selection-aware menu built via
///     <see cref="BuildSelectionMenuItems"/>. The items reuse the same
///     <see cref="ContextMenuItemModel"/> shape and <see cref="ContextMenuHost"/>
///     presenter as the single-row case, so the visual identity stays consistent.
///   </description></item>
/// </list>
///
/// Multi-selection commands are exposed as DPs so consumers (PlaylistPage, etc.) can
/// inject page-specific Remove / StartRadio handlers; default invocations fall back to
/// <see cref="IPlaybackStateService"/> + <see cref="ITrackLikeService"/> direct calls
/// — same fallback pattern <see cref="TrackContextMenuBuilder"/> uses for single tracks.
/// </summary>
public sealed partial class TrackDataGrid
{
    // ──────────────────────────────────────────────────────────────────────
    // Multi-selection command DPs
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Invoked with the <c>IReadOnlyList&lt;ITrackItem&gt;</c> selection when the user
    /// picks "Play next" from the multi-selection context menu. When unset, the grid
    /// falls back to <see cref="IPlaybackStateService.PlayNext(string)"/> per-track.
    /// </summary>
    public static readonly DependencyProperty MultiSelectPlayNextCommandProperty =
        DependencyProperty.Register(nameof(MultiSelectPlayNextCommand), typeof(ICommand), typeof(TrackDataGrid),
            new PropertyMetadata(null));

    public ICommand? MultiSelectPlayNextCommand
    {
        get => (ICommand?)GetValue(MultiSelectPlayNextCommandProperty);
        set => SetValue(MultiSelectPlayNextCommandProperty, value);
    }

    /// <summary>
    /// Invoked with the <c>IReadOnlyList&lt;ITrackItem&gt;</c> selection for "Add to
    /// queue". Falls back to <see cref="IPlaybackStateService.AddToQueue(IEnumerable{string})"/>.
    /// </summary>
    public static readonly DependencyProperty MultiSelectAddToQueueCommandProperty =
        DependencyProperty.Register(nameof(MultiSelectAddToQueueCommand), typeof(ICommand), typeof(TrackDataGrid),
            new PropertyMetadata(null));

    public ICommand? MultiSelectAddToQueueCommand
    {
        get => (ICommand?)GetValue(MultiSelectAddToQueueCommandProperty);
        set => SetValue(MultiSelectAddToQueueCommandProperty, value);
    }

    /// <summary>
    /// Invoked with the selection for "Remove from playlist / Remove from Liked Songs"
    /// (destructive). Must be page-supplied — no sensible default.
    /// </summary>
    public static readonly DependencyProperty MultiSelectRemoveCommandProperty =
        DependencyProperty.Register(nameof(MultiSelectRemoveCommand), typeof(ICommand), typeof(TrackDataGrid),
            new PropertyMetadata(null));

    public ICommand? MultiSelectRemoveCommand
    {
        get => (ICommand?)GetValue(MultiSelectRemoveCommandProperty);
        set => SetValue(MultiSelectRemoveCommandProperty, value);
    }

    /// <summary>
    /// Optional override of the Remove menu label (e.g. "Remove from Liked Songs").
    /// Mirrors the single-track <c>RemoveCommandLabel</c> pattern on TrackItem.
    /// </summary>
    public static readonly DependencyProperty MultiSelectRemoveLabelProperty =
        DependencyProperty.Register(nameof(MultiSelectRemoveLabel), typeof(string), typeof(TrackDataGrid),
            new PropertyMetadata(null));

    public string? MultiSelectRemoveLabel
    {
        get => (string?)GetValue(MultiSelectRemoveLabelProperty);
        set => SetValue(MultiSelectRemoveLabelProperty, value);
    }

    /// <summary>
    /// Invoked with the selection for "Toggle save" (Like / Unlike all selected tracks).
    /// Falls back to <see cref="ITrackLikeService.ToggleSave"/> per-track.
    /// </summary>
    public static readonly DependencyProperty MultiSelectToggleLikeCommandProperty =
        DependencyProperty.Register(nameof(MultiSelectToggleLikeCommand), typeof(ICommand), typeof(TrackDataGrid),
            new PropertyMetadata(null));

    public ICommand? MultiSelectToggleLikeCommand
    {
        get => (ICommand?)GetValue(MultiSelectToggleLikeCommandProperty);
        set => SetValue(MultiSelectToggleLikeCommandProperty, value);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Right-tap wiring
    // ──────────────────────────────────────────────────────────────────────

    private RightTappedEventHandler? _rowsItemsViewRightTappedHandler;
    private bool _rightTappedHandlersWired;

    /// <summary>
    /// Install the bubble-phase right-tap handler on the rows presenter. Called from
    /// the constructor (after <c>InitializeComponent</c>) so the handler is live
    /// before any row is realized. Idempotent.
    /// </summary>
    private void WireRowContextMenuHandlers()
    {
        if (_rightTappedHandlersWired) return;
        _rightTappedHandlersWired = true;

        _rowsItemsViewRightTappedHandler ??= OnRowsPresenterRightTapped;

        // handledEventsToo=true is required because TrackItem marks the bubble-phase
        // RightTapped Handled=true after showing its single-row flyout. We want to
        // observe it anyway so we can override with the multi-selection menu when
        // appropriate.
        RowsItemsView.AddHandler(UIElement.RightTappedEvent, _rowsItemsViewRightTappedHandler, true);
    }

    private void UnwireRowContextMenuHandlers()
    {
        if (!_rightTappedHandlersWired) return;
        _rightTappedHandlersWired = false;

        if (_rowsItemsViewRightTappedHandler is not null)
            RowsItemsView.RemoveHandler(UIElement.RightTappedEvent, _rowsItemsViewRightTappedHandler);

        _rowsItemsViewRightTappedHandler = null;
    }

    private void OnRowsPresenterRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // The right-tap reaches us AFTER TrackItem's per-row handler ran (events bubble
        // inside-out). TrackItem already showed its single-track flyout if the click
        // landed on a row. We only override the menu when the right-tapped row is part
        // of a multi-row selection — in that case the single-track flyout is the wrong
        // affordance, the user wants to act on the whole selection.
        var clickedRow = ResolveClickedRow(e.OriginalSource as DependencyObject);
        var selection = CaptureSelectionForContextMenu();

        // Not a row, or single-row click (let TrackItem's per-row menu stand).
        // Also nothing to do if the right-tapped row is not part of the current
        // multi-selection — TrackItem's menu (for the right-tapped track only)
        // is already showing, which is the desired behavior.
        if (clickedRow is null || selection.Count <= 1)
            return;
        if (!selection.Contains(clickedRow))
            return;

        DismissOpenFlyoutsAtCurrentRoot();
        ShowSelectionContextMenu(sender as FrameworkElement ?? this, selection, e.GetPosition(this));
        e.Handled = true;
    }

    /// <summary>
    /// Walk from the right-tap source up the visual tree to find the bound
    /// <see cref="ITrackItem"/>. Tries the row's <see cref="ItemContainer"/>
    /// first and falls back to the nearest <see cref="Track.TrackItem"/> in
    /// case the click landed on a deeper child.
    /// </summary>
    private static ITrackItem? ResolveClickedRow(DependencyObject? element)
    {
        while (element is not null)
        {
            switch (element)
            {
                case ItemContainer ic when ic.DataContext is ITrackItem fromContainer:
                    return fromContainer;
                case Track.TrackItem ti when ti.Track is { } fromRow:
                    return fromRow;
            }
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private IReadOnlyList<ITrackItem> CaptureSelectionForContextMenu()
    {
        return RowsItemsView.SelectedItems
            .OfType<ITrackItem>()
            .ToArray();
    }

    /// <summary>
    /// Close any flyouts hanging off the current XamlRoot. Used to dismiss the
    /// per-row flyout TrackItem opened on the bubble phase before we display the
    /// multi-selection flyout in its place. WinUI 3 doesn't expose a "close all
    /// flyouts" API — enumerate open popups whose child is a
    /// <see cref="FlyoutPresenter"/> and close them via the presenter's parent flyout.
    /// </summary>
    private void DismissOpenFlyoutsAtCurrentRoot()
    {
        if (XamlRoot is null) return;
        var popups = VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot);
        foreach (var popup in popups)
        {
            if (popup.Child is FlyoutPresenter presenter
                && FlyoutBase.GetAttachedFlyout(presenter) is { } flyout)
            {
                flyout.Hide();
                continue;
            }

            // ContextMenuHost.Show uses a plain Flyout whose Child is a custom
            // panel rather than a FlyoutPresenter. Fall back to closing the popup
            // directly when we can't reach a FlyoutBase.
            popup.IsOpen = false;
        }
    }

    private void ShowSelectionContextMenu(FrameworkElement anchor, IReadOnlyList<ITrackItem> selection, Point position)
    {
        var items = BuildSelectionMenuItems(selection);
        if (items.Count == 0) return;
        ContextMenuHost.Show(anchor, items, position);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Selection menu builder
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Build the multi-selection context-menu item list. Mirrors
    /// <see cref="TrackContextMenuBuilder"/>'s primary-row shape (Play next · Play
    /// after · Save · Radio · Remove) but the commands operate on the whole
    /// <paramref name="selection"/>. Uses the same <see cref="FluentGlyphs"/>
    /// constants so the visual identity matches the single-track menu.
    /// </summary>
    private IReadOnlyList<ContextMenuItemModel> BuildSelectionMenuItems(IReadOnlyList<ITrackItem> selection)
    {
        var items = new List<ContextMenuItemModel>();
        var count = selection.Count;
        var anyLiked = selection.Any(t => t.IsLiked);
        var allLiked = selection.All(t => t.IsLiked);

        // ── Primary row (icon-only buttons): PlayNext · AddToQueue · Save
        // Selection-count suffix appended manually rather than via a *Format
        // resource key — keeps the menu working today without requiring new
        // localizable strings before the supporting .resw entries land.
        items.Add(new ContextMenuItemModel
        {
            Text = $"{AppLocalization.GetString("TrackMenu_PlayNext")} ({count})",
            Glyph = FluentGlyphs.PlayNext,
            AccentIconStyleKey = "App.AccentIcons.Media.PlayNext",
            Command = MultiSelectPlayNextCommand,
            CommandParameter = selection,
            Invoke = MultiSelectPlayNextCommand is null
                ? () => DefaultPlayNextSelection(selection)
                : null,
            IsPrimary = true,
        });

        items.Add(new ContextMenuItemModel
        {
            Text = $"{AppLocalization.GetString("TrackMenu_AddToQueue")} ({count})",
            Glyph = FluentGlyphs.AddToQueue,
            AccentIconStyleKey = "App.AccentIcons.Media.PlayAfter",
            Command = MultiSelectAddToQueueCommand,
            CommandParameter = selection,
            Invoke = MultiSelectAddToQueueCommand is null
                ? () => DefaultAddToQueueSelection(selection)
                : null,
            IsPrimary = true,
        });

        items.Add(new ContextMenuItemModel
        {
            // When the selection is mixed, the toggle reads as "Save" — only an
            // all-liked selection becomes "Saved" (mirroring TrackItem's per-row
            // copy).
            Text = AppLocalization.GetString(allLiked ? "TrackMenu_SavedShort" : "TrackMenu_SaveShort"),
            Glyph = allLiked ? FluentGlyphs.HeartFilled : FluentGlyphs.HeartOutline,
            AccentIconStyleKey = allLiked
                ? "App.AccentIcons.Media.Saved"
                : "App.AccentIcons.Media.Save",
            Command = MultiSelectToggleLikeCommand,
            CommandParameter = selection,
            Invoke = MultiSelectToggleLikeCommand is null
                ? () => DefaultToggleLikeSelection(selection, anyLiked)
                : null,
            IsPrimary = true,
        });

        // ── Separator before destructive Remove
        if (MultiSelectRemoveCommand is not null)
        {
            items.Add(ContextMenuItemModel.Separator);
            items.Add(new ContextMenuItemModel
            {
                Text = MultiSelectRemoveLabel
                    ?? $"{AppLocalization.GetString("TrackMenu_Remove")} ({count})",
                Glyph = FluentGlyphs.Remove,
                Command = MultiSelectRemoveCommand,
                CommandParameter = selection,
                IsDestructive = true,
            });
        }

        return items;
    }

    // ──────────────────────────────────────────────────────────────────────
    // Default selection-action fallbacks (no consumer command supplied)
    // ──────────────────────────────────────────────────────────────────────

    private static void DefaultPlayNextSelection(IReadOnlyList<ITrackItem> selection)
    {
        var playback = Ioc.Default.GetService<IPlaybackStateService>();
        if (playback is null) return;

        // PlayNext inserts at head of user queue. To preserve the visual order
        // ("first selected plays next, second after, …"), walk the selection in
        // REVERSE so the head-of-queue insertions end up in the expected order.
        for (var i = selection.Count - 1; i >= 0; i--)
        {
            var uri = selection[i].Uri;
            if (string.IsNullOrEmpty(uri)) continue;
            playback.PlayNext(uri);
        }
    }

    private static void DefaultAddToQueueSelection(IReadOnlyList<ITrackItem> selection)
    {
        var playback = Ioc.Default.GetService<IPlaybackStateService>();
        if (playback is null) return;

        var uris = selection
            .Select(t => t.Uri)
            .Where(u => !string.IsNullOrEmpty(u))
            .ToArray();
        if (uris.Length == 0) return;

        playback.AddToQueue(uris);
    }

    private static void DefaultToggleLikeSelection(IReadOnlyList<ITrackItem> selection, bool anyLiked)
    {
        var likeService = Ioc.Default.GetService<ITrackLikeService>();
        if (likeService is null) return;

        // Mixed → save the gaps (single pass on save). All-liked → unlike everything.
        // Matches the same toggle semantics used elsewhere when a mixed selection
        // hits the Save toggle: prefer the "promote everything to Saved" path so a
        // single click doesn't silently de-save already-liked rows.
        var targetSavedState = !anyLiked || !selection.All(t => t.IsLiked);
        // targetSavedState == true → save all; false → unlike all.

        foreach (var track in selection)
        {
            if (string.IsNullOrEmpty(track.Uri)) continue;
            var currentlySaved = likeService.IsSaved(SavedItemType.Track, track.Uri);
            // Call with the OPPOSITE of current (ToggleSave's contract is "I know
            // it's currentlySaved, please flip it"). Skip rows that already match
            // the target.
            if (currentlySaved == targetSavedState) continue;
            likeService.ToggleSave(SavedItemType.Track, track.Uri, currentlySaved);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // Public API — let consumers programmatically open the selection menu
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Open the multi-selection context menu at the given screen-space-relative
    /// <paramref name="position"/> (in this control's coordinate space). No-op when
    /// fewer than two rows are selected — the per-row TrackItem flyout already
    /// covers the single-row case. Exposed so consumers can wire keyboard-shortcut
    /// entry points (e.g. Menu key while a multi-selection is active).
    /// </summary>
    public void ShowSelectionContextMenu(Point position)
    {
        var selection = CaptureSelectionForContextMenu();
        if (selection.Count <= 1)
        {
            Debug.WriteLine($"[trackgrid-ctx] ShowSelectionContextMenu skipped: selection.Count={selection.Count}");
            return;
        }
        ShowSelectionContextMenu(this, selection, position);
    }
}
