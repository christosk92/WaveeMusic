using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Wavee.UI.WinUI.Controls.Track.Behaviors;

/// <summary>
/// Attached behavior that manages track state.
/// Sets IsHovered and IsPlaying attached properties that can be bound to in XAML.
/// For Control elements, also drives VisualStateManager with HoverStates and PlaybackStates.
/// </summary>
public static class TrackStateBehavior
{
    // VSM state names (for Control elements)
    private const string NormalState = "Normal";
    private const string HoveredState = "Hovered";
    private const string NotPlayingState = "NotPlaying";
    private const string PlayingState = "Playing";
    private const string PausedState = "Paused";

    #region TrackId Property

    public static readonly DependencyProperty TrackIdProperty =
        DependencyProperty.RegisterAttached(
            "TrackId",
            typeof(string),
            typeof(TrackStateBehavior),
            new PropertyMetadata(null, OnTrackIdChanged));

    public static string? GetTrackId(DependencyObject obj) =>
        (string?)obj.GetValue(TrackIdProperty);

    public static void SetTrackId(DependencyObject obj, string? value) =>
        obj.SetValue(TrackIdProperty, value);

    private static void OnTrackIdChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;

        if (e.OldValue != null)
        {
            element.PointerEntered -= OnPointerEntered;
            element.PointerExited -= OnPointerExited;
            element.Loaded -= OnLoaded;
        }

        if (e.NewValue != null)
        {
            element.PointerEntered += OnPointerEntered;
            element.PointerExited += OnPointerExited;
            element.Loaded += OnLoaded;

            if (element.IsLoaded)
            {
                SetInitialStates(element, (string)e.NewValue);
            }
        }
    }

    #endregion

    #region IsEnabled Property

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(TrackStateBehavior),
            new PropertyMetadata(true));

    public static bool GetIsEnabled(DependencyObject obj) =>
        (bool)obj.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject obj, bool value) =>
        obj.SetValue(IsEnabledProperty, value);

    #endregion

    #region IsHovered Property (read-only, set by behavior)

    public static readonly DependencyProperty IsHoveredProperty =
        DependencyProperty.RegisterAttached(
            "IsHovered",
            typeof(bool),
            typeof(TrackStateBehavior),
            new PropertyMetadata(false));

    public static bool GetIsHovered(DependencyObject obj) =>
        (bool)obj.GetValue(IsHoveredProperty);

    private static void SetIsHovered(DependencyObject obj, bool value) =>
        obj.SetValue(IsHoveredProperty, value);

    #endregion

    #region IsPlaying Property (read-only, set by behavior)

    public static readonly DependencyProperty IsPlayingProperty =
        DependencyProperty.RegisterAttached(
            "IsPlaying",
            typeof(bool),
            typeof(TrackStateBehavior),
            new PropertyMetadata(false));

    public static bool GetIsPlaying(DependencyObject obj) =>
        (bool)obj.GetValue(IsPlayingProperty);

    private static void SetIsPlaying(DependencyObject obj, bool value) =>
        obj.SetValue(IsPlayingProperty, value);

    #endregion

    #region IsPaused Property (read-only, set by behavior)

    public static readonly DependencyProperty IsPausedProperty =
        DependencyProperty.RegisterAttached(
            "IsPaused",
            typeof(bool),
            typeof(TrackStateBehavior),
            new PropertyMetadata(false));

    public static bool GetIsPaused(DependencyObject obj) =>
        (bool)obj.GetValue(IsPausedProperty);

    private static void SetIsPaused(DependencyObject obj, bool value) =>
        obj.SetValue(IsPausedProperty, value);

    #endregion

    #region Event Handlers

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;

        var trackId = GetTrackId(element);
        if (trackId != null)
        {
            SetInitialStates(element, trackId);
        }
    }

    private static void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        if (!GetIsEnabled(element)) return;

        SetIsHovered(element, true);

        // Also set VSM state for Control elements
        if (element is Control control)
        {
            VisualStateManager.GoToState(control, HoveredState, useTransitions: true);
        }
    }

    private static void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        if (!GetIsEnabled(element)) return;

        SetIsHovered(element, false);

        // Also set VSM state for Control elements
        if (element is Control control)
        {
            VisualStateManager.GoToState(control, NormalState, useTransitions: true);
        }
    }

    private static void SetInitialStates(FrameworkElement element, string trackId)
    {
        // Set initial hover state
        SetIsHovered(element, false);

        // Set initial playback state
        var (isPlaying, isPaused) = GetPlaybackStateForTrack(trackId);
        SetIsPlaying(element, isPlaying);
        SetIsPaused(element, isPaused);

        // Also set VSM states for Control elements
        if (element is Control control)
        {
            VisualStateManager.GoToState(control, NormalState, useTransitions: false);

            var playbackState = isPlaying ? PlayingState : isPaused ? PausedState : NotPlayingState;
            VisualStateManager.GoToState(control, playbackState, useTransitions: false);
        }
    }

    #endregion

    #region Playback State

    private static (bool isPlaying, bool isPaused) GetPlaybackStateForTrack(string trackId)
    {
        // TODO: Inject IPlayerContext and check:
        // - If trackId == CurrentTrackId && IsPlaying -> (true, false)
        // - If trackId == CurrentTrackId && !IsPlaying -> (false, true)
        // - Otherwise -> (false, false)

        // For now, always return not playing until IPlayerContext is implemented
        return (false, false);
    }

    /// <summary>
    /// Call this method to update playback state for all tracked elements.
    /// Will be called by IPlayerContext when the current track or playback state changes.
    /// </summary>
    public static void UpdatePlaybackState(string? currentTrackId, bool isPlaying)
    {
        // TODO: Implement a registry of tracked elements to update when playback changes
        // Actual implementation will need:
        // 1. A WeakReference collection of all elements with TrackId set
        // 2. When this is called, iterate through and update each element's state
    }

    #endregion
}
