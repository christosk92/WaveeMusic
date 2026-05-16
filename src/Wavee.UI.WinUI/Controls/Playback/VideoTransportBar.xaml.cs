using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Helpers.Playback;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Styles;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls.Playback;

/// <summary>
/// Which surface is hosting this transport bar. Drives the contextual
/// behavior of the mini-player flyout entry. The same control renders
/// differently in the cinematic page vs the floating popout window.
/// </summary>
public enum VideoTransportSurface
{
    /// <summary>Hosted inside the main app window (cinematic VideoPlayerPage
    /// or the Theatre overlay). Mini-player exits the cinematic page to let
    /// the floating mini PiP take over.</summary>
    Main,

    /// <summary>Hosted inside the floating popout window
    /// (PlayerFloatingWindow). Mini-player is hidden because the popout is
    /// already the alternate surface.</summary>
    Popout,
}

/// <summary>
/// YouTube-style compact transport for every video surface in the app. One
/// horizontal row of icon buttons + a progress bar above. Shared by the
/// cinematic <c>VideoPlayerPage</c> scrim and the popout's
/// <c>ExpandedNowPlayingLayout</c> video overlay so both surfaces look and
/// behave identically. Resolves <see cref="PlayerBarViewModel"/> and
/// <see cref="INowPlayingPresentationService"/> from Ioc so the host only
/// needs to set the <see cref="Surface"/> DP.
/// </summary>
public sealed partial class VideoTransportBar : UserControl
{
    public static readonly DependencyProperty SurfaceProperty =
        DependencyProperty.Register(
            nameof(Surface),
            typeof(VideoTransportSurface),
            typeof(VideoTransportBar),
            new PropertyMetadata(VideoTransportSurface.Main, OnSurfaceChanged));

    public VideoTransportSurface Surface
    {
        get => (VideoTransportSurface)GetValue(SurfaceProperty);
        set => SetValue(SurfaceProperty, value);
    }

    private static void OnSurfaceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((VideoTransportBar)d).ApplySurfaceMode();

    private PlayerBarViewModel? _viewModel;
    private INowPlayingPresentationService? _presentation;

