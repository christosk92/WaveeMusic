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
                UpdateStepBar();
                break;
            case nameof(SpotifyConnectViewModel.IsDeviceCodeReady):
                UpdateQrVisibility();
                break;
        }
    }

    private void UpdatePanelVisibility()
    {
        var step = ViewModel.CurrentStep;
        AuthPanel.Visibility = step == ConnectStep.Authenticate ? Visibility.Visible : Visibility.Collapsed;
        ConnectingPanel.Visibility = step == ConnectStep.Connecting ? Visibility.Visible : Visibility.Collapsed;
        SyncingPanel.Visibility = step == ConnectStep.Syncing ? Visibility.Visible : Visibility.Collapsed;
        SuccessPanel.Visibility = step == ConnectStep.Complete ? Visibility.Visible : Visibility.Collapsed;
        ErrorPanel.Visibility = step == ConnectStep.Error ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateStepBar()
    {
        var step = ViewModel.CurrentStep;
        (StepText.Text, StepProgress.Value) = step switch
        {
            ConnectStep.Authenticate => ("Step 1 of 3", 20),
            ConnectStep.Connecting => ("Step 1 of 3", 33),
            ConnectStep.Syncing => ("Step 2 of 3", 66),
            ConnectStep.Complete => ("Step 3 of 3", 100),
            ConnectStep.Error => ("Error", StepProgress.Value),
            _ => ("Step 1 of 3", 20)
        };
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
