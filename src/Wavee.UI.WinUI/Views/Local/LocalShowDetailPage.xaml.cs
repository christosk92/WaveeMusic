using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Wavee.Local.Models;
using Wavee.UI.WinUI.Controls.HeroHeader;
using Wavee.UI.WinUI.Controls.Local;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels.Local;

namespace Wavee.UI.WinUI.Views.Local;

public sealed partial class LocalShowDetailPage : Page
{
    public LocalShowDetailViewModel ViewModel { get; }

    private ShyHeaderController? _shyHeader;

    public LocalShowDetailPage()
    {
        ViewModel = Ioc.Default.GetService<LocalShowDetailViewModel>() ?? new LocalShowDetailViewModel();
        InitializeComponent();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.ShowCast.CollectionChanged += (_, _) => SyncCastSection();
        Loaded += LocalShowDetailPage_Loaded;
        Unloaded += (_, _) =>
        {
            _tokenSub?.Dispose(); _tokenSub = null;
            _shyHeader?.Dispose(); _shyHeader = null;
        };
    }

    private void LocalShowDetailPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= LocalShowDetailPage_Loaded;
        _shyHeader = new ShyHeaderController(
            PageScrollView, HeroGrid, HeroOverlayPanel, ShyHeaderCard,
            (TransitionHelper)Resources["LocalShowShyHeaderTransition"],
            ShyHeaderFade.ForHeroHeader(HeroGrid),
            ShyHeaderPinOffset.Below(HeroGrid, 120));
        _shyHeader.Attach();
        _shyHeader.Reset();
        HookTokenStore();
    }

    private void SyncCastSection()
    {
        CastSection.Visibility = ViewModel.ShowCast.Count > 0
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
        var alreadyMatched = ViewModel.Show?.TmdbId is not null;

        if (!hasToken)
        {
            // Promote setup to the primary slot — the show has no metadata
            // and won't until a token's configured.
            TmdbSyncButton.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            TmdbSyncText.Text = "Set up TMDB";
            TmdbSyncGlyph.Glyph = Wavee.UI.WinUI.Styles.FluentGlyphs.SettingsGear;
        }
        else if (!alreadyMatched)
        {
            // Has token, never matched → primary "Sync" CTA.
            TmdbSyncButton.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            TmdbSyncText.Text = "Sync with TMDB";
            TmdbSyncGlyph.Glyph = Wavee.UI.WinUI.Styles.FluentGlyphs.Refresh;
        }
        else
        {
            // Already matched → hide the primary button; Force-resync lives
            // in the More flyout below so the chrome stays clean.
            TmdbSyncButton.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        }
    }

    private async void TmdbSyncButton_Click(object sender, RoutedEventArgs e)
    {
        var tokenStore = Ioc.Default.GetService<Wavee.Local.Enrichment.ITmdbTokenStore>();
        if (tokenStore?.HasToken != true)
        {
            LocalLibraryPage.OpenLocalEnrichmentSettings();
            return;
        }
        if (ViewModel.Show is not { } show) return;
        var enrichment = Ioc.Default.GetService<Wavee.Local.Enrichment.ILocalEnrichmentService>();
        if (enrichment is null) return;
        await enrichment.EnqueueShowAsync(show.Id, forceResync: false);
    }

    // x:Bind function-binding helpers — WinUI can't directly bind int / nullable
    // / string to Visibility, so the templates route through these static
    // helpers. Kept as `static` so x:Bind resolves them at compile time.
    public static Visibility VisibilityIfPositive(int count) =>
        count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility VisibilityIfNotNull(long? value) =>
        value.HasValue ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility VisibilityIfNonEmpty(string? text) =>
        !string.IsNullOrWhiteSpace(text) ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility VisibilityIfMultiple(int count) =>
        count > 1 ? Visibility.Visible : Visibility.Collapsed;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string showId)
            await ViewModel.LoadAsync(showId);
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Rebuild the SelectorBar's items whenever Seasons or SelectedSeason
        // change. SelectorBar doesn't bind ItemsSource directly, so we sync
        // imperatively. Cheap — a show has 1-N seasons, not thousands.
        if (e.PropertyName == nameof(ViewModel.Show) ||
            e.PropertyName == nameof(ViewModel.SelectedSeason))
        {
            SyncSeasonSelector();
        }
        if (e.PropertyName == nameof(ViewModel.Show))
        {
            SyncTmdbButtonState();
            SyncMetaLine();
        }
    }

    /// <summary>
    /// Builds the hero meta line: "S of T seasons · A of B episodes · N unwatched
    /// [· Status] [· ★ Rating]". Falls back to "T seasons" / "B episodes" when
    /// TMDB totals aren't populated yet (pre-Sync state or summary endpoint failed).
    /// </summary>
    private void SyncMetaLine()
    {
        if (ViewModel.Show is not { } show) { MetaLineText.Text = string.Empty; ShyMetaText.Text = string.Empty; return; }

        var seasonsPart = show.TotalSeasonsExpected is { } ts && ts > 0
            ? $"{show.SeasonCount} of {ts} seasons"
            : $"{show.SeasonCount} seasons";
        var episodesPart = show.TotalEpisodesExpected is { } te && te > 0
            ? $"{show.EpisodeCount} of {te} episodes"
            : $"{show.EpisodeCount} episodes";

        var parts = new List<string>
        {
            seasonsPart,
            episodesPart,
            $"{show.UnwatchedCount} unwatched",
        };
        if (!string.IsNullOrWhiteSpace(show.Status))
            parts.Add(show.Status!);
        if (show.VoteAverage is { } va && va > 0)
            parts.Add($"★ {va:F1}");

        MetaLineText.Text = string.Join(" · ", parts);
        // Compact subtitle for the shy pill — drop the rating/status, keep
        // season + unwatched counts so the pill stays readable at 12 px.
        ShyMetaText.Text = $"{seasonsPart} · {show.UnwatchedCount} unwatched";
    }

    private void SyncSeasonSelector()
    {
        SeasonSelector.Items.Clear();

        // Build the union of:
        //   - seasons the user has at least one file for (ViewModel.Seasons)
        //   - seasons TMDB knows the show has (1..TotalSeasonsExpected)
        // so the selector renders all seasons even when only some are on disk.
        // Missing seasons render as disabled placeholder chips — a clear
        // visual signal of "you don't have this one yet".
        var onDiskBySeason = new Dictionary<int, LocalSeason>();
        var maxOnDisk = 0;
        foreach (var s in ViewModel.Seasons)
        {
            onDiskBySeason[s.SeasonNumber] = s;
            if (s.SeasonNumber > maxOnDisk) maxOnDisk = s.SeasonNumber;
        }
        var tmdbTotal = ViewModel.Show?.TotalSeasonsExpected ?? 0;
        var totalToShow = Math.Max(maxOnDisk, tmdbTotal);
        if (totalToShow <= 0) { SeasonSelector.Visibility = Visibility.Collapsed; return; }

        // Make sure SelectedSeason is set to something we'll render — without
        // this, force-resync that adds a new on-disk season can leave Selected
        // pointing at a stale instance.
        if (ViewModel.SelectedSeason is null && ViewModel.Seasons.Count > 0)
            ViewModel.SelectedSeason = ViewModel.Seasons[0];

        for (int n = 1; n <= totalToShow; n++)
        {
            var hasOnDisk = onDiskBySeason.TryGetValue(n, out var season);
            var item = new SelectorBarItem
            {
                Text = $"Season {n}",
                Tag = hasOnDisk ? season : (object)n,           // int sentinel for missing
                IsEnabled = hasOnDisk,                          // disabled = missing
                IsSelected = hasOnDisk && ReferenceEquals(season, ViewModel.SelectedSeason),
            };
            SeasonSelector.Items.Add(item);
        }

        // Show the selector whenever there's anything to show. Single-season
        // shows now also reveal it so the active-season chip is always
        // visible — per the user's "visible which season we are on" ask.
        SeasonSelector.Visibility = Visibility.Visible;
    }

    private void SeasonSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        // Only on-disk items expose a LocalSeason; placeholder (missing)
        // items carry an int sentinel and are IsEnabled=false so the user
        // shouldn't be able to select them, but guard anyway.
        if (sender.SelectedItem is SelectorBarItem item && item.Tag is LocalSeason season)
            ViewModel.SelectedSeason = season;
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        // Hero Play queues every on-disk episode in season order + skips to
        // the first unwatched one (falls back to the first episode when
        // everything has been watched). Sends a single PlayCommand with the
        // whole queue so the playback engine treats it as one context.
        var queue = BuildShowQueue();
        if (queue.Count == 0) return;
        var firstUnwatched = queue.FindIndex(ep => ep.WatchedAt is null);
        PlayQueue(queue, firstUnwatched >= 0 ? firstUnwatched : 0);
    }

    private List<LocalEpisode> BuildShowQueue()
        => ViewModel.Seasons
            .OrderBy(s => s.SeasonNumber)
            .SelectMany(s => s.Episodes.OrderBy(ep => ep.Episode))
            .Where(ep => ep.IsOnDisk && !string.IsNullOrEmpty(ep.TrackUri))
            .ToList();

    private async void MarkAllWatchedButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.MarkAllWatchedAsync(watched: true);
    }

    private async void ForceResync_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Show is not { } show) return;
        var tokenStore = Ioc.Default.GetService<Wavee.Local.Enrichment.ITmdbTokenStore>();
        if (tokenStore?.HasToken != true)
        {
            LocalLibraryPage.OpenLocalEnrichmentSettings();
            return;
        }
        var enrichment = Ioc.Default.GetService<Wavee.Local.Enrichment.ILocalEnrichmentService>();
        if (enrichment is null) return;
        await enrichment.EnqueueShowAsync(show.Id, forceResync: true);
    }

    private void CastMember_CardClick(object? sender, EventArgs e)
    {
        // Tag carries the bound LocalCastMember row (Tag="{x:Bind}" in the
        // DataTemplate). Cast members without a TMDB PersonId (un-enriched
        // roster entries) silently no-op.
        if (sender is not FrameworkElement fe || fe.Tag is not LocalCastMember cast) return;
        if (cast.PersonId is not { } pid) return;
        NavigationHelpers.OpenLocalPersonDetail(
            pid,
            seedName: cast.Name,
            seedImageUri: cast.ProfileImageUri,
            openInNewTab: NavigationHelpers.IsCtrlPressed());
    }

    private void EpisodeCard_PlayRequested(object? sender, LocalEpisode ep)
    {
        // Missing-from-disk episodes have null TrackUri — nothing to play.
        if (string.IsNullOrEmpty(ep.TrackUri)) return;
        // Build the same season-ordered queue as the hero Play button but
        // start at the tapped episode, so "play this one + continue" works.
        var queue = BuildShowQueue();
        var idx = queue.FindIndex(e => string.Equals(e.TrackUri, ep.TrackUri, StringComparison.Ordinal));
        if (idx < 0)
        {
            PlayLocal(ep.TrackUri!);
            return;
        }
        PlayQueue(queue, idx);
    }

    private void EpisodeCard_ContextRequested(object? sender, (LocalEpisode Episode, RightTappedRoutedEventArgs Args) e)
    {
        if (sender is not FrameworkElement fe) return;
        // For missing-from-disk episodes the context menu would be useless
        // (no file path / no track URI). Skip it; the row already handles
        // its own "Not in library" affordance.
        if (string.IsNullOrEmpty(e.Episode.TrackUri) || string.IsNullOrEmpty(e.Episode.FilePath)) return;
        Services.LocalItemContextMenuPresenter.Show(
            fe, e.Args,
            trackUri: e.Episode.TrackUri!,
            filePath: e.Episode.FilePath!,
            kind: Wavee.Local.Classification.LocalContentKind.TvEpisode,
            lastPositionMs: e.Episode.LastPositionMs,
            watchedAt: e.Episode.WatchedAt);
        e.Args.Handled = true;
    }

    private static void PlayLocal(string trackUri)
        => Wavee.UI.WinUI.Services.LocalPlaybackLauncher.PlayOne(trackUri);

    private static void PlayQueue(IReadOnlyList<LocalEpisode> queue, int startIndex)
    {
        if (queue.Count == 0) return;
        Wavee.UI.WinUI.Services.LocalPlaybackLauncher.PlayQueue(
            queue.Select(ep => ep.TrackUri!),
            startIndex,
            contextName: "Local files");
    }
}
