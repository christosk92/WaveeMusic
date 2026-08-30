using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Spotify;
using Wavee.Core;

namespace Wavee.SpotifyLive;

// ── Live Spotify friend-activity feed (the "friends" panel) ──────────────────────────────────────────────────────────
// Seeds from the presence endpoint on each dealer connection id and applies payload-less dealer deltas per friend.
//   seed  : GET {baseUrl}/presence-view/v2/init-friend-feed/{connectionId}  → { "friends": [ { timestamp, user{}, track{} } ] }
//   delta : dealer MESSAGE on hm://presence2/user/{userId}  →  GET {baseUrl}/presence-view/v1/user/{userId}  → one friend object
// Auth (login5 bearer + client-token) is provided by the injected IHttpExchange (live.Pipeline). This never touches the
// library Store — presence is display-only. All shared state is guarded by one lock; Changed always fires OUTSIDE it.
sealed class SpotifyFriendActivityService : IFriendActivityService, IDisposable
{
    const string PresencePrefix = "hm://presence2/user/";

    readonly IHttpExchange _http;
    readonly Func<string> _baseUrl;
    readonly Func<string?> _currentConnectionId;
    readonly WaveeLogger _log;
    readonly Func<long> _clock;
    readonly long _watchdogMs;

    readonly SimpleEvent<int> _changed = new();
    readonly object _gate = new();
    readonly IDisposable _presenceSub;
    readonly IDisposable _connSub;
    readonly ConcurrentDictionary<string, CancellationTokenSource> _userCts = new(StringComparer.Ordinal);

    // Guarded by _gate.
    readonly List<FriendActivity> _items = new();
    FriendActivity[] _snapshot = Array.Empty<FriendActivity>();
    FriendFeedState _state = FriendFeedState.Idle;
    string? _lastError;
    string? _connId;              // last observed dealer connection id (dedupe — the subject replays)
    string? _seedConnectionId;    // the connection id of the in-flight / last-applied seed (stale-drop generation)
    long _lastPushAtMs;
    Timer? _watchdog;
    bool _disposed;

    CancellationTokenSource? _seedCts;
    int _rev;

    public SpotifyFriendActivityService(
        ITransport transport, IHttpExchange http, Func<string> baseUrl,
        IObservable<string?> connectionId, Func<string?> currentConnectionId,
        WaveeLogger log = default, Func<long>? clock = null, TimeSpan? watchdogInterval = null)
    {
        _http = http;
        _baseUrl = baseUrl;
        _currentConnectionId = currentConnectionId;
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _watchdogMs = (long)(watchdogInterval ?? TimeSpan.FromMinutes(30)).TotalMilliseconds;
        _lastPushAtMs = _clock();
        _presenceSub = transport.Events(PresencePrefix).Subscribe(Observers.From<WireEvent>(OnPush));
        _connSub = connectionId.Subscribe(Observers.From<string?>(OnConnectionId));
    }

    public IReadOnlyList<FriendActivity> Snapshot => Volatile.Read(ref _snapshot);
    public FriendFeedState State { get { lock (_gate) return _state; } }
    public string? LastError { get { lock (_gate) return _lastError; } }
    public IObservable<int> Changed => _changed;

    // ── Connection id (dealer thread) ────────────────────────────────────────────────────────────────────────────────
    void OnConnectionId(string? id)
    {
        bool offline;
        bool changed = false;
        lock (_gate)
        {
            if (string.Equals(id, _connId, StringComparison.Ordinal)) return;   // replayed same id → nothing to do
            _connId = id;
            offline = string.IsNullOrEmpty(id);
            if (offline && _state != FriendFeedState.Offline) { _state = FriendFeedState.Offline; changed = true; }
        }
        if (offline)
        {
            CancelSeed();
            CancelUserFetches();
            if (changed) FireChanged();
            return;
        }
        _ = SeedAsync(id!);
    }

    // ── Dealer push (dealer thread) ──────────────────────────────────────────────────────────────────────────────────
    void OnPush(WireEvent e)
    {
        if (!e.Topic.StartsWith(PresencePrefix, StringComparison.Ordinal)) return;
        var userId = e.Topic.Substring(PresencePrefix.Length);
        if (string.IsNullOrEmpty(userId)) return;
        lock (_gate) _lastPushAtMs = _clock();
        _ = FetchUserAsync(userId);
    }

