using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;

namespace Wavee.Connect;

/// <summary>
/// A thread-safe Subject implementation that isolates exceptions from individual subscribers.
/// When one subscriber throws an exception, it doesn't affect other subscribers.
/// </summary>
/// <typeparam name="T">The type of elements in the sequence.</typeparam>
internal sealed class SafeSubject<T> : ISubject<T>, IDisposable
{
    private readonly object _lock = new();
    private readonly List<IObserver<T>> _observers = new();
    private readonly ILogger? _logger;
    private bool _isDisposed;
    private bool _isCompleted;
    private Exception? _error;

    public SafeSubject(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets whether this subject has any active observers.
    /// </summary>
    public bool HasObservers
    {
        get
        {
            lock (_lock)
            {
                return _observers.Count > 0;
            }
        }
    }

    /// <summary>
    /// Gets the number of active observers subscribed to this subject.
    /// </summary>
    public int ObserverCount
    {
        get
        {
            lock (_lock)
            {
                return _observers.Count;
            }
        }
    }

    public void OnNext(T value)
    {
        IObserver<T>[] observers;

        lock (_lock)
        {
            if (_isDisposed || _isCompleted || _error != null)
                return;

            // Copy to array to avoid holding lock during notifications
            observers = _observers.ToArray();
        }

        // Notify each observer, isolating exceptions
        foreach (var observer in observers)
        {
            try
            {
                observer.OnNext(value);
            }
            catch (Exception ex)
            {
                // Log but don't propagate - isolate subscriber exceptions
                _logger?.LogError(ex, "Subscriber threw exception in OnNext");
            }
        }
    }

    public void OnError(Exception error)
    {
        IObserver<T>[] observers;

        lock (_lock)
        {
            if (_isDisposed || _isCompleted || _error != null)
                return;

            _error = error;
            observers = _observers.ToArray();
            _observers.Clear();
        }

        foreach (var observer in observers)
        {
            try
            {
                observer.OnError(error);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Subscriber threw exception in OnError");
            }
        }
    }

    public void OnCompleted()
    {
        IObserver<T>[] observers;

        lock (_lock)
        {
            if (_isDisposed || _isCompleted || _error != null)
                return;

            _isCompleted = true;
            observers = _observers.ToArray();
            _observers.Clear();
        }

        foreach (var observer in observers)
        {
            try
            {
                observer.OnCompleted();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Subscriber threw exception in OnCompleted");
            }
        }
    }

    public IDisposable Subscribe(IObserver<T> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        lock (_lock)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(SafeSubject<T>));

            // If already completed or errored, notify immediately
            if (_isCompleted)
            {
                observer.OnCompleted();
                return new EmptyDisposable();
            }

            if (_error != null)
            {
                observer.OnError(_error);
                return new EmptyDisposable();
            }

            _observers.Add(observer);

            return new Subscription(this, observer);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            _observers.Clear();
        }
    }

    private sealed class Subscription : IDisposable
    {
        private SafeSubject<T>? _subject;
        private IObserver<T>? _observer;

        public Subscription(SafeSubject<T> subject, IObserver<T> observer)
        {
            _subject = subject;
            _observer = observer;
        }

        public void Dispose()
        {
            var subject = Interlocked.Exchange(ref _subject, null);
            var observer = Interlocked.Exchange(ref _observer, null);

            if (subject != null && observer != null)
            {
                lock (subject._lock)
                {
                    subject._observers.Remove(observer);
                }
            }
        }
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
