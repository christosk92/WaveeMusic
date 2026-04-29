using System;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.ViewModels;
using Wavee.UI.WinUI.Controls.SidebarPlayer;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Helpers.Application;
using Wavee.UI.WinUI.Services.Docking;
using Windows.Graphics;
using Windows.UI;
using WinUIEx;

namespace Wavee.UI.WinUI.Floating;

/// <summary>
/// Floating top-level window that hosts the <see cref="Controls.SidebarPlayer.SidebarPlayerWidget"/>
/// in two layouts: a vertical mini card (compact) and an Apple-Music-style 2-column
/// "now playing" layout (expanded). The expand chevron in the title bar swaps
/// between them via a <see cref="TransitionHelper"/> that morphs the album art,
/// title, and artist text while independently fading transport / progress / volume.
///
/// X always re-docks to the main shell (per design); the docking service
/// intercepts <c>AppWindow.Closing</c> and tears the window down through
/// <see cref="RequestClose"/>.
///
/// Geometry is persisted per-mode (<c>PlayerWindowX/Y/W/H</c> for compact,
/// <c>PlayerWindowExpandedX/Y/W/H</c> for expanded) so each toggle restores the
/// last-known size for that mode.
/// </summary>
public sealed partial class PlayerFloatingWindow : WindowEx
{
    private readonly IPanelDockingService _docking;
    private readonly IShellSessionService _shellSession;
    private readonly PlayerBarViewModel _viewModel;
    private TransitionHelper? _expandTransition;
    private bool _isDocking;
    private bool _suppressGeometryTracking;
    private bool _transitionInFlight;
    private DispatcherQueueTimer? _backdropTintTimer;
    private DateTime _backdropTintStartUtc;
    private readonly Color[] _backdropTintFrom = new Color[3];
    private readonly Color[] _backdropTintTo = new Color[3];
    private const int BackdropFadeDurationMs = 700;

    /// <summary>True when the window is showing the 2-column expanded layout.</summary>
    public bool IsExpanded { get; private set; }

    public PlayerFloatingWindow()
    {
        InitializeComponent();

        _docking = Ioc.Default.GetRequiredService<IPanelDockingService>();
        _shellSession = Ioc.Default.GetRequiredService<IShellSessionService>();
        _viewModel = Ioc.Default.GetRequiredService<PlayerBarViewModel>();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);

        TitleBarHelper.ApplyTransparentButtonBackground(AppWindow);
        TitleBarHelper.ApplyCaptionButtonColors(AppWindow, RootGrid.ActualTheme);
        RootGrid.ActualThemeChanged += (s, _) =>
        {
            TitleBarHelper.ApplyCaptionButtonColors(AppWindow, s.ActualTheme);
            ApplyWindowTint(_viewModel.AlbumArtColor);
        };
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ApplyWindowTint(_viewModel.AlbumArtColor);

        AppWindow.Closing += OnAppWindowClosing;
        AppWindow.Changed += OnAppWindowChanged;

        _expandTransition = (TransitionHelper)RootGrid.Resources["PlayerExpandTransition"];
        _expandTransition.Source = CompactHost;
        _expandTransition.Target = ExpandedHost;

        // Restore persisted state. PanelDockingService has already sized the
        // window using the right geometry slot — we just sync the visible host.
        var layout = _shellSession.GetLayoutSnapshot();
        if (layout.PlayerWindowExpanded)
        {
            CompactHost.Visibility = Visibility.Collapsed;
            ExpandedHost.Visibility = Visibility.Visible;
            IsExpanded = true;
        }
        if (Enum.TryParse<ExpandedPlayerContentMode>(layout.PlayerWindowExpandedMode, out var mode))
        {
            ExpandedHost.Mode = mode;
        }

        ExpandedHost.RegisterPropertyChangedCallback(
            ExpandedPlayerView.ModeProperty,
            OnExpandedModeChanged);

