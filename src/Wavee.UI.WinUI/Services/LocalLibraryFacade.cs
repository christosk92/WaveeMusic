using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using Wavee.Core.Http;
using Wavee.Local;
using Wavee.Local.Classification;
using Wavee.Local.Enrichment;
using Wavee.Local.Groups;
using Wavee.Local.Models;
using Wavee.Protocol.Metadata;
using Wavee.UI.Library.Local;
using Wavee.UI.WinUI.Helpers;
using ProtoImage = Wavee.Protocol.Metadata.Image;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Default <see cref="ILocalLibraryFacade"/> wiring. Composes
/// <see cref="LocalLibraryService"/>, <see cref="ILocalLikeService"/>,
/// <see cref="ILocalEnrichmentService"/>, and <see cref="LocalGroupService"/>
/// into the single surface UI view-models consume.
///
/// <para>Change notifications: any write method emits a <see cref="LocalLibraryChange"/>
/// on the <see cref="Changes"/> stream after the underlying mutation succeeds. UI
/// view-models subscribe and re-fetch the relevant slice on each event.</para>
/// </summary>
public sealed class LocalLibraryFacade : ILocalLibraryFacade, IDisposable
{
    private readonly LocalLibraryService _library;
    private readonly ILocalLikeService _likes;
    private readonly ILocalEnrichmentService _enrichment;
    private readonly LocalGroupService _groups;
    private readonly IExtendedMetadataClient? _metadataClient;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly Subject<LocalLibraryChange> _changes = new();
    private readonly IDisposable? _likeChangesSub;
    private readonly IDisposable? _scanProgressSub;
    private readonly IDisposable? _enrichmentMatchedSub;
    private readonly ILogger? _logger;

