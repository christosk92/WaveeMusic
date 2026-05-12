using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Wavee.Local.Models;
using Wavee.UI.Library.Local;
using Wavee.UI.WinUI.Controls.HeroHeader;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels.Local;

namespace Wavee.UI.WinUI.Views.Local;

public sealed partial class LocalMovieDetailPage : Page
{
    public LocalMovieDetailViewModel ViewModel { get; }

    private ShyHeaderController? _shyHeader;

    public LocalMovieDetailPage()
    {
        ViewModel = Ioc.Default.GetService<LocalMovieDetailViewModel>() ?? new LocalMovieDetailViewModel();
        InitializeComponent();
        ViewModel.PropertyChanged += (_, e) =>
        {
            SyncTmdbButtonState();
            if (e.PropertyName == nameof(ViewModel.Movie))
                SyncHeroMeta();
        };
        ViewModel.Cast.CollectionChanged += (_, _) => SyncCastVisibility();
        Loaded += LocalMovieDetailPage_Loaded;
        Unloaded += (_, _) =>
        {
            _tokenSub?.Dispose(); _tokenSub = null;
            _shyHeader?.Dispose(); _shyHeader = null;
        };
    }

    private void LocalMovieDetailPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= LocalMovieDetailPage_Loaded;
        _shyHeader = new ShyHeaderController(
            PageScrollView, HeroGrid, HeroOverlayPanel, ShyHeaderCard,
            (TransitionHelper)Resources["LocalMovieShyHeaderTransition"],
            ShyHeaderFade.ForHeroHeader(HeroGrid),
            ShyHeaderPinOffset.Below(HeroGrid, 120));
        _shyHeader.Attach();
        _shyHeader.Reset();
        HookTokenStore();
    }

    public static Visibility VisibilityIfNonEmpty(string? text) =>
        !string.IsNullOrWhiteSpace(text) ? Visibility.Visible : Visibility.Collapsed;

    private void SyncHeroMeta()
    {
        if (ViewModel.Movie is not { } m)
        {
            MetaLineText.Text = string.Empty;
            ShyMetaText.Text = string.Empty;
            TaglineText.Visibility = Visibility.Collapsed;
            return;
        }

        if (!string.IsNullOrWhiteSpace(m.Tagline))
        {
            TaglineText.Text = m.Tagline;
            TaglineText.Visibility = Visibility.Visible;
        }
        else
        {
            TaglineText.Visibility = Visibility.Collapsed;
        }

        // Build "{Year} · {Runtime} min · {Genres} · ★ {VoteAverage:F1}" —
        // skip the segments with no data so a fresh / partially-enriched
        // movie still reads cleanly.
        var parts = new List<string>(4);
        if (m.Year is { } y && y > 0) parts.Add(y.ToString());
        if (m.RuntimeMinutes is { } rt && rt > 0) parts.Add(FormatRuntime(rt));
        if (m.Genres is { Count: > 0 } gs) parts.Add(string.Join(", ", gs));
        if (m.VoteAverage is { } va && va > 0) parts.Add($"★ {va:F1}");
        MetaLineText.Text = string.Join("  ·  ", parts);

        // Shy pill keeps year + runtime only — genres + rating won't fit at 12 px.
        var shyParts = new List<string>(2);
        if (m.Year is { } y2 && y2 > 0) shyParts.Add(y2.ToString());
        if (m.RuntimeMinutes is { } rt2 && rt2 > 0) shyParts.Add(FormatRuntime(rt2));
        ShyMetaText.Text = string.Join(" · ", shyParts);
    }

    private static string FormatRuntime(int minutes)
    {
        var h = minutes / 60;
        var m = minutes % 60;
        return h > 0 ? $"{h}h {m}m" : $"{m} min";
    }

    private void SyncCastVisibility()
    {
        CastSection.Visibility = ViewModel.Cast.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
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
        var hasToken = tokenStore?.HasToken == true;
        var alreadyMatched = ViewModel.Movie?.TmdbId is not null;

        if (!hasToken)
        {
            TmdbSyncButton.Visibility = Visibility.Visible;
            TmdbSyncText.Text = "Set up TMDB";
            TmdbSyncGlyph.Glyph = Wavee.UI.WinUI.Styles.FluentGlyphs.SettingsGear;
        }
        else if (alreadyMatched)
        {
            // Already matched → primary CTA hidden; Force-resync lives in
            // the More flyout next to it.
            TmdbSyncButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            TmdbSyncButton.Visibility = Visibility.Visible;
            TmdbSyncText.Text = "Sync with TMDB";
            TmdbSyncGlyph.Glyph = Wavee.UI.WinUI.Styles.FluentGlyphs.Refresh;
        }
    }

    private async void ForceResync_Click(object sender, RoutedEventArgs e)
    {
        var tokenStore = Ioc.Default.GetService<Wavee.Local.Enrichment.ITmdbTokenStore>();
        if (tokenStore?.HasToken != true)
        {
            LocalLibraryPage.OpenLocalEnrichmentSettings();
            return;
        }
        var uri = ViewModel.Movie?.TrackUri;
        if (uri is null) return;
        var enrichment = Ioc.Default.GetService<Wavee.Local.Enrichment.ILocalEnrichmentService>();
        if (enrichment is null) return;
        await enrichment.EnqueueAsync(uri);
    }

    private async void TmdbSyncButton_Click(object sender, RoutedEventArgs e)
    {
        var tokenStore = Ioc.Default.GetService<Wavee.Local.Enrichment.ITmdbTokenStore>();
        if (tokenStore?.HasToken != true)
        {
            LocalLibraryPage.OpenLocalEnrichmentSettings();
            return;
        }
        var uri = ViewModel.Movie?.TrackUri;
        if (uri is null) return;
        var enrichment = Ioc.Default.GetService<Wavee.Local.Enrichment.ILocalEnrichmentService>();
        if (enrichment is null) return;
        System.Diagnostics.Debug.WriteLine($"[enrich] user clicked Sync on movie {uri}");
        await enrichment.EnqueueAsync(uri);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string uri)
            await ViewModel.LoadAsync(uri);
    }

    private void CastMember_CardClick(object? sender, EventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not LocalCastMember cast) return;
        if (cast.PersonId is not { } pid) return;
        NavigationHelpers.OpenLocalPersonDetail(
            pid,
            seedName: cast.Name,
            seedImageUri: cast.ProfileImageUri,
            openInNewTab: NavigationHelpers.IsCtrlPressed());
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        var uri = ViewModel.Movie?.TrackUri;
        if (uri is null) return;
        Services.LocalPlaybackLauncher.PlayOne(uri);
    }

    private async void MarkWatched_Click(object sender, RoutedEventArgs e)
    {
        var uri = ViewModel.Movie?.TrackUri;
        if (uri is null) return;
        var facade = Ioc.Default.GetService<ILocalLibraryFacade>();
        if (facade is null) return;
        await facade.MarkWatchedAsync(uri, true);
    }
}
