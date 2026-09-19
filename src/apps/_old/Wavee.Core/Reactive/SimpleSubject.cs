namespace Wavee.Core;

/// <summary>
/// A minimal hand-rolled <see cref="IObservable{T}"/> so Wavee.Core takes ZERO package dependency
/// (WaveeMusic leans on System.Reactive; FluentGpu is signals-first / NativeAOT / zero-alloc, so Rx
/// is kept out). New subscribers immediately receive the last value if one has been published
/// (BehaviorSubject-like), which is what the UI bridge wants on (re)subscribe.
/// </summary>
public sealed class SimpleSubject<T> : IObservable<T>
{
    readonly object _gate = new();
    IObserver<T>[] _observers = Array.Empty<IObserver<T>>();   // copy-on-write: OnNext just grabs the reference (zero-alloc)
    T _last;
    bool _hasLast;

    public SimpleSubject() => _last = default!;
    public SimpleSubject(T initial) { _last = initial; _hasLast = true; }

    /// <summary>The most recently published value, or <c>default</c> if nothing has been published.</summary>
    public T? Current { get { lock (_gate) return _hasLast ? _last : default; } }

    public IDisposable Subscribe(IObserver<T> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        bool hasLast;
        T last;
        lock (_gate)
        {
            var next = new IObserver<T>[_observers.Length + 1];
            Array.Copy(_observers, next, _observers.Length);
            next[_observers.Length] = observer;
            _observers = next;
            hasLast = _hasLast;   // capture under the lock — T may be a multi-field struct (StoreChange), so reading it
            last = _last;          // unsynchronized could observe a torn value mid-write
        }
        if (hasLast) observer.OnNext(last);
        return new Subscription(this, observer);
    }

    /// <summary>Publish a value to all current subscribers.</summary>
    public void OnNext(T value)
    {
        IObserver<T>[] snapshot;
        lock (_gate)
        {
            _last = value;
            _hasLast = true;
            snapshot = _observers;   // the array is immutable (replaced on sub/unsub) — grab the reference, no per-publish copy
        }
        foreach (var o in snapshot) o.OnNext(value);
    }

    void Remove(IObserver<T> observer)
    {
        lock (_gate)
        {
            int i = Array.IndexOf(_observers, observer);
            if (i < 0) return;
            if (_observers.Length == 1) { _observers = Array.Empty<IObserver<T>>(); return; }
            var next = new IObserver<T>[_observers.Length - 1];
            Array.Copy(_observers, 0, next, 0, i);
            Array.Copy(_observers, i + 1, next, i, _observers.Length - i - 1);
            _observers = next;
        }
    }

    sealed class Subscription(SimpleSubject<T> parent, IObserver<T> observer) : IDisposable
    {
        SimpleSubject<T>? _parent = parent;
        public void Dispose()
        {
            _parent?.Remove(observer);
            _parent = null;
        }
    }
}

/// <summary>A minimal non-replaying observable for event streams. Unlike <see cref="SimpleSubject{T}"/>, new subscribers
/// do not receive the last value; this is for notifications where replaying stale events would be incorrect.</summary>
public sealed class SimpleEvent<T> : IObservable<T>
{
    readonly object _gate = new();
    IObserver<T>[] _observers = Array.Empty<IObserver<T>>();

    public IDisposable Subscribe(IObserver<T> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_gate)
        {
            var next = new IObserver<T>[_observers.Length + 1];
            Array.Copy(_observers, next, _observers.Length);
            next[_observers.Length] = observer;
            _observers = next;
        }
        return new Subscription(this, observer);
    }

    public void OnNext(T value)
    {
        IObserver<T>[] snapshot;
        lock (_gate) snapshot = _observers;
        foreach (var o in snapshot) o.OnNext(value);
    }

    void Remove(IObserver<T> observer)
    {
        lock (_gate)
        {
            int i = Array.IndexOf(_observers, observer);
            if (i < 0) return;
            if (_observers.Length == 1) { _observers = Array.Empty<IObserver<T>>(); return; }
            var next = new IObserver<T>[_observers.Length - 1];
            Array.Copy(_observers, 0, next, 0, i);
            Array.Copy(_observers, i + 1, next, i, _observers.Length - i - 1);
            _observers = next;
        }
    }

    sealed class Subscription(SimpleEvent<T> parent, IObserver<T> observer) : IDisposable
    {
        SimpleEvent<T>? _parent = parent;
        public void Dispose()
        {
            _parent?.Remove(observer);
            _parent = null;
        }
    }
}
