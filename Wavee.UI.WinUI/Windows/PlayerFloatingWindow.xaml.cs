using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Wavee.UI.WinUI.ViewModels;
using Wavee.UI.WinUI.Controls.SidebarPlayer;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Helpers.Application;
using Wavee.UI.WinUI.Services.Docking;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;
using WinUIEx;
using VirtualKey = Windows.System.VirtualKey;

namespace Wavee.UI.WinUI.Floating;

/// <summary>
/// Floating top-level window that hosts the expanded now-playing layout. The
/// title-bar mode button toggles real AppWindow fullscreen; compact mode stays
/// out of the popout window.
///
/// X always re-docks to the main shell (per design); the docking service
/// intercepts <c>AppWindow.Closing</c> and tears the window down through
/// <see cref="RequestClose"/>.
///
/// Normal-window geometry is persisted in <c>PlayerWindowExpandedX/Y/W/H</c>.
/// </summary>
public sealed partial class PlayerFloatingWindow : WindowEx
{
    private readonly IPanelDockingService _docking;
    private readonly IShellSessionService _shellSession;
    private readonly PlayerBarViewModel _viewModel;
    private readonly MiniVideoPlayerViewModel? _miniVideoViewModel;
    private bool _isDocking;
    private bool _disposed;
    private bool _suppressGeometryTracking;
    private bool _isFullScreen;
    private PointInt32? _normalWindowPositionBeforeFullScreen;
    private SizeInt32? _normalWindowSizeBeforeFullScreen;
    private long _expandedModeCallbackToken = -1;
    private DispatcherQueueTimer? _backdropTintTimer;
    private DateTime _backdropTintStartUtc;
    private readonly Color[] _backdropTintFrom = new Color[3];
    private readonly Color[] _backdropTintTo = new Color[3];
    private const int BackdropFadeDurationMs = 700;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopMost = new(-1);
    private static readonly IntPtr HwndNoTopMost = new(-2);

    public PlayerFloatingWindow()
    {
        InitializeComponent();

        _docking = Ioc.Default.GetRequiredService<IPanelDockingService>();
        _shellSession = Ioc.Default.GetRequiredService<IShellSessionService>();
        _viewModel = Ioc.Default.GetRequiredService<PlayerBarViewModel>();
        _miniVideoViewModel = Ioc.Default.GetService<MiniVideoPlayerViewModel>();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);

        TitleBarHelper.ApplyTransparentButtonBackground(AppWindow);
        TitleBarHelper.ApplyCaptionButtonColors(AppWindow, RootGrid.ActualTheme);
        RootGrid.ActualThemeChanged += OnRootThemeChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ApplyWindowTint(_viewModel.AlbumArtColor);

        Closed += OnClosed;
        AppWindow.Closing += OnAppWindowClosing;
        AppWindow.Changed += OnAppWindowChanged;

        RegisterKeyboardAccelerators();

        // Player windows always open in expanded focus mode.
        var layout = _shellSession.GetLayoutSnapshot();
        _shellSession.UpdateLayout(s => s.PlayerWindowExpanded = true);
        var mode = ExpandedPlayerContentMode.Lyrics;
        if (Enum.TryParse<ExpandedPlayerContentMode>(layout.PlayerWindowExpandedMode, out var parsedMode)
            && parsedMode != ExpandedPlayerContentMode.None)
        {
            mode = parsedMode;
        }
        ExpandedHost.Mode = mode;
        _shellSession.UpdateLayout(s => s.PlayerWindowExpandedMode = mode.ToString());
        AlwaysOnTopButton.IsChecked = layout.PlayerWindowAlwaysOnTop;
        ApplyAlwaysOnTop(layout.PlayerWindowAlwaysOnTop);
        UpdateAlwaysOnTopVisual(layout.PlayerWindowAlwaysOnTop);

        _expandedModeCallbackToken = ExpandedHost.RegisterPropertyChangedCallback(
            ExpandedPlayerView.ModeProperty,
            OnExpandedModeChanged);
        ExpandedHost.FitVideoWindowRequested += OnExpandedFitVideoWindowRequested;

