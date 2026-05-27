using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Windowing;

namespace Wavee.UI.WinUI.Data.Contexts;

[global::WinRT.GeneratedBindableCustomProperty]
internal sealed partial class WindowContext : ObservableObject, IWindowContext, IDisposable
{
    private bool _disposed;

    [ObservableProperty]
    public partial bool IsCompactOverlay { get; set; }

    [ObservableProperty]
    public partial bool IsFullScreen { get; set; }

    [ObservableProperty]
    public partial bool IsMinimized { get; set; }

    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;

    [ObservableProperty]
    public partial bool IsUiPowerSaving { get; set; }

    [ObservableProperty]
    public partial bool IsRunningAsAdmin { get; set; }

    public WindowContext()
    {
        if (MainWindow.Instance?.AppWindow != null)
        {
            MainWindow.Instance.AppWindow.Changed += AppWindow_Changed;
            MainWindow.Instance.VisibilityChanged += MainWindow_VisibilityChanged;
            UpdateFromAppWindow(MainWindow.Instance.AppWindow);
        }
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        UpdateFromAppWindow(sender);

        if (args.DidPresenterChange)
        {
            IsCompactOverlay = sender.Presenter.Kind == AppWindowPresenterKind.CompactOverlay;
            IsFullScreen = sender.Presenter.Kind == AppWindowPresenterKind.FullScreen;
        }
    }

    private void MainWindow_VisibilityChanged(object sender, Microsoft.UI.Xaml.WindowVisibilityChangedEventArgs args)
    {
        IsVisible = args.Visible;
        UpdatePowerSavingState();
    }

    private void UpdateFromAppWindow(AppWindow appWindow)
    {
        IsCompactOverlay = appWindow.Presenter.Kind == AppWindowPresenterKind.CompactOverlay;
        IsFullScreen = appWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
        IsMinimized = appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        UpdatePowerSavingState();
    }

    private void UpdatePowerSavingState()
        => IsUiPowerSaving = IsMinimized || !IsVisible;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (MainWindow.Instance?.AppWindow != null)
        {
            MainWindow.Instance.AppWindow.Changed -= AppWindow_Changed;
            MainWindow.Instance.VisibilityChanged -= MainWindow_VisibilityChanged;
        }
    }
}
