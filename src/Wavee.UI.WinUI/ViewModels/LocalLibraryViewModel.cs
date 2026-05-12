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
using Wavee.Local;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// View-model for the LocalLibraryPage — the "View all" destination from the
/// Home page's Local Files section. Lists every indexed local album with its
/// tracks. Lightweight by design; per-album detail pages can land later.
/// </summary>
public sealed partial class LocalLibraryViewModel : ObservableObject, IDisposable
{
    private readonly ILocalLibraryService? _service;
    private readonly DispatcherQueue _dispatcher;
    private readonly ILogger? _logger;
    private readonly IDisposable? _progressSub;

    public ObservableCollection<LocalAlbumGroupViewModel> Albums { get; } = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private int _trackCount;

    [ObservableProperty]
    private int _albumCount;

    public LocalLibraryViewModel(
        ILocalLibraryService? service = null,
        ILogger<LocalLibraryViewModel>? logger = null)
    {
        _service = service;
        _logger = logger;
        _dispatcher = DispatcherQueue.GetForCurrentThread()
                      ?? throw new InvalidOperationException("LocalLibraryViewModel must be constructed on a UI thread.");

        if (_service is not null)
            _progressSub = _service.SyncProgress.Subscribe(p => { _ = LoadAsync(); });
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_service is null)
        {
            _dispatcher.TryEnqueue(() => { IsLoading = false; IsEmpty = true; });
            return;
        }

        _dispatcher.TryEnqueue(() => IsLoading = true);
        try
        {
            var rows = await _service.GetAllTracksAsync(ct);
            var grouped = rows
                .GroupBy(r => r.AlbumUri ?? r.Album ?? r.Title ?? r.TrackUri)
                .Select(g =>
                {
                    var first = g.First();
                    var artist = first.AlbumArtist ?? first.Artist ?? "Unknown Artist";
                    var rowsList = g
                        .OrderBy(t => t.DiscNumber ?? 1)
                        .ThenBy(t => t.TrackNumber ?? 0)
                        .ThenBy(t => t.Title)
                        .Select((t, i) => new LocalTrackRowViewModel(
                            Index: i + 1,
                            TrackUri: t.TrackUri,
                            Title: t.Title ?? System.IO.Path.GetFileNameWithoutExtension(t.FilePath),
                            Artist: t.Artist ?? artist,
                            DurationMs: t.DurationMs))
                        .ToList();

                    return new LocalAlbumGroupViewModel
                    {
                        AlbumUri = g.Key,
                        AlbumTitle = first.Album ?? first.Title ?? "Unknown Album",
                        AlbumArtist = artist,
                        Year = g.Max(t => t.Year),
                        ArtworkUri = g.FirstOrDefault(t => t.ArtworkUri != null)?.ArtworkUri,
                        Tracks = rowsList,
                    };
                })
                .OrderBy(a => a.AlbumArtist)
                .ThenBy(a => a.Year ?? 0)
                .ThenBy(a => a.AlbumTitle)
                .ToList();

            _dispatcher.TryEnqueue(() =>
            {
                Albums.Clear();
                foreach (var g in grouped) Albums.Add(g);
                AlbumCount = grouped.Count;
                TrackCount = rows.Count;
                IsEmpty = rows.Count == 0;
                IsLoading = false;
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Local library load failed");
            _dispatcher.TryEnqueue(() => { IsLoading = false; IsEmpty = Albums.Count == 0; });
        }
    }

    [RelayCommand]
    private void PlayTrack(LocalTrackRowViewModel? row)
    {
        if (row is null) return;

        // Build a one-context "Local files" play. PageTracks lets the orchestrator
        // wire next/prev within the local library even though there's no Spotify
        // context entity backing this URI.
        var trackUris = Albums
            .SelectMany(a => a.Tracks)
            .Select(t => t.TrackUri)
            .ToList();
        var skipToIndex = trackUris.FindIndex(uri => uri == row.TrackUri);

        Services.LocalPlaybackLauncher.PlayQueue(
            trackUris,
            skipToIndex < 0 ? 0 : skipToIndex,
            contextName: "Local files");
    }

    public void Dispose()
    {
        _progressSub?.Dispose();
    }
}

public sealed class LocalAlbumGroupViewModel
{
    public required string AlbumUri { get; init; }
    public required string AlbumTitle { get; init; }
    public required string AlbumArtist { get; init; }
    public int? Year { get; init; }
    public string? ArtworkUri { get; init; }
    public required IReadOnlyList<LocalTrackRowViewModel> Tracks { get; init; }

    public string YearDisplay => Year is { } y && y > 0 ? y.ToString() : string.Empty;
    public string SubtitleDisplay => string.IsNullOrEmpty(YearDisplay)
        ? AlbumArtist
        : $"{AlbumArtist} · {YearDisplay}";
    public int TrackCount => Tracks.Count;
}

public sealed record LocalTrackRowViewModel(
    int Index,
    string TrackUri,
    string Title,
    string Artist,
    long DurationMs)
{
    public string DurationDisplay
    {
        get
        {
            var ts = TimeSpan.FromMilliseconds(DurationMs);
            return ts.TotalHours >= 1
                ? ts.ToString(@"h\:mm\:ss")
                : ts.ToString(@"m\:ss");
        }
    }
}
