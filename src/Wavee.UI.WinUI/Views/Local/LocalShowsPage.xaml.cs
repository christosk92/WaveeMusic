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

public sealed partial class LocalShowsPage : Page
{
    public LocalShowsViewModel ViewModel { get; }

    private ShyHeaderController? _shyHeader;

    public LocalShowsPage()
    {
        ViewModel = Ioc.Default.GetService<LocalShowsViewModel>() ?? new LocalShowsViewModel();
        InitializeComponent();
        Loaded += LocalShowsPage_Loaded;
        Unloaded += (_, _) =>
        {
            _tokenSub?.Dispose(); _tokenSub = null;
            _shyHeader?.Dispose(); _shyHeader = null;
        };
    }

    private void LocalShowsPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= LocalShowsPage_Loaded;
        _shyHeader = new ShyHeaderController(
            PageScrollView, HeroGrid, HeroOverlayPanel, ShyHeaderCard,
            (TransitionHelper)Resources["LocalShowsShyHeaderTransition"],
            ShyHeaderFade.ForCompositionOpacity(HeroGrid),
            ShyHeaderPinOffset.Below(HeroGrid, 90));
        _shyHeader.Attach();
        _shyHeader.Reset();
        HookTokenStore();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync();
    }

    private void ShowCard_CardClick(object? sender, EventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string showId)
            Frame.Navigate(typeof(LocalShowDetailPage), showId);
    }

    public static string FormatShowMeta(int seasonCount, int episodeCount)
        => $"{seasonCount} {(seasonCount == 1 ? "season" : "seasons")} · {episodeCount} {(episodeCount == 1 ? "episode" : "episodes")}";

    private void HookTokenStore()
    {
        SyncTmdbButtonState();
        var tokenStore = Ioc.Default.GetService<Wavee.Local.Enrichment.ITmdbTokenStore>();
        if (tokenStore is not null)
            _tokenSub = tokenStore.HasTokenChanged.Subscribe(_ =>
                DispatcherQueue.TryEnqueue(SyncTmdbButtonState));
    }

    private IDisposable? _tokenSub;

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
        await enrichment.EnqueueAllShowsAsync(forceResync: false);
    }
}
