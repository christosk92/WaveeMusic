using System;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;
using Windows.Media.Playback;

namespace Wavee.UI.WinUI.Controls.MiniVideoPlayer;

/// <summary>
/// Collapsed "tab" that takes Mini's place when the user dismisses the
/// floating player but video keeps playing. Hosts a tiny live video frame
/// (the same WebView2 surface the floating Mini and the fullscreen
/// <c>VideoPlayerPage</c> use) via reparenting through
/// <see cref="IActiveVideoSurfaceService"/>.
///
/// Owner priority is the lowest of the three video consumers (Full=10,
/// Mini=5, Gripper=3) so the moment Mini's <see cref="MiniVideoPlayerViewModel.Restore"/>
/// flips <c>IsDismissedByUser</c> back to false, Mini reclaims the surface
/// and the gripper hides itself.
/// </summary>
public sealed partial class VideoGripperView : UserControl, IMediaSurfaceConsumer
{
    int IMediaSurfaceConsumer.OwnerPriority => 3;

    private readonly IActiveVideoSurfaceService _surface;
    public MiniVideoPlayerViewModel ViewModel { get; }

    private MediaPlayerElement? _element;
    private FrameworkElement? _elementSurface;

    public VideoGripperView()
    {
        _surface = Ioc.Default.GetRequiredService<IActiveVideoSurfaceService>();
        ViewModel = Ioc.Default.GetRequiredService<MiniVideoPlayerViewModel>();
        InitializeComponent();
        DataContext = ViewModel;

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        _surface.SurfaceOwnershipChanged += OnSurfaceOwnershipChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        Visibility = ViewModel.IsGripperVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsGripperVisible)
        {
            _surface.AcquireSurface(this);
            // Replay the Mini → Gripper morph if the user just dismissed
            // the floating Mini — gives the collapse a smooth shrink feel.
            Wavee.UI.WinUI.Helpers.Playback.VideoSurfaceMorph.TryStartMiniToGripper(SurfaceHost);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _surface.ReleaseSurface(this);
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _surface.SurfaceOwnershipChanged -= OnSurfaceOwnershipChanged;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MiniVideoPlayerViewModel.IsGripperVisible)) return;
        if (ViewModel.IsGripperVisible)
        {
            Visibility = Visibility.Visible;
            _surface.AcquireSurface(this);
            Wavee.UI.WinUI.Helpers.Playback.VideoSurfaceMorph.TryStartMiniToGripper(SurfaceHost);
        }
        else
        {
            _surface.ReleaseSurface(this);
            Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Same retry hook the floating <c>MiniVideoPlayer</c> uses — if a
    /// higher-priority owner releases and we should be the active surface,
    /// reclaim. Belt-and-braces; the gripper is the lowest priority so it
    /// almost always wins on its own.
    /// </summary>
    private void OnSurfaceOwnershipChanged(object? sender, EventArgs e)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => OnSurfaceOwnershipChanged(sender, e));
            return;
        }

        if (ViewModel.IsGripperVisible && !_surface.IsOwnedBy(this))
            _surface.AcquireSurface(this);
    }

    private void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        // Capture the gripper's video slot for the Mini → Gripper morph
        // (replayed in Mini.OnLoaded). No-op when Mini isn't about to
        // surface, so safe to always call.
        Wavee.UI.WinUI.Helpers.Playback.VideoSurfaceMorph.PrepareGripperToMini(SurfaceHost);
        ViewModel.Restore();
    }

    // ── IMediaSurfaceConsumer ─────────────────────────────────────────────

    public void AttachSurface(MediaPlayer player)
    {
        DetachElementSurfaceInternal();
        if (_element is null)
        {
            _element = new MediaPlayerElement
            {
                AreTransportControlsEnabled = false,
                Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsHitTestVisible = false,
            };
            SurfaceHost.MountVideoElement(_element);
        }
        _element.SetMediaPlayer(player);
    }

    public void AttachElementSurface(FrameworkElement element)
    {
        DetachMediaPlayerSurfaceInternal();
        if (_elementSurface is not null && ReferenceEquals(_elementSurface, element))
            return;
        _elementSurface = element;
        SurfaceHost.MountVideoElement(element);
    }

    public void DetachSurface()
    {
        DetachMediaPlayerSurfaceInternal();
        DetachElementSurfaceInternal();
    }

    private void DetachMediaPlayerSurfaceInternal()
    {
        if (_element is null) return;
        _element.SetMediaPlayer(null);
        SurfaceHost.UnmountVideoElement(_element);
        _element = null;
    }

    private void DetachElementSurfaceInternal()
    {
        if (_elementSurface is null) return;
        SurfaceHost.UnmountVideoElement(_elementSurface);
        _elementSurface.IsHitTestVisible = true;
        _elementSurface = null;
    }
}
