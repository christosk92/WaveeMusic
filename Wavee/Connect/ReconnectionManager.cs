using Microsoft.Extensions.Logging;

namespace Wavee.Connect;

/// <summary>
/// Manages automatic reconnection with exponential backoff strategy.
/// </summary>
internal sealed class ReconnectionManager : IAsyncDisposable
{
    private readonly ILogger? _logger;
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _maxDelay;
    private readonly int? _maxAttempts;
    private readonly Func<ValueTask> _reconnectCallback;

    private int _attemptCount;
    private CancellationTokenSource? _cts;
    private Task? _reconnectTask;
    private bool _isReconnecting;
    private readonly object _lock = new();

    /// <summary>
    /// Raised when reconnection succeeds.
    /// </summary>
    public event EventHandler? ReconnectionSucceeded;

    /// <summary>
    /// Raised when all reconnection attempts have been exhausted.
    /// </summary>
    public event EventHandler? ReconnectionFailed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReconnectionManager"/> class.
    /// </summary>
    /// <param name="initialDelay">Initial delay before first reconnection attempt.</param>
    /// <param name="maxDelay">Maximum delay between reconnection attempts.</param>
    /// <param name="maxAttempts">Maximum number of attempts (null for unlimited).</param>
    /// <param name="reconnectCallback">Async callback to execute reconnection logic.</param>
    /// <param name="logger">Optional logger for diagnostic output.</param>
    public ReconnectionManager(
        TimeSpan initialDelay,
        TimeSpan maxDelay,
        int? maxAttempts,
        Func<ValueTask> reconnectCallback,
        ILogger? logger = null)
    {
        _initialDelay = initialDelay;
        _maxDelay = maxDelay;
        _maxAttempts = maxAttempts;
        _reconnectCallback = reconnectCallback ?? throw new ArgumentNullException(nameof(reconnectCallback));
        _logger = logger;
    }

    /// <summary>
    /// Gets whether a reconnection is currently in progress.
    /// </summary>
    public bool IsReconnecting
    {
        get
        {
            lock (_lock)
            {
                return _isReconnecting;
            }
        }
    }

    /// <summary>
    /// Gets the current reconnection attempt count.
    /// </summary>
    public int AttemptCount => _attemptCount;

    /// <summary>
    /// Initiates a reconnection attempt.
    /// </summary>
    public void TriggerReconnection()
    {
        lock (_lock)
        {
            if (_isReconnecting)
            {
                _logger?.LogDebug("Reconnection already in progress, ignoring trigger");
                return;
            }

            _isReconnecting = true;
            _attemptCount = 0;
        }

        _cts = new CancellationTokenSource();
        _reconnectTask = RunReconnectionLoopAsync(_cts.Token);

        _logger?.LogInformation("Reconnection triggered");
    }

    /// <summary>
    /// Resets the reconnection state (call after successful manual connection).
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _attemptCount = 0;
            _isReconnecting = false;
        }

        _logger?.LogDebug("Reconnection state reset");
    }

    /// <summary>
    /// Cancels any ongoing reconnection attempts.
    /// </summary>
    public async ValueTask CancelReconnectionAsync()
    {
        _logger?.LogDebug("Cancelling reconnection");

        _cts?.Cancel();

        if (_reconnectTask != null)
        {
            try
            {
                await _reconnectTask;
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }

        lock (_lock)
        {
            _isReconnecting = false;
        }

        _cts?.Dispose();
        _cts = null;
        _reconnectTask = null;
    }

    /// <summary>
    /// Calculates the delay for the next reconnection attempt using exponential backoff.
    /// </summary>
    private TimeSpan CalculateDelay()
    {
        // Exponential backoff: initialDelay * 2^attemptCount
        var exponentialDelay = _initialDelay.TotalSeconds * Math.Pow(2, _attemptCount);
        var clampedDelay = Math.Min(exponentialDelay, _maxDelay.TotalSeconds);

        return TimeSpan.FromSeconds(clampedDelay);
    }

    /// <summary>
    /// Checks if we should continue retrying based on max attempts configuration.
    /// </summary>
    private bool ShouldRetry()
    {
        if (!_maxAttempts.HasValue)
            return true;

        return _attemptCount < _maxAttempts.Value;
    }

    /// <summary>
    /// Main reconnection loop with exponential backoff.
    /// </summary>
    private async Task RunReconnectionLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!ShouldRetry())
                {
                    _logger?.LogError("Maximum reconnection attempts ({MaxAttempts}) reached", _maxAttempts);

                    lock (_lock)
                    {
                        _isReconnecting = false;
                    }

                    ReconnectionFailed?.Invoke(this, EventArgs.Empty);
                    break;
                }

                _attemptCount++;
                var delay = CalculateDelay();

                _logger?.LogInformation("Reconnection attempt {Attempt}{MaxInfo} after {Delay}s delay",
                    _attemptCount,
                    _maxAttempts.HasValue ? $"/{_maxAttempts.Value}" : "",
                    delay.TotalSeconds);

                // Wait before attempting
                await Task.Delay(delay, cancellationToken);

                // Attempt reconnection
                try
                {
                    await _reconnectCallback();

                    // Success!
                    _logger?.LogInformation("Reconnection succeeded after {Attempts} attempt(s)", _attemptCount);

                    lock (_lock)
                    {
                        _isReconnecting = false;
                        _attemptCount = 0;
                    }

                    ReconnectionSucceeded?.Invoke(this, EventArgs.Empty);
                    break;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Reconnection attempt {Attempt} failed", _attemptCount);

                    // If this was the last attempt, raise failure event
                    if (!ShouldRetry())
                    {
                        _logger?.LogError("All reconnection attempts exhausted");

                        lock (_lock)
                        {
                            _isReconnecting = false;
                        }

                        ReconnectionFailed?.Invoke(this, EventArgs.Empty);
                        break;
                    }

                    // Otherwise, continue to next iteration
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug("Reconnection loop cancelled");

            lock (_lock)
            {
                _isReconnecting = false;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Unexpected error in reconnection loop");

            lock (_lock)
            {
                _isReconnecting = false;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CancelReconnectionAsync();
    }
}
