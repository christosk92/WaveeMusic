using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.UI.Contracts;

/// <summary>
/// Sync O(1) read + async-write surface over Spotify's <c>ban</c> (track) and
/// <c>artistban</c> (artist) server-side collections. Reads are cached in
/// memory (warmed from SQLite on init, kept fresh via dealer-driven sync
/// like every other library collection). Writes go through the outbox and
/// sync back when the server confirms.
///
/// Spotify's official client honors these lists at autoplay rollover, the
/// Home recommendation surfaces, and Search; Wavee mirrors that filtering
/// wherever a content surface enumerates candidates.
/// </summary>
public interface IContentFilterService
{
    /// <summary>Loads the in-memory cache from SQLite. Idempotent.</summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>True when <paramref name="artistUri"/> is on the user's artist-ban list.</summary>
    bool IsArtistBlocked(string? artistUri);

    /// <summary>True when <paramref name="trackUri"/> is on the user's track-hide list.</summary>
    bool IsTrackHidden(string? trackUri);

    /// <summary>
    /// Adds (or removes) an artist URI from the artistban set. Updates the
    /// in-memory cache immediately and enqueues a write to Spotify via the
    /// library outbox. Fires <see cref="FilterChanged"/> on success.
    /// </summary>
    Task SetArtistBlockedAsync(string artistUri, bool blocked, CancellationToken ct = default);

    /// <summary>
    /// Adds (or removes) a track URI from the ban set. Updates the in-memory
    /// cache immediately and enqueues a write to Spotify via the library
    /// outbox. Fires <see cref="FilterChanged"/> on success.
    /// </summary>
    Task SetTrackHiddenAsync(string trackUri, bool hidden, CancellationToken ct = default);

    /// <summary>Fires after every successful mutation or sync-driven cache refresh.</summary>
    event Action? FilterChanged;
}
