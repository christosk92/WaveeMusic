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

public sealed partial class LocalLikedSongsPage : Page
{
    public LocalLikedSongsViewModel ViewModel { get; }
    public LocalLikedSongsPage()
    {
        ViewModel = Ioc.Default.GetService<LocalLikedSongsViewModel>() ?? new LocalLikedSongsViewModel();
        InitializeComponent();
    }
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync();
    }

    private void Track_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not Wavee.Local.LocalTrackRow row) return;
        Services.LocalItemContextMenuPresenter.Show(
            fe, e,
            trackUri: row.TrackUri,
            filePath: row.FilePath,
            kind: row.IsVideo
                ? Wavee.Local.Classification.LocalContentKind.MusicVideo
                : Wavee.Local.Classification.LocalContentKind.Music,
            lastPositionMs: row.LastPositionMs,
            isLiked: true);
        e.Handled = true;
    }
}
