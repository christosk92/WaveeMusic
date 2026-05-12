using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Wavee.Local.Models;
using Wavee.UI.Library.Local;

namespace Wavee.UI.WinUI.ViewModels.Local;

/// <summary>
/// View-model for <c>LocalLandingPage</c> — the new rail-based local-files
/// landing page replacing the old single-list <c>LocalLibraryViewModel</c>.
/// </summary>
public sealed partial class LocalLandingViewModel : ObservableObject, IDisposable
{
    private readonly ILocalLibraryFacade? _facade;
    private readonly DispatcherQueue _dispatcher;
    private readonly ILogger? _logger;
    private readonly IDisposable? _changesSub;
    private readonly IDisposable? _enrichmentSub;
    private readonly IDisposable? _tokenSub;
    private readonly Wavee.UI.WinUI.Data.Contracts.ISettingsService? _settings;

    public ObservableCollection<LocalContinueItem> Continue { get; } = new();
    public ObservableCollection<LocalShow>         Shows    { get; } = new();
    public ObservableCollection<LocalMovie>        Movies   { get; } = new();
    public ObservableCollection<LocalMusicVideo>   MusicVideos { get; } = new();
    public ObservableCollection<LocalCollection>   Collections { get; } = new();
    public ObservableCollection<LocalOtherItem>    Others   { get; } = new();
    public ObservableCollection<Wavee.Local.LocalTrackRow> RecentlyAdded   { get; } = new();
    public ObservableCollection<Wavee.Local.LocalTrackRow> RecentlyPlayed  { get; } = new();
    public ObservableCollection<Wavee.Local.LocalTrackRow> LikedTracks     { get; } = new();

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private int _showCount;
    [ObservableProperty] private int _movieCount;
    [ObservableProperty] private int _musicCount;
    [ObservableProperty] private int _musicVideoCount;
    [ObservableProperty] private int _otherCount;
    [ObservableProperty] private string? _enrichmentRibbon;
    [ObservableProperty] private bool _hasEnrichmentProgress;
    [ObservableProperty] private bool _hasContinue;
    [ObservableProperty] private bool _hasShows;
    [ObservableProperty] private bool _hasMovies;
    [ObservableProperty] private bool _hasMusic;
    [ObservableProperty] private bool _hasMusicVideos;
    [ObservableProperty] private bool _hasOthers;
    [ObservableProperty] private bool _hasCollections;

    /// <summary>
    /// True when the user hasn't configured a TMDB token yet AND there's
    /// at least one show or movie indexed AND they haven't dismissed the
    /// teaser. Drives the landing-page InfoBar.
    /// </summary>
    [ObservableProperty] private bool _showTmdbCta;

    public LocalLandingViewModel(
        ILocalLibraryFacade? facade = null,
        ILogger<LocalLandingViewModel>? logger = null)
    {
        _facade = facade;
        _logger = logger;
        _dispatcher = DispatcherQueue.GetForCurrentThread()
                      ?? throw new InvalidOperationException("LocalLandingViewModel must be constructed on a UI thread.");

        if (_facade is not null)
        {
            _changesSub = _facade.Changes.Subscribe(__change => _dispatcher.TryEnqueue(() => _ = LoadAsync()));
            _enrichmentSub = _facade.EnrichmentProgress.Subscribe(p =>
                _dispatcher.TryEnqueue(() =>
                {
                    HasEnrichmentProgress = p.Pending > 0;
                    EnrichmentRibbon = p.Pending > 0
                        ? $"Matching {p.Matched + p.NoMatch + p.Failed} of {p.Pending + p.Matched + p.NoMatch + p.Failed}…"
                        : null;
                }));
        }

        // Recompute the CTA when the token is added / cleared. The settings
        // service is read once for the dismissal flag — the InfoBar's Closed
        // handler updates it + MarkTmdbTeaserDismissed flips the local copy.
        _settings = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
            .GetService<Wavee.UI.WinUI.Data.Contracts.ISettingsService>();
        var tokenStore = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
            .GetService<Wavee.Local.Enrichment.ITmdbTokenStore>();
        if (tokenStore is not null)
        {
            _tokenSub = tokenStore.HasTokenChanged.Subscribe(_ =>
                _dispatcher.TryEnqueue(RecomputeShowTmdbCta));
        }
        RecomputeShowTmdbCta();
    }

