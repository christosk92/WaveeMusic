using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Wavee.UI.Services.Infra;

/// <summary>
/// Fire-and-forget helper for background tasks that the UI starts and doesn't
/// directly await. Replaces the audit's "naked <c>_ = SomeAsync(...)</c>" sites
/// — those leave any thrown exception to the unobserved-task scheduler, which
/// only logs through the app-domain crash handler and is easy to miss.
///
/// <para>This runner attaches a continuation that logs the failure with the
/// supplied <c>opName</c> so each background failure is traceable to its caller
/// without needing to dig through crash dumps.</para>
///
/// <para>Implementations own no cancellation of their own — callers pass a
/// <see cref="CancellationToken"/> if they want to cooperate; otherwise the
/// task runs to completion. Disposing a VM should typically cancel its own
/// CTS, which is more local than anything this helper could provide.</para>
/// </summary>
public interface IBackgroundWorkRunner
{
    /// <summary>
    /// Run <paramref name="work"/> in the background. By design returns
    /// <c>void</c> — the runner is fire-and-forget. Failures land in the
    /// configured logger keyed by <paramref name="opName"/>; cancellation
    /// is silent.
    /// </summary>
    /// <param name="work">The async work to run. Receives the supplied <paramref name="ct"/>.</param>
    /// <param name="opName">Stable identifier used in the failure log entry — pick a name that points back to the call site.</param>
    /// <param name="ct">Optional cooperation token. Cancellation is treated as success (no log).</param>
    void Run(Func<CancellationToken, Task> work, string opName, CancellationToken ct = default);

    /// <summary>
    /// Test-only awaitable variant. Production code uses <see cref="Run"/>;
    /// tests call this so they can observe completion deterministically.
    /// </summary>
    Task RunAsync(Func<CancellationToken, Task> work, string opName, CancellationToken ct = default);
}

/// <summary>
/// Default <see cref="IBackgroundWorkRunner"/>. Logs any unhandled exception
/// (other than <see cref="OperationCanceledException"/>) at Error level with
/// the supplied operation name.
/// </summary>
public sealed class BackgroundWorkRunner : IBackgroundWorkRunner
{
    private readonly ILogger<BackgroundWorkRunner>? _logger;

    public BackgroundWorkRunner(ILogger<BackgroundWorkRunner>? logger = null)
    {
        _logger = logger;
    }

    public void Run(Func<CancellationToken, Task> work, string opName, CancellationToken ct = default)
    {
        _ = RunAsync(work, opName, ct);
    }

    public Task RunAsync(Func<CancellationToken, Task> work, string opName, CancellationToken ct = default)
    {
        // Eagerly invoke so any synchronous prefix executes on the caller's
        // thread (matches the naked _ = pattern's behavior); the continuation
        // is what handles the failure path.
        var task = work(ct);
        _ = task.ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            if (t.Exception is { } ex)
            {
                _logger?.LogError(ex.GetBaseException(),
                    "Background work failed: {OpName}", opName);
            }
        }, TaskScheduler.Default);
        return task;
    }
}
