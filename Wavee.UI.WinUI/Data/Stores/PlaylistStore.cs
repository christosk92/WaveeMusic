using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Core.Data;
using Wavee.Core.Playlists;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;

namespace Wavee.UI.WinUI.Data.Stores;

// Reactive entity store for playlist detail DTOs.
//
// Wraps ILibraryDataService.GetPlaylistAsync so callers (ViewModels) can
// subscribe to an observable stream of state per playlist URI instead of
// firing imperative LoadAsync() + manually reacting to DataChanged events.
//
// The underlying PlaylistCacheService already has an in-memory + SQLite
// cache with its own inflight dedup and dealer-driven updates. This store
// adds: (a) subscription lifecycle tied to refcount — unsubscribe cancels
// the CTS so fetches don't leak TaskCanceledException into the log when
// navigation cancels a load mid-flight, and (b) push absorption so that
// when the playlist cache emits Changes (sync complete, dealer diff), any
// subscribed ViewModel gets the invalidation signal through the same
// stream rather than a parallel event wire.
public sealed class PlaylistStore : EntityStore<string, PlaylistDetailDto>
{
    private readonly ILibraryDataService _libraryDataService;
    private readonly IDisposable _changeSubscription;
    // Local copy used by ShouldPublish for the per-field "which field is bumping"
    // diagnostic — base EntityStore stores the logger privately.
    private readonly ILogger? _diagnosticsLogger;
    private bool _disposed;

    public PlaylistStore(
        ILibraryDataService libraryDataService,
        IPlaylistCacheService playlistCache,
        ILogger<PlaylistStore>? logger = null)
        : base(StringComparer.Ordinal, logger)
    {
        _libraryDataService = libraryDataService;
        _diagnosticsLogger = logger;

        _changeSubscription = playlistCache.Changes
            .Where(evt => !string.IsNullOrEmpty(evt.Uri))
            .Subscribe(evt => Invalidate(evt.Uri));
    }

    protected override TimeSpan Ttl { get; } = TimeSpan.FromHours(24);

    // Hot/cold are no-ops: the BehaviorSubject inside the base slot serves as
    // the warm cache, and the wrapped PlaylistCacheService already maintains
    // its own SQLite tier — we don't want a second layer here.
    protected override ValueTask<PlaylistDetailDto?> ReadHotAsync(string key)
        => new((PlaylistDetailDto?)null);

    protected override ValueTask<PlaylistDetailDto?> ReadColdAsync(string key, CancellationToken ct)
        => new((PlaylistDetailDto?)null);

    protected override Task<PlaylistDetailDto> FetchAsync(string key, PlaylistDetailDto? previous, CancellationToken ct)
        => _libraryDataService.GetPlaylistAsync(key, ct);

    protected override void WriteHot(string key, PlaylistDetailDto value) { /* no-op */ }

    protected override Task WriteColdAsync(string key, PlaylistDetailDto value, CancellationToken ct)
        => Task.CompletedTask;

    // Suppress the OnNext when the post-fetch DTO is content-equivalent to the
    // one already on subscribers. PlaylistCacheService can be tickled into
    // emitting an Updated event by Spotify's curation pipeline bumping a
    // playlist's revision without changing any field VMs care about. Without
    // this gate, every echo Mercury push fans out a full ApplyDetail re-render
    // (owner re-resolve, header backdrop reload, description re-tokenization).
    // The hash covers the fields the playlist page actually renders against.
    protected override bool ShouldPublish(string key, PlaylistDetailDto? previous, PlaylistDetailDto value)
    {
        if (previous is null) return true;
        var prevHash = ComputeContentHash(previous);
        var newHash = ComputeContentHash(value);
        if (prevHash == newHash) return false;

        // Diagnostic: hash differs but the user keeps reporting "Detail
        // received #2/#3" on logically-identical playlist refreshes. Log a
        // per-field equality breakdown so we can identify which field is
        // bumping (most likely candidates for editorial Mixes: HeaderImageUrl
        // session token, Description curation tagline, OwnerName casing).
        // Strip this log once the bumping field is identified and either
        // dropped from the hash or normalized.
        _diagnosticsLogger?.LogDebug(
            "ShouldPublish hash miss for {Key}: name={Name} desc={Desc} img={Img} hdr={Hdr} owner={Owner} ownerId={OwnerId} isOwner={IsOwner} isPublic={IsPublic} count={Count}",
            key,
            previous.Name == value.Name,
            previous.Description == value.Description,
            previous.ImageUrl == value.ImageUrl,
            previous.HeaderImageUrl == value.HeaderImageUrl,
            previous.OwnerName == value.OwnerName,
            previous.OwnerId == value.OwnerId,
            previous.IsOwner == value.IsOwner,
            previous.IsPublic == value.IsPublic,
            previous.TrackCount == value.TrackCount);
        return true;
    }

    private static int ComputeContentHash(PlaylistDetailDto v)
    {
        var hash = new HashCode();
        hash.Add(v.Name, StringComparer.Ordinal);
        hash.Add(v.Description, StringComparer.Ordinal);
        hash.Add(v.ImageUrl, StringComparer.Ordinal);
        hash.Add(v.HeaderImageUrl, StringComparer.Ordinal);
        hash.Add(v.OwnerName, StringComparer.Ordinal);
        hash.Add(v.OwnerId, StringComparer.Ordinal);
        hash.Add(v.IsOwner);
        hash.Add(v.IsPublic);
        hash.Add(v.TrackCount);

        // Include session-control chip signal state so a refresh that
        // transitions SessionControlOptions from "no SignalIdentifiers
        // resolved" (stale SQLite load) to "identifiers populated" (fresh
        // network fetch) is not suppressed by this dedupe gate. The chip row
        // needs the update to know clicks are actionable.
        if (v.SessionControlOptions is { Count: > 0 } opts)
        {
            hash.Add(opts.Count);
            foreach (var opt in opts)
            {
                hash.Add(opt.OptionKey, StringComparer.Ordinal);
                hash.Add(opt.SignalIdentifier, StringComparer.Ordinal);
            }
        }
        return hash.ToHashCode();
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _changeSubscription.Dispose();
        base.Dispose();
    }
}
