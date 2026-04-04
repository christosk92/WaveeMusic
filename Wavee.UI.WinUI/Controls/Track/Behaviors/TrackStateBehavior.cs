using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Controls.Track.Behaviors;

/// <summary>
/// Attached behavior that manages track playback state globally.
/// Single source of truth for IsPlaying/IsPaused/IsHovered across ALL track displays
/// (TrackCell, TrackListView rows, etc).
///
/// Subscribes to <see cref="IPlaybackStateService"/> and maintains a WeakReference registry
/// of all elements with TrackId set. When playback state changes, updates all tracked elements.
/// </summary>
public static class TrackStateBehavior
{
    // VSM state names (for Control elements)
    private const string NormalState = "Normal";
    private const string HoveredState = "Hovered";
    private const string NotPlayingState = "NotPlaying";
    private const string PlayingState = "Playing";
    private const string PausedState = "Paused";

    // Global element registry — tracks all elements with TrackId set
    private static readonly List<WeakReference<FrameworkElement>> _trackedElements = [];
    private static IPlaybackStateService? _playbackStateService;
    private static bool _subscribedToPlayback;
    private static string? _currentTrackId;
    private static bool _isPlaying;
    private static bool _isBuffering;
    private static string? _bufferingTrackId;

    /// <summary>Current playing track ID (bare ID, not URI). Read by TrackListView etc.</summary>
    public static string? CurrentTrackId => _currentTrackId;

    /// <summary>Whether the current track is actively playing (vs paused).</summary>
    public static bool IsCurrentlyPlaying => _isPlaying;

    /// <summary>Whether a track is currently loading/buffering.</summary>
    public static bool IsCurrentlyBuffering => _isBuffering;

    /// <summary>Track ID currently being loaded (for per-row indicators).</summary>
    public static string? BufferingTrackId => _bufferingTrackId;

    /// <summary>Fired when playback state changes globally. Used by controls that can't use attached properties.</summary>
    public static event Action? PlaybackStateChanged;

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
            element.Unloaded -= OnUnloaded;
            RemoveFromRegistry(element);
        }

        if (e.NewValue != null)
        {
            element.PointerEntered += OnPointerEntered;
            element.PointerExited += OnPointerExited;
            element.Loaded += OnLoaded;
            element.Unloaded += OnUnloaded;
            AddToRegistry(element);
            EnsurePlaybackSubscription();

            if (element.IsLoaded)
            {
                ApplyPlaybackState(element, (string)e.NewValue);
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

    public static void SetIsHovered(DependencyObject obj, bool value) =>
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

    public static void SetIsPlaying(DependencyObject obj, bool value) =>
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

    public static void SetIsPaused(DependencyObject obj, bool value) =>
        obj.SetValue(IsPausedProperty, value);

    #endregion

    #region Event Handlers

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        var trackId = GetTrackId(element);
        if (trackId != null)
            ApplyPlaybackState(element, trackId);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
            RemoveFromRegistry(element);
    }

    private static void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        if (!GetIsEnabled(element)) return;

        SetIsHovered(element, true);

        if (element is Control control)
            VisualStateManager.GoToState(control, HoveredState, useTransitions: true);
    }

    private static void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        if (!GetIsEnabled(element)) return;

        SetIsHovered(element, false);

        if (element is Control control)
            VisualStateManager.GoToState(control, NormalState, useTransitions: true);
    }

    #endregion

    #region Playback State — Global Subscription

    /// <summary>
    /// Ensures the global playback state subscription is active.
    /// Call this from controls that read CurrentTrackId/IsCurrentlyPlaying
    /// but don't attach TrackId (e.g. TrackListView).
    /// </summary>
    public static void EnsurePlaybackSubscription()
    {
        if (_subscribedToPlayback) return;

        var service = _playbackStateService ??= Ioc.Default.GetService<IPlaybackStateService>();
        if (service == null) return;

        _currentTrackId = service.CurrentTrackId;
        _isPlaying = service.IsPlaying;

        service.PropertyChanged += OnPlaybackServiceChanged;
        _subscribedToPlayback = true;
    }

    private static void OnPlaybackServiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not IPlaybackStateService service) return;

        if (e.PropertyName is nameof(IPlaybackStateService.CurrentTrackId) or nameof(IPlaybackStateService.IsPlaying)
            or nameof(IPlaybackStateService.IsBuffering) or nameof(IPlaybackStateService.BufferingTrackId))
        {
            _currentTrackId = service.CurrentTrackId;
            _isPlaying = service.IsPlaying;
            _isBuffering = service.IsBuffering;
            _bufferingTrackId = service.BufferingTrackId;
            UpdateAllTrackedElements();
            PlaybackStateChanged?.Invoke();
        }
    }

    /// <summary>
    /// Updates playback state on all registered elements.
    /// Called when CurrentTrackId or IsPlaying changes globally.
    /// </summary>
    private static void UpdateAllTrackedElements()
    {
        // Clean up dead references and update live ones
        for (int i = _trackedElements.Count - 1; i >= 0; i--)
        {
            if (!_trackedElements[i].TryGetTarget(out var element))
            {
                _trackedElements.RemoveAt(i);
                continue;
            }

            var trackId = GetTrackId(element);
            if (trackId != null)
                ApplyPlaybackState(element, trackId);
        }
    }

    /// <summary>
    /// Applies the current playback state to a single element.
    /// </summary>
    private static void ApplyPlaybackState(FrameworkElement element, string trackId)
    {
        var isThisTrack = trackId == _currentTrackId;
        var isPlaying = isThisTrack && _isPlaying;
        var isPaused = isThisTrack && !_isPlaying;

        SetIsPlaying(element, isPlaying);
        SetIsPaused(element, isPaused);

        // Drive VisualStateManager for Control elements
        if (element is Control control)
        {
            var playbackState = isPlaying ? PlayingState : isPaused ? PausedState : NotPlayingState;
            VisualStateManager.GoToState(control, playbackState, useTransitions: true);
        }
    }

    /// <summary>
    /// Manually trigger a refresh for a specific element.
    /// Useful after recycling in virtualized lists.
    /// </summary>
    public static void RefreshElement(FrameworkElement element)
    {
        var trackId = GetTrackId(element);
        if (trackId != null)
            ApplyPlaybackState(element, trackId);
    }

    #endregion

    #region Element Registry

    private static void AddToRegistry(FrameworkElement element)
    {
        // Avoid duplicates
        for (int i = _trackedElements.Count - 1; i >= 0; i--)
        {
            if (!_trackedElements[i].TryGetTarget(out var existing))
            {
                _trackedElements.RemoveAt(i);
                continue;
            }
            if (ReferenceEquals(existing, element)) return;
        }
        _trackedElements.Add(new WeakReference<FrameworkElement>(element));
    }

    private static void RemoveFromRegistry(FrameworkElement element)
    {
        for (int i = _trackedElements.Count - 1; i >= 0; i--)
        {
            if (!_trackedElements[i].TryGetTarget(out var existing))
            {
                _trackedElements.RemoveAt(i);
                continue;
            }
            if (ReferenceEquals(existing, element))
            {
                _trackedElements.RemoveAt(i);
                return;
            }
        }
    }

    #endregion
}