    public VideoTransportBar()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel = Ioc.Default.GetService<PlayerBarViewModel>();
        _presentation = Ioc.Default.GetService<INowPlayingPresentationService>();

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            BindProgressBar();
            BindVolume();
            UpdatePlayPauseGlyph();
            UpdateTimeText();
        }
        if (_presentation is not null)
            _presentation.PropertyChanged += OnPresentationPropertyChanged;

        ApplySurfaceMode();
        SyncExpandPresentation();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        if (_presentation is not null)
            _presentation.PropertyChanged -= OnPresentationPropertyChanged;
        _viewModel = null;
        _presentation = null;
    }

    // ── Surface-aware menu labels ─────────────────────────────────────────

    private void ApplySurfaceMode()
    {
        switch (Surface)
        {
            case VideoTransportSurface.Popout:
                // Inside the floating popout: the popout IS the alternate
                // surface, so Theatre + Mini-player are nonsense (Theatre
                // collapses chrome on the main window we're not in).
                TheatreMenuItem.Visibility = Visibility.Collapsed;
                TheatreMiniSeparator.Visibility = Visibility.Collapsed;
                MiniPlayerMenuItem.Visibility = Visibility.Collapsed;
                break;

            case VideoTransportSurface.Main:
            default:
                TheatreMenuItem.Visibility = Visibility.Visible;
                TheatreMiniSeparator.Visibility = Visibility.Visible;
                MiniPlayerMenuItem.Visibility = Visibility.Visible;
                break;
        }
    }

    // ── ViewModel + presentation sync ─────────────────────────────────────

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerBarViewModel.IsPlaying):
            case nameof(PlayerBarViewModel.IsBuffering):
                UpdatePlayPauseGlyph();
                break;
            case nameof(PlayerBarViewModel.Position):
            case nameof(PlayerBarViewModel.Duration):
            case nameof(PlayerBarViewModel.PositionText):
            case nameof(PlayerBarViewModel.DurationText):
                UpdateTimeText();
                break;
            case nameof(PlayerBarViewModel.Volume):
            case nameof(PlayerBarViewModel.IsMuted):
                UpdateVolumeGlyph();
                if (_viewModel is not null && Math.Abs(VolumeSlider.Value - _viewModel.Volume) > 0.5)
                    VolumeSlider.Value = _viewModel.Volume;
                break;
        }
    }

    private void OnPresentationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(INowPlayingPresentationService.Presentation))
            DispatcherQueue?.TryEnqueue(SyncExpandPresentation);
    }

    private void SyncExpandPresentation()
    {
        var presentation = _presentation?.Presentation ?? NowPlayingPresentation.Normal;
        var inTheatre = presentation == NowPlayingPresentation.Theatre;
        var inFullscreen = presentation == NowPlayingPresentation.Fullscreen;

        TheatreMenuItem.IsChecked = inTheatre;
        FullscreenMenuItem.IsChecked = inFullscreen;
        ExpandPlayerGlyph.Glyph = (inTheatre || inFullscreen)
            ? FluentGlyphs.BackToWindow
            : FluentGlyphs.FullScreen;
    }

    // ── Progress bar wiring ───────────────────────────────────────────────

    private void BindProgressBar()
    {
        if (_viewModel is null) return;
        // CompositionProgressBar reads via direct property assignment + the
        // anchor mechanism, not XAML bindings — keeps it fast.
        ProgressBar.PositionMs = _viewModel.Position;
        ProgressBar.DurationMs = _viewModel.Duration;
        ProgressBar.IsPlaying = _viewModel.IsPlaying;
        ProgressBar.IsBuffering = _viewModel.IsBuffering;
    }

    private void ProgressBar_SeekStarted(object sender, EventArgs e)
        => _viewModel?.StartSeeking();

    private void ProgressBar_SeekCommitted(object sender, double positionMs)
        => _viewModel?.CommitSeekFromBar(positionMs);

    private void UpdateTimeText()
    {
        if (_viewModel is null) { TimeText.Text = string.Empty; return; }
        TimeText.Text = $"{_viewModel.PositionText} / {_viewModel.DurationText}";
        ProgressBar.PositionMs = _viewModel.Position;
        ProgressBar.DurationMs = _viewModel.Duration;
    }

    private void UpdatePlayPauseGlyph()
    {
        if (_viewModel is null) return;
        PlayPauseGlyph.IsPlaying = _viewModel.IsPlaying;
        PlayPauseGlyph.IsPending = _viewModel.IsBuffering;
        ProgressBar.IsPlaying = _viewModel.IsPlaying;
        ProgressBar.IsBuffering = _viewModel.IsBuffering;
    }

    // ── Volume ────────────────────────────────────────────────────────────

    private bool _volumeSliderSyncing;

    private void BindVolume()
    {
        if (_viewModel is null) return;
        _volumeSliderSyncing = true;
        VolumeSlider.Value = _viewModel.Volume;
        _volumeSliderSyncing = false;
        UpdateVolumeGlyph();
    }

    private void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_volumeSliderSyncing || _viewModel is null) return;
        _viewModel.Volume = e.NewValue;
    }

    private void VolumeButton_Click(object sender, RoutedEventArgs e)
    {
        // Plain Click does nothing — the flyout opens automatically. Keep
        // the handler so a future muted-on-click toggle is a one-line drop-in.
    }

    private void UpdateVolumeGlyph()
    {
        if (_viewModel is null) return;
        // Four-tier glyph: muted / low / medium / high. Mirrors the OS
        // volume mixer so the icon weight matches the level at a glance.
        VolumeGlyph.Glyph = (_viewModel.IsMuted || _viewModel.Volume <= 0)
            ? FluentGlyphs.Mute
            : _viewModel.Volume < 33
                ? FluentGlyphs.Volume1
                : _viewModel.Volume < 67
                    ? FluentGlyphs.Volume2
                    : FluentGlyphs.Volume3;
    }

    // ── Transport button clicks ───────────────────────────────────────────

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        => _viewModel?.PlayPauseCommand.Execute(null);

    private void PrevButton_Click(object sender, RoutedEventArgs e)
        => _viewModel?.PreviousCommand.Execute(null);

    private void NextButton_Click(object sender, RoutedEventArgs e)
        => _viewModel?.NextCommand.Execute(null);

    private void SkipBack10Button_Click(object sender, RoutedEventArgs e)
        => _viewModel?.SkipBack10Command.Execute(null);

    private void SkipForward30Button_Click(object sender, RoutedEventArgs e)
        => _viewModel?.SkipForward30Command.Execute(null);

    // ── Track menu ────────────────────────────────────────────────────────

    private void TracksFlyout_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout flyout) return;
        flyout.Items.Clear();
        if (!MediaTracksMenuBuilder.TryPopulateFromActivePlayback(flyout))
        {
            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = "No tracks available",
                IsEnabled = false,
            });
        }
    }

    // ── Expand-player flyout items ────────────────────────────────────────

    private void TheatreMenuItem_Click(object sender, RoutedEventArgs e)
        => _presentation?.ToggleTheatre();

    private void FullscreenMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (Surface == VideoTransportSurface.Popout)
        {
            // Floating popout: toggle the popout window's own AppWindow
            // between Overlapped and FullScreen — don't touch MainWindow.
            var window = (Microsoft.UI.Xaml.Window?)XamlRoot?.Content?.GetType()
                .GetProperty("Window")?.GetValue(XamlRoot.Content);
            // The simpler path is to walk the visual tree to the host Window
            // via Window.Current — but WinUI 3 removed that. The popout passes
            // its AppWindow through a static helper instead: see
            // PlayerFloatingWindow.cs for the actual popout-fullscreen toggle.
            // Keep this branch as a no-op fallback when the surface is set
            // wrong; the popout's FullScreenButton in its own chrome handles
            // it directly.
            return;
        }
        _presentation?.ToggleFullscreen();
    }

    private void MiniPlayerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (Surface == VideoTransportSurface.Popout)
            return; // The menu item is hidden in this mode.

        _presentation?.ExitToNormal();

        // Navigate the host frame back so the tab leaves VideoPlayerPage,
        // which flips ShellViewModel.IsOnVideoPage false and lets the
        // floating mini-player take over. Falls back to Home when no back
        // history exists.
        var hostFrame = FindAncestorFrame(this);
        if (hostFrame is { CanGoBack: true })
            hostFrame.GoBack();
        else
            Wavee.UI.WinUI.Helpers.Navigation.NavigationHelpers.OpenHome();
    }

    private static Frame? FindAncestorFrame(DependencyObject node)
    {
        var current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
        while (current is not null)
        {
            if (current is Frame f) return f;
            current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