    // ── Seed ─────────────────────────────────────────────────────────────────────────────────────────────────────────
    async Task SeedAsync(string connId)
    {
        var cts = new CancellationTokenSource();
        var prev = Interlocked.Exchange(ref _seedCts, cts);
        if (prev is not null) { try { prev.Cancel(); } catch (ObjectDisposedException) { } }
        CancelUserFetches();   // a fresh seed supersedes all pending per-user fetches
        var token = cts.Token;

        bool changed = false;
        lock (_gate)
        {
            _seedConnectionId = connId;
            _lastPushAtMs = _clock();                                   // reset the watchdog window on any (re-)seed
            if (_items.Count == 0 && _state != FriendFeedState.Loading) { _state = FriendFeedState.Loading; changed = true; }
        }
        if (changed) FireChanged();

        try
        {
            var url = _baseUrl() + "/presence-view/v2/init-friend-feed/" + Uri.EscapeDataString(connId);
            using var resp = await _http.SendAsync(new HttpReq("GET", url, JsonHeaders(), null), token).ConfigureAwait(false);
            if (IsStaleSeed(connId)) return;                            // a newer connection id superseded us mid-flight
            if (resp.Status == 404) { ApplySeedEmpty(); return; }       // account with no visible friend activity
            if (resp.Status != 200) { ApplySeedFailure("presence seed HTTP " + resp.Status); return; }

            using var doc = await JsonDocument.ParseAsync(resp.Body, default, token).ConfigureAwait(false);
            var list = ParseFriends(doc.RootElement);
            if (IsStaleSeed(connId)) return;
            ApplySeed(list);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.Warn("presence seed: " + ex.Message, ex);   // once per seed cycle (per-user deltas stay quieter)
            if (IsStaleSeed(connId)) return;
            ApplySeedFailure(ex.Message);
        }
        finally
        {
            Interlocked.CompareExchange(ref _seedCts, null, cts);       // clear only if still ours
            cts.Dispose();
        }
    }

    bool IsStaleSeed(string connId)
    {
        lock (_gate) return !string.Equals(_seedConnectionId, connId, StringComparison.Ordinal);
    }

    // ── Per-user delta ───────────────────────────────────────────────────────────────────────────────────────────────
    async Task FetchUserAsync(string userId)
    {
        var cts = new CancellationTokenSource();
        ReplaceUserCts(userId, cts);                                    // coalesce: a newer push cancels the older fetch
        var token = cts.Token;
        try
        {
            var url = _baseUrl() + "/presence-view/v1/user/" + Uri.EscapeDataString(userId);
            using var resp = await _http.SendAsync(new HttpReq("GET", url, JsonHeaders(), null), token).ConfigureAwait(false);
            if (resp.Status is 404 or 403) { RemoveUser(userId); return; }   // friend no longer visible → drop the row
            if (resp.Status != 200) { _log.Info("presence user HTTP " + resp.Status); return; }

            using var doc = await JsonDocument.ParseAsync(resp.Body, default, token).ConfigureAwait(false);
            var friend = ParseFriend(doc.RootElement);
            if (friend is null) { RemoveUser(userId); return; }         // missing user.uri / track.uri → drop
            UpsertUser(friend);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.Info("presence user fetch: " + ex.Message); }
        finally
        {
            _userCts.TryRemove(new KeyValuePair<string, CancellationTokenSource>(userId, cts));   // remove only if still ours
            cts.Dispose();
        }
    }

    void ReplaceUserCts(string userId, CancellationTokenSource cts)
    {
        while (true)
        {
            if (_userCts.TryGetValue(userId, out var existing))
            {
                if (_userCts.TryUpdate(userId, cts, existing))
                {
                    try { existing.Cancel(); } catch (ObjectDisposedException) { }
                    return;
                }
            }
            else if (_userCts.TryAdd(userId, cts)) return;
        }
    }

    void CancelUserFetches()
    {
        foreach (var kv in _userCts)
            try { kv.Value.Cancel(); } catch (ObjectDisposedException) { }
    }

    void CancelSeed()
    {
        var s = Interlocked.Exchange(ref _seedCts, null);
        if (s is not null) { try { s.Cancel(); } catch (ObjectDisposedException) { } }
    }

    // ── State mutation (all fire Changed OUTSIDE the lock) ───────────────────────────────────────────────────────────
    void ApplySeed(List<FriendActivity> list)
    {
        lock (_gate)
        {
            _items.Clear();
            _items.AddRange(list);       // ParseFriends already sorted descending
            _state = _items.Count == 0 ? FriendFeedState.Empty : FriendFeedState.Populated;
            _lastError = null;
            PublishSnapshot();
        }
        FireChanged();
    }

    void ApplySeedEmpty()
    {
        lock (_gate)
        {
            _items.Clear();
            _state = FriendFeedState.Empty;
            _lastError = null;
            PublishSnapshot();
        }
        FireChanged();
    }

    void ApplySeedFailure(string error)
    {
        lock (_gate)
        {
            _lastError = error;
            if (_items.Count == 0) _state = FriendFeedState.Error;   // keep stale rows when we already have some
        }
        FireChanged();
    }

    void UpsertUser(FriendActivity friend)
    {
        lock (_gate)
        {
            _items.RemoveAll(x => string.Equals(x.UserUri, friend.UserUri, StringComparison.Ordinal));
            _items.Add(friend);
            SortDesc(_items);
            _state = FriendFeedState.Populated;
            _lastError = null;
            PublishSnapshot();
        }
        FireChanged();
    }

