using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Controls.SidebarPlayer;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Helpers.Application;
using Wavee.UI.WinUI.Services.Docking;
using Windows.Graphics;
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
    private TransitionHelper? _expandTransition;
    private bool _isDocking;
    private bool _suppressGeometryTracking;
    private bool _transitionInFlight;

    /// <summary>True when the window is showing the 2-column expanded layout.</summary>
    public bool IsExpanded { get; private set; }

    public PlayerFloatingWindow()
    {
        InitializeComponent();

        _docking = Ioc.Default.GetRequiredService<IPanelDockingService>();
        _shellSession = Ioc.Default.GetRequiredService<IShellSessionService>();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);

        TitleBarHelper.ApplyTransparentButtonBackground(AppWindow);
        TitleBarHelper.ApplyCaptionButtonColors(AppWindow, RootGrid.ActualTheme);
        RootGrid.ActualThemeChanged += (s, _) =>
            TitleBarHelper.ApplyCaptionButtonColors(AppWindow, s.ActualTheme);

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
            UpdateChevronGlyph();
        }
        if (Enum.TryParse<ExpandedPlayerContentMode>(layout.PlayerWindowExpandedMode, out var mode))
        {
            ExpandedHost.Mode = mode;
        }

        ExpandedHost.RegisterPropertyChangedCallback(
            ExpandedPlayerView.ModeProperty,
            OnExpandedModeChanged);
    }

    /// <summary>
    /// Called by <see cref="PanelDockingService"/> to actually close the window
    /// after re-docking. Suppresses the <c>Closing</c>-→-cancel re-entry.
    /// </summary>
    internal void RequestClose()
    {
        _isDocking = true;
        AppWindow.Closing -= OnAppWindowClosing;
        AppWindow.Changed -= OnAppWindowChanged;
        Close();
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_isDocking) return;
        args.Cancel = true;
        _docking.HandleFloatingClose(DetachablePanel.Player);
    }

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

    private async void ExpandButton_Click(object sender, RoutedEventArgs e)
    {
        if (_transitionInFlight) return;
        _transitionInFlight = true;
        try
        {
            await ToggleExpandedAsync().ConfigureAwait(true);
        }
        catch
        {
            // Best-effort: a layout/animation hiccup shouldn't deadlock the chevron.
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
                ResizeAndMove(
                    new SizeInt32((int)layout.PlayerWindowExpandedWidth, (int)layout.PlayerWindowExpandedHeight),
                    layout.PlayerWindowExpandedX,
                    layout.PlayerWindowExpandedY);

                IsExpanded = true;
                UpdateChevronGlyph();

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
                UpdateChevronGlyph();

                ResizeAndMove(
                    new SizeInt32((int)layout.PlayerWindowWidth, (int)layout.PlayerWindowHeight),
                    layout.PlayerWindowX,
                    layout.PlayerWindowY);
            }
        }
        finally
        {
            _suppressGeometryTracking = false;
        }
    }

    private void ResizeAndMove(SizeInt32 size, double x, double y)
    {
        if (size.Width <= 0 || size.Height <= 0) return;
        AppWindow.Resize(size);
        if (x != 0 || y != 0)
            AppWindow.Move(new PointInt32((int)x, (int)y));
    }

    private void UpdateChevronGlyph()
    {
        // E73F (FullScreen) when compact → click expands.
        // E740 (BackToWindow) when expanded → click collapses.
        ExpandGlyph.Glyph = IsExpanded ? "" : "";
        ToolTipService.SetToolTip(
            ExpandButton,
            IsExpanded ? "Collapse to mini player" : "Expand to full player");
    }
}
