using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Spotify;
using Wavee.Core;

namespace Wavee.SpotifyLive;

// ── Live Spotify social notifications (the gander feed) ──────────────────────────────────────────────────────────────
// One authed GET returns the recent social notifications (followers, concerts, announcements). Auth (bearer + client-token)
// is provided by the injected IHttpExchange (live.Pipeline). Never touches the library Store — display-only. Seeds at
// go-live so the bell badge is right before first open; EnsureFresh refetches only when stale (>5 min). Per-item try/skip
// so an undocumented-schema drift degrades one row, never the whole feed. Modelled on SpotifyFriendActivityService.
sealed class SpotifyNotificationsService : ISpotifyNotificationsService, IDisposable
{
    const long StaleMs = 5 * 60 * 1000;

    readonly IHttpExchange _http;
    readonly Func<string> _baseUrl;
    readonly WaveeLogger _log;
    readonly Func<long> _clock;
    readonly string _language;

    readonly SimpleEvent<int> _changed = new();
    readonly object _gate = new();
    SocialNotification[] _snapshot = Array.Empty<SocialNotification>();
    NotificationFeedState _state = NotificationFeedState.Idle;
    long _lastFetchMs;
    CancellationTokenSource? _cts;
    bool _disposed;
    int _rev;

    public SpotifyNotificationsService(IHttpExchange http, Func<string> baseUrl, WaveeLogger log = default, Func<long>? clock = null,
        string language = "en")
    {
        _http = http;
        _baseUrl = baseUrl;
        _log = log;
        _language = SpotifyHeaders.NormalizeLanguage(language);
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _ = FetchAsync();   // seed at construction (go-live) so the badge is correct before the first open
    }

    public IReadOnlyList<SocialNotification> Snapshot => Volatile.Read(ref _snapshot);
    public NotificationFeedState State { get { lock (_gate) return _state; } }
    public IObservable<int> Changed => _changed;

    public void EnsureFresh()
    {
        long last;
        lock (_gate) last = _lastFetchMs;
        if (_clock() - last >= StaleMs) _ = FetchAsync();
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
            _lastFetchMs = _clock();
            if (_snapshot.Length == 0 && _state != NotificationFeedState.Loading) { _state = NotificationFeedState.Loading; changed = true; }
        }
        if (changed) Fire();

        try
        {
            var url = _baseUrl() + "/gander/v2/GetNotifications?locale=" + Uri.EscapeDataString(_language) + "&limit=20";
            using var resp = await _http.SendAsync(new HttpReq("GET", url, JsonHeaders(), null), token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return;
            if (resp.Status != 200) { ApplyFailure("gander HTTP " + resp.Status); return; }

            using var doc = await JsonDocument.ParseAsync(resp.Body, default, token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return;
            var list = Parse(doc.RootElement, out var cursor);
            Apply(list, cursor);
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

    void Apply(List<SocialNotification> list, string? cursor)
    {
        lock (_gate)
        {
            _snapshot = list.ToArray();
            _state = list.Count == 0 ? NotificationFeedState.Empty : NotificationFeedState.Populated;
        }
        Fire();
    }

    void ApplyFailure(string reason)
    {
        _log.Warn("gander notifications: " + reason);   // HTTP-status failures must be visible, not just exceptions
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

    // ── parsing (AOT-safe manual JsonDocument; no reflection serializers) ────────────────────────────────────────────
    internal static List<SocialNotification> Parse(JsonElement root, out string? latestCursor)
    {
        latestCursor = null;
        var list = new List<SocialNotification>();
        if (root.ValueKind != JsonValueKind.Object) return list;
        if (root.TryGetProperty("latestCursor", out var lc) && lc.ValueKind == JsonValueKind.String) latestCursor = lc.GetString();
        if (!root.TryGetProperty("notifications", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var el in arr.EnumerateArray())
            if (ParseOne(el) is { } n) list.Add(n);
        return list;
    }

    static SocialNotification? ParseOne(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        string? id = Str(el, "id");
        string? title = Str(el, "title");
        if (id is null || title is null) return null;   // skip malformed

        // createdTimestamp is an ISO-8601 string on the wire ("2026-07-09T16:02:24.011Z"); tolerate epoch-ms too.
        long ts = 0;
        if (el.TryGetProperty("createdTimestamp", out var t))
        {
            if (t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out var ms)) ts = ms;
            else if (t.ValueKind == JsonValueKind.String && t.GetString() is { Length: > 0 } raw)
            {
                if (DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AdjustToUniversal, out var dto)) ts = dto.ToUnixTimeMilliseconds();
                else if (long.TryParse(raw, out var ms2)) ts = ms2;
            }
        }

        string? actionUri = null;
        var actionType = SocialActionType.NavigateWebview;
        if (el.TryGetProperty("action", out var action) && action.ValueKind == JsonValueKind.Object)
        {
            actionUri = Str(action, "uri");
            actionType = (Str(action, "type") ?? "").ToUpperInvariant() switch
            {
                "NAVIGATE" => SocialActionType.Navigate,
                _ => SocialActionType.NavigateWebview,   // NAVIGATE_WEBVIEW / unknown → web fallback
            };
        }

        string? imageUrl = null;
        if (el.TryGetProperty("entityImage", out var ei) && ei.ValueKind == JsonValueKind.Object)
            imageUrl = Str(ei, "imageUrl");

        var userNames = new List<string>();
        if (el.TryGetProperty("multiUserImage", out var mui) && mui.ValueKind == JsonValueKind.Object
            && mui.TryGetProperty("userImages", out var uis) && uis.ValueKind == JsonValueKind.Array)
        {
            foreach (var u in uis.EnumerateArray())
                if (Str(u, "userDisplayName") is { } dn) userNames.Add(dn);
        }

        bool isNew = el.TryGetProperty("isNew", out var n) && n.ValueKind == JsonValueKind.True;
        string? storageId = Str(el, "storageId");

        // The feed's own per-item discriminator, when it ships one. Optional on the wire and named inconsistently
        // across payload revisions, so all three spellings are accepted and NONE is required: SpotifyUpdates.KindOf
        // falls back to the action target, which is the shape this app has actually observed.
        string? wireType = Str(el, "type") ?? Str(el, "notificationType") ?? Str(el, "category");

        return new SocialNotification(id, ts, isNew, title, actionUri, actionType, imageUrl, userNames, storageId, wireType);
    }

    static string? Str(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    static Dictionary<string, string> JsonHeaders()
        => new(StringComparer.OrdinalIgnoreCase) { ["Accept"] = "application/json" };
}
