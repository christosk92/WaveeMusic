using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Windowing;

namespace Wavee.UI.WinUI.Data.Contexts;

internal sealed partial class WindowContext : ObservableObject, IWindowContext, IDisposable
{
    private bool _disposed;

    [ObservableProperty]
    private bool _isCompactOverlay;

    [ObservableProperty]
    private bool _isFullScreen;

    [ObservableProperty]
    private bool _isRunningAsAdmin;

    public WindowContext()
    {
        if (MainWindow.Instance?.AppWindow != null)
        {
            MainWindow.Instance.AppWindow.Changed += AppWindow_Changed;
        }
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPresenterChange)
        {
            IsCompactOverlay = sender.Presenter.Kind == AppWindowPresenterKind.CompactOverlay;
            IsFullScreen = sender.Presenter.Kind == AppWindowPresenterKind.FullScreen;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (MainWindow.Instance?.AppWindow != null)
        {
            MainWindow.Instance.AppWindow.Changed -= AppWindow_Changed;
        }
    }
}
