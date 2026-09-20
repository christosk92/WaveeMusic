using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.SpotifyLive;

// ── Live "What's New" feed (pathfinder queryWhatsNewFeed) ────────────────────────────────────────────────────────────
// New releases/episodes from followed artists/shows. Fetched through the shared PathfinderResource (5-min TTL). Parses
// data.whatsNewFeedItems.items[] branching on content.data.__typename (Album | Episode); unknown typenames are skipped.
// Never touches the Store — display-only. Seeds at go-live; EnsureFresh rides the PathfinderResource TTL.
sealed class SpotifyWhatsNewService : IWhatsNewService, IDisposable
{
    readonly PathfinderResource _pathfinder;
    readonly WaveeLogger _log;

    readonly SimpleEvent<int> _changed = new();
    readonly object _gate = new();
    NewReleaseNotification[] _snapshot = Array.Empty<NewReleaseNotification>();
    NotificationFeedState _state = NotificationFeedState.Idle;
    CancellationTokenSource? _cts;
    bool _disposed;
    int _rev;

    public SpotifyWhatsNewService(PathfinderResource pathfinder, WaveeLogger log = default)
    {
        _pathfinder = pathfinder;
        _log = log;
    }

    public IReadOnlyList<NewReleaseNotification> Snapshot => Volatile.Read(ref _snapshot);
    public NotificationFeedState State { get { lock (_gate) return _state; } }
    public IObservable<int> Changed => _changed;

    public void EnsureFresh()
    {
        // A fetch already in flight is left alone: cancelling-and-relaunching on every binder rebuild churned the
        // pathfinder round-trip (and its Apply/Fire) once per frame. _cts is non-null for exactly the fetch's lifetime.
        if (Volatile.Read(ref _cts) is not null) return;
        _ = FetchAsync();   // the PathfinderResource TTL (5 min) absorbs the actual network cost
    }

    public Task RefreshAsync(CancellationToken ct) => FetchAsync();

    async Task FetchAsync()
    {
        var cts = new CancellationTokenSource();
        var prev = Interlocked.Exchange(ref _cts, cts);
        if (prev is not null) { try { prev.Cancel(); } catch (ObjectDisposedException) { } }
        var token = cts.Token;

        bool changed = false;
        lock (_gate)
        {
            if (_snapshot.Length == 0 && _state != NotificationFeedState.Loading) { _state = NotificationFeedState.Loading; changed = true; }
        }
        if (changed) Fire();

        try
        {
            using var doc = await _pathfinder.UseQueryAsync(PathfinderOps.QueryWhatsNewFeed, PathfinderOps.QueryWhatsNewFeedHash,
                w =>
                {
                    w.WriteNumber("offset", 0);
                    w.WriteNumber("limit", 50);
                    w.WriteBoolean("onlyUnPlayedItems", false);
                    w.WriteStartArray("includedContentTypes");
                    w.WriteEndArray();
                    w.WriteBoolean("includeEpisodeContentRatingsV2", true);
                }, PathfinderClient.Platform.WebPlayer, token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return;
            if (doc is null) { ApplyFailure("what's-new: no payload (stale hash?)"); return; }
            Apply(Parse(doc.RootElement));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ApplyFailure(ex.Message);
        }
        finally
        {
            Interlocked.CompareExchange(ref _cts, null, cts);
            cts.Dispose();
        }
    }

    void Apply(List<NewReleaseNotification> list)
    {
        // Fire ONLY on a real content/state delta. A cache-hit refetch resolves to a byte-identical feed; publishing it
        // anyway bumped the sidebar's source epoch, which re-ran the binder, which refetched — a self-sustaining
        // every-frame render loop that never let the app go idle.
        var next = list.Count == 0 ? NotificationFeedState.Empty : NotificationFeedState.Populated;
        bool changed = false;
        lock (_gate)
        {
            if (_state != next) changed = true;
            if (!changed && !SameFeed(_snapshot, list)) changed = true;
            if (changed)
            {
                _snapshot = list.ToArray();
                _state = next;
            }
        }
        if (changed) Fire();
    }

    // Element-wise structural compare (NewReleaseNotification is a record). No LINQ — this file stays allocation-lean.
    static bool SameFeed(NewReleaseNotification[] current, List<NewReleaseNotification> next)
    {
        if (current.Length != next.Count) return false;
        for (int i = 0; i < current.Length; i++)
            if (!current[i].Equals(next[i])) return false;
        return true;
    }

    void ApplyFailure(string reason)
    {
        _log.Warn("what's-new: " + reason);   // non-exception failures (stale hash, bad payload) must be visible too
        bool changed = false;
        lock (_gate)
        {
            if (_snapshot.Length == 0 && _state != NotificationFeedState.Error) { _state = NotificationFeedState.Error; changed = true; }
        }
        if (changed) Fire();
    }

    void Fire() => _changed.OnNext(Interlocked.Increment(ref _rev));

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        var s = Interlocked.Exchange(ref _cts, null);
        if (s is not null) { try { s.Cancel(); } catch (ObjectDisposedException) { } }
    }

