using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls.Settings;

public sealed partial class StorageNetworkSettingsSection : UserControl
{
    public SettingsViewModel ViewModel { get; }

    public StorageNetworkSettingsSection(SettingsViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
    }

    private void ClearCollectionRevisions_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ClearCollectionRevisionsCommand.CanExecute(XamlRoot))
            ViewModel.ClearCollectionRevisionsCommand.Execute(XamlRoot);
    }
}
