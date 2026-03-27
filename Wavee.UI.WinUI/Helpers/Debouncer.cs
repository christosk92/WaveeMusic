using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.UI.WinUI.Helpers;

/// <summary>
/// A reusable debounce utility that delays execution until input settles.
/// Each call to <see cref="DebounceAsync"/> cancels any pending invocation
/// and starts a new delay. Only the last call within the delay window executes.
/// </summary>
public sealed class Debouncer : IDisposable
{
    private readonly TimeSpan _delay;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Creates a new Debouncer with the specified delay.
    /// </summary>
    /// <param name="delay">How long to wait after the last call before executing.</param>
    public Debouncer(TimeSpan delay)
    {
        _delay = delay;
    }

    /// <summary>
    /// Debounces the given async action. If called again before the delay elapses,
    /// the previous invocation is cancelled and the delay resets.
    /// </summary>
    /// <param name="action">The async action to execute after the delay.</param>
    /// <returns>A task that completes when the action finishes, or is cancelled.</returns>
    public async Task DebounceAsync(Func<CancellationToken, Task> action)
    {
        // Cancel any previous pending call
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            await Task.Delay(_delay, ct);
            await action(ct);
        }
        catch (OperationCanceledException)
        {
            // Expected — a newer call superseded this one
        }
    }

    /// <summary>
    /// Cancels any pending debounced action without executing it.
    /// </summary>
    public void Cancel()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
