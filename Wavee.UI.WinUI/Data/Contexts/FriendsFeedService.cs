using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Wavee.Core.Http;
using Wavee.Core.Http.Presence;
using Wavee.Core.Session;
using SessionImpl = Wavee.Core.Session.Session;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Messages;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Data.Contexts;

/// <summary>
/// Right-panel Friend Activity feed.
/// Seeds via <c>/presence-view/v2/init-friend-feed/{connectionId}</c>, then
/// applies per-user delta updates whenever a <c>hm://presence2/user/{userId}</c>
/// dealer push arrives (fetching the updated entry from
/// <c>/presence-view/v1/user/{userId}</c>).
/// </summary>
public sealed partial class FriendsFeedService
    : ObservableObject, IFriendsFeedService, IDisposable,
      IRecipient<AuthStatusChangedMessage>
{
    private const string PushUriPrefix = "hm://presence2/user/";

    // Belt-and-braces re-seed cadence in case a dealer reconnect drops a push.
    // The primary update channel is the dealer; this is a watchdog only.
    private static readonly TimeSpan WatchdogReseedInterval = TimeSpan.FromMinutes(30);

    // Cadence for ticking row VMs so their IsCurrentlyListening / TrailingText
    // transition from "now" → "X min" → "X hr" as time passes. 30 s is a good
    // tradeoff: rows feel live, cost is one timer + N trivial property sets.
    private static readonly TimeSpan RowTickInterval = TimeSpan.FromSeconds(30);

    private readonly SessionImpl _session;
    private readonly IMessenger _messenger;
    private readonly ILogger? _logger;
    private readonly DispatcherQueue? _dispatcher;
    private bool _dealerSubscribed;

    private readonly ObservableCollection<FriendFeedRowViewModel> _items = new();
    public ReadOnlyObservableCollection<FriendFeedRowViewModel> Items { get; }

    private readonly CompositeDisposable _lifecycleSubs = new();
    private IDisposable? _dealerMessagesSub;
    private Timer? _safetyTimer;
    private Timer? _rowTickTimer;

    // Seed CTS (full-feed re-seed on connectionId change or watchdog).
    private CancellationTokenSource? _inFlightCts;
    // Per-user presence fetch CTS keyed by userId. Rapid pushes for the same
    // user cancel the in-flight fetch for that user only.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _userCts = new(StringComparer.OrdinalIgnoreCase);

    private string? _seedConnectionId;
    private DateTimeOffset _lastPushAt;
    private bool _isActive;
    private bool _disposed;

    public event Action<string>? FriendUpserted;

    [ObservableProperty] private FriendsFeedState _state = FriendsFeedState.Idle;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private DateTimeOffset? _lastUpdated;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _lastPushUri;

    public FriendsFeedService(
        SessionImpl session,
        IMessenger messenger,
        ILogger<FriendsFeedService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(messenger);
        _session = session;
        _messenger = messenger;
        _logger = logger;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Items = new ReadOnlyObservableCollection<FriendFeedRowViewModel>(_items);

        _messenger.Register<AuthStatusChangedMessage>(this);

        // Row tick keeps IsCurrentlyListening / TrailingText in sync with the
        // wall clock. Always running — cost is trivial and makes the feed feel
        // alive even without pushes.
        _rowTickTimer = new Timer(OnRowTick, null, RowTickInterval, RowTickInterval);

        // Try now in case session is already authenticated (e.g. hot-reload).
        TrySubscribeToDealer();
    }

    public void Receive(AuthStatusChangedMessage message)
    {
        if (_disposed) return;
        if (message.Value == AuthStatus.Authenticated)
        {
            _logger?.LogDebug("FriendsFeed: auth=Authenticated — attempting dealer subscription");
            TrySubscribeToDealer();
        }
        else if (message.Value == AuthStatus.LoggedOut || message.Value == AuthStatus.SessionExpired)
        {
            _logger?.LogDebug("FriendsFeed: auth={Status} — tearing down", message.Value);
            TearDown();
        }
    }

    private void TearDown()
    {
        if (_disposed) return;

        CancelInFlight();
        CancelAllUserFetches();

        _dealerMessagesSub?.Dispose();
        _dealerMessagesSub = null;
        _lifecycleSubs.Clear();
        _dealerSubscribed = false;
        _seedConnectionId = null;

        Dispatch(() =>
        {
            _items.Clear();
            LastUpdated = null;
            ErrorMessage = null;
            LastPushUri = null;
            IsLoading = false;
            State = FriendsFeedState.Idle;
        });
    }

    private void TrySubscribeToDealer()
    {
        if (_dealerSubscribed) return;

        var dealer = _session.Dealer;
        if (dealer is null)
        {
            _logger?.LogDebug("FriendsFeed: Session.Dealer is null — will retry on auth change");
            SetStateOnDispatcher(FriendsFeedState.Offline);
            return;
        }

        _lifecycleSubs.Add(dealer.ConnectionId
            .DistinctUntilChanged()
            .Subscribe(OnConnectionIdChanged));

        _dealerMessagesSub = dealer.Messages
            .Where(IsPresencePush)
            .Subscribe(OnDealerPush);

        _lifecycleSubs.Add(_dealerMessagesSub);

        _dealerSubscribed = true;
        _logger?.LogInformation("FriendsFeed: subscribed to dealer (ConnectionId + presence-like pushes)");
    }

    private static bool IsPresencePush(Wavee.Connect.Protocol.DealerMessage msg)
        => !string.IsNullOrEmpty(msg.Uri)
           && msg.Uri.StartsWith(PushUriPrefix, StringComparison.OrdinalIgnoreCase);

    private static string? ExtractUserId(string uri)
    {
        if (uri.Length <= PushUriPrefix.Length) return null;
        var id = uri[PushUriPrefix.Length..].Trim();
        return string.IsNullOrEmpty(id) ? null : id;
    }

    private void OnConnectionIdChanged(string? connectionId)
    {
        if (_disposed) return;

        _logger?.LogDebug("FriendsFeed: connection id changed to {ConnId}", connectionId ?? "<null>");

        CancelInFlight();
        CancelAllUserFetches();

        if (string.IsNullOrEmpty(connectionId))
        {
            Dispatch(() =>
            {
                _items.Clear();
                LastUpdated = null;
                ErrorMessage = null;
                State = FriendsFeedState.Offline;
            });
            _seedConnectionId = null;
            return;
        }

        _seedConnectionId = connectionId;
        _ = SeedAsync(connectionId, CancellationToken.None);
    }

    private void CancelAllUserFetches()
    {
        foreach (var kv in _userCts.ToArray())
        {
            if (_userCts.TryRemove(kv.Key, out var cts))
            {
                try { cts.Cancel(); } catch { /* ignore */ }
                cts.Dispose();
            }
        }
    }

    private void OnDealerPush(Wavee.Connect.Protocol.DealerMessage msg)
    {
        if (_disposed) return;
        _lastPushAt = DateTimeOffset.UtcNow;
        Dispatch(() => LastPushUri = msg.Uri);

        var userId = ExtractUserId(msg.Uri);
        if (string.IsNullOrEmpty(userId))
        {
            _logger?.LogWarning("FriendsFeed: push with unparseable URI={Uri}", msg.Uri);
            return;
        }

        _logger?.LogDebug("FriendsFeed: push for user {UserId} → fetching presence", userId);
        _ = FetchAndUpsertUserAsync(userId);
    }

    private async Task FetchAndUpsertUserAsync(string userId)
    {
        // Cancel any previous in-flight fetch for the same user.
        if (_userCts.TryRemove(userId, out var oldCts))
        {
            try { oldCts.Cancel(); } catch { /* ignore */ }
            oldCts.Dispose();
        }

        var cts = new CancellationTokenSource();
        _userCts[userId] = cts;

        try
        {
            var entry = await _session.SpClient
                .GetFriendPresenceAsync(userId, cts.Token)
                .ConfigureAwait(false);

            if (_disposed) return;

            if (entry == null || entry.User == null || entry.Track == null)
            {
                // 404/403 or malformed → drop the row.
                Dispatch(() => RemoveRowForUser(userId));
                _logger?.LogDebug("FriendsFeed: user {UserId} no longer visible — removed", userId);
                return;
            }

            var row = new FriendFeedRowViewModel(entry);
            Dispatch(() =>
            {
                UpsertRow(row);
                // Notify view so it can flash the row. Deferred one tick to
                // let ItemsRepeater realize the element at the new index.
                _dispatcher?.TryEnqueue(() => FriendUpserted?.Invoke(row.UserUri));
            });
            _logger?.LogDebug("FriendsFeed: upserted {User} listening to {Track}",
                row.DisplayUsername, entry.Track.Name ?? "<none>");
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer push for the same user — no-op.
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "FriendsFeed: presence fetch failed for {UserId}", userId);
        }
        finally
        {
            if (_userCts.TryGetValue(userId, out var tracked) && ReferenceEquals(tracked, cts))
                _userCts.TryRemove(userId, out _);
            cts.Dispose();
        }
    }

    private void UpsertRow(FriendFeedRowViewModel row)
    {
        // Remove any existing row for this user.
        for (int i = 0; i < _items.Count; i++)
        {
            if (string.Equals(_items[i].UserUri, row.UserUri, StringComparison.OrdinalIgnoreCase))
            {
                _items.RemoveAt(i);
                break;
            }
        }

        // Insert at the correct position to keep Timestamp descending.
        int insertAt = 0;
        for (; insertAt < _items.Count; insertAt++)
        {
            if (_items[insertAt].Timestamp <= row.Timestamp) break;
        }
        _items.Insert(insertAt, row);

        LastUpdated = DateTimeOffset.UtcNow;
        if (_items.Count > 0 && State != FriendsFeedState.Populated)
            State = FriendsFeedState.Populated;
    }

    private void RemoveRowForUser(string userId)
    {
        // UserUri format: "spotify:user:{id}". Match by id suffix.
        for (int i = 0; i < _items.Count; i++)
        {
            var uri = _items[i].UserUri;
            if (string.IsNullOrEmpty(uri)) continue;
            if (uri.EndsWith(":" + userId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri, userId, StringComparison.OrdinalIgnoreCase))
            {
                _items.RemoveAt(i);
                break;
            }
        }

        if (_items.Count == 0 && State == FriendsFeedState.Populated)
            State = FriendsFeedState.Empty;
    }

    public void SetActive(bool isActive)
    {
        if (_disposed || _isActive == isActive) return;
        _isActive = isActive;

        if (isActive)
        {
            _safetyTimer ??= new Timer(OnSafetyTick, null, WatchdogReseedInterval, WatchdogReseedInterval);

            // If we have a connection but no data (e.g. service was idle when panel was closed), seed now.
            var connId = _session.Dealer?.CurrentConnectionId;
            if (!string.IsNullOrEmpty(connId) && State is FriendsFeedState.Idle or FriendsFeedState.Offline)
            {
                _seedConnectionId = connId;
                _ = SeedAsync(connId, CancellationToken.None);
            }
        }
        else
        {
            _safetyTimer?.Dispose();
            _safetyTimer = null;
        }
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var connId = _session.Dealer?.CurrentConnectionId;
        if (string.IsNullOrEmpty(connId))
        {
            SetStateOnDispatcher(FriendsFeedState.Offline);
            return Task.CompletedTask;
        }
        _seedConnectionId = connId;
        return SeedAsync(connId, cancellationToken);
    }

    private void OnSafetyTick(object? _)
    {
        if (_disposed || !_isActive) return;
        if (DateTimeOffset.UtcNow - _lastPushAt < WatchdogReseedInterval) return;

        var connId = _seedConnectionId;
        if (!string.IsNullOrEmpty(connId))
        {
            _logger?.LogDebug("FriendsFeed: watchdog re-seed (no pushes for {Min}m)", WatchdogReseedInterval.TotalMinutes);
            _ = SeedAsync(connId, CancellationToken.None);
        }
    }

    private async Task SeedAsync(string connectionId, CancellationToken externalCt)
    {
        CancelInFlight();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        _inFlightCts = cts;

        Dispatch(() =>
        {
            IsLoading = true;
            if (_items.Count == 0) State = FriendsFeedState.Loading;
            ErrorMessage = null;
        });

        try
        {
            var response = await _session.SpClient
                .GetFriendFeedAsync(connectionId, cts.Token)
                .ConfigureAwait(false);

            // Ignore stale responses from a superseded connection id.
            if (!string.Equals(_seedConnectionId, connectionId, StringComparison.Ordinal))
            {
                _logger?.LogDebug("FriendsFeed: dropping stale seed for {ConnId}", connectionId);
                return;
            }

            ApplySnapshot(response);
        }
        catch (OperationCanceledException)
        {
            // Superseded — no-op.
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "FriendsFeed: seed failed");
            Dispatch(() =>
            {
                ErrorMessage = ex.Message;
                State = _items.Count > 0 ? FriendsFeedState.Populated : FriendsFeedState.Error;
            });
        }
        finally
        {
            Dispatch(() => IsLoading = false);
            if (ReferenceEquals(_inFlightCts, cts))
                _inFlightCts = null;
            cts.Dispose();
        }
    }

    private void ApplySnapshot(FriendFeedResponse response)
    {
        var entries = response.Friends ?? [];
        var ordered = entries
            .Where(e => e.User is not null && e.Track is not null)
            .OrderByDescending(e => e.Timestamp)
            .Select(e => new FriendFeedRowViewModel(e))
            .ToList();

        Dispatch(() =>
        {
            _items.Clear();
            foreach (var row in ordered) _items.Add(row);
            LastUpdated = DateTimeOffset.UtcNow;
            State = ordered.Count == 0 ? FriendsFeedState.Empty : FriendsFeedState.Populated;
        });
    }

    private void CancelInFlight()
    {
        var cts = _inFlightCts;
        _inFlightCts = null;
        try { cts?.Cancel(); } catch { /* ignore */ }
    }

    private void SetStateOnDispatcher(FriendsFeedState state)
        => Dispatch(() => State = state);

    private void Dispatch(Action action)
    {
        if (_dispatcher is null || _dispatcher.HasThreadAccess)
            action();
        else
            _dispatcher.TryEnqueue(() => action());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelInFlight();
        CancelAllUserFetches();
        _safetyTimer?.Dispose();
        _safetyTimer = null;
        _rowTickTimer?.Dispose();
        _rowTickTimer = null;
        _lifecycleSubs.Dispose();
    }

    private void OnRowTick(object? _)
    {
        if (_disposed) return;

        // Dispatch snapshot + refresh to UI thread (touching ObservableProperty
        // setters raises PropertyChanged, which WinUI requires on the dispatcher).
        Dispatch(() =>
        {
            if (_items.Count == 0) return;
            for (int i = 0; i < _items.Count; i++)
            {
                _items[i].Refresh();
            }
        });
    }
}