    void RemoveUser(string userId)
    {
        bool changed;
        lock (_gate)
        {
            int removed = _items.RemoveAll(x => MatchesUser(x, userId));
            changed = removed > 0;
            if (changed)
            {
                _state = _items.Count == 0 ? FriendFeedState.Empty : FriendFeedState.Populated;
                PublishSnapshot();
            }
        }
        if (changed) FireChanged();
    }

    void PublishSnapshot() => _snapshot = _items.ToArray();   // called under _gate

    void FireChanged() => _changed.OnNext(Interlocked.Increment(ref _rev));

    // ── Visibility / lifecycle ───────────────────────────────────────────────────────────────────────────────────────
    public void SetActive(bool active)
    {
        if (active)
        {
            var id = _currentConnectionId();
            FriendFeedState st;
            lock (_gate) st = _state;
            if (!string.IsNullOrEmpty(id) && st is FriendFeedState.Idle or FriendFeedState.Offline or FriendFeedState.Error)
                _ = SeedAsync(id!);
            lock (_gate)
            {
                if (_disposed) return;
                _watchdog ??= new Timer(WatchdogTick, null, _watchdogMs, _watchdogMs);
            }
        }
        else
        {
            Timer? t;
            lock (_gate) { t = _watchdog; _watchdog = null; }
            t?.Dispose();
        }
    }

    void WatchdogTick(object? _)
    {
        var id = _currentConnectionId();
        if (string.IsNullOrEmpty(id)) return;
        long last;
        lock (_gate) last = _lastPushAtMs;
        if (_clock() - last >= _watchdogMs) _ = SeedAsync(id!);
    }

    public Task RefreshAsync(CancellationToken ct)
    {
        var id = _currentConnectionId();
        if (string.IsNullOrEmpty(id))
        {
            bool changed;
            lock (_gate) { changed = _state != FriendFeedState.Offline; _state = FriendFeedState.Offline; }
            if (changed) FireChanged();
            return Task.CompletedTask;
        }
        return SeedAsync(id!);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _presenceSub.Dispose();
        _connSub.Dispose();
        Timer? t;
        lock (_gate) { t = _watchdog; _watchdog = null; }
        t?.Dispose();
        CancelSeed();
        CancelUserFetches();
    }

    // ── Parsing (AOT-safe manual JsonDocument; no reflection serializers) ────────────────────────────────────────────
    static List<FriendActivity> ParseFriends(JsonElement root)
    {
        var list = new List<FriendActivity>();
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("friends", out var friends) && friends.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in friends.EnumerateArray())
                if (ParseFriend(el) is { } friend) list.Add(friend);
        }
        SortDesc(list);
        return list;
    }

    static FriendActivity? ParseFriend(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty("track", out var track) || track.ValueKind != JsonValueKind.Object) return null;

        var userUri = Str(user, "uri");
        var trackUri = Str(track, "uri");
        if (userUri is null || trackUri is null) return null;   // skip entries missing user.uri / track.uri

        long ts = 0;
        if (el.TryGetProperty("timestamp", out var t))
        {
            if (t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out var ms)) ts = ms;
            else if (t.ValueKind == JsonValueKind.String && long.TryParse(t.GetString(), out var ms2)) ts = ms2;
        }

        string userName = Str(user, "name") ?? "";
        string? userImage = Str(user, "imageUrl") ?? Str(user, "image_url");
        string trackName = Str(track, "name") ?? "";
        string? trackImage = Str(track, "imageUrl") ?? Str(track, "image_url");

        string? albumUri = null, albumName = null, artistUri = null, artistName = null, contextUri = null, contextName = null;
        if (track.TryGetProperty("album", out var album) && album.ValueKind == JsonValueKind.Object)
        { albumUri = Str(album, "uri"); albumName = Str(album, "name"); }
        if (track.TryGetProperty("artist", out var artist) && artist.ValueKind == JsonValueKind.Object)
        { artistUri = Str(artist, "uri"); artistName = Str(artist, "name"); }
        if (track.TryGetProperty("context", out var context) && context.ValueKind == JsonValueKind.Object)
        { contextUri = Str(context, "uri"); contextName = Str(context, "name"); }

        return new FriendActivity(userUri, userName, userImage, ts, trackUri, trackName, trackImage,
            albumUri, albumName, artistUri, artistName, contextUri, contextName);
    }

    static string? Str(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s
            ? s
            : null;

    static void SortDesc(List<FriendActivity> list)
        => list.Sort(static (a, b) => b.TimestampMs.CompareTo(a.TimestampMs));

    // A delta's userId (from the topic tail) matches a row by its exact UserUri or the bare id of a spotify:user:{id} uri.
    static bool MatchesUser(FriendActivity f, string userId)
        => string.Equals(f.UserUri, userId, StringComparison.Ordinal)
           || f.UserUri.EndsWith(":" + userId, StringComparison.Ordinal);

    static Dictionary<string, string> JsonHeaders()
        => new(StringComparer.OrdinalIgnoreCase) { ["Accept"] = "application/json" };
}
