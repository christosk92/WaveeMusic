using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Wavee;

/// <summary>Serialized pure work. Both superseded calculations and already-queued UI deliveries are discarded.</summary>
public sealed class LatestProjection<TInput, TOutput>(Func<TInput, TOutput> project,
    Action<Action> post, Action<TOutput> publish, Action<Exception> failed) : IDisposable
{
    readonly object _gate = new();
    TInput _pending = default!;
    bool _hasInput, _dirty, _running, _disposed;
    long _generation;

    public void Submit(TInput input)
    {
        lock (_gate)
        {
            if (_disposed || _hasInput && EqualityComparer<TInput>.Default.Equals(_pending, input)) return;
            _pending = input; _hasInput = _dirty = true; _generation++;
            if (_running) return;
            _running = true;
        }
        _ = Task.Run(Drain);
    }

    void Drain()
    {
        while (true)
        {
            TInput input;
            long generation;
            lock (_gate)
            {
                if (_disposed || !_dirty) { _running = false; return; }
                input = _pending; generation = _generation; _dirty = false;
            }
            TOutput result = default!;
            Exception? error = null;
            try { result = project(input); }
            catch (Exception exception) { error = exception; }
            lock (_gate) if (_disposed || generation != _generation) continue;
            post(() =>
            {
                lock (_gate) if (_disposed || generation != _generation) return;
                if (error is null) publish(result); else failed(error);
            });
        }
    }

    public void Dispose() { lock (_gate) { _disposed = true; _generation++; } }
}
