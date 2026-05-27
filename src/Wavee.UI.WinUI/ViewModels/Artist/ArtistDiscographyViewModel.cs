using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Wavee.Core.Http;
using Wavee.UI.Contracts;
using Wavee.UI.Helpers;
using Wavee.UI.Services.Artists;
using Wavee.UI.Services.Infra;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Extensions;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.ViewModels.Artist;

/// <summary>
/// Owns the artist discography surfaces: albums / singles / compilations /
/// appears-on / popular releases. Drives capped projections + "See all"
/// thresholds, background discography pagination via
/// <see cref="DiscographyPaginationService"/>, the album expand/collapse
/// state, and discography-grid scale persistence.
///
/// <para>The spotlight selection (which release feeds the hero card) is
/// derived from this VM's popular releases plus the header VM's pinned
/// item + latest release. The parent invokes
/// <see cref="ComputeSpotlightSelection"/> with header data to keep the
/// children decoupled.</para>
/// </summary>
[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial class ArtistDiscographyViewModel : ObservableObject, IDisposable
{
    private const string AlbumPlaceholderIdPrefix = "album-ph";
    private const string SinglePlaceholderIdPrefix = "single-ph";
    private const string CompilationPlaceholderIdPrefix = "comp-ph";

    private readonly IArtistService _artistService;
    private readonly IAlbumService _albumService;
    private readonly IColorService _colorService;
    private readonly ISettingsService? _settingsService;
    private readonly DiscographyPaginationService _paginationService;
    private readonly SpotlightSelectionService _spotlightSelectionService;
    private readonly IBackgroundWorkRunner _backgroundWork;
    private readonly ILogger? _logger;
    private readonly DispatcherQueue _dispatcherQueue;

    private readonly Func<string?> _artistIdProvider;
    private readonly Func<string?> _artistNameProvider;
    private readonly Func<int, bool> _isCurrentLoadProvider;
    private readonly Func<int> _currentGenerationProvider;

    private CancellationTokenSource? _discoCts;

    // ── Backing data ────────────────────────────────────────────────────────
    private readonly List<LazyReleaseItem> _allReleases = [];

    public ArtistDiscographyViewModel(
        IArtistService artistService,
        IAlbumService albumService,
        IColorService colorService,
        ISettingsService? settingsService,
        DiscographyPaginationService paginationService,
        SpotlightSelectionService spotlightSelectionService,
        IBackgroundWorkRunner backgroundWork,
        ILogger? logger,
        Func<string?> artistIdProvider,
        Func<string?> artistNameProvider,
        Func<int, bool> isCurrentLoadProvider,
        Func<int> currentGenerationProvider)
    {
        _artistService = artistService;
        _albumService = albumService;
        _colorService = colorService;
        _settingsService = settingsService;
        _paginationService = paginationService;
        _spotlightSelectionService = spotlightSelectionService;
        _backgroundWork = backgroundWork;
        _logger = logger;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _artistIdProvider = artistIdProvider;
        _artistNameProvider = artistNameProvider;
        _isCurrentLoadProvider = isCurrentLoadProvider;
        _currentGenerationProvider = currentGenerationProvider;

        // Hydrate the discography card-size scale from persisted settings,
        // clamped to the slider's range so a stale config can't render the
        // grid unusable.
        if (_settingsService != null)
        {
            var saved = _settingsService.Settings.ArtistDiscographyGridScale;
            DiscographyGridScale = saved >= 0.7 && saved <= 1.6 ? saved : 1.0;
        }
    }

    // ── UI-bound collections ────────────────────────────────────────────────

    private readonly ObservableCollection<LazyReleaseItem> _albums = [];
    public IReadOnlyList<LazyReleaseItem> Albums => _albums;

    private readonly ObservableCollection<LazyReleaseItem> _singles = [];
    public IReadOnlyList<LazyReleaseItem> Singles => _singles;

    // Capped projections kept as separate ObservableCollections so the
    // ArtistPage repeaters re-use ReplaceWith's smart in-place diff (single
    // Reset notification) instead of a full re-bind on every paginated
    // append.
    private readonly ObservableCollection<LazyReleaseItem> _albumsCapped = [];
    public IReadOnlyList<LazyReleaseItem> AlbumsCapped => _albumsCapped;

    private readonly ObservableCollection<LazyReleaseItem> _singlesCapped = [];
    public IReadOnlyList<LazyReleaseItem> SinglesCapped => _singlesCapped;

    private readonly ObservableCollection<LazyReleaseItem> _compilations = [];
    public IReadOnlyList<LazyReleaseItem> Compilations => _compilations;

    /// <summary>Appears-On compilations / soundtracks from
    /// <c>relatedContent.appearsOn</c>.</summary>
    private readonly ObservableCollection<LazyReleaseItem> _appearsOn = [];
    public IReadOnlyList<LazyReleaseItem> AppearsOn => _appearsOn;
    public bool HasAppearsOn => _appearsOn.Count > 0;

    /// <summary>Top-played releases for the artist — drives the "Popular releases"
    /// shelf paired with Top Tracks in the V4A composition.</summary>
    private readonly ObservableCollection<LazyReleaseItem> _popularReleases = [];
    public IReadOnlyList<LazyReleaseItem> PopularReleases => _popularReleases;
    public bool HasPopularReleases => _popularReleases.Count > 0;

    // ── Scalars ─────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAlbums))]
    [NotifyPropertyChangedFor(nameof(ShowAlbumsSeeAllTile))]
    [NotifyPropertyChangedFor(nameof(AlbumsSeeAllLabel))]
    public partial int AlbumsTotalCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSingles))]
    [NotifyPropertyChangedFor(nameof(ShowSinglesSeeAllTile))]
    [NotifyPropertyChangedFor(nameof(SinglesSeeAllLabel))]
    public partial int SinglesTotalCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCompilations))]
    public partial int CompilationsTotalCount { get; set; }

    public bool HasAlbums => AlbumsTotalCount > 0;
    public bool HasSingles => SinglesTotalCount > 0;
    public bool HasCompilations => CompilationsTotalCount > 0;

    public bool ShowAlbumsSeeAllTile => DiscographyPaginationService.ShouldCap(AlbumsTotalCount);
    public bool ShowSinglesSeeAllTile => DiscographyPaginationService.ShouldCap(SinglesTotalCount);

    public string AlbumsSeeAllLabel => $"See all {AlbumsTotalCount} albums";
    public string SinglesSeeAllLabel => $"See all {SinglesTotalCount} singles";

    [ObservableProperty] public partial double DiscographyGridScale { get; set; } = 1.0;

    [ObservableProperty] public partial bool HasAlbumsError { get; set; }
    [ObservableProperty] public partial bool HasSinglesError { get; set; }
    [ObservableProperty] public partial bool HasCompilationsError { get; set; }

    [ObservableProperty] public partial LazyReleaseItem? ExpandedAlbum { get; set; }
    [ObservableProperty] public partial ObservableCollection<LazyTrackItem> ExpandedAlbumTracks { get; set; } = [];
    [ObservableProperty] public partial bool IsLoadingExpandedTracks { get; set; }

    partial void OnDiscographyGridScaleChanged(double value)
    {
        // Mirror Library's GridScale persistence — clamp to slider range to
        // protect against out-of-bounds writes from external callers.
        var clamped = Math.Clamp(value, 0.7, 1.6);
        _settingsService?.Update(s => s.ArtistDiscographyGridScale = clamped);
    }

    // ── Reset ───────────────────────────────────────────────────────────────

    public void ResetForNewArtist()
    {
        ExpandedAlbum = null;
        ExpandedAlbumTracks.Clear();
        _allReleases.Clear();
        _albums.Clear();
        _singles.Clear();
        _compilations.Clear();
        _albumsCapped.Clear();
        _singlesCapped.Clear();
        _appearsOn.Clear();
        _popularReleases.Clear();
        HasAlbumsError = false;
        HasSinglesError = false;
        HasCompilationsError = false;
        AlbumsTotalCount = 0;
        SinglesTotalCount = 0;
        CompilationsTotalCount = 0;
        OnPropertyChanged(nameof(HasAppearsOn));
        OnPropertyChanged(nameof(HasPopularReleases));
        CancelAndDisposeDiscographyCts();
    }

    // ── Initial overview apply ──────────────────────────────────────────────

    /// <summary>
    /// Apply the discography section of the artist overview. Replaces the
    /// album / single / compilation / appears-on / popular-releases
    /// collections in one batch and recomputes the capped projections.
    /// </summary>
    public void ApplyOverview(ArtistOverviewResult overview)
    {
        // Drop any prior batch's totals first so the seed math below uses
        // the freshly-reported counts. Set through the generated property
        // setters so the [NotifyPropertyChangedFor] dependents (HasAlbums /
        // ShowSeeAllTile / etc.) fire automatically.
        AlbumsTotalCount = overview.AlbumsTotalCount;
        SinglesTotalCount = overview.SinglesTotalCount;
        CompilationsTotalCount = overview.CompilationsTotalCount;

        _allReleases.Clear();
        AddReleasesToList(overview.Albums, "ALBUM", AlbumPlaceholderIdPrefix, overview.AlbumsTotalCount);
        AddReleasesToList(overview.Singles, "SINGLE", SinglePlaceholderIdPrefix, overview.SinglesTotalCount);
        AddReleasesToList(overview.Compilations, "COMPILATION", CompilationPlaceholderIdPrefix, overview.CompilationsTotalCount);
        DispatchReleases();

        // Popular releases (batch swap) — same shape as Albums but ranked by
        // play count rather than newest-first.
        int popIdx = 0;
        _popularReleases.ReplaceWith(overview.PopularReleases.Select(r =>
            LazyReleaseItem.Loaded(r.Id, popIdx++, new ArtistReleaseVm
            {
                Id = r.Id,
                Uri = r.Uri,
                Name = r.Name,
                Type = r.Type,
                ImageUrl = r.ImageUrl,
                ReleaseDate = r.ReleaseDate,
                TrackCount = r.TrackCount,
                Label = r.Label,
                Year = r.Year
            })));

        // Appears On (batch swap) — same shape as Compilations.
        int appearsIdx = 0;
        _appearsOn.ReplaceWith(overview.AppearsOn.Select(r =>
            LazyReleaseItem.Loaded(r.Id, appearsIdx++, new ArtistReleaseVm
            {
                Id = r.Id,
                Uri = r.Uri,
                Name = r.Name,
                Type = r.Type,
                ImageUrl = r.ImageUrl,
                ReleaseDate = r.ReleaseDate,
                TrackCount = r.TrackCount,
                Label = r.Label,
                Year = r.Year
            })));

        OnPropertyChanged(nameof(HasAppearsOn));
        OnPropertyChanged(nameof(HasPopularReleases));
    }

    /// <summary>Snapshot of the discography releases the parent supplies to
    /// the color-prefetch helper after the initial apply.</summary>
    public IReadOnlyList<ArtistReleaseVm> SnapshotLoadedReleases()
        => _allReleases
            .Where(item => item.IsLoaded && item.Data != null)
            .Select(item => item.Data!)
            .ToList();

    /// <summary>
    /// Count of items currently loaded for the given group. Used by the
    /// parent to compute the start offset for the background pagination
    /// retry path.
    /// </summary>
    public int LoadedCount(string type) => type switch
    {
        "ALBUM" => _albums.Count(a => a.IsLoaded),
        "SINGLE" => _singles.Count(s => s.IsLoaded),
        "COMPILATION" => _compilations.Count(c => c.IsLoaded),
        _ => 0,
    };

    /// <summary>
    /// Compute the spotlight selection by combining this VM's popular
    /// releases with the header VM's pinned item / latest release. Pure
    /// pass-through to <see cref="SpotlightSelectionService"/>.
    /// </summary>
    public SpotlightSelection ComputeSpotlightSelection(
        ArtistPinnedItemResult? pinnedItem,
        ArtistLatestReleaseResult? latestRelease)
    {
        var popular = _popularReleases
            .Where(r => r.IsLoaded && r.Data is not null)
            .Select(r => new SpotlightPopularRelease(
                Uri: r.Data!.Uri,
                Name: r.Data.Name,
                ImageUrl: r.Data.ImageUrl,
                Type: r.Data.Type,
                Year: r.Data.Year,
                TrackCount: r.Data.TrackCount))
            .ToList();

        return _spotlightSelectionService.Select(new SpotlightSelectionInputs(
            PinnedItem: pinnedItem,
            LatestRelease: latestRelease,
            PopularReleases: popular));
    }

    /// <summary>
    /// Distributes <see cref="_allReleases"/> into Albums, Singles,
    /// Compilations collections.
    /// </summary>
    private void DispatchReleases()
    {
        var albums = new List<LazyReleaseItem>();
        var singles = new List<LazyReleaseItem>();
        var compilations = new List<LazyReleaseItem>();

        foreach (var group in _allReleases
            .GroupBy(r => r.Data?.Type ?? InferTypeFromId(r.Id))
            .OrderBy(g => g.Key))
        {
            var sorted = group.OrderByDescending(r => r.Data?.ReleaseDate ?? DateTimeOffset.MinValue);
            var target = group.Key switch
            {
                "ALBUM" => albums,
                "SINGLE" => singles,
                "COMPILATION" => compilations,
                _ => null
            };

            if (target == null) continue;
            target.AddRange(sorted);
        }

        _albums.ReplaceWith(albums);
        _singles.ReplaceWith(singles);
        _compilations.ReplaceWith(compilations);

        SyncCappedDiscographyProjections();
    }

    /// <summary>
    /// Mirror <see cref="_albums"/> / <see cref="_singles"/> into the capped
    /// projections that <c>ArtistPage</c> binds against.
    /// </summary>
    private void SyncCappedDiscographyProjections()
    {
        var albumsCap = DiscographyPaginationService.ResolveCap(AlbumsTotalCount);
        var singlesCap = DiscographyPaginationService.ResolveCap(SinglesTotalCount);
        _albumsCapped.ReplaceWith(_albums.Take(albumsCap));
        _singlesCapped.ReplaceWith(_singles.Take(singlesCap));
    }

    private static string InferTypeFromId(string id)
    {
        if (id.StartsWith(AlbumPlaceholderIdPrefix)) return "ALBUM";
        if (id.StartsWith(SinglePlaceholderIdPrefix)) return "SINGLE";
        if (id.StartsWith(CompilationPlaceholderIdPrefix)) return "COMPILATION";
        return "ALBUM";
    }

    private void AddReleasesToList(
        IReadOnlyList<ArtistReleaseResult> releases,
        string type,
        string phPrefix,
        int totalCount)
    {
        int count = 0;
        foreach (var r in releases)
        {
            var vm = new ArtistReleaseVm
            {
                Id = r.Id,
                Uri = r.Uri,
                Name = r.Name,
                Type = type,
                ImageUrl = r.ImageUrl,
                ReleaseDate = r.ReleaseDate,
                TrackCount = r.TrackCount,
                Label = r.Label,
                Year = r.Year
            };
            _allReleases.Add(LazyReleaseItem.Loaded(vm.Id, count, vm));
            count++;
        }

        var maxPlaceholders = DiscographyPaginationService.CountPlaceholderSlots(count, totalCount);
        for (int i = count; i < count + maxPlaceholders; i++)
            _allReleases.Add(LazyReleaseItem.Placeholder(DiscographyPaginationService.PlaceholderId(phPrefix, i), i));
    }

    // ── Color prefetch ──────────────────────────────────────────────────────

    /// <summary>
    /// Backfill <see cref="ArtistReleaseVm.ColorHex"/> for the supplied
    /// release VMs from the shared color service. Idempotent on already-
    /// colored entries; safe to call from background work.
    /// </summary>
    public async Task PrefetchReleaseColorsAsync(
        string artistId,
        int generation,
        IEnumerable<ArtistReleaseVm> releases)
    {
        var releasesByUrl = new Dictionary<string, List<ArtistReleaseVm>>(StringComparer.Ordinal);

        foreach (var release in releases)
        {
            if (!string.IsNullOrEmpty(release.ColorHex))
                continue;

            var imageUrl = SpotifyImageHelper.ToHttpsUrl(release.ImageUrl) ?? release.ImageUrl;
            if (string.IsNullOrWhiteSpace(imageUrl))
                continue;

            if (!releasesByUrl.TryGetValue(imageUrl, out var mapped))
            {
                mapped = [];
                releasesByUrl[imageUrl] = mapped;
            }

            mapped.Add(release);
        }

        if (releasesByUrl.Count == 0)
            return;

        try
        {
            var colors = await _colorService
                .GetColorsAsync(releasesByUrl.Keys.ToList())
                .ConfigureAwait(false);

            if (colors.Count == 0)
                return;

            if (!_isCurrentLoadProvider(generation))
                return;

            _dispatcherQueue.TryEnqueue(() =>
            {
                if (!_isCurrentLoadProvider(generation))
                    return;

                foreach (var (url, mappedReleases) in releasesByUrl)
                {
                    if (!colors.TryGetValue(url, out var color))
                        continue;

                    var hex = color.DarkHex ?? color.RawHex ?? color.LightHex;
                    if (string.IsNullOrEmpty(hex))
                        continue;

                    foreach (var release in mappedReleases)
                    {
                        if (string.IsNullOrEmpty(release.ColorHex))
                            release.ColorHex = hex;
                    }
                }
            });
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to prefetch artist release colors for {Count} images", releasesByUrl.Count);
        }
    }

    // ── Background discography pagination ───────────────────────────────────

    /// <summary>
    /// Create a fresh <see cref="CancellationTokenSource"/> for the
    /// background discography paging loop, cancelling any previous one.
    /// </summary>
    public CancellationToken CreateFreshDiscographyToken()
    {
        CancelAndDisposeDiscographyCts();
        _discoCts = new CancellationTokenSource();
        return _discoCts.Token;
    }

    private void CancelAndDisposeDiscographyCts()
    {
        var cts = Interlocked.Exchange(ref _discoCts, null);
        if (cts == null) return;

        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }
        cts.Dispose();
    }

    /// <summary>Cancel any inflight discography pagination work without
    /// otherwise disposing the VM. Called from <c>ArtistViewModel.Hibernate</c>
    /// to release the background fetch loop while keeping the VM alive for
    /// re-activation.</summary>
    public void CancelInflightWork() => CancelAndDisposeDiscographyCts();

    /// <summary>
    /// Fetch the remaining discography pages for every group that's not yet
    /// complete, then apply each page back into <c>_allReleases</c> on the
    /// dispatcher. Mirrors the legacy
    /// <c>FetchRemainingDiscographyAsync</c> behaviour: on per-group failure,
    /// strip the unloaded placeholders for that group and set the
    /// corresponding <c>HasXError</c> flag.
    /// </summary>
    public async Task FetchRemainingDiscographyAsync(
        string artistUri,
        int generation,
        int albumsLoaded, int albumsTotal,
        int singlesLoaded, int singlesTotal,
        int compilationsLoaded, int compilationsTotal,
        CancellationToken ct)
    {
        IReadOnlyList<DiscographyGroupFetch> results;
        try
        {
            results = await _paginationService.FetchRemainingDiscographyAsync(
                artistUri,
                albumsLoaded, albumsTotal,
                singlesLoaded, singlesTotal,
                compilationsLoaded, compilationsTotal,
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Discography orchestration failed for {ArtistId}", artistUri);
            return;
        }

        foreach (var groupResult in results)
        {
            if (ct.IsCancellationRequested || !_isCurrentLoadProvider(generation))
                return;

            if (groupResult.Failed)
            {
                await HandleGroupFetchErrorAsync(groupResult, artistUri, generation, ct);
                continue;
            }

            if (groupResult.Pages.Count == 0)
                continue;

            var createdReleaseVms = new List<ArtistReleaseVm>();
            var tcs = new TaskCompletionSource();
            _dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (ct.IsCancellationRequested || !_isCurrentLoadProvider(generation))
                    {
                        tcs.SetResult();
                        return;
                    }

                    foreach (var page in groupResult.Pages)
                    {
                        int i = page.Offset;
                        foreach (var r in page.Items)
                        {
                            var vm = new ArtistReleaseVm
                            {
                                Id = r.Id,
                                Uri = r.Uri,
                                Name = r.Name,
                                Type = groupResult.Type,
                                ImageUrl = r.ImageUrl,
                                ReleaseDate = r.ReleaseDate,
                                TrackCount = r.TrackCount,
                                Label = r.Label,
                                Year = r.Year
                            };
                            createdReleaseVms.Add(vm);

                            var phKey = DiscographyPaginationService.PlaceholderId(groupResult.PlaceholderPrefix, i);
                            var existing = _allReleases.FirstOrDefault(x => x.Id == phKey);
                            if (existing != null)
                                existing.Populate(vm);
                            else
                                _allReleases.Add(LazyReleaseItem.Loaded(r.Id, i, vm));
                            i++;
                        }
                    }
                    DispatchReleases();
                    tcs.SetResult();
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try { await tcs.Task; }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Discography apply failed for {Type} on {ArtistId}", groupResult.Type, artistUri);
                continue;
            }

            if (_isCurrentLoadProvider(generation))
                _backgroundWork.Run(_ => PrefetchReleaseColorsAsync(artistUri, generation, createdReleaseVms),
                    "ArtistDiscographyViewModel.PrefetchReleaseColors.Discography");
        }
    }

    private Task HandleGroupFetchErrorAsync(DiscographyGroupFetch result, string artistUri, int generation, CancellationToken ct)
    {
        _logger?.LogWarning("Discography {Type} fetch failed for {ArtistId}", result.Type, artistUri);

        var tcsCleanup = new TaskCompletionSource();
        _dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (!_isCurrentLoadProvider(generation))
                {
                    tcsCleanup.SetResult();
                    return;
                }

                _allReleases.RemoveAll(i => !i.IsLoaded && i.Id.StartsWith(result.PlaceholderPrefix));
                DispatchReleases();

                switch (result.Type)
                {
                    case "ALBUM": HasAlbumsError = true; break;
                    case "SINGLE": HasSinglesError = true; break;
                    case "COMPILATION": HasCompilationsError = true; break;
                }

                tcsCleanup.SetResult();
            }
            catch (Exception cleanupEx) { tcsCleanup.SetException(cleanupEx); }
        });

        return tcsCleanup.Task.ContinueWith(t =>
        {
            if (t.IsFaulted)
                _logger?.LogDebug(t.Exception, "Discography cleanup failed (non-critical)");
        }, TaskScheduler.Default);
    }

    // ── Commands ────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ExpandAlbum(LazyReleaseItem? album)
    {
        if (album == null || !album.IsLoaded || album.Data == null) return;

        if (ExpandedAlbum?.Id == album.Id)
        {
            CollapseAlbum();
            return;
        }

        ExpandedAlbum = album;
        IsLoadingExpandedTracks = true;
        ExpandedAlbumTracks.Clear();

        var trackCount = album.Data.TrackCount;
        if (trackCount <= 0)
        {
            trackCount = album.Data.Type switch
            {
                "SINGLE" => 2,
                "COMPILATION" => 20,
                _ => 12
            };
        }

        for (int i = 0; i < trackCount; i++)
            ExpandedAlbumTracks.Add(LazyTrackItem.Placeholder($"expanded-{i}", i + 1));

        try
        {
            var albumUri = album.Data.Uri ?? $"spotify:album:{album.Data.Id}";
            var tracks = await _albumService.GetTracksAsync(albumUri);

            // Collapsed, or switched to another album, while the fetch was in
            // flight — drop this result so it can't overwrite the live one.
            if (ExpandedAlbum != album)
                return;

            for (int i = 0; i < Math.Min(tracks.Count, ExpandedAlbumTracks.Count); i++)
                ExpandedAlbumTracks[i] = LazyTrackItem.Loaded(tracks[i].Id, i + 1, tracks[i]);

            while (ExpandedAlbumTracks.Count > tracks.Count)
                ExpandedAlbumTracks.RemoveAt(ExpandedAlbumTracks.Count - 1);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load album tracks for {AlbumUri}", album.Data.Uri);
        }
        finally
        {
            if (ExpandedAlbum == album)
                IsLoadingExpandedTracks = false;
        }
    }

    [RelayCommand]
    private void CollapseAlbum()
    {
        ExpandedAlbum = null;
        IsLoadingExpandedTracks = false;
    }

    [RelayCommand]
    private async Task RetryDiscographyAsync()
    {
        var albumsLoaded = LoadedCount("ALBUM");
        var singlesLoaded = LoadedCount("SINGLE");
        var compilationsLoaded = LoadedCount("COMPILATION");

        HasAlbumsError = false;
        HasSinglesError = false;
        HasCompilationsError = false;

        var artistId = _artistIdProvider();
        if (string.IsNullOrEmpty(artistId))
            return;

        var generation = _currentGenerationProvider();
        var ct = CreateFreshDiscographyToken();

        await Task.Run(() => FetchRemainingDiscographyAsync(
            artistId, generation,
            albumsLoaded, AlbumsTotalCount,
            singlesLoaded, SinglesTotalCount,
            compilationsLoaded, CompilationsTotalCount,
            ct), ct);
    }

    /// <summary>Re-raise <see cref="HasPopularReleases"/> and the spotlight
    /// dependents after the parent has mutated popular releases. Spotlight
    /// projection is recomputed by the parent.</summary>
    public void NotifyPopularReleasesChanged()
    {
        OnPropertyChanged(nameof(HasPopularReleases));
    }

    public void Dispose()
    {
        CancelAndDisposeDiscographyCts();
    }
}
