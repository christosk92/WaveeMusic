using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.Core;

/// <summary>Session-scoped "What's New" feed — new releases/episodes from followed artists/shows (pathfinder
/// queryWhatsNewFeed). Same shape as <see cref="ISpotifyNotificationsService"/>.</summary>
public interface IWhatsNewService
{
    IReadOnlyList<NewReleaseNotification> Snapshot { get; }
    NotificationFeedState State { get; }
    IObservable<int> Changed { get; }
    void EnsureFresh();
    Task RefreshAsync(CancellationToken ct);
}

/// <summary>A stable identity whose live provider is installed after login without rebuilding consumers.</summary>
public sealed class SwitchableWhatsNewService : IWhatsNewService, IDisposable
{
    readonly SimpleEvent<int> _changed = new();
    readonly object _gate = new();
    IWhatsNewService _inner;
    IDisposable? _sub;
    int _rev;

    public SwitchableWhatsNewService(IWhatsNewService inner)
    {
        _inner = inner;
        Wire(inner);
    }

    public void SetInner(IWhatsNewService inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        lock (_gate)
        {
            _sub?.Dispose();
            _inner = inner;
            Wire(inner);
        }
        Bump();
    }

    public IReadOnlyList<NewReleaseNotification> Snapshot => Current.Snapshot;
    public NotificationFeedState State => Current.State;
    public IObservable<int> Changed => _changed;
    public void EnsureFresh() => Current.EnsureFresh();
    public Task RefreshAsync(CancellationToken ct) => Current.RefreshAsync(ct);

    IWhatsNewService Current => Volatile.Read(ref _inner);
    void Wire(IWhatsNewService inner) => _sub = inner.Changed.Subscribe(new BumpObserver(_ => Bump()));
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

/// <summary>Offline/fake fallback: permanently offline and empty.</summary>
public sealed class NullWhatsNewService : IWhatsNewService
{
    static readonly IReadOnlyList<NewReleaseNotification> Empty = Array.Empty<NewReleaseNotification>();
    readonly SimpleEvent<int> _changed = new();

    public IReadOnlyList<NewReleaseNotification> Snapshot => Empty;
    public NotificationFeedState State => NotificationFeedState.Offline;
    public IObservable<int> Changed => _changed;
    public void EnsureFresh() { }
    public Task RefreshAsync(CancellationToken ct) => Task.CompletedTask;
}
