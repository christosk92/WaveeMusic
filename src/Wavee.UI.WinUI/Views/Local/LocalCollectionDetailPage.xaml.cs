using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Wavee.UI.WinUI.ViewModels.Local;

namespace Wavee.UI.WinUI.Views.Local;

public sealed partial class LocalCollectionDetailPage : Page
{
    public LocalCollectionDetailViewModel ViewModel { get; }
    public LocalCollectionDetailPage()
    {
        ViewModel = Ioc.Default.GetService<LocalCollectionDetailViewModel>() ?? new LocalCollectionDetailViewModel();
        InitializeComponent();
    }
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string id) await ViewModel.LoadAsync(id);
    }
    private void Member_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is Wavee.Local.LocalTrackRow row)
        {
            var trackUris = ViewModel.Members.Select(t => t.TrackUri).ToList();
            var idx = trackUris.FindIndex(uri => uri == row.TrackUri);
            Services.LocalPlaybackLauncher.PlayQueue(
                trackUris,
                idx < 0 ? 0 : idx,
                "wavee:local:collection:" + (ViewModel.Collection?.Id ?? ""),
                ViewModel.Collection?.Name ?? "Collection");
        }
    }

    private void Member_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not Wavee.Local.LocalTrackRow row) return;
        Services.LocalItemContextMenuPresenter.Show(
            fe, e,
            trackUri: row.TrackUri,
            filePath: row.FilePath,
            kind: row.IsVideo
                ? Wavee.Local.Classification.LocalContentKind.MusicVideo
                : Wavee.Local.Classification.LocalContentKind.Music,
            lastPositionMs: row.LastPositionMs);
        e.Handled = true;
    }
}
