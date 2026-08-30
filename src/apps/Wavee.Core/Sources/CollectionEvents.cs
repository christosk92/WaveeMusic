namespace Wavee.Core;

/// <summary>Which library collection changed — emitted by <see cref="ICollectionEvents"/> so a UI cache (the app's
/// LibraryStore) can refresh JUST that collection, even off-page (docs/plans/wavee/architecture.md §6 "library delta streams").</summary>
public enum CollectionKind { Albums, Artists, Shows, Playlists, Liked }

/// <summary>The aggregate library-delta stream the UI cache subscribes to ONCE at the root, so collection changes are
/// processed even when no page is mounted. Trivial today (the synthetic sources never raise it — the seam is
/// behavior-neutral); a real backend raises it and the cache refreshes in place without a skeleton flash.</summary>
public interface ICollectionEvents
{
    IObservable<CollectionKind> CollectionsChanged { get; }
}

/// <summary>A source that emits its OWN collection deltas — fanned into <see cref="ICollectionEvents"/> by the aggregate.</summary>
public interface ISourceCollectionEvents
{
    IObservable<CollectionKind> CollectionsChanged { get; }
}

/// <summary>A lambda-backed <see cref="IObserver{T}"/> so Wavee.Core can subscribe to its own <see cref="SimpleSubject{T}"/>
/// streams without an Rx dependency (the engine has a Subscribe(Action) extension; Core does not).</summary>
internal sealed class ActionObserver<T> : IObserver<T>
{
    readonly System.Action<T> _onNext;
    public ActionObserver(System.Action<T> onNext) => _onNext = onNext;
    public void OnNext(T value) => _onNext(value);
    public void OnCompleted() { }
    public void OnError(System.Exception error) { }
}
