using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;
using Windows.System;

namespace Wavee.UI.WinUI.Controls.Settings;

public sealed partial class AboutSettingsSection : UserControl
{
    public SettingsViewModel ViewModel { get; }

    public AboutSettingsSection(SettingsViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
    }

    private async void WhatsNew_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new WhatsNewDialog { XamlRoot = XamlRoot };
        await dialog.ShowAsync();
    }

    private void Feedback_Click(object sender, RoutedEventArgs e)
    {
        NavigationHelpers.OpenFeedback();
    }

    private async void GitHub_Click(object sender, RoutedEventArgs e)
    {
        await Launcher.LaunchUriAsync(new Uri("https://github.com/christosk92/WaveeMusic"));
    }
}
