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
using Wavee.Local.Models;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.ViewModels.Local;

namespace Wavee.UI.WinUI.Views;

public sealed partial class LocalLibraryPage : Page, INavigationCacheMemoryParticipant
{
    public LocalLandingViewModel ViewModel { get; }

    public LocalLibraryPage()
    {
        ViewModel = Ioc.Default.GetService<LocalLandingViewModel>()
                    ?? new LocalLandingViewModel();
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync();
    }

    private bool _trimmedForNavigationCache;
    public void TrimForNavigationCache()
    {
        if (_trimmedForNavigationCache) return;
        _trimmedForNavigationCache = true;
        Bindings?.StopTracking();
    }
    public void RestoreFromNavigationCache()
    {
        if (!_trimmedForNavigationCache) return;
        _trimmedForNavigationCache = false;
        Bindings?.Update();
    }

    private void SeeAllShows_Click(object sender, RoutedEventArgs e) =>
        Helpers.Navigation.NavigationHelpers.OpenLocalShows();

    private void SeeAllMovies_Click(object sender, RoutedEventArgs e) =>
        Helpers.Navigation.NavigationHelpers.OpenLocalMovies();

    private void SeeAllMusicVideos_Click(object sender, RoutedEventArgs e) =>
        Helpers.Navigation.NavigationHelpers.OpenLocalMusicVideos();

    private void SeeAllMusic_Click(object sender, RoutedEventArgs e) =>
        Helpers.Navigation.NavigationHelpers.OpenLocalMusic();

    private void SeeAllOthers_Click(object sender, RoutedEventArgs e) =>
        Frame.Navigate(typeof(Local.LocalOtherPage));

    // ── ContentCard CardClick routing (rails now use cards:ContentCard, see XAML) ──

    private void ShowCard_CardClick(object? sender, EventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string showId)
            Frame.Navigate(typeof(Local.LocalShowDetailPage), showId);
    }

    private void MovieCard_CardClick(object? sender, EventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string trackUri)
            Frame.Navigate(typeof(Local.LocalMovieDetailPage), trackUri);
    }

    private void MusicVideoCard_CardClick(object? sender, EventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string trackUri)
            PlayLocalTrack(trackUri);
    }

    private void ContinueItem_CardClick(object? sender, EventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is LocalContinueItem item)
            PlayLocalTrack(item.TrackUri);
    }

    // Music recently-added rail still uses an inline TrackItem-style row.
    private void MusicRow_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string trackUri)
            PlayLocalTrack(trackUri);
    }

    // ── x:Bind format helpers (static so x:Bind resolves at compile time) ──

    public static string FormatEpisodes(int count) =>
        count == 1 ? "1 episode" : $"{count} episodes";

    public static string FormatYear(int? year) =>
        year is { } y && y > 0 ? y.ToString() : string.Empty;

    // ── TMDB CTA wiring ───────────────────────────────────────────────────

    private void SetUpTmdb_Click(object sender, RoutedEventArgs e)
        => OpenLocalEnrichmentSettings();

    private void TmdbCtaBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        var settings = Ioc.Default.GetService<Wavee.UI.WinUI.Data.Contracts.ISettingsService>();
        if (settings is null) return;
        settings.Update(s => s.TmdbTeaserDismissed = true);
        // Also update the VM cache so the InfoBar binding flips immediately
        // (we don't want a tick of empty Closed/Open animation on a dismiss).
        ViewModel.MarkTmdbTeaserDismissed();
    }

    internal static void OpenLocalEnrichmentSettings()
    {
        Helpers.Navigation.NavigationHelpers.OpenSettings(
            new Data.Parameters.SettingsNavigationParameter(
                SectionTag: "storage",
                GroupKey: "local-enrichment",
                EntryTitle: "Online metadata lookups"));
    }

    private static void PlayLocalTrack(string trackUri)
        => Services.LocalPlaybackLauncher.PlayOne(trackUri);
}
