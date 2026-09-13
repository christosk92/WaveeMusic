namespace Wavee.Core;

/// <summary>One friend's most-recent listening activity — a row in the friends feed (what a friend is/was playing).</summary>
public sealed record FriendActivity(
    string UserUri,
    string UserName,
    string? UserImageUrl,
    long TimestampMs,
    string TrackUri,
    string TrackName,
    string? TrackImageUrl,
    string? AlbumUri,
    string? AlbumName,
    string? ArtistUri,
    string? ArtistName,
    string? ContextUri,
    string? ContextName);

/// <summary>The friends-feed panel state: what the UI shows (skeleton / rows / empty / offline / error).</summary>
public enum FriendFeedState
{
    Idle,
    Loading,
    Populated,
    Empty,
    Offline,
    Error,
}

/// <summary>Session-scoped friend-activity feed: seeds from the presence endpoint on connect and applies live dealer deltas.</summary>
public interface IFriendActivityService
{
    /// <summary>The current rows, sorted most-recent first. Never null (empty when nothing is visible).</summary>
    IReadOnlyList<FriendActivity> Snapshot { get; }

    /// <summary>The feed's coarse state, driving which UI surface (skeleton / empty / offline / error / rows) shows.</summary>
    FriendFeedState State { get; }

    /// <summary>The last error message when <see cref="State"/> is <see cref="FriendFeedState.Error"/>, else null.</summary>
    string? LastError { get; }

    /// <summary>Emits a monotonically increasing tick whenever <see cref="Snapshot"/>/<see cref="State"/> change.</summary>
    IObservable<int> Changed { get; }

    /// <summary>Marks the feed panel visible/hidden — starts/stops the watchdog re-seed and lazy-seeds on first show.</summary>
    void SetActive(bool active);

    /// <summary>Forces a re-seed of the whole feed. Must never throw into callers.</summary>
    Task RefreshAsync(CancellationToken ct);
}

/// <summary>A stable service identity whose live provider can be installed after login without rebuilding consumers.</summary>
public sealed class SwitchableFriendActivityService : IFriendActivityService, IDisposable
{
    readonly SimpleEvent<int> _changed = new();
    readonly object _gate = new();
    IFriendActivityService _inner;
    IDisposable? _sub;
    int _rev;

    public SwitchableFriendActivityService(IFriendActivityService inner)
    {
        _inner = inner;
        Wire(inner);
    }

    public void SetInner(IFriendActivityService inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        lock (_gate)
        {
            _sub?.Dispose();
            _inner = inner;
            Wire(inner);
        }
        // Bump Changed so a go-live / go-offline swap re-renders even if the new inner emits nothing yet.
        Bump();
    }

    public IReadOnlyList<FriendActivity> Snapshot => Current.Snapshot;
    public FriendFeedState State => Current.State;
    public string? LastError => Current.LastError;
    public IObservable<int> Changed => _changed;
    public void SetActive(bool active) => Current.SetActive(active);
    public Task RefreshAsync(CancellationToken ct) => Current.RefreshAsync(ct);

    IFriendActivityService Current => System.Threading.Volatile.Read(ref _inner);

    void Wire(IFriendActivityService inner)
        => _sub = inner.Changed.Subscribe(new FeedObserver(_ => Bump()));

    void Bump() => _changed.OnNext(System.Threading.Interlocked.Increment(ref _rev));

    public void Dispose()
    {
        lock (_gate)
        {
            _sub?.Dispose();
            _sub = null;
        }
    }

    sealed class FeedObserver(Action<int> onNext) : IObserver<int>
    {
        public void OnNext(int value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}

/// <summary>Offline/fake fallback: no live transport, so the feed is permanently offline and empty.</summary>
public sealed class NullFriendActivityService : IFriendActivityService
{
    static readonly IReadOnlyList<FriendActivity> Empty = System.Array.Empty<FriendActivity>();
    readonly SimpleEvent<int> _changed = new();

    public IReadOnlyList<FriendActivity> Snapshot => Empty;
    public FriendFeedState State => FriendFeedState.Offline;
    public string? LastError => null;
    public IObservable<int> Changed => _changed;
    public void SetActive(bool active) { }
    public Task RefreshAsync(CancellationToken ct) => Task.CompletedTask;
}
