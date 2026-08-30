using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Wavee.Protocol.Popcount;

namespace Wavee.SpotifyLive;

/// <summary>How many accounts have saved a playlist — one authed spclient GET returning four varints
/// (<c>popcount/v2/playlist/{id}/count</c>). Feeds the playlist header's meta line.</summary>
public interface IPlaylistPopcountService
{
    /// <summary>The save count for <paramref name="playlistUri"/>, or <c>null</c> when unknown/unavailable/suppressed.
    /// Never throws for a network or parse failure — an absent badge is the correct degradation.</summary>
    Task<long?> GetSaveCountAsync(string playlistUri, CancellationToken ct = default);

    /// <summary>Drop any cached count for <paramref name="playlistUri"/> so the next ask re-fetches instead of
    /// serving the 6h-TTL row. The one hook this cache has for "this playlist's identity just moved" — a rolling
    /// edition (a daylist) rolling over is exactly that, and otherwise ages out only on the TTL.</summary>
    void Invalidate(string playlistUri);
}

public sealed class NullPlaylistPopcountService : IPlaylistPopcountService
{
    public static readonly NullPlaylistPopcountService Instance = new();
    public Task<long?> GetSaveCountAsync(string playlistUri, CancellationToken ct = default) => Task.FromResult<long?>(null);
    public void Invalidate(string playlistUri) { }
}

// ── Playlist save count ──────────────────────────────────────────────────────────────────────────────────────────────
// The cheapest call in the whole seam: a GET whose entire body is 6-11 bytes. It is nonetheless cached, coalesced and
// TTL'd, because a playlist header re-renders on every navigation and the count moves on the order of hours, not
// seconds. Failures are structurally invisible: GetSaveCountAsync returns null and the meta line simply omits the
// segment, which is also what a playlist with no saves does.
sealed class SpotifyPlaylistPopcountService : IPlaylistPopcountService
{
    // Save counts drift slowly; a 6h window keeps the header stable across a session without ever showing a stale day.
    static readonly TimeSpan Ttl = TimeSpan.FromHours(6);

    /// <summary>Above this the number is not a save count. Spotify's own most-followed playlist sits around 35 M, and
    /// the one corpus sample past this bound (129,279,888) is the DJ — a lexicon-backed synthetic entity whose counter
    /// is a platform aggregate. Suppressing rather than clamping is deliberate: a wrong-but-plausible badge is worse
    /// than no badge, and this bound is checked against a UI-readable number the day one is available.</summary>
    internal const long ImplausibleSaveCount = 100_000_000;

    readonly IHttpExchange _http;
    readonly Func<string> _baseUrl;
    readonly WaveeLogger _log;
    readonly ConcurrentDictionary<string, (long? Count, DateTimeOffset At)> _cache = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, Task<long?>> _inFlight = new(StringComparer.Ordinal);

    public SpotifyPlaylistPopcountService(IHttpExchange http, Func<string> baseUrl, WaveeLogger log)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _baseUrl = baseUrl ?? throw new ArgumentNullException(nameof(baseUrl));
        _log = log;
    }

    public async Task<long?> GetSaveCountAsync(string playlistUri, CancellationToken ct = default)
    {
        if (IdOf(playlistUri) is not { Length: > 0 } id) return null;
        if (_cache.TryGetValue(id, out var hit) && DateTimeOffset.UtcNow - hit.At <= Ttl) return hit.Count;

        var task = _inFlight.GetOrAdd(id, static (i, self) => self.LoadAsync(i), this);
        // WaitAsync so navigating away cancels the AWAIT, not the shared load a second page may be joined to.
        return await task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The base-62 id of a Spotify PLAYLIST uri, or null for anything else (local/synthetic playlists,
    /// folders, collection pseudo-uris) — none of which this endpoint serves. Provider AND kind both have to match:
    /// a <c>wavee:playlist:*</c> is a playlist too, and popcount would 404 on it.</summary>
    internal static string? IdOf(string? playlistUri)
    {
        if (playlistUri is null ||
            EntityUri.Parse(playlistUri) is not { IsSpotify: true, Kind: EntityKind.Playlist, Id: { Length: > 0 } id }) return null;
        foreach (var c in id)
            if (!char.IsAsciiLetterOrDigit(c)) return null;   // keeps a hostile uri out of the request path
        return id;
    }

    async Task<long?> LoadAsync(string id)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var url = _baseUrl() + "/popcount/v2/playlist/" + id + "/count";
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Accept"] = "application/protobuf" };
            using var resp = await _http.SendAsync(new HttpReq("GET", url, headers, null), CancellationToken.None).ConfigureAwait(false);
            if (resp.Status is < 200 or > 299)
            {
                // 404 is ordinary (a playlist with no counter yet); anything else is worth seeing.
                _log.Event(resp.Status == 404 ? WaveeLogLevel.Debug : WaveeLogLevel.Warning, "popcount.http.fail",
                    "popcount rejected", id, sw.ElapsedMilliseconds, fields: [WaveeLogField.Of("status", resp.Status)]);
                return Cache(id, null);
            }

            var msg = PlaylistPopcount.Parser.ParseFrom(resp.Body);
            long count = (long)msg.Count;
            if (count is < 0 or >= ImplausibleSaveCount)
            {
                _log.Event(WaveeLogLevel.Info, "popcount.suppressed", "popcount outside the plausible save-count range",
                    id, sw.ElapsedMilliseconds, fields: [WaveeLogField.Of("count", count)]);
                return Cache(id, null);
            }

            _log.Event(WaveeLogLevel.Debug, "popcount.ok", "playlist save count fetched", id, sw.ElapsedMilliseconds,
                fields: [WaveeLogField.Of("count", count)]);
            return Cache(id, count);
        }
        catch (Exception ex)
        {
            // Best-effort by contract: a network blip or a shape change must never break a playlist header.
            _log.Event(WaveeLogLevel.Warning, "popcount.error", "popcount fetch failed", id, sw.ElapsedMilliseconds, ex);
            return Cache(id, null);
        }
        finally
        {
            _inFlight.TryRemove(id, out _);
        }
    }

    long? Cache(string id, long? count)
    {
        // Negative results are cached too — a 404 playlist must not re-request on every header render.
        _cache[id] = (count, DateTimeOffset.UtcNow);
        return count;
    }

    public void Invalidate(string playlistUri)
    {
        if (IdOf(playlistUri) is { Length: > 0 } id) _cache.TryRemove(id, out _);
    }
}

/// <summary>Stable wrapper so the composition root can hand out one instance before login and swap the live provider
/// in on go-live (the standard switchable-seam shape). Offline the inner is the null service.</summary>
public sealed class SwitchablePlaylistPopcountService : IPlaylistPopcountService
{
    volatile IPlaylistPopcountService _inner;
    public SwitchablePlaylistPopcountService(IPlaylistPopcountService inner) => _inner = inner;
    public void SetInner(IPlaylistPopcountService inner) => _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    public Task<long?> GetSaveCountAsync(string playlistUri, CancellationToken ct = default)
        => _inner.GetSaveCountAsync(playlistUri, ct);
    public void Invalidate(string playlistUri) => _inner.Invalidate(playlistUri);
}
