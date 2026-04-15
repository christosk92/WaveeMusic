using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Wavee.Core.Http;
using Wavee.Core.Http.Pathfinder;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// View model for the track/artist details right panel.
/// Subscribes to <see cref="IPlaybackStateService"/> for track changes,
/// fetches data via the queryNpvArtist Pathfinder endpoint.
/// </summary>
public sealed partial class TrackDetailsViewModel : ObservableObject, IDisposable
{
    private readonly IPlaybackStateService _playbackState;
    private readonly IPathfinderClient _pathfinder;
    private readonly ITrackCreditsService _creditsService;
    private readonly IMediaOverrideService _mediaOverrideService;
    private readonly ILogger? _logger;

    private string? _loadedTrackId;
    private CancellationTokenSource? _fetchCts;
    private bool _disposed;

    // ── Loading state ──

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasData;

    [ObservableProperty]
    private string? _errorMessage;

    // ── Artist section ──

    [ObservableProperty]
    private string? _artistName;

    [ObservableProperty]
    private string? _artistAvatarUrl;

    [ObservableProperty]
    private bool _isVerified;

    [ObservableProperty]
    private string? _followers;

    [ObservableProperty]
    private string? _monthlyListeners;

    [ObservableProperty]
    private string? _biographyText;

    [ObservableProperty]
    private bool _isBioExpanded;

    [ObservableProperty]
    private string? _artistUri;

    [ObservableProperty]
    private List<ExternalLinkVm>? _externalLinks;

    // ── Credits section ──

    [ObservableProperty]
    private ObservableCollection<CreditGroupVm> _creditGroups = [];

    [ObservableProperty]
    private string? _recordLabel;

    // ── Concerts section ──

    [ObservableProperty]
    private ObservableCollection<ConcertVm> _concerts = [];

    [ObservableProperty]
    private bool _hasConcerts;

    // ── Canvas section ──

    [ObservableProperty]
    private string? _canvasUrl;

    [ObservableProperty]
    private string? _upstreamCanvasUrl;

    [ObservableProperty]
    private string? _pendingCanvasUrl;

    [ObservableProperty]
    private string? _canvasType;

    [ObservableProperty]
    private bool _hasCanvas;

    [ObservableProperty]
    private bool _hasPendingCanvasUpdate;

    [ObservableProperty]
    private bool _isUsingCanvasSnapshot;

    [ObservableProperty]
    private bool _isManualCanvasOverride;

    [ObservableProperty]
    private string? _currentTrackUri;

    // ── Related Videos section ──

    [ObservableProperty]
    private ObservableCollection<RelatedVideoVm> _relatedVideos = [];

    [ObservableProperty]
    private bool _hasRelatedVideos;

    public IPlaybackStateService PlaybackState => _playbackState;

    public TrackDetailsViewModel(
        IPlaybackStateService playbackState,
        IPathfinderClient pathfinder,
        ITrackCreditsService creditsService,
        IMediaOverrideService mediaOverrideService,
        ILogger<TrackDetailsViewModel>? logger = null)
    {
        _playbackState = playbackState;
        _pathfinder = pathfinder;
        _creditsService = creditsService;
        _mediaOverrideService = mediaOverrideService;
        _logger = logger;

        _playbackState.PropertyChanged += OnPlaybackStateChanged;
    }

