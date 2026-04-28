using System.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Wavee.UI.WinUI.Helpers.Application;
using Wavee.UI.WinUI.Services.Docking;
using Wavee.UI.WinUI.ViewModels;
using WinUIEx;

namespace Wavee.UI.WinUI.Floating;

/// <summary>
/// Floating top-level window that hosts the <see cref="Controls.RightPanel.RightPanelView"/>.
/// X always re-docks (per design); the docking service intercepts <c>AppWindow.Closing</c>
/// and tears the window down through <see cref="RequestClose"/>.
/// </summary>
public sealed partial class RightPanelFloatingWindow : WindowEx
{
    private readonly IPanelDockingService _docking;
    private readonly ShellViewModel _shell;
    private bool _isDocking;
    private bool _suppressShellSync;

    public RightPanelFloatingWindow()
    {
        InitializeComponent();

        _docking = Ioc.Default.GetRequiredService<IPanelDockingService>();
        _shell = Ioc.Default.GetRequiredService<ShellViewModel>();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);

        TitleBarHelper.ApplyTransparentButtonBackground(AppWindow);
        TitleBarHelper.ApplyCaptionButtonColors(AppWindow, RootGrid.ActualTheme);
        RootGrid.ActualThemeChanged += (s, _) =>
            TitleBarHelper.ApplyCaptionButtonColors(AppWindow, s.ActualTheme);

        // RightPanelView ships with IsOpen=false / Width=300 and hard-sets its
        // own Width from PanelWidth — so we have to drive those explicitly for
        // the floating instance. Without this the window renders empty.
        PanelHost.MaxWidth = double.PositiveInfinity;
        PanelHost.IsOpen = true;
        PanelHost.SelectedMode = _shell.RightPanelMode;
        SyncPanelWidth();

        RootGrid.SizeChanged += OnRootSizeChanged;
        PanelHost.RegisterPropertyChangedCallback(
            Controls.RightPanel.RightPanelView.SelectedModeProperty,
            OnPanelSelectedModeChanged);
        _shell.PropertyChanged += OnShellPropertyChanged;

        AppWindow.Closing += OnAppWindowClosing;
        AppWindow.Changed += OnAppWindowChanged;
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
        RootGrid.SizeChanged -= OnRootSizeChanged;
        _shell.PropertyChanged -= OnShellPropertyChanged;
        Close();
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_isDocking) return;
        args.Cancel = true;
        _docking.HandleFloatingClose(DetachablePanel.RightPanel);
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_isDocking) return;
        if (args.DidPositionChange || args.DidSizeChange)
            _docking.NotifyFloatingGeometryChanged(DetachablePanel.RightPanel);
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e) => SyncPanelWidth();

    private void SyncPanelWidth()
    {
        // Drive RightPanelView.PanelWidth from the window's content width so the
        // panel stretches with the window (PanelWidth's setter hard-sets Width).
        var w = RootGrid.ActualWidth;
        if (w <= 0) return;
        if (System.Math.Abs(PanelHost.PanelWidth - w) > 0.5)
            PanelHost.PanelWidth = w;
    }

    private void OnPanelSelectedModeChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (_suppressShellSync) return;
        if (_shell.RightPanelMode == PanelHost.SelectedMode) return;
        _suppressShellSync = true;
        try { _shell.RightPanelMode = PanelHost.SelectedMode; }
        finally { _suppressShellSync = false; }
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ShellViewModel.RightPanelMode)) return;
        if (_suppressShellSync) return;
        if (PanelHost.SelectedMode == _shell.RightPanelMode) return;
        _suppressShellSync = true;
        try { PanelHost.SelectedMode = _shell.RightPanelMode; }
        finally { _suppressShellSync = false; }
    }
}
