using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.Core;

/// <summary>A remote feed's coarse state, driving which UI surface (skeleton / rows / empty / offline / error) shows —
/// the <see cref="FriendFeedState"/> pattern, shared by the gander + what's-new feeds.</summary>
public enum NotificationFeedState { Idle, Loading, Populated, Empty, Offline, Error }

/// <summary>Session-scoped Spotify social notifications (followers, concerts, announcements) — the gander feed. Seeds at
/// go-live so the bell badge is correct before the first open; <see cref="EnsureFresh"/> refetches if stale (&gt;5 min).</summary>
public interface ISpotifyNotificationsService
{
    IReadOnlyList<SocialNotification> Snapshot { get; }
    NotificationFeedState State { get; }
    IObservable<int> Changed { get; }
    /// <summary>Refetch if the feed is stale (called on panel open). Cheap no-op when fresh.</summary>
    void EnsureFresh();
    /// <summary>Force a re-seed. Must never throw into callers.</summary>
    Task RefreshAsync(CancellationToken ct);
}

/// <summary>A stable identity whose live provider is installed after login without rebuilding consumers (the friends
/// <see cref="SwitchableFriendActivityService"/> shape).</summary>
public sealed class SwitchableSpotifyNotificationsService : ISpotifyNotificationsService, IDisposable
{
    readonly SimpleEvent<int> _changed = new();
    readonly object _gate = new();
    ISpotifyNotificationsService _inner;
    IDisposable? _sub;
    int _rev;

    public SwitchableSpotifyNotificationsService(ISpotifyNotificationsService inner)
    {
        _inner = inner;
        Wire(inner);
    }

    public void SetInner(ISpotifyNotificationsService inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        lock (_gate)
        {
            _sub?.Dispose();
            _inner = inner;
            Wire(inner);
        }
        Bump();   // a go-live / go-offline swap re-renders even if the new inner emits nothing yet
    }

    public IReadOnlyList<SocialNotification> Snapshot => Current.Snapshot;
    public NotificationFeedState State => Current.State;
    public IObservable<int> Changed => _changed;
    public void EnsureFresh() => Current.EnsureFresh();
    public Task RefreshAsync(CancellationToken ct) => Current.RefreshAsync(ct);

    ISpotifyNotificationsService Current => Volatile.Read(ref _inner);
    void Wire(ISpotifyNotificationsService inner) => _sub = inner.Changed.Subscribe(new BumpObserver(_ => Bump()));
    void Bump() => _changed.OnNext(Interlocked.Increment(ref _rev));

    public void Dispose()
    {
        lock (_gate) { _sub?.Dispose(); _sub = null; }
    }

    sealed class BumpObserver(Action<int> onNext) : IObserver<int>
    {
        public void OnNext(int value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}

/// <summary>Offline/fake fallback: no live transport, so the feed is permanently offline and empty.</summary>
public sealed class NullSpotifyNotificationsService : ISpotifyNotificationsService
{
    static readonly IReadOnlyList<SocialNotification> Empty = Array.Empty<SocialNotification>();
    readonly SimpleEvent<int> _changed = new();

    public IReadOnlyList<SocialNotification> Snapshot => Empty;
    public NotificationFeedState State => NotificationFeedState.Offline;
    public IObservable<int> Changed => _changed;
    public void EnsureFresh() { }
    public Task RefreshAsync(CancellationToken ct) => Task.CompletedTask;
}