        UpdateFullScreenButton();
        _miniVideoViewModel?.SetSuppressedByFloatingPlayer(true);
        ExpandedHost.SetVideoSurfaceEnabled(true);
    }

    private void RegisterKeyboardAccelerators()
    {
        var fullScreenAccelerator = new KeyboardAccelerator { Key = VirtualKey.F12 };
        fullScreenAccelerator.Invoked += (_, args) =>
        {
            ToggleFullScreen();
            args.Handled = true;
        };

        var exitFullScreenAccelerator = new KeyboardAccelerator { Key = VirtualKey.Escape };
        exitFullScreenAccelerator.Invoked += (_, args) =>
        {
            if (!_isFullScreen)
                return;

            ExitFullScreen();
            args.Handled = true;
        };

        RootGrid.KeyboardAccelerators.Add(fullScreenAccelerator);
        RootGrid.KeyboardAccelerators.Add(exitFullScreenAccelerator);
    }

    /// <summary>
    /// Called by <see cref="PanelDockingService"/> to actually close the window
    /// after re-docking. Suppresses the <c>Closing</c>-→-cancel re-entry.
    /// </summary>
    internal void RequestClose()
    {
        _isDocking = true;
        DisposeWindowResources();
        Close();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        DisposeWindowResources();
    }

    private void DisposeWindowResources()
    {
        if (_disposed) return;
        _disposed = true;

        if (_backdropTintTimer != null)
        {
            _backdropTintTimer.Stop();
            _backdropTintTimer.Tick -= OnBackdropTintTick;
            _backdropTintTimer = null;
        }

        ExpandedHost.SetVideoSurfaceEnabled(false);
        ExpandedHost.ReleaseHeavyResources();
        _miniVideoViewModel?.SetSuppressedByFloatingPlayer(false);

        if (_expandedModeCallbackToken >= 0)
        {
            ExpandedHost.UnregisterPropertyChangedCallback(
                ExpandedPlayerView.ModeProperty,
                _expandedModeCallbackToken);
            _expandedModeCallbackToken = -1;
        }

        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ExpandedHost.FitVideoWindowRequested -= OnExpandedFitVideoWindowRequested;
        RootGrid.ActualThemeChanged -= OnRootThemeChanged;
        AppWindow.Closing -= OnAppWindowClosing;
        AppWindow.Changed -= OnAppWindowChanged;
        Closed -= OnClosed;
    }

    private void OnRootThemeChanged(FrameworkElement sender, object args)
    {
        if (_disposed) return;
        TitleBarHelper.ApplyCaptionButtonColors(AppWindow, sender.ActualTheme);
        ApplyWindowTint(_viewModel.AlbumArtColor);
        UpdateAlwaysOnTopVisual(AlwaysOnTopButton.IsChecked == true);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;
        if (e.PropertyName == nameof(PlayerBarViewModel.AlbumArtColor))
            ApplyWindowTint(_viewModel.AlbumArtColor);
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_disposed) return;
        if (_isDocking) return;
        args.Cancel = true;
        _docking.HandleFloatingClose(DetachablePanel.Player);
    }

    private void ApplyWindowTint(string? hexColor)
    {
        if (_disposed) return;
        if (WindowBackdropTop == null) return;

        var (top, mid, bottom) = ComputeBackdropColors(hexColor);
        AnimateBackdrop(top, mid, bottom);
    }

    private (Color top, Color mid, Color bottom) ComputeBackdropColors(string? hexColor)
    {
        if (string.IsNullOrWhiteSpace(hexColor) || hexColor.TrimStart('#').Length != 6)
        {
            var baseColor = RootGrid.ActualTheme == ElementTheme.Dark
                ? Color.FromArgb(255, 18, 18, 22)
                : Color.FromArgb(255, 224, 238, 247);
            return (baseColor, baseColor, baseColor);
        }

        try
        {
            var hex = hexColor.TrimStart('#');
            var r = Convert.ToByte(hex[..2], 16);
            var g = Convert.ToByte(hex[2..4], 16);
            var b = Convert.ToByte(hex[4..6], 16);

            if (RootGrid.ActualTheme == ElementTheme.Dark)
            {
                return (
                    Color.FromArgb(255, (byte)(r * 0.26), (byte)(g * 0.26), (byte)(b * 0.26)),
                    Color.FromArgb(255, (byte)(r * 0.18), (byte)(g * 0.18), (byte)(b * 0.18)),
                    Color.FromArgb(255, 10, 10, 13));
            }

            const float blend = 0.72f;
            var tr = (byte)(r * (1 - blend) + 255 * blend);
            var tg = (byte)(g * (1 - blend) + 255 * blend);
            var tb = (byte)(b * (1 - blend) + 255 * blend);
            return (
                Color.FromArgb(255, tr, tg, tb),
                Color.FromArgb(255, (byte)(tr * 0.96), (byte)(tg * 0.97), tb),
                Color.FromArgb(255, 232, 240, 248));
        }
        catch
        {
            var baseColor = RootGrid.ActualTheme == ElementTheme.Dark
                ? Color.FromArgb(255, 18, 18, 22)
                : Color.FromArgb(255, 224, 238, 247);
            return (baseColor, baseColor, baseColor);
        }
    }

    private void AnimateBackdrop(Color top, Color mid, Color bottom)
    {
        if (_disposed) return;

        // Snapshot the live colors so an interrupted fade picks up from the
        // intermediate hue currently on screen rather than snapping back to
        // the previous endpoint.
        _backdropTintFrom[0] = WindowBackdropTop.Color;
        _backdropTintFrom[1] = WindowBackdropMid.Color;
        _backdropTintFrom[2] = WindowBackdropBottom.Color;
        _backdropTintTo[0] = top;
        _backdropTintTo[1] = mid;
        _backdropTintTo[2] = bottom;
        _backdropTintStartUtc = DateTime.UtcNow;

        _backdropTintTimer ??= DispatcherQueue.GetForCurrentThread().CreateTimer();
        _backdropTintTimer.Tick -= OnBackdropTintTick;
        _backdropTintTimer.Tick += OnBackdropTintTick;
        _backdropTintTimer.Interval = TimeSpan.FromMilliseconds(16);
        _backdropTintTimer.Stop();
        _backdropTintTimer.Start();
    }

    private void OnBackdropTintTick(DispatcherQueueTimer sender, object args)
    {
        if (_disposed)
        {
            sender.Stop();
            return;
        }

        var elapsed = (DateTime.UtcNow - _backdropTintStartUtc).TotalMilliseconds;
        var progress = Math.Clamp(elapsed / BackdropFadeDurationMs, 0.0, 1.0);
        var t = EaseInOut(progress);

        WindowBackdropTop.Color = LerpColor(_backdropTintFrom[0], _backdropTintTo[0], t);
        WindowBackdropMid.Color = LerpColor(_backdropTintFrom[1], _backdropTintTo[1], t);
        WindowBackdropBottom.Color = LerpColor(_backdropTintFrom[2], _backdropTintTo[2], t);

        if (progress >= 1.0)
        {
            sender.Stop();
            // Snap to exact targets to prevent any drift from float math.
            WindowBackdropTop.Color = _backdropTintTo[0];
            WindowBackdropMid.Color = _backdropTintTo[1];
            WindowBackdropBottom.Color = _backdropTintTo[2];
        }
    }

    private static double EaseInOut(double t)
        => t < 0.5
            ? 2 * t * t
            : 1 - Math.Pow(-2 * t + 2, 2) / 2;

    private static Color LerpColor(Color a, Color b, double t)
        => Color.FromArgb(
            (byte)(a.A + (b.A - a.A) * t),
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPresenterChange)
        {
            _isFullScreen = sender.Presenter.Kind == AppWindowPresenterKind.FullScreen;
            UpdateFullScreenButton();
        }

        if (_disposed || _isDocking || _suppressGeometryTracking) return;
        if (_isFullScreen) return;
        if (!args.DidPositionChange && !args.DidSizeChange) return;

        var pos = AppWindow.Position;
        var size = AppWindow.Size;
        _shellSession.UpdateLayout(s =>
        {
            s.PlayerWindowExpandedX = pos.X;
            s.PlayerWindowExpandedY = pos.Y;
            s.PlayerWindowExpandedWidth = size.Width;
            s.PlayerWindowExpandedHeight = size.Height;
        });
    }

    private void OnExpandedModeChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (_disposed) return;
        var modeName = ExpandedHost.Mode.ToString();
        _shellSession.UpdateLayout(s => s.PlayerWindowExpandedMode = modeName);
    }

    private void AlwaysOnTopButton_Checked(object sender, RoutedEventArgs e)
        => SetAlwaysOnTop(true);

    private void AlwaysOnTopButton_Unchecked(object sender, RoutedEventArgs e)
        => SetAlwaysOnTop(false);

    private void SetAlwaysOnTop(bool enabled)
    {
        if (_disposed) return;
        ApplyAlwaysOnTop(enabled);
        UpdateAlwaysOnTopVisual(enabled);
        ToolTipService.SetToolTip(
            AlwaysOnTopButton,
            enabled ? "Disable always on top" : "Always on top");
        _shellSession.UpdateLayout(s => s.PlayerWindowAlwaysOnTop = enabled);
    }

    private void ApplyAlwaysOnTop(bool enabled)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        if (hwnd == IntPtr.Zero)
            return;

        _ = SetWindowPos(
            hwnd,
            enabled ? HwndTopMost : HwndNoTopMost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private void UpdateAlwaysOnTopVisual(bool enabled)
    {
        var brush = enabled
            ? TryGetBrushResource("AccentTextFillColorPrimaryBrush")
              ?? TryGetBrushResource("SystemControlHighlightAccentBrush")
            : TryGetBrushResource("TextFillColorPrimaryBrush");

        if (brush == null)
            return;

        AlwaysOnTopButton.Foreground = brush;
        AlwaysOnTopGlyph.Foreground = brush;
    }

    private static Brush? TryGetBrushResource(string key)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush)
            return brush;

        return null;
    }

    private void OnExpandedFitVideoWindowRequested(object? sender, EventArgs e)
    {
        if (_disposed || _isFullScreen)
            return;

        var currentSize = AppWindow.Size;
        var preferred = ExpandedHost.GetPreferredVideoWindowSize(currentSize.Width);
        var targetWidth = Math.Max(1, (int)Math.Round(preferred.Width));
        var targetHeight = Math.Max(1, (int)Math.Round(preferred.Height));

        var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        var workArea = displayArea.WorkArea;
        targetWidth = Math.Min(targetWidth, Math.Max(1, workArea.Width - 48));
        targetHeight = Math.Min(targetHeight, Math.Max(1, workArea.Height - 48));

        var position = AppWindow.Position;
        var targetX = position.X + (currentSize.Width - targetWidth) / 2.0;
        var targetY = position.Y + (currentSize.Height - targetHeight) / 2.0;

        targetX = Math.Clamp(targetX, workArea.X, Math.Max(workArea.X, workArea.X + workArea.Width - targetWidth));
        targetY = Math.Clamp(targetY, workArea.Y, Math.Max(workArea.Y, workArea.Y + workArea.Height - targetHeight));

        ResizeAndMove(new SizeInt32(targetWidth, targetHeight), targetX, targetY);
    }

    private void FullScreenButton_Click(object sender, RoutedEventArgs e)
        => ToggleFullScreen();

    private void ToggleFullScreen()
    {
        if (_disposed)
            return;

        if (_isFullScreen)
            ExitFullScreen();
        else
            EnterFullScreen();
    }

    private void EnterFullScreen()
    {
        if (_disposed || _isFullScreen)
            return;

        _normalWindowPositionBeforeFullScreen = AppWindow.Position;
        _normalWindowSizeBeforeFullScreen = AppWindow.Size;
        PersistNormalWindowGeometry(
            _normalWindowPositionBeforeFullScreen.Value,
            _normalWindowSizeBeforeFullScreen.Value);

        _suppressGeometryTracking = true;
        _isFullScreen = true;
        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        UpdateFullScreenButton();
        ReleaseGeometrySuppressionLater();
    }

    private void ExitFullScreen()
    {
        if (_disposed || !_isFullScreen)
            return;

        var position = _normalWindowPositionBeforeFullScreen ?? AppWindow.Position;
        var size = _normalWindowSizeBeforeFullScreen ?? AppWindow.Size;

        _suppressGeometryTracking = true;
        _isFullScreen = false;
        AppWindow.SetPresenter(AppWindowPresenterKind.Default);
        ResizeAndMove(size, position.X, position.Y);
        PersistNormalWindowGeometry(position, size);
        UpdateFullScreenButton();
        if (AlwaysOnTopButton.IsChecked == true)
            ApplyAlwaysOnTop(true);
        ReleaseGeometrySuppressionLater();
    }

    private void ReleaseGeometrySuppressionLater()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_disposed)
                _suppressGeometryTracking = false;
        });
    }

    private void PersistNormalWindowGeometry(PointInt32 position, SizeInt32 size)
    {
        _shellSession.UpdateLayout(s =>
        {
            s.PlayerWindowExpandedX = position.X;
            s.PlayerWindowExpandedY = position.Y;
            s.PlayerWindowExpandedWidth = size.Width;
            s.PlayerWindowExpandedHeight = size.Height;
        });
    }

    private void UpdateFullScreenButton()
    {
        if (FullScreenGlyph is null)
            return;

        FullScreenGlyph.Glyph = _isFullScreen ? "\uE73F" : "\uE740";
        ToolTipService.SetToolTip(
            FullScreenButton,
            _isFullScreen ? "Exit full screen (Esc)" : "Enter full screen (F12)");
    }

    /// <summary>
    /// Applies a normal-window size and position.
    /// </summary>
    private void ResizeAndMove(SizeInt32 size, double x, double y)
    {
        if (size.Width <= 0 || size.Height <= 0) return;
        AppWindow.Resize(size);
        if (x != 0 || y != 0)
            AppWindow.Move(new PointInt32((int)x, (int)y));
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);
}
