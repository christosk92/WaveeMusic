using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.Backend.Catalog;

public enum ProviderExecutionPolicy { Ready, ProtocolSession }
public enum ProviderExecutionState { Initializing, Ready, Offline }

/// <summary>Transport admission, not page readiness. Waiters belong to exactly one catalog epoch.</summary>
public sealed class ProviderExecutionGate
{
    readonly object _gate = new();
    TaskCompletionSource _changed = NewCompletion();
    long _epoch;
    ProviderExecutionState _state;
    public ProviderExecutionGate(long epoch, ProviderExecutionState state) { _epoch = epoch; _state = state; }
    static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Set(long epoch, ProviderExecutionState state)
    {
        TaskCompletionSource changed;
        lock (_gate)
        {
            if (epoch < _epoch || epoch == _epoch && state == _state) return;
            _epoch = epoch; _state = state;
            changed = _changed; _changed = NewCompletion();
        }
        changed.TrySetResult();
    }

    public async ValueTask<bool> WaitAsync(long expectedEpoch, CancellationToken ct)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_epoch != expectedEpoch || _state == ProviderExecutionState.Offline) return false;
                if (_state == ProviderExecutionState.Ready) return true;
                changed = _changed.Task;
            }
            await changed.WaitAsync(ct).ConfigureAwait(false);
        }
    }
}
