using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Spotify;
using Wavee.Core;

namespace Wavee.SpotifyLive;

/// <summary>Spotify's own curated Liked Songs content-filter chips.</summary>
public interface IContentFilterService
{
    /// <summary>The server's chip set, or an EMPTY list when it is unavailable — in which case the caller derives chips
    /// from the tracks instead. Never throws.</summary>
    Task<IReadOnlyList<ContentFilterChip>> GetLikedChipsAsync(CancellationToken ct = default);
}

public sealed class NullContentFilterService : IContentFilterService
{
    public static readonly NullContentFilterService Instance = new();
    public Task<IReadOnlyList<ContentFilterChip>> GetLikedChipsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ContentFilterChip>>(Array.Empty<ContentFilterChip>());
}

// ── Liked Songs content-filter chips ─────────────────────────────────────────────────────────────────────────────────
// GET {spclient}/content-filter/v1/liked-songs?subjective=true&market=from_token
//   → { "contentFilters": [ { "title": "Mellow", "query": "tags contains mellow" }, … ] }
//
// ETag discipline: the server's ETag is stored and echoed VERBATIM (it is an opaque `{iso}#{int}#{int}#{int}`, never
// parsed). If-None-Match is sent ONLY when a body is actually cached alongside it — a 304 with nothing to fall back on
// would be a self-inflicted empty chip bar, and the corpus shows 304 is the COMMON case for this endpoint.
//
// The cache is session-scoped by design rather than persisted: the first call of a session therefore always makes an
// unconditional request and gets a 200. That is one small request per session in exchange for never being able to
// serve a chip set whose body we no longer hold.
sealed class SpotifyContentFilterService : IContentFilterService
{
    // Chips are editorial and change on the order of weeks; this window only bounds re-asking within one long session.
    static readonly TimeSpan Ttl = TimeSpan.FromHours(6);
    const string Route = "/content-filter/v1/liked-songs?subjective=true&market=from_token";

    readonly IHttpExchange _http;
    readonly Func<string> _baseUrl;
    readonly WaveeLogger _log;
    readonly SemaphoreSlim _gate = new(1, 1);   // one in-flight fetch; the chip bar is rendered from many places

    string? _etag;
    IReadOnlyList<ContentFilterChip> _chips = Array.Empty<ContentFilterChip>();
    bool _haveBody;
    DateTimeOffset _fetchedAt;

    public SpotifyContentFilterService(IHttpExchange http, Func<string> baseUrl, WaveeLogger log)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _baseUrl = baseUrl ?? throw new ArgumentNullException(nameof(baseUrl));
        _log = log;
    }

    public async Task<IReadOnlyList<ContentFilterChip>> GetLikedChipsAsync(CancellationToken ct = default)
    {
        if (_haveBody && DateTimeOffset.UtcNow - _fetchedAt <= Ttl) return _chips;

        try { await _gate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return _chips; }
        try
        {
            // Re-check under the gate: a concurrent caller may have just refreshed.
            if (_haveBody && DateTimeOffset.UtcNow - _fetchedAt <= Ttl) return _chips;
            return await FetchAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    async Task<IReadOnlyList<ContentFilterChip>> FetchAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Accept"] = "application/json" };
            // Conditional ONLY with a body in hand — see the ETag note above.
            if (_haveBody && _etag is { Length: > 0 } tag) headers["If-None-Match"] = tag;

            using var resp = await _http.SendAsync(new HttpReq("GET", _baseUrl() + Route, headers, null), ct)
                                        .ConfigureAwait(false);

            if (resp.Status == 304)
            {
                _fetchedAt = DateTimeOffset.UtcNow;   // revalidated: the cached chips are current
                _log.Event(WaveeLogLevel.Debug, "contentfilter.not_modified", "liked-songs chips revalidated",
                    fields: [WaveeLogField.Of("chips", _chips.Count), WaveeLogField.Of("ms", sw.ElapsedMilliseconds)]);
                return _chips;
            }

            if (resp.Status is < 200 or > 299)
            {
                // 404 means this account has no chip set — ordinary. Anything else is worth surfacing.
                _log.Event(resp.Status == 404 ? WaveeLogLevel.Debug : WaveeLogLevel.Warning, "contentfilter.http.fail",
                    "liked-songs content filters rejected", elapsedMs: sw.ElapsedMilliseconds,
                    fields: [WaveeLogField.Of("status", resp.Status)]);
                return Fallback(sw, "http " + resp.Status.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            string json;
            using (var reader = new StreamReader(resp.Body))
                json = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

            var chips = ContentFilterParser.Parse(json);
            if (chips.Count == 0)
            {
                // A 200 we could not turn into chips is the shape-drift signal — the one thing worth an explicit
                // warning here, because the body format for this endpoint was never captured as a 200.
                _log.Event(WaveeLogLevel.Warning, "contentfilter.unusable",
                    "liked-songs returned 200 with no usable contentFilters (shape drift?)", elapsedMs: sw.ElapsedMilliseconds,
                    fields: [WaveeLogField.Of("bytes", json.Length)]);
                return Fallback(sw, "empty");
            }

            _chips = chips;
            _haveBody = true;
            _fetchedAt = DateTimeOffset.UtcNow;
            _etag = resp.Headers.TryGetValue("ETag", out var et) && et.Length > 0 ? et : null;   // opaque; stored verbatim
            _log.Event(WaveeLogLevel.Info, "contentfilter.ok", "liked-songs chips fetched", elapsedMs: sw.ElapsedMilliseconds,
                fields: [WaveeLogField.Of("chips", chips.Count), WaveeLogField.Of("etag", _etag is not null)]);
            return chips;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return _chips; }
        catch (Exception ex)
        {
            _log.Event(WaveeLogLevel.Warning, "contentfilter.error", "liked-songs content filters failed",
                elapsedMs: sw.ElapsedMilliseconds, ex: ex);
            return Fallback(sw, ex.GetType().Name);
        }
    }

    /// <summary>No usable server chips. Returns whatever is already cached (possibly empty) and stamps the attempt so a
    /// failing endpoint is retried on the TTL rather than on every render of the Liked page.</summary>
    IReadOnlyList<ContentFilterChip> Fallback(Stopwatch sw, string reason)
    {
        _fetchedAt = DateTimeOffset.UtcNow;
        _log.Event(WaveeLogLevel.Debug, "contentfilter.fallback", "falling back to descriptor-derived chips",
            elapsedMs: sw.ElapsedMilliseconds, fields: [WaveeLogField.Of("reason", reason), WaveeLogField.Of("cached", _chips.Count)]);
        return _chips;
    }
}

/// <summary>Stable wrapper so the composition root hands out one instance before login and swaps the live provider in
/// on go-live. Offline the inner is the null service (an empty chip set → the UI derives from descriptors).</summary>
public sealed class SwitchableContentFilterService : IContentFilterService
{
    volatile IContentFilterService _inner;
    public SwitchableContentFilterService(IContentFilterService inner) => _inner = inner;
    public void SetInner(IContentFilterService inner) => _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    public Task<IReadOnlyList<ContentFilterChip>> GetLikedChipsAsync(CancellationToken ct = default)
        => _inner.GetLikedChipsAsync(ct);
}
