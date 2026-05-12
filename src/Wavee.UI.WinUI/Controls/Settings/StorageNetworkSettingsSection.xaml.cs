using System;
using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls.Settings;

public sealed partial class StorageNetworkSettingsSection : UserControl, ISettingsSearchFilter
{
    public SettingsViewModel ViewModel { get; }

    public StorageNetworkSettingsSection(SettingsViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        if (ViewModel.LocalFiles is { } local)
        {
            local.PropertyChanged += LocalFiles_PropertyChanged;
            SyncTmdbStatusPill(local.TmdbStatus);
        }
    }

    private void LocalFiles_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LocalFilesViewModel.TmdbStatus)
            && sender is LocalFilesViewModel local)
        {
            SyncTmdbStatusPill(local.TmdbStatus);
        }
    }

    private void SyncTmdbStatusPill(TmdbStatus status)
    {
        switch (status)
        {
            case TmdbStatus.Connected:
                TmdbStatusPillText.Text = "✓ Connected";
                TmdbStatusPill.Background = (Brush)Application.Current.Resources["SystemFillColorSuccessBackgroundBrush"];
                TmdbStatusPillText.Foreground = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];
                break;
            case TmdbStatus.Testing:
                TmdbStatusPillText.Text = "Testing…";
                TmdbStatusPill.Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
                TmdbStatusPillText.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
                break;
            case TmdbStatus.InvalidToken:
                TmdbStatusPillText.Text = "✗ Invalid";
                TmdbStatusPill.Background = (Brush)Application.Current.Resources["SystemFillColorCriticalBackgroundBrush"];
                TmdbStatusPillText.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
                break;
            default:
                TmdbStatusPillText.Text = "Not configured";
                TmdbStatusPill.Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
                TmdbStatusPillText.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
                break;
        }
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

    private async void RunEnrichmentNow_Click(object sender, RoutedEventArgs e)
    {
        var enrichment = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
            .GetService<Wavee.Local.Enrichment.ILocalEnrichmentService>();
        if (enrichment is null) return;
        await enrichment.EnqueueAllPendingAsync();
    }

    private void TestTmdbToken_Click(object sender, RoutedEventArgs e)
    {
        var local = ViewModel.LocalFiles;
        if (local is null) return;
        if (local.VerifyTokenCommand.CanExecute(null))
            local.VerifyTokenCommand.Execute(null);
    }

    private void ClearTmdbToken_Click(object sender, RoutedEventArgs e)
    {
        var local = ViewModel.LocalFiles;
        if (local is null) return;
        if (local.ClearTokenCommand.CanExecute(null))
            local.ClearTokenCommand.Execute(null);
    }

    private async void GetTmdbToken_Click(object sender, RoutedEventArgs e)
    {
        await Windows.System.Launcher.LaunchUriAsync(
            new Uri("https://www.themoviedb.org/settings/api"));
    }

    private async void ClearCachedLookups_Click(object sender, RoutedEventArgs e)
    {
        var enrichment = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
            .GetService<Wavee.Local.Enrichment.ILocalEnrichmentService>();
        if (enrichment is null) return;
        await enrichment.ClearCachedLookupsAsync();
    }

    private void RescanAllLocal_Click(object sender, RoutedEventArgs e)
    {
        var local = ViewModel.LocalFiles;
        if (local is null) return;
        if (local.RescanAllCommand.CanExecute(null))
            local.RescanAllCommand.Execute(null);
    }
}
