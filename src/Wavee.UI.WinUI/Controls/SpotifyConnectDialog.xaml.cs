using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls;

public sealed partial class SpotifyConnectDialog : ContentDialog
{
    public SpotifyConnectViewModel ViewModel { get; }

    public SpotifyConnectDialog()
    {
        ViewModel = Ioc.Default.GetRequiredService<SpotifyConnectViewModel>();
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.RequestClose -= OnRequestClose;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.RequestClose += OnRequestClose;

        var accentBrush = Application.Current.Resources["AccentFillColorDefaultBrush"] as Microsoft.UI.Xaml.Media.SolidColorBrush;
        if (accentBrush != null)
        {
            ViewModel.AccentColor = accentBrush.Color;
        }

        ViewModel.Initialize();
    }

    private void OnRequestClose()
    {
        DispatcherQueue.TryEnqueue(() => Hide());
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SpotifyConnectViewModel.CurrentStep):
                UpdatePanelVisibility();
                break;
            case nameof(SpotifyConnectViewModel.IsDeviceCodeReady):
                UpdateQrVisibility();
                break;
        }
    }

    private void UpdatePanelVisibility()
    {
        // Three coarse panels: device-code/QR auth, live progress, error.
        // Fine-grained phase detail lives in MainText/SubText/OverallProgress
        // bound directly into ProgressPanel — no per-phase visibility needed.
        var step = ViewModel.CurrentStep;
        AuthPanel.Visibility = step == ConnectStep.Authenticate ? Visibility.Visible : Visibility.Collapsed;
        ProgressPanel.Visibility = step == ConnectStep.Progress ? Visibility.Visible : Visibility.Collapsed;
        ErrorPanel.Visibility = step == ConnectStep.Error ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateQrVisibility()
    {
        var ready = ViewModel.IsDeviceCodeReady;
        QrLoadingRing.IsActive = !ready;
        QrLoadingRing.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        QrImage.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        QrOverlayIcon.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        var code = ViewModel.UserCode;
        if (string.IsNullOrEmpty(code)) return;

        var dp = new DataPackage();
        dp.SetText(code);
        Clipboard.SetContent(dp);

        // Animate: switch to checkmark
        CopyIcon.Glyph = "\uE73E"; // Checkmark
        CopyCodeButton.IsEnabled = false;

        await Task.Delay(1500);

        // Restore copy icon
        CopyIcon.Glyph = "\uE8C8"; // Copy
        CopyCodeButton.IsEnabled = true;
    }
}
