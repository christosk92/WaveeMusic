using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Controls.Track.Behaviors;

/// <summary>
/// Attached behavior for track items. Handles double-tap to play, right-click/hold for context menu.
/// </summary>
public static class TrackBehavior
{
    #region Track Property

    public static readonly DependencyProperty TrackProperty =
        DependencyProperty.RegisterAttached(
            "Track",
            typeof(ITrackItem),
            typeof(TrackBehavior),
            new PropertyMetadata(null, OnTrackChanged));

    public static ITrackItem? GetTrack(DependencyObject obj) =>
        (ITrackItem?)obj.GetValue(TrackProperty);

    public static void SetTrack(DependencyObject obj, ITrackItem? value) =>
        obj.SetValue(TrackProperty, value);

    private static void OnTrackChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;

        if (e.OldValue != null)
        {
            // Unsubscribe from events
            element.DoubleTapped -= OnDoubleTapped;
            element.RightTapped -= OnRightTapped;
            element.Holding -= OnHolding;
            element.PointerEntered -= OnPointerEntered;
            element.PointerExited -= OnPointerExited;
        }

        if (e.NewValue != null)
        {
            // Subscribe to events
            element.DoubleTapped += OnDoubleTapped;
            element.RightTapped += OnRightTapped;
            element.Holding += OnHolding;
            element.PointerEntered += OnPointerEntered;
            element.PointerExited += OnPointerExited;
        }
    }

    #endregion

    #region PlayCommand Property

    public static readonly DependencyProperty PlayCommandProperty =
        DependencyProperty.RegisterAttached(
            "PlayCommand",
            typeof(ICommand),
            typeof(TrackBehavior),
            new PropertyMetadata(null));

    public static ICommand? GetPlayCommand(DependencyObject obj) =>
        (ICommand?)obj.GetValue(PlayCommandProperty);

    public static void SetPlayCommand(DependencyObject obj, ICommand? value) =>
        obj.SetValue(PlayCommandProperty, value);

    #endregion

    #region AddToQueueCommand Property

    public static readonly DependencyProperty AddToQueueCommandProperty =
        DependencyProperty.RegisterAttached(
            "AddToQueueCommand",
            typeof(ICommand),
            typeof(TrackBehavior),
            new PropertyMetadata(null));

    public static ICommand? GetAddToQueueCommand(DependencyObject obj) =>
        (ICommand?)obj.GetValue(AddToQueueCommandProperty);

    public static void SetAddToQueueCommand(DependencyObject obj, ICommand? value) =>
        obj.SetValue(AddToQueueCommandProperty, value);

    #endregion

    #region RemoveCommand Property

    public static readonly DependencyProperty RemoveCommandProperty =
        DependencyProperty.RegisterAttached(
            "RemoveCommand",
            typeof(ICommand),
            typeof(TrackBehavior),
            new PropertyMetadata(null));

    public static ICommand? GetRemoveCommand(DependencyObject obj) =>
        (ICommand?)obj.GetValue(RemoveCommandProperty);

    public static void SetRemoveCommand(DependencyObject obj, ICommand? value) =>
        obj.SetValue(RemoveCommandProperty, value);

    #endregion

    #region ContextMenuEnabled Property

    public static readonly DependencyProperty ContextMenuEnabledProperty =
        DependencyProperty.RegisterAttached(
            "ContextMenuEnabled",
            typeof(bool),
            typeof(TrackBehavior),
            new PropertyMetadata(true));

    public static bool GetContextMenuEnabled(DependencyObject obj) =>
        (bool)obj.GetValue(ContextMenuEnabledProperty);

    public static void SetContextMenuEnabled(DependencyObject obj, bool value) =>
        obj.SetValue(ContextMenuEnabledProperty, value);

    #endregion

    #region IsPointerOver Property (read-only)

    public static readonly DependencyProperty IsPointerOverProperty =
        DependencyProperty.RegisterAttached(
            "IsPointerOver",
            typeof(bool),
            typeof(TrackBehavior),
            new PropertyMetadata(false));

    public static bool GetIsPointerOver(DependencyObject obj) =>
        (bool)obj.GetValue(IsPointerOverProperty);

    private static void SetIsPointerOver(DependencyObject obj, bool value) =>
        obj.SetValue(IsPointerOverProperty, value);

    #endregion

    #region Event Handlers

    private static void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not DependencyObject d) return;

        var track = GetTrack(d);
        var command = GetPlayCommand(d);

        if (track != null && command?.CanExecute(track) == true)
        {
            command.Execute(track);
            e.Handled = true;
        }
    }

    private static void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        if (!GetContextMenuEnabled(element)) return;

        var track = GetTrack(element);
        if (track == null) return;

        ShowContextMenu(element, track, e.GetPosition(element));
        e.Handled = true;
    }

    private static void OnHolding(object sender, HoldingRoutedEventArgs e)
    {
        if (e.HoldingState != Microsoft.UI.Input.HoldingState.Started) return;
        if (sender is not FrameworkElement element) return;
        if (!GetContextMenuEnabled(element)) return;

        var track = GetTrack(element);
        if (track == null) return;

        ShowContextMenu(element, track, e.GetPosition(element));
        e.Handled = true;
    }

    private static void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is DependencyObject d)
        {
            SetIsPointerOver(d, true);
        }
    }

    private static void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is DependencyObject d)
        {
            SetIsPointerOver(d, false);
        }
    }

    private static void ShowContextMenu(FrameworkElement element, ITrackItem track, Windows.Foundation.Point position)
    {
        var options = new TrackContextMenuOptions
        {
            PlayCommand = GetPlayCommand(element),
            AddToQueueCommand = GetAddToQueueCommand(element),
            RemoveCommand = GetRemoveCommand(element)
        };

        var menu = TrackContextMenu.Create(track, options);
        menu.ShowAt(element, position);
    }

    #endregion
}