    // ── parsing (AOT-safe manual JsonDocument) ──────────────────────────────────────────────────────────────────────
    internal static List<NewReleaseNotification> Parse(JsonElement root)
    {
        var list = new List<NewReleaseNotification>();
        var items = Dig(root, "data", "whatsNewFeedItems", "items");
        if (items.ValueKind != JsonValueKind.Array) return list;

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!TryGet(item, "content", out var content) || !TryGet(content, "data", out var data)) continue;
            string typename = Str(data, "__typename") ?? "";

            bool seen = TryGet(item, "state", out var st) && string.Equals(Str(st, "state"), "SEEN", StringComparison.OrdinalIgnoreCase);
            long ts = TryGet(item, "timestamp", out var tsEl) ? IsoMs(Str(tsEl, "isoString")) : 0;

            NewReleaseNotification? n = typename switch
            {
                "Album" => ParseAlbum(data, ts, seen),
                "Episode" => ParseEpisode(data, ts, seen),
                _ => null,   // unknown typename → skip
            };
            if (n is not null) list.Add(n);
        }
        return list;
    }

    static NewReleaseNotification? ParseAlbum(JsonElement data, long ts, bool seen)
    {
        string? uri = Str(data, "uri");
        string? name = Str(data, "name");
        if (uri is null || name is null) return null;
        string? albumType = Str(data, "type") ?? Str(data, "albumType");
        string artists = JoinArtists(data);
        string? cover = CoverUrl(data);
        if (ts == 0 && TryGet(data, "date", out var d)) ts = IsoMs(Str(d, "isoString"));
        return new NewReleaseNotification(uri, ts, IsUnread: !seen, NewReleaseKind.Album, uri, name, cover, artists, albumType, Played: false);
    }

    static NewReleaseNotification? ParseEpisode(JsonElement data, long ts, bool seen)
    {
        string? uri = Str(data, "uri");
        string? name = Str(data, "name");
        if (uri is null || name is null) return null;
        string? cover = CoverUrl(data);
        string creator = "";
        if (TryGet(data, "podcastV2", out var pv2) && TryGet(pv2, "data", out var pd)) creator = Str(pd, "name") ?? "";
        bool played = false;
        if (TryGet(data, "playedState", out var ps)) played = string.Equals(Str(ps, "state"), "FULLY_PLAYED", StringComparison.OrdinalIgnoreCase);
        if (ts == 0 && TryGet(data, "releaseDate", out var rd)) ts = IsoMs(Str(rd, "isoString"));
        return new NewReleaseNotification(uri, ts, IsUnread: !seen, NewReleaseKind.Episode, uri, name, cover, creator, AlbumType: null, played);
    }

    static string JoinArtists(JsonElement data)
    {
        if (!TryGet(data, "artists", out var artists)) return "";
        // artists.items[].profile.name
        var items = artists.ValueKind == JsonValueKind.Array ? artists
                  : (TryGet(artists, "items", out var it) ? it : default);
        if (items.ValueKind != JsonValueKind.Array) return "";
        var names = new List<string>();
        foreach (var a in items.EnumerateArray())
        {
            string? nm = TryGet(a, "profile", out var pr) ? Str(pr, "name") : Str(a, "name");
            if (nm is { Length: > 0 }) names.Add(nm);
        }
        return string.Join(", ", names);
    }

    static string? CoverUrl(JsonElement data)
    {
        if (!TryGet(data, "coverArt", out var cover)) return null;
        if (TryGet(cover, "sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
            foreach (var s in sources.EnumerateArray())
                if (Str(s, "url") is { } u) return u;
        return null;
    }

    static long IsoMs(string? iso)
        => iso is { Length: > 0 } && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)
            ? dto.ToUnixTimeMilliseconds() : 0;

    static bool TryGet(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out value)) return true;
        value = default;
        return false;
    }

    static JsonElement Dig(JsonElement el, params string[] path)
    {
        foreach (var p in path)
        {
            if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(p, out el)) return default;
        }
        return el;
    }

    static string? Str(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
}
