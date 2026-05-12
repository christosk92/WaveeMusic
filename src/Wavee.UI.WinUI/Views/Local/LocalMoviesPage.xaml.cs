using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Wavee.UI.WinUI.Controls.HeroHeader;
using Wavee.UI.WinUI.ViewModels.Local;

namespace Wavee.UI.WinUI.Views.Local;

public sealed partial class LocalMoviesPage : Page
{
    public LocalMoviesViewModel ViewModel { get; }

    private ShyHeaderController? _shyHeader;

    public LocalMoviesPage()
    {
        ViewModel = Ioc.Default.GetService<LocalMoviesViewModel>() ?? new LocalMoviesViewModel();
        InitializeComponent();
        Loaded += LocalMoviesPage_Loaded;
        Unloaded += (_, _) =>
        {
            _tokenSub?.Dispose(); _tokenSub = null;
            _shyHeader?.Dispose(); _shyHeader = null;
        };
    }

    private void LocalMoviesPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= LocalMoviesPage_Loaded;
        _shyHeader = new ShyHeaderController(
            PageScrollView, HeroGrid, HeroOverlayPanel, ShyHeaderCard,
            (TransitionHelper)Resources["LocalMoviesShyHeaderTransition"],
            ShyHeaderFade.ForCompositionOpacity(HeroGrid),
            ShyHeaderPinOffset.Below(HeroGrid, 90));
        _shyHeader.Attach();
        _shyHeader.Reset();
        HookTokenStore();
    }

    private IDisposable? _tokenSub;

    private void HookTokenStore()
    {
        SyncTmdbButtonState();
        var tokenStore = Ioc.Default.GetService<Wavee.Local.Enrichment.ITmdbTokenStore>();
        if (tokenStore is not null)
            _tokenSub = tokenStore.HasTokenChanged.Subscribe(_ =>
                DispatcherQueue.TryEnqueue(SyncTmdbButtonState));
    }

    private void SyncTmdbButtonState()
    {
        var tokenStore = Ioc.Default.GetService<Wavee.Local.Enrichment.ITmdbTokenStore>();
        if (tokenStore?.HasToken == true)
        {
            SyncButtonText.Text = "Sync with TMDB";
            SyncButtonGlyph.Glyph = Wavee.UI.WinUI.Styles.FluentGlyphs.Refresh;
            ShySyncText.Text = "Sync";
            ShySyncGlyph.Glyph = Wavee.UI.WinUI.Styles.FluentGlyphs.Refresh;
        }
        else
        {
            SyncButtonText.Text = "Set up TMDB";
            SyncButtonGlyph.Glyph = Wavee.UI.WinUI.Styles.FluentGlyphs.SettingsGear;
            ShySyncText.Text = "Set up";
            ShySyncGlyph.Glyph = Wavee.UI.WinUI.Styles.FluentGlyphs.SettingsGear;
        }
    }

    private async void SyncButton_Click(object sender, RoutedEventArgs e)
    {
        var tokenStore = Ioc.Default.GetService<Wavee.Local.Enrichment.ITmdbTokenStore>();
        if (tokenStore?.HasToken != true)
        {
            LocalLibraryPage.OpenLocalEnrichmentSettings();
            return;
        }

        var enrichment = Ioc.Default.GetService<Wavee.Local.Enrichment.ILocalEnrichmentService>();
        if (enrichment is null) return;
        await enrichment.EnqueueAllMoviesAsync(forceResync: false);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync();
    }

    private void MovieCard_CardClick(object? sender, EventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is Wavee.Local.Models.LocalMovie m)
            Frame.Navigate(typeof(LocalMovieDetailPage), m.TrackUri);
    }

    private void MovieCard_CardRightTapped(Wavee.UI.WinUI.Controls.Cards.ContentCard sender, RightTappedRoutedEventArgs e)
    {
        if (sender.Tag is not Wavee.Local.Models.LocalMovie m) return;
        Services.LocalItemContextMenuPresenter.Show(
            sender, e,
            trackUri: m.TrackUri,
            filePath: m.FilePath,
            kind: Wavee.Local.Classification.LocalContentKind.Movie,
            lastPositionMs: m.LastPositionMs,
            watchedAt: m.WatchedAt);
        e.Handled = true;
    }

    public static string FormatYear(int? year) =>
        year is { } y && y > 0 ? y.ToString() : string.Empty;
}
