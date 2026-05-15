using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Wavee.Core.Audio;
using Wavee.Protocol.ExtendedMetadata;
using Wavee.Protocol.Metadata;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Stores;
using MetadataAlbum = Wavee.Protocol.Metadata.Album;
using MetadataImage = Wavee.Protocol.Metadata.Image;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Viewport-driven album metadata prefetch. Card surfaces (ContentCard,
/// AlbumsLibraryView grid, SearchResultRowCard, SearchFlyoutPanel suggestions,
/// SearchResultHeroCard) call <see cref="EnqueueAlbumPrefetch"/> when an album
/// URI scrolls into reach (or, for typeahead / hero card, is rendered). The
/// prefetcher batches enqueued URIs, requests <c>ALBUM_V4</c> extended-metadata
/// in a single POST via <see cref="ExtendedMetadataStore"/>, parses the
/// resulting <c>Album</c> protobuf into a partial <see cref="AlbumDetailResult"/>
/// (hero + total track count; no per-track names because AlbumV4 only carries
/// track GIDs), and seeds <see cref="AlbumStore"/> via
/// <see cref="AlbumStore.HintPartial"/>. When the user later clicks the card,
/// <see cref="ViewModels.AlbumViewModel.Activate"/> replays the partial
/// immediately and the page paints the hero + a correct-count skeleton while
/// the authoritative Pathfinder fetch hydrates the rest.
/// </summary>
public interface IAlbumPrefetcher
{
    /// <summary>
    /// Enqueue an album URI for viewport-driven prefetch. No-ops for URIs that
    /// don't look like an album (only <c>spotify:album:…</c> is accepted) and
    /// for URIs that have already been prefetched this session. Returns
    /// immediately; the fetch happens on a background batching tick.
    /// </summary>
    void EnqueueAlbumPrefetch(string? albumUri);
}

public sealed class AlbumPrefetcher : IAlbumPrefetcher, IDisposable
{
    private const string AlbumUriPrefix = "spotify:album:";
    private static readonly TimeSpan FlushDelay = TimeSpan.FromMilliseconds(100);

    private readonly ExtendedMetadataStore _metadataStore;
    private readonly AlbumStore _albumStore;
    private readonly ILogger? _logger;

    // Single-fire guard: once a URI's prefetch has been kicked off this session,
    // re-enqueues are dropped. ExtendedMetadataClient's SQLite cache handles
    // longer-term dedup across restarts.
    private readonly ConcurrentDictionary<string, byte> _alreadyKicked = new(StringComparer.Ordinal);

    // Pending batch — drained by FlushAsync. Lock under _gate; ConcurrentQueue
    // would also work but the snapshot-and-clear semantic is simpler under a lock.
    private readonly object _gate = new();
    private List<string>? _pending;
    private Task? _flushTask;
    private CancellationTokenSource? _disposeCts = new();

    public AlbumPrefetcher(
        ExtendedMetadataStore metadataStore,
        AlbumStore albumStore,
        ILogger<AlbumPrefetcher>? logger = null)
    {
        _metadataStore = metadataStore ?? throw new ArgumentNullException(nameof(metadataStore));
        _albumStore = albumStore ?? throw new ArgumentNullException(nameof(albumStore));
        _logger = logger;
    }

    public void EnqueueAlbumPrefetch(string? albumUri)
    {
        if (_disposeCts is null) return;
        if (string.IsNullOrEmpty(albumUri)) return;
        if (!albumUri.StartsWith(AlbumUriPrefix, StringComparison.Ordinal)) return;
        if (!_alreadyKicked.TryAdd(albumUri, 0)) return;

        bool scheduleFlush;
        lock (_gate)
        {
            _pending ??= new List<string>();
            _pending.Add(albumUri);

            // Schedule a single flush task per batch window. Subsequent
            // enqueues join the current batch.
            if (_flushTask is null)
            {
                scheduleFlush = true;
            }
            else
            {
                scheduleFlush = false;
            }
        }

        if (scheduleFlush)
        {
            var cts = _disposeCts;
            if (cts is null) return;
            var task = Task.Delay(FlushDelay, cts.Token)
                .ContinueWith(_ => FlushAsync(cts.Token), TaskScheduler.Default)
                .Unwrap();
            lock (_gate) _flushTask = task;
        }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        List<string>? batch;
        lock (_gate)
        {
            batch = _pending;
            _pending = null;
            _flushTask = null;
        }
        if (batch is null || batch.Count == 0) return;
        if (ct.IsCancellationRequested) return;

        try
        {
            // ExtendedMetadataStore.GetManyAsync handles dedup + SQLite cache
            // short-circuit + the 50 ms inner-debounce that coalesces with
            // concurrent callers. We pass each URI once with [AlbumV4].
            var requests = batch.Select(uri => (uri, (IEnumerable<ExtensionKind>)new[] { ExtensionKind.AlbumV4 }));
            var results = await _metadataStore.GetManyAsync(requests, ct).ConfigureAwait(false);

            foreach (var kv in results)
            {
                var uri = kv.Key.Uri;
                var bytes = kv.Value;
                if (bytes is null or { Length: 0 }) continue;

                try
                {
                    var album = MetadataAlbum.Parser.ParseFrom(bytes);
                    var partial = BuildPartial(uri, album);
                    _albumStore.HintPartial(uri, partial);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "AlbumPrefetcher: failed to parse AlbumV4 for {Uri}", uri);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed mid-flush — silent.
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "AlbumPrefetcher: batch flush failed (size={Size})", batch.Count);
        }
    }

    // ── Album protobuf → partial AlbumDetailResult ──────────────────────────

    private static AlbumDetailResult BuildPartial(string uri, MetadataAlbum album)
    {
        var artists = (album.Artist ?? Enumerable.Empty<Wavee.Protocol.Metadata.Artist>())
            .Select(a => new AlbumArtistResult
            {
                Id = TryGetBase62(a.Gid, SpotifyIdType.Artist),
                Uri = TryGetArtistUri(a.Gid),
                Name = a.Name,
                ImageUrl = null,
            })
            .ToList();

        // Type: prefer typeStr if present (more granular than the enum), fall
        // back to enum name → uppercase. AlbumService maps this verbatim to the
        // page's type pill ("ALBUM" / "SINGLE" / "COMPILATION" / "EP").
        string? typeStr = album.HasTypeStr && !string.IsNullOrEmpty(album.TypeStr)
            ? album.TypeStr.ToUpperInvariant()
            : album.HasType ? album.Type.ToString().ToUpperInvariant() : null;

        // ReleaseDate: AlbumV4 carries year + optional month/day. Mirror
        // AlbumService.ParseAlbumDate behavior (year-only if month/day absent).
        DateTimeOffset releaseDate = default;
        if (album.Date != null && album.Date.Year > 0)
        {
            int month = album.Date.Month > 0 ? album.Date.Month : 1;
            int day = album.Date.Day > 0 ? album.Date.Day : 1;
            try
            {
                releaseDate = new DateTimeOffset(album.Date.Year, month, day, 0, 0, 0, TimeSpan.Zero);
            }
            catch (ArgumentOutOfRangeException)
            {
                // Malformed date (e.g. Feb 30) — fall back to year only.
                releaseDate = new DateTimeOffset(album.Date.Year, 1, 1, 0, 0, 0, TimeSpan.Zero);
            }
        }

        var coverUrl = ExtractLargestCover(album);
        var totalTracks = album.Disc?.Sum(d => d.Track?.Count ?? 0) ?? 0;
        var discCount = album.Disc?.Count ?? 0;

        return new AlbumDetailResult
        {
            Name = album.Name,
            Uri = uri,
            Type = typeStr,
            Label = album.HasLabel ? album.Label : null,
            CoverArtUrl = coverUrl,
            ColorDarkHex = null,
            ColorLightHex = null,
            ColorRawHex = null,
            ReleaseDate = releaseDate,
            IsSaved = false, // Pathfinder corrects this on the full pass.
            IsPreRelease = false,
            PreReleaseEndDateTime = null,
            Copyrights = [],
            Artists = artists,
            Tracks = [],
            MoreByArtist = [],
            AlternateReleases = [],
            TotalTracks = totalTracks,
            DiscCount = discCount,
            ShareUrl = null,
            Palette = null,
            IsPartial = true,
        };
    }

    private static string? ExtractLargestCover(MetadataAlbum album)
    {
        var image = album.CoverGroup?.Image
            ?.OrderByDescending(i => i.Size == MetadataImage.Types.Size.Default ? 2
                : i.Size == MetadataImage.Types.Size.Large ? 1 : 0)
            .FirstOrDefault();
        if (image == null) return null;
        if (image.FileId.IsEmpty) return null;
        var imageId = Convert.ToHexString(image.FileId.ToByteArray()).ToLowerInvariant();
        return $"https://i.scdn.co/image/{imageId}";
    }

    private static string? TryGetBase62(ByteString? gid, SpotifyIdType type)
    {
        if (gid is null || gid.Length == 0) return null;
        try { return SpotifyId.FromRaw(gid.Span, type).ToBase62(); }
        catch { return null; }
    }

    private static string? TryGetArtistUri(ByteString? gid)
    {
        var base62 = TryGetBase62(gid, SpotifyIdType.Artist);
        return base62 is null ? null : $"spotify:artist:{base62}";
    }

    public void Dispose()
    {
        var cts = Interlocked.Exchange(ref _disposeCts, null);
        if (cts is null) return;
        try { cts.Cancel(); } catch { /* already cancelled */ }
        cts.Dispose();
    }
}
