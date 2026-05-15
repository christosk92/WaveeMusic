using System;
using Microsoft.UI.Xaml;
using Wavee.UI.WinUI.Helpers.Application;
using Wavee.UI.WinUI.Views;
using WinUIEx;

namespace Wavee.UI.WinUI.Floating;

/// <summary>
/// Single-instance floating window that hosts <see cref="DebugPage"/>.
/// Triggered by Ctrl+Shift+D from the shell. A second press of the shortcut
/// brings the existing window to the front instead of opening a duplicate.
/// Window size + position persists via WinUIEx <c>PersistenceId</c>.
/// </summary>
public sealed partial class DebugFloatingWindow : WindowEx
{
    private static DebugFloatingWindow? _current;

    private bool _disposed;

    /// <summary>
    /// Opens the Debug window if not already open, or activates the existing
    /// instance. Single entry point for the keyboard shortcut.
    /// </summary>
    public static void EnsureOpen()
    {
        if (_current is { _disposed: false })
        {
            _current.Activate();
            return;
        }

        var window = new DebugFloatingWindow();
        _current = window;
        window.Activate();
    }

    public DebugFloatingWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);

        TitleBarHelper.ApplyTransparentButtonBackground(AppWindow);
        TitleBarHelper.ApplyCaptionButtonColors(AppWindow, RootGrid.ActualTheme);
        RootGrid.ActualThemeChanged += OnRootThemeChanged;

        Closed += OnClosed;

        RootFrame.Navigate(typeof(DebugPage));
    }

    private void OnRootThemeChanged(FrameworkElement sender, object args)
    {
        if (_disposed) return;
        TitleBarHelper.ApplyCaptionButtonColors(AppWindow, sender.ActualTheme);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (_disposed) return;
        _disposed = true;

        (RootFrame.Content as IDisposable)?.Dispose();
        RootFrame.Content = null;

        RootGrid.ActualThemeChanged -= OnRootThemeChanged;
        Closed -= OnClosed;

        if (ReferenceEquals(_current, this))
            _current = null;
    }
}