    public LocalLibraryFacade(
        LocalLibraryService library,
        ILocalLikeService likes,
        ILocalEnrichmentService enrichment,
        LocalGroupService groups,
        IExtendedMetadataClient? metadataClient = null,
        IHttpClientFactory? httpClientFactory = null,
        ILogger<LocalLibraryFacade>? logger = null)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _likes = likes ?? throw new ArgumentNullException(nameof(likes));
        _enrichment = enrichment ?? throw new ArgumentNullException(nameof(enrichment));
        _groups = groups ?? throw new ArgumentNullException(nameof(groups));
        _metadataClient = metadataClient;
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        // Fan upstream signals into the facade's single Changes stream.
        _likeChangesSub = _likes.Changes.Subscribe(t =>
            _changes.OnNext(new LocalLibraryChange(LocalLibraryChangeKind.LikeChanged, t.TrackUri)));
        _scanProgressSub = _library.SyncProgress.Subscribe(p =>
        {
            // Only emit a coarse "scan completed" once per scan-end.
            if (p.ProcessedFiles == p.TotalFiles && p.CurrentPath is null)
                _changes.OnNext(new LocalLibraryChange(LocalLibraryChangeKind.ScanCompleted));
        });
        // Pipe enrichment results into Changes so detail pages re-read their
        // VM data without the user having to navigate back-and-forward.
        _enrichmentMatchedSub = _enrichment.Matched.Subscribe(trackUri =>
            _changes.OnNext(new LocalLibraryChange(LocalLibraryChangeKind.MetadataOverrideChanged, trackUri)));
    }

    public IObservable<LocalLibraryChange> Changes => _changes;
    public IObservable<EnrichmentProgress> EnrichmentProgress => _enrichment.Progress;

    // ----------------------- READS -----------------------
    public Task<IReadOnlyList<LocalShow>>          GetShowsAsync(CancellationToken ct = default) => _library.GetShowsAsync(ct);
    public Task<LocalShow?>                        GetShowAsync(string id, CancellationToken ct = default) => _library.GetShowAsync(id, ct);
    public Task<IReadOnlyList<LocalSeason>>        GetShowSeasonsAsync(string id, CancellationToken ct = default) => _library.GetShowSeasonsAsync(id, ct);
    public Task<IReadOnlyList<LocalMovie>>         GetMoviesAsync(CancellationToken ct = default) => _library.GetMoviesAsync(ct);
    public Task<LocalMovie?>                       GetMovieAsync(string uri, CancellationToken ct = default) => _library.GetMovieAsync(uri, ct);
    public Task<IReadOnlyList<LocalMusicVideo>>    GetMusicVideosAsync(CancellationToken ct = default) => _library.GetMusicVideosAsync(ct);
    public Task<LocalMusicVideo?>                  GetMusicVideoAsync(string uri, CancellationToken ct = default) => _library.GetMusicVideoAsync(uri, ct);
    public Task<LocalMusicVideo?>                  GetLinkedMusicVideoForSpotifyTrackAsync(string uri, CancellationToken ct = default) => _library.GetLinkedMusicVideoForSpotifyTrackAsync(uri, ct);
    public Task<IReadOnlyList<LocalOtherItem>>     GetOthersAsync(CancellationToken ct = default) => _library.GetOthersAsync(ct);
    public Task<IReadOnlyList<LocalContinueItem>>  GetContinueWatchingAsync(int limit = 20, CancellationToken ct = default) => _library.GetContinueWatchingAsync(limit, ct);
    public Task<IReadOnlyList<LocalTrackRow>>      GetRecentlyAddedAsync(int limit = 30, CancellationToken ct = default) => _library.GetRecentlyAddedAsync(limit, ct);
    public Task<IReadOnlyList<LocalTrackRow>>      GetMusicTracksAsync(int limit = 500, CancellationToken ct = default) => _library.GetMusicTracksAsync(limit, ct);
    public Task<IReadOnlyList<LocalTrackRow>>      GetRecentlyPlayedAsync(int limit = 30, CancellationToken ct = default) => _library.GetRecentlyPlayedAsync(limit, ct);
    public Task<IReadOnlyList<LocalSubtitle>>      GetSubtitlesForAsync(string path, CancellationToken ct = default) => _library.GetSubtitlesForAsync(path, ct);
    public Task<IReadOnlyList<LocalEmbeddedTrack>> GetAudioTracksForAsync(string path, CancellationToken ct = default) => _library.GetAudioTracksForAsync(path, ct);
    public Task<IReadOnlyList<LocalCastMember>>    GetMovieCastAsync(string trackUri, CancellationToken ct = default) => _library.GetMovieCastAsync(trackUri, ct);
    public Task<IReadOnlyList<LocalCastMember>>    GetShowCastAsync(string seriesId, CancellationToken ct = default) => _library.GetShowCastAsync(seriesId, ct);
    public Task<IReadOnlyList<LocalShow>>          GetShowsByPersonIdAsync(int personId, CancellationToken ct = default) => _library.GetShowsByPersonIdAsync(personId, ct);
    public Task<IReadOnlyList<LocalMovie>>         GetMoviesByPersonIdAsync(int personId, CancellationToken ct = default) => _library.GetMoviesByPersonIdAsync(personId, ct);
    public Task<LocalPersonInfo?>                  GetTmdbPersonAsync(int personId, CancellationToken ct = default) => _enrichment.GetTmdbPersonAsync(personId, ct);
    public Task<LocalLyrics?>                      GetLyricsAsync(string path, CancellationToken ct = default) => _library.GetLyricsAsync(path, ct);
    public Task<IReadOnlyList<LocalCollection>>    GetCollectionsAsync(CancellationToken ct = default) => _groups.ListAsync(kindFilter: "collection", ct);
    public Task<LocalCollection?>                  GetCollectionAsync(string id, CancellationToken ct = default) => _groups.GetAsync(id, ct);
    public async Task<IReadOnlyList<LocalTrackRow>> GetCollectionMembersAsync(string id, CancellationToken ct = default)
    {
        var paths = await _groups.GetMemberPathsAsync(id, ct);
        var rows = new List<LocalTrackRow>(paths.Count);
        foreach (var p in paths)
        {
            // GetTrackAsync expects a track URI; fall back to scanning all tracks
            // and matching by FilePath when we only have a path (cheaper than a
            // path-keyed query for v1 — N is small for collections).
            var byPath = await _library.GetAllTracksAsync(ct);
            var hit = byPath.FirstOrDefault(t => string.Equals(t.FilePath, p, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) rows.Add(hit);
        }
        return rows;
    }

    public async Task<IReadOnlyList<LocalTrackRow>> GetLikedTracksAsync(CancellationToken ct = default) =>
        await _library.GetLikedTracksAsync(ct);

    // ----------------------- WRITES -----------------------
    public async Task SetKindAsync(string filePath, LocalContentKind? kind, CancellationToken ct = default)
    {
        await _library.SetKindOverrideAsync(filePath, kind, ct);
        _changes.OnNext(new LocalLibraryChange(LocalLibraryChangeKind.KindOverrideChanged, filePath));
    }

    public async Task PatchMetadataAsync(string filePath, MetadataPatch patch, CancellationToken ct = default)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(patch);
        await _library.PatchMetadataOverridesAsync(filePath, json, ct);
        _changes.OnNext(new LocalLibraryChange(LocalLibraryChangeKind.MetadataOverrideChanged, filePath));
    }

    public async Task SetLikedAsync(string trackUri, bool liked, CancellationToken ct = default)
    {
        await _likes.SetLikedAsync(trackUri, liked, ct);
        // _likes already fires its own Changes observable; we mirror as a facade event too.
        _changes.OnNext(new LocalLibraryChange(LocalLibraryChangeKind.LikeChanged, trackUri));
    }

    public Task<string> CreateCollectionAsync(string name, CancellationToken ct = default)
    {
        var t = _groups.CreateAsync(name, "collection", ct);
        _changes.OnNext(new LocalLibraryChange(LocalLibraryChangeKind.CollectionChanged));
        return t;
    }

    public async Task AddToCollectionAsync(string collectionId, string filePath, CancellationToken ct = default)
    {
        await _groups.AddMemberAsync(collectionId, filePath, sortOrder: 0, ct);
        _changes.OnNext(new LocalLibraryChange(LocalLibraryChangeKind.CollectionChanged, collectionId));
    }

    public async Task RemoveFromCollectionAsync(string collectionId, string filePath, CancellationToken ct = default)
    {
        await _groups.RemoveMemberAsync(collectionId, filePath, ct);
        _changes.OnNext(new LocalLibraryChange(LocalLibraryChangeKind.CollectionChanged, collectionId));
    }

    public async Task<bool> DeleteFromDiskAsync(string filePath, CancellationToken ct = default)
    {
        var ok = await _library.DeleteFileFromDiskAsync(filePath, ct);
        if (ok) _changes.OnNext(new LocalLibraryChange(LocalLibraryChangeKind.FileRemoved, filePath));
        return ok;
    }

    public async Task<string> SetArtworkOverrideAsync(string entityUri, byte[] bytes, string? mimeType, CancellationToken ct = default)
    {
        var uri = await _library.SetArtworkOverrideAsync(entityUri, bytes, mimeType, ct);
        _changes.OnNext(new LocalLibraryChange(LocalLibraryChangeKind.MetadataOverrideChanged, entityUri));
        return uri;
    }

    public async Task ClearArtworkOverrideAsync(string entityUri, CancellationToken ct = default)
    {
        await _library.ClearArtworkOverrideAsync(entityUri, ct);
        _changes.OnNext(new LocalLibraryChange(LocalLibraryChangeKind.MetadataOverrideChanged, entityUri));
    }

    public async Task RemoveFromLibraryAsync(string filePath, CancellationToken ct = default)
    {
        await _library.RemoveFromLibraryAsync(filePath, ct);
        _changes.OnNext(new LocalLibraryChange(LocalLibraryChangeKind.FileRemoved, filePath));
    }

    public async Task MarkWatchedAsync(string trackUri, bool watched, CancellationToken ct = default)
    {
        await _library.MarkWatchedAsync(trackUri, watched, ct);
        _changes.OnNext(new LocalLibraryChange(LocalLibraryChangeKind.WatchedStateChanged, trackUri));
    }

    public Task SetLastPositionAsync(string trackUri, long positionMs, CancellationToken ct = default) =>
        _library.SetLastPositionAsync(trackUri, positionMs, ct);

    public Task RecordPlayAsync(string trackUri, long positionMs, long durationMs, CancellationToken ct = default) =>
        _library.RecordPlayAsync(trackUri, positionMs, durationMs, ct);

    public Task RefreshMetadataAsync(string trackUri, CancellationToken ct = default) =>
        _enrichment.ForceRefreshAsync(trackUri, ct);

    public async Task LinkMusicVideoToSpotifyTrackAsync(
        string localMusicVideoTrackUri,
        string spotifyTrackUri,
        CancellationToken ct = default)
    {
        await _library.LinkMusicVideoToSpotifyTrackAsync(localMusicVideoTrackUri, spotifyTrackUri, ct);
        _changes.OnNext(new LocalLibraryChange(LocalLibraryChangeKind.MusicVideoAssociationChanged, localMusicVideoTrackUri));
    }

    public async Task UnlinkMusicVideoFromSpotifyTrackAsync(string localMusicVideoTrackUri, CancellationToken ct = default)
    {
        await _library.UnlinkMusicVideoFromSpotifyTrackAsync(localMusicVideoTrackUri, ct);
        _changes.OnNext(new LocalLibraryChange(LocalLibraryChangeKind.MusicVideoAssociationChanged, localMusicVideoTrackUri));
    }

    public async Task EnrichLinkedMusicVideoFromSpotifyAsync(
        string localTrackUri,
        string filePath,
        string spotifyTrackUri,
        CancellationToken ct = default)
    {
        if (_metadataClient is null)
        {
            _logger?.LogDebug("EnrichLinkedMusicVideoFromSpotifyAsync skipped: no IExtendedMetadataClient registered");
            return;
        }

        Track? track;
        try
        {
            track = await _metadataClient.GetTrackAsync(spotifyTrackUri, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "EnrichLinkedMusicVideoFromSpotifyAsync: GetTrackAsync failed for {SpotifyUri}", spotifyTrackUri);
            return;
        }

        if (track is null) return;

        // 1) Persist title / artist / album / year as a metadata override.
        try
        {
            var artist = track.Artist is { Count: > 0 }
                ? string.Join(", ", track.Artist.Select(a => a.Name).Where(n => !string.IsNullOrWhiteSpace(n)))
                : null;
            int? year = null;
            if (track.Album?.Date is { Year: > 0 } d)
                year = d.Year;

            var patch = new MetadataPatch(
                Title: string.IsNullOrWhiteSpace(track.Name) ? null : track.Name,
                Artist: string.IsNullOrWhiteSpace(artist) ? null : artist,
                Album: string.IsNullOrWhiteSpace(track.Album?.Name) ? null : track.Album!.Name,
                Year: year);

            await PatchMetadataAsync(filePath, patch, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "EnrichLinkedMusicVideoFromSpotifyAsync: metadata patch failed");
        }

        // 2) Fetch the album cover and persist as an artwork override.
        try
        {
            var imageHttpsUrl = PickCoverUrl(track);
            if (!string.IsNullOrEmpty(imageHttpsUrl) && _httpClientFactory is not null)
            {
                var http = _httpClientFactory.CreateClient("Spotify");
                using var resp = await http.GetAsync(imageHttpsUrl, ct);
                if (resp.IsSuccessStatusCode)
                {
                    var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                    if (bytes.Length > 0)
                    {
                        var mime = resp.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
                        await SetArtworkOverrideAsync(localTrackUri, bytes, mime, ct);
                    }
                }
                else
                {
                    _logger?.LogDebug("EnrichLinkedMusicVideoFromSpotifyAsync: cover download HTTP {Status}", (int)resp.StatusCode);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "EnrichLinkedMusicVideoFromSpotifyAsync: cover download/persist failed");
        }
    }

    private static string? PickCoverUrl(Track track)
    {
        var images = track.Album?.CoverGroup?.Image;
        if (images is null || images.Count == 0) return null;
        var img = images.FirstOrDefault(i => i.Size == ProtoImage.Types.Size.Default)
                  ?? images.FirstOrDefault();
        if (img is null || img.FileId.Length == 0) return null;
        var spotifyImageUri = "spotify:image:" + Convert.ToHexString(img.FileId.ToByteArray()).ToLowerInvariant();
        return SpotifyImageHelper.ToHttpsUrl(spotifyImageUri);
    }

    public void Dispose()
    {
        _likeChangesSub?.Dispose();
        _scanProgressSub?.Dispose();
        _enrichmentMatchedSub?.Dispose();
        _changes.Dispose();
    }
}
