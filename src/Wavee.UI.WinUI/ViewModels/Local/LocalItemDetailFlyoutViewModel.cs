using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Wavee.Local.Classification;
using Wavee.Local.Models;
using Wavee.UI.Library.Local;

namespace Wavee.UI.WinUI.ViewModels.Local;

/// <summary>
/// Edit / inspect VM for one local item, shown by <c>LocalItemDetailFlyout</c>.
/// All editable properties commit through <see cref="ILocalLibraryFacade.PatchMetadataAsync"/>
/// — the underlying writes land in <c>local_files.metadata_overrides</c> as JSON,
/// so they survive re-scans and never trample the original ATL tags.
/// </summary>
public sealed partial class LocalItemDetailFlyoutViewModel : ObservableObject, IDisposable
{
    private readonly ILocalLibraryFacade? _facade;
    private readonly DispatcherQueue _dispatcher;
    private readonly ILogger? _logger;
    private readonly IDisposable? _changesSub;

    [ObservableProperty] private string? _trackUri;
    [ObservableProperty] private string? _filePath;
    [ObservableProperty] private LocalContentKind _kind;
    [ObservableProperty] private string? _artworkUri;

    // Editable fields — bound TwoWay against InlineEditableText.Text.
    [ObservableProperty] private string? _title;
    [ObservableProperty] private string? _artist;
    [ObservableProperty] private string? _album;
    [ObservableProperty] private string? _albumArtist;
    [ObservableProperty] private string? _showName;
    [ObservableProperty] private string? _episodeTitle;
    [ObservableProperty] private int? _year;
    [ObservableProperty] private int? _trackNumber;
    [ObservableProperty] private int? _discNumber;
    [ObservableProperty] private int? _season;
    [ObservableProperty] private int? _episode;

    // Read-only file detail strip
    [ObservableProperty] private long _fileSize;
    [ObservableProperty] private string? _format;
    [ObservableProperty] private long _durationMs;

    // Per-kind feature visibility
    public bool IsMusic       => Kind == LocalContentKind.Music;
    public bool IsMusicVideo  => Kind == LocalContentKind.MusicVideo;
    public bool IsTvEpisode   => Kind == LocalContentKind.TvEpisode;
    public bool IsMovie       => Kind == LocalContentKind.Movie;
    public bool IsOther       => Kind == LocalContentKind.Other;
    public bool IsVideo       => Kind.IsVideo();

    public ObservableCollection<LocalSubtitle> Subtitles { get; } = new();
    public ObservableCollection<LocalEmbeddedTrack> AudioTracks { get; } = new();

    public LocalItemDetailFlyoutViewModel(ILocalLibraryFacade? facade = null,
        ILogger<LocalItemDetailFlyoutViewModel>? logger = null)
    {
        _facade = facade;
        _logger = logger;
        _dispatcher = DispatcherQueue.GetForCurrentThread() ?? throw new InvalidOperationException();
        if (_facade is not null)
            _changesSub = _facade.Changes.Subscribe(__change =>
            {
                if (!string.IsNullOrEmpty(TrackUri))
                    _dispatcher.TryEnqueue(() => _ = LoadAsync(TrackUri!));
            });
    }

    partial void OnKindChanged(LocalContentKind value)
    {
        OnPropertyChanged(nameof(IsMusic));
        OnPropertyChanged(nameof(IsMusicVideo));
        OnPropertyChanged(nameof(IsTvEpisode));
        OnPropertyChanged(nameof(IsMovie));
        OnPropertyChanged(nameof(IsOther));
        OnPropertyChanged(nameof(IsVideo));
    }

    public async Task LoadAsync(string trackUri, CancellationToken ct = default)
    {
        TrackUri = trackUri;
        if (_facade is null) return;

        // Resolve underlying row + per-file subtitle / audio track sets in parallel.
        var coreLibrary = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
            .GetService<Wavee.Local.ILocalLibraryService>();
        var row = coreLibrary is null ? null : await coreLibrary.GetTrackAsync(trackUri, ct);
        if (row is null) return;

        FilePath = row.FilePath;
        Title = row.Title;
        Artist = row.Artist;
        Album = row.Album;
        AlbumArtist = row.AlbumArtist;
        Year = row.Year;
        TrackNumber = row.TrackNumber;
        DiscNumber = row.DiscNumber;
        ArtworkUri = row.ArtworkUri;
        DurationMs = row.DurationMs;
        FileSize = TryFileSize(row.FilePath);
        Format = System.IO.Path.GetExtension(row.FilePath).TrimStart('.').ToUpperInvariant();

        // Effective kind = kind_override ?? auto_kind. Driven through the
        // enrichment-row read so we already get the merged value.
        if (coreLibrary is not null)
        {
            var er = await coreLibrary.GetEnrichmentRowAsync(trackUri, ct);
            if (er is not null)
            {
                Kind = er.AutoKind;
                Season = er.SeasonNumber;
                Episode = er.EpisodeNumber;
                ShowName = er.SeriesName;
                EpisodeTitle = null; // sourced from metadata_overrides below
            }
        }

        // Subtitle + audio lists
        var subs = await _facade.GetSubtitlesForAsync(row.FilePath, ct);
        var audios = await _facade.GetAudioTracksForAsync(row.FilePath, ct);
        _dispatcher.TryEnqueue(() =>
        {
            Subtitles.Clear();
            foreach (var s in subs) Subtitles.Add(s);
            AudioTracks.Clear();
            foreach (var a in audios) AudioTracks.Add(a);
        });
    }

    /// <summary>
    /// Bundles every edited field into a <see cref="MetadataPatch"/> and pushes
    /// it through the facade. Called by the flyout's "Save" button or on field
    /// commit (when InlineEditableText raises Committed).
    /// </summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        if (_facade is null || string.IsNullOrEmpty(FilePath)) return;
        var patch = new MetadataPatch(
            Title:        Title,
            Artist:       Artist,
            AlbumArtist:  AlbumArtist,
            Album:        Album,
            Year:         Year,
            TrackNumber:  TrackNumber,
            DiscNumber:   DiscNumber,
            ShowName:     ShowName,
            Season:       Season,
            Episode:      Episode,
            EpisodeTitle: EpisodeTitle);
        try { await _facade.PatchMetadataAsync(FilePath!, patch, ct); }
        catch (Exception ex) { _logger?.LogWarning(ex, "Save metadata override failed for {Path}", FilePath); }
    }

    /// <summary>
    /// Persists a kind override (right-click → Set kind) through the facade.
    /// </summary>
    public async Task SetKindAsync(LocalContentKind newKind)
    {
        if (_facade is null || string.IsNullOrEmpty(FilePath)) return;
        await _facade.SetKindAsync(FilePath!, newKind);
        Kind = newKind;
    }

    /// <summary>
    /// Replaces the cover art with user-supplied bytes (drag-drop target).
    /// </summary>
    public async Task ApplyArtworkOverrideAsync(byte[] bytes, string? mimeType)
    {
        if (_facade is null || string.IsNullOrEmpty(TrackUri)) return;
        var newUri = await _facade.SetArtworkOverrideAsync(TrackUri!, bytes, mimeType);
        ArtworkUri = newUri;
    }

    private static long TryFileSize(string path)
    {
        try { return new System.IO.FileInfo(path).Length; }
        catch { return 0L; }
    }

    public void Dispose() => _changesSub?.Dispose();
}
