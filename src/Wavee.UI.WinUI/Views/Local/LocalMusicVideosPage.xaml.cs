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

[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial class LocalMusicVideosPage : UserControl, Wavee.UI.WinUI.Controls.PageHost.IPageHostAware
{
    public LocalMusicVideosViewModel ViewModel { get; }
    public LocalMusicVideosPage()
    {
        ViewModel = Ioc.Default.GetService<LocalMusicVideosViewModel>() ?? new LocalMusicVideosViewModel();
        InitializeComponent();
    }
    public async void OnEntered(object? parameter, Wavee.UI.WinUI.Controls.PageHost.PageHostNavigationMode mode)
    {
        await ViewModel.LoadAsync();
    }
    public void OnLeaving() { }
    private void Item_CardClick(object? sender, EventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is Wavee.Local.Models.LocalMusicVideo mv)
        {
            Services.LocalPlaybackLauncher.PlayOne(mv.TrackUri);
        }
    }

    private void Item_CardRightTapped(Wavee.UI.WinUI.Controls.Cards.ContentCard sender, RightTappedRoutedEventArgs e)
    {
        if (sender.Tag is not Wavee.Local.Models.LocalMusicVideo mv) return;
        Services.LocalItemContextMenuPresenter.Show(
            sender, e,
            trackUri: mv.TrackUri,
            filePath: mv.FilePath,
            kind: Wavee.Local.Classification.LocalContentKind.MusicVideo,
            linkedSpotifyTrackUri: mv.LinkedSpotifyTrackUri);
        e.Handled = true;
    }
}