    private void OnPlaybackStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IPlaybackStateService.CurrentTrackId)
            or nameof(IPlaybackStateService.CurrentArtistId))
        {
            // Both track and artist must be set before we can fetch
            if (!string.IsNullOrEmpty(_playbackState.CurrentTrackId)
                && !string.IsNullOrEmpty(_playbackState.CurrentArtistId))
            {
                _ = DeferredLoadDetailsAsync();
            }
        }
    }

    private async Task DeferredLoadDetailsAsync()
    {
        await Task.Yield(); // Let the current dispatch frame render first
        await LoadDetailsAsync();
    }

    [RelayCommand]
    private void ToggleBioExpanded()
    {
        IsBioExpanded = !IsBioExpanded;
    }

    public async Task LoadDetailsAsync()
    {
        var trackId = _playbackState.CurrentTrackId;
        var artistId = _playbackState.CurrentArtistId;

        if (string.IsNullOrEmpty(trackId) || string.IsNullOrEmpty(artistId))
        {
            ClearData();
            return;
        }

        if (trackId == _loadedTrackId) return;
        _loadedTrackId = trackId;

        _fetchCts?.Cancel();
        _fetchCts = new CancellationTokenSource();
        var ct = _fetchCts.Token;

        IsLoading = true;
        HasData = false;
        ErrorMessage = null;

        try
        {
            // CurrentArtistId is already in "spotify:artist:xxx" format
            // CurrentTrackId is the bare ID, needs "spotify:track:" prefix
            var trackUri = trackId.StartsWith("spotify:track:", StringComparison.Ordinal)
                ? trackId
                : $"spotify:track:{trackId}";

            // Fetch NpvArtist and credits in parallel
            var npvTask = _pathfinder.GetNpvArtistAsync(artistId, trackUri, ct: ct);
            var creditsTask = _creditsService.GetCreditsAsync(trackUri, ct);

            await Task.WhenAll(npvTask, creditsTask);
            if (ct.IsCancellationRequested) return;

            await MapResponseAsync(npvTask.Result, trackUri, ct);
            MapCredits(creditsTask.Result);
            HasData = true;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug("[TrackDetails] Load cancelled for {TrackId}", trackId);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load details for track {TrackId}", trackId);
            ErrorMessage = AppLocalization.GetString("TrackDetails_LoadFailed");
        }
        finally
        {
            if (!ct.IsCancellationRequested)
                IsLoading = false;
        }
    }

    private async Task MapResponseAsync(NpvArtistResponse response, string trackUri, CancellationToken ct)
    {
        var artist = response.Data?.ArtistUnion;
        var track = response.Data?.TrackUnion;

        // ── Artist ──
        ArtistName = artist?.Profile?.Name;
        ArtistUri = artist?.Uri;
        IsVerified = artist?.Profile?.Verified ?? false;
        Followers = FormatNumber(artist?.Stats?.Followers ?? 0);
        MonthlyListeners = FormatNumber(artist?.Stats?.MonthlyListeners ?? 0);
        BiographyText = StripHtml(artist?.Profile?.Biography?.Text);
        IsBioExpanded = false;

        ArtistAvatarUrl = artist?.Visuals?.AvatarImage?.Sources
            ?.OrderByDescending(s => s.Width ?? 0)
            .FirstOrDefault()?.Url;

        ExternalLinks = artist?.Profile?.ExternalLinks?.Items?
            .Select(l => new ExternalLinkVm { Name = l.Name, Url = l.Url })
            .ToList();

        // Credits + record label are now set by MapCredits() from the dedicated service

        // ── Canvas ──
        CanvasType = track?.Canvas?.Type;
        await ApplyCanvasOverrideStateAsync(trackUri, track?.Canvas?.Url, ct);

        // ── Concerts ──
        var concertList = new ObservableCollection<ConcertVm>();
        if (artist?.Goods?.Concerts?.Items is { Count: > 0 } concertItems)
        {
            foreach (var item in concertItems.Take(5))
            {
                var data = item.Data;
                if (data == null) continue;
                concertList.Add(new ConcertVm
                {
                    Title = data.Title,
                    Venue = data.Location?.Name,
                    City = data.Location?.City,
                    DateFormatted = FormatConcertDate(data.StartDateIsoString),
                    Uri = data.Uri,
                });
            }
        }
        Concerts = concertList;
        HasConcerts = Concerts.Count > 0;

        // ── Related Videos ──
        var videos = new ObservableCollection<RelatedVideoVm>();
        if (track?.RelatedVideos?.Items is { Count: > 0 } videoItems)
        {
            foreach (var v in videoItems.Take(10))
            {
                var vData = v.TrackOfVideo?.Data;
                if (vData == null) continue;
                videos.Add(new RelatedVideoVm
                {
                    Title = vData.Name,
                    ThumbnailUrl = vData.AlbumOfTrack?.CoverArt?.Sources
                        ?.FirstOrDefault()?.Url,
                    ArtistName = vData.Artists?.Items?.FirstOrDefault()?.Profile?.Name,
                    Uri = v.Uri,
                });
            }
        }
        RelatedVideos = videos;
        HasRelatedVideos = RelatedVideos.Count > 0;
    }

    private async Task ApplyCanvasOverrideStateAsync(string trackUri, string? upstreamCanvasUrl, CancellationToken ct)
    {
        var resolved = await _mediaOverrideService.ResolveTrackCanvasAsync(trackUri, upstreamCanvasUrl, ct);
        if (ct.IsCancellationRequested)
            return;

        ApplyResolvedCanvasState(trackUri, resolved);
    }

    private void ApplyResolvedCanvasState(string trackUri, ResolvedMediaOverrideResult resolved)
    {
        CurrentTrackUri = trackUri;
        UpstreamCanvasUrl = resolved.UpstreamAssetUrl;
        PendingCanvasUrl = resolved.PendingAssetUrl;
        HasPendingCanvasUpdate = resolved.HasPendingUpdate;
        IsUsingCanvasSnapshot = resolved.IsUsingLocalSnapshot;
        IsManualCanvasOverride = resolved.IsManualOverride;
        CanvasUrl = resolved.EffectiveAssetUrl;
        HasCanvas = !string.IsNullOrEmpty(CanvasUrl);
    }

    public async Task SetManualCanvasUrlAsync(string canvasUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(CurrentTrackUri))
            return;

        var resolved = await _mediaOverrideService.SetManualTrackCanvasUrlAsync(CurrentTrackUri, canvasUrl, ct);
        if (ct.IsCancellationRequested)
            return;

        ApplyResolvedCanvasState(CurrentTrackUri, resolved);
    }

    public async Task ImportManualCanvasFileAsync(string sourceFilePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(CurrentTrackUri))
            return;

        var resolved = await _mediaOverrideService.ImportManualTrackCanvasFileAsync(CurrentTrackUri, sourceFilePath, ct);
        if (ct.IsCancellationRequested)
            return;

        ApplyResolvedCanvasState(CurrentTrackUri, resolved);
    }

    public async Task ResetCanvasToUpstreamAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(CurrentTrackUri))
            return;

        var resolved = await _mediaOverrideService.ResetTrackCanvasToUpstreamAsync(CurrentTrackUri, ct);
        if (ct.IsCancellationRequested)
            return;

        ApplyResolvedCanvasState(CurrentTrackUri, resolved);
    }

    public async Task AcceptPendingCanvasUpdateAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(CurrentTrackUri))
            return;

        var resolved = await _mediaOverrideService.AcceptPendingTrackCanvasUpdateAsync(CurrentTrackUri, ct);
        if (ct.IsCancellationRequested)
            return;

        ApplyResolvedCanvasState(CurrentTrackUri, resolved);
    }

    public async Task RejectPendingCanvasUpdateAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(CurrentTrackUri))
            return;

        var resolved = await _mediaOverrideService.RejectPendingTrackCanvasUpdateAsync(CurrentTrackUri, ct);
        if (ct.IsCancellationRequested)
            return;

        ApplyResolvedCanvasState(CurrentTrackUri, resolved);
    }

    private void MapCredits(TrackCreditsResult credits)
    {
        var groups = new ObservableCollection<CreditGroupVm>();
        foreach (var group in credits.Groups)
        {
            groups.Add(new CreditGroupVm
            {
                RoleName = group.RoleName,
                Contributors = group.Contributors.Select(c => new ContributorVm
                {
                    Name = c.Name,
                    Uri = c.ArtistUri,
                    ImageUrl = c.ImageUrl,
                    Roles = c.Roles,
                }).ToList()
            });
        }
        CreditGroups = groups;
        RecordLabel = credits.RecordLabel;
    }

    private void ClearData()
    {
        _loadedTrackId = null;
        HasData = false;
        IsLoading = false;
        ErrorMessage = null;
        ArtistName = null;
        ArtistAvatarUrl = null;
        IsVerified = false;
        Followers = null;
        MonthlyListeners = null;
        BiographyText = null;
        ExternalLinks = null;
        CreditGroups = [];
        RecordLabel = null;
        CanvasUrl = null;
        UpstreamCanvasUrl = null;
        PendingCanvasUrl = null;
        CurrentTrackUri = null;
        HasCanvas = false;
        HasPendingCanvasUpdate = false;
        IsUsingCanvasSnapshot = false;
        IsManualCanvasOverride = false;
        Concerts = [];
        HasConcerts = false;
        RelatedVideos = [];
        HasRelatedVideos = false;
    }

    /// <summary>
    /// Forces a reload on the next track change (e.g. after invalidation).
    /// </summary>
    public void InvalidateTrack()
    {
        _loadedTrackId = null;
    }

    private static string FormatNumber(long value)
    {
        return value switch
        {
            >= 1_000_000_000 => $"{value / 1_000_000_000.0:F1}B",
            >= 1_000_000 => $"{value / 1_000_000.0:F1}M",
            >= 1_000 => $"{value / 1_000.0:F1}K",
            _ => value.ToString("N0", CultureInfo.InvariantCulture)
        };
    }

    private static string? StripHtml(string? html)
    {
        if (string.IsNullOrEmpty(html)) return null;
        // Replace <br>, <br/>, <br /> with newlines
        var text = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        // Strip remaining HTML tags
        text = Regex.Replace(text, @"<[^>]+>", "");
        // Decode all HTML entities (named, decimal, hex — e.g. &amp; &#39; &#x1f90d;)
        text = System.Net.WebUtility.HtmlDecode(text);
        return text.Trim();
    }

    private static string? FormatConcertDate(string? isoDate)
    {
        if (string.IsNullOrEmpty(isoDate)) return null;
        if (DateTimeOffset.TryParse(isoDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
            return dto.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
        return isoDate;
    }

    private static int GetRoleGroupOrder(string roleGroup) => roleGroup switch
    {
        "Artist" => 0,
        "Composition & Lyrics" => 1,
        "Production & Engineering" => 2,
        _ => 3
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _playbackState.PropertyChanged -= OnPlaybackStateChanged;
        _fetchCts?.Cancel();
        _fetchCts?.Dispose();
    }
}

// ── View Models / Models ──

public sealed class CreditGroupVm
{
    public string? RoleName { get; init; }
    public List<ContributorVm>? Contributors { get; init; }
}

public sealed class ContributorVm
{
    public string? Name { get; init; }
    public string? Uri { get; init; }
    public string? ImageUrl { get; init; }
    public List<string>? Roles { get; init; }
    public string RolesText => Roles != null ? string.Join(", ", Roles) : "";
}

public sealed class RelatedVideoVm
{
    public string? Title { get; init; }
    public string? ThumbnailUrl { get; init; }
    public string? ArtistName { get; init; }
    public string? Uri { get; init; }
}

public sealed class ExternalLinkVm
{
    public string? Name { get; init; }
    public string? Url { get; init; }
}