        UpdateModeButtons();
    }

    /// <summary>
    /// Called by <see cref="PanelDockingService"/> to actually close the window
    /// after re-docking. Suppresses the <c>Closing</c>-→-cancel re-entry.
    /// </summary>
    internal void RequestClose()
    {
        _isDocking = true;
        _backdropTintTimer?.Stop();
        AppWindow.Closing -= OnAppWindowClosing;
        AppWindow.Changed -= OnAppWindowChanged;
        Close();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerBarViewModel.AlbumArtColor))
            ApplyWindowTint(_viewModel.AlbumArtColor);
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_isDocking) return;
        args.Cancel = true;
        _docking.HandleFloatingClose(DetachablePanel.Player);
    }

    private void ApplyWindowTint(string? hexColor)
    {
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
        if (_isDocking || _suppressGeometryTracking) return;
        if (!args.DidPositionChange && !args.DidSizeChange) return;

        var pos = AppWindow.Position;
        var size = AppWindow.Size;
        _shellSession.UpdateLayout(s =>
        {
            if (IsExpanded)
            {
                s.PlayerWindowExpandedX = pos.X;
                s.PlayerWindowExpandedY = pos.Y;
                s.PlayerWindowExpandedWidth = size.Width;
                s.PlayerWindowExpandedHeight = size.Height;
            }
            else
            {
                s.PlayerWindowX = pos.X;
                s.PlayerWindowY = pos.Y;
                s.PlayerWindowWidth = size.Width;
                s.PlayerWindowHeight = size.Height;
            }
        });
    }

    private void OnExpandedModeChanged(DependencyObject sender, DependencyProperty dp)
    {
        var modeName = ExpandedHost.Mode.ToString();
        _shellSession.UpdateLayout(s => s.PlayerWindowExpandedMode = modeName);
    }

    private async void CompactFocusButton_Click(object sender, RoutedEventArgs e)
    {
        if (_transitionInFlight || IsExpanded) return;
        _transitionInFlight = true;
        try
        {
            ExpandedHost.Mode = ExpandedPlayerContentMode.None;
            await ToggleExpandedAsync().ConfigureAwait(true);
        }
        catch
        {
            CompactHost.Visibility = Visibility.Collapsed;
            ExpandedHost.Visibility = Visibility.Visible;
            IsExpanded = true;
            UpdateModeButtons();
        }
        finally
        {
            _transitionInFlight = false;
        }
    }

    private async void ExpandedCompactButton_Click(object sender, RoutedEventArgs e)
    {
        if (_transitionInFlight || !IsExpanded) return;
        _transitionInFlight = true;
        try
        {
            ExpandedHost.Mode = ExpandedPlayerContentMode.None;
            await ToggleExpandedAsync().ConfigureAwait(true);
        }
        catch
        {
            CompactHost.Visibility = Visibility.Visible;
            ExpandedHost.Visibility = Visibility.Collapsed;
            IsExpanded = false;
            UpdateModeButtons();
        }
        finally
        {
            _transitionInFlight = false;
        }
    }

    /// <summary>
    /// Animate the expand/collapse swap. On expand we resize the window first
    /// so <see cref="TransitionHelper"/> measures Source (compact, capped via
    /// <c>MaxWidth</c>) and Target (expanded, full window) at their real
    /// destination sizes — the morph then runs with the correct bounds. On
    /// collapse the order is reversed: animate first while the window is still
    /// large, then snap-resize down.
    /// </summary>
    private async Task ToggleExpandedAsync()
    {
        var newValue = !IsExpanded;
        _suppressGeometryTracking = true;
        try
        {
            // Persist outgoing-mode geometry before we resize (otherwise the
            // resize itself fires AppWindow.Changed and overwrites the slot).
            var pos = AppWindow.Position;
            var size = AppWindow.Size;
            _shellSession.UpdateLayout(s =>
            {
                if (IsExpanded)
                {
                    s.PlayerWindowExpandedX = pos.X;
                    s.PlayerWindowExpandedY = pos.Y;
                    s.PlayerWindowExpandedWidth = size.Width;
                    s.PlayerWindowExpandedHeight = size.Height;
                }
                else
                {
                    s.PlayerWindowX = pos.X;
                    s.PlayerWindowY = pos.Y;
                    s.PlayerWindowWidth = size.Width;
                    s.PlayerWindowHeight = size.Height;
                }
                s.PlayerWindowExpanded = newValue;
            });

            var layout = _shellSession.GetLayoutSnapshot();

            if (newValue)
            {
                // Expanding: resize first so the Target measures at its real bounds.
                ExpandedHost.Mode = ExpandedPlayerContentMode.None;
                ResizeAndMove(
                    new SizeInt32((int)layout.PlayerWindowExpandedWidth, (int)layout.PlayerWindowExpandedHeight),
                    layout.PlayerWindowExpandedX,
                    layout.PlayerWindowExpandedY);

                IsExpanded = true;

                // Let layout settle before measuring matched-Id bounds.
                await Task.Yield();
                RootGrid.UpdateLayout();

                if (_expandTransition != null)
                {
                    await _expandTransition.StartAsync();
                }
                else
                {
                    CompactHost.Visibility = Visibility.Collapsed;
                    ExpandedHost.Visibility = Visibility.Visible;
                }
            }
            else
            {
                // Collapsing: animate first (window still large), then resize.
                if (_expandTransition != null)
                {
                    await _expandTransition.ReverseAsync();
                }
                else
                {
                    CompactHost.Visibility = Visibility.Visible;
                    ExpandedHost.Visibility = Visibility.Collapsed;
                }

                IsExpanded = false;

                ResizeAndMove(
                    new SizeInt32((int)layout.PlayerWindowWidth, (int)layout.PlayerWindowHeight),
                    layout.PlayerWindowX,
                    layout.PlayerWindowY);
            }
        }
        finally
        {
            _suppressGeometryTracking = false;
            UpdateModeButtons();
        }
    }

    private void ResizeAndMove(SizeInt32 size, double x, double y)
    {
        if (size.Width <= 0 || size.Height <= 0) return;
        AppWindow.Resize(size);
        if (x != 0 || y != 0)
            AppWindow.Move(new PointInt32((int)x, (int)y));
    }

    private void UpdateModeButtons()
    {
        CompactFocusButton.Visibility = IsExpanded ? Visibility.Collapsed : Visibility.Visible;
        ExpandedCompactButton.Visibility = IsExpanded ? Visibility.Visible : Visibility.Collapsed;
    }
}