    private void RecomputeShowTmdbCta()
    {
        var tokenStore = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
            .GetService<Wavee.Local.Enrichment.ITmdbTokenStore>();
        var hasToken = tokenStore?.HasToken ?? false;
        var hasIndexedAv = ShowCount > 0 || MovieCount > 0;
        var dismissed = _settings?.Settings.TmdbTeaserDismissed ?? false;
        ShowTmdbCta = !hasToken && hasIndexedAv && !dismissed;
    }

    /// <summary>
    /// Called by the View when the user X-dismisses the landing-page InfoBar.
    /// Flips the local CTA flag immediately so the InfoBar's closing animation
    /// doesn't reopen on the next bind tick.
    /// </summary>
    public void MarkTmdbTeaserDismissed() => ShowTmdbCta = false;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_facade is null)
        {
            _dispatcher.TryEnqueue(() => { IsLoading = false; IsEmpty = true; });
            return;
        }

        _dispatcher.TryEnqueue(() => IsLoading = true);
        try
        {
            // Parallel fetch — each query opens its own connection.
            var t1 = _facade.GetContinueWatchingAsync(20, ct);
            var t2 = _facade.GetShowsAsync(ct);
            var t3 = _facade.GetMoviesAsync(ct);
            var t4 = _facade.GetMusicVideosAsync(ct);
            var t5 = _facade.GetOthersAsync(ct);
            var t6 = _facade.GetRecentlyAddedAsync(30, ct);
            var t7 = _facade.GetRecentlyPlayedAsync(30, ct);
            var t8 = _facade.GetLikedTracksAsync(ct);
            var t9 = _facade.GetCollectionsAsync(ct);

            await Task.WhenAll(t1, t2, t3, t4, t5, t6, t7, t8, t9);

            _dispatcher.TryEnqueue(() =>
            {
                ReplaceAll(Continue, t1.Result);
                ReplaceAll(Shows, t2.Result);
                ReplaceAll(Movies, t3.Result);
                ReplaceAll(MusicVideos, t4.Result);
                ReplaceAll(Others, t5.Result);
                ReplaceAll(RecentlyAdded, t6.Result);
                ReplaceAll(RecentlyPlayed, t7.Result);
                ReplaceAll(LikedTracks, t8.Result);
                ReplaceAll(Collections, t9.Result);

                ShowCount       = Shows.Count;
                MovieCount      = Movies.Count;
                MusicVideoCount = MusicVideos.Count;
                MusicCount      = RecentlyAdded.Count;
                OtherCount      = Others.Count;

                HasContinue     = Continue.Count > 0;
                HasShows        = Shows.Count > 0;
                HasMovies       = Movies.Count > 0;
                HasMusicVideos  = MusicVideos.Count > 0;
                HasMusic        = RecentlyAdded.Count > 0;
                HasOthers       = Others.Count > 0;
                HasCollections  = Collections.Count > 0;

                IsEmpty = ShowCount == 0 && MovieCount == 0 && MusicVideoCount == 0
                          && MusicCount == 0 && OtherCount == 0 && Continue.Count == 0;
                IsLoading = false;
                RecomputeShowTmdbCta();
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "LocalLanding load failed");
            _dispatcher.TryEnqueue(() => { IsLoading = false; });
        }
    }

    private static void ReplaceAll<T>(ObservableCollection<T> target, IReadOnlyList<T> source)
    {
        target.Clear();
        foreach (var x in source) target.Add(x);
    }

    public void Dispose()
    {
        _changesSub?.Dispose();
        _enrichmentSub?.Dispose();
        _tokenSub?.Dispose();
    }
}
