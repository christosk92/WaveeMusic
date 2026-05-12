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

public sealed partial class LocalMusicPage : Page
{
    public LocalMusicViewModel ViewModel { get; }
    public LocalMusicPage()
    {
        ViewModel = Ioc.Default.GetService<LocalMusicViewModel>() ?? new LocalMusicViewModel();
        InitializeComponent();
    }
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync();
    }
    private void Track_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is Wavee.Local.LocalTrackRow row)
        {
            Services.LocalPlaybackLauncher.PlayOne(row.TrackUri);
        }
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
            lastPositionMs: row.LastPositionMs);
        e.Handled = true;
    }

    // ── Sync with Spotify ─────────────────────────────────────────────────
    // Single-state CTA: the user is already authenticated to Spotify, so
    // there's no "Set up Spotify" gate — unlike the TMDB BYO-token flow,
    // the Sync button always means "go fetch metadata now". Force-resync
    // lives in the More flyout next to it.

    private async void SpotifySyncButton_Click(object sender, RoutedEventArgs e)
    {
        var enrichment = Ioc.Default.GetService<Wavee.Local.Enrichment.ILocalEnrichmentService>();
        if (enrichment is null) return;
        await enrichment.EnqueueAllMusicAsync(forceResync: false);
    }

    private async void SpotifyForceResync_Click(object sender, RoutedEventArgs e)
    {
        var enrichment = Ioc.Default.GetService<Wavee.Local.Enrichment.ILocalEnrichmentService>();
        if (enrichment is null) return;
        await enrichment.EnqueueAllMusicAsync(forceResync: true);
    }
}
