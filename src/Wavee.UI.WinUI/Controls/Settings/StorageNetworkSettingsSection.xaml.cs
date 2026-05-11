using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls.Settings;

public sealed partial class StorageNetworkSettingsSection : UserControl, ISettingsSearchFilter
{
    public SettingsViewModel ViewModel { get; }

    public StorageNetworkSettingsSection(SettingsViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
    }

    public void ApplySearchFilter(string? groupKey)
        => SettingsGroupFilter.Apply(SettingsGroupsRoot, groupKey);

    private void ClearCollectionRevisions_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ClearCollectionRevisionsCommand.CanExecute(XamlRoot))
            ViewModel.ClearCollectionRevisionsCommand.Execute(XamlRoot);
    }

    private void AddLocalFolder_Click(object sender, RoutedEventArgs e)
    {
        var local = ViewModel.LocalFiles;
        if (local is null) return;
        if (local.AddFolderCommand.CanExecute(XamlRoot))
            local.AddFolderCommand.Execute(XamlRoot);
    }

    private void RemoveLocalFolder_Click(object sender, RoutedEventArgs e)
    {
        var local = ViewModel.LocalFiles;
        if (local is null) return;
        if (sender is not FrameworkElement fe || fe.Tag is not LocalFolderRow row) return;
        if (local.RemoveFolderCommand.CanExecute(row))
            local.RemoveFolderCommand.Execute(row);
    }

    private void RescanAllLocal_Click(object sender, RoutedEventArgs e)
    {
        var local = ViewModel.LocalFiles;
        if (local is null) return;
        if (local.RescanAllCommand.CanExecute(null))
            local.RescanAllCommand.Execute(null);
    }
}
